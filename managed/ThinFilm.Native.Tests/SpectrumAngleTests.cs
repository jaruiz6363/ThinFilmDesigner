// SpectrumAngleTests.cs — the Spectrum tab against angle of incidence at one wavelength.
//
// The user (2026-10-19), to test the System Parameters flags: "It would be nice to be able to graph
// the parameters versus angle at single wavelength. We would need start angle of incidence, end
// angle of incidence and wavelength."
//
// The oracles: each angle's value against a wavelength spectrum taken at that one angle; total
// internal reflection past the critical angle when angles are in the medium, and none when they are
// in air; Snell's law between the two.

using System.Text.Json.Nodes;
using ThinFilm.Api;
using ThinFilm.Cli;
using ThinFilm.Gui.Core;
using ThinFilm.Mcp;

namespace ThinFilm.Native.Tests;

public static class SpectrumAngleTests
{
    private static readonly double[] Grid = [500.0, 600.0];

    /// <summary>Two layers between an incident medium of index n0 and a substrate of index ns.</summary>
    private static DesignDocument Stack(double n0, double ns, bool back = false, bool inAir = false, double k = 1e-5) => new()
    {
        Name = "angles",
        Wavelength = Grid,
        Materials = [MaterialDocument.Constant("H", 2.3, 0.0, Grid.Length),
                     MaterialDocument.Constant("L", 1.46, 0.0, Grid.Length)],
        Layers = [new LayerDocument(0, 60.0), new LayerDocument(1, 95.0)],
        Points = [new PointDocument(0, 0.0)],
        Incident = MaterialDocument.Constant("ambient", n0, 0.0, Grid.Length),
        Exit = MaterialDocument.Constant("substrate", ns, k, Grid.Length),
        Operands = [new OperandDocument("RA", "EQUAL", 0.0, 1.0, Points: [0])],
        BackSurface = back,
        AngleInAir = inAir,
    };

    private static string Open(ThinFilmApi api, DesignDocument doc) => api.CreateFromDocument(doc).Value!.Id;

    private static SpectrumReport VsAngle(ThinFilmApi api, string id, double from, double to, int steps,
                                          double wl, params string[] q) =>
        api.Spectrum(id, new SpectrumRequest(from, to, steps, 0.0, q, VsAngle: true, Wavelength: wl)).Value!;

    /// <summary>The value at one angle, from a wavelength spectrum whose first point is wl.</summary>
    private static double At(ThinFilmApi api, string id, double deg, double wl, string q) =>
        api.Spectrum(id, new SpectrumRequest(wl, wl + 10.0, 2, deg, [q])).Value!.Quantity[q][0];

    public static void EachAngleIsTheSpectrumAtThatAngle(Harness h)
    {
        h.Section("a sweep in angle reads, at each angle, what a wavelength spectrum at that angle reads");

        foreach (bool back in new[] { false, true })
        {
            var api = new ThinFilmApi();
            string id = Open(api, Stack(1.0, 1.52, back));
            var r = VsAngle(api, id, 0, 80, 9, 550, "RS", "RP", "TA");
            bool same = true;
            for (int i = 0; i < 9; ++i)
                foreach (var q in new[] { "RS", "RP", "TA" })
                    same &= Math.Abs(r.Quantity[q][i] - At(api, id, r.Angle![i], 550, q)) < 1e-14;
            h.Expect(r.Angle is { Length: 9 } && r.Angle[1] == 10.0 && r.Wavelength is [550.0],
                     $"back surface {(back ? "on" : "off")}: nine angles 0..80 at the one wavelength");
            h.Expect(same, $"back surface {(back ? "on" : "off")}: Rs, Rp and TA agree at every angle "
                           + $"(Rp {r.Quantity["RP"][5]:0.000000} at 50°)");
        }

        var plain = new ThinFilmApi();
        var w = plain.Spectrum(Open(plain, Stack(1.0, 1.52)), new SpectrumRequest(400, 800, 5, 30.0, ["RA"])).Value!;
        h.Expect(w.Angle is null && w.Wavelength.Length == 5, "a wavelength spectrum has no Angle array, as before");
    }

