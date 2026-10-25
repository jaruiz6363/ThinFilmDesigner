// ThinFilm.Bench — evaluations/s and Jacobians/s, and the decision they are for.
//
// X1.5 is not a benchmark for its own sake. The ledger says it "decides whether S1.10 is worth
// writing", and S1.10 is the structured derivative sweep: the Jacobian uses general complex
// arithmetic even at points the plan marked structured. tf_jacobian.cpp says so at the top rather
// than hiding it. So this measures the two rates and then answers that question with the numbers
// rather than with an opinion — including the size of the prize, which the ledger had wrong.
//
// HOW THE SWEEP'S SHARE IS ESTIMATED, since it cannot be timed directly from outside.
// tf_evaluate_jacobian does everything tf_evaluate does AND the derivative sweeps, so
//
//     sweep ≈ jacobian_time − evaluate_time
//
// and the gain the sweep would get is MEASURED rather than assumed: two cases differing in nothing
// but k give the structured path's real advantage over the general one, and that factor is applied
// to the sweep. The named assumptions are printed with the verdict so they can be argued with.
//
// MEASUREMENT DISCIPLINE. Every case is warmed until the JIT has settled, then run in time-boxed
// repetitions and reported by MEDIAN, not mean — one scheduling hiccup should not become the
// result. The thickness is moved every iteration, exactly as an optimiser moves it, so nothing
// can be hoisted and no cache that does not exist is accidentally credited.

using System.Diagnostics;
using ThinFilm.Native;
using ThinFilm.Optimize;

namespace ThinFilm.Bench;

public static class Program
{
    private const int Repetitions = 7;
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(250);

    private sealed record Case(string Name, int Layers, int Points, bool Absorbing, bool Oblique,
                               bool BothPolarisations);

    private sealed record Rate(double EvalPerSecond, double JacPerSecond, int Residuals,
                               int Structured, int General);

    public static int Main()
    {
        Console.WriteLine();
        Console.WriteLine("=== ThinFilm: evaluations/s and Jacobians/s ===");
        Console.WriteLine();
        Console.WriteLine($"  {Environment.ProcessorCount} logical cores, .NET {Environment.Version}, "
                          + $"{(Debugger.IsAttached ? "DEBUGGER ATTACHED - numbers are meaningless" : "no debugger")}");
#if DEBUG
        Console.WriteLine("  *** DEBUG BUILD - these numbers are not the ones to quote ***");
#endif
        Console.WriteLine($"  ABI {NativeMethods.tf_abi_version()}, single worker throughout");
        Console.WriteLine();

        // The P/Invoke cost, measured rather than assumed, so a reader can see it is not the
        // story. tf_abi_version does nothing but return a constant, so this is the boundary.
        double overheadNs = CallOverheadNanoseconds();
        Console.WriteLine($"  boundary cost: {overheadNs:F1} ns per P/Invoke "
                          + "(measured on tf_abi_version; bounded, never subtracted below)");
        Console.WriteLine();

        Case[] cases =
        [
            new("AR 4 layers, lossless, normal",     4,  21, false, false, false),
            new("AR 8 layers, lossless, normal",     8,  21, false, false, false),
            new("bandpass 24 layers, lossless",     24,  81, false, false, false),
            new("bandpass 48 layers, lossless",     48, 161, false, false, false),
            new("24 layers, lossless, oblique",     24,  81, false, true,  false),
            new("24 layers, ABSORBING, normal",     24,  81, true,  false, false),
            new("24 layers, lossless, s and p",     24,  81, false, true,  true),
        ];

        Console.WriteLine("  case                              lay  pts   resid    eval/s     jac/s"
                          + "   jac:eval   ns/pt/layer");
        Console.WriteLine("  " + new string('-', 96));

        var results = new List<(Case C, Rate R)>();
        foreach (var c in cases)
        {
            var r = Measure(c);
            results.Add((c, r));
            double ratio = r.EvalPerSecond / r.JacPerSecond;
            double nsPerPointLayer = 1e9 / r.EvalPerSecond / c.Points / Math.Max(1, c.Layers);
            Console.WriteLine($"  {c.Name,-33} {c.Layers,3} {c.Points,4} {r.Residuals,7}"
                              + $" {r.EvalPerSecond,9:N0} {r.JacPerSecond,9:N0}"
                              + $" {ratio,10:F2} {nsPerPointLayer,13:F1}");
        }

        Console.WriteLine();
        double jacobianShare = OptimiserSplit(results);
        Console.WriteLine();
        DecideS110(results, jacobianShare);
        Console.WriteLine();
        NoteS111(results);
        Console.WriteLine();
        return 0;
    }

