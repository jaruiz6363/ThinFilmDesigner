// SampleFiles.cs — the shipped sample designs, opened by name, for the tests.

using ThinFilm.Api;
using ThinFilm.Gui.Core;

namespace ThinFilm.Native.Tests;

public static class SampleFiles
{
    /// <summary>The examples by name ("AR-visible"), in name order.</summary>
    public static string[] Names =>
        Directory.GetFiles(Samples.ExamplesFolder, "*.tfd", SearchOption.AllDirectories)
                 .Select(p => Path.GetFileNameWithoutExtension(p)).Order(StringComparer.OrdinalIgnoreCase).ToArray();

    public static string PathOf(string name) => Path.Combine(Samples.ExamplesFolder, name, name + ".tfd");

    public static string Text(string name) => File.ReadAllText(PathOf(name));

    public static DesignDocument Document(string name) => DesignDocument.FromJson(Text(name));

    public static ApiResult<DesignSummary> OpenSample(this ThinFilmApi api, string name) => api.Load(Text(name));

    public static bool OpenSample(this DesignWorkspace w, string name) => w.Open(Text(name));

    /// <summary>The MCP load_design arguments that open a sample by its path.</summary>
    public static string LoadArgs(string name) =>
        System.Text.Json.JsonSerializer.Serialize(new { path = PathOf(name) });
}
