// SystemParametersDialog.axaml.cs — Edit > System Parameters.
//
// The back surface, the substrate thickness and where angles are measured are the design's too,
// and change how it is evaluated (ThinFilm.Api DesignDocument, "System Parameters").
//
// Two kinds of setting, kept apart because they live in different places: the length unit, the
// thickness unit and the reference wavelength are the DESIGN's (saved in its file, so it reopens
// as it was left, and offered only with a design open); the material database is the PERSON's
// (saved in their settings, the same for every design). The reference wavelength is the one
// setting here that changes the design itself: see ThinFilm.Api LayerUnits.cs.
//
// APPLY AND OK, as the user asked (2026-10-16). Both hand the choices to `apply`, which makes them
// and answers with the reason one was refused, or null; Apply leaves the dialog open, OK closes it
// once everything is taken. Cancel closes it and undoes nothing already applied.

using Avalonia.Controls;
using Avalonia.Platform.Storage;
using ThinFilm.Api;
using ThinFilm.Gui.Core;

namespace ThinFilm.Gui;

/// <summary>The design's evaluation settings: R and T with the substrate's back surface, its
/// thickness in mm, and angles of incidence given in air.</summary>
public sealed record SystemSettings(bool BackSurface = false, double SubstrateMm = 1.0, bool AngleInAir = false);

/// <summary>What Apply and OK hand over: the design's length unit, thickness unit and reference
/// (null/0 when no design is open), the database path, and the evaluation settings (null with no
/// design open).</summary>
public sealed record SystemParametersResult(string? Unit, string DatabasePath, string? ThicknessUnit = null,
                                            double ReferenceNm = 0.0, double MaximumNm = 0.0,
                                            SystemSettings? System = null);

public partial class SystemParametersDialog : Window
{
    public SystemParametersDialog() : this("nm", "", "nm", 550.0, 600.0, _ => null) { }

    /// <param name="unit">The design's length unit, or null with no design open.</param>
    /// <param name="maximumNm">The design's maximum for unconstrained layers.</param>
    /// <param name="apply">Makes the choices; returns why one was refused, or null.</param>
    public SystemParametersDialog(string? unit, string databasePath, string thicknessUnit, double referenceNm,
                                  double maximumNm, Func<SystemParametersResult, string?> apply,
                                  SystemSettings? system = null)
    {
        InitializeComponent();
        bool open = unit is not null;
        UnitChoices.IsEnabled = open;
        ThicknessChoices.IsEnabled = open;
        SystemChoices.IsEnabled = open;
        var settings = system ?? new SystemSettings();
        BackSurfaceBox.IsChecked = settings.BackSurface;
        SubstrateBox.Text = settings.SubstrateMm.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        SubstrateBox.IsEnabled = settings.BackSurface;
        BackSurfaceBox.IsCheckedChanged += (_, _) => SubstrateBox.IsEnabled = BackSurfaceBox.IsChecked == true;
        AngleInAirBox.IsChecked = settings.AngleInAir;
        NanometreChoice.IsChecked = unit != "um";
        MicronChoice.IsChecked = unit == "um";
        NmThicknessChoice.IsChecked = thicknessUnit == "nm";
        QwotChoice.IsChecked = thicknessUnit == "QWOT";
        FwotChoice.IsChecked = thicknessUnit == "FWOT";
        DatabaseBox.Text = databasePath;

        // The reference is typed in the length unit chosen above, and follows a change of it; so
        // does the name of the physical thickness choice - "µm" in a µm design, not "nm".
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string Wl() => MicronChoice.IsChecked == true ? "um" : "nm";
        string Show(double nm) => Wl() == "um" ? (nm / 1000.0).ToString("0.#####", inv) : nm.ToString("0.###", inv);
        void ShowLength(double nm, double maxNm)
        {
            ReferenceBox.Text = Show(nm);
            MaximumBox.Text = Show(maxNm);
            ReferenceUnitText.Text = MaximumUnitText.Text = Lengths.Physical(Wl());
            NmThicknessChoice.Content = $"physical ({Lengths.Physical(Wl())})";
        }
        ShowLength(referenceNm, maximumNm);
        MicronChoice.IsCheckedChanged += (_, _) =>
        {
            // The boxes were in the other unit; read them there, show them in this one.
            string was = Wl() == "um" ? "nm" : "um";
            ShowLength(ThicknessPreferences.ReferenceNm(ReferenceBox.Text, was, out _) ?? referenceNm,
                       ThicknessPreferences.MaximumNm(MaximumBox.Text, was, out _) ?? maximumNm);
        };

        BrowseButton.Click += async (_, _) =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Material database",
                AllowMultiple = false,
                // Where the person's database and the literature's copy are (MaterialDatabase).
                SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(ThinFilmHome.Directory),
            });
            if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) DatabaseBox.Text = path;
        };

        // Read the boxes and hand them to apply; true when everything was taken.
        bool Apply()
        {
            double reference = 0.0, maximum = 0.0, substrate = 1.0;
            if (open)
            {
                if (ThicknessPreferences.ReferenceNm(ReferenceBox.Text, Wl(), out var problem) is not { } nm)
                {
                    Say(problem!, refused: true);
                    return false;
                }
                if (ThicknessPreferences.MaximumNm(MaximumBox.Text, Wl(), out problem) is not { } max)
                {
                    Say(problem!, refused: true);
                    return false;
                }
                reference = nm;
                maximum = max;
                // The thickness is checked only when it is used; otherwise an unreadable box keeps
                // the design's value.
                bool read = double.TryParse(SubstrateBox.Text, System.Globalization.NumberStyles.Float,
                                            System.Globalization.CultureInfo.InvariantCulture, out substrate)
                            && substrate > 0.0;
                if (!read) substrate = settings.SubstrateMm;
                if (!read && BackSurfaceBox.IsChecked == true)
                {
                    Say("the substrate thickness must be a positive number of millimetres", refused: true);
                    return false;
                }
            }
            var refused = apply(new SystemParametersResult(
                open ? Wl() : null,
                (DatabaseBox.Text ?? "").Trim(),
                open ? QwotChoice.IsChecked == true ? "QWOT" : FwotChoice.IsChecked == true ? "FWOT" : "nm" : null,
                reference, maximum,
                open ? new SystemSettings(BackSurfaceBox.IsChecked == true, substrate, AngleInAirBox.IsChecked == true) : null));
            if (refused is not null) { Say(refused, refused: true); return false; }
            Say("Applied to the design.", refused: false);
            return true;
        }
        ApplyButton.Click += (_, _) => Apply();
        OkButton.Click += (_, _) => { if (Apply()) Close(true); };
        CancelButton.Click += (_, _) => Close(false);
    }

    private void Say(string text, bool refused)
    {
        ProblemText.Text = char.ToUpperInvariant(text[0]) + text[1..].TrimEnd('.') + ".";
        ProblemText.Foreground = refused ? Avalonia.Media.Brush.Parse("#B00020") : Avalonia.Media.Brush.Parse("#2E7D32");
        ProblemText.IsVisible = true;
    }
}
