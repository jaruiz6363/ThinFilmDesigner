// GlobalSearch.cs — multistart, because a coating merit function is not unimodal and never was.
//
// Damped least squares finds the bottom of the valley it starts in. A thin-film merit surface is
// full of valleys: layer thicknesses enter through cos and sin of an optical path, so the surface
// is periodic-ish in every variable and a stack of n layers has a combinatorial number of local
// minima that differ by whole half-waves. Refining once from one start answers a question nobody
// asked — "what is the best design NEAR this one" — and reports it as if it were the best design.
//
// WHY MULTISTART AND NOT SOMETHING CLEVERER, for now. The expensive part of every global method is
// the local refinement, and they mostly differ in how they choose the next start. Multistart's
// choice — uniformly at random in the box — is the one that makes no assumption about the surface,
// which is the right default when the surface is the thing being explored. Basin hopping and
// differential evolution reuse this same refinement and this same accept test; they are further
// rows, and they would be written on top of this rather than instead of it.
//
// THE SEED IS A PARAMETER AND THE DEFAULT IS FIXED. A global search whose results cannot be
// reproduced is a global search whose results cannot be argued with.

using ThinFilm.Native;

namespace ThinFilm.Optimize;

public sealed class GlobalSearchOptions
{
    public int Starts { get; init; } = 32;

    /// <summary>Lower and upper bounds of the box starts are drawn from, per variable. Null uses
    /// [0, 4x the current value] for each, which is wide enough to cross several half-waves.</summary>
    public double[]? Lower { get; init; }
    public double[]? Upper { get; init; }

    /// <summary>Fixed by default: a search nobody can reproduce is a search nobody can check.</summary>
    public int Seed { get; init; } = 20260918;

    /// <summary>Keep the starting design as one of the candidates. Usually right — it is often a
    /// considered design rather than an arbitrary one.</summary>
    public bool IncludeCurrent { get; init; } = true;

    /// <summary>
    /// Where each start comes from. Random draws every start anew from the whole box, which makes
    /// no assumption about the surface and suits a design with no good starting point. Basin
    /// hopping perturbs the best design so far by up to Perturbation of each thickness and refines
    /// from there, so a good structured design (a quarter-wave stack, a bandpass) is searched
    /// around rather than abandoned: on Bandpass, 40 random starts never came within 50% of the
    /// refined starting design (median 3x worse).
    /// </summary>
    public GlobalMode Mode { get; init; } = GlobalMode.Random;

    /// <summary>Basin hopping: each thickness moves by up to this fraction of itself, either way.</summary>
    public double Perturbation { get; init; } = 0.10;

    /// <summary>
    /// Basin hopping: the walk also moves to a hop that is worse than the best by no more than this
    /// fraction, so it can cross into a neighbouring basin instead of falling back into the one it
    /// is in; the best design seen is kept whatever the walk does. Zero hops only from the best.
    /// </summary>
    public double AcceptWorse { get; init; }

    /// <summary>Starts refined at once, each on its own copy of the design. One is the sequential
    /// search; basin hopping then tries this many hops from each point of the walk.</summary>
    public int Workers { get; init; } = 1;

    public DlsOptions Refinement { get; init; } = new();

    /// <summary>Called after each start's refinement. Stopping is heard through
    /// Refinement.Cancellation, between starts and inside the one running.</summary>
    public Action<GlobalStart>? StartDone { get; init; }
}

public enum GlobalMode { Random, BasinHopping }

/// <summary>One start (from 1) of Starts: the merit its refinement reached, or NaN when the engine
/// refused it, whether it is the best so far, and (basin hopping) whether the walk moved to it
/// although it is not.</summary>
public readonly record struct GlobalStart(int Start, int Starts, double Merit, double Best, bool NewBest,
                                          bool WalkMoved = false);

public readonly record struct GlobalSearchResult(
    int Starts, int Improvements, double InitialMerit, double FinalMerit, bool Cancelled = false);

