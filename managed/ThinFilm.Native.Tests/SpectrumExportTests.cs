// SpectrumExportTests.cs — Spectrum > Export…: the plotted curves as tab-separated text.
//
// The user (2026-10-21): "can you please add an export txt function to our software?"

using System.Globalization;
using ThinFilm.Gui.Core;

namespace ThinFilm.Native.Tests;

public static class SpectrumExportTests
{
    public static void TheTextIsWhatIsPlotted(Harness h)
    {
        h.Section("Spectrum > Export: a column per quantity, every number as the plot has it, under # lines");

        var none = new DesignWorkspace();
        h.Expect(none.SpectrumText() is null && none.Status.Contains("Plot a spectrum first"), "nothing plotted, nothing to export");

        var w = new DesignWorkspace();
        w.OpenSample("AR-visible");

        w.SpectrumFrom = 400.0;
        w.SpectrumTo = 800.0;
        w.SpectrumSteps = 81;
        w.SpectrumQuantities = "RA,TA,RS";
        w.RefreshSpectrum();
        var lines = w.SpectrumText()!.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        var header = lines.TakeWhile(l => l.StartsWith('#')).ToArray();
        var table = lines.Skip(header.Length).ToArray();
        h.Expect(header.Length == 3 && header[1].Contains("as fractions") && table[0] == "wavelength_nm\tRA\tTA\tRS",
                 $"three # lines, then the columns: {table[0].Replace('\t', ' ')}");

        var rows = table.Skip(1).Select(l => l.Split('\t').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray()).ToArray();
        var plotted = w.Spectrum;
        bool same = rows.Length == 81 && rows[0][0] == 400.0 && rows[^1][0] == 800.0
                    && plotted.Select((series, c) => series.Y.Select((y, i) => rows[i][c + 1] == y).All(b => b)).All(b => b);
        h.Expect(same, "81 rows from 400 to 800 nm, each value exactly the one plotted");

        // A phase is exported as it is drawn, in degrees - as the CLI and MCP write it. (The plot
        // breaks its line at a wrap with a NaN; the file has no such gaps.)
        w.SpectrumQuantities = "R_PHASE_S";
        w.RefreshSpectrum();
        var phase = w.SpectrumText()!.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        var phaseRows = phase.Where(l => !l.StartsWith('#')).Skip(1)
                             .Select(l => double.Parse(l.Split('\t')[1], CultureInfo.InvariantCulture)).ToArray();
        h.Expect(phase.Any(l => l.StartsWith("# Phases, Psi and Delta in degrees"))
                 && phaseRows.SequenceEqual(w.Spectrum[0].Y.Where(double.IsFinite)) && phaseRows.Any(v => Math.Abs(v) > 2 * Math.PI),
                 "a phase exports in degrees, each value the one plotted");
        w.SpectrumQuantities = "RA,TA,RS";

        w.SetWavelengthUnit("um");
        w.RefreshSpectrum();
        var um = w.SpectrumText()!.Split('\n').First(l => !l.StartsWith('#'));
        h.Expect(um.StartsWith("wavelength_um\t"), "a design shown in µm exports µm");

        w.SpectrumVsAngle = true;
        w.SpectrumAngleFrom = 0.0;
        w.SpectrumAngleTo = 80.0;
        w.SpectrumAngleSteps = 9;
        w.RefreshSpectrum();
        var byAngle = w.SpectrumText()!.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').Where(l => !l.StartsWith('#')).ToArray();
        h.Expect(byAngle[0].StartsWith("angle_deg\t") && byAngle.Length == 10 && byAngle[^1].StartsWith("80\t"),
                 "against angle: a column of angles in degrees, 0 to 80");
    }
}
