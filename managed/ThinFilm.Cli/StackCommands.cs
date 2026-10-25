// StackCommands.cs — the Stack menu, the layer table, Edit > Materials, Edit > System Parameters and
// the merit table's own rows, from the command line.
//
// The user (2026-10-17): "anything we can do in the GUI we need to be able to do it from the CLI,
// MCP". Each command is one call into ThinFilm.Api (StackEdits.cs and the methods it builds on),
// the call the GUI's button makes, and writes the design back to its file or to -o.
//
// LAYERS ARE NUMBERED FROM 1, as the GUI's # column numbers them (and so does the MCP server, X1.78).
// A physical length - a thickness in nm, a bound, the maximum - is typed in the
// design's length unit, as the GUI shows it: µm in a design shown in µm. Merit wavelengths are nm.

using System.Globalization;
using System.Text.Json;
using ThinFilm.Api;

namespace ThinFilm.Cli;

public static partial class CommandLine
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>thinfilm stack string|couple|decouple|clear|bounds design.tfd [...]</summary>
    private static int Stack(ThinFilmApi api, Options o, TextWriter output, TextWriter error)
    {
        string[] actions = ["string", "couple", "decouple", "clear", "bounds", "reverse"];
        if (!Edit(api, o, actions, "stack", error, out var action, out var path, out var id)) return 1;
        string unit = api.Describe(id).Value!.WavelengthUnit;
        string said;
        switch (action)
        {
            case "string":
            {
                var text = o.String("text") ?? (o.Positional.Count > 2 ? o.Positional[2] : null);
                if (text is null) return Fail(error, "stack string needs the string: --text \"(1H 1L)4\"");
                Dictionary<char, string>? letters = null;
                if (o.String("letters") is { } map)
                {
                    letters = new();
                    foreach (var pair in map.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        var kv = pair.Split('=', 2, StringSplitOptions.TrimEntries);
                        if (kv.Length != 2 || kv[0].Length != 1)
                            return Fail(error, $"--letters wants letter=material pairs, e.g. H=TiO2,L=SiO2, not '{pair}'");
                        letters[char.ToUpperInvariant(kv[0][0])] = kv[1];
                    }
                }
                var r = api.ApplyDesignText(id, text, letters, o.String("unit") ?? "QWOT",
                                            o.Flag("substrate-first"), !o.Flag("no-pickups"));
                if (!r.Ok) return Fail(error, r);
                said = $"the stack is now {DesignString.Describe(DesignString.Expand(text))}";
                break;
            }
            case "couple":
            {
                var r = api.CoupleLayers(id);
                if (!r.Ok) return Fail(error, r);
                said = $"{r.Value} layer{(r.Value == 1 ? "" : "s")} coupled to the first of {(r.Value == 1 ? "its" : "their")} material";
                break;
            }
            case "decouple":
            {
                var r = api.DecoupleLayers(id);
                if (!r.Ok) return Fail(error, r);
                said = $"{r.Value} pickup{(r.Value == 1 ? "" : "s")} removed";
                break;
            }
            case "clear":
            {
                var r = api.ClearLayers(id);
                if (!r.Ok) return Fail(error, r);
                said = "every layer deleted: the bare substrate";
                break;
            }
            case "reverse":
            {
                bool media = o.Flag("media");
                var r = api.ReverseLayers(id, media);
                if (!r.Ok) return Fail(error, r);
                said = media ? $"layers and media reversed: {r.Value!.Incident?.Name} is the incident medium, {r.Value.Exit?.Name} the substrate"
                             : "layers reversed; the media are where they were";
                break;
            }
            default:
            {
                int count = api.Describe(id).Value!.Layers;
                if (!Range(o.String("layers"), count, out int first, out int last, out var bad)) return Fail(error, bad!);
                string bounds = o.String("unit") ?? "nm";
                double? Bound(string name) => o.Double(name) is { } v ? Physical(bounds, unit, v) : null;
                var r = api.SetVaryConstraints(id, first, last, o.String("material"), bounds, Bound("min"), Bound("max"));
                if (!r.Ok) return Fail(error, r);
                said = $"bounds set on {r.Value!.Set.Length} layer{(r.Value.Set.Length == 1 ? "" : "s")}"
                       + (r.Value.PickedUp.Length > 0 ? $"; {r.Value.PickedUp.Length} picked up, so passed over" : "");
                break;
            }
        }
        return Wrote(api, o, id, path, said, output);
    }

    /// <summary>thinfilm layer insert|remove|move|set design.tfd [...]</summary>
    private static int Layer(ThinFilmApi api, Options o, TextWriter output, TextWriter error)
    {
        string[] actions = ["insert", "remove", "move", "set"];
        if (!Edit(api, o, actions, "layer", error, out var action, out var path, out var id)) return 1;
        var doc = api.Document(id).Value!;
        string unit = doc.WavelengthUnit;
        int count = doc.Layers.Length;
        int? Number(string name)
        {
            if (o.Double(name) is not { } v) return null;
            if (v != Math.Floor(v)) throw new FormatException($"--{name} wants a layer number, not {v}");
            return (int)v - 1;
        }
        ApiResult<DesignSummary> r;
        string said;
        switch (action)
        {
            case "insert":
                if (o.String("material") is { } material)
                {
                    double? value = o.Double("thickness");
                    if (value is null) return Fail(error, "layer insert --material needs --thickness, in the layer's unit");
                    int before = Number("before") ?? (Number("after") is { } a ? a + 1 : count);
                    string layerUnit = LayerUnits.Parse(o.String("unit")) ?? doc.ThicknessUnit;
                    r = api.AddLayer(id, before, material, Physical(layerUnit, unit, value.Value), o.String("unit"),
                                     !o.Flag("fixed"));
                    said = $"layer {before + 1} inserted: {material}";
                }
                else
                {
                    // As the Design tab's Insert: a copy of the layer named (or the last), after it.
                    int after = Number("after") ?? count - 1;
                    r = api.InsertLayerCopy(id, after);
                    said = count == 0 ? "layer 1 inserted" : $"layer {Math.Min(after, count - 1) + 2} inserted, a copy of layer {Math.Min(after, count - 1) + 1}";
                }
                break;
            case "remove":
            {
                if (Number("layer") is not { } layer) return Fail(error, "layer remove needs --layer <n>");
                r = api.RemoveLayer(id, layer);
                said = $"layer {layer + 1} deleted";
                break;
            }
            case "move":
            {
                if (Number("layer") is not { } layer || Number("to") is not { } to)
                    return Fail(error, "layer move needs --layer <n> --to <m>");
                r = api.MoveLayer(id, layer, to);
                said = $"layer {layer + 1} moved to {to + 1}";
                break;
            }
            default:
            {
                if (Number("layer") is not { } layer) return Fail(error, "layer set needs --layer <n>");
                if (layer < 0 || layer >= count) return Fail(error, $"there is no layer {layer + 1}; the stack has {count}");
                // The unit the layer will be in, which a physical thickness or bound is typed in.
                var unitSet = o.String("unit");
                string after = unitSet is null ? LayerUnits.UnitOf(doc, layer)
                    : unitSet.Equals("default", StringComparison.OrdinalIgnoreCase) ? doc.ThicknessUnit
                    : LayerUnits.Parse(unitSet) ?? unitSet;
                double? In(string name) => o.String(name) is "none" ? null : o.Double(name) is { } v ? Physical(after, unit, v) : null;
                bool? vary = o.String("vary")?.ToLowerInvariant() switch
                {
                    null => null, "yes" or "true" or "on" => true, "no" or "false" or "off" => false,
                    var v => throw new FormatException($"--vary wants yes or no, not '{v}'"),
                };
                LayerPickup? pickup = Number("pickup") is { } parent
                    ? new LayerPickup(parent, o.Double("scale") ?? 1.0, o.Double("offset") ?? 0.0) : null;
                r = api.EditLayer(id, layer, new LayerChange(
                    o.String("material"), unitSet, In("thickness"), vary, pickup, o.Flag("free"),
                    In("min"), In("max"), o.String("min") == "none", o.String("max") == "none"));
                said = $"layer {layer + 1} set";
                break;
            }
        }
        if (!r.Ok) return Fail(error, r);
        return Wrote(api, o, id, path, said, output);
    }

    /// <summary>thinfilm system design.tfd [--back-surface yes|no] [--substrate-mm 1] [--angles air|medium]:
    /// Edit > System Parameters' substrate and angle settings; with no change, what they are.</summary>
    private static int SystemParameters(ThinFilmApi api, Options o, TextWriter output, TextWriter error)
    {
        if (!OpenFile(api, o, 0, error, out var path, out var id)) return 1;
        string Say(DesignSummary d) =>
            (d.BackSurface
                ? $"R and T include the back surface of a {d.SubstrateThicknessMm.ToString("0.###", Inv)} mm substrate"
                : $"R and T are the coated surface alone (substrate {d.SubstrateThicknessMm.ToString("0.###", Inv)} mm, unused)")
            + (d.AngleInAir ? "; angles of incidence are in air" : "; angles of incidence are in the incident medium");
        bool? back = o.String("back-surface")?.ToLowerInvariant() switch
        {
            null => null, "yes" or "true" or "on" => true, "no" or "false" or "off" => false,
            var v => throw new FormatException($"--back-surface wants yes or no, not '{v}'"),
        };
        bool? air = o.String("angles")?.ToLowerInvariant() switch
        {
            null => null, "air" => true, "medium" or "incident" => false,
            var v => throw new FormatException($"--angles wants air or medium, not '{v}'"),
        };
        if (back is null && air is null && o.Double("substrate-mm") is null)
        {
            output.WriteLine($"  {Say(api.Describe(id).Value!)}");
            return 0;
        }
        var r = api.SetSystem(id, new SystemChange(back, o.Double("substrate-mm"), air));
        if (!r.Ok) return Fail(error, r);
        return Wrote(api, o, id, path, Say(r.Value!), output);
    }

    /// <summary>thinfilm units design.tfd [--thickness nm|QWOT|FWOT] [--reference 550] [--wavelength nm|um] [--maximum 600]</summary>
    private static int Units(ThinFilmApi api, Options o, TextWriter output, TextWriter error)
    {
        if (!OpenFile(api, o, 0, error, out var path, out var id)) return 1;
        var d = api.Describe(id).Value!;
        if (o.String("thickness") is null && o.Double("reference") is null && o.String("wavelength") is null
            && o.Double("maximum") is null)
        {
            output.WriteLine($"  thickness unit {d.ThicknessUnit}, reference {d.ReferenceWavelength.ToString("0.###", Inv)} nm, "
                             + $"wavelengths in {d.WavelengthUnit}, maximum for unconstrained layers "
                             + $"{Lengths(d.UnconstrainedMaximum, d.WavelengthUnit)}");
            return 0;
        }
        // The maximum is a physical length, typed in the unit the design is shown in - the new one,
        // when this command changes it.
        string shown = o.String("wavelength")?.Trim().ToLowerInvariant() is "um" or "µm" ? "um"
                     : o.String("wavelength") is null ? d.WavelengthUnit : "nm";
        var r = api.SetUnits(id, new UnitsChange(o.String("thickness"), o.Double("reference"), o.String("wavelength"),
                                                 o.Double("maximum") is { } m ? Physical("nm", shown, m) : null));
        if (!r.Ok) return Fail(error, r);
        var now = r.Value!;
        return Wrote(api, o, id, path, $"thickness unit {now.ThicknessUnit} at {now.ReferenceWavelength.ToString("0.###", Inv)} nm, "
                                       + $"wavelengths in {now.WavelengthUnit}, maximum {Lengths(now.UnconstrainedMaximum, now.WavelengthUnit)}",
                     output);
    }

    /// <summary>thinfilm materials design.tfd [--add A,B] [--remove C] [--replace X --with Y]
    /// [--incident air] [--substrate N-BK7]; with no change, the design's materials.</summary>
    private static int Materials(ThinFilmApi api, Options o, TextWriter output, TextWriter error)
    {
        if (!OpenFile(api, o, 0, error, out var path, out var id)) return 1;

        // --define NAME and one of --sellmeier B1,C1,… --cauchy A,B,C --constant n,k --table FILE:
        // Edit dispersion (a material of that name) or New material (one there is not), and
        // --as substrate|incident for a medium. The range and the coefficients are in µm.
        if (o.String("define") is { } defined)
        {
            string? kind = new[] { "sellmeier", "cauchy", "constant", "table" }.FirstOrDefault(k => o.String(k) is not null);
            string? text = null;
            if (kind == "table")
            {
                if (!File.Exists(o.String("table")!)) return Fail(error, $"no such file: {o.String("table")}");
                text = File.ReadAllText(o.String("table")!);
            }
            double? lo = null, hi = null;
            if (o.String("range") is { } range)
            {
                var ends = range.Split(',', StringSplitOptions.TrimEntries);
                if (ends.Length != 2 || !double.TryParse(ends[0], NumberStyles.Float, Inv, out var a)
                    || !double.TryParse(ends[1], NumberStyles.Float, Inv, out var b))
                    return Fail(error, "--range wants two numbers in µm, e.g. --range 0.54,18.2");
                (lo, hi) = (a, b);
            }
            // Only --source or --range: the material keeps its dispersion, as Edit dispersion does when
            // only those boxes change.
            if (kind is null)
            {
                if (o.String("source") is null && lo is null)
                    return Fail(error, "--define needs --sellmeier, --cauchy, --constant or --table; or only --source or --range to change those");
                var described = api.DescribeMaterial(id, defined, o.String("source"), lo, hi);
                if (!described.Ok) return Fail(error, described);
                return Wrote(api, o, id, path, $"{defined}: its dispersion kept"
                             + (o.String("source") is not null ? ", source set" : "") + (lo is not null ? ", range set" : ""), output);
            }
            var model = MaterialTables.Model(kind, o.String(kind), text, o.String("table-unit"), lo, hi, o.String("source"), out var bad);
            if (model is null) return Fail(error, bad!);
            var made = api.DefineMaterial(id, defined, model, o.String("as") ?? "coating");
            if (!made.Ok) return Fail(error, made);
            return Wrote(api, o, id, path, $"{defined}: {model.Kind.ToLowerInvariant()}"
                         + (o.String("as") is { } role && role != "coating" ? $", the {role}" : ""), output);
        }

        // --rename OLD --to NEW, and --upgrade: the Materials window's Rename and Upgrade constant materials.
        if (o.String("rename") is { } old)
        {
            if (o.String("to") is not { } to) return Fail(error, "--rename needs --to <new name>");
            var renamed = api.RenameMaterial(id, old, to);
            if (!renamed.Ok) return Fail(error, renamed);
            return Wrote(api, o, id, path, $"{old} is now {to}", output);
        }
        if (o.Flag("upgrade"))
        {
            var upgraded = api.UpgradeConstantMaterials(id, MaterialDatabase.ForPerson(out _));
            if (!upgraded.Ok) return Fail(error, upgraded);
            foreach (var line in upgraded.Value!.Replaced) output.WriteLine($"  replaced {line}");
            foreach (var name in upgraded.Value.Left) output.WriteLine($"  left {name}: the database has no {name}; --define it or --replace it");
            if (upgraded.Value.Replaced.Length == 0) { output.WriteLine("  nothing replaced"); return 0; }
            return Wrote(api, o, id, path, $"{upgraded.Value.Replaced.Length} upgraded", output);
        }

        var change = new MaterialChange(List(o.String("add")), List(o.String("remove")), o.String("replace"),
                                        o.String("with"), o.String("incident"), o.String("substrate"));
        if (change == new MaterialChange())
        {
            var set = api.MaterialSet(id).Value!;
            if (o.Json) { output.WriteLine(JsonSerializer.Serialize(set, Json)); return 0; }
            // Each with what it is, as the Materials window lists it: model, n at 550 nm, range, source.
            for (int i = 0; i < set.Materials.Length; ++i)
                output.WriteLine($"  {set.Materials[i].Name,-20} {set.LayersUsing[i],3} layer{(set.LayersUsing[i] == 1 ? " " : "s")}  {Said(set.Materials[i])}");
            output.WriteLine($"  incident  {set.Incident.Name,-16}            {Said(set.Incident)}");
            output.WriteLine($"  substrate {set.Substrate.Name,-16}            {Said(set.Substrate)}");
            return 0;
        }
        var database = MaterialDatabase.ForPerson(out var problem);
        if (problem is not null) error.WriteLine($"thinfilm: {problem}");
        var r = api.ChangeMaterials(id, database, change);
        if (!r.Ok) return Fail(error, r);
        return Wrote(api, o, id, path, $"materials: {string.Join(", ", api.MaterialNames(id).Value!)}", output);
    }

    /// <summary>A material in one line, as the Materials window says it, and where its numbers came from.</summary>
    private static string Said(NamedMaterial m) =>
        MaterialSummary.Line(m) + (m.Model.Source is { Length: > 0 } s && !m.Model.IsCatalogueGlass ? $"; {s}" : "");

    /// <summary>thinfilm database: the person's material database, as Edit > Materials lists it.</summary>
    private static int Database(Options o, TextWriter output, TextWriter error)
    {
        // --save design.tfd --material HfO2 [--replace]: a design's material into the person's
        // database, as Edit > Materials' Save to database does.
        if (o.String("save") is { } designPath)
        {
            if (o.String("material") is not { } material) return Fail(error, "database --save needs --material <name>");
            if (!File.Exists(designPath)) return Fail(error, $"no such file: {designPath}");
            var api = new ThinFilmApi();
            var opened = api.Load(File.ReadAllText(designPath));
            if (!opened.Ok) return Fail(error, opened.Error!);
            var saved = api.SaveToDatabase(opened.Value!.Id, material, o.Flag("replace"));
            if (!saved.Ok)
                return Fail(error, saved.Error!.Contains("already has", StringComparison.Ordinal)
                    ? saved.Error + "; add --replace to replace it" : saved.Error!);
            output.WriteLine($"  {saved.Value!.Name} {(saved.Value.Replaced ? "replaced in" : "saved to")} {saved.Value.Path}");
            return 0;
        }
        var database = MaterialDatabase.ForPerson(out var problem);
        if (problem is not null) error.WriteLine($"thinfilm: {problem}");
        if (o.Json) { output.WriteLine(JsonSerializer.Serialize(database.Materials.Select(m => m.Name), Json)); return 0; }
        output.WriteLine($"  {database.Path ?? "the shipped database"}: {database.Materials.Count} materials");
        foreach (var m in database.Materials) output.WriteLine($"  {m.Name,-20} {Said(m)}");
        return 0;
    }

    /// <summary>thinfilm samples [words...]: the sample designs, by folder, as File > Open sample
    /// shows them; words narrow them as its Find box does.</summary>
    private static int SampleCatalog(Options o, TextWriter output, TextWriter error)
    {
        var entries = Samples.Catalog(string.Join(' ', o.Positional));
        if (o.Json)
        {
            output.WriteLine(JsonSerializer.Serialize(entries.Select(e => new { e.Path, e.Group, e.Name, e.Source, e.Summary }), Json));
            return 0;
        }
        if (entries.Count == 0) return Fail(error, $"no sample matches '{string.Join(' ', o.Positional)}' (in {Samples.Folder})");
        output.WriteLine($"  {entries.Count} sample design{(entries.Count == 1 ? "" : "s")} in {Samples.Folder}");
        foreach (var group in entries.GroupBy(e => e.Group))
        {
            output.WriteLine();
            output.WriteLine($"  {group.Key}/");
            int width = group.Max(e => e.Name.Length);
            foreach (var e in group) output.WriteLine($"    {e.Name.PadRight(width)}  {e.Summary}".TrimEnd());
        }
        return 0;
    }

    /// <summary>thinfilm new --name N --materials A,B --substrate S [--incident air] [--reference 550]
    /// [--layers 4] [--unit nm|um]: File > New design.</summary>
    private static int NewFromScratch(ThinFilmApi api, Options o, TextWriter output, TextWriter error)
    {
        var database = MaterialDatabase.ForPerson(out var problem);
        if (problem is not null) error.WriteLine($"thinfilm: {problem}");
        if (o.String("substrate") is not { } substrate) return Fail(error, "new --materials needs --substrate <material>");
        var r = api.CreateNew(database, o.String("name") ?? "untitled", o.String("incident") ?? "air", substrate,
                              List(o.String("materials")) ?? [], o.Double("reference") ?? 550.0,
                              (int)(o.Double("layers") ?? 4), o.String("unit") ?? "nm");
        if (!r.Ok) return Fail(error, r);
        Write(o, api.Document(r.Value!.Id).Value!.ToJson(), output);
        if (!o.Json) Summarise(r.Value, output);
        return 0;
    }

    /// <summary>thinfilm merit edit|insert|remove design.tfd --row N [...]: one row of the merit
    /// table, as its cells, its Insert and its Remove change it. Rows count from 1.</summary>
    private static int MeritRow(ThinFilmApi api, Options o, TextWriter output, TextWriter error)
    {
        if (!Edit(api, o, ["edit", "insert", "remove"], "merit", error, out var action, out var path, out var id)) return 1;
        int rows = api.Operands(id).Value?.Length ?? 0;
        int? row = o.Double("row") is { } v ? (int)v - 1 : null;
        ApiResult<DesignSummary> r;
        string said;
        switch (action)
        {
            case "insert":
            {
                int after = o.Double("after") is { } a ? (int)a - 1 : rows - 1;
                r = api.InsertOperandCopy(id, after);
                said = after >= 0 && after < rows ? $"row {after + 2} inserted, a copy of row {after + 1}" : $"row {rows + 1} inserted";
                break;
            }
            case "remove":
                if (row is null) return Fail(error, "merit remove needs --row <n>");
                r = api.RemoveOperand(id, row.Value);
                said = $"row {row + 1} removed";
                break;
            default:
                if (row is null) return Fail(error, "merit edit needs --row <n>");
                r = api.EditOperand(id, row.Value, new OperandChange(
                    o.String("code"), o.String("mode"), o.Double("target"), o.Double("target2"), o.Double("weight"),
                    o.Double("wavelength"), o.Double("wavelength2"), o.Double("points") is { } n ? (int)n : null,
                    o.Double("aoi"), o.Double("limit") is { } limit ? Physical("nm", api.Describe(id).Value!.WavelengthUnit, limit) : null,
                    o.Double("temperature")));
                said = $"row {row + 1} edited";
                break;
        }
        if (!r.Ok) return Fail(error, r);
        string written = o.Output ?? path;
        Save(o, written, api.Document(id).Value!.ToJson());
        if (o.Json) { output.WriteLine(JsonSerializer.Serialize(r.Value, Json)); return 0; }
        output.WriteLine($"  {said}; {r.Value!.Operands} rows, merit "
                         + (r.Value.Blocked is { } b ? $"not evaluated ({b})" : r.Value.Merit.ToString("G6", Inv)));
        if (row is { } shown && action == "edit" && api.Operands(id).Value is { } edits && shown < edits.Length)
        {
            var e = edits[shown];
            output.WriteLine($"  row {shown + 1}: {e.Code} {e.Comparison} {e.Target.ToString("G6", Inv)}"
                             + (e.Wavelengths.Length == 0 ? "" : e.Wavelengths.Length == 1
                                 ? $" at {e.Wavelengths[0].ToString("0.###", Inv)} nm"
                                 : $" from {e.Wavelengths.Min().ToString("0.###", Inv)} to {e.Wavelengths.Max().ToString("0.###", Inv)} nm, {e.Wavelengths.Length} points")
                             + (e.Wavelengths.Length == 0 ? "" : $", {e.AngleDegrees.ToString("0.##", Inv)}°")
                             + $", weight {e.Weight.ToString("G6", Inv)}");
        }
        output.WriteLine($"  wrote {written}");
        return 0;
    }

    // ---- the parts ---------------------------------------------------------------------------

    /// <summary>An action and a design file, the design loaded.</summary>
    private static bool Edit(ThinFilmApi api, Options o, string[] actions, string command, TextWriter error,
                             out string action, out string path, out string id)
    {
        action = path = id = "";
        if (o.Positional.Count < 2 || !actions.Contains(o.Positional[0]))
        {
            Fail(error, $"{command} needs an action and a design file: thinfilm {command} {string.Join("|", actions)} <design.tfd> [...]");
            return false;
        }
        action = o.Positional[0];
        return OpenFile(api, o, 1, error, out path, out id);
    }

    private static bool OpenFile(ThinFilmApi api, Options o, int at, TextWriter error, out string path, out string id)
    {
        path = id = "";
        if (o.Positional.Count <= at) { Fail(error, "this command needs a design file"); return false; }
        path = o.Positional[at];
        if (!File.Exists(path)) { Fail(error, $"no such file: {path}"); return false; }
        var loaded = api.Load(File.ReadAllText(path));
        if (!loaded.Ok) { Fail(error, loaded); return false; }
        id = loaded.Value!.Id;
        return true;
    }

    /// <summary>The design written back (or to -o), what was done, and the stack as it now is.</summary>
    private static int Wrote(ThinFilmApi api, Options o, string id, string path, string said, TextWriter output)
    {
        string written = o.Output ?? path;
        Save(o, written, api.Document(id).Value!.ToJson());
        if (o.Json) { output.WriteLine(JsonSerializer.Serialize(api.Describe(id).Value, Json)); return 0; }
        output.WriteLine($"  {said}");
        PrintStack(api, id, output);
        output.WriteLine($"  wrote {written}");
        return 0;
    }

    /// <summary>The layer table as the Design tab shows it, numbered from 1.</summary>
    private static void PrintStack(ThinFilmApi api, string id, TextWriter output)
    {
        var e = api.Evaluate(id).Value;
        var d = api.Describe(id).Value!;
        if (e is null) return;
        string len = d.WavelengthUnit == "um" ? "um" : "nm";
        output.WriteLine($"  {d.Layers} layer{(d.Layers == 1 ? "" : "s")}, {Lengths(d.TotalThickness, d.WavelengthUnit)}, merit "
                         + (d.Blocked is { } b ? $"not evaluated ({b})" : d.Merit.ToString("G6", Inv)));
        if (e.Layers.Length == 0) return;
        output.WriteLine($"  {"#",4}  {"material",-14}{"value",10} {"unit",-5}{len,11}  vary  {"bounds",-18}pickup");
        foreach (var l in e.Layers)
        {
            // A layer in nm is shown in the design's length unit, its bounds with it.
            bool nm = l.Unit == "nm";
            string Shown(double v) => nm ? Physical(v, d.WavelengthUnit) : v.ToString("0.####", Inv);
            string value = double.IsFinite(l.Value) ? Shown(l.Value) : "";
            string bounds = l.Min is null && l.Max is null ? ""
                : $"{(l.Min is { } lo ? Shown(lo) : "0")} to {(l.Max is { } hi ? Shown(hi) : "max")}";
            string pickup = l.Pickup is { } p
                ? $"{p.Scale.ToString("0.####", Inv)} x layer {p.Parent + 1}" + (p.Offset != 0 ? $" + {p.Offset.ToString("0.###", Inv)}" : "")
                : "";
            output.WriteLine($"  {l.Index + 1,4}  {l.Material,-14}{value,10} {(nm ? len : l.Unit),-5}{Physical(l.Thickness, d.WavelengthUnit),11}  "
                             + $"{(l.Variable ? "yes" : "no"),-4}  {bounds,-18}{pickup}".TrimEnd());
        }
    }

    /// <summary>A range of layers, "3-7" or "4", from 1; all of them when not given.</summary>
    private static bool Range(string? text, int count, out int first, out int last, out string? problem)
    {
        first = 0; last = count - 1; problem = null;
        if (text is null) return true;
        var parts = text.Split('-', 2, StringSplitOptions.TrimEntries);
        if (!int.TryParse(parts[0], NumberStyles.Integer, Inv, out first)
            || !int.TryParse(parts.Length > 1 ? parts[1] : parts[0], NumberStyles.Integer, Inv, out last))
        { problem = $"--layers wants a layer or a range, e.g. 3 or 3-7, not '{text}'"; return false; }
        first -= 1; last -= 1;
        return true;
    }

    /// <summary>A length typed in the design's length unit, in nm when <paramref name="unit"/> is
    /// physical; a QWOT or FWOT count as it is.</summary>
    private static double Physical(string unit, string lengthUnit, double typed) =>
        unit.Equals("nm", StringComparison.OrdinalIgnoreCase) && lengthUnit == "um" ? typed * 1000.0 : typed;

    private static string Physical(double nm, string lengthUnit) =>
        lengthUnit == "um" ? (nm / 1000.0).ToString("0.######", Inv) : nm.ToString("0.###", Inv);

    private static string Lengths(double nm, string lengthUnit) =>
        $"{Physical(nm, lengthUnit)} {(lengthUnit == "um" ? "um" : "nm")}";
}
