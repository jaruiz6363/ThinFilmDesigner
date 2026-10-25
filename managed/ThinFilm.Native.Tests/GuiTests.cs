// GuiTests.cs — X1.10, testing the half of a GUI that a test can reach.
//
// A WINDOW NEEDS A DISPLAY AND ARITHMETIC DOES NOT, so the GUI is split: ThinFilm.Gui.Core holds
// the workspace and the plot model and has no Avalonia in it; ThinFilm.Gui holds the XAML and a
// control whose Render method walks the geometry Core produced. Everything below tests Core.
//
// WHAT IS AND IS NOT COVERED, stated plainly rather than implied by a green suite. Not covered:
// that the window opens, that a button is wired to the handler it looks wired to, that a column
// is wide enough to read. Those need a display, and the honest mitigation is that they are the
// failures a person sees in the first ten seconds. Covered: everything that computes — the axis
// arithmetic, including the degenerate cases that are real designs (a flat spectrum is an AR
// coating that worked), and the workspace operations, including that the numbers in the window
// are the API's own.

using ThinFilm.Api;
using ThinFilm.Gui.Core;

namespace ThinFilm.Native.Tests;

public static class GuiTests
{
    // ---- the plot arithmetic -----------------------------------------------------------------

    public static void TicksAreRoundNumbersInsideTheData(Harness h)
    {
        h.Section("axis ticks are round numbers, inside the range, and cover it");

        var model = new PlotModel();
        model.Update([new PlotSeries("R", [400, 500, 600, 700], [0.013, 0.004, 0.007, 0.021], 0)],
                     800, 400);

        h.Pin((long)model.XMin, 400, "the x axis starts at the data");
        h.Pin((long)model.XMax, 700, "and ends at it");
        h.Expect(model.YMin <= 0.004, $"the y axis includes the smallest value ({model.YMin:G4})");
        h.Expect(model.YMax >= 0.021, $"and the largest ({model.YMax:G4})");

        var xticks = model.XTicks();
        h.Expect(xticks.Count >= 3, $"enough x ticks to read ({xticks.Count})");
        foreach (var t in xticks)
        {
            h.Expect(t.Value >= model.XMin - 1e-9 && t.Value <= model.XMax + 1e-9,
                     $"tick {t.Value} is inside the axis");
            h.Expect(!t.Label.Contains("000000", StringComparison.Ordinal)
                     && !t.Label.Contains("999999", StringComparison.Ordinal),
                     $"and reads as a round number: '{t.Label}'");
        }

        // The step comes from the 1, 2, 2.5, 5 family, which is the whole reason a tick is not
        // 0.30000000000000004.
        foreach (var (lo, hi, target) in new[] { (0.0, 1.0, 5), (0.0, 0.021, 5), (400.0, 700.0, 6),
                                                 (-3.0, 7.0, 4) })
        {
            double step = PlotModel.NiceStep(lo, hi, target);
            double mantissa = step / Math.Pow(10, Math.Floor(Math.Log10(step)));
            h.Expect(Math.Abs(mantissa - 1) < 1e-9 || Math.Abs(mantissa - 2) < 1e-9
                     || Math.Abs(mantissa - 2.5) < 1e-9 || Math.Abs(mantissa - 5) < 1e-9,
                     $"step for [{lo},{hi}] is a nice number ({step:G6}, mantissa {mantissa:G4})");
        }
    }

    public static void TheMappingPutsTheDataInsideThePlot(Harness h)
    {
        h.Section("the first point is at the left edge, the last at the right, and y is flipped");

        var model = new PlotModel();
        model.Update([new PlotSeries("R", [400, 700], [0.0, 1.0], 0)], 800, 400);

        h.Expect(Math.Abs(model.ToPixelX(400) - model.Left) < 1e-9,
                 "the smallest x lands on the left edge");
        h.Expect(Math.Abs(model.ToPixelX(700) - (model.Left + model.PlotWidth)) < 1e-9,
                 "the largest x lands on the right edge");

        // FLIPPED: pixels grow downward, data grows upward. Getting this backwards draws every
        // spectrum upside down, which is obvious on a filter and easy to miss on a flat AR.
        h.Expect(model.ToPixelY(model.YMax) < model.ToPixelY(model.YMin),
                 "a larger value is HIGHER on the screen, meaning a smaller pixel y");
        h.Expect(Math.Abs(model.ToPixelY(model.YMax) - model.Top) < 1e-9,
                 "the top of the axis is the top of the plot");
    }

