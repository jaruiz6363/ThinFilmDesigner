// BandTests.cs — the band operands ASOL, RSOL, TSOL, EMIT and AERATIO (M3.5), and the spectra they
// are weighted by (M3.3): the weights, the values against a fine spectrum, the derivatives, the
// optimisers, the merit table, the CLI and the MCP.
//
// The user (2026-10-20), choosing what came next: "Go ahead with option 1" - finishing the
// Solar-absorber sample, the last one marked incomplete.

using System.Text.Json.Nodes;
using ThinFilm.Api;
using ThinFilm.Cli;
using ThinFilm.Gui.Core;
using ThinFilm.Mcp;

namespace ThinFilm.Native.Tests;

public static class BandTests
{
    public static void TheWeightsIntegrateTheSpectrum(Harness h)
    {
        h.Section("AM1.5 and the blackbody: the table is the standard's, and the weights integrate it");

        var (nm, e) = Spectra.Am15Global;
        double total = 0.0;
        for (int i = 1; i < nm.Length; ++i) total += 0.5 * (e[i] + e[i - 1]) * (nm[i] - nm[i - 1]);
        h.Expect(nm.Length == 2002 && nm[0] == 280.0 && nm[^1] == 4000.0 && Math.Abs(total - 1000.37) < 0.01,
                 $"ASTM G173-03 global: {nm.Length} wavelengths, 280-4000 nm, {total:F2} W/m²");

        // A linear A is integrated exactly by the hat weights, however coarse the points, so the
        // average of λ itself is the spectrum's own mean wavelength.
        double[] coarse = MeritTable.Spread(300.0, 2500.0, 12);
        var ws = Spectra.Weights("ASOL", coarse, 0.0, out _)!;
        double mean = coarse.Zip(ws).Sum(p => p.First * p.Second);
        double num = 0.0, den = 0.0;
        for (int i = 1; i < nm.Length; ++i)
        {
            double a = Math.Max(nm[i - 1], 300.0), b = Math.Min(nm[i], 2500.0);
            if (b <= a) continue;
            for (int k = 0; k < 8; ++k)
            {
                double x = a + (b - a) * (k + 0.5) / 8, s = Spectra.Solar(x), dx = (b - a) / 8;
                num += x * s * dx; den += s * dx;
            }
        }
        h.Expect(Math.Abs(ws.Sum() - 1.0) < 1e-12 && Math.Abs(mean - num / den) < 1e-3,
                 $"twelve points: weights sum to one, and λ averages to {mean:F3} nm (fine integral {num / den:F3})");

        var wb = Spectra.Weights("EMIT", MeritTable.Spread(2500.0, 25000.0, 8), 373.15, out _)!;
        double bnum = 0.0, bden = 0.0;
        for (double x = 2500.0 + 0.5; x < 25000.0; x += 1.0) { double p = Spectra.Planck(x, 373.15); bnum += x * p; bden += p; }
        double bmean = MeritTable.Spread(2500.0, 25000.0, 8).Zip(wb).Sum(p => p.First * p.Second);
        h.Expect(Math.Abs(bmean - bnum / bden) < 1e-3 * bnum / bden,
                 $"a 373.15 K blackbody on eight points: λ averages to {bmean:F1} nm ({bnum / bden:F1})");

        h.Expect(Spectra.Weights("ASOL", [5000.0, 9000.0], 0.0, out var why) is null && why!.Contains("outside AM1.5"),
                 $"a band the sun does not reach is refused ({why})");
        h.Expect(Spectra.Weights("EMIT", [3000.0, 4000.0], 0.0, out why) is null && why!.Contains("temperature"),
                 $"so is a blackbody with no temperature ({why})");
    }

