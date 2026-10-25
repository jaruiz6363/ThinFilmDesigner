// MeritRow.cs — one editable row of the merit-function table.
//
// FIXED COLUMNS, AFTER LensHH-LT. Every row has the same columns, and whether a cell means
// anything depends on the row's code and mode: a MINT row has no wavelength, a pointwise RA row
// has no second wavelength, only a Range row has a second target. Where a cell does not apply its
// text is blank and it cannot be edited, which the view reads from the Can* properties. The
// alternative - a column set that changes with the selected row - is a table whose columns move
// under the cursor.
//
// A CELL EDIT IS A WHOLE-ROW EDIT. Each setter builds the row's new OperandEdit and hands it to
// the workspace, which sends it to the API and rebuilds the table from the answer. Nothing here
// keeps a value the engine has not accepted, so a refused edit leaves the cell showing what the
// design really holds, with the reason in the status line.
//
// WAVELENGTHS ARE NANOMETRES EVERYWHERE BUT HERE. The row shows and parses them in the design's
// display unit, nm or µm, and converts at the edge; the API, the document and the engine never
// see a micron.

using System.Globalization;
using ThinFilm.Api;

namespace ThinFilm.Gui.Core;

public sealed class MeritRow
{
    private readonly DesignWorkspace _owner;
    private readonly OperandEdit _edit;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    internal MeritRow(DesignWorkspace owner, OperandRow report, OperandEdit edit, string unit)
    {
        _owner = owner;
        Report = report;
        _edit = edit;
        Unit = unit;
    }

    /// <summary>What the engine reported for this row: value, residual, where, and so on.</summary>
    public OperandRow Report { get; }
    public string Unit { get; }

    public int Index => Report.Index;
    public int Number => Report.Index + 1;
    public string Help => Report.Help;

    // ---- what kind of row this is --------------------------------------------------------------

    public bool IsStack => _edit.Wavelengths.Length == 0;
    public bool IsSpan => _edit.Wavelengths.Length > 1;
    private bool IsLimited => Code is "MINT" or "MAXT";
    private bool IsRange => Mode == "Range";

    public bool CanWavelength => !IsStack;
    public bool CanSpan => IsSpan;
    public bool CanAngle => !IsStack;
    public bool CanTarget2 => IsRange;
    public bool CanLimit => IsLimited;
    public bool CanTemperature => Codes.IsThermal(Code);

    // ---- the drop-downs ----------------------------------------------------------------------

    /// <summary>The codes a row can be given, plus the row's own if it is something else.</summary>
    public IReadOnlyList<string> CodeOptions =>
        CommonCodes.Contains(Code) ? CommonCodes : [.. CommonCodes, Code];

    public static readonly string[] CommonCodes =
        ["RA", "RS", "RP", "TA", "TS", "TP", "AA", "AS", "AP",
         "R_GD_S", "R_GD_P", "R_GDD_S", "R_GDD_P", "R_TOD_S", "R_TOD_P",
         "T_GD_S", "T_GD_P", "T_GDD_S", "T_GDD_P", "T_TOD_S", "T_TOD_P",
         "ASOL", "RSOL", "TSOL", "EMIT", "AERATIO",
         "MINT", "MAXT", "TOTT"];

    public string Code
    {
        get => _edit.Code.ToUpperInvariant();
        set
        {
            if (value is null || value == Code) return;
            // What a new code brings with it (a new budget; no wavelength for a stack code; a
            // limit for MINT and MAXT) is decided in the API, where the CLI and the MCP find it.
            Change(new OperandChange(Code: value));
        }
    }

    /// <summary>Target = equal to, Min = at least, Max = at most, Range = between, Report = shown,
    /// not scored.</summary>
    public static readonly string[] ModeOptions = ["Target", "Min", "Max", "Range", "Report"];
    public IReadOnlyList<string> ModeList => ModeOptions;

    public string Mode
    {
        get => _edit.Comparison.ToUpperInvariant() switch
        {
            "EQUAL" or "EQ" => "Target", "GE" => "Min", "LE" => "Max", "RANGE" => "Range",
            _ => "Report",
        };
        set
        {
            if (value is null || value == Mode) return;
            // So is a new mode a new budget: at most 0.005 can fall 0.005; at least 0.005 can rise 0.995.
            Change(new OperandChange(Mode: value));
        }
    }

    private void Change(OperandChange change)
    {
        var next = MeritTable.Change(_edit, change, _owner.DefaultWavelength, out var problem);
        if (problem is null) _owner.EditOperand(Index, next);
    }

    // ---- the cells ---------------------------------------------------------------------------

    public string WavelengthText
    {
        get => CanWavelength ? Show(_edit.Wavelengths.Min()) : "";
        set
        {
            if (!CanWavelength || !TryWavelength(value, out double nm)) return;
            var wls = IsSpan ? Spread(nm, _edit.Wavelengths.Max(), _edit.Wavelengths.Length) : [nm];
            _owner.EditOperand(Index, _edit with { Wavelengths = wls });
        }
    }

