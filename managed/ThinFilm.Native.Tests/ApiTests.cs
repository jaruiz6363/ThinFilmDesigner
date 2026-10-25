// ApiTests.cs — X1.7, and the two claims an API layer has to earn.
//
// FIRST: A SAVED DESIGN RELOADS TO THE SAME NUMBERS. Not nearly the same — the same. A document
// that reloads to a design evaluating slightly differently is worse than one that fails to load,
// because the failure is visible and the drift is not: somebody saves a design, reopens it a month
// later, and the merit has moved for reasons nothing reports.
//
// SECOND: THE API COMPUTES NOTHING. Spectrum() is where it would happen — a caller asks for R
// every nanometre and the obvious implementation interpolates the operand points. So the test
// asks for a spectrum whose grid lands exactly on the design's own wavelengths and checks the
// answer is bit-identical to evaluating those operands directly. An interpolating implementation
// passes that; one that interpolates WRONGLY is caught by the second half, which puts a grid
// point between two samples and checks the engine was asked rather than a straight line drawn.

using ThinFilm.Api;
using ThinFilm.Native;

namespace ThinFilm.Native.Tests;

public static class ApiTests
{
    public static void ASampleOpensAndDescribesItself(Harness h)
    {
        h.Section("the API opens every sample and describes what it opened");

        var api = new ThinFilmApi();
        h.Pin(api.AbiVersion, TfAbi.TF_ABI_VERSION, "the API reports the ABI it is bound to");
        h.Expect(SampleFiles.Names.Length == 10, $"ten samples ({SampleFiles.Names.Length})");

        foreach (var name in SampleFiles.Names)
        {
            var r = api.OpenSample(name);
            if (!r.Ok) { h.Fail($"{name}: {r.Error}"); continue; }

            var s = r.Value!;
            h.Expect(s.Layers > 0 && s.Operands > 0, $"{name}: has layers and operands");
            h.Expect(s.TotalThickness > 0.0, $"{name}: has a total thickness");
            h.Expect(double.IsFinite(s.Merit), $"{name}: has a finite merit");
            h.Expect((api.Document(s.Id).Value!.Notes ?? "").Contains("Caveats", StringComparison.Ordinal),
                     $"{name}: its notes carry its caveats to the user");
        }

        h.Pin(api.Sessions.Count, 10, "ten sessions open");
    }

    public static void AnUnknownNameIsRefusedWithHelp(Harness h)
    {
        h.Section("mistakes are refused with something a user can act on");

        var api = new ThinFilmApi();

        var bad = api.Load("{ not a design");
        h.Expect(!bad.Ok && bad.Error is not null, $"text that is not a design is refused, saying why: {bad.Error}");

        var missing = api.Describe("d99");
        h.Expect(!missing.Ok, "an unknown session is refused");
        h.Expect(missing.Error is not null && missing.Error.Contains("nothing is open", StringComparison.Ordinal),
                 $"and says what IS open: {missing.Error}");

        // A typo in a target code names the near misses rather than just refusing.
        try
        {
            Codes.Target("RSS");
            h.Fail("'RSS' should not be a target code");
        }
        catch (FormatException e)
        {
            h.Expect(e.Message.Contains("RS", StringComparison.Ordinal),
                     $"a mistyped code suggests the real one: {e.Message}");
        }

        var api2 = new ThinFilmApi();
        var opened = api2.OpenSample("AR-visible");
        h.Expect(opened.Ok, "a real sample opens");
        var stage = api2.Optimise(opened.Value!.Id, new OptimiseRequest(Stages: "polish"));
        h.Expect(!stage.Ok, "an unknown stage is refused");
        h.Expect(stage.Error is not null && stage.Error.Contains("needle", StringComparison.Ordinal),
                 $"and lists the real ones: {stage.Error}");
    }

    /// <summary>
    /// THE CLAIM THE WHOLE FILE IS FOR. Save, serialise to JSON, deserialise, load, evaluate —
    /// and the merit must be the same bits. Anything less and a design is not something you can
    /// put in a file.
    /// </summary>
    public static void ADocumentRoundTripsExactly(Harness h)
    {
        h.Section("save, reload and re-evaluate gives back the identical merit, bit for bit");

        var api = new ThinFilmApi();
        foreach (var name in SampleFiles.Names)
        {
            var opened = api.OpenSample(name);
            if (!opened.Ok) { h.Fail($"{name}: {opened.Error}"); continue; }
            string id = opened.Value!.Id;

            var before = api.Evaluate(id);
            if (!before.Ok) { h.Fail($"{name}: {before.Error}"); continue; }

            var saved = api.Save(id);
            if (!saved.Ok) { h.Fail($"{name}: save — {saved.Error}"); continue; }

            var reopened = api.Load(saved.Value!);
            if (!reopened.Ok) { h.Fail($"{name}: load — {reopened.Error}"); continue; }

            var after = api.Evaluate(reopened.Value!.Id);
            if (!after.Ok) { h.Fail($"{name}: re-evaluate — {after.Error}"); continue; }

            h.PinExact(after.Value!.Merit, before.Value!.Merit, $"{name}: merit survives the round trip");
            h.Pin(after.Value.Operands.Length, before.Value.Operands.Length,
                  $"{name}: operand count survives");

            for (int o = 0; o < before.Value.Operands.Length; ++o)
            {
                h.PinExact(after.Value.Operands[o].Value, before.Value.Operands[o].Value,
                           $"{name}: operand {o} value survives");
                h.PinExact(after.Value.Operands[o].Residual, before.Value.Operands[o].Residual,
                           $"{name}: operand {o} residual survives");
            }
        }
    }

