// MaterialTests.cs — real-materials step 1: a design carries its materials' definitions.
//
// The formulas are pinned to numbers worked independently of the code: SiO2 by the Sellmeier
// coefficients ARCoatingDesigner ships (Malitson's fused silica), whose n at the helium d line is
// the textbook 1.4585; TiO2 by its Cauchy coefficients, done by hand. Then the property the whole
// step exists for: a merit row moved to a new wavelength gets the material's index computed THERE,
// not interpolated between the old grid's points.

using ThinFilm.Api;
using ThinFilm.Gui.Core;

namespace ThinFilm.Native.Tests;

public static class MaterialTests
{
    // The ARCoatingDesigner coefficients, as the database will ship them.
    private static readonly MaterialModel SiO2 = new()
    {
        Kind = "SELLMEIER",
        Coefficients = [0.6961663, 0.0046791, 0.4079426, 0.0135121, 0.8974794, 97.934],
        MinMicrons = 0.21, MaxMicrons = 3.71,
    };

    private static readonly MaterialModel TiO2 = new() { Kind = "CAUCHY", Coefficients = [2.2, 0.03, 0.003] };

    public static void TheModelsGiveTheRightIndex(Harness h)
    {
        h.Section("Sellmeier, Cauchy, table and constant give the index they should");

        var (n, k, x) = SiO2.At(587.56, "SiO2");
        h.Expect(Math.Abs(n - 1.4585) < 1e-4 && k == 0 && !x,
                 $"SiO2 (Sellmeier) at the d line is 1.4585 ({n:F6}), clear, in range");

        // TiO2 at 550 nm by hand: 2.2 + 0.03/0.3025 + 0.003/0.09150625 = 2.33197...
        double byHand = 2.2 + 0.03 / 0.3025 + 0.003 / 0.09150625;
        h.Expect(Math.Abs(TiO2.At(550, "TiO2").N - byHand) < 1e-12,
                 $"TiO2 (Cauchy) at 550 nm is {byHand:F6} ({TiO2.At(550, "TiO2").N:F6})");

        var table = new MaterialModel { Kind = "TABLE", Table = [[0.4, 0.05, 2.0], [0.6, 0.07, 4.0]] };
        var mid = table.At(500, "Ag");
        h.Expect(Math.Abs(mid.N - 0.06) < 1e-12 && Math.Abs(mid.K - 3.0) < 1e-12 && !mid.Extrapolated,
                 $"a table interpolates between its rows ({mid.N:G4}, {mid.K:G4})");
        var past = table.At(700, "Ag");
        h.Expect(past.N == 0.07 && past.K == 4.0 && past.Extrapolated,
                 "and past its last row holds the end value AND says it is extrapolating");

        h.Expect(MaterialModel.Air.At(1234, "air") == (1.0, 0.0, false), "air is n = 1 everywhere, never extrapolated");

        var outside = SiO2.At(5000, "SiO2");
        h.Expect(outside.Extrapolated, "SiO2 at 5 µm is past its 3.71 µm range, and says so");
    }

    public static void ABadModelIsRefusedWithAReason(Harness h)
    {
        h.Section("a model that cannot give a physical index is refused, never patched");

        (string What, MaterialModel Model, double At, string Says)[] bad =
        [
            ("an odd Sellmeier list", new() { Kind = "SELLMEIER", Coefficients = [1.0, 0.01, 0.5] }, 550, "pairs"),
            ("a Sellmeier n² below zero", new() { Kind = "SELLMEIER", Coefficients = [-3.0, 0.0] }, 550, "no physical index"),
            ("a Sellmeier pole", new() { Kind = "SELLMEIER", Coefficients = [1.0, 0.3025] }, 550, "pole"),
            ("a short Cauchy", new() { Kind = "CAUCHY", Coefficients = [1.5] }, 550, "needs 3"),
            ("an empty table", new() { Kind = "TABLE", Table = [] }, 550, "no rows"),
            ("a negative k", MaterialModel.Constant(1.5, -0.1), 550, "cannot be negative"),
            ("an unknown kind", new() { Kind = "GUESS" }, 550, "not a material model"),
        ];
        foreach (var (what, model, at, says) in bad)
        {
            string? why = null;
            try { model.At(at, "M"); }
            catch (FormatException e) { why = e.Message; }
            h.Expect(why is not null && why.Contains(says, StringComparison.Ordinal) && why.Contains("'M'"),
                     $"{what} is refused, naming the material: {why}");
        }
    }