    public static void ASolarAbsorberReadsItsBands(Harness h)
    {
        h.Section("the Solar-absorber sample: ASOL, EMIT and AERATIO against a fine spectrum");

        var api = new ThinFilmApi();
        string id = api.OpenSample("Solar-absorber").Value!.Id;
        var ops = api.Evaluate(id).Value!.Operands;
        double asol = ops.Single(o => o.Code == "ASOL").Value, emit = ops.Single(o => o.Code == "EMIT").Value;
        double ratio = ops.Single(o => o.Code == "AERATIO").Value;

        double Fine(double from, double to, int steps, Func<double, double> s)
        {
            var sp = api.Spectrum(id, new SpectrumRequest(from, to, steps, 0.0, ["AA"])).Value!;
            double num = 0.0, den = 0.0;
            for (int i = 1; i < steps; ++i)
            {
                double a = sp.Wavelength[i - 1], b = sp.Wavelength[i], m = 0.5 * (a + b);
                double w = s(m) * (b - a), aa = 0.5 * (sp.Quantity["AA"][i] + sp.Quantity["AA"][i - 1]);
                num += aa * w; den += w;
            }
            return num / den;
        }
        double asolFine = Fine(300.0, 2500.0, 4401, Spectra.Solar);
        double emitFine = Fine(2500.0, 25000.0, 2251, x => Spectra.Planck(x, 373.15));
        h.Expect(Math.Abs(asol - asolFine) < 2e-3, $"ASOL {asol:F5} on 45 points, {asolFine:F5} on 4401");
        h.Expect(Math.Abs(emit - emitFine) < 2e-3, $"EMIT {emit:F5} on 46 points, {emitFine:F5} on 2251");

        // AERATIO reads its own points, 300 nm to 25 µm; on the same points it is exactly ASOL / EMIT.
        var at = api.Operands(id).Value!.Single(o => o.Code == "AERATIO").Wavelengths;
        var asolThere = new OperandEdit("ASOL", "NONE", 0, 0, 0, at, 0.0);
        var emitThere = new OperandEdit("EMIT", "NONE", 0, 0, 0, at, 0.0, Temperature: 373.15);
        api.InsertOperand(id, 0, asolThere);
        api.InsertOperand(id, 0, emitThere);
        var again = api.Evaluate(id).Value!.Operands;
        h.Expect(Math.Abs(again[1].Value / again[0].Value - ratio) < 1e-12 * ratio,
                 $"AERATIO {ratio:F6} is ASOL / EMIT on its points ({again[1].Value:F6} / {again[0].Value:F6})");

        var spec = SampleFiles.Document("AR-visible").ToSpec();
        h.Expect(spec.Operands.PointWeight is null && spec.Operands.InputCount is null
                 && SampleFiles.Document("Solar-absorber").ToSpec().Operands.Code.Length == 7,
                 "a design without band rows is handed exactly what it was; AERATIO brings two hidden rows");
    }

    /// <summary>The sample with every band row an equality aimed at zero, so a residual is a value.</summary>
    private static DesignDocument Scored(DesignDocument doc) => doc with
    {
        Operands = doc.Operands.Select(o => Codes.IsBand(o.Code)
            ? o with { Comparison = "EQUAL", Target = 0.0, Weight = 1.0, Scale = 1.0 } : o).ToArray(),
    };

