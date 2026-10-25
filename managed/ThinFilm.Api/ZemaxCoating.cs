// ZemaxCoating.cs — reading a design out of an OpticStudio coating file, and writing one into it.
//
// A COATING FILE IS SHARED. COATING.DAT holds dozens of materials and coatings that other lenses
// use, so a design is written in SURGICALLY (the user, 2026-10-21): its own COAT block and the MATE
// blocks it needs are added or replaced, and every other line - other coatings, TAPR, TABLE,
// IDEAL, ENCRYPTED, comments, the padding in `MATE          SIO2_EM` - is written back exactly as
// it was read, in the file's own encoding (OpticStudio's sample is UTF-16) and line endings.
//
// EXISTING NAMES ARE NEVER CHANGED. Other coatings name a MATE, so a material already in the file
// is never rewritten: when a design's material has the same name and the same data (1e-4 in n and
// k) the coating uses it, and when the data differ ours is added as NAME_2, NAME_3, ... and the
// report says so. A COAT of the same name is replaced where it stands, only when asked.
//
// MATERIALS ARE n,k TABLES, AND AN EQUATION RIDES ALONG IN A COMMENT (the user's idea). OpticStudio
// only reads tables; a Sellmeier, Cauchy or catalogue-glass material is tabulated finely enough
// that OpticStudio's linear interpolation is within 1e-5 of the formula, and its definition is
// written above the MATE line as `! ThinFilmDesigner MODEL ...`. Reading it back gives the
// equation again - but only if the equation still reproduces the table; if someone edited the
// numbers in the file, the table they edited wins.
//
// THICKNESSES ARE WRITTEN IN MICROMETRES, ALWAYS (the user). In waves they would be counted at the
// primary wavelength of whatever lens the coating lands on. The design's reference wavelength
// goes in a comment above the COAT instead, and on reading, a coating in waves is counted at that
// wavelength, or at the open design's reference when the file does not say. A wave of optical
// thickness is 4 QWOT, so such layers come in as QWOT.
//
// k IS NEGATIVE IN OPTICSTUDIO for an absorber (n + ik with k < 0; a positive k is gain), and
// non-negative here. Writing negates it and so does reading.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ThinFilm.Api;

/// <summary>One keyword section of a coating file: lines [Start, End) of the file, the keyword line
/// first; <see cref="Header"/> is where the ThinFilmDesigner comments directly above it begin.</summary>
public sealed record ZemaxSection(string Keyword, string Name, int Header, int Start, int End);

/// <summary>One layer line of a COAT: material, thickness, whether it is µm (else waves), loop
/// index and taper name.</summary>
public sealed record ZemaxLayer(string Material, double Thickness, bool Absolute, int Loop, string? Taper);

public sealed partial class ZemaxCoatingFile
{
    public const string Tag = "! ThinFilmDesigner";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly string[] Keywords = ["MATE", "TAPR", "COAT", "TABLE", "IDEAL", "IDEAL2", "ENCRYPTED"];

    public List<string> Lines { get; }
    public Encoding Encoding { get; }
    public string NewLine { get; }
    public IReadOnlyList<ZemaxSection> Sections { get; private set; } = [];

    private ZemaxCoatingFile(List<string> lines, Encoding encoding, string newLine)
    {
        Lines = lines;
        Encoding = encoding;
        NewLine = newLine;
        Index();
    }

    /// <summary>An empty file, as a new export starts: ASCII-safe UTF-8 without a BOM, CRLF.</summary>
    public static ZemaxCoatingFile Empty() => new([], new UTF8Encoding(false), "\r\n");

    public static ZemaxCoatingFile Read(string path) => FromBytes(File.ReadAllBytes(path));