    /// <summary>
    /// THE DEGENERATE CASES, which are all real designs rather than hypotheticals.
    /// </summary>
    public static void TheAwkwardSpectraDoNotBreakIt(Harness h)
    {
        h.Section("a flat spectrum, a single point, an empty series and a NaN all survive");

        // A PERFECTLY FLAT SPECTRUM is an AR coating that worked. Zero y range, and the naive
        // mapping divides by it.
        var flat = new PlotModel();
        flat.Update([new PlotSeries("R", [400, 500, 600], [0.002, 0.002, 0.002], 0)], 800, 400);
        h.Expect(flat.YMax > flat.YMin, "a flat series gets a range rather than a division by zero");
        h.Expect(double.IsFinite(flat.ToPixelY(0.002)), "and maps to a finite pixel");
        foreach (var t in flat.YTicks())
            h.Expect(double.IsFinite(t.Position), $"its ticks are finite too ({t.Label})");

        // ONE POINT: zero x range as well.
        var single = new PlotModel();
        single.Update([new PlotSeries("R", [550], [0.01], 0)], 800, 400);
        h.Expect(single.XMax > single.XMin, "a single point gets an x range");
        h.Expect(double.IsFinite(single.ToPixelX(550)), "and maps");

        // NOTHING AT ALL, which is the state the window opens in.
        var empty = new PlotModel();
        empty.Update([], 800, 400);
        h.Expect(double.IsFinite(empty.ToPixelX(0.5)) && double.IsFinite(empty.ToPixelY(0.5)),
                 "an empty plot still has a usable mapping");
        h.Expect(empty.XTicks().Count > 0, "and ticks, so it draws as an empty chart not a void");

        // A NaN must not take the axis with it. An optical density is a logarithm, and a perfect
        // reflector is an infinite one.
        var nan = new PlotModel();
        nan.Update([new PlotSeries("OD", [400, 500, 600],
                                   [2.0, double.NaN, 4.0], 0)], 800, 400);
        h.Expect(double.IsFinite(nan.YMin) && double.IsFinite(nan.YMax),
                 $"a NaN does not poison the axis ({nan.YMin:G4}..{nan.YMax:G4})");
        h.Expect(nan.YMin <= 2.0 && nan.YMax >= 4.0, "which still covers the finite points");

        var line = nan.Polyline(new PlotSeries("OD", [400, 500, 600], [2.0, double.NaN, 4.0], 0));
        h.Pin(line.Count, 2, "and the non-finite point is dropped from the polyline");
        foreach (var p in line)
            h.Expect(double.IsFinite(p.X) && double.IsFinite(p.Y), "every drawn point is finite");

        // A window dragged to nothing must not produce a negative plot area.
        var tiny = new PlotModel();
        tiny.Update([new PlotSeries("R", [400, 700], [0, 1], 0)], 10, 10);
        h.Expect(tiny.PlotWidth > 0 && tiny.PlotHeight > 0,
                 "a window smaller than its own margins still has a positive plot area");
    }

    // ---- the workspace -----------------------------------------------------------------------

    public static void TheWorkspaceOpensRefinesAndReports(Harness h)
    {
        h.Section("open a sample, refine it, and the window's numbers are the API's");

        var w = new DesignWorkspace();
        h.Expect(!w.IsOpen, "nothing is open to begin with");
        h.Expect(w.Status.Contains("sample", StringComparison.OrdinalIgnoreCase),
                 "and the status says what to do first");

        h.Expect(w.OpenSample("AR-visible"), "a sample opens");
        h.Expect(w.IsOpen, "and the workspace knows it");
        h.Expect(w.Layers.Count > 0, "with a layer table");
        h.Expect(w.Operands.Count > 0, "and a merit function");
        h.Expect(w.Spectrum.Count > 0, "and a spectrum, drawn without being asked");

        double before = w.Merit;
        h.Expect(before > 0, "the design has something to improve");

        h.Expect(w.Optimise("refine", iterations: 120), "refine runs");
        h.Expect(w.Merit < before, $"and improves it ({before:G6} -> {w.Merit:G6})");
        h.Expect(w.Status.Contains("merit", StringComparison.OrdinalIgnoreCase),
                 $"and says so where a person can read it: {w.Status}");

        // THE WINDOW'S NUMBERS ARE THE API'S. Third front end, same check.
        var api = new ThinFilmApi();
        var id = api.Load(w.Save()!).Value!.Id;
        var direct = api.Evaluate(id).Value!;
        h.PinExact(w.Merit, direct.Merit, "the merit shown is the merit the API computes");
        h.Pin(w.Operands.Count, direct.Operands.Length, "and every operand is there");
        for (int i = 0; i < w.Operands.Count; ++i)
            h.PinExact(w.Operands[i].Value, direct.Operands[i].Value,
                       $"operand {i} matches exactly");
    }

