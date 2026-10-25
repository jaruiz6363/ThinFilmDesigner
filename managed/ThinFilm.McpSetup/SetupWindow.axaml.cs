// SetupWindow.axaml.cs — wiring only: McpClientSetup decides, this shows.

using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using ThinFilm.Gui.Core;

namespace ThinFilm.McpSetup;

public partial class SetupWindow : Window
{
    private static readonly IBrush Good = new SolidColorBrush(Color.FromRgb(0, 128, 0));
    private static readonly IBrush Bad = new SolidColorBrush(Color.FromRgb(192, 0, 0));
    private string? _server;

    public SetupWindow()
    {
        InitializeComponent();
        _server = McpClientSetup.FindServer(AppContext.BaseDirectory);
        BrowseButton.Click += async (_, _) => await Browse();
        TestButton.Click += async (_, _) =>
        {
            if (_server is null) { Say("Choose the server first, with Browse.", false); return; }
            Say("Starting the server…", true);
            var (tools, problem) = await Task.Run(() => McpClientSetup.TestServer(_server));
            Say(problem is null ? $"The server answered: {tools} tools." : $"The server did not work: {problem}.", problem is null);
        };
        DesktopAddButton.Click += (_, _) =>
        {
            if (_server is null) { Say("Choose the server first, with Browse.", false); return; }
            var problem = McpClientSetup.ConfigureDesktop(McpClientSetup.DesktopConfigPath(), _server);
            Say(problem ?? "Claude Desktop is connected. Quit and restart it to load the server.", problem is null);
            Refresh();
        };
        DesktopRemoveButton.Click += (_, _) =>
        {
            var problem = McpClientSetup.RemoveDesktop(McpClientSetup.DesktopConfigPath());
            Say(problem ?? "Removed from Claude Desktop.", problem is null);
            Refresh();
        };
        CodeAddButton.Click += async (_, _) => await Code(add: true);
        CodeRemoveButton.Click += async (_, _) => await Code(add: false);
        CopyButton.Click += async (_, _) =>
        {
            if (_server is null) { Say("Choose the server first, with Browse.", false); return; }
            if (Clipboard is { } clipboard) await clipboard.SetTextAsync(McpClientSetup.AddCommand(_server));
            Say("Copied. Paste it into a terminal where `claude` runs.", true);
        };
        Opened += (_, _) => Refresh();
    }

    private async Task Browse()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose thinfilm-mcp",
            AllowMultiple = false,
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) { _server = path; Refresh(); }
    }

    private async Task Code(bool add)
    {
        var claude = McpClientSetup.FindClaude();
        if (claude is null)
        {
            Say("Claude Code's `claude` was not found. Use Copy the command, and run it in a terminal where `claude` works.", false);
            return;
        }
        if (add && _server is null) { Say("Choose the server first, with Browse.", false); return; }
        string args = add ? McpClientSetup.AddArguments(_server!) : $"mcp remove --scope user {McpClientSetup.ServerName}";
        Say(add ? "Connecting Claude Code…" : "Removing from Claude Code…", true);
        var (code, output) = await Task.Run(() => McpClientSetup.RunClaude(claude, args));
        Say(code == 0 ? (add ? "Claude Code is connected. Start a new session to use it." : "Removed from Claude Code.")
                      : $"claude said: {output}", code == 0);
        Refresh();
    }

    /// <summary>Every state line from what is on disk and what claude says now.</summary>
    private void Refresh()
    {
        ServerBox.Text = _server ?? "";
        State(ServerState, _server is not null && File.Exists(_server),
              "thinfilm-mcp found", "thinfilm-mcp not found: use Browse to choose it");

        string config = McpClientSetup.DesktopConfigPath();
        var desktop = McpClientSetup.DesktopServer(config);
        State(DesktopState, desktop is not null,
              desktop == _server ? "connected" : $"connected, to {desktop}",
              $"not connected ({config})");

        // `claude mcp get` starts Claude Code, a second or two: asked off the window's thread.
        var claude = McpClientSetup.FindClaude();
        if (claude is null) { State(CodeState, false, "", "`claude` not found"); return; }
        CodeState.Text = "… asking Claude Code";
        CodeState.Foreground = Brushes.Gray;
        _ = Task.Run(() => McpClientSetup.CodeConfigured(claude)).ContinueWith(t =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => State(CodeState, t.Result, "connected", "not connected")),
            TaskScheduler.Default);
    }

    private static void State(TextBlock block, bool ok, string yes, string no)
    {
        block.Text = (ok ? "✔ " : "✘ ") + (ok ? yes : no);
        block.Foreground = ok ? Good : Bad;
    }

    private void Say(string message, bool ok)
    {
        StatusText.Text = message;
        StatusText.Foreground = ok ? Good : Bad;
    }
}
