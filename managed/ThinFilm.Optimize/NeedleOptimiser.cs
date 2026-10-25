// NeedleOptimiser.cs — the stage the specification calls the one that makes synthesis work.
//
// "Needle optimization and gradual evolution are the algorithms that make thin film synthesis
// work." The engine supplies the hard half: tf_evaluate_needle returns
// d(sum of squared residuals)/d(inserting a layer of thickness eps), for every candidate material
// at every depth, analytically. What is left here is a search over that map and the bookkeeping of
// actually splitting a layer.
//
// THE MAP IS A DERIVATIVE, SO THE MOST NEGATIVE ENTRY IS THE BEST INSERTION, and it is worth being
// clear about what that does and does not promise. It is a first-order statement about an
// infinitesimal layer. A needle of real thickness is a finite step along a direction the map only
// describes at zero, so the map RANKS candidates and the refinement DECIDES: every insertion is
// followed by a damped least squares run, and an insertion that does not survive refinement is
// rejected and rolled back.
//
// WHICH IS WHY MINT CONTRIBUTES NOTHING TO THE MAP. A layer is born at zero thickness, in full
// violation of any minimum, and MINT's slope there is -1 — feeding that in would make every
// insertion look a constant amount cheaper. Its discouragement is a step at eps = 0+, and a
// first-order map cannot represent a step; it belongs to the accept/reject decision below, which
// is a discrete stage that CAN weigh a jump. The native side documents this at
// tf_stack_insertion_slope; the consequence for this file is that the accept test is the real
// guard and the map is only a proposal.

using ThinFilm.Native;

namespace ThinFilm.Optimize;

public sealed class NeedleOptions
{
    /// <summary>How many insertions to attempt. Each is followed by a refinement.</summary>
    public int MaxInsertions { get; init; } = 8;

    /// <summary>The most layers the stack may have; 0 is no limit. An insertion inside a layer adds two
    /// (it splits its host), one at a boundary or a face adds one, and one that would pass the limit is
    /// not a candidate. Gradual evolution's Max layers: it stopped only the growth, and needles took
    /// a 12-layer limit to 16 (the user, 2026-10-25: "make max_layers a hard cap").</summary>
    public int MaxLayers { get; init; }

    /// <summary>Depths sampled per unit of total thickness. The map is smooth in depth, so this
    /// buys resolution rather than accuracy.</summary>
    public int DepthSamples { get; init; } = 200;

    /// <summary>The thickness a needle is born with. Not zero: a zero-thickness layer moves
    /// nothing and the refinement would have no gradient to pull on.</summary>
    public double SeedThickness { get; init; } = 1.0;

    /// <summary>Candidate materials, by index. Null means every material in the design.</summary>
    public int[]? Candidates { get; init; }

    /// <summary>An insertion is kept only if it improves the merit by at least this fraction.</summary>
    public double MinimumImprovement { get; init; } = 1e-9;

    /// <summary>
    /// How negative a slope has to be, as a fraction of the largest magnitude in the map, before
    /// it counts as an improvement worth trying.
    ///
    /// WITHOUT THIS THE STAGE CHASES ROUNDING. Inserting a candidate that is the SAME material as
    /// its host is a no-op — it just grows the layer — so its true slope at an optimum is zero,
    /// and what comes back is the last bits of a cancellation: about -7e-11 against a map whose
    /// real entries are 6e-4. Taken at face value that is the most negative entry in the map, so
    /// the stage would insert a layer that changes nothing, fail to improve, and stop. The design
    /// was needle-stationary and the answer was right; only the noise made it look otherwise.
    /// </summary>
    public double SlopeFloor { get; init; } = 1e-6;

    public DlsOptions Refinement { get; init; } = new();

    /// <summary>Refine the design before the first map (Refined is told the merit it reached).</summary>
    public bool RefineFirst { get; init; } = true;

