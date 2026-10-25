// BoundsForm.cs — the Set vary constraints dialog, with no window in it.
//
// The user (2026-10-16): from a start layer to an end layer, all materials or one, a constraint
// type, and a minimum and maximum as the type needs. The bounds are typed in one unit for every
// layer the dialog reaches; a QWOT or FWOT bound is counted in each layer's own index, so "at
// least 0.5 QWOT" over SiO2 and TiO2 layers is half of each one's own quarter-wave.

using System.Globalization;
using ThinFilm.Api;

namespace ThinFilm.Gui.Core;

public sealed class BoundsForm
{
    public const string AllMaterials = "All materials";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly IReadOnlyList<LayerRow> _layers;
    private readonly IReadOnlyList<string> _materials;

    /// <param name="layers">The table's rows: what the preview counts.</param>
    /// <param name="materials">The design's materials, in index order.</param>
    public BoundsForm(IReadOnlyList<LayerRow> layers, IReadOnlyList<string> materials, string designUnit,
                      string lengthUnit)
    {
        _layers = layers;
        _materials = materials;
        LengthUnit = lengthUnit;
        MaterialOptions = [AllMaterials, .. materials];
        UnitOptions = [LayerUnits.Qwot, LayerUnits.Fwot, Lengths.Physical(lengthUnit)];
        Unit = Lengths.Label(designUnit, lengthUnit);
        FromText = "1";
        ToText = layers.Count.ToString(Inv);
    }

    public string LengthUnit { get; }
    public IReadOnlyList<string> MaterialOptions { get; }
    public IReadOnlyList<string> UnitOptions { get; }
    public IReadOnlyList<string> ConstraintOptions => Constraints.All;

    public string FromText { get; set; }
    public string ToText { get; set; }
    public string Material { get; set; } = AllMaterials;
    /// <summary>As shown: "QWOT", "FWOT", or "nm"/"µm" for physical.</summary>
    public string Unit { get; set; }
    public string Constraint { get; set; } = Constraints.Between;
    public string MinText { get; set; } = "";
    public string MaxText { get; set; } = "";
    public bool MinApplies => Constraint is Constraints.Between or Constraints.AtLeast;
    public bool MaxApplies => Constraint is Constraints.Between or Constraints.AtMost;

    /// <summary>The design's maximum for unconstrained layers, nm: what an open top means.</summary>
    public double UnconstrainedMaximumNm { get; init; } = 600.0;

    /// <summary>What an open side of the chosen constraint means, so no limit is unseen.</summary>
    public string BoundsNote => Constraints.Note(Constraint, UnconstrainedMaximumNm, LengthUnit);

    /// <summary>Which layers the range and material reach, in words, or why they reach none.</summary>
    public string Preview
    {
        get
        {
            if (Range(out _) is not var (first, last)) return "";
            var reached = Enumerable.Range(first, last - first + 1)
                .Where(i => Material == AllMaterials || _layers[i].Material == Material).ToArray();
            var picked = reached.Where(i => _layers[i].IsPickedUp).ToArray();
            var set = reached.Except(picked).ToArray();
            if (set.Length == 0 && picked.Length == 0) return "No layer in that range is of that material.";
            string text = set.Length == 0 ? "No free layer to set."
                : $"Sets {(set.Length == 1 ? "layer" : "layers")} {DesignWorkspace.Ranges(set)}.";
            if (picked.Length > 0)
                text += $" Passes over {DesignWorkspace.Ranges(picked)}: picked up, {(picked.Length == 1 ? "it follows its" : "they follow their")} parent.";
            return text;
        }
    }

    private (int, int)? Range(out string? problem)
    {
        problem = null;
        int n = _layers.Count;
        if (n == 0) { problem = "the design has no layers"; return null; }
        if (!int.TryParse(FromText.Trim(), NumberStyles.Integer, Inv, out int from) || from < 1 || from > n
            || !int.TryParse(ToText.Trim(), NumberStyles.Integer, Inv, out int to) || to < 1 || to > n)
        { problem = $"the start and end layers must be numbers from 1 to {n}"; return null; }
        if (from > to) { problem = "the start layer must not come after the end layer"; return null; }
        return (from - 1, to - 1);
    }

    /// <summary>The request for the API, or null with the reason.</summary>
    public BoundsRequest? ToRequest(out string? problem)
    {
        if (Range(out problem) is not var (first, last)) return null;
        int? material = Material == AllMaterials ? null : _materials.ToList().IndexOf(Material);
        if (material < 0) { problem = "choose a material"; return null; }
        string code = Lengths.Code(Unit);
        bool physical = code == LayerUnits.Nm;
        double? Read(string text) => LayerPropertiesForm.Number(text) is { } v ? (physical ? Lengths.ToNm(v, LengthUnit) : v) : null;

        double? min = null, max = null;
        if (MinApplies && ((min = Read(MinText)) is null || min < 0))
        { problem = $"the minimum must be zero or a positive number of {Unit}"; return null; }
        if (MaxApplies && ((max = Read(MaxText)) is null || max <= 0))
        { problem = $"the maximum must be a positive number of {Unit}"; return null; }
        if (min is { } a && max is { } b && !(b > a))
        { problem = "the maximum must be more than the minimum"; return null; }
        return new BoundsRequest(first, last, material, code, min, max);
    }
}
