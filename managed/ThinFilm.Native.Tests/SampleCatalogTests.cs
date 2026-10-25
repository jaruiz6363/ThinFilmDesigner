// SampleCatalogTests.cs — the samples as a catalogue: Examples and the designs from the literature,
// by category and paper, as File > Open sample, `thinfilm samples` and list_samples show them.
//
// The user (2026-10-22), after the macOS package showed no samples: "I think we need to install
// samples and the Literature files. It would be good to have sub folders in each." Then, to the
// proposed folders, categories and sample browser: "Yes please go ahead".

using System.Text.Json.Nodes;
using ThinFilm.Api;
using ThinFilm.Cli;
using ThinFilm.Gui.Core;
using ThinFilm.Mcp;

namespace ThinFilm.Native.Tests;

public static class SampleCatalogTests
{
    public static void TheCatalogGroupsAndNarrows(Harness h)
    {
        h.Section("the catalogue: Examples first, then the literature by category and paper; words narrow it");

        var all = Samples.Catalog();
        h.Pin(all.Count, Samples.Files.Count, "every sample file is in it");
        var guide = all.Skip(10).TakeWhile(e => e.Group.StartsWith("User guide/", StringComparison.Ordinal)).ToArray();
        h.Expect(all.Take(10).All(e => e.Group.StartsWith("Examples/", StringComparison.Ordinal))
                 && guide.Length == 25
                 && all.Skip(10 + guide.Length).All(e => e.Group.StartsWith("Literature/", StringComparison.Ordinal)),
                 $"the ten examples come first, then the user guide's {guide.Length} designs, then the literature");
        h.Expect(guide.Select(e => e.Group.Split('/')[1]).Distinct().Count() == 7
                 && guide.All(e => e.Source?.StartsWith("User guide, chapter ", StringComparison.Ordinal) == true),
                 "the guide's designs are in a folder per chapter, each naming its chapter");
        var loader = new ThinFilmApi();
        h.Expect(guide.All(e => loader.Load(File.ReadAllText(e.Path)) is { Ok: true } r && r.Value!.Layers > 0),
                 "and every one of them opens, with its layers");
        var literature = all.Skip(10 + guide.Length).ToArray();
        h.Expect(literature.Length >= 300, $"the literature ships ({literature.Length} designs)");
        h.Expect(literature.All(e => e.Group.Split('/').Length == 3),
                 "each literature design is in Literature/<category>/<paper>");
        int categories = literature.Select(e => e.Group.Split('/')[1]).Distinct().Count();
        h.Expect(categories >= 10, $"in categories ({categories})");
        h.Expect(literature.All(e => e.Source is { Length: > 20 }), "and every one names the paper it came from");
        h.Expect(all.All(e => e.Summary is { Length: > 0 }), "every sample says what it is, on its first line");

        var polarizers = Samples.Catalog("polarizer clapham");
        h.Expect(polarizers.Count > 0 && polarizers.All(e => e.Group.StartsWith("Literature/Polarizers/Clapham 1969", StringComparison.Ordinal)),
                 $"two words keep what holds both ({polarizers.Count}, all Clapham's polarizers)");
        h.Expect(Samples.Catalog("WARREN 1968").Count == all.Count(e => e.Group.Contains("Warren 1968", StringComparison.Ordinal)),
                 "case is ignored, and a paper's folder name finds its designs");
        h.Pin(Samples.Catalog("no-such-thing-anywhere").Count, 0, "a word nothing holds finds nothing");
    }

    /// <summary>Opening every literature design is the test that they ship working: a file that
    /// does not load is a sample nobody can use.</summary>
    public static void EveryLiteratureDesignOpens(Harness h)
    {
        h.Section("every design from the literature opens and evaluates");

        var api = new ThinFilmApi();
        int opened = 0;
        foreach (var e in Samples.Catalog("Literature"))
        {
            var r = api.Load(File.ReadAllText(e.Path));
            if (!r.Ok) { h.Fail($"{e.Name}: {r.Error}"); continue; }
            if (r.Value!.Layers == 0) { h.Fail($"{e.Name}: has no layers"); continue; }
            api.Close(r.Value.Id);
            opened++;
        }
        h.Expect(opened > 300, $"{opened} opened, each with its layers");
        h.Expect(File.Exists(MaterialDatabase.ShippedLiteraturePath)
                 && MaterialDatabase.Load(MaterialDatabase.ShippedLiteraturePath).Materials.Count > 100,
                 "and the literature's material database ships beside the program");
    }

