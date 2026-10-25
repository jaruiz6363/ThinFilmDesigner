// Runs.cs — an optimisation run: started, watched, stopped, then kept or reverted.
//
// ONE RUNNER FOR EVERY FRONT END. The GUI's optimisation windows, the CLI's refine, global, needle
// and evolve commands, and the MCP tools optimise_start / _status / _stop / _keep / _revert all
// call these methods, so the log a person
// reads in the window is the log the CLI prints and the MCP returns, line for line. LensHH-LT has
// the same engine under its three front ends but a loop of its own in each; this is the part that
// keeps them from drifting.
//
// A RUN WORKS ON A COPY. It is started from the session's design as it is, on a second Design
// built from the same document, on a background thread. The session is not touched until Keep,
// which takes the copy's thicknesses; Revert drops the copy. Nothing has to be snapshotted or put
// back, and reading the session while a run is going (the spectrum, the merit) is safe, since
// native designs share no state. Keep is refused if the session's design has been changed since the
// run started, because the result was found for a design that is no longer there.
//
// WHAT IS RECORDED: the settings, the start time, the start and end RMS (the merit is a weighted
// RMS, abi-and-allocation-contract.md), the best so far, elapsed time, and a log. The log is written
// to a file per run as it goes (ThinFilmHome.LogDirectory), and a kept or reverted run is added to
// the design's History, which is saved with it.

using System.Diagnostics;
using System.Globalization;
using ThinFilm.Native;
using ThinFilm.Optimize;

namespace ThinFilm.Api;

/// <summary>
/// What to run: refine, global, needle, evolve or thin (every layer under MinimumThickness removed). MaxIterations is every refinement's budget (for
/// global, needle and evolve, the one after each start, insertion or removal). Norm is refine's;
/// Starts, GlobalMode ("random" or "hop", basin hopping), Perturbation (a fraction of each
/// thickness a hop may move it), RandomRange (random starts lie between 0 and this multiple of
/// each thickness), AcceptWorse (a fraction above the best a hop may be and still move the walk),
/// Seed (null: a new one each run) and Threads (hops or starts refined at once; null: every core
/// but one) global's; Insertions needle's (and evolve's needles per step); GrowthSteps, GrowthQwot
/// (the layer each step adds, in quarter waves at the reference wavelength), GrowAt ("incident",
/// "substrate" or "both"), MaxLayers (0 none), MinimumThickness (nm), AllowedDegradation (a fraction
/// of the merit a removal may cost), AcceptMerit (a merit good enough to keep a removal at however
/// much it cost, and to stop growing at; 0 is off) and MaxRemovals (per step) evolve's. Raise is
/// thin's: true raises every layer under MinimumThickness to it and fixes it there, refining the
/// rest, instead of removing it. NeedleMaterials (names; null for every material in the design) are
/// the ones needle may insert, and evolve may insert and grow.
/// </summary>
public sealed record RunRequest(
    string Method = "refine", string? Norm = null, int MaxIterations = 300, int Starts = 24,
    int Insertions = 6, double MinimumThickness = 2.0, double AllowedDegradation = 0.05, int MaxRemovals = 16,
    string GlobalMode = "random", double Perturbation = 0.10, double RandomRange = 2.0, double AcceptWorse = 0.0,
    int? Seed = null, int? Threads = null, int GrowthSteps = 8, double GrowthQwot = 1.0, string GrowAt = "both",
    int MaxLayers = 0, double AcceptMerit = 0.1, bool Raise = false, string[]? NeedleMaterials = null);

/// <summary>
/// A run as it stands. State: running, finished, stopped (ended by Stop, and can still be kept),
/// failed, kept or reverted. Merits are RMS; EndMerit is null until the run has ended. Iteration is
/// the steps done of Steps: iterations for refine, starts for global, insertions or removals tried
/// for needle and evolve. Layers is the best design's layer count. Log holds the lines from the
/// `since` asked for; LogLines is how many there are in all.
/// </summary>
public sealed record RunReport(
    string Run, string Design, string Method, string Settings, string State, string Started,
    double ElapsedSeconds, double StartMerit, double BestMerit, double? EndMerit, int Iteration,
    string? Outcome, string? Error, int LogLines, string[] Log, string? LogFile, int[] PlacedOnBound,
    int Steps = 0, int Layers = 0, string[]? BetweenSamples = null);

public sealed partial class ThinFilmApi
{
    private sealed class Run
    {
        public required string Id { get; init; }
        public required string Session { get; init; }
        public required string Method { get; init; }
        public required string Settings { get; init; }
        public required DateTime Started { get; init; }
        public required string StartJson { get; init; }
        public required DesignDocument StartDocument { get; init; }
        public required Design Design { get; set; }     // replaced by needle and evolve, whose shape changes
        public required int[] Placed { get; init; }
        public required double StartMerit { get; init; }
        public required int Steps { get; init; }
        public readonly Stopwatch Clock = new();
        public readonly CancellationTokenSource Stop = new();
        public readonly object Gate = new();
        public readonly List<string> Log = new();
        public Task? Task { get; set; }
        public StreamWriter? File { get; set; }
        public string? FilePath { get; set; }

