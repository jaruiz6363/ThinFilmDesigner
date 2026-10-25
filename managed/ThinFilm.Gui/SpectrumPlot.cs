// SpectrumPlot.cs — a chart control that draws what PlotModel computed and decides nothing.
//
// NO CHARTING PACKAGE, and the reason is the same one that keeps the rest of the tree
// dependency-free: this is a polyline, two axes and some tick labels. A charting library would
// bring a dependency graph larger than the whole managed side of this project to draw a line.
//
// EVERY NUMBER IN HERE CAME FROM PlotModel, which is in a project with no Avalonia in it and a
// test suite that exercises the degenerate cases — a flat spectrum, a single point, a NaN. What is
// left here is DrawLine and DrawText, which is the part a display would be needed to check and is
// also the part that cannot be subtly wrong: a mis-drawn line is visible.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ThinFilm.Gui.Core;

namespace ThinFilm.Gui;

public sealed class SpectrumPlot : Control
{
    /// <summary>The x-axis title, which carries the wavelength unit the design is shown in.</summary>
    public static readonly StyledProperty<string> XTitleProperty =
        AvaloniaProperty.Register<SpectrumPlot, string>(nameof(XTitle), "wavelength (nm)");

    public string XTitle
    {
        get => GetValue(XTitleProperty);
        set => SetValue(XTitleProperty, value);
    }

    /// <summary>The y-axis title: the spectrum's "fraction (1 = 100%)" by default, "n, k" for a
    /// material preview.</summary>
    public static readonly StyledProperty<string> YTitleProperty =
        AvaloniaProperty.Register<SpectrumPlot, string>(nameof(YTitle), "fraction (1 = 100%)");

    /// <summary>What an empty plot says: the spectrum's "open a design", or a preview's own.</summary>
    public string EmptyText { get; set; } = "No spectrum. Open a design.";

    public string YTitle
    {
        get => GetValue(YTitleProperty);
        set => SetValue(YTitleProperty, value);
    }

    public static readonly StyledProperty<IReadOnlyList<PlotSeries>?> SeriesProperty =
        AvaloniaProperty.Register<SpectrumPlot, IReadOnlyList<PlotSeries>?>(nameof(Series));

    public IReadOnlyList<PlotSeries>? Series
    {
        get => GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    // A wider left margin than PlotModel's default, to make room for the y-axis title beside the
    // tick labels.
    private readonly PlotModel _model = new() { Left = 72.0 };

    static SpectrumPlot()
    {
        AffectsRender<SpectrumPlot>(SeriesProperty, XTitleProperty, YTitleProperty);
        AffectsMeasure<SpectrumPlot>(SeriesProperty);
    }

    public override void Render(DrawingContext context)
    {
        var series = Series ?? System.Array.Empty<PlotSeries>();
        double w = Bounds.Width, h = Bounds.Height;
        if (w < 40 || h < 40) return;

        var background = new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFA));
        context.FillRectangle(background, new Rect(0, 0, w, h));

        if (series.Count == 0)
        {
            Text(context, EmptyText, new Point(16, 16), 0xFF808080);
            return;
        }

        _model.Update(series, w, h);

