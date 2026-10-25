// MaterialsForm.cs — Edit > Materials, with no window in it.
//
// THE DATABASE ON ONE SIDE, THE DESIGN'S OWN COPIES ON THE OTHER. "Copy into design" takes a
// database material's definition and makes it the design's; from then on the design's copy is the
// one edited, and the database is never written from here. (The user's rule: the materials and
// the design live in one file, so a database changed later cannot break a design.)
//
// NOTHING REACHES THE DESIGN UNTIL OK. This form holds a working copy of the design's materials
// and media; copying in, removing and editing change only that, and OK hands the whole set to the
// API in one call (ReplaceMaterials). Cancel discards the form, and there is nothing to revert -
// unlike LensHH-LT's Variable Editor, which writes through and whose Cancel undoes nothing.

using System.Globalization;
using ThinFilm.Api;

namespace ThinFilm.Gui.Core;

public enum MaterialRole { Incident, Substrate, Coating }

/// <summary>One of the design's materials in the form: its role, its definition, which design
/// material it was (-1 if copied in here), and how many layers use it.</summary>
public sealed record MaterialEntry(MaterialRole Role, NamedMaterial Material, int Was, int LayersUsing)
{
    public string Label => Role switch
    {
        MaterialRole.Incident => $"Incident medium: {Material.Name}",
        MaterialRole.Substrate => $"Substrate: {Material.Name}",
        _ => LayersUsing > 0 ? $"{Material.Name}  ({LayersUsing} layer{(LayersUsing == 1 ? "" : "s")})"
                             : Material.Name,
    } + (MaterialsForm.IsConstant(Material) ? $"  ⚠ {MaterialsForm.Describe(Material.Model)}" : "");
}