    /// <summary>
    /// Stop once the merit is below this fraction of the refined design needle began with: the targets are met and
    /// nothing is left to gain. Past that point the map is rounding noise and a 1 nm needle only
    /// disturbs the design (Dichroic: 7.9e-9 after two insertions, a third refined back to 2.3e-2
    /// and rolled back; the user, 2026-10-17, asked for the stop).
    /// </summary>
    public double TargetMet { get; init; } = 1e-6;
    public Action<double>? Refined { get; init; }

    /// <summary>Called after each insertion is refined and judged. Stopping is heard through
    /// Refinement.Cancellation, between insertions and inside the refinement running.</summary>
    public Action<NeedleStep>? StepDone { get; init; }
}

/// <summary>One insertion tried (from 1): a needle of Material placed at Depth nm below the incident
/// side, the merit after refining it, whether it was kept, and the layer count it would give.</summary>
public readonly record struct NeedleStep(int Round, int Material, double Depth, double Merit, bool Kept, int Layers);

public readonly record struct NeedleResult(
    int Insertions, int Rejected, double InitialMerit, double FinalMerit, Design Design,
    bool NeedleStationary = false, string? Stopped = null, bool TargetMet = false);

public static class NeedleOptimiser
{
    /// <summary>
    /// Inserts layers where the needle map says they help, refining after each and rolling back
    /// any that does not survive. Returns a NEW Design whenever the stack changed, because a layer
    /// count is a shape and a shape change re-sizes every buffer.
    /// </summary>
    public static NeedleResult Run(Design design, NeedleOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(design);
        options ??= new NeedleOptions();

        design.Evaluate();
        double initial = design.Merit;

        // REFINED FIRST. The map is a statement about a design at a stationary point; one that is
        // not is still asking for thicker or thinner layers, and those slopes swamp the needles.
        if (options.RefineFirst && design.VariableCount > 0)
        {
            new DampedLeastSquares(design.VariableCount, design.Report.n_residuals, options.Refinement)
                .Minimise(design, options.Refinement);
            options.Refined?.Invoke(design.Merit);
        }
        double best = design.Merit;
        double reference = best;     // TargetMet is measured from the refined design needle began with
        int inserted = 0, rejected = 0;
        bool stationary = false;
        string? stopped = null;
        bool targetMet = false;

        for (int round = 0; round < options.MaxInsertions; ++round)
        {
            if (options.Refinement.Cancellation.IsCancellationRequested) break;
            if (best <= reference * options.TargetMet) { targetMet = true; break; }
            if (options.MaxLayers > 0 && design.LayerCount >= options.MaxLayers)
            { stopped = $"the stack has {design.LayerCount} layers, the most allowed"; break; }
            var spec = design.ToSpec();
            double total = 0.0;
            foreach (var t in spec.LayerThickness ?? []) total += t;
            // AN EMPTY STACK still takes a needle: at depth 0 it is the first real layer. Breaking
            // here, as this did, ended the stage silently when the refinement had thinned a single
            // start layer to nothing (a QWOT of SiO2 on a PBS, 2026-10-24: "needles did not refine").

            var candidates = options.Candidates ?? Enumerable.Range(0, design.MaterialCount).ToArray();

            // INCLUDING BOTH FACES. An insertion at z = 0 or z = total is a new outer layer
            // rather than a split, which is a perfectly good needle move and often the one that
            // helps most — an AR stack grows at its ends. Excluding them, as this did at first,
            // quietly removes the two most useful candidates from every map.
            int nd = Math.Max(2, options.DepthSamples);
            var depths = new double[nd];
            // Clamped: total * i / (nd - 1) can round a hair past the total at the last sample, and
            // the engine refuses a depth outside the stack (TF_INDEX_OUT_OF_RANGE), which ended the
            // stage after seven insertions on Bandpass.
            for (int i = 0; i < nd; ++i) depths[i] = Math.Max(0.0, Math.Min(total, total * i / (nd - 1.0)));

            double[] map;
            try { map = design.NeedleMap(depths, candidates); }
            // An opaque layer, or a reversed configuration: the map is refused, and the stage says so.
            catch (ThinFilmException e) { stopped = $"the needle map was refused: {e.Message}"; break; }

            double scale = 0.0;
            foreach (var s in map) scale = Math.Max(scale, Math.Abs(s));

            // A NEEDLE OF THE HOST'S OWN MATERIAL IS NOT A NEEDLE: it splits the layer and makes it
            // thicker. Its slope is the layer's thickness gradient, zero only at a fully refined
            // design, and taking it inserted nothing - on Bandpass all ten "insertions" were such
            // splits (37 layers that evolve merged straight back to 17; the user, 2026-10-17:
            // "needles ... worthless"). So the host material, and both neighbours on a boundary,
            // are never candidates.
            var thicknesses = spec.LayerThickness ?? [];
            var layerMaterials = spec.LayerMaterial ?? [];
            var varied = Varied(spec);
            int bestCandidate = -1, bestDepth = -1;
            double bestSlope = -options.SlopeFloor * scale;   // the noise floor, not zero
            for (int i = 0; i < nd; ++i)
            {
                // NOT INSIDE A FIXED LAYER, nor inside a layer of a material needle may not insert:
                // a needle there splits it in two, and the second piece is a new layer of it. The
                // guide's wide-angle AR (2026-10-24) kept MgF2 out of the needles, and a TiO2 needle
                // in its outer MgF2 still put MgF2 inside the stack; a fixed MgF2 layer was varied.
                int inside = Inside(thicknesses, depths[i]);
                if (options.MaxLayers > 0 && design.LayerCount + (inside >= 0 ? 2 : 1) > options.MaxLayers) continue;
                if (inside >= 0 && (!varied[inside]
                                    || (options.Candidates is not null && !candidates.Contains(layerMaterials[inside]))))
                    continue;
                var hosts = Hosts(thicknesses, layerMaterials, depths[i]);
                for (int c = 0; c < candidates.Length; ++c)
                {
                    if (hosts.Contains(candidates[c])) continue;
                    double s = map[c * nd + i];
                    if (s < bestSlope) { bestSlope = s; bestCandidate = c; bestDepth = i; }
                }
            }

            // NO INSERTION HELPS TO FIRST ORDER, and that is a real answer rather than a failure.
            // A refined single-layer AR is genuinely needle-stationary: every slope in its map is
            // positive except the cancellation noise of a same-material candidate. Stopping here
            // is the stage saying so.
            if (bestCandidate < 0) { stationary = true; break; }

            var trialSpec = InsertLayer(spec, depths[bestDepth], candidates[bestCandidate],
                                        options.SeedThickness, options.Candidates);
            var trial = new Design(trialSpec);
            trial.Plan();
            if (options.MaxLayers > 0 && trial.LayerCount > options.MaxLayers)
            { stopped = $"an insertion would make {trial.LayerCount} layers, more than the {options.MaxLayers} allowed"; break; }

            // THE BOUNDS FOLLOW THE SHAPE. An insertion adds variables, so bounds built for the
            // stack that went in do not describe the one that came out. Widened to the extremes
            // of what was asked, because a new layer has no bound of its own and the honest
            // default is the loosest the caller already permitted.
            var refinement = Bounds.For(options.Refinement, trial.VariableCount);
            var dls = new DampedLeastSquares(trial.VariableCount, trial.Report.n_residuals,
                                             refinement);
            dls.Minimise(trial, refinement);

            bool kept = trial.Merit < best * (1.0 - options.MinimumImprovement);
            options.StepDone?.Invoke(new NeedleStep(round + 1, candidates[bestCandidate], depths[bestDepth],
                                                    trial.Merit, kept, trial.LayerCount));
            if (kept)
            {
                best = trial.Merit;
                design = trial;
                ++inserted;
            }
            else
            {
                // ROLLED BACK. The map promised an improvement to first order and the refinement
                // did not deliver one, which is the case the first-order map cannot see coming —
                // including MINT's step, which is exactly what this test is standing in for.
                ++rejected;
                break;
            }
        }

        design.Evaluate();
        return new NeedleResult(inserted, rejected, initial, design.Merit, design, stationary, stopped, targetMet);
    }

