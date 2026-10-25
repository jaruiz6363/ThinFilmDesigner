// Cli.cs — the command line, which is a translation layer and nothing else.
//
// EVERY COMMAND IS A CALL INTO ThinFilm.Api. Nothing here evaluates, optimises, or formats a
// number it computed itself; X1.8 binds to X1.7 precisely so that the CLI, the MCP server and the
// GUI cannot answer the same question differently. What this file owns is argument parsing, table
// formatting, and exit codes.
//
// FILE IN, FILE OUT, because a CLI process does not survive between invocations and a design has
// to. The API is session-based, which is right for a server and wrong for a shell: so each command
// loads a document, opens a session on it, does one thing, and writes the document back. That also
// makes the tool compose the way a shell user expects —
//
//     copy samples/Examples/AR-visible/AR-visible.tfd ar.tfd
//     thinfilm optimise ar.tfd --stages refine,needle,remove -o ar.tfd
//     thinfilm spectrum ar.tfd --from 400 --to 700 --steps 301 -o ar.csv
//
// TWO OUTPUT MODES AND BOTH ARE FIRST CLASS. A table for a person; --json for a script, emitting
// the API's own result shapes so that anything parsing it is parsing the same thing the MCP server
// would return. Human formatting rounds; the JSON does not.
//
// ARGUMENTS ARE PARSED BY HAND. System.CommandLine is a package, and every other project here
// restores nothing — a tool that builds anywhere without a network is worth more than a tidier
// option parser.

using System.Globalization;
using System.Text;
using System.Text.Json;
using ThinFilm.Api;

namespace ThinFilm.Cli;

