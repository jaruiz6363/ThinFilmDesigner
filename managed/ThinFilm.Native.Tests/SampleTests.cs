// SampleTests.cs — the sample designs that ship in samples/, each checked as a starting problem.
//
// THE SAMPLES ARE EASY TO FAKE. Ten files that load and ten names that match: green, complete,
// and worthless. So the checks here are that a sample WORKS — it plans, it evaluates, it can be
// refined, and refining it moves the design toward what its own merit function asks for. These
// were the template library's checks (X1.6) and the samples are what the templates wrote out.

using ThinFilm.Api;
using ThinFilm.Optimize;

namespace ThinFilm.Native.Tests;

public static class SampleTests
{
    private static IEnumerable<(string Name, DesignDocument Doc)> All =>
        SampleFiles.Names.Select(n => (n, SampleFiles.Document(n)));

    private static Design Create(DesignDocument doc)
    {
        var d = new Design(doc.ToSpec());
        d.Plan();
        return d;
    }

    private static DlsOptions Refinement(DesignDocument doc, int iterations = 300)
    {
        var (lower, upper) = LayerUnits.Bounds(doc);
        return new DlsOptions { Lower = lower, Upper = upper, MaxIterations = iterations };
    }

    public static void TheTenShipAndAreDistinct(Harness h)
    {
        h.Section("the ten samples ship, one folder each, and are ten different problems");

        string[] wanted =
        [
            "AR-visible", "Bandpass", "Chirped-mirror", "Dichroic", "Laser-HR",
            "Low-e", "PBS", "Reverse-engineering", "Solar-absorber", "V-coat",
        ];
        h.Expect(SampleFiles.Names.SequenceEqual(wanted), $"the ten ({string.Join(", ", SampleFiles.Names)})");
        foreach (var name in wanted)
        {
            if (!File.Exists(SampleFiles.PathOf(name))) { h.Fail($"missing sample: {name}"); continue; }
            var doc = SampleFiles.Document(name);
            h.Expect(doc.Name == name && (doc.Notes ?? "").Length > 20, $"{name}: named, and its notes say what it is for");
        }

        int shapes = All.Select(s => (s.Doc.Layers.Length, s.Doc.Operands.Length, s.Doc.Wavelength.Length))
                        .Distinct().Count();
        h.Expect(shapes >= 8, $"genuinely different problems ({shapes} distinct shapes)");
    }

    public static void EverySamplePlansAndEvaluates(Harness h)
    {
        h.Section("every sample builds a problem the engine accepts");

        foreach (var (name, doc) in All)
        {
            Design d;
            try { d = Create(doc); }
            catch (Exception e) { h.Fail($"{name}: would not plan — {e.Message}"); continue; }

            d.Evaluate();
            h.Expect(double.IsFinite(d.Merit) && d.Merit >= 0.0, $"{name}: evaluates to a finite merit ({d.Merit:G6})");
            h.Expect(d.LayerCount > 0 && d.VariableCount == d.LayerCount,
                     $"{name}: has layers, every thickness a variable");
            h.Expect(doc.Norm == "L2", $"{name}: defaults to L2 (it says {doc.Norm})");
        }
    }

    /// <summary>
    /// A sample that left its scales at 1 would pass everything else here while being exactly
    /// what normalisation exists to prevent.
    /// </summary>
    public static void EverySampleSetsItsNormalisationScales(Harness h)
    {
        h.Section("every operand carries a scale chosen from what its target means");

        foreach (var (name, doc) in All)
        {
            h.Expect(doc.Operands.All(o => o.Scale > 0.0), $"{name}: every scale is positive, which the ABI requires");
            h.Expect(doc.Operands.Any(o => Math.Abs(o.Scale - 1.0) > 1e-15),
                     $"{name}: the scales are actually set, not left at the default of 1");
        }
    }