    public static void TheDerivativesAreTheValuesSlopes(Harness h)
    {
        h.Section("the Jacobian of ASOL, EMIT and AERATIO, and the needle map, against differences");

        // The band rows alone: MINT would see the needle itself as a layer thinner than its limit,
        // a step the needle map leaves out by design.
        var all = Scored(SampleFiles.Document("Solar-absorber"));
        var doc = all with { Operands = all.Operands.Where(o => Codes.IsBand(o.Code)).ToArray() };
        var d = new Design(doc.ToSpec());
        d.Plan();
        d.EvaluateJacobian();
        var jac = d.Jacobian.ToArray();
        int m = d.Report.n_residuals;

        Design At(DesignDocument x) { var e = new Design(x.ToSpec()); e.Plan(); e.Evaluate(); return e; }
        double worst = 0.0;
        for (int v = 0; v < doc.Layers.Length; ++v)
        {
            const double step = 1e-4;
            var up = doc.Layers.Select(l => l.Thickness).ToArray(); up[v] += step;
            var dn = doc.Layers.Select(l => l.Thickness).ToArray(); dn[v] -= step;
            var a = At(doc.WithThickness(up));
            var b = At(doc.WithThickness(dn));
            for (int o = 0; o < 3; ++o)
            {
                double fd = (a.Value[o] - b.Value[o]) / (2 * step);
                double an = jac[v * m + d.ResidualRow(o)];
                worst = Math.Max(worst, Math.Abs(fd - an) / Math.Max(1e-6, Math.Abs(fd)));
            }
        }
        h.Expect(worst < 1e-5, $"three rows, three layers: agrees ({worst:0.0e0} relative)");

        double SumSq(DesignDocument x) => At(x).Residual.ToArray().Sum(r => r * r);
        DesignDocument Split(int layer, double at, int material, double eps)
        {
            var l = doc.Layers.ToList();
            var host = l[layer];
            l.RemoveAt(layer);
            l.InsertRange(layer, [host with { Thickness = at }, new LayerDocument(material, eps),
                                  host with { Thickness = host.Thickness - at }]);
            return doc with { Layers = [.. l] };
        }
        double depth = doc.Layers[0].Thickness * 0.4;
        int other = doc.Layers[0].Material == 0 ? 1 : 0;
        var map = d.NeedleMap([depth], [other]);
        const double eps = 1e-4;
        double fd0 = (SumSq(Split(0, depth, other, eps)) - SumSq(Split(0, depth, other, -eps))) / (2 * eps);
        h.Expect(Math.Abs(map[0] - fd0) < 1e-5 * Math.Max(1e-3, Math.Abs(fd0)),
                 $"a needle in layer 1: map {map[0]:0.000000e0}, inserted {fd0:0.000000e0}");
    }

    public static void EveryOptimiserRunsOnIt(Harness h)
    {
        h.Section("Refine, Needle and Gradual evolution on the Solar-absorber sample");

        foreach (var stages in new[] { "refine", "refine,needle", "refine,evolve" })
        {
            var api = new ThinFilmApi();
            string id = api.OpenSample("Solar-absorber").Value!.Id;
            double before = api.Describe(id).Value!.Merit;
            var r = api.Optimise(id, new OptimiseRequest(Stages: stages, MaxIterations: 150, NeedleInsertions: 2));
            var asol = api.Evaluate(id).Value!.Operands.Single(o => o.Code == "ASOL").Value;
            h.Expect(r.Ok && r.Value!.FinalMerit < 0.5 * before && asol > 0.8,
                     $"{stages}: merit {before:G4} -> {(r.Ok ? r.Value!.FinalMerit : double.NaN):G4}, ASOL {asol:F3} {r.Error}");
        }
    }

