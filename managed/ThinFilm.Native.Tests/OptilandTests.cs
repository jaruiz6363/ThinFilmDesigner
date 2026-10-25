// OptilandTests.cs — Optiland's thin-film coatings (OptilandCoating.cs): reading what Optiland
// writes, and writing what it reads.
//
// The user (2026-10-21): "It would be very nice to support Optiland", then "Can we continue with
// Optiland import/export?"
//
// The fixtures are what Optiland itself wrote (save_obj_to_json on a ThinFilmCoating, and
// save_optiland_file on a lens with one, from upstream optiland 2026-10-14), cut down. That
// Optiland computes the same R, T and A for an export as this program does - 1e-10, s and p, 0 and
// 30 degrees, with an absorbing Nb2O5 layer - was checked by loading one in Optiland itself; these
// tests check the format, which is what can be checked without it.

using System.Text.Json.Nodes;
using ThinFilm.Api;
using ThinFilm.Cli;
using ThinFilm.Mcp;

namespace ThinFilm.Native.Tests;

public static class OptilandTests
{
    /// <summary>A ThinFilmCoating as Optiland's save_obj_to_json writes it.</summary>
    private const string Coating = """
    {
        "type": "ThinFilmCoating",
        "material_pre": {"type": "IdealMaterial", "propagation_model": {"class": "HomogeneousPropagation"}, "index": 1.0, "absorp": 0.0},
        "material_post": {"type": "Material", "propagation_model": {"class": "HomogeneousPropagation"},
                          "filename": "C:\\nowhere\\N-BK7.yml", "bounds": "clamp", "name": "N-BK7", "reference": "SCHOTT",
                          "catalog": null, "match_policy": "warn", "robust_search": null, "min_wavelength": null, "max_wavelength": null},
        "layers": [
            {"material": {"type": "DataMaterial", "propagation_model": {"class": "HomogeneousPropagation"}, "name": "SiO2-Malitson",
                          "definition": {"dispersion": {"kind": "formula", "formula": "formula 2",
                                                        "coefficients": [0.0, 0.6961663, 0.0046791, 0.4079426, 0.0135121, 0.8974794, 97.934],
                                                        "wavelength_range_um": null},
                                         "extinction": null, "thermal": null},
                          "bounds": "raise", "metadata": {}},
             "thickness_nm": 94.0, "name": "L1"},
            {"material": {"type": "DataMaterial", "propagation_model": {"class": "HomogeneousPropagation"}, "name": "TiO2-ebeam",
                          "definition": {"dispersion": {"kind": "tabulated", "wavelengths_um": [0.4, 0.5, 0.7], "indices": [2.45, 2.35, 2.28]},
                                         "extinction": {"kind": "tabulated_k", "wavelengths_um": [0.4, 0.7], "values": [0.002, 0.0]},
                                         "thermal": null},
                          "bounds": "clamp", "metadata": {}},
             "thickness_nm": 57.0, "name": "H1"},
            {"material": {"type": "IdealMaterial", "propagation_model": {"class": "HomogeneousPropagation"}, "index": 1.38, "absorp": 0.0},
             "thickness_nm": 99.6, "name": "M"}
        ]
    }
    """;

