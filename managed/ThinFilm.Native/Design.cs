// Design.cs — the owner of every buffer, and the reason the inner loop can allocate nothing.
//
// C0.1: the native side never allocates. That makes buffer ownership a managed responsibility and
// this class the place it lives. It does three things and refuses to do a fourth:
//
//   1. copies the spec into buffers that never move;
//   2. runs tf_plan and allocates what the plan says it needs;
//   3. calls the evaluators, allocating nothing.
//
// It computes no physical quantity. Every number it hands back was produced by the engine.
//
// WHY THE ABI STRUCTS THEMSELVES LIVE IN PINNED BUFFERS. A TfProblem held as a field of this class
// would sit on the GC heap and move, so taking its address would need a `fixed` block around every
// call — and a `fixed` block allocates nothing but does force the struct to be re-pinned, and more
// to the point it means the pointers INSIDE it would have to be re-written each time something
// moved. Putting the struct in a PinnedBuffer<TfProblem> of length one gives it a stable address,
// which means the pointers into the other buffers are written ONCE, in the constructor, and are
// still valid on the ten-thousandth evaluation. That is what makes step 5 of the contract — "must
// not allocate" — a property of the design rather than a rule somebody has to keep.
//
// WHAT MAY CHANGE BETWEEN EVALUATIONS, AND WHAT MAY NOT. Values may: LayerThickness is a writable
// span straight into the pinned buffer, which is what an optimiser drives. Shape may not: counts
// are fixed at construction, because every buffer was sized from them. There is no setter that
// would silently invalidate a buffer — the absent setter IS the enforcement.
//
// AND THE THING THE ENGINE DOES NOT ENFORCE. TF_PLAN_STALE is declared in the header and raised
// nowhere in native/, so nothing refuses an evaluation against a plan whose assumptions have
// expired. They can expire: tf_plan chooses a path per point partly from thickness, because a
// layer thick enough to absorb everything clamps, and it marks a variable inert on the same
// grounds. An optimiser that drives a layer opaque has changed a plan input. RefreshPlan() is the
// answer offered here — it re-plans and says whether anything moved — and it is documented rather
// than automatic, because running it every iteration would cost what planning once was meant to
// save.

using System.Runtime.CompilerServices;

namespace ThinFilm.Native;

/// <summary>A native call returned something other than TF_OK.</summary>
public sealed class ThinFilmException : Exception
{
    public ThinFilmException(TfStatus status, string call)
        : base($"{call} returned {status} ({(int)status})")
    {
        Status = status;
        Call = call;
    }

    public TfStatus Status { get; }
    public string Call { get; }
}

public sealed unsafe class Design
{
    // The three input structs and the three output structs, each in a buffer of one so that its
    // address is stable for the life of the Design.
    private readonly PinnedBuffer<TfProblem> _problem = new(1);
    private readonly PinnedBuffer<TfOperands> _operands = new(1);
    private readonly PinnedBuffer<TfVariables> _variables = new(1);
    private readonly PinnedBuffer<TfConfigurations> _configurations = new(1);
    private readonly PinnedBuffer<TfPlanBuffers> _planBuffers = new(1);
    private readonly PinnedBuffer<TfWorkspace> _workspace = new(1);
    private readonly PinnedBuffer<TfOutput> _output = new(1);

    // Inputs. Held as fields because the structs point into them and nothing else keeps them alive.
    private readonly PinnedBuffer<double> _layerThickness;
    private readonly PinnedBuffer<int> _layerMaterial;
    private readonly PinnedBuffer<int>? _layerIsIncoherent;
    private readonly PinnedBuffer<TfComplex> _materialIndex;
    private readonly PinnedBuffer<double> _wavelength;
    private readonly PinnedBuffer<int> _pointWavelength;
    private readonly PinnedBuffer<double> _pointAngle;
    private readonly PinnedBuffer<double> _operandWeight;
    private readonly List<object> _held = new();   // everything optional, kept alive by reference

    // Plan buffers.
    private readonly PinnedBuffer<int> _pointPath;
    private readonly PinnedBuffer<int> _pointOrder;
    private readonly PinnedBuffer<int> _variableInert;
    private readonly PinnedBuffer<int> _pointPolarisation;
    private readonly PinnedBuffer<int> _spanFirst;
    private readonly PinnedBuffer<int> _spanCount;
    private readonly PinnedBuffer<int> _spanOperand;
    private readonly PinnedBuffer<int> _spanSlot;
    private readonly PinnedBuffer<int> _operandResidual;
    private readonly PinnedBuffer<int> _operandOrder;
    private readonly PinnedBuffer<int> _operandLevel;
    private readonly PinnedBuffer<TfPlanReport> _report = new(1);
    private readonly PinnedBuffer<int> _planStatus = new(1);