    /// <summary>The materials a needle at <paramref name="depth"/> would only extend: every layer
    /// that touches it (within 1e-6 nm) - the layer it lies in, the two either side of a boundary,
    /// and any layer of zero thickness there together with the layers beyond it.
    ///
    /// ZERO-THICKNESS LAYERS ARE SEEN THROUGH. Refinement can drive a layer to its bound of 0 nm,
    /// and a needle beside it is a needle beside the next layer: the guide's beam splitter
    /// (2026-10-23) had its outer SiO2 at 0 nm, and "TiO2 at 0 nm" was inserted 27 times - each
    /// one a sliver of TiO2 in front of the TiO2 already there, the outer layer cut into pieces,
    /// 40 s of insertions that evolution then spent 20 s merging back.</summary>
    /// <summary>The layer <paramref name="depth"/> lies strictly inside, more than 1e-6 nm from
    /// both its faces; -1 on a boundary or outside the stack.</summary>
    private static int Inside(double[] thickness, double depth)
    {
        double z = 0.0;
        for (int i = 0; i < thickness.Length; ++i)
        {
            double end = z + thickness[i];
            if (depth > z + 1e-6 && depth < end - 1e-6) return i;
            z = end;
        }
        return -1;
    }

    /// <summary>Which layers' thicknesses are variables, by layer. Every shape change carries
    /// these through - an insertion, a growth, a removal - so a layer the user fixed stays fixed.
    /// Rebuilding with every layer varied, as needle, evolve and remove thin layers did, freed it.</summary>
    internal static bool[] Varied(DesignSpec spec)
    {
        var varied = new bool[(spec.LayerThickness ?? []).Length];
        var layer = spec.VariableLayer ?? [];
        var kind = spec.VariableKind;
        for (int v = 0; v < layer.Length; ++v)
            if ((kind is null || v >= kind.Length || kind[v] == (int)TfVariableKind.TF_VARIABLE_LAYER_THICKNESS)
                && layer[v] >= 0 && layer[v] < varied.Length)
                varied[layer[v]] = true;
        return varied;
    }

