// MaterialEditing.cs — the rest of Edit > Materials, for the CLI and the MCP server: a material's
// name, its source and range on their own, Upgrade constant materials, and a material said in one
// line.
//
// FOUND REPRODUCING THE GUIDE'S CHAPTER 7 FROM THE COMMAND LINE (2026-10-25): the Materials window
// renames a material, upgrades constant ones, gives a material a source without retyping its
// table, and says what every material is ("Sellmeier, n = 1.4599 at 550 nm, 210–3710 nm"); the CLI
// and MCP could do none of it, and listed materials by name only.

using System.Globalization;

namespace ThinFilm.Api;

/// <summary>A material in words, the same in the GUI's lists, the CLI's and the MCP's.</summary>
public static class MaterialSummary
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>A model in a few words: "constant n = 1.46", "Sellmeier, Malitson 1965".</summary>
    public static string Describe(MaterialModel m)
    {
        string kind = m.Kind.ToUpperInvariant() switch
        {
            "CONSTANT" => m.Coefficients.Length > 1 && m.Coefficients[1] > 0
                ? string.Format(Inv, "constant n = {0:0.####}, k = {1:0.####}", m.Coefficients[0], m.Coefficients[1])
                : string.Format(Inv, "constant n = {0:0.####}", m.Coefficients.FirstOrDefault()),
            "SELLMEIER" => "Sellmeier",
            "CAUCHY" => "Cauchy",
            "TABLE" => $"table of {m.Table?.Length ?? 0} rows",
            "GLASS" => $"catalogue glass {m.Catalog} {m.Glass}",
            _ => m.Kind,
        };
        return m.Kind.Equals("CONSTANT", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(m.Source)
            ? kind : $"{kind}, {m.Source}";
    }

    /// <summary>
    /// A material in one line, so a list says what each entry is: "Sellmeier, n = 1.4599 at 550
    /// nm, 210–3710 nm". <paramref name="unit"/> is "nm" or "um", for the wavelengths.
    /// </summary>
    public static string Line(NamedMaterial m, string unit = "nm")
    {
        var model = m.Model;
        if (model.Kind.Equals("CONSTANT", StringComparison.OrdinalIgnoreCase)) return Describe(model);
        string kind = model.Kind.ToUpperInvariant() switch
        {
            "SELLMEIER" => "Sellmeier",
            "CAUCHY" => "Cauchy",
            "TABLE" => "measured table",
            "GLASS" => "catalogue glass",
            _ => model.Kind,
        };
        string at = unit == "um" ? "0.55 µm" : "550 nm";
        string n;
        try
        {
            var (nn, k, _) = model.At(550, m.Name);
            // A k too small for four decimals is still absorption, and "k = 0" would deny it.
            n = k >= 1e-4 ? string.Format(Inv, ", n = {0:0.0000}, k = {1:0.0000} at {2}", nn, k, at)
                : k > 0 ? string.Format(Inv, ", n = {0:0.0000}, k < 0.0001 at {1}", nn, at)
                : string.Format(Inv, ", n = {0:0.0000} at {1}", nn, at);
        }
        catch (Exception x) when (x is FormatException or MaterialUnavailableException) { n = ""; }
        var (lo, hi) = model.Range();
        string range = lo is null && hi is null ? "no stated range" : Range(lo, hi, unit);
        return $"{kind}{n}, {range}";
    }

    /// <summary>"210–3710 nm", or "0.21–3.71 µm".</summary>
    public static string Range(double? lo, double? hi, string unit) =>
        unit == "um" ? string.Format(Inv, "{0:0.##}–{1:0.##} µm", lo, hi)
                     : string.Format(Inv, "{0:0.#}–{1:0.#} nm", lo * 1000.0, hi * 1000.0);
}

/// <summary>What Upgrade constant materials did: each material replaced ("SiO2 (constant n = 1.46 →
/// Sellmeier, ...)"), and each constant one left because the database has none of its name.</summary>
public sealed record MaterialUpgradeReport(string[] Replaced, string[] Left, DesignSummary Design);

