// PlotCommand.cs — thinfilm plot: the spectrum a design is saved with and opens to.

using System.Globalization;
using System.Text.Json;
using ThinFilm.Api;

namespace ThinFilm.Cli;

public static partial class CommandLine
{
    private static int PlotSettings(ThinFilmApi api, Options o, TextWriter output, TextWriter error)
    {
        if (!OpenFile(api, o, 0, error, out var path, out var id)) return 1;
        var now = api.Describe(id).Value!.Plot ?? PlotDefaults.Plot;
        string? vs = o.String("vs")?.ToLowerInvariant();
        if (vs is not (null or "wavelength" or "angle")) { error.WriteLine("--vs takes wavelength or angle"); return 2; }
        bool vsAngle = vs is null ? now.VsAngle : vs == "angle";
        bool clear = o.Flag("clear");
        bool change = clear || vs is not null || new[] { "from", "to", "steps", "angle", "wavelength", "quantities" }.Any(k => o.String(k) is not null);
        if (change)
        {
            PlotDocument? p = null;
            if (!clear)
            {
                // --from, --to and --steps are the axis being plotted: wavelengths, or angles with --vs angle.
                var q = o.String("quantities")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                p = vsAngle
                    ? now with { VsAngle = true, AngleFrom = o.Double("from") ?? now.AngleFrom, AngleTo = o.Double("to") ?? now.AngleTo,
                                 AngleSteps = (int)(o.Double("steps") ?? now.AngleSteps), Wavelength = o.Double("wavelength") ?? now.Wavelength,
                                 Quantities = q ?? now.Quantities }
                    : now with { VsAngle = false, From = o.Double("from") ?? now.From, To = o.Double("to") ?? now.To,
                                 Steps = (int)(o.Double("steps") ?? now.Steps), Angle = o.Double("angle") ?? now.Angle,
                                 Quantities = q ?? now.Quantities };
            }
            var r = api.SetPlot(id, p);
            if (!r.Ok) return Fail(error, r);
            Save(o, o.Output ?? path, api.Document(id).Value!.ToJson());
        }
        var saved = api.Describe(id).Value!.Plot;
        if (o.Json) { output.WriteLine(JsonSerializer.Serialize(saved, Json)); return 0; }
        output.WriteLine($"  {Say(saved)}");
        if (change) output.WriteLine($"  wrote {o.Output ?? path}");
        return 0;
    }

    /// <summary>A saved plot in one line, or that there is none.</summary>
    private static string Say(PlotDocument? p)
    {
        if (p is null) return "plot: none saved (the Spectrum tab's defaults)";
        var c = CultureInfo.InvariantCulture;
        string q = string.Join(",", p.Quantities);
        return p.VsAngle
            ? string.Create(c, $"plot: {q} against angle, {p.AngleFrom:0.##} to {p.AngleTo:0.##} deg in {p.AngleSteps} steps, at {p.Wavelength:0.##} nm")
            : string.Create(c, $"plot: {q} against wavelength, {p.From:0.##} to {p.To:0.##} nm in {p.Steps} steps, at {p.Angle:0.##} deg");
    }
}
