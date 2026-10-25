// MaterialModel.cs — what a material IS, as opposed to what it was sampled at.
//
// A DESIGN CARRIES ITS MATERIALS' DEFINITIONS, NOT SAMPLES (decided with the user, 2026-10-02).
// Until now a material in a design file was n and k at each wavelength of the design's grid, and
// a merit row moved to a new wavelength was given an index interpolated between grid points -
// harmless while every material was constant, wrong for a dispersive one. Now each material holds
// its model and the range the model is valid over, and is EVALUATED at whatever wavelengths the
// design needs. The engine is unchanged: it is still handed a table of values, as M3.1 records;
// the model is evaluated above the ABI, which is where the scope places dispersion.
//
// OUTSIDE ITS RANGE A MATERIAL IS FLAGGED, NEVER SILENTLY EXTENDED (C0.8). The value is still
// computed - a Sellmeier formula a little past its last fitted point is usually close - and the
// mark goes to the engine through material_index_is_extrapolated (M3.4), which sets
// SYNTHETIC_INDEX on every operand that used it. A TABLE is held at its end value outside its
// rows, and flagged the same way.
//
// AND A MODEL THAT CANNOT GIVE A PHYSICAL INDEX IS REFUSED, with the reason. ARCoatingDesigner's
// glass parser answers n = 1.5 when a formula goes negative; this answers with an exception that
// names the material and the wavelength.
//
// UNITS: model coefficients and ranges are in MICROMETRES, as Sellmeier and Cauchy coefficients
// and glass catalogues are always quoted; the design's grid is in nanometres. The conversion is
// here and nowhere else.

namespace ThinFilm.Api;

/// <summary>
/// A material's definition. <see cref="Kind"/> is one of:
/// <list type="bullet">
/// <item>CONSTANT — Coefficients [n, k]. Not shipped in the database (air excepted), but a user
/// may make one, and designs from before models existed open as one when their samples agree.</item>
/// <item>SELLMEIER — Coefficients [B1, C1, B2, C2, ...]: n² = 1 + Σ Bᵢ λ² / (λ² − Cᵢ), λ in µm,
/// Cᵢ in µm². k = 0.</item>
/// <item>CAUCHY — Coefficients [A, B, C]: n = A + B/λ² + C/λ⁴, λ in µm. k = 0.</item>
/// <item>TABLE — Table rows [λ µm, n, k], ascending in λ, interpolated linearly.</item>
/// </list>
/// The range is where the model is trusted; null at either end means unbounded there. A TABLE's
/// range is its first and last row unless stated.
/// </summary>
public sealed record MaterialModel
{
    public required string Kind { get; init; }
    public double[] Coefficients { get; init; } = [];
    public double[][]? Table { get; init; }
    public double? MinMicrons { get; init; }
    public double? MaxMicrons { get; init; }

    /// <summary>Where the numbers came from: a paper, a catalogue, "measured 2026-03".</summary>
    public string? Source { get; init; }

    /// <summary>For a GLASS: the catalogue (SCHOTT) and the glass (N-BK7). A catalogue glass is
    /// NAMED, never copied: its index always comes from the catalogue (GlassCatalog.cs).</summary>
    public string? Catalog { get; init; }
    public string? Glass { get; init; }

    public static MaterialModel CatalogueGlass(string catalog, string glass) =>
        new() { Kind = "GLASS", Catalog = catalog, Glass = glass, Source = $"{catalog} glass catalogue" };

    public bool IsCatalogueGlass => Kind.Equals("GLASS", StringComparison.OrdinalIgnoreCase);

    /// <summary>The catalogue glass this names, or MaterialUnavailableException saying where it was
    /// looked for - which blocks evaluation of the design without stopping it opening.</summary>
    private Glass FindGlass(string material) =>
        GlassCatalogs.Find(Catalog ?? "", Glass ?? "")
        ?? throw new MaterialUnavailableException(
            (string.Equals(material, $"{Catalog} {Glass}", StringComparison.OrdinalIgnoreCase)
                ? $"the catalogue glass {Catalog} {Glass}"
                : $"{material}, the catalogue glass {Catalog} {Glass},")
            + $" is not in any glass catalogue in {GlassCatalogs.Folder}");

    public static MaterialModel Constant(double n, double k = 0.0) =>
        new() { Kind = "CONSTANT", Coefficients = [n, k] };

    /// <summary>Air: n = 1 exactly, at every wavelength. The one built-in material.</summary>
    public static MaterialModel Air { get; } = Constant(1.0, 0.0) with { Source = "n = 1 exactly" };

