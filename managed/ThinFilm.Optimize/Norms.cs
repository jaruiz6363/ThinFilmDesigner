// Norms.cs — L2 is the engine's; everything else is reweighting, which is why it lives here.
//
// A4.1 says the engine computes sqrt(sum w r^2 / sum w) and nothing else. A4.2 to A4.6 say every
// other norm is ABOVE ABI and reached by IRLS — iteratively reweighted least squares. That
// division is not a convenience: a norm is a statement about how residuals COMBINE, and the
// residual vector is already the engine's output, so changing the combination needs no physics and
// no second implementation. It needs different weights and another pass.
//
// WHY MINIMAX MATTERS MORE HERE THAN IN LENS DESIGN, in the specification's own words. Almost every
// coating specification reads "R below 0.5% across the band" — a Chebyshev statement, not a
// least-squares one. An L2 fit to that will happily trade a band that is uniformly at 0.4% for one
// that is at 0.1% almost everywhere and 0.9% at one wavelength, because the sum of squares prefers
// it. The second design fails the specification and the first passes.
//
// HOW IRLS REACHES Lp. Minimising sum |r|^p is the same as minimising sum u r^2 with
// u = |r|^(p-2), so the scheme is: solve weighted least squares, recompute the weights from the
// residuals it produced, repeat. p is raised gradually rather than jumped to, because the weights
// of a large p are nearly a delta function on the worst residual and starting there hands the
// solver a problem with no information in it.
//
// THE WEIGHTS GO INTO THE ENGINE, NOT AROUND IT. Each IRLS pass writes the new weights into the
// operand table and lets tf_evaluate compute the merit from them, so there is still exactly one
// implementation of what a residual and a merit are. What this file changes is which question the
// engine is asked, never the answer it gives.

using ThinFilm.Native;

namespace ThinFilm.Optimize;

public enum Norm
{
    /// <summary>The engine's own: sqrt(sum w r^2 / sum w). No reweighting, no outer loop.</summary>
    L2,

    /// <summary>
    /// Lp with p driven up, which approaches minimax. The one the specification singles out for
    /// coatings, because a coating specification is a limit and not a target.
    /// </summary>
    Minimax,

    /// <summary>L1, which tolerates a few large residuals to fit the rest well. The opposite
    /// trade from Minimax, and the right one when some targets are known to be unreachable.</summary>
    L1,
}

public sealed class NormOptions
{
    /// <summary>IRLS passes. Each is a full damped least squares run.</summary>
    public int Passes { get; init; } = 6;

    /// <summary>
    /// The p reached on the last pass; continuation runs from 2 up to it.
    ///
    /// THREE, AND THE SMALLNESS CAME FROM MEASURING. IRLS is only stable while its weights still
    /// describe the whole problem. At p = 24 a residual twice another gets four million times the
    /// weight, the solver stops seeing anything but the single worst point, and on the AR template
    /// the worst case came back at 97.6 against plain L2's 47.2.
    ///
    /// AND THE IMPROVEMENT IS NOT MONOTONE IN p, which is the more useful thing measured. On that
    /// same template: 31.0 at p = 2.5, 30.4 at p = 3, no improvement at p = 4, 26.6 at p = 5, none
    /// again at p = 6. IRLS wanders between local minima and p only decides how hard it is pushed.
    /// That is why the loop keeps the BEST pass rather than the last one — with that rule the
    /// result is never worse than plain L2 whatever p does, and p becomes a knob for how much
    /// better it might get rather than a way to make it worse.
    /// </summary>
    public double FinalP { get; init; } = 3.0;

    /// <summary>
    /// The largest ratio allowed between the biggest and smallest IRLS weight. A second guard on
    /// the same failure: even at a modest p, one residual far above the rest can produce a weight
    /// vector that is a delta function in all but name.
    /// </summary>
    public double WeightRatioCap { get; init; } = 1e3;

    /// <summary>
    /// Floor under |r| when forming a weight, relative to the largest residual. Without it a
    /// residual that happens to be zero gets weight zero at p &gt; 2 and is never looked at again —
    /// which for a one-sided comparison is EVERY satisfied target.
    /// </summary>
    public double ResidualFloor { get; init; } = 1e-6;

    public DlsOptions Refinement { get; init; } = new();

    /// <summary>Called after each IRLS pass, with the design scored on the caller's own weights.</summary>
    public Action<NormPass>? PassDone { get; init; }
}

/// <summary>One IRLS pass (from 1), the p it ran under, and the merit and worst residual it ended
/// at under the caller's weights; Best when it is the pass kept so far.</summary>
public readonly record struct NormPass(int Pass, int Passes, double P, double Merit, double Worst, bool Best);

public readonly record struct NormResult(
    Norm Norm, int Passes, double InitialMerit, double FinalMerit, double FinalWorstResidual);

