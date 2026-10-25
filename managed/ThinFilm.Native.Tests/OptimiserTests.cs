// OptimiserTests.cs — X1.4, measured against things that are true independently of this code.
//
// AN OPTIMISER IS EASY TO TEST BADLY. "The merit went down" passes for almost any loop that takes
// small steps, including one whose step direction is wrong but whose line search saves it. So the
// tests here are anchored to facts the optimiser cannot influence:
//
//   - a single quarter-wave layer has an ANALYTIC optimum, so convergence can be checked against a
//     number nobody here computed;
//   - at a minimum the gradient is zero, which is a statement about the engine's Jacobian and is
//     checked with the engine's Jacobian;
//   - a design whose merit is worse than the one it started from is a failure however plausible
//     its trajectory;
//   - needle and evolution change the SHAPE, so their result is checked by counting layers, not
//     by believing a return value.

using ThinFilm.Native;
using ThinFilm.Optimize;

namespace ThinFilm.Native.Tests;

public static class OptimiserTests
{
    /// <summary>
    /// A quarter wave of n = 1.38 at 550 nm: 550 / (4 * 1.38). Written as a literal because the
    /// X1.1-X1.3 rule about not computing physics in C# does not stop applying because the file is
    /// about optimisation.
    /// </summary>
    private const double QuarterWave138 = 99.637681159420296;

    /// <summary>R of that layer on glass: ((n0*ns - nf^2)/(n0*ns + nf^2))^2 with n0=1, ns=1.52.</summary>
    private const double QuarterWaveReflectance = 0.012600790214630288;

    private static DesignSpec SingleLayerAr(double thickness) => new()
    {
        LayerThickness = [thickness],
        LayerMaterial = [0],
        MaterialCount = 1,
        MaterialIndex = [new TfComplex(1.38, 0.0)],
        Wavelength = [550.0],
        PointWavelength = [0],
        PointAngle = [0.0],
        IndexIncident = [new TfComplex(1.0, 0.0)],
        IndexExit = [new TfComplex(1.52, 0.0)],
        Operands = new OperandSpec
        {
            Code = [(int)TfTargetCode.TF_TARGET_RS],
            Point = [0],
            Target = [0.0],
            Weight = [1.0],
        },
        VariableKind = [(int)TfVariableKind.TF_VARIABLE_LAYER_THICKNESS],
        VariableLayer = [0],
        VariableMaterial = [0],
        VariableCoefficient = [0],
    };

    /// <summary>A broadband AR problem: two materials, several wavelengths, held below a limit.</summary>
    private static DesignSpec BroadbandAr(double[] thickness, int[] material)
    {
        double[] wl = [420.0, 470.0, 520.0, 570.0, 620.0, 670.0];
        var index = new List<TfComplex>();
        foreach (var _ in wl) index.Add(new TfComplex(2.35, 0.0));   // material 0: high
        foreach (var _ in wl) index.Add(new TfComplex(1.38, 0.0));   // material 1: low
        var inc = new List<TfComplex>();
        var exit = new List<TfComplex>();
        foreach (var _ in wl) { inc.Add(new TfComplex(1.0, 0.0)); exit.Add(new TfComplex(1.52, 0.0)); }

        var kind = new int[thickness.Length];
        var layer = new int[thickness.Length];
        for (int i = 0; i < thickness.Length; ++i)
        { kind[i] = (int)TfVariableKind.TF_VARIABLE_LAYER_THICKNESS; layer[i] = i; }

        var code = new int[wl.Length];
        var point = new int[wl.Length];
        var target = new double[wl.Length];
        var weight = new double[wl.Length];
        for (int i = 0; i < wl.Length; ++i)
        { code[i] = (int)TfTargetCode.TF_TARGET_RS; point[i] = i; target[i] = 0.0; weight[i] = 1.0; }

        return new DesignSpec
        {
            LayerThickness = thickness,
            LayerMaterial = material,
            MaterialCount = 2,
            MaterialIndex = index.ToArray(),
            Wavelength = wl,
            PointWavelength = [0, 1, 2, 3, 4, 5],
            PointAngle = [0, 0, 0, 0, 0, 0],
            IndexIncident = inc.ToArray(),
            IndexExit = exit.ToArray(),
            Operands = new OperandSpec
            { Code = code, Point = point, Target = target, Weight = weight },
            VariableKind = kind,
            VariableLayer = layer,
            VariableMaterial = new int[thickness.Length],
            VariableCoefficient = new int[thickness.Length],
        };
    }