    // Outputs, sized after the plan has said how big they are.
    private PinnedBuffer<double> _scratch = new(0);
    private PinnedBuffer<double> _residual = new(0);
    private PinnedBuffer<double> _jacobian = new(0);
    private PinnedBuffer<double> _value = new(0);
    private PinnedBuffer<double>? _trialValue;
    private PinnedBuffer<int> _operandFlag = new(0);
    private readonly PinnedBuffer<int> _flaggedCount = new(1);
    private readonly PinnedBuffer<double> _merit = new(1);
    private readonly PinnedBuffer<int> _evalStatus = new(1);

    private readonly int _workers;
    private readonly DesignSpec _spec;
    private readonly int[] _variableLayer;
    private bool _planned;

    // PICKUPS. A tied layer's thickness is a straight line in its parent's, written before every
    // evaluation (ApplyTies). When the parent is a variable the tied layer is handed to the engine
    // as one more variable, so its Jacobian column exists, and that column is folded into the
    // parent's: dr/dparent = J[parent] + scale * J[child]. The optimisers see VariableCount free
    // variables and a Jacobian of that width, and never learn that a tie exists.
    private readonly int[] _tieChild = [];
    private readonly int[] _tieParent = [];
    private readonly double[] _tieScale = [];
    private readonly double[] _tieOffset = [];
    private readonly int _engineVariableCount;
    private readonly int[] _foldInto = [];        // [extra engine variable] -> free variable
    private readonly double[] _foldScale = [];
    private PinnedBuffer<double>? _folded;

    public int LayerCount { get; }
    public int MaterialCount { get; }
    public int WavelengthCount { get; }
    public int PointCount { get; }
    public int OperandCount { get; }
    public int VariableCount { get; }
    public int TrialCount { get; }