    public static void ADesignEvaluatesItsMaterialsWhereItNeedsThem(Harness h)
    {
        h.Section("a design computes a dispersive material at each wavelength it uses");

        // A one-layer SiO2 design on a Sellmeier SiO2 "substrate", from New design's document.
        var form = new NewDesignForm(MaterialDatabase.Shipped());
        var doc = NewDesigns.Document(form.ToRequest(out _)!) with { };
        doc = doc with
        {
            Materials = [MaterialDocument.FromModel("SiO2", SiO2, doc.Wavelength)],
            Exit = MaterialDocument.FromModel("TiO2", TiO2, doc.Wavelength),
            Layers = [new LayerDocument(0, 100.0)],
        };

        var api = new ThinFilmApi();
        var id = api.CreateFromDocument(doc).Value!.Id;
        var row = api.Operands(id).Value![0];

        // THE POINT OF THE STEP: move the row to 700 nm, and the index there is the formula's.
        h.Expect(api.ReplaceOperand(id, 0, row with { Wavelengths = [700.0] }).Ok, "the row moves to 700 nm");
        var held = api.Document(id).Value!;
        int at = Array.IndexOf(held.Wavelength, 700.0);
        h.Expect(at >= 0, $"the grid now has 700 nm ({string.Join(", ", held.Wavelength)})");
        h.PinExact(held.Materials[0].N[at], SiO2.At(700, "SiO2").N,
                   "and SiO2 there is the Sellmeier value, not one interpolated from 550");
        h.PinExact(held.Exit.N[at], TiO2.At(700, "TiO2").N, "and so is the substrate's Cauchy value");

        // The spectrum, too, is computed at every plotted wavelength.
        var rep = Documents.ReportingDocument(held, new SpectrumRequest(400, 800, 5, 0.0, ["RA"]), ["RA"]);
        h.PinExact(rep.Materials[0].N[0], SiO2.At(400, "SiO2").N, "a spectrum's 400 nm uses SiO2 at 400 nm");

        // Saved and reopened, the model is still there.
        var again = new ThinFilmApi();
        var id2 = again.Load(api.Save(id).Value!).Value!.Id;
        var model = again.Document(id2).Value!.Materials[0].Model;
        h.Expect(model is { Kind: "SELLMEIER" } && model.Coefficients.SequenceEqual(SiO2.Coefficients),
                 "and saving keeps the model, coefficient for coefficient");
        h.Pin(again.Document(id2).Value!.Version, DesignDocument.CurrentVersion, "in a version-2 file");
    }

    public static void OutsideItsRangeAMaterialIsFlagged(Harness h)
    {
        h.Section("a row that uses a material outside its valid range is flagged, and the window says so");

        var w = new DesignWorkspace();
        var form = new NewDesignForm(MaterialDatabase.Shipped());
        var doc = NewDesigns.Document(form.ToRequest(out _)!);
        var narrow = SiO2 with { MaxMicrons = 0.6 };
        doc = doc with { Materials = [MaterialDocument.FromModel("SiO2", narrow, doc.Wavelength)],
                         Layers = [new LayerDocument(0, 100.0)] };
        h.Expect(w.Open(doc.ToJson()), "a design whose SiO2 is trusted only to 600 nm");

        h.Expect(!w.Operands[0].Flags.Contains("SYNTHETIC_INDEX"), "at 550 nm the row is not flagged");
        h.Expect(!w.MeritRows[0].Help.Contains("outside its valid range"), "and the help bar says nothing");

        w.MeritRows[0].WavelengthText = "650";
        h.Expect(w.Operands[0].Flags.Contains("SYNTHETIC_INDEX"),
                 $"at 650 nm the engine flags it ({w.Operands[0].Flags})");
        h.Expect(w.MeritRows[0].Help.Contains("outside its valid range"),
                 $"and the help bar says so in words: {w.MeritRows[0].Help}");

        // THE GUIDE'S Si3N4, plotted past the end of its table: the Spectrum tab's note says so,
        // not only a merit row once clicked.
        w.SpectrumFrom = 400; w.SpectrumTo = 550;
        h.Expect(w.RefreshSpectrum() && !w.PlotNote.Contains("Caution"), "plotted inside its range, the note has no caution");
        w.SpectrumTo = 800;
        h.Expect(w.RefreshSpectrum() && w.PlotNote.Contains("SiO2 is outside its valid range (") && w.PlotNote.Contains("–800 nm"),
                 $"plotted to 800 nm, the note says where: {w.PlotNote}");
        var api = new ThinFilmApi();
        var id = api.Load(doc.ToJson()).Value!.Id;
        var r = api.Spectrum(id, new SpectrumRequest(400, 800, 41, 0.0, ["RA"]));
        h.Expect(r.Ok && r.Value!.Outside.Length == 1 && r.Value.Outside[0].StartsWith("SiO2"),
                 "and the API's spectrum report, which the CLI and MCP return, says the same");
    }

    public static void TheShippedDatabaseReadsAndWritesBack(Harness h)
    {
        h.Section("the shipped database: dielectrics and Palik's tables, none constant, and it writes back as it reads");

        var db = MaterialDatabase.Shipped();
        h.Expect(db.Materials.Select(m => m.Name).SequenceEqual(["MgF2", "SiO2", "Al2O3", "TiO2", "Ta2O5", "ZrO2", "HfO2", "Nb2O5",
                     "Ag", "Al", "Au", "Cr", "Cu", "CuO", "CdTe", "CdSe (o)", "CdSe (e)", "GaAs", "Ge", "InAs", "InP", "MgO", "Mo", "Si3N4", "SiO", "Y2O3", "Pt", "Pd", "TiN", "LiNbO3 (o)", "LiNbO3 (e)", "MgF2 (o)", "MgF2 (e)"]),
                 $"the seven ARCoatingDesigner materials, the measured Nb2O5 and Palik's ({string.Join(" ", db.Materials.Select(m => m.Name))})");
        var ag = db.Find("Ag")!.Model.At(563.6, "Ag");
        h.Expect(Math.Abs(ag.N - 0.120) < 1e-9 && Math.Abs(ag.K - 3.45) < 1e-9 && !ag.Extrapolated,
                 $"Ag at 563.6 nm is Palik's row, 0.120 + 3.45i ({ag.N} + {ag.K}i)");
        var ge = db.Find("Ge")!.Model.At(5000, "Ge");
        h.Expect(Math.Abs(ge.N - 4.01619) < 1e-9 && ge.K == 0,
                 $"Ge at 5 um: n 4.01619, and k 0 where Palik gives none ({ge.N}, {ge.K})");
        h.Expect(db.Materials.All(m => m.Model.Kind is "SELLMEIER" or "CAUCHY" or "TABLE"),
                 "every one dispersive: no constant-index material ships");
        h.Expect(db.Find("air") is null, "and air is not in the file; it is built in");
        h.Expect(db.Find("SiO2")!.Model.Coefficients.SequenceEqual(SiO2.Coefficients),
                 "SiO2 carries ARCoatingDesigner's coefficients exactly");
        h.Expect(db.Find("TiO2")!.Model.Range() == (null, null),
                 "a Cauchy material with no recorded range is given none, not a guess");

        var again = MaterialDatabase.Parse(db.Write());
        h.Expect(again.Materials.Count == db.Materials.Count
                 && again.Materials.Zip(db.Materials).All(p => p.First.Name == p.Second.Name
                     && p.First.Model.Kind == p.Second.Model.Kind
                     && p.First.Model.Coefficients.SequenceEqual(p.Second.Model.Coefficients)
                     && p.First.Model.Range() == p.Second.Model.Range()
                     && p.First.Model.Source == p.Second.Model.Source),
                 "and Write then Parse gives back every material, coefficient for coefficient");

        var table = MaterialDatabase.Parse("MATERIAL Ag\n  TABLE\n    0.40 0.05 2.1\n    0.50 0.05 3.1\nEND\n");
        h.Expect(table.Find("Ag")!.Model is { Kind: "TABLE", Table.Length: 2 }, "a TABLE material reads, row by row");
    }

