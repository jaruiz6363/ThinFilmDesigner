// DesignStringTests.cs — Input design string: "(1H 0.8L)12" read into a stack with its pickups.
//
// The user's own examples are the cases: (1H 0.8L)12 is 24 layers with every H picked up from the
// first H and every L from the first L, and ( 1H (1L 1H)6 1L)3 (0.5H 0.3L)2 is nested groups with
// fractional layers. Thicknesses are checked against each material's index at the reference, taken
// from the database, so a layer counted in the wrong material's index fails here.

using ThinFilm.Api;
using ThinFilm.Gui.Core;

namespace ThinFilm.Native.Tests;

public static class DesignStringTests
{
    private static readonly MaterialDatabase Db = MaterialDatabase.Shipped();
    private static double N(string material, double nm) => Db.Find(material)!.Model.At(nm, material).N;

    private static string Spell(StringLayer[] layers) =>
        string.Join(" ", layers.Select(l => $"{l.Count:0.###}{l.Letter}"));

    public static void TheNotationIsReadAsWritten(Harness h)
    {
        h.Section("design strings: numbers, letters, groups and repeats");

        var mirror = DesignString.Expand("(1H 0.8L)12");
        h.Expect(mirror.Length == 24, $"(1H 0.8L)12 is 24 layers ({mirror.Length})");
        h.Expect(mirror.Where((l, i) => i % 2 == 0).All(l => l is { Letter: 'H', Count: 1.0 })
                 && mirror.Where((l, i) => i % 2 == 1).All(l => l is { Letter: 'L', Count: 0.8 }),
                 "alternating 1H and 0.8L, H first");

        var nested = DesignString.Expand("( 1H (1L 1H)6 1L)3  (0.5H 0.3L)2");
        h.Expect(nested.Length == 3 * 14 + 4, $"( 1H (1L 1H)6 1L)3 (0.5H 0.3L)2 is 3 x 14 + 4 = 46 layers ({nested.Length})");
        string once = "1H " + string.Concat(Enumerable.Repeat("1L 1H ", 6)) + "1L";
        string expected = string.Join(" ", Enumerable.Repeat(once, 3)) + " 0.5H 0.3L 0.5H 0.3L";
        h.Expect(Spell(nested) == expected, "in the order written, the inner group repeated inside the outer");

        h.Expect(Spell(DesignString.Expand("(HL)3")) == "1H 1L 1H 1L 1H 1L", "a letter alone is 1, and spaces are optional");
        h.Expect(Spell(DesignString.Expand("2hl")) == "2H 1L", "letters are read in upper case");
        h.Expect(Spell(DesignString.Expand("(1H 1L)^2 .5H")) == "1H 1L 1H 1L 0.5H", "'^' before a repeat, and .5");
        h.Expect(Spell(DesignString.Expand("((H)2 L)2")) == "1H 1H 1L 1H 1H 1L", "a group may hold only a group");

        foreach (var (bad, says) in new[]
        {
            ("(1H 1L", "not closed"), ("1H 1L)", "no '('"), ("1.2.3H", "not a number"), ("1H 3", "needs a material"),
            ("(HL)0", "at least once"), ("()", "no layers"), ("1H @", "'@'"), ("(H)9999", "more than"),
            ("(HL)1.5", "whole number"), ("0H", "thicker than zero"), ("", "no layers"), ("(HL)^", "'^'"),
        })
        {
            try { DesignString.Expand(bad); h.Expect(false, $"'{bad}' is refused"); }
            catch (FormatException e) { h.Expect(e.Message.Contains(says), $"'{bad}' is refused: {e.Message}"); }
        }
    }