    public Design(DesignSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(spec.Operands);

        LayerCount = spec.LayerThickness?.Length ?? 0;
        MaterialCount = spec.MaterialCount;
        WavelengthCount = spec.Wavelength.Length;
        PointCount = spec.PointAngle.Length;
        OperandCount = spec.Operands.Code.Length;
        VariableCount = spec.VariableKind?.Length ?? 0;
        TrialCount = spec.TrialCount;
        _workers = spec.Workers;

        Validate(spec);
        _spec = spec;
        _variableLayer = spec.VariableLayer ?? System.Array.Empty<int>();

        // ---- the problem ------------------------------------------------------------------
        _layerThickness = Copy(spec.LayerThickness);
        _layerMaterial = Copy(spec.LayerMaterial);
        _layerIsIncoherent = Optional(spec.LayerIsIncoherent);
        _materialIndex = Copy(spec.MaterialIndex);
        _wavelength = Copy(spec.Wavelength);
        _pointWavelength = Copy(spec.PointWavelength);
        _pointAngle = Copy(spec.PointAngle);

        ref var p = ref _problem[0];
        p.n_layers = LayerCount;
        p.n_materials = MaterialCount;
        p.n_wavelengths = WavelengthCount;
        p.n_points = PointCount;
        p.layer_thickness = _layerThickness.Pointer;
        p.layer_material = _layerMaterial.Pointer;
        p.layer_is_incoherent = Ptr(_layerIsIncoherent);
        p.material_index = _materialIndex.Pointer;
        p.wavelength = _wavelength.Pointer;
        p.point_wavelength = _pointWavelength.Pointer;
        p.point_angle = _pointAngle.Pointer;
        p.material_stress = Ptr(Optional(spec.MaterialStress));
        p.material_index_is_extrapolated = Ptr(Optional(spec.MaterialIndexIsExtrapolated));
        p.material_dindex_dwavelength = Ptr(Optional(spec.MaterialDIndexDWavelength));
        p.material_d2index_dwavelength2 = Ptr(Optional(spec.MaterialD2IndexDWavelength2));
        p.material_d3index_dwavelength3 = Ptr(Optional(spec.MaterialD3IndexDWavelength3));
        p.n_trials = TrialCount;
        p.trial_layer_thickness = Ptr(Optional(spec.TrialLayerThickness));
        p.trial_material_index = Ptr(Optional(spec.TrialMaterialIndex));
        p.substrate_thickness = spec.SubstrateThickness;
        p.index_back = Ptr(Optional(spec.IndexBack));

        if (spec.Configurations is { } cfg)
        {
            ref var c = ref _configurations[0];
            c.n_configurations = cfg.Count;
            c.index_incident = Ptr(Optional(cfg.IndexIncident));
            c.index_exit = Ptr(Optional(cfg.IndexExit));
            c.dindex_incident_dwavelength = Ptr(Optional(cfg.DIndexIncidentDWavelength));
            c.dindex_exit_dwavelength = Ptr(Optional(cfg.DIndexExitDWavelength));
            c.index_incident_is_extrapolated = Ptr(Optional(cfg.IndexIncidentIsExtrapolated));
            c.index_exit_is_extrapolated = Ptr(Optional(cfg.IndexExitIsExtrapolated));
            c.reversed = Ptr(Optional(cfg.Reversed));
            p.configurations = _configurations.Pointer;
            p.point_configuration = Ptr(Optional(spec.PointConfiguration));
        }
        else
        {
            p.index_incident = Ptr(Optional(spec.IndexIncident));
            p.index_exit = Ptr(Optional(spec.IndexExit));
            p.dindex_incident_dwavelength = Ptr(Optional(spec.DIndexIncidentDWavelength));
            p.dindex_exit_dwavelength = Ptr(Optional(spec.DIndexExitDWavelength));
            p.d2index_incident_dwavelength2 = Ptr(Optional(spec.D2IndexIncidentDWavelength2));
            p.d2index_exit_dwavelength2 = Ptr(Optional(spec.D2IndexExitDWavelength2));
            p.d3index_incident_dwavelength3 = Ptr(Optional(spec.D3IndexIncidentDWavelength3));
            p.d3index_exit_dwavelength3 = Ptr(Optional(spec.D3IndexExitDWavelength3));
            p.index_incident_is_extrapolated = Ptr(Optional(spec.IndexIncidentIsExtrapolated));
            p.index_exit_is_extrapolated = Ptr(Optional(spec.IndexExitIsExtrapolated));
        }

        // ---- the operand table -------------------------------------------------------------
        var o = spec.Operands;
        var spanIndex = Optional(o.PointIndex);
        ref var ops = ref _operands[0];
        ops.n_operands = OperandCount;
        ops.delta_convention = o.DeltaConvention;
        ops.extrapolation_policy = o.ExtrapolationPolicy;
        ops.code = Copy(o.Code).Pointer;
        ops.target = Copy(o.Target).Pointer;
        _operandWeight = Copy(o.Weight);
        ops.weight = _operandWeight.Pointer;
        ops.point = Ptr(Optional(o.Point));
        ops.n_span = spanIndex?.Count is { } n ? (int)n : 0;
        ops.point_first = Ptr(Optional(o.PointFirst));
        ops.point_count = Ptr(Optional(o.PointCount));
        ops.point_index = Ptr(spanIndex);
        ops.point_weight = Ptr(Optional(o.PointWeight));
        ops.comparison = Ptr(Optional(o.Comparison));
        ops.target_hi = Ptr(Optional(o.TargetHi));
        ops.tolerance = Ptr(Optional(o.Tolerance));
        ops.scale = Ptr(Optional(o.Scale));
        ops.reduction = Ptr(Optional(o.Reduction));
        ops.reduction_param = Ptr(Optional(o.ReductionParam));
        var inputIndex = Optional(o.InputIndex);
        ops.n_input = inputIndex?.Count is { } m ? (int)m : 0;
        ops.input_first = Ptr(Optional(o.InputFirst));
        ops.input_count = Ptr(Optional(o.InputCount));
        ops.input_index = Ptr(inputIndex);
        ops.trial_reduction = Ptr(Optional(o.TrialReduction));

        // ---- pickups ------------------------------------------------------------------------
        var kinds = spec.VariableKind ?? [];
        var varLayers = spec.VariableLayer ?? [];
        var varMaterials = spec.VariableMaterial ?? new int[VariableCount];
        var varCoefficients = spec.VariableCoefficient ?? new int[VariableCount];
        if (spec.LayerTieParent is { } tieParent)
        {
            var children = new List<int>();
            for (int c = 0; c < tieParent.Length; ++c)
                if (tieParent[c] >= 0) children.Add(c);
            _tieChild = children.ToArray();
            _tieParent = children.Select(c => tieParent[c]).ToArray();
            _tieScale = children.Select(c => spec.LayerTieScale![c]).ToArray();
            _tieOffset = children.Select(c => spec.LayerTieOffset![c]).ToArray();

            // A child of a variable parent becomes an extra engine variable, appended after the
            // free ones so that variable i is still variable i to every optimiser.
            var into = new List<int>();
            var scale = new List<double>();
            var extraLayer = new List<int>();
            for (int t = 0; t < _tieChild.Length; ++t)
            {
                int fv = Array.IndexOf(varLayers, _tieParent[t]);
                if (fv < 0 || kinds[fv] != (int)TfVariableKind.TF_VARIABLE_LAYER_THICKNESS) continue;
                into.Add(fv);
                scale.Add(_tieScale[t]);
                extraLayer.Add(_tieChild[t]);
            }
            _foldInto = into.ToArray();
            _foldScale = scale.ToArray();
            if (extraLayer.Count > 0)
            {
                kinds = [.. kinds, .. extraLayer.Select(_ => (int)TfVariableKind.TF_VARIABLE_LAYER_THICKNESS)];
                varLayers = [.. varLayers, .. extraLayer];
                varMaterials = [.. varMaterials, .. new int[extraLayer.Count]];
                varCoefficients = [.. varCoefficients, .. new int[extraLayer.Count]];
            }
        }
        _engineVariableCount = kinds.Length;

        // ---- the variables ------------------------------------------------------------------
        ref var v = ref _variables[0];
        v.n_variables = _engineVariableCount;
        v.kind = Ptr(Optional(kinds));
        v.layer = Ptr(Optional(varLayers));
        v.material = Ptr(Optional(varMaterials));
        v.coefficient = Ptr(Optional(varCoefficients));
        ApplyTies();

        // ---- the plan buffers, sized BY THE ENGINE ------------------------------------------
        //
        // The sizes are asked for, never computed here. The formulae are simple enough to copy
        // into C# and that is exactly the temptation: a copy is a second implementation, and the
        // one place it has already been wrong is point_span_slot, which is n_span in one mode and
        // n_operands in the other. Asking costs one call per Design.
        TfPlanSizes sizes;
        Check(NativeMethods.tf_query_plan_sizes(PointCount, _engineVariableCount, ops.n_span,
                                                OperandCount, &sizes),
              nameof(NativeMethods.tf_query_plan_sizes));

        _pointPath = new PinnedBuffer<int>(sizes.point_path_count);
        _pointOrder = new PinnedBuffer<int>(sizes.point_order_count);
        _variableInert = new PinnedBuffer<int>(sizes.variable_inert_count);
        _pointPolarisation = new PinnedBuffer<int>(sizes.point_polarisation_count);
        _spanFirst = new PinnedBuffer<int>(sizes.point_span_first_count);
        _spanCount = new PinnedBuffer<int>(sizes.point_span_count_count);
        _spanOperand = new PinnedBuffer<int>(sizes.point_span_operand_count);
        _spanSlot = new PinnedBuffer<int>(sizes.point_span_slot_count);
        _operandResidual = new PinnedBuffer<int>(sizes.operand_residual_count);
        _operandOrder = new PinnedBuffer<int>(sizes.operand_order_count);
        _operandLevel = new PinnedBuffer<int>(sizes.operand_level_count);

        ref var pb = ref _planBuffers[0];
        pb.point_path = _pointPath.Pointer;
        pb.point_order = _pointOrder.Pointer;
        pb.variable_inert = _variableInert.Pointer;
        pb.point_polarisation = _pointPolarisation.Pointer;
        pb.point_span_first = _spanFirst.Pointer;
        pb.point_span_count = _spanCount.Pointer;
        pb.point_span_operand = _spanOperand.Pointer;
        pb.point_span_slot = _spanSlot.Pointer;
        pb.operand_residual = _operandResidual.Pointer;
        pb.operand_order = _operandOrder.Pointer;
        pb.operand_level = _operandLevel.Pointer;
        pb.report = _report.Pointer;
        pb.status = _planStatus.Pointer;
    }

