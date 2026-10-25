// ZemaxCoatings.cs — File > Import ZEMAX coating and File > Export to ZEMAX coating, as API calls.
// The file format, and the rules for what is kept and what is changed, are in ZemaxCoating.cs.

namespace ThinFilm.Api;

/// <summary>A coating in a file: its name, layer count, the reference wavelength it says it was
/// designed at (nm, or null), and why it cannot be imported, or null when it can.</summary>
public sealed record ZemaxCoatingInfo(string Name, int Layers, double? ReferenceNm, string? Problem);

public sealed record ZemaxImportReport(DesignSummary Design, string[] Notes);

/// <summary>What Export writes. Coating defaults to the design's name; From and To (nm) to the
/// design's wavelength range; Replace must be true to overwrite a COAT of the same name.</summary>
public sealed record ZemaxExportRequest(string Path, string? Coating = null, double? FromNm = null,
                                        double? ToNm = null, bool Replace = false);

/// <summary>What Export did: the COAT written (and whether it replaced one), each design material
/// and the MATE it went to, what was added, and what a person should know.</summary>
public sealed record ZemaxExportReport(string Coating, bool Replaced, Dictionary<string, string> Materials,
                                       string[] Added, string[] Notes, string? Backup);

public sealed partial class ThinFilmApi
{
    /// <summary>Status of an export refused because the file already has a COAT of that name.</summary>
    public const string CoatingExists = "COATING_EXISTS";

    /// <summary>The layer-stack coatings in a coating file, in file order.</summary>
    public static ApiResult<ZemaxCoatingInfo[]> ZemaxCoatings(string path)
    {
        if (!TryRead(path, out var file, out var problem)) return ApiResult<ZemaxCoatingInfo[]>.Failure(problem!);
        var list = new List<ZemaxCoatingInfo>();
        foreach (var name in file!.StackNames())
        {
            var coat = file.Find("COAT", name)!;
            string? why = null;
            int count = 0;
            try
            {
                var layers = file.Layers(coat);
                count = layers.Length;
                if (layers.Any(l => l.Loop != 0)) why = "uses a loop index, which import does not expand";
                else if (layers.Select(l => l.Material).FirstOrDefault(m => file.Find("MATE", m) is null) is { } missing)
                    why = $"uses {missing}, which has no MATE in this file";
            }
            catch (FormatException e) { why = e.Message; }
            list.Add(new ZemaxCoatingInfo(name, count, file.ReferenceNm(coat), why));
        }
        return ApiResult<ZemaxCoatingInfo[]>.Success(list.ToArray());
    }