    // ---------------------------------------------------------------------------------------

    private static double CallOverheadNanoseconds()
    {
        for (int i = 0; i < 100_000; ++i) NativeMethods.tf_abi_version();

        var best = double.MaxValue;
        for (int rep = 0; rep < Repetitions; ++rep)
        {
            const int n = 2_000_000;
            var sw = Stopwatch.StartNew();
            int sink = 0;
            for (int i = 0; i < n; ++i) sink += NativeMethods.tf_abi_version();
            sw.Stop();
            if (sink == 0) Console.Write("");        // keep the loop
            best = Math.Min(best, sw.Elapsed.TotalNanoseconds / n);
        }
        return best;
    }

    private static Rate Measure(Case c)
    {
        var design = Build(c);
        var report = design.Plan();

        // Warm: the first calls through each path JIT and touch cold pages.
        for (int i = 0; i < 200; ++i) { Nudge(design, i); design.Evaluate(); }
        for (int i = 0; i < 200; ++i) { Nudge(design, i); design.EvaluateJacobian(); }

        double eval = Median(() => TimeBoxed(n =>
        {
            for (int i = 0; i < n; ++i) { Nudge(design, i); design.Evaluate(); }
        }));

        double jac = Median(() => TimeBoxed(n =>
        {
            for (int i = 0; i < n; ++i) { Nudge(design, i); design.EvaluateJacobian(); }
        }));

        return new Rate(eval, jac, report.n_residuals, report.n_structured, report.n_general);
    }

    /// <summary>Move the design every iteration, as an optimiser does. Nothing can be hoisted.</summary>
    private static void Nudge(Design d, int i)
    {
        d.LayerThickness[i % d.LayerCount] += ((i & 1) == 0 ? 1e-6 : -1e-6);
    }

    /// <summary>Calls per second, from a run of at least the budget. Returns the rate.</summary>
    private static double TimeBoxed(Action<int> run)
    {
        int n = 64;
        while (true)
        {
            var sw = Stopwatch.StartNew();
            run(n);
            sw.Stop();
            if (sw.Elapsed >= Budget) return n / sw.Elapsed.TotalSeconds;
            // Grow towards the budget rather than doubling blindly past it.
            double scale = Budget.TotalSeconds / Math.Max(sw.Elapsed.TotalSeconds, 1e-6);
            n = (int)Math.Max(n + 1, Math.Min(n * Math.Max(2.0, scale * 1.2), int.MaxValue / 2));
        }
    }

    /// <summary>Median of several runs: one scheduling hiccup should not become the answer.</summary>
    private static double Median(Func<double> f)
    {
        var v = new double[Repetitions];
        for (int i = 0; i < Repetitions; ++i) v[i] = f();
        Array.Sort(v);
        return v[Repetitions / 2];
    }

    // ---------------------------------------------------------------------------------------

