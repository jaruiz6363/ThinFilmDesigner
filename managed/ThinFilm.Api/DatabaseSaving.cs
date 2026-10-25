// DatabaseSaving.cs — a material put into the person's own material database, for the next design.
//
// THE USER, 2026-10-25, on Edit > Materials: a material made in a design (a paper's Sellmeier
// coefficients, a measured n,k table) stayed in that design, and the only way to reuse it was to
// edit the database file by hand. Save to database writes it into the person's own database - the
// one System Parameters names, or their working copy - and never the shipped one. A design still
// copies a material in when it takes it, so saving changes no design already made.

namespace ThinFilm.Api;

/// <summary>What Save to database did: the file written, and whether a material was replaced.</summary>
public sealed record DatabaseSaveReport(string Path, string Name, bool Replaced);

public sealed partial class ThinFilmApi
{
    /// <summary>
    /// <paramref name="material"/> written into the person's material database: added, or with
    /// <paramref name="replace"/> in place of the material of the same name, which is otherwise
    /// refused with its name so the caller can ask. Edit > Materials calls this with the material
    /// as the dialog holds it, before OK.
    /// </summary>
    public ApiResult<DatabaseSaveReport> SaveToDatabase(NamedMaterial material, bool replace = false)
    {
        var database = MaterialDatabase.ForPerson(out var problem);
        // Unreadable, ForPerson falls back on the shipped database, which is never written.
        if (problem is not null) return ApiResult<DatabaseSaveReport>.Failure(problem);
        string? path = database.Path;
        if (path is null) return ApiResult<DatabaseSaveReport>.Failure("there is no material database file to write to");
        bool had = database.Find(material.Name.Trim()) is not null;
        if (MaterialDatabase.Save(path, material, replace, out var refused) is null)
            return ApiResult<DatabaseSaveReport>.Failure(refused!);
        return ApiResult<DatabaseSaveReport>.Success(new DatabaseSaveReport(path, material.Name.Trim(), had));
    }

    /// <summary>
    /// A design's own material saved to the database, by name: a coating material, or the incident
    /// medium or substrate when it is named so. The CLI and the MCP server call this.
    /// </summary>
    public ApiResult<DatabaseSaveReport> SaveToDatabase(string id, string materialName, bool replace = false)
    {
        var set = MaterialSet(id);
        if (!set.Ok) return ApiResult<DatabaseSaveReport>.Failure(set.Error!, set.Status);
        var s = set.Value!;
        var m = s.Materials.Concat([s.Incident, s.Substrate])
                 .FirstOrDefault(x => string.Equals(x.Name, materialName.Trim(), StringComparison.OrdinalIgnoreCase));
        if (m is null)
            return ApiResult<DatabaseSaveReport>.Failure(
                $"the design has no material called '{materialName}'; it has {string.Join(", ", s.Materials.Select(x => x.Name))}, "
                + $"on {s.Substrate.Name} in {s.Incident.Name}");
        return SaveToDatabase(m, replace);
    }
}