    /// <summary>
    /// Every design sample scores MINT and MAXT at low weight, the stand-in for SENS its notes
    /// explain. Reverse engineering does not: a fit seeks the thicknesses actually deposited, and
    /// a manufacturability penalty would pull it away from them.
    /// </summary>
    public static void EveryDesignSampleCarriesAManufacturabilityTerm(Harness h)
    {
        h.Section("every DESIGN sample scores MINT and MAXT at low weight; the fit deliberately does not");

        foreach (var (name, doc) in All)
        {
            var mint = doc.Operands.FirstOrDefault(o => o.Code.Equals("MINT", StringComparison.OrdinalIgnoreCase));
            var maxt = doc.Operands.FirstOrDefault(o => o.Code.Equals("MAXT", StringComparison.OrdinalIgnoreCase));
            if (name == "Reverse-engineering")
            {
                h.Expect(mint is null && maxt is null, $"{name}: has NO manufacturability term");
                h.Expect((doc.Notes ?? "").Contains("estimation", StringComparison.Ordinal), $"{name}: says why it has none");
                continue;
            }
            h.Expect(mint is not null && maxt is not null, $"{name}: scores MINT and MAXT");
            if (mint is not null) h.Expect(mint.Weight < 0.5, $"{name}: MINT is a low weight ({mint.Weight:G3})");
            h.Expect((doc.Notes ?? "").Contains("SENS", StringComparison.Ordinal),
                     $"{name}: its notes say the robustness term is not SENS, and why");
        }
    }

    public static void IncompleteSamplesSaySoThemselves(Harness h)
    {
        h.Section("no sample is incomplete any more: each asks for its real specification");

        const string Marker = "INCOMPLETE BY DESIGN";
        h.Pin(All.Count(x => (x.Doc.Notes ?? "").Contains(Marker, StringComparison.Ordinal)), 0,
              "no sample is marked incomplete");

        // S1.14: the chirped mirror was one, until group delay dispersion could be asked for.
        var chirped = SampleFiles.Document("Chirped-mirror");
        var gdd = chirped.Operands.Where(o => o.Code == "R_GDD_S").ToArray();
        h.Expect(gdd.Length == 11 && gdd.All(o => o.Comparison == "EQUAL" && o.Target == -50.0),
                 $"the chirped mirror asks for GDD = −50 fs² at 11 wavelengths ({gdd.Length})");

        // M3.5: the solar absorber was the other, until band averages could be asked for.
        var solar = SampleFiles.Document("Solar-absorber").Operands;
        h.Expect(solar.Any(o => o is { Code: "ASOL", Comparison: "GE", Target: 0.93 })
                 && solar.Any(o => o is { Code: "EMIT", Comparison: "LE", Target: 0.1, Temperature: 373.15 })
                 && solar.Any(o => o is { Code: "AERATIO", Comparison: "NONE" }) && !solar.Any(o => o.Code == "AA"),
                 "the solar absorber asks for ASOL at least 0.93 and EMIT at most 0.10 at 100 °C, and reports AERATIO");
    }

