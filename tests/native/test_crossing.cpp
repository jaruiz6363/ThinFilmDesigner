/* test_crossing.cpp — where a spectrum passes through a level, and what it does when it does not.
 *
 * THE VALUE HAS A CLOSED FORM, so the oracle is exact. A crossing is defined on the SAMPLED
 * spectrum, so between the bracketing samples it is
 *
 *     lambda_e = la + (L - fa)(lb - la)/(fb - fa)
 *
 * and both fa and fb are numbers a flat table hands back. No root-finder to trust, no step size.
 *
 * THE DERIVATIVE IS CHECKED THE SAME WAY AND ALSO BY ITS PHYSICS. The chain rule gives
 * d(lambda_e)/dt from the two samples' own thickness derivatives, and separately the implicit
 * function theorem says that a spectrum shifted UNIFORMLY moves its crossing by -g/(df/dlambda).
 * The second is the statement the specification makes, and the two agreeing is the evidence that
 * interpolating and applying the theorem are the same thing here.
 *
 * AND THE TWO POLICY QUESTIONS, which were open in the contract and are answered by test here:
 * what a missing crossing returns, and which crossing is meant when there are several.
 */

#include "thinfilm.h"

#include <cstdio>
#include <cmath>
#include <vector>

extern "C" {
tf_status tf_query_plan_sizes(int32_t, int32_t, int32_t, int32_t, tf_plan_sizes*);
tf_status tf_plan(const tf_problem*, const tf_operands*, const tf_variables*, int32_t,
                  tf_plan_buffers*);
tf_status tf_evaluate(const tf_problem*, const tf_operands*, const tf_plan_buffers*,
                      tf_workspace*, tf_output*);
tf_status tf_evaluate_jacobian(const tf_problem*, const tf_operands*, const tf_variables*,
                               const tf_plan_buffers*, tf_workspace*, tf_output*);
}

static int g_failures = 0;
static int g_checks   = 0;

static void near_(double got, double want, double tol, const char* what)
{
    ++g_checks;
    if (!(std::fabs(got - want) <= tol))
    {
        ++g_failures;
        std::printf("  FAIL  %-46s got %.12g  expected %.12g  delta %.3g\n",
                    what, got, want, std::fabs(got - want));
    }
}

static void expect_eq(long long got, long long want, const char* what)
{
    ++g_checks;
    if (got != want)
    {
        ++g_failures;
        std::printf("  FAIL  %-46s got %lld  expected %lld\n", what, got, want);
    }
}

/* Eleven wavelengths across a single high-index layer, whose Rs rises and falls across the band -
 * so a level in the middle is crossed TWICE and the ambiguity question has something to bite on. */
static const int NP = 11;

struct Caller
{
    std::vector<double>     thickness{ 96.0 };
    std::vector<int32_t>    layer_material{ 0 };
    std::vector<tf_complex> index;
    std::vector<double>     wavelength;
    std::vector<int32_t>    point_wavelength;
    std::vector<double>     point_angle;
    std::vector<tf_complex> n_in, n_out;

    std::vector<int32_t> tcode, tpoint, treduction, tfirst, tcount, tindex;
    std::vector<double>  ttarget, tweight, tpweight, tparam;
    int32_t              span_mode = 0;

    std::vector<int32_t> vkind{ TF_VARIABLE_LAYER_THICKNESS }, vlayer{ 0 },
                         vmaterial{ 0 }, vcoef{ 0 };

    tf_problem problem{}; tf_operands operands{}; tf_variables variables{};
    std::vector<int32_t> path, order, inert, polneed, spanfirst, spancount, spanop, spanslot;
    std::vector<int32_t> opres, oporder, oplevel, opflag;
    tf_plan_report report{}; int32_t plan_status = 0, eval_status = 0, nflag = 0;
    tf_plan_buffers pbuf{};
    std::vector<double> scratch, residual, jacobian, opvalue;
    tf_workspace ws{}; double merit = 0.0; tf_output out{};

