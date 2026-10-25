// CoefficientsForm.cs — a material's dispersion, as fields a person fills in.
//
// "Expecting the user to edit a string will not work. No user will do it." (The user, of the
// space-separated coefficient box X1.22 had.) So each model has its own fields, labelled with the
// symbol the formula uses: Sellmeier's terms as rows of B and C, Cauchy's A, B and C, a constant's
// n and k, a table as rows of λ, n and k. The dialog shows these; everything that can be wrong
// with them is checked here, where a test reaches it, by evaluating the model they make.
//
// LOAD FROM LIBRARY fills the fields from a database material - the user's other request: "the
// user needs to be able to load the coefficients from a library." The numbers come in; the design
// still holds its own copy, and the database is never written from here.
//
// A TABLE CAN BE IMPORTED from a text or CSV file of λ n k rows, in µm or nm as the person says -
// never guessed from the size of the numbers, because a guess that is wrong once in a hundred
// files is a design built on an index a thousand times off.

using System.Globalization;
using ThinFilm.Api;

namespace ThinFilm.Gui.Core;

/// <summary>A table row as the grid edits it: text, so a half-typed number is not lost.</summary>
public sealed class TableRowEntry
{
    public string Wavelength { get; set; } = "";
    public string N { get; set; } = "";
    public string K { get; set; } = "0";
}

