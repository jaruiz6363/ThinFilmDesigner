// RunTests.cs — an optimisation run: started, logged, stopped, kept or reverted.
//
// The user (2026-10-17): optimisations move to windows of their own, after LensHH-LT; "logs,
// elapsed time, start rmse, end rmse are very important to capture"; and whatever the GUI can do,
// the CLI and the MCP server must do too, by calling the same engine routines. So the run lives
// in ThinFilmApi and these checks hold it to what each front end relies on.

using System.Text.Json;
using System.Text.Json.Nodes;
using ThinFilm.Api;
using ThinFilm.Cli;
using ThinFilm.Gui.Core;
using ThinFilm.Mcp;
using ThinFilm.Optimize;

namespace ThinFilm.Native.Tests;

public static class RunTests
{
    /// <summary>Runs a check with run logs written to a folder of its own, not the person's profile.</summary>
    private static void WithLogs(Action<string> check)
    {
        var was = Environment.GetEnvironmentVariable("THINFILM_HOME");
        var home = Path.Combine(Path.GetTempPath(), "thinfilm-run-tests", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("THINFILM_HOME", home);
        try { check(Path.Combine(home, "logs")); }
        finally { Environment.SetEnvironmentVariable("THINFILM_HOME", was); }
    }

    /// <summary>
    /// The user guide's first chapter (2026-10-23): a narrow bandpass needled out of one thick
    /// SiO2 layer meets its spec, merit ~1e-15, with a 4.6 nm layer in it. 5% of that merit is
    /// nothing, so evolve could not remove the layer; a merit good enough to accept lets it.
    /// </summary>
    public static void AMetSpecStillLosesItsThinLayers(Harness h)
    {
        h.Section("a needled bandpass that meets its spec: evolve removes its thin layers under the accepted merit");
        WithLogs(_ =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "thinfilm-run-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string f = Path.Combine(dir, "bp.tfd");
            int Run(params string[] args) => CommandLine.Run(args, new StringWriter(), new StringWriter());
            double[] Layers(string file) => DesignDocument.FromJson(File.ReadAllText(file)).ToSpec().LayerThickness ?? [];
            double Merit(string file) => new ThinFilmApi().CreateFromDocument(DesignDocument.FromJson(File.ReadAllText(file))).Value!.Merit;

            // THE NEEDLED DESIGN IS A FIXTURE, recorded on Windows: needle's 40 insertions from one
            // 2000 nm layer follow a path that differs between compilers (GCC on Linux, 2026-10-24,
            // ended with no layer under 15 nm), and this test is about what evolve does next.
            using (var s = typeof(RunTests).Assembly.GetManifestResourceStream("bandpass-needled.tfd")!)
            using (var o = File.Create(f)) s.CopyTo(o);
            bool built = File.Exists(f);
            var needled = Layers(f);
            h.Expect(built && needled.Length > 30 && Merit(f) < 1e-3,
                     $"the needled bandpass meets its spec ({needled.Length} layers, merit {Merit(f):G3})");
            h.Expect(needled.Any(t => t < 15.0), $"with a layer under 15 nm in it ({needled.Min():F2} nm)");

            string off = Path.Combine(dir, "off.tfd"), on = Path.Combine(dir, "on.tfd");
            Run("evolve", f, "--growth-steps", "2", "--min-thickness", "15", "--accept-merit", "0", "-o", off);
            h.Expect(Layers(off).Any(t => t < 15.0), "with no merit accepted, the 5% rule keeps it, as before");
            var evolveOut = new StringWriter();
            CommandLine.Run(["evolve", f, "--growth-steps", "2", "--min-thickness", "15", "-o", on], evolveOut, new StringWriter());
            var cleaned = Layers(on);

            // BETWEEN SAMPLES: blocking sampled every 2.5 nm leaves a spike to 55% at 466.2 nm.
            h.Expect(evolveOut.ToString().Split('\n').Any(l => l.StartsWith("check") && l.Contains("between samples: TA") && l.Contains(" at 466.")),
                     "the run's log ends with the spike the merit's samples miss");
            var api = new ThinFilmApi();
            var spiky = api.CreateFromDocument(DesignDocument.FromJson(File.ReadAllText(on))).Value!.Id;
            var found = api.CheckBetweenSamples(spiky);
            h.Expect(found.Ok && found.Value is [{ Code: "TA", Mode: "Max", From: 450.0, To: 535.0 } w0, ..]
                     && Math.Abs(w0.Wavelength - 466.2) < 0.2 && w0.Value > 0.3 && w0.WorstSampled < 0.01,
                     $"CheckBetweenSamples finds it: {(found.Ok && found.Value!.Length > 0 ? found.Value[0].Text : found.Error ?? "none")}");
            var evalOut = new StringWriter();
            CommandLine.Run(["evaluate", on], evalOut, new StringWriter());
            h.Expect(evalOut.ToString().Contains("! between samples: TA"), "thinfilm evaluate prints it");
            var tools = new Tools();
            var (loaded, loadFailed) = tools.Call("load_design", new JsonObject { ["path"] = on });
            var (mcpText, mcpFailed) = loadFailed ? (loaded, true)
                : tools.Call("check_between_samples", new JsonObject { ["design"] = JsonNode.Parse(loaded)!["Id"]!.GetValue<string>() });
            h.Expect(!mcpFailed && JsonNode.Parse(mcpText)!.AsArray() is [{ } first, ..] && first["Code"]!.GetValue<string>() == "TA",
                     $"the MCP's check_between_samples returns it ({mcpText[..Math.Min(120, mcpText.Length)]})");

            var clean = api.CheckBetweenSamples(api.CreateFromDocument(DesignDocument.FromJson(File.ReadAllText(f))).Value!.Id);
            h.Expect(clean.Ok, $"and it runs on any design ({clean.Error})");
            h.Expect(cleaned.All(t => t >= 15.0) && Merit(on) <= 0.1,
                     $"with the default 0.1 accepted, every layer is 15 nm or more and the merit {Merit(on):G3} is under 0.1 ({cleaned.Length} layers)");

            // The Evolve window: a start under the accepted merit has no meaningful percentage change.
            var w = new DesignWorkspace();
            w.Open(File.ReadAllText(f), f);
            var m = new RunMonitor(w, "evolve") { GrowthStepsText = "0", MinimumThicknessText = "15" };
            h.Expect(m.Start() is null, "the Evolve window starts on the needled bandpass");
            w.WaitRun(m.Report!.Run, 120000);
            m.Poll();
            h.Expect(m.ChangeText == "—", $"and its Change is a dash, not a percentage of a merit of ~0 ({m.ChangeText})");
            m.AcceptMeritText = "0";
            h.Expect(m.ChangeText.EndsWith("%"), $"with no merit accepted it is a percentage again ({m.ChangeText})");
            m.Revert();

            // The Remove thin layers window the same (+2788531.7 % on the user guide's dichroic).
            var thin = new RunMonitor(w, "thin") { MinimumThicknessText = "15" };
            h.Expect(thin.Start() is null, "the Remove thin layers window starts on the needled bandpass");
            w.WaitRun(thin.Report!.Run, 120000);
            thin.Poll();
            h.Expect(thin.ChangeText == "—", $"and its Change is a dash too ({thin.ChangeText})");
            thin.Revert();
        });
    }