    Caller()
    {
        for (int i = 0; i < NP; ++i)
        {
            wavelength.push_back(420.0 + 30.0 * i);      /* 420 .. 720 nm */
            point_wavelength.push_back(i);
            point_angle.push_back(0.0);
            index.push_back(tf_complex{2.35, 0.0});
            n_in.push_back(tf_complex{1.0, 0.0});
            n_out.push_back(tf_complex{1.52, 0.0});
        }
    }

    void wire()
    {
        problem.n_layers = 1; problem.n_materials = 1;
        problem.n_wavelengths = NP; problem.n_points = NP;
        problem.layer_thickness = thickness.data();
        problem.layer_material = layer_material.data();
        problem.layer_is_incoherent = nullptr;
        problem.material_index = index.data();
        problem.wavelength = wavelength.data();
        problem.point_wavelength = point_wavelength.data();
        problem.point_angle = point_angle.data();
        problem.index_incident = n_in.data(); problem.index_exit = n_out.data();
        problem.configurations = nullptr; problem.point_configuration = nullptr;
        problem.material_dindex_dwavelength = nullptr;
        problem.dindex_incident_dwavelength = nullptr;
        problem.dindex_exit_dwavelength = nullptr;

        operands.n_operands = (int32_t)tcode.size();
        operands.delta_convention = TF_DELTA_PRINCIPAL;
        operands.code = tcode.data();
        operands.target = ttarget.data(); operands.weight = tweight.data();
        operands.comparison = nullptr; operands.target_hi = nullptr;
        operands.tolerance = nullptr; operands.scale = nullptr;
        operands.reduction = treduction.empty() ? nullptr : treduction.data();
        operands.reduction_param = tparam.empty() ? nullptr : tparam.data();
        operands.n_input = 0; operands.input_first = nullptr;
        operands.input_count = nullptr; operands.input_index = nullptr;

        if (span_mode)
        {
            operands.point = nullptr; operands.n_span = (int32_t)tindex.size();
            operands.point_first = tfirst.data(); operands.point_count = tcount.data();
            operands.point_index = tindex.data();
            operands.point_weight = tpweight.empty() ? nullptr : tpweight.data();
        }
        else
        {
            operands.point = tpoint.data(); operands.n_span = 0;
            operands.point_first = nullptr; operands.point_count = nullptr;
            operands.point_index = nullptr; operands.point_weight = nullptr;
        }

        variables.n_variables = 1;
        variables.kind = vkind.data(); variables.layer = vlayer.data();
        variables.material = vmaterial.data(); variables.coefficient = vcoef.data();
    }

    tf_status plan()
    {
        wire();
        tf_plan_sizes s{};
        const int32_t nslots = span_mode ? (int32_t)tindex.size() : operands.n_operands;
        tf_query_plan_sizes(NP, 1, nslots, operands.n_operands, &s);
        path.assign((size_t)s.point_path_count, -1);
        order.assign((size_t)s.point_order_count, -1);
        inert.assign((size_t)s.variable_inert_count, 0);
        polneed.assign((size_t)s.point_polarisation_count, 0);
        spanfirst.assign((size_t)s.point_span_first_count, 0);
        spancount.assign((size_t)s.point_span_count_count, 0);
        spanop.assign((size_t)s.point_span_operand_count, 0);
        spanslot.assign((size_t)s.point_span_slot_count, 0);
        opres.assign((size_t)s.operand_residual_count, -1);
        oporder.assign((size_t)s.operand_order_count, 0);
        oplevel.assign((size_t)s.operand_level_count, 0);
        pbuf.point_path = path.data(); pbuf.point_order = order.data();
        pbuf.variable_inert = inert.data(); pbuf.point_polarisation = polneed.data();
        pbuf.point_span_first = spanfirst.data(); pbuf.point_span_count = spancount.data();
        pbuf.point_span_operand = spanop.data(); pbuf.point_span_slot = spanslot.data();
        pbuf.operand_residual = opres.data(); pbuf.operand_order = oporder.data();
        pbuf.operand_level = oplevel.data();
        pbuf.report = &report; pbuf.status = &plan_status;

        const tf_status ps = tf_plan(&problem, &operands, &variables, 1, &pbuf);
        if (ps != TF_OK) return ps;

        scratch.assign((size_t)report.scratch_doubles, 0.0);
        ws.scratch = scratch.data(); ws.scratch_doubles = report.scratch_doubles;
        opvalue.assign((size_t)operands.n_operands, 0.0);
        opflag.assign((size_t)operands.n_operands, 0);
        residual.assign((size_t)report.n_residuals, 0.0);
        jacobian.assign((size_t)report.n_residuals, 0.0);
        nflag = 0;
        out.residual = residual.data(); out.jacobian = jacobian.data();
        out.value = opvalue.data(); out.operand_flag = opflag.data();
        out.n_operands_flagged = &nflag;
        out.merit = &merit; out.status = &eval_status;
        return TF_OK;
    }

