// ZemaxTests.cs — OpticStudio coating files (ZemaxCoating.cs): reading a COAT into a design, and
// writing a design into a shared file without changing anything else in it.
//
// The user (2026-10-21): "We need to be able to read/write a design to ZEMAX coating file. It is a
// little tricky since a coating file has many designs and many materials. We can not change
// existing material names. Also it makes sense to output the materials as nk tables ... when we
// output an nk table we output the material model in comments so that we read it back in we can
// use dispersion equations." Then: "Always µm, go with the recommendations."
//
// The fixture is a cut-down COATING.DAT with what OpticStudio's own sample has: UTF-16 with a BOM
// and CRLF, no newline at the end, a padded MATE name, a coating in waves, one in µm with a
// blank-padded tail, a loop, a taper, an ideal coating, IDEAL, TABLE, TAPR and ENCRYPTED sections.

using System.Text;
using System.Text.Json.Nodes;
using ThinFilm.Api;
using ThinFilm.Cli;
using ThinFilm.Gui.Core;
using ThinFilm.Mcp;

namespace ThinFilm.Native.Tests;

public static class ZemaxTests
{
    private static readonly string[] Fixture =
    [
        "! Sample coating.dat file supplied with ZEMAX",
        "",
        "MATE AIR",
        "0.55 1.0 0",
        "",
        "MATE MGF2",
        ".400 1.38 0",
        ".700 1.375 0",
        "",
        "MATE          SIO2_EM",
        " .40         1.4701      -4.8E-07",
        " .80         1.4533      4.8E-07",
        "",
        "MATE BAD",
        "0.55 1.5 0.2",
        "",
        "! Sample coating definitions",
        "COAT AR",
        "MGF2 .25",
        "",
        "COAT          AR_3L",
        "SIO2_EM           .087221100520488           1                           ",
        "MGF2              .1                         1   ",
        "                       ",
        "",
        "COAT ETALON",
        "MGF2 .25 0 4",
        "",
        "COAT AR_TAPR",
        "MGF2 .25 0 0 QUAD",
        "",
        "COAT MISSING",
        "TIO2 .25",
        "",
        "COAT GAIN",
        "BAD 0.1 1",
        "",
        "COAT I.95",
        "",
        "IDEAL SPLIT 0.50 0.50",
        "TABLE PASS45",
        "ANGL 0.0",
        "WAVE 0.45 1.0 1.0 0.0 0.0 0.0 0.0 0.0 0.0",
        "",
        "TAPR QUAD",
        "RT 2 .1",
        "",
        "ENCRYPTED ZEC_AR-16.ZEC",
    ];

