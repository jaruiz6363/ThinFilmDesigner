// GroupDelayTests.cs — group delay, GDD and TOD above the engine: the materials' derivatives, the
// merit function, refinement, the spectrum, the CLI and the MCP (S1.14, ABI 11; S1.17, ABI 12).
//
// The user (2026-10-20), on the chirped-mirror sample being incomplete: "What do we need to do?",
// and then "Yes" to building GD and GDD; and (2026-10-23) "I did want to return to TOD". The engine
// half is checked in tests/native/test_dispersion.cpp.

using System.Text.Json.Nodes;
using ThinFilm.Api;
using ThinFilm.Cli;
using ThinFilm.Gui.Core;
using ThinFilm.Mcp;

namespace ThinFilm.Native.Tests;

public static class GroupDelayTests
{
    public static void EveryModelGivesItsIndexDerivatives(Harness h)
    {
        h.Section("dn/dλ, d²n/dλ² and d³n/dλ³ from each material model, against differences of the model");

        var bk7 = new MaterialModel
        {
            Kind = "SELLMEIER",
            Coefficients = [1.03961212, 0.00600069867, 0.231792344, 0.0200179144, 1.01046945, 103.560653],
        };
        var cauchy = new MaterialModel { Kind = "CAUCHY", Coefficients = [2.2, 0.03, 0.002] };
        foreach (var (name, m) in new[] { ("Sellmeier BK7", bk7), ("Cauchy", cauchy) })
        {
            const double l = 800.0, step = 0.01;
            double N(double x) => m.At(x, name).N;
            var s = m.Slopes(l, name);
            double d1 = (N(l + step) - N(l - step)) / (2 * step);
            double d2 = (N(l + step) - 2 * N(l) + N(l - step)) / (step * step);
            h.Expect(Math.Abs(s.Dn - d1) < 1e-9 * Math.Abs(d1) + 1e-12 && Math.Abs(s.D2n - d2) < 1e-4 * Math.Abs(d2) + 1e-12,
                     $"{name}: dn/dλ {s.Dn:G6} (difference {d1:G6}), d²n/dλ² {s.D2n:G6} ({d2:G6})");
            const double h3 = 0.5;
            double d3 = (N(l + 2 * h3) - 2 * N(l + h3) + 2 * N(l - h3) - N(l - 2 * h3)) / (2 * h3 * h3 * h3);
            h.Expect(Math.Abs(s.D3n - d3) < 1e-4 * Math.Abs(d3), $"{name}: d³n/dλ³ {s.D3n:G6} ({d3:G6})");
        }

        var table = new MaterialModel { Kind = "TABLE", Table = [[0.7, 2.30, 0.01], [0.9, 2.20, 0.03]] };
        var t = table.Slopes(800.0, "table");
        h.Expect(Math.Abs(t.Dn - (-0.1 / 200.0)) < 1e-15 && Math.Abs(t.Dk - 0.02 / 200.0) < 1e-15 && t.D2n == 0.0 && t.D3n == 0.0,
                 $"a table: its segment's slope, no curvature ({t.Dn:G6}, {t.Dk:G6})");
        h.Expect(MaterialModel.Constant(1.5).Slopes(800.0, "c") == (0.0, 0.0, 0.0, 0.0, 0.0, 0.0), "a constant: none");

        // A catalogue glass is differenced; its third derivative against the Sellmeier it is.
        if (GlassCatalogs.Find("SCHOTT", "N-BK7") is not null)
        {
            var glass = new MaterialModel { Kind = "GLASS", Catalog = "SCHOTT", Glass = "N-BK7" };
            var g = glass.Slopes(800.0, "N-BK7");
            var b = bk7.Slopes(800.0, "BK7");
            h.Expect(Math.Abs(g.D3n - b.D3n) < 0.01 * Math.Abs(b.D3n),
                     $"SCHOTT N-BK7 from the catalogue: d³n/dλ³ {g.D3n:G6} (the formula's {b.D3n:G6})");
        }
    }