        // Written by the worker under Gate, read by everyone else under it.
        public string State = "running";
        public double Best;
        public double? End;
        public int Iteration;
        public int Layers;
        public string? Outcome;
        public string? Error;
        /// <summary>The result's spec missed between the merit's samples (BetweenSamples.cs).</summary>
        public string[] BetweenSamples = [];
    }

    private readonly Dictionary<string, Run> _runs = new();
    private int _nextRun;

    /// <summary>Starts a run on the session's design and returns at once; the run goes on in the
    /// background. One run at a time per design.</summary>
    public ApiResult<RunReport> StartRun(string id, RunRequest request)
    {
        if (!Find(id, out var s, out var fail)) return ApiResult<RunReport>.Failure(fail!);
        if (s.Blocked is { } why) return ApiResult<RunReport>.Failure(why, "BLOCKED");
        if (_runs.Values.FirstOrDefault(r => r.Session == id && r.State == "running") is { } busy)
            return ApiResult<RunReport>.Failure($"run {busy.Id} is still going on this design; stop it first");

        string method = request.Method.Trim().ToLowerInvariant();
        var doc = Current(s);
        string norm = request.Norm ?? doc.Norm;
        if (RequestProblem(method, request, norm) is { } bad) return ApiResult<RunReport>.Failure(bad);
        if (CannotOptimise(doc, s.Design!, [method]) is { } refused)
            return ApiResult<RunReport>.Failure(refused);
        // THE MATERIALS NEEDLE MAY INSERT (the user's induced-transmission chapter, 2026-10-24):
        // a 2 nm silver needle refines well and cannot be made, so a metal can be left out.
        int[]? candidates = null;
        if (method is "needle" or "evolve")
        {
            candidates = NeedleCandidates(doc, request.NeedleMaterials, out var unknown);
            if (unknown is not null) return ApiResult<RunReport>.Failure(unknown);
        }
        string inserting = candidates is null ? ""
            : $", inserting {string.Join(" and ", candidates.Select(m => doc.Materials[m].Name))} only";

        Design copy;
        try
        {
            copy = new Design(doc.ToSpec());
            copy.Plan();
            copy.Evaluate();
        }
        catch (ThinFilmException e) { return ApiResult<RunReport>.Failure(e.Message, e.Status.ToString()); }

        var (lower, upper) = LayerUnits.Bounds(doc);
        string unit = doc.WavelengthUnit == "um" ? "um" : "nm";
        string Length(double nm) => unit == "um"
            ? $"{(nm / 1000.0).ToString("0.######", CultureInfo.InvariantCulture)} um"
            : $"{nm.ToString("0.###", CultureInfo.InvariantCulture)} nm";
        bool hopping = request.GlobalMode.Trim().ToLowerInvariant() == "hop";
        // A NEW SEED EACH RUN, written in the settings: running again tries other starts (the
        // user, 2026-10-17, on a fixed seed that repeated them), and naming it reproduces a run.
        int seed = request.Seed ?? Random.Shared.Next(100000, 1000000);
        // Every core but one, so the window and the rest of the machine stay responsive.
        int threads = request.Threads ?? Math.Max(1, Environment.ProcessorCount - 1);
        string Pct(double f) => (f * 100).ToString("0.##", CultureInfo.InvariantCulture);
        string percent = (request.Perturbation * 100).ToString("0.##", CultureInfo.InvariantCulture);
        // Wide enough for "trial 10000/10000", so the RMSE column stays straight.
        int stepWidth = $"trial {request.Starts}/{request.Starts}".Length;
        var run = new Run
        {
            Id = $"r{++_nextRun}",
            Session = id,
            Method = method,
            Settings = method switch
            {
                "refine" => $"norm {NormName(norm)}, max iterations {request.MaxIterations}",
                "global" => hopping
                    ? $"basin hopping, hops {request.Starts}, perturbation {percent}%, accept worse {Pct(request.AcceptWorse)}%, "
                      + $"seed {seed}, threads {threads}, max iterations per hop {request.MaxIterations}"
                    : $"random starts {request.Starts}, range 0 to {request.RandomRange.ToString("0.##", CultureInfo.InvariantCulture)}x "
                      + $"each thickness, seed {seed}, threads {threads}, max iterations per start {request.MaxIterations}",
                "needle" => $"max insertions {request.Insertions}{inserting}, max iterations per refine {request.MaxIterations}",
                "thin" => $"minimum thickness {Length(request.MinimumThickness)}, "
                          + (request.Raise ? "every thinner layer raised to it and fixed, " : "every thinner layer removed, ")
                          + $"max iterations per refine {request.MaxIterations}",
                _ => $"growth steps {request.GrowthSteps}, each adding {request.GrowthQwot.ToString("0.###", CultureInfo.InvariantCulture)} QWOT "
                     + $"at the {Side(request.GrowAt)}, needles per step {request.Insertions}{inserting}, "
                     + (request.MaxLayers > 0 ? $"max layers {request.MaxLayers}, " : "")
                     + $"minimum thickness {Length(request.MinimumThickness)}, allowed degradation "
                     + $"{(request.AllowedDegradation * 100).ToString("0.##", CultureInfo.InvariantCulture)}% or merit up to "
                     + $"{request.AcceptMerit.ToString("G3", CultureInfo.InvariantCulture)}, "
                     + $"max removals per step {request.MaxRemovals}, max iterations per refine {request.MaxIterations}",
            },
            Started = DateTime.Now,
            StartJson = Fingerprint(doc),
            StartDocument = doc,
            Design = copy,
            Placed = OutsideOwnBounds(doc),
            StartMerit = copy.Merit,
            Steps = method switch
            {
                "refine" => request.MaxIterations, "global" => request.Starts,
                "needle" => request.Insertions,
                // The layers under the minimum now; a join can take one more with it.
                "thin" => (copy.ToSpec().LayerThickness ?? []).Count(t => t < request.MinimumThickness),
                _ => request.GrowthSteps,
            },
        };
        run.Best = run.StartMerit;
        run.Layers = copy.LayerCount;
        _runs[run.Id] = run;

        OpenLogFile(run);
        Say(run, $"# ThinFilm {method}   {run.Started:yyyy-MM-dd HH:mm:ss}");
        Say(run, $"# design: {doc.Name}   {doc.Layers.Length} layer{(doc.Layers.Length == 1 ? "" : "s")}, {copy.VariableCount} varied");
        Say(run, $"# settings: {run.Settings}");
        if (run.Placed.Length > 0)
            Say(run, $"# {LayerUnits.Named(run.Placed)} started outside {(run.Placed.Length == 1 ? "its" : "their")} "
                     + "own min or max and will be placed on it first");
        Say(run, Line("start", $"RMSE={Rms(run.StartMerit)}   layers {copy.LayerCount}"));

        // Every method refines through these: the layers' own bounds, the budget, and Stop. Only a
        // refine run reports each of its iterations; the others report each start, insertion or
        // removal instead, and their inner refinements stay quiet.
        var refinement = new DlsOptions
        {
            Lower = lower,
            Upper = upper,
            MaxIterations = request.MaxIterations,
            Cancellation = run.Stop.Token,
            Progress = method != "refine" ? null : p =>
            {
                Progress(run, p.Iteration, p.Merit, null);
                Say(run, Line($"iter {p.Iteration,7}", $"RMSE={Rms(p.Merit)}   damping={p.Damping.ToString("0.0E+00", CultureInfo.InvariantCulture)}   {Clock(run.Clock.Elapsed)}"));
            },
        };
        string Name(int material) =>
            material >= 0 && material < doc.Materials.Length ? doc.Materials[material].Name : $"material {material}";

        Func<(Design Design, string Outcome)> work = method switch
        {
            "refine" => () =>
            {
                string outcome = Refine(copy, norm, refinement, p =>
                    Say(run, Line($"pass {p.Pass}/{p.Passes}", $"p={p.P.ToString("0.0", CultureInfo.InvariantCulture)}   RMSE={Rms(p.Merit)}   "
                             + $"worst={Rms(p.Worst)}   {Clock(run.Clock.Elapsed)}{(p.Best ? "   <- best" : "")}")));
                return (copy, $"{outcome}, {run.Iteration} iterations");
            },
            "global" => () =>
            {
                var r = GlobalSearch.Run(copy, new GlobalSearchOptions
                {
                    Starts = request.Starts,
                    Mode = hopping ? GlobalMode.BasinHopping : GlobalMode.Random,
                    Perturbation = request.Perturbation,
                    AcceptWorse = request.AcceptWorse,
                    Seed = seed,
                    Workers = threads,
                    Lower = refinement.Lower,
                    Upper = hopping ? SearchBox(refinement, copy) : RandomBox(refinement, copy, request.RandomRange),
                    Refinement = refinement,
                    // Every start is logged with what it reached, so a search that is not finding
                    // anything better can be seen not to (the user, 2026-10-17); the bests are marked.
                    StartDone = t =>
                    {
                        Progress(run, t.Start, t.Best, null);
                        string tag = $"{(hopping ? "hop" : "trial")} {t.Start}/{t.Starts}".PadRight(stepWidth);
                        Say(run, Line(tag, double.IsNaN(t.Merit)
                            ? $"refused by the engine, skipped   {Clock(run.Clock.Elapsed)}"
                            : $"RMSE={Rms(t.Merit)}   {Clock(run.Clock.Elapsed)}{(t.NewBest ? "   <- best" : t.WalkMoved ? "   (walk moves here)" : "")}"));
                    },
                });
                string what = hopping ? "hop" : "start";
                return (copy, $"{r.Improvements} improvement{(r.Improvements == 1 ? "" : "s")} over {r.Starts} {what}{(r.Starts == 1 ? "" : "s")}");
            },
            "needle" => () =>
            {
                var r = NeedleOptimiser.Run(copy, new NeedleOptions
                {
                    MaxInsertions = request.Insertions,
                    Candidates = candidates,
                    Refinement = refinement,
                    Refined = m =>
                    {
                        Progress(run, 0, m, null);
                        Say(run, Line("refined", $"RMSE={Rms(m)}   before the first needle map   {Clock(run.Clock.Elapsed)}"));
                    },
                    StepDone = t =>
                    {
                        Progress(run, t.Round, t.Kept ? t.Merit : run.Best, t.Kept ? t.Layers : null);
                        Say(run, Line(t.Kept ? $"insert {t.Round}" : $"rejected {t.Round}",
                            $"{Name(t.Material)} at {Length(t.Depth)}   RMSE={Rms(t.Merit)}   "
                            + (t.Kept ? $"layers {t.Layers}" : "not better, rolled back") + $"   {Clock(run.Clock.Elapsed)}"));
                    },
                });
                if (r.Stopped is { } why)
                    Say(run, Line("stopped", why));
                if (r.TargetMet)
                    Say(run, Line("target met", $"RMSE={Rms(r.FinalMerit)}, under a millionth of the refined start; nothing left to gain"));
                if (r.NeedleStationary)
                    Say(run, Line("stationary", "no insertion helps to first order"
                        // A STACK TOO THIN TO INTERFERE: a needle in it changes R and T only to
                        // second order, so the map is flat and the answer is a thicker start.
                        + (r.Design.ToSpec().LayerThickness?.Sum() < 100.0
                            ? $"; the stack is only {Length(r.Design.ToSpec().LayerThickness!.Sum())} thick, too thin for a needle to interfere in. Start from a thick layer, 1000 nm or more"
                            : "")));
                return (r.Design, $"{r.Insertions} inserted, {r.Rejected} rejected, {r.Design.LayerCount} layers");
            },
            "thin" when request.Raise => () =>
            {
                // RAISE AND FIX (the user, 2026-10-24: "Fixed layers"): the slivers that are doing
                // real work stay, at the minimum, and the rest of the stack makes up for them.
                int raisedSoFar = 0;
                var r = GradualEvolution.RaiseThinLayers(copy, new EvolutionOptions
                {
                    MinimumThickness = request.MinimumThickness,
                    Refinement = refinement,
                    RaiseDone = s =>
                    {
                        raisedSoFar += s.Layers.Length;
                        Progress(run, raisedSoFar, s.Merit, null);
                        Say(run, Line($"raise {s.Round}", $"{LayerUnits.Named(s.Layers)} to "
                            + $"{Length(request.MinimumThickness)}, fixed   RMSE={Rms(s.Merit)}   {Clock(run.Clock.Elapsed)}"));
                    },
                });
                return (r.Design, r.Raised == 0
                    ? $"no layer is thinner than {Length(request.MinimumThickness)}"
                    : $"{r.Raised} raised to {Length(request.MinimumThickness)} and fixed, in {r.Rounds} round{(r.Rounds == 1 ? "" : "s")}");
            },
            "thin" => () =>
            {
                // REMOVE THIN LAYERS (the user, 2026-10-24: "a very good idea"): evolve keeps a
                // sliver that is earning its place, and at a merit near zero every one is. Here each
                // goes regardless, and the log prices it.
                var r = GradualEvolution.RemoveThinLayers(copy, new EvolutionOptions
                {
                    MinimumThickness = request.MinimumThickness,
                    Force = true,
                    MaxRemovals = int.MaxValue,
                    Refinement = refinement,
                    StepDone = t =>
                    {
                        Progress(run, t.Round, t.Merit, t.Layers);
                        Say(run, Line($"remove {t.Round}", $"layer {t.Layer + 1}, {Length(t.Thickness)}   RMSE={Rms(t.Merit)}   "
                            + $"layers {t.Layers}{(t.Merged ? ", neighbours joined" : "")}   {Clock(run.Clock.Elapsed)}"));
                    },
                });
                var left = (r.Design.ToSpec().LayerThickness ?? []).Count(t => t < request.MinimumThickness);
                return (r.Design, r.Removed == 0
                    ? $"no layer is thinner than {Length(request.MinimumThickness)}"
                    : $"{r.Removed} removed, {r.Merged} with neighbours joined, {r.Design.LayerCount} layers"
                      + (left > 0 ? $"; {left} still under {Length(request.MinimumThickness)}, as the only layer left" : ""));
            },
            _ => () =>
            {
                // GRADUAL EVOLUTION (the user, 2026-10-18: "Yes please modify evolve"): each step
                // grows the stack by a layer of GrowthQwot quarter waves at the reference wavelength,
                // then needles and removes as needle and the old evolve did; the best step is the
                // result. Every needle, removal and growth is logged, and each step's merit, layer
                // count and total thickness, which is the curve to read.
                var growth = doc.Materials.Select((_, m) => request.GrowthQwot / LayerUnits.PerNm(doc, m, LayerUnits.Qwot)).ToArray();
                var r = GradualEvolution.Evolve(copy, new GrowthOptions
                {
                    GrowthThickness = growth,
                    MaxSteps = request.GrowthSteps,
                    Side = request.GrowAt.Trim().ToLowerInvariant() switch
                    {
                        "incident" => GrowthSide.Incident, "substrate" => GrowthSide.Substrate, _ => GrowthSide.Both,
                    },
                    InsertionsPerStep = request.Insertions,
                    Candidates = candidates,
                    MaxLayers = request.MaxLayers,
                    MinimumThickness = request.MinimumThickness,
                    AllowedDegradation = request.AllowedDegradation,
                    AcceptMerit = request.AcceptMerit,
                    MaxRemovals = request.MaxRemovals,
                    Refinement = refinement,
                    Refined = m =>
                    {
                        Progress(run, 0, m, null);
                        Say(run, Line("refined", $"RMSE={Rms(m)}   {Clock(run.Clock.Elapsed)}"));
                    },
                    Grown = g => Say(run, Line($"grow {g.Step}",
                        $"{Name(g.Material)} next to the {(g.Incident ? "incident medium" : "substrate")}, {Length(g.Thickness)}   "
                        + $"RMSE={Rms(g.Merit)}   layers {g.Layers}   {Clock(run.Clock.Elapsed)}")),
                    NeedleDone = (step, t) => Say(run, Line(t.Kept ? $"insert {step}.{t.Round}" : $"rejected {step}.{t.Round}",
                        $"{Name(t.Material)} at {Length(t.Depth)}   RMSE={Rms(t.Merit)}   "
                        + (t.Kept ? $"layers {t.Layers}" : "not better, rolled back") + $"   {Clock(run.Clock.Elapsed)}")),
                    RemovalDone = (step, t) => Say(run, Line(t.Kept ? $"remove {step}.{t.Round}" : $"declined {step}.{t.Round}",
                        $"layer {t.Layer + 1}, {Length(t.Thickness)}   RMSE={Rms(t.Merit)}   "
                        + (t.Kept ? $"layers {t.Layers}{(t.Merged ? ", neighbours merged" : "")}"
                                  : "worse than allowed, the layer stays") + $"   {Clock(run.Clock.Elapsed)}")),
                    CameBack = (step, earlier, multiple) => Say(run, Line("came back",
                        $"step {step} is the design of step {earlier} again; the next layer grows "
                        + $"{multiple.ToString("0.##", CultureInfo.InvariantCulture)} times as thick   {Clock(run.Clock.Elapsed)}")),
                    StepDone = c =>
                    {
                        Progress(run, c.Step, c.NewBest ? c.Merit : run.Best, c.NewBest ? c.Layers : null);
                        Say(run, Line($"step {c.Step}", $"RMSE={Rms(c.Merit)}   layers {c.Layers}, total {Length(c.TotalThickness)}   "
                                                         + $"{Clock(run.Clock.Elapsed)}{(c.NewBest ? "   <- best" : "")}"));
                    },
                });
                Say(run, Line("done", r.Stopped));
                if (r.Preferred is { } fewer)
                    Say(run, Line("chose", fewer));
                return (r.Design, $"{(r.Preferred is null ? "best" : "chose")} at step {r.BestStep} of {r.Steps}: {r.Design.LayerCount} layers, "
                                  + $"{r.Inserted} needles kept, {r.Removed} layers removed; {r.Stopped}");
            },
        };

        run.Clock.Start();
        run.Task = Task.Run(() =>
        {
            try
            {
                var (result, outcome) = work();
                result.Evaluate();
                run.Clock.Stop();
                bool stopped = run.Stop.IsCancellationRequested;
                // The last line goes in before the state changes, so whoever sees the run end has
                // already been able to read it.
                // The result checked between the merit's sample points, where it can leak unseen.
                string[] between = [];
                try
                {
                    var check = BetweenSamples(Unregistered(Documents.Rebuild(run.StartDocument, result)));
                    if (check.Ok) between = check.Value!.Select(w => w.Text).ToArray();
                }
                catch (Exception) { }      // a check that cannot run is not a reason to lose the run
                foreach (var w in between) Say(run, Line("check", w));
                Say(run, Line(stopped ? "stopped" : "end", $"RMSE={Rms(result.Merit)}   "
                         + $"({outcome})   {Seconds(run.Clock.Elapsed)}"));
                lock (run.Gate)
                {
                    run.BetweenSamples = between;
                    run.Design = result;
                    run.End = result.Merit;
                    run.Best = Math.Min(run.Best, result.Merit);
                    run.Layers = result.LayerCount;
                    run.Outcome = stopped ? $"stopped: {outcome}" : outcome;
                    run.State = stopped ? "stopped" : "finished";
                }
            }
            catch (Exception e)
            {
                run.Clock.Stop();
                Say(run, Line("failed", e.Message));
                lock (run.Gate) { run.Error = e.Message; run.State = "failed"; }
            }
        });
        return ApiResult<RunReport>.Success(Report(run, 0));
    }

    /// <summary>Why a request cannot be run, or null.</summary>
    private static string? RequestProblem(string method, RunRequest r, string norm)
    {
        if (method is not ("refine" or "global" or "needle" or "evolve" or "thin"))
            return $"'{r.Method}' is not a method; try refine, global, needle, evolve or thin";
        if (r.MaxIterations < 1) return $"{r.MaxIterations} is not a number of iterations; at least 1";
        return method switch
        {
            "refine" => NormProblem(norm),
            "global" when r.Starts < 1 => $"{r.Starts} is not a number of starts; at least 1",
            "global" when r.GlobalMode.Trim().ToLowerInvariant() is not ("random" or "hop")
                => $"'{r.GlobalMode}' is not a global search mode; random or hop",
            "global" when !(r.Perturbation > 0) => $"a perturbation of {r.Perturbation} moves nothing; more than 0",
            "global" when !(r.RandomRange > 0) => $"a range of {r.RandomRange} times each thickness holds nothing; more than 0",
            "global" when r.Threads is < 1 => $"{r.Threads} is not a number of threads; at least 1",
            "global" when !(r.AcceptWorse >= 0) => $"accepting {r.AcceptWorse} worse is not a fraction; 0 or more",
            "needle" when r.Insertions < 1 => $"{r.Insertions} is not a number of insertions; at least 1",
            "evolve" or "thin" when !(r.MinimumThickness > 0) =>$"a minimum thickness of {r.MinimumThickness} nm removes nothing; more than 0",
            "evolve" when !(r.AllowedDegradation >= 0) => $"an allowed degradation of {r.AllowedDegradation} is not one; 0 or more",
            "evolve" when !(r.AcceptMerit >= 0) => $"a merit of {r.AcceptMerit} to accept is not one; 0 or more",
            "evolve" when r.MaxRemovals < 1 => $"{r.MaxRemovals} is not a number of removals; at least 1",
            "evolve" when r.GrowthSteps < 0 => $"{r.GrowthSteps} is not a number of growth steps; 0 or more",
            "evolve" when !(r.GrowthQwot > 0) => $"a growth layer of {r.GrowthQwot} QWOT adds nothing; more than 0",
            "evolve" when r.GrowAt.Trim().ToLowerInvariant() is not ("incident" or "substrate" or "both")
                => $"'{r.GrowAt}' is not a side to grow at; incident, substrate or both",
            "evolve" when r.MaxLayers < 0 => $"{r.MaxLayers} is not a number of layers; 0 (no limit) or more",
            "evolve" when r.Insertions < 1 => $"{r.Insertions} is not a number of needles per step; at least 1",
            _ => null,
        };
    }

    /// <summary>The materials needle may insert, as indices, from their names; null for every
    /// material in the design, named or not.</summary>
    internal static int[]? NeedleCandidates(DesignDocument doc, string[]? names, out string? problem)
    {
        problem = null;
        if (names is null) return null;
        var picked = new SortedSet<int>();
        foreach (var name in names.Select(n => n.Trim()).Where(n => n.Length > 0))
        {
            int m = Array.FindIndex(doc.Materials, x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (m < 0)
            {
                problem = $"'{name}' is not one of the design's materials ({string.Join(", ", doc.Materials.Select(x => x.Name))})";
                return null;
            }
            picked.Add(m);
        }
        if (picked.Count == 0) { problem = "needle needs at least one material to insert"; return null; }
        return picked.Count == doc.Materials.Length ? null : picked.ToArray();
    }

    private static string Side(string growAt) => growAt.Trim().ToLowerInvariant() switch
    {
        "incident" => "incident side", "substrate" => "substrate side", _ => "better of the two sides",
    };

    /// <summary>A step done: the count, the best merit so far, and the layer count when it changed.</summary>
    private static void Progress(Run run, int step, double merit, int? layers)
    {
        lock (run.Gate)
        {
            run.Iteration = step;
            run.Best = Math.Min(run.Best, merit);
            if (layers is { } n) run.Layers = n;
        }
    }

    /// <summary>
    /// The box random starts are drawn from: 0 (or a layer's own minimum) to <paramref name="range"/>
    /// times each layer's thickness, inside its own bounds. Measured on Bandpass over three seeds
    /// of 100 starts: the whole 0-400 nm box ended at 35.6 on average, never better than the refined
    /// starting design; 0-1.5x at 19.0, 0-2x at 16.6, 0-3x at 23.3.
    /// </summary>
    private static double[] RandomBox(DlsOptions bounds, Design design, double range) =>
        SearchBox(bounds, design).Select((u, i) =>
            Math.Min(u, Math.Max(bounds.Lower![i], range * Math.Max(design.GetVariable(i), bounds.Lower[i])))).ToArray();

    /// <summary>The box global search draws its starts from: each layer's own bounds, with an open
    /// top closed at four times the larger of its minimum and its current thickness.</summary>
    private static double[] SearchBox(DlsOptions bounds, Design design) =>
        bounds.Upper!.Select((u, i) => double.IsFinite(u) ? u
            : 4.0 * Math.Max(bounds.Lower![i], design.GetVariable(i))).ToArray();

    /// <summary>The run as it stands, with the log lines from <paramref name="since"/> on.</summary>
    public ApiResult<RunReport> RunStatus(string run, int since = 0) =>
        FindRun(run, out var r, out var fail) ? ApiResult<RunReport>.Success(Report(r!, since))
                                              : ApiResult<RunReport>.Failure(fail!);

    /// <summary>Asks a running run to stop at the end of its current iteration, and returns at once;
    /// the design it reached can still be kept.</summary>
    public ApiResult<RunReport> StopRun(string run)
    {
        if (!FindRun(run, out var r, out var fail)) return ApiResult<RunReport>.Failure(fail!);
        if (r!.State == "running")
        {
            Say(r, Line("stop asked", Clock(r.Clock.Elapsed)));
            r.Stop.Cancel();
        }
        return ApiResult<RunReport>.Success(Report(r, 0));
    }

    /// <summary>Waits for a run to end, or for the timeout; the report says which.</summary>
    public ApiResult<RunReport> WaitRun(string run, int timeoutMs = Timeout.Infinite)
    {
        if (!FindRun(run, out var r, out var fail)) return ApiResult<RunReport>.Failure(fail!);
        r!.Task?.Wait(timeoutMs);
        return ApiResult<RunReport>.Success(Report(r, 0));
    }

    /// <summary>Every run this API has started, newest last, without their logs.</summary>
    public RunReport[] Runs() => _runs.Values.Select(r => Report(r, int.MaxValue)).ToArray();

    /// <summary>The run's design becomes the session's, and the run goes in the design's history.</summary>
    public ApiResult<DesignSummary> KeepRun(string run)
    {
        if (!FindRun(run, out var r, out var fail)) return ApiResult<DesignSummary>.Failure(fail!);
        if (Ended(r!) is { } notYet) return ApiResult<DesignSummary>.Failure(notYet);
        if (r!.State == "failed")
            return ApiResult<DesignSummary>.Failure($"run {r.Id} failed ({r.Error}); there is nothing to keep");
        if (!Find(r.Session, out var s, out fail)) return ApiResult<DesignSummary>.Failure(fail!);
        // The result was found for the design as it was when the run started.
        if (Fingerprint(Current(s)) != r.StartJson)
            return ApiResult<DesignSummary>.Failure(
                $"the design has been changed since run {r.Id} started, so its result no longer fits it; revert the run and start another");

        lock (r.Gate) r.State = "kept";
        Say(r, Line("kept", $"RMSE={Rms(r.End!.Value)}"));
        CloseLogFile(r);
        var doc = Documents.Rebuild(r.StartDocument, r.Design);
        s.Design = r.Design;
        s.Document = WithHistory(doc, r, "kept");
        return ApiResult<DesignSummary>.Success(Summarise(s));
    }

    /// <summary>Drops the run's result; the session's design is as it was. A run still going is
    /// stopped first. The run goes in the design's history as reverted.</summary>
    public ApiResult<DesignSummary> RevertRun(string run)
    {
        if (!FindRun(run, out var r, out var fail)) return ApiResult<DesignSummary>.Failure(fail!);
        if (r!.State is "kept" or "reverted")
            return ApiResult<DesignSummary>.Failure($"run {r.Id} was already {r.State}");
        if (r.State == "running")
        {
            r.Stop.Cancel();
            r.Task?.Wait();
        }
        lock (r.Gate) r.State = "reverted";
        Say(r, Line("reverted", "the design is as it was before the run"));
        CloseLogFile(r);
        if (!Find(r.Session, out var s, out fail)) return ApiResult<DesignSummary>.Failure(fail!);
        if (r.End is not null)
            s.Document = WithHistory(Current(s), r, "reverted");
        return ApiResult<DesignSummary>.Success(Summarise(s));
    }

    // ---- the parts -------------------------------------------------------------------------

    private bool FindRun(string id, out Run? run, out string? failure)
    {
        if (_runs.TryGetValue(id, out run)) { failure = null; return true; }
        failure = _runs.Count == 0 ? $"no run '{id}'; none has been started"
                                   : $"no run '{id}'; there are {string.Join(", ", _runs.Keys)}";
        return false;
    }

    private static string? Ended(Run r) => r.State switch
    {
        "running" => $"run {r.Id} is still going; stop it or wait for it to finish",
        "kept" or "reverted" => $"run {r.Id} was already {r.State}",
        _ => null,
    };

    private static RunReport Report(Run r, int since)
    {
        lock (r.Gate)
        {
            var lines = since >= r.Log.Count ? [] : r.Log.Skip(Math.Max(0, since)).ToArray();
            return new RunReport(r.Id, r.Session, r.Method, r.Settings, r.State,
                r.Started.ToString("s", CultureInfo.InvariantCulture), r.Clock.Elapsed.TotalSeconds,
                r.StartMerit, r.Best, r.End, r.Iteration, r.Outcome, r.Error, r.Log.Count, lines,
                r.FilePath, r.Placed, r.Steps, r.Layers, r.BetweenSamples);
        }
    }

    private static DesignDocument WithHistory(DesignDocument doc, Run r, string result) => doc with
    {
        History = [.. doc.History ?? [], new RunRecord(
            r.Started.ToString("s", CultureInfo.InvariantCulture), r.Method, r.Settings, r.StartMerit,
            r.End ?? r.Best, Math.Round(r.Clock.Elapsed.TotalSeconds, 3), r.Outcome ?? "", result)],
    };

    /// <summary>A log line: kept with the run, and written to its file as it comes.</summary>
    private static void Say(Run r, string line)
    {
        lock (r.Gate)
        {
            r.Log.Add(line);
            try { r.File?.WriteLine(line); }
            catch (IOException) { r.File = null; }
        }
    }

    private static void OpenLogFile(Run r)
    {
        // A log that cannot be written is not a reason to refuse the run; the window still has it.
        try
        {
            Directory.CreateDirectory(ThinFilmHome.LogDirectory);
            // Run ids start again in every process, so two CLI runs in the same second would share
            // a name; CreateNew finds the first one free instead of writing over another run's log.
            for (int n = 1; r.File is null; ++n)
            {
                string name = $"{r.Method}-{r.Started:yyyyMMdd-HHmmss}{(n > 1 ? $"-{n}" : "")}.log";
                r.FilePath = Path.Combine(ThinFilmHome.LogDirectory, name);
                try { r.File = new StreamWriter(new FileStream(r.FilePath, FileMode.CreateNew)) { AutoFlush = true }; }
                catch (IOException) when (File.Exists(r.FilePath) && n < 100) { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            r.FilePath = null;
            r.File = null;
        }
    }

    private static void CloseLogFile(Run r)
    {
        lock (r.Gate)
        {
            r.File?.Dispose();
            r.File = null;
        }
    }

    private static string NormName(string norm) =>
        norm.ToUpperInvariant() switch { "L2" => "L2", "L1" => "L1", _ => "minimax" };

    /// <summary>An RMS as every log line writes it.</summary>
    public static string Rms(double merit) => merit.ToString("0.000000E+00", CultureInfo.InvariantCulture);

    /// <summary>The first word or two in a column of their own, so the RMS values line up.</summary>
    private static string Line(string tag, string rest) => $"{tag,-12} {rest}";

    /// <summary>The design as Keep compares it: everything but its history.</summary>
    private static string Fingerprint(DesignDocument doc) => (doc with { History = null }).ToJson();

    private static string Clock(TimeSpan t) => t.ToString(@"hh\:mm\:ss\.f", CultureInfo.InvariantCulture);

    private static string Seconds(TimeSpan t) => $"{t.TotalSeconds.ToString("0.0##", CultureInfo.InvariantCulture)} s";
}
