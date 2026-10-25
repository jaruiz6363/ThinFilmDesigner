// GradualEvolution.cs — the other half of synthesis: taking layers OUT.
//
// Needle adds; this removes. They are named together in the specification because neither works
// alone: needle inserts freely and the stack accumulates layers that refinement has driven to a
// nanometre and then cannot delete, because deleting is a discrete act and a gradient stage can
// only ever approach zero thickness asymptotically.
//
// THE STEP A GRADIENT CANNOT TAKE is exactly the one here. A layer at 0.3 nm contributes almost
// nothing optically and everything practically: it is a separate deposition, a separate stop, a
// separate chance to get the thickness wrong. MINT expresses that as a penalty, and the penalty's
// discouragement is a STEP at zero thickness that the needle map is documented as unable to see.
// This is the stage that can see it, because removing a layer is not a step along a direction —
// it is a change of shape, evaluated by trying it.
//
// EVERY REMOVAL IS TRIED AND KEPT ONLY IF IT SURVIVES REFINEMENT. There is no threshold below
// which a layer is assumed to be removable: the optically dead 0.3 nm layer and the 0.3 nm layer
// that is doing something both exist, and the only way to tell them apart is to take it out,
// re-optimise what remains, and compare. A rule of thumb would be faster and would sometimes
// delete the wrong layer, silently, in a design somebody then manufactures.

using ThinFilm.Native;

namespace ThinFilm.Optimize;

public sealed class EvolutionOptions
{
    /// <summary>Layers thinner than this are CANDIDATES for removal — not removals.</summary>
    public double MinimumThickness { get; init; } = 2.0;

    /// <summary>
    /// How much worse the merit may get and still count as a good trade. Removing a layer usually
    /// costs a little performance and buys a lot of manufacturability, so a small positive value
    /// is the honest setting; zero demands the removal be free.
    /// </summary>
    public double AllowedDegradation { get; init; } = 0.05;

    /// <summary>
    /// A merit that is good enough whatever it was before: a removal that leaves the merit at or
    /// under this is kept even if it is more than AllowedDegradation worse. Without it a design
    /// that meets its spec cannot lose a layer, because 5% of a merit of 1e-15 is nothing - the
    /// user's narrow bandpass, 2026-10-23, kept a 4.6 nm layer whose removal cost a merit of
    /// 0.028. Zero turns it off.
    /// </summary>
    public double AcceptMerit { get; init; }

    public int MaxRemovals { get; init; } = 16;

    /// <summary>
    /// EVERY LAYER UNDER THE MINIMUM GOES, whatever it costs: the step before a design is made.
    /// The judged removal keeps a layer that is earning its place, and at a merit near zero every
    /// layer is, so slivers of under a nanometre survived to the end of a dichroic that met its
    /// spec (the user guide, 2026-10-24). Forced, each removal is still refined and logged with its
    /// merit, so the price of a design that can be made is there to read.
    /// </summary>
    public bool Force { get; init; }

    public DlsOptions Refinement { get; init; } = new();

    /// <summary>Called after each removal is refined and judged. Stopping is heard through
    /// Refinement.Cancellation, between removals and inside the refinement running.</summary>
    public Action<EvolutionStep>? StepDone { get; init; }

    /// <summary>Called after each round of RaiseThinLayers is refined.</summary>
    public Action<RaiseStep>? RaiseDone { get; init; }
}

/// <summary>One removal tried (from 1): layer Layer (from 0, in the stack before it) of Thickness
/// nm, the merit after refining what remained, whether it was kept, whether its neighbours were
/// merged, and the layer count it would give.</summary>
public readonly record struct EvolutionStep(int Round, int Layer, double Thickness, double Merit, bool Kept,
                                            bool Merged, int Layers);

public readonly record struct EvolutionResult(
    int Removed, int Merged, double InitialMerit, double FinalMerit, Design Design);

/// <summary>One round of raising (from 1): the layers (from 0) raised to the minimum and fixed,
/// and the merit once the rest was refined.</summary>
public readonly record struct RaiseStep(int Round, int[] Layers, double Merit);

public readonly record struct RaiseResult(int Raised, int Rounds, double InitialMerit, double FinalMerit, Design Design);

