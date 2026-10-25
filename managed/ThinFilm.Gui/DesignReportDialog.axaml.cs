// DesignReportDialog.axaml.cs — File > Export design report.
//
// Shows a DesignReportForm. Export… checks the range, asks where to save, and hands the request to
// `export`, which answers with the reason it was refused or null. Wiring only.

using Avalonia.Controls;
using Avalonia.Platform.Storage;
using ThinFilm.Api;
using ThinFilm.Gui.Core;

namespace ThinFilm.Gui;

public partial class DesignReportDialog : Window
{
    private readonly DesignReportForm _form;
    private readonly string? _folder;
    private readonly Func<DesignReportRequest, string?> _export;
    private readonly Func<Task<string?>> _choosePath;

    public DesignReportDialog() : this(null!, null, _ => null) { }

    /// <summary><paramref name="choosePath"/> stands in for the save dialog, which a headless test
    /// cannot drive; null is the save dialog.</summary>
    public DesignReportDialog(DesignReportForm form, string? folder, Func<DesignReportRequest, string?> export,
                              Func<Task<string?>>? choosePath = null)
    {
        InitializeComponent();
        _form = form;
        _folder = folder;
        _export = export;
        _choosePath = choosePath ?? ChoosePath;
        if (form is null) return;

        FromBox.Text = form.From;
        ToBox.Text = form.To;
        StepBox.Text = form.Step;
        UnitText.Text = Lengths.Physical(form.WavelengthUnit);
        ExplanationText.Text = form.Explanation;
        OkButton.Click += async (_, _) => await OnOk();
        CancelButton.Click += (_, _) => Close(false);
        Opened += (_, _) => FromBox.Focus();
    }

    private async Task OnOk()
    {
        _form.From = FromBox.Text ?? "";
        _form.To = ToBox.Text ?? "";
        _form.Step = StepBox.Text ?? "";

        // The range is checked before the save dialog, so a mistake is not found after choosing a file.
        string? problem = null;
        if (_form.ToRequest(null, out problem) is not null)
        {
            if (await _choosePath() is not { } path) return;
            problem = _export(_form.ToRequest(path, out _)!);
        }
        if (problem is not null)
        {
            ProblemText.Text = char.ToUpperInvariant(problem[0]) + problem[1..].TrimEnd('.') + ".";
            ProblemText.IsVisible = true;
            return;
        }
        Close(true);
    }

    private async Task<string?> ChoosePath()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export design report",
            SuggestedFileName = _form.SuggestedFileName,
            DefaultExtension = "txt",
            FileTypeChoices = [new FilePickerFileType("Text file (*.txt)") { Patterns = ["*.txt"] }, FilePickerFileTypes.All],
            SuggestedStartLocation = _folder is not null ? await StorageProvider.TryGetFolderFromPathAsync(_folder) : null,
        });
        return file?.TryGetLocalPath();
    }
}
