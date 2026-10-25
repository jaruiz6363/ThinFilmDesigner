// CoatingImportDialog.axaml.cs — File > Import ZEMAX coating and Import Optiland coating: choose one
// coating of a file. Wiring only: the reading is the API's, tested without a window.

using Avalonia.Controls;
using ThinFilm.Gui.Core;

namespace ThinFilm.Gui;

public partial class CoatingImportDialog : Window
{
    private readonly IReadOnlyList<CoatingChoice> _choices;
    private readonly Func<int, string?> _apply;

    public CoatingImportDialog() : this("Import coating", "", [], "", _ => null) { }

    /// <param name="explanation">What importing does, shown above the buttons.</param>
    /// <param name="apply">Imports the coating at that place in the list; answers with the reason
    /// it was refused, or null.</param>
    public CoatingImportDialog(string title, string fileName, IReadOnlyList<CoatingChoice> choices, string explanation,
                               Func<int, string?> apply)
    {
        InitializeComponent();
        Title = title;
        _choices = choices;
        _apply = apply;

        FileText.Text = $"{fileName}: {choices.Count} coating{(choices.Count == 1 ? "" : "s")}";
        ReplaceText.Text = explanation;
        int width = choices.Count == 0 ? 10 : Math.Min(32, choices.Max(c => c.Name.Length));
        CoatingList.ItemsSource = choices.Select(c =>
            $"{c.Name.PadRight(width)}  {c.Layers,4} layer{(c.Layers == 1 ? " " : "s")}"
            + (c.Detail is { } d ? $"  {d}" : "")
            + (c.Problem is { } p ? $"\n  cannot import: {p}" : "")).ToArray();
        if (choices.Count == 1) CoatingList.SelectedIndex = 0;
        CoatingList.DoubleTapped += (_, _) => OnOk();
        CoatingList.SelectionChanged += (_, _) => ProblemText.IsVisible = false;
        OkButton.Click += (_, _) => OnOk();
        CancelButton.Click += (_, _) => Close(false);
    }

    private void OnOk()
    {
        int i = CoatingList.SelectedIndex;
        string? problem = i < 0 ? "Choose a coating." : _choices[i].Problem is { } p ? $"{_choices[i].Name} {p}" : null;
        problem ??= _apply(i);
        if (problem is not null)
        {
            ProblemText.Text = char.ToUpperInvariant(problem[0]) + problem[1..].TrimEnd('.') + ".";
            ProblemText.IsVisible = true;
            return;
        }
        Close(true);
    }
}
