// RunCompareView.cs — what the optimisation window's Merit function, Spectrum and Layers tabs
// show once a run has ended, without the window.
//
// The numbers are ThinFilmApi's (RunCompare.cs), the ones the CLI's `compare` and the MCP's
// optimise_compare give. Here they become a read-only table with Before, After and Delta columns,
// before-and-after spectrum series, and two layer graphs drawn to one scale.

using System.Globalization;
using ThinFilm.Api;

namespace ThinFilm.Gui.Core;

/// <summary>One merit row, read-only: where and what it asks, and its value before and after.</summary>
public sealed record ComparisonRow(
    int Number, string Code, string Wavelength, string Wavelength2, string Points, string Angle,
    string Mode, string Target, string Target2, string Weight, string Before, string After, string Delta);

public sealed class RunCompareView
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly DesignWorkspace _workspace;

    public RunCompareView(DesignWorkspace workspace, RunComparison comparison)
    {
        _workspace = workspace;
        Comparison = comparison;
        Unit = workspace.WavelengthUnit;
        Rows = comparison.Operands.Select((edit, i) => Row(i, edit)).ToList();
        Scale = LayerGraphModel.CommonScale(comparison.BeforeStack, comparison.AfterStack);
        Colours = LayerGraphModel.Colours(comparison.BeforeStack, comparison.AfterStack);
        FromText = Show(workspace.SpectrumFrom);
        ToText = Show(workspace.SpectrumTo);
        StepsText = workspace.SpectrumSteps.ToString(Inv);
        AngleText = workspace.SpectrumAngle.ToString("0.##", Inv);
        QuantitiesText = workspace.SpectrumQuantities;
    }

    public RunComparison Comparison { get; }
    public string Unit { get; }

    // ---- Merit function ----------------------------------------------------------------------

    public List<ComparisonRow> Rows { get; }

    public string WavelengthHeader => $"Wv ({MeritRow.UnitLabel(Unit)})";
    public string Wavelength2Header => $"Wv2 ({MeritRow.UnitLabel(Unit)})";

    public string MeritSummary
    {
        get
        {
            double b = Comparison.Before.Merit, a = Comparison.After.Merit;
            string change = b > 0.0 ? $" ({((a - b) / b * 100.0).ToString("+0.0;−0.0", Inv)} %)" : "";
            return $"RMSE before {ThinFilmApi.Rms(b)}, after {ThinFilmApi.Rms(a)}{change}";
        }
    }

    private ComparisonRow Row(int i, OperandEdit edit)
    {
        double before = Comparison.Before.Operands[i].Value, after = Comparison.After.Operands[i].Value;
        bool stack = edit.Wavelengths.Length == 0, span = edit.Wavelengths.Length > 1;
        string mode = edit.Comparison.ToUpperInvariant() switch
        {
            "EQUAL" or "EQ" => "Target", "GE" => "Min", "LE" => "Max", "RANGE" => "Range", _ => "Report",
        };
        string V(double v) => double.IsNaN(v) ? "—" : v.ToString("G6", Inv);
        string delta = double.IsNaN(before) || double.IsNaN(after) ? "—"
            : after == before ? "0" : (after - before).ToString("+0.##E+00;−0.##E+00", Inv);
        return new ComparisonRow(
            i + 1, edit.Code.ToUpperInvariant(),
            stack ? "" : Show(edit.Wavelengths.Min()), span ? Show(edit.Wavelengths.Max()) : "",
            span ? edit.Wavelengths.Length.ToString(Inv) : "", stack ? "" : edit.AngleDegrees.ToString("0.##", Inv),
            mode, edit.Target.ToString("G6", Inv), mode == "Range" ? edit.TargetHi.ToString("G6", Inv) : "",
            edit.Weight.ToString("G6", Inv), V(before), V(after), delta);
    }

    // ---- Spectrum ----------------------------------------------------------------------------

    public const string BeforeAndAfter = "Before and after";
    public const string AfterOnly = "After";
    public const string BeforeOnly = "Before";
    public static IReadOnlyList<string> ShowOptions { get; } = [BeforeAndAfter, AfterOnly, BeforeOnly];

    public string ShowChoice { get; set; } = BeforeAndAfter;
    /// <summary>In the design's wavelength unit, like the main window's spectrum.</summary>
    public string FromText { get; set; }
    public string ToText { get; set; }
    public string StepsText { get; set; }
    public string AngleText { get; set; }
    /// <summary>The codes plotted, comma separated, as the API takes them.</summary>
    public string QuantitiesText { get; set; }

    /// <summary>What is plotted, in words.</summary>
    public string QuantityText =>
        string.Join(", ", QuantitiesText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                        .Select(DesignWorkspace.QuantityLabel));

    public string WavelengthTitle => $"wavelength ({MeritRow.UnitLabel(Unit)})";

    /// <summary>The series the settings ask for, or null with the reason. "Before" is dashed and
    /// lighter, "after" solid, each quantity in its own colour.</summary>
    public List<PlotSeries>? Spectrum(out string? problem)
    {
        problem = null;
        double factor = Unit == "um" ? 1000.0 : 1.0;
        if (!Number(FromText, out double from) || !Number(ToText, out double to) || !(to > from) || !(from > 0))
        { problem = "the range needs two wavelengths, the second above the first"; return null; }
        if (!int.TryParse(StepsText.Trim(), NumberStyles.Integer, Inv, out int steps) || steps < 2)
        { problem = "a spectrum needs at least two steps"; return null; }
        if (!Number(AngleText, out double angle) || !(angle >= 0.0 && angle < 90.0))
        { problem = "the angle of incidence must be at least 0° and below 90°"; return null; }
        var quantities = QuantitiesText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (quantities.Length == 0) { problem = "choose at least one quantity to plot"; return null; }

        var r = _workspace.RunSpectrum(Comparison.Run,
                                       new SpectrumRequest(from * factor, to * factor, steps, angle, quantities));
        if (r is null) { problem = _workspace.Status; return null; }

        bool showBefore = ShowChoice != AfterOnly, showAfter = ShowChoice != BeforeOnly;
        var x = r.After.Wavelength.Select(w => w / factor).ToArray();
        var series = new List<PlotSeries>();
        for (int q = 0; q < quantities.Length; ++q)
        {
            uint colour = DesignWorkspace.Palette[q % DesignWorkspace.Palette.Length];
            string label = DesignWorkspace.QuantityLabel(quantities[q]);
            if (showBefore && r.Before.Quantity.TryGetValue(quantities[q], out var b))
                series.Add(DesignWorkspace.QuantitySeries(quantities[q], $"{label} before", x, b, Lighter(colour), dashed: true));
            if (showAfter && r.After.Quantity.TryGetValue(quantities[q], out var a))
                series.Add(DesignWorkspace.QuantitySeries(quantities[q], $"{label} after", x, a, colour));
        }
        return series;
    }

    /// <summary>Halfway to white, so a dashed "before" sits behind the "after" it is compared with.</summary>
    private static uint Lighter(uint argb)
    {
        uint c(int shift) { uint v = (argb >> shift) & 0xFF; return (v + (0xFF - v) / 2) << shift; }
        return 0xFF000000 | c(16) | c(8) | c(0);
    }

    // ---- Layers ------------------------------------------------------------------------------

    public LayerScale Scale { get; }
    public IReadOnlyDictionary<string, uint> Colours { get; }

    public string LayersSummary(StackProfile p) =>
        $"{p.Layers.Length} layer{(p.Layers.Length == 1 ? "" : "s")}, {Lengths.Say(p.TotalThickness, Unit)}";

    public string ProfileNote =>
        $"Real index at {MeritRow.ShowWavelength(Comparison.AfterStack.Wavelength, Unit)} (the reference wavelength), "
        + "incident medium on the left, substrate on the right; both graphs to the same scale.";

    // ---- parsing -----------------------------------------------------------------------------

    private string Show(double nm) => Unit == "um" ? (nm / 1000.0).ToString("0.#####", Inv) : nm.ToString("0.###", Inv);

    private static bool Number(string? text, out double v) =>
        double.TryParse(text?.Trim(), NumberStyles.Float, Inv, out v) && double.IsFinite(v);
}

