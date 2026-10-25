// McpClientSetup.cs — connecting thinfilm-mcp to Claude Desktop and Claude Code, without the window.
//
// The user (2026-10-24): "a Windows MCP setup helper similar to what we provided for LensHH-LT".
// That helper's lessons are kept: Claude Desktop's config is MERGED, never overwritten (other
// servers in it are the user's), and `claude` is looked for the ways it is really installed -
// Process.Start with UseShellExecute false does not apply PATHEXT, so the npm install's claude.cmd
// is invisible to a plain "claude", and it has to be run through cmd.exe.
//
// Everything that decides something is here, so it is tested without a screen; the window
// (ThinFilm.McpSetup) only shows it. Every path the methods touch can be given, which is how the
// tests keep away from the profile of the person running them.

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ThinFilm.Gui.Core;

public static class McpClientSetup
{
    /// <summary>The name the server is registered under in both clients.</summary>
    public const string ServerName = "thinfilm";

    private static string Exe(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    /// <summary>
    /// thinfilm-mcp beside <paramref name="baseDirectory"/> (every installed layout: the Windows
    /// install folder, the macOS app's Contents/MacOS, a build's bin folder), else the built
    /// server in the source tree, Release before Debug. Null when there is none.
    /// </summary>
    public static string? FindServer(string baseDirectory)
    {
        // INSIDE THE LINUX APPIMAGE the folder beside us is a mount made afresh at every launch
        // (/tmp/.mount_XXXX), gone when the program closes: a client given a path in it would
        // find nothing the next day. The AppImage file itself is what lasts, run with --mcp, and
        // its runtime names it in APPIMAGE.
        if (Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } image && File.Exists(image))
            return image;
        string[] candidates =
        [
            Path.Combine(baseDirectory, Exe("thinfilm-mcp")),
            // From managed/ThinFilm.McpSetup/bin/<config>/net8.0 to managed/ThinFilm.Mcp/bin/...
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "..", "ThinFilm.Mcp", "bin", "Release", "net8.0", Exe("thinfilm-mcp"))),
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "..", "ThinFilm.Mcp", "bin", "Debug", "net8.0", Exe("thinfilm-mcp"))),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>thinfilm-mcp-setup, the window that does this, beside the GUI or in the source
    /// tree: what Help > Connect to Claude starts. Null when there is none.</summary>
    public static string? FindSetup(string baseDirectory)
    {
        string[] candidates =
        [
            Path.Combine(baseDirectory, Exe("thinfilm-mcp-setup")),
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "..", "ThinFilm.McpSetup", "bin", "Release", "net8.0", Exe("thinfilm-mcp-setup"))),
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "..", "ThinFilm.McpSetup", "bin", "Debug", "net8.0", Exe("thinfilm-mcp-setup"))),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>The arguments a client starts the server with: none, except for the Linux
    /// AppImage, which is one file for all three programs and runs the server with --mcp.</summary>
    public static string[] ServerArgs(string server) =>
        server.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase) ? ["--mcp"] : [];

    // ---- Claude Desktop --------------------------------------------------------------------

    /// <summary>Where Claude Desktop keeps its settings on this platform.</summary>
    public static string DesktopConfigPath()
    {
        if (OperatingSystem.IsMacOS())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                "Library", "Application Support", "Claude", "claude_desktop_config.json");
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                "Claude", "claude_desktop_config.json");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                            ".config", "Claude", "claude_desktop_config.json");
    }

    /// <summary>The server Claude Desktop's settings name, or null when it names none.</summary>
    public static string? DesktopServer(string configPath)
    {
        try
        {
            if (!File.Exists(configPath)) return null;
            return JsonNode.Parse(File.ReadAllText(configPath))?["mcpServers"]?[ServerName]?["command"]?.GetValue<string>();
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Adds or updates the thinfilm entry in Claude Desktop's settings and keeps every other
    /// entry. The file as it was is kept beside it as .bak. A file that is not JSON is left alone
    /// and the reason returned: overwriting it would lose whatever the user had in it.
    /// Returns null on success, else why not.
    /// </summary>
    public static string? ConfigureDesktop(string configPath, string server)
    {
        if (!File.Exists(server)) return $"the server is not there: {server}";
        JsonObject root;
        try
        {
            root = File.Exists(configPath) && File.ReadAllText(configPath).Trim() is { Length: > 0 } text
                ? JsonNode.Parse(text) as JsonObject ?? throw new JsonException("not a JSON object")
                : new JsonObject();
        }
        catch (JsonException e)
        {
            return $"Claude Desktop's settings are not valid JSON, so they were left as they are ({e.Message}). " +
                   $"Fix or remove {configPath} and try again.";
        }
        if (root["mcpServers"] is not JsonObject servers) root["mcpServers"] = servers = new JsonObject();
        servers[ServerName] = new JsonObject
        {
            ["command"] = server,
            ["args"] = new JsonArray(ServerArgs(server).Select(a => (JsonNode?)JsonValue.Create(a)).ToArray()),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        if (File.Exists(configPath)) File.Copy(configPath, configPath + ".bak", overwrite: true);
        File.WriteAllText(configPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return null;
    }

    /// <summary>Removes the thinfilm entry and nothing else. Null on success (or when there was
    /// nothing to remove), else why not.</summary>
    public static string? RemoveDesktop(string configPath)
    {
        if (!File.Exists(configPath)) return null;
        JsonObject? root;
        try { root = JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject; }
        catch (JsonException e) { return $"Claude Desktop's settings are not valid JSON ({e.Message}); nothing was changed."; }
        if (root?["mcpServers"] is not JsonObject servers || !servers.ContainsKey(ServerName)) return null;
        servers.Remove(ServerName);
        File.Copy(configPath, configPath + ".bak", overwrite: true);
        File.WriteAllText(configPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return null;
    }

    // ---- Claude Code -----------------------------------------------------------------------

    /// <summary>
    /// The `claude` launcher: CLAUDE_EXE if set, then PATH (claude.exe, claude.cmd, claude.bat on
    /// Windows), then where its installers put it. Null when there is none.
    /// </summary>
    public static string? FindClaude()
    {
        if (Environment.GetEnvironmentVariable("CLAUDE_EXE") is { Length: > 0 } set && File.Exists(set)) return set;
        string[] names = OperatingSystem.IsWindows() ? ["claude.exe", "claude.cmd", "claude.bat"] : ["claude"];
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            foreach (var name in names)
            {
                try
                {
                    string full = Path.Combine(dir.Trim(), name);
                    if (File.Exists(full)) return full;
                }
                catch (ArgumentException) { }      // a malformed PATH entry
            }
        }
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] known = OperatingSystem.IsWindows()
            ? [Path.Combine(home, ".local", "bin", "claude.exe"), Path.Combine(appData, "npm", "claude.cmd"),
               Path.Combine(local, "Programs", "claude", "claude.exe")]
            : [Path.Combine(home, ".local", "bin", "claude"), Path.Combine(home, ".claude", "local", "claude"),
               "/usr/local/bin/claude", "/opt/homebrew/bin/claude"];
        return known.FirstOrDefault(File.Exists);
    }

    /// <summary>The arguments that register the server with Claude Code, for every user.</summary>
    public static string AddArguments(string server) =>
        $"mcp add --transport stdio --scope user {ServerName} -- \"{server}\"" +
        string.Concat(ServerArgs(server).Select(a => " " + a));

    /// <summary>The whole command, to copy and run by hand when `claude` is not found.</summary>
    public static string AddCommand(string server) => "claude " + AddArguments(server);

    /// <summary>Runs `claude` with <paramref name="arguments"/>: the exit code and what it printed.</summary>
    public static (int Code, string Output) RunClaude(string claude, string arguments, int timeoutMs = 20000)
    {
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        string ext = Path.GetExtension(claude).ToLowerInvariant();
        if (ext is ".cmd" or ".bat")
        {
            // CreateProcess will not run a script; cmd /c strips the outer quotes and passes the rest.
            psi.FileName = "cmd.exe";
            psi.Arguments = $"/c \"\"{claude}\" {arguments}\"";
        }
        else
        {
            psi.FileName = claude;
            psi.Arguments = arguments;
        }
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch (Exception) { } return (-1, "claude did not answer in time"); }
        return (p.ExitCode, (stdout.Result + stderr.Result).Trim());
    }

    /// <summary>Whether Claude Code lists the server: `claude mcp get thinfilm` succeeds.</summary>
    public static bool CodeConfigured(string claude)
    {
        try { return RunClaude(claude, $"mcp get {ServerName}").Code == 0; }
        catch (Exception) { return false; }
    }

    // ---- the server itself -----------------------------------------------------------------

    /// <summary>
    /// Starts the server as a client would, asks for its tools, and stops it: the tool count, or
    /// the reason it did not answer. What "it is configured" cannot tell you - that it runs.
    /// </summary>
    public static (int Tools, string? Problem) TestServer(string server, int timeoutMs = 20000)
    {
        if (!File.Exists(server)) return (0, $"the server is not there: {server}");
        var psi = new ProcessStartInfo(server)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in ServerArgs(server)) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            _ = p.StandardError.ReadToEndAsync();
            p.StandardInput.WriteLine("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"thinfilm-mcp-setup","version":"1"}}}""");
            p.StandardInput.WriteLine("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
            p.StandardInput.WriteLine("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
            p.StandardInput.Flush();
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            try
            {
                while (DateTime.UtcNow < deadline)
                {
                    var line = p.StandardOutput.ReadLineAsync();
                    if (!line.Wait(deadline - DateTime.UtcNow) || line.Result is null) break;
                    var message = JsonNode.Parse(line.Result);
                    if (message?["id"]?.GetValue<int>() == 2)
                        return message["result"]?["tools"] is JsonArray tools
                            ? (tools.Count, null)
                            : (0, "the server answered without a tool list");
                }
                return (0, "the server did not answer");
            }
            finally
            {
                try { p.StandardInput.Close(); if (!p.WaitForExit(3000)) p.Kill(true); } catch (Exception) { }
            }
        }
        catch (Exception e) { return (0, $"the server would not start: {e.Message}"); }
    }
}
