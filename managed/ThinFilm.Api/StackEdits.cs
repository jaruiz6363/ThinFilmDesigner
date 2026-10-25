// StackEdits.cs — the GUI's stack, layer, material, unit and merit-row edits as calls the CLI and
// the MCP make too.
//
// The user (2026-10-17): "anything we can do in the GUI we need to be able to do it from the CLI,
// MCP. Think along calling common engine routines" - which takes in the Stack menu's buttons. The
// API already had each edit in the form a dialog hands it over (LayerProperties with every field,
// ReplaceMaterials with the whole list). A command line names one thing to change and leaves the
// rest, and names materials rather than counting them, so these take a partial change by name,
// fill in the rest from the design as it is, and call the same methods the GUI calls. Where the
// GUI decided something itself - what Insert layer copies, what a merit row becomes when its code
// changes, which material a letter in a design string stands for - that decision is here now, and
// the GUI calls it too, so the three cannot disagree.

using System.Globalization;

namespace ThinFilm.Api;

/// <summary>A change to one layer; null leaves a field as it is. Material is by name. Unit is nm,
/// QWOT, FWOT, or "default" to follow the design's. Value, Min and Max are in the layer's unit
/// (its new one, when Unit changes). Free removes the layer's pickup; NoMin and NoMax its bounds.
/// A value not given keeps the layer's number in its unit (a QWOT layer given a new material stays
/// the same number of quarter waves, as in Layer properties) unless the unit changes, when the
/// layer keeps its nanometres.</summary>
public sealed record LayerChange(
    string? Material = null, string? Unit = null, double? Value = null, bool? Vary = null,
    LayerPickup? Pickup = null, bool Free = false, double? Min = null, double? Max = null,
    bool NoMin = false, bool NoMax = false);

/// <summary>Edit > Materials as a change: materials added from the database, removed (only if
/// no layer uses them), one replaced by another from the database (its layers follow), and the
/// incident medium and substrate chosen from the database. All by name.</summary>
public sealed record MaterialChange(
    string[]? Add = null, string[]? Remove = null, string? Replace = null, string? With = null,
    string? Incident = null, string? Substrate = null);

/// <summary>Edit > System Parameters as a change: the thickness unit (nm, QWOT, FWOT) and reference
/// wavelength (nm), the wavelength unit (nm or um), and the maximum for unconstrained layers (nm).</summary>
public sealed record UnitsChange(string? Thickness = null, double? ReferenceNm = null,
                                 string? Wavelength = null, double? MaximumNm = null);

/// <summary>Edit > System Parameters as a change: whether R and T include the substrate's back
/// surface, the substrate thickness in mm, and whether angles of incidence are given in air
/// rather than in the incident medium. Null leaves a setting as it is.</summary>
public sealed record SystemChange(bool? BackSurface = null, double? SubstrateMm = null, bool? AngleInAir = null);

/// <summary>One merit row's cells, as the table edits them; null leaves a cell as it is. Mode is
/// Target, Min, Max, Range or Report; wavelengths in nm; Limit (MINT and MAXT) in nm. Wavelength2
/// with Points makes or reshapes a band of evenly spaced points. Temperature (EMIT, AERATIO) in K.</summary>
public sealed record OperandChange(
    string? Code = null, string? Mode = null, double? Target = null, double? Target2 = null,
    double? Weight = null, double? Wavelength = null, double? Wavelength2 = null, int? Points = null,
    double? Angle = null, double? Limit = null, double? Temperature = null);