/// <summary>
/// Named CommandLine and not Cli: a class with the same name as its own namespace makes
/// `Cli.Run` resolve to the namespace from anywhere outside it, which the test project found
/// immediately and a user of the library would have found later.
/// </summary>
public static partial class CommandLine
{
    // NaN is a blocked design's merit (a missing material, an empty merit function), written "NaN".
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Usage(output);
            return args.Length == 0 ? 1 : 0;
        }

        if (args[0] is "--version" or "version")
        {
            output.WriteLine($"thinfilm, ABI {new ThinFilmApi().AbiVersion}");
            return 0;
        }

        var options = Options.Parse(args[1..]);
        var api = new ThinFilmApi();

        try
        {
            int code = args[0] switch
            {
                "new" => New(api, options, output, error),
                "show" => Show(api, options, output, error),
                "describe" => Describe(api, options, output, error),
                "evaluate" => Evaluate(api, options, output, error),
                "spectrum" => Spectrum(api, options, output, error),
                "report" => Report(api, options, output, error),
                "plot" => PlotSettings(api, options, output, error),
                "optimise" or "optimize" => Optimise(api, options, output, error),
                "refine" or "global" or "needle" or "evolve" or "thin" => RunMethod(args[0], api, options, output, error),
                "needle-map" => Needle(api, options, output, error),
                "merit" => Merit(api, options, output, error),
                "stack" => Stack(api, options, output, error),
                "layer" => Layer(api, options, output, error),
                "units" => Units(api, options, output, error),
                "system" => SystemParameters(api, options, output, error),
                "materials" => Materials(api, options, output, error),
                "database" => Database(options, output, error),
                "samples" => SampleCatalog(options, output, error),
                "zemax" => Zemax(api, options, output, error),
                "optiland" => Optiland(api, options, output, error),
                _ => Unknown(args[0], error),
            };
            // A misspelt or misplaced option is said, not dropped: the command ran without it.
            if (code == 0 && options.Unread() is { Length: > 0 } unread)
            {
                error.WriteLine($"thinfilm: {args[0]} did not use {string.Join(", ", unread.Select(n => "--" + n))}; "
                                + "check the spelling, or thinfilm help for what it takes");
                return 2;
            }
            return code;
        }
        catch (UnusedOptionException e)
        {
            error.WriteLine($"thinfilm: {e.Message}");
            return 2;
        }
        catch (Exception e)
        {
            error.WriteLine($"thinfilm: {e.Message}");
            return 1;
        }
    }

    // ---- commands ---------------------------------------------------------------------------

    private static int New(ThinFilmApi api, Options o, TextWriter output, TextWriter error)
    {
        if (o.String("materials") is null)
            return Fail(error, "new needs --materials and --substrate; or start from a sample design "
                               + $"in {Samples.Folder}");
        return NewFromScratch(api, o, output, error);
    }

    private static int Show(ThinFilmApi api, Options o, TextWriter output, TextWriter error)
    {
        if (!Open(api, o, error, out var id)) return 1;
        var r = api.Describe(id!);
        if (!r.Ok) return Fail(error, r);

        if (o.Json) { output.WriteLine(JsonSerializer.Serialize(r.Value, Json)); return 0; }

        Summarise(r.Value!, output);
        if (r.Value!.Source is { } source) output.WriteLine($"  source: {source}");
        output.WriteLine();
        PrintStack(api, id!, output);
        return 0;
    }

    private static int Evaluate(ThinFilmApi api, Options o, TextWriter output, TextWriter error)
    {
        if (!Open(api, o, error, out var id)) return 1;
        var r = api.Evaluate(id!);
        if (!r.Ok) return Fail(error, r);

        if (o.Json) { output.WriteLine(JsonSerializer.Serialize(r.Value, Json)); return 0; }

        output.WriteLine($"  merit {r.Value!.Merit:G6}   "
                         + $"{r.Value.Operands.Length} operands, {r.Value.FlaggedOperands} flagged");
        output.WriteLine();
        output.WriteLine("    op  code        cmp      target        value     residual  flags");
        foreach (var op in r.Value.Operands)
            output.WriteLine($"  {op.Index,4}  {op.Code,-10}  {op.Comparison,-6} "
                             + $"{op.Target,11:G6} {op.Value,12:G6} {op.Residual,12:G6}  "
                             + string.Join(",", op.Flags));
        // The spec between the rows' sample points, where a thick design can leak unseen.
        if (api.CheckBetweenSamples(id!) is { Ok: true, Value: { Length: > 0 } between })
        {
            output.WriteLine();
            foreach (var w in between) output.WriteLine($"  ! {w.Text}");
        }
        return 0;
    }

    private static int Spectrum(ThinFilmApi api, Options o, TextWriter output, TextWriter error)
    {
        if (!Open(api, o, error, out var id)) return 1;

        // What is not given comes from the plot the design was saved with, if it has one.
        var saved = api.Describe(id!).Value?.Plot;
        string vs = (o.String("vs") ?? (saved?.VsAngle == true ? "angle" : "wavelength")).ToLowerInvariant();
        if (vs is not ("wavelength" or "angle"))
        { error.WriteLine("--vs takes wavelength or angle"); return 2; }
        bool vsAngle = vs == "angle";

        double from = o.Double("from") ?? (vsAngle ? saved?.AngleFrom ?? 0.0 : saved?.From ?? 400.0);
        double to = o.Double("to") ?? (vsAngle ? saved?.AngleTo ?? 89.0 : saved?.To ?? 800.0);
        int steps = (int)(o.Double("steps") ?? (vsAngle ? saved?.AngleSteps ?? 90 : saved?.Steps ?? 201));
        double angle = o.Double("angle") ?? saved?.Angle ?? 0.0;
        double wavelength = o.Double("wavelength") ?? saved?.Wavelength ?? 550.0;
        var quantities = o.String("quantities")
            ?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? saved?.Quantities ?? ["RA", "TA"];

        var r = api.Spectrum(id!, new SpectrumRequest(from, to, steps, angle, quantities, vsAngle, wavelength));
        if (!r.Ok) return Fail(error, r);

        // ANGLES IN DEGREES, as the Spectrum tab draws them: a retardation of 2.39 radians was
        // read as 2.39 degrees instead of 137.
        var report = SpectrumQuantities.InAxisUnits(r.Value!);

        if (o.Json) { output.WriteLine(JsonSerializer.Serialize(report, Json)); return 0; }
        // Beside the data, not in it: a CSV read back stays numbers.
        foreach (var line in report.Outside) error.WriteLine($"caution: {line}; those rows rest on an extrapolated index");

        // CSV, and the numbers are round-trip exact rather than rounded for display: a spectrum
        // written to a file is data somebody will read back, not a table somebody will glance at.
        // An angle that cannot be reached is written as NaN.
        var sb = new StringBuilder();
        sb.Append(vsAngle ? "angle" : "wavelength");
        foreach (var q in quantities) sb.Append(',').Append(q);
        sb.AppendLine();

        var wl = report.Angle ?? report.Wavelength;
        for (int i = 0; i < wl.Length; ++i)
        {
            sb.Append(wl[i].ToString("R", CultureInfo.InvariantCulture));
            foreach (var q in quantities)
                sb.Append(',').Append(report.Quantity[q][i].ToString("R", CultureInfo.InvariantCulture));
            sb.AppendLine();
        }

        var text = sb.ToString();
        if (o.Output is { } path) { Save(o, path, text); output.WriteLine($"wrote {path} ({wl.Length} rows)"); }
        else output.Write(text);
        return 0;
    }

    /// <summary>File > Export design report: to -o, or to standard output.</summary>
    private static int Report(ThinFilmApi api, Options o, TextWriter output, TextWriter error)
    {
        if (!Open(api, o, error, out var id)) return 1;
        var r = api.DesignReport(id!, new DesignReportRequest(o.Double("from"), o.Double("to"), o.Double("step"), o.Output));
        if (!r.Ok) return Fail(error, r);
        if (o.Json) { output.WriteLine(JsonSerializer.Serialize(r.Value, Json)); return 0; }
        if (o.Output is { } path)
        {
            output.WriteLine($"wrote {path}: {r.Value!.Wavelengths.Length} wavelengths, "
                             + $"{r.Value.Wavelengths[0].ToString("0.###", CultureInfo.InvariantCulture)} to {r.Value.Wavelengths[^1].ToString("0.###", CultureInfo.InvariantCulture)} nm");
            foreach (var note in r.Value.Notes) output.WriteLine($"note: {note}");
        }
        else output.Write(r.Value!.Text);
        return 0;
    }

    private static int Optimise(ThinFilmApi api, Options o, TextWriter output, TextWriter error)
    {
        if (!Open(api, o, error, out var id)) return 1;

        // --min-thickness is in the design's length unit, as for thin and evolve: 0.015 in a µm
        // design is 15 nm, not 0.015 nm.
        double perNm = api.Describe(id!).Value!.WavelengthUnit == "um" ? 1e-3 : 1.0;
        var request = new OptimiseRequest(
            Stages: o.String("stages") ?? "refine",
            Norm: o.String("norm"),
            MaxIterations: (int)(o.Double("iterations") ?? 300),
            GlobalStarts: (int)(o.Double("starts") ?? 24),
            NeedleInsertions: (int)(o.Double("insertions") ?? 6),
            MinimumThickness: (o.Double("min-thickness") ?? 2.0 * perNm) / perNm,
            AcceptMerit: o.Double("accept-merit") ?? 0.1,
            NeedleMaterials: Materials(o.String("insert")));

        var r = api.Optimise(id!, request);
        if (!r.Ok) return Fail(error, r);

        // Written back, or to -o, as a run is.
        string written = o.Output ?? o.Positional[0];
        Save(o, written, api.Document(id!).Value!.ToJson());

        if (o.Json)
        {
            output.WriteLine(JsonSerializer.Serialize(r.Value, Json));
            return 0;
        }

        var v = r.Value!;
        output.WriteLine($"  {string.Join(" then ", v.StagesRun)}: "
                         + $"merit {v.InitialMerit:G6} -> {v.FinalMerit:G6}"
                         + $" ({Improvement(v.InitialMerit, v.FinalMerit)})");
        output.WriteLine($"  {v.Layers} layer{(v.Layers == 1 ? "" : "s")}, {v.Outcome}, {v.ElapsedMilliseconds:F0} ms");
        output.WriteLine($"  wrote {written}");
        return 0;
    }

    /// <summary>--insert TiO2,SiO2: the materials needle may insert; null when not given.</summary>
    private static string[]? Materials(string? list) =>
        list?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// A refine, global, needle or evolve run, through the same StartRun / RunStatus / KeepRun the
    /// GUI's optimisation windows and the MCP tools use, so the lines printed are the lines they
    /// show. Ctrl+C stops the run; the best point it reached is kept, as Accept would keep it.
    /// </summary>
    private static int RunMethod(string method, ThinFilmApi api, Options o, TextWriter output, TextWriter error)
    {
        if (!Open(api, o, error, out var id)) return 1;

        // A minimum thickness is typed in the design's length unit, like every length it shows.
        double perNm = api.Describe(id!).Value!.WavelengthUnit == "um" ? 1e-3 : 1.0;
        var started = api.StartRun(id!, new RunRequest(
            method, o.String("norm"), (int)(o.Double("iterations") ?? 300),
            Starts: (int)(o.Double("starts") ?? 24),
            Insertions: (int)(o.Double("insertions") ?? 6),
            MinimumThickness: (o.Double("min-thickness") ?? 2.0 * perNm) / perNm,
            AllowedDegradation: (o.Double("degradation") ?? 5.0) / 100.0,
            AcceptMerit: o.Double("accept-merit") ?? 0.1,
            MaxRemovals: (int)(o.Double("removals") ?? 16),
            GlobalMode: o.String("mode") ?? "random",
            Perturbation: (o.Double("perturbation") ?? 10.0) / 100.0,
            RandomRange: o.Double("range") ?? 2.0,
            AcceptWorse: (o.Double("accept-worse") ?? 0.0) / 100.0,
            Seed: o.Double("seed") is { } seed ? (int)seed : null,
            Threads: o.Double("threads") is { } threads ? (int)threads : null,
            GrowthSteps: (int)(o.Double("growth-steps") ?? 8),
            GrowthQwot: o.Double("growth") ?? 1.0,
            GrowAt: o.String("grow-at") ?? "both",
            MaxLayers: (int)(o.Double("max-layers") ?? 0),
            Raise: o.Flag("raise"),
            NeedleMaterials: Materials(o.String("insert"))));
        if (!started.Ok) return Fail(error, started);
        string run = started.Value!.Run;

        ConsoleCancelEventHandler stop = (_, e) => { e.Cancel = true; api.StopRun(run); };
        Console.CancelKeyPress += stop;
        RunReport report;
        int shown = 0;
        try
        {
            while (true)
            {
                report = api.WaitRun(run, 100).Value!;
                var lines = api.RunStatus(run, shown).Value!;
                if (!o.Json) foreach (var line in lines.Log) output.WriteLine(line);
                shown += lines.Log.Length;
                if (report.State != "running" && shown >= lines.LogLines) break;
            }
        }
        finally { Console.CancelKeyPress -= stop; }

        if (report.State == "failed") return Fail(error, $"the run failed: {report.Error}");
        // What the window's Merit function and Layers tabs show, before the run is kept or reverted.
        RunComparison? comparison = null;
        if (o.Flag("compare"))
        {
            var c = api.CompareRun(run);
            if (!c.Ok) return Fail(error, c);
            comparison = c.Value!;
            if (!o.Json)
            {
                string unit = api.Describe(id!).Value!.WavelengthUnit;
                output.WriteLine();
                foreach (var line in ThinFilmApi.CompareLines(comparison, unit)) output.WriteLine(line);
                output.WriteLine();
                foreach (var line in ThinFilmApi.CompareStackLines(comparison, unit)) output.WriteLine(line);
                output.WriteLine();
            }
        }
        var ended = o.Flag("revert") ? api.RevertRun(run) : api.KeepRun(run);
        if (!ended.Ok) return Fail(error, ended);
        report = api.RunStatus(run, shown).Value!;
        if (!o.Json) foreach (var line in report.Log) output.WriteLine(line);

        // WRITTEN BACK, AS EVERY EDIT IS (the user, 2026-10-24: "Save by default"). A run without -o
        // used to say "kept" and save nothing, and the user guide's own examples lost every run that
        // way; -o writes elsewhere, and --revert, the way to try a run, leaves the file alone.
        string? target = o.Output ?? (o.Flag("revert") ? null : o.Positional[0]);
        if (target is not null) Save(o, target, api.Document(id!).Value!.ToJson());
        if (o.Json)
        {
            output.WriteLine(comparison is null
                ? JsonSerializer.Serialize(api.RunStatus(run).Value, Json)
                : JsonSerializer.Serialize(new { run = api.RunStatus(run).Value, comparison }, Json));
            return 0;
        }
        output.WriteLine(target is not null ? $"wrote {target}" : "(--revert: the design file is left as it was)");
        if (report.LogFile is { } log) output.WriteLine($"log   {log}");
        return 0;
    }

    private static int Needle(ThinFilmApi api, Options o, TextWriter output, TextWriter error)
    {
        if (!Open(api, o, error, out var id)) return 1;
        int samples = (int)(o.Double("samples") ?? 32);

        var r = api.NeedleMap(id!, samples);
        if (!r.Ok) return Fail(error, r);

        if (o.Json) { output.WriteLine(JsonSerializer.Serialize(r.Value, Json)); return 0; }

        var v = r.Value!;
        output.WriteLine($"  best: {v.Candidate[v.BestCandidate]} at {v.BestDepth:F2} nm, "
                         + $"slope {v.BestSlope:G4}");
        if (v.BestSlope >= 0.0)
            output.WriteLine("  (no insertion helps to first order — the design is needle-stationary)");
        output.WriteLine();
        output.WriteLine("    depth" + string.Concat(v.Candidate.Select(c => $"{c,14}")));
        for (int i = 0; i < v.Depth.Length; ++i)
        {
            output.Write($"  {v.Depth[i],7:F2}");
            for (int c = 0; c < v.Candidate.Length; ++c)
                output.Write($"{v.Map[c * v.Depth.Length + i],14:G4}");
            output.WriteLine();
        }
        return 0;
    }

    /// <summary>
    /// thinfilm merit clear|delete|weight|add &lt;design.tfd&gt; [...]: the Merit Function tab's Clear
    /// all, Delete operands, Adjust weight and Add operands buttons, through the same API calls
    /// (MeritTable.cs). AN EDIT IS WRITTEN BACK: to -o if given, otherwise to the file it read -
    /// a merit edit that printed a document and kept nothing would be no edit at all.
    /// </summary>
    private static int Merit(ThinFilmApi api, Options o, TextWriter output, TextWriter error)
    {
        string[] actions = ["clear", "delete", "weight", "add", "edit", "insert", "remove", "save", "open", "export"];
        if (o.Positional.Count < 2 || !actions.Contains(o.Positional[0]))
            return Fail(error, "merit needs an action and a design file: thinfilm merit clear|delete|weight|add|edit|insert|remove|save|open|export <design.tfd> [...]");
        if (o.Positional[0] is "edit" or "insert" or "remove") return MeritRow(api, o, output, error);
        string action = o.Positional[0], path = o.Positional[1];
        if (!File.Exists(path)) return Fail(error, $"no such file: {path}");
        var loaded = api.Load(File.ReadAllText(path));
        if (!loaded.Ok) return Fail(error, loaded);
        string id = loaded.Value!.Id;

        // The merit function as a file of its own; save and export leave the design untouched.
        if (action is "save" or "export")
        {
            var text = action == "save" ? api.SaveMeritFunction(id) : api.ExportMeritCsv(id);
            if (!text.Ok) return Fail(error, text);
            if (o.Output is { } to) { Save(o, to, text.Value!); output.WriteLine($"  wrote {to}"); }
            else output.Write(text.Value);
            return 0;
        }
        if (action == "open")
        {
            var file = o.String("file") ?? (o.Positional.Count > 2 ? o.Positional[2] : null);
            if (file is null) return Fail(error, "merit open needs the merit file: --file merit.tfm");
            if (!File.Exists(file)) return Fail(error, $"no such file: {file}");
            bool append = o.Flag("append");
            var opened = api.OpenMeritFunction(id, File.ReadAllText(file), append);
            if (!opened.Ok) return Fail(error, opened);
            string into = o.Output ?? path;
            Save(o, into, api.Document(id).Value!.ToJson());
            output.WriteLine(append
                ? $"  {N(opened.Value!.Rows, "row")} from {file} appended; {N(opened.Value.Design.Operands, "row")} now"
                : $"  {N(opened.Value!.Rows, "row")} from {file}, in place of the merit function there was");
            output.WriteLine($"  wrote {into}");
            return 0;
        }

        OperandFilter Filter()
        {
            AngleTest? angle = null;
            if (o.Double("aoi-above") is { } above) angle = new AngleTest("above", above);
            else if (o.Double("aoi-below") is { } below) angle = new AngleTest("below", below);
            else if (o.String("aoi-between") is { } between)
            {
                var ab = between.Split(',', StringSplitOptions.TrimEntries);
                if (ab.Length != 2 || !double.TryParse(ab[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
                    || !double.TryParse(ab[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var b))
                    throw new FormatException("--aoi-between wants two angles, e.g. 10,30");
                angle = new AngleTest("between", a, b);
            }
            return new OperandFilter(o.Double("from"), o.Double("to"), List(o.String("codes")), List(o.String("modes")), angle);
        }

        var r = action switch
        {
            "clear" => api.ClearOperands(id),
            "delete" => api.DeleteOperands(id, Filter()),
            "weight" => o.Double("weight") is { } w
                ? api.SetOperandWeights(id, Filter(), w)
                : ApiResult<MeritEditReport>.Failure("merit weight needs --weight <w>"),
            _ => Add(),
        };
        if (!r.Ok) return Fail(error, r);

        ApiResult<MeritEditReport> Add()
        {
            string? code = o.String("code"), mode = o.String("mode");
            double? target = o.Double("target"), from = o.Double("from");
            if (code is null || mode is null || target is null || from is null)
                return ApiResult<MeritEditReport>.Failure("merit add needs --code, --mode, --target and --from (and --to --step for a band)");
            return api.AddOperands(id, new OperandSweep(
                code, mode, target.Value, o.Double("weight") ?? 1.0, from.Value, o.Double("to"), o.Double("step"),
                o.Double("aoi") ?? o.Double("aoi-from") ?? 0.0, o.Double("aoi-to"), o.Double("aoi-step"),
                o.Double("target2") ?? 0.0, o.Double("temperature") ?? 0.0));
        }

        string written = o.Output ?? path;
        Save(o, written, api.Document(id).Value!.ToJson());
        if (o.Json) { output.WriteLine(JsonSerializer.Serialize(r.Value, Json)); return 0; }

        var d = r.Value!.Design;
        string verb = action switch { "clear" => "removed", "delete" => "deleted", "weight" => "reweighted", _ => "added" };
        output.WriteLine($"  {N(r.Value.Rows, "row")} {verb}; {N(d.Operands, "operand")}, merit "
                         + (d.Blocked is { } b ? $"not evaluated ({b})" : d.Merit.ToString("G6", CultureInfo.InvariantCulture)));
        output.WriteLine($"  wrote {written}");
        return 0;
    }

    private static string[]? List(string? s) =>
        s?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // ---- plumbing ---------------------------------------------------------------------------

    private static bool Open(ThinFilmApi api, Options o, TextWriter error, out string? id)
    {
        id = null;
        if (o.Positional.Count == 0)
        { error.WriteLine("thinfilm: this command needs a design file"); return false; }

        var path = o.Positional[0];
        if (!File.Exists(path))
        { error.WriteLine($"thinfilm: no such file: {path}"); return false; }

        var r = api.Load(File.ReadAllText(path));
        if (!r.Ok)
        {
            error.WriteLine($"thinfilm: {path}: {r.Error}"
                            + (r.Status is not null ? $" [{r.Status}]" : ""));
            return false;
        }
        id = r.Value!.Id;
        return true;
    }

    private static void Write(Options o, string json, TextWriter output)
    {
        if (o.Output is { } path) Save(o, path, json);
        else output.WriteLine(json);
    }

    private static string N(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

    private static void Summarise(DesignSummary s, TextWriter output)
    {
        output.WriteLine($"  {s.Name}: {N(s.Layers, "layer")}, {s.TotalThickness:F1} nm total, "
                         + $"{N(s.Operands, "operand")} over {N(s.Points, "point")}");
        output.WriteLine($"  {N(s.Variables, "variable")}, {N(s.Residuals, "residual")}, merit {s.Merit:G6}");
    }

    private static string Improvement(double before, double after) =>
        before > 0.0 && after < before
            ? $"{(1.0 - after / before) * 100:F1}% better"
            : after > before ? "worse" : "unchanged";

    private static int Fail(TextWriter error, string message)
    { error.WriteLine($"thinfilm: {message}"); return 1; }

    private static int Fail<T>(TextWriter error, ApiResult<T> r)
    {
        error.WriteLine($"thinfilm: {r.Error}" + (r.Status is not null ? $" [{r.Status}]" : ""));
        return 1;
    }

    private static int Unknown(string command, TextWriter error)
    {
        string[] known = ["new", "show", "describe", "evaluate", "spectrum", "plot", "optimise", "refine", "global", "needle", "evolve", "thin",
                          "merit", "stack", "layer", "units", "system", "materials", "database", "zemax", "optiland"];
        var near = known.Where(k => k.StartsWith(command[..Math.Min(2, command.Length)],
                                                StringComparison.OrdinalIgnoreCase)).ToArray();
        error.WriteLine($"thinfilm: '{command}' is not a command."
                        + (near.Length > 0 ? $" Did you mean {string.Join(" or ", near)}?" : ""));
        error.WriteLine("thinfilm: try 'thinfilm --help'");
        return 1;
    }

    private static void Usage(TextWriter output)
    {
        output.WriteLine("""
thinfilm — thin film design

  thinfilm new --materials TiO2,SiO2 --substrate N-BK7 [--incident air] [--name N] [--reference 550]
               [--layers 4] [--unit nm|um] [-o F]   File > New design, from the material database
  thinfilm show <design.tfd>               layers, counts and merit
  thinfilm describe <design.tfd> [--name "..."] [--source "..."] [--notes "..." | --notes-file F]
                                           the design's name, and its Description: where it came from,
                                           and notes; given, they are written back to the file (or -o);
                                           "" clears the source or notes (a name cannot be blank)
  thinfilm evaluate <design.tfd>           every operand: value and residual, and where a Min or Max
                                           row's spec is missed between its sample points
  thinfilm plot <design.tfd> [--from --to --steps --angle --quantities | --vs angle --wavelength] [--clear]
                                           the spectrum the design is saved with and opens to in the
                                           Spectrum tab; given, it is written back to the file (or -o)
  thinfilm spectrum <design.tfd> [...]     R/T/A on a grid, as CSV, against wavelength or angle
  thinfilm report <design.tfd> [--from 400 --to 800 --step 10] [-o report.txt]
                                           the layers in nm and an n,k table for every material used
                                           and both media, tab-separated, for a program without
                                           dispersion equations; the range defaults to the saved plot's
  thinfilm optimise <design.tfd> [...]     run several stages in one go, without a log
  thinfilm refine <design.tfd> [-o F]      damped least squares   [--norm L2] [--iterations 300]
  thinfilm global <design.tfd> [-o F]      multistart             [--starts 24] [--iterations 300]
                                                                  [--mode random|hop] [--seed N] [--threads N]
                                                                  random: [--range 2] (0 to 2x each thickness)
                                                                  hop: [--perturbation 10] [--accept-worse 0] (%)
  thinfilm needle <design.tfd> [-o F]      insert layers          [--insertions 6] [--iterations 300]
                                                                  [--insert TiO2,SiO2] (the materials a needle may be;
                                                                  default every one in the design)
  thinfilm evolve <design.tfd> [-o F]      gradual evolution      [--growth-steps 8] [--growth 1] (QWOT per step)
                                                                  [--grow-at both|incident|substrate] [--max-layers N]
                                                                  [--insertions 6] (needles per step) [--min-thickness 2]
                                                                  [--degradation 5] [--accept-merit 0.1] [--removals 16]
                                                                  [--iterations 300] [--insert TiO2,SiO2] (needles and
                                                                  grown layers)
  thinfilm thin <design.tfd> [-o F]        remove thin layers     [--min-thickness 2] [--iterations 300] [--raise]
                                           every layer under the minimum, whatever it costs; the log
                                           gives the merit after each removal. --raise instead raises
                                           each to the minimum and fixes it there, refining the rest
    each prints its log as it runs, the same lines the GUI's window shows; Ctrl+C stops and keeps
    the best so far. The result is written back to the design file, or to -o; --revert runs and
    logs but leaves the file as it was (with -o, writes the design as it was there). --min-thickness is in the design's length unit, --degradation in %;
    a removal leaving the merit under --accept-merit is kept however much it cost, and evolve stops there
    --compare prints, before keeping or reverting, every merit row before, after and the change,
    and the two stacks layer by layer: what the window's Merit function and Layers tabs show
  thinfilm needle-map <design.tfd>         where a layer would help, as a map
  thinfilm merit clear <design.tfd>        remove every merit row
  thinfilm merit delete <design.tfd> [filter]          remove the rows that match
  thinfilm merit weight <design.tfd> --weight W [filter]  reweight the rows that match
  thinfilm merit add <design.tfd> --code RA --mode Max --target 0.005 [--weight 1]
                     --from 420 [--to 680 --step 10] [--aoi 0 | --aoi-from 0 --aoi-to 60 --aoi-step 15]
    a band code (ASOL, RSOL, TSOL against AM1.5 global; EMIT against a blackbody at --temperature K,
    default 373.15; AERATIO = ASOL / EMIT) adds one row per angle averaging over --from to --to
  thinfilm merit edit <design.tfd> --row N [--code RA] [--mode Max] [--target T] [--target2 T2]
                     [--weight W] [--wavelength 550] [--wavelength2 650 --points 11] [--aoi 0] [--limit 8]
                     [--temperature 373.15]
  thinfilm merit insert <design.tfd> [--after N]   a copy of row N after it (the last row by default)
  thinfilm merit remove <design.tfd> --row N
  thinfilm merit save <design.tfd> [-o merit.tfm]      the merit function on its own (rows, no layers)
  thinfilm merit open <design.tfd> --file merit.tfm [--append]
                                           its rows in place of the design's merit function, or after it
  thinfilm merit export <design.tfd> [-o merit.csv]    the table as CSV, with each row's value and residual
    a merit edit is written back to the file, or to -o
  thinfilm stack string <design.tfd> --text "(1H 1L)4 1H" [--letters H=TiO2,L=SiO2] [--unit QWOT|FWOT]
                        [--substrate-first] [--no-pickups]    Input design string (letters not named
                        are guessed: H highest index, L lowest, M middle, else by first letter)
  thinfilm stack couple|decouple|clear <design.tfd>            the Stack menu's other items
  thinfilm stack reverse <design.tfd> [--media]                Reverse layers; --media swaps the incident
                        medium and the substrate too (an incident medium's k is treated as 0)
  thinfilm stack bounds <design.tfd> [--layers 3-7] [--material SiO2] [--unit nm|QWOT|FWOT]
                        [--min X] [--max Y]                   Set vary constraints; neither is unconstrained
  thinfilm layer insert <design.tfd> [--after N]               a copy of layer N after it, as Insert does
  thinfilm layer insert <design.tfd> --material M --thickness T [--unit U] [--before N|--after N] [--fixed]
  thinfilm layer remove <design.tfd> --layer N
  thinfilm layer move <design.tfd> --layer N --to M
  thinfilm layer set <design.tfd> --layer N [--material M] [--thickness T] [--unit nm|QWOT|FWOT|default]
                     [--vary yes|no] [--pickup P [--scale 1] [--offset 0] | --free] [--min X|none] [--max Y|none]
  thinfilm units <design.tfd> [--thickness nm|QWOT|FWOT] [--reference 550] [--wavelength nm|um] [--maximum 600]
  thinfilm system <design.tfd> [--back-surface yes|no] [--substrate-mm 1] [--angles air|medium]
                                           R and T with the substrate's back surface; angles in air or the medium
  thinfilm materials <design.tfd> [--add A,B] [--remove C] [--replace X --with Y] [--incident air] [--substrate N-BK7]
  thinfilm materials <design.tfd> --define NAME [--as coating|substrate|incident]
                     --sellmeier B1,C1,B2,C2,... (C in um^2) | --cauchy A,B,C | --constant n,k
                     | --table FILE [--table-unit nm|um] [--range 0.54,18.2 (um)] [--source TEXT]
                                           a material's dispersion: Edit dispersion, or New material
  thinfilm samples [words...]              the sample designs (examples, and designs from the
                                           literature by category and paper), as File > Open sample
                                           shows them; words narrow them: thinfilm samples polarizer
  thinfilm database                        the material database materials are taken from
  thinfilm database --save <design.tfd> --material HfO2 [--replace]
                                           a design's material into your own database, for later designs
  thinfilm zemax list COATING.DAT          the layer-stack coatings in an OpticStudio coating file
  thinfilm zemax import <design.tfd> --file COATING.DAT --coating NAME [-o F]
                                           the coating's layers in place of the design's; its MATEs
                                           added as materials (the model in a ThinFilmDesigner comment
                                           when it still matches the table); waves counted at the
                                           reference the file gives, else the design's
  thinfilm zemax export <design.tfd> --file COATING.DAT [--coating NAME] [--from 400 --to 700] [--replace]
                                           the design as one COAT in um, and n,k tables for its materials
                                           over --from to --to nm (default: the design's wavelengths);
                                           the rest of the file is kept as it was, a backup beside it
                                           as .bak; a MATE of the same name with other data is never
                                           changed - ours is written as NAME_2
  thinfilm optiland list coating.json      the thin-film coatings in an Optiland coating or lens file
  thinfilm optiland import <design.tfd> --file coating.json [--coating 1] [-o F]
                                           a coating's layers in place of the design's (numbered as
                                           list numbers them); its materials added
  thinfilm optiland export <design.tfd> --file coating.json
                                           the design as an Optiland ThinFilmCoating, materials as
                                           equations or tables, for load_obj_from_json(BaseCoating, path)
    layers count from 1, as the Design tab numbers them; a physical thickness, bound or maximum is
    typed in the design's length unit (um in a design shown in um); a stack, layer, units or
    materials edit is written back to the file, or to -o, and prints the stack as it now is

filter (merit delete and weight; anything left out is not filtered on)
  --from --to <nm>       rows whose wavelengths all lie in this band
  --codes <list>         e.g. RA,TA, or R for RA,RS,RP
  --modes <list>         Target, Min, Max, Range, Report
  --aoi-above <deg> | --aoi-below <deg> | --aoi-between <a,b>

options
  -o, --output <file>    write here instead of standard output
  --json                 emit JSON rather than a table
  --from --to --steps    spectrum grid (default: the design's saved plot, else 400 800 201)
  --angle <degrees>      spectrum angle of incidence (default 0)
  --vs angle             spectrum against angle instead: --from --to in degrees (default 0 89 90)
  --wavelength <nm>      at this wavelength (default 550); angles are in air or the incident
                         medium as `thinfilm system --angles` says, an unreachable one is NaN
  --quantities <list>    spectrum quantities (default RA,TA): RS RP RA TS TP TA AS AP AA,
                         ODS ODP ODA, R_PSI R_DELTA R_PHASE_S ... (angles in degrees)
  --stages <list>        refine, global, needle, evolve (gradual evolution), remove, thin (default refine)
  --norm <name>          L2, minimax or L1
  --iterations <n>       refinement budget (default 300)
  --samples <n>          needle map depth samples (default 32)

a pipeline, from a sample design (the samples folder beside thinfilm)
  copy samples/Examples/AR-visible/AR-visible.tfd ar.tfd
  thinfilm optimise ar.tfd --stages refine,needle,remove -o ar.tfd
  thinfilm spectrum ar.tfd --from 400 --to 700 --steps 301 -o ar.csv
""");
    }

    /// <summary>
    /// Every file the command line writes goes through here, so an option the command did not read
    /// stops it BEFORE anything is written: `evolve d.tfd --min-thicknes 10` ran at the default 2 nm
    /// and would have written that design, the misspelling said only afterwards (X1.79).
    /// </summary>
    private static void Save(Options o, string path, string text)
    {
        if (o.Unread() is { Length: > 0 } unread) throw new UnusedOptionException(unread);
        File.WriteAllText(path, text);
    }

    private sealed class UnusedOptionException(string[] names) : Exception(
        $"{string.Join(", ", names.Select(n => "--" + n))} {(names.Length == 1 ? "is" : "are")} not an option of this command, "
        + "so nothing was written; check the spelling, or thinfilm help for what it takes");

    // ---- arguments --------------------------------------------------------------------------

    private sealed class Options
    {
        public List<string> Positional { get; } = new();
        private readonly Dictionary<string, string> _named = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);

        // WHAT A COMMAND READ, so an option it did not is said rather than dropped: `materials
        // --rename SiO2 --to Silica` did nothing and said nothing before rename existed (X1.79).
        private readonly HashSet<string> _read = new(StringComparer.OrdinalIgnoreCase);

        public bool Json => Flag("json");
        public bool Flag(string name) { _read.Add(name); return _flags.Contains(name); }
        public string? Output => String("output") ?? String("o");

        public string? String(string name) { _read.Add(name); return _named.TryGetValue(name, out var v) ? v : null; }

        /// <summary>The options given that the command never looked at.</summary>
        public string[] Unread() => _named.Keys.Concat(_flags).Where(n => !_read.Contains(n)).Distinct().ToArray();

        public double? Double(string name)
        {
            var s = String(name);
            if (s is null) return null;
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                throw new FormatException($"--{name} wants a number, not '{s}'");
            return v;
        }

        public static Options Parse(string[] args)
        {
            var o = new Options();
            for (int i = 0; i < args.Length; ++i)
            {
                var a = args[i];
                if (!a.StartsWith('-')) { o.Positional.Add(a); continue; }

                var name = a.TrimStart('-');
                if (name.IndexOf('=') is var eq and > 0) { o._named[name[..eq]] = name[(eq + 1)..]; continue; }
                // A flag is a switch with no value; anything followed by a non-option is named. A
                // negative number is a value, not an option: a GDD target is usually below zero.
                if (name is "json" or "compare" or "revert" or "substrate-first" or "no-pickups" or "fixed" or "free" or "upgrade")
                { o._flags.Add(name); continue; }
                if (i + 1 < args.Length && (!args[i + 1].StartsWith('-')
                                            || double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
                { o._named[name] = args[++i]; continue; }
                o._flags.Add(name);
            }
            return o;
        }
    }
}