public static class GradualEvolution
{
    /// <summary>
    /// Removes sub-minimum layers and merges neighbours of the same material, refining after each
    /// change and keeping only what survives. Returns a new Design whenever the shape changed.
    /// </summary>
    public static EvolutionResult RemoveThinLayers(Design design, EvolutionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(design);
        options ??= new EvolutionOptions();

        design.Evaluate();
        double initial = design.Merit;
        double current = initial;
        int removed = 0, merged = 0;

        for (int round = 0; round < options.MaxRemovals; ++round)
        {
            if (options.Refinement.Cancellation.IsCancellationRequested) break;
            var spec = design.ToSpec();
            var thickness = spec.LayerThickness ?? [];
            var material = spec.LayerMaterial ?? [];
            if (thickness.Length <= 1) break;

            // The thinnest layer under the minimum is the candidate. Thinnest first because it is
            // the one whose removal disturbs the design least, so it is the one most likely to
            // survive refinement — and a removal that survives changes the stack that the next
            // round looks at.
            // A FIXED LAYER IS THE USER'S, and stays, however thin; an empty one is not a layer.
            var varied = NeedleOptimiser.Varied(spec);
            int candidate = -1;
            double thinnest = double.MaxValue;
            // Nor is a layer removed whose removal would join a fixed layer to its neighbour beyond:
            // the joined layer would be thicker and either fixed there or freed.
            bool JoinsFixed(int i) => i > 0 && i < thickness.Length - 1 && material[i - 1] == material[i + 1]
                                      && (!varied[i - 1] || !varied[i + 1]);
            for (int i = 0; i < thickness.Length; ++i)
                if (thickness[i] < options.MinimumThickness && thickness[i] < thinnest
                    && (varied[i] || thickness[i] <= 0.0) && !JoinsFixed(i))
                { thinnest = thickness[i]; candidate = i; }

            if (candidate < 0) break;

            var (t, m, v, didMerge) = RemoveLayer(thickness, material, varied, candidate);
            var keep = NeedleOptimiser.Indices(v);
            var trial = new Design(NeedleOptimiser.Rebuild(spec, t, m, keep));
            trial.Plan();

            if (trial.VariableCount > 0)
            {
                // A removal drops variables, so the bounds shrink with the stack.
                var refinement = Bounds.For(options.Refinement, trial.VariableCount);
                // FORCED, NO NEW SLIVERS: a layer at or over the minimum is held there while the rest
                // is refined, or refinement thins it under the minimum and the next round removes it
                // too - a quarter-wave AR went from six layers to one that way.
                if (options.Force) refinement = Floored(refinement, t, keep, options.MinimumThickness);
                var dls = new DampedLeastSquares(trial.VariableCount, trial.Report.n_residuals,
                                                 refinement);
                dls.Minimise(trial, refinement);
            }
            else trial.Evaluate();

            bool kept = options.Force || trial.Merit <= Math.Max(current * (1.0 + options.AllowedDegradation), options.AcceptMerit);
            options.StepDone?.Invoke(new EvolutionStep(round + 1, candidate, thinnest, trial.Merit, kept,
                                                       didMerge, trial.LayerCount));
            if (kept)
            {
                current = trial.Merit;
                design = trial;
                ++removed;
                if (didMerge) ++merged;
            }
            else
            {
                // THE LAYER IS EARNING ITS PLACE. Thin and load-bearing is a real combination —
                // a matching layer in a wideband AR is often under a nanometre — and this is the
                // only stage that can tell that apart from thin and vestigial.
                break;
            }
        }

        design.Evaluate();
        return new EvolutionResult(removed, merged, initial, design.Merit, design);
    }

