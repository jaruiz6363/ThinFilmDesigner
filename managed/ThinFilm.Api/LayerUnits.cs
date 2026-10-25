// LayerUnits.cs — layer thicknesses in nm, QWOT or FWOT, and pickups between layers.
//
// THE ENGINE WORKS IN NANOMETRES, AND SO DOES THE DOCUMENT. LayerDocument.Thickness is always the
// physical thickness; a unit is how a person reads and types it. A quarter-wave optical thickness
// (QWOT) at the reference wavelength is 4 n d / lambda_ref, a full-wave one (FWOT) n d / lambda_ref,
// with n the real index of the layer's own material at the reference. So 1 QWOT of TiO2 at 550 nm
// is about 58.5 nm and 1 QWOT of SiO2 about 94.2 nm.
//
// WHAT A CHANGE KEEPS, as the user decided (2026-10-16):
//   - Changing a UNIT keeps the coating: the same nanometres, read in another unit.
//   - Changing the REFERENCE WAVELENGTH keeps the QWOT and FWOT numbers, so those layers grow or
//     shrink and the spectrum moves - which is how a designer shifts a design. A layer in nm does
//     not move. To keep the spectrum instead, switch to nm first, then back at the new reference.
//   - Changing a layer's MATERIAL (or its dispersion) keeps its QWOT or FWOT number too: a
//     quarter-wave of SiO2 replaced by Ta2O5 is a quarter-wave of Ta2O5.
//
// PICKUPS: child = Scale x parent + Offset, in the unit both layers share. The user's rule is that
// the two MUST share a unit - 1 QWOT of SiO2 picked up with scale 3 is 3 QWOT of TiO2, 100 nm
// picked up with scale 0.5 is 50 nm - so any change that would leave them in different units is
// refused, naming the pickup. A pickup names a free layer, never another picked-up one.
//
// BOUNDS (the user, 2026-10-16): a layer may be unconstrained, between a minimum and a maximum, at
// least a minimum or at most a maximum. They are thicknesses, so they are kept as the thickness
// is - in nanometres, shown in the layer's unit - and a change that keeps a QWOT number keeps the
// bounds' QWOT numbers too: 0.5 to 2 QWOT stays 0.5 to 2 QWOT at a new reference.
//
// UNCONSTRAINED, as the user decided (2026-10-16): no minimum but zero - no floor the person did not
// set - and, above, the design's MAXIMUM FOR UNCONSTRAINED LAYERS (DesignDocument.UpperBound),
// shown and set in Edit > System Parameters. That ceiling has to exist for now: refine scales each step
// by the merit's sensitivity to the layer, which is near zero at a flat start (a quarter-wave stack
// at its own reference), and without a ceiling one step then runs to a thick layer in another basin.
// A step limit that fixes this properly changed the templates' results both ways and is left for
// later. A layer whose own minimum is above the ceiling has asked to be thick, so has no ceiling.
// A picked-up layer follows its parent, so its bounds are kept but not used.

namespace ThinFilm.Api;

/// <summary>A layer's thickness picked up from another layer: this layer = Scale x layer Parent +
/// Offset, in the unit both layers use. Parent counts from 0, as layers do in the API.</summary>
public sealed record LayerPickup(int Parent, double Scale = 1.0, double Offset = 0.0);

public static class LayerUnits
{
    public const string Nm = "nm", Qwot = "QWOT", Fwot = "FWOT";

    public static readonly string[] All = [Nm, Qwot, Fwot];

    /// <summary>"nm", "QWOT" or "FWOT" from what a person typed, or null if it is none of them.</summary>
    public static string? Parse(string? unit) => unit?.Trim().ToUpperInvariant() switch
    {
        "NM" => Nm,
        "QWOT" or "QW" => Qwot,
        "FWOT" or "FW" or "FWT" => Fwot,
        _ => null,
    };

    /// <summary>The unit layer <paramref name="layer"/> is shown in: its own, or the design's.</summary>
    public static string UnitOf(DesignDocument doc, int layer) =>
        doc.Layers[layer].Unit ?? doc.ThicknessUnit;

    /// <summary>
    /// How many of <paramref name="unit"/> one nanometre of <paramref name="material"/> is, at the
    /// design's reference wavelength. Throws FormatException, saying why, when the material has no
    /// usable index there (a glass whose catalogue is missing, or n not positive).
    /// </summary>
    public static double PerNm(DesignDocument doc, int material, string unit)
    {
        if (unit == Nm) return 1.0;
        var m = doc.Materials[material];
        double reference = doc.ReferenceWavelength;
        double n = (m.Model ?? MaterialModel.FromSamples(doc.Wavelength, m.N, m.K)).At(reference, m.Name).N;
        if (!(n > 0.0) || !double.IsFinite(n))
            throw new FormatException($"{m.Name} has no positive index at the reference wavelength "
                                      + $"({reference:0.###} nm), so it cannot be given in {unit}");
        return unit == Qwot ? 4.0 * n / reference : n / reference;
    }

