// PlotTests.cs — the spectrum a design is saved with and opens to.
//
// The user (2026-10-21): "are the spectrum choices saved in the file? I think it makes sense to
// save the most relevant choices based on the article. The wavelength range should match the
// article and what to plot."

using System.Text.Json.Nodes;
using ThinFilm.Api;
using ThinFilm.Cli;
using ThinFilm.Gui.Core;
using ThinFilm.Mcp;

namespace ThinFilm.Native.Tests;

public static class PlotTests
{
    private static readonly PlotDocument Paper = new(1500.0, 1600.0, 1001, 45.0, ["RS", "RP"]);

    public static void APlotIsSavedWithTheDesign(Harness h)
    {
        h.Section("A design's plot: set, saved in the file, refused when it cannot be plotted, cleared");

        var api = new ThinFilmApi();
        string id = api.OpenSample("AR-visible").Value!.Id;
        h.Expect(api.Describe(id).Value!.Plot is null, "a sample has no saved plot");
        double merit = api.Describe(id).Value!.Merit;
        var r = api.SetPlot(id, Paper with { Quantities = ["rs", " rp "] });
        h.Expect(r.Ok && r.Value!.Plot!.Quantities.SequenceEqual(["RS", "RP"]) && r.Value.Merit == merit,
                 "set: codes in capitals, and nothing computed changes");
        var again = api.Load(api.Save(id).Value!).Value!;
        h.Expect(again.Plot == Paper with { Quantities = again.Plot!.Quantities } && again.Plot.Quantities.SequenceEqual(["RS", "RP"]),
                 "and it comes back from the saved file");
        h.Expect(api.SetPlot(id, Paper with { To = 1400.0 }) is { Ok: false } bad && bad.Error!.Contains("increasing"),
                 "a range that runs backwards is refused");
        h.Expect(api.SetPlot(id, Paper with { Quantities = ["XX"] }) is { Ok: false } q && q.Error!.Contains("'XX'"),
                 "and a quantity that does not exist");
        h.Expect(api.SetPlot(id, null).Value!.Plot is null && !api.Save(id).Value!.Contains("\"Plot\""),
                 "null removes it, and the file has none");
    }

    public static void TheWorkspaceOpensToIt(Harness h)
    {
        h.Section("The Spectrum tab opens to a design's plot and saves what it shows; changing it is no edit");

        var api = new ThinFilmApi();
        string id = api.OpenSample("AR-visible").Value!.Id;
        api.SetPlot(id, Paper with { VsAngle = true, AngleFrom = 10, AngleTo = 80, AngleSteps = 71, Wavelength = 1550 });
        string json = api.Save(id).Value!;

        var w = new DesignWorkspace();
        w.Open(json, "paper.tfd");
        h.Expect(w.SpectrumVsAngle && w.SpectrumAngleFrom == 10 && w.SpectrumAngleTo == 80 && w.SpectrumAngleSteps == 71
                 && w.SpectrumWavelength == 1550 && w.SpectrumFrom == 1500 && w.SpectrumTo == 1600 && w.SpectrumSteps == 1001
                 && w.SpectrumAngle == 45 && w.SpectrumQuantities == "RS,RP",
                 "opened: against angle as saved, and the wavelength axis's settings kept for a switch");
        h.Expect(w.Spectrum.Count == 2 && w.Spectrum[0].X.Length == 71, "and that is what is plotted");

        w.SpectrumVsAngle = false;
        w.SpectrumTo = 1700;
        h.Expect(!w.IsModified, "changing the plot alone does not ask to be saved");
        var saved = DesignDocument.FromJson(w.Save()!).Plot!;
        h.Expect(!saved.VsAngle && saved.To == 1700 && saved.AngleSteps == 71, "a save writes the plot as it now is");

        var bare = new DesignWorkspace();
        bare.SpectrumFrom = 300;
        bare.OpenSample("AR-visible");
        h.Expect(bare.SpectrumFrom == 300, "a design with no saved plot leaves the Spectrum tab as it was");
    }

    public static void AnOpaqueFilmIsPlotted(Harness h)
    {
        h.Section("A spectrum of a film opaque at every plotted wavelength is plotted, not refused as nothing to optimise");

        // Every layer a metal-like k = 5, thick enough to clamp everywhere: as variables they are
        // all inert, which an optimisation refuses and a spectrum has no reason to.
        var doc = JsonNode.Parse(File.ReadAllText(SampleFiles.PathOf("AR-visible")))!;
        foreach (var m in doc["Materials"]!.AsArray())
        {
            m!["Model"] = new JsonObject { ["Kind"] = "CONSTANT", ["Coefficients"] = new JsonArray(1.0, 5.0) };
            m["K"] = new JsonArray(m["N"]!.AsArray().Select(_ => (JsonNode?)JsonValue.Create(5.0)).ToArray());
        }
        foreach (var l in doc["Layers"]!.AsArray()) l!["Thickness"] = 2000.0;

        var api = new ThinFilmApi();
        string id = api.Load(doc.ToJsonString()).Value!.Id;
        var r = api.Spectrum(id, new SpectrumRequest(20.0, 20.5, 2, 0.0, ["RA", "TA"]));
        h.Expect(r.Ok && r.Value!.Wavelength.Length == 2 && r.Value.Quantity["TA"].All(t => t < 1e-12),
                 $"two wavelengths, T zero at both ({r.Error})");
    }