public static class Norms
{
    /// <summary>
    /// Minimise under the given norm. L2 is one damped least squares run; the others wrap it in
    /// IRLS, rewriting the operand weights between passes.
    /// </summary>
    public static NormResult Minimise(Design design, Norm norm, NormOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(design);
        options ??= new NormOptions();

        design.Evaluate();
        double initial = design.Merit;

        var dls = new DampedLeastSquares(design.VariableCount, design.Report.n_residuals,
                                         options.Refinement);

        if (norm == Norm.L2)
        {
            dls.Minimise(design, options.Refinement);
            return new NormResult(norm, 1, initial, design.Merit, Worst(design));
        }

        var baseWeight = design.OperandWeight.ToArray();
        int passes = Math.Max(1, options.Passes);

        // THE BEST PASS IS KEPT, NOT THE LAST ONE, and this is the difference between IRLS that
        // works and IRLS that is a lottery.
        //
        // Every pass leaves the design at the optimum of ITS OWN weighting, which is not the
        // objective the caller asked for. Returning that is returning a design chosen by whichever
        // set of weights the loop happened to build last — and it showed: continued to p = 3 the
        // worst residual came back at 74 against plain L2's 47, while p = 4 gave 34. The answer was
        // not moving with p in any meaningful way, it was moving with which local minimum the final
        // weighting happened to sit in.
        //
        // So after each pass the base weights go back, the design is scored on the TRUE criterion,
        // and the best one seen is kept. The result can then never be worse than the first pass,
        // which is the plain L2 answer — monotone by construction rather than by luck.
        var best = new double[design.VariableCount];
        for (int i = 0; i < best.Length; ++i) best[i] = design.GetVariable(i);
        double bestWorst = double.PositiveInfinity, bestMerit = double.PositiveInfinity;
        // Only the first pass runs under the caller's weights, so only its steps are reported.
        var quiet = options.Refinement.WithProgress(null);
        double pNow = 2.0;

        for (int pass = 0; pass < passes; ++pass)
        {
            if (pass > 0 && options.Refinement.Cancellation.IsCancellationRequested) break;
            dls.Minimise(design, pass == 0 ? options.Refinement : quiet);

            design.SetOperandWeight(baseWeight);
            design.Evaluate();
            double worst = Worst(design);

            // Judged by what the norm is FOR: minimax by the worst residual, L1 by the merit it
            // was driving down.
            bool better = norm == Norm.Minimax ? worst < bestWorst : design.Merit < bestMerit;
            if (better)
            {
                bestWorst = worst;
                bestMerit = design.Merit;
                for (int i = 0; i < best.Length; ++i) best[i] = design.GetVariable(i);
            }
            options.PassDone?.Invoke(new NormPass(pass + 1, passes, pNow, design.Merit, worst, better));

            if (pass == passes - 1) break;

            // p climbs from 2 towards the target; L1 goes the other way and stays there.
            double p = norm == Norm.L1
                ? 1.0
                : 2.0 + (options.FinalP - 2.0) * (pass + 1.0) / (passes - 1.0);
            pNow = p;

            Reweight(design, baseWeight, p, options.ResidualFloor, options.WeightRatioCap);
        }

        // The weights go back and the design goes back to the best point found under them, so the
        // merit means what the caller asked for rather than what the last pass found convenient.
        design.SetOperandWeight(baseWeight);
        for (int i = 0; i < best.Length; ++i) design.SetVariable(i, best[i]);
        design.Evaluate();

        return new NormResult(norm, passes, initial, design.Merit, Worst(design));
    }

    /// <summary>w_i &lt;- w_i * |r_i|^(p-2), normalised so the weight sum is unchanged.</summary>
    private static void Reweight(Design design, double[] baseWeight, double p, double floor,
                                 double ratioCap)
    {
        var residual = design.Residual;
        double largest = 0.0;
        foreach (var r in residual) largest = Math.Max(largest, Math.Abs(r));
        double eps = Math.Max(largest * floor, double.Epsilon);

        var w = new double[baseWeight.Length];
        double before = 0.0, after = 0.0;
        for (int i = 0; i < baseWeight.Length; ++i) before += baseWeight[i];

        for (int o = 0; o < w.Length; ++o)
        {
            int row = design.ResidualRow(o);
            double r = row >= 0 ? Math.Abs(residual[row]) : 0.0;
            w[o] = baseWeight[o] * Math.Pow(Math.Max(r, eps), p - 2.0);
            after += w[o];
        }

        // Clamp the dynamic range before normalising, so no single residual can turn the weight
        // vector into a delta function and take the rest of the problem out of the solver's view.
        double smallest = double.MaxValue, biggest = 0.0;
        foreach (var x in w) { if (x > 0.0 && x < smallest) smallest = x; if (x > biggest) biggest = x; }
        if (biggest > 0.0 && smallest < double.MaxValue && biggest / smallest > ratioCap)
        {
            double floorWeight = biggest / ratioCap;
            after = 0.0;
            for (int o = 0; o < w.Length; ++o) { w[o] = Math.Max(w[o], floorWeight); after += w[o]; }
        }

        if (after > 0.0)
            for (int o = 0; o < w.Length; ++o) w[o] *= before / after;

        design.SetOperandWeight(w);
    }

    private static double Worst(Design design)
    {
        double worst = 0.0;
        foreach (var r in design.Residual) worst = Math.Max(worst, Math.Abs(r));
        return worst;
    }
}
