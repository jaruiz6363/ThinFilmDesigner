// NewDesignForm.cs — what File > New design asks for, with no window in it.
//
// The dialog shows this and hands it back; everything that can be wrong with a new design - no
// coating material, no layers, a reference wavelength of zero, a material with no index there - is
// checked here or in NewDesigns.Problem, where a test can reach it, and reported as one sentence
// the dialog shows above its buttons.
//
// EVERY CHOICE IS FROM THE MATERIAL DATABASE, AND EACH IS COPIED. The media and the coating
// materials are named here; the request carries their definitions, and the design keeps those
// copies. To make a substrate a little different from the database's - n = 1.517 at 550 rather
// than the fit - edit the design's copy in Edit > Materials. There is one way to customise a
// material, not two. (The n and k boxes X1.20 had here were that second way, and went with the
// constant library they edited.)

using System.Globalization;
using ThinFilm.Api;

namespace ThinFilm.Gui.Core;

public sealed class NewDesignForm
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public NewDesignForm(MaterialDatabase database)
    {
        Database = database;
        SubstrateName = database.Find("SiO2")?.Name ?? database.Materials.FirstOrDefault()?.Name ?? "";
        // N-BK7 by default when the catalogues are here: the commonest substrate there is.
        SubstrateIsGlass = GlassCatalogs.Find("SCHOTT", "N-BK7") is not null;
        Materials.AddRange(new[] { "SiO2", "TiO2" }.Where(n => database.Find(n) is not null));
    }

    public MaterialDatabase Database { get; }

    /// <summary>Air, the built-in, and then the database: what an incident medium can be.</summary>
    public IReadOnlyList<string> IncidentChoices =>
        [MaterialDatabase.Air.Name, .. Database.Materials.Select(m => m.Name)];

    /// <summary>What a database substrate can be; SubstrateIsGlass chooses a catalogue glass instead.</summary>
    public IReadOnlyList<string> SubstrateChoices => Database.Materials.Select(m => m.Name).ToArray();

    /// <summary>What a coating layer can be made of.</summary>
    public IReadOnlyList<NamedMaterial> CoatingChoices => Database.Materials;

    public string Name { get; set; } = "new design";
    public string IncidentName { get; set; } = MaterialDatabase.Air.Name;

    /// <summary>True when the incident medium is a catalogue glass (IncidentCatalog, IncidentGlass),
    /// as `thinfilm new --incident N-BK7` already allowed: the user guide's beam splitter is a plate
    /// immersed in oil index-matched to BK7, glass on both sides (2026-10-23).</summary>
    public bool IncidentIsGlass { get; set; }
    public string IncidentCatalog { get; set; } = "SCHOTT";
    public string IncidentGlass { get; set; } = "N-BK7";

    public string SubstrateName { get; set; }

    /// <summary>True when the substrate is a catalogue glass (SubstrateCatalog, SubstrateGlass),
    /// false when it is a database material (SubstrateName).</summary>
    public bool SubstrateIsGlass { get; set; }
    public string SubstrateCatalog { get; set; } = "SCHOTT";
    public string SubstrateGlass { get; set; } = "N-BK7";

    /// <summary>The substrate glass's caution, when its catalogue disagrees with itself.</summary>
    public string? SubstrateCaution => SubstrateIsGlass ? GlassCatalogs.Find(SubstrateCatalog, SubstrateGlass)?.Caution : null;

    /// <summary>The coating materials, in the order layers alternate through them.</summary>
    public List<string> Materials { get; } = new();

    /// <summary>The reference wavelength AS TYPED, in <see cref="WavelengthUnit"/>: "550" in nm, "0.55" in µm.</summary>
    public string ReferenceNm { get; set; } = "550";
    public string Layers { get; set; } = "2";

    /// <summary>"nm" or "um", from the design open when the dialog was asked for. Changing it
    /// re-expresses the reference box, so the default 550 nm reads 0.55 µm.</summary>
    public string WavelengthUnit
    {
        get => _unit;
        set
        {
            if (value == _unit) return;
            if (double.TryParse(ReferenceNm, NumberStyles.Float, Inv, out double r))
                ReferenceNm = (value == "um" ? r / 1000.0 : r * 1000.0).ToString(value == "um" ? "0.#####" : "0.###", Inv);
            _unit = value;
        }
    }
    private string _unit = "nm";

    /// <summary>The request, or null with <paramref name="problem"/> saying why not.</summary>
    public NewDesignRequest? ToRequest(out string? problem)
    {
        problem = null;
        if (!double.TryParse(ReferenceNm, NumberStyles.Float, Inv, out double reference) || !double.IsFinite(reference))
        {
            problem = "the reference wavelength is not a number";
            return null;
        }
        if (WavelengthUnit == "um") reference *= 1000.0;
        if (!int.TryParse(Layers, NumberStyles.Integer, Inv, out int layers))
        {
            problem = "the number of layers must be a whole number";
            return null;
        }

        NamedMaterial? Lookup(string name) =>
            string.Equals(name, MaterialDatabase.Air.Name, StringComparison.OrdinalIgnoreCase)
                ? MaterialDatabase.Air : Database.Find(name);

        NamedMaterial? incident;
        if (IncidentIsGlass)
        {
            var g = GlassCatalogs.Find(IncidentCatalog, IncidentGlass);
            if (g is null) { problem = $"{IncidentCatalog} {IncidentGlass} is not in the glass catalogues"; return null; }
            incident = new NamedMaterial(g.FullName, MaterialModel.CatalogueGlass(g.Catalog, g.Name));
        }
        else incident = Lookup(IncidentName);
        NamedMaterial? substrate;
        if (SubstrateIsGlass)
        {
            // A CATALOGUE GLASS IS NAMED, NOT COPIED: the design records the catalogue and the glass.
            var g = GlassCatalogs.Find(SubstrateCatalog, SubstrateGlass);
            if (g is null) { problem = $"{SubstrateCatalog} {SubstrateGlass} is not in the glass catalogues"; return null; }
            substrate = new NamedMaterial(g.FullName, MaterialModel.CatalogueGlass(g.Catalog, g.Name));
        }
        else substrate = Database.Find(SubstrateName);
        if (incident is null) { problem = $"'{IncidentName}' is not in the material database"; return null; }
        if (substrate is null) { problem = $"choose a substrate ('{SubstrateName}' is not in the material database)"; return null; }

        var materials = new List<NamedMaterial>();
        foreach (var name in Materials)
        {
            var m = Database.Find(name);
            if (m is null) { problem = $"'{name}' is not in the material database"; return null; }
            materials.Add(m);
        }

        var request = new NewDesignRequest(Name, incident, substrate, materials.ToArray(),
                                           reference, layers, WavelengthUnit);
        problem = NewDesigns.Problem(request);
        return problem is null ? request : null;
    }
}
