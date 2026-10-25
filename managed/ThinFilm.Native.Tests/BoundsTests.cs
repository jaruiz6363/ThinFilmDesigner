// BoundsTests.cs — per-layer thickness bounds: unconstrained, between, at least, at most.
//
// The user (2026-10-16): each layer may be unconstrained, between a min and a max, greater than a
// min or less than a max, set in Layer properties and, for a range of layers, in Set vary
// constraints. The optimiser must keep to them, so the checks run refine and look at where it
// stopped, against a run without the bound that goes further.

using ThinFilm.Api;
using ThinFilm.Gui.Core;

namespace ThinFilm.Native.Tests;

public static class BoundsTests
{
    private static readonly MaterialDatabase Db = MaterialDatabase.Shipped();
    private static double N(string material, double nm) => Db.Find(material)!.Model.At(nm, material).N;

    /// <summary>Four layers SiO2 TiO2 SiO2 TiO2, quarter-waves at 550 nm, scored over the visible.</summary>
    private static (ThinFilmApi Api, string Id) FourLayers()
    {
        var api = new ThinFilmApi();
        string id = api.CreateNew(new NewDesignForm(Db) { Layers = "4" }.ToRequest(out _)!).Value!.Id;
        api.AddOperands(id, new OperandSweep("RA", "Target", 0.0, 1.0, 420, 680, 20));
        return (api, id);
    }

    private static LayerReport[] Layers(ThinFilmApi api, string id) => api.Evaluate(id).Value!.Layers;

    private static LayerProperties Props(LayerReport l, ThinFilmApi api, string id, double? min, double? max) =>
        new(Array.IndexOf(api.MaterialNames(id).Value!, l.Material), l.UnitSetting, l.Value, l.Variable, l.Pickup, min, max);