    public static void TotalInternalReflectionShowsTheFlag(Harness h)
    {
        h.Section("glass into air: total internal reflection past the critical angle in the medium, never from air");

        double critical = Math.Asin(1.0 / 1.52) * 180.0 / Math.PI;     // 41.14 degrees in glass
        var api = new ThinFilmApi();
        string medium = Open(api, Stack(1.52, 1.0, k: 0.0));
        var m = VsAngle(api, medium, 0, 88, 89, 550, "RS", "RP");
        bool tir = true, below = true;
        for (int i = 0; i < m.Angle!.Length; ++i)
        {
            if (m.Angle[i] > critical + 0.5) tir &= Math.Abs(m.Quantity["RS"][i] - 1) < 1e-9 && Math.Abs(m.Quantity["RP"][i] - 1) < 1e-9;
            if (m.Angle[i] < critical - 0.5) below &= m.Quantity["RS"][i] < 0.999;
        }
        h.Expect(tir, $"angles in the medium: Rs = Rp = 1 past {critical:0.00}° (Rs {m.Quantity["RS"][45]:R} at 45°)");
        h.Expect(below, "and below it the coating transmits");

        string air = Open(api, Stack(1.52, 1.0, inAir: true, k: 0.0));
        var a = VsAngle(api, air, 0, 88, 89, 550, "RS");
        h.Expect(a.Quantity["RS"].All(v => v < 0.999 && double.IsFinite(v)),
                 $"angles in air: no angle reaches it (Rs {a.Quantity["RS"][88]:0.0000} at 88° in air)");
        double inGlass = Math.Asin(Math.Sin(60.0 * Math.PI / 180) / 1.52) * 180 / Math.PI;
        h.Expect(Math.Abs(a.Quantity["RS"][60] - At(api, medium, inGlass, 550, "RS")) < 1e-12,
                 $"60° in air is {inGlass:0.000}° in the glass, by Snell's law");
    }