    public static void EditingAThicknessTakesEffect(Harness h)
    {
        h.Section("editing a layer changes the design and the plot follows");

        var w = new DesignWorkspace();
        h.Expect(w.OpenSample("V-coat"), "a design opens");
        double before = w.Merit;
        double original = w.Layers[0].Thickness;

        h.Expect(w.SetThickness(0, original + 20.0), "a thickness can be set");
        h.Expect(w.Merit != before, "and the merit moves");
        h.Expect(Math.Abs(w.Layers[0].Thickness - (original + 20.0)) < 1e-9,
                 "and the layer table shows the new value");

        h.Expect(!w.SetThickness(99, 10.0), "a layer that does not exist is refused");
        h.Expect(w.Status.Length > 0, "with a message rather than an exception");
        h.Expect(!w.SetThickness(0, -1.0), "and so is a negative thickness");
    }

    public static void TheWindowSaysWhatItIsShowing(Harness h)
    {
        h.Section("the first things a new user asked: what substrate, which end, what units");

        var w = new DesignWorkspace();
        h.Expect(w.OpenSample("AR-visible"), "a design opens");

        // Names from the sample, not "material0" on "exit".
        h.Expect(w.Layers.All(l => !l.Material.StartsWith("material", StringComparison.Ordinal)),
                 $"layers carry the sample's material names ({string.Join(", ", w.Layers.Select(l => l.Material))})");
        h.Expect(w.Layers[0].Material == "SiO2" && w.Layers[1].Material == "Nb2O5",
                 "and they are the right ones, low index outermost, as an AR has it");

        h.Expect(w.IncidentText.Contains("air", StringComparison.Ordinal)
                 && w.IncidentText.Contains("layer 1", StringComparison.Ordinal),
                 $"the incident medium is named and placed: {w.IncidentText}");
        // A constant substrate is named by its value, to six places, and not repeated.
        h.Expect(w.SubstrateText.StartsWith("Substrate: n=1.520000 — ", StringComparison.Ordinal)
                 && !w.SubstrateText.Contains("n = ", StringComparison.Ordinal)
                 && w.SubstrateText.Contains($"layer {w.Layers.Count}", StringComparison.Ordinal),
                 $"and so is the substrate: {w.SubstrateText}");

        // One-based for a person, zero-based for the API, and the two never confused.
        h.Pin(w.Layers[0].Number, 1, "the first layer is layer 1");
        h.Pin(w.Layers[0].Index, 0, "and index 0 underneath");
        h.Expect(w.SetThickness(0, w.Layers[0].Thickness + 1.0), "setting index 0 works");
        h.Expect(w.Status.StartsWith("Layer 1 ", StringComparison.Ordinal),
                 $"and reports it as layer 1: {w.Status}");

        h.Expect(w.PlotNote.Contains("1 = 100%", StringComparison.Ordinal)
                 && w.PlotNote.Contains("back surface", StringComparison.Ordinal),
                 "the plot note says the scale and that the back surface is not in T");
        h.Expect(w.Spectrum.Any(s => s.Name == "R (avg s,p)"),
                 $"and the legend says what RA means ({string.Join(", ", w.Spectrum.Select(s => s.Name))})");

        // Each merit row says where it is evaluated; 27 rows of "RA LE 0.005" say nothing.
        var at = w.Operands.Select(o => o.At).ToArray();
        h.Expect(at[0] == "420 nm" && at[26] == "680 nm",
                 $"pointwise operands name their wavelength ({at[0]} … {at[26]})");
        h.Expect(at.Distinct().Count() == 28, "and every one is different, bar the two stack rows");
        h.Expect(at[27] == "stack" && at[28] == "stack", "MINT and MAXT read the stack, and say so");
        h.Expect(DesignWorkspace.Where(new OperandPoints(3, 500, 600, 45, 45)) == "500–600 nm, 45°",
                 "a band at an angle reads as one");
        h.Expect(w.Operands.All(o => o.MetMark is "✓" or "✗"), "every scored row has a mark");

        // A dispersive or absorbing medium must not be described as a constant clear one.
        h.Expect(DesignWorkspace.Describe(new MediumReport("x", 1.50, 1.53, 0.0)) == "x, n = 1.500–1.530",
                 "a varying index is shown as a range");
        h.Expect(DesignWorkspace.Describe(new MediumReport("steel", 2.75, 2.75, 3.2)).Contains("k ≤ 3.200"),
                 "and an absorbing one shows its k");
    }

