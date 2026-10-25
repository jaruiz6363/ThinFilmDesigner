// ReverseTests.cs — Stack > Reverse layers, and Reverse layers and media.
//
// The user (2026-10-19): "One item that would help me test the AOI is to have an operation, reverse
// entire stack including substrate and ambient. (Having an operation reverse entire stack, keeping
// substrate and ambient in place would also be good)." And on the substrate's k becoming the
// incident medium's: "I don't like the idea of physically replacing the k ... Can't we do this code
// setting the k to zero for just this calculation? ... Some kind of warning is justified."
//
// The oracle is reciprocity: T of a stack seen from the other side, at the angle Snell's law gives
// there, is the same T; R is the same too while nothing absorbs, and differs once a layer does.

using System.Text.Json.Nodes;
using ThinFilm.Api;
using ThinFilm.Cli;
using ThinFilm.Mcp;

namespace ThinFilm.Native.Tests;

public static class ReverseTests
{
    private static readonly double[] Grid = [500.0, 600.0];

    /// <summary>Air | H 60 | L 95 | A 20 | H 40 | glass. A absorbs when absorberK is not 0.</summary>
    private static DesignDocument Stack(double absorberK, double substrateK = 0.0) => new()
    {
        Name = "reverse",
        Wavelength = Grid,
        Materials = [MaterialDocument.Constant("H", 2.3, 0.0, Grid.Length),
                     MaterialDocument.Constant("L", 1.46, 0.0, Grid.Length),
                     MaterialDocument.Constant("A", 2.0, absorberK, Grid.Length)],
        Layers = [new LayerDocument(0, 60.0), new LayerDocument(1, 95.0) { Min = 10, Max = 200, Unit = "QWOT" },
                  new LayerDocument(2, 20.0, Variable: false), new LayerDocument(0, 40.0)],
        Points = [new PointDocument(0, 0.0)],
        Incident = MaterialDocument.Constant("air", 1.0, 0.0, Grid.Length),
        Exit = MaterialDocument.Constant("glass", 1.52, substrateK, Grid.Length),
        Operands = [new OperandDocument("RA", "EQUAL", 0.0, 1.0, Points: [0])],
    };

    private static double At(ThinFilmApi api, string id, double deg, string q) =>
        api.Spectrum(id, new SpectrumRequest(550, 560, 2, deg, [q])).Value!.Quantity[q][0];

    public static void ReversingTwiceIsTheSameDesign(Harness h)
    {
        h.Section("Reverse layers: the order flips, everything else travels with each layer, twice is the start");

        var api = new ThinFilmApi();
        string id = api.CreateFromDocument(Stack(0.0)).Value!.Id;
        api.CoupleLayers(id);
        var start = api.Document(id).Value!;
        var r = api.ReverseLayers(id, media: false);
        var once = api.Document(id).Value!;
        h.Expect(r.Ok && once.Layers.Select(l => l.Thickness).SequenceEqual(start.Layers.Select(l => l.Thickness).Reverse()),
                 $"the thicknesses read backwards ({r.Error})");
        h.Expect(once.Layers[2] is { Unit: "QWOT", Min: 10, Max: 200 } && once.Layers[1].Variable == false,
                 "a layer's unit, bounds and vary flag go with it");
        h.Expect(once.Layers[0].Pickup is { Parent: 3 } && once.Layers[3].Pickup is null,
                 "the pickup now names the coupled layer at its new place (H 40 from H 60, now layer 4)");
        h.Expect(once.Incident.Name == "air" && once.Exit.Name == "glass", "the media stay where they were");
        api.ReverseLayers(id, media: false);
        h.Expect(api.Document(id).Value!.ToJson() == start.ToJson(), "reversed twice, it is the design it started as");
        api.ReverseLayers(id, media: true);
        api.ReverseLayers(id, media: true);
        h.Expect(api.Document(id).Value!.ToJson() == start.ToJson(), "and so with the media");
    }