    /// <summary>Layer <paramref name="layer"/>'s thickness in its own unit.</summary>
    public static double ValueOf(DesignDocument doc, int layer) =>
        doc.Layers[layer].Thickness * PerNm(doc, doc.Layers[layer].Material, UnitOf(doc, layer));

    /// <summary>Every layer's thickness in its own unit; NaN for one that cannot be converted.</summary>
    public static double[] Values(DesignDocument doc) =>
        doc.Layers.Select((_, i) =>
        {
            try { return ValueOf(doc, i); }
            catch (FormatException) { return double.NaN; }
        }).ToArray();

    /// <summary>
    /// <paramref name="next"/> with every QWOT and FWOT layer given back the number it had in
    /// <paramref name="before"/>: what a change of reference wavelength or of material keeps. Both
    /// documents have the same layers in the same order. Pickups are applied after.
    /// </summary>
    public static DesignDocument KeepOpticalThickness(DesignDocument before, DesignDocument next)
    {
        var layers = next.Layers.ToArray();
        for (int i = 0; i < layers.Length; ++i)
        {
            string unit = UnitOf(next, i);
            if (unit == Nm || UnitOf(before, i) != unit) continue;
            // The same number in the new index or at the new reference: thickness and bounds alike.
            double factor = PerNm(before, before.Layers[i].Material, unit) / PerNm(next, layers[i].Material, unit);
            layers[i] = layers[i] with
            {
                Thickness = before.Layers[i].Thickness * factor,
                Min = before.Layers[i].Min * factor,
                Max = before.Layers[i].Max * factor,
            };
        }
        return ApplyPickups(next with { Layers = layers });
    }

    /// <summary>Every picked-up layer set from its parent, in their shared unit.</summary>
    public static DesignDocument ApplyPickups(DesignDocument doc)
    {
        if (!doc.Layers.Any(l => l.Pickup is not null)) return doc;
        var (parent, scale, offset) = Ties(doc);
        var layers = doc.Layers.ToArray();
        for (int c = 0; c < layers.Length; ++c)
            if (parent[c] >= 0)
                layers[c] = layers[c] with
                {
                    Thickness = Math.Max(0.0, scale[c] * layers[parent[c]].Thickness + offset[c]),
                };
        return doc with { Layers = layers };
    }

    /// <summary>
    /// The pickups as the engine's ties: thickness[c] = scale[c] x thickness[parent[c]] + offset[c],
    /// in nanometres. With child value v_c = g_c d_c and parent value v_p = g_p d_p (g = units per
    /// nm), v_c = s v_p + o gives d_c = s (g_p / g_c) d_p + o / g_c.
    /// </summary>
    public static (int[] Parent, double[] Scale, double[] Offset) Ties(DesignDocument doc)
    {
        int n = doc.Layers.Length;
        var parent = Enumerable.Repeat(-1, n).ToArray();
        var scale = new double[n];
        var offset = new double[n];
        for (int c = 0; c < n; ++c)
        {
            if (doc.Layers[c].Pickup is not { } p) continue;
            string unit = UnitOf(doc, c);
            double gc = PerNm(doc, doc.Layers[c].Material, unit);
            double gp = PerNm(doc, doc.Layers[p.Parent].Material, unit);
            parent[c] = p.Parent;
            scale[c] = p.Scale * gp / gc;
            offset[c] = p.Offset / gc;
        }
        return (parent, scale, offset);
    }

