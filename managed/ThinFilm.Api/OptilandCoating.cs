// OptilandCoating.cs — Optiland's thin-film coatings, read and written.
//
// WHAT OPTILAND HAS. No coating file of its own: a multilayer is a ThinFilmCoating object in JSON,
// either a file by itself (optiland.fileio.save_obj_to_json / load_obj_from_json(BaseCoating, ...))
// or on a surface of a lens file, at surface_group.surfaces[i].interaction_model.coating. Layers are
// listed incident side first, as ours are, each {"material", "thickness_nm", "name"}; the media are
// "material_pre" and "material_post". Its k is positive for an absorber, as ours is. (Read from
// optiland/coatings.py, thin_film/ and materials/, upstream 2026-10-14.)
//
// MATERIALS GO AS EQUATIONS, NOT TABLES. Optiland's DataMaterial holds a refractiveindex.info
// formula inline - our Sellmeier is its formula 2, our Cauchy its formula 5, a catalogue glass's
// Schott, Sellmeier 1 and Herzberger formulas its 3, 2 and 7 - or an n,k table. So nothing is
// sampled on the way out. Our own definition rides along in the material's metadata
// ({"thinfilm": {...}}), which Optiland keeps and ignores, and on the way in it is used while it
// still gives what the material does. Optiland enforces a formula's wavelength range by refusing
// to evaluate outside it, where we flag; so the range goes in our metadata, not in the formula.
//
// ON THE WAY IN, anything Optiland can hold is read: DataMaterial (formula or table, any of the
// nine formulas), IdealMaterial, AGFMaterial, and a database Material or MaterialFile through the
// refractiveindex.info YAML file it names, when that file is on this computer. A formula we have no
// model for is tabulated finely and said so. An AbbeMaterial is refused: it is a fit to two
// numbers, not data.
//
// Optiland writes Infinity and NaN into lens files, which JSON does not have; they are read as null.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ThinFilm.Api;

