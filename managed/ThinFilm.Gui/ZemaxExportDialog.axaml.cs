// ZemaxExportDialog.axaml.cs — File > Export to ZEMAX coating.
//
// Shows a ZemaxExportForm. OK hands its request to `export`, which answers with the reason it was
// refused (or null) and whether the refusal was a COAT of that name already in the file; that one
// is asked about here and, on yes, sent again with Replace. Wiring only.

using Avalonia.Controls;
using Avalonia.Platform.Storage;
using ThinFilm.Api;
using ThinFilm.Gui.Core;

namespace ThinFilm.Gui;

public partial class ZemaxExportDialog : Window
{
    private readonly ZemaxExportForm _form;
    private readonly Func<ZemaxExportRequest, (string? Problem, bool Exists)> _export;

    public ZemaxExportDialog() : this(null!, _ => (null, false)) { }

    public ZemaxExportDialog(ZemaxExportForm form, Func<ZemaxExportRequest, (string? Problem, bool Exists)> export)
    {
        InitializeComponent();
        _form = form;
        _export = export;
        if (form is null) return;

        PathBox.Text = form.Path;
        CoatingBox.Text = form.Coating;
        FromBox.Text = form.From;
        ToBox.Text = form.To;
        UnitText.Text = Lengths.Physical(form.WavelengthUnit);
        ExplanationText.Text = form.Explanation;
        BrowseButton.Click += async (_, _) => await OnBrowse();
        OkButton.Click += async (_, _) => await OnOk();
        CancelButton.Click += (_, _) => Close(false);
        Opened += (_, _) => CoatingBox.Focus();
        // Typed in capitals, because that is how it is written and how OpticStudio shows it.
        CoatingBox.TextChanged += (_, _) =>
        {
            var text = CoatingBox.Text ?? "";
            var upper = text.ToUpperInvariant();
            if (upper == text) return;
            int caret = CoatingBox.CaretIndex;
            CoatingBox.Text = upper;
            CoatingBox.CaretIndex = caret;
        };
    }

    private async Task OnBrowse()
    {
        string? folder = Path.GetDirectoryName(PathBox.Text ?? "");
        var start = folder is { Length: > 0 } && Directory.Exists(folder)
            ? await StorageProvider.TryGetFolderFromPathAsync(folder)
            : ZemaxExportForm.DefaultFolder is { } d ? await StorageProvider.TryGetFolderFromPathAsync(d) : null;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Coating file to write into",
            AllowMultiple = false,
            SuggestedStartLocation = start,
            FileTypeFilter = [new FilePickerFileType("Coating file (*.dat)") { Patterns = ["*.dat", "*.DAT"] }, FilePickerFileTypes.All],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) PathBox.Text = path;
    }

    private async Task OnOk()
    {
        _form.Path = PathBox.Text ?? "";
        _form.Coating = CoatingBox.Text ?? "";
        _form.From = FromBox.Text ?? "";
        _form.To = ToBox.Text ?? "";

        var request = _form.ToRequest(out var problem);
        if (request is not null)
        {
            (problem, bool exists) = _export(request);
            if (exists)
            {
                bool yes = await new ConfirmDialog("Replace coating",
                    $"{Path.GetFileName(request.Path)} already has a coating called {ZemaxCoatingFile.Sanitise(request.Coating!)}. "
                    + "Replace it where it stands? Its materials are kept; the rest of the file is not touched.",
                    "Replace").ShowDialog<bool>(this);
                if (!yes) return;
                (problem, _) = _export(request with { Replace = true });
            }
        }
        if (problem is not null)
        {
            ProblemText.Text = char.ToUpperInvariant(problem[0]) + problem[1..].TrimEnd('.') + ".";
            ProblemText.IsVisible = true;
            return;
        }
        Close(true);
    }
}
