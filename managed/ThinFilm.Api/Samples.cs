// Samples.cs — where the sample designs are, and the catalogue of them.
//
// The samples are ordinary design files shipped beside the programs, in folders that group them:
//   samples/Examples/AR-visible/AR-visible.tfd                    the starting problems
//   samples/User guide/1 Narrow bandpass filter/<design>.tfd      the user guide's designs, by chapter
//   samples/Literature/Antireflection/Young 1961/<design>.tfd     designs rebuilt from papers
// Nothing here builds them: they are opened like any other file. The catalogue is what the GUI's
// sample browser, the CLI's `samples` and the MCP's list_samples all read.

using System.Text.Json;

namespace ThinFilm.Api;

/// <summary>One sample design: its file, the folder it is grouped in, and what it says it is.</summary>
/// <param name="Group">Its folder under samples, with '/' between the levels: "Examples/AR-visible",
/// "Literature/Antireflection/Young 1961".</param>
/// <param name="Source">Where it came from (a citation), or null.</param>
/// <param name="Summary">The first line of its notes, or null.</param>
public sealed record SampleEntry(string Path, string Group, string Name, string? Source, string? Summary, string? Notes);

public static class Samples
{
    /// <summary>The samples folder beside the running program.</summary>
    public static string Folder => System.IO.Path.Combine(AppContext.BaseDirectory, "samples");

    /// <summary>The starting problems, one folder each: Examples/&lt;name&gt;/&lt;name&gt;.tfd.</summary>
    public static string ExamplesFolder => System.IO.Path.Combine(Folder, "Examples");

    /// <summary>Every sample file, by path, in name order; empty when the folder is missing.</summary>
    public static IReadOnlyList<string> Files =>
        Directory.Exists(Folder)
            ? Directory.GetFiles(Folder, "*.tfd", SearchOption.AllDirectories)
                       .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray()
            : [];

    /// <summary>
    /// The samples, Examples first, then the user guide's, then by group and name, each with its source and summary.
    /// A <paramref name="filter"/> keeps those whose group, name, source or notes contain every one
    /// of its words, ignoring case: "polarizer 1969", "Warren".
    /// </summary>
    public static IReadOnlyList<SampleEntry> Catalog(string? filter = null)
    {
        string[] words = (filter ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return Files.Select(Entry)
                    .Where(e => words.All(w => Matches(e, w)))
                    .OrderBy(e => e.Group.StartsWith("Examples/", StringComparison.Ordinal) ? 0
                                : e.Group.StartsWith("User guide/", StringComparison.Ordinal) ? 1 : 2)
                    .ThenBy(e => e.Group, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
    }

    private static bool Matches(SampleEntry e, string word) =>
        new[] { e.Group, e.Name, e.Source, e.Notes }.Any(s => s?.Contains(word, StringComparison.OrdinalIgnoreCase) == true);

    private static SampleEntry Entry(string path)
    {
        string group = System.IO.Path.GetRelativePath(Folder, System.IO.Path.GetDirectoryName(path)!).Replace('\\', '/');
        string name = System.IO.Path.GetFileNameWithoutExtension(path);
        string? source = null, notes = null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            source = Text(doc.RootElement, "Source");
            notes = Text(doc.RootElement, "Notes");
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            notes = $"(could not be read: {e.Message})";
        }
        string? summary = notes?.Split('\n', 2)[0].Trim();
        return new SampleEntry(path, group, name, source, string.IsNullOrEmpty(summary) ? null : summary, notes);
    }

    private static string? Text(JsonElement root, string property) =>
        root.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s
            ? s : null;
}
