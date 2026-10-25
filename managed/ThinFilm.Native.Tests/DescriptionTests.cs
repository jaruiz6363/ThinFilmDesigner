// DescriptionTests.cs — a design's Description: where it came from (Source) and notes on it.
//
// The user (2026-10-21): "find designs and materials in the literature and pass these to you. We
// should [have] a section to give description of the file and where it came from. This should be
// in the display." Then, choosing it above the layer table: "A, go ahead".

using System.Text.Json.Nodes;
using ThinFilm.Api;
using ThinFilm.Cli;
using ThinFilm.Gui.Core;
using ThinFilm.Mcp;

namespace ThinFilm.Native.Tests;

public static class DescriptionTests
{
    private const string Cite = "H. A. Macleod, Thin-Film Optical Filters, 5th ed. (CRC, 2018), Fig. 4.12";

    public static void ADescriptionIsSavedWithTheDesign(Harness h)
    {
        h.Section("Source and notes: set, saved in the file, cleared, and in the summary");

        var api = new ThinFilmApi();
        string id = api.OpenSample("AR-visible").Value!.Id;
        var r = api.SetDescription(id, Cite + "\r\n", "Four-layer AR.\r\nThicknesses read from the figure.");
        h.Expect(r.Ok && r.Value!.Source == Cite && r.Value.Notes == "Four-layer AR.\nThicknesses read from the figure.",
                 "set: the source on one line, the notes keeping their lines, both trimmed");
        var again = api.Load(api.Save(id).Value!).Value!;
        h.Expect(again.Source == Cite && again.Notes!.StartsWith("Four-layer"), "and both come back from the saved file");
        h.Expect(api.SetDescription(id, null, "") is { Ok: true } c && c.Value!.Notes is null && c.Value.Source == Cite,
                 "null leaves a field, empty clears it");
        double merit = api.Describe(id).Value!.Merit;
        h.Expect(merit == api.Describe(again.Id).Value!.Merit, "and the description changes nothing that is computed");
    }

    public static void TheExportsCarryIt(Harness h)
    {
        h.Section("A ZEMAX COAT and an Optiland coating carry the source");

        var api = new ThinFilmApi();
        string id = api.OpenSample("AR-visible").Value!.Id;
        api.SetDescription(id, Cite, "notes here");
        string dir = Path.Combine(Path.GetTempPath(), "thinfilm-description-tests");
        Directory.CreateDirectory(dir);
        string dat = Path.Combine(dir, "c.dat"), json = Path.Combine(dir, "c.json");
        File.Delete(dat);
        api.ExportZemaxCoating(id, new ZemaxExportRequest(dat, "AR"));
        var lines = File.ReadAllLines(dat);
        int coat = Array.IndexOf(lines, "COAT AR");
        h.Expect(lines[coat - 1] == $"{ZemaxCoatingFile.Tag}: source {Cite}" && lines[coat - 2].Contains("designed at reference"),
                 "the COAT has the source in a comment above it, after its reference wavelength");
        var replaced = api.ExportZemaxCoating(id, new ZemaxExportRequest(dat, "AR", Replace: true));
        h.Expect(replaced.Ok && File.ReadAllLines(dat).Count(l => l.Contains(": source ")) == 1
                 && ZemaxCoatingFile.Read(dat).ReferenceNm(ZemaxCoatingFile.Read(dat).Find("COAT", "AR")!) == 550.0,
                 "replaced, it has one source line, and its reference is still read");

        api.ExportOptilandCoating(id, json);
        var t = JsonNode.Parse(File.ReadAllText(json))!["thinfilm"]!;
        h.Expect(t["source"]!.GetValue<string>() == Cite && t["notes"]!.GetValue<string>() == "notes here",
                 "the Optiland coating's thinfilm entry has the source and notes");
    }