/// <summary>The common scale of a pair of layer graphs: physical thickness (nm) and real index.</summary>
public readonly record struct LayerScale(double Thickness, double IndexMin, double IndexMax);

/// <summary>One bar of a layer graph, in pixels. Layer is from 0; -1 is the incident medium and
/// -2 the substrate.</summary>
public readonly record struct LayerBar(double X, double Y, double Width, double Height, uint Colour,
                                       string Material, double Index, int Layer);

/// <summary>
/// The arithmetic of a layer graph, with no Avalonia in it: each layer a bar as wide as it is thick
/// and as tall as its index, the incident medium in a band of its own on the left and the substrate
/// on the right of where the stack ends. Two graphs given one LayerScale line up: the same
/// thickness is the same width and the same index the same height in both.
/// </summary>
public sealed class LayerGraphModel
{
    /// <summary>The width of the incident and substrate bands, pixels.</summary>
    public double MediumWidth { get; init; } = 22.0;

    private readonly PlotModel _plot;

    public LayerGraphModel()
    {
        _plot = new PlotModel { Left = 56.0 + 22.0, Right = 12.0 + 22.0, Top = 8.0, Bottom = 30.0 };
    }

    public PlotModel Plot => _plot;

    /// <summary>A scale both stacks fit: the thicker one's total, and every index in either, media
    /// included, with room above and below so no bar is flat against an edge.</summary>
    public static LayerScale CommonScale(params StackProfile[] stacks)
    {
        double total = stacks.Max(s => s.TotalThickness);
        var n = stacks.SelectMany(s => s.Layers.Select(l => l.Index).Append(s.IncidentIndex).Append(s.SubstrateIndex))
                      .Where(double.IsFinite).ToArray();
        double lo = n.Length > 0 ? n.Min() : 1.0, hi = n.Length > 0 ? n.Max() : 2.0;
        return new LayerScale(total > 0.0 ? total : 1.0, Math.Max(0.0, lo - 0.25), hi + 0.1);
    }

