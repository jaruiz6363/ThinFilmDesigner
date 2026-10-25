// RunCompare.cs — a run's design beside the one it started from, before it is kept or reverted.
//
// What the optimisation window's Merit function, Spectrum and Layers tabs show, and what the CLI's
// `compare` and the MCP's optimise_compare return: the same calls, so the three agree. Both designs
// are evaluated as sessions of their own that are never registered - the start document as it
// was, and the run's design rebuilt onto it - so nothing open is touched and no session id moves.
//
// Only once the run has ended: a running run's design is being changed on another thread.

using System.Globalization;
using ThinFilm.Native;

namespace ThinFilm.Api;

/// <summary>One layer of a stack profile: its material, physical thickness (nm) and the real part
/// of its index at the profile's wavelength.</summary>
public sealed record ProfileLayer(string Material, double Thickness, double Index);

/// <summary>A stack as an index profile at one wavelength (nm), incident side first: what a layer
/// graph draws.</summary>
public sealed record StackProfile(double Wavelength, string Incident, double IncidentIndex,
                                  string Substrate, double SubstrateIndex, ProfileLayer[] Layers)
{
    public double TotalThickness => Layers.Sum(l => l.Thickness);
}

/// <summary>A run's start design and its result, evaluated: the merit rows of each (same rows, in
/// the same order, since a run changes no operand), the operands as written, and each stack's
/// profile at the design's reference wavelength.</summary>
public sealed record RunComparison(string Run, string State, EvaluationReport Before, EvaluationReport After,
                                   OperandEdit[] Operands, StackProfile BeforeStack, StackProfile AfterStack);

/// <summary>The same spectrum of the start design and of the run's result.</summary>
public sealed record RunSpectra(string Run, SpectrumReport Before, SpectrumReport After);

public sealed partial class ThinFilmApi
{
    /// <summary>The run's start design beside its result: merit rows, operands and layer profiles.</summary>
    public ApiResult<RunComparison> CompareRun(string run)
    {
        if (!Ended(run, out var r, out var before, out var after, out var fail))
            return ApiResult<RunComparison>.Failure(fail!);
        var b = Evaluate(before!);
        if (!b.Ok) return ApiResult<RunComparison>.Failure(b.Error!, b.Status);
        var a = Evaluate(after!);
        if (!a.Ok) return ApiResult<RunComparison>.Failure(a.Error!, a.Status);
        return ApiResult<RunComparison>.Success(new RunComparison(
            r!.Id, r.State, b.Value!, a.Value!, before!.Document.Operands.Select(o => ToEdit(before.Document, o)).ToArray(),
            Profile(before.Document), Profile(after!.Document)));
    }

    /// <summary>The same spectrum of the run's start design and of its result.</summary>
    public ApiResult<RunSpectra> RunSpectrum(string run, SpectrumRequest request)
    {
        if (!Ended(run, out var r, out var before, out var after, out var fail))
            return ApiResult<RunSpectra>.Failure(fail!);
        var b = Spectrum(before!, request);
        if (!b.Ok) return ApiResult<RunSpectra>.Failure(b.Error!, b.Status);
        var a = Spectrum(after!, request);
        if (!a.Ok) return ApiResult<RunSpectra>.Failure(a.Error!, a.Status);
        return ApiResult<RunSpectra>.Success(new RunSpectra(r!.Id, b.Value!, a.Value!));
    }

    /// <summary>A stack's index profile at the design's reference wavelength (the middle of its
    /// grid when it has none).</summary>
    public static StackProfile Profile(DesignDocument doc)
    {
        double wl = doc.ReferenceWavelength > 0.0 ? doc.ReferenceWavelength
            : doc.Wavelength.Length > 0 ? doc.Wavelength[doc.Wavelength.Length / 2] : 550.0;
        double N(MaterialDocument m)
        {
            try
            {
                var model = m.Model ?? MaterialModel.FromSamples(doc.Wavelength, m.N, m.K);
                return model.At(wl, m.Name).N;
            }
            catch (Exception) { return double.NaN; }   // drawn as a gap, not a reason to refuse the rest
        }
        var index = doc.Materials.Select(N).ToArray();
        return new StackProfile(wl, doc.Incident.Name, N(doc.Incident), doc.Exit.Name, N(doc.Exit),
            doc.Layers.Select(l => new ProfileLayer(doc.Materials[l.Material].Name, l.Thickness,
                                                    index[l.Material])).ToArray());
    }

