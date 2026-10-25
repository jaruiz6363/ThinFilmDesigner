// DesignStringDialog.axaml.cs — Input design string: the whole stack from text like (1H 1L)12.
//
// Shows a DesignStringForm. The letters' material boxes are rebuilt as the string is typed; OK
// passes the form's request to `apply`, which answers with the reason it was refused or null.
// Wiring only: the reading and the checking are the form's and the API's, tested without a window.

using Avalonia.Controls;
using Avalonia.Layout;
using ThinFilm.Api;
using ThinFilm.Gui.Core;

namespace ThinFilm.Gui;

public partial class DesignStringDialog : Window
{
    private readonly DesignStringForm _form;
    private readonly Func<DesignStringRequest, string?> _apply;
    private char[]? _shownLetters;

    public DesignStringDialog() : this(null!, "", _ => null) { }

    /// <param name="referenceText">The design's reference wavelength as shown, "550 nm".</param>
    public DesignStringDialog(DesignStringForm form, string referenceText, Func<DesignStringRequest, string?> apply)
    {
        InitializeComponent();
        _form = form;
        _apply = apply;
        if (form is null) return;

        StringBox.Text = form.Text;
        QwotChoice.IsChecked = form.Unit == LayerUnits.Qwot;
        FwotChoice.IsChecked = form.Unit == LayerUnits.Fwot;
        IncidentChoice.IsChecked = !form.FirstNextToSubstrate;
        SubstrateChoice.IsChecked = form.FirstNextToSubstrate;
        PickupsBox.IsChecked = form.Pickups;
        ReferenceText.Text = $"Counted at the design's reference wavelength, {referenceText}, in each layer's own "
                             + "index there (Edit > System Parameters sets the reference).";
        ReplaceText.Text = form.ReplaceNote;

        StringBox.TextChanged += (_, _) => { _form.Text = StringBox.Text ?? ""; ProblemText.IsVisible = false; ShowForm(); };
        OkButton.Click += (_, _) => OnOk();
        CancelButton.Click += (_, _) => Close(false);
        Opened += (_, _) => StringBox.Focus();
        ShowForm();
    }

    /// <summary>The preview, and a material box per letter - rebuilt only when the letters change,
    /// so a box being used is not replaced under the pointer.</summary>
    private void ShowForm()
    {
        _form.Update();
        PreviewText.Text = _form.Preview;
        PreviewText.Foreground = Avalonia.Media.Brush.Parse(_form.Parses ? "#2E7D32" : "#B00020");
        var letters = _form.Letters.Select(l => l.Letter).ToArray();
        if (_shownLetters is not null && letters.SequenceEqual(_shownLetters)) return;
        _shownLetters = letters;
        LettersPanel.Children.Clear();
        foreach (var choice in _form.Letters)
        {
            var box = new ComboBox { ItemsSource = choice.MaterialOptions, SelectedItem = choice.Material, Width = 220 };
            box.SelectionChanged += (_, _) => choice.Material = box.SelectedItem as string;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new TextBlock { Text = choice.Label, Width = 40, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(box);
            LettersPanel.Children.Add(row);
        }
        if (_form.Letters.Count == 0)
            LettersPanel.Children.Add(new TextBlock { Text = "Each letter in the string gets a material here.", FontSize = 11 });
    }

    private void OnOk()
    {
        _form.Text = StringBox.Text ?? "";
        _form.Unit = FwotChoice.IsChecked == true ? LayerUnits.Fwot : LayerUnits.Qwot;
        _form.FirstNextToSubstrate = SubstrateChoice.IsChecked == true;
        _form.Pickups = PickupsBox.IsChecked == true;

        var request = _form.ToRequest(out var problem);
        if (request is not null) problem = _apply(request);
        if (problem is not null)
        {
            ProblemText.Text = char.ToUpperInvariant(problem[0]) + problem[1..].TrimEnd('.') + ".";
            ProblemText.IsVisible = true;
            return;
        }
        Close(true);
    }
}
