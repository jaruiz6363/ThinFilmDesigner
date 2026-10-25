// ValidRange.cs — which materials a spectrum reaches outside their valid range, in words.
//
// FOUND WRITING THE GUIDE'S MATERIALS CHAPTER: Si3N4's Palik table ends at 826.6 nm. A spectrum
// plotted to 1000 nm drew a smooth curve on an index held at its last row, and nothing on the
// Spectrum tab said so; only a merit row's help bar did, once that row was clicked. The design
// report already said it. Now the plot's note says it too, and the CLI and MCP get the same lines.

using System.Globalization;

namespace ThinFilm.Api;

public sealed partial class ThinFilmApi
{
    internal static string[] OutsideRange(DesignDocument doc, double[] wavelengthsNm)
    {
        if (wavelengthsNm.Length == 0) return [];
        var inv = CultureInfo.InvariantCulture;
        string Nm(double v) => v.ToString("0.#", inv);
        var said = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var materials = doc.Layers.Select(l => doc.Materials[l.Material]).Append(doc.Incident).Append(doc.Exit);
        foreach (var m in materials)
        {
            if (!seen.Add(m.Name) || m.Model is not { } model) continue;
            double[] outside;
            try { outside = wavelengthsNm.Where(w => model.At(w, m.Name).Extrapolated).ToArray(); }
            catch (FormatException) { continue; }
            if (outside.Length == 0) continue;
            var (lo, hi) = model.Range();
            string range = (lo, hi) switch
            {
                ({ } a, { } b) => $"{Nm(a * 1000.0)}–{Nm(b * 1000.0)} nm",
                ({ } a, null) => $"from {Nm(a * 1000.0)} nm",
                (null, { } b) => $"up to {Nm(b * 1000.0)} nm",
                _ => "none stated",
            };
            string where = outside.Length == 1 || outside.Min() == outside.Max()
                ? $"at {Nm(outside[0])} nm"
                : $"at {Nm(outside.Min())}–{Nm(outside.Max())} nm";
            said.Add($"{m.Name} is outside its valid range ({range}) {where}");
        }
        return said.ToArray();
    }
}