    /// <summary>The JSON is a file a person can read, and a changed file changes the design.</summary>
    public static void TheDocumentIsReadableAndItsEditsTakeEffect(Harness h)
    {
        h.Section("the saved form is readable JSON, and editing it changes the design");

        var api = new ThinFilmApi();
        var opened = api.OpenSample("V-coat");
        h.Expect(opened.Ok, "V-coat opens");
        var json = api.Save(opened.Value!.Id).Value!;

        // Names, not numbers: a file that said "code": 0 would be unreadable and uncheckable.
        h.Expect(json.Contains("\"Code\": \"RA\"", StringComparison.Ordinal),
                 "operands are named, not numbered");
        h.Expect(json.Contains("\"Comparison\"", StringComparison.Ordinal), "comparisons are named");
        h.Expect(json.Contains("\"Thickness\"", StringComparison.Ordinal), "layers carry thicknesses");

        var doc = DesignDocument.FromJson(json);
        var thicker = doc.WithThickness([doc.Layers[0].Thickness + 25.0, doc.Layers[1].Thickness]);

        var a = api.CreateFromDocument(doc);
        var b = api.CreateFromDocument(thicker);
        h.Expect(a.Ok && b.Ok, "both versions open");
        h.Expect(a.Value!.Merit != b.Value!.Merit,
                 "and a 25 nm edit changes the merit, so the document really drives the design");
    }

    /// <summary>
    /// THE API MUST NOT COMPUTE A SPECTRUM. Asked for a grid that lands on the design's own
    /// wavelengths, it must return exactly what the engine returns for those points.
    /// </summary>
    public static void ASpectrumComesFromTheEngineAndNotFromInterpolation(Harness h)
    {
        h.Section("a spectrum on the design's own grid is bit-identical to evaluating it");

        var api = new ThinFilmApi();
        var opened = api.OpenSample("AR-visible");
        h.Expect(opened.Ok, "AR-visible opens");
        string id = opened.Value!.Id;

        var doc = api.Document(id).Value!;
        double from = doc.Wavelength[0], to = doc.Wavelength[^1];
        int steps = doc.Wavelength.Length;

        var spectrum = api.Spectrum(id, new SpectrumRequest(from, to, steps, 0.0, ["RA"]));
        if (!spectrum.Ok) { h.Fail($"spectrum: {spectrum.Error}"); return; }

        var evaluation = api.Evaluate(id).Value!;

        // The sample's first 27 operands are RA at each point, in order, so they line up.
        var ra = spectrum.Value!.Quantity["RA"];
        h.Pin(ra.Length, steps, "one value per requested step");
        h.Pin(spectrum.Value.Wavelength.Length, steps, "and one wavelength per value");

        int compared = 0;
        for (int i = 0; i < steps && i < evaluation.Operands.Length; ++i)
        {
            if (evaluation.Operands[i].Code != "RA") continue;
            h.PinExact(ra[i], evaluation.Operands[i].Value,
                       $"spectrum[{i}] is the engine's own value, not an interpolation");
            ++compared;
        }
        h.Expect(compared >= 20, $"and enough points were compared to mean something ({compared})");

        // OFF-GRID, where an interpolating implementation would be caught. A ripple between two
        // samples is exactly what a filter designer is looking for, and a straight line drawn
        // between them is exactly how it disappears.
        var dense = api.Spectrum(id, new SpectrumRequest(from, to, steps * 4 - 3, 0.0, ["RA"]));
        if (!dense.Ok) { h.Fail($"dense spectrum: {dense.Error}"); return; }
        var denseRa = dense.Value!.Quantity["RA"];

        // Every fourth dense point lands on a coarse one and must match exactly.
        for (int i = 0; i < steps; ++i)
            h.PinExact(denseRa[i * 4], ra[i], $"the dense grid agrees on shared point {i}");

        // And the points between are NOT the straight line between their neighbours, which they
        // would be if anything here were interpolating the spectrum.
        int offLine = 0;
        for (int i = 0; i + 1 < steps; ++i)
        {
            double mid = denseRa[i * 4 + 2];
            double line = 0.5 * (ra[i] + ra[i + 1]);
            if (Math.Abs(mid - line) > 1e-12) ++offLine;
        }
        h.Expect(offLine > steps / 2,
                 $"the intermediate points are the engine's, not a chord ({offLine} of {steps - 1} "
                 + "differ from the straight line)");
    }