    private static double OptimiserSplit(List<(Case C, Rate R)> results)
    {
        Console.WriteLine("  WHAT AN OPTIMISER ACTUALLY SPENDS");

        var c = new Case("refine", 24, 81, false, false, false);
        var d = Build(c);
        var rep = d.Plan();

        var dls = new DampedLeastSquares(d.VariableCount, rep.n_residuals);
        var sw = Stopwatch.StartNew();
        var r = dls.Minimise(d);
        sw.Stop();

        // THE SHARE THAT MATTERS IS TIME, NOT COUNT. One Jacobian per two evaluations sounds like
        // a third of the work and is nothing of the kind, because a Jacobian costs several
        // evaluations. This weights the counts by the rates just measured.
        var like = results.First(x => x.C.Layers == 24 && !x.C.Absorbing && !x.C.Oblique
                                      && !x.C.BothPolarisations);
        double jacTime = r.Jacobians / like.R.JacPerSecond;
        double evalTime = r.Evaluations / like.R.EvalPerSecond;
        double share = jacTime / (jacTime + evalTime);

        Console.WriteLine($"    a {c.Layers}-layer refinement: {r.Iterations} iterations, "
                          + $"{sw.Elapsed.TotalMilliseconds:F0} ms, outcome {r.Outcome}");
        Console.WriteLine($"    {r.Jacobians} Jacobians at {like.R.JacPerSecond:N0}/s and "
                          + $"{r.Evaluations} evaluations at {like.R.EvalPerSecond:N0}/s");
        Console.WriteLine($"    puts {share * 100:F0}% of the time in the Jacobian — it is not an "
                          + "occasional cost, it is the work.");
        return share;
    }

    private static void DecideS110(List<(Case C, Rate R)> results, double jacobianTimeShare)
    {
        Console.WriteLine("  S1.10 — IS THE STRUCTURED DERIVATIVE SWEEP WORTH WRITING?");
        Console.WriteLine();

        var big = results.First(x => x.C.Name.StartsWith("bandpass 48"));
        var lossless = results.First(x => x.C.Name.StartsWith("bandpass 24"));
        var absorbing = results.First(x => x.C.Absorbing);

        // THE STRUCTURED ADVANTAGE, MEASURED RATHER THAN ASSUMED. These two cases differ in
        // nothing but k, so the ratio of their value-path rates IS the structured path's advantage
        // over the general one. The ledger calls S1.10 "the 4x on lossless stacks"; four is the
        // ratio of the matrix arithmetic alone, and it is not what a solve costs, because the
        // transcendentals are shared by both paths and do not halve with the matrix.
        double structuredGain = lossless.R.EvalPerSecond / absorbing.R.EvalPerSecond;

        double tEval = 1.0 / big.R.EvalPerSecond;
        double tJac = 1.0 / big.R.JacPerSecond;
        double sweep = tJac - tEval;
        double sweepShare = sweep / tJac;

        double newJac = tEval + sweep / structuredGain;
        double jacSpeedup = tJac / newJac;
        double runSpeedup = 1.0 / (1.0 - jacobianTimeShare + jacobianTimeShare / jacSpeedup);

        Console.WriteLine($"    The structured path's real advantage, from the two cases that differ");
        Console.WriteLine($"    in nothing but k:  {structuredGain:F2}x  — NOT the 4x the ledger claims.");
        Console.WriteLine($"    Four is the ratio of the matrix arithmetic; the transcendentals are");
        Console.WriteLine($"    shared by both paths and do not halve with the matrix.");
        Console.WriteLine();
        Console.WriteLine($"    On the 48-layer lossless case every point is structured "
                          + $"({big.R.Structured} of {big.R.Structured + big.R.General}).");
        Console.WriteLine($"    A Jacobian costs {tJac / tEval:F2} evaluations, so the sweep is "
                          + $"{sweepShare * 100:F0}% of it.");
        Console.WriteLine($"    At {structuredGain:F2}x on the sweep, a Jacobian speeds up {jacSpeedup:F2}x.");
        Console.WriteLine($"    A refinement spends {jacobianTimeShare * 100:F0}% of its time in Jacobians,");
        Console.WriteLine($"    so the whole run speeds up about {runSpeedup:F2}x "
                          + $"({(1.0 - 1.0 / runSpeedup) * 100:F0}% less time).");
        Console.WriteLine();

        string verdict = runSpeedup >= 1.4 ? "WORTH WRITING"
                       : runSpeedup >= 1.15 ? "WORTH WRITING, but not the next thing"
                       : "NOT worth writing yet";
        Console.WriteLine($"    VERDICT: {verdict}.");
        Console.WriteLine();
        Console.WriteLine("    Two assumptions, stated so they can be argued with. That");
        Console.WriteLine("    tf_evaluate_jacobian's value pass costs what tf_evaluate's does, so the");
        Console.WriteLine("    difference between them is the sweep. And that the sweep would gain what");
        Console.WriteLine("    the value path gains, which is the more doubtful of the two: the sweep");
        Console.WriteLine("    carries derivative matrices the value path does not, so its mix of");
        Console.WriteLine("    arithmetic to transcendentals differs and its gain could land either side.");
    }