public sealed class CoefficientsForm
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>How many Sellmeier terms the dialog offers. Glass catalogues use three; six covers
    /// every published fit ThinFilm has met.</summary>
    public const int SellmeierTerms = 6;

    public static readonly string[] Kinds = ["SELLMEIER", "CAUCHY", "TABLE", "CONSTANT"];

    public CoefficientsForm(string material, MaterialModel model, MaterialDatabase database)
    {
        Material = material;
        Database = database;
        Load(model);
    }

    public string Material { get; }

    /// <summary>A material that does not exist yet (Edit > Materials, New material): the dialog asks
    /// for its name, in <see cref="NewName"/>.</summary>
    public bool IsNew { get; init; }
    public string NewName { get; set; } = "";
    public MaterialDatabase Database { get; }

    /// <summary>"nm" or "um": what the preview's axis is in. The coefficients, the table and the
    /// valid range stay in µm, because the formulas are written in µm.</summary>
    public string WavelengthUnit { get; init; } = "nm";

    public string Kind { get; set; } = "SELLMEIER";

    public string[] SellmeierB { get; } = new string[SellmeierTerms];
    public string[] SellmeierC { get; } = new string[SellmeierTerms];
    public string CauchyA { get; set; } = "";
    public string CauchyB { get; set; } = "";
    public string CauchyC { get; set; } = "";
    public string ConstantN { get; set; } = "";
    public string ConstantK { get; set; } = "0";
    public List<TableRowEntry> Table { get; } = new();

    public string RangeMin { get; set; } = "";
    public string RangeMax { get; set; } = "";
    public string Source { get; set; } = "";

    /// <summary>The formula, for the dialog to show above the fields.</summary>
    public string Formula => Kind switch
    {
        "SELLMEIER" => "n² = 1 + Σ Bᵢ λ² / (λ² − Cᵢ)      λ in µm, Cᵢ in µm²",
        "CAUCHY" => "n = A + B / λ² + C / λ⁴      λ in µm",
        "TABLE" => "rows of λ (µm), n, k - interpolated linearly between rows",
        "CONSTANT" => "the same n and k at every wavelength",
        _ => "",
    };

    // ---- in and out --------------------------------------------------------------------------

    /// <summary>The fields filled from a model - the material's own, or one loaded from the library.</summary>
    public void Load(MaterialModel m)
    {
        Kind = m.Kind.ToUpperInvariant() is var k && Kinds.Contains(k) ? k : "SELLMEIER";
        Array.Fill(SellmeierB, ""); Array.Fill(SellmeierC, "");
        CauchyA = CauchyB = CauchyC = ""; ConstantN = ""; ConstantK = "0";
        Table.Clear();
        var c = m.Coefficients;
        switch (Kind)
        {
            case "SELLMEIER":
                for (int i = 0; i + 1 < c.Length && i / 2 < SellmeierTerms; i += 2)
                    (SellmeierB[i / 2], SellmeierC[i / 2]) = (Show(c[i]), Show(c[i + 1]));
                break;
            case "CAUCHY":
                if (c.Length > 0) CauchyA = Show(c[0]);
                if (c.Length > 1) CauchyB = Show(c[1]);
                if (c.Length > 2) CauchyC = Show(c[2]);
                break;
            case "CONSTANT":
                if (c.Length > 0) ConstantN = Show(c[0]);
                if (c.Length > 1) ConstantK = Show(c[1]);
                break;
            case "TABLE":
                foreach (var r in m.Table ?? [])
                    Table.Add(new TableRowEntry { Wavelength = Show(r[0]), N = Show(r[1]), K = Show(r[2]) });
                break;
        }
        RangeMin = m.MinMicrons is { } lo ? Show(lo) : "";
        RangeMax = m.MaxMicrons is { } hi ? Show(hi) : "";
        Source = m.Source ?? "";
    }

    /// <summary>The model the fields describe, or null with the reason - checked by evaluating it.</summary>
    public MaterialModel? ToModel(out string? problem)
    {
        problem = null;
        try
        {
            double[] coefficients = [];
            double[][]? table = null;
            switch (Kind)
            {
                case "SELLMEIER":
                    var terms = new List<double>();
                    for (int i = 0; i < SellmeierTerms; ++i)
                    {
                        bool hasB = !string.IsNullOrWhiteSpace(SellmeierB[i]);
                        bool hasC = !string.IsNullOrWhiteSpace(SellmeierC[i]);
                        if (!hasB && !hasC) continue;
                        if (hasB != hasC) throw new FormatException($"Sellmeier term {i + 1} needs both B{i + 1} and C{i + 1}");
                        terms.Add(Number(SellmeierB[i], $"B{i + 1}"));
                        terms.Add(Number(SellmeierC[i], $"C{i + 1}"));
                    }
                    if (terms.Count == 0) throw new FormatException("a Sellmeier model needs at least one term");
                    coefficients = terms.ToArray();
                    break;
                case "CAUCHY":
                    coefficients = [Number(CauchyA, "A"), Optional(CauchyB, "B"), Optional(CauchyC, "C")];
                    break;
                case "CONSTANT":
                    coefficients = [Number(ConstantN, "n"), Optional(ConstantK, "k")];
                    break;
                case "TABLE":
                    var rows = Table.Where(r => !(string.IsNullOrWhiteSpace(r.Wavelength)
                                                  && string.IsNullOrWhiteSpace(r.N)))
                                    .Select((r, i) => new[]
                                    {
                                        Number(r.Wavelength, $"row {i + 1}, λ"), Number(r.N, $"row {i + 1}, n"),
                                        Optional(r.K, $"row {i + 1}, k"),
                                    }).ToArray();
                    if (rows.Length == 0) throw new FormatException("a table needs at least one row");
                    for (int i = 1; i < rows.Length; ++i)
                        if (!(rows[i][0] > rows[i - 1][0]))
                            throw new FormatException($"table wavelengths must increase (row {i + 1})");
                    table = rows;
                    break;
                default:
                    throw new FormatException($"'{Kind}' is not a model this dialog edits");
            }

            double? lo = string.IsNullOrWhiteSpace(RangeMin) ? null : Number(RangeMin, "the low end of the range");
            double? hi = string.IsNullOrWhiteSpace(RangeMax) ? null : Number(RangeMax, "the high end of the range");
            if (lo is { } a && hi is { } b && !(a < b)) throw new FormatException("the range must run from low to high");

            var model = new MaterialModel
            {
                Kind = Kind, Coefficients = coefficients, Table = table,
                MinMicrons = lo, MaxMicrons = hi,
                Source = string.IsNullOrWhiteSpace(Source) ? null : Source.Trim(),
            };
            var (pl, ph) = model.Range();
            model.At(1000.0 * (pl is { } x && ph is { } y ? (x + y) / 2 : pl ?? ph ?? 0.55), Material);
            return model;
        }
        catch (FormatException e) { problem = e.Message; return null; }
    }

    // ---- the library -------------------------------------------------------------------------

    /// <summary>Database materials whose coefficients can be loaded - all of them; loading one of
    /// another kind switches the fields to that kind.</summary>
    public IReadOnlyList<NamedMaterial> Library => Database.Materials;

    public string? LoadFromLibrary(string name)
    {
        var m = Database.Find(name);
        if (m is null) return $"'{name}' is not in the material database";
        Load(m.Model);
        return null;
    }

    // ---- importing a table -------------------------------------------------------------------

    /// <summary>
    /// Rows of λ n [k] from text: commas, tabs or spaces between the numbers, lines that are not
    /// numbers (a header, a comment) skipped and counted. <paramref name="unit"/> says what λ is
    /// in, "um" or "nm"; it is stated by the person, never inferred. Returns what it did, or why
    /// it did nothing.
    /// </summary>
    public string ImportTable(string text, string? unit, out bool ok)
    {
        ok = false;
        // No unit chosen: the file's own header may say it; otherwise the person must.
        bool fromHeader = false;
        if (string.IsNullOrEmpty(unit))
        {
            unit = MaterialTables.StatedUnit(text);
            if (unit is null) return "The file does not say whether its wavelengths are in µm or nm: choose one, then import again.";
            fromHeader = true;
        }
        ImportedUnit = unit;
        // The one parser the CLI and the MCP server use too.
        var parsed = MaterialTables.Parse(text, unit, out int skipped, out var problem);
        if (parsed is null) return problem == "no rows of numbers were found" ? "no rows of numbers were found in the file" : problem!;
        var rows = parsed.Select(r => new TableRowEntry { Wavelength = Show(r[0]), N = Show(r[1]), K = Show(r[2]) }).ToList();
        Kind = "TABLE";
        Table.Clear();
        Table.AddRange(rows);
        ok = true;
        return $"Imported {rows.Count} row{(rows.Count == 1 ? "" : "s")}, wavelengths read as "
               + (unit == "nm" ? "nanometres and converted to µm" : "micrometres")
               + (fromHeader ? ", as the file's header says" : "")
               + (skipped > 0 ? $"; {skipped} line{(skipped == 1 ? "" : "s")} that were not numbers skipped" : "") + ".";
    }

    /// <summary>The unit the last import read its wavelengths in, for the dialog's box to show.</summary>
    public string? ImportedUnit { get; private set; }

    // ---- parsing -----------------------------------------------------------------------------

    private static string Show(double v) => v.ToString("R", Inv);

    private static double Number(string text, string what) =>
        double.TryParse(text?.Trim(), NumberStyles.Float, Inv, out double v) && double.IsFinite(v)
            ? v : throw new FormatException(string.IsNullOrWhiteSpace(text) ? $"{what} is empty" : $"{what}, '{text}', is not a number");

    private static double Optional(string text, string what) =>
        string.IsNullOrWhiteSpace(text) ? 0.0 : Number(text, what);
}
