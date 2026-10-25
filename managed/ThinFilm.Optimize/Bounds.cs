// Bounds.cs — bounds that follow a change of shape.
//
// A needle insertion adds variables and a removal drops them, so a DlsOptions carrying bounds for
// the stack that went in does not describe the one that came out. That was an index out of range
// thrown from a constructor, which is the least informative way a shape mismatch can present
// itself; the solver now refuses it by name, and this is what the stages use so they never hand
// it one.
//
// BOUNDS REFLECT, THEY DO NOT CLAMP (the user, 2026-10-16: "I expect all constraints to be via
// reflection"). A step that carries a variable past a bound is mirrored back inside by the amount
// it overshot: 620 nm against a 600 nm maximum is 580, 690 against a 700 nm minimum is 710.
// Two exceptions, both for the same reason - a reflection must stay a small correction:
//   - ZERO IS NOT A CONSTRAINT, it is where a layer stops existing, so a step below a floor of
//     zero stops at zero. Needle and evolve depend on it: a layer that is doing nothing is driven
//     to zero and removed, where a reflection would bounce it back up for ever.
//   - A STEP THAT OVERSHOOTS BY MORE THAN THE WHOLE RANGE is not folded back and forth, which
//     would put the variable anywhere; the solver is told to take a shorter step instead.

namespace ThinFilm.Optimize;

public static class Bounds
{
    /// <summary>
    /// <paramref name="x"/> brought back inside [<paramref name="lower"/>, <paramref name="upper"/>]:
    /// mirrored at the bound it passed, or stopped at a floor of zero. Null when it overshot by
    /// more than the range is wide - a step too long to correct, to be retried shorter.
    /// </summary>
    public static double? Reflect(double x, double lower, double upper)
    {
        if (x >= lower && x <= upper) return x;
        if (x < lower && lower <= 0.0) return lower;
        double width = upper - lower;
        double overshoot = x < lower ? lower - x : x - upper;
        if (overshoot > width) return null;
        return x < lower ? lower + overshoot : upper - overshoot;
    }

    /// <summary>
    /// The same options with bounds resized to <paramref name="variables"/>.
    ///
    /// A NEW LAYER HAS NO BOUND OF ITS OWN, so it gets the loosest the caller already permitted —
    /// the minimum of the lower bounds and the maximum of the upper ones. Inventing something
    /// tighter would constrain a layer the caller never described; inventing something looser
    /// would ignore a limit they did.
    /// </summary>
    public static DlsOptions For(DlsOptions options, int variables)
    {
        if ((options.Lower is null || options.Lower.Length == variables)
            && (options.Upper is null || options.Upper.Length == variables))
            return options;

        double lo = options.Lower is { Length: > 0 } l ? l.Min() : 0.0;
        double hi = options.Upper is { Length: > 0 } u ? u.Max() : double.PositiveInfinity;

        var lower = new double[variables];
        var upper = new double[variables];
        Array.Fill(lower, lo);
        Array.Fill(upper, hi);

        return new DlsOptions
        {
            MaxIterations = options.MaxIterations,
            InitialDamping = options.InitialDamping,
            DampingUp = options.DampingUp,
            DampingDown = options.DampingDown,
            MeritTolerance = options.MeritTolerance,
            StepTolerance = options.StepTolerance,
            GradientTolerance = options.GradientTolerance,
            Cancellation = options.Cancellation,      // Stop reaches the refinement inside a stage too
            Lower = lower,
            Upper = upper,
        };
    }
}