public static class OptilandCoating
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ---- reading JSON -------------------------------------------------------------------------

    /// <summary>The text as JSON, with Optiland's bare Infinity, -Infinity and NaN read as null.</summary>
    public static JsonNode Parse(string text)
    {
        var s = new StringBuilder(text.Length);
        bool inString = false;
        for (int i = 0; i < text.Length; ++i)
        {
            char c = text[i];
            if (inString)
            {
                s.Append(c);
                if (c == '\\' && i + 1 < text.Length) s.Append(text[++i]);
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') { inString = true; s.Append(c); continue; }
            if (Word(text, i, "-Infinity")) { s.Append("null"); i += 8; continue; }
            if (Word(text, i, "Infinity")) { s.Append("null"); i += 7; continue; }
            if (Word(text, i, "NaN")) { s.Append("null"); i += 2; continue; }
            s.Append(c);
        }
        return JsonNode.Parse(s.ToString()) ?? throw new FormatException("the file is empty");

        static bool Word(string t, int at, string w) =>
            string.CompareOrdinal(t, at, w, 0, w.Length) == 0
            && (at + w.Length == t.Length || !char.IsLetterOrDigit(t[at + w.Length]));
    }

    /// <summary>A thin-film coating found in a file: what to call it, and the coating object.</summary>
    public sealed record Found(string Name, JsonObject Coating);

    /// <summary>Every ThinFilmCoating in a file: the file itself when it is one, otherwise each
    /// coated surface of a lens file, named by its index and comment.</summary>
    public static List<Found> Coatings(JsonNode root)
    {
        var found = new List<Found>();
        if (root is JsonObject o && Str(o, "type") == "ThinFilmCoating")
        {
            found.Add(new Found("coating", o));
            return found;
        }
        if (root["surface_group"]?["surfaces"] is JsonArray surfaces)
            for (int i = 0; i < surfaces.Count; ++i)
            {
                if (surfaces[i] is not JsonObject surface) continue;
                var coating = surface["interaction_model"]?["coating"] as JsonObject ?? surface["coating"] as JsonObject;
                if (coating is null || Str(coating, "type") != "ThinFilmCoating") continue;
                string comment = Str(surface, "comment") ?? "";
                found.Add(new Found(comment.Length > 0 ? $"surface {i} ({comment})" : $"surface {i}", coating));
            }
        return found;
    }

    public static string? Str(JsonNode? node, string key) =>
        node is JsonObject o && o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static double? Num(JsonNode? node, string key) =>
        node is JsonObject o && o[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    private static double[]? Nums(JsonNode? node)
    {
        if (node is not JsonArray a) return null;
        var r = new double[a.Count];
        for (int i = 0; i < a.Count; ++i)
            if (a[i] is not JsonValue v || !v.TryGetValue(out r[i])) return null;
        return r;
    }

    // ---- refractiveindex.info formulas ---------------------------------------------------------

    /// <summary>One of refractiveindex.info's nine dispersion formulas at λ µm, as Optiland
    /// evaluates them (optiland/materials/dispersion.py).</summary>
    public static double Formula(string formula, double[] c, double w)
    {
        double w2 = w * w;
        switch (formula)
        {
            case "formula 1":
            case "formula 2":
            {
                double v = 1 + c[0];
                for (int i = 1; i + 1 < c.Length; i += 2)
                    v += c[i] * w2 / (w2 - (formula == "formula 1" ? c[i + 1] * c[i + 1] : c[i + 1]));
                return Math.Sqrt(v);
            }
            case "formula 3":
            case "formula 5":
            {
                double v = c[0];
                for (int i = 1; i + 1 < c.Length; i += 2) v += c[i] * Math.Pow(w, c[i + 1]);
                return formula == "formula 3" ? Math.Sqrt(v) : v;
            }
            case "formula 4":
            {
                double v = c[0] + c[1] * Math.Pow(w, c[2]) / (w2 - Math.Pow(c[3], c[4]))
                           + c[5] * Math.Pow(w, c[6]) / (w2 - Math.Pow(c[7], c[8]));
                for (int i = 9; i + 1 < c.Length; i += 2) v += c[i] * Math.Pow(w, c[i + 1]);
                return Math.Sqrt(v);
            }
            case "formula 6":
            {
                double v = 1 + c[0];
                for (int i = 1; i + 1 < c.Length; i += 2) v += c[i] / (c[i + 1] - 1.0 / w2);
                return v;
            }
            case "formula 7":
            {
                double r = 1.0 / (w2 - 0.028);
                double v = c[0] + c[1] * r + c[2] * r * r;
                for (int i = 3; i < c.Length; ++i) v += c[i] * Math.Pow(w, 2 * (i - 2));
                return v;
            }
            case "formula 8":
            {
                double v = c[0] + c[1] * w2 / (w2 - c[2]) + c[3] * w2;
                return Math.Sqrt((1 + 2 * v) / (1 - v));
            }
            case "formula 9":
                return Math.Sqrt(c[0] + c[1] / (w2 - c[2]) + c[3] * (w - c[4]) / ((w - c[4]) * (w - c[4]) + c[5]));
            default:
                throw new FormatException($"'{formula}' is not one of refractiveindex.info's formulas 1 to 9");
        }
    }

    /// <summary>The model a formula is, when we have one: formulas 1 and 2 are a Sellmeier (a
    /// constant term is a Sellmeier term with no resonance), formula 5 with only λ^0, λ^-2 and λ^-4
    /// a Cauchy, formula 5 with only a constant a constant. Null otherwise.</summary>
    public static MaterialModel? ModelOfFormula(string formula, double[] c)
    {
        if (formula is "formula 1" or "formula 2")
        {
            var terms = new List<double>();
            if (c[0] != 0.0) terms.AddRange([c[0], 0.0]);
            for (int i = 1; i + 1 < c.Length; i += 2)
                terms.AddRange([c[i], formula == "formula 1" ? c[i + 1] * c[i + 1] : c[i + 1]]);
            return terms.Count == 0 ? MaterialModel.Constant(1.0) : new MaterialModel { Kind = "SELLMEIER", Coefficients = terms.ToArray() };
        }
        if (formula == "formula 5")
        {
            double a = c[0], b = 0.0, cc = 0.0;
            for (int i = 1; i + 1 < c.Length; i += 2)
            {
                if (c[i + 1] == 0.0) a += c[i];
                else if (c[i + 1] == -2.0) b += c[i];
                else if (c[i + 1] == -4.0) cc += c[i];
                else return null;
            }
            return b == 0.0 && cc == 0.0 ? MaterialModel.Constant(a) : new MaterialModel { Kind = "CAUCHY", Coefficients = [a, b, cc] };
        }
        return null;
    }

    /// <summary>A catalogue glass's formula as refractiveindex.info's, or null: Schott (AGF 1) is
    /// formula 3, Sellmeier 1 (AGF 2) formula 2, Herzberger (AGF 3) formula 7.</summary>
    public static (string Formula, double[] Coefficients)? FormulaOfGlass(int agf, double[] k)
    {
        double C(int i) => i < k.Length ? k[i] : 0.0;
        return agf switch
        {
            1 => ("formula 3", [C(0), C(1), 2, C(2), -2, C(3), -4, C(4), -6, C(5), -8]),
            2 => ("formula 2", [0, C(0), C(1), C(2), C(3), C(4), C(5)]),
            3 => ("formula 7", [C(0), C(1), C(2), C(3), C(4), C(5)]),
            _ => null,
        };
    }

    // ---- refractiveindex.info YAML, for Optiland's database materials --------------------------

    /// <summary>A refractiveindex.info material file's DATA entries: each one's type ("formula 2",
    /// "tabulated n", "tabulated k", "tabulated nk"), its coefficients, its range and its rows.</summary>
    public sealed record YamlEntry(string Type, double[] Coefficients, double[]? Range, List<double[]> Rows);

    public static List<YamlEntry> ReadYaml(string text)
    {
        var entries = new List<YamlEntry>();
        string? type = null;
        double[] coefficients = [];
        double[]? range = null;
        var rows = new List<double[]>();
        bool inData = false, inRows = false;
        void Finish()
        {
            if (type is not null) entries.Add(new YamlEntry(type, coefficients, range, rows));
            (type, coefficients, range, rows, inRows) = (null, [], null, new List<double[]>(), false);
        }
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.TrimEnd();
            string t = line.Trim();
            if (!inData)
            {
                if (t == "DATA:") inData = true;
                continue;
            }
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && !t.StartsWith('-')) { Finish(); break; }   // the next top-level key
            if (t.StartsWith("- type:")) { Finish(); type = t["- type:".Length..].Trim(); continue; }
            if (t.StartsWith("type:")) { Finish(); type = t["type:".Length..].Trim(); continue; }
            if (t.StartsWith("coefficients:")) { coefficients = Numbers(t["coefficients:".Length..]); inRows = false; continue; }
            if (t.StartsWith("wavelength_range:")) { range = Numbers(t["wavelength_range:".Length..]); inRows = false; continue; }
            if (t.StartsWith("data:")) { inRows = true; continue; }
            if (inRows && t.Length > 0 && (char.IsDigit(t[0]) || t[0] is '.' or '-' or '+'))
                rows.Add(Numbers(t));
        }
        Finish();
        return entries;

        static double[] Numbers(string s) =>
            s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
             .Select(x => double.Parse(x, NumberStyles.Float, Inv)).ToArray();
    }

    // ---- an Optiland material as ours ----------------------------------------------------------

    /// <summary>What an Optiland material reads as: a name, our model of it, and a note when the
    /// reading lost or changed something. Problem says why it cannot be read at all.</summary>
    public sealed record Read(string Name, MaterialModel? Model, string? Note, string? Problem);

    /// <param name="gridUm">Where a formula we have no model for is tabulated when it gives no
    /// range of its own: the design's wavelengths.</param>
    public static Read FromOptiland(JsonObject m, (double Lo, double Hi) gridUm)
    {
        string type = Str(m, "type") ?? "";
        try
        {
            switch (type)
            {
                case "IdealMaterial":
                {
                    double n = Num(m, "index") ?? 1.0, k = Num(m, "absorp") ?? 0.0;
                    string name = n == 1.0 && k == 0.0 ? "air" : $"n={n.ToString("0.#####", Inv)}" + (k > 0 ? $" k={k.ToString("G3", Inv)}" : "");
                    return new Read(name, n == 1.0 && k == 0.0 ? MaterialModel.Air : MaterialModel.Constant(n, k), null, null);
                }
                case "DataMaterial":
                {
                    string name = Str(m, "name") is { Length: > 0 } nm ? nm : "DataMaterial";
                    var definition = m["definition"] as JsonObject ?? throw new FormatException("a DataMaterial with no definition");
                    var read = FromDefinition(name, definition, gridUm, Ours(m));
                    return read;
                }
                case "AGFMaterial":
                {
                    string name = Str(m, "name") ?? "glass", catalog = Str(m, "catalog") ?? "";
                    if (GlassCatalogs.Find(catalog, name) is not null)
                        return new Read(name, MaterialModel.CatalogueGlass(catalog, name), null, null);
                    int agf = (int)(Num(m, "formula") ?? 0);
                    var coefficients = Nums(m["coefficients"]) ?? [];
                    if (FormulaOfGlass(agf, coefficients) is not { } f)
                        return new Read(name, null, null, $"{name} is a catalogue glass with formula {agf}, which is not read here, and {catalog} is not among the glass catalogues");
                    return FromFormula(name, f.Formula, f.Coefficients, Nums(m["wavelength_range"]), null, gridUm,
                                       $"from its {catalog} formula; {catalog} is not among this program's glass catalogues");
                }
                case "Material":
                case "MaterialFile":
                {
                    string file = Str(m, "filename") ?? "";
                    string name = Str(m, "name") ?? Path.GetFileNameWithoutExtension(file);
                    string reference = Str(m, "reference") is { Length: > 0 } r ? $" ({r})" : "";
                    if (file.Length == 0 || !File.Exists(file))
                        return new Read(name, null, null,
                            $"{name}{reference} is a material from Optiland's database, read from {(file.Length == 0 ? "a file it does not name" : file)}, "
                            + "which is not on this computer");
                    return FromYaml(name, ReadYaml(File.ReadAllText(file)), gridUm, Path.GetFileName(file));
                }
                case "AbbeMaterial":
                case "AbbeMaterialE":
                    return new Read("Abbe", null, null,
                        $"an {type} (n = {Num(m, "index")}, V = {Num(m, "abbe")}) is a fit to two numbers rather than data; give the layer a real material in Optiland");
                default:
                    return new Read(type, null, null, $"'{type}' is not an Optiland material this program reads");
            }
        }
        catch (Exception e) when (e is FormatException or InvalidOperationException or IndexOutOfRangeException)
        {
            return new Read(Str(m, "name") ?? type, null, null, $"{Str(m, "name") ?? type}: {e.Message}");
        }
    }

    /// <summary>Our own definition from a material's metadata, if it carries one.</summary>
    private static MaterialModel? Ours(JsonObject m)
    {
        if (m["metadata"]?["thinfilm"] is not JsonObject t) return null;
        string? kind = Str(t, "model");
        double[]? range = Nums(t["range_um"]);
        string? source = Str(t, "source");
        MaterialModel? model = kind switch
        {
            "GLASS" when Str(t, "catalog") is { } cat && Str(t, "glass") is { } g => MaterialModel.CatalogueGlass(cat, g),
            "CONSTANT" or "SELLMEIER" or "CAUCHY" when Nums(t["coefficients"]) is { } c => new MaterialModel { Kind = kind, Coefficients = c },
            _ => null,
        };
        if (model is null || model.IsCatalogueGlass) return model;
        return model with { MinMicrons = range?[0], MaxMicrons = range?[1], Source = source };
    }

    private static Read FromDefinition(string name, JsonObject definition, (double Lo, double Hi) gridUm, MaterialModel? ours)
    {
        var dispersion = definition["dispersion"] as JsonObject ?? throw new FormatException("no dispersion");
        double[][]? k = null;
        if (definition["extinction"] is JsonObject e)
        {
            var kw = Nums(e["wavelengths_um"]) ?? []; var kv = Nums(e["values"]) ?? [];
            if (kv.Any(v => v > 0.0)) k = kw.Zip(kv, (a, b) => new[] { a, b }).ToArray();
        }
        string kind = Str(dispersion, "kind") ?? "";
        Read read;
        if (kind == "tabulated")
        {
            var w = Nums(dispersion["wavelengths_um"]) ?? []; var n = Nums(dispersion["indices"]) ?? [];
            read = FromTables(name, w.Zip(n, (a, b) => new[] { a, b }).ToArray(), k);
        }
        else if (kind == "formula")
            read = FromFormula(name, Str(dispersion, "formula") ?? "", Nums(dispersion["coefficients"]) ?? [],
                               Nums(dispersion["wavelength_range_um"]), k, gridUm, null);
        else
            throw new FormatException($"a dispersion of kind '{kind}'");

        // Ours, from the metadata, when it still gives what the material does.
        if (ours is not null && read.Model is { } got)
        {
            var (lo, hi) = got.Range();
            double a = lo ?? gridUm.Lo, b = hi ?? gridUm.Hi;
            bool same = Enumerable.Range(0, 21).Select(i => a + (b - a) * i / 20).All(um =>
            {
                try
                {
                    var x = ours.At(um * 1000, name); var y = got.At(um * 1000, name);
                    return Math.Abs(x.N - y.N) <= 1e-6 && Math.Abs(x.K - y.K) <= 1e-6;
                }
                catch (FormatException) { return false; }
            });
            if (same) return new Read(name, ours, null, null);
            return read with { Note = Also(read.Note, $"{name}'s data no longer match the {ours.Kind} model ThinFilmDesigner wrote with it, so the data are used") };
        }
        return read;
    }

    private static Read FromYaml(string name, List<YamlEntry> entries, (double Lo, double Hi) gridUm, string file)
    {
        double[][]? k = entries.FirstOrDefault(x => x.Type == "tabulated k") is { } kt && kt.Rows.Any(r => r[1] > 0)
            ? kt.Rows.Select(r => new[] { r[0], r[1] }).ToArray() : null;
        if (entries.FirstOrDefault(x => x.Type == "tabulated nk") is { } nk)
            return FromTables(name, nk.Rows.Select(r => new[] { r[0], r[1] }).ToArray(),
                              nk.Rows.Any(r => r.Length > 2 && r[2] > 0) ? nk.Rows.Select(r => new[] { r[0], r.Length > 2 ? r[2] : 0.0 }).ToArray() : k)
                   with { Note = null };
        if (entries.FirstOrDefault(x => x.Type == "tabulated n") is { } tn)
            return FromTables(name, tn.Rows.Select(r => new[] { r[0], r[1] }).ToArray(), k);
        if (entries.FirstOrDefault(x => x.Type.StartsWith("formula ")) is { } f)
            return FromFormula(name, f.Type, f.Coefficients, f.Range, k, gridUm, $"from Optiland's database file {file}");
        throw new FormatException($"{file} has no data this program reads");
    }

    /// <summary>An n table and a k table on their own grids, as one TABLE on both.</summary>
    private static Read FromTables(string name, double[][] n, double[][]? k)
    {
        if (n.Length == 0) throw new FormatException("an empty table");
        var w = n.Select(r => r[0]).Concat(k?.Select(r => r[0]) ?? []).Distinct().OrderBy(x => x).ToArray();
        var rows = w.Select(l => new[] { l, Linear(n, l), k is null ? 0.0 : Linear(k, l) }).ToArray();
        var model = rows.Length == 1
            ? MaterialModel.Constant(rows[0][1], rows[0][2])
            : new MaterialModel { Kind = "TABLE", Table = rows };
        return new Read(name, model with { Source = "an Optiland coating" }, null, null);
    }

    private static Read FromFormula(string name, string formula, double[] c, double[]? range, double[][]? k,
                                    (double Lo, double Hi) gridUm, string? from)
    {
        Formula(formula, c, gridUm.Lo);   // refuses an unknown formula
        var model = k is null ? ModelOfFormula(formula, c) : null;
        if (model is not null)
            return new Read(name, model with { MinMicrons = range?[0], MaxMicrons = range?[1], Source = from is null ? "an Optiland coating" : $"Optiland, {from}" }, null, null);

        // No model of ours: a table, fine enough that linear interpolation is within 1e-6.
        double lo = range?[0] ?? gridUm.Lo, hi = range?[1] ?? gridUm.Hi;
        if (k is not null) { lo = Math.Min(lo, k[0][0]); hi = Math.Max(hi, k[^1][0]); }
        if (range is not null) { lo = Math.Max(lo, range[0]); hi = Math.Min(hi, range[1]); }
        var rows = Sample(l => (Formula(formula, c, l), k is null ? 0.0 : Linear(k, l)), lo, hi);
        string why = k is not null ? "it has a measured k beside its formula" : $"this program has no model for {formula}";
        return new Read(name, new MaterialModel { Kind = "TABLE", Table = rows, Source = "an Optiland coating" },
                        $"{name} is read as a table of {rows.Length} rows from {lo:0.###} to {hi:0.###} µm, because {why}", null);
    }

    /// <summary>Rows where the function bends, until linear interpolation is within 1e-6.</summary>
    private static double[][] Sample(Func<double, (double N, double K)> f, double lo, double hi)
    {
        double[] Row(double l) { var (n, k) = f(l); return [l, n, k]; }
        var rows = new List<double[]> { Row(lo), Row(hi) };
        if (!(hi > lo)) return [rows[0]];
        for (int guard = 0; guard < 4000; ++guard)
        {
            int worst = -1;
            double miss = 1e-6;
            double[] at = [];
            for (int i = 0; i + 1 < rows.Count; ++i)
            {
                var mid = Row(0.5 * (rows[i][0] + rows[i + 1][0]));
                double e = Math.Max(Math.Abs(mid[1] - 0.5 * (rows[i][1] + rows[i + 1][1])), Math.Abs(mid[2] - 0.5 * (rows[i][2] + rows[i + 1][2])));
                if (e > miss) (worst, miss, at) = (i, e, mid);
            }
            if (worst < 0) break;
            rows.Insert(worst + 1, at);
        }
        return rows.ToArray();
    }

    private static double Linear(double[][] t, double l)
    {
        if (l <= t[0][0]) return t[0][1];
        if (l >= t[^1][0]) return t[^1][1];
        int i = 1;
        while (t[i][0] < l) ++i;
        double f = (l - t[i - 1][0]) / (t[i][0] - t[i - 1][0]);
        return t[i - 1][1] + f * (t[i][1] - t[i - 1][1]);
    }

    private static string Also(string? a, string b) => a is null ? b : $"{a}; {b}";

    // ---- ours as an Optiland material ----------------------------------------------------------

    private static JsonObject Propagation() => new() { ["class"] = "HomogeneousPropagation" };

    private static JsonArray Array(IEnumerable<double> v) => new(v.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());

    /// <summary>
    /// A material as Optiland holds it: air as an IdealMaterial; everything else as a
    /// DataMaterial, named, with the equation itself or the table, and our definition in its
    /// metadata. A catalogue glass goes as its catalogue's formula.
    /// </summary>
    public static JsonObject ToOptiland(string name, MaterialModel m)
    {
        string kind = m.Kind.ToUpperInvariant();
        if (kind == "CONSTANT" && m.Coefficients is [1.0, 0.0])
            return new JsonObject { ["type"] = "IdealMaterial", ["propagation_model"] = Propagation(), ["index"] = 1.0, ["absorp"] = 0.0 };

        JsonObject dispersion;
        JsonObject? extinction = null;
        var ours = new JsonObject { ["model"] = kind };
        switch (kind)
        {
            case "CONSTANT":
            {
                double n = m.Coefficients[0], k = m.Coefficients.Length > 1 ? m.Coefficients[1] : 0.0;
                dispersion = FormulaNode("formula 5", [n]);
                if (k > 0) extinction = KNode([0.1, 100.0], [k, k]);
                ours["coefficients"] = Array(m.Coefficients);
                break;
            }
            case "SELLMEIER":
                dispersion = FormulaNode("formula 2", [0.0, .. m.Coefficients]);
                ours["coefficients"] = Array(m.Coefficients);
                break;
            case "CAUCHY":
                dispersion = FormulaNode("formula 5", [m.Coefficients[0], m.Coefficients[1], -2, m.Coefficients[2], -4]);
                ours["coefficients"] = Array(m.Coefficients);
                break;
            case "TABLE":
            {
                var t = m.Table!;
                dispersion = new JsonObject
                {
                    ["kind"] = "tabulated",
                    ["wavelengths_um"] = Array(t.Select(r => r[0])),
                    ["indices"] = Array(t.Select(r => r[1])),
                };
                if (t.Any(r => r[2] > 0)) extinction = KNode(t.Select(r => r[0]).ToArray(), t.Select(r => r[2]).ToArray());
                break;
            }
            case "GLASS":
            {
                var g = GlassCatalogs.Find(m.Catalog ?? "", m.Glass ?? "")
                        ?? throw new FormatException($"{name}, the catalogue glass {m.Catalog} {m.Glass}, is not in any glass catalogue in {GlassCatalogs.Folder}");
                var f = FormulaOfGlass(g.Formula, g.Coefficients)
                        ?? throw new FormatException($"{g.FullName} uses glass formula {g.Formula}, which has no refractiveindex.info equivalent here");
                dispersion = FormulaNode(f.Formula, f.Coefficients);
                ours["catalog"] = g.Catalog;
                ours["glass"] = g.Name;
                ours["range_um"] = Array([g.MinMicrons, g.MaxMicrons]);
                name = g.Name;
                break;
            }
            default:
                throw new FormatException($"{name} has a {m.Kind} model, which cannot be written for Optiland");
        }
        if (kind is not ("TABLE" or "GLASS") && m.MinMicrons is { } lo && m.MaxMicrons is { } hi) ours["range_um"] = Array([lo, hi]);
        if (!string.IsNullOrWhiteSpace(m.Source) && kind != "GLASS") ours["source"] = m.Source;

        return new JsonObject
        {
            ["type"] = "DataMaterial",
            ["propagation_model"] = Propagation(),
            ["name"] = name,
            ["definition"] = new JsonObject { ["dispersion"] = dispersion, ["extinction"] = extinction, ["thermal"] = null },
            // Tables are held at their ends outside their rows, as here and in OpticStudio.
            ["bounds"] = "clamp",
            ["metadata"] = new JsonObject { ["thinfilm"] = ours },
        };

        static JsonObject FormulaNode(string formula, double[] c) => new()
        {
            ["kind"] = "formula", ["formula"] = formula, ["coefficients"] = Array(c), ["wavelength_range_um"] = null,
        };
        static JsonObject KNode(double[] w, double[] k) => new()
        {
            ["kind"] = "tabulated_k", ["wavelengths_um"] = Array(w), ["values"] = Array(k),
        };
    }

    public static string Write(JsonNode node) =>
        node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
}
