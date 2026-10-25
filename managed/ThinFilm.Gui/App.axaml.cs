using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Platform.Storage;

namespace ThinFilm.Gui;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;

            // A design to open: on Windows and Linux it is the first argument (a .tfd opened from
            // Explorer or a file manager arrives that way); on macOS, Finder sends it as an
            // activation instead.
            if (desktop.Args is [var path, ..])
                window.Opened += async (_, _) => await window.OpenPath(path);
            if (this.TryGetFeature<IActivatableLifetime>() is { } activatable)
                activatable.Activated += async (_, e) =>
                {
                    if (e is FileActivatedEventArgs { Files: var files } && files.FirstOrDefault()?.TryGetLocalPath() is { } opened)
                        await window.OpenPath(opened);
                };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