    public static void FailuresBecomeStatusTextRatherThanExceptions(Harness h)
    {
        h.Section("a window that throws is a window that closes, so nothing here throws");

        var w = new DesignWorkspace();

        h.Expect(!w.Optimise("refine"), "optimising with nothing open fails");
        h.Expect(w.Status.Contains("No design", StringComparison.OrdinalIgnoreCase),
                 $"and says so: {w.Status}");

        h.Expect(!w.RefreshSpectrum(), "so does asking for a spectrum");
        h.Expect(w.Save() is null, "and saving returns nothing rather than throwing");

        h.Expect(!w.Open("{ not json"), "a malformed document is refused");
        h.Expect(w.Status.Length > 0, "with a message");

        h.Expect(w.OpenSample("AR-visible"), "and the workspace still works afterwards");
        w.SpectrumSteps = 1;
        h.Expect(!w.RefreshSpectrum(), "a one-step spectrum is refused");
        h.Expect(w.Status.Contains("two", StringComparison.OrdinalIgnoreCase),
                 $"naming the constraint: {w.Status}");
    }

    public static void TheMeritTableEditsInPlace(Harness h)
    {
        h.Section("the merit table: fixed columns, blank where they do not apply, and editable");

        var w = new DesignWorkspace();
        h.Expect(w.OpenSample("AR-visible"), "a design opens");
        h.Pin(w.MeritRows.Count, 29, "one merit row per operand");

        // WHICH CELLS APPLY. A pointwise RA row has a wavelength and an angle; a MINT row has
        // neither, and has a limit instead; only a Range row has a second target.
        var ra = w.MeritRows[0];
        h.Expect(ra.Code == "RA" && ra.Mode == "Max", $"row 1 is RA, Max ({ra.Code}, {ra.Mode})");
        h.Expect(ra.WavelengthText == "420" && ra.AngleText == "0", $"at 420 nm, 0° ({ra.WavelengthText}, {ra.AngleText})");
        h.Expect(ra.Wavelength2Text == "" && ra.PointsText == "" && !ra.CanSpan, "with no span cells");
        h.Expect(ra.LimitText == "" && !ra.CanLimit, "no limit");
        h.Expect(ra.Target2Text == "" && !ra.CanTarget2, "and no second target");
        h.Expect(ra.WeightText == "1", $"and its weight shown ({ra.WeightText})");

        var mint = w.MeritRows.First(r => r.Code == "MINT");
        h.Expect(mint.WavelengthText == "" && !mint.CanWavelength && mint.AngleText == "",
                 "MINT has no wavelength and no angle");
        h.Expect(mint.LimitText == "8" && mint.CanLimit, $"and its limit is 8 nm ({mint.LimitText})");
        h.Expect(mint.WeightText == "0.02", $"and it shows its weight ({mint.WeightText})");

        // EDITING. Each goes to the API and the table is rebuilt from the answer.
        double before = w.Merit;
        w.MeritRows[0].WeightText = "4";
        h.Expect(w.MeritRows[0].WeightText == "4", "a weight can be edited");

        string json = w.Save()!;
        h.Expect(json.Contains("\"Scale\": 0.005", StringComparison.Ordinal),
                 "and editing only the weight leaves the hidden scale where the sample put it");

        w.MeritRows[0].WavelengthText = "425";
        h.Expect(w.MeritRows[0].WavelengthText == "425", "a wavelength can be edited");
        h.Pin(w.MeritRows.Count, 29, "without changing the number of rows");
        h.Expect(w.Operands[0].At == "425 nm", $"and the engine evaluates it there ({w.Operands[0].At})");

        w.MeritRows[0].Mode = "Range";
        h.Expect(w.MeritRows[0].CanTarget2 && w.MeritRows[0].Target2Text == "0.005",
                 $"switching to Range opens the second target ({w.MeritRows[0].Target2Text})");
        h.Expect(w.MeritRows[0].Help.Contains("between 0.005 and 0.005", StringComparison.Ordinal),
                 $"and the help bar says so in words: {w.MeritRows[0].Help}");

        w.MeritRows[0].Code = "MINT";
        h.Expect(!w.MeritRows[0].CanWavelength && w.MeritRows[0].LimitText == "8",
                 "changing the code to MINT drops the wavelength and asks for a limit");
        w.MeritRows[0].Code = "RA";
        h.Expect(w.MeritRows[0].CanWavelength && w.MeritRows[0].WavelengthText.Length > 0,
                 $"and back to RA gives it a wavelength again ({w.MeritRows[0].WavelengthText})");

        // A REFUSED EDIT LEAVES THE ROW AS THE DESIGN HOLDS IT, and says why.
        string weight = w.MeritRows[1].WeightText;
        w.MeritRows[1].WeightText = "-1";
        h.Expect(w.MeritRows[1].WeightText == weight, "a negative weight is refused");
        h.Expect(w.Status.Contains("weight", StringComparison.OrdinalIgnoreCase),
                 $"and the status line says so: {w.Status}");

        // INSERT AND REMOVE.
        h.Expect(w.InsertOperand(1), "a row can be inserted");
        h.Pin(w.MeritRows.Count, 30, "below the one it copies");
        h.Expect(w.MeritRows[2].WavelengthText == w.MeritRows[1].WavelengthText, "as a copy of it");
        h.Expect(w.RemoveOperand(2), "and removed");
        h.Pin(w.MeritRows.Count, 29, "back to 29");

        // UNITS: shown in µm, held in nm.
        h.Expect(w.SetWavelengthUnit("um"), "the wavelength unit can be set to µm");
        h.Expect(w.MeritRows[1].WavelengthText == "0.43", $"and a wavelength reads in µm ({w.MeritRows[1].WavelengthText})");
        w.MeritRows[1].WavelengthText = "0.435";
        h.Expect(w.SetWavelengthUnit("nm") && w.MeritRows[1].WavelengthText == "435",
                 $"a value typed in µm is held in nm ({w.MeritRows[1].WavelengthText} nm)");
        h.Expect(w.SetWavelengthUnit("um"), "back to µm");
        var reopened = new DesignWorkspace();
        h.Expect(reopened.Open(w.Save()!) && reopened.WavelengthUnit == "um",
                 "and the unit is saved with the design");

        // THE HIDDEN SCALE TRAVELS WITH THE ROW. On AR-visible scale and target are both 0.005,
        // so a rule that recomputed scale from the target would pass there; Bandpass is where
        // they differ - T >= 0.9 is budgeted 0.1, the room to 100% - and where such a rule cut a
        // row's effective weight eighty-fold when its target was retyped.
        var b = new DesignWorkspace();
        b.OpenSample("Bandpass");
        int ta = b.MeritRows.FindIndex(r => r.Code == "TA" && r.Mode == "Min");
        h.Expect(ta >= 0, "Bandpass has a TA-at-least row");
        double ScaleOf(DesignWorkspace x, int i)
        {
            var api = new ThinFilmApi();
            var id = api.Load(x.Save()!).Value!.Id;
            return api.Operands(id).Value![i].Scale;
        }
        h.Expect(Math.Abs(ScaleOf(b, ta) - 0.1) < 1e-12, $"budgeted 0.1 by the sample ({ScaleOf(b, ta):G4})");
        b.MeritRows[ta].TargetText = "0.92";
        h.Expect(Math.Abs(ScaleOf(b, ta) - 0.1) < 1e-12,
                 $"retyping its target keeps the budget ({ScaleOf(b, ta):G4})");
        b.MeritRows[ta].WeightText = "3";
        h.Expect(Math.Abs(ScaleOf(b, ta) - 0.1) < 1e-12, "and so does retyping its weight");
        h.Expect(b.InsertOperand(ta) && Math.Abs(ScaleOf(b, ta + 1) - 0.1) < 1e-12,
                 "and an inserted copy carries it");
        b.MeritRows[ta].Mode = "Max";
        h.Expect(Math.Abs(ScaleOf(b, ta) - 0.92) < 1e-12,
                 $"a new mode is a new kind of error and gets a new budget: at most 0.92 can fall 0.92 ({ScaleOf(b, ta):G4})");

        // A SPAN ROW, where the second wavelength and the point count apply.
        var d = new DesignWorkspace();
        d.OpenSample("Dichroic");
        var span = d.MeritRows.FirstOrDefault(r => r.IsSpan);
        h.Expect(span is not null && span.CanSpan && span.PointsText.Length > 0
                 && span.Wavelength2Text.Length > 0,
                 $"a span row shows its end and its point count ({span?.WavelengthText}–{span?.Wavelength2Text}, {span?.PointsText} pts)");
    }