    public static void SeenFromTheOtherSideTransmissionIsTheSame(Harness h)
    {
        h.Section("Reverse layers and media: reciprocity - the same T from the other side, at the Snell angle");

        foreach (double k in new[] { 0.0, 0.05 })
        {
            var api = new ThinFilmApi();
            string front = api.CreateFromDocument(Stack(k)).Value!.Id;
            string back = api.CreateFromDocument(Stack(k)).Value!.Id;
            var r = api.ReverseLayers(back, media: true);
            h.Expect(r.Ok && r.Value!.Incident!.Name == "glass" && r.Value.Exit!.Name == "air",
                     $"k = {k}: glass is the incident medium and air the substrate");

            bool t = true, rs = true;
            double rFront = 0, rBack = 0;
            foreach (double deg in new[] { 0.0, 30.0, 60.0 })
            {
                double inGlass = Math.Asin(Math.Sin(deg * Math.PI / 180) / 1.52) * 180 / Math.PI;
                foreach (var q in new[] { "TS", "TP" })
                    t &= Math.Abs(At(api, front, deg, q) - At(api, back, inGlass, q)) < 1e-12;
                rFront = At(api, front, deg, "RS");
                rBack = At(api, back, inGlass, "RS");
                rs &= Math.Abs(rFront - rBack) < 1e-12;
            }
            h.Expect(t, $"k = {k}: Ts and Tp agree at 0, 30 and 60° in air (the same angles in the glass)");
            h.Expect(k == 0.0 ? rs : !rs,
                     k == 0.0 ? "lossless, R agrees too" : $"with an absorber, R differs ({rFront:0.0000} vs {rBack:0.0000} at 60°)");
        }
    }

    public static void AnIncidentMediumsKIsKeptButNotUsed(Harness h)
    {
        h.Section("a substrate with a k becomes the incident medium: the design keeps its k, the calculation uses 0");

        var api = new ThinFilmApi();
        string id = api.CreateFromDocument(Stack(0.0, substrateK: 1e-5)).Value!.Id;
        var r = api.ReverseLayers(id, media: true);
        h.Expect(r.Ok && r.Value!.Warnings.Any(w => w.Contains("glass has k up to 1E-05") && w.Contains("treated as 0")),
                 $"it is taken, with a warning ({string.Join(" | ", r.Value?.Warnings ?? [])})");
        var doc = api.Document(id).Value!;
        h.Expect(doc.Incident.K.All(v => v == 1e-5), "the design keeps the glass's own k");

        // The same reversed design with the glass made transparent by hand reads the same numbers.
        string clear = api.CreateFromDocument(doc with
        {
            Incident = MaterialDocument.Constant("glass", 1.52, 0.0, Grid.Length),
        }).Value!.Id;
        bool same = new[] { 0.0, 20.0, 40.0 }.All(deg =>
            new[] { "RS", "TP", "AA" }.All(q => At(api, id, deg, q) == At(api, clear, deg, q)));
        h.Expect(same, "every number is what a transparent glass gives, exactly");
        h.Expect(api.Describe(clear).Value!.Warnings.Length == 0, "and with no k there is no warning");

        api.ReverseLayers(id, media: true);
        h.Expect(api.Document(id).Value!.Exit.K.All(v => v == 1e-5) && api.Describe(id).Value!.Warnings.Length == 0,
                 "reversed back, the substrate has its k again and the warning goes");
    }

    public static void TheCliAndTheMcpReverse(Harness h)
    {
        h.Section("thinfilm stack reverse [--media], and the MCP's edit_stack reverse and reverse_media");

        var dir = Path.Combine(Path.GetTempPath(), "thinfilm-reverse-tests");
        Directory.CreateDirectory(dir);
        string design = Path.Combine(dir, "reverse.tfd");
        File.WriteAllText(design, Stack(0.0).ToJson());
        var o = new StringWriter();
        var e = new StringWriter();
        int code = CommandLine.Run(["stack", "reverse", design], o, e);
        var saved = DesignDocument.FromJson(File.ReadAllText(design));
        h.Expect(code == 0 && saved.Layers[0].Thickness == 40.0 && saved.Incident.Name == "air",
                 $"stack reverse writes the reversed layers back ({e.ToString().Trim()})");
        code = CommandLine.Run(["stack", "reverse", design, "--media"], o = new StringWriter(), e = new StringWriter());
        saved = DesignDocument.FromJson(File.ReadAllText(design));
        h.Expect(code == 0 && saved.Layers[0].Thickness == 60.0 && saved.Incident.Name == "glass" && saved.Exit.Name == "air",
                 $"--media swaps the media too ({o.ToString().Split('\n')[0].Trim()})");

        var tools = new Tools();
        var (text, _) = tools.Call("load_design", new JsonObject { ["document"] = Stack(0.0).ToJson() });
        string id = JsonNode.Parse(text)!["Id"]!.GetValue<string>();
        (text, var failed) = tools.Call("edit_stack", new JsonObject { ["design"] = id, ["action"] = "reverse_media" });
        var json = JsonNode.Parse(text)!;
        h.Expect(!failed && json["Incident"]!["Name"]!.GetValue<string>() == "glass",
                 $"reverse_media answers with the media swapped ({text[..Math.Min(100, text.Length)]})");
        (_, failed) = tools.Call("edit_stack", new JsonObject { ["design"] = id, ["action"] = "reverse" });
        h.Expect(!failed, "and reverse is an action too");
    }
}