    /// <summary>
    /// THE ANCHOR. A single film on glass has its reflectance minimum exactly at quarter wave, so
    /// this is convergence checked against arithmetic done on paper rather than against whatever
    /// the optimiser happened to produce.
    /// </summary>
    public static void DlsFindsTheAnalyticOptimumOfASingleLayer(Harness h)
    {
        h.Section("damped least squares converges on the quarter-wave a single AR layer must have");

        var d = new Design(SingleLayerAr(70.0));    // a long way off, and on the near side
        d.Plan();
        d.Evaluate();
        double start = d.Merit;

        var dls = new DampedLeastSquares(d.VariableCount, d.Report.n_residuals);
        var r = dls.Minimise(d);

        h.Expect(r.FinalMerit < start, "the merit fell");
        // 1e-4 nm, AND THE LOOSENESS IS THE POINT RATHER THAN A CONCESSION. The minimum is
        // quadratic, so an error of eps in the merit corresponds to sqrt(eps) in the thickness:
        // converging the merit to its last few bits still leaves the parameter uncertain at
        // around 1e-5 nm, which is what this measures. Demanding 1e-6 nm would be demanding that
        // the optimiser resolve a distance the merit function cannot distinguish - and 1e-4 nm is
        // already ten thousand times finer than anything that can be deposited.
        h.Expect(Math.Abs(d.GetVariable(0) - QuarterWave138) < 1e-4,
                 $"converged to quarter wave: {d.GetVariable(0):R} against {QuarterWave138:R}");
        h.Expect(Math.Abs(d.Value[0] - QuarterWaveReflectance) < 1e-12,
                 $"and to the reflectance that thickness gives: {d.Value[0]:R}");
        // IT STOPPED BECAUSE IT WAS DONE, not because it ran out of budget - which is what the
        // outcome is for. Stalled counts: the solver distinguishes it from Converged by the
        // gradient tolerance, and 2e-5 nm from a quadratic minimum leaves a gradient that is tiny
        // in absolute terms and not necessarily 1e-6 of where it started. What must NOT happen is
        // IterationLimit, which would mean the merit was still falling when the budget ended.
        h.Expect(r.Outcome is DlsOutcome.Converged or DlsOutcome.StepTooSmall
                            or DlsOutcome.Stalled or DlsOutcome.NoDownhillStep,
                 $"and stopped because it was done, not because it ran out ({r.Outcome})");

        // AND FROM THE OTHER SIDE, so the result is the minimum rather than an artefact of
        // approaching it downhill from one direction.
        var e = new Design(SingleLayerAr(135.0));
        e.Plan();
        new DampedLeastSquares(e.VariableCount, e.Report.n_residuals).Minimise(e);
        h.Expect(Math.Abs(e.GetVariable(0) - QuarterWave138) < 1e-4,
                 $"and from above too: {e.GetVariable(0):R}");
    }

    /// <summary>
    /// At a minimum the gradient vanishes. This is checked with the ENGINE's Jacobian, not with a
    /// difference taken here — C0.7 forbids the silent finite difference and this file has no
    /// business computing one.
    /// </summary>
    public static void TheGradientVanishesWhereItStops(Harness h)
    {
        h.Section("the engine's own Jacobian is ~zero at the point the optimiser stopped at");

        var d = new Design(BroadbandAr([90.0, 120.0, 85.0, 95.0], [1, 0, 1, 0]));
        d.Plan();

        double before = GradientNorm(d);
        var dls = new DampedLeastSquares(d.VariableCount, d.Report.n_residuals);
        dls.Minimise(d);
        double after = GradientNorm(d);

        // RELATIVE, because an absolute threshold on a gradient is a threshold on the residual
        // scaling. These residuals happen to be O(1); a template that normalises by 1e-4 would
        // have a gradient ten thousand times larger while being just as stationary.
        h.Expect(after <= 1e-6 * before,
                 $"|J^T r| fell from {before:G4} to {after:G4}, a factor of {before / after:G4}");

        static double GradientNorm(Design d)
        {
            d.EvaluateJacobian();
            int m = d.Report.n_residuals;
            double largest = 0.0;
            for (int v = 0; v < d.VariableCount; ++v)
            {
                double g = 0.0;
                for (int r = 0; r < m; ++r) g += d.Jacobian[v * m + r] * d.Residual[r];
                largest = Math.Max(largest, Math.Abs(g));
            }
            return largest;
        }
    }

    public static void RefinementNeverReturnsAWorseDesign(Harness h)
    {
        h.Section("however the search goes, the design that comes back is the best one seen");

        double[] starts = [40.0, 70.0, 100.0, 160.0, 240.0];
        foreach (var s in starts)
        {
            var d = new Design(SingleLayerAr(s));
            d.Plan();
            d.Evaluate();
            double before = d.Merit;

            var r = new DampedLeastSquares(d.VariableCount, d.Report.n_residuals).Minimise(d);

            h.Expect(r.FinalMerit <= before,
                     $"from {s} nm the merit did not get worse ({before:R} to {r.FinalMerit:R})");
            h.PinExact(d.Merit, r.FinalMerit,
                       $"from {s} nm the design is left AT the reported merit");
        }
    }