    // ---- what an optimiser drives ------------------------------------------------------------

    /// <summary>
    /// The layer thicknesses, writable in place. Writing here changes what the next evaluation
    /// sees and allocates nothing — it is the same memory the engine reads.
    /// </summary>
    public Span<double> LayerThickness => _layerThickness.Span;

    /// <summary>The material index table, writable in place, [n_materials * n_wavelengths].</summary>
    public Span<TfComplex> MaterialIndex => _materialIndex.Span;

    /// <summary>
    /// The operand weights, writable in place.
    ///
    /// WRITABLE BECAUSE A NORM IS A REWEIGHTING. A4.2 to A4.6 put every norm but L2 above the ABI
    /// and reach them by IRLS, which means rewriting these between passes and asking the engine
    /// again. Changing a weight changes no shape, so nothing is re-planned and nothing is
    /// re-allocated — it is the same memory tf_evaluate already reads.
    /// </summary>
    public ReadOnlySpan<double> OperandWeight => _operandWeight.Span;

    public void SetOperandWeight(ReadOnlySpan<double> weights)
    {
        if (weights.Length != OperandCount)
            throw new ArgumentException(
                $"expected one weight per operand ({OperandCount}), got {weights.Length}",
                nameof(weights));
        weights.CopyTo(_operandWeight.Span);
    }

    /// <summary>
    /// Which residual row an operand produced, or -1 if it is unscored. The plan decides this and
    /// a reweighting has to know it: an operand with no row has no residual to be weighted by.
    /// </summary>
    public int ResidualRow(int operand) => _operandResidual.Span[operand];

    /// <summary>[n_variables] the layer each variable moves.</summary>

    public ReadOnlySpan<int> VariableLayer => _variableLayer;

    /// <summary>
    /// One optimisation variable's current value.
    ///
    /// THIS IS THE ABI'S OWN MAPPING AND NOT A MODEL OF ANYTHING. tf_variables says a variable of
    /// kind LAYER_THICKNESS moves layer_thickness[variable.layer]; this reads exactly that and
    /// invents nothing. An optimiser needs the mapping in both directions and should not have to
    /// know how the buffers are laid out to get it.
    /// </summary>
    public double GetVariable(int i) => _layerThickness.Span[_variableLayer[i]];

    public void SetVariable(int i, double value) => _layerThickness.Span[_variableLayer[i]] = value;

