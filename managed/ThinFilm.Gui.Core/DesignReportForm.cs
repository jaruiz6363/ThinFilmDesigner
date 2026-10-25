// DesignReportForm.cs — what File > Export design report asks for, checked without a window.
// The report itself is the API's (DesignReport.cs).

using System.Globalization;
using ThinFilm.Api;

namespace ThinFilm.Gui.Core;

public sealed class DesignReportForm
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The range as the Spectrum tab shows it - the design's saved plot when it has one -
    /// in the design's wavelength unit, every 10 nm.</summary>
    public DesignReportForm(string designName, double fromNm, double toNm, string wavelengthUnit)
    {
        DesignName = designName;
        WavelengthUnit = wavelengthUnit;
        From = Lengths.Shown(fromNm, wavelengthUnit).ToString("0.######", Inv);
        To = Lengths.Shown(toNm, wavelengthUnit).ToString("0.######", Inv);
        Step = Lengths.Shown(10.0, wavelengthUnit).ToString("0.######", Inv);
    }

    public string DesignName { get; }
    public string WavelengthUnit { get; }
    public string From { get; set; }
    public string To { get; set; }
    public string Step { get; set; }

    /// <summary>The file name the save dialog suggests: design-report.txt.</summary>
    public string SuggestedFileName =>
        string.Concat(DesignName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + "-report.txt";

    /// <summary>The request for <paramref name="path"/>, or null and why not.</summary>
    public DesignReportRequest? ToRequest(string? path, out string? problem)
    {
        problem = null;
        if (!Number(From, out double from) || !Number(To, out double to) || !Number(Step, out double step))
        { problem = "The range and the step need three numbers."; return null; }
        double fromNm = Lengths.ToNm(from, WavelengthUnit), toNm = Lengths.ToNm(to, WavelengthUnit),
               stepNm = Lengths.ToNm(step, WavelengthUnit);
        if (!(fromNm > 0.0) || !(toNm >= fromNm))
        { problem = "The range runs from a wavelength above 0 to one at or after it."; return null; }
        if (!(stepNm > 0.0)) { problem = "The step must be above 0."; return null; }
        if (Math.Floor((toNm - fromNm) / stepNm) + 2 > ThinFilmApi.ReportMaxWavelengths)
        { problem = $"That is more than {ThinFilmApi.ReportMaxWavelengths} wavelengths; take a larger step."; return null; }
        return new DesignReportRequest(fromNm, toNm, stepNm, path);
    }

    /// <summary>What the dialog says the report is.</summary>
    public string Explanation =>
        "A text file for another program: the layers, incident side first, with physical thicknesses in nm, then an "
        + "n, k table for every material the layers use and for the incident medium and substrate (N = n + ik, k ≥ 0 "
        + "absorbing). The tables are tab-separated, so each pastes into a spreadsheet as it stands. The last "
        + "wavelength is always included.";

    private static bool Number(string text, out double v) =>
        double.TryParse(text.Trim(), NumberStyles.Float, Inv, out v);
}
