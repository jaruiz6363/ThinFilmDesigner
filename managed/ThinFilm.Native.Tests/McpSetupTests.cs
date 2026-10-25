// McpSetupTests.cs — thinfilm-mcp-setup's decisions (Gui.Core McpClientSetup), on files in a
// temporary folder: never the Claude settings of the person running the tests.

using System.Text.Json.Nodes;
using ThinFilm.Gui.Core;

namespace ThinFilm.Native.Tests;

public static class McpSetupTests
{
    public static void ClaudeDesktopIsMergedNotOverwritten(Harness h)
    {
        h.Section("MCP setup: Claude Desktop's settings merged, kept and restorable; the server found and tested");

        var dir = Path.Combine(Path.GetTempPath(), "thinfilm-mcp-setup-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string config = Path.Combine(dir, "Claude", "claude_desktop_config.json");

        // The server: found beside this program's own build, as the helper finds it beside its own.
        string? server = McpClientSetup.FindServer(AppContext.BaseDirectory);
        h.Expect(server is not null && File.Exists(server), $"the built thinfilm-mcp is found ({server ?? "none"})");
        if (server is null) return;

        // No settings yet: made, with the server and no arguments.
        h.Expect(McpClientSetup.ConfigureDesktop(config, server) is null && McpClientSetup.DesktopServer(config) == server,
                 "with no settings file, one is made naming the server");
        h.Expect(JsonNode.Parse(File.ReadAllText(config))!["mcpServers"]!["thinfilm"]!["args"]!.AsArray().Count == 0,
                 "and no arguments");

        // Someone else's server and a setting of their own: both kept.
        File.WriteAllText(config, """{"theme":"dark","mcpServers":{"other":{"command":"other.exe","args":["-x"]}}}""");
        h.Expect(McpClientSetup.ConfigureDesktop(config, server) is null, "an existing file is merged");
        var merged = JsonNode.Parse(File.ReadAllText(config))!;
        h.Expect(merged["theme"]!.GetValue<string>() == "dark" && merged["mcpServers"]!["other"]!["command"]!.GetValue<string>() == "other.exe"
                 && merged["mcpServers"]!["thinfilm"]!["command"]!.GetValue<string>() == server,
                 "the other server and setting are kept, and thinfilm is added beside them");
        h.Expect(File.Exists(config + ".bak") && File.ReadAllText(config + ".bak").Contains("\"other\""),
                 "the file as it was is kept as .bak");

        // Removing takes only ours.
        h.Expect(McpClientSetup.RemoveDesktop(config) is null && McpClientSetup.DesktopServer(config) is null
                 && JsonNode.Parse(File.ReadAllText(config))!["mcpServers"]!["other"] is not null,
                 "remove takes thinfilm out and leaves the other server");

        // A file that is not JSON is never overwritten.
        File.WriteAllText(config, "{ this is not json");
        var refused = McpClientSetup.ConfigureDesktop(config, server);
        h.Expect(refused is not null && refused.Contains("not valid JSON") && File.ReadAllText(config) == "{ this is not json",
                 $"settings that are not JSON are left as they are ({refused})");

        // The AppImage is the one layout that needs an argument.
        h.Expect(McpClientSetup.ServerArgs("/opt/ThinFilmDesigner-0.1.0-x86_64.AppImage") is ["--mcp"]
                 && McpClientSetup.ServerArgs(server).Length == 0,
                 "the Linux AppImage is started with --mcp, everything else with nothing");
        h.Expect(McpClientSetup.AddCommand(server) == $"claude mcp add --transport stdio --scope user thinfilm -- \"{server}\"",
                 $"the Claude Code command to copy ({McpClientSetup.AddCommand(server)})");

        // Run from the Linux AppImage, the server to register is the AppImage, not its passing mount.
        string image = Path.Combine(dir, "ThinFilmDesigner-0.1.0-x86_64.AppImage");
        File.WriteAllText(image, "");
        string? was = Environment.GetEnvironmentVariable("APPIMAGE");
        Environment.SetEnvironmentVariable("APPIMAGE", image);
        try
        {
            h.Expect(McpClientSetup.FindServer(AppContext.BaseDirectory) == image,
                     "inside the AppImage, the AppImage file is the server, not the temporary mount beside it");
        }
        finally { Environment.SetEnvironmentVariable("APPIMAGE", was); }

        // It runs: what being configured cannot tell.
        var (tools, problem) = McpClientSetup.TestServer(server);
        h.Expect(problem is null && tools > 40, $"the server starts and lists its tools ({tools}, {problem ?? "ok"})");
        h.Expect(McpClientSetup.TestServer(Path.Combine(dir, "missing.exe")).Problem is { } missing && missing.Contains("not there"),
                 "a missing server is said to be missing");

        try { Directory.Delete(dir, true); } catch (IOException) { }
    }
}