    public static void TheWorkspaceTheCliAndTheMcp(Harness h)
    {
        h.Section("The workspace edits it only when it changes; thinfilm describe; the MCP's set_description");

        var w = new DesignWorkspace();
        w.OpenSample("AR-visible");
        h.Expect(w.DescriptionSummary.StartsWith("none yet") || w.DesignNotes.Length > 0, "a design with no source says so when folded");
        h.Expect(w.SetDescription(w.DesignSource, w.DesignNotes) && !w.IsModified,
                 "leaving a box without typing is no edit: the design is not marked changed");
        h.Expect(w.SetDescription(Cite, null) && w.IsModified && w.DesignSource == Cite && w.DescriptionSummary == Cite,
                 "typing a source is an edit, and the folded header shows it");

        string dir = Path.Combine(Path.GetTempPath(), "thinfilm-description-tests");
        Directory.CreateDirectory(dir);
        string f = Path.Combine(dir, "d.tfd");
        File.Copy(SampleFiles.PathOf("AR-visible"), f, overwrite: true);
        var output = new StringWriter();
        h.Expect(CommandLine.Run(["describe", f, "--source", Cite, "--notes", "from the CLI"], output, new StringWriter()) == 0
                 && DesignDocument.FromJson(File.ReadAllText(f)) is { Source: Cite, Notes: "from the CLI" }
                 && output.ToString().Contains($"source: {Cite}"),
                 "thinfilm describe --source --notes writes them and prints them");
        output = new StringWriter();
        h.Expect(CommandLine.Run(["show", f], output, new StringWriter()) == 0 && output.ToString().Contains($"source: {Cite}"),
                 "thinfilm show prints the source");

        var tools = new Tools();
        var (opened, _) = tools.Call("load_design", new JsonObject { ["path"] = f });
        string id = JsonNode.Parse(opened)!["Id"]!.GetValue<string>();
        var (set, failed) = tools.Call("set_description", new JsonObject { ["design"] = id, ["notes"] = "from the MCP" });
        var (described, _) = tools.Call("describe_design", new JsonObject { ["design"] = id });
        var d = JsonNode.Parse(described)!;
        h.Expect(!failed && d["Source"]!.GetValue<string>() == Cite && d["Notes"]!.GetValue<string>() == "from the MCP",
                 $"set_description, and describe_design returns both ({set[..Math.Min(40, set.Length)]})");
    }

    public static void ADesignCanBeRenamed(Harness h)
    {
        h.Section("Renaming: the workspace and its title, thinfilm describe --name, the MCP's rename_design");

        // The user, 2026-10-24: a name mistyped in New design ("Wid-angle AR") could not be put right.
        string dir = Path.Combine(Path.GetTempPath(), "thinfilm-description-tests");
        Directory.CreateDirectory(dir);
        string typo = Path.Combine(dir, "typo.tfd");
        CommandLine.Run(["new", "--materials", "SiO2,TiO2", "--substrate", "N-BK7", "--name", "Wid-angle AR", "-o", typo],
                        new StringWriter(), new StringWriter());
        var w = new DesignWorkspace();
        w.Open(File.ReadAllText(typo));       // no file name: the title is the design's name, as after New design
        h.Expect(w.DesignName == "Wid-angle AR" && w.Title == "ThinFilm — Wid-angle AR", $"a new design has its name ({w.Title})");
        h.Expect(w.Rename("  Wide-angle AR ") && w.DesignName == "Wide-angle AR" && w.Title == "ThinFilm — Wide-angle AR" && w.IsModified,
                 $"renamed, the title follows and the design is changed ({w.Title})");
        h.Expect(!w.Rename("   ") && w.DesignName == "Wide-angle AR" && w.Status.Contains("needs a name"),
                 $"a blank name is refused and the name stays ({w.Status})");
        h.Expect(w.Rename("Wide-angle AR") && w.DesignName == "Wide-angle AR", "the same name again is no edit");

        string f = Path.Combine(dir, "named.tfd");
        File.Copy(SampleFiles.PathOf("AR-visible"), f, overwrite: true);
        var output = new StringWriter();
        h.Expect(CommandLine.Run(["describe", f, "--name", "Camera AR"], output, new StringWriter()) == 0
                 && DesignDocument.FromJson(File.ReadAllText(f)).Name == "Camera AR" && output.ToString().Contains("Camera AR"),
                 "thinfilm describe --name writes the new name");
        var refused = new StringWriter();
        h.Expect(CommandLine.Run(["describe", f, "--name", ""], new StringWriter(), refused) != 0
                 && DesignDocument.FromJson(File.ReadAllText(f)).Name == "Camera AR",
                 $"and refuses a blank one ({refused.ToString().Trim()})");

        var tools = new Tools();
        var (opened, _) = tools.Call("load_design", new JsonObject { ["path"] = f });
        string id = JsonNode.Parse(opened)!["Id"]!.GetValue<string>();
        var (renamed, failed) = tools.Call("rename_design", new JsonObject { ["design"] = id, ["name"] = "Lens AR" });
        var (described, _) = tools.Call("describe_design", new JsonObject { ["design"] = id });
        h.Expect(!failed && JsonNode.Parse(described)!["Name"]!.GetValue<string>() == "Lens AR",
                 $"rename_design renames it ({renamed[..Math.Min(40, renamed.Length)]})");
    }
}
