// LayerPropertiesForm.cs — the Layer properties dialog, with no window in it.
//
// One layer: its material, its thickness and the unit it is typed in, whether the optimiser may
// move it, and a pickup from another layer. The dialog edits this form's copy and nothing reaches
// the design until OK, which hands ToProperties() to the API in one call.
//
// WHAT THE BOXES DO WHEN SOMETHING ELSE CHANGES follows the rules in ThinFilm.Api LayerUnits.cs:
// choosing another UNIT re-expresses the same nanometres (0.2500 FWOT becomes 1.0000 QWOT), and
// choosing another MATERIAL keeps the number typed (1 QWOT of SiO2 becomes 1 QWOT of TiO2), with
// the nanometres beside it to show what that means.

using System.Globalization;
using ThinFilm.Api;

namespace ThinFilm.Gui.Core;

public sealed class LayerPropertiesForm
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly Func<int, string, double> _perNm;

    /// <param name="perNm">How many of a unit one nanometre of a material (by index) is, in this
    /// design at its reference - DesignWorkspace.UnitsPerNm. NaN when it cannot be had.</param>
    /// <param name="wavelengthUnit">The design's length unit, "nm" or "um": the reference is shown
    /// in it, and a physical thickness is shown and typed in it (Lengths.cs).</param>
    public LayerPropertiesForm(LayerRow row, int layerCount, string designUnit, double referenceNm,
                               string wavelengthUnit, Func<int, string, double> perNm)
    {
        _perNm = perNm;
        LengthUnit = wavelengthUnit;
        LayerNumber = row.Number;
        LayerCount = layerCount;
        DesignUnit = designUnit;
        MaterialOptions = row.MaterialOptions;
        Material = row.Material;
        UnitOptions = row.UnitOptions;
        UnitChoice = row.UnitChoice;
        ThicknessText = _shown = row.ValueText;
        _exact = row.Value;
        Vary = row.Pickup is null ? row.Variable : true;
        HasPickup = row.Pickup is not null;
        PickupParentText = row.Pickup is { } p ? (p.Parent + 1).ToString(Inv) : (row.Number > 1 ? "1" : "2");
        PickupScaleText = (row.Pickup?.Scale ?? 1.0).ToString("G10", Inv);
        double offset = row.Pickup?.Offset ?? 0.0;
        PickupOffsetText = (Unit == LayerUnits.Nm ? Lengths.Shown(offset, LengthUnit) : offset).ToString("G10", Inv);
        ReferenceText = MeritRow.ShowWavelength(referenceNm, wavelengthUnit);
        Constraint = Constraints.Of(row.Min, row.Max);
        MinText = BoundText(row.Min);
        MaxText = BoundText(row.Max);
    }

    public int LayerNumber { get; }
    public int LayerCount { get; }
    public string DesignUnit { get; }
    public string ReferenceText { get; }

    /// <summary>"nm" or "um": what a physical thickness is shown and typed in.</summary>
    public string LengthUnit { get; }

    public IReadOnlyList<string> MaterialOptions { get; }
    public string Material { get; set; }

    /// <summary>The drop-down's choices as shown: "Default (QWOT)", "µm" or "nm", "QWOT", "FWOT".</summary>
    public IReadOnlyList<string> UnitOptions { get; }
    public string UnitChoice { get; private set; }

    /// <summary>The unit the thickness box is in, as the API names it ("nm" for physical, whatever
    /// it is shown as): the choice, or the design's for Default.</summary>
    public string Unit => UnitChoice.StartsWith("Default", StringComparison.Ordinal) ? DesignUnit : Lengths.Code(UnitChoice);

    /// <summary>The same unit as shown: "µm" for physical in a µm design.</summary>
    public string UnitLabel => Lengths.Label(Unit, LengthUnit);

    public string ThicknessText { get; set; }

    // THE EXACT THICKNESS BEHIND THE BOX. The box shows four decimals of a QWOT or a thousandth
    // of a nm, so reading the box back would round the layer every time the dialog was opened and
    // OK pressed, and every time the unit was switched and switched back. While the box still
    // shows what was put in it, the exact value is used; once the person types, what they typed is.
    private double _exact;     // in Unit as the API takes it: nm for physical, else QWOT or FWOT
    private string _shown;

    /// <summary>The box's thickness in the API's unit (nm for physical), or null if it is not a number.</summary>
    private double? Typed() =>
        ThicknessText == _shown ? _exact
        : Number(ThicknessText) is { } v ? (Unit == LayerUnits.Nm ? Lengths.ToNm(v, LengthUnit) : v) : null;
    public bool Vary { get; set; }

    // ---- BOUNDS, below Vary: none, both, a minimum or a maximum, in the thickness box's unit.

    /// <summary>One of <see cref="Constraints.All"/>.</summary>
    public string Constraint { get; set; }
    public string MinText { get; set; }
    public string MaxText { get; set; }
    public bool MinApplies => Constraint is Constraints.Between or Constraints.AtLeast;
    public bool MaxApplies => Constraint is Constraints.Between or Constraints.AtMost;

    /// <summary>The design's maximum for unconstrained layers, nm: what an open top means.</summary>
    public double UnconstrainedMaximumNm { get; init; } = 600.0;

    /// <summary>What an open side of the chosen constraint means, so no limit is unseen.</summary>
    public string BoundsNote => Constraints.Note(Constraint, UnconstrainedMaximumNm, LengthUnit);

    /// <summary>A bound in the API's unit, for its box: physical ones in the length unit.</summary>
    private string BoundText(double? v) => v is not { } x ? ""
        : (Unit == LayerUnits.Nm ? Lengths.Shown(x, LengthUnit) : x).ToString("G10", Inv);

    /// <summary>A bound's box read back in the API's unit, or null when it is not a number.</summary>
    private double? BoundValue(string text) =>
        Number(text) is { } v ? (Unit == LayerUnits.Nm ? Lengths.ToNm(v, LengthUnit) : v) : null;

    public bool HasPickup { get; set; }
    public string PickupParentText { get; set; }
    public string PickupScaleText { get; set; }
    public string PickupOffsetText { get; set; }

    private int MaterialIndex => MaterialOptions is IList<string> l ? l.IndexOf(Material) : -1;

    /// <summary>How many of a unit, as the API names it, one nanometre is (1 for physical).</summary>
    private double PerNm(string unit) => unit == LayerUnits.Nm ? 1.0 : _perNm(MaterialIndex, unit);

    /// <summary>A value in the API's unit, for the box: a physical one in the length unit.</summary>
    private string Format(double value, string unit) =>
        unit == LayerUnits.Nm ? Lengths.Cell(value, LengthUnit) : value.ToString("F4", Inv);

    /// <summary>
    /// Another unit chosen: the thickness box re-expressed in it, the same nanometres. A box that
    /// does not hold a number is left as it is, for the person to finish.
    /// </summary>
    public void ChooseUnit(string choice)
    {
        if (choice == UnitChoice) return;
        string from = Unit;
        double? minWas = BoundValue(MinText), maxWas = BoundValue(MaxText);
        UnitChoice = choice;
        double factor = PerNm(Unit) / PerNm(from);
        if (double.IsFinite(factor))
        {
            if (minWas is { } lo) MinText = BoundText(lo * factor);
            if (maxWas is { } hi) MaxText = BoundText(hi * factor);
        }
        if (Typed() is { } v)
        {
            double value = v / PerNm(from) * PerNm(Unit);
            if (double.IsFinite(value)) { _exact = value; ThicknessText = _shown = Format(value, Unit); }
        }
    }

    /// <summary>What the thickness box means physically, in the material chosen and the length
    /// unit: "= 58.51 nm of TiO2", "= 0.05851 µm of TiO2". Nothing for a physical unit.</summary>
    public string NmPreview
    {
        get
        {
            if (HasPickup) return "set by the pickup";
            if (Typed() is not { } v) return "";
            if (Unit == LayerUnits.Nm) return "";
            double nm = v / _perNm(MaterialIndex, Unit);
            return double.IsFinite(nm) ? $"= {Lengths.Say(nm, LengthUnit)} of {Material}" : "";
        }
    }

    /// <summary>The pickup in words, for the line under its boxes.</summary>
    public string PickupNote =>
        $"Layer {LayerNumber} = scale × parent + offset, in {UnitLabel}. The parent must be in {UnitLabel} too, "
        + "and must not itself be picked up. A picked-up layer is not varied on its own: it follows its parent.";

    /// <summary>The properties to hand the API, or null with <paramref name="problem"/> saying which box.</summary>
    public LayerProperties? ToProperties(out string? problem)
    {
        problem = null;
        int material = MaterialIndex;
        if (material < 0) { problem = "choose a material"; return null; }
        string? unit = UnitChoice.StartsWith("Default", StringComparison.Ordinal) ? null : Lengths.Code(UnitChoice);
        bool physical = Unit == LayerUnits.Nm;

        LayerPickup? pickup = null;
        double value = 0.0;
        if (HasPickup)
        {
            if (!int.TryParse(PickupParentText.Trim(), NumberStyles.Integer, Inv, out int parent)
                || parent < 1 || parent > LayerCount)
            { problem = $"the parent must be a layer number from 1 to {LayerCount}"; return null; }
            if (parent == LayerNumber) { problem = "a layer cannot pick up its own thickness"; return null; }
            if (Number(PickupScaleText) is not { } scale) { problem = "the scale must be a number"; return null; }
            if (Number(PickupOffsetText) is not { } offset) { problem = "the offset must be a number"; return null; }
            pickup = new LayerPickup(parent - 1, scale, physical ? Lengths.ToNm(offset, LengthUnit) : offset);
        }
        else
        {
            if (Typed() is not { } v || v < 0)
            { problem = $"the thickness must be zero or a positive number of {UnitLabel}"; return null; }
            value = v;
        }
        double? min = null, max = null;
        if (MinApplies && ((min = BoundValue(MinText)) is null || min < 0))
        { problem = $"the minimum must be zero or a positive number of {UnitLabel}"; return null; }
        if (MaxApplies && ((max = BoundValue(MaxText)) is null || max <= 0))
        { problem = $"the maximum must be a positive number of {UnitLabel}"; return null; }
        if (min is { } a && max is { } b && !(b > a))
        { problem = "the maximum must be more than the minimum"; return null; }
        return new LayerProperties(material, unit, value, HasPickup || Vary, pickup, min, max);
    }

    internal static double? Number(string? text) =>
        double.TryParse(text?.Trim(), NumberStyles.Float, Inv, out double v) && double.IsFinite(v) ? v : null;
}