    /// <summary>
    /// The user guide's beam splitter (2026-10-23): a coated plate in index-matching oil at 54°,
    /// over 50 to 58°. Gradual evolution grew a layer, removed it again and came back to the same
    /// design, three times; it now stops the first time.
    /// </summary>
    public static void EvolutionStopsWhenItComesBack(Harness h)
    {
        h.Section("gradual evolution grows a thicker layer when a step comes back to the design before it");
        WithLogs(_ =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "thinfilm-run-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string f = Path.Combine(dir, "plate.tfd");
            int Run(params string[] args) => CommandLine.Run(args, new StringWriter(), new StringWriter());
            bool built = Run("new", "--materials", "TiO2,SiO2", "--substrate", "N-BK7", "--incident", "N-BK7", "--reference", "550", "--layers", "1", "-o", f) == 0
                && Run("units", f, "--maximum", "5000") == 0
                && Run("layer", "set", f, "--layer", "1", "--material", "SiO2", "--thickness", "2000", "--unit", "nm") == 0
                && Run("merit", "clear", f) == 0
                && Run("merit", "add", f, "--code", "TS", "--mode", "Max", "--target", "0.001", "--from", "430", "--to", "680", "--step", "5", "--aoi-from", "50", "--aoi-to", "58", "--aoi-step", "4") == 0
                && Run("merit", "add", f, "--code", "TP", "--mode", "Min", "--target", "0.95", "--from", "430", "--to", "680", "--step", "5", "--aoi-from", "50", "--aoi-to", "58", "--aoi-step", "4") == 0;
            var needled = new StringWriter();
            CommandLine.Run(["needle", f, "--insertions", "60", "--iterations", "50", "-o", f], needled, new StringWriter());
            // Refinement takes the outer SiO2 to 0 nm; a TiO2 needle there only thickens the TiO2
            // behind it, and was inserted again and again as a sliver of its own.
            int surface = needled.ToString().Split('\n').Count(l => l.StartsWith("insert") && l.Contains(" at 0 nm"));
            h.Expect(built && surface <= 1, $"needle does not cut the outer layer into slivers ({surface} insertions at 0 nm)");
            var output = new StringWriter();
            CommandLine.Run(["evolve", f, "--growth-steps", "12", "--min-thickness", "15", "--iterations", "50"], output, new StringWriter());
            var lines = output.ToString().Split('\n');
            // It came back at step 2 and used to stop there, at the merit of step 1. Growing the
            // next layer twice as thick gets it out, again and again, to a better design.
            double Rmse(string? line) => line is null ? double.NaN : double.Parse(
                System.Text.RegularExpressions.Regex.Match(line, @"RMSE=(\S+)").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            double first = Rmse(lines.FirstOrDefault(l => l.StartsWith("step 1 ")));
            double end = Rmse(lines.FirstOrDefault(l => l.StartsWith("end")));
            h.Expect(built && lines.Any(l => l.StartsWith("came back") && l.Contains("2 times as thick")),
                     "a step that comes back is said, and the next layer grows twice as thick");
            h.Expect(end < 0.9 * first, $"and evolve goes on past it to a better design (step 1 {first:G4}, end {end:G4})");
        });
    }