    /// <summary>
    /// The design's layers replaced by a coating from a file. Its materials are added under their
    /// names in the file - or the design's own material is used, when it has the same name and data
    /// - each as the equation its ThinFilmDesigner comment names if that still reproduces its table,
    /// otherwise as the table. A coating in waves is counted at the reference wavelength the file
    /// gives, or the design's. The media, wavelengths and merit function are kept.
    /// </summary>
    public ApiResult<ZemaxImportReport> ImportZemaxCoating(string id, string path, string coating)
    {
        if (!Find(id, out var s, out var fail)) return ApiResult<ZemaxImportReport>.Failure(fail!);
        if (!TryRead(path, out var file, out var problem)) return ApiResult<ZemaxImportReport>.Failure(problem!);
        string fileName = Path.GetFileName(path);

        // As export names it: "Si photodiode AR" was written as SI_PHOTODIODE_AR.
        var coat = file!.Find("COAT", coating) ?? file.Find("COAT", ZemaxCoatingFile.Sanitise(coating));
        if (coat is null)
            return ApiResult<ZemaxImportReport>.Failure(file.FindCoating(coating) is { } other
                ? $"{other.Name} is a {other.Keyword} coating, defined by its effect rather than by layers, so it cannot be imported"
                : $"{fileName} has no coating called {coating}");
        var doc = Current(s);
        var notes = new List<string>();
        try
        {
            var layers = file.Layers(coat);
            if (layers.Length == 0)
                return ApiResult<ZemaxImportReport>.Failure($"{coat.Name} has no layers");
            if (layers.Any(l => l.Loop != 0))
                return ApiResult<ZemaxImportReport>.Failure(
                    $"{coat.Name} uses a loop index, which import does not expand; write its layers out in full");
            if (layers.Any(l => l.Taper is not null))
                notes.Add($"{coat.Name} is tapered across the surface; its layers are taken at their nominal thickness");

            var materials = doc.Materials.ToList();
            var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in layers.Select(l => l.Material).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var mate = file.Find("MATE", name);
                if (mate is null)
                    return ApiResult<ZemaxImportReport>.Failure($"{coat.Name} uses {name}, which has no MATE in {fileName}");
                var (model, note) = ModelOf(file, mate, fileName);
                if (note is not null) notes.Add(note);

                var incoming = MaterialDocument.FromModel(mate.Name, model, doc.Wavelength);
                string chosen = mate.Name;
                // A material this program exported: SiO2 that had to be written as SiO2_2 comes back
                // as the design's SiO2 when the data still agree.
                int at = materials.FindIndex(m => Exported(m.Name, mate.Name) && SameOnGrid(m, incoming));
                if (at >= 0) chosen = materials[at].Name;
                for (int k = 1; at < 0; ++k)
                {
                    chosen = k == 1 ? mate.Name : $"{mate.Name}_{k}";
                    int same = materials.FindIndex(m => m.Name.Equals(chosen, StringComparison.OrdinalIgnoreCase));
                    if (same < 0)
                    {
                        materials.Add(incoming with { Name = chosen });
                        at = materials.Count - 1;
                    }
                    else if (SameOnGrid(materials[same], incoming)) at = same;
                }
                if (!chosen.Equals(mate.Name, StringComparison.OrdinalIgnoreCase) && !Exported(chosen, mate.Name))
                    notes.Add($"the design already has a different {mate.Name}; the file's is {chosen} in the design");
                index[name] = at;
            }

            double? reference = file.ReferenceNm(coat);
            if (layers.Any(l => !l.Absolute))
                notes.Add(reference is { } r
                    ? $"thicknesses in waves are counted at {r:0.###} nm, which the file says it was designed at"
                    : $"thicknesses in waves are counted at {doc.ReferenceWavelength:0.###} nm, the design's reference; the file does not say");
            var next = doc with { Materials = materials.ToArray(), ReferenceWavelength = reference ?? doc.ReferenceWavelength };

            var stack = layers.Select(l =>
            {
                int m = index[l.Material];
                if (l.Absolute)
                    return new LayerDocument(m, l.Thickness * 1000.0) { Unit = next.ThicknessUnit == LayerUnits.Nm ? null : LayerUnits.Nm };
                // A wave of optical thickness is 4 QWOT.
                double qwot = 4.0 * l.Thickness;
                return new LayerDocument(m, qwot / LayerUnits.PerNm(next, m, LayerUnits.Qwot))
                { Unit = next.ThicknessUnit == LayerUnits.Qwot ? null : LayerUnits.Qwot };
            }).ToArray();

            var r2 = ReplaceLayers(s, next with { Layers = stack });
            if (!r2.Ok) return ApiResult<ZemaxImportReport>.Failure(r2.Error!, r2.Status);
            return ApiResult<ZemaxImportReport>.Success(new ZemaxImportReport(r2.Value!, notes.ToArray()));
        }
        catch (FormatException e) { return ApiResult<ZemaxImportReport>.Failure(e.Message); }
    }

    /// <summary>
    /// The design written into a coating file as one COAT and the MATEs it needs, leaving every
    /// other line as it was; a new file when there is none. A backup of an existing file is kept
    /// beside it as .bak. Refused with status <see cref="CoatingExists"/> when the file already has
    /// that COAT and Replace is false.
    /// </summary>
    public ApiResult<ZemaxExportReport> ExportZemaxCoating(string id, ZemaxExportRequest request)
    {
        if (!Find(id, out var s, out var fail)) return ApiResult<ZemaxExportReport>.Failure(fail!);
        var doc = Current(s);
        if (doc.Layers.Length == 0) return ApiResult<ZemaxExportReport>.Failure("the design has no layers to export");

        bool exists = File.Exists(request.Path);
        ZemaxCoatingFile file;
        if (exists)
        {
            if (!TryRead(request.Path, out var read, out var problem)) return ApiResult<ZemaxExportReport>.Failure(problem!);
            file = read!;
        }
        else file = ZemaxCoatingFile.Empty();

        string coatName = ZemaxCoatingFile.Sanitise(request.Coating ?? doc.Name);
        if (coatName.StartsWith("I.", StringComparison.OrdinalIgnoreCase))
            return ApiResult<ZemaxExportReport>.Failure("a coating name starting I. is an ideal coating in OpticStudio; choose another");
        var existing = file.FindCoating(coatName);
        if (existing is not null && existing.Keyword != "COAT")
            return ApiResult<ZemaxExportReport>.Failure(
                $"{Path.GetFileName(request.Path)} already has a {existing.Keyword} coating called {existing.Name}; choose another name");
        if (existing is not null && !request.Replace)
            return ApiResult<ZemaxExportReport>.Failure(
                $"{Path.GetFileName(request.Path)} already has a coating called {existing.Name}", CoatingExists);

        double lo = (request.FromNm ?? doc.Wavelength.Min()) / 1000.0;
        double hi = (request.ToNm ?? doc.Wavelength.Max()) / 1000.0;
        if (!(lo > 0.0) || !(hi >= lo))
            return ApiResult<ZemaxExportReport>.Failure("the table range needs a start above 0 and an end at or after it");
        double referenceUm = doc.ReferenceWavelength / 1000.0;

        var notes = new List<string>();
        var nameOf = new Dictionary<int, string>();
        var added = new List<(string Name, string[] Lines, double[][] Rows)>();
        try
        {
            foreach (int i in doc.Layers.Select(l => l.Material).Distinct())
            {
                var m = doc.Materials[i];
                var model = m.Model ?? MaterialModel.FromSamples(doc.Wavelength, m.N, m.K);
                var rows = ZemaxCoatingFile.Tabulate(model, m.Name, lo, hi, referenceUm, out double error);
                if (error > ZemaxCoatingFile.Tolerance)
                    notes.Add($"{m.Name} is written as {rows.Length} rows, the most OpticStudio is known to take; "
                              + $"between them OpticStudio's interpolation is within {error:G2} of it in n and k");
                string stem = ZemaxCoatingFile.Sanitise(m.Name);
                string chosen = stem;
                for (int k = 1; ; ++k)
                {
                    string suffix = k == 1 ? "" : $"_{k}";
                    chosen = (stem.Length + suffix.Length > 32 ? stem[..(32 - suffix.Length)] : stem) + suffix;
                    var pending = added.FirstOrDefault(a => a.Name.Equals(chosen, StringComparison.OrdinalIgnoreCase));
                    if (pending.Name is not null)
                    {
                        if (ZemaxCoatingFile.Agree(rows, pending.Rows, lo, hi)) break;
                        continue;
                    }
                    var mate = file.Find("MATE", chosen);
                    if (mate is null)
                    {
                        added.Add((chosen, ZemaxCoatingFile.MateLines(chosen, ZemaxCoatingFile.ModelNotes(model), rows), rows));
                        break;
                    }
                    var theirs = file.Rows(mate).Select(r => new[] { r[0], r[1], -r[2] }).OrderBy(r => r[0]).ToArray();
                    if (theirs.Length > 0 && theirs.All(r => r[2] >= 0.0) && ZemaxCoatingFile.Agree(rows, theirs, lo, hi))
                    {
                        chosen = mate.Name;
                        break;
                    }
                }
                if (!chosen.Equals(stem, StringComparison.OrdinalIgnoreCase))
                    notes.Add($"{stem} in the file has other data and is left as it is; {m.Name} is written as {chosen}");
                if (rows.Length == 1 && model.Kind.ToUpperInvariant() is not ("TABLE" or "CONSTANT"))
                    notes.Add($"{m.Name} is written at one wavelength, so OpticStudio will use it as a constant index");
                // PAST ITS DATA OpticStudio holds the last row, with nothing to say so (X1.77): Si3N4's
                // table ends at 826.6 nm, and an export to 1100 nm was silent about it.
                if (model.Kind.ToUpperInvariant() != "CONSTANT" && model.Range() is var (vlo, vhi))
                {
                    static string Nm(double um) => (um * 1000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
                    var past = new List<string>();
                    if (vlo is { } a && lo < a - 1e-9) past.Add($"below {Nm(a)} nm");
                    if (vhi is { } b && hi > b + 1e-9) past.Add($"above {Nm(b)} nm");
                    if (past.Count > 0)
                        notes.Add($"{m.Name} has data only from {(vlo is { } c ? Nm(c) : "-")} to "
                                  + $"{(vhi is { } d ? Nm(d) : "-")} nm; {string.Join(" and ", past)} OpticStudio will hold its end values");
                }
                nameOf[i] = chosen;
            }
        }
        catch (FormatException e) { return ApiResult<ZemaxExportReport>.Failure(e.Message); }

        // The COAT first, then the MATEs before it: each splice re-finds the sections.
        var coatLines = ZemaxCoatingFile.CoatLines(coatName, referenceUm,
            doc.Layers.Select(l => (nameOf[l.Material], l.Thickness / 1000.0)), doc.Source);
        if (existing is not null) file.Splice(existing.Header, existing.End, coatLines);
        else Append(file, coatLines);

        if (added.Count > 0)
        {
            var block = new List<string>();
            foreach (var a in added) { block.Add(""); block.AddRange(a.Lines); }
            var lastMate = file.Sections.LastOrDefault(x => x.Keyword == "MATE");
            if (lastMate is not null) file.Splice(lastMate.End, lastMate.End, block);
            else
            {
                var first = file.Sections.First();   // there is one: the COAT just written
                block.RemoveAt(0);
                block.Add("");
                file.Splice(first.Header, first.Header, block);
            }
        }

        string? backup = null;
        try
        {
            if (exists)
            {
                backup = request.Path + ".bak";
                File.Copy(request.Path, backup, overwrite: true);
            }
            string temporary = request.Path + ".tmp";
            File.WriteAllBytes(temporary, file.ToBytes());
            File.Move(temporary, request.Path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { return ApiResult<ZemaxExportReport>.Failure($"could not write {request.Path}: {e.Message}"); }

        var map = doc.Layers.Select(l => l.Material).Distinct()
                     .ToDictionary(i => doc.Materials[i].Name, i => nameOf[i]);
        return ApiResult<ZemaxExportReport>.Success(new ZemaxExportReport(
            coatName, existing is not null, map, added.Select(a => a.Name).ToArray(), notes.ToArray(), backup));
    }

    private static void Append(ZemaxCoatingFile file, string[] lines)
    {
        var l = file.Lines;
        bool trailingNewline = l.Count > 0 && l[^1].Length == 0;
        int at = trailingNewline ? l.Count - 1 : l.Count;
        var block = new List<string>();
        if (at > 0 && l[at - 1].Trim().Length > 0) block.Add("");
        block.AddRange(lines);
        // A file that did not end in a newline still does not.
        file.Splice(at, at, block);
    }

    private static bool TryRead(string path, out ZemaxCoatingFile? file, out string? problem)
    {
        file = null;
        problem = null;
        try { file = ZemaxCoatingFile.Read(path); return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { problem = $"could not read {path}: {e.Message}"; return false; }
    }

    /// <summary>A MATE as a material: the equation its comment names when that still gives its
    /// table, else the table; and a note when the comment was set aside.</summary>
    private static (MaterialModel Model, string? Note) ModelOf(ZemaxCoatingFile file, ZemaxSection mate, string fileName)
    {
        var raw = file.Rows(mate);
        if (raw.Length == 0) throw new FormatException($"MATE {mate.Name} has no rows");
        // A positive imaginary part is gain in OpticStudio. OpticStudio's own sample file has a few
        // of order 1e-7 and below, which are rounding, and are read as 0 with a note; a larger one
        // is more likely a sign written the other way round, and is refused rather than guessed at.
        string? note = null;
        if (raw.Where(r => r[2] > 0.0).OrderByDescending(r => r[2]).FirstOrDefault() is { } gain)
        {
            if (gain[2] >= 1e-3)
                throw new FormatException(
                    $"MATE {mate.Name} has a positive imaginary part ({gain[2]:G4} at {gain[0]:0.####} µm), which OpticStudio "
                    + "treats as gain; an absorber's is negative there");
            note = $"MATE {mate.Name} has positive imaginary parts up to {gain[2]:G2}, gain in OpticStudio; they are read as k = 0";
        }
        var rows = raw.Select(r => new[] { r[0], r[1], Math.Max(0.0, -r[2]) })
                      .GroupBy(r => r[0]).Select(g => g.First()).OrderBy(r => r[0]).ToArray();
        string source = $"{fileName}, MATE {mate.Name}";
        var table = rows.Length == 1
            ? MaterialModel.Constant(rows[0][1], rows[0][2]) with { Source = source }
            : new MaterialModel { Kind = "TABLE", Table = rows, Source = source };

        var described = ZemaxCoatingFile.ModelFrom(file.Notes(mate));
        if (described is null) return (table, note);
        try
        {
            bool reproduces = rows.All(r =>
            {
                var (n, k, _) = described.At(r[0] * 1000.0, mate.Name);
                return Math.Abs(n - r[1]) <= 1e-4 && Math.Abs(k - r[2]) <= 1e-4;
            });
            if (reproduces) return (described, note);
            return (table, Also(note, $"{mate.Name}'s table no longer matches the {described.Kind} model in its comment, so the table is used"));
        }
        catch (FormatException e)
        {
            return (table, Also(note, $"{mate.Name}: the {described.Kind} model in its comment cannot be used ({e.Message}), so the table is used"));
        }
    }

    /// <summary>Whether a MATE name is what Export would write for a material of this name: the name
    /// made safe, perhaps with _2, _3, ... after it.</summary>
    private static bool Exported(string material, string mate)
    {
        string stem = ZemaxCoatingFile.Sanitise(material);
        if (mate.Equals(stem, StringComparison.OrdinalIgnoreCase)) return true;
        int bar = mate.LastIndexOf('_');
        return bar > 0 && int.TryParse(mate[(bar + 1)..], out int k) && k >= 2
               && stem.StartsWith(mate[..bar], StringComparison.OrdinalIgnoreCase)
               && (mate[..bar].Length == stem.Length || mate.Length == 32);
    }

    private static string Also(string? first, string second) => first is null ? second : $"{first}; {second}";

    private static bool SameOnGrid(MaterialDocument a, MaterialDocument b)
    {
        for (int i = 0; i < a.N.Length; ++i)
            if (Math.Abs(a.N[i] - b.N[i]) > 1e-4 || Math.Abs(a.K[i] - b.K[i]) > 1e-4) return false;
        return true;
    }
}
