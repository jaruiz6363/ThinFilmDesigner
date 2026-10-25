// MaterialDatabase.cs — every material in one text file, ThinFilm's own format.
//
// THE DATABASE IS WHAT A PERSON CHOOSES FROM; IT IS NEVER WHAT A DESIGN USES. Choosing a material
// copies its definition into the design file, and the design keeps that copy for good: a database
// edited later - a coefficient corrected, a material renamed or deleted - cannot change, or break,
// a design made from it. (The user's rule, 2026-10-02: "The materials and the design must live in
// one file.")
//
// THE FORMAT, all materials in one file in the spirit of a ZEMAX catalogue, and written to be
// edited by hand:
//
//     # comment, anywhere; wavelengths in micrometres throughout
//     MATERIAL SiO2
//       SELLMEIER 0.6961663 0.0046791 0.4079426 0.0135121 0.8974794 97.934
//       RANGE 0.21 3.71
//       SOURCE Malitson 1965, fused silica
//     END
//     MATERIAL Ag
//       TABLE
//         0.40  0.05  2.10
//         0.50  0.05  3.10
//     END
//
// One model line per material: CONSTANT n k, SELLMEIER B1 C1 B2 C2 ..., CAUCHY A B C, or TABLE
// followed by rows of λ n k. RANGE and SOURCE are optional. A material's name is the rest of its
// MATERIAL line, so it may contain spaces. A file that does not parse is refused with the line
// number and what was wrong, never half-read.

using System.Globalization;
using System.Text;

namespace ThinFilm.Api;

/// <summary>A named material definition: what the database holds and what a design copies.</summary>
public sealed record NamedMaterial(string Name, MaterialModel Model);

