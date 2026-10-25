// Program.cs — the Avalonia entry point.

using Avalonia;

namespace ThinFilm.Gui;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
                  .UsePlatformDetect()
                  .LogToTrace();
}
