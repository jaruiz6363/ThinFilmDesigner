// RunDialog.axaml.cs — Optimization > Refine, Global search, Needle, Gradual evolution: one run,
// watched, compared with the design it started from, then accepted or reverted.
//
// Wiring only. A 200 ms timer polls the RunMonitor and copies what it says into the controls; the
// run goes on in the background, so the window stays live and Stop is heard. Once the run has
// ended, the Merit function, Spectrum and Layers tabs are filled from RunCompareView. Revert drops
// the run and leaves the window open to start again. Closing the window any way but Accept - the
// Close button, Escape, or the title bar's X - stops the run, waits until it has stopped, and
// reverts it: LensHH-LT's dialogs once left a search running after their window had closed.

using Avalonia.Controls;
using Avalonia.Threading;
using ThinFilm.Gui.Core;

namespace ThinFilm.Gui;

public partial class RunDialog : Window
{
    private readonly RunMonitor _monitor;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private bool _accepted;
    private RunCompareView? _shown;

    public RunDialog() : this(null!) { }

    /// <summary>What the window shows, for a test to read the run behind it.</summary>
    public RunMonitor Monitor => _monitor;

    public RunDialog(RunMonitor monitor)
    {
        InitializeComponent();
        _monitor = monitor;
        if (monitor is null) return;

        Title = monitor.Title;
        DescriptionText.Text = monitor.Description;
        NormPanel.IsVisible = monitor.ShowsNorm;
        StartsPanel.IsVisible = monitor.ShowsStarts;
        InsertionsPanel.IsVisible = monitor.ShowsInsertions;
        GrowthPanel.IsVisible = JudgedRemovalPanel.IsVisible = monitor.ShowsEvolution;
        EvolutionPanel.IsVisible = monitor.ShowsMinimumThickness;
        ThinActionPanel.IsVisible = monitor.ShowsThinAction;
        RaiseThinChoice.IsChecked = monitor.Raise;
        RemoveThinChoice.IsChecked = !monitor.Raise;
        InsertionsLabel.Text = monitor.InsertionsLabel;
        GrowthStepsBox.Text = monitor.GrowthStepsText;
        GrowthQwotBox.Text = monitor.GrowthQwotText;
        GrowAtBox.ItemsSource = RunMonitor.GrowSides;
        GrowAtBox.SelectedItem = monitor.GrowAt;
        MaxLayersBox.Text = monitor.MaxLayersText;
        NormBox.ItemsSource = RunMonitor.NormOptions;
        NormBox.SelectedItem = monitor.Norm;
        IterationsLabel.Text = monitor.IterationsLabel;
        IterationsBox.Text = monitor.IterationsText;
        StartsBox.Text = monitor.StartsText;
        GlobalModeBox.ItemsSource = RunMonitor.GlobalModes;
        GlobalModeBox.SelectedItem = monitor.GlobalMode;
        PerturbationBox.Text = monitor.PerturbationText;
        AcceptWorseBox.Text = monitor.AcceptWorseText;
        RandomRangeBox.Text = monitor.RandomRangeText;
        SeedBox.Text = monitor.SeedText;
        ThreadsBox.Text = monitor.ThreadsText;
        GlobalModeBox.SelectionChanged += (_, _) =>
        {
            if (GlobalModeBox.SelectedItem is string mode) _monitor.GlobalMode = mode;
            ShowRun();
        };
        InsertionsBox.Text = monitor.InsertionsText;
        foreach (var name in monitor.MaterialNames)
        {
            var box = new CheckBox { Content = name, IsChecked = monitor.Inserted.Contains(name), Tag = name };
            box.IsCheckedChanged += (_, _) =>
            {
                if (box.IsChecked == true) _monitor.Inserted.Add(name);
                else _monitor.Inserted.Remove(name);
            };
            InsertMaterialsPanel.Children.Add(box);
        }
        MinimumThicknessBox.Text = monitor.MinimumThicknessText;
        MinimumThicknessUnit.Text = monitor.MinimumThicknessUnit;
        DegradationBox.Text = monitor.DegradationText;
        AcceptMeritBox.Text = monitor.AcceptMeritText;
        RemovalsBox.Text = monitor.RemovalsText;
        StepLabel.Text = monitor.StepLabel;
        ShowBox.ItemsSource = RunCompareView.ShowOptions;
        ShowBox.SelectedItem = RunCompareView.BeforeAndAfter;
        ShowBox.SelectionChanged += (_, _) => { if (_shown is not null) DrawSpectrum(); };

        StartButton.Click += (_, _) => OnStart();
        StopButton.Click += (_, _) => { _monitor.Stop(); ShowRun(); };
        AcceptButton.Click += (_, _) => OnAccept();
        RevertButton.Click += (_, _) => OnRevert();
        CloseButton.Click += (_, _) => Close(false);
        RedrawButton.Click += (_, _) => DrawSpectrum();
        QuantitiesButton.Click += async (_, _) =>
        {
            if (_shown is not { } view) return;
            var codes = await new SpectrumQuantitiesDialog(
                view.QuantitiesText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .ShowDialog<string[]?>(this);
            if (codes is null) return;
            view.QuantitiesText = string.Join(",", codes);
            QuantitiesText.Text = view.QuantityText;
            DrawSpectrum();
        };
        CopyLogButton.Click += async (_, _) =>
        {
            if (Clipboard is { } clipboard) await clipboard.SetTextAsync(_monitor.Log);
        };
        _timer.Tick += (_, _) => Tick();
        // EVERY WAY OF CLOSING COMES THROUGH HERE - Close, Escape, the title bar's X, the owner
        // closing - and none of them leaves the run going: Revert stops it and waits for it.
        Closing += (_, _) =>
        {
            _timer.Stop();
            if (!_accepted) _monitor.Revert();
        };
        Tabs.SelectedItem = LogTab;
        ShowRun();
    }

    /// <summary>One poll: new log lines and the readout. Public so a test can tick it.</summary>
    public void Tick()
    {
        _monitor.Poll();
        ShowRun();     // every tick, so the elapsed time moves even through a long step
        if (!_monitor.Running) _timer.Stop();
    }

    private void OnStart()
    {
        if (NormBox.SelectedItem is string norm) _monitor.Norm = norm;
        _monitor.IterationsText = IterationsBox.Text ?? "";
        _monitor.StartsText = StartsBox.Text ?? "";
        if (GlobalModeBox.SelectedItem is string mode) _monitor.GlobalMode = mode;
        _monitor.PerturbationText = PerturbationBox.Text ?? "";
        _monitor.AcceptWorseText = AcceptWorseBox.Text ?? "";
        _monitor.RandomRangeText = RandomRangeBox.Text ?? "";
        _monitor.SeedText = SeedBox.Text ?? "";
        _monitor.ThreadsText = ThreadsBox.Text ?? "";
        _monitor.InsertionsText = InsertionsBox.Text ?? "";
        _monitor.MinimumThicknessText = MinimumThicknessBox.Text ?? "";
        _monitor.Raise = RaiseThinChoice.IsChecked == true;
        _monitor.DegradationText = DegradationBox.Text ?? "";
        _monitor.AcceptMeritText = AcceptMeritBox.Text ?? "";
        _monitor.RemovalsText = RemovalsBox.Text ?? "";
        _monitor.GrowthStepsText = GrowthStepsBox.Text ?? "";
        _monitor.GrowthQwotText = GrowthQwotBox.Text ?? "";
        if (GrowAtBox.SelectedItem is string side) _monitor.GrowAt = side;
        _monitor.MaxLayersText = MaxLayersBox.Text ?? "";
        if (_monitor.Start() is { } problem) { Say(problem); return; }
        ProblemText.IsVisible = false;
        Tabs.SelectedItem = LogTab;
        _timer.Start();
        ShowRun();
    }

    private void OnAccept()
    {
        if (_monitor.Accept() is { } problem) { Say(problem); return; }
        _accepted = true;
        Close(true);
    }

    /// <summary>The design goes back to how it was, and the window stays open for another run.</summary>
    private void OnRevert()
    {
        _timer.Stop();
        _monitor.Revert();
        Tabs.SelectedItem = LogTab;
        ShowRun();
    }

    private void ShowRun()
    {
        StateText.Text = _monitor.StateText;
        ElapsedText.Text = _monitor.ElapsedText;
        StepText.Text = _monitor.StepText;
        LayersText.Text = _monitor.LayersText;
        StartMeritText.Text = _monitor.StartMeritText;
        CurrentLabel.Text = _monitor.CurrentLabel;
        CurrentMeritText.Text = _monitor.CurrentMeritText;
        ChangeText.Text = _monitor.ChangeText;
        LogFileText.Text = _monitor.LogFileText;
        if (LogBox.Text?.Length != _monitor.Log.Length)
        {
            LogBox.Text = _monitor.Log;
            LogBox.CaretIndex = LogBox.Text.Length;
        }
        StepLabel.Text = _monitor.StepLabel;
        StartsLabel.Text = _monitor.PerturbationApplies ? "Hops" : "Starts";
        bool settings = _monitor.CanStart;
        GlobalModeBox.IsEnabled = SeedBox.IsEnabled = ThreadsBox.IsEnabled = settings;
        // Each mode's own settings, the other's hidden.
        HopPanel.IsVisible = _monitor.PerturbationApplies;
        RandomPanel.IsVisible = !_monitor.PerturbationApplies;
        PerturbationBox.IsEnabled = AcceptWorseBox.IsEnabled = RandomRangeBox.IsEnabled = settings;
        NormBox.IsEnabled = IterationsBox.IsEnabled = StartsBox.IsEnabled = InsertionsBox.IsEnabled = settings;
        RemoveThinChoice.IsEnabled = RaiseThinChoice.IsEnabled = settings;
        foreach (var box in InsertMaterialsPanel.Children) box.IsEnabled = settings;
        MinimumThicknessBox.IsEnabled = DegradationBox.IsEnabled = AcceptMeritBox.IsEnabled = RemovalsBox.IsEnabled = StartButton.IsEnabled = settings;
        GrowthStepsBox.IsEnabled = GrowthQwotBox.IsEnabled = GrowAtBox.IsEnabled = MaxLayersBox.IsEnabled = settings;
        StopButton.IsEnabled = _monitor.CanStop;
        AcceptButton.IsEnabled = _monitor.CanAccept;
        RevertButton.IsEnabled = _monitor.CanRevert;
        ShowComparison();
    }

    /// <summary>Fills the three tabs once a run has ended, and empties and closes them otherwise.</summary>
    private void ShowComparison()
    {
        var view = _monitor.CanCompare ? _monitor.Compare() : null;
        MeritTab.IsEnabled = SpectrumTab.IsEnabled = LayersTab.IsEnabled = view is not null;
        if (view is null && _monitor.CanCompare && !ProblemText.IsVisible) Say(_monitor.CompareProblem);
        if (view is null && Tabs.SelectedItem != LogTab) Tabs.SelectedItem = LogTab;
        if (ReferenceEquals(view, _shown)) return;
        _shown = view;
        if (view is null)
        {
            CompareGrid.ItemsSource = null;
            ComparePlot.Series = null;
            BeforeGraph.Profile = AfterGraph.Profile = null;
            return;
        }

        MeritSummaryText.Text = view.MeritSummary;
        CompareGrid.Columns[2].Header = view.WavelengthHeader;
        CompareGrid.Columns[3].Header = view.Wavelength2Header;
        CompareGrid.ItemsSource = view.Rows;

        FromBox.Text = view.FromText;
        ToBox.Text = view.ToText;
        StepsBox.Text = view.StepsText;
        AngleBox.Text = view.AngleText;
        QuantitiesText.Text = view.QuantityText;
        SpectrumUnitText.Text = $"{(view.Unit == "um" ? "µm" : "nm")}, steps";
        ComparePlot.XTitle = view.WavelengthTitle;
        DrawSpectrum();

        foreach (var (graph, profile, name) in new[]
                 {
                     (BeforeGraph, view.Comparison.BeforeStack, "Before"),
                     (AfterGraph, view.Comparison.AfterStack, "After"),
                 })
        {
            graph.Scale = view.Scale;
            graph.Colours = view.Colours;
            graph.LengthUnit = view.Unit;
            graph.Heading = $"{name} — {view.LayersSummary(profile)}";
            graph.Profile = profile;
            graph.InvalidateVisual();
        }
        ProfileNoteText.Text = view.ProfileNote;
    }

    private void DrawSpectrum()
    {
        if (_shown is not { } view) return;
        view.ShowChoice = ShowBox.SelectedItem as string ?? RunCompareView.BeforeAndAfter;
        view.FromText = FromBox.Text ?? "";
        view.ToText = ToBox.Text ?? "";
        view.StepsText = StepsBox.Text ?? "";
        view.AngleText = AngleBox.Text ?? "";
        var series = view.Spectrum(out var problem);
        SpectrumProblemText.IsVisible = problem is not null;
        if (problem is not null)
        {
            SpectrumProblemText.Text = char.ToUpperInvariant(problem[0]) + problem[1..].TrimEnd('.') + ".";
            return;
        }
        ComparePlot.Series = series;
    }

    private void Say(string problem)
    {
        ProblemText.Text = char.ToUpperInvariant(problem[0]) + problem[1..].TrimEnd('.') + ".";
        ProblemText.IsVisible = true;
    }
}