    public static void ALayerKeepsToItsBounds(Harness h)
    {
        h.Section("bounds in Layer properties: kept in the layer's unit, and kept to by the optimiser");

        // HOW FAR REFINE MOVES LAYER 1 WITH NO BOUND, so the bounded run below has something to stop.
        var (free, freeId) = FourLayers();
        free.Optimise(freeId, new OptimiseRequest(MaxIterations: 60));
        double freeValue = Layers(free, freeId)[0].Value;
        h.Expect(Math.Abs(freeValue - 1.0) > 0.03, $"unbounded, refine moves layer 1 more than 0.03 QWOT ({freeValue:F4})");

        var (api, id) = FourLayers();
        var l = Layers(api, id);
        h.Expect(l.All(x => x.Min is null && x.Max is null), "a new design's layers are unconstrained");
        h.Expect(api.SetLayerProperties(id, 0, Props(l[0], api, id, 0.98, 1.02)).Ok, "layer 1 between 0.98 and 1.02 QWOT");
        l = Layers(api, id);
        h.Expect(l[0].Min is { } lo && Math.Abs(lo - 0.98) < 1e-12 && l[0].Max is { } hi && Math.Abs(hi - 1.02) < 1e-12,
                 $"reported back in QWOT ({l[0].Min}, {l[0].Max})");
        var doc = api.Document(id).Value!;
        h.Expect(Math.Abs(doc.Layers[0].Min!.Value - 0.98 * 550 / (4 * N("SiO2", 550))) < 1e-9,
                 $"and held in nm, in SiO2's own index ({doc.Layers[0].Min:F3} nm)");

        api.Optimise(id, new OptimiseRequest(MaxIterations: 60));
        double v = Layers(api, id)[0].Value;
        h.Expect(v >= 0.98 - 1e-9 && v <= 1.02 + 1e-9, $"refine keeps layer 1 within 0.98–1.02 QWOT ({v:F4})");

        // ONE SIDE ONLY, and above the design-wide 600 nm the other layers still have.
        var (api2, id2) = FourLayers();
        l = Layers(api2, id2);
        h.Expect(api2.SetThicknessUnits(id2, "nm", 550).Ok, "in nm");
        l = Layers(api2, id2);
        h.Expect(api2.SetLayerProperties(id2, 1, Props(l[1], api2, id2, 700.0, null)).Ok
                 && Layers(api2, id2)[1].Max is null, "layer 2 at least 700 nm, no maximum");
        api2.Optimise(id2, new OptimiseRequest(MaxIterations: 40));
        h.Expect(Layers(api2, id2)[1].Thickness >= 700.0 - 1e-9,
                 $"refine lifts it to its minimum and keeps it there ({Layers(api2, id2)[1].Thickness:F2} nm)");
        h.Expect(api2.SetLayerProperties(id2, 2, Props(Layers(api2, id2)[2], api2, id2, null, 50.0)).Ok, "layer 3 at most 50 nm");
        api2.Optimise(id2, new OptimiseRequest(MaxIterations: 40));
        h.Expect(Layers(api2, id2)[2].Thickness <= 50.0 + 1e-9, $"and refine keeps it under ({Layers(api2, id2)[2].Thickness:F2} nm)");

        // REFUSED: a bad pair, and the shape-changing stages.
        l = Layers(api, id);
        var bad = api.SetLayerProperties(id, 0, Props(l[0], api, id, 1.2, 1.1));
        h.Expect(!bad.Ok && bad.Error!.Contains("more than the minimum"), $"a maximum under the minimum is refused: {bad.Error}");
        h.Expect(!api.SetLayerProperties(id, 0, Props(l[0], api, id, -1.0, null)).Ok, "and a negative minimum");
        var needle = api.Optimise(id, new OptimiseRequest(Stages: "refine,needle"));
        h.Expect(!needle.Ok && needle.Error!.Contains("min and max"), $"needle is refused while a layer is bounded: {needle.Error}");

        // WHAT A CHANGE KEEPS: a new reference keeps the QWOT numbers of the bounds, like the
        // thickness; a new unit keeps their nanometres.
        h.Expect(api.SetThicknessUnits(id, "QWOT", 600).Ok, "the reference moved to 600 nm");
        l = Layers(api, id);
        h.Expect(Math.Abs(l[0].Min!.Value - 0.98) < 1e-12 && Math.Abs(l[0].Max!.Value - 1.02) < 1e-12,
                 $"the bounds are still 0.98–1.02 QWOT ({l[0].Min:F6}–{l[0].Max:F6})");
        double minNm = api.Document(id).Value!.Layers[0].Min!.Value;
        h.Expect(api.SetLayerUnit(id, 0, "FWOT").Ok, "layer 1 shown in FWOT");
        l = Layers(api, id);
        h.Expect(Math.Abs(l[0].Min!.Value - 0.98 / 4) < 1e-12 && Math.Abs(api.Document(id).Value!.Layers[0].Min!.Value - minNm) < 1e-9,
                 $"its minimum reads 0.245 FWOT, the same nanometres ({l[0].Min:F6})");

        // SAVED AND REOPENED.
        var reopened = api.Load(api.Save(id).Value!).Value!.Id;
        h.Expect(Layers(api, reopened)[0].Min is { } m && Math.Abs(m - 0.98 / 4) < 1e-12, "the bounds are saved in the design file");

        // UNCONSTRAINED AGAIN.
        l = Layers(api, id);
        h.Expect(api.SetLayerProperties(id, 0, Props(l[0], api, id, null, null)).Ok
                 && Layers(api, id)[0] is { Min: null, Max: null }, "and cleared");
        h.Expect(api.Optimise(id, new OptimiseRequest(Stages: "refine,needle", MaxIterations: 20)).Ok,
                 "after which needle runs again");
    }

