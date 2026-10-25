// MaterialDefining.cs — a material's dispersion given from the CLI and the MCP server, as Edit >
// Materials' Edit dispersion and New material give it from the GUI.
//
// ONE ROUTINE FOR A TABLE FILE (MaterialTables.Parse): the dispersion dialog's Import file, the
// CLI's --table and the MCP's table_path all read λ n k rows the same way, with the wavelength
// unit stated by the person, never guessed from the size of the numbers.

using System.Globalization;

namespace ThinFilm.Api;

public static class MaterialTables
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// Rows of λ n [k] from text: commas, tabs, spaces or semicolons between the numbers; a line
    /// that is not all numbers (a header, a comment) is skipped and counted. <paramref name="unit"/>
    /// is what λ is in, "um" or "nm"; the rows come back in µm. Null with the reason when there
    /// is nothing to use.
    /// </summary>
    public static double[][]? Parse(string text, string unit, out int skipped, out string? problem)
    {
        skipped = 0;
        problem = null;
        double scale = unit switch { "um" or "µm" => 1.0, "nm" => 0.001, _ => double.NaN };
        if (double.IsNaN(scale)) { problem = $"'{unit}' is not a wavelength unit; use um or nm"; return null; }
        var rows = new List<double[]>();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var parts = line.Split(new[] { ',', '\t', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
            var numbers = new List<double>();
            foreach (var p in parts)
                if (double.TryParse(p, NumberStyles.Float, Inv, out double v)) numbers.Add(v); else break;
            if (numbers.Count < 2 || numbers.Count != parts.Length) { ++skipped; continue; }
            rows.Add([numbers[0] * scale, numbers[1], numbers.Count > 2 ? numbers[2] : 0.0]);
        }
        if (rows.Count == 0) { problem = "no rows of numbers were found"; return null; }
        // THE USER'S IMPORT OF Si-Green-2008.csv: its header says wavelength_nm, the unit box said
        // µm, and silicon came in from 250 to 1450 µm. A unit the file itself states is not a
        // guess; one that contradicts the person's is refused.
        if (StatedUnit(text) is { } stated && stated != (scale == 1.0 ? "um" : "nm"))
        {
            problem = $"the file's header says its wavelengths are in {Unit(stated)}, not {Unit(scale == 1.0 ? "um" : "nm")}; "
                      + $"read them as {Unit(stated)}";
            return null;
        }
        return rows.ToArray();
    }

    private static string Unit(string u) => u == "nm" ? "nm" : "µm";

    /// <summary>
    /// The wavelength unit a table file names in its header or comments - "nm" or "um" - or null
    /// when it names none, or both. Read from the words (wavelength_nm, "(µm)", microns), never
    /// from the size of the numbers.
    /// </summary>
    public static string? StatedUnit(string text)
    {
        bool nm = false, um = false;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var parts = line.Split(new[] { ',', '\t', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.All(p => double.TryParse(p, NumberStyles.Float, Inv, out _))) continue;
            var lower = line.ToLowerInvariant();
            nm |= NmWord.IsMatch(lower);
            um |= UmWord.IsMatch(lower);
        }
        return nm == um ? null : nm ? "nm" : "um";
    }

    private static readonly System.Text.RegularExpressions.Regex NmWord =
        new(@"(^|[^a-z])(nm|nanometers?|nanometres?)([^a-z]|$)");
    private static readonly System.Text.RegularExpressions.Regex UmWord =
        new(@"(^|[^a-z])(um|µm|μm|microns?|micrometers?|micrometres?)([^a-z]|$)");

    /// <summary>
    /// A model from what the CLI and the MCP server are given: <paramref name="kind"/> sellmeier
    /// (coefficients B1,C1,B2,C2,… with C in µm²), cauchy (A,B,C, λ in µm), constant (n[,k]) or
    /// table (<paramref name="tableText"/>, its λ in <paramref name="tableUnit"/>); a valid range in
    /// µm, and a source. Null with the reason.
    /// </summary>
    public static MaterialModel? Model(string kind, string? coefficients, string? tableText, string? tableUnit,
                                       double? minMicrons, double? maxMicrons, string? source, out string? problem)
    {
        problem = null;
        string k = kind.Trim().ToUpperInvariant();
        double[] numbers = [];
        if (k != "TABLE")
        {
            var parts = (coefficients ?? "").Split(new[] { ',', ' ', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            numbers = new double[parts.Length];
            for (int i = 0; i < parts.Length; ++i)
                if (!double.TryParse(parts[i], NumberStyles.Float, Inv, out numbers[i]) || !double.IsFinite(numbers[i]))
                { problem = $"'{parts[i]}' is not a number"; return null; }
        }
        double[][]? table = null;
        switch (k)
        {
            case "SELLMEIER":
                if (numbers.Length == 0 || numbers.Length % 2 != 0)
                { problem = "Sellmeier takes pairs B1,C1,B2,C2,… (C in µm²)"; return null; }
                break;
            case "CAUCHY":
                if (numbers.Length is < 1 or > 3) { problem = "Cauchy takes A[,B[,C]]"; return null; }
                numbers = [.. numbers, .. new double[3 - numbers.Length]];
                break;
            case "CONSTANT":
                if (numbers.Length is < 1 or > 2) { problem = "a constant takes n[,k]"; return null; }
                numbers = numbers.Length == 1 ? [numbers[0], 0.0] : numbers;
                break;
            case "TABLE":
                if (tableText is null) { problem = "a table needs its rows (a file of λ n k)"; return null; }
                string? unit = tableUnit ?? StatedUnit(tableText);
                if (unit is null) { problem = "the table does not say whether its wavelengths are in nm or um; state it"; return null; }
                table = Parse(tableText, unit, out _, out problem);
                if (table is null) return null;
                break;
            default:
                problem = $"'{kind}' is not a model; use sellmeier, cauchy, constant or table";
                return null;
        }
        if ((minMicrons is null) != (maxMicrons is null)) { problem = "give both ends of the valid range, or neither"; return null; }
        if (minMicrons is { } lo && maxMicrons is { } hi && !(hi > lo && lo > 0))
        { problem = "the valid range must be positive and increasing, in µm"; return null; }
        return new MaterialModel
        {
            Kind = k, Coefficients = numbers, Table = table,
            MinMicrons = k == "TABLE" ? null : minMicrons, MaxMicrons = k == "TABLE" ? null : maxMicrons,
            Source = string.IsNullOrWhiteSpace(source) ? null : source.Trim(),
        };
    }
}

public sealed partial class ThinFilmApi
{
    /// <summary>
    /// A material given its dispersion: as a coating material (replacing the definition of the
    /// design's material of that name - its layers follow - or added when there is none), or as
    /// the substrate or incident medium (<paramref name="role"/> "substrate" or "incident"). The
    /// model must evaluate across its range. Edit dispersion and New material, from the CLI and MCP.
    /// </summary>
    public ApiResult<DesignSummary> DefineMaterial(string id, string name, MaterialModel model, string role = "coating")
    {
        name = name.Trim();
        if (name.Length == 0) return ApiResult<DesignSummary>.Failure("a material needs a name");
        if (model.IsCatalogueGlass) return ApiResult<DesignSummary>.Failure("a catalogue glass is named, not defined; use edit_materials' substrate");
        try
        {
            var (lo, hi) = model.Range();
            foreach (double um in new[] { lo ?? 0.55, hi ?? 0.55, lo is { } a && hi is { } b ? (a + b) / 2 : 0.55 })
                model.At(1000.0 * um, name);
        }
        catch (FormatException x) { return ApiResult<DesignSummary>.Failure($"{name}: {x.Message}"); }

        var set = MaterialSet(id);
        if (!set.Ok) return ApiResult<DesignSummary>.Failure(set.Error!, set.Status);
        var s = set.Value!;
        var named = new NamedMaterial(name, model);
        var edits = s.Materials.Select((m, i) => new MaterialEdit(m, i)).ToList();
        var incident = s.Incident;
        var substrate = s.Substrate;
        switch (role.Trim().ToLowerInvariant())
        {
            case "coating" or "material" or "":
                int at = edits.FindIndex(e => string.Equals(e.Material.Name, name, StringComparison.OrdinalIgnoreCase));
                if (at >= 0) edits[at] = edits[at] with { Material = named };
                else edits.Add(new MaterialEdit(named, -1));
                break;
            case "substrate": substrate = named; break;
            case "incident": incident = named; break;
            default: return ApiResult<DesignSummary>.Failure($"'{role}' is not a role; use coating, substrate or incident");
        }
        return ReplaceMaterials(id, edits.ToArray(), incident, substrate);
    }
}