    /// <summary>
    /// RAISES every layer under the minimum TO it, fixes it there, and refines the rest; again,
    /// until no varied layer has been refined under it. The other way to a design that can be made,
    /// and the one to try when the thin layers are doing real work: the guide's notch (2026-10-24)
    /// had twelve under 10 nm, and removing them cost a merit of 2e-6 to 2.8, while the user's way -
    /// "Fixed layers" - put its slivers at 5 nm and refined back to 8e-6 in two rounds. Fixed, not
    /// bounded: refining with a floor of 5 nm reached only 1.5e-2 in 2000 iterations, the steps
    /// reflecting off the floor where a fixed layer simply is not a variable.
    /// </summary>
    public static RaiseResult RaiseThinLayers(Design design, EvolutionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(design);
        options ??= new EvolutionOptions();
        double minimum = options.MinimumThickness;

        design.Evaluate();
        double initial = design.Merit;
        var start = design.ToSpec();
        var varied = new SortedSet<int>(
            (start.VariableLayer ?? []).Where((_, v) => start.VariableKind is not { } k || v >= k.Length
                                                        || k[v] == (int)TfVariableKind.TF_VARIABLE_LAYER_THICKNESS));
        int raised = 0, rounds = 0;

        // A raised layer is fixed, so a round can only add to the fixed ones and at most every
        // layer is raised once; the bound is there for a refinement that never settles.
        for (int round = 0; round < Math.Max(1, design.LayerCount); ++round)
        {
            if (options.Refinement.Cancellation.IsCancellationRequested) break;
            var spec = design.ToSpec();
            var t = (double[])(spec.LayerThickness ?? []).Clone();
            var m = spec.LayerMaterial ?? [];
            var thin = Enumerable.Range(0, t.Length).Where(i => t[i] < minimum - 1e-9).ToArray();
            if (thin.Length == 0) break;

            foreach (int i in thin) { t[i] = minimum; varied.Remove(i); }
            var trial = new Design(NeedleOptimiser.Rebuild(spec, t, m, varied.ToArray()));
            trial.Plan();
            if (trial.VariableCount > 0)
            {
                var refinement = Bounds.For(options.Refinement, trial.VariableCount);
                new DampedLeastSquares(trial.VariableCount, trial.Report.n_residuals, refinement)
                    .Minimise(trial, refinement);
            }
            else trial.Evaluate();

            design = trial;
            raised += thin.Length;
            rounds = round + 1;
            options.RaiseDone?.Invoke(new RaiseStep(rounds, thin, design.Merit));
        }

        design.Evaluate();
        return new RaiseResult(raised, rounds, initial, design.Merit, design);
    }

    /// <summary>The bounds with every varied layer already at or over the minimum floored at it;
    /// variable k is layer <paramref name="layers"/>[k].</summary>
    private static DlsOptions Floored(DlsOptions options, double[] thickness, int[] layers, double minimum)
    {
        int n = layers.Length;
        var lower = new double[n];
        var upper = new double[n];
        for (int i = 0; i < n; ++i)
        {
            lower[i] = options.Lower is { } l && i < l.Length ? l[i] : 0.0;
            upper[i] = options.Upper is { } u && i < u.Length ? u[i] : double.PositiveInfinity;
            if (thickness[layers[i]] >= minimum) lower[i] = Math.Max(lower[i], Math.Min(minimum, upper[i]));
        }
        return new DlsOptions
        {
            MaxIterations = options.MaxIterations,
            InitialDamping = options.InitialDamping,
            DampingUp = options.DampingUp,
            DampingDown = options.DampingDown,
            MeritTolerance = options.MeritTolerance,
            StepTolerance = options.StepTolerance,
            GradientTolerance = options.GradientTolerance,
            Cancellation = options.Cancellation,
            Lower = lower,
            Upper = upper,
        };
    }

