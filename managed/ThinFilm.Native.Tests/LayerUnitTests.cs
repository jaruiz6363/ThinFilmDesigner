// LayerUnitTests.cs — thicknesses in nm, QWOT and FWOT, and pickups between layers.
//
// Every number is checked against the material's own index at the reference wavelength, taken
// straight from the database, so a conversion that used the wrong index or the wrong wavelength
// fails here rather than looking plausible in a table.

using ThinFilm.Api;
using ThinFilm.Gui.Core;
using ThinFilm.Native;

namespace ThinFilm.Native.Tests;

public static class LayerUnitTests
{
    private static readonly MaterialDatabase Db = MaterialDatabase.Shipped();
    private static double N(string material, double nm) => Db.Find(material)!.Model.At(nm, material).N;

    /// <summary>A new design of four layers, SiO2 TiO2 SiO2 TiO2, quarter-waves at 550 nm, with a
    /// merit function over the visible so that every thickness matters.</summary>
    private static (ThinFilmApi Api, string Id) FourLayers()
    {
        var form = new NewDesignForm(Db) { Layers = "4" };
        var api = new ThinFilmApi();
        string id = api.CreateNew(form.ToRequest(out _)!).Value!.Id;
        api.AddOperands(id, new OperandSweep("RA", "Target", 0.0, 1.0, 420, 680, 20));
        return (api, id);
    }

    private static LayerReport[] Layers(ThinFilmApi api, string id) => api.Evaluate(id).Value!.Layers;
    private static double Merit(ThinFilmApi api, string id) => api.Evaluate(id).Value!.Merit;

    public static void AQuarterWaveIsCountedInItsOwnIndex(Harness h)
    {
        h.Section("QWOT and FWOT: 4nd/lambda and nd/lambda, n the layer's own index at the reference");

        var (api, id) = FourLayers();
        var d = api.Describe(id).Value!;
        h.Expect(d.ThicknessUnit == "QWOT" && d.ReferenceWavelength == 550.0,
                 $"a new design is in QWOT at its reference, 550 nm ({d.ThicknessUnit} at {d.ReferenceWavelength})");

        var l = Layers(api, id);
        h.Expect(l.All(x => x.Unit == "QWOT" && x.UnitSetting is null && Math.Abs(x.Value - 1.0) < 1e-12),
                 $"every layer reads 1 QWOT, following the design ({string.Join(", ", l.Select(x => x.Value.ToString("F6")))})");
        h.Expect(Math.Abs(l[1].Thickness - 550.0 / (4 * N("TiO2", 550))) < 1e-9,
                 $"1 QWOT of TiO2 is 550/(4 x {N("TiO2", 550):F4}) = {l[1].Thickness:F3} nm");

        h.Expect(api.SetLayerUnit(id, 1, "FWOT").Ok, "a layer can name its own unit");
        l = Layers(api, id);
        h.Expect(l[1].Unit == "FWOT" && l[1].UnitSetting == "FWOT" && Math.Abs(l[1].Value - 0.25) < 1e-12,
                 $"and a quarter-wave is 0.25 FWOT ({l[1].Value:F6})");
        h.Expect(api.SetLayerUnit(id, 1, "nm").Ok && Math.Abs(Layers(api, id)[1].Value - l[1].Thickness) < 1e-12,
                 "and in nm it is its thickness");

        h.Expect(api.SetLayerValue(id, 0, 0.5).Ok, "a thickness is typed in the layer's unit");
        h.Expect(Math.Abs(Layers(api, id)[0].Thickness - 0.5 * 550.0 / (4 * N("SiO2", 550))) < 1e-9,
                 $"0.5 QWOT of SiO2 is {Layers(api, id)[0].Thickness:F3} nm");
        h.Expect(!api.SetLayerUnit(id, 0, "furlongs").Ok, "a unit that is not one is refused");
    }

    public static void AUnitIsHowAThicknessIsReadNotWhatItIs(Harness h)
    {
        h.Section("changing a unit keeps the coating: the same nanometres, read another way");

        var (api, id) = FourLayers();
        double merit = Merit(api, id);
        var nm = Layers(api, id).Select(x => x.Thickness).ToArray();

        h.Expect(api.SetThicknessUnits(id, "nm", 550).Ok, "the design switches to nm");
        h.PinExact(Merit(api, id), merit, "and its merit is bit for bit what it was");
        h.Expect(Layers(api, id).Select(x => x.Thickness).SequenceEqual(nm), "every layer keeps its nanometres");
        h.Expect(Layers(api, id).All(x => x.Unit == "nm" && x.Value == x.Thickness), "and now reads in nm");

        h.Expect(api.SetThicknessUnits(id, "FWOT", 550).Ok, "and to FWOT");
        h.Expect(Layers(api, id).All(x => Math.Abs(x.Value - 0.25) < 1e-12), "where every layer is 0.25");
        h.PinExact(Merit(api, id), merit, "still the same coating");
    }

