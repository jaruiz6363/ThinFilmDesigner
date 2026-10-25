// StackEditTests.cs — the GUI's stack, layer, material, unit and merit-row edits, from the CLI and
// the MCP, through the same API calls (StackEdits.cs).
//
// The user (2026-10-17): "anything we can do in the GUI we need to be able to do it from the CLI,
// MCP. Think along calling common engine routines. BTW, that also goes for all the very nice
// buttons you just implemented in the GUI." The checks hold each shared call to what the GUI's
// button did, then run the CLI command and the MCP tool for it.

using System.Text.Json.Nodes;
using ThinFilm.Api;
using ThinFilm.Cli;
using ThinFilm.Gui.Core;
using ThinFilm.Mcp;

namespace ThinFilm.Native.Tests;

public static class StackEditTests
{
    /// <summary>A check with ThinFilm's home - settings, the person's material database - in a
    /// folder of its own.</summary>
    private static void AtHome(Action check)
    {
        var was = Environment.GetEnvironmentVariable("THINFILM_HOME");
        Environment.SetEnvironmentVariable("THINFILM_HOME",
            Path.Combine(Path.GetTempPath(), "thinfilm-stack-tests", Guid.NewGuid().ToString("N")));
        try { check(); }
        finally { Environment.SetEnvironmentVariable("THINFILM_HOME", was); }
    }

    public static void LayersAreEditedByName(Harness h)
    {
        h.Section("EditLayer, AddLayer, InsertLayerCopy: Layer properties and Insert, a field at a time");

        var api = new ThinFilmApi();
        string id = api.OpenSample("AR-visible").Value!.Id;
        LayerReport L(int i) => api.Evaluate(id).Value!.Layers[i];

        h.Expect(api.EditLayer(id, 0, new LayerChange(Unit: "QWOT")).Ok && L(0).Unit == "QWOT"
                 && Math.Abs(L(0).Thickness - 89.04109589041096) < 1e-9,
                 $"a new unit keeps the nanometres ({L(0).Value:0.####} QWOT)");
        double qwot = L(0).Value;
        h.Expect(api.EditLayer(id, 0, new LayerChange(Material: "nb2o5")).Ok && L(0).Material == "Nb2O5"
                 && Math.Abs(L(0).Value - qwot) < 1e-12 && L(0).Thickness < 89.0,
                 "a new material, named in any case, keeps a QWOT layer's number of quarter waves");
        h.Expect(api.EditLayer(id, 0, new LayerChange(Value: 1.0, Vary: false)).Ok && L(0).Value == 1.0 && !L(0).Variable,
                 "the value in the layer's unit, and vary");
        h.Expect(api.EditLayer(id, 2, new LayerChange(Min: 10, Max: 50)).Ok && L(2).Min == 10 && L(2).Max == 50
                 && L(2).Material == "SiO2" && L(2).Thickness == 30,
                 "bounds, the rest of the layer as it was");
        h.Expect(api.EditLayer(id, 2, new LayerChange(NoMax: true)).Ok && L(2).Min == 10 && L(2).Max is null,
                 "one bound removed, the other kept");
        h.Expect(api.EditLayer(id, 3, new LayerChange(Pickup: new LayerPickup(1, 0.5))).Ok
                 && L(3).Pickup is { Parent: 1, Scale: 0.5 } && Math.Abs(L(3).Thickness - L(1).Thickness * 0.5) < 1e-9,
                 "a pickup: half of layer 2");
        h.Expect(api.EditLayer(id, 3, new LayerChange(Free: true)).Ok && L(3).Pickup is null, "and freed");
        h.Expect(api.EditLayer(id, 0, new LayerChange(Material: "TiO2")) is { Ok: false } r1 && r1.Error!.Contains("Nb2O5, SiO2"),
                 "a material the design does not have is refused, naming those it has");
        h.Expect(api.EditLayer(id, 9, new LayerChange(Vary: true)) is { Ok: false } r2 && r2.Error!.Contains("has 4"),
                 "so is a layer it does not have");
        h.Expect(api.EditLayer(id, 1, new LayerChange(Pickup: new LayerPickup(1))) is { Ok: false }, "and a layer picking itself up");

        int before = api.Describe(id).Value!.Layers;
        h.Expect(api.InsertLayerCopy(id, 2).Ok && api.Describe(id).Value!.Layers == before + 1
                 && L(3).Material == L(2).Material && L(3).Thickness == L(2).Thickness && L(3).Min == L(2).Min,
                 "Insert copies the layer after it: material, thickness, bounds");
        h.Expect(api.AddLayer(id, 0, "SiO2", 1.0, "QWOT").Ok && L(0).Material == "SiO2" && L(0).Unit == "QWOT"
                 && Math.Abs(L(0).Value - 1.0) < 1e-12,
                 "a new layer by name, in the unit given");
        h.Expect(api.ClearLayers(id).Ok && api.InsertLayerCopy(id, 0).Ok && L(0).Thickness == 100.0,
                 "an empty stack gets 100 nm of the first material, the design being in nm");
        api.SetUnits(id, new UnitsChange(Thickness: "QWOT"));
        api.ClearLayers(id);
        h.Expect(api.InsertLayerCopy(id, 0).Ok && Math.Abs(L(0).Value - 1.0) < 1e-9 && L(0).Unit == "QWOT",
                 "or one quarter wave, the design being in QWOT");

        // The GUI's Insert is this call: the workspace's copy is the API's.
        var w = new DesignWorkspace();
        w.OpenSample("AR-visible");
        w.SetLayerBounds(new BoundsRequest(1, 1, null, "nm", 20, 200));
        w.InsertLayer(1);
        var direct = new ThinFilmApi();
        string d2 = direct.OpenSample("AR-visible").Value!.Id;
        direct.SetLayerBounds(d2, new BoundsRequest(1, 1, null, "nm", 20, 200));
        direct.InsertLayerCopy(d2, 1);
        h.Expect(w.Layers.Select(l => (l.Material, l.Thickness, l.Min, l.Max))
                  .SequenceEqual(direct.Evaluate(d2).Value!.Layers.Select(l => (l.Material, l.Thickness, l.Min, l.Max))),
                 "the GUI's Insert and the API's InsertLayerCopy make the same stack");
    }

