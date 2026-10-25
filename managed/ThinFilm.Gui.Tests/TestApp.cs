// TestApp.cs — the app builder the headless session uses.
//
// THE DEFAULT HEADLESS PLATFORM DOES NOT DRAW. It lays out, hit-tests and dispatches input — which
// is enough for four of the tests here — but CaptureRenderedFrame refuses, because there is no
// rasteriser behind it. Skia with UseHeadlessDrawing turned off puts a real one there, rendering
// into a bitmap with no display attached, and that is what makes "the plot actually paints"
// something a test can assert rather than something a person has to look at.
//
// Discovered through the assembly attribute below, which is how HeadlessUnitTestSession finds a
// builder other than its own default.

using Avalonia;
using Avalonia.Headless;
using ThinFilm.Gui;
using ThinFilm.Gui.Tests;

[assembly: AvaloniaTestApplication(typeof(TestApp))]

namespace ThinFilm.Gui.Tests;

public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
                  .UseSkia()
                  .UseHeadless(new AvaloniaHeadlessPlatformOptions
                  {
                      UseHeadlessDrawing = false,
                  });
}