    /// <summary>The indices of the varied layers, for Rebuild.</summary>
    internal static int[] Indices(IReadOnlyList<bool> varied) =>
        Enumerable.Range(0, varied.Count).Where(i => varied[i]).ToArray();

    private static HashSet<int> Hosts(double[] thickness, int[] material, double depth)
    {
        var hosts = new HashSet<int>();
        double z = 0.0;
        for (int i = 0; i < thickness.Length; ++i)
        {
            double end = z + thickness[i];
            if (z <= depth + 1e-6 && end >= depth - 1e-6) hosts.Add(material[i]);
            z = end;
        }
        if (hosts.Count == 0 && thickness.Length > 0) hosts.Add(material[^1]);
        return hosts;
    }

    /// <summary>
    /// Splits the layer containing `depth` into two of the same material with a needle between
    /// them. The split is what makes an insertion continuous: M_j(d) = M_j(t) * M_j(d - t), so a
    /// needle of zero thickness leaves the stack exactly as it was.
    /// </summary>
    private static DesignSpec InsertLayer(DesignSpec spec, double depth, int material, double seed,
                                          int[]? allowed = null)
    {
        var thickness = spec.LayerThickness ?? [];
        var layerMaterial = spec.LayerMaterial ?? [];

        int host = 0;
        double walked = 0.0;
        for (; host < thickness.Length; ++host)
        {
            if (depth <= walked + thickness[host]) break;
            walked += thickness[host];
        }
        if (host >= thickness.Length) host = thickness.Length - 1;

        double before = Math.Max(0.0, depth - walked);
        double after = Math.Max(0.0, thickness[host] - before);

        // Each piece of the host keeps its vary flag, and the needle varies. A needle is never
        // inside a fixed layer (Run), only on its face. The empty piece a needle on a face leaves
        // is dropped when the host is fixed, or of a material needle may not insert: refined, it
        // would grow into a new layer of it (a TiO2 needle on the outer face of the wide-angle
        // AR's MgF2 left an empty MgF2 in front, which grew, and MgF2 was inside the stack).
        var varied = Varied(spec);
        bool keepEmpty = varied[host] && (allowed is null || allowed.Contains(layerMaterial[host]));
        var t = new List<double>(thickness.Length + 2);
        var m = new List<int>(thickness.Length + 2);
        var v = new List<bool>(thickness.Length + 2);
        for (int i = 0; i < thickness.Length; ++i)
        {
            if (i != host) { t.Add(thickness[i]); m.Add(layerMaterial[i]); v.Add(varied[i]); continue; }
            if (keepEmpty || before > 0.0) { t.Add(before); m.Add(layerMaterial[i]); v.Add(varied[i]); }
            t.Add(seed); m.Add(material); v.Add(true);
            if (keepEmpty || after > 0.0) { t.Add(after); m.Add(layerMaterial[i]); v.Add(varied[i]); }
        }

        return Rebuild(spec, t.ToArray(), m.ToArray(), Indices(v));
    }

