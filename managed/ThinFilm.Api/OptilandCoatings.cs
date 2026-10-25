// OptilandCoatings.cs — File > Import Optiland coating and File > Export Optiland coating, as API
// calls. The format and how materials travel are in OptilandCoating.cs.

using System.Text.Json.Nodes;

namespace ThinFilm.Api;

/// <summary>A thin-film coating in an Optiland file: its place in the list, what to call it ("coating"
/// for a coating file, "surface 3 (comment)" in a lens file), its layer count, and why it cannot be
/// imported, or null when it can.</summary>
public sealed record OptilandCoatingInfo(int Index, string Name, int Layers, string? Problem);

public sealed record OptilandImportReport(DesignSummary Design, string[] Notes);

public sealed record OptilandExportReport(string Path, int Layers, string[] Notes);

public sealed partial class ThinFilmApi
{
    /// <summary>The thin-film coatings in an Optiland coating file or lens file.</summary>
    public static ApiResult<OptilandCoatingInfo[]> OptilandCoatings(string path)
    {
        if (!TryReadOptiland(path, out var found, out var problem)) return ApiResult<OptilandCoatingInfo[]>.Failure(problem!);
        var grid = (0.4, 0.8);
        return ApiResult<OptilandCoatingInfo[]>.Success(found!.Select((f, i) =>
        {
            var layers = f.Coating["layers"] as JsonArray ?? [];
            string? why = layers.Count == 0 ? "has no layers" : null;
            foreach (var l in layers)
            {
                if (why is not null) break;
                if (l?["material"] is not JsonObject m) { why = "has a layer with no material"; break; }
                why = OptilandCoating.FromOptiland(m, grid).Problem;
            }
            return new OptilandCoatingInfo(i, f.Name, layers.Count, why);
        }).ToArray());
    }

    /// <summary>
    /// The design's layers replaced by an Optiland thin-film coating: <paramref name="index"/>
    /// counts the coatings OptilandCoatings lists. Its materials are added under their Optiland
    /// names - the design's own is used when the name and data agree, otherwise the file's is
    /// NAME_2 - and the media, wavelengths and merit function are kept. A reference wavelength
    /// ThinFilmDesigner wrote with the coating becomes the design's.
    /// </summary>
    public ApiResult<OptilandImportReport> ImportOptilandCoating(string id, string path, int index)
    {
        if (!Find(id, out var s, out var fail)) return ApiResult<OptilandImportReport>.Failure(fail!);
        if (!TryReadOptiland(path, out var found, out var problem)) return ApiResult<OptilandImportReport>.Failure(problem!);
        if (index < 0 || index >= found!.Count)
            return ApiResult<OptilandImportReport>.Failure($"{Path.GetFileName(path)} has {found!.Count} thin-film coating{(found.Count == 1 ? "" : "s")}; there is no number {index}");

        var doc = Current(s);
        var coating = found[index].Coating;
        var layers = coating["layers"] as JsonArray ?? [];
        if (layers.Count == 0) return ApiResult<OptilandImportReport>.Failure($"{found[index].Name} has no layers");
        var grid = (doc.Wavelength.Min() / 1000.0, doc.Wavelength.Max() / 1000.0);

        var notes = new List<string>();
        var materials = doc.Materials.ToList();
        var stack = new List<LayerDocument>();
        var seen = new Dictionary<string, int>();
        try
        {
            foreach (var layer in layers)
            {
                if (layer?["material"] is not JsonObject m || layer["thickness_nm"] is not JsonValue tv || !tv.TryGetValue(out double nm))
                    return ApiResult<OptilandImportReport>.Failure($"{found[index].Name}: a layer without a material or a thickness_nm");
                string key = m.ToJsonString();
                if (!seen.TryGetValue(key, out int at))
                {
                    var read = OptilandCoating.FromOptiland(m, grid);
                    if (read.Problem is not null) return ApiResult<OptilandImportReport>.Failure(read.Problem);
                    if (read.Note is not null) notes.Add(read.Note);
                    var incoming = MaterialDocument.FromModel(read.Name, read.Model!, doc.Wavelength);
                    at = -1;
                    for (int k = 1; at < 0; ++k)
                    {
                        string chosen = k == 1 ? read.Name : $"{read.Name}_{k}";
                        int same = materials.FindIndex(x => x.Name.Equals(chosen, StringComparison.OrdinalIgnoreCase));
                        if (same < 0) { materials.Add(incoming with { Name = chosen }); at = materials.Count - 1; }
                        else if (SameOnGrid(materials[same], incoming)) at = same;
                        if (at >= 0 && k > 1) notes.Add($"the design already has a different {read.Name}; the file's is {chosen} in the design");
                    }
                    seen[key] = at;
                }
                stack.Add(new LayerDocument(at, nm) { Unit = doc.ThicknessUnit == LayerUnits.Nm ? null : LayerUnits.Nm });
            }
        }
        catch (FormatException e) { return ApiResult<OptilandImportReport>.Failure(e.Message); }

        double? reference = coating["thinfilm"]?["reference_nm"] is JsonValue rv && rv.TryGetValue(out double r) && r > 0 ? r : null;
        notes.Add($"the coating's own media ({Medium(coating["material_pre"], grid)} and {Medium(coating["material_post"], grid)}) are not used; the design keeps its own");
        var next = doc with { Materials = materials.ToArray(), Layers = stack.ToArray(), ReferenceWavelength = reference ?? doc.ReferenceWavelength };
        var replaced = ReplaceLayers(s, next);
        if (!replaced.Ok) return ApiResult<OptilandImportReport>.Failure(replaced.Error!, replaced.Status);
        return ApiResult<OptilandImportReport>.Success(new OptilandImportReport(replaced.Value!, notes.ToArray()));

        static string Medium(JsonNode? m, (double, double) grid) =>
            m is JsonObject o ? OptilandCoating.FromOptiland(o, grid).Name : "none given";
    }