        var axisPen = new Pen(new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60)), 1.0);
        var gridPen = new Pen(new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)), 1.0);

        double left = _model.Left, top = _model.Top;
        double right = left + _model.PlotWidth, bottom = top + _model.PlotHeight;

        foreach (var t in _model.YTicks())
        {
            context.DrawLine(gridPen, new Point(left, t.Position), new Point(right, t.Position));
            Text(context, t.Label, new Point(20, t.Position - 8), 0xFF404040);
        }

        foreach (var t in _model.XTicks())
        {
            context.DrawLine(gridPen, new Point(t.Position, top), new Point(t.Position, bottom));
            Text(context, t.Label, new Point(t.Position - 16, bottom + 6), 0xFF404040);
        }

        context.DrawLine(axisPen, new Point(left, top), new Point(left, bottom));
        context.DrawLine(axisPen, new Point(left, bottom), new Point(right, bottom));

        // The axes after the first, on the right: a line, ticks and labels outside it, and the
        // title turned beside them. An axis with one colour of line on it takes that colour, so
        // the line and its scale read together.
        for (int a = 1; a < _model.Axes.Count; ++a)
        {
            double ax = _model.AxisPosition(a);
            uint colour = AxisColour(series, a);
            var pen = new Pen(new SolidColorBrush(Color.FromUInt32(colour)), 1.0);
            context.DrawLine(pen, new Point(ax, top), new Point(ax, bottom));
            foreach (var t in _model.YTicks(axis: a))
            {
                context.DrawLine(pen, new Point(ax, t.Position), new Point(ax + 4, t.Position));
                Text(context, t.Label, new Point(ax + 6, t.Position - 8), colour);
            }
            Vertical(context, _model.Axes[a].Title ?? "", new Point(ax + _model.AxisGap - 12, (top + bottom) / 2), colour);
        }

        foreach (var s in series)
        {
            var runs = _model.Segments(s).Where(r => r.Count >= 2).ToList();
            if (runs.Count == 0) continue;

            var pen = new Pen(new SolidColorBrush(Color.FromUInt32(s.Colour)), 1.8,
                              s.Dashed ? new DashStyle([4.0, 3.0], 0.0) : null);
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
                foreach (var points in runs)
                {
                    g.BeginFigure(new Point(points[0].X, points[0].Y), false);
                    for (int i = 1; i < points.Count; ++i)
                        g.LineTo(new Point(points[i].X, points[i].Y));
                    g.EndFigure(false);
                }
            context.DrawGeometry(null, pen, geometry);
        }

        // The legend, placed inside the plot so it cannot be cut off by a narrow window.
        // As wide as its longest name, so "R (avg s,p) before" fits as well as "n"; a dashed series
        // has a short dashed sample beside its name.
        double widest = series.Max(s => Formatted(s.Name, 0).Width);
        double x = right - widest - 30, y = top + 4;
        foreach (var s in series)
        {
            var sample = new Pen(new SolidColorBrush(Color.FromUInt32(s.Colour)), 1.8,
                                 s.Dashed ? new DashStyle([4.0, 3.0], 0.0) : null);
            context.DrawLine(sample, new Point(x, y + 8), new Point(x + 20, y + 8));
            Text(context, s.Name, new Point(x + 24, y), s.Colour);
            y += 16;
        }

        Text(context, XTitle, new Point((left + right) / 2 - 44, bottom + 20), 0xFF404040);

        // The y-axis title, turned to read bottom to top. The quantities are fractions, and a
        // bare 0.004 on an axis is read as 0.004% as often as 0.4%.
        // A series that names its axis titles it; otherwise the control's own YTitle does.
        Vertical(context, _model.Axes[0].Title ?? YTitle, new Point(8, (top + bottom) / 2), 0xFF404040);
    }

    /// <summary>The colour of the lines on an axis when they all share one, grey otherwise.</summary>
    private uint AxisColour(IReadOnlyList<PlotSeries> series, int axis)
    {
        var colours = series.Where(s => _model.AxisOf(s) == axis).Select(s => s.Colour).Distinct().ToList();
        return colours.Count == 1 ? colours[0] : 0xFF404040;
    }

    /// <summary>Text turned to read bottom to top, centred on a point.</summary>
    private static void Vertical(DrawingContext context, string text, Point centre, uint colour)
    {
        double width = Formatted(text, colour).Width;
        using (context.PushTransform(Matrix.CreateTranslation(-centre.X, -centre.Y)
                                     * Matrix.CreateRotation(-Math.PI / 2)
                                     * Matrix.CreateTranslation(centre.X, centre.Y)))
            Text(context, text, new Point(centre.X - width / 2, centre.Y - 7), colour);
    }

    private static void Text(DrawingContext context, string text, Point at, uint colour)
    {
        var formatted = new FormattedText(
            text, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, Typeface.Default, 11,
            new SolidColorBrush(Color.FromUInt32(colour)));
        context.DrawText(formatted, at);
    }

    private static FormattedText Formatted(string text, uint colour) => new(
        text, System.Globalization.CultureInfo.InvariantCulture,
        FlowDirection.LeftToRight, Typeface.Default, 11, new SolidColorBrush(Color.FromUInt32(colour)));
}