    private static string Dir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "thinfilm-zemax-tests");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>The fixture written as OpticStudio writes COATING.DAT: UTF-16 LE, BOM, CRLF.</summary>
    private static string WriteFixture(string name, IEnumerable<string>? lines = null)
    {
        string path = Path.Combine(Dir(), name);
        var encoding = new UnicodeEncoding(false, true);
        File.WriteAllBytes(path, [.. encoding.GetPreamble(), .. encoding.GetBytes(string.Join("\r\n", lines ?? Fixture))]);
        File.Delete(path + ".bak");
        return path;
    }

    private static string[] LinesOf(string path) => ZemaxCoatingFile.Read(path).Lines.ToArray();

    /// <summary>A design from the database, as File > New design makes one: TiO2 (Cauchy), SiO2
    /// (Sellmeier) and Nb2O5 (a measured table) on N-BK7.</summary>
    private static string Dispersive(ThinFilmApi api)
    {
        string f = Path.Combine(Dir(), "dispersive.tfd");
        CommandLine.Run(["new", "--materials", "TiO2,SiO2,Nb2O5", "--substrate", "SCHOTT N-BK7", "--layers", "4",
                         "--name", "DISP", "-o", f], new StringWriter(), new StringWriter());
        CommandLine.Run(["layer", "set", f, "--layer", "3", "--material", "Nb2O5"], new StringWriter(), new StringWriter());
        return api.Load(File.ReadAllText(f)).Value!.Id;
    }

    public static void ReadingTheFileFindsItsCoatings(Harness h)
    {
        h.Section("A coating file: its sections, the stacks it lists, and what cannot be imported");

        string path = WriteFixture("list.dat");
        var file = ZemaxCoatingFile.Read(path);
        h.Expect(file.Encoding is UnicodeEncoding && file.NewLine == "\r\n" && file.Lines[^1] == "ENCRYPTED ZEC_AR-16.ZEC",
                 "UTF-16 with CRLF and no newline at the end is read as it is");
        h.Expect(file.MaterialNames().SequenceEqual(["AIR", "MGF2", "SIO2_EM", "BAD"]),
                 $"MATEs, the padded one by its name: {string.Join(", ", file.MaterialNames())}");
        var mate = file.Find("MATE", "sio2_em")!;
        h.Expect(mate.Start == 9 && mate.End == 12 && file.Rows(mate)[0][2] == -4.8e-7,
                 "a MATE is found by name in any case, its rows end before the blank line, k as written");
        var coat = file.Find("COAT", "AR_3L")!;
        h.Expect(coat.End == 23 && file.Layers(coat) is [{ Material: "SIO2_EM", Absolute: true }, { Thickness: 0.1 }],
                 "a COAT's blank-padded last line ends it, and its layers are material, thickness, is_absolute");

        var list = ThinFilmApi.ZemaxCoatings(path).Value!;
        h.Expect(list.Select(c => c.Name).SequenceEqual(["AR", "AR_3L", "ETALON", "AR_TAPR", "MISSING", "GAIN"]),
                 $"layer stacks only, not I.95, IDEAL or TABLE: {string.Join(", ", list.Select(c => c.Name))}");
        h.Expect(list.Single(c => c.Name == "ETALON").Problem!.Contains("loop")
                 && list.Single(c => c.Name == "MISSING").Problem!.Contains("TIO2")
                 && list.Where(c => c.Name is "AR" or "AR_3L").All(c => c.Problem is null),
                 "a loop and a material with no MATE are named as the reason; the others can be imported");
    }

    public static void ImportGivesTheLayersAndMaterials(Harness h)
    {
        h.Section("Import: µm as nm, waves as QWOT at the file's reference or the design's, k's sign, refusals");

        var api = new ThinFilmApi();
        string path = WriteFixture("import.dat");
        string id = api.OpenSample("AR-visible").Value!.Id;
        var before = api.Document(id).Value!;

        var r = api.ImportZemaxCoating(id, path, "ar_3l");
        var doc = api.Document(id).Value!;
        h.Expect(r.Ok && doc.Layers.Length == 2 && Math.Abs(doc.Layers[0].Thickness - 87.221100520488) < 1e-9
                 && doc.Layers[1].Thickness == 100.0 && doc.Operands.Length == before.Operands.Length
                 && doc.Exit.Name == before.Exit.Name,
                 "AR_3L: 87.2211 and 100 nm, and the merit function and substrate are kept");
        var sio2 = doc.Materials.Single(m => m.Name == "SIO2_EM");
        h.Expect(sio2.Model!.Kind == "TABLE" && sio2.Model.Table![0][2] == 4.8e-7 && sio2.Model.Table[1][2] == 0.0
                 && r.Value!.Notes.Any(n => n.Contains("read as k = 0")),
                 "SIO2_EM's -4.8E-07 is absorption, k = 4.8E-07; its tiny positive one is read as k = 0, with a note");
        h.Expect(doc.Materials.Single(m => m.Name == "MGF2").Model!.Table!.Length == 2,
                 "MGF2 comes in as its two-row table");

        r = api.ImportZemaxCoating(id, path, "AR");
        doc = api.Document(id).Value!;
        double n = doc.Materials.Single(m => m.Name == "MGF2").Model!.At(doc.ReferenceWavelength, "MGF2").N;
        h.Expect(r.Ok && doc.Layers is [{ Unit: "QWOT" }] && Math.Abs(doc.Layers[0].Thickness - doc.ReferenceWavelength / (4 * n)) < 1e-9
                 && r.Value!.Notes.Any(x => x.Contains("the file does not say")),
                 $"AR's 0.25 wave is 1 QWOT at the design's {doc.ReferenceWavelength} nm, and the note says the file does not give one");

        var dated = Fixture.ToList();
        dated.Insert(dated.IndexOf("COAT AR"), "! designed at 0.6328 um");
        string datedPath = WriteFixture("dated.dat", dated);
        r = api.ImportZemaxCoating(id, datedPath, "AR");
        doc = api.Document(id).Value!;
        n = doc.Materials.Single(m => m.Name == "MGF2").Model!.At(632.8, "MGF2").N;
        h.Expect(r.Ok && Math.Abs(doc.ReferenceWavelength - 632.8) < 1e-9
                 && Math.Abs(doc.Layers[0].Thickness - 632.8 / (4 * n)) < 1e-9,
                 "a person's \"designed at 0.6328 um\" above the COAT is the reference it is counted at");

        h.Expect(api.ImportZemaxCoating(id, path, "AR_TAPR") is { Ok: true } t && t.Value!.Notes.Any(x => x.Contains("tapered")),
                 "a tapered coating comes in at its nominal thickness, with a note");
        h.Expect(api.ImportZemaxCoating(id, path, "ETALON") is { Ok: false } e1 && e1.Error!.Contains("loop"),
                 "a loop index is refused rather than guessed at");
        h.Expect(api.ImportZemaxCoating(id, path, "GAIN") is { Ok: false } e2 && e2.Error!.Contains("gain"),
                 $"a real positive imaginary part is refused: {api.ImportZemaxCoating(id, path, "GAIN").Error}");
        h.Expect(api.ImportZemaxCoating(id, path, "SPLIT") is { Ok: false } e3 && e3.Error!.Contains("IDEAL")
                 && api.ImportZemaxCoating(id, path, "NONE") is { Ok: false } e4 && e4.Error!.Contains("no coating"),
                 "an IDEAL coating and a name not in the file are refused, saying which");
        h.Expect(MaterialsUnchangedInAFailure(api, id), "a refused import leaves the design as it was");
    }

    private static bool MaterialsUnchangedInAFailure(ThinFilmApi api, string id)
    {
        var before = api.Document(id).Value!;
        api.ImportZemaxCoating(id, WriteFixture("again.dat"), "MISSING");
        var after = api.Document(id).Value!;
        return after.Materials.Length == before.Materials.Length && after.Layers.Length == before.Layers.Length;
    }

    public static void ExportKeepsEverythingElse(Harness h)
    {
        h.Section("Export: one COAT and its MATEs added, every other line kept, in the file's encoding");

        var api = new ThinFilmApi();
        string path = WriteFixture("export.dat");
        string id = api.OpenSample("AR-visible").Value!.Id;
        var doc = api.Document(id).Value!;

        var r = api.ExportZemaxCoating(id, new ZemaxExportRequest(path, "AR VIS"));
        var bytes = File.ReadAllBytes(path);
        var lines = LinesOf(path);
        h.Expect(r.Ok && r.Value!.Coating == "AR_VIS" && bytes[0] == 0xFF && bytes[1] == 0xFE
                 && Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2).Contains("\r\n") && lines[^1].Trim().Length > 0,
                 "written as COAT AR_VIS, still UTF-16 with a BOM and CRLF, still without a final newline");
        h.Expect(IsSubsequence(Fixture, lines) && lines.Length - Fixture.Length == OurLines(lines),
                 $"every original line is there, in order, and the {lines.Length - Fixture.Length} new ones are ours");
        h.Expect(File.Exists(path + ".bak") && LinesOf(path + ".bak").SequenceEqual(Fixture),
                 "the file as it was is kept beside it as .bak");

        int mate = Array.IndexOf(lines, "MATE SIO2"), lastOld = Array.IndexOf(lines, "0.55 1.5 0.2"),
            firstCoat = Array.IndexOf(lines, "COAT AR"), coat = Array.IndexOf(lines, "COAT AR_VIS");
        h.Expect(mate > lastOld && mate < firstCoat && coat > Array.IndexOf(lines, "ENCRYPTED ZEC_AR-16.ZEC"),
                 "the MATEs go after the file's last MATE, before any coating; the COAT at the end");
        h.Expect(lines[coat - 1].Contains("designed at reference wavelength 0.55 um")
                 && lines[coat + 1].Split(' ', StringSplitOptions.RemoveEmptyEntries) is [_, var um, "1"]
                 && Math.Abs(double.Parse(um, System.Globalization.CultureInfo.InvariantCulture) * 1000 - doc.Layers[0].Thickness) < 1e-6,
                 $"the COAT says its reference and gives µm with is_absolute 1: {lines[coat + 1]}");
        h.Expect(lines[mate - 1] == "! ThinFilmDesigner MODEL CONSTANT 1.46 0" && lines[mate + 1] == "0.55 1.46 0",
                 "a constant is one row, its model in the comment above");

        var again = api.ExportZemaxCoating(id, new ZemaxExportRequest(path, "AR_VIS"));
        h.Expect(!again.Ok && again.Status == ThinFilmApi.CoatingExists && LinesOf(path).SequenceEqual(lines),
                 "the same name again is refused with COATING_EXISTS, and nothing is written");

        api.SetThickness(id, 0, doc.Layers[0].Thickness + 10.0);
        var replaced = api.ExportZemaxCoating(id, new ZemaxExportRequest(path, "AR_VIS", Replace: true));
        var after = LinesOf(path);
        h.Expect(replaced.Ok && replaced.Value!.Replaced && replaced.Value.Added.Length == 0
                 && after.Length == lines.Length && after.Count(l => l.StartsWith("COAT AR_VIS")) == 1
                 && after.Count(l => l.Contains("designed at reference")) == 1 && Array.IndexOf(after, "COAT AR_VIS") == coat
                 && after[coat + 1] != lines[coat + 1],
                 "with Replace it is replaced where it stands, its comment with it, and its materials are found already there");

        h.Expect(api.ExportZemaxCoating(id, new ZemaxExportRequest(path, "SPLIT")) is { Ok: false } e && e.Error!.Contains("IDEAL")
                 && api.ExportZemaxCoating(id, new ZemaxExportRequest(path, "I.5")) is { Ok: true } i5 && i5.Value!.Coating == "I_5",
                 "a name an IDEAL coating has is refused; I.5, which OpticStudio would read as an ideal coating, is written as I_5");
    }

    private static int OurLines(string[] lines)
    {
        // Ours: the tagged comments, the MATE and COAT blocks after them, and one blank before each.
        int count = 0;
        for (int i = 0; i < lines.Length; ++i)
        {
            if (!lines[i].StartsWith(ZemaxCoatingFile.Tag)) continue;
            int j = i;
            while (j < lines.Length && lines[j].StartsWith(ZemaxCoatingFile.Tag)) ++j;
            int end = j + 1;
            while (end < lines.Length && lines[end].Trim().Length > 0 && !lines[end].StartsWith("!")) ++end;
            count += end - i + 1;
            i = end - 1;
        }
        return count;
    }

    private static bool IsSubsequence(string[] small, string[] big)
    {
        int j = 0;
        foreach (var line in big) if (j < small.Length && line == small[j]) ++j;
        return j == small.Length;
    }

    public static void ExistingMaterialsAreNeverChanged(Harness h)
    {
        h.Section("A MATE of the same name: used when its data agree, left alone and ours written as _2 when not");

        var api = new ThinFilmApi();
        string path = WriteFixture("names.dat");
        string ar = api.OpenSample("AR-visible").Value!.Id;
        api.ExportZemaxCoating(ar, new ZemaxExportRequest(path, "FIRST"));
        string disp = Dispersive(api);
        var r = api.ExportZemaxCoating(disp, new ZemaxExportRequest(path, "SECOND", 400.0, 800.0));
        var lines = LinesOf(path);
        h.Expect(r.Ok && r.Value!.Materials["SiO2"] == "SIO2_2" && r.Value.Materials["Nb2O5"] == "NB2O5_2"
                 && r.Value.Materials["TiO2"] == "TIO2" && r.Value.Notes.Count(n => n.Contains("left as it is")) == 2,
                 $"SiO2 and Nb2O5 differ from the constants already there: {string.Join(", ", r.Value!.Materials.Select(p => $"{p.Key}->{p.Value}"))}");
        int first = Array.IndexOf(lines, "MATE SIO2");
        h.Expect(lines[first + 1] == "0.55 1.46 0" && lines.Count(l => l == "MATE SIO2") == 1,
                 "the existing SiO2 is exactly as it was");

        var imported = new ThinFilmApi();
        string back = imported.OpenSample("AR-visible").Value!.Id;
        imported.ImportZemaxCoating(back, path, "SECOND");
        var third = imported.ExportZemaxCoating(back, new ZemaxExportRequest(path, "THIRD", 400.0, 800.0));
        h.Expect(third.Ok && third.Value!.Added.Length == 0,
                 "a design that imported those materials writes back to them, adding nothing");
        h.Expect(LinesOf(path).Count(l => l.StartsWith("MATE ")) == 4 + 5,
                 "and the file has the fixture's four MATEs and the five written: no material is ever rewritten");
    }

    public static void EquationsComeBackAsEquations(Harness h)
    {
        h.Section("An equation as a table within 1e-5, its model in a comment, and the model back on import");

        var api = new ThinFilmApi();
        string path = Path.Combine(Dir(), "new.dat");
        File.Delete(path);
        File.Delete(path + ".bak");
        string id = Dispersive(api);
        var doc = api.Document(id).Value!;
        double merit = api.Describe(id).Value!.Merit;

        var r = api.ExportZemaxCoating(id, new ZemaxExportRequest(path, null, 400.0, 800.0));
        var bytes = File.ReadAllBytes(path);
        h.Expect(r.Ok && r.Value!.Coating == "DISP" && r.Value.Backup is null && bytes[0] != 0xFF && bytes[0] != 0xEF,
                 "a new file is made, plain text without a BOM, the coating named after the design");

        var file = ZemaxCoatingFile.Read(path);
        var sio2 = doc.Materials.Single(m => m.Name == "SiO2").Model!;
        var rows = file.Rows(file.Find("MATE", "SiO2")!).Select(x => new[] { x[0], x[1], -x[2] }).ToArray();
        double worst = 0.0;
        for (double um = 0.4; um <= 0.8; um += 0.00137)
            worst = Math.Max(worst, Math.Abs(ZemaxCoatingFile.Interpolate(rows, um).N - sio2.At(um * 1000, "SiO2").N));
        h.Expect(rows[0][0] == 0.4 && rows[^1][0] == 0.8 && worst < 1.1e-5,
                 $"SiO2's Sellmeier on {rows.Length} rows from 0.4 to 0.8 µm: linear interpolation within {worst:G2}");
        h.Expect(file.Notes(file.Find("MATE", "SiO2")!)[0].StartsWith("MODEL SELLMEIER 0.6961663")
                 && file.Notes(file.Find("MATE", "TiO2")!)[0] == "MODEL CAUCHY 2.2 0.03 0.003",
                 "the comments carry the coefficients");
        var nb = file.Rows(file.Find("MATE", "Nb2O5")!);
        var nbModel = doc.Materials.Single(m => m.Name == "Nb2O5").Model!;
        var inRange = nbModel.Table!.Where(x => x[0] >= 0.4 && x[0] <= 0.8).ToArray();
        h.Expect(nb.Length <= inRange.Length + 2 && nb.Length >= inRange.Length && nb[0][0] <= 0.4 && nb[^1][0] >= 0.8
                 && nb.Any(x => x[2] < 0) && nb.All(x => x[2] <= 0),
                 $"the Nb2O5 table is its own {inRange.Length} rows from 0.4 to 0.8 µm, and one beyond an end that falls between rows, of {nbModel.Table!.Length}, its absorption as negative k");

        string id2 = api.OpenSample("AR-visible").Value!.Id;
        var back = api.ImportZemaxCoating(id2, path, "DISP");
        var got = api.Document(id2).Value!;
        string KindOf(string name) => got.Materials.Single(m => m.Name == name).Model!.Kind;
        h.Expect(back.Ok && KindOf("SIO2_2") == "SELLMEIER" && KindOf("TIO2") == "CAUCHY" && KindOf("NB2O5_2") == "TABLE",
                 "imported into a design with other SiO2 and Nb2O5: the Sellmeier and the Cauchy come back as themselves");

        var same = api.ImportZemaxCoating(id, path, "DISP");
        double merit2 = api.Describe(id).Value!.Merit;
        h.Expect(same.Ok && api.Document(id).Value!.Materials.Length == doc.Materials.Length
                 && Math.Abs(merit2 - merit) <= 1e-9 * Math.Max(1.0, merit),
                 $"imported into the design it came from: the same materials, and the merit {merit2:G10} against {merit:G10}");

        // Someone edits a number in OpticStudio: the table they edited wins over the comment.
        var text = File.ReadAllLines(path).ToList();
        int at = text.FindIndex(text.IndexOf("MATE TIO2"), l => l.StartsWith("0.55 "));
        var w = text[at].Split(' ');
        text[at] = $"{w[0]} 2.6 {w[2]}";
        File.WriteAllLines(path, text);
        var edited = api.ImportZemaxCoating(id2, path, "DISP");
        h.Expect(edited.Ok && edited.Value!.Notes.Any(x => x.Contains("TIO2") && x.Contains("no longer matches"))
                 && api.Document(id2).Value!.Materials.Any(m => m.Model!.Kind == "TABLE" && m.Name.StartsWith("TIO2")),
                 $"a table edited after export is used as the table, with a note: {edited.Error}{string.Join(" | ", edited.Value?.Notes ?? [])}");
    }

    /// <summary>THE USER (2026-10-21), on an export of the 451-row Nb2O5 table: OpticStudio said
    /// "Maximum Wavelength-index points exceeded in coating file".</summary>
    public static void NoMaterialHasMoreRowsThanOpticStudioTakes(Harness h)
    {
        h.Section("At most 120 rows a MATE, OpticStudio's own largest: equations placed where they bend, tables thinned");

        var api = new ThinFilmApi();
        string path = Path.Combine(Dir(), "wide.dat");
        File.Delete(path);
        string id = Dispersive(api);
        var doc = api.Document(id).Value!;
        var r = api.ExportZemaxCoating(id, new ZemaxExportRequest(path, null, 250.0, 2500.0));
        var file = ZemaxCoatingFile.Read(path);
        var counts = file.MaterialNames().ToDictionary(n => n, n => file.Rows(file.Find("MATE", n)!).Length);
        h.Expect(r.Ok && counts.Values.All(c => c <= ZemaxCoatingFile.MaxRows) && counts["NB2O5"] == ZemaxCoatingFile.MaxRows,
                 $"from 250 to 2500 nm every MATE has at most 120 rows: {string.Join(", ", counts.Select(p => $"{p.Key} {p.Value}"))}");

        // The thinned table against every row of the original: the worst is what the note says.
        var nbModel = doc.Materials.Single(m => m.Name == "Nb2O5").Model!;
        var nb = file.Rows(file.Find("MATE", "Nb2O5")!).Select(x => new[] { x[0], x[1], -x[2] }).ToArray();
        double worst = nbModel.Table!.Where(x => x[0] >= 0.25 && x[0] <= 2.5)
            .Max(x => Math.Max(Math.Abs(ZemaxCoatingFile.Interpolate(nb, x[0]).N - x[1]),
                               Math.Abs(ZemaxCoatingFile.Interpolate(nb, x[0]).K - x[2])));
        h.Expect(nb.All(x => nbModel.Table!.Any(t => t[0] == x[0])) && worst < 1e-3
                 && r.Value!.Notes.Any(n => n.StartsWith("Nb2O5 is written as 120 rows")),
                 $"Nb2O5 keeps 120 of its own rows, within {worst:G2} of all the others, and the note says so");

        var tio2 = doc.Materials.Single(m => m.Name == "TiO2").Model!;
        var rows = file.Rows(file.Find("MATE", "TiO2")!);
        double gapShort = rows[1][0] - rows[0][0], gapLong = rows[^1][0] - rows[^2][0];
        double miss = 0.0;
        for (double um = 0.25; um <= 2.5; um += 0.0011)
            miss = Math.Max(miss, Math.Abs(ZemaxCoatingFile.Interpolate(rows, um).N - tio2.At(um * 1000, "TiO2").N));
        h.Expect(gapShort < gapLong / 10 && miss < 2e-4,
                 $"TiO2's Cauchy has its rows close where it bends ({gapShort * 1000:G3} nm apart at 250 nm, {gapLong * 1000:G3} at 2500), within {miss:G2}");

        string fresh = Path.Combine(Dir(), "narrow.dat");
        File.Delete(fresh);
        var narrow = api.ExportZemaxCoating(id, new ZemaxExportRequest(fresh, "NARROW", 500.0, 600.0));
        var sio2 = ZemaxCoatingFile.Read(fresh);
        h.Expect(narrow.Ok && sio2.Rows(sio2.Find("MATE", narrow.Value!.Materials["SiO2"])!).Length < 20,
                 $"over 500-600 nm the Sellmeier needs only a few rows for 1e-5 ({sio2.Rows(sio2.Find("MATE", narrow.Value!.Materials["SiO2"])!).Length}, as {narrow.Value!.Materials["SiO2"]})");
    }

    public static void AWorkspaceKnowsWhenItHasChanged(Harness h)
    {
        h.Section("Whether the design has changed since it was opened, made or saved - what the window asks about");

        var w = new DesignWorkspace();
        h.Expect(!w.IsModified, "nothing open has nothing to save");
        w.OpenSample("AR-visible");
        h.Expect(!w.IsModified, "a design just opened is unchanged");
        w.SetThickness(0, w.Layers[0].Thickness + 1.0);
        h.Expect(w.IsModified, "a thickness changed is a change");
        w.SetThickness(0, w.Layers[0].Thickness - 1.0);
        h.Expect(!w.IsModified, "and changed back, is not");
        w.RemoveOperand(0);
        h.Expect(w.IsModified, "a merit row removed is a change");
        var json = w.Save()!;
        w.SavedAs("ar.tfd");
        h.Expect(!w.IsModified && json.Length > 0, "saved, it is unchanged again");
    }

    public static void TheFirstLayerFacesTheIncidentMedium(Harness h)
    {
        h.Section("OpticStudio lists a coating incident side first, as layer 1 is here: HEAR1 on N-BK7");

        // OpticStudio's own COATING.DAT: COAT HEAR1 / MGF2 .25 / ZRO2 .50 / CEF3 .25, the classic
        // quarter-half-quarter AR, which works only with the low-index MgF2 next to air. With constant
        // indices the half wave is absent at the reference, and R = ((n0 n3² - ns n1²)/(n0 n3² + ns n1²))²:
        // 0.18% this way up on N-BK7, 12.9% upside down.
        string path = Path.Combine(Dir(), "hear1.dat");
        File.WriteAllText(path, string.Join("\r\n",
            "MATE MGF2", "0.55 1.38 0", "", "MATE ZRO2", "0.55 2.05 0", "", "MATE CEF3", "0.55 1.63 0", "",
            "! designed at 0.55 um", "COAT HEAR_TEST", "MGF2 .25", "ZRO2 .50", "CEF3 .25", ""));
        string design = Path.Combine(Dir(), "hear1.tfd");
        CommandLine.Run(["new", "--materials", "SiO2", "--substrate", "N-BK7", "--reference", "550", "--layers", "1", "-o", design],
                        new StringWriter(), new StringWriter());
        var api = new ThinFilmApi();
        var id = api.Load(File.ReadAllText(design)).Value!.Id;

        var imported = api.ImportZemaxCoating(id, path, "hear test");
        h.Expect(imported.Ok, $"imported by the name as a person would type it, \"hear test\" for HEAR_TEST ({imported.Error})");
        double R() => api.Spectrum(id, new SpectrumRequest(550, 551, 2, 0.0, ["RA"])).Value!.Quantity["RA"][0];
        double right = R();
        api.ReverseLayers(id, media: false);
        double flipped = R();
        h.Expect(Math.Abs(right - 0.0018) < 3e-4 && Math.Abs(flipped - 0.129) < 3e-3,
                 $"MgF2 is layer 1, next to air: R = {right:P2} at 550 nm, and {flipped:P1} upside down");
    }

    public static void AnExportPastAMaterialsDataSaysSo(Harness h)
    {
        h.Section("Export past a material's data: OpticStudio holds its end values, and the export says so");

        // The guide's chapter 7: Si3N4 (Palik) ends at 826.6 nm. Exported to 1100 nm it was silent.
        string design = Path.Combine(Dir(), "past.tfd"), dat = Path.Combine(Dir(), "past.dat");
        File.Delete(dat); File.Delete(dat + ".bak");
        CommandLine.Run(["new", "--materials", "Si3N4,SiO2", "--substrate", "N-BK7", "--reference", "650", "--layers", "2", "-o", design],
                        new StringWriter(), new StringWriter());
        var api = new ThinFilmApi();
        var id = api.Load(File.ReadAllText(design)).Value!.Id;
        var inside = api.ExportZemaxCoating(id, new ZemaxExportRequest(dat, "INSIDE", 450, 800));
        h.Expect(inside.Ok && !inside.Value!.Notes.Any(n => n.Contains("has data only")), "inside every material's data, no note");
        var past = api.ExportZemaxCoating(id, new ZemaxExportRequest(dat, "PAST", 400, 1100));
        h.Expect(past.Ok && past.Value!.Notes.Any(n => n.StartsWith("Si3N4 has data only from 51.7 to 826.6 nm; above 826.6 nm")),
                 $"to 1100 nm, Si3N4's end is named: {string.Join(" | ", past.Value?.Notes ?? [])}");
    }

    public static void TheCliTheMcpAndTheWindow(Harness h)
    {
        h.Section("thinfilm zemax list|import|export, the MCP's three tools, and the export form");

        string dat = WriteFixture("cli.dat");
        string f = Path.Combine(Dir(), "cli.tfd");
        File.Copy(SampleFiles.PathOf("AR-visible"), f, overwrite: true);
        var output = new StringWriter();
        int Run(params string[] args) { output = new StringWriter(); return CommandLine.Run(args, output, new StringWriter()); }

        h.Expect(Run("zemax", "list", dat) == 0 && output.ToString().Contains("AR_3L") && output.ToString().Contains("cannot import: uses a loop"),
                 "zemax list names the stacks and why one cannot be imported");
        h.Expect(Run("zemax", "import", f, "--file", dat, "--coating", "AR_3L") == 0
                 && DesignDocument.FromJson(File.ReadAllText(f)).Layers.Length == 2,
                 "zemax import writes the design back with the coating's layers");
        h.Expect(Run("zemax", "export", f, "--file", dat, "--coating", "AR_3L") == 1
                 && Run("zemax", "export", f, "--file", dat, "--coating", "AR_3L", "--replace") == 0
                 && output.ToString().Contains("replaced COAT AR_3L"),
                 "zemax export refuses an existing name, and --replace replaces it");

        var tools = new Tools();
        var (opened, _) = tools.Call("load_design", new JsonObject { ["path"] = SampleFiles.PathOf("AR-visible") });
        string id = JsonNode.Parse(opened)!["Id"]!.GetValue<string>();
        var (listed, f1) = tools.Call("zemax_coatings", new JsonObject { ["path"] = dat });
        var (imported, f2) = tools.Call("import_zemax_coating", new JsonObject { ["design"] = id, ["path"] = dat, ["coating"] = "AR" });
        var (exported, f3) = tools.Call("export_zemax_coating", new JsonObject
        { ["design"] = id, ["path"] = dat, ["coating"] = "FROM_MCP", ["from_nm"] = 400.0, ["to_nm"] = 700.0 });
        h.Expect(!f1 && !f2 && !f3 && listed.Contains("ETALON") && imported.Contains("Notes")
                 && JsonNode.Parse(exported)!["Coating"]!.GetValue<string>() == "FROM_MCP",
                 "zemax_coatings, import_zemax_coating and export_zemax_coating");

        var w = new DesignWorkspace();
        w.OpenSample("AR-visible");
        var form = w.ZemaxExportForm(dat)!;
        h.Expect(form.Coating == "AR-VISIBLE" && form.From == "420" && form.To == "680" && form.ToRequest(out _) is { Replace: false },
                 $"the export form starts from the design's name and wavelengths ({form.Coating}, {form.From} to {form.To})");
        form.To = "300";
        h.Expect(form.ToRequest(out var why) is null && why!.Contains("range"), $"and refuses a range that runs backwards: {why}");
        form.To = "680";
        h.Expect(w.ExportZemaxCoating(form.ToRequest(out _)!, out _) is not null
                 && w.ExportZemaxCoating(form.ToRequest(out _)!, out bool exists) is null && exists
                 && w.ExportZemaxCoating(form.ToRequest(out _)! with { Replace = true }, out _) is { Replaced: true },
                 $"the workspace says when the coating is already there, and replaces it when asked: {w.Status}");
        h.Expect(w.ImportZemaxCoating(dat, "AR_3L") && w.Layers.Count == 2 && w.Status.Contains("Imported AR_3L"),
                 $"and imports: {w.Status}");
    }
}