    /// <summary>Why the document's units or pickups cannot stand, or null. Layers are named from 1.</summary>
    public static string? Problem(DesignDocument doc)
    {
        if (Parse(doc.ThicknessUnit) != doc.ThicknessUnit)
            return $"'{doc.ThicknessUnit}' is not a thickness unit; use nm, QWOT or FWOT";
        if (!(doc.ReferenceWavelength > 0.0) || !double.IsFinite(doc.ReferenceWavelength))
            return "the reference wavelength must be a positive number of nanometres";

        for (int c = 0; c < doc.Layers.Length; ++c)
        {
            var layer = doc.Layers[c];
            if (layer.Unit is { } u && Parse(u) != u)
                return $"layer {c + 1}: '{u}' is not a thickness unit; use nm, QWOT or FWOT";
            try { PerNm(doc, layer.Material, UnitOf(doc, c)); }
            catch (FormatException e) { return $"layer {c + 1}: {e.Message}"; }
            if (layer.Min is { } lo && !(lo >= 0.0 && double.IsFinite(lo)))
                return $"layer {c + 1}: a minimum must be zero or a positive thickness";
            if (layer.Max is { } hi && !(hi > 0.0 && double.IsFinite(hi)))
                return $"layer {c + 1}: a maximum must be a positive thickness";
            if (layer.Min is { } a && layer.Max is { } b && !(b > a))
                return $"layer {c + 1}: the maximum must be more than the minimum";

            if (layer.Pickup is not { } p) continue;
            if (p.Parent < 0 || p.Parent >= doc.Layers.Length)
                return $"layer {c + 1} picks up layer {p.Parent + 1}, and the stack has {doc.Layers.Length}";
            if (p.Parent == c) return $"layer {c + 1} cannot pick up its own thickness";
            if (doc.Layers[p.Parent].Pickup is not null)
                return $"layer {c + 1} picks up layer {p.Parent + 1}, which is itself picked up from "
                       + $"layer {doc.Layers[p.Parent].Pickup!.Parent + 1}; pick up layer "
                       + $"{doc.Layers[p.Parent].Pickup!.Parent + 1} instead";
            if (!double.IsFinite(p.Scale) || !double.IsFinite(p.Offset))
                return $"layer {c + 1}: the pickup's scale and offset must be numbers";
            if (UnitOf(doc, c) != UnitOf(doc, p.Parent))
                return $"layer {c + 1} picks up layer {p.Parent + 1}, so both must use the same unit, "
                       + $"and layer {c + 1} is in {UnitOf(doc, c)} while layer {p.Parent + 1} is in "
                       + $"{UnitOf(doc, p.Parent)}";
        }
        return null;
    }

    /// <summary>The layers renumbered after a change of order: <paramref name="newIndexOf"/>[old]
    /// is the layer's new index, or -1 for a layer that is gone. A pickup whose child is gone goes
    /// with it; one whose parent is gone is the caller's to refuse first.</summary>
    public static LayerDocument[] Renumber(LayerDocument[] layers, int[] newIndexOf) =>
        layers.Select(l => l.Pickup is { } p
            ? l with { Pickup = newIndexOf[p.Parent] >= 0 ? p with { Parent = newIndexOf[p.Parent] } : null }
            : l).ToArray();

    /// <summary>A bound of layer <paramref name="layer"/>, in nanometres, in its own unit; null stays null.</summary>
    public static double? InUnit(DesignDocument doc, int layer, double? nm) =>
        nm * PerNm(doc, doc.Layers[layer].Material, UnitOf(doc, layer));

    /// <summary>The optimiser's bounds, one per variable in the order the design declares them -
    /// every layer that is varied and not picked up: [Min or 0, Max or the design's maximum for
    /// unconstrained layers], in nm.</summary>
    public static (double[] Lower, double[] Upper) Bounds(DesignDocument doc)
    {
        var free = doc.Layers.Where(l => l.Variable && l.Pickup is null).ToArray();
        return (free.Select(l => l.Min ?? 0.0).ToArray(),
                free.Select(l => l.Max ?? (l.Min > doc.UpperBound ? double.PositiveInfinity : doc.UpperBound)).ToArray());
    }

    /// <summary>The free, unconstrained-above layers already thicker than the design's maximum for
    /// unconstrained layers: the optimiser would pull them down to it, so it says so instead.</summary>
    public static int[] AboveMaximum(DesignDocument doc) =>
        doc.Layers.Select((l, i) => (l, i))
           .Where(x => x.l.Variable && x.l.Pickup is null && x.l.Max is null && !(x.l.Min > doc.UpperBound)
                       && x.l.Thickness > doc.UpperBound)
           .Select(x => x.i).ToArray();

    /// <summary>Whether any layer the optimiser may move is bounded.</summary>
    public static bool AnyBounds(DesignDocument doc) =>
        doc.Layers.Any(l => l.Variable && l.Pickup is null && (l.Min is not null || l.Max is not null));

    /// <summary>"layer 3", "layers 3 and 5", "layers 3, 5 and 7".</summary>
    public static string Named(IReadOnlyList<int> layers)
    {
        var n = layers.Select(i => (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        return n.Length == 1 ? $"layer {n[0]}"
            : $"layers {string.Join(", ", n[..^1])} and {n[^1]}";
    }
}
