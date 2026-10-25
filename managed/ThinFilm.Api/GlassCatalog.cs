// GlassCatalog.cs — vendor glass catalogues (ZEMAX AGF), read and evaluated.
//
// A GLASS IS NAMED BY A DESIGN, NEVER COPIED INTO IT AND NEVER EDITED (the user, 2026-10-02). The
// design says "SCHOTT N-BK7" and its index always comes from the catalogue. That is the opposite
// of a database material, which is copied in and edited there; a catalogue glass is a vendor's
// published melt data, and a design that altered it would no longer be using that glass.
//
// THE CATALOGUES SHIP WITH THE PROGRAM (materials/glass/*.AGF, the vendors' own files, kept byte
// for byte). Five of them, 1799 glasses, and they use three dispersion formulas between them:
//
//     1  Schott       n² = a0 + a1λ² + a2λ⁻² + a3λ⁻⁴ + a4λ⁻⁶ + a5λ⁻⁸
//     2  Sellmeier 1  n² − 1 = K1λ²/(λ² − L1) + K2λ²/(λ² − L2) + K3λ²/(λ² − L3)
//     3  Herzberger   n = A + B·L + C·L² + Dλ² + Eλ⁴ + Fλ⁶,  L = 1/(λ² − 0.028)
//
// λ in µm throughout. Any other formula number is REFUSED with the glass and the number named:
// ARCoatingDesigner's parser answers an unknown formula with its first coefficient and a negative
// n² with 1.5, and an index that is wrong without saying so is the one thing this must not do.
//
// THE CHECK THAT MATTERS: every NM line carries the vendor's own nd, so every one of the 1799
// glasses is evaluated at the helium d line and compared with it (MaterialTests). A transposed
// coefficient or a misremembered formula fails hundreds of glasses at once.
//
// What is read: NM (name, formula, nd, vd), CD (coefficients), LD (valid range). What is not, yet:
// IT (internal transmittance - an absorption a thick substrate would show; glasses are k = 0 here
// for now) and TD (thermal coefficients; temperature is ABOVE ABI, C5.6).

using System.Globalization;

namespace ThinFilm.Api;

/// <summary>One glass from a catalogue.</summary>
public sealed record Glass(
    string Catalog, string Name, int Formula, double[] Coefficients,
    double MinMicrons, double MaxMicrons, double Nd, double Vd)
{
    public string FullName => $"{Catalog} {Name}";

    /// <summary>
    /// How far the catalogue's formula is from the catalogue's own nd at the helium d line, or
    /// null when there is nothing to compare: nd given as 1 (a placeholder, as for CDGM's infrared
    /// HWS glasses) or the d line outside the glass's range. Three of the 1799 shipped glasses
    /// disagree with themselves by more than 2e-4 - CDGM H-TK9 by 0.034 - and choosing one says
    /// so rather than quietly using whichever number happened to be read.
    /// </summary>
    public double? NdDisagreement
    {
        get
        {
            const double d = 587.5618;
            if (Nd <= 1.0 || d / 1000.0 < MinMicrons || d / 1000.0 > MaxMicrons) return null;
            try { return Math.Abs(At(d).N - Nd); }
            catch (FormatException) { return null; }
        }
    }

    /// <summary>A sentence when the catalogue disagrees with itself; null when it does not.</summary>
    public string? Caution =>
        NdDisagreement is > 2e-4 and var x
            ? $"{FullName}: the catalogue's dispersion formula gives n = {At(587.5618).N:F5} at the d "
              + $"line, but its own nd is {Nd:F5} (off by {x:G2}). ThinFilm uses the formula; check the "
              + "vendor's data sheet."
            : null;

    /// <summary>n at <paramref name="nanometres"/>, and whether that is outside the catalogue's
    /// stated range for the glass. Throws, naming the glass, when it cannot give a physical n.</summary>
    public (double N, bool Extrapolated) At(double nanometres)
    {
        double l = nanometres / 1000.0, l2 = l * l;
        double[] c = Coefficients;
        double C(int i) => i < c.Length ? c[i] : 0.0;

        double n = Formula switch
        {
            1 => Root(C(0) + C(1) * l2 + C(2) / l2 + C(3) / (l2 * l2) + C(4) / (l2 * l2 * l2)
                      + C(5) / (l2 * l2 * l2 * l2)),
            2 => Root(1.0 + C(0) * l2 / (l2 - C(1)) + C(2) * l2 / (l2 - C(3)) + C(4) * l2 / (l2 - C(5))),
            3 => Herzberger(),
            _ => throw new FormatException(
                $"glass {FullName} uses dispersion formula {Formula}, which ThinFilm does not "
                + "evaluate (it reads formulas 1 Schott, 2 Sellmeier 1 and 3 Herzberger)"),
        };
        if (!(n > 0.0) || !double.IsFinite(n))
            throw new FormatException($"glass {FullName} has no physical index at {nanometres:0.###} nm");
        bool outside = l < MinMicrons - 1e-12 || l > MaxMicrons + 1e-12;
        return (n, outside);

        static double Root(double n2) => n2 > 0.0 ? Math.Sqrt(n2) : double.NaN;

        double Herzberger()
        {
            double L = 1.0 / (l2 - 0.028);
            return C(0) + C(1) * L + C(2) * L * L + C(3) * l2 + C(4) * l2 * l2 + C(5) * l2 * l2 * l2;
        }
    }
}

