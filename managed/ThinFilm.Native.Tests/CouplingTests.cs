// CouplingTests.cs — Couple all layers, Decouple all layers and Delete all layers.
//
// The user (2026-10-16): couple every layer to the first instance of its material in the stack,
// with no dialog; decouple removes every pickup; delete all empties the stack. Coupling must not
// move any layer, so each check compares thicknesses before and after.

using ThinFilm.Api;
using ThinFilm.Gui.Core;

namespace ThinFilm.Native.Tests;

public static class CouplingTests
{
    private static readonly MaterialDatabase Db = MaterialDatabase.Shipped();

    private static (ThinFilmApi Api, string Id, Dictionary<char, int> Map) Design()
    {
        var api = new ThinFilmApi();
        string id = api.CreateNew(new NewDesignForm(Db) { Layers = "4" }.ToRequest(out _)!).Value!.Id;
        api.AddOperands(id, new OperandSweep("RA", "Target", 0.0, 1.0, 420, 680, 20));
        var names = api.MaterialNames(id).Value!;
        return (api, id, new Dictionary<char, int> { ['H'] = Array.IndexOf(names, "TiO2"), ['L'] = Array.IndexOf(names, "SiO2") });
    }

    private static LayerReport[] Layers(ThinFilmApi api, string id) => api.Evaluate(id).Value!.Layers;

    public static void CoupleAllTiesEachMaterialToItsFirstLayer(Harness h)
    {
        h.Section("Couple all layers: each layer picked up from the first of its material, nothing moved");

        var (api, id, map) = Design();
        api.ApplyDesignString(id, new DesignStringRequest("1H 2L 0.5H 1L 3H", map, Pickups: false));
        var before = Layers(api, id).Select(l => l.Thickness).ToArray();
        var c = api.CoupleLayers(id);
        var l = Layers(api, id);
        h.Expect(c.Ok && c.Value == 3, $"three layers coupled ({c.Value})");
        h.Expect(l[0].Pickup is null && l[1].Pickup is null, "the first H and the first L are free");
        h.Expect(l[2].Pickup is { Parent: 0, Scale: 0.5 } && l[3].Pickup is { Parent: 1, Scale: 0.5 } && l[4].Pickup is { Parent: 0, Scale: 3.0 },
                 "the rest follow them at the ratio of their thicknesses");
        h.Expect(l.Select(x => x.Thickness).Zip(before).All(p => Math.Abs(p.First - p.Second) < 1e-9), "and no layer moved");
        h.Expect(api.SetLayerValue(id, 0, 2.0).Ok && Math.Abs(Layers(api, id)[4].Value - 6.0) < 1e-12, "the H layers now move together");

        // AGAIN, after an edit: the old pickups are replaced, not added to.
        h.Expect(api.CoupleLayers(id).Value == 3, "coupling again couples the same three");

        // A MATERIAL IN TWO UNITS gets a parent per unit, since a pickup joins layers in one unit.
        api.DecoupleLayers(id);
        h.Expect(api.SetLayerUnit(id, 4, "nm").Ok, "layer 5 (H) in nm");
        api.CoupleLayers(id);
        l = Layers(api, id);
        h.Expect(l[4].Pickup is null && l[2].Pickup is { Parent: 0 }, "the H in nm is a free layer, not tied to a QWOT one");

        // A ZERO-THICKNESS FIRST LAYER cannot be a parent: there is no ratio.
        api.ApplyDesignString(id, new DesignStringRequest("1H 1L 1H 1L", map, Pickups: false));
        api.SetLayerValue(id, 0, 0.0);
        api.CoupleLayers(id);
        l = Layers(api, id);
        h.Expect(l[0].Pickup is null && l[2].Pickup is null && l[3].Pickup is { Parent: 1 },
                 "a zero-thickness H is passed over as a parent, and the next H is free");
    }

    public static void DecoupleAllFreesEveryLayer(Harness h)
    {
        h.Section("Decouple all layers: every pickup removed, each layer a variable of its own");

        var (api, id, map) = Design();
        api.ApplyDesignString(id, new DesignStringRequest("(1H 0.8L)6", map));
        var before = Layers(api, id).Select(l => l.Thickness).ToArray();
        var d = api.DecoupleLayers(id);
        var l = Layers(api, id);
        h.Expect(d.Ok && d.Value == 10, $"ten pickups removed ({d.Value})");
        h.Expect(l.All(x => x.Pickup is null && x.Variable), "every layer free and varied");
        h.Expect(l.Select(x => x.Thickness).Zip(before).All(p => Math.Abs(p.First - p.Second) < 1e-12), "and none moved");
        h.Expect(api.DecoupleLayers(id).Value == 0, "decoupling a free stack does nothing");
    }

    public static void DeleteAllLeavesTheBareSubstrate(Harness h)
    {
        h.Section("Delete all layers: an empty stack, evaluated, saved and built up again");

        var (api, id, map) = Design();
        h.Expect(api.ClearLayers(id).Ok, "all layers deleted");
        var e = api.Evaluate(id);
        h.Expect(e.Ok && e.Value!.Layers.Length == 0, $"the empty stack evaluates ({e.Error})");
        var sp = api.Spectrum(id, new SpectrumRequest(400, 700, 7, 0.0));
        h.Expect(sp.Ok, $"its spectrum is the bare substrate's ({sp.Error})");
        var saved = api.Save(id);
        h.Expect(saved.Ok && api.Load(saved.Value!).Ok, "it saves and reopens");
        var o = api.Optimise(id, new OptimiseRequest(MaxIterations: 10));
        h.Expect(!o.Ok && o.Error!.Contains("no layers"), $"optimising it is refused, saying why ({o.Error})");
        h.Expect(api.ApplyDesignString(id, new DesignStringRequest("(HL)2", map)).Ok && Layers(api, id).Length == 4,
                 "and a design string builds it up again");

        var (api2, id2, _) = Design();
        for (int i = 0; i < 4; ++i) api2.RemoveLayer(id2, 0);
        h.Expect(Layers(api2, id2).Length == 0, "deleting layers one by one may empty the stack too");
    }
}