    /// <summary>
    /// GRADUAL EVOLUTION, after Tikhonravov and Trubetskov: the design grows a layer at a time and
    /// is needle-optimised at each size. Needle alone stops at the first design no insertion helps
    /// - the best design of that total thickness, near enough - and a better one usually needs a
    /// thicker stack. So each step adds a layer at the outside (whichever side and material
    /// refines best), refines, inserts needles while they help, removes the layers that have
    /// thinned below the minimum, and records where that left the merit. The first step adds
    /// nothing: it is the needle and removal pass on the design as given.
    ///
    /// THE BEST DESIGN SEEN IS RETURNED, not the last. A grown design is often worse than the
    /// one before it until its needles have done their work, and sometimes stays worse; merit
    /// against total thickness is the curve a designer reads, and the bottom of it is the answer.
    /// Steps stop at MaxSteps, at MaxLayers, when the target is met, or after Patience steps in a
    /// row with no better design.
    /// </summary>
    public static GrowthResult Evolve(Design design, GrowthOptions options)
    {
        ArgumentNullException.ThrowIfNull(design);
        ArgumentNullException.ThrowIfNull(options);
        var cancel = options.Refinement.Cancellation;

        design.Evaluate();
        double initial = design.Merit;
        if (design.VariableCount > 0)
        {
            new DampedLeastSquares(design.VariableCount, design.Report.n_residuals, options.Refinement)
                .Minimise(design, options.Refinement);
            options.Refined?.Invoke(design.Merit);
        }
        double reference = design.Merit;
        var best = design;
        double bestMerit = double.PositiveInfinity;
        int bestStep = 0, inserted = 0, removed = 0, since = 0, steps = 0;
        double thicker = 1.0;     // the grown layer's multiple of GrowthThickness; see "came back"
        var seen = new List<(int Step, Design Design)>();
        string stopped = $"{options.MaxSteps} growth step{(options.MaxSteps == 1 ? "" : "s")} done";

        for (int step = 0; step <= options.MaxSteps; ++step)
        {
            if (cancel.IsCancellationRequested) { stopped = "stopped"; break; }
            if (step > 0)
            {
                if (options.MaxLayers > 0 && design.LayerCount >= options.MaxLayers)
                { stopped = $"the stack has {design.LayerCount} layers, the most allowed"; break; }
                if (Grow(design, options, step, thicker) is not { } grown) { stopped = "no layer could be added"; break; }
                design = grown;
                steps = step;
            }

            int at = step;
            var n = NeedleOptimiser.Run(design, new NeedleOptions
            {
                MaxInsertions = options.InsertionsPerStep,
                MaxLayers = options.MaxLayers,
                Candidates = options.Candidates,
                RefineFirst = false,           // grown designs are refined already
                TargetMet = 0.0,               // measured here, against the refined start
                Refinement = Bounds.For(options.Refinement, design.VariableCount),
                StepDone = t => options.NeedleDone?.Invoke(at, t),
            });
            design = n.Design;
            inserted += n.Insertions;

            if (!cancel.IsCancellationRequested)
            {
                var e = RemoveThinLayers(design, new EvolutionOptions
                {
                    MinimumThickness = options.MinimumThickness,
                    AllowedDegradation = options.AllowedDegradation,
                    AcceptMerit = options.AcceptMerit,
                    MaxRemovals = options.MaxRemovals,
                    Refinement = Bounds.For(options.Refinement, design.VariableCount),
                    StepDone = t => options.RemovalDone?.Invoke(at, t),
                });
                design = e.Design;
                removed += e.Removed;
            }

            design.Evaluate();
            seen.Add((step, design));
            bool newBest = design.Merit < bestMerit * (1.0 - 1e-9);
            if (newBest) { best = design; bestMerit = design.Merit; bestStep = step; since = 0; }
            else ++since;
            options.StepDone?.Invoke(new EvolutionCycle(step, design.Merit, design.LayerCount, Total(design), newBest));

            // The grown layer was removed again and refinement found the same design: the same
            // growth would only repeat it (the user guide's beam splitter, 2026-10-23, repeated one
            // 19-layer design three times before Patience stopped it). A thicker layer can leave
            // the minimum a thin one refines back into: the guide's wide-angle AR, 420-680 nm at
            // 0-45 deg, stuck at 2 layers growing 1 QWOT, met its target in one step growing 2.
            // So the next layer is grown twice as thick, up to MaxGrowthMultiple, then it stops.
            // Not only the step before: the wide-angle AR alternated between two 24-layer designs
            // 0.01 nm apart, their merits differing in the sixth figure, for five steps.
            int earlier = step == 0 ? -1
                : seen.Take(seen.Count - 1).Where(s => SameStack(s.Design, design)).Select(s => s.Step).DefaultIfEmpty(-1).Last();
            if (earlier >= 0)
            {
                if (thicker * 2.0 > options.MaxGrowthMultiple + 1e-9)
                {
                    stopped = $"step {step} came back to the design of step {earlier}, growing up to "
                              + $"{thicker:0.##} times as thick; growing again would repeat it";
                    break;
                }
                thicker *= 2.0;
                options.CameBack?.Invoke(step, earlier, thicker);
            }
            else thicker = 1.0;

            if (bestMerit <= reference * options.TargetMet) { stopped = "target met"; break; }
            // Good enough, and growing only adds layers to a design that needs none.
            if (bestMerit <= options.AcceptMerit)
            { stopped = $"merit {bestMerit:G3}, under the {options.AcceptMerit:G3} accepted"; break; }
            if (since >= options.Patience)
            { stopped = $"{options.Patience} step{(options.Patience == 1 ? "" : "s")} in a row without a better design"; break; }
        }

        // FEWER LAYERS AT NEARLY THE SAME MERIT. The lowest merit is not always the design to
        // keep: a step a few layers short of it, within the degradation a removal is allowed, is
        // the same quality and easier to make (the user guide's beam splitter, 2026-10-24: 62
        // layers at 0.2140 were returned over 58 at 0.2207). Fewest layers wins; then lower merit.
        string? preferred = null;
        double allowed = bestMerit * (1.0 + options.AllowedDegradation);
        var pick = seen.Where(s => s.Design.Merit <= allowed)
                       .OrderBy(s => s.Design.LayerCount).ThenBy(s => s.Design.Merit).FirstOrDefault();
        if (pick.Design is not null && pick.Design.LayerCount < best.LayerCount)
        {
            preferred = FormattableString.Invariant(
                $"step {pick.Step}: {pick.Design.LayerCount} layers at {pick.Design.Merit:G4}, within {options.AllowedDegradation * 100:0.#}% of step {bestStep}'s {bestMerit:G4} with {best.LayerCount - pick.Design.LayerCount} fewer layers");
            best = pick.Design;
            bestStep = pick.Step;
        }

        best.Evaluate();
        return new GrowthResult(steps, inserted, removed, initial, best.Merit, best, bestStep, stopped, preferred);
    }