public sealed partial class ThinFilmApi
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ---- layers ------------------------------------------------------------------------------

    /// <summary>One layer changed, the rest of it as it was: Layer properties, a field at a time.</summary>
    public ApiResult<DesignSummary> EditLayer(string id, int layer, LayerChange c)
    {
        if (!Find(id, out var s, out var fail)) return ApiResult<DesignSummary>.Failure(fail);
        var doc = Current(s);
        if (layer < 0 || layer >= doc.Layers.Length)
            return ApiResult<DesignSummary>.Failure(NoLayer(doc, layer));
        var l = doc.Layers[layer];

        int material = l.Material;
        if (c.Material is { } name && !MaterialIndex(doc, name, out material, out var unknown))
            return ApiResult<DesignSummary>.Failure(unknown!);
        string? unitSetting = l.Unit;
        if (c.Unit is { } u)
        {
            if (u.Trim().Equals("default", StringComparison.OrdinalIgnoreCase)) unitSetting = null;
            else if ((unitSetting = LayerUnits.Parse(u)) is null)
                return ApiResult<DesignSummary>.Failure($"'{u}' is not a thickness unit; use nm, QWOT, FWOT or default");
        }
        if (c.Pickup is { } p && (p.Parent < 0 || p.Parent >= doc.Layers.Length || p.Parent == layer))
            return ApiResult<DesignSummary>.Failure(p.Parent == layer
                ? $"layer {layer + 1} cannot pick up from itself" : NoLayer(doc, p.Parent));

        var layers = doc.Layers.ToArray();
        layers[layer] = l with { Material = material, Unit = unitSetting };
        var next = doc with { Layers = layers };
        string oldUnit = LayerUnits.UnitOf(doc, layer), newUnit = LayerUnits.UnitOf(next, layer);
        double perNm;
        try { perNm = LayerUnits.PerNm(next, material, newUnit); }
        catch (FormatException e) { return ApiResult<DesignSummary>.Failure($"layer {layer + 1}: {e.Message}"); }

        double value = c.Value ?? (oldUnit == newUnit ? LayerUnits.ValueOf(doc, layer) : l.Thickness * perNm);
        return SetLayerProperties(id, layer, new LayerProperties(
            material, unitSetting, value, c.Vary ?? (l.Variable && l.Pickup is null),
            c.Free ? null : c.Pickup ?? l.Pickup,
            c.NoMin ? null : c.Min ?? l.Min * perNm,
            c.NoMax ? null : c.Max ?? l.Max * perNm));
    }

    /// <summary>
    /// Insert layer, as the Design tab's button does it: a copy of layer <paramref name="after"/>
    /// placed after it (the last layer when <paramref name="after"/> is out of range) - its
    /// material, thickness, unit, vary, bounds and pickup, so a layer in a periodic stack gets
    /// another tied to the same parent. A copy of a parent is not tied to it. An empty stack gets one
    /// quarter-wave of the first material in the design's unit, or 100 nm when that is nm.
    /// </summary>
    public ApiResult<DesignSummary> InsertLayerCopy(string id, int after)
    {
        if (!Find(id, out var s, out var fail)) return ApiResult<DesignSummary>.Failure(fail);
        var doc = Current(s);
        if (doc.Layers.Length == 0)
        {
            double nm = 100.0;
            if (doc.ThicknessUnit != LayerUnits.Nm && doc.Materials.Length > 0)
            {
                try
                {
                    double perNm = LayerUnits.PerNm(doc, 0, doc.ThicknessUnit);
                    if (double.IsFinite(perNm) && perNm > 0)
                        nm = (doc.ThicknessUnit == LayerUnits.Fwot ? 0.25 : 1.0) / perNm;
                }
                catch (FormatException) { }
            }
            return InsertLayer(id, 0, 0, nm);
        }
        int source = after >= 0 && after < doc.Layers.Length ? after : doc.Layers.Length - 1;
        var copy = doc.Layers[source];
        return InsertLayer(id, source + 1, copy.Material, copy.Thickness, copy.Variable && copy.Pickup is null,
                           copy.Unit, copy.Pickup, copy.Min, copy.Max);
    }

    /// <summary>A new layer of a material named, before layer <paramref name="before"/> (the layer
    /// count appends), its thickness in <paramref name="unit"/> (nm, QWOT, FWOT; null the design's).</summary>
    public ApiResult<DesignSummary> AddLayer(string id, int before, string material, double value,
                                             string? unit = null, bool vary = true)
    {
        if (!Find(id, out var s, out var fail)) return ApiResult<DesignSummary>.Failure(fail);
        var doc = Current(s);
        if (!MaterialIndex(doc, material, out int m, out var unknown)) return ApiResult<DesignSummary>.Failure(unknown!);
        string? setting = null;
        if (unit is not null && (setting = LayerUnits.Parse(unit)) is null)
            return ApiResult<DesignSummary>.Failure($"'{unit}' is not a thickness unit; use nm, QWOT or FWOT");
        if (!(value >= 0.0) || !double.IsFinite(value))
            return ApiResult<DesignSummary>.Failure("a thickness must be zero or a positive number");
        double perNm;
        try { perNm = LayerUnits.PerNm(doc, m, setting ?? doc.ThicknessUnit); }
        catch (FormatException e) { return ApiResult<DesignSummary>.Failure(e.Message); }
        return InsertLayer(id, before, m, value / perNm, vary, setting);
    }

    /// <summary>Set vary constraints with the material named (null: every material).</summary>
    public ApiResult<BoundsReport> SetVaryConstraints(string id, int first, int last, string? material,
                                                      string unit, double? min, double? max)
    {
        if (!Find(id, out var s, out var fail)) return ApiResult<BoundsReport>.Failure(fail);
        int? m = null;
        if (material is not null)
        {
            if (!MaterialIndex(Current(s), material, out int found, out var unknown))
                return ApiResult<BoundsReport>.Failure(unknown!);
            m = found;
        }
        return SetLayerBounds(id, new BoundsRequest(first, last, m, unit, min, max));
    }

    /// <summary>Input design string with the letters' materials by name. A letter not given is
    /// guessed as the dialog guesses it (DesignString.Guess): H the highest index, L the lowest, M
    /// the middle, any other the first material whose name starts with it.</summary>
    public ApiResult<DesignSummary> ApplyDesignText(string id, string text, IReadOnlyDictionary<char, string>? letters = null,
                                                   string unit = "QWOT", bool firstNextToSubstrate = false,
                                                   bool pickups = true)
    {
        if (!Find(id, out var s, out var fail)) return ApiResult<DesignSummary>.Failure(fail);
        var doc = Current(s);
        char[] used;
        try { used = DesignString.Expand(text).Select(l => l.Letter).Distinct().ToArray(); }
        catch (FormatException e) { return ApiResult<DesignSummary>.Failure(e.Message); }
        var names = doc.Materials.Select(m => m.Name).ToArray();
        var qwot = names.Select((_, i) =>
        {
            try { return LayerUnits.PerNm(doc, i, LayerUnits.Qwot); }
            catch (FormatException) { return double.NaN; }
        }).ToArray();
        var map = new Dictionary<char, int>();
        foreach (char c in used)
        {
            if (letters is not null && letters.TryGetValue(c, out var name))
            {
                if (!MaterialIndex(doc, name, out int m, out var unknown)) return ApiResult<DesignSummary>.Failure(unknown!);
                map[c] = m;
            }
            else if (DesignString.Guess(names, qwot, c) is { } guessed) map[c] = guessed;
            else return ApiResult<DesignSummary>.Failure(
                $"no material is chosen for {c}, and none of {string.Join(", ", names)} starts with it; name one, as {c}=<material>");
        }
        return ApplyDesignString(id, new DesignStringRequest(text, map, unit, firstNextToSubstrate, pickups));
    }

    // ---- materials and units -----------------------------------------------------------------

    /// <summary>Edit > Materials as one change, from <paramref name="database"/>; applied with
    /// ReplaceMaterials, so a material still in use cannot be removed and layers follow a replacement.</summary>
    public ApiResult<DesignSummary> ChangeMaterials(string id, MaterialDatabase database, MaterialChange c)
    {
        var set = MaterialSet(id);
        if (!set.Ok) return ApiResult<DesignSummary>.Failure(set.Error!, set.Status);
        var edits = set.Value!.Materials.Select((m, i) => new MaterialEdit(m, i)).ToList();
        string? problem = null;
        NamedMaterial? From(string name)
        {
            var found = database.Resolve(name);
            problem ??= found is null
                ? $"'{name}' is not air, a catalogue glass or in the material database ({database.Path ?? "the shipped one"})" : null;
            return found;
        }
        int Index(string name)
        {
            int i = edits.FindIndex(e => e.Material.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (i < 0) problem ??= $"'{name}' is not one of this design's materials ({string.Join(", ", edits.Select(e => e.Material.Name))})";
            return i;
        }

        if ((c.Replace is null) != (c.With is null))
            return ApiResult<DesignSummary>.Failure("a replacement names both the material replaced and the one replacing it");
        if (c.Replace is { } old && c.With is { } with)
        {
            int i = Index(old);
            if (From(with) is { } m && i >= 0) edits[i] = edits[i] with { Material = m };
        }
        foreach (var name in c.Remove ?? [])
        {
            int i = Index(name);
            if (i >= 0) edits.RemoveAt(i);
        }
        foreach (var name in c.Add ?? [])
        {
            if (edits.Any(e => e.Material.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)))
                problem ??= $"{name.Trim()} is already one of this design's materials";
            else if (From(name) is { } m) edits.Add(new MaterialEdit(m, -1));
        }
        var incident = c.Incident is { } inc ? From(inc) : set.Value.Incident;
        var substrate = c.Substrate is { } sub ? From(sub) : set.Value.Substrate;
        if (problem is not null) return ApiResult<DesignSummary>.Failure(problem);
        return ReplaceMaterials(id, edits.ToArray(), incident!, substrate!);
    }

    /// <summary>Edit > System Parameters: whichever of the units and the maximum are given. Each is
    /// checked before any is applied.</summary>
    public ApiResult<DesignSummary> SetUnits(string id, UnitsChange c)
    {
        if (!Find(id, out var s, out var fail)) return ApiResult<DesignSummary>.Failure(fail);
        var doc = Current(s);
        string? thickness = c.Thickness is null ? null : LayerUnits.Parse(c.Thickness);
        if (c.Thickness is not null && thickness is null)
            return ApiResult<DesignSummary>.Failure($"'{c.Thickness}' is not a thickness unit; use nm, QWOT or FWOT");
        string? wavelength = c.Wavelength?.Trim().ToLowerInvariant() switch
        {
            null => null, "nm" => "nm", "um" or "µm" => "um", _ => "?",
        };
        if (wavelength == "?") return ApiResult<DesignSummary>.Failure($"'{c.Wavelength}' is not a wavelength unit; use nm or um");
        if (c.ReferenceNm is { } r && !(r > 0.0 && double.IsFinite(r)))
            return ApiResult<DesignSummary>.Failure("the reference wavelength must be a positive number of nanometres");
        if (c.MaximumNm is { } mx && !(mx > 0.0 && double.IsFinite(mx)))
            return ApiResult<DesignSummary>.Failure("the maximum for unconstrained layers must be positive");

        var result = Describe(id);
        if (thickness is not null || c.ReferenceNm is not null)
        {
            result = SetThicknessUnits(id, thickness ?? doc.ThicknessUnit, c.ReferenceNm ?? doc.ReferenceWavelength);
            if (!result.Ok) return result;
        }
        if (wavelength is not null && !(result = SetWavelengthUnit(id, wavelength)).Ok) return result;
        if (c.MaximumNm is { } max && !(result = SetUnconstrainedMaximum(id, max)).Ok) return result;
        return result;
    }

    /// <summary>Edit > System Parameters: the back surface, the substrate thickness and where angles
    /// of incidence are measured. The design is re-evaluated; an angle that cannot exist in the
    /// incident medium when given in air is refused and nothing changes.</summary>
    public ApiResult<DesignSummary> SetSystem(string id, SystemChange c)
    {
        if (!Find(id, out var s, out var fail)) return ApiResult<DesignSummary>.Failure(fail);
        if (c.SubstrateMm is { } mm && !(mm > 0.0 && double.IsFinite(mm)))
            return ApiResult<DesignSummary>.Failure("the substrate thickness must be a positive number of millimetres");
        var doc = Current(s);
        return Replace(s, doc with
        {
            BackSurface = c.BackSurface ?? doc.BackSurface,
            SubstrateThicknessMm = c.SubstrateMm ?? doc.SubstrateThicknessMm,
            AngleInAir = c.AngleInAir ?? doc.AngleInAir,
        });
    }

    /// <summary>File > New design… with the materials named from <paramref name="database"/>.</summary>
    public ApiResult<DesignSummary> CreateNew(MaterialDatabase database, string name, string incident, string substrate,
                                              IReadOnlyList<string> materials, double referenceNm, int layers,
                                              string wavelengthUnit = "nm")
    {
        var missing = materials.Append(incident).Append(substrate).Where(m => database.Resolve(m) is null).ToArray();
        if (missing.Length > 0)
            return ApiResult<DesignSummary>.Failure(
                $"not air, a catalogue glass or in the material database ({database.Path ?? "the shipped one"}): {string.Join(", ", missing)}");
        return CreateNew(new NewDesignRequest(name, database.Resolve(incident)!, database.Resolve(substrate)!,
            materials.Select(m => database.Resolve(m)!).ToArray(), referenceNm, layers, wavelengthUnit));
    }

    // ---- merit rows --------------------------------------------------------------------------

    /// <summary>One merit row's cells changed, as the merit table edits them (MeritTable.Change).</summary>
    public ApiResult<DesignSummary> EditOperand(string id, int index, OperandChange c)
    {
        var rows = Operands(id);
        if (!rows.Ok) return ApiResult<DesignSummary>.Failure(rows.Error!, rows.Status);
        if (index < 0 || index >= rows.Value!.Length)
            return ApiResult<DesignSummary>.Failure($"there is no merit row {index + 1}; the table has {rows.Value!.Length}");
        var next = MeritTable.Change(rows.Value[index], c, DefaultWavelength(id), out var problem);
        return problem is null ? ReplaceOperand(id, index, next) : ApiResult<DesignSummary>.Failure(problem);
    }

    /// <summary>Insert in the merit table: a copy of row <paramref name="after"/> placed after it,
    /// or, with no such row, RA at most 0.005 at the middle of the table's wavelengths, at the end.</summary>
    public ApiResult<DesignSummary> InsertOperandCopy(string id, int after)
    {
        var rows = Operands(id);
        if (!rows.Ok) return ApiResult<DesignSummary>.Failure(rows.Error!, rows.Status);
        var edits = rows.Value!;
        bool copy = after >= 0 && after < edits.Length;
        var row = copy ? edits[after] : new OperandEdit("RA", "LE", 0.005, 0.0, 1.0, [DefaultWavelength(id)], 0.0);
        return InsertOperand(id, copy ? after + 1 : edits.Length, row);
    }

    /// <summary>The wavelength a new row is given: the middle of the table's, or 550 nm.</summary>
    public double DefaultWavelength(string id)
    {
        var all = (Operands(id).Value ?? []).SelectMany(e => e.Wavelengths).OrderBy(w => w).ToArray();
        return all.Length > 0 ? all[all.Length / 2] : 550.0;
    }

    // ---- the parts ---------------------------------------------------------------------------

    private static bool MaterialIndex(DesignDocument doc, string name, out int index, out string? problem)
    {
        index = Array.FindIndex(doc.Materials, m => m.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
        problem = index >= 0 ? null
            : $"'{name}' is not one of this design's materials ({string.Join(", ", doc.Materials.Select(m => m.Name))}); "
              + "add it from the database first";
        return index >= 0;
    }

    private static string NoLayer(DesignDocument doc, int layer) =>
        doc.Layers.Length == 0 ? $"there is no layer {layer + 1}; the stack is empty"
                               : $"there is no layer {layer + 1}; the stack has {doc.Layers.Length.ToString(Inv)}";
}