    public static void TheStackMenuByName(Harness h)
    {
        h.Section("ApplyDesignText, SetVaryConstraints, SetUnits, ChangeMaterials, CreateNew");

        AtHome(() =>
        {
            var api = new ThinFilmApi();
            string id = api.OpenSample("AR-visible").Value!.Id;
            var layers = () => api.Evaluate(id).Value!.Layers;

            h.Expect(api.ApplyDesignText(id, "(1H 1L)3 2H").Ok && layers().Length == 7
                     && layers()[0].Material == "Nb2O5" && layers()[1].Material == "SiO2" && layers()[6].Value == 2.0,
                     "a design string with H and L guessed: H the higher index (Nb2O5), L the lower");
            h.Expect(layers()[2].Pickup is { Parent: 0 } && layers()[4].Pickup is { Parent: 0 },
                     "repeated letters picked up from their first");
            h.Expect(api.ApplyDesignText(id, "1A 1B", new Dictionary<char, string> { ['A'] = "SiO2", ['B'] = "Nb2O5" }, "FWOT", pickups: false).Ok
                     && layers() is [{ Material: "SiO2", Unit: "FWOT" }, { Material: "Nb2O5" }],
                     "or the letters named, in FWOT");
            h.Expect(api.ApplyDesignText(id, "1H 1X") is { Ok: false } x && x.Error!.Contains("X="),
                     $"a letter that cannot be guessed is refused, saying how to name it");

            api.ApplyDesignText(id, "(1H 1L)2");
            h.Expect(api.DecoupleLayers(id).Value == 2 && layers().All(l => l.Pickup is null), "decouple");
            h.Expect(api.SetVaryConstraints(id, 0, 3, "SiO2", "nm", 50, 120).Value!.Set is [1, 3]
                     && api.Document(id).Value!.Layers[1].Min == 50 && api.Document(id).Value!.Layers[3].Max == 120
                     && api.Document(id).Value!.Layers[0].Min is null,
                     "vary constraints on one material, named");
            h.Expect(api.SetVaryConstraints(id, 0, 3, "MgF2", "nm", 1, 2) is { Ok: false }, "not one the design lacks");

            var u = api.SetUnits(id, new UnitsChange("FWOT", 600, "um", 500));
            var d = api.Describe(id).Value!;
            h.Expect(u.Ok && d.ThicknessUnit == "FWOT" && d.ReferenceWavelength == 600 && d.WavelengthUnit == "um"
                     && d.UnconstrainedMaximum == 500,
                     "System Parameters: thickness unit, reference, wavelength unit and maximum");
            h.Expect(api.SetUnits(id, new UnitsChange(Wavelength: "mm")) is { Ok: false }
                     && api.SetUnits(id, new UnitsChange(Thickness: "QWOT", MaximumNm: -1)) is { Ok: false }
                     && api.Describe(id).Value!.ThicknessUnit == "FWOT",
                     "a wrong one refused before anything is applied");

            var db = MaterialDatabase.ForPerson(out var problem);
            h.Expect(problem is null && db.Find("TiO2") is not null, "the person's database opens, a copy of the shipped one");
            var m = api.ChangeMaterials(id, db, new MaterialChange(Add: ["TiO2", "MgF2"], Substrate: "N-BK7"));
            var set = api.MaterialSet(id).Value!;
            h.Expect(m.Ok && set.Materials.Select(x => x.Name).SequenceEqual(["Nb2O5", "SiO2", "TiO2", "MgF2"])
                     && set.Substrate.Name == "SCHOTT N-BK7",
                     $"materials added from the database, the substrate a catalogue glass ({m.Error})");
            h.Expect(api.ChangeMaterials(id, db, new MaterialChange(Replace: "Nb2O5", With: "Ta2O5")).Ok
                     && layers()[0].Material == "Ta2O5",
                     "a replacement: its layers follow it");
            h.Expect(api.ChangeMaterials(id, db, new MaterialChange(Remove: ["SiO2"])) is { Ok: false } used
                     && used.Error!.Contains("layer"),
                     "a material in use cannot be removed");
            h.Expect(api.ChangeMaterials(id, db, new MaterialChange(Remove: ["MgF2"], Incident: "air")).Ok
                     && api.MaterialSet(id).Value!.Materials.Length == 3,
                     "one not in use can");
            h.Expect(api.ChangeMaterials(id, db, new MaterialChange(Add: ["Unobtainium"])) is { Ok: false } un
                     && un.Error!.Contains("Unobtainium") && api.MaterialSet(id).Value!.Materials.Length == 3,
                     "a material the database lacks is refused, nothing changed");

            var n = api.CreateNew(db, "From the CLI", "air", "SCHOTT N-BK7", ["TiO2", "SiO2"], 600, 6);
            h.Expect(n.Ok && n.Value!.Layers == 6 && n.Value.Materials == 2, $"New design by name ({n.Error})");
            h.Expect(api.CreateNew(db, "x", "air", "glass?", ["TiO2"], 600, 2) is { Ok: false } bad && bad.Error!.Contains("glass?"),
                     "naming what it cannot find");
        });
    }