    public static void UnconstrainedHasNoFloorAndASeenCeiling(Harness h)
    {
        h.Section("unconstrained: no minimum but zero, and a maximum the person can see and set");

        var (api, id) = FourLayers();
        var doc = api.Document(id).Value!;
        var (lower, upper) = LayerUnits.Bounds(doc);
        h.Expect(lower.All(l => l == 0.0), "no floor but zero: no minimum the person did not set");
        h.Expect(upper.All(u => u == 600.0) && api.Describe(id).Value!.UnconstrainedMaximum == 600.0,
                 "and the design's maximum for unconstrained layers above, reported with the design");

        // A NEW INFRARED DESIGN is not held to a visible one's layers.
        var ir = new ThinFilmApi();
        var irForm = new NewDesignForm(Db) { Layers = "4", ReferenceNm = "1550" };
        string irId = ir.CreateNew(irForm.ToRequest(out _)!).Value!.Id;
        h.Expect(ir.Describe(irId).Value!.UnconstrainedMaximum == 1550.0,
                 $"a new design's maximum is its reference when that is past 600 nm ({ir.Describe(irId).Value!.UnconstrainedMaximum})");

        // A LAYER ALREADY ABOVE IT is not pulled down unseen: Optimise says so.
        api.SetThicknessUnits(id, "nm", 550);
        h.Expect(api.SetLayerValue(id, 1, 900.0).Ok, "layer 2 typed at 900 nm, unconstrained");
        var o = api.Optimise(id, new OptimiseRequest(MaxIterations: 20));
        h.Expect(!o.Ok && o.Error!.Contains("layer 2") && o.Error.Contains("600 nm") && o.Error.Contains("System Parameters"),
                 $"Optimise refuses, naming the layer, the maximum and where to change it: {o.Error}");
        h.Expect(api.SetUnconstrainedMaximum(id, 1000.0).Ok && api.Describe(id).Value!.UnconstrainedMaximum == 1000.0,
                 "the maximum raised to 1000 nm");
        h.Expect(api.Optimise(id, new OptimiseRequest(MaxIterations: 20)).Ok, "and Optimise runs");
        h.Expect(!api.SetUnconstrainedMaximum(id, 0.0).Ok && !api.SetUnconstrainedMaximum(id, double.NaN).Ok,
                 "a maximum of zero or not a number is refused");
        var reopened = api.Load(api.Save(id).Value!).Value!.Id;
        h.Expect(api.Describe(reopened).Value!.UnconstrainedMaximum == 1000.0, "the maximum is saved in the design file");

        // A LAYER'S OWN MINIMUM ABOVE THE CEILING has asked to be thick: no ceiling for it.
        var withMin = doc with { Layers = doc.Layers.Select((l, i) => i == 0 ? l with { Min = 700.0 } : l).ToArray() };
        (_, upper) = LayerUnits.Bounds(withMin);
        h.Expect(double.IsPositiveInfinity(upper[0]) && upper[1] == 600.0, "a layer at least 700 nm has no 600 nm ceiling");

        h.Expect(Constraints.Note(Constraints.None, 600.0, "nm") == "From 0 up to 600 nm, the design's maximum for unconstrained layers (Edit > System Parameters).",
                 "Layer properties says what unconstrained means");
        h.Expect(Constraints.Note(Constraints.AtMost, 600.0, "nm") == "From 0." && Constraints.Note(Constraints.Between, 600.0, "nm") == ""
                 && Constraints.Note(Constraints.AtLeast, 600.0, "um").Contains("0.6 µm"),
                 "and what an open side of each constraint means");
    }

