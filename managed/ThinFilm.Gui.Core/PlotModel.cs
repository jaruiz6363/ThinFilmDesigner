// PlotModel.cs — the arithmetic of a plot, with no Avalonia in it.
//
// WHY THIS IS A SEPARATE PROJECT. A GUI cannot be tested without a display, but almost nothing
// that goes wrong in a plot is a drawing problem: it is an axis that does not include the data, a
// tick at 0.30000000000000004, a mapping that puts the last point one pixel off the right edge, an
// empty series that divides by a zero range. All of that is arithmetic, and arithmetic can be
// tested. So the arithmetic lives here and is tested; the Avalonia control does nothing but walk
// the geometry this produces and call DrawLine.
//
// THE DEGENERATE CASES ARE THE ONES THAT MATTER. A spectrum that is perfectly flat — an AR coating
// that actually worked — has zero range on its y axis, and the naive mapping divides by it. A
// design with one wavelength has zero range on x. An operand that produced a NaN should not take
// the window down. Each of those has a test because each of them is a real design somebody will
// open.
//
// SEVERAL Y AXES. Quantities in different units (a reflectance, an optical density, a phase in
// degrees) can be plotted together, each against its own y axis: the first on the left, the
// others on the right, each a gap further out. A series names its axis by title.

namespace ThinFilm.Gui.Core;

public readonly record struct PlotPoint(double X, double Y);

/// <summary>Dashed: drawn as a dashed line, for the "before" of a before-and-after plot. Axis: the
/// title of the y axis it is drawn against; series with different titles get different axes, the
/// first on the left and the others on the right. Null is the plot's one default axis.</summary>
public sealed record PlotSeries(string Name, double[] X, double[] Y, uint Colour, bool Dashed = false,
                                string? Axis = null);

/// <summary>One y axis: its title (null for the default axis) and its range, rounded to ticks.</summary>
public sealed record YAxis(string? Title, double Min, double Max);

public readonly record struct AxisTick(double Value, double Position, string Label);

/// <summary>
/// A plot's arithmetic: data bounds, ticks, and the mapping from data to pixels. Pixels are
/// top-left origin, which is what every drawing API uses and the opposite of what the data means,
/// so the y mapping flips and that flip is tested.
/// </summary>
public sealed class PlotModel
{
    public double Left { get; init; } = 56.0;
    public double Right { get; init; } = 12.0;
    public double Top { get; init; } = 12.0;
    public double Bottom { get; init; } = 32.0;
    /// <summary>The room each y axis after the first takes on the right, for its ticks and title.</summary>
    public double AxisGap { get; init; } = 64.0;

    public double Width { get; private set; }
    public double Height { get; private set; }

    public double XMin { get; private set; }
    public double XMax { get; private set; }

    /// <summary>The y axes in the order their series first appear; there is always at least one.</summary>
    public IReadOnlyList<YAxis> Axes { get; private set; } = [new YAxis(null, 0.0, 1.0)];

    /// <summary>The first y axis, the one on the left.</summary>
    public double YMin => Axes[0].Min;
    public double YMax => Axes[0].Max;

    public IReadOnlyList<PlotSeries> Series { get; private set; } = System.Array.Empty<PlotSeries>();

    public double PlotWidth => Math.Max(1.0, Width - Left - Right - AxisGap * (Axes.Count - 1));
    public double PlotHeight => Math.Max(1.0, Height - Top - Bottom);

    public void Update(IReadOnlyList<PlotSeries> series, double width, double height)
    {
        Series = series;
        Width = width;
        Height = height;

        var titles = series.Select(s => s.Axis).Distinct().ToList();
        if (titles.Count == 0) titles.Add(null);
        var ylo = titles.Select(_ => double.MaxValue).ToArray();
        var yhi = titles.Select(_ => double.MinValue).ToArray();
        double xlo = double.MaxValue, xhi = double.MinValue;

        foreach (var s in series)
        {
            int a = titles.IndexOf(s.Axis);
            int n = Math.Min(s.X.Length, s.Y.Length);
            for (int i = 0; i < n; ++i)
            {
                // A NaN or an infinity in one point must not take the axis with it. They happen:
                // an optical density is a logarithm and a perfect reflector is an infinite one.
                if (!double.IsFinite(s.X[i]) || !double.IsFinite(s.Y[i])) continue;
                xlo = Math.Min(xlo, s.X[i]); xhi = Math.Max(xhi, s.X[i]);
                ylo[a] = Math.Min(ylo[a], s.Y[i]); yhi[a] = Math.Max(yhi[a], s.Y[i]);
            }
        }

        if (xlo > xhi) { xlo = 0.0; xhi = 1.0; }      // nothing finite at all

        // A FLAT SERIES IS NOT AN ERROR, it is an AR coating that worked. Give it a band around
        // the value rather than a zero range, so it draws as a line through the middle instead of
        // dividing by nothing.
        if (xhi - xlo <= 0.0) { double pad = Math.Max(Math.Abs(xlo) * 0.05, 0.5); xlo -= pad; xhi += pad; }

        XMin = xlo; XMax = xhi;
        Axes = titles.Select((t, a) => Range(t, ylo[a], yhi[a])).ToArray();
    }

