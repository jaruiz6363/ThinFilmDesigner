// DatabaseSaveTests.cs — Save to database: a design's material into the person's own material
// database (ThinFilmApi.SaveToDatabase), from Edit > Materials, the CLI and the MCP server. Every
// check runs with THINFILM_HOME in a folder of its own, never the person's database.

using System.Text.Json.Nodes;
using ThinFilm.Api;
using ThinFilm.Cli;
using ThinFilm.Gui.Core;
using ThinFilm.Mcp;

namespace ThinFilm.Native.Tests;

public static class DatabaseSaveTests
{
    private static readonly MaterialModel Made = new()
    {
        Kind = "SELLMEIER", Coefficients = [2.0, 0.02, 0.5, 0.05], MinMicrons = 0.25, MaxMicrons = 2.0,
        Source = "a test fit",
    };

    public static void ANewMaterialIsMadeFromNothing(Harness h)
    {
        h.Section("New material: a coating material, a substrate or an incident medium, from the dispersion dialog's fields");

        var db = MaterialDatabase.Shipped();
        var w = new DesignWorkspace();
        w.CreateNew(new NewDesignForm(db).ToRequest(out _)!);
        var form = new MaterialsForm(db, w.Materials!);
        int coatings = form.Entries.Count(e => e.Role == MaterialRole.Coating);

        // The guide's silicon: Green 2008, wavelengths in nm, imported as the dialog does.
        string csv;
        using (var s = typeof(DatabaseSaveTests).Assembly.GetManifestResourceStream("Si-Green-2008.csv")!)
        using (var r = new StreamReader(s)) csv = r.ReadToEnd();
        var dispersion = new CoefficientsForm("substrate", new MaterialModel { Kind = "SELLMEIER" }, db) { IsNew = true };
        h.Expect(dispersion.ToModel(out var empty) is null, $"an empty new material is not a model yet ({empty})");
        string said = dispersion.ImportTable(csv, "nm", out bool ok);
        h.Expect(ok && dispersion.Table.Count == 121 && said.Contains("5 lines"),
                 $"the guide's Si file imports 121 rows, its 5 header lines skipped ({said})");
        // THE USER'S IMPORT: the unit box said µm, the header wavelength_nm. Refused; with no
        // unit chosen, the header's is used and said.
        var wrong = new CoefficientsForm("substrate", new MaterialModel { Kind = "SELLMEIER" }, db) { IsNew = true };
        string refused = wrong.ImportTable(csv, "um", out bool took);
        h.Expect(!took && wrong.Table.Count == 0 && refused.Contains("nm"), $"µm against a wavelength_nm header is refused ({refused})");
        string read = wrong.ImportTable(csv, null, out took);
        h.Expect(took && wrong.ImportedUnit == "nm" && read.Contains("header") && Math.Abs(double.Parse(wrong.Table[0].Wavelength, System.Globalization.CultureInfo.InvariantCulture) - 0.25) < 1e-12,
                 $"no unit chosen: the header's nm is used, and said ({read})");
        h.Expect(wrong.ImportTable("500 1.5 0\n600 1.49 0\n", null, out took) is var asks && !took && asks.Contains("choose"),
                 $"a file that names no unit, with none chosen, is refused ({asks})");

        var si = dispersion.ToModel(out var problem);
        h.Expect(si is not null, $"and makes a table ({problem})");
        var (n, k, _) = si!.At(633.0, "Si");
        h.Expect(Math.Abs(n - 3.88) < 0.02 && k > 0.01 && k < 0.03, $"silicon at 633 nm: n {n:F3}, k {k:F4}");

        int sub = form.NewMaterial(MaterialRole.Substrate, "Si", si, out problem);
        h.Expect(sub >= 0 && form.Substrate.Name == "Si" && form.Substrate.Model.Kind == "TABLE",
                 $"New substrate: Si is the substrate ({problem})");
        h.Pin(form.Entries.Count(e => e.Role == MaterialRole.Coating), coatings, "and no coating material is added");

        var zns = new MaterialModel { Kind = "CAUCHY", Coefficients = [2.25, 0.02, 0.0] };
        int c1 = form.NewMaterial(MaterialRole.Coating, "TiO2", zns, out _);
        h.Expect(c1 >= 0 && form.Entries[c1].Material.Name == "TiO2 (2)",
                 $"a new coating material with a name already used is made unique ({form.Entries[c1].Material.Name})");
        h.Expect(form.NewMaterial(MaterialRole.Coating, "  ", zns, out problem) < 0 && problem!.Contains("name"),
                 "and a blank name is refused");

        // Through OK: the design's substrate is silicon, absorbing.
        h.Expect(w.ApplyMaterials(form.CoatingEdits(), form.Incident, form.Substrate), $"OK applies ({w.Status})");
        h.Expect(w.Materials!.Substrate.Name == "Si", "the design is on silicon");
    }