    public static void OnlyADispersionOperandSendsTheDerivatives(Harness h)
    {
        h.Section("the derivatives go to the engine only when a GD, GDD or TOD row is in the table");

        var ar = SampleFiles.Document("AR-visible").ToSpec();
        h.Expect(ar.MaterialDIndexDWavelength is null && ar.MaterialD2IndexDWavelength2 is null,
                 "AR-visible is handed exactly what it was before");
        var cm = SampleFiles.Document("Chirped-mirror").ToSpec();
        h.Expect(cm.MaterialDIndexDWavelength is { } d1 && d1.Length == cm.MaterialIndex.Length
                 && cm.D2IndexExitDWavelength2 is { } d2 && d2.Length == cm.Wavelength.Length,
                 "the chirped mirror is handed dn/dλ and d²n/dλ² for every material and medium");
        h.Expect(cm.MaterialD3IndexDWavelength3 is null && cm.D3IndexExitDWavelength3 is null,
                 "but not d³n/dλ³, which only TOD reads");

        var doc = SampleFiles.Document("Chirped-mirror");
        var tod = doc with { Operands = [.. doc.Operands, doc.Operands.First(o => o.Code == "R_GDD_S") with { Code = "R_TOD_S" }] };
        var ts = tod.ToSpec();
        h.Expect(ts.MaterialD3IndexDWavelength3 is { } d3 && d3.Length == ts.MaterialIndex.Length
                 && ts.D3IndexIncidentDWavelength3 is { } i3 && i3.Length == ts.Wavelength.Length
                 && ts.D3IndexExitDWavelength3 is { } e3 && e3.Length == ts.Wavelength.Length,
                 "a TOD row sends d³n/dλ³ for every material and medium");
    }

    public static void TheJacobianOfTheSampleIsItsThicknessSlope(Harness h)
    {
        h.Section("the chirped mirror's Jacobian - 40 layers, 21 points, GDD, Rs and stack rows - against differences");

        var api0 = new ThinFilmApi();
        var doc = api0.Document(api0.OpenSample("Chirped-mirror").Value!.Id).Value!;
        var tod = doc with { Operands = [.. doc.Operands, doc.Operands.First(o => o.Code == "R_GDD_S") with { Code = "R_TOD_S" }] };
        foreach (var (label, d) in new[] { ("as saved", doc), ("with a TOD row", tod) })
        {
            var design = new Design(d.ToSpec());
            design.Plan();
            design.Evaluate();
            var r0 = design.Residual.ToArray();
            double m0 = design.Merit;
            design.EvaluateJacobian();
            var J = design.Jacobian.ToArray();
            var rj = design.Residual.ToArray();
            double rworst = r0.Zip(rj, (a, b) => Math.Abs(a - b) / (1.0 + Math.Abs(a))).Max();
            h.Expect(rworst < 1e-12 && Math.Abs(design.Merit - m0) <= 1e-12 * m0,
                     $"{label}: the Jacobian call leaves the residuals and merit Evaluate gives " +
                     $"(worst {rworst:G3}; merit {design.Merit:G8} vs {m0:G8})");
            int nr = design.Residual.Length, nv = J.Length / Math.Max(1, nr);
            double worst = 0.0;
            for (int v = 0; v < nv; ++v)
            {
                double x = design.GetVariable(v), step = 1e-4;
                design.SetVariable(v, x + step); design.Evaluate(); var up = design.Residual.ToArray();
                design.SetVariable(v, x - step); design.Evaluate(); var dn = design.Residual.ToArray();
                design.SetVariable(v, x);
                for (int r = 0; r < nr; ++r)
                {
                    double fd = (up[r] - dn[r]) / (2 * step);
                    worst = Math.Max(worst, Math.Abs(J[v * nr + r] - fd) / (1.0 + Math.Abs(fd)));
                }
            }
            h.Expect(nv > 0 && worst < 1e-4, $"{label}: worst relative disagreement {worst:G3} over {nv} layers and {nr} residuals");

            // THE DEEPEST LAYER's column is zero to rounding, ~1e-16 noise from the series rather
            // than an exact 0, and damped least squares must treat it as inert: damped by its own
            // 1e-31 diagonal it stepped hundreds of nm on noise and stalled (found 2026-10-23).
            var (lo, hi) = LayerUnits.Bounds(d);
            var opts = new ThinFilm.Optimize.DlsOptions { Lower = lo, Upper = hi, MaxIterations = 30 };
            var res = new ThinFilm.Optimize.DampedLeastSquares(design.VariableCount, nr, opts).Minimise(design, opts);
            h.Expect(res.FinalMerit < 0.75 * res.InitialMerit,
                     $"{label}: 30 iterations of refinement lower the merit ({res.InitialMerit:G6} -> {res.FinalMerit:G6}, {res.Outcome})");
        }
    }