    /// <summary>The file's text in whatever encoding its byte-order mark names: OpticStudio writes
    /// UTF-16; a hand-made file is usually ASCII.</summary>
    public static ZemaxCoatingFile FromBytes(byte[] bytes)
    {
        Encoding encoding;
        int skip = 0;
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) { encoding = new UnicodeEncoding(false, true); skip = 2; }
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) { encoding = new UnicodeEncoding(true, true); skip = 2; }
        else if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) { encoding = new UTF8Encoding(true); skip = 3; }
        else encoding = new UTF8Encoding(false);
        string text = encoding.GetString(bytes, skip, bytes.Length - skip);
        return FromText(text, encoding);
    }

    public static ZemaxCoatingFile FromText(string text, Encoding? encoding = null)
    {
        string newLine = text.Contains("\r\n") ? "\r\n" : text.Contains('\n') ? "\n" : "\r\n";
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        // A file ending in a newline splits to a last empty line; it is put back by Text.
        return new ZemaxCoatingFile(lines, encoding ?? new UTF8Encoding(false), newLine);
    }

    public string Text => string.Join(NewLine, Lines);

    public byte[] ToBytes()
    {
        var body = Encoding.GetBytes(Text);
        var bom = Encoding.GetPreamble();
        return [.. bom, .. body];
    }

    // ---- structure ------------------------------------------------------------------------

    private static string[] Words(string line) => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static bool IsComment(string line) => line.TrimStart().StartsWith('!');

    private static string? KeywordOf(string line)
    {
        var w = Words(line);
        if (w.Length == 0) return null;
        return Keywords.FirstOrDefault(k => k.Equals(w[0], StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Finds every section. A section runs from its keyword line to the next keyword or
    /// comment line, less any blank lines at its end.</summary>
    private void Index()
    {
        var sections = new List<ZemaxSection>();
        for (int i = 0; i < Lines.Count; ++i)
        {
            if (KeywordOf(Lines[i]) is not { } keyword) continue;
            var w = Words(Lines[i]);
            string name = w.Length > 1 ? w[1] : "";
            int end = i + 1;
            while (end < Lines.Count && KeywordOf(Lines[end]) is null && !IsComment(Lines[end])) ++end;
            while (end > i + 1 && Lines[end - 1].Trim().Length == 0) --end;
            int header = i;
            while (header > 0 && Lines[header - 1].TrimStart().StartsWith(Tag, StringComparison.OrdinalIgnoreCase)) --header;
            sections.Add(new ZemaxSection(keyword, name, header, i, end));
        }
        Sections = sections;
    }

    public ZemaxSection? Find(string keyword, string name) =>
        Sections.FirstOrDefault(s => s.Keyword == keyword && s.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>A coating of any kind by that name - COAT, TABLE, IDEAL, IDEAL2 or ENCRYPTED -
    /// which all share one name space in OpticStudio.</summary>
    public ZemaxSection? FindCoating(string name) =>
        Sections.FirstOrDefault(s => s.Keyword is not ("MATE" or "TAPR")
                                     && s.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The names of the coatings that are layer stacks, in file order: COATs with at least
    /// one layer. `COAT I.95` and its kind are an ideal transmission, not a stack.</summary>
    public string[] StackNames() =>
        Sections.Where(s => s.Keyword == "COAT" && s.End > s.Start + 1 && !s.Name.StartsWith("I.", StringComparison.OrdinalIgnoreCase))
                .Select(s => s.Name).ToArray();

    public string[] MaterialNames() => Sections.Where(s => s.Keyword == "MATE").Select(s => s.Name).ToArray();

    // ---- reading a section ----------------------------------------------------------------

    /// <summary>A COAT's layers, or a FormatException naming the line.</summary>
    public ZemaxLayer[] Layers(ZemaxSection coat)
    {
        var layers = new List<ZemaxLayer>();
        for (int i = coat.Start + 1; i < coat.End; ++i)
        {
            var w = Words(Lines[i]);
            if (w.Length == 0) continue;
            if (w.Length < 2 || !Number(w[1], out double t))
                throw new FormatException($"line {i + 1}: a COAT layer is 'material thickness [is_absolute] [loop] [taper]', not '{Lines[i].Trim()}'");
            int absolute = 0, loop = 0;
            if (w.Length > 2 && !int.TryParse(w[2], NumberStyles.Integer, Inv, out absolute))
                throw new FormatException($"line {i + 1}: is_absolute is 0 or 1, not '{w[2]}'");
            if (absolute is not (0 or 1))
                throw new FormatException($"line {i + 1}: is_absolute is 0 (waves) or 1 (µm), not {absolute}");
            if (w.Length > 3 && !int.TryParse(w[3], NumberStyles.Integer, Inv, out loop))
                throw new FormatException($"line {i + 1}: the loop index is a whole number, not '{w[3]}'");
            layers.Add(new ZemaxLayer(w[0], t, absolute == 1, loop, w.Length > 4 ? w[4] : null));
        }
        return layers.ToArray();
    }

    /// <summary>A MATE's rows, [λ µm, n, k] with k as OpticStudio writes it (negative absorbs).</summary>
    public double[][] Rows(ZemaxSection mate)
    {
        var rows = new List<double[]>();
        for (int i = mate.Start + 1; i < mate.End; ++i)
        {
            var w = Words(Lines[i]);
            if (w.Length == 0) continue;
            if (w.Length < 3 || !Number(w[0], out double l) || !Number(w[1], out double n) || !Number(w[2], out double k))
                throw new FormatException($"line {i + 1}: a MATE row is 'wavelength real imaginary', not '{Lines[i].Trim()}'");
            rows.Add([l, n, k]);
        }
        return rows.ToArray();
    }

    /// <summary>The ThinFilmDesigner comment lines above a section, without the tag.</summary>
    public string[] Notes(ZemaxSection s) =>
        Lines.Skip(s.Header).Take(s.Start - s.Header)
             .Select(l => l.TrimStart()[Tag.Length..].Trim()).ToArray();

    /// <summary>Every comment line directly above a section, ours or a person's.</summary>
    public string[] CommentsAbove(ZemaxSection s)
    {
        int i = s.Start;
        while (i > 0 && IsComment(Lines[i - 1])) --i;
        return Lines.Skip(i).Take(s.Start - i).Select(l => l.TrimStart().TrimStart('!').Trim()).ToArray();
    }

    private static bool Number(string s, out double v) => double.TryParse(s, NumberStyles.Float, Inv, out v);

    // ---- names, models and tables ---------------------------------------------------------

    /// <summary>A name OpticStudio accepts: capitals, digits, _ and -, at most 32 characters. Letters
    /// are made capitals, as OpticStudio shows every name (the user, 2026-10-21: "All names are
    /// capitalized"); spaces and anything else become _.</summary>
    public static string Sanitise(string name)
    {
        var s = new StringBuilder();
        foreach (char c in name.Trim())
            s.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? char.ToUpperInvariant(c) : '_');
        var r = s.ToString();
        if (r.Length == 0) r = "MATERIAL";
        return r.Length > 32 ? r[..32] : r;
    }

    /// <summary>The comment lines that carry a material's definition, or none for a TABLE, whose
    /// definition is the table itself.</summary>
    public static string[] ModelNotes(MaterialModel m)
    {
        var notes = new List<string>();
        string kind = m.Kind.ToUpperInvariant();
        if (kind == "TABLE") { }
        else if (kind == "GLASS") notes.Add($"MODEL GLASS {m.Catalog} {m.Glass}");
        else notes.Add($"MODEL {kind} " + string.Join(" ", m.Coefficients.Select(v => v.ToString("R", Inv))));
        if (kind is not ("TABLE" or "GLASS") && m.MinMicrons is { } lo && m.MaxMicrons is { } hi)
            notes.Add($"RANGE {lo.ToString("R", Inv)} {hi.ToString("R", Inv)}");
        if (!string.IsNullOrWhiteSpace(m.Source) && kind != "GLASS") notes.Add($"SOURCE {m.Source.Trim()}");
        return notes.ToArray();
    }

    /// <summary>The model the notes above a MATE describe, or null when they describe none.</summary>
    public static MaterialModel? ModelFrom(string[] notes)
    {
        MaterialModel? model = null;
        double? lo = null, hi = null;
        string? source = null;
        foreach (var note in notes)
        {
            var w = Words(note);
            if (w.Length == 0) continue;
            switch (w[0].ToUpperInvariant())
            {
                case "MODEL" when w.Length >= 2:
                    string kind = w[1].ToUpperInvariant();
                    if (kind == "GLASS" && w.Length >= 4)
                        model = MaterialModel.CatalogueGlass(w[2], string.Join(" ", w.Skip(3)));
                    else if (kind is "CONSTANT" or "SELLMEIER" or "CAUCHY")
                    {
                        var c = new List<double>();
                        foreach (var x in w.Skip(2))
                        {
                            if (!Number(x, out double v)) return null;
                            c.Add(v);
                        }
                        model = new MaterialModel { Kind = kind, Coefficients = c.ToArray() };
                    }
                    break;
                case "RANGE" when w.Length >= 3 && Number(w[1], out double a) && Number(w[2], out double b):
                    (lo, hi) = (a, b);
                    break;
                case "SOURCE":
                    source = note.Trim()[w[0].Length..].Trim();
                    break;
            }
        }
        if (model is null || model.IsCatalogueGlass) return model;
        return model with { MinMicrons = lo, MaxMicrons = hi, Source = source };
    }

    /// <summary>The most rows a MATE is written with. OpticStudio refuses a file with too many
    /// ("Maximum Wavelength-index points exceeded in coating file", the user, 2026-10-21, on a
    /// 451-row Nb2O5) and does not say where the limit is; its own COATING.DAT has materials of 120
    /// rows, so 120 is known to load.</summary>
    public const int MaxRows = 120;

    /// <summary>How close linear interpolation between the rows is asked to come to the material.</summary>
    public const double Tolerance = 1e-5;

    /// <summary>
    /// The rows a material is written as, [λ µm, n, k ≥ 0], between <paramref name="loUm"/> and
    /// <paramref name="hiUm"/>, never more than <see cref="MaxRows"/>; <paramref name="error"/> is
    /// the largest difference in n or k that OpticStudio's linear interpolation between them makes.
    /// A CONSTANT is one row (OpticStudio then uses it at every wavelength). An equation is sampled
    /// where it bends - each interval split at its middle while the middle is off by more than
    /// <see cref="Tolerance"/> - and not past the range it is valid over. A TABLE is its own rows
    /// over the range and one beyond each end, so it is exact inside it, thinned when there are
    /// too many by dropping, one at a time, the row whose loss changes the table least.
    /// </summary>
    public static double[][] Tabulate(MaterialModel m, string name, double loUm, double hiUm, double referenceUm,
                                      out double error)
    {
        error = 0.0;
        string kind = m.Kind.ToUpperInvariant();
        if (kind == "CONSTANT")
        {
            var (n, k, _) = m.At(referenceUm * 1000.0, name);
            return [[Round(referenceUm), n, k]];
        }
        if (kind == "TABLE") return Thin(Within(m.Table!, loUm, hiUm), out error);

        var (min, max) = m.Range();
        if (min is { } a && loUm < a) loUm = a;
        if (max is { } b && hiUm > b) hiUm = b;
        double[] Row(double l) { var (n, k, _) = m.At(l * 1000.0, name); return [l, n, k]; }
        if (!(hiUm > loUm)) return [Row(Round(loUm))];

        // Intervals split at their middles, worst first, until every middle is within tolerance or
        // the rows run out. The worst middle left is the error.
        var rows = new List<double[]> { Row(Round(loUm)), Row(Round(hiUm)) };
        double Miss(int i, out double[] mid)
        {
            mid = Row(Round(0.5 * (rows[i][0] + rows[i + 1][0])));
            return Math.Max(Math.Abs(mid[1] - 0.5 * (rows[i][1] + rows[i + 1][1])),
                            Math.Abs(mid[2] - 0.5 * (rows[i][2] + rows[i + 1][2])));
        }
        while (true)
        {
            int worst = -1;
            double miss = 0.0;
            double[] at = [];
            for (int i = 0; i + 1 < rows.Count; ++i)
            {
                if (!(rows[i + 1][0] - rows[i][0] > 2e-6)) continue;   // no finer than the 1e-6 µm written
                double e = Miss(i, out var mid);
                if (e > miss) (worst, miss, at) = (i, e, mid);
            }
            error = miss;
            if (worst < 0 || miss <= Tolerance || rows.Count >= MaxRows) return rows.ToArray();
            rows.Insert(worst + 1, at);
        }
    }

    /// <summary>A table's rows over [lo, hi] and the one beyond each end, so linear interpolation
    /// inside the range is the table's own.</summary>
    private static double[][] Within(double[][] table, double loUm, double hiUm)
    {
        int first = 0, last = table.Length - 1;
        while (first + 1 < table.Length && table[first + 1][0] <= loUm) ++first;
        while (last - 1 >= 0 && table[last - 1][0] >= hiUm) --last;
        if (first > last) (first, last) = (last, first);
        return table[first..(last + 1)].Select(r => new[] { r[0], r[1], r[2] }).ToArray();
    }

    /// <summary>At most MaxRows of a table: the interior row whose removal moves the interpolated
    /// table least, against every original row it then spans, goes first.</summary>
    private static double[][] Thin(double[][] table, out double error)
    {
        error = 0.0;
        var keep = Enumerable.Range(0, table.Length).ToList();
        double Cost(int j)
        {
            int a = keep[j - 1], b = keep[j + 1];
            double worst = 0.0;
            for (int i = a + 1; i < b; ++i)
            {
                double f = (table[i][0] - table[a][0]) / (table[b][0] - table[a][0]);
                worst = Math.Max(worst, Math.Max(
                    Math.Abs(table[a][1] + f * (table[b][1] - table[a][1]) - table[i][1]),
                    Math.Abs(table[a][2] + f * (table[b][2] - table[a][2]) - table[i][2])));
            }
            return worst;
        }
        while (keep.Count > MaxRows)
        {
            int best = 1;
            double least = double.MaxValue;
            for (int j = 1; j + 1 < keep.Count; ++j)
            {
                double c = Cost(j);
                if (c < least) (best, least) = (j, c);
            }
            keep.RemoveAt(best);
        }
        for (int j = 0; j + 1 < keep.Count; ++j)
        {
            int a = keep[j], b = keep[j + 1];
            for (int i = a + 1; i < b; ++i)
            {
                double f = (table[i][0] - table[a][0]) / (table[b][0] - table[a][0]);
                error = Math.Max(error, Math.Max(
                    Math.Abs(table[a][1] + f * (table[b][1] - table[a][1]) - table[i][1]),
                    Math.Abs(table[a][2] + f * (table[b][2] - table[a][2]) - table[i][2])));
            }
        }
        return keep.Select(i => table[i]).ToArray();
    }

    private static double Round(double um) => Math.Round(um, 6);

    /// <summary>A table as OpticStudio reads it: linear between rows, held at the ends.</summary>
    public static (double N, double K) Interpolate(double[][] rows, double um)
    {
        if (rows.Length == 1 || um <= rows[0][0]) return (rows[0][1], rows[0][2]);
        if (um >= rows[^1][0]) return (rows[^1][1], rows[^1][2]);
        int i = 1;
        while (rows[i][0] < um) ++i;
        double f = (um - rows[i - 1][0]) / (rows[i][0] - rows[i - 1][0]);
        return (rows[i - 1][1] + f * (rows[i][1] - rows[i - 1][1]), rows[i - 1][2] + f * (rows[i][2] - rows[i - 1][2]));
    }

    /// <summary>Whether two tables (k ≥ 0 in both) agree within <paramref name="tolerance"/> in n
    /// and k at every row of either, between <paramref name="loUm"/> and <paramref name="hiUm"/>.</summary>
    public static bool Agree(double[][] a, double[][] b, double loUm, double hiUm, double tolerance = 1e-4)
    {
        var at = a.Concat(b).Select(r => r[0]).Where(l => l >= loUm - 1e-9 && l <= hiUm + 1e-9)
                  .Append(loUm).Append(hiUm).Distinct();
        foreach (var l in at)
        {
            var (na, ka) = Interpolate(a, l);
            var (nb, kb) = Interpolate(b, l);
            if (Math.Abs(na - nb) > tolerance || Math.Abs(ka - kb) > tolerance) return false;
        }
        return true;
    }

    // ---- writing ---------------------------------------------------------------------------

    public static string[] MateLines(string name, string[] notes, double[][] rows)
    {
        var lines = notes.Select(n => $"{Tag} {n}").ToList();
        lines.Add($"MATE {name}");
        foreach (var r in rows)
            lines.Add($"{r[0].ToString("0.0#####", Inv)} {r[1].ToString("0.0######", Inv)} {Imaginary(r[2])}");
        return lines.ToArray();
    }

    /// <summary>k as OpticStudio wants it: negative for an absorber, and plain 0 for none.</summary>
    private static string Imaginary(double k) => k == 0.0 ? "0" : (-k).ToString("G7", Inv);

    /// <param name="source">The design's Description source, written as a comment above the COAT.</param>
    public static string[] CoatLines(string name, double referenceUm, IEnumerable<(string Material, double Microns)> layers,
                                     string? source = null)
    {
        var lines = new List<string> { $"{Tag}: designed at reference wavelength {referenceUm.ToString("0.0#####", Inv)} um" };
        if (!string.IsNullOrWhiteSpace(source)) lines.Add($"{Tag}: source {source.ReplaceLineEndings(" ").Trim()}");
        lines.Add($"COAT {name}");
        var list = layers.ToList();
        int width = Math.Max(8, list.Count == 0 ? 0 : list.Max(l => l.Material.Length) + 2);
        foreach (var (material, um) in list)
            lines.Add($"{material.PadRight(width)}{um.ToString("0.0#########", Inv)} 1");
        return lines.ToArray();
    }

    /// <summary>The reference wavelength a coating says it was designed at, in nm: our comment, or a
    /// person's "designed at 550 nm" or "reference wavelength 0.55 um" directly above the COAT.</summary>
    public double? ReferenceNm(ZemaxSection coat)
    {
        foreach (var c in CommentsAbove(coat))
        {
            var m = ReferencePattern().Match(c);
            if (!m.Success) continue;
            double v = double.Parse(m.Groups[1].Value, Inv);
            string unit = m.Groups[2].Value.ToLowerInvariant();
            bool nm = unit == "nm" || (unit.Length == 0 && v > 50.0);
            double r = nm ? v : v * 1000.0;
            if (r > 0.0) return r;
        }
        return null;
    }

    [GeneratedRegex(@"(?i)(?:designed\s+at|reference\s+wavelength)\D*?([0-9]*\.?[0-9]+(?:[eE][-+]?[0-9]+)?)\s*(nm|um|µm|microns?)?")]
    private static partial Regex ReferencePattern();

    /// <summary>Lines replaced: [from, to) by <paramref name="with"/>. The sections are found again.</summary>
    public void Splice(int from, int to, IReadOnlyList<string> with)
    {
        Lines.RemoveRange(from, to - from);
        Lines.InsertRange(from, with);
        Index();
    }
}