    public static void MeritRowsChangeAsTheTableChangesThem(Harness h)
    {
        h.Section("MeritTable.Change, EditOperand, InsertOperandCopy: the merit table's cells");

        var api = new ThinFilmApi();
        string id = api.OpenSample("AR-visible").Value!.Id;
        OperandEdit Row(int i) => api.Operands(id).Value![i];
        int rows = api.Operands(id).Value!.Length;

        h.Expect(api.EditOperand(id, 0, new OperandChange(Code: "MINT")).Ok
                 && Row(0) is { Code: "MINT", Wavelengths: [], Threshold: 8.0, Comparison: "LE", Target: 0.0 },
                 "MINT: no wavelength, a limit of 8 nm, at most 0");
        h.Expect(api.EditOperand(id, 0, new OperandChange(Limit: 12)).Ok && Row(0).Threshold == 12, "its limit");
        h.Expect(api.EditOperand(id, 0, new OperandChange(Wavelength: 500)) is { Ok: false } s && s.Error!.Contains("stack"),
                 "a stack row has no wavelength");
        h.Expect(api.EditOperand(id, 0, new OperandChange(Code: "TA")).Ok && Row(0).Wavelengths.Length == 1,
                 "made spectral again, it gets a wavelength");
        h.Expect(api.EditOperand(id, 1, new OperandChange(Wavelength: 500, Wavelength2: 600, Points: 11)).Ok
                 && Row(1).Wavelengths.Length == 11 && Row(1).Wavelengths[^1] == 600,
                 "a band of evenly spaced points");
        h.Expect(api.EditOperand(id, 1, new OperandChange(Wavelength: 450)).Ok
                 && Row(1).Wavelengths[0] == 450 && Row(1).Wavelengths[^1] == 600 && Row(1).Wavelengths.Length == 11,
                 "its first wavelength moved, the band kept");
        h.Expect(api.EditOperand(id, 2, new OperandChange(Mode: "Range")).Ok && Row(2).Comparison == "RANGE"
                 && Row(2).TargetHi == Row(2).Target,
                 "Range lifts the upper target to the lower");
        h.Expect(api.EditOperand(id, 2, new OperandChange(Target: 0.01, Target2: 0.02, Weight: 3, Angle: 45)).Ok
                 && Row(2) is { Target: 0.01, TargetHi: 0.02, Weight: 3, AngleDegrees: 45 },
                 "targets, weight and angle");
        h.Expect(api.EditOperand(id, 2, new OperandChange(Mode: "sideways")) is { Ok: false }
                 && api.EditOperand(id, 2, new OperandChange(Angle: 95)) is { Ok: false }
                 && api.EditOperand(id, 2, new OperandChange(Code: "XYZ")) is { Ok: false }
                 && api.EditOperand(id, rows, new OperandChange(Weight: 1)) is { Ok: false },
                 "a wrong mode, angle, code or row is refused");
        bool inserted = api.InsertOperandCopy(id, 2).Ok;
        var (copied, original) = (Row(3), Row(2));
        h.Expect(inserted && api.Operands(id).Value!.Length == rows + 1
                 && copied with { Wavelengths = original.Wavelengths } == original && copied.Wavelengths.SequenceEqual(original.Wavelengths),
                 "Insert copies the row after it");
        h.Expect(api.RemoveOperand(id, 3).Ok && api.Operands(id).Value!.Length == rows, "Remove");

        // The GUI's cells are this call.
        var w = new DesignWorkspace();
        w.OpenSample("AR-visible");
        w.MeritRows[0].Code = "MAXT";
        var direct = new ThinFilmApi();
        string d2 = direct.OpenSample("AR-visible").Value!.Id;
        direct.EditOperand(d2, 0, new OperandChange(Code: "MAXT"));
        h.Expect(direct.Operands(d2).Value![0] is { Code: "MAXT", Threshold: 300.0 } && w.MeritRows[0].LimitText == "300",
                 "the merit table's Code cell and EditOperand make the same row");
    }