    public static void UnreachableAnglesAreGaps(Harness h)
    {
        h.Section("angles in air into a medium less dense than air: the unreachable ones are NaN, not a refusal");

        double limit = Math.Asin(0.9) * 180 / Math.PI;      // 64.16 degrees
        var api = new ThinFilmApi();
        string id = Open(api, Stack(0.9, 1.52, inAir: true));
        var r = api.Spectrum(id, new SpectrumRequest(0, 80, 9, 0.0, ["RS", "TS"], VsAngle: true, Wavelength: 550));
        h.Expect(r.Ok, $"the sweep is computed ({r.Error})");
        var rs = r.Value!.Quantity["RS"];
        bool split = true;
        for (int i = 0; i < 9; ++i)
            split &= (r.Value.Angle![i] > limit) == double.IsNaN(rs[i]);
        h.Expect(split, $"exactly the angles past {limit:0.00}° are NaN ({string.Join(" ", rs.Select(v => v.ToString("0.###")))})");
        h.Expect(Math.Abs(rs[3] - At(api, id, 30, 550, "RS")) < 1e-14, "and the others are what they would be on their own");

        var none = api.Spectrum(id, new SpectrumRequest(70, 80, 3, 0.0, ["RS"], VsAngle: true, Wavelength: 550));
        h.Expect(!none.Ok && none.Error!.Contains("cannot be reached"), $"with none reachable it is refused ({none.Error})");
        h.Expect(!api.Spectrum(id, new SpectrumRequest(0, 90, 3, 0.0, ["RS"], VsAngle: true)).Ok, "90° is refused");
        h.Expect(!api.Spectrum(id, new SpectrumRequest(0, 60, 3, 0.0, ["RS"], VsAngle: true, Wavelength: 0)).Ok,
                 "a wavelength of 0 is refused");

        var model = new PlotModel();
        var s = new PlotSeries("RS", r.Value.Angle!, rs, 0xFF000000);
        model.Update([s], 400, 300);
        var runs = model.Segments(s);
        h.Expect(runs.Count == 1 && runs[0].Count == 7, "the plot draws the reachable run and stops at the gap");
        var holed = new PlotSeries("x", [0, 1, 2, 3, 4], [0, double.NaN, 1, 1, double.NaN], 0);
        h.Expect(model.Segments(holed).Select(g => g.Count).SequenceEqual([1, 2]),
                 "a NaN in the middle breaks the line in two");
    }

    public static void TheWorkspaceKeepsBothRanges(Harness h)
    {
        h.Section("the Spectrum tab's selector: each axis keeps its own range, and the note says how angles are taken");

        var w = new DesignWorkspace();
        h.Expect(w.Open(Stack(1.0, 1.52, back: true, inAir: true).ToJson()), "a design opens");
        w.SpectrumFrom = 420; w.SpectrumTo = 680;
        w.SpectrumVsAngle = true;
        w.SpectrumAngleFrom = 10; w.SpectrumAngleTo = 70; w.SpectrumAngleSteps = 13; w.SpectrumWavelength = 600;
        w.SpectrumQuantities = "RS,RP";
        h.Expect(w.RefreshSpectrum(), $"the sweep in angle plots ({w.Status})");
        h.Expect(w.Spectrum.Count == 2 && w.Spectrum[0].X.Length == 13 && w.Spectrum[0].X[0] == 10 && w.Spectrum[0].X[12] == 70,
                 "x is the angle, 10 to 70 in 13 steps");
        h.Expect(w.PlotNote.Contains("600 nm") && w.PlotNote.Contains("in air") && w.PlotNote.Contains("back surface"),
                 $"the note names the wavelength, angles in air and the back surface ({w.PlotNote})");
        w.SpectrumVsAngle = false;
        h.Expect(w.RefreshSpectrum() && w.Spectrum[0].X[0] == 420 && w.SpectrumAngleFrom == 10,
                 "back on wavelength, the wavelength range is as it was and the angle range is kept");
        w.SpectrumVsAngle = true;
        w.SpectrumAngleTo = 90;
        h.Expect(!w.RefreshSpectrum(), "an end angle of 90° is refused");
    }

    public static void TheCliAndTheMcpSweepAngle(Harness h)
    {
        h.Section("thinfilm spectrum --vs angle, and the MCP's spectrum with vs 'angle'");

        var dir = Path.Combine(Path.GetTempPath(), "thinfilm-angle-tests");
        Directory.CreateDirectory(dir);
        string design = Path.Combine(dir, "angles.tfd");
        File.WriteAllText(design, Stack(0.9, 1.52, inAir: true).ToJson());
        var o = new StringWriter();
        var e = new StringWriter();
        int code = CommandLine.Run(["spectrum", design, "--vs", "angle", "--from", "0", "--to", "80", "--steps", "9",
                                    "--wavelength", "550", "--quantities", "RS,TS"], o, e);
        var lines = o.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        h.Expect(code == 0 && lines[0] == "angle,RS,TS" && lines.Length == 10 && lines[2].StartsWith("10,"),
                 $"a CSV with an angle column ({e.ToString().Trim()}{lines.FirstOrDefault()})");
        h.Expect(lines[9].StartsWith("80,NaN"), $"an unreachable angle is written as NaN ({lines[^1]})");
        h.Expect(CommandLine.Run(["spectrum", design, "--vs", "frequency"], new StringWriter(), new StringWriter()) != 0,
                 "--vs wants wavelength or angle");

        var tools = new Tools();
        var (text, _) = tools.Call("load_design", new JsonObject { ["document"] = File.ReadAllText(design) });
        string id = JsonNode.Parse(text)!["Id"]!.GetValue<string>();
        bool failed;
        (text, failed) = tools.Call("spectrum", new JsonObject
        {
            ["design"] = id, ["from"] = 0.0, ["to"] = 60.0, ["steps"] = 4.0, ["vs"] = "angle", ["wavelength"] = 600.0,
            ["quantities"] = "RS",
        });
        var json = JsonNode.Parse(text)!;
        h.Expect(!failed && json["Angle"]!.AsArray().Count == 4 && json["Wavelength"]![0]!.GetValue<double>() == 600,
                 $"the result has the angles and the one wavelength ({text[..Math.Min(140, text.Length)]})");
        (_, failed) = tools.Call("spectrum", new JsonObject
        {
            ["design"] = id, ["from"] = 0.0, ["to"] = 60.0, ["steps"] = 4.0, ["vs"] = "sideways",
        });
        h.Expect(failed, "vs wants 'wavelength' or 'angle'");
    }
}
