// DesignReportTests.cs — File > Export design report: the layers in nm and an n, k table for
// every material used, for a program without dispersion equations.
//
// The user (2026-10-22): "a report export file that lists the design in physical units, and the
// index of refraction of all materials that are used ... something a user would need to input into
// another program where dispersion equations are not available." And: "Report should be
// accessible via GUI, CLI, and MCP".

using System.Globalization;
using System.Text.Json.Nodes;
using ThinFilm.Api;
using ThinFilm.Cli;
using ThinFilm.Mcp;

namespace ThinFilm.Native.Tests;

public static class DesignReportTests
{
    /// <summary>AR-visible with dispersion to report: SiO2 as fused silica's Sellmeier, trusted only
    /// from 500 nm; layer 2 shown in QWOT; an unused MgF2; a saved plot from 400 to 705 nm.</summary>
    private static string Design()
    {
        var doc = JsonNode.Parse(File.ReadAllText(SampleFiles.PathOf("AR-visible")))!;
        foreach (var m in doc["Materials"]!.AsArray())
            if (m!["Name"]!.GetValue<string>() == "SiO2")
                m["Model"] = new JsonObject
                {
                    ["Kind"] = "SELLMEIER",
                    ["Coefficients"] = new JsonArray(0.6961663, 0.0046791, 0.4079426, 0.0135121, 0.8974794, 97.934),
                    ["MinMicrons"] = 0.5, ["MaxMicrons"] = 3.71, ["Source"] = "Malitson 1965",
                };
        int nw = doc["Wavelength"]!.AsArray().Count;
        doc["Materials"]!.AsArray().Add(new JsonObject
        {
            ["Name"] = "MgF2", ["N"] = new JsonArray(Enumerable.Range(0, nw).Select(_ => (JsonNode?)JsonValue.Create(1.38)).ToArray()),
            ["K"] = new JsonArray(Enumerable.Range(0, nw).Select(_ => (JsonNode?)JsonValue.Create(0.0)).ToArray()),
        });
        doc["Layers"]![1]!["Unit"] = "QWOT";
        doc["Plot"] = new JsonObject { ["From"] = 400.0, ["To"] = 705.0, ["Steps"] = 31, ["Angle"] = 0.0, ["Quantities"] = new JsonArray("RA") };
        return doc.ToJsonString();
    }

    /// <summary>The rows of a [section]: tab-split, its header row first, '#' lines left out.</summary>
    private static string[][] Section(string text, string title)
    {
        var lines = text.Split('\n');
        int i = Array.IndexOf(lines, $"[{title}]");
        if (i < 0) return [];
        return lines.Skip(i + 1).TakeWhile(l => l.Length > 0).Where(l => !l.StartsWith('#'))
                    .Select(l => l.Split('\t')).ToArray();
    }

