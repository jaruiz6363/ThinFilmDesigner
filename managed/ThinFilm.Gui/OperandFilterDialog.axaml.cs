// OperandFilterDialog.axaml.cs — Delete Operands and Adjust Weight.
//
// Wiring only: the boxes are copied into an OperandFilterForm (Gui.Core, tested), and the count is
// asked of the workspace, which asks the API - the same match OK will act on. ShowDialog returns
// true on OK, when the caller reads the form; false on Cancel.

using Avalonia.Controls;
using ThinFilm.Api;
using ThinFilm.Gui.Core;

namespace ThinFilm.Gui;

public partial class OperandFilterDialog : Window
{
    private readonly OperandFilterForm _form;
    private readonly Func<OperandFilter, int?> _count;
    private readonly bool _weight;

    public OperandFilterDialog() : this(new OperandFilterForm("nm"), _ => 0, weight: false) { }

    /// <param name="count">How many rows a filter matches (DesignWorkspace.MatchCount).</param>
    /// <param name="weight">Adjust Weight rather than Delete Operands.</param>
    public OperandFilterDialog(OperandFilterForm form, Func<OperandFilter, int?> count, bool weight)
    {
        InitializeComponent();
        _form = form;
        _count = count;
        _weight = weight;

        Title = weight ? "Adjust Weight" : "Delete Operands";
        OkButton.Content = weight ? "Set weight" : "Delete";
        IntroText.Text = weight
            ? "Give the matching rows a new weight. Leave a box blank, or a choice at (any), to not filter on it."
            : "Delete the matching rows. Leave a box blank, or a choice at (any), to not filter on it.";
        WeightLabel.IsVisible = WeightBox.IsVisible = weight;
        WavelengthLabel.Text = $"WV1 to WV2 ({MeritRow.UnitLabel(form.Unit)})";

        Wavelength1Box.Text = form.Wavelength1;
        Wavelength2Box.Text = form.Wavelength2;
        CodeBox.ItemsSource = MeritChoices.FilterCodes;
        CodeBox.SelectedItem = form.CodeChoice;
        ModeBox.ItemsSource = MeritChoices.FilterModes;
        ModeBox.SelectedItem = form.ModeChoice;
        AngleTestBox.ItemsSource = MeritChoices.AngleTests;
        AngleTestBox.SelectedItem = form.AngleTest;
        Angle1Box.Text = form.Angle1;
        Angle2Box.Text = form.Angle2;
        WeightBox.Text = form.Weight;

        foreach (var box in new[] { Wavelength1Box, Wavelength2Box, Angle1Box, Angle2Box, WeightBox })
            box.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) Recount(); };
        foreach (var combo in new[] { CodeBox, ModeBox, AngleTestBox })
            combo.SelectionChanged += (_, _) => Recount();

        OkButton.Click += (_, _) =>
        {
            Read();
            var filter = _form.ToFilter(out var problem);
            if (filter is not null && _weight) _form.ToWeight(out problem);
            if (problem is not null) { ProblemText.Text = problem; return; }
            if (_count(filter!) == 0) { ProblemText.Text = "No row matches; nothing to do."; return; }
            Close(true);
        };
        CancelButton.Click += (_, _) => Close(false);
        Recount();
    }

    private void Read()
    {
        _form.Wavelength1 = Wavelength1Box.Text ?? "";
        _form.Wavelength2 = Wavelength2Box.Text ?? "";
        _form.CodeChoice = CodeBox.SelectedItem as string ?? MeritChoices.Any;
        _form.ModeChoice = ModeBox.SelectedItem as string ?? MeritChoices.Any;
        _form.AngleTest = AngleTestBox.SelectedItem as string ?? MeritChoices.Any;
        _form.Angle1 = Angle1Box.Text ?? "";
        _form.Angle2 = Angle2Box.Text ?? "";
        _form.Weight = WeightBox.Text ?? "";
    }

    /// <summary>The rows OK would act on, said as the boxes change.</summary>
    private void Recount()
    {
        Read();
        bool test = _form.AngleTest != MeritChoices.Any;
        Angle1Box.IsEnabled = test;
        Angle2Box.IsEnabled = _form.AngleTest == "AOI between";

        var filter = _form.ToFilter(out var problem);
        ProblemText.Text = problem ?? "";
        int? n = filter is null ? null : _count(filter);
        CountText.Text = n is null ? "" : n == 1 ? "1 row matches." : $"{n} rows match.";
        OkButton.IsEnabled = n > 0;
    }
}