    public static void BoundsAreRespected(Harness h)
    {
        h.Section("a thickness is never driven negative, whatever the step wanted");

        var d = new Design(BroadbandAr([90.0, 120.0, 85.0, 95.0], [1, 0, 1, 0]));
        d.Plan();

        var options = new DlsOptions
        {
            Lower = [5.0, 5.0, 5.0, 5.0],
            Upper = [200.0, 200.0, 200.0, 200.0],
        };
        var dls = new DampedLeastSquares(d.VariableCount, d.Report.n_residuals, options);
        dls.Minimise(d, options);

        for (int i = 0; i < d.VariableCount; ++i)
        {
            double v = d.GetVariable(i);
            h.Expect(v >= 5.0 - 1e-12 && v <= 200.0 + 1e-12,
                     $"variable {i} stayed inside its bounds at {v:R}");
        }
    }

    /// <summary>
    /// The needle stage must actually change the stack and actually improve it. Counting layers
    /// rather than trusting the returned count, because the two disagreeing is the bug.
    /// </summary>
    public static void NeedleAddsALayerAndImprovesTheDesign(Harness h)
    {
        h.Section("needle inserts where the map says, and the result is better and longer");

        // A THICK SINGLE LAYER, which is where needle synthesis actually starts, and not the
        // thin one this test used first. A refined quarter-wave AR is genuinely
        // NEEDLE-STATIONARY: every slope in its map is positive bar the cancellation noise of a
        // same-material candidate, so the honest result there is "no insertion helps" and a test
        // demanding one would have been demanding the wrong answer. A thick layer has room for
        // the map to go negative, which is the whole reason the method starts from one.
        var d = new Design(BroadbandAr([500.0], [1]));
        d.Plan();
        var dls = new DampedLeastSquares(d.VariableCount, d.Report.n_residuals);
        dls.Minimise(d);
        double refined = d.Merit;
        int layersBefore = d.LayerCount;

        var r = NeedleOptimiser.Run(d, new NeedleOptions { MaxInsertions = 4, DepthSamples = 120 });

        h.Expect(r.Insertions > 0, "at least one insertion survived refinement");
        h.Expect(r.Design.LayerCount > layersBefore,
                 $"the stack really grew: {layersBefore} to {r.Design.LayerCount}");
        h.Pin(r.Design.LayerCount, layersBefore + 2 * r.Insertions,
              "each insertion splits a layer, so the count rises by two per needle");
        h.Expect(r.FinalMerit < refined,
                 $"and the merit improved on an already-refined design: {refined:R} to {r.FinalMerit:R}");

        // The returned Design is the one that was improved, not a stale handle to the old one.
        r.Design.Evaluate();
        h.PinExact(r.Design.Merit, r.FinalMerit, "the returned Design carries the reported merit");
    }