public sealed class MaterialsForm
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>"nm" or "um": how the design shows wavelengths, which the summaries, the preview
    /// axis and its range text follow. Material definitions themselves stay in µm.</summary>
    public string WavelengthUnit { get; init; } = "nm";

    public MaterialsForm(MaterialDatabase database, MaterialSet design)
    {
        Database = database;
        Entries.Add(new MaterialEntry(MaterialRole.Incident, design.Incident, -1, 0));
        Entries.Add(new MaterialEntry(MaterialRole.Substrate, design.Substrate, -1, 0));
        for (int i = 0; i < design.Materials.Length; ++i)
            Entries.Add(new MaterialEntry(MaterialRole.Coating, design.Materials[i], i, design.LayersUsing[i]));
    }

    /// <summary>The database on the left; Save to database reads it again after writing.</summary>
    public MaterialDatabase Database { get; private set; }

    // ---- a material the database does not have ------------------------------------------------

    /// <summary>
    /// A material made from nothing - its dispersion typed, loaded or imported in the dispersion
    /// dialog - as a new coating material, or as the substrate or incident medium in place of the
    /// design's. A coating material's name is made unique ("Si (2)"). Returns the entry's index, or
    /// -1 with the reason in <paramref name="problem"/>.
    /// </summary>
    public int NewMaterial(MaterialRole role, string name, MaterialModel model, out string? problem)
    {
        problem = null;
        name = name.Trim();
        if (name.Length == 0) { problem = "a material needs a name"; return -1; }
        try
        {
            var (lo, hi) = model.Range();
            model.At(1000.0 * (lo is { } a && hi is { } b ? (a + b) / 2 : lo ?? hi ?? 0.55), name);
        }
        catch (FormatException x) { problem = x.Message; return -1; }
        if (role == MaterialRole.Coating)
        {
            string unique = name;
            for (int n = 2; Entries.Any(e => e.Role == MaterialRole.Coating
                                             && string.Equals(e.Material.Name, unique, StringComparison.OrdinalIgnoreCase)); ++n)
                unique = $"{name} ({n})";
            Entries.Add(new MaterialEntry(MaterialRole.Coating, new NamedMaterial(unique, model), -1, 0));
            return Entries.Count - 1;
        }
        int i = Entries.FindIndex(e => e.Role == role);
        Entries[i] = new MaterialEntry(role, new NamedMaterial(name, model), -1, 0);
        return i;
    }

    // ---- saving a design material for the next design ----------------------------------------

    /// <summary>Why the selected material cannot be saved to the database, or null when it can.</summary>
    public string? SaveRefusal(int entry)
    {
        if (entry < 0 || entry >= Entries.Count) return "select a material in the design";
        var m = Entries[entry].Material;
        if (m.Model.IsCatalogueGlass) return $"{m.Name} is a catalogue glass, always taken from its catalogue";
        if (m.Name.Equals(MaterialDatabase.Air.Name, StringComparison.OrdinalIgnoreCase)) return "air is built in";
        return null;
    }

    /// <summary>A database material of the selected material's name, which saving would replace.</summary>
    public NamedMaterial? SaveWouldReplace(int entry) =>
        entry >= 0 && entry < Entries.Count ? Database.Find(Entries[entry].Material.Name) : null;

    /// <summary>
    /// The selected design material as it is now in this form - edits included, before OK - written
    /// into the person's own material database (ThinFilmApi.SaveToDatabase), replacing one of the
    /// same name only when <paramref name="replace"/>. The list on the left is read again, so the
    /// material is there to add at once. Returns a sentence saying what was done, or null with the
    /// reason in <paramref name="problem"/>.
    /// </summary>
    public string? SaveToDatabase(int entry, bool replace, out string? problem)
    {
        problem = SaveRefusal(entry);
        if (problem is not null) return null;
        var r = new ThinFilmApi().SaveToDatabase(Entries[entry].Material, replace);
        if (!r.Ok) { problem = r.Error; return null; }
        Database = MaterialDatabase.ForPerson(out _);
        var s = r.Value!;
        return $"{s.Name} {(s.Replaced ? "replaced in" : "saved to")} the material database ({s.Path}). "
               + "Later designs can add it; this design keeps its own copy.";
    }

    /// <summary>The design's side: incident medium, substrate, then the coating materials.</summary>
    public List<MaterialEntry> Entries { get; } = new();

    /// <summary>What the database offers, air first (it is built in, not in the file).</summary>
    public IReadOnlyList<NamedMaterial> Offered => [MaterialDatabase.Air, .. Database.Materials];

    // ---- the database side -------------------------------------------------------------------

    /// <summary>A database material's definition copied in as a coating material. A name already
    /// in the design gets a suffix, so a second, differently edited SiO2 is possible.</summary>
    public string? CopyIntoDesign(string databaseName)
    {
        var m = Find(databaseName);
        if (m is null) return $"'{databaseName}' is not in the material database";
        string name = m.Name;
        for (int i = 2; Entries.Any(e => e.Role == MaterialRole.Coating
                                         && string.Equals(e.Material.Name, name, StringComparison.OrdinalIgnoreCase)); ++i)
            name = $"{m.Name} ({i})";
        Entries.Add(new MaterialEntry(MaterialRole.Coating, m with { Name = name }, -1, 0));
        return null;
    }

    /// <summary>A database material copied in as the substrate, replacing the design's.</summary>
    public string? UseAsSubstrate(string databaseName) => UseAs(MaterialRole.Substrate, databaseName);

    /// <summary>The catalogues a substrate glass can come from (GlassCatalogs).</summary>
    public IReadOnlyList<GlassCatalog> Catalogues => GlassCatalogs.All;

    /// <summary>
    /// A catalogue glass as the substrate. NAMED, not copied (the user's rule for catalogue
    /// glasses): the design records "SCHOTT N-BK7" and takes its index from the catalogue, and the
    /// entry cannot be edited. Returns the glass's caution, if its catalogue disagrees with itself,
    /// as a note rather than a refusal.
    /// </summary>
    public string? UseGlassAsSubstrate(string catalog, string glass, out string? caution)
    {
        caution = null;
        var g = GlassCatalogs.Find(catalog, glass);
        if (g is null) return $"{catalog} {glass} is not in the glass catalogues";
        int i = Entries.FindIndex(e => e.Role == MaterialRole.Substrate);
        Entries[i] = new MaterialEntry(MaterialRole.Substrate,
            new NamedMaterial(g.FullName, MaterialModel.CatalogueGlass(g.Catalog, g.Name)), -1, 0);
        caution = g.Caution;
        return null;
    }

    /// <summary>A database material, or air, copied in as the incident medium.</summary>
    public string? UseAsIncident(string databaseName)
    {
        var m = Find(databaseName);
        if (m is null) return $"'{databaseName}' is not in the material database";
        return UseAs(MaterialRole.Incident, databaseName);
    }

    private string? UseAs(MaterialRole role, string databaseName)
    {
        var m = Find(databaseName);
        if (m is null) return $"'{databaseName}' is not in the material database";
        int i = Entries.FindIndex(e => e.Role == role);
        Entries[i] = new MaterialEntry(role, m, -1, 0);
        return null;
    }

    private NamedMaterial? Find(string name) =>
        string.Equals(name, MaterialDatabase.Air.Name, StringComparison.OrdinalIgnoreCase)
            ? MaterialDatabase.Air : Database.Find(name);

    // ---- replacing ---------------------------------------------------------------------------

    /// <summary>
    /// THE WAY FROM A CONSTANT MATERIAL TO A REAL ONE. The design's material at
    /// <paramref name="entry"/> takes a database material's definition - copied, as always - and
    /// keeps its place: every layer made of it is made of the replacement, through OK, because the
    /// entry still says which design material it was. A substrate or an incident medium is replaced
    /// the same way. Before this, getting from a sample's constant SiO2 to the database's Sellmeier
    /// SiO2 was a copy, a layer-by-layer switch on the Design tab, and a removal.
    /// </summary>
    public string? ReplaceWithDatabase(int entry, string databaseName)
    {
        if (entry < 0 || entry >= Entries.Count) return "select the design material to replace";
        var e = Entries[entry];
        if (e.Role == MaterialRole.Substrate) return UseAsSubstrate(databaseName);
        if (e.Role == MaterialRole.Incident) return UseAsIncident(databaseName);

        var m = Database.Find(databaseName);
        if (m is null) return $"'{databaseName}' is not in the material database";
        if (Entries.Where((x, i) => i != entry && x.Role == MaterialRole.Coating)
                   .Any(x => string.Equals(x.Material.Name, m.Name, StringComparison.OrdinalIgnoreCase)))
            return $"the design already has another material called '{m.Name}'; rename one of them first";
        Entries[entry] = e with { Material = m };
        return null;
    }

    /// <summary>One line of an upgrade: what a design material is now and what it would become.</summary>
    public sealed record Upgrade(int Entry, string Name, string From, string? To);

    /// <summary>
    /// What "upgrade constant materials" would do: every constant-index material in the design
    /// (media included) with a database material of the same name, and the constant ones with no
    /// match - listed, so a person sees what is left as it was, not only what changed.
    /// </summary>
    public IReadOnlyList<Upgrade> UpgradePlan()
    {
        var plan = new List<Upgrade>();
        for (int i = 0; i < Entries.Count; ++i)
        {
            var e = Entries[i];
            if (!e.Material.Model.Kind.Equals("CONSTANT", StringComparison.OrdinalIgnoreCase)) continue;
            if (e.Material.Name.Equals(MaterialDatabase.Air.Name, StringComparison.OrdinalIgnoreCase)) continue;
            var match = Database.Find(e.Material.Name);
            plan.Add(new Upgrade(i, e.Material.Name, Summary(e.Material, WavelengthUnit),
                                 match is null ? null : Summary(match, WavelengthUnit)));
        }
        return plan;
    }

    /// <summary>The upgrade applied to the form, and a sentence saying what it did and did not do.</summary>
    public string UpgradeAll()
    {
        var plan = UpgradePlan();
        if (plan.Count == 0) return "Nothing to upgrade: no constant-index material in this design.";
        var done = new List<string>();
        var left = new List<string>();
        foreach (var u in plan)
        {
            if (u.To is null) { left.Add(u.Name); continue; }
            if (ReplaceWithDatabase(u.Entry, u.Name) is null) done.Add($"{u.Name} ({u.From} → {u.To})");
            else left.Add(u.Name);
        }
        string s = done.Count > 0 ? "Replaced " + string.Join("; ", done) + "." : "Nothing replaced.";
        if (left.Count > 0)
            s += $" No database material of the same name for {string.Join(", ", left)}: left as "
                 + "it is - replace it with a chosen one, or edit it.";
        return s;
    }

    /// <summary>A model in a few words: "constant n = 1.46", "Sellmeier (Malitson 1965...)".</summary>
    public static string Describe(MaterialModel m) => MaterialSummary.Describe(m);

    // ---- saying what a material is -----------------------------------------------------------

    /// <summary>
    /// A material in one line, so a list says what each entry is without a click:
    /// "Sellmeier, n = 1.4599 at 550 nm, 0.21–3.71 µm". THE USER'S REPORT: "Replace SiO2 with
    /// database SiO2 - how does the user know what database SiO2 is? If it is constant n, that is
    /// really bad." Nothing on the button or the list said it was Malitson's fused silica.
    /// </summary>
    public static string Summary(NamedMaterial m, string unit = "nm") => MaterialSummary.Line(m, unit);

    /// <summary>A constant index - not a real material, and the thing to replace. Air is exactly
    /// constant, so it is not flagged.</summary>
    public static bool IsConstant(NamedMaterial m) =>
        m.Model.Kind.Equals("CONSTANT", StringComparison.OrdinalIgnoreCase)
        && !m.Name.Equals(MaterialDatabase.Air.Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>The same definition, whatever the name or the source note says.</summary>
    public static bool SameDefinition(MaterialModel a, MaterialModel b) =>
        a.Kind.Equals(b.Kind, StringComparison.OrdinalIgnoreCase)
        && a.Coefficients.SequenceEqual(b.Coefficients)
        && (a.Table is null ? b.Table is null
            : b.Table is not null && a.Table.Length == b.Table.Length
              && a.Table.Zip(b.Table).All(r => r.First.SequenceEqual(r.Second)))
        && a.MinMicrons == b.MinMicrons && a.MaxMicrons == b.MaxMicrons
        && string.Equals(a.Catalog, b.Catalog, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Glass, b.Glass, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Said before Add is pressed: the design already holds a material with exactly this
    /// definition, so adding makes a second, separate copy. That is how the user's design came to
    /// have an unused "SiO2 (2)" identical to its SiO2.
    /// </summary>
    public string? AddWarning(string databaseName)
    {
        var m = Find(databaseName);
        if (m is null) return null;
        var same = Entries.FirstOrDefault(e => e.Role == MaterialRole.Coating && SameDefinition(e.Material.Model, m.Model));
        return same is null ? null
            : $"The design already has {same.Material.Name} with exactly this definition. Adding makes a second, "
              + "separate copy - useful only if you will then edit its dispersion.";
    }

    /// <summary>
    /// What Replace will do, both sides spelled out: "Now: constant n = 1.46. Becomes: Sellmeier,
    /// n = 1.4599 at 550 nm, 0.21–3.71 µm (database SiO2). Its 2 layers follow." Null when it
    /// would change nothing - the definitions are already the same.
    /// </summary>
    public string? ReplacePreview(int entry, NamedMaterial replacement, string from, out bool changes)
    {
        changes = false;
        if (entry < 0 || entry >= Entries.Count) return null;
        var e = Entries[entry];
        if (SameDefinition(e.Material.Model, replacement.Model))
            return $"{e.Material.Name} is already identical to the {from} {replacement.Name}; replacing would change nothing.";
        changes = true;
        string layers = e.LayersUsing switch
        {
            0 => "",
            1 => " Its 1 layer follows.",
            int n => $" Its {n} layers follow.",
        };
        return $"Now: {Summary(e.Material, WavelengthUnit)}.\nBecomes: {Summary(replacement, WavelengthUnit)} ({from} {replacement.Name}).{layers}";
    }

    /// <summary>
    /// For a constant-index design material, what to do about it - and, when the database has
    /// nothing of that name (Nb2O5), that Upgrade cannot help and Edit dispersion is the way.
    /// </summary>
    public string? ConstantAdvice(int entry)
    {
        if (entry < 0 || entry >= Entries.Count) return null;
        var (role, m) = (Entries[entry].Role, Entries[entry].Material);
        if (!IsConstant(m)) return null;
        string what = $"{m.Name} has a constant index ({Describe(m.Model)} at every wavelength), which no real material has.";
        var match = Database.Find(m.Name);
        return match is not null
            ? $"{what} The database's {match.Name} is {Summary(match, WavelengthUnit)}: select it on the left and press Replace, "
              + "or press Upgrade constant materials."
            : role == MaterialRole.Substrate
                ? $"{what} The database has no {m.Name}, so Upgrade cannot help: select a catalogue glass "
                  + "(or a database material) on the left and press Replace, or use Edit dispersion…."
                : $"{what} The database has no {m.Name}, so Upgrade cannot help: give it a real dispersion with "
                  + "Edit dispersion…, or select another database material on the left and press Replace.";
    }

    // ---- the design side ---------------------------------------------------------------------

    /// <summary>A coating material dropped from the design - refused while a layer uses it.</summary>
    public string? Remove(int entry)
    {
        if (entry < 0 || entry >= Entries.Count) return "select a material";
        var e = Entries[entry];
        if (e.Role != MaterialRole.Coating) return "the incident medium and the substrate can be replaced, not removed";
        if (e.LayersUsing > 0)
            return $"{e.Material.Name} is used by {e.LayersUsing} layer{(e.LayersUsing == 1 ? "" : "s")}; "
                   + "change those layers' material first";
        if (Entries.Count(x => x.Role == MaterialRole.Coating) == 1) return "a design needs at least one coating material";
        Entries.RemoveAt(entry);
        return null;
    }

    /// <summary>
    /// The design's copy given a new name and definition - what OK in the coefficients dialog
    /// does. The same checks as any edit: a catalogue glass is not editable, a name must be the
    /// design's only one, an incident medium must be clear, and the model must evaluate.
    /// </summary>
    public string? SetDefinition(int entry, string name, MaterialModel model)
    {
        if (entry < 0 || entry >= Entries.Count) return "select a material";
        var e = Entries[entry];
        if (e.Material.Model.IsCatalogueGlass)
            return $"{e.Material.Name} is a catalogue glass: the vendor's data, named by the design and "
                   + "not copied into it, so it cannot be edited. Choose another substrate instead.";
        name = name.Trim();
        if (name.Length == 0) return "a material needs a name";
        if (e.Role == MaterialRole.Coating
            && Entries.Where((x, i) => i != entry && x.Role == MaterialRole.Coating)
                      .Any(x => string.Equals(x.Material.Name, name, StringComparison.OrdinalIgnoreCase)))
            return $"the design already has a material called '{name}'";
        try
        {
            var (lo, hi) = model.Range();
            model.At(1000.0 * (lo is { } a && hi is { } b ? (a + b) / 2 : lo ?? hi ?? 0.55), name);
        }
        catch (FormatException x) { return x.Message; }
        Entries[entry] = e with { Material = new NamedMaterial(name, model) };
        return null;
    }


    /// <summary>
    /// n and k across the material's range, for the preview: its stated range, or 0.3 to 1.2 µm
    /// when it has none - which is said, so an unbounded Cauchy is not mistaken for a validated
    /// one. Null with the reason when the definition does not evaluate.
    /// </summary>
    public static (double[] X, double[] N, double[] K, string Span)? Preview(NamedMaterial m, out string? problem,
                                                                          string unit = "nm")
    {
        problem = null;
        var (lo, hi) = m.Model.Range();
        double a = lo ?? 0.3, b = hi ?? 1.2;
        if (b - a > 3.0) b = a + 3.0;   // a 7 µm range plotted whole hides the visible
        const int n = 121;
        var nm = new double[n]; var ns = new double[n]; var ks = new double[n];
        try
        {
            for (int i = 0; i < n; ++i)
            {
                nm[i] = 1000.0 * (a + (b - a) * i / (n - 1.0));
                (ns[i], ks[i], _) = m.Model.At(nm[i], m.Name);
            }
        }
        catch (FormatException e) { problem = e.Message; return null; }
        string Range(double? x, double? y) => MeritRow.ShowMicronRange(x, y, unit);
        string span = lo is null && hi is null
            ? $"{Range(a, b)} shown; this material states no valid range"
            : b < (hi ?? b) - 1e-9
                ? $"{Range(a, b)} shown, of its valid range {Range(lo ?? a, hi)}"
                : $"{Range(a, b)}, its valid range";
        if (unit == "um") for (int i = 0; i < n; ++i) nm[i] /= 1000.0;
        // A catalogue glass whose own nd disagrees with its formula says so here too.
        if (m.Model.IsCatalogueGlass && GlassCatalogs.Find(m.Model.Catalog ?? "", m.Model.Glass ?? "")?.Caution is { } caution)
            span += ". " + caution;
        return (nm, ns, ks, span);
    }

    // ---- handing it back ---------------------------------------------------------------------

    public MaterialEdit[] CoatingEdits() =>
        Entries.Where(e => e.Role == MaterialRole.Coating).Select(e => new MaterialEdit(e.Material, e.Was)).ToArray();

    public NamedMaterial Incident => Entries.First(e => e.Role == MaterialRole.Incident).Material;
    public NamedMaterial Substrate => Entries.First(e => e.Role == MaterialRole.Substrate).Material;

}