    /// <summary>
    /// n and k at <paramref name="nanometres"/>, and whether that wavelength is outside the range
    /// the model is valid over. Throws FormatException, naming <paramref name="material"/>, when
    /// the model is malformed or gives no physical index there.
    /// </summary>
    public (double N, double K, bool Extrapolated) At(double nanometres, string material)
    {
        double um = nanometres / 1000.0;
        var (lo, hi) = Range();
        bool outside = (lo is { } a && um < a - 1e-12) || (hi is { } b && um > b + 1e-12);

        (double n, double k) = Kind.ToUpperInvariant() switch
        {
            "CONSTANT" => Need(2) ? (Coefficients[0], Coefficients[1]) : default,
            "SELLMEIER" => (Sellmeier(um), 0.0),
            "CAUCHY" => Need(3) ? (Coefficients[0] + Coefficients[1] / (um * um)
                                   + Coefficients[2] / (um * um * um * um), 0.0) : default,
            "TABLE" => Interpolate(um),
            "GLASS" => (FindGlass(material).At(nanometres).N, 0.0),
            _ => throw new FormatException($"material '{material}': '{Kind}' is not a material model; "
                                           + "use CONSTANT, SELLMEIER, CAUCHY, TABLE or GLASS"),
        };

        if (!(n > 0.0) || !double.IsFinite(n))
            throw new FormatException(
                $"material '{material}' has no physical index at {nanometres:0.###} nm (n = {n:G4}); "
                + "its model does not hold there");
        if (!(k >= 0.0) || !double.IsFinite(k))
            throw new FormatException(
                $"material '{material}' has extinction k = {k:G4} at {nanometres:0.###} nm; k cannot be negative");
        return (n, k, outside);

        bool Need(int count) => Coefficients.Length >= count
            ? true
            : throw new FormatException(
                $"material '{material}': a {Kind} model needs {count} coefficients, has {Coefficients.Length}");

        double Sellmeier(double l)
        {
            if (Coefficients.Length == 0 || Coefficients.Length % 2 != 0)
                throw new FormatException(
                    $"material '{material}': a SELLMEIER model needs pairs B, C; has {Coefficients.Length} numbers");
            double l2 = l * l, n2 = 1.0;
            for (int i = 0; i < Coefficients.Length; i += 2)
            {
                double denominator = l2 - Coefficients[i + 1];
                if (Math.Abs(denominator) < 1e-12)
                    throw new FormatException(
                        $"material '{material}': {nanometres:0.###} nm is on a pole of its Sellmeier formula");
                n2 += Coefficients[i] * l2 / denominator;
            }
            return n2 > 0.0 ? Math.Sqrt(n2) : double.NaN;
        }

        (double, double) Interpolate(double l)
        {
            var t = Table;
            if (t is null || t.Length == 0)
                throw new FormatException($"material '{material}': a TABLE model has no rows");
            if (t.Any(r => r.Length < 3))
                throw new FormatException($"material '{material}': every TABLE row needs λ, n and k");
            if (l <= t[0][0]) return (t[0][1], t[0][2]);
            if (l >= t[^1][0]) return (t[^1][1], t[^1][2]);
            int i = 1;
            while (t[i][0] < l) ++i;
            double f = (l - t[i - 1][0]) / (t[i][0] - t[i - 1][0]);
            return (t[i - 1][1] + f * (t[i][1] - t[i - 1][1]), t[i - 1][2] + f * (t[i][2] - t[i - 1][2]));
        }
    }