    /// <summary>
    /// Evolution must remove a layer that is doing nothing, and must NOT remove one that is. The
    /// second half is what stops it being a delete loop with good manners.
    /// </summary>
    public static void EvolutionRemovesAVestigialLayerAndKeepsALoadBearingOne(Harness h)
    {
        h.Section("a vestigial layer is removed; a thin layer that is earning its place is not");

        // A three-layer AR with a needle-thin layer of the SAME material as its neighbour: it
        // cannot be doing anything the neighbour is not already doing.
        var withVestigial = new Design(BroadbandAr([95.0, 0.20, 118.0, 88.0], [1, 1, 0, 1]));
        withVestigial.Plan();
        new DampedLeastSquares(withVestigial.VariableCount,
                               withVestigial.Report.n_residuals).Minimise(withVestigial);

        var r = GradualEvolution.RemoveThinLayers(withVestigial, new EvolutionOptions { MinimumThickness = 1.0 });
        h.Expect(r.Removed > 0, "the vestigial layer was removed");
        h.Expect(r.Design.LayerCount < withVestigial.LayerCount,
                 $"the stack shrank: {withVestigial.LayerCount} to {r.Design.LayerCount}");
        h.Expect(r.Merged > 0,
                 "and its neighbours, being the same material, were merged rather than left as a "
                 + "boundary that is no longer a boundary");

        // NOW THE OTHER HALF. A design whose every layer is comfortably above the minimum must
        // come back untouched: a stage that removes something here is removing for its own sake.
        // The fixture is CHECKED to be that, rather than assumed - refinement moves thicknesses,
        // and a "healthy" design that refinement had quietly driven under the minimum would make
        // this test pass or fail for a reason that has nothing to do with the code under test.
        // BOUNDED, because an unbounded refinement of this stack drives one layer to 2.5e-5 nm -
        // it would rather be a two-layer design - and then "the healthy fixture" is not healthy
        // and the test measures the fixture instead of the code. A real design carries bounds or
        // a MINT penalty for exactly this reason.
        var bounded = new DlsOptions { Lower = [10.0, 10.0, 10.0], Upper = [400.0, 400.0, 400.0] };
        var healthy = new Design(BroadbandAr([95.0, 118.0, 88.0], [1, 0, 1]));
        healthy.Plan();
        new DampedLeastSquares(healthy.VariableCount, healthy.Report.n_residuals, bounded)
            .Minimise(healthy, bounded);
        int before = healthy.LayerCount;

        double thinnest = double.MaxValue;
        foreach (var t in healthy.LayerThickness) thinnest = Math.Min(thinnest, t);
        h.Expect(thinnest > 1.0,
                 $"the fixture really has no sub-minimum layer after refinement " +
                 $"(thinnest {thinnest:G4} nm)");

        var s = GradualEvolution.RemoveThinLayers(healthy, new EvolutionOptions { MinimumThickness = 1.0 });
        h.Pin(s.Removed, 0, "nothing was removed from a design with no sub-minimum layer");
        h.Pin(s.Design.LayerCount, before, "and the stack is the length it was");
    }

    /// <summary>
    /// The point of a global search is the start that local refinement would never have reached.
    /// So the test is not "it improved" — it is "it beat the local answer from a deliberately bad
    /// start", which is the only claim multistart actually makes.
    /// </summary>
    public static void GlobalSearchBeatsASingleBadStart(Harness h)
    {
        h.Section("multistart finds a valley a single refinement from a bad start cannot");

        double[] bad = [300.0, 300.0, 300.0, 300.0];

        var local = new Design(BroadbandAr((double[])bad.Clone(), [1, 0, 1, 0]));
        local.Plan();
        new DampedLeastSquares(local.VariableCount, local.Report.n_residuals).Minimise(local);
        double localMerit = local.Merit;

        var global = new Design(BroadbandAr((double[])bad.Clone(), [1, 0, 1, 0]));
        global.Plan();
        var r = GlobalSearch.Run(global, new GlobalSearchOptions { Starts = 24 });

        h.Expect(r.FinalMerit <= localMerit,
                 $"the search is never worse than the single start ({r.FinalMerit:R} vs {localMerit:R})");
        h.Expect(r.FinalMerit < localMerit,
                 $"and on this surface it is strictly better: {localMerit:R} to {r.FinalMerit:R}");
        h.PinExact(global.Merit, r.FinalMerit, "the design is left at the best point found");

        // REPRODUCIBLE. Same seed, same answer — a search whose result cannot be reproduced is a
        // result nobody can argue with.
        var again = new Design(BroadbandAr((double[])bad.Clone(), [1, 0, 1, 0]));
        again.Plan();
        var r2 = GlobalSearch.Run(again, new GlobalSearchOptions { Starts = 24 });
        h.PinExact(r2.FinalMerit, r.FinalMerit, "the same seed gives the same merit, exactly");
    }

    /// <summary>
    /// The stages compose: synthesise with needle, tidy with evolution, and the result should be
    /// better than refinement alone. This is the sequence the specification describes as what
    /// makes synthesis work, so it is worth having one test that runs it end to end.
    /// </summary>
    public static void TheStagesComposeIntoSynthesis(Harness h)
    {
        h.Section("refine, needle, evolve — the sequence, end to end");

        var d = new Design(BroadbandAr([500.0], [1]));
        d.Plan();
        new DampedLeastSquares(d.VariableCount, d.Report.n_residuals).Minimise(d);
        double refinedOnly = d.Merit;

        var n = NeedleOptimiser.Run(d, new NeedleOptions { MaxInsertions = 5, DepthSamples = 120 });
        var e = GradualEvolution.RemoveThinLayers(n.Design, new EvolutionOptions { MinimumThickness = 2.0 });

        h.Expect(e.FinalMerit < refinedOnly,
                 $"synthesis beat refinement alone: {refinedOnly:R} to {e.FinalMerit:R}");
        h.Expect(e.Design.LayerCount >= 1, "and left a stack with layers in it");
        h.Note($"one layer at merit {refinedOnly:G6} became {e.Design.LayerCount} layers at " +
               $"{e.FinalMerit:G6}");
    }
}