    /// <summary>Whether any layer is picked up from another.</summary>
    public bool HasPickups => _tieChild.Length > 0;

    /// <summary>
    /// Every picked-up layer set from its parent: thickness = scale * parent + offset. Run before
    /// each evaluation, so a parent moved by an optimiser or by a person takes its children with
    /// it. A child that a negative offset would take below zero is held at zero, which is the
    /// thinnest a layer can be. Allocates nothing.
    /// </summary>
    public void ApplyTies()
    {
        var t = _layerThickness.Span;
        for (int i = 0; i < _tieChild.Length; ++i)
            t[_tieChild[i]] = Math.Max(0.0, _tieScale[i] * t[_tieParent[i]] + _tieOffset[i]);
    }

    /// <summary>
    /// The spec this Design would be rebuilt from, with the CURRENT thicknesses rather than the
    /// ones it was constructed with. Needle insertion and layer removal change the shape, and a
    /// shape change means a new Design — so the stage that changes it needs the current state as
    /// a spec, not as the stale array it started from.
    /// </summary>
    public DesignSpec ToSpec()
    {
        var thickness = LayerCount > 0 ? _layerThickness.Span.ToArray() : _spec.LayerThickness;
        return new DesignSpec
        {
            LayerThickness = thickness,
            LayerMaterial = _spec.LayerMaterial,
            LayerIsIncoherent = _spec.LayerIsIncoherent,
            MaterialCount = _spec.MaterialCount,
            MaterialIndex = _materialIndex.Span.ToArray(),
            MaterialStress = _spec.MaterialStress,
            MaterialIndexIsExtrapolated = _spec.MaterialIndexIsExtrapolated,
            MaterialDIndexDWavelength = _spec.MaterialDIndexDWavelength,
            MaterialD2IndexDWavelength2 = _spec.MaterialD2IndexDWavelength2,
            MaterialD3IndexDWavelength3 = _spec.MaterialD3IndexDWavelength3,
            Wavelength = _spec.Wavelength,
            PointWavelength = _spec.PointWavelength,
            PointAngle = _spec.PointAngle,
            IndexIncident = _spec.IndexIncident,
            IndexExit = _spec.IndexExit,
            DIndexIncidentDWavelength = _spec.DIndexIncidentDWavelength,
            DIndexExitDWavelength = _spec.DIndexExitDWavelength,
            D2IndexIncidentDWavelength2 = _spec.D2IndexIncidentDWavelength2,
            D2IndexExitDWavelength2 = _spec.D2IndexExitDWavelength2,
            D3IndexIncidentDWavelength3 = _spec.D3IndexIncidentDWavelength3,
            D3IndexExitDWavelength3 = _spec.D3IndexExitDWavelength3,
            IndexIncidentIsExtrapolated = _spec.IndexIncidentIsExtrapolated,
            IndexExitIsExtrapolated = _spec.IndexExitIsExtrapolated,
            Configurations = _spec.Configurations,
            PointConfiguration = _spec.PointConfiguration,
            TrialLayerThickness = _spec.TrialLayerThickness,
            TrialMaterialIndex = _spec.TrialMaterialIndex,
            TrialCount = _spec.TrialCount,
            SubstrateThickness = _spec.SubstrateThickness,
            IndexBack = _spec.IndexBack,
            Operands = _spec.Operands,
            VariableKind = _spec.VariableKind,
            VariableLayer = _spec.VariableLayer,
            VariableMaterial = _spec.VariableMaterial,
            VariableCoefficient = _spec.VariableCoefficient,
            LayerTieParent = _spec.LayerTieParent,
            LayerTieScale = _spec.LayerTieScale,
            LayerTieOffset = _spec.LayerTieOffset,
            Workers = _spec.Workers,
        };
    }