    public static void BoundsReflectRatherThanClamp(Harness h)
    {
        h.Section("bounds reflect: an overshoot is mirrored back inside, not parked on the bound");

        double? R(double x, double lo, double hi) => ThinFilm.Optimize.Bounds.Reflect(x, lo, hi);
        h.Expect(R(620, 0, 600) == 580, "620 nm against a 600 nm maximum lands at 580");
        h.Expect(R(690, 700, double.PositiveInfinity) == 710, "690 against a 700 nm minimum lands at 710");
        h.Expect(R(0.9, 1.0, 1.2) is { } v && Math.Abs(v - 1.1) < 1e-12, "0.9 against 1.0–1.2 lands at 1.1");
        h.Expect(R(300, 0, 600) == 300, "a value inside is left alone");
        h.Expect(R(-5, 0, 600) == 0, "below a floor of zero it stops at zero: a layer vanishing is not a bound");
        h.Expect(R(1300, 0, 600) is null && R(500, 700, 800) is null,
                 "an overshoot wider than the range is not folded: the step is too long and is retried shorter");

        // THE ONE MOVE THAT IS NOT A REFLECTION, and the report that names it.
        var (api, id) = FourLayers();
        api.SetThicknessUnits(id, "nm", 550);
        var l = Layers(api, id);
        api.SetLayerProperties(id, 1, Props(l[1], api, id, 700.0, null));
        var o = api.Optimise(id, new OptimiseRequest(MaxIterations: 10));
        h.Expect(o.Ok && o.Value!.PlacedOnBound is [1], "a 59 nm layer with a 700 nm minimum is named as placed on it");
        h.Expect(Layers(api, id)[1].Thickness >= 700.0 - 1e-9, "and is at least 700 nm after");
        o = api.Optimise(id, new OptimiseRequest(MaxIterations: 10));
        h.Expect(o.Ok && o.Value!.PlacedOnBound is [], "the next run starts inside, so names none");
    }

    public static void ARangeOfLayersIsBoundedAtOnce(Harness h)
    {
        h.Section("Set vary constraints: a range of layers, one material or all, in each layer's own index");

        var (api, id) = FourLayers();
        var names = api.MaterialNames(id).Value!;
        int tio2 = Array.IndexOf(names, "TiO2");
        var r = api.SetLayerBounds(id, new BoundsRequest(0, 3, null, "QWOT", 0.5, null));
        h.Expect(r.Ok && r.Value!.Set.SequenceEqual([0, 1, 2, 3]), "every layer at least 0.5 QWOT");
        var doc = api.Document(id).Value!;
        h.Expect(Math.Abs(doc.Layers[0].Min!.Value - 0.5 * 550 / (4 * N("SiO2", 550))) < 1e-9
                 && Math.Abs(doc.Layers[1].Min!.Value - 0.5 * 550 / (4 * N("TiO2", 550))) < 1e-9,
                 $"half of each layer's own quarter-wave ({doc.Layers[0].Min:F2} nm of SiO2, {doc.Layers[1].Min:F2} nm of TiO2)");

        r = api.SetLayerBounds(id, new BoundsRequest(1, 3, tio2, "nm", 20.0, 200.0));
        var l = Layers(api, id);
        h.Expect(r.Ok && r.Value!.Set.SequenceEqual([1, 3]), "TiO2 only, layers 2 to 4: layers 2 and 4");
        h.Expect(l[1].Min is not null && l[0].Max is null && l[2].Max is null, "the SiO2 layers keep what they had");

        h.Expect(api.SetLayerPickup(id, 3, new LayerPickup(1)).Ok, "layer 4 picks up layer 2");
        r = api.SetLayerBounds(id, new BoundsRequest(0, 3, null, "QWOT", null, null));
        h.Expect(r.Ok && r.Value!.Set.SequenceEqual([0, 1, 2]) && r.Value.PickedUp.SequenceEqual([3]),
                 "all unconstrained, the picked-up layer 4 passed over and named");
        h.Expect(!api.SetLayerBounds(id, new BoundsRequest(2, 1, null, "QWOT", null, null)).Ok, "a range that runs backwards is refused");
        h.Expect(!api.SetLayerBounds(id, new BoundsRequest(0, 9, null, "QWOT", null, null)).Ok, "and one past the last layer");
    }