    public static void ANewReferenceMovesTheQuarterWaves(Harness h)
    {
        h.Section("a new reference wavelength keeps QWOT numbers, so the design moves (the user, 2026-10-16)");

        var (api, id) = FourLayers();
        double merit = Merit(api, id);
        h.Expect(api.SetLayerUnit(id, 3, "nm").Ok, "layer 4 is kept in nm");
        double layer4 = Layers(api, id)[3].Thickness;

        h.Expect(api.SetThicknessUnits(id, "QWOT", 600).Ok, "the reference moves to 600 nm");
        var l = Layers(api, id);
        h.Expect(l.Take(3).All(x => Math.Abs(x.Value - 1.0) < 1e-12), "layers 1-3 still read 1 QWOT");
        h.Expect(Math.Abs(l[1].Thickness - 600.0 / (4 * N("TiO2", 600))) < 1e-9,
                 $"and are quarter-waves at 600 nm in the index there: TiO2 {l[1].Thickness:F3} nm at n {N("TiO2", 600):F4}");
        h.PinExact(l[3].Thickness, layer4, "the nm layer has not moved");
        h.Expect(Merit(api, id) != merit, "and the spectrum has moved with the design");

        // TO KEEP THE SPECTRUM: nm first, then QWOT at the new reference - the user's own recipe.
        var (api2, id2) = FourLayers();
        double merit2 = Merit(api2, id2);
        var before = Layers(api2, id2).Select(x => x.Thickness).ToArray();
        h.Expect(api2.SetThicknessUnits(id2, "nm", 550).Ok && api2.SetThicknessUnits(id2, "QWOT", 600).Ok,
                 "nm at 550, then QWOT at 600");
        h.Expect(Layers(api2, id2).Select(x => x.Thickness).SequenceEqual(before), "keeps every nanometre");
        h.PinExact(Merit(api2, id2), merit2, "and the spectrum");
        double expect = 4 * N("TiO2", 600) * before[1] / 600.0;
        h.Expect(Math.Abs(Layers(api2, id2)[1].Value - expect) < 1e-12,
                 $"and the TiO2 quarter-wave at 550 reads {Layers(api2, id2)[1].Value:F4} QWOT at 600");

        h.Expect(!api.SetThicknessUnits(id, "QWOT", -1).Ok, "a reference that is not a wavelength is refused");
    }

    public static void ANewMaterialKeepsTheQuarterWave(Harness h)
    {
        h.Section("a QWOT layer given a new material stays a quarter-wave; an nm layer keeps its nm");

        var (api, id) = FourLayers();
        var names = api.MaterialNames(id).Value!;
        int sio2 = Array.IndexOf(names, "SiO2"), tio2 = Array.IndexOf(names, "TiO2");
        h.Expect(api.SetLayerMaterial(id, 0, tio2).Ok, "layer 1 becomes TiO2");
        var l = Layers(api, id);
        h.Expect(Math.Abs(l[0].Value - 1.0) < 1e-12 && Math.Abs(l[0].Thickness - l[1].Thickness) < 1e-9,
                 $"and is a TiO2 quarter-wave, {l[0].Thickness:F3} nm");

        h.Expect(api.SetLayerUnit(id, 2, "nm").Ok, "layer 3 is put in nm");
        double nm = Layers(api, id)[2].Thickness;
        h.Expect(api.SetLayerMaterial(id, 2, tio2).Ok && Layers(api, id)[2].Thickness == nm,
                 "and keeps its nanometres when its material changes");
        _ = sio2;
    }