    /// <summary>A y range with the flat case padded, rounded outward to a tick so the top line is
    /// a number somebody can read.</summary>
    private static YAxis Range(string? title, double lo, double hi)
    {
        if (lo > hi) { lo = 0.0; hi = 1.0; }
        if (hi - lo <= 0.0) { double pad = Math.Max(Math.Abs(lo) * 0.05, 0.05); lo -= pad; hi += pad; }
        double step = NiceStep(lo, hi, 5);
        double min = Math.Floor(lo / step) * step, max = Math.Ceiling(hi / step) * step;
        if (max - min <= 0.0) max = min + step;
        return new YAxis(title, min, max);
    }

    /// <summary>The axis a series is drawn against.</summary>
    public int AxisOf(PlotSeries s)
    {
        for (int a = 0; a < Axes.Count; ++a)
            if (Axes[a].Title == s.Axis) return a;
        return 0;
    }

    /// <summary>Where an axis's line is: the first at the plot's left edge, the second at its right
    /// edge, and each after that a gap further right.</summary>
    public double AxisPosition(int axis) => axis == 0 ? Left : Left + PlotWidth + AxisGap * (axis - 1);

    public double ToPixelX(double x) => Left + (x - XMin) / (XMax - XMin) * PlotWidth;

    /// <summary>Flipped: pixels grow downward and data grows upward.</summary>
    public double ToPixelY(double y, int axis = 0) =>
        Top + (Axes[axis].Max - y) / (Axes[axis].Max - Axes[axis].Min) * PlotHeight;

    public IReadOnlyList<AxisTick> XTicks(int target = 6) => Ticks(XMin, XMax, target, null);
    public IReadOnlyList<AxisTick> YTicks(int target = 5, int axis = 0) =>
        Ticks(Axes[axis].Min, Axes[axis].Max, target, axis);

    /// <summary>Ticks along x when axis is null, along that y axis otherwise.</summary>
    private List<AxisTick> Ticks(double lo, double hi, int target, int? axis)
    {
        var ticks = new List<AxisTick>();
        double step = NiceStep(lo, hi, target);
        if (step <= 0.0) return ticks;

        // Start at the first multiple of the step at or above lo, so every tick is a round number.
        double first = Math.Ceiling(lo / step) * step;
        for (double v = first; v <= hi + step * 1e-9; v += step)
        {
            double snapped = Math.Abs(v) < step * 1e-9 ? 0.0 : v;   // kill -0 and 1e-17
            ticks.Add(new AxisTick(
                snapped,
                axis is { } a ? ToPixelY(snapped, a) : ToPixelX(snapped),
                Label(snapped, step)));
            if (ticks.Count > 64) break;       // a guard, never reached with a sane step
        }
        return ticks;
    }

    /// <summary>
    /// A step from the 1, 2, 2.5, 5 family, which is what makes ticks read as round numbers
    /// instead of 0.30000000000000004.
    /// </summary>
    public static double NiceStep(double lo, double hi, int target)
    {
        double range = hi - lo;
        if (!double.IsFinite(range) || range <= 0.0 || target < 1) return 0.0;

        double rough = range / target;
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(rough)));
        double normalised = rough / magnitude;

        double nice = normalised switch
        {
            <= 1.0 => 1.0,
            <= 2.0 => 2.0,
            <= 2.5 => 2.5,
            <= 5.0 => 5.0,
            _ => 10.0,
        };
        return nice * magnitude;
    }

    /// <summary>Enough decimals for the step and no more, so a 0.001 axis is not printed as 0.</summary>
    private static string Label(double value, double step)
    {
        int decimals = Math.Max(0, (int)Math.Ceiling(-Math.Log10(step)) + 1);
        if (decimals > 6 || Math.Abs(value) >= 1e5)
            return value.ToString("G3", System.Globalization.CultureInfo.InvariantCulture);
        return value.ToString("F" + decimals, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The polyline for one series, already in pixels, with non-finite points dropped.</summary>
    public List<PlotPoint> Polyline(PlotSeries s)
    {
        var points = new List<PlotPoint>();
        int axis = AxisOf(s);
        int n = Math.Min(s.X.Length, s.Y.Length);
        for (int i = 0; i < n; ++i)
        {
            if (!double.IsFinite(s.X[i]) || !double.IsFinite(s.Y[i])) continue;
            points.Add(new PlotPoint(ToPixelX(s.X[i]), ToPixelY(s.Y[i], axis)));
        }
        return points;
    }

    /// <summary>The series as separate runs of finite points: a NaN breaks the line, so an angle
    /// that cannot be reached shows as a gap rather than a straight line across it.</summary>
    public List<List<PlotPoint>> Segments(PlotSeries s)
    {
        var runs = new List<List<PlotPoint>>();
        List<PlotPoint>? run = null;
        int axis = AxisOf(s);
        int n = Math.Min(s.X.Length, s.Y.Length);
        for (int i = 0; i < n; ++i)
        {
            if (!double.IsFinite(s.X[i]) || !double.IsFinite(s.Y[i])) { run = null; continue; }
            if (run is null) runs.Add(run = new List<PlotPoint>());
            run.Add(new PlotPoint(ToPixelX(s.X[i]), ToPixelY(s.Y[i], axis)));
        }
        return runs;
    }
}