public sealed class MaterialDatabase
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public IReadOnlyList<NamedMaterial> Materials { get; }

    /// <summary>The file this was read from, or null for one made in memory.</summary>
    public string? Path { get; }

    public MaterialDatabase(IReadOnlyList<NamedMaterial> materials, string? path = null)
    {
        Materials = materials;
        Path = path;
    }

    public NamedMaterial? Find(string name) =>
        Materials.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A material named as a person names one anywhere a medium or material is chosen: air, a
    /// material of this database, or a catalogue glass - "SCHOTT N-BK7", or "N-BK7" when only one
    /// catalogue has it. A glass is named, not copied, as New design and Edit > Materials do.
    /// </summary>
    public NamedMaterial? Resolve(string name)
    {
        name = name.Trim();
        if (name.Equals(Air.Name, StringComparison.OrdinalIgnoreCase)) return Air;
        if (Find(name) is { } own) return own;
        var space = name.IndexOf(' ');
        Glass? glass = space > 0 ? GlassCatalogs.Find(name[..space], name[(space + 1)..].Trim()) : null;
        if (glass is null)
        {
            var all = GlassCatalogs.All.Select(c => c.Find(name)).OfType<Glass>().ToArray();
            if (all.Length == 1) glass = all[0];
        }
        return glass is null ? null : new NamedMaterial(glass.FullName, MaterialModel.CatalogueGlass(glass.Catalog, glass.Name));
    }

    /// <summary>Air, n = 1 exactly: the one built-in material, offered as an incident medium
    /// whatever the database holds.</summary>
    public static NamedMaterial Air { get; } = new("air", MaterialModel.Air);

    // ---- where it lives ----------------------------------------------------------------------

    /// <summary>The database that ships with the program, beside it in materials/.</summary>
    public static string ShippedPath =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "materials", "thinfilm-materials.txt");

    public static MaterialDatabase Shipped() => Load(ShippedPath);

    /// <summary>The materials the literature samples use (Samples), a second database beside the first.</summary>
    public static string ShippedLiteraturePath =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "materials", "literature-materials.txt");

    /// <summary>Where a person finds the literature database to choose in System Parameters: in
    /// ThinFilmHome beside their own, because on macOS the shipped copy is inside the .app, where a
    /// file picker cannot reach.</summary>
    public static string LiteratureUserPath =>
        System.IO.Path.Combine(ThinFilmHome.Directory, "literature-materials.txt");

    /// <summary>The shipped literature database copied to LiteratureUserPath when it is not there yet,
    /// or when a newer release ships a newer one; a copy the person has changed is kept.</summary>
    public static void CopyLiterature()
    {
        if (!File.Exists(ShippedLiteraturePath)) return;
        string marker = LiteratureUserPath + ".shipped";
        string shipped = File.ReadAllText(ShippedLiteraturePath);
        bool untouched = !File.Exists(LiteratureUserPath)
                         || (File.Exists(marker) && File.ReadAllText(marker) == File.ReadAllText(LiteratureUserPath));
        if (!untouched || (File.Exists(LiteratureUserPath) && File.ReadAllText(LiteratureUserPath) == shipped)) return;
        Directory.CreateDirectory(ThinFilmHome.Directory);
        File.WriteAllText(LiteratureUserPath, shipped);
        File.WriteAllText(marker, shipped);
    }

    /// <summary>A person's own working database, which starts as a copy of the shipped one.</summary>
    public static string DefaultUserPath =>
        System.IO.Path.Combine(ThinFilmHome.Directory, "materials.txt");

    /// <summary>
    /// The database at <paramref name="path"/>, which is made from the shipped one first if it does
    /// not exist yet - so a person's edits go to their own copy, and a new release of the shipped
    /// file never overwrites them. A copy nobody has changed follows the shipped one: a release that
    /// ships new materials brings them to it. Beside it, <c>.shipped</c> records what was copied.
    /// </summary>
    public static MaterialDatabase OpenOrCreate(string path)
    {
        string marker = path + ".shipped";
        string shipped = File.ReadAllText(ShippedPath);
        if (!File.Exists(path) || (File.ReadAllText(path) != shipped && Untouched(path, marker)))
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, shipped);
            File.WriteAllText(marker, shipped);
        }
        return Load(path);
    }

    /// <summary>The copy is as it was made: the same text as its marker, or - a copy made before
    /// markers, which has none - every material in it still as the shipped database has it.</summary>
    private static bool Untouched(string path, string marker)
    {
        string text = File.ReadAllText(path);
        if (File.Exists(marker)) return File.ReadAllText(marker) == text;
        try
        {
            var shipped = Shipped();
            return Parse(text, path).Materials.All(m =>
                shipped.Find(m.Name) is { } s && Definition(s) == Definition(m));
        }
        catch (FormatException) { return false; }

        static string Definition(NamedMaterial m) => new MaterialDatabase([m]).Write();
    }

    /// <summary>
    /// The person's own database: the one System Parameters names, or their working copy in ThinFilmHome,
    /// made from the shipped one the first time. The GUI, the CLI and the MCP all open it so, so a
    /// material added in one is there in the others. If it cannot be read, the shipped one, and why.
    /// </summary>
    public static MaterialDatabase ForPerson(out string? problem)
    {
        var settings = ThinFilmHome.LoadSettings(out problem);
        string path = settings.MaterialDatabasePath ?? DefaultUserPath;
        try
        {
            CopyLiterature();
            return OpenOrCreate(path);
        }
        catch (Exception x) when (x is IOException or FormatException or UnauthorizedAccessException)
        {
            problem = $"The material database could not be read - {x.Message}. Using the shipped one.";
            return Shipped();
        }
    }

    // ---- reading -----------------------------------------------------------------------------

    public static MaterialDatabase Load(string path) => Parse(File.ReadAllText(path), path);

    /// <summary>The text parsed, or a FormatException naming the line and what is wrong.</summary>
    public static MaterialDatabase Parse(string text, string? path = null)
    {
        var materials = new List<NamedMaterial>();
        string? name = null;
        MaterialModel? model = null;
        List<double[]>? rows = null;
        double? min = null, max = null;
        string? source = null;
        int started = 0;

        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; ++i)
        {
            int number = i + 1;
            string line = lines[i];
            int hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash];
            line = line.Trim();
            if (line.Length == 0) continue;

            var words = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            string key = words[0].ToUpperInvariant();

            if (name is null)
            {
                if (key != "MATERIAL") throw Bad(number, $"expected MATERIAL, found '{words[0]}'");
                name = line[words[0].Length..].Trim();
                if (name.Length == 0) throw Bad(number, "MATERIAL needs a name");
                if (materials.Any(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)))
                    throw Bad(number, $"'{name}' is defined twice");
                started = number;
                continue;
            }

            switch (key)
            {
                case "CONSTANT":
                case "SELLMEIER":
                case "CAUCHY":
                    if (model is not null || rows is not null) throw Bad(number, $"'{name}' has a second model line");
                    model = new MaterialModel { Kind = key, Coefficients = Numbers(words, 1, number) };
                    break;
                case "TABLE":
                    if (model is not null || rows is not null) throw Bad(number, $"'{name}' has a second model line");
                    rows = new List<double[]>();
                    break;
                case "RANGE":
                    var r = Numbers(words, 1, number);
                    if (r.Length != 2 || !(r[0] < r[1])) throw Bad(number, "RANGE needs two numbers, low then high, in µm");
                    (min, max) = (r[0], r[1]);
                    break;
                case "SOURCE":
                    source = line[words[0].Length..].Trim();
                    break;
                case "END":
                    if (model is null && rows is null) throw Bad(number, $"'{name}' has no model line");
                    if (rows is not null)
                    {
                        if (rows.Count == 0) throw Bad(number, $"'{name}' has a TABLE with no rows");
                        for (int j = 1; j < rows.Count; ++j)
                            if (!(rows[j][0] > rows[j - 1][0]))
                                throw Bad(number, $"'{name}': TABLE wavelengths must increase");
                        model = new MaterialModel { Kind = "TABLE", Table = rows.ToArray() };
                    }
                    model = model! with { MinMicrons = min, MaxMicrons = max, Source = source };
                    // Evaluated once here, so a definition that cannot give an index - an odd
                    // Sellmeier list, a short Cauchy - is refused when the file is read, not when a
                    // design first asks for it.
                    var (lo, hi) = model.Range();
                    double probe = 1000.0 * (lo is { } a && hi is { } b ? (a + b) / 2 : lo ?? hi ?? 0.55);
                    try { model.At(probe, name); }
                    catch (FormatException e) { throw Bad(number, e.Message); }
                    materials.Add(new NamedMaterial(name, model));
                    (name, model, rows, min, max, source) = (null, null, null, null, null, null);
                    break;
                default:
                    if (rows is not null && double.TryParse(words[0], NumberStyles.Float, Inv, out _))
                    {
                        var row = Numbers(words, 0, number);
                        if (row.Length != 3) throw Bad(number, "a TABLE row is λ (µm), n, k");
                        rows.Add(row);
                        break;
                    }
                    throw Bad(number, $"'{words[0]}' is not a database keyword");
            }
        }

        if (name is not null) throw Bad(started, $"'{name}' has no END");
        return new MaterialDatabase(materials, path);

        FormatException Bad(int line, string why) =>
            new($"{System.IO.Path.GetFileName(path) ?? "material database"}, line {line}: {why}");

        double[] Numbers(string[] words, int from, int line)
        {
            var v = new double[words.Length - from];
            for (int j = from; j < words.Length; ++j)
                if (!double.TryParse(words[j], NumberStyles.Float, Inv, out v[j - from]))
                    throw Bad(line, $"'{words[j]}' is not a number");
            return v;
        }
    }

    // ---- writing -----------------------------------------------------------------------------

    /// <summary>The database as text, in the format it is read in; Parse(Write()) gives it back.</summary>
    public string Write()
    {
        var s = new StringBuilder();
        s.AppendLine("# ThinFilm material database. Wavelengths in micrometres throughout.");
        s.AppendLine("# Choosing a material copies it into the design; editing this file never");
        s.AppendLine("# changes a design already made from it.");
        foreach (var m in Materials)
        {
            s.AppendLine();
            s.Append(Block(m));
        }
        return s.ToString();
    }

    /// <summary>One material as the file holds it, MATERIAL to END.</summary>
    private static string Block(NamedMaterial m)
    {
        var s = new StringBuilder();
        s.AppendLine($"MATERIAL {m.Name}");
        var model = m.Model;
        if (model.Kind.Equals("TABLE", StringComparison.OrdinalIgnoreCase))
        {
            s.AppendLine("  TABLE");
            foreach (var r in model.Table ?? [])
                s.AppendLine("    " + string.Join("  ", r.Select(v => v.ToString("R", Inv))));
        }
        else
            s.AppendLine($"  {model.Kind.ToUpperInvariant()} "
                         + string.Join(" ", model.Coefficients.Select(v => v.ToString("R", Inv))));
        if (model.MinMicrons is { } lo && model.MaxMicrons is { } hi
            && !model.Kind.Equals("TABLE", StringComparison.OrdinalIgnoreCase))
            s.AppendLine($"  RANGE {lo.ToString("R", Inv)} {hi.ToString("R", Inv)}");
        if (!string.IsNullOrWhiteSpace(model.Source)) s.AppendLine($"  SOURCE {model.Source}");
        s.AppendLine("END");
        return s.ToString();
    }

    // ---- saving one material -----------------------------------------------------------------

    /// <summary>
    /// A material written into the database file at <paramref name="path"/>, so the next design can
    /// take it: added at the end, or - with <paramref name="replace"/> - in place of the one of the
    /// same name. ONLY THAT MATERIAL'S LINES CHANGE: every comment and every other material stays
    /// as the person (or the shipped file) wrote it, which Write, rewriting the whole file, would
    /// not keep. The shipped databases are never written; a catalogue glass and air are not
    /// materials a database holds. Returns the database as saved, or null with the reason.
    /// </summary>
    public static MaterialDatabase? Save(string path, NamedMaterial material, bool replace, out string? problem)
    {
        problem = null;
        string name = material.Name.Trim();
        if (name.Length == 0) { problem = "a material needs a name"; return null; }
        if (name.Contains('#')) { problem = "a material's name cannot contain #, which starts a comment in the database file"; return null; }
        if (name.Equals(Air.Name, StringComparison.OrdinalIgnoreCase)) { problem = "air is built in; it is not saved to the database"; return null; }
        if (material.Model.IsCatalogueGlass)
        { problem = $"{name} is a catalogue glass: it is always taken from its catalogue, so it is not saved to the database"; return null; }
        string full = System.IO.Path.GetFullPath(path);
        if (new[] { ShippedPath, ShippedLiteraturePath }.Any(p => string.Equals(System.IO.Path.GetFullPath(p), full, StringComparison.OrdinalIgnoreCase)))
        { problem = "the database that ships with the program is never changed; choose your own in Edit > System Parameters"; return null; }

        string text = File.Exists(path) ? File.ReadAllText(path) : "";
        string newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        string block = Block(material with { Name = name }).Replace("\r\n", "\n").TrimEnd('\n');

        // The existing material of this name, MATERIAL to END.
        int start = lines.FindIndex(l => Header(l) is { } n && n.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (start >= 0)
        {
            if (!replace) { problem = $"the database already has a material called '{Header(lines[start])}'"; return null; }
            int end = lines.FindIndex(start, l => Uncommented(l).Equals("END", StringComparison.OrdinalIgnoreCase));
            if (end < 0) { problem = $"the database's {name} has no END line; correct the file by hand first"; return null; }
            lines.RemoveRange(start, end - start + 1);
            lines.InsertRange(start, block.Split('\n'));
        }
        else
        {
            while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
            if (lines.Count > 0) lines.Add("");
            lines.AddRange(block.Split('\n'));
        }
        string written = string.Join(newline, lines) + newline;

        // Read back before writing: a file this program cannot read again is not written.
        MaterialDatabase saved;
        try { saved = Parse(written, path); }
        catch (FormatException x) { problem = $"the database would not read back ({x.Message}); it is left as it was"; return null; }
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(path, written);
        return saved;

        static string Uncommented(string line) => (line.IndexOf('#') is int h && h >= 0 ? line[..h] : line).Trim();
        static string? Header(string line)
        {
            string l = Uncommented(line);
            return l.StartsWith("MATERIAL ", StringComparison.OrdinalIgnoreCase) ? l["MATERIAL ".Length..].Trim() : null;
        }
    }
}
