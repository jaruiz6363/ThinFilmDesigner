// BackSurfaceTests.cs — Edit > System Parameters: the substrate's back surface and where angles of
// incidence are measured (ABI 10).
//
// The user (2026-10-18): "Somewhere we need to specify whether the substrate backside be included in
// the calculations ... The substrate thickness in mm needs to be entered ... we need to specify
// whether the AOI is defined in Air or the ambient material", and "some parameters will not include
// the substrate backside by default, independent of the include backside setting, Tan Psi, Psi, Cos
// Delta, Delta, Phase" - "the main parameters affected are reflection and transmission".
//
// The oracles: an uncoated plate against the textbook incoherent sum, worked here with
// System.Numerics.Complex; the thickness Jacobian and the needle map against central differences
// with the back surface on; and the phase-type operands, which must not move at all.

using System.Numerics;
using System.Text.Json.Nodes;
using ThinFilm.Api;
using ThinFilm.Cli;
using ThinFilm.Mcp;

namespace ThinFilm.Native.Tests;

public static class BackSurfaceTests
{
    private static readonly double[] Grid = [550.0, 600.0];

    /// <summary>A plate in air, its layers given as (material, nm); materials 0 = H (n 2.3), 1 = L
    /// (1.46), 2 = an absorber (2.0 - 0.05i). Every operand reads every point.</summary>
    private static DesignDocument Plate((int M, double D)[] layers, double substrateK, string[] codes,
                                        double[] anglesDeg, bool back, double mm = 1.0, double incidentN = 1.0)
    {
        var points = new List<PointDocument>();
        foreach (var a in anglesDeg)
            for (int w = 0; w < Grid.Length; ++w) points.Add(new PointDocument(w, a));
        var ops = new List<OperandDocument>();
        foreach (var c in codes)
            for (int p = 0; p < points.Count; ++p)
                ops.Add(new OperandDocument(c, "EQUAL", 0.3, 1.0, Points: [p]));
        return new DesignDocument
        {
            Name = "plate",
            Wavelength = Grid,
            Materials = [MaterialDocument.Constant("H", 2.3, 0.0, Grid.Length),
                         MaterialDocument.Constant("L", 1.46, 0.0, Grid.Length),
                         MaterialDocument.Constant("A", 2.0, 0.05, Grid.Length)],
            Layers = layers.Select(l => new LayerDocument(l.M, l.D)).ToArray(),
            Points = points.ToArray(),
            Incident = MaterialDocument.Constant("ambient", incidentN, 0.0, Grid.Length),
            Exit = MaterialDocument.Constant("glass", 1.52, substrateK, Grid.Length),
            Operands = ops.ToArray(),
            BackSurface = back,
            SubstrateThicknessMm = mm,
        };
    }

    private static Design Evaluated(DesignDocument doc)
    {
        var d = new Design(doc.ToSpec());
        d.Plan();
        d.Evaluate();
        return d;
    }

    /// <summary>One interface's (R, T) by Fresnel, for the textbook sum. Admittances as the engine
    /// takes them: kappa = sqrt(n^2 - (n0 sin)^2), eta_s = kappa, eta_p = n^2 / kappa.</summary>
    private static (double R, double T) Interface(Complex na, Complex nb, double u, bool p)
    {
        static Complex Kappa(Complex n, double u) => Complex.Sqrt(n * n - u);
        Complex ea = p ? na * na / Kappa(na, u) : Kappa(na, u);
        Complex eb = p ? nb * nb / Kappa(nb, u) : Kappa(nb, u);
        var r = (ea - eb) / (ea + eb);
        return (r.Magnitude * r.Magnitude, 4 * ea.Real * eb.Real / Math.Pow((ea + eb).Magnitude, 2));
    }