    public static void NeedleSaysAStackIsTooThin(Harness h)
    {
        h.Section("needle on a stack refined to nothing says it is too thin, rather than stopping silently");
        WithLogs(_ =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "thinfilm-run-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string f = Path.Combine(dir, "plate.tfd");
            int Run(params string[] args) => CommandLine.Run(args, new StringWriter(), new StringWriter());
            // Air in front of the plate and one QWOT of SiO2: the refinement thins it to 0 nm.
            bool built = Run("new", "--materials", "TiO2,SiO2", "--substrate", "N-BK7", "--reference", "550", "--layers", "1", "-o", f) == 0
                && Run("layer", "set", f, "--layer", "1", "--material", "SiO2", "--thickness", "1", "--unit", "QWOT") == 0
                && Run("merit", "clear", f) == 0
                && Run("merit", "add", f, "--code", "TS", "--mode", "Max", "--target", "0.001", "--from", "430", "--to", "680", "--step", "2", "--aoi-from", "54", "--aoi-to", "54") == 0
                && Run("merit", "add", f, "--code", "TP", "--mode", "Min", "--target", "0.95", "--from", "430", "--to", "680", "--step", "2", "--aoi-from", "54", "--aoi-to", "54") == 0;
            var output = new StringWriter();
            CommandLine.Run(["needle", f, "--insertions", "40", "--iterations", "50"], output, new StringWriter());
            var line = output.ToString().Split('\n').FirstOrDefault(l => l.StartsWith("stationary"))?.Trim();
            h.Expect(built && line is not null && line.Contains("too thin") && line.Contains("thick layer"),
                     $"it says the stack is too thin and to start from a thick layer ({line})");
        });
    }

    public static void ThinLayersAreRemovedWhateverTheyCost(Harness h)
    {
        h.Section("remove thin layers: every layer under the minimum goes, priced in the log (GUI, CLI and MCP)");
        WithLogs(_ =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "thinfilm-run-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string f = Path.Combine(dir, "ar.tfd");
            int Run(params string[] args) => CommandLine.Run(args, new StringWriter(), new StringWriter());
            // Four quarter waves, H L H L, with two slivers put in: 0.8 nm of SiO2 after the first H
            // and 3 nm of TiO2 after the second, so H s L H t L.
            bool built = Run("new", "--materials", "TiO2,SiO2", "--substrate", "N-BK7", "--reference", "550", "--layers", "1", "-o", f) == 0
                && Run("stack", "string", f, "--text", "1H 1L 1H 1L", "--letters", "H=TiO2,L=SiO2") == 0
                && Run("stack", "decouple", f) == 0      // the string couples like letters; thin refuses pickups
                && Run("merit", "clear", f) == 0
                && Run("merit", "add", f, "--code", "RA", "--mode", "Max", "--target", "0.005", "--from", "430", "--to", "670", "--step", "10") == 0
                && Run("layer", "insert", f, "--material", "SiO2", "--thickness", "0.8", "--unit", "nm", "--after", "1") == 0
                && Run("layer", "insert", f, "--material", "TiO2", "--thickness", "3", "--unit", "nm", "--after", "4") == 0;

            var output = new StringWriter();
            int code = CommandLine.Run(["thin", f, "--min-thickness", "5", "--iterations", "100", "-o", f], output, new StringWriter());
            var lines = output.ToString().Split('\n');
            var api = new ThinFilmApi();
            var opened = api.Load(File.ReadAllText(f));
            var shown = new StringWriter();
            CommandLine.Run(["show", f], shown, new StringWriter());
            // The layer rows: "  1  TiO2  0.3381 QWOT  19.937  yes", the physical thickness fifth.
            var thick = shown.ToString().Split('\n').Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Where(c => c.Length >= 5 && int.TryParse(c[0], out int _) && (c[1] is "TiO2" or "SiO2"))
                .Select(c => double.Parse(c[4], System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            // Each sliver sits against a layer of its own material, so removing one joins the stack
            // up and the other goes with it. Refined after it, no other layer may be thinned away:
            // without the floor at the minimum this went from six layers to one.
            h.Expect(built && code == 0 && lines.Any(l => l.StartsWith("remove 1")) && thick.Length == 4
                     && thick.All(t => t >= 5 - 1e-9),
                     $"CLI: the slivers removed, 4 layers left, all 5 nm or more ({string.Join(", ", thick.Select(t => t.ToString("0.###")))})");
            h.Expect(lines.Any(l => l.StartsWith("# settings: minimum thickness 5 nm, every thinner layer removed")),
                     "the settings line says every thinner layer is removed");

            // RAISED INSTEAD (the user, 2026-10-24: "Fixed layers"): the same two slivers stay, at
            // 5 nm and fixed, and nothing else moves under 5 nm.
            string g = Path.Combine(dir, "raised.tfd");
            File.Copy(Path.Combine(dir, "ar.tfd"), g);
            var raisedOut = new StringWriter();
            bool rebuilt = Run("new", "--materials", "TiO2,SiO2", "--substrate", "N-BK7", "--reference", "550", "--layers", "1", "-o", g) == 0
                && Run("stack", "string", g, "--text", "1H 1L 1H 1L", "--letters", "H=TiO2,L=SiO2") == 0
                && Run("stack", "decouple", g) == 0
                && Run("merit", "clear", g) == 0
                && Run("merit", "add", g, "--code", "RA", "--mode", "Max", "--target", "0.005", "--from", "430", "--to", "670", "--step", "10") == 0
                && Run("layer", "insert", g, "--material", "SiO2", "--thickness", "0.8", "--unit", "nm", "--after", "1") == 0
                && Run("layer", "insert", g, "--material", "TiO2", "--thickness", "3", "--unit", "nm", "--after", "4") == 0;
            int raisedCode = CommandLine.Run(["thin", g, "--min-thickness", "5", "--raise", "--iterations", "100", "-o", g], raisedOut, new StringWriter());
            var raisedShown = new StringWriter();
            CommandLine.Run(["show", g], raisedShown, new StringWriter());
            var raisedRows = raisedShown.ToString().Split('\n').Select(l => l.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Where(c => c.Length >= 6 && int.TryParse(c[0], out int _) && (c[1] is "TiO2" or "SiO2")).ToArray();
            var fixedRows = raisedRows.Where(c => c[5] == "no").ToArray();
            h.Expect(rebuilt && raisedCode == 0 && raisedRows.Length == 6
                     && fixedRows.Length >= 2 && fixedRows.All(c => Math.Abs(double.Parse(c[4], System.Globalization.CultureInfo.InvariantCulture) - 5) < 1e-9)
                     && raisedRows.All(c => double.Parse(c[4], System.Globalization.CultureInfo.InvariantCulture) >= 5 - 1e-9)
                     && raisedOut.ToString().Contains("raise 1"),
                     $"CLI --raise: 6 layers kept, the slivers at 5 nm and fixed, none under 5 ({string.Join(", ", raisedRows.Select(c => c[4] + (c[5] == "no" ? " fixed" : "")))})");

            // Nothing to remove is said, not a failure.
            var again = new StringWriter();
            CommandLine.Run(["thin", f, "--min-thickness", "5"], again, new StringWriter());
            h.Expect(again.ToString().Contains("no layer is thinner than 5 nm"), "a design with no thin layer says so");

            // The same run through the API's runner, which the GUI window uses, and through the MCP.
            if (opened.Ok)
            {
                var bad = api.StartRun(opened.Value!.Id, new RunRequest("thin", MinimumThickness: 0));
                h.Expect(!bad.Ok && bad.Error!.Contains("minimum thickness"), $"a minimum of 0 is refused ({bad.Error})");
            }
            // The GUI's window: only the minimum thickness to set, and the run's outcome in its state.
            var w = new DesignWorkspace();
            w.Open(File.ReadAllText(f), f);
            var m = new RunMonitor(w, "thin") { MinimumThicknessText = "70" };
            h.Expect(m.Title == "Remove thin layers" && m.ShowsMinimumThickness && !m.ShowsEvolution && !m.ShowsInsertions
                     && m.StepLabel == "Removal",
                     "the Remove thin layers window shows the minimum thickness and nothing of evolve's");
            h.Expect(m.Start() is null, "it starts");
            w.WaitRun(m.Report!.Run, 60000);
            m.Poll();
            h.Expect(m.StateText.StartsWith("Finished:") && m.StateText.Contains("removed") && m.Log.Contains("remove 1"),
                     $"and finishes, saying what it removed ({m.StateText})");
            m.Revert();
            var raiseWindow = new RunMonitor(w, "thin") { MinimumThicknessText = "70", Raise = true };
            h.Expect(raiseWindow.ShowsThinAction && raiseWindow.ToRequest(out string? _)?.Raise == true, "the window's Raise choice reaches the request");
            h.Expect(raiseWindow.Start() is null, "and runs");
            w.WaitRun(raiseWindow.Report!.Run, 60000);
            raiseWindow.Poll();
            h.Expect(raiseWindow.StateText.Contains("raised to 70 nm and fixed") && raiseWindow.Log.Contains("raise 1"),
                     $"raising, it says so ({raiseWindow.StateText})");
            h.Expect(raiseWindow.StepLabel == "Raised" && !raiseWindow.StepText.Contains(" of "),
                     $"and counts the layers raised, not 'of' the ones first found thin ({raiseWindow.StepLabel} {raiseWindow.StepText})");
            raiseWindow.Revert();

            var tools = new Tools();
            var (loaded, _) = tools.Call("load_design", new JsonObject { ["path"] = f });
            string id = JsonNode.Parse(loaded)!["Id"]!.GetValue<string>();
            var (started, failed) = tools.Call("optimise_start", new JsonObject { ["design"] = id, ["method"] = "thin", ["min_thickness"] = 70.0 });
            string? run = failed ? null : JsonNode.Parse(started)!["Run"]!.GetValue<string>();
            var (status, _) = run is null ? ("{}", true) : tools.Call("optimise_status", new JsonObject { ["run"] = run, ["wait_seconds"] = 30 });
            var log = JsonNode.Parse(status)!["Log"]?.AsArray().Select(n => n!.GetValue<string>()).ToArray() ?? [];
            h.Expect(!failed && log.Any(l => l.StartsWith("remove 1")) && log.Any(l => l.StartsWith("end")),
                     $"MCP: optimise_start with method thin runs and logs its removals ({(failed ? started : log.LastOrDefault())})");
            if (run is not null) tools.Call("optimise_revert", new JsonObject { ["run"] = run });
            var (raiseStarted, raiseFailed) = tools.Call("optimise_start",
                new JsonObject { ["design"] = id, ["method"] = "thin", ["min_thickness"] = 70.0, ["raise"] = true });
            string? raiseRun = raiseFailed ? null : JsonNode.Parse(raiseStarted)!["Run"]!.GetValue<string>();
            var (raiseStatus, _) = raiseRun is null ? ("{}", true) : tools.Call("optimise_status", new JsonObject { ["run"] = raiseRun, ["wait_seconds"] = 30 });
            var raiseLog = JsonNode.Parse(raiseStatus)!["Log"]?.AsArray().Select(n => n!.GetValue<string>()).ToArray() ?? [];
            h.Expect(!raiseFailed && raiseLog.Any(l => l.StartsWith("raise 1")),
                     $"MCP: raise true raises instead ({(raiseFailed ? raiseStarted : raiseLog.LastOrDefault())})");
        });
    }

    public static void NeedleInsertsOnlyTheMaterialsAllowed(Harness h)
    {
        h.Section("needle's materials: a metal left out is never inserted (GUI, CLI and MCP)");
        WithLogs(_ =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "thinfilm-run-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string f = Path.Combine(dir, "itf.tfd");
            int Run(params string[] args) => CommandLine.Run(args, new StringWriter(), new StringWriter());
            // An induced-transmission filter: two silver layers between TiO2/SiO2 matching stacks.
            bool built = Run("new", "--materials", "TiO2,SiO2,Ag", "--substrate", "N-BK7", "--reference", "550", "--layers", "1", "-o", f) == 0
                && Run("stack", "string", f, "--text", "1H 1L 1H 1A 2H 1A 1H 1L 1H", "--letters", "H=TiO2,L=SiO2,A=Ag", "--no-pickups") == 0
                && Run("layer", "set", f, "--layer", "4", "--thickness", "35", "--unit", "nm") == 0
                && Run("layer", "set", f, "--layer", "6", "--thickness", "35", "--unit", "nm") == 0
                && Run("merit", "clear", f) == 0
                && Run("merit", "add", f, "--code", "TA", "--mode", "Max", "--target", "0.01", "--from", "400", "--to", "480", "--step", "10") == 0
                && Run("merit", "add", f, "--code", "TA", "--mode", "Min", "--target", "0.7", "--from", "530", "--to", "570", "--step", "5") == 0
                && Run("merit", "add", f, "--code", "TA", "--mode", "Max", "--target", "0.01", "--from", "640", "--to", "1000", "--step", "20") == 0;
            int Silver(string path)
            {
                var api = new ThinFilmApi();
                var doc = api.Document(api.Load(File.ReadAllText(path)).Value!.Id).Value!;
                int ag = Array.FindIndex(doc.Materials, m => m.Name == "Ag");
                return doc.Layers.Count(l => l.Material == ag);
            }

            string g = Path.Combine(dir, "needled.tfd");
            var output = new StringWriter();
            int code = CommandLine.Run(["needle", f, "--insertions", "8", "--iterations", "100", "--insert", "TiO2, SiO2", "-o", g],
                                       output, new StringWriter());
            var lines = output.ToString().Split('\n').Select(l => l.Trim()).ToArray();
            h.Expect(built && code == 0 && lines.Any(l => l.StartsWith("insert 1")) && Silver(g) == 2
                     && !lines.Any(l => l.StartsWith("insert") && l.Contains("Ag at")),
                     $"CLI --insert TiO2,SiO2: needles go in, none of silver ({Silver(g)} silver layers)");
            h.Expect(lines.Any(l => l.Contains("inserting TiO2 and SiO2 only")), "the settings line names the materials");
            var refused = new StringWriter();
            h.Expect(CommandLine.Run(["needle", f, "--insert", "Au"], new StringWriter(), refused) != 0
                     && refused.ToString().Contains("not one of the design's materials"),
                     $"a material the design does not have is refused ({refused.ToString().Trim()})");

            // The GUI's window: a box per material, all ticked; unticking one names the rest.
            var w = new DesignWorkspace();
            w.Open(File.ReadAllText(f), f);
            var m = new RunMonitor(w, "needle");
            h.Expect(m.MaterialNames.SequenceEqual(["TiO2", "SiO2", "Ag"]) && m.ToRequest(out string? _)?.NeedleMaterials is null,
                     "the needle window offers every material, all ticked, which asks for no limit");
            m.Inserted.Remove("Ag");
            h.Expect(m.ToRequest(out string? _)?.NeedleMaterials is ["TiO2", "SiO2"], "unticking silver leaves TiO2 and SiO2");
            m.Inserted.Clear();
            h.Expect(m.ToRequest(out string? none) is null && none!.Contains("at least one material"), $"and none ticked is refused ({none})");
            var e = new RunMonitor(w, "evolve");
            e.Inserted.Remove("Ag");
            h.Expect(e.ToRequest(out string? _)?.NeedleMaterials is ["TiO2", "SiO2"], "evolve's window has the same choice");

            var tools = new Tools();
            var (loaded, _) = tools.Call("load_design", new JsonObject { ["path"] = f });
            string id = JsonNode.Parse(loaded)!["Id"]!.GetValue<string>();
            var (started, failed) = tools.Call("optimise_start",
                new JsonObject { ["design"] = id, ["method"] = "needle", ["insertions"] = 4, ["insert"] = "TiO2,SiO2" });
            string? run = failed ? null : JsonNode.Parse(started)!["Run"]!.GetValue<string>();
            var (status, _) = run is null ? ("{}", true) : tools.Call("optimise_status", new JsonObject { ["run"] = run, ["wait_seconds"] = 30 });
            var log = JsonNode.Parse(status)!["Log"]?.AsArray().Select(n => n!.GetValue<string>()).ToArray() ?? [];
            h.Expect(!failed && log.Any(l => l.Contains("inserting TiO2 and SiO2 only")) && !log.Any(l => l.Contains("Ag at")),
                     $"MCP: insert TiO2,SiO2 reaches the run ({(failed ? started : log.FirstOrDefault(l => l.StartsWith("# settings")))})");
            if (run is not null) tools.Call("optimise_revert", new JsonObject { ["run"] = run });
        });
    }

    public static void EvolveGrowsThickerWhenItComesBack(Harness h)
    {
        h.Section("evolve: a step that comes back to an earlier design grows the next layer thicker");
        WithLogs(_ =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "thinfilm-run-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string f = Path.Combine(dir, "ar.tfd");
            int Run(params string[] args) => CommandLine.Run(args, new StringWriter(), new StringWriter());
            // The guide's wide-angle AR: one quarter wave of MgF2, R under 0.5% from 420 to 680 nm
            // at 0, 15, 30 and 45 deg. Growing 1 QWOT of SiO2 at the substrate refined back to the
            // 2-layer design of step 0, and evolve used to stop there at R 2.7%.
            bool built = Run("new", "--materials", "SiO2,TiO2,MgF2", "--substrate", "N-BK7", "--reference", "550", "--layers", "1", "-o", f) == 0
                && Run("layer", "set", f, "--layer", "1", "--material", "MgF2", "--thickness", "1", "--unit", "QWOT") == 0
                && Run("merit", "clear", f) == 0
                && Run("merit", "add", f, "--code", "RA", "--mode", "Max", "--target", "0.005", "--from", "420", "--to", "680",
                       "--step", "10", "--aoi-from", "0", "--aoi-to", "45", "--aoi-step", "15") == 0;
            var output = new StringWriter();
            int code = CommandLine.Run(["evolve", f, "--insert", "TiO2,SiO2", "--grow-at", "substrate", "--accept-merit", "0",
                                        "--growth-steps", "6", "-o", f], output, new StringWriter());
            var lines = output.ToString().Split('\n').Select(l => l.Trim()).ToArray();
            var api = new ThinFilmApi();
            var d = api.Describe(api.Load(File.ReadAllText(f)).Value!.Id).Value!;
            h.Expect(built && code == 0 && lines.Any(l => l.StartsWith("came back") && l.Contains("2 times as thick"))
                     && lines.Any(l => l.StartsWith("grow") && l.Contains("188.")) && d.Layers > 2 && d.Merit < 1e-3,
                     $"it comes back once, grows 2 QWOT, and gets out ({d.Layers} layers, merit {d.Merit:G3})");
        });
    }

    public static void ShapeChangesKeepFixedLayersAndLeftOutMaterials(Harness h)
    {
        h.Section("needle and evolve: a fixed layer stays fixed, and a material left out is never split into the stack");
        WithLogs(_ =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "thinfilm-run-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            int Run(params string[] args) => CommandLine.Run(args, new StringWriter(), new StringWriter());
            // An AR from one quarter wave of MgF2, R under 0.5% from 420 to 680 nm at normal incidence.
            string start = Path.Combine(dir, "start.tfd");
            bool built = Run("new", "--materials", "SiO2,TiO2,MgF2", "--substrate", "N-BK7", "--reference", "550", "--layers", "1", "-o", start) == 0
                && Run("layer", "set", start, "--layer", "1", "--material", "MgF2", "--thickness", "1", "--unit", "QWOT") == 0
                && Run("merit", "clear", start) == 0
                && Run("merit", "add", start, "--code", "RA", "--mode", "Max", "--target", "0.005", "--from", "420", "--to", "680", "--step", "5") == 0;
            (string Material, double Nm, bool Varied)[] Layers(string path)
            {
                var api = new ThinFilmApi();
                var doc = api.Document(api.Load(File.ReadAllText(path)).Value!.Id).Value!;
                var spec = doc.ToSpec();
                return doc.Layers.Select((l, i) => (doc.Materials[l.Material].Name, spec.LayerThickness![i], l.Variable)).ToArray();
            }

            // MgF2 varied, but not inserted: a TiO2 needle on its outer face used to leave an empty
            // MgF2 in front, which grew, and MgF2 ended up inside the stack.
            string free = Path.Combine(dir, "free.tfd");
            File.Copy(start, free);
            Run("evolve", free, "--insert", "TiO2,SiO2", "--grow-at", "substrate", "--accept-merit", "0", "-o", free);
            var f = Layers(free);
            h.Expect(built && f.Length > 2 && f[0].Material == "MgF2" && f.Skip(1).All(l => l.Material != "MgF2"),
                     $"MgF2 left out stays only where it was, outside ({string.Join(" ", f.Select(l => l.Material))})");

            // MgF2 fixed: never split, never varied, still fixed in the design that comes out.
            string held = Path.Combine(dir, "held.tfd");
            File.Copy(start, held);
            Run("layer", "insert", held, "--material", "TiO2", "--thickness", "0.5", "--unit", "QWOT", "--after", "1");
            Run("layer", "set", held, "--layer", "1", "--vary", "no");
            double before = Layers(held)[0].Nm;
            Run("evolve", held, "--grow-at", "substrate", "--accept-merit", "0", "-o", held);
            var g = Layers(held);
            var kept = g.Where(l => !l.Varied).ToArray();
            h.Expect(g.Length > 2 && kept.Length == 1 && kept[0].Material == "MgF2" && Math.Abs(kept[0].Nm - before) < 1e-9,
                     $"a fixed MgF2 layer keeps its {before:0.###} nm and stays fixed, the only fixed layer "
                     + $"({string.Join(" ", g.Select(l => $"{l.Material} {l.Nm:0.#}{(l.Varied ? "" : " fixed")}"))})");
        });
    }

    public static void ARunIsWrittenBackLikeAnEdit(Harness h)
    {
        h.Section("thinfilm refine, needle, evolve, thin and optimise write the result back; --revert leaves the file");
        WithLogs(_ =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "thinfilm-run-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string f = Path.Combine(dir, "ar.tfd");
            File.Copy(SampleFiles.PathOf("AR-visible"), f);
            string before = File.ReadAllText(f);

            // The user, 2026-10-24: "Save by default". A run without -o said "kept" and saved
            // nothing, and the CLI examples lost every run that way.
            var tried = new StringWriter();
            CommandLine.Run(["refine", f, "--revert"], tried, new StringWriter());
            h.Expect(File.ReadAllText(f) == before && tried.ToString().Contains("left as it was"),
                     "--revert leaves the design file as it was, and says so");

            var kept = new StringWriter();
            int code = CommandLine.Run(["refine", f], kept, new StringWriter());
            double merit = new ThinFilmApi().CreateFromDocument(DesignDocument.FromJson(File.ReadAllText(f))).Value!.Merit;
            double start = new ThinFilmApi().CreateFromDocument(DesignDocument.FromJson(before)).Value!.Merit;
            h.Expect(code == 0 && merit < start && kept.ToString().Contains($"wrote {f}"),
                     $"without -o the refined design is written back ({start:G4} -> {merit:G4})");

            string g = Path.Combine(dir, "other.tfd");
            string now = File.ReadAllText(f);
            CommandLine.Run(["optimise", f, "--stages", "refine", "-o", g], new StringWriter(), new StringWriter());
            h.Expect(File.Exists(g) && File.ReadAllText(f) == now, "-o writes elsewhere and leaves the design file");
            CommandLine.Run(["optimise", f, "--stages", "refine"], new StringWriter(), new StringWriter());
            h.Expect(File.ReadAllText(f) != now, "and optimise without -o writes back too");
        });
    }

    public static void ARunWorksOnACopyAndIsKeptWithItsLog(Harness h)
    {
        h.Section("a refine run: on a copy, logged start to end, kept into the design and its history");

        WithLogs(logs =>
        {
            var api = new ThinFilmApi();
            string id = api.OpenSample("AR-visible").Value!.Id;
            double before = api.Describe(id).Value!.Merit;
            var thick = api.Document(id).Value!.Layers.Select(l => l.Thickness).ToArray();

            var started = api.StartRun(id, new RunRequest("refine"));
            h.Expect(started.Ok, $"refine starts ({started.Error})");
            if (!started.Ok) return;
            h.Expect(Math.Abs(started.Value!.StartMerit - before) < 1e-12,
                     $"its start RMSE is the design's merit ({started.Value.StartMerit:E6} against {before:E6})");

            var r = api.WaitRun(started.Value.Run).Value!;
            h.Expect(r.State == "finished" && r.EndMerit < r.StartMerit,
                     $"it finishes lower ({r.State}, {r.StartMerit:E3} → {r.EndMerit:E3})");
            h.Expect(r.ElapsedSeconds > 0.0 && r.Iteration > 0, $"with its elapsed time and iterations ({r.ElapsedSeconds:F4} s, {r.Iteration})");
            h.Expect(api.Describe(id).Value!.Merit == before
                     && api.Document(id).Value!.Layers.Select(l => l.Thickness).SequenceEqual(thick),
                     "the design itself is untouched until the run is kept");

            var log = api.RunStatus(r.Run).Value!.Log;
            h.Expect(log[0].StartsWith("# ThinFilm refine") && log.Any(l => l.StartsWith("# settings: norm L2, max iterations 300")),
                     "the log opens with the method, the time and the settings");
            h.Expect(log.Any(l => l.StartsWith("start") && l.Contains($"RMSE={ThinFilmApi.Rms(before)}")), "then the start RMSE");
            h.Expect(log.Count(l => l.StartsWith("iter")) == r.Iteration, $"one line per iteration ({r.Iteration})");
            h.Expect(log[^1].StartsWith("end") && log[^1].Contains(ThinFilmApi.Rms(r.EndMerit!.Value)) && log[^1].Contains(" s"),
                     $"and ends with the end RMSE and the time ({log[^1]})");
            var since = api.RunStatus(r.Run, log.Length - 2).Value!;
            h.Expect(since.Log.Length == 2 && since.LogLines == log.Length, "a caller can ask for only the lines it has not read");

            var kept = api.KeepRun(r.Run);
            h.Expect(kept.Ok && Math.Abs(kept.Value!.Merit - r.EndMerit!.Value) < 1e-12,
                     $"keep makes the run's design the design's ({kept.Value?.Merit:E6})");
            var history = api.Document(id).Value!.History;
            h.Expect(history is { Length: 1 } && history[0].Result == "kept" && history[0].StartMerit == r.StartMerit
                     && history[0].EndMerit == r.EndMerit && history[0].Settings == "norm L2, max iterations 300",
                     "the run goes in the design's history, with its merits and settings");
            var reopened = api.Load(api.Save(id).Value!).Value!.Id;
            h.Expect(api.Document(reopened).Value!.History is { Length: 1 }, "and the history is saved with the design");
            h.Expect(!api.KeepRun(r.Run).Ok && !api.RevertRun(r.Run).Ok, "a kept run cannot be kept or reverted again");

            var file = api.RunStatus(r.Run).Value!.LogFile;
            h.Expect(file is not null && file.StartsWith(logs) && File.Exists(file), $"the log is written to a file ({file})");
            if (file is not null && File.Exists(file))
            {
                var lines = File.ReadAllLines(file);
                h.Expect(lines.SequenceEqual(api.RunStatus(r.Run).Value!.Log), "line for line the log the run reports");
                h.Expect(lines[^1].StartsWith("kept"), "ending with what became of it");
            }
        });
    }

    public static void ARunCanBeRevertedStoppedOrRefused(Harness h)
    {
        h.Section("revert leaves the design as it was; Stop keeps the best so far; a changed design refuses Keep");

        WithLogs(_ =>
        {
            var api = new ThinFilmApi();
            string id = api.OpenSample("AR-visible").Value!.Id;
            double before = api.Describe(id).Value!.Merit;

            var r = api.StartRun(id, new RunRequest("refine", "minimax")).Value!;
            api.WaitRun(r.Run);
            var log = api.RunStatus(r.Run).Value!.Log;
            h.Expect(log.Count(l => l.StartsWith("pass")) == 6, "minimax logs each of its six reweighting passes");
            var reverted = api.RevertRun(r.Run);
            h.Expect(reverted.Ok && reverted.Value!.Merit == before, "revert leaves the merit where it was");
            h.Expect(api.Document(id).Value!.History is [{ Result: "reverted" }], "and the run goes in the history as reverted");

            // A DESIGN CHANGED UNDER A RUN: its result was found for a design that is gone.
            var second = api.StartRun(id, new RunRequest("refine")).Value!;
            api.WaitRun(second.Run);
            api.SetThickness(id, 0, 50.0);
            var refused = api.KeepRun(second.Run);
            h.Expect(!refused.Ok && refused.Error!.Contains("changed since"), $"keep is refused ({refused.Error})");

            // REFUSED BEFORE IT STARTS.
            h.Expect(api.StartRun(id, new RunRequest("refine", "L7")).Error!.Contains("not a norm"), "an unknown norm is refused");
            h.Expect(api.StartRun(id, new RunRequest("refine", MaxIterations: 0)).Error!.Contains("iterations"), "no iterations is refused");
            h.Expect(api.StartRun(id, new RunRequest("sideways")).Error!.Contains("not a method"), "an unknown method is refused");
            h.Expect(api.StartRun(id, new RunRequest("global", Starts: 0)).Error!.Contains("starts"), "global with no starts is refused");
            h.Expect(api.StartRun(id, new RunRequest("needle", Insertions: 0)).Error!.Contains("insertions"), "needle with no insertions is refused");
            h.Expect(api.StartRun(id, new RunRequest("evolve", MinimumThickness: 0)).Error!.Contains("minimum thickness"),
                     "evolve with no minimum thickness is refused");
            h.Expect(!api.RunStatus("r99").Ok, "an unknown run is named as unknown");
        });

        // STOP, at the engine: a run stopped after its second step ends there, at its best point.
        var engine = new ThinFilmApi();
        string eid = engine.OpenSample("AR-visible").Value!.Id;
        var spec = engine.Document(eid).Value!.ToSpec();
        var design = new Design(spec);
        design.Plan();
        using var stop = new CancellationTokenSource();
        var merits = new List<double>();
        var options = new DlsOptions
        {
            Cancellation = stop.Token,
            Progress = p => { merits.Add(p.Merit); if (p.Iteration == 2) stop.Cancel(); },
        };
        var result = new DampedLeastSquares(design.VariableCount, design.Report.n_residuals, options).Minimise(design, options);
        h.Expect(result.Outcome == DlsOutcome.Cancelled && merits.Count == 2, $"stopped after two steps ({result.Outcome}, {merits.Count})");
        h.Expect(Math.Abs(result.FinalMerit - merits[^1]) < 1e-15, "at the best point it had reached");

        // And through the API: whether Stop lands before the end or not, the run ends keepable.
        WithLogs(_ =>
        {
            var api = new ThinFilmApi();
            // The largest sample, so Stop has a chance to land before the run ends by itself.
            string id = api.OpenSample("Chirped-mirror").Value!.Id;
            var r = api.StartRun(id, new RunRequest("refine", MaxIterations: 2000)).Value!;
            api.StopRun(r.Run);
            var ended = api.WaitRun(r.Run).Value!;
            h.Expect(ended.State is "stopped" or "finished", $"a stopped run ends ({ended.State}, {ended.Outcome})");
            h.Expect(ended.State != "stopped" || (ended.Outcome!.StartsWith("stopped: Cancelled") && api.RunStatus(r.Run).Value!.Log[^1].StartsWith("stopped")),
                     "a run that heard Stop says so in its last line");
            h.Expect(api.KeepRun(r.Run).Ok, "and what it reached can be kept");
        });
    }

    public static void GlobalNeedleAndEvolveRunTheSameWay(Harness h)
    {
        h.Section("global, needle and evolve: the same runner, each logging its own steps");

        WithLogs(_ =>
        {
            var api = new ThinFilmApi();
            string id = api.OpenSample("AR-visible").Value!.Id;
            double before = api.Describe(id).Value!.Merit;
            int layers = api.Describe(id).Value!.Layers;

            // GLOBAL: every start counted, each new best logged.
            var g = api.WaitRun(api.StartRun(id, new RunRequest("global", Starts: 5)).Value!.Run).Value!;
            var log = api.RunStatus(g.Run).Value!.Log;
            h.Expect(g.State == "finished" && g.Iteration == 5 && g.Steps == 5, $"global runs its five starts ({g.Iteration} of {g.Steps})");
            h.Expect(log.Any(l => l.StartsWith("# settings: random starts 5, range 0 to 2x each thickness, seed ") && l.EndsWith("max iterations per start 300")), "its settings are logged");
            h.Expect(log.Count(l => l.StartsWith("trial ")) == 5 && log.Count(l => l.Contains("<- best")) >= 1,
                     "every start is logged with what it reached, the new bests marked");
            h.Expect(g.EndMerit < before && log[^1].Contains("over 5 starts"), $"it ends lower, saying how ({log[^1]})");
            // Reverted, so needle below starts from a design it can still add to.
            h.Expect(api.RevertRun(g.Run).Ok, "and is reverted");

            // NEEDLE: the layer count grows, and Keep rebuilds the stack.
            var n = api.WaitRun(api.StartRun(id, new RunRequest("needle", Insertions: 2)).Value!.Run).Value!;
            log = api.RunStatus(n.Run).Value!.Log;
            h.Expect(log.Any(l => l.StartsWith("insert 1") && l.Contains(" at ") && l.Contains(" nm")),
                     "needle logs each insertion: material and depth");
            h.Expect(n.Layers > layers, $"and the layers it gave ({layers} → {n.Layers})");
            h.Expect(api.Describe(id).Value!.Layers == layers, "the design keeps its stack until Keep");
            var kept = api.KeepRun(n.Run).Value!;
            h.Expect(kept.Layers == n.Layers && Math.Abs(kept.Merit - n.EndMerit!.Value) < 1e-12,
                     $"Keep takes the new stack ({kept.Layers} layers)");

            // EVOLVE with no growth: its removal allowed any cost, one removal; the stack shrinks.
            var e = api.WaitRun(api.StartRun(id, new RunRequest("evolve", MinimumThickness: 1000,
                                                                  AllowedDegradation: 1e12, MaxRemovals: 1, GrowthSteps: 0)).Value!.Run).Value!;
            log = api.RunStatus(e.Run).Value!.Log;
            h.Expect(log.Any(l => l.StartsWith("remove 0.1") && l.Contains("layer ")), "evolve logs the removal: layer and thickness");
            h.Expect(e.Layers < kept.Layers && api.KeepRun(e.Run).Value!.Layers == e.Layers,
                     $"and Keep takes the smaller stack ({kept.Layers} → {e.Layers})");
            var none = api.WaitRun(api.StartRun(id, new RunRequest("evolve", MinimumThickness: 1e-6, GrowthSteps: 1)).Value!.Run).Value!;
            var noneLog = api.RunStatus(none.Run).Value!.Log;
            h.Expect(!noneLog.Any(l => l.StartsWith("remove")) && noneLog.Any(l => l.StartsWith("step 0")) && noneLog.Any(l => l.StartsWith("done")),
                     "with nothing under the minimum it removes nothing, and logs each step and why it ended");
            api.RevertRun(none.Run);
            h.Expect(api.Document(id).Value!.History is { Length: 4 } hist && hist.Select(r => r.Method).SequenceEqual(["global", "needle", "evolve", "evolve"]),
                     "all four runs are in the history");

            // PICKUPS: needle would renumber the layers they name.
            api.CoupleLayers(id);
            h.Expect(api.StartRun(id, new RunRequest("needle")).Error!.Contains("pickups"), "needle is refused while layers have pickups");
        });

        // BASIN HOPPING, the user's case (2026-10-17): on Bandpass no random start comes near the
        // refined starting design, so random search ends where it began; hopping around the best
        // goes lower.
        WithLogs(_ =>
        {
            var api = new ThinFilmApi();
            string id = api.OpenSample("Bandpass").Value!.Id;
            double refined = api.WaitRun(api.StartRun(id, new RunRequest("refine")).Value!.Run).Value!.EndMerit!.Value;
            var hop = api.WaitRun(api.StartRun(id, new RunRequest("global", Starts: 30, GlobalMode: "hop", Perturbation: 0.2, Seed: 1, Threads: 1)).Value!.Run).Value!;
            var hopLog = api.RunStatus(hop.Run).Value!.Log;
            h.Expect(hopLog.Any(l => l.StartsWith("# settings: basin hopping, hops 30, perturbation 20%"))
                     && hopLog.Count(l => l.StartsWith("hop ")) == 30, "basin hopping logs its settings and every hop");
            h.Expect(hop.EndMerit < 0.8 * refined,
                     $"and on Bandpass it goes well below the refined starting design ({refined:G4} → {hop.EndMerit:G4})");
            // Small hops stay in the basin they start from, which random starts would not.
            var small = api.WaitRun(api.StartRun(id, new RunRequest("global", Starts: 10, GlobalMode: "hop", Perturbation: 0.01, Seed: 1, Threads: 1)).Value!.Run).Value!;
            h.Expect(api.RunStatus(small.Run).Value!.Log.Where(l => l.StartsWith("hop ")).All(l => Math.Abs(double.Parse(l.Split("RMSE=")[1].Split(" ")[0], System.Globalization.CultureInfo.InvariantCulture) - refined) < 0.05 * refined),
                     "hops of 1% all come back to the starting basin");

            // RANDOM STARTS (2026-10-17): drawn from 0 to 2x each thickness, not the whole 0-400 nm box,
            // where no start came near the refined starting design; and a new seed each run.
            double Random(double range, int seed) => api.WaitRun(api.StartRun(id, new RunRequest(
                "global", Starts: 40, RandomRange: range, Seed: seed)).Value!.Run).Value!.EndMerit!.Value;
            double near = Random(2, 1), whole = Random(100, 1);
            h.Expect(near < 0.8 * whole, $"0 to 2x each thickness does far better than the whole box ({whole:G4} → {near:G4})");
            h.Expect(Random(2, 1) == near, "the same seed repeats a run exactly");
            var s1 = api.WaitRun(api.StartRun(id, new RunRequest("global", Starts: 1)).Value!.Run).Value!;
            var s2 = api.WaitRun(api.StartRun(id, new RunRequest("global", Starts: 1)).Value!.Run).Value!;
            h.Expect(s1.Settings.Contains("seed ") && s1.Settings != s2.Settings,
                     $"with no seed given each run draws its own, and says which ({s1.Settings} / {s2.Settings})");

            // ACCEPT WORSE: the walk can move to a hop near the best, and the log says when it does.
            string WalkLog(double acceptWorse) => string.Join("\n", api.RunStatus(api.WaitRun(api.StartRun(id, new RunRequest(
                "global", Starts: 40, GlobalMode: "hop", Perturbation: 0.4, AcceptWorse: acceptWorse, Seed: 3, Threads: 1)).Value!.Run).Value!.Run).Value!.Log);
            h.Expect(WalkLog(0.05).Contains("(walk moves here)") && !WalkLog(0).Contains("(walk moves here)"),
                     "accepting 5% worse moves the walk off the best; accepting nothing never does");

            // IN PARALLEL (the user, 2026-10-17): each thread refines its own copy of the design.
            (RunReport Report, string[] Rmse) Threaded(string mode, int threads)
            {
                var r = api.WaitRun(api.StartRun(id, new RunRequest("global", Starts: 32, GlobalMode: mode,
                                                                    Perturbation: 0.4, Seed: 5, Threads: threads)).Value!.Run).Value!;
                var lines = api.RunStatus(r.Run).Value!.Log.Where(l => l.StartsWith("trial ") || l.StartsWith("hop ")).ToArray();
                return (r, lines.Select(l => l.Split("RMSE=")[1].Split(' ')[0]).ToArray());
            }
            var one = Threaded("random", 1);
            var many = Threaded("random", 4);
            h.Expect(one.Rmse.SequenceEqual(many.Rmse) && one.Report.EndMerit == many.Report.EndMerit,
                     "random starts: four threads give the same starts, results and log as one");
            h.Expect(many.Report.Settings.Contains("threads 4"), "the thread count is in the settings");
            h.Expect(many.Report.ElapsedSeconds < 0.67 * one.Report.ElapsedSeconds,
                     $"and are faster ({one.Report.ElapsedSeconds:F2} s → {many.Report.ElapsedSeconds:F2} s)");
            var hops = Threaded("hop", 4);
            var hopLines = api.RunStatus(hops.Report.Run).Value!.Log.Where(l => l.StartsWith("hop ")).ToArray();
            h.Expect(hopLines.Length == 32 && hopLines.Select((l, i) => l.StartsWith($"hop {i + 1}/32")).All(x => x),
                     "basin hopping on four threads logs every hop, in order");
            h.Expect(hops.Report.ElapsedSeconds < 0.67 * Threaded("hop", 1).Report.ElapsedSeconds, "and is faster than on one");
            h.Expect(api.StartRun(id, new RunRequest("global", Threads: 0)).Error!.Contains("threads"), "no threads is refused");

            h.Expect(api.StartRun(id, new RunRequest("global", GlobalMode: "sideways")).Error!.Contains("not a global search mode"),
                     "an unknown mode is refused");
            h.Expect(api.StartRun(id, new RunRequest("global", GlobalMode: "hop", Perturbation: 0)).Error!.Contains("perturbation"),
                     "a zero perturbation is refused");
        });

        // NEEDLE ON BANDPASS (the user, 2026-10-17: "needles and evolution are worthless ... in my
        // legacy implementation adding a layer always helped"). Three faults: a needle of the host's
        // own material was taken (a split, not a layer: ten of them, 37 layers that evolve merged
        // back to 17); the design was not refined before the first map, so thickness gradients
        // posed as needles; and the map was refused once a rounded depth fell past the stack.
        WithLogs(_ =>
        {
            var api = new ThinFilmApi();
            string id = api.OpenSample("Bandpass").Value!.Id;
            var n = api.WaitRun(api.StartRun(id, new RunRequest("needle", Insertions: 20)).Value!.Run).Value!;
            var log = api.RunStatus(n.Run).Value!.Log;
            double refined = double.Parse((log.FirstOrDefault(l => l.StartsWith("refined")) ?? "RMSE=NaN ").Split("RMSE=")[1].Split(' ')[0],
                                          System.Globalization.CultureInfo.InvariantCulture);
            h.Expect(log.Any(l => l.StartsWith("refined")), "needle refines before its first map, and logs it");
            h.Expect(!log.Any(l => l.StartsWith("stopped")), "the map is never refused part way");
            h.Expect(n.EndMerit < 0.2 * refined && log.Count(l => l.StartsWith("insert")) >= 10,
                     $"real insertions take Bandpass far below its refined start ({refined:G4} → {n.EndMerit:G4})");
            api.KeepRun(n.Run);
            var materials = api.Document(id).Value!.Layers.Select(l => l.Material).ToArray();
            h.Expect(materials.Zip(materials.Skip(1)).All(p => p.First != p.Second),
                     "no needle is its host's own material: no two neighbouring layers share one");
            int before = materials.Length;
            // GRADUAL EVOLUTION (the user, 2026-10-18): grown a layer at a time, needled at each size,
            // Bandpass goes from where needle stopped (1.87) to under a millionth of it.
            var e = api.WaitRun(api.StartRun(id, new RunRequest("evolve")).Value!.Run).Value!;
            var evolveLog = api.RunStatus(e.Run).Value!.Log;
            h.Expect(e.EndMerit < 1e-3 * n.EndMerit!.Value,
                     $"evolve grows Bandpass past where needle stopped ({before} → {e.Layers} layers, {n.EndMerit:G4} → {e.EndMerit:G4})");
            h.Expect(evolveLog.Any(l => l.StartsWith("grow 1") && l.Contains("next to the")) && evolveLog.Any(l => l.StartsWith("step 1"))
                     && evolveLog.Any(l => l.StartsWith("insert 1.")),
                     "and logs each growth, each step's needles, and each step");
            h.Expect(evolveLog.Count(l => l.Contains("<- best")) >= 2 && e.Outcome!.StartsWith("best at step"),
                     $"the best steps marked, the best one the result ({e.Outcome})");
        });

        // TARGET MET (the user, 2026-10-17): once the merit is under a millionth of the refined
        // start there is nothing to gain, and a 1 nm needle only disturbs the design.
        WithLogs(_ =>
        {
            var api = new ThinFilmApi();
            string id = api.OpenSample("Dichroic").Value!.Id;
            var n = api.WaitRun(api.StartRun(id, new RunRequest("needle", MaxIterations: 6000)).Value!.Run).Value!;
            var log = api.RunStatus(n.Run).Value!.Log;
            h.Expect(log.Any(l => l.StartsWith("target met")) && !log.Any(l => l.StartsWith("rejected")),
                     $"Dichroic: needle stops at target met, with no needle tried past it ({n.EndMerit:E3}, {n.Iteration} insertions)");
        });

        // STOP at the engine, for each: heard between steps, the design left at its best.
        var api2 = new ThinFilmApi();
        string id2 = api2.OpenSample("AR-visible").Value!.Id;
        Design Fresh() { var d = new Design(api2.Document(id2).Value!.ToSpec()); d.Plan(); return d; }
        using (var stop = new CancellationTokenSource())
        {
            var r = GlobalSearch.Run(Fresh(), new GlobalSearchOptions
            {
                Starts = 10,
                Refinement = new DlsOptions { Cancellation = stop.Token },
                StartDone = t => { if (t.Start == 2) stop.Cancel(); },
            });
            h.Expect(r.Cancelled && r.Starts == 2, $"global stops after its second start ({r.Starts})");
        }
        using (var stop = new CancellationTokenSource())
        {
            int steps = 0;
            var r = NeedleOptimiser.Run(Fresh(), new NeedleOptions
            {
                MaxInsertions = 6,
                Refinement = new DlsOptions { Cancellation = stop.Token },
                StepDone = _ => { ++steps; stop.Cancel(); },
            });
            h.Expect(steps == 1 && r.Insertions + r.Rejected == 1, $"needle stops after its first insertion ({steps})");
        }
    }

    /// <summary>The user (2026-10-18), on whether evolve should also grow the design: "Yes please
    /// modify evolve". Gradual evolution, held to what it is for.</summary>
    public static void EvolutionGrowsTheDesign(Harness h)
    {
        h.Section("gradual evolution: grown a layer at a time, needled at each size, the best step kept");

        (Design Design, double[] Growth, DlsOptions Dls) Open(string sample)
        {
            var api = new ThinFilmApi();
            string id = api.OpenSample(sample).Value!.Id;
            var doc = api.Document(id).Value!;
            var (lo, up) = LayerUnits.Bounds(doc);
            var d = new Design(doc.ToSpec());
            d.Plan();
            var growth = doc.Materials.Select((_, m) => 1.0 / LayerUnits.PerNm(doc, m, LayerUnits.Qwot)).ToArray();
            return (d, growth, new DlsOptions { Lower = lo, Upper = up, MaxIterations = 300 });
        }

        // V-COAT: two layers, a merit of 446 that needle alone leaves where it is.
        var (v, g, dls) = Open("V-coat");
        var needled = NeedleOptimiser.Run(v, new NeedleOptions { MaxInsertions = 30, Refinement = dls });
        (v, g, dls) = Open("V-coat");
        var steps = new List<EvolutionCycle>();
        var grown = new List<GrowthStep>();
        var r = GradualEvolution.Evolve(v, new GrowthOptions
        {
            GrowthThickness = g, Refinement = dls, StepDone = steps.Add, Grown = grown.Add,
        });
        h.Expect(r.FinalMerit < 1e-3 * needled.FinalMerit,
                 $"V-coat: far past where needle alone stops ({needled.FinalMerit:G4} → {r.FinalMerit:G4}, {r.Design.LayerCount} layers)");
        h.Expect(steps[0].Step == 0 && grown.Count == steps.Count - 1 && grown.All(s => s.Step >= 1),
                 "step 0 grows nothing; every later step grows one layer");
        // THE RESULT: the fewest layers among the steps within the allowed degradation (5%) of the
        // lowest merit; the lowest itself when none of those has fewer layers.
        double lowest = steps.Min(s => s.Merit);
        var within = steps.Where(s => s.Merit <= lowest * 1.05).ToList();
        h.Expect(r.FinalMerit <= lowest * 1.05 + 1e-15 && r.Design.LayerCount == within.Min(s => s.Layers)
                 && Math.Abs(r.FinalMerit - steps[r.BestStep].Merit) <= 1e-12 * Math.Max(1e-300, r.FinalMerit)
                 && (r.Preferred is null) == (r.FinalMerit == lowest),
                 $"the result is the fewest layers within 5% of the lowest merit (step {r.BestStep}, "
                 + $"{r.Design.LayerCount} layers at {r.FinalMerit:G4}; lowest {lowest:G4}; {r.Preferred})");
        h.Expect(r.Stopped == "target met", $"and it says why it stopped ({r.Stopped})");

        // A GROWN DESIGN CAN STAY WORSE: AR-visible, grown half a quarter wave at a time, is best as
        // it was needled at step 0, and three worse steps end the evolution. The result is step 0's.
        (v, g, dls) = Open("AR-visible");
        steps.Clear();
        var ar = GradualEvolution.Evolve(v, new GrowthOptions
        {
            GrowthThickness = g.Select(x => x / 2).ToArray(), Refinement = dls, StepDone = steps.Add,
        });
        h.Expect(ar.Stopped.Contains("without a better design") && ar.BestStep < steps[^1].Step
                 && ar.FinalMerit < steps[^1].Merit && Math.Abs(ar.FinalMerit - steps[ar.BestStep].Merit) <= 1e-12 * ar.FinalMerit
                 && ar.Design.LayerCount == steps[ar.BestStep].Layers,
                 $"when later steps are worse, the best step is returned, not the last (best {ar.BestStep} "
                 + $"at {ar.FinalMerit:G4}, last {steps[^1].Step} at {steps[^1].Merit:G4}; {ar.Stopped})");

        // THE SIDES, THE LIMITS.
        (v, g, dls) = Open("Bandpass");
        grown.Clear();
        var incident = GradualEvolution.Evolve(v, new GrowthOptions
        {
            GrowthThickness = g, Refinement = dls, Side = GrowthSide.Incident, MaxSteps = 2, InsertionsPerStep = 1,
            Grown = grown.Add,
        });
        h.Expect(grown.Count == 2 && grown.All(s => s.Incident), "Side Incident grows next to the incident medium only");
        (v, g, dls) = Open("Bandpass");
        grown.Clear();
        GradualEvolution.Evolve(v, new GrowthOptions
        {
            GrowthThickness = g, Refinement = dls, Side = GrowthSide.Substrate, MaxSteps = 1, InsertionsPerStep = 1,
            Grown = grown.Add,
        });
        h.Expect(grown is [{ Incident: false }], "Side Substrate next to the substrate");
        (v, g, dls) = Open("Bandpass");
        int layers = v.LayerCount;
        var capped = GradualEvolution.Evolve(v, new GrowthOptions
        {
            GrowthThickness = g, Refinement = dls, MaxLayers = layers, InsertionsPerStep = 1, MaxSteps = 4,
        });
        h.Expect(capped.Design.LayerCount <= layers && capped.Stopped.Contains("most allowed"),
                 $"MaxLayers: never past it, needles included ({capped.Design.LayerCount} of {layers}; {capped.Stopped})");
        (v, g, dls) = Open("Bandpass");
        var none = GradualEvolution.Evolve(v, new GrowthOptions { GrowthThickness = g, Refinement = dls, MaxSteps = 0, InsertionsPerStep = 1 });
        h.Expect(none.Steps == 0 && none.BestStep == 0, "MaxSteps 0: needles and removals only");
    }

    public static void TheCliAndTheMcpRunTheSameRun(Harness h)
    {
        h.Section("thinfilm refine and the optimise_* tools: the same runner, the same lines");

        WithLogs(_ =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "thinfilm-run-tests");
            Directory.CreateDirectory(dir);
            string design = Path.Combine(dir, "ar.tfd"), result = Path.Combine(dir, "ar-refined.tfd");
            var o = new StringWriter();
            File.Copy(SampleFiles.PathOf("AR-visible"), design, overwrite: true);

            var output = new StringWriter();
            var error = new StringWriter();
            int code = CommandLine.Run(["refine", design, "-o", result], output, error);
            var lines = output.ToString().Split('\n', StringSplitOptions.TrimEntries);
            h.Pin(code, 0, $"thinfilm refine exits zero ({error})");
            h.Expect(lines.Any(l => l.StartsWith("start") && l.Contains("RMSE=")) && lines.Any(l => l.StartsWith("iter"))
                     && lines.Any(l => l.StartsWith("end")) && lines.Any(l => l.StartsWith("kept")),
                     "it prints the run's log as it goes: start, each iteration, end, kept");
            h.Expect(lines.Any(l => l.StartsWith("log ")), "and where the log file is");
            var saved = DesignDocument.FromJson(File.ReadAllText(result));
            h.Expect(saved.History is [{ Result: "kept", Method: "refine" }], "the design it writes carries the run in its history");

            output = new StringWriter();
            CommandLine.Run(["refine", result, "--revert", "-o", result], output, new StringWriter());
            saved = DesignDocument.FromJson(File.ReadAllText(result));
            h.Expect(saved.History is [{ Result: "kept" }, { Result: "reverted" }], "--revert writes the design as it was, the run recorded");

            // THE MCP TOOLS on the same sample: the log they return has the same shape.
            var tools = new Tools();
            JsonNode Call(string name, string args)
            {
                var (text, failed) = tools.Call(name, JsonNode.Parse(args)!.AsObject());
                h.Expect(!failed, $"{name} succeeds ({(failed ? text : "")})");
                return JsonNode.Parse(text)!;
            }
            Call("load_design", SampleFiles.LoadArgs("AR-visible"));
            var run = Call("optimise_start", """{"design":"d1","iterations":300}""")["Run"]!.GetValue<string>();
            var status = Call("optimise_status", $$"""{"run":"{{run}}","wait_seconds":30}""");
            h.Expect(status["State"]!.GetValue<string>() == "finished", "optimise_status can wait for the run to end");
            var mcpLog = status["Log"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
            h.Expect(mcpLog.Any(l => l.StartsWith("start")) && mcpLog[^1].StartsWith("end"),
                     "and returns the same log lines");
            h.Expect(status["StartMerit"] is not null && status["EndMerit"] is not null && status["ElapsedSeconds"] is not null,
                     "with the start and end RMSE and the elapsed time");
            var kept = Call("optimise_keep", $$"""{"run":"{{run}}"}""");
            h.Expect(Math.Abs(kept["Merit"]!.GetValue<double>() - status["EndMerit"]!.GetValue<double>()) < 1e-12,
                     "optimise_keep makes it the design's");
            h.Expect(Call("optimise_runs", "{}").AsArray() is [{ } listed] && listed["State"]!.GetValue<string>() == "kept",
                     "optimise_runs lists it, kept");
        });
    }

    public static void TheWindowsMonitorReadsTheRun(Harness h)
    {
        h.Section("RunMonitor: what the Refine window shows, from the same run");

        WithLogs(_ =>
        {
            var w = new DesignWorkspace();
            w.OpenSample("AR-visible");
            double before = w.Merit;
            var m = new RunMonitor(w) { IterationsText = "zero" };
            h.Expect(m.CanStart && !m.CanAccept && m.StartMeritText == "—", "before Start: ready, nothing to accept");
            h.Expect(m.Start()!.Contains("not a number of iterations"), "an unreadable iteration count is refused");
            m.IterationsText = "300";
            h.Expect(m.Start() is null && !m.CanStart, "Start runs it, once");
            w.WaitRun(m.Report!.Run, 30000);
            m.Poll();
            h.Expect(m.CanAccept && m.StateText.StartsWith("Finished"), $"it finishes ({m.StateText})");
            h.Expect(m.StartMeritText == ThinFilmApi.Rms(before) && m.CurrentLabel == "End RMSE"
                     && m.CurrentMeritText == ThinFilmApi.Rms(m.Report.EndMerit!.Value),
                     $"start and end RMSE ({m.StartMeritText} → {m.CurrentMeritText})");
            h.Expect(m.ChangeText.StartsWith("−") && m.ChangeText.EndsWith("%"), $"and the change ({m.ChangeText})");
            h.Expect(m.Log.Split('\n').Count(l => l.StartsWith("iter")) == m.Report.Iteration, "the log has every iteration");
            h.Expect(w.Merit == before, "the design is unchanged until Accept");
            h.Expect(m.Accept() is null && Math.Abs(w.Merit - m.Report.EndMerit!.Value) < 1e-12, "Accept keeps it");
            h.Expect(w.Status.StartsWith("refine kept: RMSE"), $"and the status line says so ({w.Status})");
        });
    }
}