    public static void TheCliEditsTheStack(Harness h)
    {
        h.Section("thinfilm stack, layer, units, materials, merit edit|insert|remove, new --materials");

        AtHome(() =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "thinfilm-stack-tests");
            Directory.CreateDirectory(dir);
            string design = Path.Combine(dir, "stack.tfd");
            (int Code, string Out, string Err) Run(params string[] args)
            {
                var o = new StringWriter();
                var e = new StringWriter();
                int code = CommandLine.Run(args, o, e);
                return (code, o.ToString(), e.ToString());
            }
            DesignDocument Saved() => DesignDocument.FromJson(File.ReadAllText(design));

            var made = Run("new", "--materials", "TiO2,SiO2", "--substrate", "N-BK7", "--name", "CLI", "--layers", "2", "-o", design);
            h.Pin(made.Code, 0, $"new --materials makes a design ({made.Err})");
            h.Expect(Saved() is { Name: "CLI", Layers.Length: 2 } d0 && d0.Exit.Name == "SCHOTT N-BK7", "with the substrate named");

            var s = Run("stack", "string", design, "--text", "(1H 1L)4 1H");
            h.Expect(s.Code == 0 && Saved().Layers.Length == 9 && s.Out.Contains("9 layers"),
                     $"stack string replaces the stack and prints it ({s.Err})");
            h.Expect(s.Out.Contains("   1  TiO2") && s.Out.Contains("1 x layer 1"),
                     "numbered from 1, the pickups named by layer number");
            h.Expect(Run("stack", "decouple", design).Code == 0 && Saved().Layers.All(l => l.Pickup is null), "stack decouple");
            h.Expect(Run("stack", "couple", design).Code == 0 && Saved().Layers.Count(l => l.Pickup is not null) == 7, "stack couple");
            Run("stack", "decouple", design);
            h.Expect(Run("stack", "bounds", design, "--layers", "2-4", "--material", "SiO2", "--min", "40", "--max", "160").Code == 0
                     && Saved().Layers[1].Min == 40 && Saved().Layers[3].Max == 160 && Saved().Layers[2].Min is null,
                     "stack bounds on layers 2 to 4 of SiO2");

            h.Expect(Run("layer", "set", design, "--layer", "2", "--thickness", "120", "--unit", "nm", "--vary", "no").Code == 0
                     && Saved().Layers[1] is { Thickness: 120, Variable: false },
                     "layer set: layer 2 is the second layer");
            h.Expect(Run("layer", "set", design, "--layer", "2", "--min", "none").Code == 0 && Saved().Layers[1].Min is null,
                     "--min none removes a bound");
            h.Expect(Run("layer", "insert", design, "--after", "2").Code == 0 && Saved().Layers.Length == 10
                     && Saved().Layers[2].Thickness == 120, "layer insert copies layer 2 after it");
            h.Expect(Run("layer", "insert", design, "--material", "SiO2", "--thickness", "0.5", "--unit", "QWOT", "--before", "1").Code == 0
                     && Saved().Layers[0] is { Unit: "QWOT" } && Saved().Layers.Length == 11,
                     "layer insert --material at the top");
            h.Expect(Run("layer", "move", design, "--layer", "1", "--to", "3").Code == 0 && Saved().Layers[2].Unit == "QWOT", "layer move");
            h.Expect(Run("layer", "remove", design, "--layer", "3").Code == 0 && Saved().Layers.Length == 10, "layer remove");
            var refused = Run("layer", "remove", design, "--layer", "99");
            h.Expect(refused.Code == 1 && refused.Err.Contains("no layer 99"), $"a layer it lacks is refused: {refused.Err.Trim()}");

            h.Expect(Run("units", design, "--wavelength", "um", "--maximum", "0.5").Code == 0
                     && Saved() is { WavelengthUnit: "um", UpperBound: 500 },
                     "units: a maximum typed in µm in a design shown in µm");
            h.Expect(Run("layer", "set", design, "--layer", "2", "--thickness", "0.1").Code == 0
                     && Math.Abs(Saved().Layers[1].Thickness - 100) < 1e-9,
                     "and a physical thickness, 0.1 µm = 100 nm");
            var units = Run("units", design);
            h.Expect(units.Out.Contains("wavelengths in um") && units.Out.Contains("0.5 um"), $"units alone says what they are: {units.Out.Trim()}");

            h.Expect(Run("materials", design, "--add", "MgF2", "--replace", "TiO2", "--with", "Ta2O5").Code == 0
                     && Saved().Materials.Select(m => m.Name).SequenceEqual(["Ta2O5", "SiO2", "MgF2"]),
                     "materials: add and replace");
            var list = Run("materials", design);
            h.Expect(list.Out.Contains("MgF2") && list.Out.Contains("  0 layers") && list.Out.Contains("SCHOTT N-BK7"),
                     "materials alone lists them, with their use");
            h.Expect(Run("database").Out.Contains("HfO2"), "database lists the material database");

            int rows = Saved().Operands.Length;
            h.Expect(Run("merit", "insert", design, "--after", "1").Code == 0 && Saved().Operands.Length == rows + 1, "merit insert");
            var edit = Run("merit", "edit", design, "--row", "2", "--mode", "Max", "--target", "0.01", "--wavelength", "500",
                           "--wavelength2", "600", "--points", "5");
            h.Expect(edit.Code == 0 && Saved().Operands[1] is { Comparison: "LE", Target: 0.01, Points.Length: 5 },
                     $"merit edit: row 2, a band of five points ({edit.Err.Trim()})");
            h.Expect(edit.Out.Contains("from 500 to 600 nm, 5 points"), "and says what the row now is");
            h.Expect(Run("merit", "remove", design, "--row", "2").Code == 0 && Saved().Operands.Length == rows, "merit remove");

            var help = Run("help");
            foreach (var command in new[] { "stack string", "stack bounds", "layer set", "layer insert", "units", "materials",
                                            "merit edit", "new --materials" })
                h.Expect(help.Out.Contains("thinfilm " + command), $"help lists {command}");
        });
    }

    public static void TheMcpEditsTheStack(Harness h)
    {
        h.Section("new_design, edit_stack, edit_layer, list_materials, edit_materials, set_units, edit_operand");

        AtHome(() =>
        {
            var tools = new Tools();
            var names = tools.Describe().Select(t => t!["name"]!.GetValue<string>()).ToArray();
            h.Expect(new[] { "new_design", "edit_stack", "edit_layer", "list_materials", "edit_materials", "set_units", "edit_operand" }
                         .All(names.Contains),
                     "each is listed");
            JsonNode Call(string name, string args, bool fails = false)
            {
                var (text, failed) = tools.Call(name, JsonNode.Parse(args)!.AsObject());
                h.Expect(failed == fails, $"{name} {(fails ? "is refused" : "succeeds")} ({text[..Math.Min(140, text.Length)]})");
                return JsonNode.Parse(text)!;
            }
            JsonArray Layers(string id) => Call("evaluate_design", $$"""{"design":"{{id}}"}""")["Layers"]!.AsArray();

            string id = Call("new_design", """{"name":"MCP","materials":"TiO2,SiO2","substrate":"N-BK7","layers":2}""")["Id"]!.GetValue<string>();
            Call("edit_stack", $$"""{"design":"{{id}}","action":"string","text":"(1H 1L)2","pickups":false}""");
            h.Pin(Layers(id).Count, 4, "edit_stack string");
            Call("edit_stack", $$"""{"design":"{{id}}","action":"couple"}""");
            h.Expect(Layers(id)[2]!["Pickup"] is not null, "edit_stack couple");
            Call("edit_stack", $$"""{"design":"{{id}}","action":"decouple"}""");
            Call("edit_stack", $$"""{"design":"{{id}}","action":"bounds","material":"TiO2","min":20,"max":90}""");
            var bounded = JsonNode.Parse(Call("save_design", $$"""{"design":"{{id}}"}""").GetValue<string>())!["Layers"]!.AsArray();
            h.Expect(bounded[0]!["Max"]!.GetValue<double>() == 90 && bounded[1]!["Max"] is null, "edit_stack bounds, in nm");

            Call("edit_layer", $$"""{"design":"{{id}}","action":"set","layer":2,"value":2,"unit":"QWOT","vary":false}""");
            var l1 = Layers(id)[1]!;
            h.Expect(l1["Unit"]!.GetValue<string>() == "QWOT" && l1["Value"]!.GetValue<double>() == 2 && !l1["Variable"]!.GetValue<bool>()
                     && l1["Index"]!.GetValue<int>() == 2,
                     "edit_layer set: layer 2 is the second layer, as the Design tab numbers it, and evaluate_design says 2");
            Call("edit_layer", $$"""{"design":"{{id}}","action":"insert","after":2}""");
            Call("edit_layer", $$"""{"design":"{{id}}","action":"insert","material":"SiO2","value":75,"unit":"nm","before":1}""");
            h.Expect(Layers(id).Count == 6 && Layers(id)[0]!["Thickness"]!.GetValue<double>() == 75, "edit_layer insert, a copy and by name");
            Call("edit_layer", $$"""{"design":"{{id}}","action":"move","layer":1,"to":6}""");
            Call("edit_layer", $$"""{"design":"{{id}}","action":"remove","layer":6}""");
            h.Pin(Layers(id).Count, 5, "edit_layer move and remove");
            Call("edit_layer", $$"""{"design":"{{id}}","action":"set","layer":1,"material":"Unobtainium"}""", fails: true);

            h.Expect(Call("list_materials", "{}").AsArray().Any(n => n!["Name"]!.GetValue<string>() == "ZrO2" && n["Summary"]!.GetValue<string>().Contains("at 550 nm")),
                     "list_materials: the database, each material with what it is");
            Call("edit_materials", $$"""{"design":"{{id}}","add":"ZrO2","incident":"air"}""");
            h.Expect(Call("list_materials", $$"""{"design":"{{id}}"}""")["Materials"]!.AsArray().Count == 3,
                     "edit_materials adds; list_materials with a design lists its own");
            Call("set_units", $$"""{"design":"{{id}}","thickness_unit":"QWOT","reference_nm":600,"maximum_nm":450}""");
            var d = Call("describe_design", $$"""{"design":"{{id}}"}""");
            h.Expect(d["ThicknessUnit"]!.GetValue<string>() == "QWOT" && d["UnconstrainedMaximum"]!.GetValue<double>() == 450,
                     "set_units");

            Call("edit_operand", $$"""{"design":"{{id}}","action":"insert"}""");
            Call("edit_operand", $$"""{"design":"{{id}}","action":"edit","row":2,"code":"TA","mode":"Min","target":0.9,"wavelength":500,"wavelength2":700,"points":21}""");
            var save = JsonNode.Parse(Call("save_design", $$"""{"design":"{{id}}"}""").GetValue<string>())!;
            h.Expect(save["Operands"]!.AsArray().Count == 2 && save["Operands"]![1]!["Comparison"]!.GetValue<string>() == "GE",
                     "edit_operand insert and edit");
            Call("edit_operand", $$"""{"design":"{{id}}","action":"remove","row":2}""");
            Call("edit_operand", $$"""{"design":"{{id}}","action":"edit","row":5,"weight":2}""", fails: true);
        });
    }
}