    public static void AnUncoatedPlateIsTheTextbookSum(Harness h)
    {
        h.Section("an uncoated plate: R = R1 + T1^2 R1 a^2 / (1 - R1^2 a^2), T = T1^2 a / (1 - R1^2 a^2)");

        string[] codes = ["RS", "RP", "TS", "TP"];
        foreach (double k in new[] { 0.0, 1e-5 })
            foreach (double deg in new[] { 0.0, 45.0 })
            {
                var d = Evaluated(Plate([], k, codes, [deg], back: true, mm: 1.0));
                var off = Evaluated(Plate([], k, codes, [deg], back: false));
                double u = Math.Pow(Math.Sin(deg * Math.PI / 180), 2);
                var ns = new Complex(1.52, -k);
                bool ok = true, front = true;
                for (int w = 0; w < Grid.Length; ++w)
                    for (int pol = 0; pol < 2; ++pol)
                    {
                        var (r1, t1) = Interface(1.0, ns, u, pol == 1);
                        // Im(kappa) as Im(kappa^2) / 2 Re(kappa), which keeps its digits for a small k.
                        var kappa = Complex.Sqrt(ns * ns - u);
                        double kim = (ns * ns).Imaginary / (2 * kappa.Real);
                        double a = Math.Exp(-4 * Math.PI * Math.Abs(kim) * 1e6 / Grid[w]);
                        // From inside, the front face reflects what the back face does: same pair.
                        double den = 1 - r1 * r1 * a * a;
                        double R = r1 + t1 * t1 * r1 * a * a / den, T = t1 * t1 * a / den;
                        int rRow = pol * 2 + w, tRow = (2 + pol) * 2 + w;
                        ok &= Math.Abs(d.Value[rRow] - R) < 1e-13 && Math.Abs(d.Value[tRow] - T) < 1e-13;
                        front &= Math.Abs(off.Value[rRow] - r1) < 1e-13 && Math.Abs(off.Value[tRow] - t1) < 1e-13;
                    }
                h.Expect(ok, $"k = {k:0e0}, {deg}°: Rs, Rp, Ts, Tp are the incoherent sum "
                             + $"(Ts {d.Value[4]:0.000000} at 550 nm)");
                h.Expect(front, $"k = {k:0e0}, {deg}°: and without the back surface, one interface");
            }

        var glass = Evaluated(Plate([], 0.0, ["TS"], [0.0], back: true));
        h.Expect(Math.Abs(glass.Value[0] - 2 * 1.52 / (1.52 * 1.52 + 1)) < 1e-14,
                 $"a lossless plate at normal incidence transmits 2n/(n^2 + 1) = {glass.Value[0]:0.000000}");
        var thick = Evaluated(Plate([], 1e-5, ["TS"], [0.0], back: true, mm: 3.0));
        var thin = Evaluated(Plate([], 1e-5, ["TS"], [0.0], back: true, mm: 1.0));
        h.Expect(thick.Value[0] < thin.Value[0] && Evaluated(Plate([], 0.0, ["TS"], [0.0], back: true, mm: 3.0)).Value[0] == glass.Value[0],
                 "a thicker absorbing substrate transmits less; a lossless one does not care how thick it is");
    }

    /// <summary>Found while building the back surface: DesignDocument and the templates handed the
    /// engine n - ik, and the ABI takes n + ik, so an absorber gained light (Nb2O5 at 300 nm showed
    /// TA 2.2 and AA -2.1).</summary>
    public static void AnAbsorberAbsorbs(Harness h)
    {
        h.Section("a material with k > 0 absorbs: A = 1 - R - T is positive");

        var d = Evaluated(Plate([(2, 40.0), (1, 90.0)], 0.0, ["AS", "AP", "TS", "RS"], [0.0, 50.0], back: false));
        h.Expect(Enumerable.Range(0, 8).All(o => d.Value[o] > 1e-3), $"AS and AP above zero at every point (AS {d.Value[0]:0.0000})");
        h.Expect(Enumerable.Range(0, 4).All(p => d.Value[12 + p] + d.Value[8 + p] + d.Value[p] is > 0.999999999 and < 1.000000001),
                 "and Rs + Ts + As is one");
        var sub = Evaluated(Plate([], 1e-4, ["TS", "AS"], [0.0], back: false));
        h.Expect(sub.Value[0] > 0.95 && Math.Abs(sub.Value[2]) < 1e-12,
                 $"an absorbing substrate: T into it is 1 - R, positive ({sub.Value[0]:0.000000})");

        var spec = Plate([(2, 40.0)], 0.0, ["TS"], [0.0], back: false).ToSpec();
        h.Expect(spec.MaterialIndex[2 * Grid.Length].im == 0.05, "the ABI is handed n + ik, k as the document stores it");
    }