    public string Wavelength2Text
    {
        get => CanSpan ? Show(_edit.Wavelengths.Max()) : "";
        set
        {
            if (!CanSpan || !TryWavelength(value, out double nm)) return;
            _owner.EditOperand(Index, _edit with
                { Wavelengths = Spread(_edit.Wavelengths.Min(), nm, _edit.Wavelengths.Length) });
        }
    }

    public string PointsText
    {
        get => CanSpan ? _edit.Wavelengths.Length.ToString(Inv) : "";
        set
        {
            if (!CanSpan || !int.TryParse(value, NumberStyles.Integer, Inv, out int n) || n < 2) return;
            _owner.EditOperand(Index, _edit with
                { Wavelengths = Spread(_edit.Wavelengths.Min(), _edit.Wavelengths.Max(), n) });
        }
    }

    public string AngleText
    {
        get => CanAngle ? _edit.AngleDegrees.ToString("0.##", Inv) : "";
        set
        {
            if (!CanAngle || !TryNumber(value, out double a)) return;
            _owner.EditOperand(Index, _edit with { AngleDegrees = a });
        }
    }

    public string TargetText
    {
        get => _edit.Target.ToString("G6", Inv);
        set { if (TryNumber(value, out double t)) _owner.EditOperand(Index, _edit with { Target = t }); }
    }

    public string Target2Text
    {
        get => CanTarget2 ? _edit.TargetHi.ToString("G6", Inv) : "";
        set
        {
            if (!CanTarget2 || !TryNumber(value, out double t)) return;
            _owner.EditOperand(Index, _edit with { TargetHi = t });
        }
    }

    public string LimitText
    {
        // A layer limit is a physical thickness, so it is shown and typed in the length unit too.
        get => CanLimit ? Lengths.Shown(_edit.Threshold, Unit).ToString(Unit == "um" ? "0.######" : "0.###", Inv) : "";
        set
        {
            if (!CanLimit || !TryNumber(value, out double t)) return;
            _owner.EditOperand(Index, _edit with { Threshold = Lengths.ToNm(t, Unit), Scale = 0.0 });
        }
    }

    /// <summary>The blackbody's temperature, K, for EMIT and AERATIO.</summary>
    public string TemperatureText
    {
        get => CanTemperature ? _edit.Temperature.ToString("0.##", Inv) : "";
        set
        {
            if (!CanTemperature || !TryNumber(value, out double k)) return;
            Change(new OperandChange(Temperature: k));
        }
    }

    /// <summary>Always shown and always editable: how much this row counts.</summary>
    public string WeightText
    {
        get => _edit.Weight.ToString("G6", Inv);
        set { if (TryNumber(value, out double w)) _owner.EditOperand(Index, _edit with { Weight = w }); }
    }

    /// <summary>The engine's value, or a dash when the design is blocked and nothing was evaluated.</summary>
    public string ValueText => double.IsNaN(Report.Value) ? "—" : Report.Value.ToString("G6", Inv);

    // ---- units and parsing -------------------------------------------------------------------

    public static string UnitLabel(string unit) => unit == "um" ? "µm" : "nm";

    /// <summary>A wavelength held in nm, written in the design's unit with its label: "550 nm",
    /// "0.55 µm". Every dialog that names a wavelength goes through this, so none is left in nm
    /// when the person has chosen µm.</summary>
    public static string ShowWavelength(double nm, string unit) =>
        unit == "um" ? (nm / 1000.0).ToString("0.#####", Inv) + " µm" : nm.ToString("0.###", Inv) + " nm";

    /// <summary>A range held in µm (as material definitions hold it), in the design's unit:
    /// "0.21–3.71 µm" or "210–3710 nm".</summary>
    public static string ShowMicronRange(double? lo, double? hi, string unit) =>
        unit == "um" ? string.Format(Inv, "{0:0.##}–{1:0.##} µm", lo, hi)
                     : string.Format(Inv, "{0:0.#}–{1:0.#} nm", lo * 1000.0, hi * 1000.0);

    private string Show(double nm) =>
        Unit == "um" ? (nm / 1000.0).ToString("0.#####", Inv) : nm.ToString("0.###", Inv);

    private bool TryWavelength(string? text, out double nm)
    {
        nm = 0;
        if (!TryNumber(text, out double v) || !(v > 0)) return false;
        nm = Unit == "um" ? v * 1000.0 : v;
        return true;
    }

    private static bool TryNumber(string? text, out double v) =>
        double.TryParse(text, NumberStyles.Float, Inv, out v) && double.IsFinite(v);

    /// <summary>n points evenly from a to b, both ends included.</summary>
    private static double[] Spread(double a, double b, int n)
    {
        var w = new double[n];
        for (int i = 0; i < n; ++i) w[i] = a + (b - a) * i / (n - 1.0);
        return w;
    }
}
