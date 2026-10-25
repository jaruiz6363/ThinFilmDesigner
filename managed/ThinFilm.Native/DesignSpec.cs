// DesignSpec.cs — what a Design is built from.
//
// THE ARRAYS MIRROR THE ABI ONE FOR ONE: same names, same shapes, same "null means absent". That
// is deliberate and it is the whole reason this type is dull. A friendlier model — a list of Layer
// objects, a Material that knows its own dispersion — has to be flattened into these arrays by
// somebody, and whoever does that flattening is choosing index orders and array strides. Doing it
// here would put that choice inside the layer whose job is to own memory, where a mistake looks
// like a working program returning the wrong number.
//
// So Design validates and copies, and the friendly modelling belongs above it, where getting a
// stride wrong is a bug in a builder rather than a bug in the allocator.

namespace ThinFilm.Native;

/// <summary>
/// The problem, the operand table and the variables, as flat arrays in the ABI's own layout.
/// Everything nullable is optional in exactly the sense the header means: null is absent, not zero.
/// </summary>
public sealed class DesignSpec
{
    // ---- the stack ------------------------------------------------------------------------
    /// <summary>[n_layers]. Null or empty is a bare substrate, which is a legitimate problem.</summary>
    public double[]? LayerThickness { get; init; }

    /// <summary>[n_layers] -> material index.</summary>
    public int[]? LayerMaterial { get; init; }

    /// <summary>[n_layers] non-zero marks an incoherent layer. Refused by the engine for now.</summary>
    public int[]? LayerIsIncoherent { get; init; }

    // ---- pickups: a layer's thickness tied to another's -------------------------------------
    /// <summary>
    /// [n_layers] the layer each layer's thickness is picked up from, or -1. Null is no pickups.
    /// A tied layer is thickness[c] = TieScale[c] * thickness[parent] + TieOffset[c], in
    /// nanometres; whatever unit a person wrote the pickup in has been folded into the two
    /// numbers before they get here. NOT IN THE ABI: the engine never sees a tie, only the
    /// thicknesses it produces and an extra variable column that Design folds back (Design.cs).
    /// </summary>
    public int[]? LayerTieParent { get; init; }
    public double[]? LayerTieScale { get; init; }
    public double[]? LayerTieOffset { get; init; }

    // ---- materials ------------------------------------------------------------------------
    /// <summary>[n_materials * n_wavelengths], MATERIAL-MAJOR.</summary>
    public required TfComplex[] MaterialIndex { get; init; }

    /// <summary>How many materials MaterialIndex describes. The wavelength count comes from Wavelength.</summary>
    public required int MaterialCount { get; init; }

    /// <summary>[n_materials]. Null means no stress data — a STRESS operand then refuses.</summary>
    public double[]? MaterialStress { get; init; }

    /// <summary>[n_materials * n_wavelengths]. Non-zero marks a synthesised index value.</summary>
    public int[]? MaterialIndexIsExtrapolated { get; init; }

    /// <summary>[n_materials * n_wavelengths]. Required only by the wavelength derivative.</summary>
    public TfComplex[]? MaterialDIndexDWavelength { get; init; }

    /// <summary>[n_materials * n_wavelengths], d2n/dlambda2. Required only by the GDD operands.</summary>
    public TfComplex[]? MaterialD2IndexDWavelength2 { get; init; }

    /// <summary>[n_materials * n_wavelengths], d3n/dlambda3. Required only by the TOD operands.</summary>
    public TfComplex[]? MaterialD3IndexDWavelength3 { get; init; }

    // ---- the sampling ---------------------------------------------------------------------
    public required double[] Wavelength { get; init; }
    public required int[] PointWavelength { get; init; }
    public required double[] PointAngle { get; init; }

    // ---- media: the one-configuration form ------------------------------------------------
    /// <summary>[n_wavelengths]. Both required unless Configurations is given.</summary>
    public TfComplex[]? IndexIncident { get; init; }
    public TfComplex[]? IndexExit { get; init; }
    public TfComplex[]? DIndexIncidentDWavelength { get; init; }
    public TfComplex[]? DIndexExitDWavelength { get; init; }
    public TfComplex[]? D2IndexIncidentDWavelength2 { get; init; }
    public TfComplex[]? D2IndexExitDWavelength2 { get; init; }
    public TfComplex[]? D3IndexIncidentDWavelength3 { get; init; }
    public TfComplex[]? D3IndexExitDWavelength3 { get; init; }
    public int[]? IndexIncidentIsExtrapolated { get; init; }
    public int[]? IndexExitIsExtrapolated { get; init; }

