// CoefficientsDialog.axaml.cs — a material's dispersion, edited as fields.
//
// Wiring only: what the fields mean, whether they make a model, the library and the table import
// are CoefficientsForm's (Gui.Core, tested). Returns the model from ShowDialog on OK, and null on
// Cancel. The preview redraws as the fields change, so a mistyped coefficient shows as a curve
// that is wrong before it is a design that is.

using Avalonia.Controls;
using Avalonia.Platform.Storage;
using ThinFilm.Api;
using ThinFilm.Gui.Core;

namespace ThinFilm.Gui;

public partial class CoefficientsDialog : Window
{
    private readonly CoefficientsForm _form;
    private readonly TextBox[] _b, _c;
    private bool _loading;

    public CoefficientsDialog() : this(new CoefficientsForm("SiO2", MaterialModel.Constant(1.46), MaterialDatabase.Shipped())) { }

    public CoefficientsDialog(CoefficientsForm form)
    {
        InitializeComponent();
        _form = form;
        _b = [B1, B2, B3, B4, B5, B6];
        _c = [C1, C2, C3, C4, C5, C6];
        TitleText.Text = form.IsNew ? $"A new {form.Material}: its name and dispersion" : $"Dispersion of {form.Material}";
        NamePanel.IsVisible = form.IsNew;
        LibraryBox.ItemsSource = form.Library.Select(m => $"{m.Name}  ({m.Model.Kind.ToLowerInvariant()})").ToArray();

        ShowFields();

        KindBox.SelectionChanged += (_, _) =>
        {
            if (_loading || KindBox.SelectedItem is not ComboBoxItem { Tag: string kind }) return;
            Read();
            _form.Kind = kind;
            ShowFields();
        };
        LoadButton.Click += (_, _) =>
        {
            if (LibraryBox.SelectedIndex < 0) { Say("Choose a library material to load."); return; }
            var m = form.Library[LibraryBox.SelectedIndex];
            var problem = form.LoadFromLibrary(m.Name);
            ShowFields();   // redraws the preview, which clears the message line - so speak after it
            if (problem is not null) Say(problem);
            else Tell($"Loaded {m.Name}'s {m.Model.Kind.ToLowerInvariant()} coefficients. Press OK to use them.");
        };
        AddRowButton.Click += (_, _) => { Read(); form.Table.Add(new TableRowEntry()); ShowFields(); };
        RemoveRowButton.Click += (_, _) =>
        {
            Read();
            if (TableGrid.SelectedItem is TableRowEntry row) form.Table.Remove(row);
            ShowFields();
        };
        ImportButton.Click += async (_, _) =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                { Title = "Import n, k table", AllowMultiple = false });
            if (files.Count == 0) return;
            await using var stream = await files[0].OpenReadAsync();
            using var reader = new StreamReader(stream);
            string? unit = (ImportUnitBox.SelectedItem as ComboBoxItem)?.Tag as string;
            string said = form.ImportTable(await reader.ReadToEndAsync(), unit, out bool ok);
            ShowFields();
            if (ok) ImportUnitBox.SelectedIndex = form.ImportedUnit == "nm" ? 1 : 0;
            if (ok) Tell(said); else Say(said);
        };

        foreach (var box in _b.Concat(_c).Append(CauchyA).Append(CauchyB).Append(CauchyC)
                              .Append(ConstantN).Append(ConstantK).Append(RangeMinBox).Append(RangeMaxBox))
            box.TextChanged += (_, _) => { if (!_loading) { Read(); ShowPreview(); } };
        TableGrid.CellEditEnded += (_, _) => ShowPreview();

        OkButton.Click += (_, _) =>
        {
            Read();
            if (form.IsNew && string.IsNullOrWhiteSpace(form.NewName = NewNameBox.Text ?? "")) { Say("Give the new material a name."); return; }
            var model = form.ToModel(out var problem);
            if (model is null) { Say($"Cannot use these: {problem}."); return; }
            Close(model);
        };
        CancelButton.Click += (_, _) => Close(null);
    }

    /// <summary>The fields read back into the form.</summary>
    private void Read()
    {
        for (int i = 0; i < CoefficientsForm.SellmeierTerms; ++i)
            (_form.SellmeierB[i], _form.SellmeierC[i]) = (_b[i].Text ?? "", _c[i].Text ?? "");
        (_form.CauchyA, _form.CauchyB, _form.CauchyC) = (CauchyA.Text ?? "", CauchyB.Text ?? "", CauchyC.Text ?? "");
        (_form.ConstantN, _form.ConstantK) = (ConstantN.Text ?? "", ConstantK.Text ?? "");
        (_form.RangeMin, _form.RangeMax, _form.Source) = (RangeMinBox.Text ?? "", RangeMaxBox.Text ?? "", SourceBox.Text ?? "");
    }

    /// <summary>The form shown: the panel for its kind, every field filled, the preview drawn.</summary>
    private void ShowFields()
    {
        _loading = true;
        KindBox.SelectedItem = KindBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == _form.Kind);
        FormulaText.Text = _form.Formula;
        SellmeierPanel.IsVisible = _form.Kind == "SELLMEIER";
        CauchyPanel.IsVisible = _form.Kind == "CAUCHY";
        ConstantPanel.IsVisible = _form.Kind == "CONSTANT";
        TablePanel.IsVisible = _form.Kind == "TABLE";
        for (int i = 0; i < CoefficientsForm.SellmeierTerms; ++i)
            (_b[i].Text, _c[i].Text) = (_form.SellmeierB[i], _form.SellmeierC[i]);
        (CauchyA.Text, CauchyB.Text, CauchyC.Text) = (_form.CauchyA, _form.CauchyB, _form.CauchyC);
        (ConstantN.Text, ConstantK.Text) = (_form.ConstantN, _form.ConstantK);
        TableGrid.ItemsSource = null;
        TableGrid.ItemsSource = _form.Table;
        (RangeMinBox.Text, RangeMaxBox.Text, SourceBox.Text) = (_form.RangeMin, _form.RangeMax, _form.Source);
        _loading = false;
        ShowPreview();
    }

    private void ShowPreview()
    {
        var model = _form.ToModel(out var problem);
        if (model is null)
        {
            Preview.Series = Array.Empty<PlotSeries>();
            PreviewNote.Text = $"No preview: {problem}.";
            return;
        }
        var p = MaterialsForm.Preview(new NamedMaterial(_form.Material, model), out var why, _form.WavelengthUnit);
        Preview.XTitle = $"wavelength ({MeritRow.UnitLabel(_form.WavelengthUnit)})";
        if (p is null) { Preview.Series = Array.Empty<PlotSeries>(); PreviewNote.Text = $"No preview: {why}."; return; }
        var (nm, n, k, span) = p.Value;
        bool absorbing = k.Any(v => v > 0.0);
        Preview.Series = absorbing
            ? [new PlotSeries("n", nm, n, 0xFF1F77B4), new PlotSeries("k", nm, k, 0xFFD62728)]
            : [new PlotSeries("n", nm, n, 0xFF1F77B4)];
        Preview.YTitle = absorbing ? "n (blue), k (red)" : "n";
        PreviewNote.Text = $"{span}{(absorbing ? "" : "; k = 0 throughout")}.";
        ProblemText.Text = "";
    }

    private void Say(string? problem)
    {
        ProblemText.Foreground = Avalonia.Media.Brushes.Firebrick;
        ProblemText.Text = problem ?? "";
    }

    private void Tell(string message)
    {
        ProblemText.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(0x1B, 0x6E, 0x2E));
        ProblemText.Text = message;
    }
}
