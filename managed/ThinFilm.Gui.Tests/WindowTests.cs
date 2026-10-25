// WindowTests.cs — X1.11: the part of the GUI that needed a display, tested without one.
//
// X1.10 SPLIT THE GUI SO THE TESTABLE HALF COULD BE TESTED, and then recorded what was left over:
// that the window opens, that a button is wired to the handler it appears wired to, that the plot
// actually paints. Those were called untestable. They are not — Avalonia.Headless renders to a
// bitmap with no display attached — and the reason they were left is worth naming: the package was
// not in the local cache, and I concluded from that it could not be had, without checking whether
// a restore would reach the network. It would. The cache miss was a fact; the conclusion drawn
// from it was not.
//
// REAL MOUSE INPUT, NOT RAISED EVENTS. A click here is MouseDown and MouseUp at the point the
// button actually occupies after layout, which is what makes this worth more than calling the
// handler directly: it exercises layout, hit-testing and the wiring together. A button that ended
// up behind another control, or sized to nothing, passes an event-raising test and fails this one.
//
// AND THE PLOT IS CHECKED BY READING ITS PIXELS. "The spectrum draws" is the one claim that really
// does need rendering, so the frame is captured and the plot region examined: a blank chart is one
// colour, and a chart with a curve on it is not.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Controls.Primitives;
using Avalonia.Platform;
using Avalonia.VisualTree;
using ThinFilm.Gui.Core;
using ThinFilm.Native.Tests;

namespace ThinFilm.Gui.Tests;

