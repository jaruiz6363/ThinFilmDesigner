// Spectra.cs — the named spectra a band operand is weighted by (M3.3), and the weights themselves.
//
// A band operand is a weighted average of a per-point quantity over the points it reads:
// ASOL = ∫ A S dλ / ∫ S dλ with S the AM1.5 global spectrum, EMIT the same with a blackbody at the
// row's temperature. The engine already reduces a span as a weighted sum (TF_REDUCE_SUM over
// point_weight), so all that is needed here is the weights. That is why the spectra live above
// the ABI: the engine never has to know what AM1.5 is.
//
// THE WEIGHTS INTEGRATE THE SPECTRUM, NOT SAMPLE IT. A(λ) is taken as linear between the row's
// points, and each point's weight is ∫ S(λ) φ_i(λ) dλ with φ_i the hat function on that point.
// AM1.5 has deep water and oxygen bands a 50 nm grid would sample at random; integrated this way
// every feature of the table counts at its true size however coarse the row's points are, and the
// error that remains is only that of A being linear between points, which is smooth.
//
// THE BAND IS THE ROW'S OWN, first point to last, and the average is normalised over it, so a row
// from 300 to 2500 nm reads the fraction of the solar power in that band that is absorbed.

using System.Globalization;
using System.Reflection;

namespace ThinFilm.Api;

public static class Spectra
{
    /// <summary>The second radiation constant hc/k, nm·K.</summary>
    public const double C2 = 1.438776877e7;

    /// <summary>The temperature a new EMIT or AERATIO row is given: 100 °C, where solar absorbers
    /// are usually specified.</summary>
    public const double DefaultTemperature = 373.15;

    /// <summary>Which spectrum this row's code is weighted by, in words.</summary>
    public static string Describe(string code, double temperature) => code.ToUpperInvariant() switch
    {
        "EMIT" => $"a blackbody at {temperature.ToString("0.##", CultureInfo.InvariantCulture)} K",
        _ => "AM1.5 global (ASTM G173-03)",
    };

    /// <summary>
    /// ASTM G173-03 global tilt, 37° facing the sun: wavelength in nm and spectral irradiance in
    /// W m⁻² nm⁻¹, 280 to 4000 nm. Read once from the copy embedded in this assembly, so evaluating
    /// a design never depends on a file beside the program.
    /// </summary>
    public static (double[] Nm, double[] Irradiance) Am15Global => _am15.Value;

