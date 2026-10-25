// LayerGraph.cs — a stack drawn as its index profile: each layer a bar as wide as it is thick and
// as tall as its index. Draws what LayerGraphModel (Gui.Core) computed and decides nothing, like
// SpectrumPlot. Two of these given the same Scale line up, which is how the optimisation window
// shows a run's design before and after.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ThinFilm.Api;
using ThinFilm.Gui.Core;

namespace ThinFilm.Gui;

public sealed class LayerGraph : Control
{
    public static readonly StyledProperty<StackProfile?> ProfileProperty =
        AvaloniaProperty.Register<LayerGraph, StackProfile?>(nameof(Profile));

    public StackProfile? Profile
    {
        get => GetValue(ProfileProperty);
        set => SetValue(ProfileProperty, value);
    }

    /// <summary>The scale this graph shares with its pair.</summary>
    public LayerScale Scale { get; set; }

    public IReadOnlyDictionary<string, uint> Colours { get; set; } = new Dictionary<string, uint>();

    /// <summary>Above the graph: "Before - 12 layers, 1,234 nm".</summary>
    public string Heading { get; set; } = "";

    /// <summary>Physical thicknesses are in nm or um, as the design shows its wavelengths.</summary>
    public string LengthUnit { get; set; } = "nm";

    private readonly LayerGraphModel _model = new();

    static LayerGraph()
    {
        AffectsRender<LayerGraph>(ProfileProperty);
    }

    public override void Render(DrawingContext context)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w < 80 || h < 60) return;
        context.FillRectangle(new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFA)), new Rect(0, 0, w, h));
        if (Profile is not { } profile) return;

        // The heading takes the top line; the model lays the axes out below it.
        const double heading = 18.0;
        using (context.PushTransform(Matrix.CreateTranslation(0, heading)))
        {
            _model.Update(Scale, w, h - heading);
            var plot = _model.Plot;
            double left = plot.Left, top = plot.Top, right = left + plot.PlotWidth, bottom = top + plot.PlotHeight;
            var gridPen = new Pen(new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)), 1.0);
            var axisPen = new Pen(new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60)), 1.0);

            foreach (var t in _model.YTicks())
            {
                context.DrawLine(gridPen, new Point(left - _model.MediumWidth, t.Position),
                                 new Point(right + _model.MediumWidth, t.Position));
                Text(context, t.Label, new Point(16, t.Position - 8), 0xFF404040);
            }
            // A thickness axis in the design's unit: the model works in nm.
            double factor = LengthUnit == "um" ? 1000.0 : 1.0;
            foreach (var t in _model.XTicks())
            {
                string label = factor == 1.0 ? t.Label
                    : (t.Value / factor).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                Text(context, label, new Point(t.Position - 14, bottom + 4), 0xFF404040);
            }

            var edge = new Pen(new SolidColorBrush(Color.FromArgb(0x60, 0, 0, 0)), 0.5);
            foreach (var b in _model.Bars(profile, Colours))
            {
                var rect = new Rect(b.X, b.Y, Math.Max(0.5, b.Width), b.Height);
                IBrush fill = b.Layer < 0
                    ? new SolidColorBrush(Color.FromUInt32(LayerGraphModel.MediumColour))
                    : new SolidColorBrush(Color.FromUInt32(b.Colour));
                context.FillRectangle(fill, rect);
                context.DrawRectangle(null, edge, rect);
            }
            context.DrawLine(axisPen, new Point(left - _model.MediumWidth, bottom), new Point(right + _model.MediumWidth, bottom));
            context.DrawLine(axisPen, new Point(left - _model.MediumWidth, top), new Point(left - _model.MediumWidth, bottom));

            Text(context, $"thickness ({(LengthUnit == "um" ? "µm" : "nm")}) from the incident side",
                 new Point((left + right) / 2 - 90, bottom + 16), 0xFF404040);
            var centre = new Point(6, (top + bottom) / 2);
            using (context.PushTransform(Matrix.CreateTranslation(-centre.X, -centre.Y)
                                         * Matrix.CreateRotation(-Math.PI / 2)
                                         * Matrix.CreateTranslation(centre.X, centre.Y)))
                Text(context, "index n", new Point(centre.X - 20, centre.Y - 7), 0xFF404040);
        }

        Text(context, Heading, new Point(8, 2), 0xFF202020, bold: true);
        // The legend on the heading's line, right-aligned: each material with its index.
        var items = Colours.Select(kv => (kv.Value, $"{kv.Key} {IndexOf(profile, kv.Key)}")).ToList();
        double x = w - 8;
        for (int i = items.Count - 1; i >= 0; --i)
        {
            var (colour, label) = items[i];
            double width = Formatted(label, 0xFF404040, false).Width;
            x -= width;
            Text(context, label, new Point(x, 2), 0xFF404040);
            x -= 14;
            context.FillRectangle(new SolidColorBrush(Color.FromUInt32(colour)), new Rect(x, 5, 10, 10));
            x -= 12;
        }
    }

    private static string IndexOf(StackProfile p, string material)
    {
        var l = p.Layers.FirstOrDefault(l => l.Material == material);
        return l is null || !double.IsFinite(l.Index) ? ""
            : l.Index.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void Text(DrawingContext context, string text, Point at, uint colour, bool bold = false) =>
        context.DrawText(Formatted(text, colour, bold), at);

    private static FormattedText Formatted(string text, uint colour, bool bold) => new(
        text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
        new Typeface(FontFamily.Default, FontStyle.Normal, bold ? FontWeight.SemiBold : FontWeight.Normal),
        11, new SolidColorBrush(Color.FromUInt32(colour)));
}