    /// <summary>
    /// The needle map: d(sum of squared residuals)/d(inserting a layer of each candidate material
    /// at each depth), CANDIDATE-MAJOR — map[c * depths.Length + i].
    ///
    /// Allocates, and that is correct: a needle map is not the inner loop. It is asked for once
    /// between refinements, and sizing its buffers to the request is cheaper than carrying the
    /// largest request anyone might make.
    /// </summary>
    public double[] NeedleMap(ReadOnlySpan<double> depths, ReadOnlySpan<int> candidateMaterials)
    {
        RequirePlan();
        if (depths.Length == 0 || candidateMaterials.Length == 0)
            throw new ArgumentException("a needle map needs at least one depth and one candidate");

        var depth = PinnedBuffer<double>.From(depths);
        var cand = PinnedBuffer<int>.From(candidateMaterials);
        var map = new PinnedBuffer<double>((long)depths.Length * candidateMaterials.Length);
        var residual = new PinnedBuffer<double>(_residual.Count);
        var status = new PinnedBuffer<int>(1);

        ApplyTies();
        var request = new TfNeedleRequest
        {
            n_depths = depths.Length,
            depth = depth.Pointer,
            n_candidates = candidateMaterials.Length,
            candidate_material = cand.Pointer,
        };
        var output = new TfNeedleOutput
        {
            residual = residual.Pointer,
            dsumsq_dinsertion = map.Pointer,
            status = status.Pointer,
        };

        Check(NativeMethods.tf_evaluate_needle(_problem.Pointer, _operands.Pointer,
                                               _planBuffers.Pointer, &request,
                                               _workspace.Pointer, &output),
              nameof(NativeMethods.tf_evaluate_needle));

        // HELD TO HERE, AND NOT BY THE POINTERS. The engine has their addresses, but an address
        // in a struct is not a reference: once the optimising JIT saw nothing below read these
        // four, they were garbage for the whole call, and a collection on another thread (the
        // CLI's progress poll, the GUI's timer) freed them while the engine read the depths and
        // wrote the residual. The pinned heap then reused or rethreaded that memory, and the next
        // pinned allocation died - "Internal CLR error" in Design.Plan, after ~70 needle rounds
        // on a 145-layer notch (2026-10-24). The Design is held too: it owns every other buffer
        // the call touches, and nothing below reads a field of it either.
        GC.KeepAlive(depth);
        GC.KeepAlive(cand);
        GC.KeepAlive(residual);
        GC.KeepAlive(status);
        GC.KeepAlive(this);

        return map.Span.ToArray();
    }


    // ---- results ------------------------------------------------------------------------------

    public ReadOnlySpan<double> Value => _value.Span;
    public ReadOnlySpan<double> Residual => _residual.Span;

    /// <summary>[n_residuals * n_variables], COLUMN-MAJOR: J[v * n_residuals + r]. With pickups,
    /// each parent's column already includes its children's (EvaluateJacobian).</summary>
    public ReadOnlySpan<double> Jacobian => _folded is { } f ? f.Span : _jacobian.Span;

    public ReadOnlySpan<int> OperandFlag => _operandFlag.Span;
    public int FlaggedOperandCount => _flaggedCount.Span[0];
    public double Merit => _merit.Span[0];

    /// <summary>[n_trials * n_operands], trial-major. Empty unless TrialValues was asked for.</summary>
    public ReadOnlySpan<double> TrialValue => _trialValue is null ? default : _trialValue.Span;

    public TfPlanReport Report => _report.Span[0];
    public ReadOnlySpan<int> PointPath => _pointPath.Span;
    public ReadOnlySpan<int> VariableInert => _variableInert.Span[..Math.Min(VariableCount, (int)_variableInert.Count)];
    public ReadOnlySpan<int> PointPolarisation => _pointPolarisation.Span;

    // ---- the lifecycle -------------------------------------------------------------------------

    /// <summary>
    /// Runs tf_plan and allocates everything the plan says is needed. Call once; call again only
    /// through RefreshPlan, which exists to say whether anything moved.
    /// </summary>
    /// <param name="keepTrialValues">
    /// Allocate the [n_trials * n_operands] per-trial table. Off by default because it is the only
    /// output whose size grows with the trial count, and the marginal a yield operand returns is
    /// usually the whole of what a caller wants. It is needed for a yield over a SET of specs,
    /// which is a joint and cannot be had from the marginals.
    /// </param>
    public TfPlanReport Plan(bool keepTrialValues = false)
    {
        Check(NativeMethods.tf_plan(_problem.Pointer, _operands.Pointer, _variables.Pointer,
                                    _workers, _planBuffers.Pointer),
              nameof(NativeMethods.tf_plan));

        var rep = _report.Span[0];

        _scratch = new PinnedBuffer<double>(rep.scratch_doubles);
        _residual = new PinnedBuffer<double>(rep.n_residuals);
        _value = new PinnedBuffer<double>(OperandCount);
        _operandFlag = new PinnedBuffer<int>(OperandCount);
        _jacobian = new PinnedBuffer<double>((long)rep.n_residuals * _engineVariableCount);
        _folded = _foldInto.Length > 0 ? new PinnedBuffer<double>((long)rep.n_residuals * VariableCount) : null;
        _trialValue = keepTrialValues && TrialCount > 0
            ? new PinnedBuffer<double>((long)TrialCount * OperandCount)
            : null;

        ref var w = ref _workspace[0];
        w.scratch = _scratch.Pointer;
        w.scratch_doubles = rep.scratch_doubles;

        ref var outp = ref _output[0];
        outp.residual = _residual.Pointer;
        outp.value = _value.Pointer;
        outp.operand_flag = _operandFlag.Pointer;
        outp.n_operands_flagged = _flaggedCount.Pointer;
        outp.trial_value = Ptr(_trialValue);
        outp.merit = _merit.Pointer;
        outp.status = _evalStatus.Pointer;
        outp.jacobian = null;   // set per call: its presence is what selects the entry point

        _planned = true;
        return rep;
    }

