// BetweenSamples.cs — the spec checked between the merit function's sample points.
//
// The merit function only sees the wavelengths it samples, and a thick design can have a resonance
// narrower than their spacing. The user guide met it twice on 2026-10-23: a bandpass sampled every
// 2.5 nm met its merit function with a spike to 55% at 466.2 nm, and a beam splitter sampled every
// 2 nm let 48% of s through at 439 nm. Both looked perfect in the merit table.
//
// So every Min and Max row of R, T or A is grouped into bands - one code, mode, target and angle,
// wavelengths at a steady step - and each band is evaluated FINELY_ times as densely. A band is
// reported when somewhere between its samples the spec is missed by clearly more than at any
// sample: twice the worst sampled miss, plus a tenth of the room the target leaves (the target
// itself for a Max, 1 - target for a Min). A design that misses its spec evenly is not reported;
// the merit already says so.

using System.Globalization;

namespace ThinFilm.Api;

/// <summary>Where a band's spec is missed between its sample points: the row as written (code,
/// "Min" or "Max", target, angle in degrees), the band it belongs to (nm), and the worst point
/// found - wavelength (nm) and value - beside the worst value at a sample.</summary>
public sealed record BetweenSamplesWarning(string Code, string Mode, double Target, double Angle,
                                           double From, double To, double Wavelength, double Value,
                                           double WorstSampled)
{
    public string Text =>
        FormattableString.Invariant($"between samples: {Code} {Value:0.#####} at {Wavelength:0.0#} nm, {Angle:0.##}° ")
        + FormattableString.Invariant($"({Code} {Mode} {Target:G4} from {From:0.##} to {To:0.##} nm; worst at a sample {WorstSampled:0.#####}); ")
        + "sample more finely there";
}

public sealed partial class ThinFilmApi
{
    private const int Finely = 20;
    private static readonly HashSet<string> Fractions = new(StringComparer.OrdinalIgnoreCase)
        { "RA", "RS", "RP", "TA", "TS", "TP", "AA", "AS", "AP" };

    /// <summary>The design's Min and Max rows of R, T and A checked between their sample points.</summary>
    public ApiResult<BetweenSamplesWarning[]> CheckBetweenSamples(string id) =>
        Find(id, out var s, out var fail) ? BetweenSamples(s) : ApiResult<BetweenSamplesWarning[]>.Failure(fail);

    /// <summary>The same check on a run's result, before it is kept or reverted.</summary>
    public ApiResult<BetweenSamplesWarning[]> RunBetweenSamples(string run) =>
        Ended(run, out _, out _, out var after, out var fail)
            ? BetweenSamples(after!) : ApiResult<BetweenSamplesWarning[]>.Failure(fail!);

    private static ApiResult<BetweenSamplesWarning[]> BetweenSamples(Session s)
    {
        var doc = s.Document;
        var rows = new List<(string Code, string Cmp, double Target, double Angle, double Wl)>();
        foreach (var o in doc.Operands)
        {
            if (o.Comparison is not ("GE" or "LE") || !Fractions.Contains(o.Code) || o.Points is not { Length: 1 } p)
                continue;
            var point = doc.Points[p[0]];
            rows.Add((o.Code.ToUpperInvariant(), o.Comparison, o.Target, point.AngleDegrees, doc.Wavelength[point.Wavelength]));
        }

        var warnings = new List<BetweenSamplesWarning>();
        foreach (var group in rows.GroupBy(r => (r.Code, r.Cmp, r.Target, r.Angle)))
        {
            var wl = group.Select(r => r.Wl).Distinct().OrderBy(w => w).ToArray();
            foreach (var band in Bands(wl))
            {
                var (code, cmp, target, angle) = group.Key;
                int steps = (band.Length - 1) * Finely + 1;
                var spectrum = Spectrum(s, new SpectrumRequest(band[0], band[^1], steps, angle, [code]));
                if (!spectrum.Ok) return ApiResult<BetweenSamplesWarning[]>.Failure(spectrum.Error!, spectrum.Status);
                var value = spectrum.Value!.Quantity[code];
                var x = spectrum.Value!.Wavelength;

                double Miss(double v) => cmp == "LE" ? v - target : target - v;
                double sampled = 0.0, worst = 0.0, worstSampled = double.NaN;
                int at = -1;
                for (int i = 0; i < steps; ++i)
                {
                    if (double.IsNaN(value[i])) continue;
                    double miss = Miss(value[i]);
                    if (i % Finely == 0)
                    {
                        if (double.IsNaN(worstSampled) || Miss(worstSampled) < miss) worstSampled = value[i];
                        sampled = Math.Max(sampled, miss);
                    }
                    if (miss > worst) { worst = miss; at = i; }
                }
                double room = Math.Max(cmp == "LE" ? target : 1.0 - target, 1e-4);
                if (at >= 0 && worst > 2.0 * sampled + 0.1 * room)
                    warnings.Add(new BetweenSamplesWarning(code, cmp == "LE" ? "Max" : "Min", target, angle,
                                                           band[0], band[^1], x[at], value[at], worstSampled));
            }
        }
        // Worst first, measured against the room the target leaves.
        return ApiResult<BetweenSamplesWarning[]>.Success(warnings
            .OrderByDescending(w => (w.Mode == "Max" ? w.Value - w.Target : w.Target - w.Value)
                                    / Math.Max(w.Mode == "Max" ? w.Target : 1.0 - w.Target, 1e-4))
            .ToArray());
    }

    /// <summary>Sorted wavelengths split where the step changes: 450 to 535 and 565 to 650 at 1 nm
    /// are two bands, not one with a 30 nm gap. A lone wavelength has nothing between it.</summary>
    private static IEnumerable<double[]> Bands(double[] wl)
    {
        int start = 0;
        for (int i = 1; i <= wl.Length; ++i)
        {
            bool split = i == wl.Length
                || (i - start >= 2 && Math.Abs((wl[i] - wl[i - 1]) - (wl[i - 1] - wl[i - 2])) > 1e-6 * Math.Max(1.0, wl[i]));
            if (!split) continue;
            if (i - start >= 2) yield return wl[start..i];
            start = i;
        }
    }
}