    /// <summary>The design with one layer added at the outside and refined: of every side and
    /// material allowed (not the material of the layer it would touch), the one that refines to
    /// the lowest merit. Null when none could be tried.</summary>
    private static Design? Grow(Design design, GrowthOptions options, int step, double thicker = 1.0)
    {
        var spec = design.ToSpec();
        var t = spec.LayerThickness ?? [];
        var m = spec.LayerMaterial ?? [];
        var varied = NeedleOptimiser.Varied(spec);
        bool[] sides = options.Side switch
        {
            GrowthSide.Incident => [true], GrowthSide.Substrate => [false], _ => [true, false],
        };
        Design? chosen = null;
        GrowthStep report = default;
        foreach (bool incident in sides)
        {
            int touching = t.Length == 0 ? -1 : incident ? m[0] : m[^1];
            for (int material = 0; material < design.MaterialCount; ++material)
            {
                if (material == touching || options.Refinement.Cancellation.IsCancellationRequested) continue;
                if (options.Candidates is { } allowed && !allowed.Contains(material)) continue;
                double g = options.GrowthThickness[material] * thicker;
                double[] t2 = incident ? [g, .. t] : [.. t, g];
                int[] m2 = incident ? [material, .. m] : [.. m, material];
                bool[] v2 = incident ? [true, .. varied] : [.. varied, true];
                var trial = new Design(NeedleOptimiser.Rebuild(spec, t2, m2, NeedleOptimiser.Indices(v2)));
                trial.Plan();
                var refinement = Bounds.For(options.Refinement, trial.VariableCount);
                try
                {
                    new DampedLeastSquares(trial.VariableCount, trial.Report.n_residuals, refinement)
                        .Minimise(trial, refinement);
                }
                catch (ThinFilmException) { continue; }     // a start the engine refuses is not tried
                if (chosen is null || trial.Merit < chosen.Merit)
                {
                    chosen = trial;
                    report = new GrowthStep(step, material, incident, g, trial.Merit, trial.LayerCount);
                }
            }
        }
        if (chosen is not null) options.Grown?.Invoke(report);
        return chosen;
    }

    /// <summary>The same materials in the same order, every thickness within 0.1 nm.</summary>
    private static bool SameStack(Design a, Design b)
    {
        if (a.LayerCount != b.LayerCount) return false;
        var sa = a.ToSpec();
        var sb = b.ToSpec();
        var (ta, tb) = (sa.LayerThickness ?? [], sb.LayerThickness ?? []);
        var (ma, mb) = (sa.LayerMaterial ?? [], sb.LayerMaterial ?? []);
        for (int i = 0; i < ta.Length; ++i)
            if (ma[i] != mb[i] || Math.Abs(ta[i] - tb[i]) > 0.1) return false;
        return true;
    }

    private static double Total(Design design)
    {
        double total = 0.0;
        foreach (var t in design.ToSpec().LayerThickness ?? []) total += t;
        return total;
    }