    public static void TheMeritTableMakesBandRows(Harness h)
    {
        h.Section("the merit table: a band code brings its band, EMIT its temperature, in a Temp (K) column");

        var w = new DesignWorkspace();
        w.OpenSample("AR-visible");
        var row = w.MeritRows[0];
        row.Code = "EMIT";
        row = w.MeritRows[0];
        h.Expect(row.Code == "EMIT" && row.CanSpan && row.WavelengthText == "2500" && row.Wavelength2Text == "25000"
                 && row.PointsText == "46" && row.CanTemperature && row.TemperatureText == "373.15",
                 $"RA made EMIT: 2.5-25 µm in 46 points at 373.15 K ({w.Status})");
        row.TemperatureText = "500";
        row = w.MeritRows[0];
        h.Expect(row.TemperatureText == "500" && row.Help.Contains("blackbody at 500 K"), $"and 500 K is kept: {row.Help}");
        row.Code = "ASOL";
        row = w.MeritRows[0];
        h.Expect(!row.CanTemperature && row.TemperatureText == "" && row.PointsText == "46",
                 "made ASOL it keeps its band and loses its temperature");
        h.Expect(MeritRow.CommonCodes.Contains("AERATIO") && MeritRow.CommonCodes.Contains("TSOL"), "the codes are offered");

        var api = new ThinFilmApi();
        string id = api.OpenSample("AR-visible").Value!.Id;
        var one = api.AddOperands(id, new OperandSweep("TSOL", "Min", 0.9, 1.0, 400.0));
        h.Expect(!one.Ok && one.Error!.Contains("band"), $"a band code at one wavelength is refused ({one.Error})");
        var spectrum = api.Spectrum(id, new SpectrumRequest(400, 700, 3, 0.0, ["ASOL"]));
        h.Expect(!spectrum.Ok && spectrum.Error!.Contains("plot what it averages (AA)"),
                 $"and so is a band code in a spectrum ({spectrum.Error})");
    }

    public static void TheCliAndTheMcpToo(Harness h)
    {
        h.Section("thinfilm merit add --code EMIT --temperature; the MCP's add_operands and edit_operand");

        var dir = Path.Combine(Path.GetTempPath(), "thinfilm-band-tests");
        Directory.CreateDirectory(dir);
        string f = Path.Combine(dir, "sa.tfd");
        File.Copy(SampleFiles.PathOf("AR-visible"), f, overwrite: true);
        int Run(params string[] args) => CommandLine.Run(args, new StringWriter(), new StringWriter());

        int before = DesignDocument.FromJson(File.ReadAllText(f)).Operands.Length;
        h.Expect(Run("merit", "add", f, "--code", "EMIT", "--mode", "Max", "--target", "0.1", "--from", "3000",
                     "--to", "20000", "--step", "1000", "--temperature", "350") == 0
                 && DesignDocument.FromJson(File.ReadAllText(f)) is var doc && doc.Operands.Length == before + 1
                 && doc.Operands[^1] is { Code: "EMIT", Temperature: 350.0, Points.Length: 18 },
                 "one EMIT row over 18 wavelengths at 350 K");
        h.Expect(Run("merit", "edit", f, "--row", (before + 1).ToString(), "--temperature", "400") == 0
                 && DesignDocument.FromJson(File.ReadAllText(f)).Operands[^1].Temperature == 400.0,
                 "merit edit --temperature");

        var tools = new Tools();
        var (opened, _) = tools.Call("load_design", new JsonObject { ["path"] = SampleFiles.PathOf("AR-visible") });
        string id = JsonNode.Parse(opened)!["Id"]!.GetValue<string>();
        var (added, failed) = tools.Call("add_operands", new JsonObject
        {
            ["design"] = id, ["code"] = "AERATIO", ["mode"] = "Report", ["target"] = 0.0,
            ["from"] = 400.0, ["to"] = 20000.0, ["step"] = 400.0, ["temperature_k"] = 330.0,
        });
        var (edited, failed2) = tools.Call("edit_operand", new JsonObject
        {
            ["design"] = id, ["action"] = "edit", ["row"] = (double)(before + 1), ["temperature_k"] = 340.0,
        });
        var (evaluated, _) = tools.Call("evaluate_design", new JsonObject { ["design"] = id });
        var last = JsonNode.Parse(evaluated)!["Operands"]!.AsArray()[^1]!;
        h.Expect(!failed && !failed2 && last["Code"]!.GetValue<string>() == "AERATIO"
                 && last["Temperature"]!.GetValue<double>() == 340.0 && last["Value"]!.GetValue<double>() > 0,
                 $"AERATIO added and set to 340 K ({added[..Math.Min(60, added.Length)]} / {edited[..Math.Min(60, edited.Length)]})");
    }
}