    public static void TheCliWritesAnglesInDegrees(Harness h)
    {
        h.Section("thinfilm spectrum and the MCP give a phase or a Delta in degrees, as the Spectrum tab draws it");

        // At 45 deg a bare-looking sample still has phases well away from zero, so a column left
        // in radians cannot pass for degrees.
        string dir = Path.Combine(Path.GetTempPath(), "thinfilm-plot-tests");
        Directory.CreateDirectory(dir);
        string f = Path.Combine(dir, "deg.tfd"), csv = Path.Combine(dir, "deg.csv");
        File.Copy(SampleFiles.PathOf("AR-visible"), f, overwrite: true);
        string[] codes = ["RA", "R_PHASE_S", "R_DELTA"];

        var api = new ThinFilmApi();
        string id = api.Load(File.ReadAllText(f)).Value!.Id;
        var engine = api.Spectrum(id, new SpectrumRequest(500, 600, 3, 45.0, codes)).Value!;

        CommandLine.Run(["spectrum", f, "--from", "500", "--to", "600", "--steps", "3", "--angle", "45",
                         "--quantities", string.Join(',', codes), "-o", csv], new StringWriter(), new StringWriter());
        var rows = File.ReadAllLines(csv).Skip(1)
            .Select(l => l.Split(',').Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray()).ToArray();
        bool Same(int col, string q, double scale) =>
            rows.Select((r, i) => Math.Abs(r[col] - engine.Quantity[q][i] * scale)).Max() < 1e-9;
        h.Expect(rows.Length == 3 && Same(2, "R_PHASE_S", 180 / Math.PI) && Same(3, "R_DELTA", 180 / Math.PI),
                 "phase and Delta columns are the engine's radians times 180/pi");
        h.Expect(Same(1, "RA", 1.0), "and a reflectance is left a fraction");

        var json = new StringWriter();
        CommandLine.Run(["spectrum", f, "--from", "500", "--to", "600", "--steps", "3", "--angle", "45",
                         "--quantities", "R_DELTA", "--json"], json, new StringWriter());
        double d0 = JsonNode.Parse(json.ToString())!["Quantity"]!["R_DELTA"]![0]!.GetValue<double>();
        h.Expect(Math.Abs(d0 - engine.Quantity["R_DELTA"][0] * 180 / Math.PI) < 1e-9, "--json too");

        var tools = new Tools();
        string mid = JsonNode.Parse(tools.Call("load_design", new JsonObject { ["path"] = f }).Item1)!["Id"]!.GetValue<string>();
        var (text, failed) = tools.Call("spectrum", new JsonObject
        {
            ["design"] = mid, ["from"] = 500.0, ["to"] = 600.0, ["steps"] = 3, ["angle_degrees"] = 45.0,
            ["quantities"] = "RA,R_DELTA",
        });
        var q = failed ? null : JsonNode.Parse(text)!["Quantity"];
        h.Expect(q is not null
                 && Math.Abs(q["R_DELTA"]![0]!.GetValue<double>() - engine.Quantity["R_DELTA"][0] * 180 / Math.PI) < 1e-9
                 && Math.Abs(q["RA"]![0]!.GetValue<double>() - engine.Quantity["RA"][0]) < 1e-12,
                 $"and the MCP's spectrum tool, the same numbers ({(failed ? text : "")})");
    }

    public static void TheCliAndTheMcp(Harness h)
    {
        h.Section("thinfilm plot sets it and spectrum uses it; the MCP's set_plot");

        string dir = Path.Combine(Path.GetTempPath(), "thinfilm-plot-tests");
        Directory.CreateDirectory(dir);
        string f = Path.Combine(dir, "p.tfd"), csv = Path.Combine(dir, "p.csv");
        File.Copy(SampleFiles.PathOf("AR-visible"), f, overwrite: true);
        var output = new StringWriter();
        h.Expect(CommandLine.Run(["plot", f, "--from", "450", "--to", "650", "--steps", "21", "--angle", "30", "--quantities", "RS,RP"],
                                 output, new StringWriter()) == 0
                 && output.ToString().Contains("RS,RP against wavelength, 450 to 650 nm in 21 steps, at 30 deg"),
                 $"plot sets and says it: {output.ToString().Trim()}");
        CommandLine.Run(["spectrum", f, "-o", csv], new StringWriter(), new StringWriter());
        var lines = File.ReadAllLines(csv);
        h.Expect(lines[0] == "wavelength,RS,RP" && lines.Length == 22 && lines[1].StartsWith("450,"),
                 "spectrum with no options plots the saved one");
        h.Expect(CommandLine.Run(["plot", f, "--clear"], new StringWriter(), new StringWriter()) == 0
                 && !File.ReadAllText(f).Contains("\"Plot\""), "--clear removes it");

        var tools = new Tools();
        var (opened, _) = tools.Call("load_design", new JsonObject { ["path"] = f });
        string id = JsonNode.Parse(opened)!["Id"]!.GetValue<string>();
        var (set, failed) = tools.Call("set_plot", new JsonObject { ["design"] = id, ["from_nm"] = 500.0, ["to_nm"] = 900.0, ["quantities"] = "TA" });
        var plot = failed ? null : JsonNode.Parse(set)!["Plot"];
        h.Expect(plot?["From"]?.GetValue<double>() == 500 && plot["To"]!.GetValue<double>() == 900
                 && plot["Steps"]!.GetValue<int>() == PlotDefaults.Plot.Steps && plot["Quantities"]![0]!.GetValue<string>() == "TA",
                 $"set_plot: what is given, the rest from the defaults ({JsonNode.Parse(set)?["Plot"]?.ToJsonString() ?? set})");
    }
}