    /// <summary>
    /// A spec with a new layer table, and a variable per layer named in <paramref name="varied"/>
    /// (every layer when null). Callers changing the shape pass the flags they carried (Varied).
    /// </summary>
    internal static DesignSpec Rebuild(DesignSpec spec, double[] thickness, int[] material,
                                       IReadOnlyList<int>? varied = null)
    {
        // Only the layers named, when they are named: raising a sliver fixes it, and the rest vary.
        var layer = varied?.ToArray() ?? Enumerable.Range(0, thickness.Length).ToArray();
        var kind = new int[layer.Length];
        Array.Fill(kind, (int)TfVariableKind.TF_VARIABLE_LAYER_THICKNESS);

        return new DesignSpec
        {
            LayerThickness = thickness,
            LayerMaterial = material,
            MaterialCount = spec.MaterialCount,
            MaterialIndex = spec.MaterialIndex,
            MaterialStress = spec.MaterialStress,
            MaterialIndexIsExtrapolated = spec.MaterialIndexIsExtrapolated,
            MaterialDIndexDWavelength = spec.MaterialDIndexDWavelength,
            MaterialD2IndexDWavelength2 = spec.MaterialD2IndexDWavelength2,
            MaterialD3IndexDWavelength3 = spec.MaterialD3IndexDWavelength3,
            Wavelength = spec.Wavelength,
            PointWavelength = spec.PointWavelength,
            PointAngle = spec.PointAngle,
            IndexIncident = spec.IndexIncident,
            IndexExit = spec.IndexExit,
            DIndexIncidentDWavelength = spec.DIndexIncidentDWavelength,
            DIndexExitDWavelength = spec.DIndexExitDWavelength,
            D2IndexIncidentDWavelength2 = spec.D2IndexIncidentDWavelength2,
            D2IndexExitDWavelength2 = spec.D2IndexExitDWavelength2,
            D3IndexIncidentDWavelength3 = spec.D3IndexIncidentDWavelength3,
            D3IndexExitDWavelength3 = spec.D3IndexExitDWavelength3,
            IndexIncidentIsExtrapolated = spec.IndexIncidentIsExtrapolated,
            IndexExitIsExtrapolated = spec.IndexExitIsExtrapolated,
            Configurations = spec.Configurations,
            PointConfiguration = spec.PointConfiguration,
            TrialCount = 0,          // a perturbed stack table cannot survive a shape change
            SubstrateThickness = spec.SubstrateThickness,
            IndexBack = spec.IndexBack,
            Operands = spec.Operands,
            VariableKind = kind,
            VariableLayer = layer,
            VariableMaterial = new int[layer.Length],
            VariableCoefficient = new int[layer.Length],
            Workers = spec.Workers,
        };
    }
}