    /// <summary>
    /// Re-runs tf_plan and reports whether any of its decisions changed.
    ///
    /// THIS IS THE ANSWER TO TF_PLAN_STALE NOT BEING ENFORCED. The engine declares that status and
    /// raises it nowhere, so nothing refuses an evaluation against a plan whose assumptions have
    /// expired — and they can expire, because tf_plan picks a path per point partly from thickness
    /// (a layer thick enough to absorb everything clamps) and marks variables inert on the same
    /// grounds. An optimiser that drives a layer opaque has changed a plan input without changing
    /// any count.
    ///
    /// It is not automatic. Planning on every iteration would cost what planning once was for, and
    /// for a design that never approaches opacity the answer is always false. Call it when a
    /// thickness has moved far, or once per outer iteration, or in a test.
    /// </summary>
    /// <returns>true if any plan decision moved, in which case the previous results were computed
    /// against assumptions that no longer hold.</returns>
    public bool RefreshPlan()
    {
        RequirePlan();

        var before = _report.Span[0];
        Span<int> pathBefore = stackalloc int[Math.Min(PointCount, 256)];
        bool sampledPath = PointCount <= pathBefore.Length;
        if (sampledPath) _pointPath.Span[..PointCount].CopyTo(pathBefore);

        int inertBefore = 0;
        foreach (var f in _variableInert.Span) inertBefore += f;

        Check(NativeMethods.tf_plan(_problem.Pointer, _operands.Pointer, _variables.Pointer,
                                    _workers, _planBuffers.Pointer),
              nameof(NativeMethods.tf_plan));

        var after = _report.Span[0];
        bool changed =
            after.n_structured != before.n_structured ||
            after.n_general != before.n_general ||
            after.n_incoherent != before.n_incoherent ||
            after.n_unsupported != before.n_unsupported ||
            after.n_variables_inert != before.n_variables_inert ||
            after.max_clamped_layers != before.max_clamped_layers ||
            after.n_points_one_polarisation != before.n_points_one_polarisation ||
            after.scratch_doubles != before.scratch_doubles ||
            after.n_residuals != before.n_residuals;

        if (!changed && sampledPath)
            for (int i = 0; i < PointCount && !changed; ++i)
                changed = _pointPath.Span[i] != pathBefore[i];

        if (!changed)
        {
            int inertAfter = 0;
            foreach (var f in _variableInert.Span) inertAfter += f;
            changed = inertAfter != inertBefore;
        }

        // The scratch is the one buffer whose size the plan can raise. Growing it is the only
        // allocation this method can make, and it makes it rather than returning a stale pointer.
        if (after.scratch_doubles > _scratch.Count)
        {
            _scratch = new PinnedBuffer<double>(after.scratch_doubles);
            ref var w = ref _workspace[0];
            w.scratch = _scratch.Pointer;
            w.scratch_doubles = after.scratch_doubles;
        }

        return changed;
    }

    /// <summary>Values, residuals and merit. Allocates nothing.</summary>
    public void Evaluate()
    {
        RequirePlan();
        ApplyTies();
        _output[0].jacobian = null;
        Check(NativeMethods.tf_evaluate(_problem.Pointer, _operands.Pointer, _planBuffers.Pointer,
                                        _workspace.Pointer, _output.Pointer),
              nameof(NativeMethods.tf_evaluate));
        // Nothing here reads a field after the call, so without this a caller's last use of a
        // Design could free its buffers mid-evaluation - the hole NeedleMap fell into.
        GC.KeepAlive(this);
    }

    /// <summary>Everything Evaluate gives, and the Jacobian. Allocates nothing.</summary>
    public void EvaluateJacobian()
    {
        RequirePlan();
        if (VariableCount == 0)
            throw new InvalidOperationException(
                "a Jacobian was asked for on a Design with no variables — there are no columns to " +
                "fill. Declare the variables in the spec, or call Evaluate.");

        ApplyTies();
        _output[0].jacobian = _jacobian.Pointer;
        Check(NativeMethods.tf_evaluate_jacobian(_problem.Pointer, _operands.Pointer,
                                                 _variables.Pointer, _planBuffers.Pointer,
                                                 _workspace.Pointer, _output.Pointer),
              nameof(NativeMethods.tf_evaluate_jacobian));
        FoldTies();
    }

    /// <summary>The chain rule for pickups: each tied layer's column, times its scale, added to its
    /// parent's. A child held at zero by ApplyTies still folds; at that one point the derivative is
    /// one-sided, and the optimiser's next step settles which side it is on.</summary>
    private void FoldTies()
    {
        if (_folded is not { } folded) return;
        int m = _report.Span[0].n_residuals;
        var engine = _jacobian.Span;
        var into = folded.Span;
        engine[..(m * VariableCount)].CopyTo(into);
        for (int e = 0; e < _foldInto.Length; ++e)
        {
            var child = engine.Slice((VariableCount + e) * m, m);
            var parent = into.Slice(_foldInto[e] * m, m);
            for (int r = 0; r < m; ++r) parent[r] += _foldScale[e] * child[r];
        }
    }

