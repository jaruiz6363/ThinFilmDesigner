// Lengths.cs — physical thicknesses shown in the design's length unit, nm or µm.
//
// ONE UNIT FOR EVERY LENGTH. Edit > System Parameters chooses nanometres or micrometres for the design
// (DesignDocument.WavelengthUnit, saved in its file), and the user's rule (2026-10-16) is that it
// governs physical thicknesses as well as wavelengths: choose µm, and the layer table, Layer
// properties, the merit table's Limit and the status bar all read µm. The document and the
// engine stay in nanometres, and a layer's physical unit is still written "nm" in the file; only
// what is shown and typed is converted, here.

using System.Globalization;

namespace ThinFilm.Gui.Core;

public static class Lengths
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The physical unit's label for a design: "nm", or "µm" when it is shown in µm.</summary>
    public static string Physical(string lengthUnit) => lengthUnit == "um" ? "µm" : "nm";

    /// <summary>A layer unit as shown: "nm" (physical) becomes the design's length label.</summary>
    public static string Label(string code, string lengthUnit) => code == "nm" ? Physical(lengthUnit) : code;

    /// <summary>A label back to the layer unit the API takes: "µm" and "nm" are both physical.</summary>
    public static string Code(string label) => label is "µm" or "um" or "nm" ? "nm" : label;

    /// <summary>A physical length held in nm, as a number in the design's unit.</summary>
    public static double Shown(double nm, string lengthUnit) => lengthUnit == "um" ? nm / 1000.0 : nm;

    /// <summary>A physical length typed in the design's unit, in nm.</summary>
    public static double ToNm(double typed, string lengthUnit) => lengthUnit == "um" ? typed * 1000.0 : typed;

    /// <summary>A layer thickness for a table cell: three decimals of a nm, six of a µm - the same
    /// thousandth of a nanometre either way.</summary>
    public static string Cell(double nm, string lengthUnit) =>
        Shown(nm, lengthUnit).ToString(lengthUnit == "um" ? "F6" : "F3", Inv);

    /// <summary>A physical length in a sentence, with its label: "58.5 nm", "0.0585 µm".</summary>
    public static string Say(double nm, string lengthUnit) =>
        lengthUnit == "um" ? (nm / 1000.0).ToString("0.#####", Inv) + " µm" : nm.ToString("0.##", Inv) + " nm";
}
