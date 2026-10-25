// ZemaxForms.cs — what File > Export to ZEMAX coating asks for, checked without a window.
// The writing itself is the API's (ZemaxCoatings.cs).

using System.Globalization;
using ThinFilm.Api;

namespace ThinFilm.Gui.Core;

public sealed class ZemaxExportForm
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public ZemaxExportForm(string? path, string coating, double fromNm, double toNm, string wavelengthUnit)
    {
        WavelengthUnit = wavelengthUnit;
        Path = path ?? System.IO.Path.Combine(DefaultFolder ?? "", "COATING.DAT");
        Coating = ZemaxCoatingFile.Sanitise(coating);
        From = Lengths.Shown(fromNm, wavelengthUnit).ToString("0.######", Inv);
        To = Lengths.Shown(toNm, wavelengthUnit).ToString("0.######", Inv);
    }

    public string WavelengthUnit { get; }
    public string Path { get; set; }
    public string Coating { get; set; }
    public string From { get; set; }
    public string To { get; set; }

    /// <summary>OpticStudio's coating folder, Documents\Zemax\Coatings, when there is one.</summary>
    public static string? DefaultFolder
    {
        get
        {
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var folder = System.IO.Path.Combine(documents, "Zemax", "Coatings");
            return Directory.Exists(folder) ? folder : null;
        }
    }

    /// <summary>The request, or null and why not. Replace is false: the window asks first.</summary>
    public ZemaxExportRequest? ToRequest(out string? problem)
    {
        problem = null;
        string path = Path.Trim().Trim('"');
        if (path.Length == 0) { problem = "Choose the coating file to write into."; return null; }
        string? folder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (folder is not null && !Directory.Exists(folder)) { problem = $"There is no folder {folder}."; return null; }
        if (Coating.Trim().Length == 0) { problem = "The coating needs a name."; return null; }
        if (!Number(From, out double from) || !Number(To, out double to))
        { problem = "The table's range needs two numbers."; return null; }
        double fromNm = Lengths.ToNm(from, WavelengthUnit), toNm = Lengths.ToNm(to, WavelengthUnit);
        if (!(fromNm > 0.0) || !(toNm >= fromNm))
        { problem = "The table's range runs from a wavelength above 0 to one at or after it."; return null; }
        return new ZemaxExportRequest(path, Coating.Trim(), fromNm, toNm);
    }

    /// <summary>What the dialog says the export will do.</summary>
    public string Explanation =>
        "Writes this design as one COAT, in µm, and an n,k table (MATE) for each of its materials; an equation's "
        + "coefficients go in a comment above its table, so importing it here gives the equation back. Names are written "
        + "in capitals, as OpticStudio shows them, with anything but letters, digits, _ and - made _. Every other line "
        + "of the file is kept as it was, and the file as it was is kept beside it as .bak. A material the file already "
        + "has under the same name is never changed: if its data differ, ours is written as NAME_2.";

    private static bool Number(string text, out double v) =>
        double.TryParse(text.Trim(), NumberStyles.Float, Inv, out v);
}
