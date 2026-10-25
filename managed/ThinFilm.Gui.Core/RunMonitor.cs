// RunMonitor.cs — what an optimisation window shows and does, without the window.
//
// One for each method - refine, global, needle, evolve, thin - each with its own settings. The window
// (ThinFilm.Gui RunDialog) ticks Poll every 200 ms and copies these strings into its controls;
// everything that decides what they say is here, so it is tested without a screen. The run itself
// is ThinFilmApi's (Runs.cs), the one the CLI and the MCP server use, so the log lines in the
// window are theirs too.

using System.Globalization;
using System.Text;
using ThinFilm.Api;

namespace ThinFilm.Gui.Core;

public sealed class RunMonitor
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly DesignWorkspace _workspace;
    private readonly StringBuilder _log = new();
    private int _lines;

    public static IReadOnlyList<string> Methods { get; } = ["refine", "global", "needle", "evolve", "thin"];

    public RunMonitor(DesignWorkspace workspace, string method = "refine")
    {
        if (!Methods.Contains(method)) throw new ArgumentException($"'{method}' is not a method", nameof(method));
        _workspace = workspace;
        Method = method;
        LengthUnit = workspace.WavelengthUnit;
        MaterialNames = workspace.MaterialNames.ToArray();
        Inserted = new HashSet<string>(MaterialNames);
        MinimumThicknessText = Lengths.Shown(2.0, LengthUnit).ToString("0.#####", Inv);
    }

    public string Method { get; }

    /// <summary>The design's length unit, which the minimum thickness is typed in.</summary>
    public string LengthUnit { get; }

    public string Title => Method switch
    {
        "refine" => "Refine",
        "global" => "Global search",
        "needle" => "Needle optimisation",
        "thin" => "Remove thin layers",
        _ => "Gradual evolution",
    };

    public string Description => Method switch
    {
        "refine" => "Damped least squares on the varied layer thicknesses, within each layer's min and max. "
                    + "L1 and minimax are reached by reweighting passes around it.",
        "global" => "Refines from many starts and keeps the best, for a design refine leaves in a poor local "
                    + "minimum. Random starts are drawn anew inside the layers' bounds each time; basin hopping "
                    + "perturbs the best design so far, which suits a good structured design such as a bandpass. "
                    + "The log lists every start, the new bests marked.",
        "needle" => "Inserts a thin layer where the needle map says one helps most, refines, and keeps it only "
                    + "if the merit falls; then tries again. This is how a design gains layers.",
        "thin" => "Every layer thinner than the minimum is either removed, thinnest first, joining the layers either "
                  + "side when they are the same material, or raised to the minimum and fixed there. Either way the rest "
                  + "is refined, and the log gives the merit after each change: the price of a design that can be made.",
        _ => "Grows the design a layer at a time (Tikhonravov's gradual evolution): each step adds a layer at the "
             + "outside, refines, inserts needles while they help, and removes layers thinner than the minimum. "
             + "Step 0 adds nothing. The log gives each step's RMSE, layers and total thickness; the best step is the result.",
    };

    // ---- the settings, as typed --------------------------------------------------------------

    public static IReadOnlyList<string> NormOptions { get; } = ["L2", "L1", "minimax"];
    public string Norm { get; set; } = "L2";
    public string IterationsText { get; set; } = "300";
    public string StartsText { get; set; } = "24";
    public const string RandomStarts = "Random starts";
    public const string BasinHopping = "Basin hopping (around the best)";
    public static IReadOnlyList<string> GlobalModes { get; } = [RandomStarts, BasinHopping];
    public string GlobalMode { get; set; } = RandomStarts;
    public string PerturbationText { get; set; } = "10";
    public string AcceptWorseText { get; set; } = "0";
    public string RandomRangeText { get; set; } = "2";
    /// <summary>Blank for a new seed each run.</summary>
    public string SeedText { get; set; } = "";
    /// <summary>Starts or hops refined at once: every core but one, to begin with.</summary>
    public string ThreadsText { get; set; } = Math.Max(1, Environment.ProcessorCount - 1).ToString(Inv);
    /// <summary>The perturbation applies to basin hopping only.</summary>
    public bool PerturbationApplies => GlobalMode == BasinHopping;
    public string InsertionsText { get; set; } = "6";
    public string MinimumThicknessText { get; set; }
    public string DegradationText { get; set; } = "5";
    /// <summary>A merit good enough to keep a removal at, whatever it cost; 0 is off.</summary>
    public string AcceptMeritText { get; set; } = "0.1";
    public string RemovalsText { get; set; } = "16";
    public string GrowthStepsText { get; set; } = "8";
    /// <summary>The layer each growth step adds, in quarter waves at the reference wavelength.</summary>
    public string GrowthQwotText { get; set; } = "1";
    public const string GrowEither = "Either side (the better)";
    public const string GrowIncident = "Incident side";
    public const string GrowSubstrate = "Substrate side";
    public static IReadOnlyList<string> GrowSides { get; } = [GrowEither, GrowIncident, GrowSubstrate];
    public string GrowAt { get; set; } = GrowEither;
    /// <summary>Blank for no limit.</summary>
    public string MaxLayersText { get; set; } = "";

    /// <summary>The design's materials, in order.</summary>
    public IReadOnlyList<string> MaterialNames { get; }

    /// <summary>The materials needle may insert (and evolve grow): every one, to begin with. A thin
    /// needle of a metal refines well and cannot be made, so it can be left out.</summary>
    public HashSet<string> Inserted { get; }

    public bool ShowsNorm => Method == "refine";
    public bool ShowsStarts => Method == "global";
    public bool ShowsInsertions => Method is "needle" or "evolve";
    public string InsertionsLabel => Method == "evolve" ? "Needles per step" : "Max insertions";
    public bool ShowsEvolution => Method == "evolve";
    public bool ShowsMinimumThickness => Method is "evolve" or "thin";
    public bool ShowsThinAction => Method == "thin";

    /// <summary>Remove thin layers: raise each to the minimum and fix it there instead of removing it.</summary>
    public bool Raise { get; set; }
    public string IterationsLabel => Method == "refine" ? "Max iterations" : "Max iterations per refine";
    public string MinimumThicknessUnit => Lengths.Physical(LengthUnit);

    /// <summary>The request the settings make, or null with the reason.</summary>
    public RunRequest? ToRequest(out string? problem)
    {
        string? first = null;
        int Whole(string text, string what)
        {
            if (int.TryParse(text.Trim(), NumberStyles.Integer, Inv, out int n) && n >= 1) return n;
            first ??= $"'{text.Trim()}' is not a number of {what}; at least 1";
            return 0;
        }
        int Count(string text, string what)
        {
            if (int.TryParse(text.Trim(), NumberStyles.Integer, Inv, out int n) && n >= 0) return n;
            first ??= $"'{text.Trim()}' is not a number of {what}; 0 or more";
            return 0;
        }
        double Number(string text, string what)
        {
            if (double.TryParse(text.Trim(), NumberStyles.Float, Inv, out double v) && double.IsFinite(v)) return v;
            first ??= $"'{text.Trim()}' is not a {what}";
            return 0;
        }

        int iterations = Whole(IterationsText, "iterations");
        string[]? inserted = null;
        if (ShowsInsertions && MaterialNames.Any(m => !Inserted.Contains(m)))
        {
            inserted = MaterialNames.Where(Inserted.Contains).ToArray();
            if (inserted.Length == 0) first ??= "tick at least one material for needle to insert";
        }
        var r = new RunRequest(Method, Norm, iterations, NeedleMaterials: inserted);
        r = Method switch
        {
            "global" => r with
            {
                Starts = Whole(StartsText, PerturbationApplies ? "hops" : "starts"),
                GlobalMode = PerturbationApplies ? "hop" : "random",
                Perturbation = PerturbationApplies ? Number(PerturbationText, "perturbation") / 100.0 : 0.10,
                AcceptWorse = PerturbationApplies ? Number(AcceptWorseText, "percentage to accept") / 100.0 : 0.0,
                RandomRange = PerturbationApplies ? 2.0 : Number(RandomRangeText, "range"),
                Seed = string.IsNullOrWhiteSpace(SeedText) ? null : Whole(SeedText, "seed"),
                Threads = Whole(ThreadsText, "threads"),
            },
            "needle" => r with { Insertions = Whole(InsertionsText, "insertions") },
            "thin" => r with
            {
                MinimumThickness = Lengths.ToNm(Number(MinimumThicknessText, "minimum thickness"), LengthUnit),
                Raise = Raise,
            },
            "evolve" => r with
            {
                MinimumThickness = Lengths.ToNm(Number(MinimumThicknessText, "minimum thickness"), LengthUnit),
                AllowedDegradation = Number(DegradationText, "percentage") / 100.0,
                AcceptMerit = Number(AcceptMeritText, "merit to accept"),
                MaxRemovals = Whole(RemovalsText, "removals"),
                Insertions = Whole(InsertionsText, "needles per step"),
                GrowthSteps = Count(GrowthStepsText, "growth steps"),
                GrowthQwot = Number(GrowthQwotText, "growth layer thickness"),
                GrowAt = GrowAt == GrowIncident ? "incident" : GrowAt == GrowSubstrate ? "substrate" : "both",
                MaxLayers = string.IsNullOrWhiteSpace(MaxLayersText) ? 0 : Whole(MaxLayersText, "layers"),
            },
            _ => r,
        };
        problem = first;
        return problem is null ? r : null;
    }

    // ---- the run -----------------------------------------------------------------------------

    /// <summary>The run as last polled; null before Start.</summary>
    public RunReport? Report { get; private set; }

    public string Log => _log.ToString();

    public bool Running => Report?.State == "running";

    /// <summary>Start is offered before the first run, and again once a run has been reverted (or
    /// has failed), so a person can change the settings and try again in the same window (the
    /// user, 2026-10-18).</summary>
    public bool CanStart => Report is null || Report.State is "reverted" or "failed";

    public bool CanStop => Running;

    /// <summary>A finished or stopped run has a design to keep; a failed one does not.</summary>
    public bool CanAccept => Report?.State is "finished" or "stopped";

    /// <summary>Starts the run; returns why it was refused, or null.</summary>
    public string? Start()
    {
        if (!CanStart) return "this window has already run; accept or revert it";
        if (ToRequest(out var problem) is not { } request) return problem;
        var r = _workspace.StartRun(request);
        if (r is null) return _workspace.Status;
        // A new run, a new log: the last one is in its own file.
        _log.Clear();
        _lines = 0;
        _compare = null;
        Report = r;
        Poll();
        return null;
    }

    /// <summary>Reads the run's state and any new log lines; true when something changed.</summary>
    public bool Poll()
    {
        if (Report is null) return false;
        var r = _workspace.RunStatus(Report.Run, _lines);
        if (r is null) return false;
        foreach (var line in r.Log) _log.AppendLine(line);
        _lines += r.Log.Length;
        bool changed = r.Log.Length > 0 || r.State != Report.State || r.Iteration != Report.Iteration;
        Report = r;
        return changed;
    }

    public void Stop()
    {
        if (Running) _workspace.StopRun(Report!.Run);
    }

    /// <summary>Keeps the run's design; returns why it could not, or null.</summary>
    public string? Accept()
    {
        if (!CanAccept) return "there is no finished run to accept";
        return _workspace.KeepRun(Report!.Run) ? null : _workspace.Status;
    }

    /// <summary>Drops the run, stopping it first if it is still going and waiting until it has: the
    /// design is as it was, and nothing is left running. Nothing to do before Start.</summary>
    public void Revert()
    {
        if (Report is null || Report.State is "kept" or "reverted") return;
        _workspace.RevertRun(Report.Run);
        _compare = null;
        Poll();
    }

    // ---- before and after --------------------------------------------------------------------

    private RunCompareView? _compare;

    /// <summary>The Merit function, Spectrum and Layers tabs open once the run has ended with a
    /// design (finished or stopped), and close again when it is reverted.</summary>
    public bool CanCompare => Report?.State is "finished" or "stopped";

    /// <summary>The run's design beside the start, built once per run; null before the run has
    /// ended, or with the reason in the workspace's Status when it cannot be built.</summary>
    public RunCompareView? Compare()
    {
        if (!CanCompare) return null;
        if (_compare is null && _workspace.CompareRun(Report!.Run) is { } c) _compare = new RunCompareView(_workspace, c);
        return _compare;
    }

    /// <summary>Why Compare gave nothing, when it did.</summary>
    public string CompareProblem => $"the run cannot be compared with the start: {_workspace.Status}";

    /// <summary>Revert is offered while a run is going (it stops it) and once it has ended, until
    /// it is accepted or reverted.</summary>
    public bool CanRevert => Report?.State is "running" or "finished" or "stopped";

    // ---- what the window shows ---------------------------------------------------------------

    public string StateText => Report switch
    {
        null => "Ready. The design is not changed until you accept the result.",
        { State: "running" } => "Running…",
        { State: "finished" } r => $"Finished: {r.Outcome}.{Advice(r.Outcome)}{Between(r)}",
        { State: "stopped" } => "Stopped. The best design reached can still be accepted.",
        { State: "failed" } r => $"Failed: {r.Error}",
        { State: "reverted" } => "Reverted: the design is as it was. Change the settings and start again, or close.",
        { State: var s } => char.ToUpperInvariant(s[0]) + s[1..] + ".",
    };

    /// <summary>The worst place the result misses its spec between the merit's samples, and how
    /// many more the log lists.</summary>
    private static string Between(RunReport r) => r.BetweenSamples is { Length: > 0 } b
        ? $"\n⚠ {char.ToUpperInvariant(b[0][0])}{b[0][1..]}{(b.Length > 1 ? $" ({b.Length - 1} more in the log)" : "")}."
        : "";

    public string ElapsedText => Report is null ? "—" : $"{Report.ElapsedSeconds.ToString("0.0", Inv)} s";

    public string StepLabel => Method switch
    {
        "refine" => "Iteration",
        "global" => PerturbationApplies ? "Hop" : "Start",
        "needle" => "Insertion",
        "thin" => Raise ? "Raised" : "Removal",
        _ => "Step",
    };

    /// <summary>The steps done, and of how many where that is a fixed number. Raising has no fixed
    /// number: refining can push more layers under the minimum, so it counts the layers raised.</summary>
    public string StepText => Report is null ? "—"
        : Method == "refine" || (Method == "thin" && Raise) ? Report.Iteration.ToString(Inv)
        : $"{Report.Iteration} of {Report.Steps}";

    public string LayersText => Report is null ? "—" : Report.Layers.ToString(Inv);

    public string StartMeritText => Report is null ? "—" : ThinFilmApi.Rms(Report.StartMerit);

    /// <summary>"Best" while it runs, "End" once it has ended.</summary>
    public string CurrentLabel => Report?.EndMerit is null ? "Best RMSE" : "End RMSE";

    public string CurrentMeritText => Report is null ? "—" : ThinFilmApi.Rms(Report.EndMerit ?? Report.BestMerit);

    public string ChangeText
    {
        get
        {
            if (Report is null || !(Report.StartMerit > 0.0)) return "—";
            // A start already under the accepted merit meets its spec; a percentage of it is
            // noise (+3173806 % for 5e-7 to 0.017, the user guide's bandpass, 2026-10-23).
            // Remove thin layers too: 7e-12 to 2e-7 read +2788531.7 % (the user guide's dichroic).
            if (Method is "evolve" or "thin" && double.TryParse(AcceptMeritText, NumberStyles.Float, Inv, out double accept)
                && Report.StartMerit <= accept) return "—";
            double now = Report.EndMerit ?? Report.BestMerit;
            double pct = (now - Report.StartMerit) / Report.StartMerit * 100.0;
            return pct == 0.0 ? "no change" : $"{pct.ToString("+0.0;−0.0", Inv)} %";
        }
    }

    public string LogFileText => Report?.LogFile is { } f ? $"Log file: {f}" : "";

    /// <summary>What a refine outcome means, for the ones that need saying.</summary>
    private static string Advice(string? outcome) => outcome?.Split(',')[0] switch
    {
        "Stalled" => " The merit stopped improving before the gradient reached its tolerance; a global search may do better.",
        "StepTooSmall" => " Every step had become too small to move.",
        "IterationLimit" => " The merit was still falling; more iterations may help.",
        "NoDownhillStep" => " No small change to any thickness lowers the merit: a local minimum. Try a global search, or needle to add layers.",
        _ => "",
    };
}