    public static void ANewDesignNeedsNoTemplate(Harness h)
    {
        h.Section("File > New design: a design from database materials, copied in, no template");

        var db = MaterialDatabase.Shipped();
        var form = new NewDesignForm(db);
        h.Expect(form.IncidentName == "air" && form.SubstrateIsGlass
                 && form.SubstrateCatalog == "SCHOTT" && form.SubstrateGlass == "N-BK7"
                 && form.Materials.SequenceEqual(["SiO2", "TiO2"]),
                 $"the defaults: air, on SCHOTT N-BK7, layers of SiO2 and TiO2 ({form.IncidentName}, {form.SubstrateCatalog} {form.SubstrateGlass}, {string.Join(" ", form.Materials)})");
        h.Expect(form.IncidentChoices[0] == "air" && form.IncidentChoices.Contains("MgF2"),
                 "the incident medium is air or any database material");

        var request = form.ToRequest(out var problem);
        h.Expect(request is not null, $"the defaults make a request ({problem})");

        var w = new DesignWorkspace();
        h.Expect(w.CreateNew(request!), $"and a design: {w.Status}");
        h.Pin(w.Layers.Count, 2, "two starting layers");
        h.Expect(w.Layers[0].Material == "SiO2" && w.Layers[1].Material == "TiO2",
                 "alternating through the ticked materials, layer 1 first");

        // Each a quarter-wave in ITS OWN material's index at the reference - SiO2's Sellmeier and
        // TiO2's Cauchy, evaluated at 550 nm, not a constant.
        double nSiO2 = db.Find("SiO2")!.Model.At(550, "SiO2").N;
        double nTiO2 = db.Find("TiO2")!.Model.At(550, "TiO2").N;
        h.Expect(Math.Abs(w.Layers[0].Thickness - 550.0 / (4 * nSiO2)) < 1e-9
                 && Math.Abs(w.Layers[1].Thickness - 550.0 / (4 * nTiO2)) < 1e-9,
                 $"each a quarter-wave at 550 nm in its own index ({w.Layers[0].Thickness:F3} nm at n {nSiO2:F4}, {w.Layers[1].Thickness:F3} nm at n {nTiO2:F4})");

        // THE COPIES ARE THE DESIGN'S. The definitions came from the database and are now held in
        // the design file itself.
        var set = w.Materials!;
        h.Expect(set.Materials[0].Model.Kind == "SELLMEIER" && set.Materials[1].Model.Kind == "CAUCHY",
                 "the design holds the materials' definitions, Sellmeier and Cauchy");
        // THE SUBSTRATE IS A CATALOGUE GLASS, NAMED: the design records SCHOTT N-BK7, not its coefficients.
        h.Expect(set.Substrate.Name == "SCHOTT N-BK7" && set.Substrate.Model is { Kind: "GLASS", Catalog: "SCHOTT", Glass: "N-BK7" }
                 && set.Substrate.Model.Coefficients.Length == 0,
                 $"the substrate is the catalogue glass SCHOTT N-BK7, named and not copied ({set.Substrate.Name})");
        h.Expect(w.SubstrateText.Contains("n = 1.519"), $"at its catalogue index at 550 nm, 1.5185 ({w.SubstrateText})");
        h.Pin(w.MeritRows.Count, 1, "with one placeholder merit row");
        h.Expect(w.MeritRows[0].Code == "RA" && w.MeritRows[0].Mode == "Target"
                 && w.MeritRows[0].WavelengthText == "550", "R = 0 at the reference wavelength");

        // A QUARTER-WAVE START IS A LOCAL MINIMUM AT ITS OWN REFERENCE: probed, the merit rises
        // by the same amount for a layer moved 0.5 nm either way. So refine cannot move it, and
        // must say why rather than appear to do nothing; global,refine escapes it.
        double before = w.Merit;
        h.Expect(w.Optimise("refine") && w.Merit == before, "refine finds no downhill step from the quarter-wave start");
        h.Expect(w.Status.Contains("local minimum", StringComparison.Ordinal) && w.Status.Contains("global,refine"),
                 $"and says why, and what to try: {w.Status}");
        h.Expect(w.Optimise("global,refine") && w.Merit < before,
                 $"global,refine escapes it ({before:G4} -> {w.Merit:G4})");

        var other = new NewDesignForm(db);
        other.Materials.Clear();
        other.Materials.AddRange(["MgF2", "Ta2O5", "Al2O3"]);
        other.Layers = "5";
        var w2 = new DesignWorkspace();
        h.Expect(w2.CreateNew(other.ToRequest(out _)!), "three materials");
        h.Expect(w2.Layers.Select(l => l.Material).SequenceEqual(["MgF2", "Ta2O5", "Al2O3", "MgF2", "Ta2O5"]),
                 $"and five layers cycle through them ({string.Join(" ", w2.Layers.Select(l => l.Material))})");

        // A CATALOGUE GLASS ON BOTH SIDES: the user guide's beam splitter, a plate immersed in oil
        // index-matched to BK7 (2026-10-23); `thinfilm new --incident N-BK7` already allowed it.
        var immersed = new NewDesignForm(db) { IncidentIsGlass = true, SubstrateIsGlass = true };
        var w3 = new DesignWorkspace();
        h.Expect(w3.CreateNew(immersed.ToRequest(out var whyNot)!), $"glass on both sides ({whyNot})");
        h.Expect(w3.Materials!.Incident.Name == "SCHOTT N-BK7" && w3.Materials.Incident.Model is { Kind: "GLASS", Glass: "N-BK7" },
                 $"the incident medium is the catalogue glass, named ({w3.Materials.Incident.Name})");

        // REFUSALS, each with its reason.
        (string What, Action<NewDesignForm> Break, string Says)[] bad =
        [
            ("no materials", f => f.Materials.Clear(), "coating material"),
            ("no layers", f => f.Layers = "0", "at least one layer"),
            ("a non-number", f => f.ReferenceNm = "green", "not a number"),
            ("no name", f => f.Name = " ", "name"),
            ("a substrate not in the database", f => { f.SubstrateIsGlass = false; f.SubstrateName = "unobtainium"; }, "not in the material database"),
            ("a glass not in the catalogues", f => { f.SubstrateIsGlass = true; f.SubstrateGlass = "N-UNOBTAINIUM"; }, "not in the glass catalogues"),
            ("an incident glass not in the catalogues", f => { f.IncidentIsGlass = true; f.IncidentGlass = "N-UNOBTAINIUM"; }, "not in the glass catalogues"),
            ("a material chosen twice", f => f.Materials.Add("SiO2"), "twice"),
        ];
        foreach (var (what, breakIt, says) in bad)
        {
            var f = new NewDesignForm(db);
            breakIt(f);
            h.Expect(f.ToRequest(out var why) is null && why is not null && why.Contains(says),
                     $"{what} is refused: {why}");
        }
    }