    private static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    public static void TheReportIsNumbers(Harness h)
    {
        h.Section("Export design report: the layers in nm and an n, k table for each material used, at the wavelengths asked for");

        var api = new ThinFilmApi();
        string id = api.Load(Design()).Value!.Id;
        var doc = api.Document(id).Value!;
        var r = api.DesignReport(id, new DesignReportRequest());
        h.Expect(r.Ok, $"made ({r.Error})");
        if (!r.Ok) return;
        string text = r.Value!.Text;
        var w = r.Value.Wavelengths;
        h.Expect(w.Length == 32 && w[0] == 400 && w[^2] == 700 && w[^1] == 705,
                 $"by default the saved plot's range every 10 nm, and its end though the steps miss it ({w[0]}..{w[^1]}, {w.Length})");

        var layers = Section(text, "Layers");
        h.Expect(layers.Length == doc.Layers.Length + 3 && layers[0].SequenceEqual(["layer", "material", "thickness_nm"])
                 && layers[1][0] == "incident" && layers[1][1] == "air" && layers[^1][0] == "substrate",
                 "the layers between the incident medium and the substrate, incident side first");
        h.Expect(doc.Layers.Select((l, i) => Math.Abs(D(layers[i + 2][2]) - l.Thickness)).Max() < 1e-4
                 && layers[3][1] == doc.Materials[doc.Layers[1].Material].Name,
                 "each in nm - the QWOT layer too - with its material");

        var silica = Section(text, "Index: SiO2");
        var model = doc.Materials.First(m => m.Name == "SiO2").Model!;
        var at550 = silica.FirstOrDefault(row => row[0] == "550");
        h.Expect(silica.Length == 33 && silica[0].SequenceEqual(["wavelength_nm", "n", "k"]) && at550 is not null
                 && Math.Abs(D(at550[1]) - model.At(550, "SiO2").N) < 1e-8 && D(at550[2]) == 0.0,
                 $"SiO2 as numbers from its Sellmeier: n {at550?[1]} at 550 nm");
        h.Expect(text.Contains("# Data: Malitson 1965") && text.Contains("Outside the data's valid range (500 to 3710 nm) at 400-490 nm")
                 && r.Value.Notes.Any(n => n.Contains("SiO2")),
                 "where it says, and that 400-490 nm are extrapolated");
        h.Expect(Section(text, "Index: Nb2O5").Length == 33 && Section(text, "Index: MgF2").Length == 0,
                 "every material a layer uses, and none that no layer uses");
        h.Expect(Section(text, "Index: air (incident medium)").Length == 33
                 && Section(text, $"Index: {doc.Exit.Name} (substrate)").Length == 33,
                 "and both media");
        h.Expect(text.Contains("N = n + ik, k >= 0 absorbing"), "the index convention is stated");

        var some = api.DesignReport(id, new DesignReportRequest(500, 600, 25)).Value!;
        h.Expect(some.Wavelengths.SequenceEqual([500.0, 525, 550, 575, 600]), "a range and step as asked");
        h.Expect(api.DesignReport(id, new DesignReportRequest(StepNm: 0)) is { Ok: false } z && z.Error!.Contains("step")
                 && api.DesignReport(id, new DesignReportRequest(600, 500)) is { Ok: false }
                 && api.DesignReport(id, new DesignReportRequest(1, 1e6, 0.01)) is { Ok: false } many && many.Error!.Contains("larger step"),
                 "a zero step, a backward range and a million rows are refused");

        string path = Path.Combine(Path.GetTempPath(), "thinfilm-report-test.txt");
        var written = api.DesignReport(id, new DesignReportRequest(500, 600, 50, path));
        h.Expect(written.Ok && File.ReadAllText(path) == written.Value!.Text, "and written to a path as returned");
    }

    public static void TheCliAndTheMcp(Harness h)
    {
        h.Section("thinfilm report, and the MCP's design_report");

        string dir = Path.Combine(Path.GetTempPath(), "thinfilm-report-tests");
        Directory.CreateDirectory(dir);
        string f = Path.Combine(dir, "r.tfd"), txt = Path.Combine(dir, "r.txt");
        File.WriteAllText(f, Design());
        File.Delete(txt);

        var output = new StringWriter();
        int code = CommandLine.Run(["report", f, "--from", "500", "--to", "600", "--step", "50", "-o", txt], output, new StringWriter());
        h.Expect(code == 0 && File.Exists(txt) && Section(File.ReadAllText(txt), "Index: SiO2").Length == 4
                 && output.ToString().Contains("3 wavelengths, 500 to 600 nm"),
                 $"-o writes it and says so: {output.ToString().Trim()}");
        var stdout = new StringWriter();
        CommandLine.Run(["report", f], stdout, new StringWriter());
        h.Expect(stdout.ToString().StartsWith("# Design report: AR-visible") && Section(stdout.ToString(), "Index: Nb2O5").Length == 33,
                 "without -o it goes to standard output, over the saved plot's range");
        h.Expect(CommandLine.Run(["report", f, "--step", "0"], new StringWriter(), new StringWriter()) != 0, "and a bad step fails");

        var tools = new Tools();
        string id = JsonNode.Parse(tools.Call("load_design", new JsonObject { ["path"] = f }).Item1)!["Id"]!.GetValue<string>();
        var (text, failed) = tools.Call("design_report", new JsonObject { ["design"] = id, ["from_nm"] = 500, ["to_nm"] = 600, ["step_nm"] = 50 });
        var report = failed ? null : JsonNode.Parse(text)!["Text"]?.GetValue<string>();
        h.Expect(report is not null && Section(report, "Index: SiO2").Length == 4 && Section(report, "Layers").Length == 7,
                 $"design_report returns the text ({(failed ? text : "")})");
    }
}
