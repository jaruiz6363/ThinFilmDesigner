// FixtureTests.cs — X1.3: the managed path over the fixtures the native suite runs.
//
// THE SAME FILE, NOT A SIMILAR ONE. tests/fixtures/evaluation.fixtures is embedded in this
// assembly, exactly as the ABI pins are, so there is no copy to drift and no path to get wrong.
// test_fixtures.cpp reads it and checks the ENGINE against the recorded numbers; this reads it and
// checks DESIGN against the same numbers.
//
// WHY THAT SPLIT IS THE WHOLE POINT OF X1.3. Two suites with fixtures of their own can both be
// green while disagreeing about everything, because nothing ever puts one problem through both.
// With one fixture file the blame separates by itself: if the engine changes, the native suite
// fails and names the fixture; if only this suite fails, the fault is in the binding and cannot be
// anywhere else.
//
// AND IT IS WHAT CATCHES A SECOND IMPLEMENTATION. The failure the ledger rule is about is a number
// computed in C# that should have come from the engine — an average taken managed-side, a
// comparison applied twice, a residual "helpfully" rescaled. Any of those shows up here as a
// mismatch against a value the engine produced, on a problem nobody could tune to hide it.
//
// Comparison is EXACT. Both sides run the same compiled engine over the same doubles; there is no
// arithmetic between them that could legitimately move a bit, so a tolerance could only conceal
// the marshalling mistakes this exists to find.

using System.Globalization;
using System.Reflection;
using ThinFilm.Native;

namespace ThinFilm.Native.Tests;

public static class FixtureTests
{
    private sealed class Fixture
    {
        public string Name = "";
        public readonly Dictionary<string, double[]> D = new();
        public readonly Dictionary<string, int[]> I = new();

        public double[]? Dv(string k) => D.TryGetValue(k, out var v) && v.Length > 0 ? v : null;
        public int[]? Iv(string k) => I.TryGetValue(k, out var v) && v.Length > 0 ? v : null;
        public int Scalar(string k, int fallback) => Iv(k) is { } v ? v[0] : fallback;

        public TfComplex[]? Cv(string k)
        {
            var v = Dv(k);
            if (v is null) return null;
            var c = new TfComplex[v.Length / 2];
            for (int n = 0; n < c.Length; ++n) c[n] = new TfComplex(v[2 * n], v[2 * n + 1]);
            return c;
        }
    }

    private static List<Fixture> Read()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var s = asm.GetManifestResourceStream("evaluation.fixtures")
            ?? throw new InvalidOperationException(
                "tests/fixtures/evaluation.fixtures is not embedded in this assembly, so there is " +
                "nothing to compare against and passing would mean nothing.");
        using var r = new StreamReader(s);

        var all = new List<Fixture>();
        string? raw;
        while ((raw = r.ReadLine()) is not null)
        {
            var text = raw;
            int hash = text.IndexOf('#');
            if (hash >= 0) text = text[..hash];

            var tok = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tok.Length == 0) continue;

            if (tok[0] == "fixture") { all.Add(new Fixture { Name = tok.Length > 1 ? tok[1] : "?" }); continue; }
            if (tok[0] == "end" || all.Count == 0) continue;
            if (tok.Length < 2) continue;