    public static void TodIsTheSlopeOfGdd(Harness h)
    {
        h.Section("TOD through the API is d(GDD)/dω of the API's own GDD, materials' dispersion and all");

        var api = new ThinFilmApi();
        string id = api.OpenSample("Chirped-mirror").Value!.Id;
        foreach (var (q, gdd) in new[] { ("R_TOD_S", "R_GDD_S"), ("T_TOD_P", "T_GDD_P") })
        {
            const double l = 800.0, step = 0.01;
            var s = api.Spectrum(id, new SpectrumRequest(l - step, l + step, 3, 30.0, [gdd, q]));
            if (!s.Ok) { h.Expect(false, $"{q}: {s.Error}"); continue; }
            var g = s.Value!.Quantity[gdd];
            double L1 = -(l * l) / (2.0 * Math.PI * 299.792458);
            double fd = (g[2] - g[0]) / (2.0 * step) * L1;
            double tod = s.Value!.Quantity[q][1];
            h.Expect(Math.Abs(tod - fd) < 1e-4 * Math.Abs(fd) + 1e-6,
                     $"{q} at 800 nm, 30°: {tod:G8} fs³, and the slope of {gdd} gives {fd:G8}");
        }

        // A TOD row in the merit function: evaluated, and refined towards its target.
        var doc = api.Document(id).Value!;
        var gddRow = doc.Operands.First(o => o.Code == "R_GDD_S");
        var withTod = doc with { Operands = [.. doc.Operands, gddRow with { Code = "R_TOD_S", Target = 0.0 }] };
        string tid = api.CreateFromDocument(withTod).Value!.Id;
        var e = api.Evaluate(tid);
        h.Expect(e.Ok && e.Value!.Operands.Any(o => o.Code == "R_TOD_S" && double.IsFinite(o.Value)),
                 $"a TOD row evaluates ({e.Error})");
        var r = api.Optimise(tid, new OptimiseRequest(Stages: "refine", MaxIterations: 50));
        h.Expect(r.Ok, $"and refinement runs with it ({r.Error})");
    }

    public static void TheChirpedMirrorIsDesignedForItsGdd(Harness h)
    {
        h.Section("the chirped-mirror sample: GDD = −50 fs² from 750 to 850 nm, and refinement gets there");

        var api = new ThinFilmApi();
        string id = api.OpenSample("Chirped-mirror").Value!.Id;
        double Miss() => api.Evaluate(id).Value!.Operands.Where(o => o.Code == "R_GDD_S")
                                .Average(o => Math.Abs(o.Value + 50.0));
        double before = Miss();
        var r = api.Optimise(id, new OptimiseRequest(Stages: "refine", MaxIterations: 300));
        double after = Miss();
        var rs = api.Evaluate(id).Value!.Operands.Where(o => o.Code == "RS").Min(o => o.Value);
        h.Expect(r.Ok && after < 0.25 * before, $"the mean miss falls from {before:F1} to {after:F1} fs² ({r.Error})");
        h.Expect(rs > 0.99, $"while Rs stays above 99% ({rs:P2})");

        // The spectrum and the merit table read the same number at the same wavelength.
        var row = api.Evaluate(id).Value!.Operands.First(o => o.Code == "R_GDD_S");
        double at = api.Document(id).Value!.Wavelength[api.Document(id).Value!.Points[api.Document(id).Value!.Operands
                        .First(o => o.Code == "R_GDD_S").Points![0]].Wavelength];
        var spectrum = api.Spectrum(id, new SpectrumRequest(at, at + 10, 2, 0.0, ["R_GDD_S", "R_GD_S"]));
        h.Expect(spectrum.Ok && Math.Abs(spectrum.Value!.Quantity["R_GDD_S"][0] - row.Value) < 1e-9 * Math.Abs(row.Value),
                 $"the spectrum's GDD at {at:F0} nm is the merit row's ({row.Value:F4} fs²)");
    }