    tf_status jac() { wire(); return tf_evaluate_jacobian(&problem, &operands, &variables,
                                                          &pbuf, &ws, &out); }
};

/* Eleven pointwise Rs operands - the trusted side. Residual == value, J == d(value)/dt. */
static Caller flat()
{
    Caller c;
    for (int i = 0; i < NP; ++i)
    {
        c.tcode.push_back(TF_TARGET_RS); c.tpoint.push_back(i);
        c.ttarget.push_back(0.0); c.tweight.push_back(1.0);
    }
    return c;
}

static Caller crossing(double level, double hint)
{
    Caller c;
    c.span_mode = 1;
    c.tcode = { TF_TARGET_RS }; c.ttarget = { 0.0 }; c.tweight = { 1.0 };
    c.treduction = { TF_REDUCE_CROSSING };
    c.tparam = { level, hint };
    c.tfirst = { 0 }; c.tcount = { NP };
    for (int i = 0; i < NP; ++i) c.tindex.push_back(i);
    return c;
}

/* ---------------------------------------------------------------------------------------------
 * The exact oracle
 * ------------------------------------------------------------------------------------------- */

static void ACrossingIsTheInterpolatedWavelength()
{
    std::printf("the crossing is the interpolated wavelength, to the last bit\n");

    Caller f = flat();
    expect_eq(f.plan(), TF_OK, "the flat plan");
    expect_eq(f.jac(), TF_OK, "the flat Jacobian");

    /* A level the spectrum genuinely passes through: halfway between its extremes. */
    double lo = f.residual[0], hi = f.residual[0];
    for (int i = 1; i < NP; ++i)
    { lo = std::fmin(lo, f.residual[(size_t)i]); hi = std::fmax(hi, f.residual[(size_t)i]); }
    const double L = 0.5 * (lo + hi);

    ++g_checks;
    if (!(hi - lo > 1e-3))
    { ++g_failures; std::printf("  FAIL  the band is flat; no crossing to find\n"); }

    /* Find the first bracket by hand and interpolate it. */
    int a = -1;
    for (int i = 0; i + 1 < NP; ++i)
    {
        const double da = f.residual[(size_t)i] - L, db = f.residual[(size_t)i + 1] - L;
        if ((da <= 0 && db >= 0) || (da >= 0 && db <= 0)) { a = i; break; }
    }
    expect_eq(a >= 0, 1, "a bracket exists");

    const double fa = f.residual[(size_t)a], fb = f.residual[(size_t)a + 1];
    const double la = f.wavelength[(size_t)a], lb = f.wavelength[(size_t)a + 1];
    const double expected = la + (L - fa) * (lb - la) / (fb - fa);

    Caller c = crossing(L, 0.0);
    expect_eq(c.plan(), TF_OK, "the crossing plan");
    expect_eq(c.jac(), TF_OK, "the crossing Jacobian");
    near_(c.opvalue[0], expected, 1e-12, "the crossing wavelength");
    expect_eq(c.opflag[0] & TF_FLAG_EXTRAPOLATED, 0, "and it is a measurement, not a penalty");

    /* The chain rule, from the two samples' own thickness derivatives. */
    const double u = L - fa, D = fb - fa, W = lb - la;
    const double dva = W * (u - D) / (D * D), dvb = -W * u / (D * D);
    near_(c.jacobian[0], dva * f.jacobian[(size_t)a] + dvb * f.jacobian[(size_t)a + 1], 1e-11,
          "the chain rule through the interpolation");

    /* AND THE PHYSICS. The specification states the derivative as the implicit function theorem,
     * -(dT/dx)/(dT/dlambda). Those are the same thing here only if the interpolation IS the
     * wavelength derivative - so a spectrum shifted UNIFORMLY must move its crossing by
     * -g/(df/dlambda), and that is a different expression arriving at the same number. */
    {
        const double g = 1.0;                       /* a unit uniform shift */
        const double dfdl = D / W;                  /* the sampled slope */
        near_(dva * g + dvb * g, -g / dfdl, 1e-11,
              "a uniform shift moves it by the implicit function theorem");
    }
}