    private static readonly Lazy<(double[], double[])> _am15 = new(() =>
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ThinFilm.Api.astm-g173-03.csv")
                           ?? throw new InvalidOperationException("the AM1.5 table is not embedded in ThinFilm.Api");
        using var reader = new StreamReader(stream);
        var nm = new List<double>();
        var global = new List<double>();
        while (reader.ReadLine() is { } line)
        {
            var f = line.Split(',');
            if (f.Length < 3 || !double.TryParse(f[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double w))
                continue;   // the title and the header
            nm.Add(w);
            global.Add(double.Parse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture));
        }
        return (nm.ToArray(), global.ToArray());
    });

    /// <summary>Spectral radiance of a blackbody, up to a constant factor, at λ nm and T kelvin.</summary>
    public static double Planck(double nm, double kelvin)
    {
        double x = C2 / (nm * kelvin);
        if (x > 700.0) return 0.0;
        return 1.0 / (Math.Pow(nm * 1e-3, 5) * (Math.Exp(x) - 1.0));
    }

    /// <summary>The averages a band row is made of: AERATIO is an ASOL over an EMIT on the same
    /// points, every other band code is itself.</summary>
    public static string[] WeightingsOf(string code) =>
        code.Trim().Equals("AERATIO", StringComparison.OrdinalIgnoreCase) ? ["ASOL", "EMIT"] : [code.Trim().ToUpperInvariant()];

    /// <summary>The solar irradiance at λ nm, linear between the table's wavelengths and zero
    /// outside them.</summary>
    public static double Solar(double nm)
    {
        var (x, y) = Am15Global;
        if (nm < x[0] || nm > x[^1]) return 0.0;
        int i = Array.BinarySearch(x, nm);
        if (i >= 0) return y[i];
        i = ~i;
        double t = (nm - x[i - 1]) / (x[i] - x[i - 1]);
        return y[i - 1] + t * (y[i] - y[i - 1]);
    }

    /// <summary>
    /// The weight of each of <paramref name="wavelengths"/> (nm, in any order) in the average
    /// against the spectrum <paramref name="code"/> names - "SOLAR" for the AM1.5 codes, "EMIT" for
    /// a blackbody at <paramref name="kelvin"/>. They sum to one. Null with the reason in
    /// <paramref name="problem"/> when the band holds none of the spectrum's power.
    /// </summary>
    public static double[]? Weights(string code, IReadOnlyList<double> wavelengths, double kelvin, out string? problem)
    {
        problem = null;
        bool thermal = code.Equals("EMIT", StringComparison.OrdinalIgnoreCase);
        if (thermal && !(kelvin > 0.0 && double.IsFinite(kelvin)))
        { problem = "a blackbody needs a temperature above 0 K"; return null; }
        if (wavelengths.Count < 2)
        { problem = $"{code.ToUpperInvariant()} is an average over a band and needs at least two wavelengths"; return null; }

        var x = wavelengths.Distinct().OrderBy(w => w).ToArray();
        if (x.Length < 2)
        { problem = $"{code.ToUpperInvariant()} needs a band, not one wavelength"; return null; }

        var w = new double[x.Length];
        for (int j = 0; j + 1 < x.Length; ++j)
        {
            var (left, right) = thermal ? HatPlanck(x[j], x[j + 1], kelvin) : HatSolar(x[j], x[j + 1]);
            w[j] += left;
            w[j + 1] += right;
        }
        double total = w.Sum();
        if (!(total > 0.0) || !double.IsFinite(total))
        {
            problem = thermal
                ? $"a blackbody at {kelvin:0.##} K emits nothing measurable between {x[0]:0.###} and {x[^1]:0.###} nm"
                : $"the band {x[0]:0.###}–{x[^1]:0.###} nm is outside AM1.5, which runs from 280 to 4000 nm";
            return null;
        }

        // Back to the caller's order; a wavelength listed twice shares its weight.
        var result = new double[wavelengths.Count];
        for (int i = 0; i < result.Length; ++i)
        {
            int j = Array.BinarySearch(x, wavelengths[i]);
            int copies = wavelengths.Count(v => v == wavelengths[i]);
            result[i] = w[j] / total / copies;
        }
        return result;
    }

    /// <summary>∫ S φ over [a, b] for the two hats that meet there: the one falling from a and the
    /// one rising to b. The table is linear between its own wavelengths, so on each piece between
    /// them S φ is quadratic and Simpson's rule is exact.</summary>
    private static (double Left, double Right) HatSolar(double a, double b)
    {
        var (nm, _) = Am15Global;
        var nodes = new List<double> { a };
        int i = Array.BinarySearch(nm, a);
        for (i = i >= 0 ? i + 1 : ~i; i < nm.Length && nm[i] < b; ++i) nodes.Add(nm[i]);
        nodes.Add(b);
        return Hats(a, b, nodes, Solar);
    }

    /// <summary>The same for a blackbody, which is smooth, on 64 Simpson panels.</summary>
    private static (double Left, double Right) HatPlanck(double a, double b, double kelvin)
    {
        const int panels = 64;
        var nodes = Enumerable.Range(0, panels + 1).Select(k => a + (b - a) * k / panels).ToList();
        return Hats(a, b, nodes, nm => Planck(nm, kelvin));
    }

    private static (double, double) Hats(double a, double b, List<double> nodes, Func<double, double> s)
    {
        double h = b - a, left = 0.0, right = 0.0;
        for (int k = 0; k + 1 < nodes.Count; ++k)
        {
            double p = nodes[k], q = nodes[k + 1], m = 0.5 * (p + q);
            if (!(q > p)) continue;
            double sp = s(p), sm = s(m), sq = s(q);
            // Simpson on S·(b-λ)/h and S·(λ-a)/h.
            left += (q - p) / 6.0 * (sp * (b - p) + 4.0 * sm * (b - m) + sq * (b - q)) / h;
            right += (q - p) / 6.0 * (sp * (p - a) + 4.0 * sm * (m - a) + sq * (q - a)) / h;
        }
        return (left, right);
    }
}
