// MeritFiles.cs — a merit function on its own: saved, opened into another design, exported as CSV.
//
// The user (2026-10-19): "Under merit function table we need a way to settings only: save a merit
// function table, save as a merit function table, and open a merit function table. We also need a
// way to export a merit function table as CSV with values exported."
//
// A MERIT FILE IS THE ROWS, NOT THE DESIGN. Each row carries what it reads as (wavelength nm, angle
// degrees) pairs rather than indexes into a design's point list, so a file saved from one design
// opens into any other: the design's grid and points are rebuilt from the rows, as an edit in the
// table rebuilds them (Documents.WithOperands). Thicknesses and layers are never in it.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ThinFilm.Api;

/// <summary>Where a merit row reads: a wavelength in nm and an angle of incidence in degrees.</summary>
public sealed record MeritPoint(double Wavelength, double AngleDegrees);

/// <summary>One row of a merit file: the operand as the design holds it, without its point indexes,
/// and the points it reads.</summary>
public sealed record MeritFileRow(OperandDocument Operand, MeritPoint[] At);

/// <summary>A merit function saved on its own (.tfm).</summary>
public sealed record MeritFileDocument
{
    public const string FileKind = "ThinFilm merit function";
    public string Kind { get; init; } = FileKind;
    public int Version { get; init; } = 1;
    public required MeritFileRow[] Rows { get; init; }
}

public sealed partial class ThinFilmApi
{
    private static readonly JsonSerializerOptions MeritJson = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The design's merit function as a merit file (JSON).</summary>
    public ApiResult<string> SaveMeritFunction(string id)
    {
        if (!Find(id, out var s, out var fail)) return ApiResult<string>.Failure(fail!);
        var doc = Current(s);
        var rows = doc.Operands.Select(o => new MeritFileRow(
            o with { Points = null },
            Documents.PointsOf(doc, o).Select(p => new MeritPoint(p.Wl, p.Angle)).ToArray())).ToArray();
        return ApiResult<string>.Success(JsonSerializer.Serialize(new MeritFileDocument { Rows = rows }, MeritJson));
    }

    /// <summary>A merit file's rows in place of the design's whole merit function, or with append
    /// after the rows it has. Answers with how many rows came from the file; a file that is not a
    /// merit function, or a row the engine refuses, leaves the design as it was.</summary>
    public ApiResult<MeritEditReport> OpenMeritFunction(string id, string json, bool append = false)
    {
        MeritFileDocument? file;
        try { file = JsonSerializer.Deserialize<MeritFileDocument>(json, MeritJson); }
        catch (JsonException e) { return ApiResult<MeritEditReport>.Failure($"not a merit function file: {e.Message}"); }
        if (file is null || file.Kind != MeritFileDocument.FileKind || file.Rows is null)
            return ApiResult<MeritEditReport>.Failure(
                "not a merit function file (a design is opened with File > Open, not here)");
        if (file.Version > 1)
            return ApiResult<MeritEditReport>.Failure($"this merit function file is version {file.Version}; this program reads version 1");
        return Bulk(id, rows =>
        {
            if (!append) rows.Clear();
            foreach (var r in file.Rows)
                rows.Add((r.Operand with { Points = null }, r.At.Select(p => (p.Wavelength, p.AngleDegrees)).ToArray()));
            return (file.Rows.Length, null);
        });
    }

    /// <summary>
    /// The merit table as CSV, one line per row with the columns the table shows and the value,
    /// residual and flags the evaluation gives. Wavelengths and layer limits are in the design's
    /// wavelength unit, as the table shows them; numbers are round-trip exact. A cell the row does
    /// not use is empty. A design that cannot be evaluated exports NaN values.
    /// </summary>
    public ApiResult<string> ExportMeritCsv(string id)
    {
        if (!Find(id, out var s, out var fail)) return ApiResult<string>.Failure(fail!);
        var doc = Current(s);
        var e = Evaluate(s);
        if (!e.Ok) return ApiResult<string>.Failure(e.Error!, e.Status);
        var report = e.Value!;

        var inv = CultureInfo.InvariantCulture;
        bool um = doc.WavelengthUnit == "um";
        string u = um ? "um" : "nm";
        string N(double v) => v.ToString("R", inv);
        string L(double nm) => N(um ? nm / 1000.0 : nm);
        static string Q(string text) =>
            text.IndexOfAny([',', '"', '\n']) >= 0 ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;

        var sb = new StringBuilder();
        sb.AppendLine($"#,Code,Wv ({u}),Wv2 ({u}),Pts,AOI (deg),Mode,Target,Target 2,Limit ({u}),Weight,Value,Residual,Flags");
        for (int i = 0; i < doc.Operands.Length; ++i)
        {
            var o = doc.Operands[i];
            var at = Documents.PointsOf(doc, o);
            var wl = at.Select(a => a.Wl).ToArray();
            string mode = o.Comparison.ToUpperInvariant() switch
            {
                "EQUAL" or "EQ" => "Target", "GE" => "Min", "LE" => "Max", "RANGE" => "Range", _ => "Report",
            };
            var r = report.Operands[i];
            sb.Append(i + 1).Append(',')
              .Append(Q(o.Code)).Append(',')
              .Append(wl.Length > 0 ? L(wl.Min()) : "").Append(',')
              .Append(wl.Length > 1 ? L(wl.Max()) : "").Append(',')
              .Append(wl.Length > 1 ? wl.Length.ToString(inv) : "").Append(',')
              .Append(at.Length > 0 ? N(at[0].Angle) : "").Append(',')
              .Append(mode).Append(',')
              .Append(N(o.Target)).Append(',')
              .Append(mode == "Range" ? N(o.TargetHi) : "").Append(',')
              .Append(o.Threshold != 0.0 ? L(o.Threshold) : "").Append(',')
              .Append(N(o.Weight)).Append(',')
              .Append(N(r.Value)).Append(',')
              .Append(N(r.Residual)).Append(',')
              .Append(Q(string.Join(" ", r.Flags)))
              .AppendLine();
        }
        return ApiResult<string>.Success(sb.ToString());
    }
}
