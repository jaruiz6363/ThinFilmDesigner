// BoundsDialog.axaml.cs — Stack > Set vary constraints: bounds on a range of layers at once.
//
// Shows a BoundsForm. OK passes the form's request to `apply`, which answers with the reason it
// was refused or null. Wiring only: the reading and checking are the form's and the API's.

using Avalonia.Controls;
using ThinFilm.Api;
using ThinFilm.Gui.Core;

namespace ThinFilm.Gui;

public partial class BoundsDialog : Window
{
    private readonly BoundsForm _form;
    private readonly Func<BoundsRequest, string?> _apply;

    public BoundsDialog() : this(null!, _ => null) { }

    public BoundsDialog(BoundsForm form, Func<BoundsRequest, string?> apply)
    {
        InitializeComponent();
        _form = form;
        _apply = apply;
        if (form is null) return;

        FromBox.Text = form.FromText;
        ToBox.Text = form.ToText;
        MaterialBox.ItemsSource = form.MaterialOptions;
        MaterialBox.SelectedItem = form.Material;
        ConstraintBox.ItemsSource = form.ConstraintOptions;
        ConstraintBox.SelectedItem = form.Constraint;
        UnitBox.ItemsSource = form.UnitOptions;
        UnitBox.SelectedItem = form.Unit;

        FromBox.TextChanged += (_, _) => ShowForm();
        ToBox.TextChanged += (_, _) => ShowForm();
        MaterialBox.SelectionChanged += (_, _) => ShowForm();
        ConstraintBox.SelectionChanged += (_, _) => ShowForm();
        OkButton.Click += (_, _) => OnOk();
        CancelButton.Click += (_, _) => Close(false);
        ShowForm();
    }

    private void Read()
    {
        _form.FromText = FromBox.Text ?? "";
        _form.ToText = ToBox.Text ?? "";
        if (MaterialBox.SelectedItem is string m) _form.Material = m;
        if (ConstraintBox.SelectedItem is string c) _form.Constraint = c;
        if (UnitBox.SelectedItem is string u) _form.Unit = u;
        _form.MinText = MinBox.Text ?? "";
        _form.MaxText = MaxBox.Text ?? "";
    }

    /// <summary>Which boxes apply, and which layers the choices reach.</summary>
    private void ShowForm()
    {
        Read();
        MinBox.IsEnabled = _form.MinApplies;
        MaxBox.IsEnabled = _form.MaxApplies;
        UnitBox.IsEnabled = _form.MinApplies || _form.MaxApplies;
        PreviewText.Text = _form.Preview;
        BoundsNoteText.Text = _form.BoundsNote;
        ProblemText.IsVisible = false;
    }

    private void OnOk()
    {
        Read();
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