    public static void TheLayerStackIsEdited(Harness h)
    {
        h.Section("layers: insert, delete, move, change material, fix a thickness");

        var w = new DesignWorkspace();
        h.Expect(w.OpenSample("AR-visible"), "a design opens");
        string[] Stack() => w.Layers.Select(l => $"{l.Material}:{l.Thickness:F1}").ToArray();
        var start = Stack();
        h.Pin(start.Length, 4, "four layers");

        h.Expect(w.InsertLayer(1), "a layer can be inserted below layer 2");
        h.Pin(w.Layers.Count, 5, "five layers");
        h.Expect(Stack()[2] == start[1], $"as a copy of layer 2 ({Stack()[2]})");
        h.Expect(w.RemoveLayer(2), "and deleted again");
        h.Expect(Stack().SequenceEqual(start), "leaving the stack as it was");

        h.Expect(w.MoveLayer(3, -1), "layer 4 moves up");
        h.Expect(Stack()[2] == start[3] && Stack()[3] == start[2], "and swaps with layer 3");
        h.Expect(!w.MoveLayer(0, -1) && w.Status.Contains("incident", StringComparison.Ordinal),
                 $"layer 1 cannot move further up, and says why: {w.Status}");
        h.Expect(w.MoveLayer(2, +1), "and back down");
        h.Expect(Stack().SequenceEqual(start), "restoring the stack");

        w.Layers[0].MaterialChoice = "Nb2O5";
        h.Expect(w.Layers[0].Material == "Nb2O5", $"a layer's material is chosen from the drop-down ({w.Layers[0].Material})");
        w.Layers[0].MaterialChoice = "SiO2";

        // A FIXED LAYER IS NOT A VARIABLE: the optimiser leaves it exactly where it is.
        double fixedAt = w.Layers[1].Thickness;
        w.Layers[1].Vary = false;
        h.Expect(!w.Layers[1].Variable, "layer 2 can be fixed");
        h.Expect(w.Summary.Contains("3 variables", StringComparison.Ordinal),
                 $"and the design has one variable fewer: {w.Summary}");
        h.Expect(w.Optimise("refine"), "refine runs");
        h.PinExact(w.Layers[1].Thickness, fixedAt, "and the fixed layer has not moved");

        // AND IT STAYS FIXED THROUGH A NEEDLE RUN, which rebuilds the stack. The rebuild once
        // defaulted every layer back to variable.
        h.Expect(w.Optimise("refine,needle"), "a needle run completes");
        h.Expect(w.Layers.Any(l => !l.Variable && l.Thickness == fixedAt),
                 $"and the fixed layer is still fixed at {fixedAt:F3} nm ({w.Layers.Count} layers now)");

        // THE FILE, NOT THE TEMPLATE, NAMES A SAVED DESIGN.
        var reopened = new DesignWorkspace();
        h.Expect(reopened.Open(w.Save()!, "coat-7.json"), "a saved design reopens");
        h.Expect(reopened.Title.Contains("coat-7.json", StringComparison.Ordinal),
                 $"with the file in the title ({reopened.Title})");
        h.Expect(reopened.Status.Contains("coat-7.json", StringComparison.Ordinal)
                 && reopened.Status.Contains("AR-visible", StringComparison.Ordinal),
                 $"and the status naming the file and the design inside it: {reopened.Status}");
    }