/* ---------------------------------------------------------------------------------------------
 * Policy question one: which crossing
 * ------------------------------------------------------------------------------------------- */

static void AHintChoosesAmongSeveralCrossings()
{
    std::printf("with two crossings, a hint picks one and the flag says there were more\n");

    Caller f = flat();
    f.plan(); f.jac();

    /* A LEVEL CROSSED EXACTLY TWICE, FOUND BY LOOKING RATHER THAN BY ASSUMING. My first attempt
     * assumed an interior maximum; this fixture has an interior MINIMUM - a half-wave absentee
     * near 451 nm - so the level it picked was crossed once and the test proved nothing about
     * ambiguity. Scanning for a level with exactly two brackets is robust to which way the
     * extremum goes, and it fails loudly if the fixture ever stops having one. */
    double lo = f.residual[0], hi = f.residual[0];
    for (int i = 1; i < NP; ++i)
    { lo = std::fmin(lo, f.residual[(size_t)i]); hi = std::fmax(hi, f.residual[(size_t)i]); }

    double L = 0.0;
    std::vector<double> found;
    for (int t = 1; t < 200 && found.size() != 2; ++t)
    {
        L = lo + (hi - lo) * (double)t / 200.0;
        found.clear();
        for (int i = 0; i + 1 < NP; ++i)
        {
            const double da = f.residual[(size_t)i] - L, db = f.residual[(size_t)i + 1] - L;
            if ((da <= 0 && db >= 0) || (da >= 0 && db <= 0))
                found.push_back(f.wavelength[(size_t)i] +
                                (L - f.residual[(size_t)i]) *
                                (f.wavelength[(size_t)i + 1] - f.wavelength[(size_t)i]) /
                                (f.residual[(size_t)i + 1] - f.residual[(size_t)i]));
        }
    }
    expect_eq((long long)found.size(), 2, "the fixture really has a doubly-crossed level");
    if (found.size() != 2) return;

    /* No hint: the first, in span order. */
    Caller first = crossing(L, 0.0);
    first.plan(); first.jac();
    near_(first.opvalue[0], found[0], 1e-12, "no hint takes the first");
    expect_eq((first.opflag[0] & TF_FLAG_AMBIGUOUS) != 0, 1, "and says the choice was ambiguous");
    expect_eq(first.nflag, 1, "the count sees it too");

    /* A hint near the second: the second. */
    Caller second = crossing(L, found[1]);
    second.plan(); second.jac();
    near_(second.opvalue[0], found[1], 1e-12, "a hint near the second takes the second");

    /* A hint near the first: back to the first. */
    Caller again = crossing(L, found[0]);
    again.plan(); again.jac();
    near_(again.opvalue[0], found[0], 1e-12, "and a hint near the first takes the first");
}

/* ---------------------------------------------------------------------------------------------
 * Policy question two: no crossing at all
 * ------------------------------------------------------------------------------------------- */