    /// <summary>
    /// The design as an Optiland coating file, which Optiland reads with
    /// load_obj_from_json(BaseCoating, path): a ThinFilmCoating with the design's incident medium
    /// and substrate as its media, its layers incident side first in nm, and its materials as
    /// equations or tables (OptilandCoating.ToOptiland). The reference wavelength and the design's
    /// name go in a "thinfilm" entry Optiland ignores. The file is written whole.
    /// </summary>
    public ApiResult<OptilandExportReport> ExportOptilandCoating(string id, string path)
    {
        if (!Find(id, out var s, out var fail)) return ApiResult<OptilandExportReport>.Failure(fail!);
        var doc = Current(s);
        if (doc.Layers.Length == 0) return ApiResult<OptilandExportReport>.Failure("the design has no layers to export");
        var notes = new List<string>();
        try
        {
            MaterialModel Model(MaterialDocument m) => m.Model ?? MaterialModel.FromSamples(doc.Wavelength, m.N, m.K);
            var written = doc.Materials.Select(m => OptilandCoating.ToOptiland(m.Name, Model(m))).ToArray();
            var coating = new JsonObject
            {
                ["type"] = "ThinFilmCoating",
                ["material_pre"] = OptilandCoating.ToOptiland(doc.Incident.Name, Model(doc.Incident)),
                ["material_post"] = OptilandCoating.ToOptiland(doc.Exit.Name, Model(doc.Exit)),
                ["layers"] = new JsonArray(doc.Layers.Select((l, i) => (JsonNode)new JsonObject
                {
                    ["material"] = written[l.Material].DeepClone(),
                    ["thickness_nm"] = l.Thickness,
                    ["name"] = $"{i + 1}",
                }).ToArray()),
                ["thinfilm"] = new JsonObject
                {
                    ["design"] = doc.Name,
                    ["reference_nm"] = doc.ReferenceWavelength,
                    ["source"] = doc.Source,
                    ["notes"] = doc.Notes,
                    ["note"] = "written by ThinFilmDesigner; Optiland ignores this entry",
                },
            };
            if (doc.Incident.K.Any(k => k > 0))
                notes.Add($"the incident medium {doc.Incident.Name} is written with its k; this program treats it as 0, Optiland may not");
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, OptilandCoating.Write(coating));
            File.Move(temporary, path, overwrite: true);
        }
        catch (FormatException e) { return ApiResult<OptilandExportReport>.Failure(e.Message); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { return ApiResult<OptilandExportReport>.Failure($"could not write {path}: {e.Message}"); }
        return ApiResult<OptilandExportReport>.Success(new OptilandExportReport(path, doc.Layers.Length, notes.ToArray()));
    }

    private static bool TryReadOptiland(string path, out List<OptilandCoating.Found>? found, out string? problem)
    {
        found = null;
        problem = null;
        try
        {
            found = OptilandCoating.Coatings(OptilandCoating.Parse(File.ReadAllText(path)));
            if (found.Count == 0) { problem = $"{Path.GetFileName(path)} has no thin-film coating (an Optiland ThinFilmCoating)"; return false; }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { problem = $"could not read {path}: {e.Message}"; return false; }
        catch (System.Text.Json.JsonException e)
        { problem = $"{Path.GetFileName(path)} is not an Optiland file: {e.Message}"; return false; }
    }
}