    public static void HelpFindsTheUserGuide(Harness h)
    {
        h.Section("Help > User guide: the packaged pages, or the source tree's guide");

        string package = Path.Combine(Path.GetTempPath(), "thinfilm-guide-tests", Guid.NewGuid().ToString("N"));
        string page = Path.Combine(package, "docs", "user-guide", "README.html");
        Directory.CreateDirectory(Path.GetDirectoryName(page)!);
        File.WriteAllText(page, "<html></html>");
        h.Expect(UserGuide.Find(package) == page, "a package's docs/user-guide/README.html");
        h.Expect(UserGuide.Find(AppContext.BaseDirectory) is { } md && md.EndsWith(Path.Combine("docs", "user-guide", "README.md")),
                 "run from the source tree, the guide's own README.md");
        try { Directory.Delete(package, true); } catch (IOException) { }
    }

    public static void TheTreeIsTheFolders(Harness h)
    {
        h.Section("the browser's tree: folders as branches, a folder of one design shows the design");

        var roots = SampleTree.Build(Samples.Catalog());
        h.Expect(roots.Select(r => r.Name).SequenceEqual(["Examples", "User guide", "Literature"]), "Examples, then User guide, then Literature");
        var examples = roots[0];
        h.Expect(examples.Children.Count == 10 && examples.Children.All(c => c.Entry is not null),
                 "the ten examples are designs directly under Examples, not one folder each");
        h.Pin(roots.Sum(r => r.Count), Samples.Files.Count, "every design is somewhere in it");
        h.Expect(roots[2].Children.All(c => c.Entry is null && c.Header.EndsWith(')')),
                 "under Literature, categories, each with how many designs it holds");
        var antireflection = roots[2].Children.First(c => c.Name == "Antireflection");
        h.Expect(antireflection.Children.Any(c => c.Name == "Young 1961" && c.Count > 5),
                 "and under a category, its papers");
        h.Expect(antireflection.Children.Any(c => c.Entry is not null && c.Name.StartsWith("Bermel2007", StringComparison.Ordinal)),
                 "a paper with one design shows that design in its place");
    }

    public static void TheCliAndTheMcpList(Harness h)
    {
        h.Section("thinfilm samples and the MCP's list_samples read the same catalogue");

        var output = new StringWriter();
        h.Expect(CommandLine.Run(["samples", "notch"], output, new StringWriter()) == 0
                 && output.ToString().Contains("Literature/Notch filters/Young 1967/", StringComparison.Ordinal)
                 && !output.ToString().Contains("Examples/", StringComparison.Ordinal),
                 "thinfilm samples notch: the notch filters, by folder");
        output = new StringWriter();
        h.Expect(CommandLine.Run(["samples", "--json"], output, new StringWriter()) == 0
                 && JsonNode.Parse(output.ToString())!.AsArray().Count == Samples.Files.Count,
                 "--json lists them all");
        var error = new StringWriter();
        h.Expect(CommandLine.Run(["samples", "no-such-thing-anywhere"], new StringWriter(), error) == 1
                 && error.ToString().Contains("no sample matches", StringComparison.Ordinal),
                 $"nothing found is a failure that says so ({error.ToString().Trim()})");

        var tools = new Tools();
        var (listed, failed) = tools.Call("list_samples", new JsonObject { ["filter"] = "Examples" });
        var list = JsonNode.Parse(listed)!.AsArray();
        h.Expect(!failed && list.Count == 10 && list.All(e => File.Exists(e!["Path"]!.GetValue<string>())),
                 "list_samples Examples: the ten, each with a path that exists");
        var (opened, notOpened) = tools.Call("load_design", new JsonObject { ["path"] = list[0]!["Path"]!.GetValue<string>() });
        h.Expect(!notOpened && JsonNode.Parse(opened)!["Id"] is not null, "and load_design opens one by it");
        var (everything, _) = tools.Call("list_samples", new JsonObject());
        h.Pin(JsonNode.Parse(everything)!.AsArray().Count, Samples.Files.Count, "without a filter, every sample");
        string description = tools.Describe().Single(t => t!["name"]!.GetValue<string>() == "load_design")!["description"]!.GetValue<string>();
        h.Expect(description.Contains("list_samples", StringComparison.Ordinal) && description.Length < 1000,
                 "load_design points to list_samples instead of listing hundreds of files");
    }
}