/// <summary>The thickness settings in Edit > System Parameters, read from their boxes.</summary>
public static class ThicknessPreferences
{
    /// <summary>The reference wavelength typed in <paramref name="wavelengthUnit"/> ("nm" or "um"),
    /// in nanometres, or null with the reason.</summary>
    public static double? ReferenceNm(string? text, string wavelengthUnit, out string? problem)
    {
        problem = null;
        if (LayerPropertiesForm.Number(text) is not { } v || v <= 0)
        {
            problem = $"the reference wavelength must be a positive number of {(wavelengthUnit == "um" ? "µm" : "nm")}";
            return null;
        }
        return wavelengthUnit == "um" ? v * 1000.0 : v;
    }

    /// <summary>The maximum for unconstrained layers typed in <paramref name="wavelengthUnit"/>, in
    /// nanometres, or null with the reason.</summary>
    public static double? MaximumNm(string? text, string wavelengthUnit, out string? problem)
    {
        problem = null;
        if (LayerPropertiesForm.Number(text) is not { } v || v <= 0)
        {
            problem = $"the maximum for unconstrained layers must be a positive number of {(wavelengthUnit == "um" ? "µm" : "nm")}";
            return null;
        }
        return wavelengthUnit == "um" ? v * 1000.0 : v;
    }
}

/// <summary>The four kinds of bound a layer may have, as the dialogs name them.</summary>
public static class Constraints
{
    public const string None = "Unconstrained";
    public const string Between = "Between min and max";
    public const string AtLeast = "Greater than min";
    public const string AtMost = "Less than max";
    public static readonly string[] All = [None, Between, AtLeast, AtMost];

    /// <summary>What the open side of a constraint means, in words: from 0, and up to the design's
    /// maximum for unconstrained layers.</summary>
    public static string Note(string constraint, double maximumNm, string lengthUnit)
    {
        string max = $"{Lengths.Say(maximumNm, lengthUnit)}, the design's maximum for unconstrained layers (Edit > System Parameters)";
        return constraint switch
        {
            None => $"From 0 up to {max}.",
            AtLeast => $"Up to {max}, unless the minimum is above it.",
            AtMost => "From 0.",
            _ => "",
        };
    }

    public static string Of(double? min, double? max) => (min, max) switch
    {
        (not null, not null) => Between,
        (not null, null) => AtLeast,
        (null, not null) => AtMost,
        _ => None,
    };
}