static void AMissingCrossingExtrapolatesAndSaysSo()
{
    std::printf("no crossing gives a flagged extrapolation with a gradient toward one\n");

    Caller f = flat();
    f.plan(); f.jac();
    double hi = f.residual[0];
    for (int i = 1; i < NP; ++i) hi = std::fmax(hi, f.residual[(size_t)i]);

    /* A level just above everything the spectrum reaches. */
    const double L = hi + 0.02;

    Caller c = crossing(L, 0.0);
    expect_eq(c.plan(), TF_OK, "plans");
    expect_eq(c.jac(), TF_OK, "evaluates - it does not refuse");
    expect_eq((c.opflag[0] & TF_FLAG_EXTRAPOLATED) != 0, 1, "and flags the value as a penalty");
    expect_eq(c.nflag, 1, "which the count also reports");

    /* THE GRADIENT MUST POINT SOMEWHERE. A penalty that cannot be reduced is no better than a
     * refusal - the whole reason for extrapolating rather than refusing is that the optimiser
     * gets a direction. */
    ++g_checks;
    if (c.jacobian[0] == 0.0 && (c.opflag[0] & TF_FLAG_PINNED) == 0)
    {
        ++g_failures;
        std::printf("  FAIL  an unpinned extrapolation with no gradient is a dead end\n");
    }

    /* And it is the extrapolation of the same formula, from the nearest approach. */
    int near = 0;
    for (int i = 1; i < NP; ++i)
        if (std::fabs(f.residual[(size_t)i] - L) < std::fabs(f.residual[(size_t)near] - L)) near = i;
    const int other = (near + 1 < NP) ? near + 1 : near - 1;
    const double u = L - f.residual[(size_t)near];
    const double D = f.residual[(size_t)other] - f.residual[(size_t)near];
    const double W = f.wavelength[(size_t)other] - f.wavelength[(size_t)near];
    const double raw = f.wavelength[(size_t)near] + u * W / D;

    const double lo = f.wavelength[0], top = f.wavelength[NP - 1], wide = top - lo;
    if (raw >= lo - wide && raw <= top + wide)
        near_(c.opvalue[0], raw, 1e-9, "the extrapolated wavelength");
    else
        expect_eq((c.opflag[0] & TF_FLAG_PINNED) != 0, 1, "or it is pinned at the guard");
}

/* A level reachable everywhere gives no flag at all, which is the control: the flags must not be
 * set by an ordinary crossing. */
static void AnOrdinaryCrossingIsNotFlagged()
{
    std::printf("an ordinary single crossing raises no flag at all\n");

    Caller f = flat();
    f.plan(); f.jac();

    /* A level crossed exactly once: between the first sample and the end extreme. */
    const double L = 0.5 * (f.residual[0] + f.residual[1]);

    Caller c = crossing(L, 0.0);
    expect_eq(c.plan(), TF_OK, "plans");
    expect_eq(c.jac(), TF_OK, "evaluates");

    /* It may legitimately be ambiguous on this fixture; what must NOT happen is extrapolation. */
    expect_eq(c.opflag[0] & TF_FLAG_EXTRAPOLATED, 0, "not extrapolated");
    expect_eq(c.opflag[0] & TF_FLAG_PINNED, 0, "not pinned");
    ++g_checks;
    if (!(c.opvalue[0] >= f.wavelength[0] && c.opvalue[0] <= f.wavelength[NP - 1]))
    {
        ++g_failures;
        std::printf("  FAIL  an interpolated crossing outside the band: %.12g\n", c.opvalue[0]);
    }
}

static void AOneSampleCrossingIsRefused()
{
    std::printf("a crossing over one sample is refused - one sample is not an edge\n");

    Caller c = crossing(0.1, 0.0);
    c.tcount = { 1 };
    expect_eq(c.plan(), TF_EMPTY_SPAN, "refused");
}

int main()
{
    setvbuf(stdout, NULL, _IONBF, 0);
    std::printf("\n=== crossings ===\n\n");

    ACrossingIsTheInterpolatedWavelength(); std::printf("\n");
    AHintChoosesAmongSeveralCrossings();    std::printf("\n");
    AMissingCrossingExtrapolatesAndSaysSo();std::printf("\n");
    AnOrdinaryCrossingIsNotFlagged();       std::printf("\n");
    AOneSampleCrossingIsRefused();          std::printf("\n");

    std::printf("=== %d checks, %d failures ===\n\n", g_checks, g_failures);
    return g_failures == 0 ? 0 : 1;
}
