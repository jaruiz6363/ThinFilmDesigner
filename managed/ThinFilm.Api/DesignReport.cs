// DesignReport.cs — File > Export design report: the design in physical units and the n, k of
// every material it uses, as numbers.
//
// The user (2026-10-22): "a report export file that lists the design in physical units, and the
// index of refraction of all materials that are used. Think of it as something a user would need
// to input into another program where dispersion equations are not available."
//
// So nothing in it is a formula or a unit to convert: thicknesses in nm, whatever unit a layer is
// shown in, and each material as an n, k table over a range and step the user picks (by default
// the design's saved plot range, every 10 nm). One text file; the tables are tab-separated so each
// pastes into a spreadsheet or another program's table as it stands, and every other line starts
// with '#' or '[' so a reader can skip them.

using System.Globalization;
using System.Text;

namespace ThinFilm.Api;

/// <summary>What a report covers. From and To (nm) default to the design's saved plot range, or its
/// wavelength grid's ends; Step (nm) to 10. Path, when given, is where it is written; the text is
/// returned either way.</summary>
public sealed record DesignReportRequest(double? FromNm = null, double? ToNm = null, double? StepNm = null,
                                         string? Path = null);

/// <summary>The report's text, where it was written (or null), its wavelengths, and what a person
/// should know - a material outside its valid range, the incident medium's k.</summary>
public sealed record DesignReport(string Text, string? Path, double[] Wavelengths, string[] Notes);

public sealed partial class ThinFilmApi
{
    /// <summary>The most wavelengths a report lists: a table is for reading into something.</summary>
    public const int ReportMaxWavelengths = 20001;