    public static void ABadDatabaseFileIsRefusedWithItsLine(Harness h)
    {
        h.Section("a database file that does not parse is refused with the line and the reason");

        (string What, string Text, string Says)[] bad =
        [
            ("a missing END", "MATERIAL SiO2\n  CAUCHY 1.45 0.0 0.0\n", "line 1: 'SiO2' has no END"),
            ("an unknown keyword", "MATERIAL X\n  CAUCHI 1.5 0 0\nEND\n", "line 2: 'CAUCHI' is not a database keyword"),
            ("a name used twice", "MATERIAL X\n CAUCHY 1.5 0 0\nEND\nMATERIAL x\n CAUCHY 1.6 0 0\nEND\n", "line 4: 'x' is defined twice"),
            ("a backwards range", "MATERIAL X\n CAUCHY 1.5 0 0\n RANGE 2 1\nEND\n", "line 3: RANGE needs two numbers"),
            ("a table going backwards", "MATERIAL Ag\n TABLE\n 0.5 0.05 3\n 0.4 0.05 2\nEND\n", "line 5: 'Ag': TABLE wavelengths must increase"),
            ("a model that does not evaluate", "MATERIAL X\n SELLMEIER 1.0 0.01 0.5\nEND\n", "line 3: material 'X'"),
            ("a word where a number goes", "MATERIAL X\n CAUCHY 1.5 zero 0\nEND\n", "line 2: 'zero' is not a number"),
        ];
        foreach (var (what, text, says) in bad)
        {
            string? why = null;
            try { MaterialDatabase.Parse(text, "mine.txt"); }
            catch (FormatException e) { why = e.Message; }
            h.Expect(why is not null && why.Contains("mine.txt, " + says, StringComparison.Ordinal),
                     $"{what}: {why}");
        }
    }

