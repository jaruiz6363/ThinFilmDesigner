// MeritTable.cs — building a merit function quickly: clear, delete, reweight and add by the row.
//
// ONE ROUTINE, THREE FRONT ENDS. The Merit Function tab's Clear / Delete operands / Adjust weight /
// Add operands buttons, the CLI's `thinfilm merit ...` and the MCP server's operand tools all call
// ThinFilmApi, which calls what is here. A filter that means one thing in the GUI and another on
// the command line would be two merit functions built from the same words.
//
// THIS IS DOCUMENT EDITING, NOT PHYSICS, which is why it lives here and not in the native engine.
// The engine evaluates a merit table it is handed and allocates nothing; which rows the table holds
// is the managed side's business, exactly as the wavelength grid is (Documents.WithOperands).
//
// A BLANK FILTER FIELD IS NO FILTER. Codes null or empty: every code. Modes null or empty: every
// mode. No wavelength bounds: every row, including stack rows (MINT, TOTT) that read no
// wavelength; with bounds, only rows whose every wavelength lies inside them, so a stack row is
// never caught by a wavelength range. An angle test likewise only ever matches rows that have an
// angle.

using System.Globalization;

namespace ThinFilm.Api;

/// <summary>"above" (AOI &gt; A), "below" (AOI &lt; A) or "between" (A ≤ AOI ≤ B), in degrees.</summary>
public sealed record AngleTest(string Kind, double A, double B = 0.0);

/// <summary>Which merit rows an edit applies to. Every field blank (null) matches every row.</summary>
public sealed record OperandFilter(
    double? WavelengthFrom = null, double? WavelengthTo = null,
    string[]? Codes = null, string[]? Modes = null, AngleTest? Angle = null);

/// <summary>
/// Rows to add: one per (angle, wavelength), each a pointwise row of the same code, mode, target
/// and weight. Wavelengths run From to To in Step nm (To and Step null: From alone); angles run
/// Angle to AngleTo in AngleStep degrees (AngleTo and AngleStep null: Angle alone). The Add
/// operands, Add multiple AOI and Add single Wv dialogs are this one record with different fields
/// left blank. A band code (ASOL, EMIT, ...) makes one row per angle over all the wavelengths,
/// since it is an average over them; Temperature is the blackbody's for EMIT and AERATIO.
/// </summary>
public sealed record OperandSweep(
    string Code, string Mode, double Target, double Weight,
    double WavelengthFrom, double? WavelengthTo = null, double? WavelengthStep = null,
    double Angle = 0.0, double? AngleTo = null, double? AngleStep = null,
    double TargetHi = 0.0, double Temperature = 0.0);

/// <summary>What a bulk edit did: how many rows it touched, and the design afterwards.</summary>
public sealed record MeritEditReport(int Rows, DesignSummary Design);