    public static void ChangingTheStackGoesThroughTheApi(Harness h)
    {
        h.Section("thickness edits are in place; adding a layer rebuilds the design");

        var api = new ThinFilmApi();
        var opened = api.OpenSample("AR-visible");
        string id = opened.Value!.Id;
        int layers = opened.Value.Layers;

        var before = api.Evaluate(id).Value!.Merit;
        h.Expect(api.SetThickness(id, 0, 55.0).Ok, "a thickness can be set");
        var after = api.Evaluate(id).Value!.Merit;
        h.Expect(before != after, "and it changes the merit");

        h.Expect(!api.SetThickness(id, 99, 10.0).Ok, "a layer that does not exist is refused");
        h.Expect(!api.SetThickness(id, 0, -5.0).Ok, "a negative thickness is refused");

        var inserted = api.InsertLayer(id, 1, 0, 40.0);
        h.Expect(inserted.Ok, $"a layer can be inserted: {inserted.Error}");
        h.Pin(inserted.Value!.Layers, layers + 1, "and the stack is one longer");

        var removed = api.RemoveLayer(id, 1);
        h.Expect(removed.Ok, "and removed again");
        h.Pin(removed.Value!.Layers, layers, "leaving the stack as it was");

        // The document follows the design, so a save after a shape change is loadable.
        var saved = api.Save(id);
        h.Expect(saved.Ok, "and the result saves");
        h.Expect(api.Load(saved.Value!).Ok, "and reloads");
    }

    public static void OptimisationRunsThroughTheApi(Harness h)
    {
        h.Section("the stages run by name, and the session carries the result");

        var api = new ThinFilmApi();
        var opened = api.OpenSample("AR-visible");
        string id = opened.Value!.Id;
        double start = opened.Value.Merit;

        var refined = api.Optimise(id, new OptimiseRequest(Stages: "refine", MaxIterations: 120));
        h.Expect(refined.Ok, $"refine runs: {refined.Error}");
        h.Expect(refined.Value!.FinalMerit < start, "and improves the design");
        h.Expect(refined.Value.StagesRun.Length == 1, "one stage ran");

        // The SESSION now holds the refined design: a front end that asked again would otherwise
        // get the design it started with and no warning.
        var described = api.Describe(id);
        h.PinExact(described.Value!.Merit, refined.Value.FinalMerit,
                   "the session carries the optimised design, not the original");

        // A shape-changing sequence, and the document must survive it.
        var synth = api.Optimise(id, new OptimiseRequest(
            Stages: "needle,evolve", NeedleInsertions: 3, MinimumThickness: 3.0));
        h.Expect(synth.Ok, $"needle and evolve run: {synth.Error}");
        h.Expect(synth.Value!.StagesRun.Length == 2, "both stages ran");

        var saved = api.Save(id);
        h.Expect(saved.Ok, "the result saves after a shape change");
        var reloaded = api.Load(saved.Value!);
        h.Expect(reloaded.Ok, "and reloads");
        h.PinExact(reloaded.Value!.Merit, synth.Value.FinalMerit,
                   "to the same merit — the document tracked the shape change");
    }

    public static void ANeedleMapComesBackWithItsBestPoint(Harness h)
    {
        h.Section("the needle map is exposed with the insertion it recommends");

        var api = new ThinFilmApi();
        var opened = api.OpenSample("Bandpass");
        string id = opened.Value!.Id;

        var map = api.NeedleMap(id, depthSamples: 32);
        h.Expect(map.Ok, $"the map comes back: {map.Error}");
        h.Pin(map.Value!.Depth.Length, 32, "one entry per depth");
        h.Pin(map.Value.Map.Length, 32 * map.Value.Candidate.Length,
              "candidate-major, one entry per candidate and depth");
        h.Expect(map.Value.BestSlope <= 0.0 || map.Value.Map.All(v => v > 0.0),
                 "the best entry is the most negative one, or none is negative");

        // The reported best really is the smallest entry — the summary must not disagree with the
        // array it summarises.
        double smallest = map.Value.Map.Min();
        h.PinExact(map.Value.BestSlope, smallest, "and the reported best IS the smallest entry");
    }

    /// <summary>
    /// A native refusal must arrive as a status a front end can show, not as a stack trace or a
    /// generic failure. TF_NO_STRESS_DATA tells a user what to fix.
    /// </summary>
    public static void ANativeRefusalReachesTheCallerByName(Harness h)
    {
        h.Section("a refusal from the engine arrives with its own name attached");

        var api = new ThinFilmApi();
        var doc = new DesignDocument
        {
            Name = "stress with no data",
            Wavelength = [550.0],
            Materials = [MaterialDocument.Constant("L", 1.46, 0.0, 1)],
            Layers = [new LayerDocument(0, 100.0)],
            Points = [new PointDocument(0, 0.0)],
            Incident = MaterialDocument.Constant("air", 1.0, 0.0, 1),
            Exit = MaterialDocument.Constant("glass", 1.52, 0.0, 1),
            Operands = [new OperandDocument("STRESS", "EQUAL", 0.0, 1.0)],
        };

        var r = api.CreateFromDocument(doc);
        h.Expect(!r.Ok, "a STRESS operand with no stress data is refused");
        h.Expect(r.Status == "TF_NO_STRESS_DATA",
                 $"and the engine's own status reaches the caller: {r.Status ?? "(none)"}");
    }
}
