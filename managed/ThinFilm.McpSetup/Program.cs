// Program.cs — the Avalonia entry point of thinfilm-mcp-setup.

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Themes.Fluent;

namespace ThinFilm.McpSetup;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args) =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args);
}

internal sealed class App : Application
{
    public override void Initialize()
    {
        Name = "ThinFilmDesigner MCP setup";
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new SetupWindow();
        base.OnFrameworkInitializationCompleted();
    }
}