    public static void TheSpectrumCanBeTakenAtAnAngle(Harness h)
    {
        h.Section("the displayed spectrum is computed at the angle of incidence asked for");

        var w = new DesignWorkspace();
        h.Expect(w.OpenSample("AR-visible"), "a design opens");
        var normal = w.Spectrum.First(s => s.Name.StartsWith("R", StringComparison.Ordinal)).Y.ToArray();
        h.Expect(w.PlotNote.Contains("normal incidence", StringComparison.Ordinal), "at 0° the note says normal incidence");

        w.SpectrumAngle = 45.0;
        h.Expect(w.RefreshSpectrum(), "a 45° spectrum is computed");
        var tilted = w.Spectrum.First(s => s.Name.StartsWith("R", StringComparison.Ordinal)).Y;
        double most = normal.Zip(tilted, (a, b) => Math.Abs(a - b)).Max();
        h.Expect(most > 1e-3, $"and it differs from normal incidence (by up to {most:G3})");
        h.Expect(w.PlotNote.Contains("45° incidence", StringComparison.Ordinal),
                 $"and the note says the angle: {w.PlotNote}");

        // R at 45° from the engine directly, the same number by another route.
        var api = new ThinFilmApi();
        var id = api.Load(w.Save()!).Value!.Id;
        var direct = api.Spectrum(id, new SpectrumRequest(400, 800, 241, 45.0, ["RA"])).Value!;
        h.PinExact(tilted[100], direct.Quantity["RA"][100], "the plotted value is the API's at 45°");

        w.SpectrumAngle = 90.0;
        h.Expect(!w.RefreshSpectrum() && w.Status.Contains("angle", StringComparison.OrdinalIgnoreCase),
                 $"90° is refused with a reason: {w.Status}");
    }