    public static void PhaseQuantitiesDescribeTheCoatedSurfaceAlone(Harness h)
    {
        h.Section("Tan Psi, Psi, Cos Delta, Delta and the phases ignore the back surface");

        (int, double)[] stack = [(0, 60.0), (2, 12.0), (1, 95.0), (0, 40.0)];
        string[] phase = ["R_TANPSI", "R_COSDELTA", "T_TANPSI", "T_COSDELTA", "R_PSI", "R_DELTA",
                          "T_PSI", "T_DELTA", "R_PHASE_S", "R_PHASE_P", "T_PHASE_S", "T_PHASE_P"];
        var on = Evaluated(Plate(stack, 1e-5, phase, [0.0, 50.0], back: true));
        var off = Evaluated(Plate(stack, 1e-5, phase, [0.0, 50.0], back: false));
        h.Expect(Enumerable.Range(0, on.OperandCount).All(o => on.Value[o] == off.Value[o]),
                 $"all {phase.Length} codes, at 0° and 50°, are bit for bit the same with it on");

        string[] intensity = ["RS", "RP", "TS", "TP", "RA", "TA", "AS", "AP", "AA", "ODS", "ODA",
                              "R_NPOL", "T_NPOL", "R_EXT_DB", "T_EXT_DB", "R_DIAT", "T_DIAT"];
        on = Evaluated(Plate(stack, 1e-5, intensity, [0.0, 50.0], back: true));
        off = Evaluated(Plate(stack, 1e-5, intensity, [0.0, 50.0], back: false));
        int moved = Enumerable.Range(0, intensity.Length).Count(c =>
            Enumerable.Range(0, 4).Any(p => on.Value[c * 4 + p] != off.Value[c * 4 + p]));
        h.Pin(moved, intensity.Length, "every R and T quantity, and those built on them, moves");
    }

    public static void TheJacobianAndNeedleMapMatchDifferences(Harness h)
    {
        h.Section("with the back surface: the thickness Jacobian and the needle map against central differences");

        (int M, double D)[] stack = [(0, 60.0), (2, 12.0), (1, 95.0), (0, 40.0)];
        string[] codes = ["RS", "RP", "TS", "TP", "RA", "TA", "AS", "AA", "ODA", "R_NPOL", "T_DIAT", "T_EXT_DB", "R_TANPSI"];
        var doc = Plate(stack, 2e-5, codes, [0.0, 35.0], back: true, mm: 0.5);
        var d = new Design(doc.ToSpec());
        d.Plan();
        d.EvaluateJacobian();
        var jac = d.Jacobian.ToArray();
        int m = d.Report.n_residuals;

        double worst = 0.0;
        for (int v = 0; v < stack.Length; ++v)
        {
            const double step = 1e-4;
            var up = stack.ToArray(); up[v].D += step;
            var dn = stack.ToArray(); dn[v].D -= step;
            var a = Evaluated(Plate(up, 2e-5, codes, [0.0, 35.0], back: true, mm: 0.5));
            var b = Evaluated(Plate(dn, 2e-5, codes, [0.0, 35.0], back: true, mm: 0.5));
            for (int o = 0; o < d.OperandCount; ++o)
            {
                double fd = (a.Value[o] - b.Value[o]) / (2 * step);
                double an = jac[v * m + d.ResidualRow(o)];
                worst = Math.Max(worst, Math.Abs(fd - an) / Math.Max(1e-3, Math.Abs(fd)));
            }
        }
        h.Expect(worst < 1e-6, $"every column, {codes.Length} codes at two angles, agrees ({worst:0.0e0} relative)");

        // The needle map: d(sum r^2)/d(eps) for a layer of L inserted 30 nm into the first H layer,
        // and of A at 50 nm into the L layer, against inserting it for real.
        double SumSq(DesignDocument x) { var e = Evaluated(x); return e.Residual.ToArray().Sum(r => r * r); }
        DesignDocument Split(int layer, double at, int material, double eps)
        {
            var l = stack.ToList();
            var host = l[layer];
            l.RemoveAt(layer);
            l.InsertRange(layer, [(host.M, at), (material, eps), (host.M, host.D - at)]);
            return Plate(l.ToArray(), 2e-5, codes, [0.0, 35.0], back: true, mm: 0.5);
        }
        double[] depths = [30.0, 60.0 + 12.0 + 50.0];
        var map = d.NeedleMap(depths, [1, 2]);
        const double eps = 1e-4;
        double fd0 = (SumSq(Split(0, 30.0, 1, eps)) - SumSq(Split(0, 30.0, 1, -eps))) / (2 * eps);
        double fd1 = (SumSq(Split(2, 50.0, 2, eps)) - SumSq(Split(2, 50.0, 2, -eps))) / (2 * eps);
        h.Expect(Math.Abs(map[0] - fd0) < 1e-6 * Math.Max(1.0, Math.Abs(fd0)),
                 $"L at 30 nm: map {map[0]:0.0000000e0}, difference {fd0:0.0000000e0}");
        h.Expect(Math.Abs(map[2 + 1] - fd1) < 1e-6 * Math.Max(1.0, Math.Abs(fd1)),
                 $"the absorber at 122 nm: map {map[3]:0.0000000e0}, difference {fd1:0.0000000e0}");
    }