    public static void TheFormsReadAndCheckBounds(Harness h)
    {
        h.Section("the bounds in Layer properties and Set vary constraints, read from their boxes");

        var w = new DesignWorkspace();
        h.Expect(w.CreateNew(new NewDesignForm(Db) { Layers = "4" }.ToRequest(out _)!), "a design of four layers");
        var form = new LayerPropertiesForm(w.Layers[0], 4, w.ThicknessUnit, w.ReferenceWavelength, w.WavelengthUnit, w.UnitsPerNm);
        h.Expect(form.Constraint == Constraints.None && !form.MinApplies && !form.MaxApplies, "unconstrained to begin with");
        form.Constraint = Constraints.Between;
        form.MinText = "0.8";
        form.MaxText = "1.2";
        var p = form.ToProperties(out var problem);
        h.Expect(p is { Min: 0.8, Max: 1.2 }, $"between 0.8 and 1.2 QWOT ({problem})");
        form.Constraint = Constraints.AtLeast;
        p = form.ToProperties(out _);
        h.Expect(p is { Min: 0.8, Max: null }, "greater than min ignores the max box");
        form.Constraint = Constraints.AtMost;
        p = form.ToProperties(out _);
        h.Expect(p is { Min: null, Max: 1.2 }, "less than max ignores the min box");
        form.Constraint = Constraints.Between;
        form.MaxText = "0.5";
        h.Expect(form.ToProperties(out problem) is null && problem!.Contains("more than the minimum"), $"0.8 to 0.5 is refused: {problem}");
        form.MaxText = "x";
        h.Expect(form.ToProperties(out problem) is null && problem!.Contains("maximum"), $"so is a maximum that is not a number: {problem}");

        form.MaxText = "1.2";
        form.ChooseUnit("FWOT");
        h.Expect(form.MinText == "0.2" && form.MaxText == "0.3", $"a new unit converts the bounds with the thickness ({form.MinText}, {form.MaxText})");

        h.Expect(w.SetLayerProperties(0, form.ToProperties(out _)!), "applied");
        h.Expect(w.Layers[0].BoundsText == "0.2 – 0.3", $"the Bounds column shows them ({w.Layers[0].BoundsText})");
        h.Expect(w.InsertLayer(0) && w.Layers[1].BoundsText == "0.2 – 0.3", "a copy of the layer copies its bounds");

        // SET VARY CONSTRAINTS.
        var bulk = new BoundsForm(w.Layers, w.MaterialNames, w.ThicknessUnit, w.WavelengthUnit);
        h.Expect(bulk.FromText == "1" && bulk.ToText == "5" && bulk.Unit == "QWOT" && bulk.Material == BoundsForm.AllMaterials,
                 "every layer, all materials, in the design's unit, to begin with");
        bulk.Material = "TiO2";
        h.Expect(bulk.Preview == "Sets layers 3, 5.", $"the preview names the TiO2 layers ({bulk.Preview})");
        bulk.MinText = "0.5";
        bulk.MaxText = "2";
        var req = bulk.ToRequest(out problem);
        h.Expect(req is { First: 0, Last: 4, Min: 0.5, Max: 2.0, Unit: "QWOT" } && req.Material == Array.IndexOf(w.MaterialNames.ToArray(), "TiO2"),
                 $"read as a request ({problem})");
        h.Expect(w.SetLayerBounds(req!) && w.Layers[2].BoundsText == "0.5 – 2" && w.Status.Contains("Layers 3, 5"),
                 $"applied: {w.Status}");
        bulk.FromText = "4";
        bulk.ToText = "2";
        h.Expect(bulk.ToRequest(out problem) is null && problem!.Contains("start layer"), $"a backwards range is refused: {problem}");
        bulk.FromText = "1";
        bulk.ToText = "5";
        bulk.Unit = "nm";
        bulk.MinText = "20";
        bulk.MaxText = "150";
        h.Expect(bulk.ToRequest(out _) is { Unit: "nm", Min: 20.0, Max: 150.0 }, "a physical bound in nm");
        w.SetWavelengthUnit("um");
        var micron = new BoundsForm(w.Layers, w.MaterialNames, "nm", w.WavelengthUnit) { MinText = "0.02", MaxText = "0.15" };
        h.Expect(micron.Unit == "µm" && micron.ToRequest(out _) is { Unit: "nm" } mr && Math.Abs(mr.Min!.Value - 20.0) < 1e-9 && Math.Abs(mr.Max!.Value - 150.0) < 1e-9,
                 "and one typed in µm in a µm design is held in nm");
    }
}
