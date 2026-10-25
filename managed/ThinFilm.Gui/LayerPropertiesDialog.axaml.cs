// LayerPropertiesDialog.axaml.cs — one layer's material, thickness and unit, vary flag and pickup.
//
// Shows a LayerPropertiesForm. OK passes the form's properties to `apply`, which answers with the
// reason they were refused or null; the dialog closes with true only once they are taken. Wiring
// only: the conversions and the checking are the form's, and the form is tested in Gui.Core.

using Avalonia.Controls;
using ThinFilm.Api;
using ThinFilm.Gui.Core;

namespace ThinFilm.Gui;

public partial class LayerPropertiesDialog : Window
{
    private readonly LayerPropertiesForm _form;
    private readonly Func<LayerProperties, string?> _apply;
    private bool _loading = true;

    public LayerPropertiesDialog() : this(null!, _ => null) { }

    public LayerPropertiesDialog(LayerPropertiesForm form, Func<LayerProperties, string?> apply)
    {
        InitializeComponent();
        _form = form;
        _apply = apply;
        if (form is null) return;

        HeadingText.Text = $"Layer {form.LayerNumber} of {form.LayerCount}";
        MaterialBox.ItemsSource = form.MaterialOptions;
        MaterialBox.SelectedItem = form.Material;
        UnitBox.ItemsSource = form.UnitOptions;
        UnitBox.SelectedItem = form.UnitChoice;
        ThicknessBox.Text = form.ThicknessText;
        VaryBox.IsChecked = form.Vary;
        PickupBox.IsChecked = form.HasPickup;
        ParentBox.Text = form.PickupParentText;
        ScaleBox.Text = form.PickupScaleText;
        OffsetBox.Text = form.PickupOffsetText;
        ConstraintBox.ItemsSource = Constraints.All;
        ConstraintBox.SelectedItem = form.Constraint;
        MinBox.Text = form.MinText;
        MaxBox.Text = form.MaxText;
        UnitNoteText.Text = $"QWOT and FWOT are counted at the reference wavelength, {form.ReferenceText} "
                            + "(Edit > System Parameters), in this layer's own index there. A new unit keeps the thickness and "
                            + "its bounds; a new material keeps the numbers typed.";

        MaterialBox.SelectionChanged += (_, _) =>
        {
            if (_loading || MaterialBox.SelectedItem is not string m) return;
            _form.Material = m;
            ShowForm();
        };
        UnitBox.SelectionChanged += (_, _) =>
        {
            if (_loading || UnitBox.SelectedItem is not string u) return;
            _form.ThicknessText = ThicknessBox.Text ?? "";
            _form.MinText = MinBox.Text ?? "";
            _form.MaxText = MaxBox.Text ?? "";
            _form.ChooseUnit(u);
            ThicknessBox.Text = _form.ThicknessText;
            MinBox.Text = _form.MinText;
            MaxBox.Text = _form.MaxText;
            ShowForm();
        };
        ThicknessBox.TextChanged += (_, _) =>
        {
            if (_loading) return;
            _form.ThicknessText = ThicknessBox.Text ?? "";
            ShowForm();
        };
        ConstraintBox.SelectionChanged += (_, _) =>
        {
            if (_loading || ConstraintBox.SelectedItem is not string c) return;
            _form.Constraint = c;
            ShowForm();
        };
        PickupBox.IsCheckedChanged += (_, _) =>
        {
            if (_loading) return;
            _form.HasPickup = PickupBox.IsChecked == true;
            ShowForm();
        };

        OkButton.Click += (_, _) => OnOk();
        CancelButton.Click += (_, _) => Close(false);
        _loading = false;
        ShowForm();
    }

    /// <summary>What depends on other boxes: the nanometres, and which boxes apply.</summary>
    private void ShowForm()
    {
        NmText.Text = _form.NmPreview;
        ThicknessBox.IsEnabled = !_form.HasPickup;
        VaryBox.IsEnabled = !_form.HasPickup;
        PickupFields.IsEnabled = _form.HasPickup;
        // A picked-up layer follows its parent, so its own bounds are not used.
        BoundsFields.IsEnabled = !_form.HasPickup;
        MinBox.IsEnabled = _form.MinApplies;
        MaxBox.IsEnabled = _form.MaxApplies;
        BoundsUnitText.Text = _form.UnitLabel;
        BoundsNoteText.Text = _form.BoundsNote;
        PickupNoteText.Text = _form.PickupNote;
    }

    private void OnOk()
    {
        _form.ThicknessText = ThicknessBox.Text ?? "";
        _form.Vary = VaryBox.IsChecked == true;
        _form.MinText = MinBox.Text ?? "";
        _form.MaxText = MaxBox.Text ?? "";
        _form.HasPickup = PickupBox.IsChecked == true;
        _form.PickupParentText = ParentBox.Text ?? "";
        _form.PickupScaleText = ScaleBox.Text ?? "";
        _form.PickupOffsetText = OffsetBox.Text ?? "";

        var properties = _form.ToProperties(out var problem);
        if (properties is not null) problem = _apply(properties);
        if (problem is not null)
        {
            ProblemText.Text = char.ToUpperInvariant(problem[0]) + problem[1..] + ".";
            ProblemText.IsVisible = true;
            return;
        }
        Close(true);
    }
}