            var fx = all[^1];
            var key = tok[1];
            if (tok[0] == "i")
            {
                var v = new int[tok.Length - 2];
                for (int n = 0; n < v.Length; ++n) v[n] = int.Parse(tok[n + 2], CultureInfo.InvariantCulture);
                fx.I[key] = v;
            }
            else
            {
                var v = new double[tok.Length - 2];
                for (int n = 0; n < v.Length; ++n) v[n] = double.Parse(tok[n + 2], CultureInfo.InvariantCulture);
                fx.D[key] = v;
            }
        }
        return all;
    }

    /// <summary>
    /// The fixture as a DesignSpec. Nothing is interpreted: each key becomes the array of the same
    /// name, because any reshaping here would be a place for the two sides to disagree about a
    /// stride while both believing they read the same file.
    /// </summary>
    private static DesignSpec ToSpec(Fixture f)
    {
        var pointCfg = f.Iv("point_configuration");
        ConfigurationSpec? cfg = null;
        if (pointCfg is not null)
        {
            cfg = new ConfigurationSpec
            {
                Count = f.Scalar("n_configurations", 1),
                IndexIncident = f.Cv("cfg_index_incident")!,
                IndexExit = f.Cv("cfg_index_exit")!,
                Reversed = f.Iv("cfg_reversed"),
            };
        }

        return new DesignSpec
        {
            LayerThickness = f.Dv("layer_thickness"),
            LayerMaterial = f.Iv("layer_material"),
            MaterialCount = f.Scalar("n_materials", 1),
            MaterialIndex = f.Cv("material_index")!,
            MaterialStress = f.Dv("material_stress"),
            Wavelength = f.Dv("wavelength")!,
            PointWavelength = f.Iv("point_wavelength")!,
            PointAngle = f.Dv("point_angle")!,
            IndexIncident = cfg is null ? f.Cv("index_incident") : null,
            IndexExit = cfg is null ? f.Cv("index_exit") : null,
            Configurations = cfg,
            PointConfiguration = pointCfg,
            TrialCount = f.Scalar("n_trials", 0),
            TrialLayerThickness = f.Dv("trial_layer_thickness"),
            TrialMaterialIndex = f.Cv("trial_material_index"),
            VariableKind = f.Iv("variable_kind"),
            VariableLayer = f.Iv("variable_layer"),
            VariableMaterial = f.Iv("variable_layer") is { } vl ? new int[vl.Length] : null,
            VariableCoefficient = f.Iv("variable_layer") is { } vl2 ? new int[vl2.Length] : null,
            Operands = new OperandSpec
            {
                Code = f.Iv("code")!,
                Target = f.Dv("target")!,
                Weight = f.Dv("weight")!,
                Point = f.Iv("point"),
                PointFirst = f.Iv("point_first"),
                PointCount = f.Iv("point_count"),
                PointIndex = f.Iv("point_index"),
                PointWeight = f.Dv("point_weight"),
                Comparison = f.Iv("comparison"),
                TargetHi = f.Dv("target_hi"),
                Tolerance = f.Dv("tolerance"),
                Scale = f.Dv("scale"),
                Reduction = f.Iv("reduction"),
                ReductionParam = f.Dv("reduction_param"),
                InputFirst = f.Iv("input_first"),
                InputCount = f.Iv("input_count"),
                InputIndex = f.Iv("input_index"),
                TrialReduction = f.Iv("trial_reduction"),
                DeltaConvention = f.Scalar("delta_convention",
                                           (int)TfDeltaConvention.TF_DELTA_PRINCIPAL),
                ExtrapolationPolicy = f.Scalar("extrapolation_policy",
                                               (int)TfExtrapolationPolicy.TF_EXTRAPOLATION_FLAG),
            },
        };
    }

    public static void TheManagedPathReproducesEveryFixture(Harness h)
    {
        h.Section("every fixture the native suite runs, through Design, bit for bit");

        var fixtures = Read();
        h.Expect(fixtures.Count > 0, "the fixture file is embedded and parsed");

        int values = 0, residuals = 0, jacobians = 0;

        foreach (var f in fixtures)
        {
            var expectValue = f.Dv("expect_value");
            var expectResidual = f.Dv("expect_residual");
            var expectMerit = f.Dv("expect_merit");
            var expectJacobian = f.Dv("expect_jacobian");

            if (expectValue is null || expectResidual is null || expectMerit is null)
            {
                h.Fail($"{f.Name}: the fixture file carries no recorded expectation");
                continue;
            }

            Design d;
            try { d = new Design(ToSpec(f)); }
            catch (Exception e)
            {
                h.Fail($"{f.Name}: Design would not accept the fixture — {e.Message}");
                continue;
            }

            try { d.Plan(); }
            catch (ThinFilmException e)
            {
                h.Fail($"{f.Name}: tf_plan refused through Design — {e.Status}");
                continue;
            }

            d.Evaluate();

            h.Pin(d.Value.Length, expectValue.Length, $"{f.Name}: value count");
            for (int k = 0; k < d.Value.Length && k < expectValue.Length; ++k, ++values)
                PinRecorded(h, d.Value[k], expectValue[k], $"{f.Name}: value[{k}]");

            for (int k = 0; k < d.Residual.Length && k < expectResidual.Length; ++k, ++residuals)
                PinRecorded(h, d.Residual[k], expectResidual[k], $"{f.Name}: residual[{k}]");

            PinRecorded(h, d.Merit, expectMerit[0], $"{f.Name}: merit");

            if (expectJacobian is not null && d.VariableCount > 0)
            {
                d.EvaluateJacobian();

                // The Jacobian entry point recomputes the values, so they must still agree — a
                // binding that wired one entry point correctly and the other loosely would show
                // up exactly here.
                for (int k = 0; k < d.Value.Length && k < expectValue.Length; ++k)
                    PinRecorded(h, d.Value[k], expectValue[k],
                               $"{f.Name}: value[{k}] from the Jacobian call");

                h.Pin(d.Jacobian.Length, expectJacobian.Length, $"{f.Name}: jacobian size");
                for (int k = 0; k < d.Jacobian.Length && k < expectJacobian.Length; ++k, ++jacobians)
                    PinRecorded(h, d.Jacobian[k], expectJacobian[k], $"{f.Name}: jacobian[{k}]");
            }
        }

        h.Note($"{fixtures.Count} fixtures: {values} values, {residuals} residuals, " +
               $"{jacobians} Jacobian entries, all to 1e-12");
    }

    /// <summary>A recorded number, to 1e-12 relative rather than bit for bit: the file was recorded
    /// with MSVC, and another compiler orders the same arithmetic differently in the last figures
    /// (GCC, 2026-10-24: the 14th to 16th). As test_fixtures.cpp, which reads the same file.</summary>
    private static void PinRecorded(Harness h, double got, double want, string what)
    {
        double scale = Math.Max(Math.Abs(got), Math.Abs(want));
        h.Expect(got == want || Math.Abs(got - want) <= 1e-12 * scale, $"{what} is {got:R}, recorded {want:R}");
    }

    /// <summary>
    /// THE GUARD ON THE GUARD. If the fixture file were silently empty, or every expectation were
    /// zero, the test above would pass and prove nothing. This checks the fixtures have content
    /// worth comparing: several distinct non-zero values, and the breadth the file claims.
    /// </summary>
    public static void TheFixturesAreWorthComparing(Harness h)
    {
        h.Section("the fixtures carry real numbers and real breadth, so agreement means something");

        var fixtures = Read();
        h.Expect(fixtures.Count >= 8, "there are at least eight fixtures");

        var distinct = new HashSet<double>();
        int withJacobian = 0, withSpans = 0, withTrials = 0, withConfigurations = 0, absorbing = 0;

        foreach (var f in fixtures)
        {
            foreach (var v in f.Dv("expect_value") ?? []) distinct.Add(v);
            if (f.Dv("expect_jacobian") is not null) ++withJacobian;
            if (f.Iv("point_index") is not null) ++withSpans;
            if (f.Scalar("n_trials", 0) > 0) ++withTrials;
            if (f.Iv("point_configuration") is not null) ++withConfigurations;
            var mi = f.Cv("material_index");
            if (mi is not null && Array.Exists(mi, c => c.im != 0.0)) ++absorbing;
        }

        h.Expect(distinct.Count >= 20, $"at least twenty distinct values ({distinct.Count})");
        h.Expect(!distinct.Contains(0.0) || distinct.Count > 20, "and they are not mostly zero");
        h.Expect(withJacobian >= 5, $"at least five fixtures carry a Jacobian ({withJacobian})");
        h.Expect(withSpans >= 2, $"spans and reductions are exercised ({withSpans})");
        h.Expect(withTrials >= 1, $"the trial axis is exercised ({withTrials})");
        h.Expect(withConfigurations >= 1, $"configurations are exercised ({withConfigurations})");
        h.Expect(absorbing >= 1, $"an absorbing stack forces the general path ({absorbing})");
    }
}