    /// <summary>The run, ended, with its start design and its result as unregistered sessions.</summary>
    private bool Ended(string run, out Run? r, out Session? before, out Session? after, out string? failure)
    {
        before = after = null;
        if (!FindRun(run, out r, out failure)) return false;
        if (r!.State == "running")
        {
            failure = $"run {r.Id} is still going; stop it or wait for it to finish";
            return false;
        }
        if (r.State == "failed" || r.End is null)
        {
            failure = $"run {r.Id} failed ({r.Error}); it has no design to compare";
            return false;
        }
        try
        {
            before = Unregistered(r.StartDocument);
            after = Unregistered(Documents.Rebuild(r.StartDocument, r.Design));
            return true;
        }
        catch (ThinFilmException e) { failure = e.Message; }
        catch (MaterialUnavailableException e) { failure = e.Message; }
        return false;
    }

    private static Session Unregistered(DesignDocument doc)
    {
        var design = new Design(doc.ToSpec());
        design.Plan();
        design.Evaluate();
        return new Session { Id = "compare", Document = doc, Design = design };
    }

    /// <summary>A comparison's operand row in a few words, for the CLI and the MCP: its code, where,
    /// the target and mode, and the value before and after.</summary>
    public static string[] CompareLines(RunComparison c, string unit = "nm")
    {
        var inv = CultureInfo.InvariantCulture;
        var lines = new List<string>
        {
            $"{"#",-4}{"code",-6}{"where",-24}{"mode",-7}{"target",12}{"before",14}{"after",14}{"delta",14}",
        };
        foreach (var o in c.After.Operands)
        {
            double before = c.Before.Operands[o.Index].Value;
            string where = o.At is { } at ? Where(at, unit) : "stack";
            string mode = o.Comparison.ToUpperInvariant() switch
            {
                "EQUAL" or "EQ" => "target", "GE" => "min", "LE" => "max", "RANGE" => "range", _ => "report",
            };
            lines.Add($"{o.Index + 1,-4}{o.Code,-6}{where,-24}{mode,-7}{o.Target.ToString("G6", inv),12}"
                      + $"{before.ToString("G6", inv),14}{o.Value.ToString("G6", inv),14}"
                      + $"{(o.Value - before).ToString("+0.#####E+00;-0.#####E+00;0", inv),14}");
        }
        lines.Add($"RMSE before {Rms(c.Before.Merit)}, after {Rms(c.After.Merit)}");
        return lines.ToArray();
    }

    /// <summary>The two stacks side by side, layer by layer from the incident side: material,
    /// thickness and index before and after. A run that added or removed layers leaves one side
    /// shorter, so rows are paired by position, not matched.</summary>
    public static string[] CompareStackLines(RunComparison c, string unit = "nm")
    {
        var inv = CultureInfo.InvariantCulture;
        string L(double nm) => unit == "um" ? (nm / 1000.0).ToString("0.######", inv) : nm.ToString("0.###", inv);
        string u = unit == "um" ? "um" : "nm";
        StackProfile b = c.BeforeStack, a = c.AfterStack;
        var lines = new List<string>
        {
            $"index n at {L(a.Wavelength)} {u}; incident {a.Incident} {a.IncidentIndex.ToString("0.###", inv)}, "
            + $"substrate {a.Substrate} {a.SubstrateIndex.ToString("0.###", inv)}",
            $"{"#",-4}{"before",-12}{u,12}{"n",8}   {"after",-12}{u,12}{"n",8}",
        };
        string Cell(StackProfile p, int i) => i < p.Layers.Length
            ? $"{p.Layers[i].Material,-12}{L(p.Layers[i].Thickness),12}{p.Layers[i].Index.ToString("0.###", inv),8}"
            : new string(' ', 32);
        for (int i = 0; i < Math.Max(b.Layers.Length, a.Layers.Length); ++i)
            lines.Add($"{i + 1,-4}{Cell(b, i)}   {Cell(a, i)}".TrimEnd());
        lines.Add($"total   {b.Layers.Length} layer{(b.Layers.Length == 1 ? "" : "s")}, {L(b.TotalThickness)} {u}   ->   "
                  + $"{a.Layers.Length} layer{(a.Layers.Length == 1 ? "" : "s")}, {L(a.TotalThickness)} {u}");
        return lines.ToArray();
    }

    private static string Where(OperandPoints at, string unit)
    {
        var inv = CultureInfo.InvariantCulture;
        string W(double nm) => unit == "um" ? (nm / 1000.0).ToString("0.#####", inv) : nm.ToString("0.###", inv);
        string wl = at.WavelengthMin == at.WavelengthMax ? W(at.WavelengthMin)
                                                         : $"{W(at.WavelengthMin)}-{W(at.WavelengthMax)}";
        string angle = at.AngleMin == at.AngleMax ? at.AngleMin.ToString("0.##", inv)
                                                  : $"{at.AngleMin.ToString("0.##", inv)}-{at.AngleMax.ToString("0.##", inv)}";
        return $"{wl} {(unit == "um" ? "um" : "nm")} @{angle}°";
    }
}