    /// <summary>
    /// Drops one layer, and merges its neighbours if removing it left two of the same material
    /// adjacent. The merge matters: without it the stack keeps a boundary that is no longer a
    /// boundary, which costs a variable and an interface that the optimiser then tunes to zero
    /// effect. Each layer keeps its vary flag; a merge never involves a fixed layer (the caller
    /// does not remove a layer between one and its own material), so the merged layer varies.
    /// </summary>
    private static (double[] thickness, int[] material, bool[] varied, bool merged) RemoveLayer(
        double[] thickness, int[] material, bool[] varied, int drop)
    {
        var t = new List<double>(thickness.Length);
        var m = new List<int>(thickness.Length);
        var v = new List<bool>(thickness.Length);
        for (int i = 0; i < thickness.Length; ++i)
        {
            if (i == drop) continue;
            if (m.Count > 0 && m[^1] == material[i])
            {
                t[^1] += thickness[i];            // the merge
                v[^1] |= varied[i];
                continue;
            }
            t.Add(thickness[i]);
            m.Add(material[i]);
            v.Add(varied[i]);
        }
        bool merged = m.Count < thickness.Length - 1;
        return (t.ToArray(), m.ToArray(), v.ToArray(), merged);
    }
}

/// <summary>Where gradual evolution adds a layer: next to the incident medium, next to the
/// substrate, or whichever of the two refines to the lower merit.</summary>
public enum GrowthSide { Incident, Substrate, Both }

public sealed class GrowthOptions
{
    /// <summary>The thickness (nm) a layer of each material is added at, by material index.</summary>
    public required double[] GrowthThickness { get; init; }

    /// <summary>Growth steps after the first needle and removal pass. Zero only needles and
    /// removes.</summary>
    public int MaxSteps { get; init; } = 8;

    public GrowthSide Side { get; init; } = GrowthSide.Both;

    /// <summary>Needles tried in each step; the needle pass also ends when none helps.</summary>
    public int InsertionsPerStep { get; init; } = 6;

    /// <summary>The materials a needle or a grown layer may be, by index; null for every material
    /// in the design.</summary>
    public int[]? Candidates { get; init; }

    /// <summary>The most layers the design may have, a hard limit: no growth once the stack has this
    /// many, and no needle that would pass it. 0 is no limit.</summary>
    public int MaxLayers { get; init; }

    /// <summary>When a step comes back to the design of the step before, the next layer grows twice
    /// as thick, up to this multiple of GrowthThickness; past it the evolution stops.</summary>
    public double MaxGrowthMultiple { get; init; } = 4.0;

    /// <summary>Steps in a row without a better design before the evolution stops.</summary>
    public int Patience { get; init; } = 3;

    /// <summary>Stop once the merit is under this fraction of the refined start, as needle does.</summary>
    public double TargetMet { get; init; } = 1e-6;

    /// <summary>The removal pass in each step (RemoveThinLayers).</summary>
    public double MinimumThickness { get; init; } = 2.0;
    public double AllowedDegradation { get; init; } = 0.05;
    /// <summary>A merit good enough to keep a removal at, and to stop growing at; 0 is off.</summary>
    public double AcceptMerit { get; init; }
    public int MaxRemovals { get; init; } = 16;

    public DlsOptions Refinement { get; init; } = new();

    public Action<double>? Refined { get; init; }
    public Action<GrowthStep>? Grown { get; init; }
    /// <summary>Each needle tried, with the step it belongs to.</summary>
    public Action<int, NeedleStep>? NeedleDone { get; init; }
    /// <summary>Each removal tried, with the step it belongs to.</summary>
    public Action<int, EvolutionStep>? RemovalDone { get; init; }
    public Action<EvolutionCycle>? StepDone { get; init; }
    /// <summary>A step came back to an earlier step's design: the step, that earlier step, and the
    /// multiple of GrowthThickness the next layer grows at.</summary>
    public Action<int, int, double>? CameBack { get; init; }
}

/// <summary>The layer a growth step added (Incident: next to the incident medium, else the
/// substrate), at Thickness nm, and the merit and layer count once refined.</summary>
public readonly record struct GrowthStep(int Step, int Material, bool Incident, double Thickness, double Merit, int Layers);

/// <summary>Where a step left the design, after its needles and removals: the merit, layer count,
/// total physical thickness (nm), and whether it is the best so far.</summary>
public readonly record struct EvolutionCycle(int Step, double Merit, int Layers, double TotalThickness, bool NewBest);

public readonly record struct GrowthResult(
    int Steps, int Inserted, int Removed, double InitialMerit, double FinalMerit, Design Design,
    int BestStep, string Stopped, string? Preferred = null);