public sealed class GlassCatalog
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public string Name { get; }
    public IReadOnlyList<Glass> Glasses { get; }
    private readonly Dictionary<string, Glass> _byName;

    private GlassCatalog(string name, List<Glass> glasses)
    {
        Name = name;
        Glasses = glasses;
        _byName = new Dictionary<string, Glass>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in glasses) _byName.TryAdd(g.Name, g);
    }

    public Glass? Find(string glass) => _byName.GetValueOrDefault(glass);

    /// <summary>A catalogue file, named by its file name (SCHOTT.AGF is "SCHOTT"). The encoding is
    /// taken from its byte-order mark: CDGM issues UTF-16, the others ASCII.</summary>
    public static GlassCatalog Load(string path) =>
        Parse(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path).ToUpperInvariant());

    public static GlassCatalog Parse(string text, string catalog)
    {
        var glasses = new List<Glass>();
        string? name = null;
        int formula = 0;
        double nd = 0, vd = 0;
        double[] cd = [];
        double lo = 0, hi = double.PositiveInfinity;
        int line = 0;

        void Finish()
        {
            if (name is not null) glasses.Add(new Glass(catalog, name, formula, cd, lo, hi, nd, vd));
            (name, cd, lo, hi) = (null, [], 0, double.PositiveInfinity);
        }

        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            ++line;
            var w = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (w.Length == 0) continue;
            switch (w[0])
            {
                case "NM":
                    Finish();
                    if (w.Length < 6) throw Bad("an NM line needs a name, a formula, a code, nd and vd");
                    name = w[1];
                    formula = (int)Number(w[2]);
                    nd = Number(w[4]);
                    vd = Number(w[5]);
                    break;
                case "CD" when name is not null:
                    cd = w.Skip(1).Select(Number).ToArray();
                    break;
                case "LD" when name is not null && w.Length >= 3:
                    lo = Number(w[1]);
                    hi = Number(w[2]);
                    break;
            }
        }
        Finish();
        return new GlassCatalog(catalog, glasses);

        double Number(string s) =>
            double.TryParse(s, NumberStyles.Float, Inv, out double v) ? v
            : throw Bad($"'{s}' is not a number");
        FormatException Bad(string why) => new($"glass catalogue {catalog}, line {line}: {why}");
    }
}

/// <summary>
/// Every catalogue in a folder, and the one place a glass named by a design is looked up. The
/// shipped catalogues by default; a front end may point it elsewhere.
/// </summary>
public static class GlassCatalogs
{
    public static string ShippedFolder => Path.Combine(AppContext.BaseDirectory, "materials", "glass");

    private static string _folder = ShippedFolder;
    private static IReadOnlyList<GlassCatalog>? _catalogs;
    private static readonly object Gate = new();

    public static string Folder
    {
        get => _folder;
        set { lock (Gate) { _folder = value; _catalogs = null; } }
    }

    /// <summary>The catalogues in <see cref="Folder"/>, read once. A catalogue that does not parse
    /// is skipped and reported in <see cref="Problems"/>, so one bad file does not lose the rest.</summary>
    public static IReadOnlyList<GlassCatalog> All
    {
        get
        {
            lock (Gate)
            {
                if (_catalogs is not null) return _catalogs;
                var list = new List<GlassCatalog>();
                var problems = new List<string>();
                if (Directory.Exists(_folder))
                    foreach (var f in Directory.GetFiles(_folder).Where(f => f.EndsWith(".agf", StringComparison.OrdinalIgnoreCase)).Order())
                    {
                        try { list.Add(GlassCatalog.Load(f)); }
                        catch (Exception e) when (e is FormatException or IOException) { problems.Add(e.Message); }
                    }
                Problems = problems;
                return _catalogs = list;
            }
        }
    }

    public static IReadOnlyList<string> Problems { get; private set; } = [];

    public static Glass? Find(string catalog, string glass) =>
        All.FirstOrDefault(c => string.Equals(c.Name, catalog, StringComparison.OrdinalIgnoreCase))?.Find(glass);
}

/// <summary>
/// A material a design names but cannot reach - a catalogue glass not in any catalogue here. Not
/// a malformed design: the design opens, shows its layers and materials, and refuses to be
/// evaluated with this message until its substrate is one that can be.
/// </summary>
public sealed class MaterialUnavailableException(string message) : FormatException(message);