    public static void AnglesAreGivenInAirOrInTheMedium(Harness h)
    {
        h.Section("angles of incidence in air, or in the incident medium");

        var inGlass = Plate([(0, 80.0), (1, 120.0)], 0.0, ["RS", "RP"], [45.0], back: false, incidentN: 1.52);
        double medium = Math.Asin(Math.Sin(Math.PI / 4) / 1.52) * 180 / Math.PI;
        var a = Evaluated(inGlass with { AngleInAir = true });
        var b = Evaluated(Plate([(0, 80.0), (1, 120.0)], 0.0, ["RS", "RP"], [medium], back: false, incidentN: 1.52));
        h.Expect(Enumerable.Range(0, a.OperandCount).All(o => Math.Abs(a.Value[o] - b.Value[o]) < 1e-14),
                 $"45° in air is {medium:0.###}° in glass of 1.52");
        var c = Evaluated(inGlass);
        h.Expect(Math.Abs(c.Value[0] - a.Value[0]) > 1e-3, "unticked, 45° is 45° in the glass itself - a cube's angle");

        var api = new ThinFilmApi();
        string id = api.Load(Plate([(0, 80.0)], 0.0, ["RS"], [60.0], back: false, incidentN: 0.8).ToJson()).Value!.Id;
        var refused = api.SetSystem(id, new SystemChange(AngleInAir: true));
        h.Expect(!refused.Ok && refused.Error!.Contains("cannot be reached") && !api.Describe(id).Value!.AngleInAir,
                 $"an angle that cannot exist in the medium is refused, and nothing changes ({refused.Error})");

        var set = api.SetSystem(id, new SystemChange(BackSurface: true, SubstrateMm: 2.5));
        h.Expect(set.Ok && set.Value!.BackSurface && set.Value.SubstrateThicknessMm == 2.5, "SetSystem sets the back surface and thickness");
        h.Expect(!api.SetSystem(id, new SystemChange(SubstrateMm: 0.0)).Ok, "and refuses a substrate of no thickness");
        var saved = DesignDocument.FromJson(api.Save(id).Value!);
        h.Expect(saved.BackSurface && saved.SubstrateThicknessMm == 2.5 && !saved.AngleInAir, "they are saved with the design");
        h.Expect(DesignDocument.FromJson(Plate([], 0, ["RS"], [0.0], false).ToJson()) is { BackSurface: false, SubstrateThicknessMm: 1.0, AngleInAir: false },
                 "a file without them reads as before: no back surface, 1 mm, angles in the medium");
    }

    public static void TheCliAndTheMcpSetThem(Harness h)
    {
        h.Section("thinfilm system, and the MCP's set_system");

        var dir = Path.Combine(Path.GetTempPath(), "thinfilm-system-tests");
        Directory.CreateDirectory(dir);
        string design = Path.Combine(dir, "plate.tfd");
        File.WriteAllText(design, Plate([(0, 60.0), (1, 95.0)], 1e-5, ["TS"], [0.0], back: false).ToJson());
        (int Code, string Out, string Err) Run(params string[] args)
        {
            var o = new StringWriter();
            var e = new StringWriter();
            int code = CommandLine.Run(args, o, e);
            return (code, o.ToString(), e.ToString());
        }
        var shown = Run("system", design);
        h.Expect(shown.Code == 0 && shown.Out.Contains("coated surface alone") && shown.Out.Contains("incident medium"),
                 $"with no change it says what they are ({shown.Out.Trim()})");
        var set = Run("system", design, "--back-surface", "yes", "--substrate-mm", "2", "--angles", "air");
        var saved = DesignDocument.FromJson(File.ReadAllText(design));
        h.Expect(set.Code == 0 && saved is { BackSurface: true, SubstrateThicknessMm: 2.0, AngleInAir: true },
                 $"--back-surface, --substrate-mm and --angles are written to the file ({set.Err})");
        h.Expect(Run("system", design, "--angles", "sideways").Code != 0, "--angles wants air or medium");

        var tools = new Tools();
        h.Expect(tools.Describe().Any(t => t!["name"]!.GetValue<string>() == "set_system"), "set_system is listed");
        var (text, failed) = tools.Call("load_design", new JsonObject { ["document"] = File.ReadAllText(design) });
        string id = JsonNode.Parse(text)!["Id"]!.GetValue<string>();
        (text, failed) = tools.Call("set_system", new JsonObject
        {
            ["design"] = id, ["back_surface"] = false, ["angle_in_air"] = false,
        });
        var now = JsonNode.Parse(text)!;
        h.Expect(!failed && now["BackSurface"]!.GetValue<bool>() == false && now["AngleInAir"]!.GetValue<bool>() == false
                 && now["SubstrateThicknessMm"]!.GetValue<double>() == 2.0,
                 $"set_system turns them off and keeps the thickness ({text[..Math.Min(120, text.Length)]})");
    }
}