public sealed partial class ThinFilmApi
{
    /// <summary>
    /// A design material renamed - a coating material (its layers follow), or the substrate or
    /// incident medium. A catalogue glass keeps its catalogue name; a coating material's new name
    /// must not be another's. Edit > Materials' Rename.
    /// </summary>
    public ApiResult<DesignSummary> RenameMaterial(string id, string name, string newName)
    {
        newName = newName.Trim();
        if (newName.Length == 0) return ApiResult<DesignSummary>.Failure("a material needs a name");
        var set = MaterialSet(id);
        if (!set.Ok) return ApiResult<DesignSummary>.Failure(set.Error!, set.Status);
        var s = set.Value!;
        var edits = s.Materials.Select((m, i) => new MaterialEdit(m, i)).ToArray();
        int at = Array.FindIndex(edits, e => Same(e.Material.Name, name));
        var incident = s.Incident;
        var substrate = s.Substrate;
        if (at >= 0)
        {
            if (edits.Where((e, i) => i != at).Any(e => Same(e.Material.Name, newName)))
                return ApiResult<DesignSummary>.Failure($"the design already has a material called '{newName}'");
            edits[at] = edits[at] with { Material = edits[at].Material with { Name = newName } };
        }
        else if (Same(substrate.Name, name) || Same(incident.Name, name))
        {
            var medium = Same(substrate.Name, name) ? substrate : incident;
            if (medium.Model.IsCatalogueGlass)
                return ApiResult<DesignSummary>.Failure($"{medium.Name} is a catalogue glass, named by its catalogue; it cannot be renamed");
            if (Same(substrate.Name, name)) substrate = substrate with { Name = newName };
            else incident = incident with { Name = newName };
        }
        else return ApiResult<DesignSummary>.Failure($"the design has no material called '{name}'");
        return ReplaceMaterials(id, edits, incident, substrate);
    }

    /// <summary>
    /// Edit dispersion's Source and Valid range on their own: the material's model is kept, and
    /// only what is given changes. A table's range is its rows, and is not set here.
    /// </summary>
    public ApiResult<DesignSummary> DescribeMaterial(string id, string name, string? source, double? minMicrons, double? maxMicrons)
    {
        var set = MaterialSet(id);
        if (!set.Ok) return ApiResult<DesignSummary>.Failure(set.Error!, set.Status);
        var s = set.Value!;
        var found = s.Materials.Concat([s.Substrate, s.Incident]).FirstOrDefault(m => Same(m.Name, name));
        if (found is null) return ApiResult<DesignSummary>.Failure($"the design has no material called '{name}'");
        if (found.Model.IsCatalogueGlass)
            return ApiResult<DesignSummary>.Failure($"{found.Name} is a catalogue glass; its source and range are the catalogue's");
        var model = found.Model;
        if (minMicrons is not null || maxMicrons is not null)
        {
            if (model.Kind.Equals("TABLE", StringComparison.OrdinalIgnoreCase))
                return ApiResult<DesignSummary>.Failure($"{found.Name} is a table: its range is its first and last row");
            double? lo = minMicrons ?? model.MinMicrons, hi = maxMicrons ?? model.MaxMicrons;
            if (lo is { } a && hi is { } b && !(b > a && a > 0))
                return ApiResult<DesignSummary>.Failure("the valid range must be positive and increasing, in µm");
            model = model with { MinMicrons = lo, MaxMicrons = hi };
        }
        if (source is not null) model = model with { Source = string.IsNullOrWhiteSpace(source) ? null : source.Trim() };
        string role = Same(s.Substrate.Name, name) && !s.Materials.Any(m => Same(m.Name, name)) ? "substrate"
                    : Same(s.Incident.Name, name) && !s.Materials.Any(m => Same(m.Name, name)) ? "incident" : "coating";
        return DefineMaterial(id, found.Name, model, role);
    }

    /// <summary>
    /// Upgrade constant materials: each constant-index material (media included, air excepted)
    /// takes the definition of the database material of the same name; one the database has no
    /// match for is left as it is, and named.
    /// </summary>
    public ApiResult<MaterialUpgradeReport> UpgradeConstantMaterials(string id, MaterialDatabase database)
    {
        var set = MaterialSet(id);
        if (!set.Ok) return ApiResult<MaterialUpgradeReport>.Failure(set.Error!, set.Status);
        var s = set.Value!;
        var replaced = new List<string>();
        var left = new List<string>();
        NamedMaterial Upgrade(NamedMaterial m)
        {
            if (!m.Model.Kind.Equals("CONSTANT", StringComparison.OrdinalIgnoreCase) || Same(m.Name, MaterialDatabase.Air.Name))
                return m;
            if (database.Find(m.Name) is not { } match) { left.Add(m.Name); return m; }
            replaced.Add($"{m.Name} ({MaterialSummary.Line(m)} → {MaterialSummary.Line(match)})");
            return match with { Name = m.Name };
        }
        var edits = s.Materials.Select((m, i) => new MaterialEdit(Upgrade(m), i)).ToArray();
        var incident = Upgrade(s.Incident);
        var substrate = Upgrade(s.Substrate);
        var r = replaced.Count > 0 ? ReplaceMaterials(id, edits, incident, substrate) : Describe(id);
        return r.Ok
            ? ApiResult<MaterialUpgradeReport>.Success(new MaterialUpgradeReport(replaced.ToArray(), left.ToArray(), r.Value!))
            : ApiResult<MaterialUpgradeReport>.Failure(r.Error!, r.Status);
    }

    private static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}