    /// <summary>
    /// Refine each sample: refinement never makes it worse, stays in bounds, and a reported
    /// Converged agrees with the gradient it left behind. Stationarity is not demanded of every
    /// sample: a quarter-wave laser HR starts at its optimum, and a bandpass is too hard a surface
    /// to finish in the budget.
    /// </summary>
    public static void EverySampleRefinesAndReportsHonestly(Harness h)
    {
        h.Section("refinement never worsens a sample, and a reported Converged is a true one");

        int improved = 0, converged = 0, count = 0;
        foreach (var (name, doc) in All)
        {
            ++count;
            var d = Create(doc);
            var options = Refinement(doc, 200);
            d.Evaluate();
            double before = d.Merit;
            double gradientBefore = Gradient(d, options);

            var dls = new DampedLeastSquares(d.VariableCount, d.Report.n_residuals, options);
            var r = dls.Minimise(d, options);

            h.Expect(r.FinalMerit <= before + 1e-15, $"{name}: refinement never makes it worse ({before:G6} -> {r.FinalMerit:G6})");
            h.Expect(before > 0.0, $"{name}: the starting design has a merit to reason about");
            if (r.FinalMerit < before) ++improved;

            double largest = Gradient(d, options);
            if (r.Outcome == DlsOutcome.Converged)
            {
                ++converged;
                // The solver's own criterion: the gradient fell to 1e-6 of where it started.
                double allowed = 1e-6 * Math.Max(gradientBefore, 1e-300);
                h.Expect(largest <= allowed * 1.001,
                         $"{name}: reported Converged, and |J^T r| fell {gradientBefore:G4} -> {largest:G4}, within {allowed:G4}");
            }

            for (int i = 0; i < d.VariableCount; ++i)
            {
                double v = d.GetVariable(i);
                if (v < options.Lower![i] - 1e-9 || v > options.Upper![i] + 1e-9)
                {
                    h.Fail($"{name}: variable {i} left its bounds at {v:G6}");
                    break;
                }
            }
        }

        h.Expect(improved >= 8, $"most samples have real work to do ({improved} of {count} improved)");
        h.Note($"{improved} improved, {converged} reported Converged and were checked against their gradient");
    }

    private static double Gradient(Design d, DlsOptions o)
    {
        d.EvaluateJacobian();
        int m = d.Report.n_residuals;
        double largest = 0.0;
        for (int v = 0; v < d.VariableCount; ++v)
        {
            double x = d.GetVariable(v);
            // A variable held by a bound need not have a vanishing gradient.
            if (Math.Abs(x - o.Lower![v]) < 1e-9 || Math.Abs(x - o.Upper![v]) < 1e-9) continue;
            double g = 0.0;
            for (int row = 0; row < m; ++row) g += d.Jacobian[v * m + row] * d.Residual[row];
            largest = Math.Max(largest, Math.Abs(g));
        }
        return largest;
    }

    /// <summary>
    /// Minimax is never worse than L2 on the worst residual (by construction: the first pass is
    /// the L2 answer and the best pass is kept), and on AR-visible it is better.
    /// </summary>
    public static void MinimaxTradesTheRmsForTheWorstCase(Harness h)
    {
        h.Section("minimax never has the worse worst case, and here it has a better one");

        var doc = SampleFiles.Document("AR-visible");
        foreach (var p in new[] { 2.5, 3.0, 4.0, 5.0, 6.0 })
        {
            var options = new NormOptions { Refinement = Refinement(doc), Passes = 5, FinalP = p };
            var a = Norms.Minimise(Create(doc), Norm.L2, options);
            var b = Norms.Minimise(Create(doc), Norm.Minimax, options);
            h.Expect(b.FinalWorstResidual <= a.FinalWorstResidual * (1.0 + 1e-12),
                     $"p={p}: minimax is not worse than L2 on the worst residual ({b.FinalWorstResidual:G6} against {a.FinalWorstResidual:G6})");
        }

        var demo = new NormOptions { Refinement = Refinement(doc), Passes = 5, FinalP = 3.0 };
        var l2r = Norms.Minimise(Create(doc), Norm.L2, demo);
        var best = Create(doc);
        var mmr = Norms.Minimise(best, Norm.Minimax, demo);
        h.Expect(l2r.FinalWorstResidual > 0.0, "the L2 fit leaves a worst residual to improve on");
        h.Expect(mmr.FinalWorstResidual < l2r.FinalWorstResidual,
                 $"and minimax improves it ({mmr.FinalWorstResidual:G6} against {l2r.FinalWorstResidual:G6})");

        // The weights are back where the document put them.
        var w0 = doc.ToSpec().Operands.Weight;
        h.Expect(best.OperandWeight.SequenceEqual(w0), "and the IRLS weights are restored, so the merit means what was asked");
    }
}