    public static void APickupHoldsInTheUnitBothLayersShare(Harness h)
    {
        h.Section("pickups: child = scale x parent + offset, in one unit for both (the user's examples)");

        var (api, id) = FourLayers();
        // Layer 1 = 1 QWOT SiO2; layer 2 (TiO2, scale 3, offset 0, parent 1) = 3 QWOT TiO2.
        h.Expect(api.SetLayerPickup(id, 1, new LayerPickup(0, 3.0)).Ok, "layer 2 picks up layer 1, scale 3");
        var l = Layers(api, id);
        h.Expect(Math.Abs(l[1].Value - 3.0) < 1e-12, $"layer 2 is 3 QWOT of TiO2 ({l[1].Value:F6})");
        h.Expect(Math.Abs(l[1].Thickness - 3 * 550.0 / (4 * N("TiO2", 550))) < 1e-9,
                 $"which is {l[1].Thickness:F3} nm, three TiO2 quarter-waves - not three times layer 1's nm");
        h.Expect(!l[1].Variable && l[1].Pickup is { Parent: 0, Scale: 3.0 }, "and is not a variable of its own");

        // Layer 1 = 100 nm SiO2; layer 2 (TiO2, scale 0.5, offset 0, parent 1) = 50 nm TiO2.
        var (api2, id2) = FourLayers();
        h.Expect(api2.SetThicknessUnits(id2, "nm", 550).Ok && api2.SetLayerValue(id2, 0, 100).Ok,
                 "in nm, layer 1 is 100 nm");
        h.Expect(api2.SetLayerPickup(id2, 1, new LayerPickup(0, 0.5)).Ok, "layer 2 picks it up at 0.5");
        h.Expect(Math.Abs(Layers(api2, id2)[1].Thickness - 50.0) < 1e-12, $"and is 50 nm ({Layers(api2, id2)[1].Thickness})");
        h.Expect(api2.SetLayerPickup(id2, 2, new LayerPickup(0, 1.0, 7.5)).Ok
                 && Math.Abs(Layers(api2, id2)[2].Thickness - 107.5) < 1e-12, "an offset adds, in the shared unit");

        h.Expect(api2.SetLayerValue(id2, 0, 120).Ok && Math.Abs(Layers(api2, id2)[1].Thickness - 60.0) < 1e-12,
                 "the child follows its parent");
        h.Expect(!api2.SetLayerValue(id2, 1, 10).Ok, "and cannot be typed over");
        h.Expect(!api2.SetThickness(id2, 1, 10).Ok, "nor set in nm");
        h.Expect(!api2.SetLayerVariable(id2, 1, true).Ok, "nor made a variable");

        // THE UNITS MUST MATCH. Refused, naming both layers and both units.
        var u = api.SetLayerUnit(id, 1, "nm");
        h.Expect(!u.Ok && u.Error!.Contains("layer 2") && u.Error.Contains("QWOT") && u.Error.Contains("nm"),
                 $"a child in nm under a QWOT parent is refused: {u.Error}");
        h.Expect(!api.SetLayerPickup(id, 2, new LayerPickup(1)).Ok, "a pickup of a picked-up layer is refused");
        h.Expect(!api.SetLayerPickup(id, 0, new LayerPickup(3)).Ok, "and a parent cannot pick up another layer");
        h.Expect(!api.SetLayerPickup(id, 2, new LayerPickup(2)).Ok, "nor a layer itself");
        var rm = api.RemoveLayer(id, 0);
        h.Expect(!rm.Ok && rm.Error!.Contains("layer 2"), $"a parent cannot be deleted while picked up: {rm.Error}");

        // RENUMBERED with the stack: insert above, move, and the pickup still names the same layers.
        var names = api.MaterialNames(id).Value!;
        h.Expect(api.InsertLayer(id, 0, Array.IndexOf(names, "SiO2"), 20.0).Ok, "a layer inserted at the top");
        h.Expect(Layers(api, id)[2].Pickup is { Parent: 1 }, "the pickup follows its parent to layer 2");
        h.Expect(api.MoveLayer(id, 2, 3).Ok && Layers(api, id)[3].Pickup is { Parent: 1 }, "and the child moved to layer 4");
        h.Expect(api.SetLayerPickup(id, 3, null).Ok && Layers(api, id)[3].Variable, "a freed layer is a variable again");

        // SAVED AND REOPENED as it was: units, reference, pickups.
        h.Expect(api2.SetLayerUnit(id2, 3, "QWOT").Ok, "one layer in QWOT in an nm design");
        var json = api2.Save(id2).Value!;
        var again = api2.Load(json).Value!;
        h.Expect(again.ThicknessUnit == "nm" && again.ReferenceWavelength == 550.0, "the unit and reference reopen");
        var l2 = Layers(api2, again.Id);
        h.Expect(l2[3].UnitSetting == "QWOT" && l2[1].Pickup is { Parent: 0, Scale: 0.5 }
                 && l2[2].Pickup is { Offset: 7.5 }, "and so do the layer's own unit and the pickups");
    }