    /// <summary>
    /// dn/dλ, dk/dλ and their second and third derivatives at <paramref name="nanometres"/>, per
    /// nm, nm² and nm³, for group delay, GDD (ABI 11) and TOD (ABI 12). Analytic for CONSTANT,
    /// CAUCHY and SELLMEIER. A TABLE is interpolated linearly, so its slope is the segment's and its
    /// higher derivatives zero, as the interpolant it is; outside its rows it is held, so all are
    /// zero. A catalogue glass is one of several closed-form formulas, differenced centrally - over
    /// ±0.5 nm for the first two and ±1, ±2 nm for the third, where a smaller step would lose more
    /// to rounding than a smooth formula loses to the step.
    /// </summary>
    public (double Dn, double Dk, double D2n, double D2k, double D3n, double D3k) Slopes(double nanometres, string material)
    {
        double um = nanometres / 1000.0;
        const double PerUm = 1e-3, PerUm2 = 1e-6, PerUm3 = 1e-9;   // d/dλ(µm) to d/dλ(nm)
        switch (Kind.ToUpperInvariant())
        {
            case "CONSTANT":
                return (0.0, 0.0, 0.0, 0.0, 0.0, 0.0);
            case "CAUCHY":
            {
                double b = Coefficients[1], c = Coefficients[2];
                double d1 = -2.0 * b / Math.Pow(um, 3) - 4.0 * c / Math.Pow(um, 5);
                double d2 = 6.0 * b / Math.Pow(um, 4) + 20.0 * c / Math.Pow(um, 6);
                double d3 = -24.0 * b / Math.Pow(um, 5) - 120.0 * c / Math.Pow(um, 7);
                return (d1 * PerUm, 0.0, d2 * PerUm2, 0.0, d3 * PerUm3, 0.0);
            }
            case "SELLMEIER":
            {
                // f = n² = 1 + Σ B x/(x − C), x = λ²:  f_x = Σ −B C/(x − C)²,  f_xx = Σ 2 B C/(x − C)³,
                // f_xxx = Σ −6 B C/(x − C)⁴
                double x = um * um, fx = 0.0, fxx = 0.0, fxxx = 0.0;
                for (int i = 0; i + 1 < Coefficients.Length; i += 2)
                {
                    double b = Coefficients[i], c = Coefficients[i + 1], q = x - c;
                    fx += -b * c / (q * q);
                    fxx += 2.0 * b * c / (q * q * q);
                    fxxx += -6.0 * b * c / (q * q * q * q);
                }
                double n = At(nanometres, material).N;
                // d/dλ, d²/dλ² and d³/dλ³ of f, by the chain rule through x = λ²
                double f1 = 2.0 * um * fx, f2 = 4.0 * x * fxx + 2.0 * fx, f3 = 12.0 * um * fxx + 8.0 * x * um * fxxx;
                // f = n²:  f' = 2 n n',  f'' = 2 n'² + 2 n n'',  f''' = 6 n' n'' + 2 n n'''
                double n1 = f1 / (2.0 * n), n2 = (f2 - 2.0 * n1 * n1) / (2.0 * n), n3 = (f3 - 6.0 * n1 * n2) / (2.0 * n);
                return (n1 * PerUm, 0.0, n2 * PerUm2, 0.0, n3 * PerUm3, 0.0);
            }
            case "TABLE":
            {
                var t = Table!;
                if (t.Length < 2 || um <= t[0][0] || um >= t[^1][0]) return (0.0, 0.0, 0.0, 0.0, 0.0, 0.0);
                int i = 1;
                while (t[i][0] < um) ++i;
                double span = t[i][0] - t[i - 1][0];
                return ((t[i][1] - t[i - 1][1]) / span * PerUm, (t[i][2] - t[i - 1][2]) / span * PerUm, 0.0, 0.0, 0.0, 0.0);
            }
            default:
            {
                const double h = 0.5;
                double up = At(nanometres + h, material).N, mid = At(nanometres, material).N,
                       down = At(nanometres - h, material).N;
                double u1 = At(nanometres + 1.0, material).N, u2 = At(nanometres + 2.0, material).N,
                       d1 = At(nanometres - 1.0, material).N, d2 = At(nanometres - 2.0, material).N;
                return ((up - down) / (2.0 * h), 0.0, (up - 2.0 * mid + down) / (h * h), 0.0,
                        (u2 - 2.0 * u1 + 2.0 * d1 - d2) / 2.0, 0.0);
            }
        }
    }

    /// <summary>The range the model is trusted over, in µm: as stated, or a table's rows.</summary>
    public (double? Min, double? Max) Range()
    {
        if (Kind.Equals("GLASS", StringComparison.OrdinalIgnoreCase))
            return GlassCatalogs.Find(Catalog ?? "", Glass ?? "") is { } g ? (g.MinMicrons, g.MaxMicrons) : (null, null);
        if (Kind.Equals("TABLE", StringComparison.OrdinalIgnoreCase) && Table is { Length: > 0 } t)
            return (MinMicrons ?? t[0][0], MaxMicrons ?? t[^1][0]);
        return (MinMicrons, MaxMicrons);
    }

    /// <summary>
    /// The model for a material that only has samples - a design saved before models existed.
    /// Samples that are all the same are a CONSTANT, valid everywhere, which is what those
    /// designs meant; anything else is a TABLE over the grid it was sampled on.
    /// </summary>
    public static MaterialModel FromSamples(double[] gridNm, double[] n, double[] k)
    {
        bool constant = n.All(v => v == n[0]) && k.All(v => v == k[0]);
        if (constant) return Constant(n[0], k[0]);
        var rows = new double[gridNm.Length][];
        for (int i = 0; i < gridNm.Length; ++i) rows[i] = [gridNm[i] / 1000.0, n[i], k[i]];
        return new MaterialModel { Kind = "TABLE", Table = rows, Source = "sampled values from an older design file" };
    }
}