    // ---- media: many configurations over one stack ----------------------------------------
    /// <summary>Non-null switches to the configuration form, where the two arrays above must be null.</summary>
    public ConfigurationSpec? Configurations { get; init; }

    /// <summary>[n_points] -> configuration. Required with Configurations, forbidden without.</summary>
    public int[]? PointConfiguration { get; init; }

    // ---- the Monte Carlo trial axis -------------------------------------------------------
    /// <summary>[n_trials * n_layers], trial-major. Null means no thickness is perturbed.</summary>
    public double[]? TrialLayerThickness { get; init; }

    /// <summary>[n_trials * n_materials * n_wavelengths]. Null means no index is perturbed.</summary>
    public TfComplex[]? TrialMaterialIndex { get; init; }

    /// <summary>0 means there is no trial axis at all.</summary>
    public int TrialCount { get; init; }

    // ---- the substrate's back surface --------------------------------------------------
    /// <summary>Nanometres. 0, the default, is a semi-infinite substrate with no back surface;
    /// anything positive adds the uncoated back surface incoherently to R and T (ABI 10).</summary>
    public double SubstrateThickness { get; init; }

    /// <summary>[n_wavelengths], the medium behind the back surface. Null means the incident medium.</summary>
    public TfComplex[]? IndexBack { get; init; }

    // ---- the operand table ----------------------------------------------------------------
    public required OperandSpec Operands { get; init; }

    // ---- the variables --------------------------------------------------------------------
    /// <summary>[n_variables] tf_variable_kind. Null is a problem with nothing to optimise, which
    /// is legal: tf_evaluate wants no variables at all.</summary>
    public int[]? VariableKind { get; init; }
    public int[]? VariableLayer { get; init; }
    public int[]? VariableMaterial { get; init; }
    public int[]? VariableCoefficient { get; init; }

    /// <summary>Workers for tf_plan. One unless something above has measured that more is faster.</summary>
    public int Workers { get; init; } = 1;
}

/// <summary>The media, per configuration. [n_configurations * n_wavelengths], configuration-major.</summary>
public sealed class ConfigurationSpec
{
    public required int Count { get; init; }
    public required TfComplex[] IndexIncident { get; init; }
    public required TfComplex[] IndexExit { get; init; }
    public TfComplex[]? DIndexIncidentDWavelength { get; init; }
    public TfComplex[]? DIndexExitDWavelength { get; init; }
    public int[]? IndexIncidentIsExtrapolated { get; init; }
    public int[]? IndexExitIsExtrapolated { get; init; }

    /// <summary>[n_configurations] non-zero traverses the stack back to front.</summary>
    public int[]? Reversed { get; init; }
}

/// <summary>
/// The operand table, in either of the two forms the ABI accepts: pointwise, where Point names one
/// point per operand, or span, where PointFirst/PointCount/PointIndex give each operand a run of
/// them. The two do not mix, and Design refuses a table that tries.
/// </summary>
public sealed class OperandSpec
{
    public required int[] Code { get; init; }
    public required double[] Target { get; init; }
    public required double[] Weight { get; init; }

    /// <summary>[n_operands] -> point. The pointwise form; null in span mode.</summary>
    public int[]? Point { get; init; }

    // ---- span mode ---------------------------------------------------------------------
    public int[]? PointFirst { get; init; }
    public int[]? PointCount { get; init; }
    public int[]? PointIndex { get; init; }
    public double[]? PointWeight { get; init; }

    // ---- comparisons -------------------------------------------------------------------
    public int[]? Comparison { get; init; }
    public double[]? TargetHi { get; init; }
    public double[]? Tolerance { get; init; }
    public double[]? Scale { get; init; }

    // ---- reductions --------------------------------------------------------------------
    public int[]? Reduction { get; init; }

    /// <summary>[n_operands * 2] — sharpness and a second parameter, per operand.</summary>
    public double[]? ReductionParam { get; init; }

    // ---- operands that read operands ----------------------------------------------------
    public int[]? InputFirst { get; init; }
    public int[]? InputCount { get; init; }
    public int[]? InputIndex { get; init; }

    // ---- the trial axis -----------------------------------------------------------------
    public int[]? TrialReduction { get; init; }

    // ---- table-wide settings --------------------------------------------------------------
    public int DeltaConvention { get; init; } = (int)TfDeltaConvention.TF_DELTA_PRINCIPAL;
    public int ExtrapolationPolicy { get; init; } = (int)TfExtrapolationPolicy.TF_EXTRAPOLATION_FLAG;
}