    public static void TheLayerPropertiesFormConvertsAndChecks(Harness h)
    {
        h.Section("Layer properties: a new unit keeps the nm, a new material keeps the number, bad boxes refused");

        var w = new DesignWorkspace();
        h.Expect(w.CreateNew(new NewDesignForm(Db) { Layers = "4" }.ToRequest(out _)!), "a new design, in QWOT");
        var form = new LayerPropertiesForm(w.Layers[1], w.Layers.Count, w.ThicknessUnit, w.ReferenceWavelength,
                                           w.WavelengthUnit, w.UnitsPerNm);
        h.Expect(form.ThicknessText == "1.0000" && form.Unit == "QWOT" && form.UnitChoice == "Default (QWOT)",
                 $"it opens on layer 2's own state ({form.ThicknessText} {form.UnitChoice})");
        h.Expect(form.NmPreview == $"= {w.Layers[1].Thickness:F2} nm of TiO2", $"with the nanometres beside it ({form.NmPreview})");

        form.ChooseUnit("FWOT");
        h.Expect(form.ThicknessText == "0.2500", $"FWOT re-expresses it: 0.2500 ({form.ThicknessText})");
        form.ChooseUnit("nm");
        h.Expect(form.ThicknessText == w.Layers[1].Thickness.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                 $"and nm shows its nanometres ({form.ThicknessText})");
        form.ChooseUnit("QWOT");
        form.Material = "SiO2";
        h.Expect(form.ThicknessText == "1.0000" && form.NmPreview == $"= {w.Layers[0].Thickness:F2} nm of SiO2",
                 $"a new material keeps 1.0000 QWOT, now {form.NmPreview}");

        var p = form.ToProperties(out var why);
        h.Expect(p is { Unit: "QWOT", Pickup: null } && Math.Abs(p.Value - 1.0) < 1e-12, $"and hands over QWOT 1.0, exactly through four unit changes ({p?.Value:R} {why})");
        h.Expect(w.SetLayerProperties(1, p!) && w.Layers[1].Material == "SiO2" && w.Layers[1].UnitSetting == "QWOT",
                 $"which the design takes: {w.Status}");

        form.HasPickup = true;
        foreach (var (what, set, says) in new (string, Action, string)[]
        {
            ("a parent past the stack", () => form.PickupParentText = "9", "from 1 to 4"),
            ("the layer itself", () => form.PickupParentText = "2", "its own"),
            ("a scale that is a word", () => { form.PickupParentText = "1"; form.PickupScaleText = "big"; }, "scale"),
        })
        {
            set();
            h.Expect(form.ToProperties(out var bad) is null && bad!.Contains(says), $"{what} is refused: {bad}");
        }
        h.Expect(ThicknessPreferences.ReferenceNm("0.6", "um", out _) == 600.0, "a reference of 0.6 µm is 600 nm");
        h.Expect(ThicknessPreferences.ReferenceNm("-1", "nm", out var neg) is null && neg!.Contains("positive"),
                 $"and a negative one is refused: {neg}");
    }

    public static void MicronsAreUsedForEveryLengthAndSavedInTheFile(Harness h)
    {
        h.Section("µm chosen: physical thicknesses read µm too, and the settings are in the design file");

        var w = new DesignWorkspace();
        h.Expect(w.OpenSample("AR-visible"), "a sample opens, in nm");
        double nm0 = w.Layers[0].Thickness;
        h.Expect(w.SetWavelengthUnit("um"), "the design is switched to µm");
        var row = w.Layers[0];
        h.Expect(row.UnitLabel == "µm" && row.UnitChoice == "Default (µm)" && row.UnitOptions.Contains("µm")
                 && !row.UnitOptions.Contains("nm"),
                 $"a physical layer reads µm, and so does its drop-down ({row.UnitChoice}: {string.Join(", ", row.UnitOptions)})");
        h.Expect(row.ValueText == (nm0 / 1000).ToString("F6", System.Globalization.CultureInfo.InvariantCulture)
                 && row.PhysicalText == row.ValueText, $"its thickness is shown in µm ({row.ValueText})");
        row.ValueText = "0.1";
        h.Expect(Math.Abs(w.Layers[0].Thickness - 100.0) < 1e-9, $"0.1 typed is 0.1 µm, 100 nm ({w.Layers[0].Thickness})");
        h.Expect(w.Summary.Contains(" µm ·"), $"the total thickness is in µm: {w.Summary}");
        var mint = w.MeritRows.First(r => r.Code == "MINT");
        h.Expect(mint.LimitText == "0.008", $"MINT's 8 nm limit reads 0.008 µm ({mint.LimitText})");
        h.Expect(w.Operands.First(o => o.Code == "MINT").Help.Contains("0.008 µm"), "and its help says so");

        var form = new LayerPropertiesForm(w.Layers[0], w.Layers.Count, w.ThicknessUnit, w.ReferenceWavelength,
                                           w.WavelengthUnit, w.UnitsPerNm);
        h.Expect(form.UnitLabel == "µm" && form.ThicknessText == "0.100000", $"Layer properties is in µm too ({form.ThicknessText} {form.UnitLabel})");
        form.ChooseUnit("QWOT");
        h.Expect(form.NmPreview.Contains("= 0.1 µm"), $"and its preview of a QWOT value is in µm ({form.NmPreview})");
        form.ChooseUnit("µm");
        h.Expect(form.ThicknessText == "0.100000" && form.ToProperties(out _)!.Value == 100.0,
                 "and back to µm, handed to the design as 100 nm");

        // IN THE DESIGN FILE: the length unit, the thickness unit and the reference.
        h.Expect(w.SetThicknessUnits("QWOT", 600), "QWOT at 600 nm");
        var json = w.Save()!;
        h.Expect(json.Contains("\"WavelengthUnit\": \"um\"") && json.Contains("\"ThicknessUnit\": \"QWOT\"")
                 && json.Contains("\"ReferenceWavelength\": 600"), "the file records µm, QWOT and 600 nm");
        var again = new DesignWorkspace();
        h.Expect(again.Open(json, "coat.tfd") && again.WavelengthUnit == "um" && again.ThicknessUnit == "QWOT"
                 && again.ReferenceWavelength == 600.0, "and the design reopens with all three");
    }

