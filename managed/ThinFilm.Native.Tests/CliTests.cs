// CliTests.cs — X1.8, run in process so the tests read what a user would have seen.
//
// CommandLine.Run takes its writers as arguments for exactly this reason. A CLI whose only interface is a
// spawned process is a CLI nobody tests: every check becomes a string match against stdout from a
// child, and the first flaky one gets deleted. Here the dispatcher is called directly, the output
// is a StringWriter, and the exit code is a return value.
//
// WHAT IS WORTH CHECKING IN A CLI, given that it computes nothing. Three things:
//
//   - the pipeline composes — new, optimise, spectrum, each reading what the last wrote;
//   - what it PRINTS agrees with what the API returns, because a front end that rounds a number
//     into a different number is the one bug a translation layer can still have;
//   - mistakes exit non-zero and say something actionable, since a CLI that fails silently or
//     exits zero on failure breaks every script that uses it.

using System.Text.Json;
using ThinFilm.Api;
using ThinFilm.Cli;
using ThinFilm.Gui.Core;

namespace ThinFilm.Native.Tests;

public static class CliTests
{
    /// <summary>A sample design copied to <paramref name="path"/>, the way a person starts one.</summary>
    private static (int Code, string Out, string Err) Sample(string name, string path)
    {
        File.Copy(SampleFiles.PathOf(name), path, overwrite: true);
        return (0, "", "");
    }

    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        int code = CommandLine.Run(args, o, e);
        return (code, o.ToString(), e.ToString());
    }

    private static string Temp(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "thinfilm-cli-tests");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, name);
    }

    public static void HelpAndVersionWork(Harness h)
    {
        h.Section("help lists the commands; no arguments is an error that still helps");

        var help = Run("--help");
        h.Pin(help.Code, 0, "--help exits zero");
        foreach (var command in new[] { "new", "show", "evaluate", "spectrum",
                                        "optimise", "refine", "global", "needle", "evolve", "needle-map" })
            h.Expect(help.Out.Contains(command, StringComparison.Ordinal),
                     $"help mentions '{command}'");

        var bare = Run();
        h.Pin(bare.Code, 1, "no arguments is an error");
        h.Expect(bare.Out.Contains("thinfilm", StringComparison.Ordinal),
                 "and still prints the usage rather than nothing");

        var version = Run("--version");
        h.Pin(version.Code, 0, "--version exits zero");
        h.Expect(version.Out.Contains(TfAbi.TF_ABI_VERSION.ToString(), StringComparison.Ordinal),
                 "and names the ABI it is bound to");
    }

    /// <summary>
    /// THE PIPELINE. Each command reads what the last one wrote, which is the whole reason a CLI
    /// is file-in file-out rather than session-based.
    /// </summary>
    public static void ThePipelineComposes(Harness h)
    {
        h.Section("a sample, then optimise, then spectrum — each reading what the last wrote");

        var design = Temp("pipeline.json");
        var optimised = Temp("pipeline-opt.json");
        var csv = Temp("pipeline.csv");
        foreach (var f in new[] { design, optimised, csv }) File.Delete(f);

        var made = Sample("AR-visible", design);
        h.Pin(made.Code, 0, "a sample is copied");
        h.Expect(File.Exists(design), "and wrote the design");

        var before = new ThinFilmApi();
        var loadedBefore = before.Load(File.ReadAllText(design));
        h.Expect(loadedBefore.Ok, "the written design loads through the API");
        double startMerit = loadedBefore.Value!.Merit;

        var opt = Run("optimise", design, "--stages", "refine,needle,evolve",
                      "--iterations", "120", "-o", optimised);
        h.Pin(opt.Code, 0, $"optimise exits zero: {opt.Err}");
        h.Expect(File.Exists(optimised), "and wrote the result");

        var after = new ThinFilmApi();
        var loadedAfter = after.Load(File.ReadAllText(optimised));
        h.Expect(loadedAfter.Ok, "which loads too");
        h.Expect(loadedAfter.Value!.Merit < startMerit,
                 $"and is better than what went in ({startMerit:G6} -> {loadedAfter.Value.Merit:G6})");

        var spec = Run("spectrum", optimised, "--from", "420", "--to", "680",
                       "--steps", "27", "--quantities", "RA", "-o", csv);
        h.Pin(spec.Code, 0, $"spectrum exits zero: {spec.Err}");
        h.Expect(File.Exists(csv), "and wrote the CSV");

        var lines = File.ReadAllLines(csv);
        h.Pin(lines.Length, 28, "a header and 27 rows");
        h.Expect(lines[0] == "wavelength,RA", $"with a header naming the columns: {lines[0]}");
    }

    /// <summary>
    /// THE ONE BUG A TRANSLATION LAYER CAN STILL HAVE. The CLI computes nothing, so the only way
    /// it can be wrong about a number is by printing a different one — rounding a spectrum into a
    /// file somebody then reads back as data.
    /// </summary>
    public static void WhatIsPrintedIsWhatTheApiReturned(Harness h)
    {
        h.Section("the CSV carries the API's numbers to the last bit, not a rounded view of them");

        var design = Temp("exact.json");
        var csv = Temp("exact.csv");
        File.Delete(design); File.Delete(csv);

        h.Pin(Sample("Bandpass", design).Code, 0, "a design is written");
        h.Pin(Run("spectrum", design, "--from", "500", "--to", "600", "--steps", "11",
                  "--quantities", "TA", "-o", csv).Code, 0, "and a spectrum");

        var api = new ThinFilmApi();
        var id = api.Load(File.ReadAllText(design)).Value!.Id;
        var expected = api.Spectrum(id, new SpectrumRequest(500, 600, 11, 0.0, ["TA"])).Value!;

        var lines = File.ReadAllLines(csv);
        for (int i = 0; i < 11; ++i)
        {
            var parts = lines[i + 1].Split(',');
            h.PinExact(double.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture),
                       expected.Wavelength[i], $"row {i}: wavelength round-trips");
            h.PinExact(double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
                       expected.Quantity["TA"][i], $"row {i}: TA round-trips");
        }
    }

    public static void JsonOutputIsTheApisOwnShape(Harness h)
    {
        h.Section("--json emits what the API returns, so a script and the MCP server agree");

        var design = Temp("json.json");
        File.Delete(design);
        h.Pin(Sample("V-coat", design).Code, 0, "a design is written");

        var ev = Run("evaluate", design, "--json");
        h.Pin(ev.Code, 0, "evaluate --json exits zero");

        var report = JsonSerializer.Deserialize<EvaluationReport>(ev.Out);
        h.Expect(report is not null, "and parses as the API's EvaluationReport");

        var api = new ThinFilmApi();
        var id = api.Load(File.ReadAllText(design)).Value!.Id;
        var direct = api.Evaluate(id).Value!;

        h.PinExact(report!.Merit, direct.Merit, "the merit matches the API exactly");
        h.Pin(report.Operands.Length, direct.Operands.Length, "and every operand is there");
        for (int i = 0; i < report.Operands.Length; ++i)
            h.PinExact(report.Operands[i].Value, direct.Operands[i].Value,
                       $"operand {i} value matches exactly");
    }

    /// <summary>
    /// A CLI that exits zero on failure breaks every script that uses it, and one that fails
    /// without saying why wastes the next hour of somebody's day.
    /// </summary>
    public static void MistakesExitNonZeroAndSaySomethingUseful(Harness h)
    {
        h.Section("every mistake exits non-zero, on stderr, naming what to do instead");

        var unknown = Run("shwo", "x.json");
        h.Pin(unknown.Code, 1, "an unknown command fails");
        h.Expect(unknown.Err.Contains("show", StringComparison.Ordinal),
                 $"and suggests the real one: {unknown.Err.Trim()}");
        h.Expect(unknown.Out.Length == 0, "with nothing on stdout, so a pipeline is not poisoned");

        var missing = Run("show", Temp("does-not-exist.json"));
        h.Pin(missing.Code, 1, "a missing file fails");
        h.Expect(missing.Err.Contains("no such file", StringComparison.Ordinal),
                 "and says so");

        var bareNew = Run("new");
        h.Pin(bareNew.Code, 1, "new without --materials fails");
        h.Expect(bareNew.Err.Contains("--materials", StringComparison.Ordinal) && bareNew.Err.Contains("sample", StringComparison.Ordinal),
                 $"and names the missing option, and where the samples are: {bareNew.Err.Trim()}");

        var design = Temp("errors.json");
        File.Delete(design);
        Sample("AR-visible", design);

        var badStage = Run("optimise", design, "--stages", "polish");
        h.Pin(badStage.Code, 1, "an unknown stage fails");
        h.Expect(badStage.Err.Contains("needle", StringComparison.Ordinal),
                 $"and lists the real stages: {badStage.Err.Trim()}");

        var badNumber = Run("spectrum", design, "--steps", "many");
        h.Pin(badNumber.Code, 1, "a non-numeric option fails");
        h.Expect(badNumber.Err.Contains("steps", StringComparison.Ordinal),
                 $"and names the option: {badNumber.Err.Trim()}");

        // A malformed document must be refused by the loader rather than crashing the process.
        var broken = Temp("broken.json");
        File.WriteAllText(broken, "{ \"Version\": 1, \"Wavelength\": [] }");
        var loadBroken = Run("show", broken);
        h.Pin(loadBroken.Code, 1, "a malformed document fails");
        h.Expect(loadBroken.Err.Length > 0, "with a message");
    }

    public static void TheNeedleMapIsReadableFromTheCommandLine(Harness h)
    {
        h.Section("needle-map prints the map and names the insertion it recommends");

        var design = Temp("needle.json");
        File.Delete(design);
        h.Pin(Sample("Bandpass", design).Code, 0, "a design is written");

        var r = Run("needle-map", design, "--samples", "8");
        h.Pin(r.Code, 0, $"needle-map exits zero: {r.Err}");
        h.Expect(r.Out.Contains("best:", StringComparison.Ordinal), "it names a best insertion");
        h.Expect(r.Out.Contains("depth", StringComparison.Ordinal), "and prints the map");

        // The printed best agrees with the API's, which is the only thing the CLI could get wrong.
        var api = new ThinFilmApi();
        var id = api.Load(File.ReadAllText(design)).Value!.Id;
        var map = api.NeedleMap(id, 8).Value!;
        h.Expect(r.Out.Contains(map.Candidate[map.BestCandidate], StringComparison.Ordinal),
                 "and names the same candidate the API chose");
    }

    public static void AMinimumThicknessIsInTheDesignsUnit(Harness h)
    {
        h.Section("a minimum thickness is typed in the design's length unit: the run windows, thin and optimise");

        // A µm design with a 10 nm layer: 0.015 is 15 nm, so the layer goes.
        string f = Temp("micron.tfd");
        Run("new", "--materials", "TiO2,SiO2", "--substrate", "N-BK7", "--reference", "550", "--layers", "4", "-o", f);
        Run("units", f, "--wavelength", "um");
        Run("layer", "set", f, "--layer", "2", "--thickness", "0.010", "--unit", "nm");
        var api = new ThinFilmApi();
        int Layers(string path) => api.Load(File.ReadAllText(path)).Value!.Layers;
        h.Pin(Layers(f), 4, "four layers, the second 10 nm");

        string a = Temp("micron-thin.tfd"), b = Temp("micron-optimise.tfd");
        h.Expect(Run("thin", f, "--min-thickness", "0.015", "-o", a).Code == 0 && Layers(a) < 4,
                 $"thin --min-thickness 0.015 removes it ({Layers(a)} layers)");
        h.Expect(Run("optimise", f, "--stages", "thin", "--min-thickness", "0.015", "-o", b).Code == 0 && Layers(b) == Layers(a),
                 $"and so does optimise --stages thin ({Layers(b)} layers)");

        // The windows: the default 2 nm is shown as 0.002 µm and read back as 2 nm.
        var w = new DesignWorkspace();
        w.Open(File.ReadAllText(f), f);
        var m = new RunMonitor(w, "thin");
        h.Expect(m.MinimumThicknessText == "0.002" && m.MinimumThicknessUnit == "µm",
                 $"the Remove thin layers window shows {m.MinimumThicknessText} {m.MinimumThicknessUnit}");
        m.MinimumThicknessText = "0.015";
        h.Expect(m.ToRequest(out _) is { } r && Math.Abs(r.MinimumThickness - 15.0) < 1e-9,
                 "and 0.015 typed there is 15 nm");
    }

    public static void MaxLayersIsAHardCap(Harness h)
    {
        h.Section("Max layers is a hard cap: growth stops there and no needle passes it");

        // The user's request through MCP, 2026-10-25: an AR from 450 to 1000 nm on N-BK7 in up to 12
        // layers. --max-layers 12 stopped the growth at 12, and the needles that followed made 16.
        string f = Temp("ar-12.tfd");
        Run("new", "--materials", "TiO2,SiO2", "--substrate", "N-BK7", "--reference", "700", "--layers", "1", "-o", f);
        Run("stack", "string", f, "--text", "1L", "--letters", "L=SiO2");
        Run("merit", "clear", f);
        Run("merit", "add", f, "--code", "RA", "--mode", "Max", "--target", "0.005", "--from", "450", "--to", "1000", "--step", "10");
        var api = new ThinFilmApi();
        foreach (int cap in new[] { 12, 9, 6 })
        {
            string g = Temp($"ar-cap-{cap}.tfd");
            var run = Run("evolve", f, "--min-thickness", "10", "--accept-merit", "0", "--max-layers", cap.ToString(), "-o", g);
            int layers = api.Load(File.ReadAllText(g)).Value!.Layers;
            h.Expect(run.Code == 0 && layers <= cap && layers > 1, $"--max-layers {cap}: {layers} layers");
        }
    }

    public static void EvolveMeansOneThing(Harness h)
    {
        h.Section("evolve is gradual evolution everywhere: thinfilm evolve, optimise --stages evolve; the clean-up is remove");

        // The user, 2026-10-25, approving: the stage called evolve removed thin layers while the method
        // called evolve was gradual evolution, so an agent asked to evolve got the wrong one unawares.
        string start = Temp("one-layer.tfd");
        Run("new", "--materials", "TiO2,SiO2", "--substrate", "N-BK7", "--reference", "550", "--layers", "1", "-o", start);
        Run("merit", "clear", start);
        Run("merit", "add", start, "--code", "RA", "--mode", "Max", "--target", "0.005", "--from", "420", "--to", "680", "--step", "10");
        string a = Temp("evolve-run.tfd"), b = Temp("evolve-stage.tfd"), c = Temp("remove-stage.tfd");
        var api = new ThinFilmApi();
        (int Layers, double Merit) Of(string path) { var d = api.Load(File.ReadAllText(path)).Value!; return (d.Layers, d.Merit); }
        h.Expect(Run("evolve", start, "-o", a).Code == 0 && Run("optimise", start, "--stages", "evolve", "-o", b).Code == 0,
                 "both run");
        var (ra, rb) = (Of(a), Of(b));
        h.Expect(ra.Layers > 1 && ra.Layers == rb.Layers && Math.Abs(ra.Merit - rb.Merit) <= 1e-9 * Math.Max(1.0, ra.Merit),
                 $"the stage grows the design as the run does: {rb.Layers} layers, merit {rb.Merit:G6} (run: {ra.Layers}, {ra.Merit:G6})");
        h.Expect(Run("optimise", start, "--stages", "remove", "-o", c).Code == 0 && Of(c).Layers == 1,
                 "and the old clean-up, remove, leaves a one-layer design alone");
    }
}