    public ApiResult<DesignReport> DesignReport(string id, DesignReportRequest request)
    {
        if (!Find(id, out var s, out var fail)) return ApiResult<DesignReport>.Failure(fail!);
        var doc = LayerUnits.ApplyPickups(Current(s));

        double from = request.FromNm ?? doc.Plot?.From ?? doc.Wavelength.Min();
        double to = request.ToNm ?? doc.Plot?.To ?? doc.Wavelength.Max();
        double step = request.StepNm ?? 10.0;
        if (!(from > 0.0) || !(to >= from) || !double.IsFinite(to))
            return ApiResult<DesignReport>.Failure("the range runs from a wavelength above 0 nm to one at or after it");
        if (!(step > 0.0) || !double.IsFinite(step))
            return ApiResult<DesignReport>.Failure("the step must be a positive number of nm");
        var grid = ReportGrid(from, to, step);
        if (grid is null)
            return ApiResult<DesignReport>.Failure(FormattableString.Invariant(
                $"{from:0.###} to {to:0.###} nm every {step:0.###} nm is more than {ReportMaxWavelengths} wavelengths; take a larger step"));

        string text;
        var notes = new List<string>();
        try { text = ReportText(doc, grid, notes); }
        catch (FormatException e) { return ApiResult<DesignReport>.Failure(e.Message); }

        if (request.Path is { } path)
        {
            try
            {
                string temporary = path + ".tmp";
                File.WriteAllText(temporary, text);
                File.Move(temporary, path, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { return ApiResult<DesignReport>.Failure($"could not write {path}: {e.Message}"); }
        }
        return ApiResult<DesignReport>.Success(new DesignReport(text, request.Path, grid, notes.ToArray()));
    }

    /// <summary>From, From + Step, ... up to To, and To itself when the steps miss it - so the
    /// range asked for is the range covered. Null when that is too many.</summary>
    private static double[]? ReportGrid(double from, double to, double step)
    {
        double count = Math.Floor((to - from) / step + 1e-9);
        if (count + 2 > ReportMaxWavelengths) return null;
        var grid = new List<double>();
        for (int i = 0; i <= (int)count; ++i) grid.Add(Math.Round(from + i * step, 9));
        if (to - grid[^1] > 1e-9) grid.Add(to);
        return grid.ToArray();
    }

    private static string ReportText(DesignDocument doc, double[] grid, List<string> notes)
    {
        static string F(double v, string format = "0.######") => v.ToString(format, CultureInfo.InvariantCulture);
        static string G(double v) => v.ToString("G9", CultureInfo.InvariantCulture);
        static IEnumerable<string> Commented(string? text) =>
            (text ?? "").Replace("\r", "").Split('\n').Where(l => l.Trim().Length > 0).Select(l => "#   " + l.Trim());

        var sb = new StringBuilder();
        void Line(string line = "") => sb.Append(line).Append('\n');

        Line($"# Design report: {doc.Name}");
        if (doc.Source is { Length: > 0 } source) { Line("# Source:"); foreach (var l in Commented(source)) Line(l); }
        if (doc.Notes is { Length: > 0 } designNotes) { Line("# Notes:"); foreach (var l in Commented(designNotes)) Line(l); }
        Line("# Written by ThinFilmDesigner. Thicknesses are physical, in nm; wavelengths in nm (vacuum).");
        Line("# Index convention: N = n + ik, k >= 0 absorbing (the same material is n - ik in programs that use that sign).");
        Line($"# Reference wavelength (for QWOT and FWOT): {F(doc.ReferenceWavelength)} nm");
        Line(doc.AngleInAir
            ? "# Angles of incidence are given in air."
            : $"# Angles of incidence are given in the incident medium ({doc.Incident.Name}).");
        Line(doc.BackSurface
            ? $"# The substrate's back surface is included: substrate {F(doc.SubstrateThicknessMm)} mm thick, incoherent, {doc.Incident.Name} behind it."
            : "# The substrate is semi-infinite: its back surface is not included.");
        Line();

        // THE STACK, incident side first, as light meets it.
        Line("[Layers]");
        Line("# Incident medium first, as light meets the stack.");
        Line("layer\tmaterial\tthickness_nm");
        Line($"incident\t{doc.Incident.Name}\t");
        for (int i = 0; i < doc.Layers.Length; ++i)
            Line($"{i + 1}\t{doc.Materials[doc.Layers[i].Material].Name}\t{F(doc.Layers[i].Thickness, "0.####")}");
        Line($"substrate\t{doc.Exit.Name}\t");
        Line($"# {doc.Layers.Length} layer{(doc.Layers.Length == 1 ? "" : "s")}, total {F(doc.Layers.Sum(l => l.Thickness), "0.####")} nm");
        Line();

        // THE MATERIALS: each one a layer uses, in order of first use, then the two media - a
        // medium that is also a layer material with the same model is not listed twice.
        // Same means the same name and the same numbers in this report.
        var tables = new List<(string Title, MaterialDocument M, MaterialModel Model, (double W, (double N, double K, bool Extrapolated) V)[] Rows)>();
        void Add(string title, MaterialDocument m, bool medium)
        {
            var model = m.Model ?? MaterialModel.FromSamples(doc.Wavelength, m.N, m.K);
            var rows = grid.Select(w => (W: w, V: model.At(w, m.Name))).ToArray();
            if (medium && tables.Any(t => t.M.Name == m.Name && t.Rows.Select(r => (r.V.N, r.V.K)).SequenceEqual(rows.Select(r => (r.V.N, r.V.K)))))
                return;
            tables.Add((title, m, model, rows));
        }
        foreach (int m in doc.Layers.Select(l => l.Material).Distinct())
            Add($"Index: {doc.Materials[m].Name}", doc.Materials[m], medium: false);
        Add($"Index: {doc.Incident.Name} (incident medium)", doc.Incident, medium: true);
        Add($"Index: {doc.Exit.Name} (substrate)", doc.Exit, medium: true);

        foreach (var (title, m, model, rows) in tables)
        {
            Line($"[{title}]");
            if (model.Source is { Length: > 0 } src) Line($"# Data: {src}");
            var outside = rows.Where(r => r.V.Extrapolated).Select(r => r.W).ToArray();
            if (outside.Length > 0)
            {
                var (lo, hi) = model.Range();
                string range = $"{(lo is { } a ? F(a * 1000.0, "0.#") : "-")} to {(hi is { } b ? F(b * 1000.0, "0.#") : "-")} nm";
                Line($"# Outside the data's valid range ({range}) at {F(outside.Min(), "0.###")}-{F(outside.Max(), "0.###")} nm: those rows are extrapolated.");
                notes.Add($"{m.Name} is extrapolated outside {range}");
            }
            Line("wavelength_nm\tn\tk");
            foreach (var (w, v) in rows) Line($"{F(w, "0.###")}\t{G(v.N)}\t{G(v.K)}");
            Line();
        }

        if (doc.IncidentKWarning is { } kWarning)
        {
            Line($"# Note: {kWarning}.");
            notes.Add(kWarning);
        }
        return sb.ToString();
    }
}