    public static void AStringReplacesTheStackWithItsPickups(Harness h)
    {
        h.Section("Input design string: the stack, in QWOT or FWOT, each letter picked up from its first layer");

        var form = new NewDesignForm(Db) { Layers = "4" };
        var api = new ThinFilmApi();
        string id = api.CreateNew(form.ToRequest(out _)!).Value!.Id;
        api.AddOperands(id, new OperandSweep("RA", "Target", 0.0, 1.0, 420, 680, 20));
        var names = api.MaterialNames(id).Value!;
        var map = new Dictionary<char, int> { ['H'] = Array.IndexOf(names, "TiO2"), ['L'] = Array.IndexOf(names, "SiO2") };
        double qH = 550.0 / (4 * N("TiO2", 550)), qL = 550.0 / (4 * N("SiO2", 550));
        LayerReport[] Layers() => api.Evaluate(id).Value!.Layers;

        // THE USER'S MIRROR.
        h.Expect(api.ApplyDesignString(id, new DesignStringRequest("(1H 0.8L)12", map)).Ok, "(1H 0.8L)12 applied");
        var l = Layers();
        h.Expect(l.Length == 24, $"24 layers ({l.Length})");
        h.Expect(l[0].Material == "TiO2" && l[1].Material == "SiO2", "layer 1, next to the incident medium, is H");
        h.Expect(Math.Abs(l[0].Thickness - qH) < 1e-9 && Math.Abs(l[1].Thickness - 0.8 * qL) < 1e-9,
                 $"1 QWOT of TiO2 and 0.8 QWOT of SiO2 at 550 nm ({l[0].Thickness:F3}, {l[1].Thickness:F3} nm)");
        h.Expect(l[0].Pickup is null && l[1].Pickup is null && l[0].Variable && l[1].Variable,
                 "the first H and the first L are free variables");
        h.Expect(l.Skip(2).All(x => x.Pickup is { Scale: 1.0, Offset: 0.0 } p && p.Parent == (x.Material == "TiO2" ? 0 : 1)),
                 "every other H is picked up from layer 1 and every other L from layer 2, at scale 1");
        h.Expect(l.All(x => x.UnitSetting is null && x.Unit == "QWOT"), "in QWOT, the design's own unit");
        h.Expect(api.SetLayerValue(id, 1, 1.0).Ok && Layers().Where(x => x.Material == "SiO2").All(x => Math.Abs(x.Value - 1.0) < 1e-12),
                 "making layer 2 a full quarter-wave makes every L one");

        // NESTED, WITH FRACTIONS: the fractional layers are picked up at the ratio of their numbers.
        h.Expect(api.ApplyDesignString(id, new DesignStringRequest("( 1H (1L 1H)6 1L)3  (0.5H 0.3L)2", map)).Ok,
                 "the nested example applied");
        l = Layers();
        h.Expect(l.Length == 46, $"46 layers ({l.Length})");
        h.Expect(l[42].Pickup is { Parent: 0, Scale: 0.5 } && l[43].Pickup is { Parent: 1 } p43 && Math.Abs(p43.Scale - 0.3) < 1e-12,
                 "0.5H is half of layer 1, 0.3L is 0.3 of layer 2");
        h.Expect(Math.Abs(l[42].Value - 0.5) < 1e-12 && Math.Abs(l[43].Value - 0.3) < 1e-12, "and reads 0.5 and 0.3 QWOT");

        // FROM THE SUBSTRATE: the string read backwards into the table; the parents stay at the top.
        h.Expect(api.ApplyDesignString(id, new DesignStringRequest("1H 2L 3H", map, FirstNextToSubstrate: true)).Ok,
                 "1H 2L 3H with the leftmost layer next to the substrate");
        l = Layers();
        h.Expect(l.Select(x => $"{x.Value:0.###}{x.Material}").SequenceEqual(["3TiO2", "2SiO2", "1TiO2"]),
                 $"layer 1 is 3H, the last layer 1H ({string.Join(" ", l.Select(x => $"{x.Value:0.###}{x.Material}"))})");
        h.Expect(l[2].Pickup is { Parent: 0 } p2 && Math.Abs(p2.Scale - 1.0 / 3.0) < 1e-12,
                 "the 1H next to the substrate is picked up from layer 1 at a third");

        // FWOT, AND NO PICKUPS.
        h.Expect(api.ApplyDesignString(id, new DesignStringRequest("(0.25H 0.25L)2", map, "FWOT", Pickups: false)).Ok,
                 "(0.25H 0.25L)2 in FWOT, without pickups");
        l = Layers();
        h.Expect(l.All(x => x.UnitSetting == "FWOT" && x.Pickup is null && x.Variable),
                 "each layer names FWOT, the design being in QWOT, and each is a variable of its own");
        h.Expect(Math.Abs(l[0].Thickness - qH) < 1e-9, "0.25 FWOT is the same quarter-wave");

        // REFUSALS leave the stack as it was.
        var before = Layers().Length;
        var r = api.ApplyDesignString(id, new DesignStringRequest("(1H 1X)3", map));
        h.Expect(!r.Ok && r.Error!.Contains("X") && Layers().Length == before, $"a letter with no material is refused: {r.Error}");
        r = api.ApplyDesignString(id, new DesignStringRequest("(1H 1L", map));
        h.Expect(!r.Ok && Layers().Length == before, $"so is a string that does not parse: {r.Error}");
        h.Expect(!api.ApplyDesignString(id, new DesignStringRequest("1H", map, "nm")).Ok, "and a unit other than QWOT or FWOT");

        // THE OPTIMISER moves a letter as one thickness.
        api.ApplyDesignString(id, new DesignStringRequest("(1H 1L)4", map));
        double start = api.Evaluate(id).Value!.Merit;
        var o = api.Optimise(id, new OptimiseRequest(MaxIterations: 60));
        l = Layers();
        h.Expect(o.Ok && o.Value!.FinalMerit < start, $"DLS improves the picked-up stack ({start:G4} -> {o.Value?.FinalMerit:G4})");
        h.Expect(l.Where(x => x.Material == "TiO2").Select(x => x.Thickness).Distinct().Count() == 1,
                 "and every H is still the same thickness");
    }

    public static void TheFormGuessesTheLettersAndChecksThem(Harness h)
    {
        h.Section("the Input design string form: letters found as typed, H and L guessed by index");

        string[] materials = ["SiO2", "TiO2", "Al2O3"];
        double[] n = [1.46, 2.35, 1.66];
        var form = new DesignStringForm(materials, (i, _) => 4 * n[i] / 550.0, "QWOT", 4, "(1H 0.8L)12");
        h.Expect(form.Parses && form.Preview == "24 layers: 12 H, 12 L.", $"the preview counts the layers ({form.Preview})");
        h.Expect(form.Letters.Select(l => $"{l.Letter}={l.Material}").SequenceEqual(["H=TiO2", "L=SiO2"]),
                 "H is the highest index and L the lowest");
        form.Letters[1].Material = "Al2O3";
        form.Text = "(1H 0.8L 1M)2";
        form.Update();
        h.Expect(form.Letters.Select(l => $"{l.Letter}={l.Material}").SequenceEqual(["H=TiO2", "L=Al2O3", "M=Al2O3"]),
                 "a letter keeps its choice as the string changes, and M is the middle index");
        form.Text = "(1H 0.8L";
        form.Update();
        h.Expect(!form.Parses && form.Preview.Contains("not closed") && form.Letters.Count == 3,
                 "a half-typed string says why and keeps the letters");
        h.Expect(form.ToRequest(out var problem) is null && problem!.Contains("not closed"), "and OK is refused");

        form.Text = "1H 1Q";
        h.Expect(form.ToRequest(out problem) is null && problem == "choose a material for Q",
                 $"a letter with no material asks for one ({problem})");
        form.Text = "1H 1S";
        var request = form.ToRequest(out problem);
        h.Expect(request is not null && request.Materials['S'] == 0 && request.Materials['H'] == 1,
                 "a letter is guessed from a material's name, S for SiO2");
        h.Expect(new DesignStringForm(materials, (i, _) => 4 * n[i] / 550.0, "FWOT", 4).Unit == "FWOT",
                 "the numbers are FWOT when the design is");
        h.Expect(new DesignStringForm(materials, (i, _) => 4 * n[i] / 550.0, "nm", 4).Unit == "QWOT",
                 "and QWOT when it is in nm");
    }
}