    public static void APersonsDatabaseStartsAsACopy(Harness h)
    {
        h.Section("a person's working database is made from the shipped one, and then is their own");

        var dir = Path.Combine(Path.GetTempPath(), "thinfilm-db-test-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "materials.txt");
        try
        {
            var db = MaterialDatabase.OpenOrCreate(path);
            int shipped = MaterialDatabase.Shipped().Materials.Count;
            h.Expect(File.Exists(path) && db.Materials.Count == shipped && shipped == 33,
                     $"the first open makes the file, a copy of the shipped one ({db.Materials.Count} of {shipped})");

            // A copy an older release made, before the metals shipped, and nobody has changed.
            string older = new MaterialDatabase(db.Materials.Where(m => m.Model.Source?.StartsWith("Palik") != true).ToList()).Write();
            File.WriteAllText(path, older);
            File.Delete(path + ".shipped");
            h.Expect(MaterialDatabase.OpenOrCreate(path).Find("Ag") is not null,
                     "an unchanged copy from an older release gains the materials a new release ships");
            File.WriteAllText(path, older);
            File.WriteAllText(path + ".shipped", older);
            h.Expect(MaterialDatabase.OpenOrCreate(path).Materials.Count == shipped, "and so does one with its marker");

            File.WriteAllText(path, File.ReadAllText(path).Replace("CAUCHY 2.2 0.03 0.003", "CAUCHY 2.3 0.03 0.003"));
            h.Expect(MaterialDatabase.OpenOrCreate(path).Find("TiO2")!.Model.Coefficients[0] == 2.3,
                     "an edit to it is kept - the next open does not overwrite it");
            File.WriteAllText(path, older.Replace("CAUCHY 2.2 0.03 0.003", "CAUCHY 2.3 0.03 0.003"));
            File.Delete(path + ".shipped");
            h.Expect(MaterialDatabase.OpenOrCreate(path).Find("Ag") is null,
                     "an edited copy from an older release is kept as it is too");
            h.Expect(MaterialDatabase.Shipped().Find("TiO2")!.Model.Coefficients[0] == 2.2,
                     "and the shipped database is untouched");
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    public static void ChangingTheDatabaseCannotChangeADesign(Harness h)
    {
        h.Section("the user's rule: a material is copied into the design, so the database can change freely");

        var db = MaterialDatabase.Shipped();
        var w = new DesignWorkspace();
        w.CreateNew(new NewDesignForm(db).ToRequest(out _)!);
        string saved = w.Save()!;
        double before = w.Merit;

        // The database is edited afterwards: TiO2 changed, SiO2 deleted altogether.
        var edited = new MaterialDatabase(db.Materials
            .Where(m => m.Name != "SiO2")
            .Select(m => m.Name == "TiO2" ? m with { Model = m.Model with { Coefficients = [2.6, 0.05, 0.004] } } : m)
            .ToArray());

        var reopened = new DesignWorkspace { Database = edited };
        h.Expect(reopened.Open(saved), "the design still opens with SiO2 gone from the database");
        h.PinExact(reopened.Merit, before, "and evaluates exactly as before");
        h.Expect(reopened.Materials!.Materials[1].Model.Coefficients.SequenceEqual([2.2, 0.03, 0.003]),
                 "its TiO2 is still the copy it took, not the edited database's");
    }

    public static void TheMaterialsEditorWorksOnTheDesignsCopies(Harness h)
    {
        h.Section("Edit > Materials: copy in, edit the copy, substitute the media, remove - then OK");

        var db = MaterialDatabase.Shipped();
        var w = new DesignWorkspace();
        w.CreateNew(new NewDesignForm(db).ToRequest(out _)!);
        var form = new MaterialsForm(db, w.Materials!);
        h.Pin(form.Entries.Count, 4, "incident, substrate and two coating materials");
        h.Expect(form.Entries[2].Label.Contains("1 layer"), $"each saying how many layers use it ({form.Entries[2].Label})");

        h.Expect(form.CopyIntoDesign("SiO2") is null && form.Entries[^1].Material.Name == "SiO2 (2)",
                 $"a second copy of SiO2 comes in under its own name ({form.Entries[^1].Material.Name})");
        int copy = form.Entries.Count - 1;
        h.Expect(form.SetDefinition(copy, "SiO2 dense", new MaterialModel { Kind = "CAUCHY", Coefficients = [1.47, 0.004, 0], MinMicrons = 0.4, MaxMicrons = 0.9, Source = "measured, chamber 2" }) is null,
                 "and is edited: renamed, a Cauchy, a range, a source");
        h.Expect(form.SetDefinition(copy, "X", new MaterialModel { Kind = "SELLMEIER", Coefficients = [1.0, 0.3025] }) is not null
                 && form.Entries[copy].Material.Name == "SiO2 dense",
                 "an edit that cannot evaluate is refused, and the copy is left as it was");
        h.Expect(form.SetDefinition(copy, "TiO2", new MaterialModel { Kind = "CAUCHY", Coefficients = [1.5, 0, 0] })?.Contains("already has") == true,
                 "a name already in the design is refused");

        h.Expect(form.UseAsSubstrate("MgF2") is null && form.Substrate.Name == "MgF2", "the substrate becomes a copy of MgF2");
        h.Expect(form.UseAsIncident("TiO2") is null, "the incident medium can be a dielectric");
        h.Expect(form.UseAsIncident("air") is null && form.Incident.Name == "air", "and air again");

        h.Expect(form.Remove(2)?.Contains("used by 1 layer") == true, "a material a layer uses cannot be removed");
        h.Expect(form.Remove(0)?.Contains("replaced, not removed") == true, "nor can the incident medium");

        var preview = MaterialsForm.Preview(db.Find("TiO2")!, out _);
        h.Expect(preview is { } p && p.Span.Contains("states no valid range"),
                 $"the preview of a material with no range says so ({preview?.Span})");

        // OK.
        h.Expect(w.ApplyMaterials(form.CoatingEdits(), form.Incident, form.Substrate), $"OK applies it: {w.Status}");
        var set = w.Materials!;
        h.Expect(set.Materials.Select(m => m.Name).SequenceEqual(["SiO2", "TiO2", "SiO2 dense"]),
                 $"the design has its three materials ({string.Join(", ", set.Materials.Select(m => m.Name))})");
        h.Expect(set.Substrate.Name == "MgF2" && w.SubstrateText.Contains("MgF2"), "on MgF2");
        h.Expect(w.Layers[0].MaterialOptions.Contains("SiO2 dense"), "and every layer's drop-down offers the new copy");
        w.Layers[0].MaterialChoice = "SiO2 dense";
        h.Expect(w.Layers[0].Material == "SiO2 dense", "which a layer can then be made of");
        h.Expect(db.Find("SiO2")!.Model.Kind == "SELLMEIER" && db.Find("SiO2 dense") is null,
                 "and the database has not been touched");

        // Cancel is simply not applying the form.
        var cancelled = new MaterialsForm(db, w.Materials!);
        cancelled.CopyIntoDesign("HfO2");
        h.Pin(w.Materials!.Materials.Length, 3, "a form dropped without OK changes nothing");
    }

    /// <summary>
    /// EVERY GLASS AGAINST ITS VENDOR'S OWN nd. Each NM line carries the catalogue's index at the
    /// helium d line, so all 1799 glasses are evaluated there and compared - which is what catches
    /// a transposed coefficient or a misremembered formula, because it fails hundreds at once.
    /// </summary>
    public static void EveryGlassMatchesItsCatalogueNd(Harness h)
    {
        h.Section("all 1799 catalogue glasses, evaluated at the d line, against the vendor's nd");

        var catalogs = GlassCatalogs.All;
        h.Expect(GlassCatalogs.Problems.Count == 0, $"every shipped catalogue parses ({string.Join("; ", GlassCatalogs.Problems)})");
        h.Expect(catalogs.Select(c => c.Name).SequenceEqual(["CDGM", "HOYA", "OHARA", "SCHOTT", "SUMITA"]),
                 $"the five vendors' catalogues ({string.Join(" ", catalogs.Select(c => c.Name))})");
        int total = catalogs.Sum(c => c.Glasses.Count);
        h.Pin(total, 1799, "1799 glasses between them");

        // THE VENDORS' OWN INCONSISTENCIES, found by this check and named so a fix by a vendor shows
        // up here: each glass's coefficients disagree with its own nd. 281 other CDGM Sellmeier
        // glasses and every SCHOTT, OHARA and SUMITA glass agree, so the formulas are not the cause.
        string[] inconsistent = ["CDGM H-TK9", "HOYA MC-FAD61", "HOYA MC-TAF115"];

        int compared = 0;
        foreach (var c in catalogs)
        {
            double worst = 0; string? worstName = null; int refused = 0;
            foreach (var g in c.Glasses)
            {
                try { g.At(1000 * (g.MinMicrons + g.MaxMicrons) / 2); }
                catch (FormatException) { ++refused; continue; }
                if (g.NdDisagreement is not { } d || inconsistent.Contains(g.FullName)) continue;
                ++compared;
                if (d > worst) (worst, worstName) = (d, g.Name);
            }
            h.Expect(refused == 0, $"{c.Name}: every glass evaluates ({refused} refused)");
            h.Expect(worst < 2e-4,
                     $"{c.Name}: {c.Glasses.Count} glasses, worst |n(d) − nd| = {worst:E2} ({worstName})");
        }
        h.Pin(compared, 1787, "1787 glasses compared: all bar the 9 with no d-line nd and the 3 inconsistent");

        var flagged = catalogs.SelectMany(c => c.Glasses).Where(g => g.Caution is not null)
                              .Select(g => g.FullName).Order().ToArray();
        h.Expect(flagged.SequenceEqual(inconsistent),
                 $"exactly those three carry a caution ({string.Join(", ", flagged)})");
        h.Expect(GlassCatalogs.Find("CDGM", "H-TK9")!.Caution!.Contains("its own nd is 1.58717"),
                 "and the caution gives both numbers");

        // The infrared glasses with nd = 1 (a placeholder) are checked where they are meant to be used.
        var hws = catalogs.SelectMany(c => c.Glasses).Where(g => g.Nd <= 1.0).ToArray();
        h.Pin(hws.Length, 9, "nine glasses give nd as a placeholder 1");
        h.Expect(hws.All(g => g.At(1000 * (g.MinMicrons + g.MaxMicrons) / 2).N is > 2.3 and < 3.5),
                 "and each has a chalcogenide's index, 2.4 to 3.3, in the middle of its own range");

        var bk7 = GlassCatalogs.Find("SCHOTT", "N-BK7")!;
        h.Expect(Math.Abs(bk7.At(587.5618).N - 1.5168) < 5e-5, $"SCHOTT N-BK7 is 1.5168 at the d line ({bk7.At(587.5618).N:F5})");
        h.Expect(!bk7.At(550).Extrapolated && bk7.At(2600).Extrapolated,
                 "and inside its 0.3–2.5 µm range at 550 nm, outside it at 2.6 µm");

        string? why = null;
        try { (bk7 with { Formula = 7 }).At(550); }
        catch (FormatException e) { why = e.Message; }
        h.Expect(why?.Contains("formula 7") == true && why.Contains("N-BK7"),
                 $"a formula ThinFilm does not evaluate is refused, naming the glass: {why}");
    }

    public static void ADesignNamesItsGlassAndNeverCopiesIt(Harness h)
    {
        h.Section("a catalogue glass substrate is named by the design, evaluated from the catalogue, not editable");

        var w = new DesignWorkspace();
        h.Expect(w.CreateNew(new NewDesignForm(MaterialDatabase.Shipped()).ToRequest(out _)!), "a design on SCHOTT N-BK7");

        var json = w.Save()!;
        var exit = DesignDocument.FromJson(json).Exit;
        h.Expect(exit.Model is { Kind: "GLASS", Catalog: "SCHOTT", Glass: "N-BK7" } && exit.Model.Coefficients.Length == 0,
                 "the file names the glass and holds none of its coefficients");

        // Evaluated from the catalogue wherever it is needed, exactly.
        var api = new ThinFilmApi();
        var id = api.Load(json).Value!.Id;
        var row = api.Operands(id).Value![0];
        api.ReplaceOperand(id, 0, row with { Wavelengths = [700.0] });
        var held = api.Document(id).Value!;
        h.PinExact(held.Exit.N[Array.IndexOf(held.Wavelength, 700.0)],
                   GlassCatalogs.Find("SCHOTT", "N-BK7")!.At(700).N,
                   "N-BK7 at 700 nm is the catalogue formula's value");

        // Not editable, and a glass that disagrees with itself says so.
        var form = new MaterialsForm(MaterialDatabase.Shipped(), w.Materials!);
        h.Expect(form.SetDefinition(1, "N-BK7 better", new MaterialModel { Kind = "CAUCHY", Coefficients = [1.5, 0, 0] })?.Contains("cannot be edited") == true,
                 "editing the catalogue glass is refused, saying why");
        h.Expect(form.UseGlassAsSubstrate("CDGM", "H-TK9", out var caution) is null && caution?.Contains("H-TK9") == true,
                 $"choosing CDGM H-TK9 works, and carries its catalogue caution: {caution}");
        h.Expect(form.UseGlassAsSubstrate("SCHOTT", "N-NOTHING", out _)?.Contains("not in the glass catalogues") == true,
                 "a glass that does not exist is refused");
    }

    public static void AMissingGlassBlocksTheDesignWithoutStoppingIt(Harness h)
    {
        h.Section("a design whose glass is not here opens, says why, and is not evaluated until fixed");

        var w = new DesignWorkspace();
        w.CreateNew(new NewDesignForm(MaterialDatabase.Shipped()).ToRequest(out _)!);
        string saved = w.Save()!;
        double merit = w.Merit;

        string shipped = GlassCatalogs.Folder;
        var empty = Path.Combine(Path.GetTempPath(), "thinfilm-no-glass-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        try
        {
            GlassCatalogs.Folder = empty;
            var b = new DesignWorkspace();
            h.Expect(b.Open(saved, "coat.json"), "the design still opens");
            h.Expect(b.Blocked?.Contains("SCHOTT N-BK7") == true && b.Blocked.Contains(empty),
                     $"blocked, naming the glass and where it was looked for: {b.Blocked}");
            h.Expect(b.Status.StartsWith("Not evaluated:", StringComparison.Ordinal),
                     $"and the status line says so - not a dialog: {b.Status}");

            // NOT EVALUATED, and NOT the values the file last held: those would be a silent substitute.
            h.Expect(double.IsNaN(b.Merit), "there is no merit");
            h.Expect(b.MeritRows.All(r => r.ValueText == "—"), "and every merit value is a dash");
            h.Pin(b.Layers.Count, 2, "but the stack is shown");
            h.Expect(b.MeritRows.Count == 1 && b.MeritRows[0].WavelengthText == "550", "and the merit rows");

            h.Expect(!b.Optimise("refine") && b.Status.Contains("SCHOTT N-BK7"), $"optimising is refused with the reason: {b.Status}");
            h.Expect(!b.RefreshSpectrum(), "and so is a spectrum");
            h.Expect(b.SetThickness(0, 99.0) && b.Layers[0].Thickness == 99.0, "a thickness can still be edited");

            // Fixed by choosing a substrate that is here: the design is evaluated again.
            var form = new MaterialsForm(MaterialDatabase.Shipped(), b.Materials!);
            form.UseAsSubstrate("SiO2");
            h.Expect(b.ApplyMaterials(form.CoatingEdits(), form.Incident, form.Substrate), "a database substrate is chosen");
            h.Expect(b.Blocked is null && double.IsFinite(b.Merit), $"and the design evaluates again (merit {b.Merit:G4})");
        }
        finally
        {
            GlassCatalogs.Folder = shipped;
            try { Directory.Delete(empty, true); } catch (IOException) { }
        }

        var again = new DesignWorkspace();
        h.Expect(again.Open(saved) && again.Blocked is null && again.Merit == merit,
                 "with the catalogues back, the same file evaluates to the same merit");
    }

    /// <summary>
    /// THE USER'S QUESTION: "how do I transition from the default constant index materials to
    /// real materials with dispersion?" AR-visible is exactly that case - constant SiO2 and Nb2O5
    /// on a constant n=1.520000 substrate.
    /// </summary>
    public static void ConstantMaterialsAreReplacedByRealOnes(Harness h)
    {
        h.Section("from constant materials to real ones: Upgrade, and Replace, with the layers following");

        var db = MaterialDatabase.Shipped();
        var w = new DesignWorkspace();
        w.OpenSample("AR-visible");
        string[] Stack() => w.Layers.Select(l => l.Material).ToArray();
        var before = Stack();
        h.Expect(before.SequenceEqual(["SiO2", "Nb2O5", "SiO2", "Nb2O5"]), $"AR-visible: {string.Join(" ", before)}");
        h.Expect(w.Materials!.Materials.All(m => m.Model.Kind == "CONSTANT"), "every material constant");

        var form = new MaterialsForm(db, w.Materials!);
        var plan = form.UpgradePlan();
        h.Expect(plan.Select(u => u.Name).SequenceEqual(["n=1.520000", "Nb2O5", "SiO2"]),
                 $"the plan lists every constant material bar air ({string.Join(", ", plan.Select(u => u.Name))})");
        h.Expect(plan.Single(u => u.Name == "SiO2").To?.StartsWith("Sellmeier") == true,
                 "SiO2 has a database match, a Sellmeier");
        h.Expect(plan.Single(u => u.Name == "Nb2O5").To?.StartsWith("measured table") == true, "and so has Nb2O5, a measured table");
        h.Expect(plan.Single(u => u.Name == "n=1.520000").To is null, "the constant substrate has none");

        string said = form.UpgradeAll();
        h.Expect(said.Contains("SiO2 (constant n = 1.46 → Sellmeier") && said.Contains("Nb2O5 (constant n = 2.3 → measured table")
                 && said.Contains("n=1.520000: left as it is"),
                 $"the upgrade says what it replaced and what it left: {said}");

        // A material with no match is replaced by a chosen one: Nb2O5 by the database's TiO2.
        int nb = form.Entries.FindIndex(e => e.Material.Name == "Nb2O5");
        h.Expect(form.ReplaceWithDatabase(nb, "SiO2")?.Contains("already has another material called 'SiO2'") == true,
                 "replacing it with SiO2, which the design already has, is refused");
        h.Expect(form.ReplaceWithDatabase(nb, "TiO2") is null, "replacing it with TiO2 is not");
        h.Expect(form.Entries[nb].LayersUsing == 2, "and the entry keeps its two layers");

        h.Expect(w.ApplyMaterials(form.CoatingEdits(), form.Incident, form.Substrate), $"OK: {w.Status}");
        h.Expect(Stack().SequenceEqual(["SiO2", "TiO2", "SiO2", "TiO2"]),
                 $"every layer followed its material ({string.Join(" ", Stack())})");
        var set = w.Materials!;
        h.Expect(set.Materials.Single(m => m.Name == "SiO2").Model.Kind == "SELLMEIER"
                 && set.Materials.Single(m => m.Name == "TiO2").Model.Kind == "CAUCHY",
                 "and the design now holds real, dispersive definitions");
        h.Expect(w.Layers.Count == 4 && w.MeritRows.Count == 29, "with the stack and the merit function untouched");
        h.Expect(db.Find("SiO2")!.Model.Coefficients.SequenceEqual(SiO2.Coefficients), "and the database untouched");

        var none = new MaterialsForm(db, w.Materials!);
        h.Expect(none.UpgradePlan().Single().Name == "n=1.520000",
                 "afterwards only the constant substrate is left to upgrade - by choosing a glass");
    }

    /// <summary>
    /// THE USER: "What does Replace SiO2 with database SiO2 mean? How does the user know what
    /// database SiO2 is? If it is constant n, that is really bad. Also what does Copy SiO2 into
    /// design mean?" - with a saved design holding an unused SiO2 (2) identical to its SiO2, and a
    /// constant Nb2O5 the database cannot upgrade. Every one of those is now said in words.
    /// </summary>
    public static void TheMaterialsDialogSaysWhatThingsAre(Harness h)
    {
        h.Section("Materials: each material described, Replace showing both sides, duplicates and constants flagged");

        var db = MaterialDatabase.Shipped();
        var w = new DesignWorkspace();
        w.OpenSample("AR-visible");
        var form = new MaterialsForm(db, w.Materials!);

        string silica = MaterialsForm.Summary(db.Find("SiO2")!);
        h.Expect(silica.StartsWith("Sellmeier, n = 1.4599") && silica.Contains("at 550 nm") && silica.EndsWith("210–3710 nm"),
                 $"the database's SiO2 says what it is: {silica}");
        // IN THE DESIGN'S WAVELENGTH UNIT: a design shown in µm reads µm here too.
        string inMicrons = MaterialsForm.Summary(db.Find("SiO2")!, "um");
        h.Expect(inMicrons.Contains("at 0.55 µm") && inMicrons.EndsWith("0.21–3.71 µm"),
                 $"and in µm when the design is shown in µm: {inMicrons}");
        var micronPreview = MaterialsForm.Preview(db.Find("SiO2")!, out _, "um")!.Value;
        h.Expect(micronPreview.X[0] < 1.0 && micronPreview.Span.Contains("µm"), $"the preview axis follows too ({micronPreview.X[0]}, {micronPreview.Span})");
        var nd = new NewDesignForm(db) { WavelengthUnit = "um" };
        h.Expect(nd.ReferenceNm == "0.55" && nd.ToRequest(out _)!.ReferenceNm == 550.0,
                 $"New design's reference is typed in µm and read as 550 nm ({nd.ReferenceNm})");
        h.Expect(MaterialsForm.Summary(db.Find("TiO2")!).EndsWith("no stated range"),
                 $"and a Cauchy with no range says so: {MaterialsForm.Summary(db.Find("TiO2")!)}");

        int sio2 = form.Entries.FindIndex(e => e.Material.Name == "SiO2");
        int nb = form.Entries.FindIndex(e => e.Material.Name == "Nb2O5");
        h.Expect(form.Entries[nb].Label.Contains("⚠ constant n = 2.3"), $"a constant material is flagged in the list: {form.Entries[nb].Label}");
        h.Expect(!form.Entries[0].Label.Contains('⚠'), $"but air is not: {form.Entries[0].Label}");
        h.Expect(form.ConstantAdvice(nb)?.Contains("The database's Nb2O5 is measured table, n = 2.3603, k < 0.0001 at 550 nm") == true
                 && form.ConstantAdvice(nb)!.Contains("press Replace"),
                 $"Nb2O5 points to the database's measured Nb2O5: {form.ConstantAdvice(nb)}");
        h.Expect(form.ConstantAdvice(sio2)?.Contains("press Replace") == true, $"SiO2 points to Replace: {form.ConstantAdvice(sio2)}");
        h.Expect(form.ConstantAdvice(1)?.Contains("database has no n=1.520000") == true
                 && form.ConstantAdvice(1)!.Contains("catalogue glass"),
                 $"the constant substrate, with no match, points to a catalogue glass: {form.ConstantAdvice(1)}");
        var plain = new MaterialsForm(db, new MaterialSet([new NamedMaterial("ITO", MaterialModel.Constant(1.9))],
                                                        MaterialDatabase.Air, MaterialDatabase.Air, [1]));
        h.Expect(plain.ConstantAdvice(2)?.Contains("Edit dispersion") == true,
                 $"and a coating material with no match points to Edit dispersion: {plain.ConstantAdvice(2)}");

        string? preview = form.ReplacePreview(sio2, db.Find("SiO2")!, "database", out bool changes);
        h.Expect(changes && preview!.Contains("Now: constant n = 1.46") && preview.Contains("Becomes: Sellmeier")
                 && preview.Contains("Its 2 layers follow"),
                 $"Replace states both sides before it is pressed: {preview}");

        h.Expect(form.AddWarning("SiO2") is null, "adding SiO2 while the design's is constant is not a duplicate");
        h.Expect(form.ReplaceWithDatabase(sio2, "SiO2") is null, "replaced");
        h.Expect(form.ReplacePreview(sio2, db.Find("SiO2")!, "database", out changes)!.Contains("would change nothing") && !changes,
                 "and replacing it again would change nothing, which is said");
        h.Expect(form.AddWarning("SiO2")?.Contains("already has SiO2 with exactly this definition") == true,
                 $"now adding SiO2 warns that it would be an identical second copy: {form.AddWarning("SiO2")}");
    }

    /// <summary>
    /// THE USER: "For each dispersion type, there should be a dialog that the user can enter the
    /// coefficients. Expecting the user to edit a string will not work." And: "the user needs to be
    /// able to load the coefficients from a library."
    /// </summary>
    public static void CoefficientsAreFieldsNotStrings(Harness h)
    {
        h.Section("the dispersion form: a field per coefficient, the library, and table import");

        var db = MaterialDatabase.Shipped();

        // Every model in, and the same model out.
        foreach (var name in new[] { "SiO2", "TiO2" })
        {
            var m = db.Find(name)!.Model;
            var f = new CoefficientsForm(name, m, db);
            var back = f.ToModel(out var why);
            h.Expect(back is not null && back.Kind == m.Kind && back.Coefficients.SequenceEqual(m.Coefficients)
                     && back.Range() == m.Range() && back.Source == m.Source,
                     $"{name}'s {m.Kind.ToLowerInvariant()} round-trips through the fields ({why})");
        }
        var sio2 = new CoefficientsForm("SiO2", db.Find("SiO2")!.Model, db);
        h.Expect(sio2.SellmeierB[0] == "0.6961663" && sio2.SellmeierC[2] == "97.934" && sio2.SellmeierB[3] == "",
                 "Sellmeier fields are B1..B3, C1..C3, the rest empty");

        // The library: a constant SiO2 given the database's Sellmeier, kind and all.
        var constant = new CoefficientsForm("SiO2", MaterialModel.Constant(1.46), db);
        h.Expect(constant.ConstantN == "1.46", $"a constant SiO2 shows n = 1.46 ({constant.ConstantN})");
        h.Expect(constant.LoadFromLibrary("SiO2") is null && constant.Kind == "SELLMEIER"
                 && constant.SellmeierB[0] == "0.6961663", "Load from library switches it to the database's Sellmeier");
        h.Expect(constant.ToModel(out _)!.At(587.56, "SiO2").N is > 1.4584 and < 1.4586, "which gives 1.4585 at the d line");

        // A Cauchy edited field by field.
        var cauchy = new CoefficientsForm("X", MaterialModel.Constant(1.5), db) { Kind = "CAUCHY", CauchyA = "1.7", CauchyB = "0.01" };
        h.Expect(cauchy.ToModel(out _) is { Coefficients: [1.7, 0.01, 0.0] }, "a Cauchy C left empty is 0");

        // Refusals, each saying which field.
        var halfTerm = new CoefficientsForm("X", db.Find("SiO2")!.Model, db);
        halfTerm.SellmeierB[3] = "0.1";
        h.Expect(halfTerm.ToModel(out var p1) is null && p1!.Contains("term 4 needs both B4 and C4"), $"half a Sellmeier term: {p1}");
        var badA = new CoefficientsForm("X", MaterialModel.Constant(1.5), db) { Kind = "CAUCHY", CauchyA = "one" };
        h.Expect(badA.ToModel(out var p2) is null && p2!.Contains("A, 'one', is not a number"), $"a word for A: {p2}");
        var empty = new CoefficientsForm("X", MaterialModel.Constant(1.5), db) { Kind = "TABLE" };
        h.Expect(empty.ToModel(out var p3) is null && p3!.Contains("at least one row"), $"an empty table: {p3}");

        // Import: a CSV with a header, in nm because the person said nm.
        var t = new CoefficientsForm("Ag", MaterialModel.Constant(1.0), db);
        string csv = "wavelength (nm), n, k\n400, 0.050, 2.104\n500, 0.050, 3.131\n600, 0.055, 4.010\n";
        string said = t.ImportTable(csv, "nm", out bool ok);
        h.Expect(ok && t.Kind == "TABLE" && t.Table.Count == 3 && t.Table[0].Wavelength == "0.4",
                 $"three rows, 400 nm read as 0.4 µm: {said}");
        h.Expect(said.Contains("1 line that were not numbers skipped"), "and the header is counted as skipped, not silently dropped");
        var ag = t.ToModel(out _)!;
        h.Expect(Math.Abs(ag.At(450, "Ag").K - (2.104 + 3.131) / 2) < 1e-12, "and the table interpolates between its rows");
        var asMicrons = new CoefficientsForm("Ag", MaterialModel.Constant(1.0), db);
        asMicrons.ImportTable(csv, "um", out bool tookMicrons);
        h.Expect(!tookMicrons && asMicrons.Table.Count == 0, "the same file read as µm is refused: its header says nm");
        asMicrons.ImportTable(csv[(csv.IndexOf('\n') + 1)..], "um", out tookMicrons);
        h.Expect(tookMicrons && asMicrons.Table[0].Wavelength == "400",
                 "without the header, read as µm it is taken as µm - never guessed from its size");
    }

    public static void AnOlderFileOpensWithModels(Harness h)
    {
        h.Section("a design saved before models existed opens, its samples becoming models");

        var w = new DesignWorkspace();
        w.OpenSample("AR-visible");
        // A version-1 file: the same document with every "Model" removed.
        var doc = DesignDocument.FromJson(w.Save()!);
        var v1 = doc with
        {
            Version = 1,
            Materials = doc.Materials.Select(m => m with { Model = null }).ToArray(),
            Incident = doc.Incident with { Model = null },
            Exit = doc.Exit with { Model = null },
        };
        string json = v1.ToJson();
        h.Expect(!json.Contains("\"Model\"", StringComparison.Ordinal), "the file has no models in it");

        var opened = DesignDocument.FromJson(json);
        h.Pin(opened.Version, DesignDocument.CurrentVersion, "it opens as the current version");
        h.Expect(opened.Materials.All(m => m.Model is { Kind: "CONSTANT" }),
                 "constant samples become CONSTANT models, valid everywhere, as they were meant");
        h.Expect(opened.Materials[0].Model!.Coefficients[0] == doc.Materials[0].N[0],
                 "with the index they had");

        var sampled = MaterialModel.FromSamples([400, 600], [1.50, 1.48], [0, 0]);
        h.Expect(sampled is { Kind: "TABLE" } && sampled.Range() == (0.4, 0.6),
                 "samples that vary become a TABLE over the grid they were sampled on");
    }
}