public static class GlobalSearch
{
    /// <summary>
    /// Refines from many starts and leaves the design at the best one found. The shape never
    /// changes, so this works in place — no Design is rebuilt and nothing is re-planned.
    /// </summary>
    public static GlobalSearchResult Run(Design design, GlobalSearchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(design);
        options ??= new GlobalSearchOptions();

        int n = design.VariableCount;
        if (n == 0) throw new ArgumentException("a search over no variables has nothing to search",
                                                nameof(design));

        design.Evaluate();
        double initial = design.Merit;

        var lower = new double[n];
        var upper = new double[n];
        for (int i = 0; i < n; ++i)
        {
            lower[i] = options.Lower is { } lo ? lo[i] : 0.0;
            upper[i] = options.Upper is { } up ? up[i] : Math.Max(4.0 * design.GetVariable(i), 1.0);
        }

        var best = new double[n];
        for (int i = 0; i < n; ++i) best[i] = design.GetVariable(i);
        double bestMerit = double.PositiveInfinity;
        int improvements = 0;
        // Where basin hopping hops from: the best so far, unless a hop not much worse than it was
        // accepted (AcceptWorse), which is how the walk leaves a basin it keeps falling back into.
        var walk = (double[])best.Clone();

        // IN PARALLEL, IN ROUNDS (the user, 2026-10-17: "parallelization on the hops"). The engine
        // is single-threaded and native designs share nothing, so each worker refines its own copy.
        // A round draws Workers starts in order from the one generator - random points, or hops
        // from where the walk is - refines them at once, and then takes the results in start order.
        // So a random search is the same whatever the worker count; basin hopping with one worker
        // is the sequential walk, and with more each round tries several hops from the same point.
        int workers = Math.Max(1, Math.Min(options.Workers, options.Starts));
        var pool = new (Design Design, DampedLeastSquares Dls)[workers];
        for (int w = 0; w < workers; ++w)
        {
            var copy = new Design(design.ToSpec());
            copy.Plan();
            pool[w] = (copy, new DampedLeastSquares(n, copy.Report.n_residuals, options.Refinement));
        }
        var rng = new Random(options.Seed);

        int done = 0;
        for (int first = 0; first < options.Starts; first += workers)
        {
            if (options.Refinement.Cancellation.IsCancellationRequested) break;
            int count = Math.Min(workers, options.Starts - first);

            var points = new double[count][];
            for (int k = 0; k < count; ++k)
            {
                int start = first + k;
                var x = points[k] = new double[n];
                if (start == 0 && options.IncludeCurrent)
                    Array.Copy(best, x, n);
                else if (options.Mode == GlobalMode.BasinHopping)
                    // A hop from where the walk is, kept inside the box. Relative, so a thin layer
                    // moves a little and a thick one more, as their optical effect does.
                    for (int i = 0; i < n; ++i)
                        x[i] = Math.Clamp(walk[i] * (1.0 + options.Perturbation * (2.0 * rng.NextDouble() - 1.0)),
                                          lower[i], upper[i]);
                else
                    for (int i = 0; i < n; ++i)
                        x[i] = lower[i] + rng.NextDouble() * (upper[i] - lower[i]);
            }

            var merit = new double[count];
            var reached = new double[count][];
            Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = workers }, k =>
            {
                var (d, dls) = pool[k];
                for (int i = 0; i < n; ++i) d.SetVariable(i, points[k][i]);
                // A start can land somewhere the engine refuses - every variable inert because a
                // layer came out opaque, say. That start is discarded, not the search.
                try { dls.Minimise(d, options.Refinement); merit[k] = d.Merit; }
                catch (ThinFilmException) { merit[k] = double.NaN; }
                reached[k] = new double[n];
                for (int i = 0; i < n; ++i) reached[k][i] = d.GetVariable(i);
            });

            for (int k = 0; k < count; ++k)
            {
                int start = first + k;
                done = start + 1;
                if (double.IsNaN(merit[k]))
                {
                    options.StartDone?.Invoke(new GlobalStart(start + 1, options.Starts, double.NaN, bestMerit, false));
                    continue;
                }
                // Better by more than rounding: a hop that settles back into the same minimum comes
                // out a few parts in 1e12 lower, and counting that as a new best is noise.
                bool newBest = merit[k] < bestMerit * (1.0 - 1e-9);
                if (newBest)
                {
                    bestMerit = merit[k];
                    Array.Copy(reached[k], best, n);
                    ++improvements;
                }
                // THE WALK MOVES to a new best, or to a design within AcceptWorse of the best: measured
                // against the best rather than the walk's own point, so it cannot drift away.
                bool moved = newBest
                             || (options.Mode == GlobalMode.BasinHopping && options.AcceptWorse > 0.0
                                 && merit[k] <= bestMerit * (1.0 + options.AcceptWorse));
                if (moved) Array.Copy(reached[k], walk, n);
                // Reported only when the walk went somewhere worse; back into the best one's own
                // minimum is not news.
                options.StartDone?.Invoke(new GlobalStart(start + 1, options.Starts, merit[k], bestMerit, newBest,
                                                          moved && !newBest && merit[k] > bestMerit * (1.0 + 1e-6)));
            }
        }

        // Stopped before any start finished: the design goes back to where it began.
        if (double.IsPositiveInfinity(bestMerit)) bestMerit = initial;
        for (int i = 0; i < n; ++i) design.SetVariable(i, best[i]);
        design.Evaluate();
        return new GlobalSearchResult(done, improvements, initial, design.Merit,
                                      options.Refinement.Cancellation.IsCancellationRequested);
    }
}
