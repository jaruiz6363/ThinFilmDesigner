// MainWindow.axaml.cs — wiring, and deliberately nothing else.
//
// Every handler here does the same three things: read a control, call one method on
// DesignWorkspace, and push the result back into the controls. There is no arithmetic, no
// formatting decision that matters, and no branch on anything but "did that work". That is the
// point of the split — the half of a GUI that can be wrong in a way a person would not notice is
// the half that computes, and it is in Gui.Core with a test suite on it.
//
// Handlers are attached in code rather than in the XAML because a missing handler is then a
// compile error rather than a runtime one, and a window that opens with a dead button is the
// failure mode a GUI is worst at reporting.
//
// THE MENU AND THE BUTTON STRIPS CALL THE SAME HANDLERS. A menu item that does something subtly
// different from the button beside it is two features that drift; here there is one of each.

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ThinFilm.Api;
using ThinFilm.Gui.Core;

namespace ThinFilm.Gui;

public partial class MainWindow : Window
{
    private readonly DesignWorkspace _workspace = new();

    public MainWindow()
    {
        InitializeComponent();
        OpenMaterialDatabase();

        PlotButton.Click += OnPlot;
        ExportSpectrumButton.Click += OnExportSpectrum;
        QuantitiesButton.Click += OnSpectrumQuantities;
        VsAngleButton.IsCheckedChanged += (_, _) => { if (VsAngleButton.IsChecked == true) OnSpectrumAxis(true); };
        VsWavelengthButton.IsCheckedChanged += (_, _) => { if (VsWavelengthButton.IsChecked == true) OnSpectrumAxis(false); };
        OperandGrid.SelectionChanged += OnOperandSelected;
        DesignPanel.SizeChanged += (_, _) => FitLayerTable();

        OpenMenu.Click += OnLoad;
        OpenSampleMenu.Click += async (_, _) => await OpenSample();
        SaveMenu.Click += OnSave;
        SaveAsMenu.Click += OnSaveAs;
        // The Description: each box reaches the design when it is left. A blank name is refused,
        // and the box shows the name again.
        NameBox.LostFocus += (_, _) =>
        {
            if (!_workspace.Rename(NameBox.Text ?? "")) NameBox.Text = _workspace.DesignName;
            Title = _workspace.Title;
            StatusText.Text = _workspace.Status;
        };
        SourceBox.LostFocus += (_, _) => { if (_workspace.SetDescription(SourceBox.Text, null)) ShowDescription(); StatusText.Text = _workspace.Status; };
        NotesBox.LostFocus += (_, _) => { if (_workspace.SetDescription(null, NotesBox.Text)) ShowDescription(); StatusText.Text = _workspace.Status; };
        DescriptionExpander.PropertyChanged += (_, e) => { if (e.Property == Expander.IsExpandedProperty) ShowDescription(); };
        ImportZemaxMenu.Click += OnImportZemax;
        ExportZemaxMenu.Click += OnExportZemax;
        ImportOptilandMenu.Click += OnImportOptiland;
        ExportOptilandMenu.Click += OnExportOptiland;
        ExportReportMenu.Click += OnExportReport;
        ExitMenu.Click += (_, _) => Close();
        RefineMenu.Click += (_, _) => OnRun("refine");
        GlobalMenu.Click += (_, _) => OnRun("global");
        NeedleRunMenu.Click += (_, _) => OnRun("needle");
        EvolveMenu.Click += (_, _) => OnRun("evolve");
        ThinMenu.Click += (_, _) => OnRun("thin");
        MeritMenu.Click += (_, _) => ShowTab(MeritTab);
        SpectrumMenu.Click += (_, _) => ShowTab(SpectrumTab);
        // Help > User guide: the packaged web pages, in the person's browser (UserGuide.cs).
        UserGuideMenu.Click += (_, _) =>
        {
            if (UserGuide.Find(AppContext.BaseDirectory) is not { } page)
            {
                StatusText.Text = "The user guide is not installed beside this program.";
                return;
            }
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(page) { UseShellExecute = true }); }
            catch (Exception e) { StatusText.Text = $"The user guide would not open: {e.Message}"; }
        };
        // Help > Connect to Claude: thinfilm-mcp-setup, its own small program, as LensHH-LT's.
        McpSetupMenu.Click += (_, _) =>
        {
            if (McpClientSetup.FindSetup(AppContext.BaseDirectory) is not { } setup)
            {
                StatusText.Text = "thinfilm-mcp-setup is not installed beside this program.";
                return;
            }
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(setup) { UseShellExecute = false }); }
            catch (Exception e) { StatusText.Text = $"thinfilm-mcp-setup would not start: {e.Message}"; }
        };
        SystemParametersMenu.Click += OnSystemParameters;
        NewDesignMenu.Click += OnNewDesign;
        MaterialsMenu.Click += OnMaterials;
        InsertOperandButton.Click += (_, _) => { _workspace.InsertOperand(OperandGrid.SelectedIndex); Push(); };
        RemoveOperandButton.Click += OnRemoveOperand;
        ClearOperandsButton.Click += OnClearOperands;
        OpenMeritItem.Click += (_, _) => OnOpenMerit(append: false);
        AppendMeritItem.Click += (_, _) => OnOpenMerit(append: true);
        SaveMeritItem.Click += (_, _) => { if (_meritFile is null) OnSaveMeritAs(); else _ = SaveMeritTo(_meritFile); };
        SaveMeritAsItem.Click += (_, _) => OnSaveMeritAs();
        ExportMeritItem.Click += OnExportMerit;
        DeleteOperandsButton.Click += (_, _) => OnOperandFilter(weight: false);
        AdjustWeightButton.Click += (_, _) => OnOperandFilter(weight: true);
        AddOperandsButton.Click += (_, _) => OnAddOperands(SweepKind.Band);
        AddMultipleAoiButton.Click += (_, _) => OnAddOperands(SweepKind.BandManyAngles);
        AddSingleWvButton.Click += (_, _) => OnAddOperands(SweepKind.OneWavelengthManyAngles);
        InsertLayerButton.Click += (_, _) => { _workspace.InsertLayer(LayerGrid.SelectedIndex); Push(); };
        DeleteLayerButton.Click += (_, _) => OnLayer(i => _workspace.RemoveLayer(i));
        MoveLayerUpButton.Click += (_, _) => OnLayer(i => _workspace.MoveLayer(i, -1), -1);
        MoveLayerDownButton.Click += (_, _) => OnLayer(i => _workspace.MoveLayer(i, +1), +1);
        LayerPropertiesButton.Click += (_, _) =>
        {
            if (LayerGrid.SelectedIndex < 0) { StatusText.Text = "Select a layer first."; return; }
            OnLayerProperties(LayerGrid.SelectedIndex);
        };
        DesignStringItem.Click += (_, _) => OnDesignString();
        CoupleLayersItem.Click += (_, _) => { _workspace.CoupleLayers(); Push(); };
        DecoupleLayersItem.Click += (_, _) => { _workspace.DecoupleLayers(); Push(); };
        ReverseLayersItem.Click += (_, _) => { _workspace.ReverseLayers(media: false); Push(); };
        ReverseAllItem.Click += (_, _) => { _workspace.ReverseLayers(media: true); Push(); };
        ClearLayersItem.Click += OnClearLayers;
        BoundsItem.Click += OnBounds;
        // The "…" in a row: Layer properties for that row, whichever row is selected.
        LayerGrid.AddHandler(Button.ClickEvent, (_, e) =>
        {
            if (e.Source is Button b && b.Classes.Contains("layerProperties") && b.DataContext is LayerRow row)
                OnLayerProperties(row.Index);
        });

        // A cell edit reaches the design when the cell loses focus. Enter commits too, by moving
        // focus to the grid - a TextBox that ignores Enter reads as one that did not take.
        OperandGrid.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter && e.Source is TextBox) OperandGrid.Focus();
        }, RoutingStrategies.Tunnel);
        LayerGrid.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter && e.Source is TextBox) LayerGrid.Focus();
        }, RoutingStrategies.Tunnel);

        // A row setter edits the design from inside a binding; the table is rebuilt after the
        // binding has finished, not in the middle of it.
        _workspace.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(DesignWorkspace.MeritRows) or nameof(DesignWorkspace.Layers))
                Avalonia.Threading.Dispatcher.UIThread.Post(Push);
        };

        Title = $"ThinFilm — ABI {_workspace.AbiVersion}";
        // A new design at start, so there is always one to work on (the user, 2026-10-21): what
        // File > New design gives with its defaults.
        if (new NewDesignForm(_workspace.Database).ToRequest(out _) is { } start) _workspace.CreateNew(start);
        Push();
        if (_databaseProblem is not null) StatusText.Text = _databaseProblem;

        Closing += OnClosing;
    }

    // ---- unsaved changes ---------------------------------------------------------------------

    /// <summary>Ask before closing a changed design. Off only in tests, which close their windows
    /// with nobody there to answer.</summary>
    public bool AskToSave { get; set; } = true;

    private bool _closeConfirmed;

    /// <summary>Closing a changed design asks first; the close waits for the answer.</summary>
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        CommitDescription();
        if (_closeConfirmed || !AskToSave || !_workspace.IsModified) return;
        e.Cancel = true;
        if (!await SaveChangesIfWanted("closing")) return;
        _closeConfirmed = true;
        Close();
    }

    /// <summary>
    /// True when the design may be replaced or closed: it has not changed, or the person chose
    /// Don't save, or chose Save and it was saved. False for Cancel, or a Save As cancelled.
    /// </summary>
    private async Task<bool> SaveChangesIfWanted(string before)
    {
        CommitDescription();
        if (!AskToSave || !_workspace.IsModified) return true;
        string name = _workspace.FileName ?? _workspace.Title.Replace("ThinFilm — ", "");
        var choice = await new SaveChangesDialog(name, before).ShowDialog<SaveChoice>(this);
        if (choice == SaveChoice.Discard) return true;
        if (choice == SaveChoice.Cancel) { StatusText.Text = "Nothing changed; the design is still open."; return false; }
        var file = _file ?? await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save design as",
            SuggestedFileName = "design.tfd",
            DefaultExtension = "tfd",
            FileTypeChoices = [DesignFiles],
        });
        if (file is null) { StatusText.Text = "Save cancelled; the design is still open."; return false; }
        await SaveTo(file);
        return !_workspace.IsModified;
    }

    // ---- handlers ----------------------------------------------------------------------------

    /// <summary>Optimization > Refine, Global search, Needle, Gradual evolution: a window each,
    /// after LensHH-LT; the design changes only if the result is accepted.</summary>
    private async void OnRun(string method)
    {
        if (!_workspace.IsOpen) { StatusText.Text = "No design open."; return; }
        var monitor = new RunMonitor(_workspace, method);
        bool kept = await new RunDialog(monitor).ShowDialog<bool>(this);
        Push();
        // After Push, which shows the workspace's own status line.
        if (!kept && monitor.Report is null) StatusText.Text = $"{monitor.Title} closed without running.";
    }

    // QUESTIONABLE USEFULNESS, MAY BE DELETED IN THE FINAL VERSION. "Where would a layer help?" -
    // menu item and button - was removed at the user's request (2026-10-13); this handler is no
    // longer attached to anything. See DesignWorkspace.ShowNeedleAdvice.
    // private void OnNeedle(object? sender, RoutedEventArgs e)
    // {
    //     _workspace.ShowNeedleAdvice();
    //     StatusText.Text = _workspace.Status;
    // }

    // ---- building the merit function (Clear all, Delete operands, Adjust weight, Add) --------

    private async void OnClearOperands(object? sender, RoutedEventArgs e)
    {
        if (!_workspace.IsOpen) { StatusText.Text = "No design open."; return; }
        int n = _workspace.MeritRows.Count;
        if (n == 0) { StatusText.Text = "The merit function is already empty."; return; }
        bool yes = await new ConfirmDialog("Clear all",
            $"Delete all {n} merit rows? The design is not evaluated again until operands are added.",
            "Clear all").ShowDialog<bool>(this);
        if (!yes) { StatusText.Text = "Merit function unchanged."; return; }
        _workspace.ClearOperands();
        Push();
    }

    /// <summary>Stack > Set vary constraints: bounds on a range of layers, applied on OK.</summary>
    private async void OnBounds(object? sender, RoutedEventArgs e)
    {
        if (!_workspace.IsOpen) { StatusText.Text = "No design open."; return; }
        if (_workspace.Layers.Count == 0) { StatusText.Text = "The design has no layers."; return; }
        var form = new BoundsForm(_workspace.Layers, _workspace.MaterialNames, _workspace.ThicknessUnit,
                                  _workspace.WavelengthUnit)
        { UnconstrainedMaximumNm = _workspace.UnconstrainedMaximum };
        bool ok = await new BoundsDialog(form, r => _workspace.SetLayerBounds(r) ? null : _workspace.Status)
            .ShowDialog<bool>(this);
        if (!ok) StatusText.Text = "Constraints unchanged.";
        else Push();
    }

    private async void OnClearLayers(object? sender, RoutedEventArgs e)
    {
        if (!_workspace.IsOpen) { StatusText.Text = "No design open."; return; }
        int n = _workspace.Layers.Count;
        if (n == 0) { StatusText.Text = "The stack is already empty."; return; }
        bool yes = await new ConfirmDialog("Delete all layers",
            $"Delete all {n} layers, leaving the bare substrate? The materials and merit function are kept.",
            "Delete all").ShowDialog<bool>(this);
        if (!yes) { StatusText.Text = "Layers unchanged."; return; }
        _workspace.ClearLayers();
        Push();
    }

    private async void OnOperandFilter(bool weight)
    {
        if (!_workspace.IsOpen) { StatusText.Text = "No design open."; return; }
        var form = new OperandFilterForm(_workspace.WavelengthUnit);
        bool ok = await new OperandFilterDialog(form, _workspace.MatchCount, weight).ShowDialog<bool>(this);
        if (!ok) { StatusText.Text = "Merit function unchanged."; return; }
        var filter = form.ToFilter(out _)!;
        if (weight) _workspace.SetOperandWeights(filter, form.ToWeight(out _)!.Value);
        else _workspace.DeleteOperands(filter);
        Push();
    }

    private async void OnAddOperands(SweepKind kind)
    {
        if (!_workspace.IsOpen) { StatusText.Text = "No design open."; return; }
        var form = new OperandSweepForm(kind, _workspace.WavelengthUnit);
        _workspace.PrefillSweep(form);
        bool ok = await new OperandSweepDialog(form).ShowDialog<bool>(this);
        if (!ok) { StatusText.Text = "Merit function unchanged."; return; }
        _workspace.AddOperands(form.ToSweep(out _)!);
        Push();
    }

    private void OnPlot(object? sender, RoutedEventArgs e)
    {
        ReadSpectrumBoxes();
        _workspace.RefreshSpectrum();
        Push();
    }

    /// <summary>Quantities…: the checklist, and a redraw with what it returns.</summary>
    private async void OnSpectrumQuantities(object? sender, RoutedEventArgs e)
    {
        var codes = await new SpectrumQuantitiesDialog(_workspace.SpectrumQuantityCodes).ShowDialog<string[]?>(this);
        if (codes is null) return;
        _workspace.SpectrumQuantities = string.Join(",", codes);
        ReadSpectrumBoxes();
        if (_workspace.IsOpen) _workspace.RefreshSpectrum();
        Push();
    }

    /// <summary>The boxes into the workspace, for whichever x axis they currently describe.</summary>
    private void ReadSpectrumBoxes()
    {
        static bool Num(string? text, out double v) =>
            double.TryParse(text, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out v);
        if (_workspace.SpectrumVsAngle)
        {
            if (Num(FromBox.Text, out var from)) _workspace.SpectrumAngleFrom = from;
            if (Num(ToBox.Text, out var to)) _workspace.SpectrumAngleTo = to;
            if (int.TryParse(StepsBox.Text, out var steps)) _workspace.SpectrumAngleSteps = steps;
            if (Num(AngleBox.Text, out var wl)) _workspace.SpectrumWavelength = wl * UnitFactor;
        }
        else
        {
            if (Num(FromBox.Text, out var from)) _workspace.SpectrumFrom = from * UnitFactor;
            if (Num(ToBox.Text, out var to)) _workspace.SpectrumTo = to * UnitFactor;
            if (int.TryParse(StepsBox.Text, out var steps)) _workspace.SpectrumSteps = steps;
            if (Num(AngleBox.Text, out var angle)) _workspace.SpectrumAngle = angle;
        }
    }

    /// <summary>The x axis switched: keep what the boxes said for the old one, show the new one's
    /// values, and redraw.</summary>
    private void OnSpectrumAxis(bool vsAngle)
    {
        if (_workspace.SpectrumVsAngle == vsAngle) return;
        ReadSpectrumBoxes();
        _workspace.SpectrumVsAngle = vsAngle;
        if (_workspace.IsOpen) _workspace.RefreshSpectrum();
        Push();
    }

    /// <summary>Layer properties for one layer: modal, applied on OK in one call, and kept open
    /// with the reason when the design refuses it.</summary>
    private async void OnLayerProperties(int index)
    {
        if (index < 0 || index >= _workspace.Layers.Count) return;
        var form = new LayerPropertiesForm(_workspace.Layers[index], _workspace.Layers.Count,
                                           _workspace.ThicknessUnit, _workspace.ReferenceWavelength,
                                           _workspace.WavelengthUnit, _workspace.UnitsPerNm)
        { UnconstrainedMaximumNm = _workspace.UnconstrainedMaximum };
        bool ok = await new LayerPropertiesDialog(form,
                p => _workspace.SetLayerProperties(index, p) ? null : _workspace.Status)
            .ShowDialog<bool>(this);
        if (!ok) StatusText.Text = $"Layer {index + 1} unchanged.";
        else
        {
            Push();
            LayerGrid.SelectedIndex = index;
        }
    }

    /// <summary>The last string typed in Input design string, offered again next time.</summary>
    private string _designString = "";

    /// <summary>Input design string: the whole stack from text, modal, applied on OK and kept open
    /// with the reason when the design refuses it.</summary>
    private async void OnDesignString()
    {
        if (_workspace.MaterialNames.Count == 0) { StatusText.Text = "No design open."; return; }
        var form = new DesignStringForm(_workspace.MaterialNames, _workspace.UnitsPerNm, _workspace.ThicknessUnit,
                                        _workspace.Layers.Count, _designString);
        bool ok = await new DesignStringDialog(form, Lengths.Say(_workspace.ReferenceWavelength, _workspace.WavelengthUnit),
                r => { _designString = r.Text; return _workspace.ApplyDesignString(r) ? null : _workspace.Status; })
            .ShowDialog<bool>(this);
        if (!ok) StatusText.Text = "Layers unchanged.";
        else Push();
    }

    private void OnOperandSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (OperandGrid.SelectedItem is MeritRow row) OperandHelp.Text = row.Help;
    }

    /// <summary>The file the design was opened from or last saved to: what Save writes. Null for
    /// a new design or a sample, which Save then treats as Save As.</summary>
    private IStorageFile? _file;

    private static readonly FilePickerFileType DesignFiles = new("ThinFilm design (*.tfd)") { Patterns = ["*.tfd"] };
    private static readonly FilePickerFileType OlderDesignFiles = new("Older ThinFilm design (*.json)") { Patterns = ["*.json"] };

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        if (_file is null) OnSaveAs(sender, e);
        else _ = SaveTo(_file);
    }

    private async void OnSaveAs(object? sender, RoutedEventArgs e)
    {
        if (!_workspace.IsOpen) { StatusText.Text = "Nothing to save."; return; }
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save design as",
            // .tfd, a ThinFilm design: the file is JSON, but .json says nothing about what it holds.
            // A design opened from an older .json is offered back as .tfd.
            SuggestedFileName = _file is null ? "design.tfd" : Path.ChangeExtension(_file.Name, ".tfd"),
            DefaultExtension = "tfd",
            FileTypeChoices = [DesignFiles],
        });
        if (file is null) { StatusText.Text = "Save cancelled."; return; }
        await SaveTo(file);
    }

    private async Task SaveTo(IStorageFile file)
    {
        CommitDescription();
        ReadSpectrumBoxes();   // the plot is saved as the boxes show it, plotted or not
        var json = _workspace.Save();
        if (json is null) { StatusText.Text = _workspace.Status; return; }

        await using var stream = await file.OpenWriteAsync();
        // Truncated first: a design saved shorter than the file it replaces must not keep the
        // old file's tail, which would leave a JSON document that no longer parses.
        if (stream.CanSeek) stream.SetLength(0);
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(json);
        _file = file;
        _workspace.SavedAs(file.Name);
        Title = _workspace.Title;
        StatusText.Text = _workspace.Status;
    }

    // ---- the merit function as a file of its own -------------------------------------------

    /// <summary>The merit file last opened or saved: what Save merit function writes.</summary>
    private IStorageFile? _meritFile;

    private static readonly FilePickerFileType MeritFiles = new("ThinFilm merit function (*.tfm)") { Patterns = ["*.tfm"] };
    private static readonly FilePickerFileType CsvFiles = new("CSV (*.csv)") { Patterns = ["*.csv"] };

    private static async Task WriteText(IStorageFile file, string text)
    {
        await using var stream = await file.OpenWriteAsync();
        if (stream.CanSeek) stream.SetLength(0);
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(text);
    }

    private async void OnSaveMeritAs()
    {
        if (!_workspace.IsOpen) { StatusText.Text = "No design open."; return; }
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save merit function as",
            SuggestedFileName = _meritFile?.Name ?? "merit.tfm",
            DefaultExtension = "tfm",
            FileTypeChoices = [MeritFiles],
        });
        if (file is null) { StatusText.Text = "Save cancelled."; return; }
        await SaveMeritTo(file);
    }

    private async Task SaveMeritTo(IStorageFile file)
    {
        var json = _workspace.SaveMeritFunction();
        if (json is null) { StatusText.Text = _workspace.Status; return; }
        await WriteText(file, json);
        _meritFile = file;
        _workspace.SavedMeritFunction(file.Name);
        StatusText.Text = _workspace.Status;
    }

    /// <summary>Open replaces the table (asking first when it has rows); append adds the file's
    /// rows after them, and the file opened is not what Save merit function then writes.</summary>
    private async void OnOpenMerit(bool append)
    {
        if (!_workspace.IsOpen) { StatusText.Text = "No design open."; return; }
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = append ? "Append merit function" : "Open merit function",
            AllowMultiple = false,
            FileTypeFilter = [MeritFiles, FilePickerFileTypes.All],
        });
        if (files.Count == 0) { StatusText.Text = append ? "Append cancelled." : "Open cancelled."; return; }
        int n = _workspace.MeritRows.Count;
        if (!append && n > 0)
        {
            bool yes = await new ConfirmDialog("Open merit function",
                $"Replace the {n} merit rows with those in {files[0].Name}? The layers are kept.",
                "Replace").ShowDialog<bool>(this);
            if (!yes) { StatusText.Text = "Merit function unchanged."; return; }
        }
        await using var stream = await files[0].OpenReadAsync();
        using var reader = new StreamReader(stream);
        if (_workspace.OpenMeritFunction(await reader.ReadToEndAsync(), files[0].Name, append) && !append)
            _meritFile = files[0];
        Push();
    }

    private static readonly FilePickerFileType TextFiles = new("Text, tab-separated (*.txt)") { Patterns = ["*.txt"] };

    /// <summary>Spectrum > Export…: what is plotted, as text a spreadsheet or another program reads.</summary>
    private async void OnExportSpectrum(object? sender, RoutedEventArgs e)
    {
        var text = _workspace.SpectrumText();
        if (text is null) { StatusText.Text = _workspace.Status; return; }
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export spectrum as text",
            SuggestedFileName = Path.ChangeExtension(_file?.Name ?? "spectrum", ".txt"),
            DefaultExtension = "txt",
            FileTypeChoices = [TextFiles, FilePickerFileTypes.All],
        });
        if (file is null) { StatusText.Text = "Export cancelled."; return; }
        await WriteText(file, text);
        StatusText.Text = $"Spectrum exported to {file.Name}: the plotted curves, tab-separated.";
    }

    private async void OnExportMerit(object? sender, RoutedEventArgs e)
    {
        var csv = _workspace.ExportMeritCsv();
        if (csv is null) { StatusText.Text = _workspace.Status; return; }
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export merit function as CSV",
            SuggestedFileName = Path.ChangeExtension(_meritFile?.Name ?? _file?.Name ?? "merit", ".csv"),
            DefaultExtension = "csv",
            FileTypeChoices = [CsvFiles],
        });
        if (file is null) { StatusText.Text = "Export cancelled."; return; }
        await WriteText(file, csv);
        StatusText.Text = $"Merit function exported to {file.Name}, with each row's value, residual and flags.";
    }

    // ---- OpticStudio coating files -----------------------------------------------------------

    /// <summary>The coating file last imported from or exported to, offered again.</summary>
    private string? _zemaxFile;

    private static readonly FilePickerFileType CoatingFiles = new("Coating file (*.dat)") { Patterns = ["*.dat", "*.DAT"] };

    private async void OnImportZemax(object? sender, RoutedEventArgs e)
    {
        if (!_workspace.IsOpen) { StatusText.Text = "Open or create a design first: the coating replaces its layers."; return; }
        string? folder = _zemaxFile is not null ? Path.GetDirectoryName(_zemaxFile) : ZemaxExportForm.DefaultFolder;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import ZEMAX coating",
            AllowMultiple = false,
            FileTypeFilter = [CoatingFiles, FilePickerFileTypes.All],
            SuggestedStartLocation = folder is not null ? await StorageProvider.TryGetFolderFromPathAsync(folder) : null,
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) { StatusText.Text = "Import cancelled."; return; }
        var coatings = _workspace.ZemaxCoatings(path);
        if (coatings is null) { StatusText.Text = _workspace.Status; return; }
        if (coatings.Length == 0) { StatusText.Text = $"{files[0].Name} has no layer-stack coatings."; return; }
        _zemaxFile = path;
        var choices = coatings.Select(c => new CoatingChoice(c.Name, c.Layers,
            c.ReferenceNm is { } w ? $"at {Lengths.Say(w, _workspace.WavelengthUnit)}" : null, c.Problem)).ToArray();
        int layers = _workspace.Layers.Count;
        bool ok = await new CoatingImportDialog("Import ZEMAX coating", files[0].Name, choices,
                (layers > 0 ? $"The coating replaces the design's {layers} layers. " : "")
                + "Its materials are added under their names in the file; the media, wavelengths and merit function are kept. "
                + "A thickness in waves is counted at the wavelength the file says it was designed at, or at the design's reference wavelength.",
                i => _workspace.ImportZemaxCoating(path, coatings[i].Name) ? null : _workspace.Status)
            .ShowDialog<bool>(this);
        if (!ok) { StatusText.Text = "Layers unchanged."; return; }
        Push();
    }

    private async void OnExportZemax(object? sender, RoutedEventArgs e)
    {
        if (_workspace.ZemaxExportForm(_zemaxFile) is not { } form) { StatusText.Text = "No design open."; return; }
        if (_workspace.Layers.Count == 0) { StatusText.Text = "The design has no layers to export."; return; }
        string? written = null;
        bool ok = await new ZemaxExportDialog(form, r =>
            {
                var report = _workspace.ExportZemaxCoating(r, out bool exists);
                if (report is not null) written = r.Path;
                return (report is null ? _workspace.Status : null, exists);
            })
            .ShowDialog<bool>(this);
        if (!ok) { StatusText.Text = "Nothing exported."; return; }
        _zemaxFile = written;
        StatusText.Text = _workspace.Status;
    }

    /// <summary>The Optiland file last imported from or exported to, offered again.</summary>
    private string? _optilandFile;

    private static readonly FilePickerFileType OptilandFiles = new("Optiland coating or lens (*.json)") { Patterns = ["*.json"] };

    private async void OnImportOptiland(object? sender, RoutedEventArgs e)
    {
        if (!_workspace.IsOpen) { StatusText.Text = "Open or create a design first: the coating replaces its layers."; return; }
        string? folder = _optilandFile is not null ? Path.GetDirectoryName(_optilandFile) : null;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Optiland coating",
            AllowMultiple = false,
            FileTypeFilter = [OptilandFiles, FilePickerFileTypes.All],
            SuggestedStartLocation = folder is not null ? await StorageProvider.TryGetFolderFromPathAsync(folder) : null,
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) { StatusText.Text = "Import cancelled."; return; }
        var coatings = _workspace.OptilandCoatings(path);
        if (coatings is null) { StatusText.Text = _workspace.Status; return; }
        _optilandFile = path;
        int layers = _workspace.Layers.Count;
        bool ok = await new CoatingImportDialog("Import Optiland coating", files[0].Name,
                coatings.Select(c => new CoatingChoice(c.Name, c.Layers, null, c.Problem)).ToArray(),
                (layers > 0 ? $"The coating replaces the design's {layers} layers. " : "")
                + "Its materials are added under their Optiland names, as equations where they are equations; the media, "
                + "wavelengths and merit function are kept.",
                i => _workspace.ImportOptilandCoating(path, i, coatings[i].Name) ? null : _workspace.Status)
            .ShowDialog<bool>(this);
        if (!ok) { StatusText.Text = "Layers unchanged."; return; }
        Push();
    }

    private string? _reportFile;

    /// <summary>File > Export design report: the range, then where to save it.</summary>
    private async void OnExportReport(object? sender, RoutedEventArgs e)
    {
        if (_workspace.DesignReportForm() is not { } form) { StatusText.Text = "No design open."; return; }
        string? written = null;
        bool ok = await new DesignReportDialog(form, _reportFile is not null ? Path.GetDirectoryName(_reportFile) : null, r =>
            {
                if (!_workspace.ExportDesignReport(r)) return _workspace.Status;
                written = r.Path;
                return null;
            })
            .ShowDialog<bool>(this);
        if (!ok) { StatusText.Text = "Nothing exported."; return; }
        _reportFile = written;
        StatusText.Text = _workspace.Status;
    }

    private async void OnExportOptiland(object? sender, RoutedEventArgs e)
    {
        if (!_workspace.IsOpen) { StatusText.Text = "No design open."; return; }
        if (_workspace.Layers.Count == 0) { StatusText.Text = "The design has no layers to export."; return; }
        string? folder = _optilandFile is not null ? Path.GetDirectoryName(_optilandFile) : null;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Optiland coating",
            // design-coating.json, so a coating export is not mistaken for, or written over, a design
            // (the user, 2026-10-21: an export replaced an older design1.json).
            SuggestedFileName = $"{Path.GetFileNameWithoutExtension(_workspace.FileName ?? "design")}-coating.json",
            DefaultExtension = "json",
            FileTypeChoices = [OptilandFiles],
            SuggestedStartLocation = folder is not null ? await StorageProvider.TryGetFolderFromPathAsync(folder) : null,
        });
        if (file is null || file.TryGetLocalPath() is not { } path) { StatusText.Text = "Export cancelled."; return; }
        if (_workspace.ExportOptilandCoating(path)) _optilandFile = path;
        StatusText.Text = _workspace.Status;
    }

    private async void OnLoad(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open design",
            AllowMultiple = false,
            FileTypeFilter = [DesignFiles, OlderDesignFiles, FilePickerFileTypes.All],
        });
        if (files.Count == 0) { StatusText.Text = "Open cancelled."; return; }
        await OpenFile(files[0]);
    }

    /// <summary>File > Open sample: the sample browser, not a file picker, so the samples are reached
    /// the same way everywhere - a macOS picker cannot look inside the .app they ship in. A sample
    /// opens as a copy: no file is remembered, so Save asks where to write it.</summary>
    private async Task OpenSample()
    {
        if (await new SampleBrowserDialog().ShowDialog<string?>(this) is not { } path)
        {
            StatusText.Text = "Open sample cancelled.";
            return;
        }
        await OpenSamplePath(path);
    }

    /// <summary>A sample design opened as a copy, by its path.</summary>
    public async Task OpenSamplePath(string path)
    {
        if (!await SaveChangesIfWanted("opening another design")) return;
        OpenDocument(await File.ReadAllTextAsync(path), Path.GetFileName(path), null);
    }

    /// <summary>A design named from outside the window: on the command line (thinfilm-gui
    /// design.tfd), or by opening a .tfd file in Explorer, Finder or a file manager.</summary>
    public async Task OpenPath(string path)
    {
        if (!File.Exists(path) || await StorageProvider.TryGetFileFromPathAsync(Path.GetFullPath(path)) is not { } file)
        {
            StatusText.Text = $"Could not open {path}: there is no such file.";
            return;
        }
        await OpenFile(file);
    }

    private async Task OpenFile(IStorageFile file)
    {
        if (!await SaveChangesIfWanted("opening another design")) return;
        await using var stream = await file.OpenReadAsync();
        using var reader = new StreamReader(stream);
        OpenDocument(await reader.ReadToEndAsync(), file.Name, file);
    }

    /// <summary>A design's text, opened under a file name; <paramref name="file"/> is where Save
    /// writes, or null to have Save ask.</summary>
    public void OpenDocument(string json, string fileName, IStorageFile? file = null)
    {
        if (_workspace.Open(json, fileName)) _file = file;
        ShowTab(DesignTab);
        Push();
    }

    /// <summary>A layer operation on the selected row; the selection follows a moved layer, so
    /// pressing Move up three times moves one layer three places.</summary>
    private void OnLayer(Func<int, bool> operation, int follow = 0)
    {
        int i = LayerGrid.SelectedIndex;
        if (i < 0) { StatusText.Text = "Select a layer first."; return; }
        bool ok = operation(i);
        Push();
        int next = ok ? i + follow : i;
        if (next >= 0 && next < _workspace.Layers.Count) LayerGrid.SelectedIndex = next;
    }

    private void OnRemoveOperand(object? sender, RoutedEventArgs e)
    {
        if (OperandGrid.SelectedItem is MeritRow row) _workspace.RemoveOperand(row.Index);
        else StatusText.Text = "Select the merit row to remove.";
        Push();
    }

    /// <summary>
    /// The person's own material database: the one System Parameters names, or their working copy in
    /// ThinFilmHome, made from the shipped database the first time. If it cannot be read the
    /// window says why and carries on with the shipped one, rather than refusing to start.
    /// </summary>
    private string? _databaseProblem;

    private void OpenMaterialDatabase()
    {
        _workspace.Database = ThinFilm.Api.MaterialDatabase.ForPerson(out _databaseProblem);
    }

    private async void OnMaterials(object? sender, RoutedEventArgs e)
    {
        if (_workspace.Materials is not { } set) { StatusText.Text = "Open or create a design first."; return; }
        var form = new MaterialsForm(_workspace.Database, set) { WavelengthUnit = _workspace.WavelengthUnit };
        bool ok = await new MaterialsDialog(form).ShowDialog<bool>(this);
        if (!ok) { StatusText.Text = "Materials unchanged."; return; }
        _workspace.ApplyMaterials(form.CoatingEdits(), form.Incident, form.Substrate);
        Push();
    }

    private async void OnNewDesign(object? sender, RoutedEventArgs e)
    {
        var form = new NewDesignForm(_workspace.Database) { WavelengthUnit = _workspace.WavelengthUnit };
        var request = await new NewDesignDialog(form).ShowDialog<ThinFilm.Api.NewDesignRequest?>(this);
        if (request is null) { StatusText.Text = "New design cancelled."; return; }
        if (!await SaveChangesIfWanted("starting a new design")) return;
        if (_workspace.CreateNew(request)) _file = null;
        ShowTab(DesignTab);
        Push();
    }

    private async void OnSystemParameters(object? sender, RoutedEventArgs e)
    {
        string current = _workspace.Database.Path ?? ThinFilm.Api.MaterialDatabase.DefaultUserPath;
        bool ok = await new SystemParametersDialog(_workspace.IsOpen ? _workspace.WavelengthUnit : null, current,
                                              _workspace.ThicknessUnit, _workspace.ReferenceWavelength,
                                              _workspace.UnconstrainedMaximum,
                                              ApplySystemParameters,
                                              new SystemSettings(_workspace.BackSurface, _workspace.SubstrateThicknessMm,
                                                                 _workspace.AngleInAir))
            .ShowDialog<bool>(this);
        if (!ok) Push();
    }

    /// <summary>
    /// What Apply and OK in System Parameters do: each change made, in order, the window redrawn, and
    /// the reason returned if one was refused (null when all were taken). Apply leaves the dialog
    /// open, so "nm, Apply, then QWOT at 600 nm, OK" is one visit - the user's recipe for moving
    /// the reference without moving the spectrum.
    /// </summary>
    private string? ApplySystemParameters(SystemParametersResult result)
    {
        string current = _workspace.Database.Path ?? ThinFilm.Api.MaterialDatabase.DefaultUserPath;
        string? refused = null;
        if (result.Unit is { } unit && unit != _workspace.WavelengthUnit && !_workspace.SetWavelengthUnit(unit))
            refused = _workspace.Status;
        if (refused is null && result.ThicknessUnit is { } t
            && (t != _workspace.ThicknessUnit || result.ReferenceNm != _workspace.ReferenceWavelength)
            && !_workspace.SetThicknessUnits(t, result.ReferenceNm))
            refused = _workspace.Status;
        if (refused is null && result.MaximumNm > 0 && Math.Abs(result.MaximumNm - _workspace.UnconstrainedMaximum) > 1e-9
            && !_workspace.SetUnconstrainedMaximum(result.MaximumNm))
            refused = _workspace.Status;
        if (refused is null && result.System is { } sys
            && (sys.BackSurface != _workspace.BackSurface || sys.AngleInAir != _workspace.AngleInAir
                || Math.Abs(sys.SubstrateMm - _workspace.SubstrateThicknessMm) > 1e-12)
            && !_workspace.SetSystem(sys.BackSurface, sys.SubstrateMm, sys.AngleInAir))
            refused = _workspace.Status;
        string message = _workspace.Status;

        // A different database is read before it is adopted, so a file that does not parse leaves
        // the current one in use and says why.
        if (refused is null && result.DatabasePath.Length > 0
            && !string.Equals(result.DatabasePath, current, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                _workspace.Database = ThinFilm.Api.MaterialDatabase.Load(result.DatabasePath);
                ThinFilm.Api.ThinFilmHome.SaveSettings(new ThinFilm.Api.AppSettings { MaterialDatabasePath = result.DatabasePath });
                message = $"Material database: {result.DatabasePath} ({_workspace.Database.Materials.Count} materials).";
            }
            catch (Exception x) when (x is IOException or FormatException or UnauthorizedAccessException)
            {
                refused = $"That material database was not used - {x.Message}";
            }
        }
        Push();
        StatusText.Text = refused ?? message;
        return refused;
    }

    /// <summary>The merit as text: a dash for a blocked design, which has none.</summary>
    private string Merit() => double.IsNaN(_workspace.Merit) ? "—" : _workspace.Merit.ToString("G6");

    // ---- units ------------------------------------------------------------------------------

    /// <summary>Nanometres per display unit: what a number typed in a wavelength box is worth.</summary>
    private double UnitFactor => _workspace.WavelengthUnit == "um" ? 1000.0 : 1.0;

    /// <summary>Every header, label and box that shows a wavelength, in the design's unit.</summary>
    private void ShowUnits()
    {
        string u = MeritRow.UnitLabel(_workspace.WavelengthUnit);
        OperandGrid.Columns[MeritWavelengthColumn].Header = $"Wv ({u})";
        OperandGrid.Columns[MeritWavelength2Column].Header = $"Wv2 ({u})";
        // Physical thicknesses follow the same unit (Lengths.cs): the layer table's physical
        // column and the merit table's layer limit.
        OperandGrid.Columns[MeritLimitColumn].Header = $"Limit ({u})";
        LayerGrid.Columns[LayerPhysicalColumn].Header = u;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string f = u == "µm" ? "0.####" : "0.##";
        if (_workspace.SpectrumVsAngle)
        {
            SpectrumUnitText.Text = "°, steps";
            FixedLabel.Text = $"λ ({u})";
            Plot.XTitle = "angle of incidence (°)";
            FromBox.Text = _workspace.SpectrumAngleFrom.ToString("0.##", inv);
            ToBox.Text = _workspace.SpectrumAngleTo.ToString("0.##", inv);
            StepsBox.Text = _workspace.SpectrumAngleSteps.ToString(inv);
            AngleBox.Text = (_workspace.SpectrumWavelength / UnitFactor).ToString(f, inv);
        }
        else
        {
            SpectrumUnitText.Text = $"{u}, steps";
            FixedLabel.Text = "AOI (°)";
            Plot.XTitle = $"wavelength ({u})";
            FromBox.Text = (_workspace.SpectrumFrom / UnitFactor).ToString(f, inv);
            ToBox.Text = (_workspace.SpectrumTo / UnitFactor).ToString(f, inv);
            StepsBox.Text = _workspace.SpectrumSteps.ToString(inv);
            AngleBox.Text = _workspace.SpectrumAngle.ToString("0.##", inv);
        }
        VsAngleButton.IsChecked = _workspace.SpectrumVsAngle;
        VsWavelengthButton.IsChecked = !_workspace.SpectrumVsAngle;
    }

    // The columns whose headers carry the unit, by position in the XAML.
    private const int MeritWavelengthColumn = 2, MeritWavelength2Column = 3, MeritLimitColumn = 9;
    private const int LayerPhysicalColumn = 4;

    // ---- layout ------------------------------------------------------------------------------

    private void ShowTab(TabItem tab) => Tabs.SelectedItem = tab;

    /// <summary>
    /// The layer table takes the height it needs, up to what the panel has left after the two
    /// medium lines. An Auto row is measured with unbounded height, so without this limit a
    /// hundred-layer stack would push the substrate line off the bottom of the window.
    /// </summary>
    private void FitLayerTable()
    {
        double reserved = IncidentText.Bounds.Height + SubstrateText.Bounds.Height + 40;
        LayerGrid.MaxHeight = Math.Max(120, DesignPanel.Bounds.Height - reserved);
    }

    // ---- the one place the controls are written --------------------------------------------

    /// <summary>What is typed in the Description's boxes, given to the design before it is saved
    /// or left - Ctrl+S with the cursor still in a box would otherwise save the text before it.</summary>
    private void CommitDescription()
    {
        if (!_workspace.IsOpen) return;
        if (!_workspace.Rename(NameBox.Text ?? "")) NameBox.Text = _workspace.DesignName;
        _workspace.SetDescription(SourceBox.Text, NotesBox.Text);
    }

    /// <summary>The Description's header: the word alone when open, the source in one line when folded.</summary>
    private void ShowDescription() =>
        DescriptionHeader.Text = DescriptionExpander.IsExpanded ? "Description" : $"Description: {_workspace.DescriptionSummary}";

    /// <summary>The design whose text the Description's boxes hold.</summary>
    private string? _described;

    private void Push()
    {
        int layerSelected = LayerGrid.SelectedIndex;
        LayerGrid.ItemsSource = _workspace.Layers;
        if (layerSelected >= 0 && layerSelected < _workspace.Layers.Count) LayerGrid.SelectedIndex = layerSelected;
        int selected = OperandGrid.SelectedIndex;
        OperandGrid.ItemsSource = _workspace.MeritRows;
        if (selected >= 0 && selected < _workspace.MeritRows.Count) OperandGrid.SelectedIndex = selected;
        OperandHelp.Text = "Select a row to see what it asks for.";
        Plot.Series = _workspace.Spectrum;
        QuantitiesText.Text = _workspace.SpectrumQuantityText;
        ShowUnits();
        SummaryText.Text = _workspace.IsOpen
            ? $"merit {Merit()}  ·  {_workspace.Summary}"
            : "";
        MeritText.Text = _workspace.IsOpen
            ? $"merit {Merit()}"
            : "No design open.";
        IncidentText.Text = _workspace.IncidentText;
        SubstrateText.Text = _workspace.SubstrateText;
        // Not while a box is being typed in: the design has not got that text yet. But another
        // design always gets its own text, or leaving the box would give it the last one's.
        bool another = _workspace.DesignId != _described;
        _described = _workspace.DesignId;
        if (another || !NameBox.IsFocused) NameBox.Text = _workspace.DesignName;
        if (another || !SourceBox.IsFocused) SourceBox.Text = _workspace.DesignSource;
        if (another || !NotesBox.IsFocused) NotesBox.Text = _workspace.DesignNotes;
        ShowDescription();
        PlotNote.Text = _workspace.IsOpen ? _workspace.PlotNote : "";
        StatusText.Text = _workspace.Status;
        Title = _workspace.Title;
    }
}