    /// <summary>One colour per material, the same in both graphs, in order of first appearance.</summary>
    public static IReadOnlyDictionary<string, uint> Colours(params StackProfile[] stacks)
    {
        uint[] palette = [0xFF4E79A7, 0xFFF28E2B, 0xFF59A14F, 0xFFE15759, 0xFF76B7B2, 0xFFEDC948, 0xFFB07AA1, 0xFF9C755F];
        var colours = new Dictionary<string, uint>();
        foreach (var name in stacks.SelectMany(s => s.Layers.Select(l => l.Material)))
            if (!colours.ContainsKey(name)) colours[name] = palette[colours.Count % palette.Length];
        return colours;
    }

    public const uint MediumColour = 0xFFBDBDBD;

    /// <summary>Lays the axes out for this size; call before Bars and the ticks.</summary>
    public void Update(LayerScale scale, double width, double height) =>
        _plot.Update([new PlotSeries("", [0.0, scale.Thickness], [scale.IndexMin, scale.IndexMax], 0)], width, height);

    public IReadOnlyList<AxisTick> XTicks() => _plot.XTicks();
    public IReadOnlyList<AxisTick> YTicks() => _plot.YTicks(4);

    public List<LayerBar> Bars(StackProfile p, IReadOnlyDictionary<string, uint> colours)
    {
        double bottom = _plot.Top + _plot.PlotHeight;
        double Top(double n) => double.IsFinite(n) ? Math.Clamp(_plot.ToPixelY(n), _plot.Top, bottom) : bottom;
        var bars = new List<LayerBar>();
        double left = _plot.Left;
        double y = Top(p.IncidentIndex);
        bars.Add(new LayerBar(left - MediumWidth, y, MediumWidth, bottom - y, MediumColour, p.Incident, p.IncidentIndex, -1));
        double walked = 0.0;
        for (int i = 0; i < p.Layers.Length; ++i)
        {
            var l = p.Layers[i];
            double x0 = _plot.ToPixelX(walked), x1 = _plot.ToPixelX(walked + l.Thickness);
            y = Top(l.Index);
            bars.Add(new LayerBar(x0, y, Math.Max(0.0, x1 - x0), bottom - y,
                                  colours.TryGetValue(l.Material, out var c) ? c : MediumColour, l.Material, l.Index, i));
            walked += l.Thickness;
        }
        double end = _plot.ToPixelX(walked);
        y = Top(p.SubstrateIndex);
        bars.Add(new LayerBar(end, y, MediumWidth, bottom - y, MediumColour, p.Substrate, p.SubstrateIndex, -2));
        return bars;
    }
}