    public static void EveryProjectIsInTheSolution(Harness h)
    {
        h.Section("every managed project is in the solution, so building it builds what a person runs");

        // ThinFilm.Gui WAS NOT IN ThinFilm.sln. The GUI suite still built it - as a reference, into
        // the suite's own folder - so 116 checks passed on the new code while the thinfilm-gui.exe
        // a person launches, in ThinFilm.Gui/bin, stayed two changes old. The first person to test
        // X1.14 was shown X1.13 and reported the fix as not working, correctly.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ThinFilm.sln")))
            dir = dir.Parent;
        if (dir is null) { h.Expect(false, "ThinFilm.sln is found above the test binaries"); return; }

        var sln = File.ReadAllText(Path.Combine(dir.FullName, "ThinFilm.sln")).Replace('\\', '/');
        var projects = Directory.GetFiles(dir.FullName, "*.csproj", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(dir.FullName, p).Replace('\\', '/'))
            .Where(p => !p.Contains("/obj/") && !p.Contains("/bin/"))
            .ToArray();
        h.Expect(projects.Length >= 10, $"the projects are found ({projects.Length})");
        foreach (var p in projects)
            h.Expect(sln.Contains($"\"{p}\"", StringComparison.OrdinalIgnoreCase),
                     $"{p} is in ThinFilm.sln");
    }

    public static void TheArSampleMeetsItsSpecWithTheDefaultStage(Harness h)
    {
        h.Section("open AR-visible, press Optimise with the default stage, and it meets its spec");

        // THE PATH A PERSON TAKES, found by one taking it. The sample used to start from HLHL
        // quarter-waves - a reflector - and "refine", the stage the window selects by default,
        // stalled at 15.7% peak R against a 0.5% target. A sample that fails on the first
        // button a user presses is a sample that teaches them the program does not work.
        var w = new DesignWorkspace();
        h.Expect(w.OpenSample("AR-visible"), "the sample opens");

        var start = w.Operands.Where(o => o.Code == "RA").Max(o => o.Value);
        h.Expect(start < 0.05, $"it starts as an antireflection coating, not a mirror (peak R {start:P2})");

        h.Expect(w.Optimise("refine"), "refine runs");
        var ra = w.Operands.Where(o => o.Code == "RA").ToArray();
        h.Pin(ra.Length, 27, "all 27 reflectance targets are there");
        h.Expect(ra.All(o => o.Met),
                 $"and every one is met (peak R {ra.Max(o => o.Value):P3} against 0.500%)");
    }
}