    // ---- plumbing --------------------------------------------------------------------------

    private void RequirePlan()
    {
        if (!_planned)
            throw new InvalidOperationException(
                "Plan() has not been run, so no buffer has been sized and no path has been chosen. " +
                "The plan is what says how much scratch the evaluators need.");
    }

    private static void Check(TfStatus st, string call)
    {
        if (st != TfStatus.TF_OK) throw new ThinFilmException(st, call);
    }

    private PinnedBuffer<T> Copy<T>(T[]? source) where T : unmanaged
    {
        var b = PinnedBuffer<T>.From(source ?? System.Array.Empty<T>());
        _held.Add(b);
        return b;
    }

    /// <summary>Null in, null out — the ABI's own convention, kept rather than translated.</summary>
    private PinnedBuffer<T>? Optional<T>(T[]? source) where T : unmanaged
    {
        if (source is null || source.Length == 0) return null;
        return Copy(source);
    }

    private static T* Ptr<T>(PinnedBuffer<T>? b) where T : unmanaged => b is null ? null : b.Pointer;

    private void Validate(DesignSpec spec)
    {
        void Require(bool ok, string what)
        {
            if (!ok) throw new ArgumentException(what, nameof(spec));
        }

        Require(WavelengthCount > 0, "a problem needs at least one wavelength");
        Require(PointCount > 0, "a problem needs at least one point");
        Require(OperandCount > 0, "a problem needs at least one operand");
        Require(MaterialCount > 0, "n_materials must be positive even for a bare substrate");

        Require(spec.PointWavelength.Length == PointCount,
                "PointWavelength and PointAngle must have one entry per point");
        Require(spec.MaterialIndex.Length == (long)MaterialCount * WavelengthCount,
                $"MaterialIndex must be [n_materials * n_wavelengths] = " +
                $"{(long)MaterialCount * WavelengthCount}, material-major; got {spec.MaterialIndex.Length}");

        if (LayerCount > 0)
            Require(spec.LayerMaterial is not null && spec.LayerMaterial.Length == LayerCount,
                    "LayerMaterial must have one entry per layer");

        var o = spec.Operands;
        Require(o.Target.Length == OperandCount && o.Weight.Length == OperandCount,
                "Code, Target and Weight must have one entry per operand");

        bool spanMode = o.PointCount is not null;
        Require(!(spanMode && o.Point is not null),
                "an operand table is pointwise or span, never both: Point and PointCount cannot " +
                "both be given");
        Require(spanMode || o.Point is not null,
                "a pointwise table needs Point; a span table needs PointFirst, PointCount and PointIndex");
        if (spanMode)
            Require(o.PointFirst is not null && o.PointIndex is not null,
                    "a span table needs PointFirst and PointIndex alongside PointCount");

        Require((spec.Configurations is null) == (spec.PointConfiguration is null),
                "Configurations and PointConfiguration are given together or not at all");
        if (spec.Configurations is null)
            Require(spec.IndexIncident is not null && spec.IndexExit is not null,
                    "the one-configuration form needs IndexIncident and IndexExit");
        else
            Require(spec.IndexIncident is null && spec.IndexExit is null,
                    "with Configurations the problem-wide media must be absent — the two forms do not mix");

        if (spec.TrialCount > 0 && spec.TrialLayerThickness is { } tt)
            Require(tt.Length == (long)spec.TrialCount * Math.Max(LayerCount, 1) ||
                    LayerCount == 0,
                    "TrialLayerThickness must be [n_trials * n_layers], trial-major");

        Require(double.IsFinite(spec.SubstrateThickness) && spec.SubstrateThickness >= 0.0,
                "SubstrateThickness must be zero (no back surface) or a positive number of nanometres");
        if (spec.IndexBack is { } back)
            Require(back.Length == WavelengthCount, "IndexBack must have one entry per wavelength");

        if (VariableCount > 0)
            Require(spec.VariableLayer is not null && spec.VariableLayer.Length == VariableCount,
                    "VariableLayer must have one entry per variable");

        if (spec.LayerTieParent is { } tie)
        {
            Require(tie.Length == LayerCount && spec.LayerTieScale?.Length == LayerCount
                    && spec.LayerTieOffset?.Length == LayerCount,
                    "LayerTieParent, LayerTieScale and LayerTieOffset must have one entry per layer");
            for (int c = 0; c < tie.Length; ++c)
            {
                if (tie[c] < 0) continue;
                Require(tie[c] < LayerCount && tie[c] != c,
                        $"layer {c} is tied to layer {tie[c]}, which is not another layer of the stack");
                Require(tie[tie[c]] < 0,
                        $"layer {c} is tied to layer {tie[c]}, which is itself tied; a pickup names a free layer");
                Require(spec.VariableLayer is null || Array.IndexOf(spec.VariableLayer, c) < 0,
                        $"layer {c} is tied, so it cannot also be a variable");
            }
        }
    }
}