    public static void TheRestOfTheMaterialsWindowFromTheCliAndMcp(Harness h)
    {
        h.Section("Rename, a source on its own, Upgrade constant materials, and each material said: CLI and MCP");

        // Found reproducing the guide's chapter 7 from the command line: none of these could be done,
        // the listings gave names only, and `--rename` was dropped without a word.
        string dir = Path.Combine(Path.GetTempPath(), "thinfilm-materials-window", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string design = Path.Combine(dir, "d.tfd");
        var said = new StringWriter();
        int Run(params string[] args) { said.GetStringBuilder().Clear(); return CommandLine.Run(args, said, said); }
        Run("new", "--materials", "Si3N4,SiO2", "--substrate", "N-BK7", "--reference", "650", "--layers", "2", "-o", design);

        h.Expect(Run("materials", design) == 0 && said.ToString().Contains("measured table, n = 2.0327 at 550 nm, 51.7–826.6 nm; Palik"),
                 "materials lists each with what it is, its range and its source");
        h.Expect(Run("materials", design, "--define", "Si3N4", "--source", "my own film, 2026") == 0, "--define with only --source");
        var api = new ThinFilmApi();
        NamedMaterial Find(string name) =>
            api.MaterialSet(api.Load(File.ReadAllText(design)).Value!.Id).Value!.Materials.Single(m => m.Name == name);
        h.Expect(Find("Si3N4").Model is { Kind: "TABLE", Source: "my own film, 2026" } m && m.Table!.Length == 41,
                 "and the table is kept, only its source changed");
        h.Expect(Run("materials", design, "--rename", "SiO2", "--to", "Silica") == 0 && Find("Silica").Model.Kind == "SELLMEIER",
                 "--rename: SiO2 is now Silica, its layer with it");
        h.Expect(Run("materials", design, "--define", "Silica", "--constant", "1.46") == 0
                 && Run("materials", design, "--rename", "Silica", "--to", "SiO2") == 0
                 && Run("materials", design, "--upgrade") == 0 && said.ToString().Contains("replaced SiO2 (constant n = 1.46 → Sellmeier")
                 && Find("SiO2").Model.Kind == "SELLMEIER",
                 $"--upgrade: a constant SiO2 becomes the database's Sellmeier ({said.ToString().Trim()})");
        h.Expect(Run("materials", design, "--renam", "SiO2", "--to", "Glass") == 2 && said.ToString().Contains("did not use --renam"),
                 $"a misspelt option is said, not dropped: {said.ToString().Trim()}");
        string before = File.ReadAllText(design);
        h.Expect(Run("stack", "reverse", design, "--mediaa") == 2 && File.ReadAllText(design) == before
                 && said.ToString().Contains("nothing was written"),
                 $"and a command that would write the design writes nothing: {said.ToString().Trim()}");

        // MCP: the same, and list_materials says each material in a line unless the whole is asked for.
        string Call(int n, string tool, JsonObject args) =>
            new JsonObject { ["jsonrpc"] = "2.0", ["id"] = n, ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = args } }.ToJsonString();
        var input = new StringReader(string.Join("\n",
            Call(1, "load_design", new JsonObject { ["path"] = design }),
            Call(2, "edit_materials", new JsonObject { ["design"] = "d1", ["rename"] = "Si3N4", ["rename_to"] = "SiN" }),
            Call(3, "define_material", new JsonObject { ["design"] = "d1", ["name"] = "SiN", ["source"] = "renamed" }),
            Call(4, "list_materials", new JsonObject { ["design"] = "d1" }),
            Call(5, "list_materials", new JsonObject { ["design"] = "d1", ["full"] = true }),
            Call(6, "edit_materials", new JsonObject { ["design"] = "d1", ["upgrade"] = true })) + "\n");
        var output = new StringWriter();
        new McpServer().Serve(input, output);
        var replies = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
                            .Select(l => JsonNode.Parse(JsonNode.Parse(l)!["result"]!["content"]![0]!["text"]!.GetValue<string>())!).ToArray();
        var listed = replies[3]["Materials"]!.AsArray();
        h.Expect(listed.Any(x => x!["Name"]!.GetValue<string>() == "SiN" && x["Source"]!.GetValue<string>() == "renamed"
                                 && x["Summary"]!.GetValue<string>().StartsWith("measured table")),
                 "MCP: renamed, given a source, and listed in a line");
        h.Expect(replies[3].ToJsonString().Length < 2000 && replies[4]["Materials"]![0]!["Model"]!["Table"] is not null,
                 $"the line list is short ({replies[3].ToJsonString().Length} characters); full gives the tables");
        h.Expect(replies[5]["Replaced"]!.AsArray().Count == 0, "and upgrade with nothing constant replaces nothing");

        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    public static void AMaterialIsDefinedFromTheCliAndMcp(Harness h)
    {
        h.Section("define a material's dispersion from the CLI and MCP: Edit dispersion and New material");

        string dir = Path.Combine(Path.GetTempPath(), "thinfilm-define-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string design = Path.Combine(dir, "d.tfd"), si = Path.Combine(dir, "Si.csv");
        using (var s = typeof(DatabaseSaveTests).Assembly.GetManifestResourceStream("Si-Green-2008.csv")!)
        using (var f = File.Create(si)) s.CopyTo(f);
        int Run(params string[] args) => CommandLine.Run(args, new StringWriter(), new StringWriter());
        Run("new", "--materials", "Si3N4,SiO2", "--substrate", "N-BK7", "--reference", "650", "--layers", "2", "-o", design);

        // The guide's silicon substrate, and ZnSe from Connolly 1979 / Tatian 1984, C squared.
        h.Expect(Run("materials", design, "--define", "Si", "--as", "substrate", "--table", si, "--table-unit", "nm") == 0,
                 "--define Si --as substrate --table, in nm");
        h.Expect(Run("materials", design, "--define", "ZnSe", "--sellmeier",
                     "4.45813734,0.04034468055,0.467216334,0.1531713896,2.89566290,2221.822369",
                     "--range", "0.54,18.2", "--source", "Connolly 1979; Tatian 1984") == 0,
                 "--define ZnSe --sellmeier, a new coating material");
        var api = new ThinFilmApi();
        var set = api.MaterialSet(api.Load(File.ReadAllText(design)).Value!.Id).Value!;
        h.Expect(set.Substrate.Name == "Si" && set.Substrate.Model.Kind == "TABLE" && set.Substrate.Model.Table!.Length == 121,
                 "the substrate is the 121-row silicon table");
        var znse = set.Materials.Single(m => m.Name == "ZnSe").Model;
        h.Expect(Math.Abs(znse.At(10600, "ZnSe").N - 2.4028) < 1e-3 && Math.Abs(znse.At(632.8, "ZnSe").N - 2.5911) < 1e-3,
                 $"ZnSe: n = {znse.At(10600, "ZnSe").N:F4} at 10.6 µm and {znse.At(632.8, "ZnSe").N:F4} at 632.8 nm, as published");
        h.Expect(Run("materials", design, "--define", "ZnSe", "--cauchy", "2.5,0.05") == 0
                 && api.MaterialSet(api.Load(File.ReadAllText(design)).Value!.Id).Value!.Materials.Count(m => m.Name == "ZnSe") == 1,
                 "a name already in the design is redefined, not added twice");
        // THE USER'S IMPORT: the header says wavelength_nm; read as µm, silicon ran from 250 to 1450 µm.
        h.Expect(Run("materials", design, "--define", "X", "--as", "substrate", "--table", si, "--table-unit", "um") == 1,
                 "a unit that contradicts the file's header (wavelength_nm) is refused");
        h.Expect(Run("materials", design, "--define", "Si", "--as", "substrate", "--table", si) == 0,
                 "and with no unit given, the header's is used");
        string bare = Path.Combine(dir, "bare.txt");
        File.WriteAllText(bare, "500 1.5 0\n600 1.49 0\n");
        h.Expect(Run("materials", design, "--define", "X", "--table", bare) == 1, "a table that names no unit, given none, is refused");
        h.Expect(Run("materials", design, "--define", "X", "--sellmeier", "1,2,3") == 1, "and Sellmeier coefficients not in pairs");

        // MCP, through the same routine.
        string Call(int n, string tool, JsonObject args) =>
            new JsonObject { ["jsonrpc"] = "2.0", ["id"] = n, ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = args } }.ToJsonString();
        var input = new StringReader(string.Join("\n",
            Call(1, "load_design", new JsonObject { ["path"] = design }),
            Call(2, "define_material", new JsonObject { ["design"] = "d1", ["name"] = "Ge film", ["kind"] = "constant", ["coefficients"] = "4.0,0.01" }),
            Call(3, "define_material", new JsonObject { ["design"] = "d1", ["name"] = "Si", ["kind"] = "table", ["table_path"] = bare, ["role"] = "substrate" }),
            Call(4, "list_materials", new JsonObject { ["design"] = "d1" })) + "\n");
        var output = new StringWriter();
        new McpServer().Serve(input, output);
        var replies = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!["result"]!).ToArray();
        h.Expect(replies[1]["isError"]?.GetValue<bool>() != true, "define_material: a constant coating material over MCP");
        h.Expect(replies[2]["isError"]?.GetValue<bool>() == true && replies[2].ToJsonString().Contains("nm or um"),
                 "and a table that names no unit, given none, is a tool error saying so");
        h.Expect(replies[3].ToJsonString().Contains("Ge film"), "list_materials shows the new material");

        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    public static void ADesignsMaterialCanBeSavedForTheNextDesign(Harness h)
    {
        h.Section("Save to database: one material's lines written, everything else in the file kept");

        var was = Environment.GetEnvironmentVariable("THINFILM_HOME");
        var home = Path.Combine(Path.GetTempPath(), "thinfilm-database-tests", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("THINFILM_HOME", home);
        try
        {
            var api = new ThinFilmApi();
            var before = MaterialDatabase.ForPerson(out _);
            string path = before.Path!;
            string text = File.ReadAllText(path);
            h.Expect(path.StartsWith(home, StringComparison.OrdinalIgnoreCase), $"the person's database is in the test's home ({path})");

            // A new material: appended, and every line that was there is still there.
            var saved = api.SaveToDatabase(new NamedMaterial("HfO2 test", Made));
            h.Expect(saved.Ok && !saved.Value!.Replaced && saved.Value.Path == path, $"a new material is saved ({saved.Error})");
            string after = File.ReadAllText(path);
            h.Expect(after.StartsWith(text.TrimEnd(), StringComparison.Ordinal),
                     "the file before it is kept exactly, comments and all");
            var db = MaterialDatabase.ForPerson(out _);
            h.Expect(db.Find("HfO2 test") is { } back && MaterialsForm.SameDefinition(back.Model, Made),
                     "and it reads back with the same definition");
            h.Pin(db.Materials.Count, before.Materials.Count + 1, "one more material");

            // The same name again: refused, unless replace is asked for.
            var again = api.SaveToDatabase(new NamedMaterial("hfo2 TEST", Made with { Coefficients = [2.1, 0.02] }));
            h.Expect(!again.Ok && again.Error!.Contains("already has"), $"a name already there is refused ({again.Error})");
            var replaced = api.SaveToDatabase(new NamedMaterial("HfO2 test", Made with { Coefficients = [2.1, 0.02] }), replace: true);
            h.Expect(replaced.Ok && replaced.Value!.Replaced, "and replaced when asked");
            db = MaterialDatabase.ForPerson(out _);
            h.Expect(db.Find("HfO2 test")!.Model.Coefficients.SequenceEqual(new[] { 2.1, 0.02 })
                     && db.Materials.Count == before.Materials.Count + 1
                     && File.ReadAllText(path).StartsWith(text.TrimEnd(), StringComparison.Ordinal),
                     "in place: the same count, the new coefficients, the rest of the file as it was");

            // A measured table round-trips, k included.
            var table = new MaterialModel { Kind = "TABLE", Table = [[0.4, 2.1, 0.01], [0.6, 2.0, 0.0], [0.8, 1.98, 0.0]] };
            h.Expect(api.SaveToDatabase(new NamedMaterial("Film table", table)).Ok
                     && MaterialDatabase.ForPerson(out _).Find("Film table") is { } t && MaterialsForm.SameDefinition(t.Model, table),
                     "a table of n and k is saved and reads back the same");

            // What is never saved.
            h.Expect(!api.SaveToDatabase(new NamedMaterial("SCHOTT N-BK7", MaterialModel.CatalogueGlass("SCHOTT", "N-BK7"))).Ok,
                     "a catalogue glass is refused");
            h.Expect(!api.SaveToDatabase(MaterialDatabase.Air).Ok, "and air");
            h.Expect(!api.SaveToDatabase(new NamedMaterial("a # b", Made)).Ok, "and a name with #, which starts a comment");
            h.Expect(MaterialDatabase.Save(MaterialDatabase.ShippedPath, new NamedMaterial("X", Made), false, out var shipped) is null
                     && shipped!.Contains("ships with the program"),
                     $"the shipped database is never written ({shipped})");

            // Edit > Materials: the copy as the dialog holds it, and the list on the left read again.
            var w = new DesignWorkspace();
            w.CreateNew(new NewDesignForm(MaterialDatabase.ForPerson(out _)).ToRequest(out _)!);
            var form = new MaterialsForm(MaterialDatabase.ForPerson(out _), w.Materials!);
            int sio2 = form.Entries.FindIndex(e => e.Material.Name == "SiO2");
            h.Expect(form.SetDefinition(sio2, "SiO2 film", Made) is null, "the design's SiO2 edited and renamed in the dialog");
            h.Expect(form.SaveWouldReplace(sio2) is null && form.SaveRefusal(sio2) is null, "no SiO2 film in the database yet");
            h.Expect(form.SaveToDatabase(sio2, false, out var problem) is { } said && said.Contains("saved to"),
                     $"Save to database from the dialog ({problem})");
            h.Expect(form.Database.Find("SiO2 film") is not null && form.Offered.Any(m => m.Name == "SiO2 film"),
                     "and the database list offers it at once");
            int substrate = form.Entries.FindIndex(e => e.Role == MaterialRole.Substrate);
            h.Expect(form.SaveRefusal(substrate) is { } glass && glass.Contains("catalogue glass"),
                     "the dialog says a catalogue glass substrate cannot be saved");

            // The CLI and the MCP server, through the same routine, by the design's material name.
            string design = Path.Combine(home, "design.tfd");
            var o = new StringWriter(); var e = new StringWriter();
            CommandLine.Run(["new", "--materials", "TiO2,SiO2", "--substrate", "N-BK7", "--reference", "550", "--layers", "2", "-o", design], o, e);
            CommandLine.Run(["materials", design, "--replace", "TiO2", "--with", "HfO2 test"], o, e);
            o = new StringWriter(); e = new StringWriter();
            int code = CommandLine.Run(["database", "--save", design, "--material", "HfO2 test"], o, e);
            h.Expect(code == 1 && e.ToString().Contains("--replace"), $"the CLI refuses a name already there and says how ({e.ToString().Trim()})");
            o = new StringWriter(); e = new StringWriter();
            code = CommandLine.Run(["database", "--save", design, "--material", "HfO2 test", "--replace"], o, e);
            h.Expect(code == 0 && o.ToString().Contains("replaced in"), $"and replaces with --replace ({o.ToString().Trim()}{e})");

            string Call(int n, string tool, JsonObject args) =>
                new JsonObject { ["jsonrpc"] = "2.0", ["id"] = n, ["method"] = "tools/call",
                    ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = args } }.ToJsonString();
            var input = new StringReader(string.Join("\n",
                Call(1, "load_design", new JsonObject { ["path"] = design }),
                Call(2, "save_material_to_database", new JsonObject { ["design"] = "d1", ["material"] = "SiO2", ["replace"] = true })) + "\n");
            var output = new StringWriter();
            new McpServer().Serve(input, output);
            var last = JsonNode.Parse(output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1])!["result"]!;
            h.Expect(last["isError"]?.GetValue<bool>() != true
                     && JsonNode.Parse(last["content"]![0]!["text"]!.GetValue<string>())!["Replaced"]!.GetValue<bool>(),
                     $"save_material_to_database over MCP ({last.ToJsonString()[..Math.Min(200, last.ToJsonString().Length)]})");
        }
        finally
        {
            Environment.SetEnvironmentVariable("THINFILM_HOME", was);
            try { Directory.Delete(home, true); } catch (IOException) { }
        }
    }
}
