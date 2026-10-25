// MeritFileTests.cs — the merit function saved on its own, opened into a design, exported as CSV.
//
// The user (2026-10-19): "Under merit function table we need a way to settings only: save a merit
// function table, save as a merit function table, and open a merit function table. We also need a
// way to export a merit function table as CSV with values exported."

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ThinFilm.Api;
using ThinFilm.Cli;
using ThinFilm.Gui.Core;
using ThinFilm.Mcp;

namespace ThinFilm.Native.Tests;

public static class MeritFileTests
{
    private static string Operands(DesignDocument d) => JsonSerializer.Serialize(d.Operands)
                                                        + JsonSerializer.Serialize(d.Points)
                                                        + JsonSerializer.Serialize(d.Wavelength);

    public static void AMeritFileRoundTrips(Harness h)
    {
        h.Section("Save merit function, clear, Open merit function: the same table and the same merit");

        var api = new ThinFilmApi();
        string id = api.OpenSample("AR-visible").Value!.Id;
        var start = api.Document(id).Value!;
        double merit = api.Describe(id).Value!.Merit;
        var saved = api.SaveMeritFunction(id);
        h.Expect(saved.Ok && saved.Value!.Contains("\"Kind\": \"ThinFilm merit function\"") && !saved.Value.Contains("\"Layers\""),
                 "the file says what it is and holds no layers");
        api.ClearOperands(id);
        var opened = api.OpenMeritFunction(id, saved.Value!);
        var back = api.Document(id).Value!;
        h.Expect(opened.Ok && opened.Value!.Rows == start.Operands.Length, $"every row comes back ({opened.Error})");
        h.Expect(Operands(back) == Operands(start), "the rows, their points and the grid are as they were");
        h.Expect(api.Describe(id).Value!.Merit == merit, $"and the merit is the same ({merit:G6})");
    }

    public static void AMeritFileOpensIntoAnotherDesign(Harness h)
    {
        h.Section("a merit file from one design opened into another: its rows, that design's layers");

        var api = new ThinFilmApi();
        var names = SampleFiles.Names;
        string a = api.OpenSample("AR-visible").Value!.Id;
        string other = names.First(n => n != "AR-visible");
        string b = api.OpenSample(other).Value!.Id;
        var layersBefore = JsonSerializer.Serialize(api.Document(b).Value!.Layers);
        int rowsBefore = api.Document(b).Value!.Operands.Length;
        var r = api.OpenMeritFunction(b, api.SaveMeritFunction(a).Value!);
        var docA = api.Document(a).Value!;
        var docB = api.Document(b).Value!;
        h.Expect(r.Ok && docB.Operands.Length == docA.Operands.Length,
                 $"{other} takes AR-visible's {docA.Operands.Length} rows in place of its {rowsBefore} ({r.Error})");
        h.Expect(JsonSerializer.Serialize(docB.Layers) == layersBefore, "its layers are untouched");
        h.Expect(docB.Wavelength.SequenceEqual(docA.Wavelength), "and its grid is rebuilt from the rows");

        var design = api.Save(a).Value!;
        var refused = api.OpenMeritFunction(b, design);
        h.Expect(!refused.Ok && refused.Error!.Contains("not a merit function file")
                 && api.Document(b).Value!.Operands.Length == docA.Operands.Length,
                 $"a design file is refused, and the table is left as it was ({refused.Error})");
        h.Expect(!api.OpenMeritFunction(b, "{ not json").Ok, "so is text that is not JSON");
    }

    public static void AppendAddsAfterTheTable(Harness h)
    {
        h.Section("Append merit function: the file's rows after the table's own, in order");

        var api = new ThinFilmApi();
        string a = api.OpenSample("AR-visible").Value!.Id;
        string other = SampleFiles.Names.First(n => n != "AR-visible");
        string b = api.OpenSample(other).Value!.Id;
        var own = api.Operands(b).Value!;
        var theirs = api.Operands(a).Value!;
        var r = api.OpenMeritFunction(b, api.SaveMeritFunction(a).Value!, append: true);
        var now = api.Operands(b).Value!;
        string J(object o) => JsonSerializer.Serialize(o);
        h.Expect(r.Ok && r.Value!.Rows == theirs.Length && now.Length == own.Length + theirs.Length,
                 $"{own.Length} + {theirs.Length} rows ({r.Error})");
        h.Expect(J(now.Take(own.Length)) == J(own) && J(now.Skip(own.Length)) == J(theirs),
                 "its own rows first, unchanged, then the file's, as they were");
        h.Expect(!api.OpenMeritFunction(b, "{ not json", append: true).Ok && api.Operands(b).Value!.Length == now.Length,
                 "a bad file appends nothing");

        var w = new DesignWorkspace();
        w.OpenSample("AR-visible");
        int n = w.MeritRows.Count;
        h.Expect(w.OpenMeritFunction(w.SaveMeritFunction()!, "ar.tfm", append: true) && w.MeritRows.Count == 2 * n
                 && w.Status.Contains("appended"),
                 $"the workspace appends and says so ({w.Status})");
    }

