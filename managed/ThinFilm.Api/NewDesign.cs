// NewDesign.cs — a design started from nothing, rather than from a template.
//
// TEMPLATES ARE TO BE PHASED OUT (the user, 2026-10-01). Until X1.20 a template was the only way to
// get a design with media, materials and a merit function at all; this is the other way. The user
// chooses the media and the coating materials from the material database (MaterialDatabase.cs) -
// each COPIED into the design, never referred to - the stack starts as alternating quarter-waves
// at a reference wavelength, each a quarter-wave in its own material's index there, and the merit
// function starts with ONE placeholder row: not none, because the engine refuses a table with
// nothing scored, and a design that cannot be evaluated is not a starting point.

namespace ThinFilm.Api;

/// <summary>
/// What a new design is made from. Layers alternate through <paramref name="Materials"/> in order,
/// starting at the incident side, each a quarter-wave at <paramref name="ReferenceNm"/>.
/// </summary>
public sealed record NewDesignRequest(
    string Name,
    NamedMaterial Incident,
    NamedMaterial Substrate,
    NamedMaterial[] Materials,
    double ReferenceNm,
    int Layers,
    string WavelengthUnit = "nm");

public static class NewDesigns
{
    /// <summary>The request checked, or the reason it cannot make a design.</summary>
    public static string? Problem(NewDesignRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Name)) return "a design needs a name";
        if (r.Materials.Length == 0) return "choose at least one coating material";
        if (r.Layers < 1) return "a design needs at least one layer";
        if (!(r.ReferenceNm > 0.0) || !double.IsFinite(r.ReferenceNm))
            return "the reference wavelength must be a positive number of nanometres";
        if (r.Materials.Select(m => m.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != r.Materials.Length)
            return "a coating material is chosen twice";

        // Each is evaluated at the reference, so a material that has no index there - or whose
        // definition is broken - is refused now, with its own reason, not at the first evaluation.
        foreach (var m in r.Materials.Append(r.Incident).Append(r.Substrate))
        {
            try { m.Model.At(r.ReferenceNm, m.Name); }
            catch (FormatException e) { return e.Message; }
        }
        // An incident medium with a k is taken, and its k treated as 0 (DesignDocument.IncidentKWarning).
        return null;
    }

    /// <summary>The document for a request, which <see cref="Problem"/> has accepted.</summary>
    public static DesignDocument Document(NewDesignRequest r)
    {
        double[] grid = [r.ReferenceNm];
        MaterialDocument Doc(NamedMaterial m) => MaterialDocument.FromModel(m.Name, m.Model, grid);

        var layers = new LayerDocument[r.Layers];
        for (int i = 0; i < r.Layers; ++i)
        {
            int m = i % r.Materials.Length;
            double n = r.Materials[m].Model.At(r.ReferenceNm, r.Materials[m].Name).N;
            layers[i] = new LayerDocument(m, r.ReferenceNm / (4.0 * n));
        }

        return new DesignDocument
        {
            Name = r.Name.Trim(),
            Notes = "Started from New design. The merit function holds one placeholder row - "
                    + "R = 0 at the reference wavelength - because the engine needs at least one "
                    + "scored row; edit it or replace it.",
            Wavelength = grid,
            Materials = r.Materials.Select(Doc).ToArray(),
            Layers = layers,
            Points = [new PointDocument(0, 0.0)],
            Incident = Doc(r.Incident),
            Exit = Doc(r.Substrate),
            // The placeholder: R equal to zero at the reference, weight 1, and the budget a new
            // "Target" reflectance row gets everywhere else (1% absolute).
            Operands =
            [
                new OperandDocument("RA", "EQUAL", 0.0, 1.0, Scale: 0.01, Points: [0]),
            ],
            Norm = "L2",
            // The ceiling for unconstrained layers, shown in Edit > System Parameters: 600 nm, or the
            // reference wavelength when that is longer, so an infrared design is not held to a
            // visible one's layers.
            UpperBound = Math.Max(600.0, r.ReferenceNm),
            WavelengthUnit = r.WavelengthUnit is "um" ? "um" : "nm",
            // The stack is built of quarter-waves at the reference, so it is shown as one: every
            // layer reads 1 QWOT until the person changes it (Edit > System Parameters).
            ThicknessUnit = LayerUnits.Qwot,
            ReferenceWavelength = r.ReferenceNm,
        };
    }
}
