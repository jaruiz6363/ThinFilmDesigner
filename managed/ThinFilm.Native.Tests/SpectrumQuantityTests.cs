// SpectrumQuantityTests.cs — the quantities a spectrum offers, and quantities in different units
// plotted together against separate y axes.
//
// The user (2026-10-19): "the user should not have to type them in ... The software should support
// multiple Y axes so that more items can be plotted in one graph."

using ThinFilm.Api;
using ThinFilm.Gui.Core;

namespace ThinFilm.Native.Tests;

public static class SpectrumQuantityTests
{
    public static void EveryListedQuantityComputes(Harness h)
    {
        h.Section("every quantity the checklist offers is computed by a spectrum");

        var api = new ThinFilmApi();
        string id = api.OpenSample("AR-visible").Value!.Id;
        var codes = SpectrumQuantities.All.Select(q => q.Code).ToArray();
        var r = api.Spectrum(id, new SpectrumRequest(450, 650, 3, 30.0, codes));
        h.Expect(r.Ok, $"all {codes.Length} in one request ({r.Error})");
        var bad = r.Ok ? codes.Where(c => !r.Value!.Quantity[c].All(double.IsFinite)).ToArray() : codes;
        h.Expect(bad.Length == 0, $"each finite at 30° ({string.Join(", ", bad)})");
        h.Expect(SpectrumQuantities.All.Select(q => q.Code).Distinct().Count() == codes.Length
                 && SpectrumQuantities.Groups.Count == SpectrumQuantities.All.Select(q => q.Group).Distinct().Count(),
                 "no code is listed twice");
    }

    public static void DifferentUnitsGetDifferentAxes(Harness h)
    {
        h.Section("PlotModel: a series per unit, an axis per unit, the first on the left");

        double[] x = [400, 500, 600];
        var model = new PlotModel();
        model.Update([
            new PlotSeries("R", x, [0.01, 0.02, 0.03], 0xFF000000, Axis: "fraction"),
            new PlotSeries("Ψ", x, [10.0, 40.0, 80.0], 0xFF000000, Axis: "degrees"),
            new PlotSeries("T", x, [0.97, 0.98, 0.99], 0xFF000000, Axis: "fraction"),
        ], 600, 300);
        h.Expect(model.Axes.Count == 2 && model.Axes[0].Title == "fraction" && model.Axes[1].Title == "degrees",
                 $"two axes, in the order their series come ({model.Axes.Count})");
        h.Expect(model.Axes[0].Min <= 0.01 && model.Axes[0].Max >= 0.99 && model.Axes[1].Min <= 10 && model.Axes[1].Max >= 80,
                 $"each covers its own data ({model.Axes[0].Min}..{model.Axes[0].Max}, {model.Axes[1].Min}..{model.Axes[1].Max})");
        h.Expect(Math.Abs(model.PlotWidth - (600 - model.Left - model.Right - model.AxisGap)) < 1e-9
                 && Math.Abs(model.AxisPosition(1) - (model.Left + model.PlotWidth)) < 1e-9,
                 "the second axis is at the plot's right edge, with room for it beyond");
        var psi = model.Polyline(model.Series[1]);
        h.Expect(Math.Abs(psi[2].Y - model.ToPixelY(80.0, 1)) < 1e-9 && Math.Abs(model.YTicks(axis: 1)[0].Position - model.ToPixelY(model.YTicks(axis: 1)[0].Value, 1)) < 1e-9,
                 "a series is drawn against its own axis, and so are that axis's ticks");

        var one = new PlotModel();
        one.Update([new PlotSeries("R", x, [0.01, 0.02, 0.03], 0xFF000000)], 600, 300);
        h.Expect(one.Axes.Count == 1 && Math.Abs(one.PlotWidth - (600 - one.Left - one.Right)) < 1e-9,
                 "one unit, one axis, and the plot as wide as before");
    }

    public static void TheWorkspacePlotsEachQuantityInItsUnit(Harness h)
    {
        h.Section("the workspace: R, Ψ and OD on three axes, Ψ in degrees");

        var w = new DesignWorkspace();
        h.Expect(w.SpectrumQuantityText == "R (avg s,p), T (avg s,p)", $"it starts with R and T ({w.SpectrumQuantityText})");
        w.OpenSample("AR-visible");
        w.SpectrumAngle = 45.0;
        w.SpectrumQuantities = "RA,R_PSI,ODA";
        h.Expect(w.SpectrumQuantityText == "R (avg s,p), Ψ R (°), OD (avg s,p)", $"the list says what is plotted ({w.SpectrumQuantityText})");
        h.Expect(w.RefreshSpectrum() && w.Spectrum.Count == 3, $"three series ({w.Status})");
        h.Expect(w.Spectrum.Select(s => s.Axis).SequenceEqual([SpectrumQuantities.Fraction, SpectrumQuantities.Degrees, SpectrumQuantities.Density]),
                 "each against its unit's axis");

        var api = new ThinFilmApi();
        string id = api.OpenSample("AR-visible").Value!.Id;
        var radians = api.Spectrum(id, new SpectrumRequest(w.SpectrumFrom, w.SpectrumTo, w.SpectrumSteps, 45.0, ["R_PSI"])).Value!.Quantity["R_PSI"];
        h.Expect(Math.Abs(w.Spectrum[1].Y[0] - radians[0] * 180.0 / Math.PI) < 1e-9,
                 $"Ψ is drawn in degrees, from the engine's radians ({w.Spectrum[1].Y[0]:G5}°)");

        var (bx, by) = DesignWorkspace.BreakAtWraps([1, 2, 3, 4], [170, 178, -176, -170]);
        h.Expect(bx.Length == 5 && double.IsNaN(by[2]) && bx[2] == 2.5 && by[3] == -176,
                 "a Δ that wraps from +180° to −180° breaks the line there rather than crossing the plot");

        w.SpectrumQuantities = "";
        h.Expect(!w.RefreshSpectrum() && w.Status.Contains("Choose at least one"), $"nothing chosen is said ({w.Status})");
    }
}
