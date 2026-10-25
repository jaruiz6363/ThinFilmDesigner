// MeritForms.cs — the Merit Function tab's Delete operands, Adjust weight and Add operands
// dialogs, with no window in them.
//
// TEXT IN, AN API REQUEST OUT. Each form holds what the dialog's boxes say, in the design's
// wavelength unit, and turns it into an OperandFilter or an OperandSweep in nanometres - or into a
// sentence saying which box is wrong. The work itself is ThinFilm.Api's (MeritTable.cs), which the
// CLI and the MCP server call too.
//
// A BLANK BOX IS NO FILTER, a blank code or mode choice is "(any)", and an AOI test of "(any)"
// tests nothing. A blank wavelength on one side is open on that side.

using System.Globalization;
using ThinFilm.Api;

namespace ThinFilm.Gui.Core;

public static class MeritChoices
{
    public const string Any = "(any)";

    /// <summary>"(any)", the three families, then each code - for a filter.</summary>
    public static readonly string[] FilterCodes =
        [Any, "R (RA, RS, RP)", "T (TA, TS, TP)", "A (AA, AS, AP)", .. MeritRow.CommonCodes];

    /// <summary>The codes a sweep can add: the optical ones. MINT, MAXT and TOTT describe the
    /// stack and are added one at a time with Insert. A band code makes one row over the whole
    /// range, at 373.15 K for EMIT and AERATIO until its Temp (K) cell is changed.</summary>
    public static readonly string[] SweepCodes =
        ["RA", "RS", "RP", "TA", "TS", "TP", "AA", "AS", "AP", "ASOL", "RSOL", "TSOL", "EMIT", "AERATIO"];

    public static readonly string[] FilterModes = [Any, .. MeritRow.ModeOptions];

    /// <summary>The AOI tests, as the dialog words them.</summary>
    public static readonly string[] AngleTests = [Any, "AOI >", "AOI <", "AOI between"];

    /// <summary>"R (RA, RS, RP)" → "R"; "(any)" or blank → null; a code → itself.</summary>
    internal static string? Code(string? choice)
    {
        if (string.IsNullOrWhiteSpace(choice) || choice == Any) return null;
        int space = choice.IndexOf(' ');
        return space > 0 ? choice[..space] : choice.Trim();
    }

    internal static bool Number(string? text, out double v) =>
        double.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && double.IsFinite(v);
}

/// <summary>Delete operands and Adjust weight: which rows, and for Adjust weight, the new weight.</summary>
public sealed class OperandFilterForm
{
    public OperandFilterForm(string unit) { Unit = unit; }

    /// <summary>"nm" or "um": what the wavelength boxes are typed in.</summary>
    public string Unit { get; }
    public string Wavelength1 { get; set; } = "";
    public string Wavelength2 { get; set; } = "";
    public string CodeChoice { get; set; } = MeritChoices.Any;
    public string ModeChoice { get; set; } = MeritChoices.Any;
    public string AngleTest { get; set; } = MeritChoices.Any;
    public string Angle1 { get; set; } = "";
    public string Angle2 { get; set; } = "";
    public string Weight { get; set; } = "1";

    private double Factor => Unit == "um" ? 1000.0 : 1.0;

    /// <summary>The boxes as a filter, in nanometres, or null with the reason.</summary>
    public OperandFilter? ToFilter(out string? problem)
    {
        string? bad = null;
        double? Wl(string text, string which)
        {
            if (text.Trim().Length == 0) return null;
            if (MeritChoices.Number(text, out double v) && v > 0) return v * Factor;
            bad ??= $"{which} must be a positive number, or blank";
            return null;
        }
        var w1 = Wl(Wavelength1, "WV1");
        var w2 = Wl(Wavelength2, "WV2");
        problem = bad;
        if (problem is not null) return null;

        AngleTest? angle = null;
        if (AngleTest != MeritChoices.Any)
        {
            if (!MeritChoices.Number(Angle1, out double a)) { problem = "the AOI test needs an angle in degrees"; return null; }
            double b = 0.0;
            if (AngleTest == "AOI between" && !MeritChoices.Number(Angle2, out b))
            { problem = "AOI between needs a second angle"; return null; }
            angle = new AngleTest(AngleTest switch { "AOI >" => "above", "AOI <" => "below", _ => "between" }, a, b);
        }

        var code = MeritChoices.Code(CodeChoice);
        var mode = ModeChoice == MeritChoices.Any ? null : ModeChoice;
        var filter = new OperandFilter(w1, w2, code is null ? null : [code], mode is null ? null : [mode], angle);
        problem = MeritTable.Problem(filter);
        return problem is null ? filter : null;
    }