public static class WindowTests
{
    /// <summary>
    /// One window, laid out and rendered, handed to the body. Every test needs the same four
    /// lines and getting one of them wrong shows up as a control with zero size rather than as an
    /// error, so they live here once.
    /// </summary>
    private static void WithWindow(HeadlessUnitTestSession session, Action<MainWindow> body,
                                   double? width = null)
    {
        session.Dispatch(() =>
        {
            var window = new MainWindow();
            if (width is { } w) window.Width = w;
            window.Show();
            // Layout has to happen before anything has a position to be clicked at.
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            body(window);
            window.AskToSave = false;
            window.Close();
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static T Find<T>(MainWindow window, string name) where T : Control =>
        window.GetControl<T>(name);

    /// <summary>A sample design opened as File > Open sample opens it, without the file picker.</summary>
    private static void OpenSample(MainWindow window, string name = "AR-visible")
    {
        window.OpenDocument(File.ReadAllText(Path.Combine(ThinFilm.Api.Samples.ExamplesFolder, name, name + ".tfd")),
                            name + ".tfd");
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    /// <summary>A click where the control actually is, after layout.</summary>
    private static void Click(MainWindow window, Control control)
    {
        // A menu's submenu lives in a popup, which is a visual root of its own, so the click
        // goes to whichever top level the control is really on.
        var root = control.GetVisualRoot() as TopLevel ?? window;
        var centre = control.TranslatePoint(
            new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), root);
        if (centre is null) throw new InvalidOperationException(
            "the control has no position in the window — it was never laid out");

        root.MouseMove(centre.Value);
        root.MouseDown(centre.Value, MouseButton.Left);
        root.MouseUp(centre.Value, MouseButton.Left);
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }

    /// <summary>A tab switched the way a person switches it: a click on its header.</summary>
    private static void ShowTab(MainWindow window, string tab) =>
        Click(window, Find<TabItem>(window, tab));

    // ---- the tests ---------------------------------------------------------------------------

    public static void TheWindowOpensWithItsControls(Harness h, HeadlessUnitTestSession session)
    {
        h.Section("the window opens, lays out, and every control the code names is really there");

        WithWindow(session, window =>
        {
            h.Expect(window.IsVisible, "the window is shown");
            h.Expect(window.Bounds.Width > 400 && window.Bounds.Height > 300,
                     $"and has a size ({window.Bounds.Width}x{window.Bounds.Height})");

            // EVERY NAME THE CODE-BEHIND LOOKS UP. A typo here is a NullReferenceException on
            // startup for a user and nothing at all for a compiler.
            foreach (var name in new[] { "LayerGrid",
                                         "ClearOperandsButton", "DeleteOperandsButton",
                                         "AdjustWeightButton", "AddOperandsButton",
                                         "AddMultipleAoiButton", "AddSingleWvButton",
                                         "OperandGrid", "Plot", "FromBox", "ToBox", "StepsBox",
                                         "QuantitiesButton", "QuantitiesText", "PlotButton", "StatusText",
                                         "SummaryText", "IncidentText", "SubstrateText",
                                         "PlotNote", "Tabs", "DesignTab", "MeritTab",
                                         "SpectrumTab", "DesignPanel", "MeritText",
                                         "OperandHelp", "MainMenu", "FileMenu",
                                         "OpenSampleMenu", "OpenMenu", "SaveMenu",
                                         "ExitMenu", "OptimizationMenu", "RefineMenu", "GlobalMenu",
                                         "NeedleRunMenu", "EvolveMenu",
                                         "MeritMenu", "AnalysisMenu",
                                         "SpectrumMenu", "SaveAsMenu", "AngleBox",
                                         "InsertLayerButton", "DeleteLayerButton",
                                         "MoveLayerUpButton", "MoveLayerDownButton",
                                         "NewDesignMenu", "MaterialsMenu",
                                         "LayerPropertiesButton", "StackButton" })
            {
                var control = window.FindControl<Control>(name);
                h.Expect(control is not null, $"{name} exists");
            }

            // X1.33: optimisation moved to windows of its own; the Design tab's controls are gone.
            foreach (var gone in new[] { "StageBox", "NormBox", "OptimiseButton", "OptimiseMenu", "TemplateBox", "OpenButton", "NewFromTemplateMenu" })
                h.Expect(window.FindControl<Control>(gone) is null, $"{gone} is gone");

            var status = Find<TextBlock>(window, "StatusText");
            h.Expect(!string.IsNullOrWhiteSpace(status.Text),
                     $"and the status line says what to do first: {status.Text}");
        });
    }

    /// <summary>
    /// THE CLAIM X1.10 COULD NOT CHECK: that a button is wired to the handler it appears wired to.
    /// Clicked where it sits, not invoked by name.
    /// </summary>
    public static void ClickingOpenFillsTheTables(Harness h, HeadlessUnitTestSession session)
    {
        h.Section("clicking Open — at the pixel it occupies — fills the layer and merit tables");

        WithWindow(session, window =>
        {
            var layers = Find<DataGrid>(window, "LayerGrid");
            var operands = Find<DataGrid>(window, "OperandGrid");
            // The window starts with a new design (the user, 2026-10-21): File > New design's defaults.
            h.Expect(layers.ItemsSource?.Cast<LayerRow>().Count() == 2
                     && Find<TextBlock>(window, "StatusText").Text!.StartsWith("New design"),
                     "the window starts with a new two-layer design, not an empty table");

            OpenSample(window);

            var layerRows = layers.ItemsSource?.Cast<object>().ToArray() ?? [];
            var operandRows = operands.ItemsSource?.Cast<object>().ToArray() ?? [];
            h.Expect(layerRows.Length > 0, $"the layer table filled ({layerRows.Length} rows)");
            h.Expect(operandRows.Length > 0, $"and the merit function ({operandRows.Length} rows)");

            var summary = Find<TextBlock>(window, "SummaryText");
            h.Expect(summary.Text is not null && summary.Text.Contains("merit", StringComparison.Ordinal),
                     $"and the summary reports a merit: {summary.Text}");

            // What a first-time user could not find: the substrate, which end is which, units.
            foreach (var name in new[] { "IncidentText", "SubstrateText", "PlotNote" })
                h.Expect(!string.IsNullOrWhiteSpace(Find<TextBlock>(window, name).Text),
                         $"{name} is filled in: {Find<TextBlock>(window, name).Text}");
            h.Expect(layerRows[0] is LayerRow { Number: 1 }, "and the first layer row is layer 1");
        });
    }

    // ---- the optimisation windows (X1.32, X1.33) ------------------------------------------------

    /// <summary>Opens Optimization > the item named, and returns its window.</summary>
    private static RunDialog? OpenRun(MainWindow window, string menu)
    {
        Click(window, Find<MenuItem>(window, "OptimizationMenu"));
        Click(window, Find<MenuItem>(window, menu));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var d = window.OwnedWindows.OfType<RunDialog>().LastOrDefault(w => w.IsVisible);
        d?.UpdateLayout();
        return d;
    }

    /// <summary>Starts the window's run and ticks its poll until it has ended - it runs in the background.</summary>
    private static void RunToEnd(MainWindow window, RunDialog d)
    {
        Click(window, d.GetControl<Button>("StartButton"));
        for (int i = 0; i < 800 && !d.GetControl<Button>("AcceptButton").IsEnabled
                                && !d.GetControl<TextBlock>("StateText").Text!.StartsWith("Failed"); ++i)
        {
            System.Threading.Thread.Sleep(25);
            d.Tick();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
    }

    public static void EachOptimisationHasAWindowOfItsOwn(Harness h, HeadlessUnitTestSession session)
    {
        h.Section("Optimization: Refine, Global search, Needle, Gradual evolution, each a window with its own settings");

        WithWindow(session, window =>
        {
            OpenSample(window);
            // Gradual evolution grows, needles and removes, so it shows all three's settings.
            var expected = new (string Menu, string Title, string[] Panel, string StepLabel)[]
            {
                ("RefineMenu", "Refine", ["NormPanel"], "Iteration"),
                ("GlobalMenu", "Global search", ["StartsPanel"], "Start"),
                ("NeedleRunMenu", "Needle optimisation", ["InsertionsPanel"], "Insertion"),
                ("EvolveMenu", "Gradual evolution", ["GrowthPanel", "InsertionsPanel", "EvolutionPanel"], "Step"),
            };
            string[] panels = ["NormPanel", "StartsPanel", "InsertionsPanel", "GrowthPanel", "EvolutionPanel"];
            foreach (var (menu, title, panel, step) in expected)
            {
                var d = OpenRun(window, menu);
                h.Expect(d is not null && d.Title == title, $"{menu} opens {title} ({d?.Title})");
                if (d is null) continue;
                h.Expect(panels.All(p => d.GetControl<Control>(p).IsVisible == panel.Contains(p)),
                         $"showing only its own settings ({string.Join(", ", panel)})");
                h.Expect(d.GetControl<TextBlock>("StepLabel").Text == step, $"and counting {step.ToLowerInvariant()}s");
                Click(window, d.GetControl<Button>("CloseButton"));
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                h.Expect(!d.IsVisible, "Close closes it unrun");
            }
            h.Expect(Find<TextBlock>(window, "StatusText").Text == "Gradual evolution closed without running.",
                     $"and the status line says nothing ran ({Find<TextBlock>(window, "StatusText").Text})");

            // NEEDLE adds layers; Gradual evolution with no growth steps, its removal allowed to cost
            // anything, takes one out again.
            var grid = Find<DataGrid>(window, "LayerGrid");
            int Layers() => grid.ItemsSource!.Cast<LayerRow>().Count();
            int was = Layers();
            var needle = OpenRun(window, "NeedleRunMenu")!;
            RunToEnd(window, needle);
            var needleLog = needle.GetControl<TextBox>("LogBox").Text ?? "";
            h.Expect(needleLog.Contains("insert 1") && needleLog.Contains(" at "), "the needle log says what went in where");
            Click(window, needle.GetControl<Button>("AcceptButton"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            int grown = Layers();
            h.Expect(grown > was, $"accepted, the stack has more layers ({was} → {grown})");

            var evolve = OpenRun(window, "EvolveMenu")!;
            h.Expect(evolve.GetControl<TextBlock>("MinimumThicknessUnit").Text == "nm", "the minimum thickness is in the design's unit");
            TypeInto(window, evolve, evolve.GetControl<TextBox>("MinimumThicknessBox"), "1000");
            TypeInto(window, evolve, evolve.GetControl<TextBox>("DegradationBox"), "1e12");
            TypeInto(window, evolve, evolve.GetControl<TextBox>("RemovalsBox"), "1");
            h.Expect(evolve.GetControl<TextBox>("GrowthStepsBox").Text == "8" && evolve.GetControl<TextBox>("GrowthQwotBox").Text == "1"
                     && evolve.GetControl<ComboBox>("GrowAtBox").SelectedItem as string == RunMonitor.GrowEither
                     && evolve.GetControl<TextBlock>("InsertionsLabel").Text == "Needles per step",
                     "evolve offers eight growth steps of one QWOT at either side, and needles per step");
            TypeInto(window, evolve, evolve.GetControl<TextBox>("GrowthStepsBox"), "0");
            RunToEnd(window, evolve);
            var evolveLog = evolve.GetControl<TextBox>("LogBox").Text ?? "";
            h.Expect(evolveLog.Contains("remove 0.1") && evolveLog.Contains("step 0") && !evolveLog.Contains("grow 1"),
                     "evolve logs the removal and step 0, and with no growth steps grows nothing");
            Click(window, evolve.GetControl<Button>("AcceptButton"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            h.Expect(Layers() < grown, $"accepted, the stack has fewer layers ({grown} → {Layers()})");

            // GLOBAL SEARCH by basin hopping, run and accepted.
            var summary = Find<TextBlock>(window, "SummaryText");
            double before = MeritFrom(summary.Text ?? "");
            var global = OpenRun(window, "GlobalMenu")!;
            h.Expect(!global.GetControl<StackPanel>("HopPanel").IsVisible && global.GetControl<StackPanel>("RandomPanel").IsVisible
                     && global.GetControl<TextBox>("RandomRangeBox").Text == "2", "random starts: a range of 0 to 2x, no perturbation");
            h.Expect(global.GetControl<TextBox>("ThreadsBox").Text == Math.Max(1, Environment.ProcessorCount - 1).ToString(),
                     $"every core but one to begin with ({global.GetControl<TextBox>("ThreadsBox").Text})");
            global.GetControl<ComboBox>("GlobalModeBox").SelectedItem = RunMonitor.BasinHopping;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            h.Expect(global.GetControl<StackPanel>("HopPanel").IsVisible && !global.GetControl<StackPanel>("RandomPanel").IsVisible
                     && global.GetControl<TextBlock>("StepLabel").Text == "Hop"
                     && global.GetControl<TextBlock>("StartsLabel").Text == "Hops",
                     "basin hopping: the perturbation is offered, and hops are counted");
            TypeInto(window, global, global.GetControl<TextBox>("StartsBox"), "6");
            RunToEnd(window, global);
            var globalLog = global.GetControl<TextBox>("LogBox").Text ?? "";
            h.Expect(global.GetControl<TextBlock>("StepText").Text == "6 of 6", $"all six hops ({global.GetControl<TextBlock>("StepText").Text})");
            h.Expect(globalLog.Contains("basin hopping, hops 6, perturbation 10%, accept worse 0%, seed ") && globalLog.Contains("hop 6/6")
                     && globalLog.Contains("<- best"), "the log has every hop, the new bests marked");
            Click(window, global.GetControl<Button>("AcceptButton"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            double after = MeritFrom(summary.Text ?? "");
            h.Expect(after < before, $"accepted, the merit fell ({before:G6} → {after:G6})");
        });
    }

    /// <summary>THE USER: remove the menu item and the button "Where would a layer help?" (2026-10-13).</summary>
    public static void TheNeedleAdviceIsGone(Harness h, HeadlessUnitTestSession session)
    {
        h.Section("'Where would a layer help?' is gone from the menu and the button strip");

        WithWindow(session, window =>
        {
            h.Expect(window.FindControl<Control>("NeedleButton") is null && window.FindControl<Control>("NeedleMenu") is null,
                     "neither control exists");
            var labels = window.GetVisualDescendants().OfType<Button>().Select(b => b.Content as string)
                .Concat(Find<MenuItem>(window, "OptimizationMenu").Items.OfType<MenuItem>().Select(m => m.Header as string));
            h.Expect(!labels.Any(l => l?.Contains("layer help", StringComparison.OrdinalIgnoreCase) == true),
                     "and no label says it");
        });
    }

    /// <summary>
    /// THE USER: buttons to build a merit function quickly - Clear all, Delete operands, Adjust
    /// weight, Add operands, Add multiple AOI, Add single Wv. Clicked where they sit, the dialogs
    /// filled as a person fills them.
    /// </summary>
    public static void TheMeritFunctionIsBuiltWithTheNewButtons(Harness h, HeadlessUnitTestSession session)
    {
        h.Section("Merit Function: Clear all, then Add operands, Add multiple AOI, Delete operands and Adjust weight");

        WithWindow(session, window =>
        {
            OpenSample(window);
            window.GetControl<TabControl>("Tabs").SelectedItem = Find<TabItem>(window, "MeritTab");
            window.UpdateLayout();
            var grid = Find<DataGrid>(window, "OperandGrid");
            int Rows() => grid.ItemsSource?.Cast<object>().Count() ?? 0;
            T Dialog<T>() where T : Window
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                var d = window.OwnedWindows.OfType<T>().Last();
                d.UpdateLayout();
                return d;
            }

            // Clear all asks first, and Cancel is the default.
            Click(window, Find<Button>(window, "ClearOperandsButton"));
            var confirm = Dialog<ConfirmDialog>();
            h.Expect(confirm.GetControl<TextBlock>("MessageText").Text?.Contains("Delete all") == true,
                     $"Clear all asks first: {confirm.GetControl<TextBlock>("MessageText").Text}");
            Click(window, confirm.GetControl<Button>("YesButton"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            h.Expect(Rows() == 0, $"and empties the table ({Rows()} rows)");
            h.Expect(Find<TextBlock>(window, "StatusText").Text?.Contains("Add operands") == true,
                     $"saying what to do next: {Find<TextBlock>(window, "StatusText").Text}");
            h.Expect(Find<TextBlock>(window, "MeritText").Text == "merit —", $"with no merit ({Find<TextBlock>(window, "MeritText").Text})");

            // Add operands: RA at most 0.5% from 420 to 680 nm every 20 nm, at normal incidence.
            Click(window, Find<Button>(window, "AddOperandsButton"));
            var add = Dialog<OperandSweepDialog>();
            h.Expect(add.Title == "Add Operands" && !add.GetControl<TextBox>("Angle2Box").IsVisible,
                     "Add Operands asks for one AOI");
            add.GetControl<TextBox>("Wavelength1Box").Text = "420";
            add.GetControl<TextBox>("Wavelength2Box").Text = "680";
            add.GetControl<TextBox>("WavelengthStepBox").Text = "20";
            h.Expect(add.GetControl<TextBlock>("CountText").Text == "14 rows will be added.",
                     $"and says how many rows it will add ({add.GetControl<TextBlock>("CountText").Text})");
            Click(window, add.GetControl<Button>("OkButton"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            h.Expect(Rows() == 14, $"14 rows in the table ({Rows()})");
            h.Expect(MeritFrom(Find<TextBlock>(window, "SummaryText").Text ?? "") >= 0,
                     $"and the design is evaluated again ({Find<TextBlock>(window, "SummaryText").Text})");

            // Add multiple AOI: the same band at 30 and 45 degrees, prefilled from the table.
            Click(window, Find<Button>(window, "AddMultipleAoiButton"));
            var many = Dialog<OperandSweepDialog>();
            h.Expect(many.GetControl<TextBox>("Wavelength1Box").Text == "420" && many.GetControl<TextBox>("WavelengthStepBox").Text == "20",
                     "Add multiple AOI starts on the band the table covers");
            many.GetControl<TextBox>("Angle1Box").Text = "30";
            many.GetControl<TextBox>("Angle2Box").Text = "45";
            many.GetControl<TextBox>("AngleStepBox").Text = "15";
            Click(window, many.GetControl<Button>("OkButton"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            h.Expect(Rows() == 42, $"28 more rows, 14 at each angle ({Rows()})");

            // Delete operands: AOI above 40 degrees - the 45 degree block.
            Click(window, Find<Button>(window, "DeleteOperandsButton"));
            var delete = Dialog<OperandFilterDialog>();
            h.Expect(delete.GetControl<TextBlock>("CountText").Text == "42 rows match.",
                     $"with nothing filtered, every row matches ({delete.GetControl<TextBlock>("CountText").Text})");
            delete.GetControl<ComboBox>("AngleTestBox").SelectedItem = "AOI >";
            delete.GetControl<TextBox>("Angle1Box").Text = "40";
            h.Expect(delete.GetControl<TextBlock>("CountText").Text == "14 rows match.",
                     $"AOI > 40 matches the 45° block ({delete.GetControl<TextBlock>("CountText").Text})");
            Click(window, delete.GetControl<Button>("OkButton"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            h.Expect(Rows() == 28, $"deleted ({Rows()} left)");

            // Adjust weight: the 30 degree rows from 500 to 600 nm get weight 3.
            Click(window, Find<Button>(window, "AdjustWeightButton"));
            var weight = Dialog<OperandFilterDialog>();
            h.Expect(weight.Title == "Adjust Weight" && weight.GetControl<TextBox>("WeightBox").IsVisible, "Adjust Weight asks for the weight");
            weight.GetControl<TextBox>("Wavelength1Box").Text = "500";
            weight.GetControl<TextBox>("Wavelength2Box").Text = "600";
            weight.GetControl<ComboBox>("AngleTestBox").SelectedItem = "AOI between";
            weight.GetControl<TextBox>("Angle1Box").Text = "29";
            weight.GetControl<TextBox>("Angle2Box").Text = "31";
            weight.GetControl<TextBox>("WeightBox").Text = "3";
            h.Expect(weight.GetControl<TextBlock>("CountText").Text == "6 rows match.",
                     $"500, 520, ..., 600 nm at 30° ({weight.GetControl<TextBlock>("CountText").Text})");
            Click(window, weight.GetControl<Button>("OkButton"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var weights = grid.ItemsSource!.Cast<MeritRow>().Select(r => r.WeightText).ToArray();
            h.Expect(weights.Count(w => w == "3") == 6 && weights.Count(w => w == "1") == 22,
                     $"six rows at weight 3, the rest at 1 ({string.Join(" ", weights)})");
        });
    }

    /// <summary>
    /// THE ONE CLAIM THAT REALLY NEEDED RENDERING. The frame is captured and the plot area
    /// examined: an empty chart is close to one colour, and a chart with a spectrum on it is not.
    /// </summary>
    public static void ThePlotActuallyPaintsACurve(Harness h, HeadlessUnitTestSession session)
    {
        h.Section("the spectrum plot really paints: its pixels are read back from a frame");

        WithWindow(session, window =>
        {
            ShowTab(window, "SpectrumTab");
            var plot = Find<SpectrumPlot>(window, "Plot");
            h.Expect(plot.Bounds.Width > 100 && plot.Bounds.Height > 100,
                     $"the plot has room to draw ({plot.Bounds.Width}x{plot.Bounds.Height})");

            // BEFORE IS NOT ALWAYS EMPTY: on Linux (2026-10-24) the window still showed an earlier
            // test's design, its curves drawn, so "more colours after" failed on a correct plot.
            // What opening a design must do is repaint the plot with its own spectrum.
            int beforeColours = DistinctColoursIn(window, plot, h, "before", out ulong before);
            ShowTab(window, "DesignTab");
            OpenSample(window);
            ShowTab(window, "SpectrumTab");
            int drawnColours = DistinctColoursIn(window, plot, h, "after", out ulong after);

            h.Expect(after != before,
                     $"opening a design repainted the plot ({beforeColours} -> {drawnColours} colours)");
            h.Expect(drawnColours >= 4,
                     $"and there are enough of them to be axes and a curve ({drawnColours})");
        });
    }

    public static void FailuresReachTheStatusLineRatherThanThrowing(Harness h,
                                                                    HeadlessUnitTestSession session)
    {
        h.Section("a bad spectrum range is reported instead of closing the window");

        WithWindow(session, window =>
        {
            // Refine with nothing open was checked here; the window now always has a design.
            var status = Find<TextBlock>(window, "StatusText");
            OpenSample(window);
            ShowTab(window, "SpectrumTab");
            Find<TextBox>(window, "StepsBox").Text = "1";
            Click(window, Find<Button>(window, "PlotButton"));
            h.Expect(window.IsVisible, "a one-step spectrum does not take the window down");
            h.Expect(status.Text is not null && status.Text.Length > 0, "and is reported");
        });
    }

    // ---- reading the window back --------------------------------------------------------------

    /// <summary>The merit out of "merit 0.0108  ·  8 layers, …" — the text a user reads.</summary>
    private static double MeritFrom(string summary)
    {
        const string key = "merit ";
        int at = summary.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return -1;
        var rest = summary[(at + key.Length)..];
        var token = new string(rest.TakeWhile(c => char.IsDigit(c) || c is '.' or 'E' or 'e'
                                                   or '+' or '-').ToArray());
        return double.TryParse(token, System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? v : -1;
    }

    /// <summary>
    /// How many distinct colours appear inside the plot control's bounds in the rendered frame.
    /// A blank chart is its background plus a little text; a drawn one has grid, axes and a curve.
    /// </summary>
    private static unsafe int DistinctColoursIn(MainWindow window, Control plot, Harness h,
                                                string when, out ulong checksum)
    {
        checksum = 1469598103934665603UL;
        using var frame = window.CaptureRenderedFrame();
        if (frame is null) { h.Fail($"no frame was rendered ({when})"); checksum = 0; return 0; }

        var origin = plot.TranslatePoint(new Point(0, 0), window) ?? new Point(0, 0);
        int x0 = (int)origin.X, y0 = (int)origin.Y;
        int w = (int)plot.Bounds.Width, hgt = (int)plot.Bounds.Height;

        var colours = new HashSet<uint>();
        using (var buffer = frame.Lock())
        {
            var pixels = (byte*)buffer.Address;
            int stride = buffer.RowBytes;
            int maxX = Math.Min(x0 + w, buffer.Size.Width);
            int maxY = Math.Min(y0 + hgt, buffer.Size.Height);

            for (int y = Math.Max(0, y0); y < maxY; y += 2)
                for (int x = Math.Max(0, x0); x < maxX; x += 2)
                {
                    uint* row = (uint*)(pixels + y * stride);
                    colours.Add(row[x]);
                    checksum = (checksum ^ row[x]) * 1099511628211UL;
                }
        }
        return colours.Count;
    }

    // ---- the tables fit --------------------------------------------------------------------

    public static void TheTablesFitTheirPane(Harness h, HeadlessUnitTestSession session)
    {
        h.Section("both tables fit their pane: no clipped column, no truncated header");

        double before = 0, after = 0;
        WithWindow(session, window =>
        {
            OpenSample(window);
            CheckTablesFit(h, window, "at the window's opening size");
            ShowTab(window, "MeritTab");
            before = Find<DataGrid>(window, "OperandGrid").Bounds.Width;

            // The substrate label belongs under the last layer, not at the foot of an empty table.
            ShowTab(window, "DesignTab");
            var layers = Find<DataGrid>(window, "LayerGrid");
            var gap = Find<TextBlock>(window, "SubstrateText").TranslatePoint(new Point(0, 0), layers);
            h.Expect(gap is not null && gap.Value.Y - layers.Bounds.Height < 12,
                     $"the substrate label sits directly under the layer table "
                     + $"({(gap?.Y ?? 0) - layers.Bounds.Height:F0} px below it)");
        });

        // FIXED COLUMNS DO NOT STRETCH. The merit table was once a star column that grew with
        // the window, and the user found the wide column hard to read; its columns now have
        // fixed widths, after LensHH-LT. So a wider window leaves the table as it is, and at the
        // opening size the whole table fits, with nothing under the edge. Two windows rather
        // than one resized, because the headless platform ignores Width on a shown window.
        WithWindow(session, window =>
        {
            OpenSample(window);
            CheckTablesFit(h, window, "in a wider window");
            ShowTab(window, "MeritTab");
            after = Find<DataGrid>(window, "OperandGrid").Bounds.Width;
        }, width: 1580);

        h.Expect(Math.Abs(after - before) < 1.0,
                 $"a wider window leaves the fixed columns alone ({before:F0} -> {after:F0} px)");
        h.Expect(before < 1180 - 16, $"and the table fits the opening window ({before:F0} px)");
    }

    // ---- the tabs and the menu -------------------------------------------------------------

    public static void EachTabShowsItsPanel(Harness h, HeadlessUnitTestSession session)
    {
        h.Section("clicking a tab header shows that panel and only that panel");

        WithWindow(session, window =>
        {
            OpenSample(window);
            var tabs = Find<TabControl>(window, "Tabs");
            h.Expect(tabs.SelectedItem == Find<TabItem>(window, "DesignTab"),
                     "the window opens on the Design tab");

            // Headers at body size, the selected one bold - measured from the text actually drawn,
            // not the TabItem's own property, so a theme that ignores the setter is caught.
            foreach (var name in new[] { "DesignTab", "MeritTab", "SpectrumTab" })
            {
                var text = Find<TabItem>(window, name).GetVisualDescendants().OfType<TextBlock>()
                                                      .FirstOrDefault();
                h.Expect(text is not null && text.FontSize <= 15,
                         $"{name}'s header is body size ({text?.FontSize} px)");
                bool selected = name == "DesignTab";
                h.Expect(text is not null && (text.FontWeight >= Avalonia.Media.FontWeight.Bold) == selected,
                         $"and {(selected ? "bold, being selected" : "not bold")} ({text?.FontWeight})");
            }

            foreach (var (tab, inside) in new[] { ("MeritTab", "OperandGrid"),
                                                  ("SpectrumTab", "Plot"),
                                                  ("DesignTab", "LayerGrid") })
            {
                ShowTab(window, tab);
                h.Expect(tabs.SelectedItem == Find<TabItem>(window, tab), $"clicking {tab} selects it");
                var control = Find<Control>(window, inside);
                h.Expect(control.IsEffectivelyVisible && control.Bounds.Width > 100,
                         $"and {inside} is laid out on it ({control.Bounds.Width:F0} px wide)");
            }
        });
    }

    public static void TheMenuReachesWhatTheButtonsDo(Harness h, HeadlessUnitTestSession session)
    {
        h.Section("the menu, clicked with the mouse, opens samples and panels");

        WithWindow(session, window =>
        {
            // File > Open sample opens the sample browser (TheSampleBrowserFindsAndOpens), so the
            // item is checked to be there and a sample is opened the way it opens one.
            var file = Find<MenuItem>(window, "FileMenu");
            h.Expect(file.Items.OfType<MenuItem>().Any(m => m.Name == "OpenSampleMenu"
                                                          && (m.Header as string) == "Open sa_mple…"),
                     "File has Open sample…");
            h.Pin(Directory.GetFiles(ThinFilm.Api.Samples.ExamplesFolder, "*.tfd", SearchOption.AllDirectories).Length, 10,
                  "and the ten examples ship beside the program");
            OpenSample(window, "V-coat");
            h.Expect(window.Title?.Contains("V-coat", StringComparison.Ordinal) == true,
                     $"a sample opens under its file name ({window.Title})");

            Click(window, Find<MenuItem>(window, "AnalysisMenu"));
            Click(window, Find<MenuItem>(window, "SpectrumMenu"));
            h.Expect(Find<TabControl>(window, "Tabs").SelectedItem == Find<TabItem>(window, "SpectrumTab"),
                     "Analysis > Spectrum shows the Spectrum tab");
        });
    }

    public static void AMeritCellIsEditedByTyping(Harness h, HeadlessUnitTestSession session)
    {
        h.Section("a merit cell is edited by clicking it, typing and pressing Enter");

        WithWindow(session, window =>
        {
            OpenSample(window);
            ShowTab(window, "MeritTab");
            var grid = Find<DataGrid>(window, "OperandGrid");

            h.Expect((grid.Columns[2].Header as string) == "Wv (nm)",
                     $"the wavelength column is headed in nm ({grid.Columns[2].Header})");
            h.Expect(!grid.Columns.Any(c => (c.Header as string) is "met" or "at"),
                     "and there is no met or at column");
            var temperature = Cell(window, grid, 0, TemperatureColumn);
            h.Expect((grid.Columns[TemperatureColumn].Header as string) == "Temp (K)" && temperature is { IsEnabled: false, Text: "" },
                     "the Temp (K) column is blank and disabled on a row that is not EMIT or AERATIO");

            // Row 1's weight: a click on the cell, the text replaced, Enter.
            var box = Cell(window, grid, 0, WeightColumn);
            h.Expect(box is not null && box.IsEnabled, "row 1's weight cell is there and editable");
            if (box is null) return;
            Click(window, box);
            box.SelectAll();
            window.KeyTextInput("2.5");
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var first = grid.ItemsSource!.Cast<MeritRow>().First();
            h.Expect(first.WeightText == "2.5", $"and the design holds the typed weight ({first.WeightText})");

            // A cell that does not apply is blank and cannot be focused into.
            int mint = grid.ItemsSource!.Cast<MeritRow>().ToList().FindIndex(r => r.Code == "MINT");
            grid.ScrollIntoView(grid.ItemsSource!.Cast<object>().ElementAt(mint), null);
            window.UpdateLayout();
            var wv = Cell(window, grid, mint, 2);
            h.Expect(wv is not null && !wv.IsEnabled && string.IsNullOrEmpty(wv.Text),
                     $"MINT's wavelength cell is blank and disabled ('{wv?.Text}', enabled {wv?.IsEnabled})");
        });
    }

    public static void LayersAreEditedWithTheMouse(Harness h, HeadlessUnitTestSession session)
    {
        h.Section("the Design tab's buttons insert, move and delete layers, and Vary is a click");

        WithWindow(session, window =>
        {
            OpenSample(window);
            var grid = Find<DataGrid>(window, "LayerGrid");
            int Rows() => grid.ItemsSource!.Cast<LayerRow>().Count();
            h.Pin(Rows(), 4, "four layers to start");

            grid.SelectedIndex = 1;
            Click(window, Find<Button>(window, "InsertLayerButton"));
            h.Pin(Rows(), 5, "Insert layer adds one");

            grid.SelectedIndex = 2;
            Click(window, Find<Button>(window, "MoveLayerUpButton"));
            h.Pin(grid.SelectedIndex, 1, "Move up moves it, and the selection follows");

            Click(window, Find<Button>(window, "DeleteLayerButton"));
            h.Pin(Rows(), 4, "Delete layer removes the selected one");

            // Vary, clicked where the checkbox really is.
            var row = grid.GetVisualDescendants().OfType<DataGridRow>()
                          .First(r => r.DataContext is LayerRow { Index: 0 });
            var box = row.GetVisualDescendants().OfType<CheckBox>().FirstOrDefault();
            h.Expect(box is not null && box.IsChecked == true, "layer 1's Vary box is there, ticked");
            if (box is null) return;
            Click(window, box);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var first = grid.ItemsSource!.Cast<LayerRow>().First();
            h.Expect(!first.Variable, "and clicking it fixes the layer in the design");
            h.Expect(Find<TextBlock>(window, "SummaryText").Text?.Contains("3 variables") == true,
                     $"with one variable fewer in the summary ({Find<TextBlock>(window, "SummaryText").Text})");
        });
    }

    public static void NewDesignIsMadeFromTheDialog(Harness h, HeadlessUnitTestSession session)
    {
        h.Section("File > New design opens a dialog; Create refuses what cannot be built, then builds");

        WithWindow(session, window =>
        {
            Click(window, Find<MenuItem>(window, "FileMenu"));
            Click(window, Find<MenuItem>(window, "NewDesignMenu"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            var dialog = window.OwnedWindows.OfType<NewDesignDialog>().FirstOrDefault();
            h.Expect(dialog is not null && dialog.IsVisible, "the New design dialog opens, owned by the window");
            if (dialog is null) return;
            dialog.UpdateLayout();

            // Untick every material, press Create: refused, and the dialog stays with the reason.
            foreach (var box in dialog.GetControl<WrapPanel>("MaterialChoices").Children.OfType<CheckBox>())
                box.IsChecked = false;
            Click(window, dialog.GetControl<Button>("CreateButton"));
            var problem = dialog.GetControl<TextBlock>("ProblemText");
            h.Expect(dialog.IsVisible && problem.IsVisible
                     && problem.Text?.Contains("coating material") == true,
                     $"with no material it stays open and says why: {problem.Text}");

            // Tick SiO2 and TiO2, ask for three layers, name it, create.
            foreach (var box in dialog.GetControl<WrapPanel>("MaterialChoices").Children.OfType<CheckBox>())
                box.IsChecked = (string)box.Tag! is "SiO2" or "TiO2";
            dialog.GetControl<TextBox>("LayersBox").Text = "3";
            dialog.GetControl<TextBox>("NameBox").Text = "my AR";
            Click(window, dialog.GetControl<Button>("CreateButton"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            h.Expect(!dialog.IsVisible, "Create closes it");
            h.Expect(window.Title?.Contains("my AR") == true, $"and the window holds the new design ({window.Title})");
            var layers = Find<DataGrid>(window, "LayerGrid").ItemsSource!.Cast<LayerRow>()
                             .Select(l => l.Material).ToArray();
            h.Expect(layers.SequenceEqual(["SiO2", "TiO2", "SiO2"]),
                     $"with the three layers asked for ({string.Join(" ", layers)})");
        });
    }

    public static void MaterialsAreCopiedInFromTheDatabase(Harness h, HeadlessUnitTestSession session)
    {
        h.Section("Edit > Materials: copy a database material in, make another the substrate, OK");

        WithWindow(session, window =>
        {
            OpenSample(window);
            Click(window, Find<MenuItem>(window, "EditMenu"));
            Click(window, Find<MenuItem>(window, "MaterialsMenu"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            var dialog = window.OwnedWindows.OfType<MaterialsDialog>().FirstOrDefault();
            h.Expect(dialog is not null && dialog.IsVisible, "the Materials dialog opens");
            if (dialog is null) return;
            dialog.UpdateLayout();

            // The database this window reads is the test's own, made in its temporary home.
            var path = dialog.GetControl<TextBlock>("DatabasePathText").Text ?? "";
            h.Expect(path.Contains("thinfilm-gui-tests-", StringComparison.Ordinal),
                     $"from the test's own database, never the profile of whoever runs it ({path})");

            var database = dialog.GetControl<ListBox>("DatabaseList");
            var names = database.ItemsSource!.Cast<string>().ToList();
            h.Expect(names[0] == "air" && names.Contains("HfO2"), $"listing air and the database ({string.Join(" ", names)})");

            database.SelectedIndex = names.IndexOf("HfO2");
            Click(window, dialog.GetControl<Button>("CopyButton"));
            database.SelectedIndex = names.IndexOf("MgF2");
            Click(window, dialog.GetControl<Button>("UseSubstrateButton"));
            var design = dialog.GetControl<ListBox>("DesignList").ItemsSource!.Cast<string>().ToList();
            h.Expect(design.Contains("HfO2") && design.Contains("Substrate: MgF2"),
                     $"the design side shows the copy and the new substrate ({string.Join(" | ", design)})");
            h.Expect(dialog.GetControl<SpectrumPlot>("Preview").Series is { Count: 1 } s && s[0].Name == "n",
                     "and the preview draws n for the selected material - and not a k that is zero everywhere");

            Click(window, dialog.GetControl<Button>("OkButton"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            h.Expect(!dialog.IsVisible, "OK closes it");
            h.Expect(Find<TextBlock>(window, "SubstrateText").Text?.Contains("MgF2") == true,
                     $"the design is now on MgF2 ({Find<TextBlock>(window, "SubstrateText").Text})");
            var options = Find<DataGrid>(window, "LayerGrid").ItemsSource!.Cast<LayerRow>().First().MaterialOptions;
            h.Expect(options.Contains("HfO2"), $"and every layer can be made of HfO2 ({string.Join(" ", options)})");
        });
    }

    public static void ACatalogueGlassIsChosenAndShownReadOnly(Harness h, HeadlessUnitTestSession session)
    {
        h.Section("Edit > Materials: a catalogue glass as the substrate, named and not editable");

        WithWindow(session, window =>
        {
            OpenSample(window);
            Click(window, Find<MenuItem>(window, "EditMenu"));
            Click(window, Find<MenuItem>(window, "MaterialsMenu"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var dialog = window.OwnedWindows.OfType<MaterialsDialog>().First();
            dialog.UpdateLayout();

            var catalogues = dialog.GetControl<ComboBox>("CatalogueBox");
            h.Expect((catalogues.SelectedItem as string) == "SCHOTT"
                     && catalogues.ItemsSource!.Cast<string>().SequenceEqual(["CDGM", "HOYA", "OHARA", "SCHOTT", "SUMITA"]),
                     "the five catalogues are offered, SCHOTT selected");

            // Typed, as a person filters: a click into the box, then the keys.
            Click(window, dialog.GetControl<TextBox>("GlassFilterBox"));
            dialog.KeyTextInput("BK7");
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var glasses = dialog.GetControl<ListBox>("GlassList");
            var found = glasses.ItemsSource!.Cast<string>().ToList();
            h.Expect(found.Contains("N-BK7") && found.All(g => g.Contains("BK7")),
                     $"typing BK7 filters the glasses ({string.Join(" ", found)})");

            glasses.SelectedItem = "N-BK7";
            Click(window, dialog.GetControl<Button>("UseSubstrateButton"));
            var design = dialog.GetControl<ListBox>("DesignList");
            h.Expect(design.ItemsSource!.Cast<string>().Contains("Substrate: SCHOTT N-BK7"),
                     "and Use glass as substrate puts it on the design side");

            design.SelectedIndex = 1;
            dialog.UpdateLayout();
            h.Expect(!dialog.GetControl<Grid>("EditorPanel").IsEnabled
                     && dialog.GetControl<TextBlock>("EditorTitle").Text?.Contains("not editable") == true,
                     $"selected, it is shown read-only ({dialog.GetControl<TextBlock>("EditorTitle").Text})");

            Click(window, dialog.GetControl<Button>("OkButton"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            h.Expect(Find<TextBlock>(window, "SubstrateText").Text?.Contains("SCHOTT N-BK7") == true,
                     $"OK, and the design is on SCHOTT N-BK7 ({Find<TextBlock>(window, "SubstrateText").Text})");
        });
    }

    /// <summary>
    /// THE USER'S REPORT: "I select a material and hit Use as substrate; it warns to select a
    /// material from the database." The glass list and the database list each had their own
    /// button, and the substrate button read only the database. Now one selection, one set of
    /// buttons, each saying what it will do.
    /// </summary>
    public static void OneSelectionDrivesTheSourceButtons(Harness h, HeadlessUnitTestSession session)
    {
        h.Section("Edit > Materials: whatever is selected on the left, Use as substrate uses it");

        WithWindow(session, window =>
        {
            OpenSample(window);
            Click(window, Find<MenuItem>(window, "EditMenu"));
            Click(window, Find<MenuItem>(window, "MaterialsMenu"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var dialog = window.OwnedWindows.OfType<MaterialsDialog>().First();
            dialog.UpdateLayout();
            var copy = dialog.GetControl<Button>("CopyButton");
            var substrate = dialog.GetControl<Button>("UseSubstrateButton");
            var incident = dialog.GetControl<Button>("UseIncidentButton");
            var database = dialog.GetControl<ListBox>("DatabaseList");
            var glasses = dialog.GetControl<ListBox>("GlassList");

            h.Expect(!copy.IsEnabled && !substrate.IsEnabled && !incident.IsEnabled,
                     "with nothing selected, no button is live");
            h.Expect(dialog.GetControl<TextBlock>("SourceHint").Text?.StartsWith("Select a material") == true,
                     "and the hint says what to do");

            // A database material first, then a glass: the glass replaces it as the selection.
            database.SelectedItem = "MgF2";
            h.Expect((substrate.Content as string) == "Use MgF2 as substrate →" && copy.IsEnabled && incident.IsEnabled,
                     $"a database material: every button live, each naming it ({substrate.Content})");
            glasses.SelectedItem = "N-BK7";
            h.Expect(database.SelectedItem is null, "selecting a glass clears the database selection");
            h.Expect((substrate.Content as string) == "Use SCHOTT N-BK7 as substrate →" && !copy.IsEnabled && !incident.IsEnabled,
                     $"a glass: only Use as substrate, naming it ({substrate.Content})");

            Click(window, substrate);
            var message = dialog.GetControl<TextBlock>("ProblemText").Text ?? "";
            h.Expect(message.Contains("SCHOTT N-BK7 is now the substrate") && message.Contains("Press OK"),
                     $"and it works, saying so and that OK applies it: {message}");
            h.Expect(!message.Contains("select a material", StringComparison.OrdinalIgnoreCase),
                     "never 'select a material from the database'");
            h.Expect(dialog.GetControl<ListBox>("DesignList").ItemsSource!.Cast<string>().Contains("Substrate: SCHOTT N-BK7"),
                     "and the design side shows the new substrate");

            database.SelectedItem = "Al2O3";
            h.Expect(glasses.SelectedItem is null, "and selecting in the database clears the glass");
            Click(window, substrate);
            h.Expect(dialog.GetControl<ListBox>("DesignList").ItemsSource!.Cast<string>().Contains("Substrate: Al2O3"),
                     "so the same button now uses Al2O3");
            dialog.Close(false);
        });
    }

    public static void AConstantMaterialIsReplacedByARealOne(Harness h, HeadlessUnitTestSession session)
    {
        h.Section("Edit > Materials: Replace a constant SiO2 with the database's, and Upgrade");

        WithWindow(session, window =>
        {
            OpenSample(window);
            Click(window, Find<MenuItem>(window, "EditMenu"));
            Click(window, Find<MenuItem>(window, "MaterialsMenu"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var dialog = window.OwnedWindows.OfType<MaterialsDialog>().First();
            dialog.UpdateLayout();
            var replace = dialog.GetControl<Button>("ReplaceButton");
            var design = dialog.GetControl<ListBox>("DesignList");
            var labels = design.ItemsSource!.Cast<string>().ToList();

            h.Expect(!replace.IsEnabled, "Replace is off until both sides are chosen");
            h.Expect(dialog.GetControl<Button>("UpgradeButton").IsEnabled, "Upgrade is on: there are constant materials with a match");

            design.SelectedIndex = labels.FindIndex(l => l.StartsWith("SiO2"));
            dialog.GetControl<ListBox>("DatabaseList").SelectedItem = "SiO2";
            h.Expect(replace.IsEnabled && (replace.Content as string) == "Replace SiO2 with database SiO2",
                     $"with both chosen it says exactly what it will do ({replace.Content})");
            var hint = dialog.GetControl<TextBlock>("ReplaceHint").Text ?? "";
            h.Expect(hint.Contains("Now: constant n = 1.46") && hint.Contains("Becomes: Sellmeier"),
                     $"and what SiO2 is now and what it becomes: {hint}");
            h.Expect(dialog.GetControl<TextBlock>("DesignHint").Text?.Contains("constant index") == true,
                     $"the selected constant material is called out ({dialog.GetControl<TextBlock>("DesignHint").Text})");
            dialog.UpdateLayout();   // the hints above it have moved the button
            Click(window, replace);
            var message = dialog.GetControl<TextBlock>("ProblemText").Text ?? "";
            h.Expect(message.Contains("its 2 layers follow"), $"and says the layers follow: {message}");

            Click(window, dialog.GetControl<Button>("OkButton"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var rows = Find<DataGrid>(window, "LayerGrid").ItemsSource!.Cast<LayerRow>().Select(l => l.Material).ToArray();
            h.Expect(rows.SequenceEqual(["SiO2", "Nb2O5", "SiO2", "Nb2O5"]), $"the stack is unchanged in shape ({string.Join(" ", rows)})");
        });
    }

    public static void DispersionIsEditedInItsOwnDialog(Harness h, HeadlessUnitTestSession session)
    {
        h.Section("Edit dispersion: a constant SiO2 loads the library's Sellmeier, field by field");

        WithWindow(session, window =>
        {
            OpenSample(window);
            Click(window, Find<MenuItem>(window, "EditMenu"));
            Click(window, Find<MenuItem>(window, "MaterialsMenu"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var materials = window.OwnedWindows.OfType<MaterialsDialog>().First();
            materials.UpdateLayout();
            var design = materials.GetControl<ListBox>("DesignList");
            design.SelectedIndex = design.ItemsSource!.Cast<string>().ToList().FindIndex(l => l.StartsWith("SiO2"));
            h.Expect(materials.GetControl<TextBlock>("ModelSummary").Text == "constant n = 1.46",
                     $"the design's SiO2 is a constant ({materials.GetControl<TextBlock>("ModelSummary").Text})");

            Click(window, materials.GetControl<Button>("EditDispersionButton"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var dispersion = materials.OwnedWindows.OfType<CoefficientsDialog>().FirstOrDefault();
            h.Expect(dispersion is not null && dispersion.IsVisible, "Edit dispersion opens its own dialog");
            if (dispersion is null) return;
            dispersion.UpdateLayout();
            h.Expect(dispersion.GetControl<Grid>("ConstantPanel").IsVisible
                     && dispersion.GetControl<TextBox>("ConstantN").Text == "1.46",
                     "showing the constant's n in its own field");

            var library = dispersion.GetControl<ComboBox>("LibraryBox");
            library.SelectedIndex = library.ItemsSource!.Cast<string>().ToList().FindIndex(s => s.StartsWith("SiO2"));
            Click(window, dispersion.GetControl<Button>("LoadButton"));
            dispersion.UpdateLayout();
            h.Expect(dispersion.GetControl<Grid>("SellmeierPanel").IsVisible
                     && dispersion.GetControl<TextBox>("B1").Text == "0.6961663"
                     && dispersion.GetControl<TextBox>("C3").Text == "97.934",
                     "Load from library fills the Sellmeier fields, B1 to C3");
            h.Expect(dispersion.GetControl<SpectrumPlot>("Preview").Series?.Count == 1,
                     "and the preview draws the curve");

            Click(window, dispersion.GetControl<Button>("OkButton"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            materials.UpdateLayout();
            h.Expect(!dispersion.IsVisible, "OK closes the dispersion dialog");
            h.Expect(materials.GetControl<TextBlock>("ModelSummary").Text?.StartsWith("Sellmeier") == true,
                     $"and the design's SiO2 is now Sellmeier ({materials.GetControl<TextBlock>("ModelSummary").Text})");
            materials.Close(false);
        });
    }

    public static void SystemParametersSetTheWavelengthUnit(Harness h, HeadlessUnitTestSession session)
    {
        h.Section("Edit > System Parameters, a modal dialog, switches the wavelength unit to µm");

        WithWindow(session, window =>
        {
            OpenSample(window);
            Click(window, Find<MenuItem>(window, "EditMenu"));
            Click(window, Find<MenuItem>(window, "SystemParametersMenu"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            var dialog = window.OwnedWindows.OfType<SystemParametersDialog>().FirstOrDefault();
            h.Expect(dialog is not null && dialog.IsVisible, "the System Parameters dialog opens, owned by the window");
            if (dialog is null) return;
            dialog.UpdateLayout();

            var micron = dialog.GetControl<RadioButton>("MicronChoice");
            h.Expect(micron.IsChecked != true, "with nm chosen, as the design holds");
            Click(window, micron);
            Click(window, dialog.GetControl<Button>("OkButton"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            h.Expect(!dialog.IsVisible, "OK closes it");
            var grid = Find<DataGrid>(window, "OperandGrid");
            h.Expect((grid.Columns[2].Header as string) == "Wv (µm)",
                     $"and the wavelength column is now headed in µm ({grid.Columns[2].Header})");
            h.Expect(grid.ItemsSource!.Cast<MeritRow>().First().WavelengthText == "0.42",
                     $"with row 1 at 0.42 µm ({grid.ItemsSource!.Cast<MeritRow>().First().WavelengthText})");
            h.Expect(Find<SpectrumPlot>(window, "Plot").XTitle == "wavelength (µm)",
                     "and the spectrum's axis follows");
        });
    }

    /// <summary>THE USER (2026-10-18): Preferences is renamed System Parameters, and holds whether the
    /// substrate's back surface is in the calculation, the substrate thickness in mm, and whether the
    /// angle of incidence is given in air or in the ambient medium.</summary>
    public static void TheSpectrumTabPlotsAgainstAngle(Harness h, HeadlessUnitTestSession session)
    {
        h.Section("Analysis > Spectrum: vs wavelength or vs angle, each with its own boxes");

        WithWindow(session, window =>
        {
            OpenSample(window);
            ShowTab(window, "SpectrumTab");
            var from = Find<TextBox>(window, "FromBox");
            var to = Find<TextBox>(window, "ToBox");
            var steps = Find<TextBox>(window, "StepsBox");
            var fixedBox = Find<TextBox>(window, "AngleBox");
            var plot = Find<SpectrumPlot>(window, "Plot");
            h.Expect(Find<RadioButton>(window, "VsWavelengthButton").IsChecked == true
                     && Find<TextBlock>(window, "FixedLabel").Text == "AOI (°)",
                     "it starts against wavelength, at an angle of incidence");
            from.Text = "420";

            Find<RadioButton>(window, "VsAngleButton").IsChecked = true;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            h.Expect(from.Text == "0" && to.Text == "89" && steps.Text == "90" && fixedBox.Text == "550"
                     && Find<TextBlock>(window, "FixedLabel").Text == "λ (nm)" && plot.XTitle == "angle of incidence (°)",
                     $"switched to angle: 0 to 89°, 90 steps, at 550 nm ({from.Text} {to.Text} {steps.Text} {fixedBox.Text})");
            h.Expect(plot.Series is { Count: > 0 } s && s[0].X.Length == 90 && s[0].X[89] == 89,
                     "and it redraws against angle");

            to.Text = "60"; steps.Text = "7"; fixedBox.Text = "633";
            Click(window, Find<Button>(window, "PlotButton"));
            string note = Find<TextBlock>(window, "PlotNote").Text ?? "";
            h.Expect(plot.Series![0].X.Length == 7 && note.Contains("633 nm") && note.Contains("incident medium"),
                     $"Redraw takes the boxes; the note names the wavelength and where angles are ({note})");

            Find<RadioButton>(window, "VsWavelengthButton").IsChecked = true;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            h.Expect(from.Text == "420" && fixedBox.Text == "0" && plot.XTitle.StartsWith("wavelength"),
                     $"back on wavelength, the boxes are as they were ({from.Text}, {fixedBox.Text})");
            Find<RadioButton>(window, "VsAngleButton").IsChecked = true;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            h.Expect(to.Text == "60" && steps.Text == "7" && fixedBox.Text == "633", "and so is the angle range");
        });
    }

    public static void SpectrumQuantitiesAreTickedInADialog(Harness h, HeadlessUnitTestSession session)
    {
        h.Section("Spectrum > Quantities…: checkboxes, the list beside the button, an axis per unit");

        WithWindow(session, window =>
        {
            OpenSample(window);
            ShowTab(window, "SpectrumTab");
            var text = Find<TextBlock>(window, "QuantitiesText");
            h.Expect(text.Text == "R (avg s,p), T (avg s,p)", $"the list says what is plotted ({text.Text})");

            Click(window, Find<Button>(window, "QuantitiesButton"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var d = window.OwnedWindows.OfType<SpectrumQuantitiesDialog>().Last();
            d.UpdateLayout();
            h.Expect(d.Box("RA")!.IsChecked == true && d.Box("TA")!.IsChecked == true && d.Box("RS")!.IsChecked == false,
                     "the dialog ticks what is plotted now");
            d.Box("TA")!.IsChecked = false;
            d.Box("R_DELTA")!.IsChecked = true;
            d.Box("ODA")!.IsChecked = true;
            Click(window, d.GetControl<Button>("OkButton"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var plot = Find<SpectrumPlot>(window, "Plot");
            h.Expect(text.Text == "R (avg s,p), OD (avg s,p), Δ R (°)", $"OK takes the ticks, in the list's order ({text.Text})");
            h.Expect(plot.Series is { Count: 3 } s && s.Select(x => x.Axis).Distinct().Count() == 3,
                     "and redraws them against three axes");

            Click(window, Find<Button>(window, "QuantitiesButton"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            d = window.OwnedWindows.OfType<SpectrumQuantitiesDialog>().Last();
            d.UpdateLayout();
            Click(window, d.GetControl<Button>("ClearButton"));
            Click(window, d.GetControl<Button>("OkButton"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            h.Expect(d.IsVisible && d.GetControl<TextBlock>("ProblemText").Text == "Tick at least one quantity.",
                     "with nothing ticked OK says so and stays open");
            Click(window, d.GetControl<Button>("CancelButton"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            h.Expect(text.Text == "R (avg s,p), OD (avg s,p), Δ R (°)", "Cancel changes nothing");
        });
    }

    public static void SystemParametersSetTheBackSurfaceAndTheAngles(Harness h, HeadlessUnitTestSession session)
    {
        h.Section("Edit > System Parameters: the back surface, the substrate thickness and angles in air");

        WithWindow(session, window =>
        {
            OpenSample(window);
            string before = Find<TextBlock>(window, "MeritText").Text ?? "";
            h.Expect(Find<MenuItem>(window, "SystemParametersMenu").Header as string == "_System Parameters…",
                     "the Edit menu item is named System Parameters");
            h.Expect(Find<MenuItem>(window, "MaterialsMenu").Parent == Find<MenuItem>(window, "EditMenu")
                     && window.FindControl<MenuItem>("EditorsMenu") is null,
                     "Materials is in the Edit menu, and there is no Editors menu");

            SystemParametersDialog Open()
            {
                Click(window, Find<MenuItem>(window, "EditMenu"));
                Click(window, Find<MenuItem>(window, "SystemParametersMenu"));
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                var d = window.OwnedWindows.OfType<SystemParametersDialog>().First();
                d.UpdateLayout();
                return d;
            }

            var dialog = Open();
            h.Expect(dialog.Title == "System Parameters", $"the dialog is titled System Parameters ({dialog.Title})");
            var back = dialog.GetControl<CheckBox>("BackSurfaceBox");
            var mm = dialog.GetControl<TextBox>("SubstrateBox");
            var air = dialog.GetControl<CheckBox>("AngleInAirBox");
            h.Expect(back.IsChecked != true && !mm.IsEnabled && mm.Text == "1" && air.IsChecked != true,
                     "a design starts without its back surface, 1 mm, angles in the incident medium");
            Click(window, back);
            h.Expect(mm.IsEnabled, "ticking the back surface enables the thickness");
            mm.Text = "2";
            Click(window, air);
            Click(window, dialog.GetControl<Button>("OkButton"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            string status = Find<TextBlock>(window, "StatusText").Text ?? "";
            h.Expect(!dialog.IsVisible && status.Contains("back surface of a 2 mm substrate") && status.Contains("in air"),
                     $"OK takes them and says so ({status})");
            string after = Find<TextBlock>(window, "MeritText").Text ?? "";
            h.Expect(after != before, $"and the merit moves with the back surface ({before} -> {after})");

            dialog = Open();
            h.Expect(dialog.GetControl<CheckBox>("BackSurfaceBox").IsChecked == true
                     && dialog.GetControl<TextBox>("SubstrateBox").Text == "2"
                     && dialog.GetControl<CheckBox>("AngleInAirBox").IsChecked == true,
                     "opened again, the dialog shows what the design holds");
            dialog.GetControl<TextBox>("SubstrateBox").Text = "0";
            Click(window, dialog.GetControl<Button>("ApplyButton"));
            var problem = dialog.GetControl<TextBlock>("ProblemText");
            h.Expect(dialog.IsVisible && problem.Text!.Contains("positive number of millimetres"),
                     $"a substrate of no thickness is refused, and the dialog stays ({problem.Text})");
            Click(window, dialog.GetControl<CheckBox>("BackSurfaceBox"));
            Click(window, dialog.GetControl<Button>("OkButton"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            h.Expect(!dialog.IsVisible && Find<TextBlock>(window, "StatusText").Text!.Contains("coated surface alone"),
                     $"unticked, the box is not read, and R and T are the coated surface's ({Find<TextBlock>(window, "StatusText").Text})");
        });
    }

    /// <summary>THE USER (2026-10-16): µm must reach physical thicknesses too, and System Parameters wants
    /// an Apply button.</summary>
    public static void ApplyInSystemParametersPutsEveryLengthInMicrons(Harness h, HeadlessUnitTestSession session)
    {
        h.Section("System Parameters: Apply switches every length to µm and leaves the dialog open");

        WithWindow(session, window =>
        {
            OpenSample(window);
            Click(window, Find<MenuItem>(window, "EditMenu"));
            Click(window, Find<MenuItem>(window, "SystemParametersMenu"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var prefs = window.OwnedWindows.OfType<SystemParametersDialog>().FirstOrDefault();
            h.Expect(prefs is not null, "System Parameters opens");
            if (prefs is null) return;
            prefs.UpdateLayout();

            Click(window, prefs.GetControl<RadioButton>("MicronChoice"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            h.Expect(prefs.GetControl<RadioButton>("NmThicknessChoice").Content as string == "physical (µm)",
                     $"the physical thickness choice is named in µm ({prefs.GetControl<RadioButton>("NmThicknessChoice").Content})");
            Click(window, prefs.GetControl<Button>("ApplyButton"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            h.Expect(prefs.IsVisible, "Apply leaves the dialog open");
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var said = Find<TextBlock>(window, "StatusText").Text ?? "";
            h.Expect(said.Contains("µm") && !said.Contains("is now in"),
                     $"the status says what Apply did, and no layer was changed by the table being rebuilt ({said})");

            var layers = Find<DataGrid>(window, "LayerGrid");
            var merit = Find<DataGrid>(window, "OperandGrid");
            h.Expect(layers.Columns[4].Header as string == "µm", $"the layer table's physical column is headed µm ({layers.Columns[4].Header})");
            h.Expect(merit.Columns[9].Header as string == "Limit (µm)", $"the merit table's limit is in µm ({merit.Columns[9].Header})");
            var first = layers.ItemsSource!.Cast<LayerRow>().First();
            h.Expect(first.UnitChoice == "Default (µm)" && first.ValueText.StartsWith("0.0"),
                     $"and layer 1 reads in µm ({first.ValueText} {first.UnitChoice})");
            h.Expect(Find<TextBlock>(window, "SummaryText").Text?.Contains(" µm ·") == true,
                     $"as does the total thickness ({Find<TextBlock>(window, "SummaryText").Text})");

            Click(window, prefs.GetControl<Button>("CancelButton"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            h.Expect(!prefs.IsVisible && layers.Columns[4].Header as string == "µm",
                     "Cancel closes it, and what was applied stays applied");

            // A UNIT PICKED IN THE TABLE STAYS PICKED when the table is rebuilt. It once came back
            // as Default: a recycled drop-down wrote its old row's choice into the new row.
            ComboBox UnitBox(int index) => layers.GetVisualDescendants().OfType<DataGridRow>()
                .First(r => r.DataContext is LayerRow l && l.Index == index)
                .GetVisualDescendants().OfType<ComboBox>().First(c => c.ItemsSource is IReadOnlyList<string> o && o.Contains("QWOT"));
            UnitBox(1).SelectedItem = "QWOT";
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            UnitBox(2).SelectedItem = "FWOT";
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var rows = layers.ItemsSource!.Cast<LayerRow>().ToArray();
            h.Expect(rows[1].UnitChoice == "QWOT" && rows[2].UnitChoice == "FWOT"
                     && rows[0].UnitChoice == "Default (µm)" && rows[3].UnitChoice == "Default (µm)",
                     $"layers 2 and 3 keep QWOT and FWOT through two rebuilds ({string.Join(", ", rows.Select(r => r.UnitChoice))})");
            h.Expect(UnitBox(1).SelectedItem as string == "QWOT" && UnitBox(2).SelectedItem as string == "FWOT",
                     "and their drop-downs show it");

            // AND THROUGH A CHANGE OF LENGTH UNIT, which gives every drop-down a new list
            // ("Default (µm)" becomes "Default (nm)"): the case that reset them in a rendered frame.
            Click(window, Find<MenuItem>(window, "EditMenu"));
            Click(window, Find<MenuItem>(window, "SystemParametersMenu"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            prefs = window.OwnedWindows.OfType<SystemParametersDialog>().First();
            prefs.UpdateLayout();
            Click(window, prefs.GetControl<RadioButton>("NanometreChoice"));
            Click(window, prefs.GetControl<Button>("OkButton"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            rows = layers.ItemsSource!.Cast<LayerRow>().ToArray();
            h.Expect(rows[1].UnitChoice == "QWOT" && rows[2].UnitChoice == "FWOT"
                     && rows[0].UnitChoice == "Default (nm)",
                     $"back in nm, layers 2 and 3 are still QWOT and FWOT ({string.Join(", ", rows.Select(r => r.UnitChoice))})");
        });
    }

    /// <summary>THE USER (2026-10-16): an "Input design string" command; (1H 0.8L)12 is 24 layers,
    /// every H picked up from the first H and every L from the first L.</summary>
    public static void ADesignStringFillsTheLayerTable(Harness h, HeadlessUnitTestSession session)
    {
        h.Section("Input design string: (1H 0.8L)12 typed in its dialog fills the table with its pickups");

        WithWindow(session, window =>
        {
            OpenSample(window);
            var grid = Find<DataGrid>(window, "LayerGrid");
            LayerRow[] Rows() => grid.ItemsSource!.Cast<LayerRow>().ToArray();
            int before = Rows().Length;

            FromStack(window, "DesignStringItem");
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var dialog = window.OwnedWindows.OfType<DesignStringDialog>().FirstOrDefault();
            h.Expect(dialog is not null && dialog.IsVisible, "the button opens Input design string");
            if (dialog is null) return;
            dialog.UpdateLayout();
            h.Expect(dialog.GetControl<TextBlock>("ReplaceText").Text!.Contains($"all {before} layers"),
                     "it says OK replaces the layers there are");

            // HALF TYPED: the preview says why, and OK keeps the dialog open.
            TypeInto(window, dialog, dialog.GetControl<TextBox>("StringBox"), "(1H 0.8L");
            dialog.UpdateLayout();
            h.Expect(dialog.GetControl<TextBlock>("PreviewText").Text!.Contains("not closed"),
                     $"an unclosed bracket is named ({dialog.GetControl<TextBlock>("PreviewText").Text})");
            Click(window, dialog.GetControl<Button>("OkButton"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            h.Expect(dialog.IsVisible && dialog.GetControl<TextBlock>("ProblemText").IsVisible && Rows().Length == before,
                     "OK on it is refused, the table untouched");

            TypeInto(window, dialog, dialog.GetControl<TextBox>("StringBox"), "(1H 0.8L)12");
            dialog.UpdateLayout();
            h.Expect(dialog.GetControl<TextBlock>("PreviewText").Text == "24 layers: 12 H, 12 L.",
                     $"the preview counts 24 layers ({dialog.GetControl<TextBlock>("PreviewText").Text})");
            var boxes = dialog.GetControl<StackPanel>("LettersPanel").GetVisualDescendants().OfType<ComboBox>().ToArray();
            h.Expect(boxes.Length == 2 && boxes.All(b => b.SelectedItem is string),
                     "H and L each have a material box, already chosen");
            Click(window, dialog.GetControl<Button>("OkButton"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            h.Expect(!dialog.IsVisible, "OK closes it");

            var rows = Rows();
            h.Expect(rows.Length == 24, $"the table has 24 layers ({rows.Length})");
            h.Expect(!rows[0].IsPickedUp && !rows[1].IsPickedUp && rows.Skip(2).All(r => r.IsPickedUp),
                     "layers 1 and 2 are free, the other 22 picked up");
            h.Expect(rows.Where((r, i) => i % 2 == 0).All(r => r.Material == rows[0].Material && Math.Abs(r.Value - 1.0) < 1e-9)
                     && rows.Where((r, i) => i % 2 == 1).All(r => r.Material == rows[1].Material && Math.Abs(r.Value - 0.8) < 1e-9),
                     $"H at 1 and L at 0.8 throughout ({rows[0].Material} {rows[0].ValueText}, {rows[1].Material} {rows[1].ValueText})");
            h.Expect(rows[2].PickupText.Contains("L1") && rows[3].PickupText.Contains("L2"),
                     $"the Pickup column names the parents ({rows[2].PickupText}, {rows[3].PickupText})");

            // OPENED AGAIN, the last string is offered.
            FromStack(window, "DesignStringItem");
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            dialog = window.OwnedWindows.OfType<DesignStringDialog>().First();
            h.Expect(dialog.GetControl<TextBox>("StringBox").Text == "(1H 0.8L)12", "reopened, it offers the last string");
            Click(window, dialog.GetControl<Button>("CancelButton"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            h.Expect(Rows().Length == 24, "and Cancel changes nothing");
        });
    }

    /// <summary>A command from the layer strip's Stack menu: the button opens it, the item is clicked.</summary>
    public static void TheMeritFunctionMenuHasItsFileCommands(Harness h, HeadlessUnitTestSession session)
    {
        h.Section("the Merit Function tab's Merit function menu: open, save, save as, export as CSV");

        WithWindow(session, window =>
        {
            OpenSample(window);
            ShowTab(window, "MeritTab");
            Click(window, Find<DropDownButton>(window, "MeritFileButton"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var headers = new[] { "OpenMeritItem", "AppendMeritItem", "SaveMeritItem", "SaveMeritAsItem", "ExportMeritItem" }
                .Select(n => window.FindControl<MenuItem>(n)?.Header as string).ToArray();
            h.Expect(headers.SequenceEqual(["Open merit function…", "Append merit function…", "Save merit function",
                                            "Save merit function as…", "Export as CSV with values…"]),
                     $"the five commands, in order ({string.Join(" | ", headers)})");
        });
    }

    /// <summary>THE USER (2026-10-22): a report of the design in physical units and the index of
    /// every material used, from the GUI as from the CLI and the MCP. The save dialog is replaced by
    /// a path, since a headless window cannot drive it.</summary>
    public static void TheDesignReportGoesThroughItsDialog(Harness h, HeadlessUnitTestSession session)
    {
        h.Section("File > Export design report, through its dialog");

        WithWindow(session, window =>
        {
            var items = Find<MenuItem>(window, "FileMenu").Items.OfType<MenuItem>()
                .Select(m => (m.Name, m.Header as string)).ToArray();
            h.Expect(items.Contains(("ExportReportMenu", "Export design _report…")), "File has Export design report…");

            var w = new DesignWorkspace();
            w.Open(File.ReadAllText(Path.Combine(ThinFilm.Api.Samples.ExamplesFolder, "AR-visible", "AR-visible.tfd")), "AR-visible.tfd");
            w.SpectrumFrom = 450;
            w.SpectrumTo = 650;
            string txt = Path.Combine(Path.GetTempPath(), "thinfilm-gui-report.txt");
            File.Delete(txt);

            DesignReportDialog Show()
            {
                var d = new DesignReportDialog(w.DesignReportForm()!, null,
                                               r => w.ExportDesignReport(r) ? null : w.Status,
                                               () => Task.FromResult<string?>(txt));
                _ = d.ShowDialog<bool>(window);
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                d.UpdateLayout();
                return d;
            }

            var dialog = Show();
            h.Expect(dialog.GetControl<TextBox>("FromBox").Text == "450" && dialog.GetControl<TextBox>("ToBox").Text == "650"
                     && dialog.GetControl<TextBox>("StepBox").Text == "10" && dialog.GetControl<TextBlock>("UnitText").Text == "nm",
                     "it opens on the Spectrum tab's range, every 10 nm");

            dialog.GetControl<TextBox>("StepBox").Text = "0";
            Click(window, dialog.GetControl<Button>("OkButton"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            h.Expect(dialog.IsVisible && dialog.GetControl<TextBlock>("ProblemText").IsVisible && !File.Exists(txt),
                     $"a zero step is refused in the dialog, before a file is chosen ({dialog.GetControl<TextBlock>("ProblemText").Text})");

            dialog.GetControl<TextBox>("StepBox").Text = "50";
            Click(window, dialog.GetControl<Button>("OkButton"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var text = File.Exists(txt) ? File.ReadAllText(txt) : "";
            h.Expect(!dialog.IsVisible && text.Contains("[Layers]") && text.Contains("\n450\t") && text.Contains("\n650\t")
                     && w.Status.Contains("5 wavelengths from 450 nm to 650 nm"),
                     $"Export writes it and closes ({w.Status})");

            // Shown in µm, typed in µm, written in nm.
            w.SetWavelengthUnit("um");
            var form = w.DesignReportForm()!;
            h.Expect(form.From == "0.45" && form.Step == "0.01", $"in a design shown in µm the range is in µm ({form.From}, {form.Step})");
            form.Step = "0.1";
            h.Expect(form.ToRequest("x.txt", out _) is { FromNm: 450, ToNm: 650, StepNm: 100 }, "and goes to the report in nm");
        });
    }

    /// <summary>thinfilm-gui design.tfd, and a .tfd opened from Explorer, Finder or a file manager:
    /// the design opens as File > Open opens it, and Save writes back to that file.</summary>
    public static void ADesignNamedAtLaunchOpens(Harness h, HeadlessUnitTestSession session)
    {
        h.Section("a design named on the command line or opened from a file manager opens in the window");

        string dir = Path.Combine(Path.GetTempPath(), "thinfilm-gui-open");
        Directory.CreateDirectory(dir);
        string tfd = Path.Combine(dir, "Launched.tfd");
        File.Copy(Path.Combine(ThinFilm.Api.Samples.ExamplesFolder, "Bandpass", "Bandpass.tfd"), tfd, overwrite: true);

        WithWindow(session, window =>
        {
            void Wait(Task t) { while (!t.IsCompleted) Avalonia.Threading.Dispatcher.UIThread.RunJobs(); t.GetAwaiter().GetResult(); }

            Wait(window.OpenPath(tfd));
            int Layers() => Find<DataGrid>(window, "LayerGrid").ItemsSource?.Cast<LayerRow>().Count() ?? 0;
            int layers = Layers();
            var opened = Find<TextBlock>(window, "StatusText").Text ?? "";
            h.Expect(layers > 2 && opened.Contains("Launched"), $"the design opens ({layers} layers: {opened})");

            Wait(window.OpenPath(Path.Combine(dir, "missing.tfd")));
            var status = Find<TextBlock>(window, "StatusText").Text ?? "";
            h.Expect(status.StartsWith("Could not open") && Layers() == layers,
                     $"a file that is not there says so and leaves the design open ({status})");
        });
    }

    /// <summary>File > Open sample is a browser, not a file picker: a macOS picker cannot look inside
    /// the .app the samples ship in. Driven here as a person would: find, choose, read, open.</summary>
    public static void TheSampleBrowserFindsAndOpens(Harness h, HeadlessUnitTestSession session)
    {
        h.Section("File > Open sample: the catalogue as a tree, narrowed by Find, opened as a copy");

        WithWindow(session, window =>
        {
            void Wait(Task t) { while (!t.IsCompleted) Avalonia.Threading.Dispatcher.UIThread.RunJobs(); t.GetAwaiter().GetResult(); }

            var dialog = new SampleBrowserDialog();
            var result = dialog.ShowDialog<string?>(window);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            dialog.UpdateLayout();

            h.Expect(dialog.Roots.Select(r => r.Name).SequenceEqual(["Examples", "User guide", "Literature"]),
                     "it opens on Examples, the user guide's designs and Literature");
            h.Expect(dialog.GetControl<TextBlock>("CountText").Text == $"{ThinFilm.Api.Samples.Files.Count} designs",
                     $"and says how many designs there are ({dialog.GetControl<TextBlock>("CountText").Text})");
            h.Expect(!dialog.GetControl<Button>("OkButton").IsEnabled, "Open waits for a design to be chosen");
            var tree = dialog.GetControl<TreeView>("SampleTreeView");
            h.Expect(tree.ContainerFromIndex(0) is TreeViewItem { IsExpanded: true } && tree.ContainerFromIndex(1) is TreeViewItem { IsExpanded: true },
                     "Examples and Literature start open, so the examples and the categories are in view");

            dialog.GetControl<TextBox>("FilterBox").Text = "Bermel silicon";
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var found = dialog.Roots.SelectMany(r => r.Designs).ToArray();
            h.Expect(found.Length == 1 && found[0].Name == "Bermel2007-SiN-AR-on-Si"
                     && dialog.GetControl<TextBlock>("CountText").Text == "1 design",
                     $"Find narrows it to what holds every word ({string.Join(", ", found.Select(e => e.Name))})");

            h.Expect(dialog.Select(found[0]) && dialog.Selected == found[0], "choosing the design selects it");
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            h.Expect(dialog.GetControl<SelectableTextBlock>("SourceText").Text == found[0].Source
                     && (dialog.GetControl<SelectableTextBlock>("NotesText").Text ?? "").StartsWith("Single-layer silicon nitride", StringComparison.Ordinal)
                     && dialog.GetControl<TextBlock>("GroupText").Text == "Literature › Antireflection › Bermel 2007"
                     && dialog.GetControl<Button>("OkButton").IsEnabled,
                     "its source, notes and folder are shown beside the tree, and Open is offered");

            Click(window, dialog.GetControl<Button>("OkButton"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            h.Expect(result.IsCompleted && result.Result == found[0].Path, "Open closes with the design's path");

            Wait(window.OpenSamplePath(result.Result!));
            h.Expect(window.Title?.Contains("Bermel2007-SiN-AR-on-Si", StringComparison.Ordinal) == true,
                     $"and the window opens it ({window.Title})");

            var cancelled = new SampleBrowserDialog();
            var none = cancelled.ShowDialog<string?>(window);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Click(window, cancelled.GetControl<Button>("CancelButton"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            h.Expect(none.IsCompleted && none.Result is null, "Cancel closes with nothing");
        });
    }

    /// <summary>THE USER (2026-10-21): read and write a design in an OpticStudio coating file. The
    /// menu items open a file picker, which a headless window cannot drive, so they are checked to be
    /// there and the dialogs are driven as the items drive them.</summary>
    public static void ZemaxCoatingsGoThroughTheirDialogs(Harness h, HeadlessUnitTestSession session)
    {
        h.Section("File > Import ZEMAX coating and Export to ZEMAX coating, through their dialogs");

        WithWindow(session, window =>
        {
            var items = Find<MenuItem>(window, "FileMenu").Items.OfType<MenuItem>()
                .Select(m => (m.Name, m.Header as string)).ToArray();
            h.Expect(items.Contains(("ImportZemaxMenu", "_Import ZEMAX coating…"))
                     && items.Contains(("ExportZemaxMenu", "Export to _ZEMAX coating…")),
                     "File has Import ZEMAX coating… and Export to ZEMAX coating…");

            var w = new DesignWorkspace();
            w.Open(File.ReadAllText(Path.Combine(ThinFilm.Api.Samples.ExamplesFolder, "AR-visible", "AR-visible.tfd")), "AR-visible.tfd");
            string dat = Path.Combine(Path.GetTempPath(), "thinfilm-gui-zemax.dat");
            File.Delete(dat);
            File.Delete(dat + ".bak");

            ZemaxExportDialog Export()
            {
                var form = w.ZemaxExportForm(dat)!;
                var d = new ZemaxExportDialog(form, r =>
                {
                    var report = w.ExportZemaxCoating(r, out bool exists);
                    return (report is null ? w.Status : null, exists);
                });
                _ = d.ShowDialog<bool>(window);
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                d.UpdateLayout();
                return d;
            }

            var dialog = Export();
            h.Expect(dialog.GetControl<TextBox>("PathBox").Text == dat && dialog.GetControl<TextBox>("CoatingBox").Text == "AR-VISIBLE"
                     && dialog.GetControl<TextBox>("FromBox").Text == "420",
                     "Export opens on the file, the design's name and its wavelengths");
            Click(window, dialog.GetControl<Button>("OkButton"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            h.Expect(!dialog.IsVisible && File.Exists(dat) && File.ReadAllText(dat).Contains("COAT AR-VISIBLE"),
                     $"Export writes the file and closes ({w.Status})");

            // The same name again: asked, and replaced on OK.
            dialog = Export();
            Click(window, dialog.GetControl<Button>("OkButton"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var ask = dialog.OwnedWindows.OfType<ConfirmDialog>().FirstOrDefault();
            h.Expect(ask is not null && ask.GetControl<TextBlock>("MessageText").Text!.Contains("already has a coating called AR-VISIBLE"),
                     "a coating already there is asked about");
            if (ask is null) return;
            ask.UpdateLayout();
            Click(window, ask.GetControl<Button>("YesButton"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            h.Expect(!dialog.IsVisible && w.Status.StartsWith("Replaced COAT AR-VISIBLE"), $"and replaced on OK ({w.Status})");

            int layers = w.Layers.Count;
            w.ClearLayers();
            var coatings = w.ZemaxCoatings(dat)!;
            var import = new CoatingImportDialog("Import ZEMAX coating", Path.GetFileName(dat),
                coatings.Select(c => new CoatingChoice(c.Name, c.Layers, null, c.Problem)).ToArray(), "",
                i => w.ImportZemaxCoating(dat, coatings[i].Name) ? null : w.Status);
            _ = import.ShowDialog<bool>(window);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            import.UpdateLayout();
            var list = import.GetControl<ListBox>("CoatingList");
            h.Expect(list.SelectedIndex == 0, "a file with one coating has it chosen already");
            list.SelectedIndex = -1;
            Click(window, import.GetControl<Button>("OkButton"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            h.Expect(import.IsVisible && import.GetControl<TextBlock>("ProblemText").Text == "Choose a coating.",
                     "Import with nothing chosen asks for a coating");
            list.SelectedIndex = 0;
            Click(window, import.GetControl<Button>("OkButton"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            h.Expect(!import.IsVisible && w.Layers.Count == layers, $"and with one chosen, imports it ({w.Status})");
        });
    }

    /// <summary>THE USER (2026-10-21): "It would be very nice to support Optiland".</summary>
    public static void OptilandCoatingsGoThroughTheirDialog(Harness h, HeadlessUnitTestSession session)
    {
        h.Section("File > Import Optiland coating and Export Optiland coating");

        WithWindow(session, window =>
        {
            var items = Find<MenuItem>(window, "FileMenu").Items.OfType<MenuItem>().Select(m => m.Name).ToArray();
            h.Expect(items.Contains("ImportOptilandMenu") && items.Contains("ExportOptilandMenu"),
                     "File has Import Optiland coating… and Export Optiland coating…");

            var w = new DesignWorkspace();
            w.Open(File.ReadAllText(Path.Combine(ThinFilm.Api.Samples.ExamplesFolder, "AR-visible", "AR-visible.tfd")), "AR-visible.tfd");
            string json = Path.Combine(Path.GetTempPath(), "thinfilm-gui-optiland.json");
            File.Delete(json);
            int layers = w.Layers.Count;
            h.Expect(w.ExportOptilandCoating(json) && File.Exists(json) && w.Status.Contains("load_obj_from_json"),
                     $"Export writes the file ({w.Status})");

            w.ClearLayers();
            var coatings = w.OptilandCoatings(json)!;
            var import = new CoatingImportDialog("Import Optiland coating", Path.GetFileName(json),
                coatings.Select(c => new CoatingChoice(c.Name, c.Layers, null, c.Problem)).ToArray(), "",
                i => w.ImportOptilandCoating(json, i, coatings[i].Name) ? null : w.Status);
            _ = import.ShowDialog<bool>(window);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            import.UpdateLayout();
            h.Expect(import.Title == "Import Optiland coating" && import.GetControl<TextBlock>("FileText").Text!.EndsWith("1 coating"),
                     "the dialog lists the file's one coating");
            Click(window, import.GetControl<Button>("OkButton"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            h.Expect(!import.IsVisible && w.Layers.Count == layers, $"and Import brings its layers back ({w.Status})");
        });
    }

    /// <summary>THE USER (2026-10-21): "If the user changes a opened file, there should be a question
    /// whether he wants to save upon opening another file or closing the program."</summary>
    public static void ClosingAChangedDesignAsksToSaveIt(Harness h, HeadlessUnitTestSession session)
    {
        h.Section("closing a changed design asks Save, Don't save or Cancel; an unchanged one just closes");

        session.Dispatch(() =>
        {
            var untouched = new MainWindow();
            untouched.Show();
            untouched.Close();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            h.Expect(!untouched.IsVisible && !untouched.OwnedWindows.Any(), "the start-up design, unchanged, closes without a question");

            var window = new MainWindow();
            window.Show();
            window.UpdateLayout();
            OpenSample(window);
            window.Close();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            h.Expect(!window.IsVisible, "a design just opened closes without a question");

            window = new MainWindow();
            window.Show();
            window.UpdateLayout();
            OpenSample(window);
            Find<DataGrid>(window, "LayerGrid").SelectedIndex = 0;
            Click(window, Find<Button>(window, "DeleteLayerButton"));
            window.Close();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var ask = window.OwnedWindows.OfType<SaveChangesDialog>().FirstOrDefault();
            h.Expect(window.IsVisible && ask is not null
                     && ask.GetControl<TextBlock>("MessageText").Text!.Contains("AR-visible.tfd has changed"),
                     "with a layer deleted, closing asks first and the window stays");
            if (ask is null) { window.AskToSave = false; window.Close(); return; }
            ask.UpdateLayout();
            Click(window, ask.GetControl<Button>("CancelButton"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            h.Expect(window.IsVisible && !ask.IsVisible, "Cancel keeps it open");

            window.Close();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            ask = window.OwnedWindows.OfType<SaveChangesDialog>().First();
            ask.UpdateLayout();
            Click(window, ask.GetControl<Button>("DiscardButton"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            h.Expect(!window.IsVisible, "Don't save closes it");
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static void FromStack(MainWindow window, string item)
    {
        Click(window, Find<DropDownButton>(window, "StackButton"));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Click(window, Find<MenuItem>(window, item));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    /// <summary>THE USER (2026-10-16): couple all layers to the first instance of their material,
    /// with no dialog; decouple them all; delete all layers - and room in the strip for more.</summary>
    public static void TheStackMenuReversesTheLayersAndTheMedia(Harness h, HeadlessUnitTestSession session)
    {
        h.Section("the Stack menu: Reverse layers, and Reverse layers and media");

        WithWindow(session, window =>
        {
            OpenSample(window);
            var grid = Find<DataGrid>(window, "LayerGrid");
            LayerRow[] Rows() => grid.ItemsSource!.Cast<LayerRow>().ToArray();
            string Text(string name) => Find<TextBlock>(window, name).Text ?? "";
            var before = Rows().Select(r => (r.Material, r.Thickness)).ToArray();
            string incident = Text("IncidentText"), substrate = Text("SubstrateText");

            FromStack(window, "ReverseLayersItem");
            h.Expect(Rows().Select(r => (r.Material, r.Thickness)).SequenceEqual(before.Reverse()),
                     $"Reverse layers puts the last layer first ({Text("StatusText")})");
            h.Expect(Text("IncidentText") == incident && Text("SubstrateText") == substrate,
                     "and leaves the incident medium and the substrate where they were");

            FromStack(window, "ReverseAllItem");
            h.Expect(Rows().Select(r => (r.Material, r.Thickness)).SequenceEqual(before),
                     "Reverse layers and media reverses them again");
            string newIncident = Text("IncidentText");
            static string Name(string line, string prefix) => line[prefix.Length..].Split(new[] { ",", " —" }, 2, StringSplitOptions.None)[0];
            h.Expect(newIncident.StartsWith("Incident medium: " + Name(substrate, "Substrate: "))
                     && Text("SubstrateText").StartsWith("Substrate: " + Name(incident, "Incident medium: ")),
                     $"and the media change places ({newIncident} / {Text("SubstrateText")})");
            h.Expect(Text("StatusText").Contains("is now the incident medium"), $"the status says so ({Text("StatusText")})");
        });
    }

    public static void TheStackMenuCouplesDecouplesAndEmpties(Harness h, HeadlessUnitTestSession session)
    {
        h.Section("the Stack menu: couple all, decouple all, delete all layers, and building up from empty");

        WithWindow(session, window =>
        {
            OpenSample(window);
            var grid = Find<DataGrid>(window, "LayerGrid");
            LayerRow[] Rows() => grid.ItemsSource!.Cast<LayerRow>().ToArray();
            string Status() => Find<TextBlock>(window, "StatusText").Text ?? "";
            var before = Rows().Select(r => r.Thickness).ToArray();
            var materials = Rows().Select(r => r.Material).ToArray();

            FromStack(window, "CoupleLayersItem");
            var rows = Rows();
            int expected = materials.Length - materials.Distinct().Count();
            h.Expect(rows.Count(r => r.IsPickedUp) == expected,
                     $"every layer after the first of its material is coupled ({rows.Count(r => r.IsPickedUp)} of {expected}): {Status()}");
            h.Expect(rows.All(r => !r.IsPickedUp || rows[r.Pickup!.Parent].Material == r.Material
                                   && materials.ToList().IndexOf(r.Material) == r.Pickup.Parent),
                     "each to the first layer of its own material");
            h.Expect(rows.Select(r => r.Thickness).Zip(before).All(p => Math.Abs(p.First - p.Second) < 1e-9), "and nothing moved");
            h.Expect(!window.OwnedWindows.Any(), "with no dialog");

            FromStack(window, "DecoupleLayersItem");
            h.Expect(Rows().All(r => !r.IsPickedUp && r.Variable), $"decoupled, every layer is free and varied: {Status()}");

            // DELETE ALL asks first; Cancel keeps the layers.
            int n = Rows().Length;
            FromStack(window, "ClearLayersItem");
            var confirm = window.OwnedWindows.OfType<ConfirmDialog>().LastOrDefault();
            h.Expect(confirm is not null && confirm.GetControl<TextBlock>("MessageText").Text!.Contains($"all {n} layers"),
                     "Delete all layers asks first, naming how many");
            if (confirm is null) return;
            confirm.UpdateLayout();
            Click(window, confirm.GetControl<Button>("NoButton"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            h.Expect(Rows().Length == n, "Cancel keeps them");
            FromStack(window, "ClearLayersItem");
            confirm = window.OwnedWindows.OfType<ConfirmDialog>().Last();
            confirm.UpdateLayout();
            Click(window, confirm.GetControl<Button>("YesButton"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            h.Expect(Rows().Length == 0 && Status().Contains("bare substrate"), $"Yes empties the stack: {Status()}");

            var refine = OpenRun(window, "RefineMenu");
            if (refine is not null)
            {
                Click(window, refine.GetControl<Button>("StartButton"));
                var problem = refine.GetControl<TextBlock>("ProblemText");
                h.Expect(problem.IsVisible && problem.Text!.Contains("no layers"),
                         $"Refine on an empty stack says why it cannot: {problem.Text}");
                Click(window, refine.GetControl<Button>("CloseButton"));
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            }
            else h.Expect(false, "Refine opens on an empty stack");

            Click(window, Find<Button>(window, "InsertLayerButton"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            h.Expect(Rows().Length == 1 && Rows()[0].Thickness > 0, $"Insert layer starts a stack again: {Status()}");
        });
    }

    /// <summary>THE USER (2026-10-16): per-layer min and max below Vary in Layer properties, and a
    /// Set vary constraints command for a range of layers.</summary>
    public static void BoundsAreSetInLayerPropertiesAndForARange(Harness h, HeadlessUnitTestSession session)
    {
        h.Section("bounds: Layer properties for one layer, Set vary constraints for a range, shown in the table");

        WithWindow(session, window =>
        {
            OpenSample(window);
            var grid = Find<DataGrid>(window, "LayerGrid");
            LayerRow[] Rows() => grid.ItemsSource!.Cast<LayerRow>().ToArray();
            string Status() => Find<TextBlock>(window, "StatusText").Text ?? "";

            // LAYER PROPERTIES on layer 1: between 50 and 120 nm.
            grid.SelectedIndex = 0;
            Click(window, Find<Button>(window, "LayerPropertiesButton"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var dialog = window.OwnedWindows.OfType<LayerPropertiesDialog>().FirstOrDefault();
            h.Expect(dialog is not null, "Layer properties opens");
            if (dialog is null) return;
            dialog.UpdateLayout();
            var constraint = dialog.GetControl<ComboBox>("ConstraintBox");
            h.Expect(constraint.SelectedItem as string == Constraints.None && !dialog.GetControl<TextBox>("MinBox").IsEnabled,
                     "the layer is unconstrained, its min and max boxes off");
            constraint.SelectedItem = Constraints.Between;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            h.Expect(dialog.GetControl<TextBox>("MinBox").IsEnabled && dialog.GetControl<TextBox>("MaxBox").IsEnabled,
                     "between min and max turns both on");
            TypeInto(window, dialog, dialog.GetControl<TextBox>("MinBox"), "120");
            TypeInto(window, dialog, dialog.GetControl<TextBox>("MaxBox"), "50");
            Click(window, dialog.GetControl<Button>("OkButton"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            h.Expect(dialog.IsVisible && dialog.GetControl<TextBlock>("ProblemText").Text!.Contains("more than the minimum"),
                     "120 to 50 is refused, the dialog kept open");
            TypeInto(window, dialog, dialog.GetControl<TextBox>("MinBox"), "50");
            TypeInto(window, dialog, dialog.GetControl<TextBox>("MaxBox"), "120");
            Click(window, dialog.GetControl<Button>("OkButton"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            h.Expect(!dialog.IsVisible && Rows()[0].BoundsText == "50 – 120",
                     $"OK sets them, and the Bounds column shows them ({Rows()[0].BoundsText})");

            // SET VARY CONSTRAINTS from the Stack menu: every SiO2 layer at least 20 nm.
            FromStack(window, "BoundsItem");
            var bulk = window.OwnedWindows.OfType<BoundsDialog>().FirstOrDefault();
            h.Expect(bulk is not null, "Set vary constraints opens from the Stack menu");
            if (bulk is null) return;
            bulk.UpdateLayout();
            h.Expect(bulk.GetControl<TextBox>("FromBox").Text == "1" && bulk.GetControl<TextBox>("ToBox").Text == $"{Rows().Length}",
                     "over every layer to begin with");
            bulk.GetControl<ComboBox>("MaterialBox").SelectedItem = "SiO2";
            bulk.GetControl<ComboBox>("ConstraintBox").SelectedItem = Constraints.AtLeast;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            h.Expect(!bulk.GetControl<TextBox>("MaxBox").IsEnabled, "greater than min turns the max box off");
            var sio2 = Rows().Where(r => r.Material == "SiO2").Select(r => r.Index).ToArray();
            h.Expect(bulk.GetControl<TextBlock>("PreviewText").Text == $"Sets layers {DesignWorkspace.Ranges(sio2)}.",
                     $"the preview names the SiO2 layers ({bulk.GetControl<TextBlock>("PreviewText").Text})");
            TypeInto(window, bulk, bulk.GetControl<TextBox>("MinBox"), "20");
            Click(window, bulk.GetControl<Button>("OkButton"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            h.Expect(!bulk.IsVisible, "OK closes it");
            h.Expect(Rows().Where(r => r.Material == "SiO2").All(r => r.BoundsText == "≥ 20")
                     && Rows().Where(r => r.Material != "SiO2").All(r => r.BoundsText != "≥ 20"),
                     $"every SiO2 layer is at least 20 nm, layer 1 included, and no other: {Status()}");
        });
    }

    /// <summary>THE USER (2026-10-16): no limit the person cannot see - the maximum for
    /// unconstrained layers is shown and set in System Parameters, and Layer properties says it.</summary>
    public static void RefineRunsInAWindowOfItsOwn(Harness h, HeadlessUnitTestSession session)
    {
        h.Section("Optimization > Refine: Start, a live log and readout, then Accept or Revert");

        WithWindow(session, window =>
        {
            OpenSample(window);
            var summary = Find<TextBlock>(window, "SummaryText");
            double before = MeritFrom(summary.Text ?? "");

            RunDialog? Open() => OpenRun(window, "RefineMenu");
            // The run goes on in the background; tick the window's poll until it has ended.
            void Finish(RunDialog d)
            {
                for (int i = 0; i < 400 && !d.GetControl<Button>("AcceptButton").IsEnabled; ++i)
                {
                    System.Threading.Thread.Sleep(25);
                    d.Tick();
                    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                }
            }

            var dialog = Open();
            h.Expect(dialog is not null, "Refine… opens its window");
            if (dialog is null) return;
            h.Expect(dialog.GetControl<Button>("StartButton").IsEnabled && !dialog.GetControl<Button>("AcceptButton").IsEnabled
                     && !dialog.GetControl<Button>("StopButton").IsEnabled,
                     "Start is offered; nothing to stop or accept yet");
            TypeInto(window, dialog, dialog.GetControl<TextBox>("IterationsBox"), "0");
            Click(window, dialog.GetControl<Button>("StartButton"));
            h.Expect(dialog.GetControl<TextBlock>("ProblemText").IsVisible, "no iterations is refused in the window");
            TypeInto(window, dialog, dialog.GetControl<TextBox>("IterationsBox"), "300");
            Click(window, dialog.GetControl<Button>("StartButton"));
            Finish(dialog);

            var log = dialog.GetControl<TextBox>("LogBox").Text ?? "";
            h.Expect(dialog.GetControl<Button>("AcceptButton").IsEnabled, "once it has finished, Accept is offered");
            h.Expect(!dialog.GetControl<Button>("StartButton").IsEnabled, "and Start is not, one run to a window");
            h.Expect(log.Contains("start") && log.Contains("iter") && log.Contains("end ") && log.Contains("RMSE="),
                     "the log shows the start, each iteration and the end");
            h.Expect(dialog.GetControl<TextBlock>("StartMeritText").Text!.Contains('E')
                     && dialog.GetControl<TextBlock>("CurrentLabel").Text == "End RMSE"
                     && dialog.GetControl<TextBlock>("ElapsedText").Text!.EndsWith(" s"),
                     $"the readout: start and end RMSE, elapsed ({dialog.GetControl<TextBlock>("StartMeritText").Text} → "
                     + $"{dialog.GetControl<TextBlock>("CurrentMeritText").Text}, {dialog.GetControl<TextBlock>("ElapsedText").Text})");
            h.Expect(dialog.GetControl<TextBlock>("LogFileText").Text!.StartsWith("Log file:"), "and where the log file is");
            h.Expect(MeritFrom(summary.Text ?? "") == before, "the design is untouched while the window is open");

            Click(window, dialog.GetControl<Button>("AcceptButton"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            double after = MeritFrom(summary.Text ?? "");
            h.Expect(!dialog.IsVisible && after < before, $"Accept closes it and keeps the result ({before:G6} → {after:G6})");
            h.Expect(Find<TextBlock>(window, "StatusText").Text!.StartsWith("refine kept: RMSE"),
                     $"the status line says so ({Find<TextBlock>(window, "StatusText").Text})");

            // REVERT: a second run, thrown away.
            var second = Open();
            if (second is null) { h.Expect(false, "Refine… opens again"); return; }
            var tabs = second.GetControl<TabControl>("Tabs");
            string[] compareTabs = ["MeritTab", "SpectrumTab", "LayersTab"];
            bool TabsOpen() => compareTabs.All(t => second.GetControl<TabItem>(t).IsEnabled);
            bool TabsShut() => compareTabs.All(t => !second.GetControl<TabItem>(t).IsEnabled);
            h.Expect(tabs.SelectedItem == second.GetControl<TabItem>("LogTab") && TabsShut(),
                     "it opens on the log, the comparison tabs shut");
            second.GetControl<ComboBox>("NormBox").SelectedItem = "minimax";
            Click(window, second.GetControl<Button>("StartButton"));
            h.Expect(TabsShut(), "they stay shut while it runs");
            Finish(second);
            h.Expect((second.GetControl<TextBox>("LogBox").Text ?? "").Contains("pass 6/6"), "minimax logs its passes");

            // THE COMPARISON TABS, once it has ended.
            h.Expect(TabsOpen(), "Merit function, Spectrum and Layers open once it has ended");
            tabs.SelectedItem = second.GetControl<TabItem>("MeritTab");
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            second.UpdateLayout();
            var compare = second.GetControl<DataGrid>("CompareGrid");
            var rows = compare.ItemsSource!.Cast<ComparisonRow>().ToArray();
            h.Expect(rows.Length == Find<DataGrid>(window, "OperandGrid").ItemsSource!.Cast<object>().Count()
                     && rows.Any(r => r.Before != r.After),
                     $"Merit function: every row, its value before and after ({rows.Length} rows)");
            h.Expect(compare.Columns.Select(c => c.Header as string).TakeLast(3).SequenceEqual(["Value (Before)", "Value (After)", "Delta"])
                     && compare.IsReadOnly,
                     "the last columns Value (Before), Value (After), Delta, and nothing editable");
            h.Expect(second.GetControl<TextBlock>("MeritSummaryText").Text!.StartsWith("RMSE before"),
                     $"with both RMSE above it ({second.GetControl<TextBlock>("MeritSummaryText").Text})");
            var plot = second.GetControl<SpectrumPlot>("ComparePlot");
            h.Expect(plot.Series is { Count: 4 } s && s.Count(x => x.Dashed) == 2,
                     "Spectrum: R and T, before (dashed) and after");
            second.GetControl<ComboBox>("ShowBox").SelectedItem = RunCompareView.AfterOnly;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var a = plot.Series ?? [];
            h.Expect(a.Count == 2 && a.All(x => !x.Dashed), "Show After draws the result only");
            tabs.SelectedItem = second.GetControl<TabItem>("SpectrumTab");
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            second.UpdateLayout();
            TypeInto(window, second, second.GetControl<TextBox>("AngleBox"), "45");
            Click(window, second.GetControl<Button>("RedrawButton"));
            h.Expect(plot.Series is { Count: 2 } at45 && !at45[0].Y.SequenceEqual(a[0].Y), "at another angle on Redraw");
            var beforeGraph = second.GetControl<LayerGraph>("BeforeGraph");
            var afterGraph = second.GetControl<LayerGraph>("AfterGraph");
            h.Expect(beforeGraph.Profile is { } bp && afterGraph.Profile is { } ap
                     && bp.Layers.Length == ap.Layers.Length && bp.Layers.Zip(ap.Layers).Any(p => p.First.Thickness != p.Second.Thickness),
                     "Layers: the stack before and after");
            h.Expect(beforeGraph.Scale == afterGraph.Scale && beforeGraph.Heading.StartsWith("Before")
                     && afterGraph.Heading.StartsWith("After"),
                     $"to one scale ({beforeGraph.Heading}; {afterGraph.Heading})");

            // REVERT: the design as it was, the window open to start again.
            Click(window, second.GetControl<Button>("RevertButton"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            h.Expect(second.IsVisible && MeritFrom(summary.Text ?? "") == after,
                     "Revert leaves the design as it was, and the window open");
            h.Expect(second.GetControl<Button>("StartButton").IsEnabled && !second.GetControl<Button>("AcceptButton").IsEnabled
                     && !second.GetControl<Button>("RevertButton").IsEnabled,
                     "Start is offered again; nothing to accept or revert");
            h.Expect(TabsShut() && tabs.SelectedItem == second.GetControl<TabItem>("LogTab"),
                     "the comparison tabs shut, the log shown");
            h.Expect(second.GetControl<TextBlock>("StateText").Text!.StartsWith("Reverted"),
                     $"and it says so ({second.GetControl<TextBlock>("StateText").Text})");

            // CLOSING A RUNNING WINDOW STOPS THE RUN. Close() raises the same Closing event as the
            // title bar's X (and Escape, and the Close button), and that is where the run is stopped.
            // A search far too long to finish first, started again in the same window.
            Click(window, second.GetControl<Button>("CloseButton"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            h.Expect(!second.IsVisible, "Close closes it");
            var global = OpenRun(window, "GlobalMenu");
            if (global is null) { h.Expect(false, "Global search opens"); return; }
            TypeInto(window, global, global.GetControl<TextBox>("StartsBox"), "5");
            Click(window, global.GetControl<Button>("StartButton"));
            for (int i = 0; i < 400 && global.Monitor.Running; ++i) { System.Threading.Thread.Sleep(25); global.Tick(); }
            Click(window, global.GetControl<Button>("RevertButton"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            TypeInto(window, global, global.GetControl<TextBox>("StartsBox"), "100000");
            Click(window, global.GetControl<Button>("StartButton"));
            System.Threading.Thread.Sleep(200);
            global.Tick();
            h.Expect(global.Monitor.Running && !(global.GetControl<TextBox>("LogBox").Text ?? "").Contains("reverted"),
                     "a second run starts in the same window, with a log of its own");
            global.Close();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var m = global.Monitor;
            h.Expect(!global.IsVisible && m.Report!.State == "reverted", $"closing the window reverts the run ({m.Report?.State})");
            double stopped = m.Report!.ElapsedSeconds;
            System.Threading.Thread.Sleep(300);
            m.Poll();
            h.Expect(m.Report.ElapsedSeconds == stopped, "and it has stopped: its clock no longer moves");
            h.Expect(MeritFrom(summary.Text ?? "") == after, "the design is as it was");
        });
    }

    public static void TheUnconstrainedMaximumIsSetInSystemParameters(Harness h, HeadlessUnitTestSession session)
    {
        h.Section("the maximum for unconstrained layers: in System Parameters, in its unit, and named in Layer properties");

        WithWindow(session, window =>
        {
            OpenSample(window);
            Click(window, Find<MenuItem>(window, "EditMenu"));
            Click(window, Find<MenuItem>(window, "SystemParametersMenu"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var prefs = window.OwnedWindows.OfType<SystemParametersDialog>().FirstOrDefault();
            h.Expect(prefs is not null, "System Parameters opens");
            if (prefs is null) return;
            prefs.UpdateLayout();
            var box = prefs.GetControl<TextBox>("MaximumBox");
            h.Expect(box.Text == "300" && prefs.GetControl<TextBlock>("MaximumUnitText").Text == "nm",
                     $"the AR template's maximum for unconstrained layers reads 300 nm ({box.Text} {prefs.GetControl<TextBlock>("MaximumUnitText").Text})");
            Click(window, prefs.GetControl<RadioButton>("MicronChoice"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            h.Expect(box.Text == "0.3" && prefs.GetControl<TextBlock>("MaximumUnitText").Text == "µm", $"in µm it reads 0.3 µm ({box.Text})");
            TypeInto(window, prefs, box, "0");
            Click(window, prefs.GetControl<Button>("ApplyButton"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            h.Expect(prefs.GetControl<TextBlock>("ProblemText").Text!.Contains("positive"), "zero is refused");
            TypeInto(window, prefs, box, "1.2");
            Click(window, prefs.GetControl<Button>("OkButton"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            h.Expect(!prefs.IsVisible, "1.2 µm is taken");

            var grid = Find<DataGrid>(window, "LayerGrid");
            grid.SelectedIndex = 0;
            Click(window, Find<Button>(window, "LayerPropertiesButton"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var dialog = window.OwnedWindows.OfType<LayerPropertiesDialog>().First();
            dialog.UpdateLayout();
            string note = dialog.GetControl<TextBlock>("BoundsNoteText").Text ?? "";
            h.Expect(note.Contains("From 0 up to 1.2 µm") && note.Contains("System Parameters"),
                     $"Layer properties says what unconstrained means: {note}");
            Click(window, dialog.GetControl<Button>("CancelButton"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        });
    }

    /// <summary>Text typed into a box in a dialog: clicked, selected, replaced.</summary>
    /// <summary>THE USER (2026-10-21): "a section to give description of the file and where it came
    /// from. This should be in the display" - above the layer table, folding to one line.</summary>
    public static void TheDescriptionIsAboveTheLayers(Harness h, HeadlessUnitTestSession session)
    {
        h.Section("the Design tab's Description: a sample's notes shown, a source typed in, the header when folded");

        WithWindow(session, window =>
        {
            OpenSample(window, "Solar-absorber");
            var source = Find<TextBox>(window, "SourceBox");
            var notes = Find<TextBox>(window, "NotesBox");
            var expander = Find<Expander>(window, "DescriptionExpander");
            h.Expect(expander.IsExpanded && notes.Text!.StartsWith("Solar-selective absorber") && string.IsNullOrEmpty(source.Text),
                     "open, with the sample's notes and no source yet");

            const string cite = "Kennedy, NREL/TP-520-31267 (2002)";
            TypeInto(window, window, source, cite);
            Find<DataGrid>(window, "LayerGrid").Focus();   // leaving the box gives the design its text
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            expander.IsExpanded = false;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            h.Expect(Find<TextBlock>(window, "DescriptionHeader").Text == $"Description: {cite}"
                     && Find<TextBlock>(window, "StatusText").Text!.Contains("Description saved"),
                     $"typed and left, the source is the design's, and folded the header shows it ({Find<TextBlock>(window, "DescriptionHeader").Text})");

            OpenSample(window, "AR-visible");
            h.Expect(source.Text == "" && Find<TextBlock>(window, "DescriptionHeader").Text!.StartsWith("Description: "),
                     "another design shows its own description, still folded");

            // RENAMING (the user, 2026-10-24: "we need to be able to rename").
            expander.IsExpanded = true;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var name = Find<TextBox>(window, "NameBox");
            h.Expect(!string.IsNullOrEmpty(name.Text), $"the Name box shows the design's name ({name.Text})");
            TypeInto(window, window, name, "Camera AR");
            Find<DataGrid>(window, "LayerGrid").Focus();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            h.Expect(Find<TextBlock>(window, "StatusText").Text!.Contains("Renamed Camera AR"),
                     $"typed and left, the design is renamed ({Find<TextBlock>(window, "StatusText").Text})");
            Click(window, name);
            name.Text = "";
            Find<DataGrid>(window, "LayerGrid").Focus();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            h.Expect(name.Text == "Camera AR" && Find<TextBlock>(window, "StatusText").Text!.Contains("needs a name"),
                     $"a blank name is refused, and the box shows the name again ({name.Text})");
            expander.IsExpanded = false;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            // THE USER (2026-10-21): "I opened one of the files and the source information is
            // empty" - the cursor was in the Source box when the file was opened.
            expander.IsExpanded = true;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Click(window, source);
            h.Expect(source.IsFocused, "the cursor is in the Source box");
            var paper = new DesignWorkspace();
            paper.Open(File.ReadAllText(Path.Combine(ThinFilm.Api.Samples.ExamplesFolder, "AR-visible", "AR-visible.tfd")));
            paper.SetDescription(cite, "from a paper");
            window.OpenDocument(paper.Save()!, "paper.tfd");
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            h.Expect(source.Text == cite && notes.Text == "from a paper",
                     $"a design opened with the cursor in the box shows its own source ({source.Text})");
            Find<DataGrid>(window, "LayerGrid").Focus();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            expander.IsExpanded = false;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            h.Expect(source.Text == cite && Find<TextBlock>(window, "DescriptionHeader").Text == $"Description: {cite}",
                     "and leaving the box keeps it: the design's source is still the paper's");

            // THE USER (2026-10-21): "The wavelength range should match the article and what to plot."
            var api = new ThinFilm.Api.ThinFilmApi();
            string id = api.Load(paper.Save()!).Value!.Id;
            api.SetPlot(id, new ThinFilm.Api.PlotDocument(1500.0, 1600.0, 1001, 45.0, ["RS", "RP"]));
            window.OpenDocument(api.Save(id).Value!, "paper.tfd");
            h.Expect(Find<TextBox>(window, "FromBox").Text == "1500" && Find<TextBox>(window, "ToBox").Text == "1600"
                     && Find<TextBox>(window, "StepsBox").Text == "1001" && Find<TextBox>(window, "AngleBox").Text == "45"
                     && Find<TextBlock>(window, "QuantitiesText").Text!.Contains(ThinFilm.Api.SpectrumQuantities.Label("RS")),
                     $"a design saved with a plot opens to it ({Find<TextBox>(window, "FromBox").Text} to {Find<TextBox>(window, "ToBox").Text}, {Find<TextBlock>(window, "QuantitiesText").Text})");
        });
    }

    private static void TypeInto(MainWindow window, Window dialog, TextBox box, string text)
    {
        Click(window, box);
        box.SelectAll();
        dialog.KeyTextInput(text);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    /// <summary>THE USER (2026-10-16): layers in QWOT or FWOT at a reference wavelength set beside
    /// the default unit; each layer shows its unit and may have its own; a pickup is child = scale
    /// x parent + offset, the two in one unit.</summary>
    public static void LayerUnitsAndPickupsAreSetInTheDialogs(Harness h, HeadlessUnitTestSession session)
    {
        h.Section("System Parameters puts the layers in QWOT; Layer properties sets a pickup; the table shows both");

        WithWindow(session, window =>
        {
            OpenSample(window);
            var grid = Find<DataGrid>(window, "LayerGrid");
            LayerRow[] Rows() => grid.ItemsSource!.Cast<LayerRow>().ToArray();
            h.Expect(Rows().All(r => r.Unit == "nm"), "a template opens in nm");
            h.Expect((grid.Columns[2].Header as string) == "Thickness" && (grid.Columns[3].Header as string) == "Unit"
                     && (grid.Columns[4].Header as string) == "nm" && (grid.Columns[6].Header as string) == "Bounds" && (grid.Columns[7].Header as string) == "Pickup",
                     "the table has Thickness, Unit, nm, Bounds and Pickup columns");

            // EDIT > PREFERENCES: QWOT, counted at 550 nm.
            Click(window, Find<MenuItem>(window, "EditMenu"));
            Click(window, Find<MenuItem>(window, "SystemParametersMenu"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var prefs = window.OwnedWindows.OfType<SystemParametersDialog>().FirstOrDefault();
            h.Expect(prefs is not null, "System Parameters opens");
            if (prefs is null) return;
            prefs.UpdateLayout();
            Click(window, prefs.GetControl<RadioButton>("QwotChoice"));
            TypeInto(window, prefs, prefs.GetControl<TextBox>("ReferenceBox"), "abc");
            Click(window, prefs.GetControl<Button>("OkButton"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            h.Expect(prefs.IsVisible && prefs.GetControl<TextBlock>("ProblemText").IsVisible,
                     "a reference that is not a number keeps the dialog open, saying why");
            TypeInto(window, prefs, prefs.GetControl<TextBox>("ReferenceBox"), "550");
            Click(window, prefs.GetControl<Button>("OkButton"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            h.Expect(!prefs.IsVisible, "OK closes it");
            h.Expect(Rows().All(r => r.Unit == "QWOT" && r.UnitChoice == "Default (QWOT)"),
                     $"every layer is now shown in QWOT, following the design ({string.Join(", ", Rows().Select(r => r.UnitChoice))})");

            // THE "…" ON LAYER 2: pick up layer 1 at scale 3.
            var row2 = grid.GetVisualDescendants().OfType<DataGridRow>().First(r => r.DataContext is LayerRow { Index: 1 });
            var dots = row2.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Classes.Contains("layerProperties"));
            h.Expect(dots is not null, "layer 2 has a … button");
            if (dots is null) return;
            Click(window, dots);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var dialog = window.OwnedWindows.OfType<LayerPropertiesDialog>().FirstOrDefault();
            h.Expect(dialog is not null && dialog.IsVisible, "and it opens Layer properties for layer 2");
            if (dialog is null) return;
            dialog.UpdateLayout();
            h.Expect(dialog.GetControl<TextBlock>("HeadingText").Text == $"Layer 2 of {Rows().Length}",
                     $"headed with the layer ({dialog.GetControl<TextBlock>("HeadingText").Text})");
            Click(window, dialog.GetControl<CheckBox>("PickupBox"));
            TypeInto(window, dialog, dialog.GetControl<TextBox>("ParentBox"), "1");
            TypeInto(window, dialog, dialog.GetControl<TextBox>("ScaleBox"), "3");
            Click(window, dialog.GetControl<Button>("OkButton"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            h.Expect(!dialog.IsVisible, "OK closes it");
            var rows = Rows();
            h.Expect(rows[1].PickupText == "= 3 × L1" && Math.Abs(rows[1].Value - 3 * rows[0].Value) < 1e-9,
                     $"layer 2 is 3 x layer 1 in QWOT ({rows[1].PickupText}: {rows[1].ValueText} = 3 x {rows[0].ValueText})");
            h.Expect(!rows[1].CanEditThickness && !rows[1].CanVary, "and its thickness and Vary are the pickup's, not editable");

            // A CHILD IN ANOTHER UNIT IS REFUSED, and the dialog stays open with the reason.
            row2 = grid.GetVisualDescendants().OfType<DataGridRow>().First(r => r.DataContext is LayerRow { Index: 1 });
            Click(window, row2.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("layerProperties")));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            dialog = window.OwnedWindows.OfType<LayerPropertiesDialog>().First();
            dialog.UpdateLayout();
            dialog.GetControl<ComboBox>("UnitBox").SelectedItem = "nm";
            Click(window, dialog.GetControl<Button>("OkButton"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var problem = dialog.GetControl<TextBlock>("ProblemText");
            h.Expect(dialog.IsVisible && problem.IsVisible && problem.Text!.Contains("same unit"),
                     $"layer 2 in nm under a QWOT layer 1 is refused: {problem.Text}");
            Click(window, dialog.GetControl<Button>("CancelButton"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            h.Expect(Rows()[1].Unit == "QWOT", "and Cancel leaves it as it was");

            // TYPED IN THE TABLE, IN QWOT: layer 1 to 0.5, and layer 2 follows to 1.5.
            var box = Cell(window, grid, 0, 2);
            h.Expect(box is not null && box.IsEnabled, "layer 1's thickness cell is editable");
            if (box is null) return;
            Click(window, box);
            box.SelectAll();
            window.KeyTextInput("0.5");
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            rows = Rows();
            h.Expect(rows[0].ValueText == "0.5000" && rows[1].ValueText == "1.5000",
                     $"layer 1 reads 0.5000 QWOT and layer 2 follows to 1.5000 ({rows[0].ValueText}, {rows[1].ValueText})");
        });
    }

    // 0 #, 1 Code, 2 Wv, 3 Wv2, 4 Pts, 5 AOI, 6 Mode, 7 Target, 8 Target 2, 9 Limit, 10 Temp, 11 Weight.
    private const int TemperatureColumn = 10;
    private const int WeightColumn = 11;

    /// <summary>The TextBox in a merit cell, found through the row the grid really built.</summary>
    private static TextBox? Cell(MainWindow window, DataGrid grid, int row, int column)
    {
        var item = grid.ItemsSource!.Cast<object>().ElementAt(row);
        var gridRow = grid.GetVisualDescendants().OfType<DataGridRow>()
                          .FirstOrDefault(r => ReferenceEquals(r.DataContext, item));
        if (gridRow is null) return null;
        return grid.Columns[column].GetCellContent(gridRow) as TextBox
               ?? (grid.Columns[column].GetCellContent(gridRow) as Control)?
                    .GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
    }

    public static void TheHelpBarExplainsTheSelectedRow(Harness h, HeadlessUnitTestSession session)
    {
        h.Section("the merit help bar says in words what a row asks for");

        WithWindow(session, window =>
        {
            OpenSample(window);
            ShowTab(window, "MeritTab");
            var grid = Find<DataGrid>(window, "OperandGrid");
            var help = Find<TextBlock>(window, "OperandHelp");
            var rows = grid.ItemsSource!.Cast<MeritRow>().ToList();

            grid.SelectedItem = rows.First(r => r.Code == "MINT");
            h.Expect(help.Text?.Contains("thinner than 8 nm", StringComparison.Ordinal) == true,
                     $"MINT says what it holds: {help.Text}");

            grid.SelectedItem = rows[0];
            h.Expect(help.Text?.Contains("Reflectance", StringComparison.Ordinal) == true
                     && help.Text.Contains("420 nm", StringComparison.Ordinal),
                     $"and RA says what it measures and where: {help.Text}");

            var strip = Find<TextBlock>(window, "MeritText").Text ?? "";
            h.Expect(strip.StartsWith("merit ", StringComparison.Ordinal) && !strip.Contains("met ", StringComparison.Ordinal),
                     $"the strip shows the merit and no tally of rows met: {strip}");
            h.Expect(!(help.Text ?? "").Contains("met", StringComparison.Ordinal),
                     $"and the help bar does not say met or not met: {help.Text}");
        });
    }

    /// <summary>
    /// A column is clipped when it runs under the vertical scrollbar or past the grid's edge, and
    /// a header is truncated when its text needs more width than it was given. Both are measured.
    ///
    /// THE FIRST VERSION OF THIS CHECK PASSED ON THE LAYOUT IT WAS WRITTEN TO CATCH. It compared
    /// the header TextBlock's DesiredSize with its Bounds, but DesiredSize is measured under the
    /// constraint the cell imposes, so a clipped header "wants" exactly what it got. The width the
    /// text needs comes from measuring the same text with no constraint. The overlap was also
    /// missed: Fluent draws the vertical scrollbar OVER the last 16 px of the rows, which is what
    /// hid `met`.
    /// </summary>
    private static void CheckTablesFit(Harness h, MainWindow window, string when)
    {
        foreach (var (name, tab) in new[] { ("LayerGrid", "DesignTab"), ("OperandGrid", "MeritTab") })
        {
            ShowTab(window, tab);
            var grid = Find<DataGrid>(window, name);
            var headers = grid.GetVisualDescendants().OfType<DataGridColumnHeader>()
                              .Where(c => c.IsVisible && c.Content is string { Length: > 0 })
                              .ToArray();
            h.Expect(headers.Length > 0, $"{name} has headers {when}");

            double usable = grid.Bounds.Width;
            foreach (var bar in grid.GetVisualDescendants().OfType<ScrollBar>())
                if (bar.Orientation == Avalonia.Layout.Orientation.Vertical && bar.IsVisible)
                    usable -= bar.Bounds.Width;

            foreach (var header in headers)
            {
                var right = header.TranslatePoint(new Point(header.Bounds.Width, 0), grid);
                h.Expect(right is not null && right.Value.X <= usable + 0.5,
                         $"{name} column '{header.Content}' ends clear of the scrollbar {when} "
                         + $"({right?.X:F0} of {usable:F0} usable px)");

                var text = header.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault();
                double needs = double.PositiveInfinity;
                if (text is not null)
                {
                    var probe = new TextBlock
                    {
                        Text = header.Content as string, FontFamily = text.FontFamily,
                        FontSize = text.FontSize, FontWeight = text.FontWeight,
                    };
                    probe.Measure(Size.Infinity);
                    needs = probe.DesiredSize.Width;
                }
                h.Expect(text is not null && needs <= text.Bounds.Width + 0.5,
                         $"{name} header '{header.Content}' is not truncated {when} "
                         + $"(needs {needs:F0}, has {text?.Bounds.Width:F0} px)");
            }
        }
    }
}
