// OperandSweepDialog.axaml.cs — Add Operands, Add Operands Multiple AOI and Add Operands Single Wv.
//
// Wiring only: the boxes are copied into an OperandSweepForm (Gui.Core, tested), which says how
// many rows they make or which box is wrong. ShowDialog returns true on OK, when the caller reads
// the form; false on Cancel.

using Avalonia.Controls;
using ThinFilm.Gui.Core;

namespace ThinFilm.Gui;

public partial class OperandSweepDialog : Window
{
    private readonly OperandSweepForm _form;

    public OperandSweepDialog() : this(new OperandSweepForm(SweepKind.Band, "nm")) { }

    public OperandSweepDialog(OperandSweepForm form)
    {
        InitializeComponent();
        _form = form;
        string u = MeritRow.UnitLabel(form.Unit);

        Title = form.Title;
        IntroText.Text = form.Kind switch
        {
            SweepKind.Band => "One row per wavelength from WV1 to WV2, every Step WV, at one AOI.",
            SweepKind.BandManyAngles => "One row per wavelength from WV1 to WV2, every Step WV, at each AOI from Start AOI to End AOI, every Step AOI.",
            _ => "One row at WV1 for each AOI from Start AOI to End AOI, every Step AOI.",
        } + " The rows are added at the end of the table.";
        WavelengthLabel.Text = form.HasBand ? $"WV1, WV2, Step WV ({u})" : $"WV1 ({u})";
        Wavelength2Box.IsVisible = WavelengthStepBox.IsVisible = form.HasBand;
        AngleLabel.Text = form.HasManyAngles ? "Start, End, Step AOI (°)" : "AOI (°)";
        Angle1Box.Watermark = form.HasManyAngles ? "Start AOI" : "AOI";
        Angle2Box.IsVisible = AngleStepBox.IsVisible = form.HasManyAngles;

        CodeBox.ItemsSource = MeritChoices.SweepCodes;
        CodeBox.SelectedItem = form.Code;
        ModeBox.ItemsSource = MeritRow.ModeOptions;
        ModeBox.SelectedItem = form.Mode;
        TargetBox.Text = form.Target;
        Target2Box.Text = form.Target2;
        WeightBox.Text = form.Weight;
        Wavelength1Box.Text = form.Wavelength1;
        Wavelength2Box.Text = form.Wavelength2;
        WavelengthStepBox.Text = form.WavelengthStep;
        Angle1Box.Text = form.Angle1;
        Angle2Box.Text = form.Angle2;
        AngleStepBox.Text = form.AngleStep;

        foreach (var box in new[] { TargetBox, Target2Box, WeightBox, Wavelength1Box, Wavelength2Box,
                                    WavelengthStepBox, Angle1Box, Angle2Box, AngleStepBox })
            box.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) Recount(); };
        CodeBox.SelectionChanged += (_, _) => Recount();
        ModeBox.SelectionChanged += (_, _) => Recount();

        OkButton.Click += (_, _) =>
        {
            Read();
            if (_form.ToSweep(out var problem) is null) { ProblemText.Text = problem; return; }
            Close(true);
        };
        CancelButton.Click += (_, _) => Close(false);
        Recount();
    }

    private void Read()
    {
        _form.Code = CodeBox.SelectedItem as string ?? "RA";
        _form.Mode = ModeBox.SelectedItem as string ?? "Max";
        _form.Target = TargetBox.Text ?? "";
        _form.Target2 = Target2Box.Text ?? "";
        _form.Weight = WeightBox.Text ?? "";
        _form.Wavelength1 = Wavelength1Box.Text ?? "";
        _form.Wavelength2 = Wavelength2Box.Text ?? "";
        _form.WavelengthStep = WavelengthStepBox.Text ?? "";
        _form.Angle1 = Angle1Box.Text ?? "";
        _form.Angle2 = Angle2Box.Text ?? "";
        _form.AngleStep = AngleStepBox.Text ?? "";
    }

    /// <summary>How many rows OK would add, said as the boxes change.</summary>
    private void Recount()
    {
        Read();
        Target2Label.IsVisible = Target2Box.IsVisible = _form.HasTarget2;
        var sweep = _form.ToSweep(out var problem);
        ProblemText.Text = problem ?? "";
        int? n = sweep is null ? null : _form.RowCount();
        CountText.Text = n is null ? "" : n == 1 ? "1 row will be added." : $"{n} rows will be added.";
        OkButton.IsEnabled = n > 0;
    }
}