    public static void TheOptimiserMovesAParentAndItsChildrenTogether(Harness h)
    {
        h.Section("pickups under optimisation: the folded Jacobian is the derivative, and ties hold");

        var (api, id) = FourLayers();
        h.Expect(api.SetLayerPickup(id, 2, new LayerPickup(0, 1.0)).Ok, "layer 3 = layer 1");
        h.Expect(api.SetLayerPickup(id, 3, new LayerPickup(1, 2.0, 0.1)).Ok, "layer 4 = 2 x layer 2 + 0.1 QWOT");

        // THE CHAIN RULE, checked against a finite difference of the residuals.
        var design = new Design(api.Document(id).Value!.ToSpec());
        design.Plan();
        h.Pin(design.VariableCount, 2, "two free variables, layers 1 and 2");
        design.EvaluateJacobian();
        int m = design.Report.n_residuals;
        var j = design.Jacobian.ToArray();
        double worst = 0.0;
        for (int v = 0; v < 2; ++v)
        {
            double x = design.GetVariable(v), step = 1e-4;
            design.SetVariable(v, x + step); design.Evaluate(); var up = design.Residual.ToArray();
            design.SetVariable(v, x - step); design.Evaluate(); var down = design.Residual.ToArray();
            design.SetVariable(v, x);
            for (int r = 0; r < m; ++r)
            {
                double fd = (up[r] - down[r]) / (2 * step);
                worst = Math.Max(worst, Math.Abs(fd - j[v * m + r]) / Math.Max(1e-6, Math.Abs(fd)));
            }
        }
        h.Expect(worst < 1e-5, $"each parent's column matches a central difference that moves its children too (worst {worst:E1})");

        double start = Merit(api, id);
        var r1 = api.Optimise(id, new OptimiseRequest("refine", MaxIterations: 80));
        h.Expect(r1.Ok && r1.Value!.FinalMerit <= start, $"refine runs and does not make it worse ({start:G4} -> {r1.Value?.FinalMerit:G4})");
        var l = Layers(api, id);
        h.Expect(Math.Abs(l[2].Value - l[0].Value) < 1e-9 && Math.Abs(l[3].Value - (2 * l[1].Value + 0.1)) < 1e-9,
                 $"and the pickups still hold after it ({l[0].Value:F4} = {l[2].Value:F4}; 2 x {l[1].Value:F4} + 0.1 = {l[3].Value:F4})");
        h.Expect(api.Optimise(id, new OptimiseRequest("global,refine", GlobalStarts: 6, MaxIterations: 40)).Ok
                 && Math.Abs(Layers(api, id)[2].Value - Layers(api, id)[0].Value) < 1e-9,
                 "a global search keeps them too");

        var needle = api.Optimise(id, new OptimiseRequest("refine,needle"));
        h.Expect(!needle.Ok && needle.Error!.Contains("pickups"),
                 $"needle, which renumbers layers, is refused with pickups: {needle.Error}");
    }
}
