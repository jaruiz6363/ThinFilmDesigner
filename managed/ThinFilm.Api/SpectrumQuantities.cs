// SpectrumQuantities.cs — the quantities a spectrum can plot, with the words and the axis for each.
//
// A spectrum reads its quantities as operand codes, so any code that reads a point works. This is
// the list the Spectrum tab offers as checkboxes: the point codes, each with a label, the group it
// is listed under, and the axis it is drawn against. Quantities with different units go on
// different axes; a quantity's Scale is what its engine value is multiplied by to read in that
// axis's unit (the angles come out of the engine in radians and are drawn in degrees).

namespace ThinFilm.Api;

public sealed record SpectrumQuantity(string Code, string Label, string Group, string Axis, double Scale = 1.0);

public static class SpectrumQuantities
{
    public const string Fraction = "fraction (1 = 100%)";
    public const string Density = "optical density";
    public const string Degrees = "angle (°)";
    public const string Unitless = "tan Ψ, cos Δ";
    public const string Decibels = "s/p ratio (dB)";
    public const string GroupDelay = "group delay (fs)";
    public const string Gdd = "GDD (fs²)";
    public const string Tod = "TOD (fs³)";

    private const double ToDegrees = 180.0 / Math.PI;

    public static readonly IReadOnlyList<SpectrumQuantity> All =
    [
        new("RS", "Rs", "Reflectance", Fraction),
        new("RP", "Rp", "Reflectance", Fraction),
        new("RA", "R (avg s,p)", "Reflectance", Fraction),
        new("TS", "Ts", "Transmittance", Fraction),
        new("TP", "Tp", "Transmittance", Fraction),
        new("TA", "T (avg s,p)", "Transmittance", Fraction),
        new("AS", "As", "Absorptance", Fraction),
        new("AP", "Ap", "Absorptance", Fraction),
        new("AA", "A (avg s,p)", "Absorptance", Fraction),
        new("ODS", "OD s", "Optical density", Density),
        new("ODP", "OD p", "Optical density", Density),
        new("ODA", "OD (avg s,p)", "Optical density", Density),
        new("R_PHASE_S", "Phase R s (°)", "Phase", Degrees, ToDegrees),
        new("R_PHASE_P", "Phase R p (°)", "Phase", Degrees, ToDegrees),
        new("T_PHASE_S", "Phase T s (°)", "Phase", Degrees, ToDegrees),
        new("T_PHASE_P", "Phase T p (°)", "Phase", Degrees, ToDegrees),
        new("R_PSI", "Ψ R (°)", "Ellipsometry", Degrees, ToDegrees),
        new("R_DELTA", "Δ R (°)", "Ellipsometry", Degrees, ToDegrees),
        new("R_TANPSI", "tan Ψ R", "Ellipsometry", Unitless),
        new("R_COSDELTA", "cos Δ R", "Ellipsometry", Unitless),
        new("T_PSI", "Ψ T (°)", "Ellipsometry", Degrees, ToDegrees),
        new("T_DELTA", "Δ T (°)", "Ellipsometry", Degrees, ToDegrees),
        new("T_TANPSI", "tan Ψ T", "Ellipsometry", Unitless),
        new("T_COSDELTA", "cos Δ T", "Ellipsometry", Unitless),
        new("R_NPOL", "|Rs − Rp|", "Polarisation", Fraction),
        new("T_NPOL", "|Ts − Tp|", "Polarisation", Fraction),
        new("R_DIAT", "Diattenuation R", "Polarisation", Fraction),
        new("T_DIAT", "Diattenuation T", "Polarisation", Fraction),
        new("R_EXT_DB", "Rs/Rp (dB)", "Polarisation", Decibels),
        new("T_EXT_DB", "Ts/Tp (dB)", "Polarisation", Decibels),
        new("R_GD_S", "GD R s (fs)", "Dispersion", GroupDelay),
        new("R_GD_P", "GD R p (fs)", "Dispersion", GroupDelay),
        new("T_GD_S", "GD T s (fs)", "Dispersion", GroupDelay),
        new("T_GD_P", "GD T p (fs)", "Dispersion", GroupDelay),
        new("R_GDD_S", "GDD R s (fs²)", "Dispersion", Gdd),
        new("R_GDD_P", "GDD R p (fs²)", "Dispersion", Gdd),
        new("T_GDD_S", "GDD T s (fs²)", "Dispersion", Gdd),
        new("T_GDD_P", "GDD T p (fs²)", "Dispersion", Gdd),
        new("R_TOD_S", "TOD R s (fs³)", "Dispersion", Tod),
        new("R_TOD_P", "TOD R p (fs³)", "Dispersion", Tod),
        new("T_TOD_S", "TOD T s (fs³)", "Dispersion", Tod),
        new("T_TOD_P", "TOD T p (fs³)", "Dispersion", Tod),
    ];

    /// <summary>The groups in the order they are listed.</summary>
    public static IReadOnlyList<string> Groups { get; } = All.Select(q => q.Group).Distinct().ToArray();

    /// <summary>The entry for a code, or null for a code that is not a spectrum quantity.</summary>
    public static SpectrumQuantity? Find(string code) =>
        All.FirstOrDefault(q => string.Equals(q.Code, code.Trim(), StringComparison.OrdinalIgnoreCase));

    public static string Label(string code) => Find(code)?.Label ?? code;

    /// <summary>
    /// A spectrum with each quantity in its axis's unit - a phase, Psi or Delta in degrees, not
    /// the engine's radians - which is what the CLI and the MCP report, as the Spectrum tab draws
    /// it. A column of radians beside an angle of incidence in degrees was read as degrees.
    /// </summary>
    public static SpectrumReport InAxisUnits(SpectrumReport report) => report with
    {
        Quantity = report.Quantity.ToDictionary(kv => kv.Key, kv =>
            Find(kv.Key)?.Scale is double s && s != 1.0 ? kv.Value.Select(v => v * s).ToArray() : kv.Value),
    };

    public static ApiResult<SpectrumReport> InAxisUnits(ApiResult<SpectrumReport> r) =>
        r.Ok ? r with { Value = InAxisUnits(r.Value!) } : r;
}