    private static string Dir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "thinfilm-optiland-tests");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string Write(string name, string text)
    {
        string path = Path.Combine(Dir(), name);
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>A lens file as save_optiland_file writes one: Infinity and NaN included, and the
    /// coating on surface 1's interaction model.</summary>
    private static string Lens() => """
    {"version": 1.0, "name": null,
     "surface_group": {"surfaces": [
        {"type": "ObjectSurface", "thickness": Infinity, "geometry": {"cs": {"z": -Infinity, "rx": NaN}},
         "material_post": {"type": "IdealMaterial", "propagation_model": {"class": "HomogeneousPropagation"}, "index": 1.0, "absorp": 0.0},
         "comment": "", "interaction_model": {"type": "RefractiveReflectiveModel", "is_reflective": false, "coating": null, "bsdf": null}},
        {"type": "Surface", "thickness": 5.0, "comment": "front",
         "interaction_model": {"type": "RefractiveReflectiveModel", "is_reflective": false, "coating": COATING, "bsdf": null}},
        {"type": "Surface", "thickness": 40.0, "comment": "",
         "interaction_model": {"type": "RefractiveReflectiveModel", "is_reflective": false,
                               "coating": {"type": "FresnelCoating"}, "bsdf": null}}
     ]}}
    """.Replace("COATING", Coating);

    public static void OptilandsOwnFilesAreRead(Harness h)
    {
        h.Section("Optiland's coating and lens files: listed, read, and their materials taken as what they are");

        string coating = Write("coating.json", Coating), lens = Write("lens.json", Lens());
        var one = ThinFilmApi.OptilandCoatings(coating).Value!;
        var many = ThinFilmApi.OptilandCoatings(lens).Value!;
        h.Expect(one is [{ Name: "coating", Layers: 3, Problem: null }],
                 "a coating file is one coating of three layers");
        h.Expect(many is [{ Name: "surface 1 (front)", Layers: 3, Problem: null }],
                 $"a lens file - with Infinity and NaN in it - lists its one thin-film surface, not the Fresnel one: {string.Join(", ", many.Select(c => c.Name))}");

        var api = new ThinFilmApi();
        string id = api.OpenSample("AR-visible").Value!.Id;
        var before = api.Document(id).Value!;
        var r = api.ImportOptilandCoating(id, lens, 0);
        var doc = api.Document(id).Value!;
        MaterialModel Model(string name) => doc.Materials.Single(m => m.Name == name).Model!;
        h.Expect(r.Ok && doc.Layers.Select(l => l.Thickness).SequenceEqual([94.0, 57.0, 99.6])
                 && doc.Operands.Length == before.Operands.Length && doc.Exit.Name == before.Exit.Name,
                 "94, 57 and 99.6 nm, incident side first; the merit function and the substrate are kept");
        h.Expect(Model("SiO2-Malitson") is { Kind: "SELLMEIER", Coefficients: [0.6961663, 0.0046791, 0.4079426, 0.0135121, 0.8974794, 97.934] },
                 "formula 2 is our Sellmeier, coefficient for coefficient");
        var tio2 = Model("TiO2-ebeam");
        h.Expect(tio2.Kind == "TABLE" && tio2.Table!.Length == 3 && tio2.Table[0][2] == 0.002 && Math.Abs(tio2.Table[1][2] - 0.002 * 2 / 3) < 1e-12,
                 "an n table and a k table on their own grids become one table, k interpolated onto n's rows");
        h.Expect(Model("n=1.38") is { Kind: "CONSTANT", Coefficients: [1.38, 0.0] }, "an IdealMaterial is a constant");
        h.Expect(r.Value!.Notes.Any(n => n.Contains("air and N-BK7") && n.Contains("keeps its own")),
                 "and the coating's own media are named and not used");

        var bad = Coating.Replace("\"type\": \"IdealMaterial\", \"propagation_model\": {\"class\": \"HomogeneousPropagation\"}, \"index\": 1.38, \"absorp\": 0.0",
                                  "\"type\": \"AbbeMaterial\", \"propagation_model\": {\"class\": \"HomogeneousPropagation\"}, \"index\": 1.5, \"abbe\": 60, \"model\": \"buchdahl\"");
        var abbe = ThinFilmApi.OptilandCoatings(Write("abbe.json", bad)).Value!;
        h.Expect(abbe[0].Problem!.Contains("fit to two numbers"), $"an AbbeMaterial layer is refused, saying why: {abbe[0].Problem}");
        h.Expect(ThinFilmApi.OptilandCoatings(Write("none.json", "{\"version\": 1.0, \"surface_group\": {\"surfaces\": []}}")) is { Ok: false } e
                 && e.Error!.Contains("no thin-film coating"), "a lens with no thin-film coating says so");
    }

    public static void DatabaseMaterialsAreReadFromTheirFiles(Harness h)
    {
        h.Section("An Optiland database material: through the refractiveindex.info file it names, when it is here");

        string yml = Write("N-BK7.yml", """
            # this file is part of refractiveindex.info database
            REFERENCES: "SCHOTT Zemax catalog 2017-01-20b"
            DATA:
              - type: formula 2
                wavelength_range: 0.3 2.5
                coefficients: 0 1.03961212 0.00600069867 0.231792344 0.0200179144 1.01046945 103.560653
            SPECS:
                n_is_absolute: false
            """);
        var entries = OptilandCoating.ReadYaml(File.ReadAllText(yml));
        h.Expect(entries is [{ Type: "formula 2", Range: [0.3, 2.5] }] && entries[0].Coefficients.Length == 7,
                 "the YAML's DATA: formula 2, its range and seven coefficients");

        var material = (JsonObject)JsonNode.Parse(
            $$"""{"type": "Material", "filename": {{System.Text.Json.JsonSerializer.Serialize(yml)}}, "name": "N-BK7", "reference": "SCHOTT"}""")!;
        var read = OptilandCoating.FromOptiland(material, (0.4, 0.8));
        double n = read.Model!.At(587.5618, "N-BK7").N;
        h.Expect(read.Model is { Kind: "SELLMEIER", MinMicrons: 0.3, MaxMicrons: 2.5 } && Math.Abs(n - 1.5168) < 1e-4,
                 $"N-BK7 is the Sellmeier its file gives, valid 0.3 to 2.5 µm: nd = {n:F5}");

        File.AppendAllText(yml, "");
        var withK = Write("k.yml", """
            DATA:
              - type: formula 2
                coefficients: 0 1.03961212 0.00600069867 0.231792344 0.0200179144 1.01046945 103.560653
              - type: tabulated k
                data: |
                    0.400 1.0E-03
                    0.800 3.0E-03
            """);
        var k = OptilandCoating.FromOptiland((JsonObject)JsonNode.Parse(
            $$"""{"type": "MaterialFile", "filename": {{System.Text.Json.JsonSerializer.Serialize(withK)}}}""")!, (0.4, 0.8));
        h.Expect(k.Model is { Kind: "TABLE" } && Math.Abs(k.Model.At(600, "k").K - 0.002) < 1e-12 && k.Note!.Contains("measured k"),
                 $"a formula with a measured k becomes a table of both, with a note: {k.Note}");

        var missing = OptilandCoating.FromOptiland((JsonObject)JsonNode.Parse(
            """{"type": "Material", "filename": "C:\\elsewhere\\TiO2.yml", "name": "TiO2", "reference": "Zhukovsky"}""")!, (0.4, 0.8));
        h.Expect(missing.Problem!.Contains("TiO2 (Zhukovsky)") && missing.Problem.Contains("not on this computer"),
                 $"and refused when its file is not here: {missing.Problem}");
    }

    public static void EveryFormulaIsOptilandsAlgebra(Harness h)
    {
        h.Section("refractiveindex.info's formulas as Optiland evaluates them, and ours as them");

        double w = 0.55;
        double[] sell = [0.6961663, 0.0046791, 0.4079426, 0.0135121, 0.8974794, 97.934];
        var ours = new MaterialModel { Kind = "SELLMEIER", Coefficients = sell };
        h.Expect(Math.Abs(OptilandCoating.Formula("formula 2", [0, .. sell], w) - ours.At(550, "SiO2").N) < 1e-15
                 && Math.Abs(OptilandCoating.Formula("formula 1", [0, sell[0], Math.Sqrt(sell[1]), sell[2], Math.Sqrt(sell[3]), sell[4], Math.Sqrt(sell[5])], w)
                             - ours.At(550, "SiO2").N) < 1e-14,
                 "formula 2 is our Sellmeier; formula 1 is it with the resonances' square roots");
        var cauchy = new MaterialModel { Kind = "CAUCHY", Coefficients = [2.2, 0.03, 0.003] };
        h.Expect(Math.Abs(OptilandCoating.Formula("formula 5", [2.2, 0.03, -2, 0.003, -4], w) - cauchy.At(550, "TiO2").N) < 1e-15
                 && OptilandCoating.ModelOfFormula("formula 5", [2.2, 0.03, -2, 0.003, -4]) is { Kind: "CAUCHY", Coefficients: [2.2, 0.03, 0.003] }
                 && OptilandCoating.ModelOfFormula("formula 5", [1.0, 0.5, -1]) is null
                 && OptilandCoating.ModelOfFormula("formula 2", [0.2, 1.0, 0.01]) is { Kind: "SELLMEIER", Coefficients: [0.2, 0.0, 1.0, 0.01] },
                 "formula 5 is our Cauchy when its powers are -2 and -4; a constant term in formula 2 is a Sellmeier term with no resonance");

        foreach (var g in new[] { ("SCHOTT", "N-BK7"), ("SCHOTT", "N-SF6"), ("OHARA", "S-LAH64") })
        {
            if (GlassCatalogs.Find(g.Item1, g.Item2) is not { } glass) continue;
            var f = OptilandCoating.FormulaOfGlass(glass.Formula, glass.Coefficients)!.Value;
            double diff = Math.Abs(OptilandCoating.Formula(f.Formula, f.Coefficients, w) - glass.At(550).N);
            h.Expect(diff < 1e-14, $"{glass.FullName}, AGF formula {glass.Formula}, as {f.Formula}: {diff:G2}");
        }

        // A formula with no model of ours is tabulated, finely.
        var table = OptilandCoating.FromOptiland((JsonObject)JsonNode.Parse("""
            {"type": "DataMaterial", "name": "F4", "bounds": "clamp", "metadata": {},
                "definition": {"dispersion": {"kind": "formula", "formula": "formula 3",
                                              "coefficients": [2.27, -0.01, 2, 0.011, -2, 0.0001, -4], "wavelength_range_um": [0.4, 0.8]},
                               "extinction": null, "thermal": null}}
            """)!.AsObject(), (0.4, 0.8));
        double worst = 0;
        for (double um = 0.4; um <= 0.8; um += 0.0013)
            worst = Math.Max(worst, Math.Abs(table.Model!.At(um * 1000, "F4").N - OptilandCoating.Formula("formula 3", [2.27, -0.01, 2, 0.011, -2, 0.0001, -4], um)));
        h.Expect(table.Model!.Kind == "TABLE" && worst < 1.1e-6 && table.Note!.Contains("no model for formula 3"),
                 $"formula 3 (here, not a glass) becomes a table within {worst:G2}, with a note");
    }

    public static void WhatIsWrittenIsWhatOptilandReads(Harness h)
    {
        h.Section("Export: a ThinFilmCoating Optiland loads, materials as equations, and back to the same design");

        var api = new ThinFilmApi();
        string f = Path.Combine(Dir(), "dispersive.tfd");
        CommandLine.Run(["new", "--materials", "TiO2,SiO2,Nb2O5", "--substrate", "SCHOTT N-BK7", "--layers", "4", "--name", "DISP", "-o", f],
                        new StringWriter(), new StringWriter());
        CommandLine.Run(["layer", "set", f, "--layer", "3", "--material", "Nb2O5"], new StringWriter(), new StringWriter());
        string id = api.Load(File.ReadAllText(f)).Value!.Id;
        var doc = api.Document(id).Value!;
        double merit = api.Describe(id).Value!.Merit;

        string path = Path.Combine(Dir(), "out.json");
        var r = api.ExportOptilandCoating(id, path);
        var c = (JsonObject)OptilandCoating.Parse(File.ReadAllText(path));
        var layers = (JsonArray)c["layers"]!;
        JsonNode Disp(int i) => layers[i]!["material"]!["definition"]!["dispersion"]!;
        h.Expect(r.Ok && c["type"]!.GetValue<string>() == "ThinFilmCoating" && layers.Count == 4
                 && layers.Select(l => l!["thickness_nm"]!.GetValue<double>()).SequenceEqual(doc.Layers.Select(l => l.Thickness)),
                 "a ThinFilmCoating with the four layers, incident side first, in nm");
        h.Expect(Disp(0)["formula"]!.GetValue<string>() == "formula 5"
                 && Disp(0)["coefficients"]!.AsArray().Select(x => x!.GetValue<double>()).SequenceEqual([2.2, 0.03, -2, 0.003, -4])
                 && Disp(1)["formula"]!.GetValue<string>() == "formula 2" && Disp(1)["coefficients"]![0]!.GetValue<double>() == 0.0,
                 "TiO2's Cauchy as formula 5, SiO2's Sellmeier as formula 2");
        var nb = layers[2]!["material"]!["definition"]!;
        h.Expect(nb["dispersion"]!["kind"]!.GetValue<string>() == "tabulated" && nb["extinction"]!["kind"]!.GetValue<string>() == "tabulated_k"
                 && nb["extinction"]!["values"]!.AsArray().All(v => v!.GetValue<double>() >= 0) && layers[2]!["material"]!["bounds"]!.GetValue<string>() == "clamp",
                 "Nb2O5's measured table as n and k tables, k positive, held at its ends");
        h.Expect(c["material_pre"]!["type"]!.GetValue<string>() == "IdealMaterial"
                 && c["material_post"]!["name"]!.GetValue<string>() == "N-BK7" && c["material_post"]!["definition"]!["dispersion"]!["formula"]!.GetValue<string>() == "formula 2"
                 && c["material_post"]!["metadata"]!["thinfilm"]!["catalog"]!.GetValue<string>() == "SCHOTT"
                 && c["thinfilm"]!["reference_nm"]!.GetValue<double>() == doc.ReferenceWavelength,
                 "air as an IdealMaterial, N-BK7 as its Sellmeier with the catalogue named, and the reference wavelength beside");

        var back = api.ImportOptilandCoating(id, path, 0);
        var got = api.Document(id).Value!;
        h.Expect(back.Ok && got.Materials.Length == doc.Materials.Length
                 && got.Materials.Zip(doc.Materials).All(p => p.First.Model!.Kind == p.Second.Model!.Kind)
                 && Math.Abs(api.Describe(id).Value!.Merit - merit) <= 1e-12 * Math.Max(1, merit),
                 $"imported into the design it came from: the same materials and the merit ({merit:G10})");
    }

    public static void TheCliAndTheMcpToo(Harness h)
    {
        h.Section("thinfilm optiland list|import|export, and the MCP's three tools");

        string lens = Write("cli-lens.json", Lens());
        string f = Path.Combine(Dir(), "cli.tfd");
        File.Copy(SampleFiles.PathOf("AR-visible"), f, overwrite: true);
        var output = new StringWriter();
        int Run(params string[] args) { output = new StringWriter(); return CommandLine.Run(args, output, new StringWriter()); }
        h.Expect(Run("optiland", "list", lens) == 0 && output.ToString().Contains("surface 1 (front)"), "optiland list");
        h.Expect(Run("optiland", "import", f, "--file", lens) == 0 && DesignDocument.FromJson(File.ReadAllText(f)).Layers.Length == 3,
                 "optiland import writes the design back with the coating's layers");
        string json = Path.Combine(Dir(), "cli-out.json");
        h.Expect(Run("optiland", "export", f, "--file", json) == 0 && File.Exists(json) && output.ToString().Contains("load_obj_from_json"),
                 "optiland export");

        var tools = new Tools();
        var (opened, _) = tools.Call("load_design", new JsonObject { ["path"] = SampleFiles.PathOf("AR-visible") });
        string id = JsonNode.Parse(opened)!["Id"]!.GetValue<string>();
        string mcp = Path.Combine(Dir(), "mcp.json");
        var (listed, f1) = tools.Call("optiland_coatings", new JsonObject { ["path"] = lens });
        var (imported, f2) = tools.Call("import_optiland_coating", new JsonObject { ["design"] = id, ["path"] = lens, ["coating"] = 1.0 });
        var (exported, f3) = tools.Call("export_optiland_coating", new JsonObject { ["design"] = id, ["path"] = mcp });
        h.Expect(!f1 && !f2 && !f3 && listed.Contains("surface 1") && imported.Contains("Notes") && File.Exists(mcp),
                 $"optiland_coatings, import_optiland_coating and export_optiland_coating ({exported[..Math.Min(60, exported.Length)]})");
    }
}