    public double? ToWeight(out string? problem)
    {
        problem = null;
        if (MeritChoices.Number(Weight, out double w) && w >= 0) return w;
        problem = "the new weight must be zero or a positive number";
        return null;
    }
}

/// <summary>Which of the three Add dialogs: a band at one AOI, a band at several, or one
/// wavelength at several.</summary>
public enum SweepKind { Band, BandManyAngles, OneWavelengthManyAngles }

public sealed class OperandSweepForm
{
    public OperandSweepForm(SweepKind kind, string unit) { Kind = kind; Unit = unit; }

    public SweepKind Kind { get; }
    public string Unit { get; }

    public string Code { get; set; } = "RA";
    public string Mode { get; set; } = "Max";
    public string Target { get; set; } = "0.005";
    public string Target2 { get; set; } = "";
    public string Weight { get; set; } = "1";
    public string Wavelength1 { get; set; } = "";
    public string Wavelength2 { get; set; } = "";
    public string WavelengthStep { get; set; } = "";
    /// <summary>The one AOI of a Band; the first of a many-angle sweep.</summary>
    public string Angle1 { get; set; } = "0";
    public string Angle2 { get; set; } = "";
    public string AngleStep { get; set; } = "";

    public bool HasBand => Kind != SweepKind.OneWavelengthManyAngles;
    public bool HasManyAngles => Kind != SweepKind.Band;
    public bool HasTarget2 => Mode == "Range";

    public string Title => Kind switch
    {
        SweepKind.Band => "Add Operands",
        SweepKind.BandManyAngles => "Add Operands Multiple AOI",
        _ => "Add Operands Single Wv",
    };

    private double Factor => Unit == "um" ? 1000.0 : 1.0;

    public OperandSweep? ToSweep(out string? problem)
    {
        string? bad = null;
        double Need(string text, string which, bool positive = false)
        {
            if (bad is not null) return 0;
            if (MeritChoices.Number(text, out double v) && (!positive || v > 0)) return v;
            bad = positive ? $"{which} must be a positive number" : $"{which} must be a number";
            return 0;
        }

        double target = Need(Target, "Target");
        double target2 = HasTarget2 ? Need(Target2, "Target 2") : 0.0;
        double weight = Need(Weight, "Weight");
        if (bad is null && weight < 0) bad = "Weight must be zero or positive";
        double w1 = Need(Wavelength1, "WV1", positive: true) * Factor;
        double? w2 = HasBand ? Need(Wavelength2, "WV2", positive: true) * Factor : null;
        double? ws = HasBand ? Need(WavelengthStep, "Step WV", positive: true) * Factor : null;
        double a1 = Need(Angle1, HasManyAngles ? "Start AOI" : "AOI");
        double? a2 = HasManyAngles ? Need(Angle2, "End AOI") : null;
        double? astep = HasManyAngles ? Need(AngleStep, "Step AOI", positive: true) : null;
        problem = bad;
        if (problem is not null) return null;

        var sweep = new OperandSweep(Code, Mode, target, weight, w1, w2, ws, a1, a2, astep, target2);
        MeritTable.Rows(sweep, out problem);
        return problem is null ? sweep : null;
    }

    /// <summary>How many rows the boxes would add, or null when they do not yet make a sweep.</summary>
    public int? RowCount()
    {
        var s = ToSweep(out _);
        return s is null ? null : MeritTable.Rows(s, out _).Length;
    }
}