public static class MeritTable
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>A sweep this long is a typing mistake (a step of 0.01 nm), not a merit function.</summary>
    public const int MaxRowsAdded = 5000;

    /// <summary>
    /// A mode as the GUI names it (Target, Min, Max, Range, Report) or as a document stores it
    /// (EQUAL, GE, LE, RANGE, NONE), as the document's word. Either is accepted everywhere, so a
    /// CLI user can type what the table shows.
    /// </summary>
    public static string Comparison(string mode) => mode.Trim().ToUpperInvariant() switch
    {
        "TARGET" or "EQUAL" or "EQ" or "=" => "EQUAL",
        "MIN" or "GE" or ">=" => "GE",
        "MAX" or "LE" or "<=" => "LE",
        "RANGE" => "RANGE",
        "REPORT" or "NONE" => "NONE",
        _ => throw new FormatException($"'{mode}' is not a mode; use Target, Min, Max, Range or Report"),
    };

    /// <summary>
    /// A merit row with some of its cells changed, as the merit table changes them, or the row as
    /// it was with the reason in <paramref name="problem"/>. A new code or mode is a new kind of
    /// error, so its budget (Scale) is reset. A stack code (MINT, MAXT, TOTT, NLAY, STRESS) has no
    /// wavelength or angle; a row made a stack row loses them, and a stack row made spectral gets
    /// <paramref name="defaultWavelength"/>. MINT and MAXT start at a limit of 8 and 300 nm, at
    /// most 0. A Range whose upper target is below its lower one is lifted to it. The merit
    /// table's cells (Gui.Core MeritRow) go through here, and so do the CLI and the MCP.
    /// </summary>
    public static OperandEdit Change(OperandEdit e, OperandChange c, double defaultWavelength, out string? problem)
    {
        problem = null;
        var was = e;
        if (c.Code is { } rawCode && !rawCode.Trim().Equals(e.Code, StringComparison.OrdinalIgnoreCase))
        {
            string code = rawCode.Trim().ToUpperInvariant();
            try { _ = Codes.Target(code); }
            catch (FormatException x) { problem = x.Message; return was; }
            bool wasStack = e.Wavelengths.Length == 0;
            e = e with { Code = code, Scale = 0.0 };
            if (code is "MINT" or "MAXT" or "TOTT" or "NLAY" or "STRESS") e = e with { Wavelengths = [], AngleDegrees = 0.0 };
            else if (ThinFilm.Api.Codes.IsBand(code) && e.Wavelengths.Length < 2) e = e with { Wavelengths = DefaultBand(code) };
            else if (wasStack) e = e with { Wavelengths = [defaultWavelength] };
            if (ThinFilm.Api.Codes.IsThermal(code) && !(e.Temperature > 0.0)) e = e with { Temperature = Spectra.DefaultTemperature };
            if (code is "MINT" or "MAXT" && e.Threshold <= 0.0)
                e = e with { Threshold = code == "MINT" ? 8.0 : 300.0, Comparison = "LE", Target = 0.0 };
        }
        bool lift = false;
        if (c.Mode is { } mode)
        {
            string cmp;
            try { cmp = Comparison(mode); }
            catch (FormatException x) { problem = x.Message; return was; }
            if (cmp != Comparison(e.Comparison))
            {
                e = e with { Comparison = cmp, Scale = 0.0 };
                lift = cmp == "RANGE";
            }
        }

        bool stack = e.Wavelengths.Length == 0;
        if (stack && (c.Wavelength ?? c.Wavelength2 ?? c.Angle) is not null || stack && c.Points is not null)
        { problem = $"a {e.Code} row is about the stack, so it has no wavelength or angle"; return was; }
        foreach (var w in new[] { c.Wavelength, c.Wavelength2 })
            if (w is { } nm && !(nm > 0.0 && double.IsFinite(nm))) { problem = "a wavelength must be a positive number"; return was; }
        if (!stack && (c.Wavelength ?? c.Wavelength2 ?? (double?)c.Points) is not null)
        {
            double lo = c.Wavelength ?? e.Wavelengths.Min();
            bool band = c.Wavelength2 is not null || e.Wavelengths.Length > 1;
            if (!band && c.Points is not null)
            { problem = "a row at one wavelength has no points; give a second wavelength to make it a band"; return was; }
            if (band)
            {
                double hi = c.Wavelength2 ?? e.Wavelengths.Max();
                int n = c.Points ?? Math.Max(2, e.Wavelengths.Length);
                if (n < 2) { problem = "a band needs at least two points"; return was; }
                if (!(hi > lo)) { problem = "the second wavelength must be above the first"; return was; }
                e = e with { Wavelengths = Spread(lo, hi, n) };
            }
            else e = e with { Wavelengths = [lo] };
        }
        if (c.Angle is { } a)
        {
            if (!(a >= 0.0 && a < 90.0)) { problem = "the angle of incidence must be at least 0° and below 90°"; return was; }
            e = e with { AngleDegrees = a };
        }
        if (c.Limit is { } limit)
        {
            if (e.Code is not ("MINT" or "MAXT")) { problem = $"only MINT and MAXT rows have a limit, not {e.Code}"; return was; }
            if (!(limit >= 0.0 && double.IsFinite(limit))) { problem = "a limit must be zero or a positive number"; return was; }
            e = e with { Threshold = limit, Scale = 0.0 };
        }
        if (c.Temperature is { } kelvin)
        {
            if (!ThinFilm.Api.Codes.IsThermal(e.Code)) { problem = $"only EMIT and AERATIO rows have a temperature, not {e.Code}"; return was; }
            if (!(kelvin > 0.0 && double.IsFinite(kelvin))) { problem = "a temperature must be above 0 K"; return was; }
            e = e with { Temperature = kelvin };
        }
        foreach (var v in new[] { c.Target, c.Target2, c.Weight })
            if (v is { } x && !double.IsFinite(x)) { problem = "a target or weight must be a number"; return was; }
        if (c.Target is { } t) e = e with { Target = t };
        if (c.Target2 is { } t2) e = e with { TargetHi = t2 };
        if (c.Weight is { } wt) e = e with { Weight = wt };
        if (lift && c.Target2 is null && e.TargetHi < e.Target) e = e with { TargetHi = e.Target };
        return e;
    }

    /// <summary>
    /// The band a row is given when it becomes a band code: 300 to 2500 nm in 45 points for the
    /// solar averages, which holds 99% of AM1.5; 2.5 to 25 µm in 46 for EMIT, which holds 90% of a
    /// 100 °C blackbody; and both for AERATIO, whose ASOL and EMIT read the same points.
    /// </summary>
    public static double[] DefaultBand(string code) => code.Trim().ToUpperInvariant() switch
    {
        "EMIT" => Spread(2500.0, 25000.0, 46),
        "AERATIO" => [.. Spread(300.0, 2500.0, 45), .. Spread(3000.0, 25000.0, 45)],
        _ => Spread(300.0, 2500.0, 45),
    };

    /// <summary>n points evenly from a to b, both ends included.</summary>
    public static double[] Spread(double a, double b, int n)
    {
        var w = new double[n];
        for (int i = 0; i < n; ++i) w[i] = a + (b - a) * i / (n - 1.0);
        return w;
    }

    /// <summary>The R, T and A families: "R" stands for RA, RS and RP. Anything else is a code.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Types = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["R"] = ["RA", "RS", "RP"],
        ["T"] = ["TA", "TS", "TP"],
        ["A"] = ["AA", "AS", "AP"],
    };

    /// <summary>The codes a filter names, types expanded and each checked, so a typo is refused
    /// rather than silently matching nothing.</summary>
    public static HashSet<string>? CodeSet(string[]? codes)
    {
        if (codes is null) return null;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in codes.Select(c => c.Trim()).Where(c => c.Length > 0))
        {
            if (Types.TryGetValue(raw, out var family)) { set.UnionWith(family); continue; }
            ThinFilm.Api.Codes.Target(raw);   // throws with a "did you mean" list
            set.Add(raw);
        }
        return set.Count == 0 ? null : set;
    }

    /// <summary>The filter checked once, so a bad code or a reversed range is one clear refusal.</summary>
    public static string? Problem(OperandFilter f)
    {
        try { CodeSet(f.Codes); foreach (var m in f.Modes ?? []) if (m.Trim().Length > 0) Comparison(m); }
        catch (FormatException e) { return e.Message; }
        if (f.WavelengthFrom is { } a && f.WavelengthTo is { } b && b < a)
            return "the second wavelength is below the first";
        if (f.Angle is { } t)
        {
            string kind = t.Kind.ToLowerInvariant();
            if (kind is not ("above" or "below" or "between")) return $"'{t.Kind}' is not an angle test; use above, below or between";
            if (kind == "between" && t.B < t.A) return "the second angle is below the first";
        }
        return null;
    }

    /// <summary>Whether a row (as an editor holds it) passes the filter.</summary>
    public static bool Matches(OperandEdit row, OperandFilter f)
    {
        const double eps = 1e-9;
        var codes = CodeSet(f.Codes);
        if (codes is not null && !codes.Contains(row.Code)) return false;

        var modes = f.Modes?.Where(m => m.Trim().Length > 0).Select(Comparison).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (modes is { Count: > 0 } && !modes.Contains(Comparison(row.Comparison))) return false;

        bool bounded = f.WavelengthFrom is not null || f.WavelengthTo is not null;
        if (bounded)
        {
            if (row.Wavelengths.Length == 0) return false;
            if (f.WavelengthFrom is { } lo && row.Wavelengths.Min() < lo - eps) return false;
            if (f.WavelengthTo is { } hi && row.Wavelengths.Max() > hi + eps) return false;
        }

        if (f.Angle is { } t)
        {
            if (row.Wavelengths.Length == 0) return false;
            double a = row.AngleDegrees;
            bool ok = t.Kind.ToLowerInvariant() switch
            {
                "above" => a > t.A + eps,
                "below" => a < t.A - eps,
                _ => a >= t.A - eps && a <= t.B + eps,
            };
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>n values from a to b in steps of step, b included when the steps land on it.</summary>
    public static double[] Steps(double a, double? b, double? step, string what, out string? problem)
    {
        problem = null;
        if (b is null || step is null || b == a) return [a];
        if (!(step > 0.0)) { problem = $"the {what} step must be positive"; return []; }
        if (b < a) { problem = $"the end {what} is below the start"; return []; }
        int n = (int)Math.Floor((b.Value - a) / step.Value + 1e-9) + 1;
        if (n > MaxRowsAdded) { problem = $"that is {n} {what}s; at most {MaxRowsAdded} rows can be added at once"; return []; }
        // Computed from the index, not accumulated, so 400 + 0.1 * 3000 lands on 700, not 699.99999.
        return Enumerable.Range(0, n).Select(i => Math.Round(a + i * step.Value, 9)).ToArray();
    }

    /// <summary>The rows a sweep makes, angle by angle and, within an angle, by wavelength - so a
    /// table built for 0° and 45° reads as two blocks.</summary>
    public static OperandEdit[] Rows(OperandSweep s, out string? problem)
    {
        int code;
        try { code = ThinFilm.Api.Codes.Target(s.Code); }
        catch (FormatException e) { problem = e.Message; return []; }
        if (ThinFilm.Api.Codes.IsStack(code))
        {
            problem = $"{s.Code.ToUpperInvariant()} describes the stack, not a wavelength; add it with Insert";
            return [];
        }
        string cmp;
        try { cmp = Comparison(s.Mode); }
        catch (FormatException e) { problem = e.Message; return []; }

        var wls = Steps(s.WavelengthFrom, s.WavelengthTo, s.WavelengthStep, "wavelength", out problem);
        if (problem is not null) return [];
        var angles = Steps(s.Angle, s.AngleTo, s.AngleStep, "angle", out problem);
        if (problem is not null) return [];
        if (wls.Length * angles.Length > MaxRowsAdded)
        {
            problem = $"that is {wls.Length * angles.Length} rows; at most {MaxRowsAdded} can be added at once";
            return [];
        }
        string name = s.Code.ToUpperInvariant();
        if (ThinFilm.Api.Codes.IsBand(name))
        {
            // An average over the band: one row per angle, reading every wavelength.
            if (wls.Length < 2)
            { problem = $"{name} is an average over a band; give it an end wavelength and a step"; return []; }
            double kelvin = ThinFilm.Api.Codes.IsThermal(name)
                ? (s.Temperature > 0.0 ? s.Temperature : Spectra.DefaultTemperature) : 0.0;
            return angles.Select(a => new OperandEdit(
                name, cmp, s.Target, cmp == "RANGE" ? s.TargetHi : 0.0, s.Weight, wls, a, Temperature: kelvin)).ToArray();
        }
        return angles.SelectMany(a => wls.Select(w => new OperandEdit(
            s.Code.ToUpperInvariant(), cmp, s.Target, cmp == "RANGE" ? s.TargetHi : 0.0, s.Weight, [w], a))).ToArray();
    }

    /// <summary>A filter in words, for a status line: "RA, Max, 420–680 nm, AOI > 30°".</summary>
    public static string Describe(OperandFilter f)
    {
        var parts = new List<string>();
        if (f.Codes is { Length: > 0 } c) parts.Add(string.Join("/", c));
        if (f.Modes is { Length: > 0 } m) parts.Add(string.Join("/", m));
        if (f.WavelengthFrom is not null || f.WavelengthTo is not null)
            parts.Add($"{(f.WavelengthFrom is { } a ? a.ToString("0.###", Inv) : "…")}–"
                      + $"{(f.WavelengthTo is { } b ? b.ToString("0.###", Inv) : "…")} nm");
        if (f.Angle is { } t)
            parts.Add(t.Kind.ToLowerInvariant() switch
            {
                "above" => $"AOI > {t.A.ToString("0.##", Inv)}°",
                "below" => $"AOI < {t.A.ToString("0.##", Inv)}°",
                _ => $"AOI {t.A.ToString("0.##", Inv)}–{t.B.ToString("0.##", Inv)}°",
            });
        return parts.Count == 0 ? "every row" : string.Join(", ", parts);
    }
}