    public static void TheCsvHasTheValues(Harness h)
    {
        h.Section("Export as CSV: the table's columns, and each row's value and residual");

        var api = new ThinFilmApi();
        string id = api.OpenSample("AR-visible").Value!.Id;
        var csv = api.ExportMeritCsv(id);
        var lines = csv.Value!.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var eval = api.Evaluate(id).Value!;
        h.Expect(csv.Ok && lines[0] == "#,Code,Wv (nm),Wv2 (nm),Pts,AOI (deg),Mode,Target,Target 2,Limit (nm),Weight,Value,Residual,Flags",
                 $"a header with the table's columns ({lines[0]})");
        h.Expect(lines.Length == eval.Operands.Length + 1, $"one line per row ({lines.Length - 1})");
        var cells = lines[1].Split(',');
        h.Expect(cells[0] == "1" && cells[1] == eval.Operands[0].Code
                 && double.Parse(cells[11], CultureInfo.InvariantCulture) == eval.Operands[0].Value
                 && double.Parse(cells[12], CultureInfo.InvariantCulture) == eval.Operands[0].Residual,
                 $"the value and residual are the evaluation's, exactly ({lines[1]})");

        api.SetWavelengthUnit(id, "um");
        var um = api.ExportMeritCsv(id).Value!.Split('\n')[1].Split(',');
        h.Expect(api.ExportMeritCsv(id).Value!.StartsWith("#,Code,Wv (um)")
                 && Math.Abs(double.Parse(um[2], CultureInfo.InvariantCulture) * 1000 - double.Parse(cells[2], CultureInfo.InvariantCulture)) < 1e-9,
                 "in a design shown in µm, wavelengths are in µm");

        var w = new DesignWorkspace();
        w.OpenSample("AR-visible");
        string? file = w.SaveMeritFunction();
        w.ClearOperands();
        h.Expect(file is not null && w.OpenMeritFunction(file, "ar.tfm") && w.MeritRows.Count == eval.Operands.Length
                 && w.Status.Contains("ar.tfm"),
                 $"the workspace saves and opens it and says so ({w.Status})");
    }

    public static void TheCliAndTheMcpToo(Harness h)
    {
        h.Section("thinfilm merit save|open|export, and the MCP's save_merit_function, open_merit_function, export_merit_csv");

        var dir = Path.Combine(Path.GetTempPath(), "thinfilm-merit-file-tests");
        Directory.CreateDirectory(dir);
        var api = new ThinFilmApi();
        string a = Path.Combine(dir, "a.tfd"), b = Path.Combine(dir, "b.tfd"), m = Path.Combine(dir, "m.tfm"), c = Path.Combine(dir, "m.csv");
        File.WriteAllText(a, api.Save(api.OpenSample("AR-visible").Value!.Id).Value!);
        string other = SampleFiles.Names.First(n => n != "AR-visible");
        File.WriteAllText(b, api.Save(api.OpenSample(other).Value!.Id).Value!);
        int Run(params string[] args) => CommandLine.Run(args, new StringWriter(), new StringWriter());

        h.Expect(Run("merit", "save", a, "-o", m) == 0 && File.ReadAllText(m).Contains("ThinFilm merit function"),
                 "merit save writes the merit file");
        h.Expect(Run("merit", "open", b, "--file", m) == 0
                 && DesignDocument.FromJson(File.ReadAllText(b)).Operands.Length == DesignDocument.FromJson(File.ReadAllText(a)).Operands.Length,
                 "merit open puts its rows into another design and writes it back");
        h.Expect(Run("merit", "export", a, "-o", c) == 0 && File.ReadAllText(c).StartsWith("#,Code,"), "merit export writes the CSV");
        h.Expect(Run("merit", "open", b) != 0, "merit open without --file is refused");
        int before = DesignDocument.FromJson(File.ReadAllText(b)).Operands.Length;
        h.Expect(Run("merit", "open", b, "--file", m, "--append") == 0
                 && DesignDocument.FromJson(File.ReadAllText(b)).Operands.Length == 2 * before,
                 "merit open --append adds the rows after the table's own");

        var tools = new Tools();
        var (text, _) = tools.Call("load_design", new JsonObject { ["document"] = File.ReadAllText(a) });
        string id = JsonNode.Parse(text)!["Id"]!.GetValue<string>();
        var (saved, f1) = tools.Call("save_merit_function", new JsonObject { ["design"] = id });
        var (csv, f2) = tools.Call("export_merit_csv", new JsonObject { ["design"] = id });
        h.Expect(!f1 && saved.Contains("ThinFilm merit function") && !f2 && csv.Contains("#,Code,"),
                 "save_merit_function and export_merit_csv answer with the text");
        string fileText = JsonNode.Parse(saved) is JsonValue v ? v.GetValue<string>() : saved;
        var (_, f3) = tools.Call("open_merit_function", new JsonObject { ["design"] = id, ["document"] = fileText });
        h.Expect(!f3, "and open_merit_function takes it back");
        int rows = api.Operands(api.Load(File.ReadAllText(a)).Value!.Id).Value!.Length;
        var (appended, f4) = tools.Call("open_merit_function", new JsonObject
        {
            ["design"] = id, ["document"] = fileText, ["append"] = true,
        });
        h.Expect(!f4 && JsonNode.Parse(appended)!["Design"]!["Operands"]!.GetValue<int>() == 2 * rows,
                 $"with append it adds them ({appended[..Math.Min(80, appended.Length)]})");
    }
}
