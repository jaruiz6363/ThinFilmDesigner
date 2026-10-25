// RunCompareTests.cs — a run's design beside the one it started from, before Accept or Revert.
//
// The user (2026-10-18): the optimisation window gets Merit function, Spectrum and Layers tabs,
// open once the run has stopped, comparing before and after; Revert puts the design back and
// leaves the window open to start again; and closing the window must really stop the run, which
// LensHH-LT's dialogs once did not. The CLI (--compare) and the MCP (optimise_compare) give the
// same comparison from the same API call.

using System.Text.Json.Nodes;
using ThinFilm.Api;
using ThinFilm.Cli;
using ThinFilm.Gui.Core;
using ThinFilm.Mcp;

namespace ThinFilm.Native.Tests;

public static class RunCompareTests
{
    private static void WithLogs(Action check)
    {
        var was = Environment.GetEnvironmentVariable("THINFILM_HOME");
        var home = Path.Combine(Path.GetTempPath(), "thinfilm-run-tests", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("THINFILM_HOME", home);
        try { check(); }
        finally { Environment.SetEnvironmentVariable("THINFILM_HOME", was); }
    }

    public static void ARunIsComparedWithItsStart(Harness h)
    {
        h.Section("CompareRun and RunSpectrum: the start design beside the run's result");

        WithLogs(() =>
        {
            var api = new ThinFilmApi();
            string id = api.OpenSample("AR-visible").Value!.Id;
            var start = api.Evaluate(id).Value!;
            var spectrumRequest = new SpectrumRequest(400, 700, 31, 0.0, ["RA"]);
            var startSpectrum = api.Spectrum(id, spectrumRequest).Value!;

            var run = api.StartRun(id, new RunRequest("global", Starts: 400, Threads: 1)).Value!.Run;
            h.Expect(api.CompareRun(run) is { Ok: false } c0 && c0.Error!.Contains("still going"),
                     "refused while the run is going: its design is being changed on another thread");
            // Stopped once a few starts have finished, so the result is not the start.
            for (int i = 0; i < 600 && api.RunStatus(run).Value!.Iteration < 3; ++i) Thread.Sleep(10);
            api.StopRun(run);
            api.WaitRun(run);
            var r = api.RunStatus(run).Value!;

            var c = api.CompareRun(run);
            h.Expect(c.Ok, $"a stopped run can be compared ({c.Error})");
            var v = c.Value!;
            h.Expect(Math.Abs(v.Before.Merit - r.StartMerit) < 1e-12 && Math.Abs(v.After.Merit - r.EndMerit!.Value) < 1e-12,
                     $"before is the start RMSE and after the end RMSE ({v.Before.Merit:E4} → {v.After.Merit:E4})");
            h.Expect(v.Before.Operands.Length == start.Operands.Length && v.After.Operands.Length == start.Operands.Length
                     && v.Operands.Length == start.Operands.Length,
                     "every merit row, before and after, and the operands as written");
            h.Expect(v.Before.Operands.Zip(start.Operands).All(p => p.First.Value == p.Second.Value),
                     "before is the design as it was, row by row");
            h.Expect(v.After.Operands.Zip(v.Before.Operands).Any(p => p.First.Value != p.Second.Value),
                     "after is the run's result");

            h.Expect(v.BeforeStack.Layers.Length == start.Layers.Length
                     && v.BeforeStack.Layers.Zip(start.Layers).All(p => p.First.Thickness == p.Second.Thickness
                                                                        && p.First.Material == p.Second.Material),
                     "the before stack is the start's layers");
            h.Expect(v.BeforeStack.Layers.All(l => l.Index > 1.0) && v.BeforeStack.IncidentIndex == 1.0
                     && v.BeforeStack.SubstrateIndex > 1.4,
                     $"with each layer's index, the incident medium's and the substrate's (substrate {v.BeforeStack.SubstrateIndex:0.###})");
            h.Expect(v.AfterStack.Layers.Zip(v.BeforeStack.Layers).Any(p => p.First.Thickness != p.Second.Thickness),
                     "the after stack has the run's thicknesses");

            var s = api.RunSpectrum(run, spectrumRequest);
            h.Expect(s.Ok && s.Value!.Before.Quantity["RA"].SequenceEqual(startSpectrum.Quantity["RA"]),
                     "the before spectrum is the start design's");
            h.Expect(!s.Value!.After.Quantity["RA"].SequenceEqual(startSpectrum.Quantity["RA"]),
                     "the after spectrum the result's");
            h.Expect(api.Evaluate(id).Value!.Merit == start.Merit, "comparing changes nothing open");
            h.Expect(api.Sessions.Count == 1, "and opens no session");

            h.Expect(api.RevertRun(run).Ok && api.CompareRun(run).Ok,
                     "a reverted run can still be compared from the API (the window closes its tabs)");
            h.Expect(api.CompareRun("r99") is { Ok: false }, "an unknown run is refused");
        });
    }

    public static void ANeedleRunIsComparedLayerByLayer(Harness h)
    {
        h.Section("Comparing a run that changed the stack's shape");

        WithLogs(() =>
        {
            var api = new ThinFilmApi();
            string id = api.OpenSample("Bandpass").Value!.Id;
            var run = api.StartRun(id, new RunRequest("needle", Insertions: 2)).Value!.Run;
            api.WaitRun(run);
            var v = api.CompareRun(run).Value!;
            h.Expect(v.AfterStack.Layers.Length > v.BeforeStack.Layers.Length,
                     $"needle's result has more layers ({v.BeforeStack.Layers.Length} → {v.AfterStack.Layers.Length})");
            var lines = ThinFilmApi.CompareStackLines(v);
            h.Pin(lines.Length, Math.Max(v.BeforeStack.Layers.Length, v.AfterStack.Layers.Length) + 3,
                  "the stack lines: a header, the index line, a row per position, the totals");
            h.Expect(lines[^1].Contains($"{v.BeforeStack.Layers.Length} layers") && lines[^1].Contains($"{v.AfterStack.Layers.Length} layers"),
                     $"the totals give both counts ({lines[^1]})");
            var merit = ThinFilmApi.CompareLines(v);
            h.Pin(merit.Length, v.Operands.Length + 2, "the merit lines: a header, a row per operand, the RMSE");
            h.Expect(merit[0].Contains("before") && merit[0].Contains("after") && merit[0].Contains("delta"),
                     "with before, after and delta columns");
        });
    }

    public static void TheWindowComparesAndStartsAgainAfterRevert(Harness h)
    {
        h.Section("RunMonitor and RunCompareView: the tabs, Revert, and starting again");

        WithLogs(() =>
        {
            var w = new DesignWorkspace();
            w.OpenSample("AR-visible");
            double start = w.Merit;
            var m = new RunMonitor(w);
            h.Expect(!m.CanCompare && m.Compare() is null && !m.CanRevert, "before Start: nothing to compare or revert");
            m.Start();
            h.Expect(m.CanRevert, "Revert is offered while it runs (it stops it)");
            w.WaitRun(m.Report!.Run, 30000);
            m.Poll();
            h.Expect(m.CanCompare, "the tabs open once the run has ended");
            var view = m.Compare()!;
            h.Expect(ReferenceEquals(view, m.Compare()), "built once per run");
            h.Pin(view.Rows.Count, w.MeritRows.Count, "a row per merit row");
            var row = view.Rows[0];
            h.Expect(row.Before == w.MeritRows[0].ValueText, $"Value (Before) is the design's value ({row.Before})");
            h.Expect(row.After != row.Before && row.Delta is ['+' or '−', ..],
                     $"Value (After) the result's, and the change signed ({row.After}, {row.Delta})");
            h.Expect(view.MeritSummary.Contains(ThinFilmApi.Rms(start)) && view.MeritSummary.EndsWith("%)"),
                     $"the summary gives both RMSE ({view.MeritSummary})");

            var both = view.Spectrum(out var problem)!;
            h.Expect(problem is null && both.Count == 4 && both.Count(s => s.Dashed) == 2,
                     "Before and after: each quantity twice, the before dashed");
            view.ShowChoice = RunCompareView.AfterOnly;
            h.Expect(view.Spectrum(out _) is [{ Dashed: false }, { Dashed: false }], "After: the result only");
            view.ShowChoice = RunCompareView.BeforeOnly;
            h.Expect(view.Spectrum(out _) is [{ Dashed: true }, { Dashed: true }], "Before: the start only");
            view.AngleText = "95";
            h.Expect(view.Spectrum(out problem) is null && problem!.Contains("angle"), "an impossible angle is refused");
            view.AngleText = "45";
            var at45 = view.Spectrum(out _)!;
            h.Expect(!at45[0].Y.SequenceEqual(both[0].Y), "and the angle is the one asked for");

            // REVERT: the design as it was, the window ready for another run.
            m.Revert();
            h.Expect(w.Merit == start && m.Report!.State == "reverted", "Revert leaves the design as it was");
            h.Expect(m.CanStart && !m.CanCompare && m.Compare() is null && !m.CanAccept && !m.CanRevert,
                     "Start is offered again; the tabs close");
            h.Expect(m.StateText.StartsWith("Reverted") && m.StateText.Contains("start again"), $"and says so ({m.StateText})");
            string firstRun = m.Report.Run;
            m.IterationsText = "5";
            h.Expect(m.Start() is null && m.Report.Run != firstRun, "a second run starts in the same window");
            h.Expect(!m.Log.Contains("reverted"), "with a log of its own");
            w.WaitRun(m.Report.Run, 30000);
            m.Poll();
            h.Expect(m.CanCompare && !ReferenceEquals(m.Compare(), view), "and a comparison of its own");
            h.Expect(m.Accept() is null && Math.Abs(w.Merit - m.Report.EndMerit!.Value) < 1e-12, "which can be accepted");
        });
    }

    public static void TheLayerGraphsShareOneScale(Harness h)
    {
        h.Section("LayerGraphModel: two stacks, one scale");

        var thin = new StackProfile(550, "air", 1.0, "glass", 1.52,
            [new ProfileLayer("H", 100, 2.3), new ProfileLayer("L", 50, 1.46)]);
        var thick = new StackProfile(550, "air", 1.0, "glass", 1.52,
            [new ProfileLayer("L", 100, 1.46), new ProfileLayer("H", 100, 2.3), new ProfileLayer("M", 100, 1.7)]);
        var scale = LayerGraphModel.CommonScale(thin, thick);
        h.Expect(scale.Thickness == 300 && scale.IndexMin < 1.0 && scale.IndexMax > 2.3,
                 $"the thicker stack's total, and room around every index, media included ({scale})");
        var colours = LayerGraphModel.Colours(thin, thick);
        h.Expect(colours.Count == 3 && colours["H"] != colours["L"], "one colour per material, the same in both");

        var g = new LayerGraphModel();
        g.Update(scale, 600, 300);
        var a = g.Bars(thin, colours);
        var b = g.Bars(thick, colours);
        h.Pin(a.Count, 4, "a bar per layer, and one each for the incident medium and the substrate");
        h.Expect(a[0].Layer == -1 && a[^1].Layer == -2 && a[0].Colour == LayerGraphModel.MediumColour,
                 "the incident medium first, the substrate last, in grey");
        h.Expect(Math.Abs(a[1].Width - b[2].Width) < 1e-9 && Math.Abs(a[1].Width - 2 * a[2].Width) < 1e-9,
                 "the same thickness is the same width in both graphs, and widths go as thickness");
        h.Expect(Math.Abs(a[1].Y - b[2].Y) < 1e-9 && a[1].Y < a[2].Y && a[1].Y + a[1].Height == a[2].Y + a[2].Height,
                 "the same index is the same height in both, a higher index a taller bar, all on one baseline");
        h.Expect(a[^1].X < b[^1].X, "the substrate starts where each stack ends");
        h.Expect(a[1].Colour == b[2].Colour && a[1].Colour == colours["H"], "a material keeps its colour across both");
    }

    public static void TheCliAndTheMcpCompareTheSameWay(Harness h)
    {
        h.Section("thinfilm refine --compare and optimise_compare");

        WithLogs(() =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "thinfilm-run-tests");
            Directory.CreateDirectory(dir);
            string design = Path.Combine(dir, "ar-compare.tfd");
            File.Copy(SampleFiles.PathOf("AR-visible"), design, overwrite: true);
            var output = new StringWriter();
            var error = new StringWriter();
            int code = CommandLine.Run(["refine", design, "--compare", "--revert"], output, error);
            var lines = output.ToString().Split('\n', StringSplitOptions.TrimEntries);
            h.Pin(code, 0, $"thinfilm refine --compare exits zero ({error})");
            h.Expect(lines.Any(l => l.StartsWith("#") && l.Contains("before") && l.Contains("after") && l.Contains("delta")),
                     "it prints the merit rows before and after");
            h.Expect(lines.Any(l => l.StartsWith("RMSE before")) && lines.Any(l => l.StartsWith("total")),
                     "the RMSE, and the two stacks");
            int compared = Array.FindIndex(lines, l => l.StartsWith("RMSE before"));
            int reverted = Array.FindIndex(lines, l => l.StartsWith("reverted"));
            h.Expect(compared >= 0 && reverted > compared, "before the run is reverted");

            var tools = new Tools();
            JsonNode Call(string name, string args, bool fails = false)
            {
                var (text, failed) = tools.Call(name, JsonNode.Parse(args)!.AsObject());
                h.Expect(failed == fails, $"{name} {(fails ? "is refused" : "succeeds")} ({text[..Math.Min(120, text.Length)]})");
                return failed ? JsonValue.Create(text)! : JsonNode.Parse(text)!;
            }
            Call("load_design", SampleFiles.LoadArgs("AR-visible"));
            var run = Call("optimise_start", """{"design":"d1","iterations":300}""")["Run"]!.GetValue<string>();
            Call("optimise_status", $$"""{"run":"{{run}}","wait_seconds":30}""");
            var c = Call("optimise_compare", $$"""{"run":"{{run}}"}""");
            h.Expect(c["Before"]!["Merit"] is not null && c["After"]!["Operands"]!.AsArray().Count > 0
                     && c["AfterStack"]!["Layers"]!.AsArray().Count == 4,
                     "optimise_compare gives both evaluations and both stacks");
            var cs = Call("optimise_compare", $$"""{"run":"{{run}}","from":400,"to":700,"steps":11,"quantities":"RA"}""");
            h.Expect(cs["spectra"]!["Before"]!["Quantity"]!["RA"]!.AsArray().Count == 11
                     && cs["spectra"]!["After"]!["Quantity"]!["RA"]!.AsArray().Count == 11,
                     "and with a range, the same spectrum of both");
            Call("optimise_revert", $$"""{"run":"{{run}}"}""");
            Call("optimise_compare", """{"run":"r99"}""", fails: true);
        });
    }
}