    private static void NoteS111(List<(Case C, Rate R)> results)
    {
        Console.WriteLine("  S1.11 — NORMAL-INCIDENCE DEGENERACY, measured while the data was free");

        var one = results.First(x => x.C.Name == "24 layers, lossless, oblique");
        var both = results.First(x => x.C.BothPolarisations);

        Console.WriteLine($"    one polarisation: {one.R.EvalPerSecond,10:N0} eval/s");
        Console.WriteLine($"    s and p:          {both.R.EvalPerSecond,10:N0} eval/s  "
                          + $"({one.R.EvalPerSecond / both.R.EvalPerSecond:F2}x the cost)");
        Console.WriteLine("    That ratio is the ceiling on what S1.11 could return at theta = 0,");
        Console.WriteLine("    where s and p are the same solve. It is a decision rather than a free");
        Console.WriteLine("    win because collapsing them changes results in the last ulp.");
    }

    // ---------------------------------------------------------------------------------------

    private static Design Build(Case c)
    {
        int nw = c.Points;
        var wl = new double[nw];
        for (int i = 0; i < nw; ++i) wl[i] = 400.0 + 400.0 * i / Math.Max(1, nw - 1);

        double k = c.Absorbing ? 0.004 : 0.0;
        var index = new List<TfComplex>();
        foreach (var _ in wl) index.Add(new TfComplex(2.35, k));
        foreach (var _ in wl) index.Add(new TfComplex(1.46, 0.0));

        var inc = new List<TfComplex>();
        var exit = new List<TfComplex>();
        foreach (var _ in wl) { inc.Add(new TfComplex(1.0, 0.0)); exit.Add(new TfComplex(1.52, 0.0)); }

        var thickness = new double[c.Layers];
        var material = new int[c.Layers];
        for (int i = 0; i < c.Layers; ++i)
        {
            material[i] = i % 2;
            thickness[i] = 550.0 / (4.0 * (material[i] == 0 ? 2.35 : 1.46));
        }

        var kind = new int[c.Layers];
        var layer = new int[c.Layers];
        for (int i = 0; i < c.Layers; ++i)
        { kind[i] = (int)TfVariableKind.TF_VARIABLE_LAYER_THICKNESS; layer[i] = i; }

        var pw = new int[nw];
        var pa = new double[nw];
        double angle = c.Oblique ? 0.5235987755982988 : 0.0;   // 30 degrees
        for (int i = 0; i < nw; ++i) { pw[i] = i; pa[i] = angle; }

        // One operand per point. TF_TARGET_RA reads both polarisations; RS reads only s, and the
        // plan's polarisation mask means that difference is real work rather than bookkeeping.
        int code = c.BothPolarisations ? (int)TfTargetCode.TF_TARGET_RA
                                       : (int)TfTargetCode.TF_TARGET_RS;
        var codes = new int[nw];
        var points = new int[nw];
        var target = new double[nw];
        var weight = new double[nw];
        for (int i = 0; i < nw; ++i)
        { codes[i] = code; points[i] = i; target[i] = 0.0; weight[i] = 1.0; }

        var d = new Design(new DesignSpec
        {
            LayerThickness = thickness,
            LayerMaterial = material,
            MaterialCount = 2,
            MaterialIndex = index.ToArray(),
            Wavelength = wl,
            PointWavelength = pw,
            PointAngle = pa,
            IndexIncident = inc.ToArray(),
            IndexExit = exit.ToArray(),
            Operands = new OperandSpec
            { Code = codes, Point = points, Target = target, Weight = weight },
            VariableKind = kind,
            VariableLayer = layer,
            VariableMaterial = new int[c.Layers],
            VariableCoefficient = new int[c.Layers],
        });
        return d;
    }
}