    public static void TheSpectrumPlotsThemOnTheirOwnAxes(Harness h)
    {
        h.Section("Spectrum > Quantities: GD and GDD in a Dispersion group, each against its own axis");

        h.Expect(SpectrumQuantities.Groups.Contains("Dispersion")
                 && SpectrumQuantities.All.Count(q => q.Group == "Dispersion") == 12, "twelve dispersion quantities");
        var w = new DesignWorkspace();
        w.OpenSample("Chirped-mirror");
        w.SpectrumFrom = 700; w.SpectrumTo = 900; w.SpectrumSteps = 41;
        w.SpectrumQuantities = "RS,R_GD_S,R_GDD_S,R_TOD_S";
        h.Expect(w.RefreshSpectrum() && w.Spectrum.Select(s => s.Axis).SequenceEqual(
                     [SpectrumQuantities.Fraction, SpectrumQuantities.GroupDelay, SpectrumQuantities.Gdd, SpectrumQuantities.Tod]),
                 $"Rs, GD, GDD and TOD on four axes ({w.Status})");
        h.Expect(MeritRow.CommonCodes.Contains("R_GDD_S") && MeritRow.CommonCodes.Contains("T_GD_P")
                 && MeritRow.CommonCodes.Contains("R_TOD_S") && MeritRow.CommonCodes.Contains("T_TOD_P"),
                 "and the merit table offers the codes");
    }

    public static void NeedleAndEvolveRunOnTheChirpedMirror(Harness h)
    {
        h.Section("Needle and Gradual evolution on the chirped mirror: the GDD rows go through both");

        // Found by the user: Needle stopped with TF_NO_DISPERSION_DATA after its first insertion,
        // because the rebuilt stack lost d²n/dλ²; and the needle map had been skipping GDD rows.
        foreach (var stages in new[] { "refine,needle", "refine,evolve" })
        {
            var api = new ThinFilmApi();
            string id = api.OpenSample("Chirped-mirror").Value!.Id;
            double before = api.Describe(id).Value!.Merit;
            var r = api.Optimise(id, new OptimiseRequest(Stages: stages, MaxIterations: 150, NeedleInsertions: 2));
            h.Expect(r.Ok && r.Value!.FinalMerit < before,
                     $"{stages}: runs and improves the merit ({before:G4} -> {(r.Ok ? r.Value!.FinalMerit : double.NaN):G4}) {r.Error}");
        }
    }

    public static void TheCliAndTheMcpToo(Harness h)
    {
        h.Section("thinfilm merit add --target -50 and spectrum --quantities R_GDD_S, R_TOD_S; the MCP's spectrum");

        var dir = Path.Combine(Path.GetTempPath(), "thinfilm-gd-tests");
        Directory.CreateDirectory(dir);
        string f = Path.Combine(dir, "ar.tfd"), csv = Path.Combine(dir, "gd.csv");
        File.Copy(SampleFiles.PathOf("AR-visible"), f, overwrite: true);
        int Run(params string[] args) => CommandLine.Run(args, new StringWriter(), new StringWriter());

        h.Expect(Run("merit", "add", f, "--code", "R_GDD_S", "--mode", "Target", "--target", "-20", "--from", "550") == 0
                 && DesignDocument.FromJson(File.ReadAllText(f)).Operands.Any(o => o.Code == "R_GDD_S" && o.Target == -20.0),
                 "a negative target is a value, not an option");
        h.Expect(Run("spectrum", f, "--from", "500", "--to", "600", "--steps", "3", "--quantities", "R_GD_S,R_GDD_S,R_TOD_S", "-o", csv) == 0
                 && File.ReadAllLines(csv)[0] == "wavelength,R_GD_S,R_GDD_S,R_TOD_S", "spectrum writes GD, GDD and TOD columns");
        h.Expect(Run("merit", "add", f, "--code", "T_TOD_P", "--mode", "Target", "--target", "-300", "--from", "550") == 0
                 && DesignDocument.FromJson(File.ReadAllText(f)).Operands.Any(o => o.Code == "T_TOD_P" && o.Target == -300.0),
                 "merit add takes a TOD row");

        var tools = new Tools();
        var (opened, _) = tools.Call("load_design", new JsonObject { ["path"] = SampleFiles.PathOf("Chirped-mirror") });
        string id = JsonNode.Parse(opened)!["Id"]!.GetValue<string>();
        var (text, failed) = tools.Call("spectrum", new JsonObject
        {
            ["design"] = id, ["from"] = 750.0, ["to"] = 850.0, ["steps"] = 3.0, ["quantities"] = "R_GDD_S,R_TOD_S",
        });
        h.Expect(!failed && JsonNode.Parse(text)!["Quantity"]!["R_GDD_S"]!.AsArray().Count == 3
                 && JsonNode.Parse(text)!["Quantity"]!["R_TOD_S"]!.AsArray().Count == 3,
                 $"the MCP's spectrum returns GDD and TOD ({text[..Math.Min(80, text.Length)]})");
    }
}
