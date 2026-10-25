/* test_evaluate.cpp — the evaluator against closed-form optics.
 *
 * Every check here has an answer that can be written down without running the code: the Fresnel
 * reflectance of a bare surface, the quarter-wave and half-wave results, energy conservation, and
 * Brewster's angle. A test that only compared the evaluator with itself would pass with the
 * admittance formula wrong.
 *
 * And the two paths are checked AGAINST EACH OTHER on the same physics, which is the check that
 * catches a structured path that has quietly diverged from the general one.
 */

#include "thinfilm.h"
#include "tf_layer.h"

#include <cstdio>
#include <cmath>
#include <vector>

extern "C" {
tf_status tf_query_plan_sizes(int32_t, int32_t, int32_t, int32_t, tf_plan_sizes*);
tf_status tf_plan(const tf_problem*, const tf_operands*, const tf_variables*, int32_t,
                  tf_plan_buffers*);
tf_status tf_evaluate(const tf_problem*, const tf_operands*, const tf_plan_buffers*,
                      tf_workspace*, tf_output*);
}

static int g_failures = 0;
static int g_checks   = 0;

static void near(double got, double want, double tol, const char* what)
{
    ++g_checks;
    if (!(std::fabs(got - want) <= tol))
    {
        ++g_failures;
        std::printf("  FAIL  %-52s got %.12g  expected %.12g  delta %.3g\n",
                    what, got, want, std::fabs(got - want));
    }
}

static void expect_eq(long long got, long long want, const char* what)
{
    ++g_checks;
    if (got != want)
    {
        ++g_failures;
        std::printf("  FAIL  %-52s got %lld  expected %lld\n", what, got, want);
    }
}

static const double PI = 3.14159265358979323846;

/* A caller. It owns every buffer, as the contract requires; std::vector appears here and never in
 * the engine. */
struct Caller
{
    std::vector<double>     thickness;
    std::vector<int32_t>    layer_material;
    std::vector<tf_complex> index;
    std::vector<double>     wavelength{ 550.0 };
    std::vector<int32_t>    point_wavelength;
    std::vector<double>     point_angle;
    std::vector<tf_complex> n_in, n_out;
    std::vector<int32_t>    tcode, tpoint;
    int32_t                 delta_convention = TF_DELTA_PRINCIPAL;
    std::vector<double>     tvalue, tweight;
    std::vector<int32_t>    vkind{ TF_VARIABLE_LAYER_THICKNESS }, vlayer{ 0 },
                            vmaterial{ 0 }, vcoef{ 0 };

    tf_problem   problem{};
    tf_operands   operands{};
    tf_variables variables{};
    int32_t      n_materials = 1;

    std::vector<int32_t> path, order, inert, polneed;
    std::vector<int32_t> opres, oporder, oplevel;
    std::vector<int32_t> spanfirst, spancount, spanop, spanslot;
    tf_plan_report report{};
    int32_t plan_status = 0, eval_status = 0;
    tf_plan_buffers pbuf{};
    std::vector<double> scratch;
    std::vector<double> opvalue;
    tf_workspace ws{};
    std::vector<double> residual;
    double merit = 0.0;
    tf_output out{};

    void wire()
    {
        problem.n_layers      = (int32_t)thickness.size();
        problem.n_materials   = n_materials;
        problem.n_wavelengths = (int32_t)wavelength.size();
        problem.n_points      = (int32_t)point_angle.size();
        problem.layer_thickness     = thickness.empty() ? nullptr : thickness.data();
        problem.layer_material      = layer_material.empty() ? nullptr : layer_material.data();
        problem.layer_is_incoherent = nullptr;
        problem.material_index      = index.data();
        problem.wavelength          = wavelength.data();
        problem.point_wavelength    = point_wavelength.data();
        problem.point_angle         = point_angle.data();
        problem.index_incident      = n_in.data();
        problem.index_exit          = n_out.data();

        operands.n_operands = (int32_t)tcode.size();
        operands.delta_convention = delta_convention;
        operands.code = tcode.data(); operands.point = tpoint.data();
        operands.target = tvalue.data(); operands.weight = tweight.data();

        variables.n_variables = (int32_t)vkind.size();
        variables.kind = vkind.data(); variables.layer = vlayer.data();
        variables.material = vmaterial.data(); variables.coefficient = vcoef.data();
    }

    /* An empty stack still needs a valid layer-thickness variable to exist, so a zero-layer
     * problem is given none. */
    tf_status run()
    {
        wire();
        if (problem.n_layers == 0) { variables.n_variables = 0; }

        tf_plan_sizes sizes{};
        tf_query_plan_sizes(problem.n_points, variables.n_variables, operands.n_operands, operands.n_operands, &sizes);
        path.assign((size_t)sizes.point_path_count, -1);
        order.assign((size_t)sizes.point_order_count, -1);
        inert.assign((size_t)sizes.variable_inert_count, 0);
        polneed.assign((size_t)sizes.point_polarisation_count, 0);
        pbuf.point_path = path.data();
        pbuf.point_order = order.data();
        pbuf.variable_inert = inert.empty() ? nullptr : inert.data();
        pbuf.point_polarisation = polneed.data();

        spanfirst.assign((size_t)sizes.point_span_first_count, 0);

        spancount.assign((size_t)sizes.point_span_count_count, 0);

        spanop.assign((size_t)sizes.point_span_operand_count, 0);

        spanslot.assign((size_t)sizes.point_span_slot_count, 0);

        pbuf.point_span_first = spanfirst.data();  pbuf.point_span_count = spancount.data();

        pbuf.point_span_operand = spanop.data();   pbuf.point_span_slot = spanslot.data();


        opres.assign((size_t)sizes.operand_residual_count, -1);


        oporder.assign((size_t)sizes.operand_order_count, 0);


        oplevel.assign((size_t)sizes.operand_level_count, 0);


        pbuf.operand_residual = opres.data(); pbuf.operand_order = oporder.data();


        pbuf.operand_level = oplevel.data();
        pbuf.report = &report;
        pbuf.status = &plan_status;

        const tf_status ps = tf_plan(&problem, &operands, &variables, 1, &pbuf);
        if (ps != TF_OK) return ps;

        scratch.assign((size_t)report.scratch_doubles, 0.0);
        ws.scratch = scratch.empty() ? nullptr : scratch.data();
        ws.scratch_doubles = report.scratch_doubles;

        residual.assign((size_t)report.n_residuals, 0.0);
        opvalue.assign((size_t)operands.n_operands, 0.0);

        out.value = opvalue.data();

        out.residual = residual.data();
        out.jacobian = nullptr;
        out.merit = &merit;
        out.status = &eval_status;

        return tf_evaluate(&problem, &operands, &pbuf, &ws, &out);
    }

    /* The target value, recovered from the residual. weight 1 and target 0 makes residual == value. */
    double value(int i) const
    {
        if ((size_t)i >= residual.size()) return std::nan("");   /* not a crash, and not a number */
        return residual[(size_t)i];
    }
};

static Caller bare_substrate(double n0, double ns, double angle_deg, int32_t code)
{
    Caller c;
    c.index = { tf_complex{ns, 0.0} };
    c.n_in  = { tf_complex{n0, 0.0} };
    c.n_out = { tf_complex{ns, 0.0} };
    c.point_wavelength = { 0 };
    c.point_angle = { angle_deg * PI / 180.0 };
    c.tcode = { code }; c.tpoint = { 0 }; c.tvalue = { 0.0 }; c.tweight = { 1.0 };
    return c;
}

/* ------------------------------------------------------------------------------------------- */

static void ABareSurfaceGivesFresnel()
{
    std::printf("a bare surface gives the Fresnel reflectance, which can be written down\n");

    Caller c = bare_substrate(1.0, 1.52, 0.0, TF_TARGET_RS);
    expect_eq(c.run(), TF_OK, "evaluates");

    const double expected = std::pow((1.0 - 1.52) / (1.0 + 1.52), 2.0);   /* 0.042629... */
    near(c.value(0), expected, 1e-14, "R at normal incidence on n = 1.52");

    /* s and p must agree at normal incidence - there is no plane of incidence to distinguish. */
    Caller p = bare_substrate(1.0, 1.52, 0.0, TF_TARGET_RP);
    p.run();
    near(p.value(0), expected, 1e-14, "Rp equals Rs head-on");
}

static void BrewstersAngleKillsRp()
{
    std::printf("Rp vanishes at Brewster's angle - a zero with a closed-form location\n");

    const double ns = 1.52;
    const double brewster = std::atan(ns) * 180.0 / PI;      /* 56.66 degrees */

    Caller p = bare_substrate(1.0, ns, brewster, TF_TARGET_RP);
    expect_eq(p.run(), TF_OK, "evaluates");
    near(p.value(0), 0.0, 1e-25, "Rp at Brewster");

    /* and Rs most certainly does not vanish there */
    Caller s = bare_substrate(1.0, ns, brewster, TF_TARGET_RS);
    s.run();
    ++g_checks;
    if (!(s.value(0) > 0.1)) { ++g_failures; std::printf("  FAIL  Rs at Brewster should be large\n"); }
}

static void AQuarterWaveLayerGivesTheTextbookResult()
{
    std::printf("a quarter-wave layer: R = ((n0 ns - n1^2) / (n0 ns + n1^2))^2\n");

    const double n0 = 1.0, n1 = 1.38, ns = 1.52, wv = 550.0;

    Caller c;
    c.n_materials = 2;
    c.index = { tf_complex{n1, 0.0}, tf_complex{ns, 0.0} };
    c.thickness = { wv / (4.0 * n1) };            /* quarter wave optical thickness */
    c.layer_material = { 0 };
    c.n_in = { tf_complex{n0, 0.0} };
    c.n_out = { tf_complex{ns, 0.0} };
    c.point_wavelength = { 0 };
    c.point_angle = { 0.0 };
    c.tcode = { TF_TARGET_RS }; c.tpoint = { 0 }; c.tvalue = { 0.0 }; c.tweight = { 1.0 };

    expect_eq(c.run(), TF_OK, "evaluates");
    expect_eq(c.report.n_structured, 1, "and takes the structured path");

    const double y = n1 * n1 / ns;
    const double expected = std::pow((n0 - y) / (n0 + y), 2.0);
    near(c.value(0), expected, 1e-13, "quarter-wave R");
}

static void AHalfWaveLayerIsAbsent()
{
    std::printf("a half-wave layer is an absentee - it must leave the bare substrate unchanged\n");

    const double n0 = 1.0, n1 = 2.35, ns = 1.52, wv = 550.0;

    Caller c;
    c.n_materials = 2;
    c.index = { tf_complex{n1, 0.0}, tf_complex{ns, 0.0} };
    c.thickness = { wv / (2.0 * n1) };
    c.layer_material = { 0 };
    c.n_in = { tf_complex{n0, 0.0} };
    c.n_out = { tf_complex{ns, 0.0} };
    c.point_wavelength = { 0 };
    c.point_angle = { 0.0 };
    c.tcode = { TF_TARGET_RS }; c.tpoint = { 0 }; c.tvalue = { 0.0 }; c.tweight = { 1.0 };
    expect_eq(c.run(), TF_OK, "evaluates");

    const double bare = std::pow((n0 - ns) / (n0 + ns), 2.0);
    near(c.value(0), bare, 1e-12, "half-wave layer leaves R at the bare value");
}

static void ALosslessStackConservesEnergy()
{
    std::printf("R + T = 1 for a lossless stack, at normal incidence and at sixty degrees\n");

    for (double angle : { 0.0, 30.0, 60.0 })
        for (int pol = 0; pol < 2; ++pol)
        {
            Caller c;
            c.n_materials = 3;
            c.index = { tf_complex{2.35, 0.0}, tf_complex{1.46, 0.0}, tf_complex{1.52, 0.0} };
            c.thickness = { 58.5, 94.2, 58.5, 94.2 };
            c.layer_material = { 0, 1, 0, 1 };
            c.n_in = { tf_complex{1.0, 0.0} };
            c.n_out = { tf_complex{1.52, 0.0} };
            c.point_wavelength = { 0 };
            c.point_angle = { angle * PI / 180.0 };
            c.tcode = { pol ? TF_TARGET_RP : TF_TARGET_RS, pol ? TF_TARGET_TP : TF_TARGET_TS };
            c.tpoint = { 0, 0 }; c.tvalue = { 0.0, 0.0 }; c.tweight = { 1.0, 1.0 };

            expect_eq(c.run(), TF_OK, "evaluates");

            char buf[128];
            std::snprintf(buf, sizeof buf, "R + T at %.0f deg, %s", angle, pol ? "p" : "s");
            near(c.value(0) + c.value(1), 1.0, 1e-12, buf);
        }
}

static void AnAbsorbingStackLosesEnergyButNeverGainsIt()
{
    std::printf("R + T < 1 once something absorbs, and never above one\n");

    Caller c;
    c.n_materials = 2;
    c.index = { tf_complex{1.5, 0.05}, tf_complex{1.52, 0.0} };
    c.thickness = { 200.0 };
    c.layer_material = { 0 };
    c.n_in = { tf_complex{1.0, 0.0} };
    c.n_out = { tf_complex{1.52, 0.0} };
    c.point_wavelength = { 0 };
    c.point_angle = { 0.0 };
    c.tcode = { TF_TARGET_RS, TF_TARGET_TS };
    c.tpoint = { 0, 0 }; c.tvalue = { 0.0, 0.0 }; c.tweight = { 1.0, 1.0 };

    expect_eq(c.run(), TF_OK, "evaluates");
    expect_eq(c.report.n_general, 1, "an absorbing layer takes the general path");

    const double sum = c.value(0) + c.value(1);
    ++g_checks;
    if (!(sum < 1.0 && sum > 0.5))
    {
        ++g_failures;
        std::printf("  FAIL  R + T with absorption should be below 1 and sensible, got %.12g\n", sum);
    }
}

static void TheTwoPathsAgreeOnTheSamePhysics()
{
    std::printf("the structured and general paths on the same lossless stack - the cross-check\n"
                "that catches a fast path which has quietly drifted from the slow one\n");

    for (double angle : { 0.0, 25.0, 55.0 })
    {
        Caller s;                                   /* k exactly zero -> structured */
        s.n_materials = 3;
        s.index = { tf_complex{2.35, 0.0}, tf_complex{1.46, 0.0}, tf_complex{1.52, 0.0} };
        s.thickness = { 58.5, 94.2, 58.5, 94.2, 120.0 };
        s.layer_material = { 0, 1, 0, 1, 0 };
        s.n_in = { tf_complex{1.0, 0.0} };
        s.n_out = { tf_complex{1.52, 0.0} };
        s.point_wavelength = { 0 };
        s.point_angle = { angle * PI / 180.0 };
        s.tcode = { TF_TARGET_RS, TF_TARGET_RP, TF_TARGET_TS, TF_TARGET_TP };
        s.tpoint = { 0, 0, 0, 0 }; s.tvalue = { 0, 0, 0, 0 }; s.tweight = { 1, 1, 1, 1 };

        Caller g = s;                               /* same stack, k = 1e-300 -> general */
        g.index[0].im = 1e-300;

        expect_eq(s.run(), TF_OK, "structured evaluates");
        expect_eq(g.run(), TF_OK, "general evaluates");
        expect_eq(s.report.n_structured, 1, "one really is structured");
        expect_eq(g.report.n_general,    1, "and the other really is general");

        for (int i = 0; i < 4; ++i)
        {
            char buf[128];
            static const char* nm[] = { "Rs", "Rp", "Ts", "Tp" };
            std::snprintf(buf, sizeof buf, "%s at %.0f deg, structured vs general", nm[i], angle);
            near(s.value(i), g.value(i), 1e-12, buf);
        }
    }
}

static void EllipsometricTargetsAreConsistentWithEachOther()
{
    std::printf("tan(Psi), Psi and cos(Delta) against each other and against r_p / r_s\n");

    Caller c;
    c.n_materials = 2;
    c.index = { tf_complex{2.0, 0.1}, tf_complex{3.87, 0.02} };
    c.thickness = { 45.0 };
    c.layer_material = { 0 };
    c.n_in = { tf_complex{1.0, 0.0} };
    c.n_out = { tf_complex{3.87, 0.02} };
    c.point_wavelength = { 0 };
    c.point_angle = { 70.0 * PI / 180.0 };
    c.tcode  = { TF_TARGET_R_TANPSI, TF_TARGET_R_PSI, TF_TARGET_R_COSDELTA, TF_TARGET_R_DELTA };
    c.tpoint = { 0, 0, 0, 0 }; c.tvalue = { 0, 0, 0, 0 }; c.tweight = { 1, 1, 1, 1 };

    expect_eq(c.run(), TF_OK, "evaluates");

    near(std::tan(c.value(1)), c.value(0), 1e-12, "tan(Psi) equals the tan-Psi target");
    near(std::cos(c.value(3)), c.value(2), 1e-12, "cos(Delta) equals the cos-Delta target");
    ++g_checks;
    if (!(c.value(0) > 0.0)) { ++g_failures; std::printf("  FAIL  tan(Psi) should be positive\n"); }
}

/* ------------------------------------------------------------------------------------------- */

/* ---------------------------------------------------------------------------------------------
 * The merit and weight convention, which is LensHH-LT's and was established by MEASURING it.
 *
 * LensHH-LT's own display code reads (Value - Target) * Weight, which would mean the weight enters
 * linearly. Running it says otherwise: entering weight 2 in place of 1 multiplies the reported
 * residual by sqrt(2), and the merit of a single operand does not change with its weight at all.
 * The numbers below are the same experiment repeated here, so that the two programs cannot drift
 * apart without this failing.
 * ------------------------------------------------------------------------------------------- */

static void TheWeightEntersAsItsSquareRoot()
{
    std::printf("a weight enters the residual as its square root, as LensHH-LT's does\n");

    Caller a = bare_substrate(1.0, 1.52, 0.0, TF_TARGET_RS);
    a.tweight = { 1.0 };
    a.run();

    Caller b = bare_substrate(1.0, 1.52, 0.0, TF_TARGET_RS);
    b.tweight = { 2.0 };
    b.run();

    near(b.residual[0] / a.residual[0], std::sqrt(2.0), 1e-14,
         "twice the weight is sqrt(2) times the residual");
}

static void TheMeritIsAWeightedRms()
{
    std::printf("the merit is sqrt(sum r^2 / sum w) - so scaling every weight changes nothing\n");

    const double R = std::pow((1.0 - 1.52) / (1.0 + 1.52), 2.0);

    /* One residual, target zero: the merit is the error itself whatever the weight. That is the
     * first thing LensHH-LT showed - 43.9047 at weight 1 and again at weight 2. */
    Caller a = bare_substrate(1.0, 1.52, 0.0, TF_TARGET_RS);
    a.tweight = { 1.0 };
    a.run();
    near(a.merit, R, 1e-14, "merit of one operand is its error");

    Caller b = bare_substrate(1.0, 1.52, 0.0, TF_TARGET_RS);
    b.tweight = { 7.0 };
    b.run();
    near(b.merit, R, 1e-14, "and is unchanged by its weight");

    /* Two operands of equal weight, the second already met: the merit falls by sqrt(2), which is
     * what LensHH-LT did when a satisfied operand was added beside an unsatisfied one. */
    Caller c = bare_substrate(1.0, 1.52, 0.0, TF_TARGET_RS);
    c.tcode  = { TF_TARGET_RS, TF_TARGET_RS };
    c.tpoint = { 0, 0 };
    c.tvalue = { 0.0, R };
    c.tweight = { 2.0, 2.0 };
    c.run();
    near(c.merit, R / std::sqrt(2.0), 1e-14, "a satisfied operand of equal weight halves the mean");

    /* And the sum is over weights, not squared weights. Two operands at weights 1 and 3, the
     * second met: merit = sqrt(1 * R^2 / 4) = R/2. If the denominator were sum w^2 = 10 it would
     * be R/sqrt(10) instead, so this distinguishes them. */
    Caller d = bare_substrate(1.0, 1.52, 0.0, TF_TARGET_RS);
    d.tcode  = { TF_TARGET_RS, TF_TARGET_RS };
    d.tpoint = { 0, 0 };
    d.tvalue = { 0.0, R };
    d.tweight = { 1.0, 3.0 };
    d.run();
    near(d.merit, R / 2.0, 1e-14, "the denominator is sum of weights, not of their squares");
}

/* ---------------------------------------------------------------------------------------------
 * The Delta branch cut
 * ------------------------------------------------------------------------------------------- */

/* A fixture whose Delta sits near the cut, so the three conventions actually differ. */
static Caller delta_fixture(int32_t convention, double target)
{
    Caller c;
    c.index = { tf_complex{2.0, 0.1} };
    c.thickness = { 45.0 };
    c.layer_material = { 0 };
    c.n_in  = { tf_complex{1.0, 0.0} };
    c.n_out = { tf_complex{3.87, 0.02} };
    c.point_wavelength = { 0 };
    c.point_angle = { 70.0 * PI / 180.0 };
    c.tcode  = { TF_TARGET_R_DELTA };
    c.tpoint = { 0 };
    c.tvalue = { target };
    c.tweight = { 1.0 };
    c.delta_convention = convention;
    return c;
}

static void TheThreeDeltaConventionsAgreeAboutTheAngle()
{
    std::printf("the three Delta conventions name the same angle in different intervals\n");

    Caller p = delta_fixture(TF_DELTA_PRINCIPAL, 0.0);    p.run();
    Caller q = delta_fixture(TF_DELTA_POSITIVE,  0.0);    q.run();
    Caller s = delta_fixture(TF_DELTA_SHORTEST_ARC, 0.0); s.run();

    const double principal = p.residual[0];
    const double positive  = q.residual[0];

    ++g_checks;
    if (!(principal > -PI && principal <= PI))
    {
        ++g_failures;
        std::printf("  FAIL  principal value %.12g outside (-pi, pi]\n", principal);
    }
    ++g_checks;
    if (!(positive >= 0.0 && positive < 2.0 * PI))
    {
        ++g_failures;
        std::printf("  FAIL  positive value %.12g outside [0, 2pi)\n", positive);
    }

    near(std::cos(positive), std::cos(principal), 1e-14, "they are the same angle");
    near(std::sin(positive), std::sin(principal), 1e-14, "in sine as well as cosine");

    /* With a target of zero, the shortest arc is the principal value. */
    near(s.residual[0], principal, 1e-14, "shortest arc from zero is the principal value");
}

static void TheShortestArcIsWhatStopsTheResidualJumping()
{
    std::printf("across the cut, subtraction reports 2 pi of error where there is almost none\n");

    /* Find the model's Delta, then put operands just on either side of the cut relative to it. */
    Caller probe = delta_fixture(TF_DELTA_PRINCIPAL, 0.0);
    probe.run();
    const double d = probe.residual[0];

    /* A target a thousandth of a radian away, expressed on the far side of the cut: d - 0.001
     * wrapped by a full turn. Subtraction sees nearly 2 pi; the shortest arc sees 0.001. */
    const double far_side = d - 0.001 + 2.0 * PI;

    Caller plain = delta_fixture(TF_DELTA_PRINCIPAL, far_side);
    plain.run();
    near(std::fabs(plain.residual[0]), 2.0 * PI - 0.001, 1e-12,
         "plain subtraction reports almost a full turn");

    Caller arc = delta_fixture(TF_DELTA_SHORTEST_ARC, far_side);
    arc.run();
    near(arc.residual[0], 0.001, 1e-12, "the shortest arc reports the thousandth it is");

    /* And it is signed, not a magnitude - an optimiser needs a direction. */
    Caller other = delta_fixture(TF_DELTA_SHORTEST_ARC, d + 0.001 - 2.0 * PI);
    other.run();
    near(other.residual[0], -0.001, 1e-12, "and carries the sign of the direction to move");
}

static void AnUnknownDeltaConventionIsRefused()
{
    std::printf("an out-of-range convention is refused by the plan, not quietly treated as zero\n");

    Caller c = delta_fixture(99, 0.0);
    expect_eq(c.run(), TF_UNKNOWN_DELTA_CONVENTION, "refused with its own code");
}

static void CosDeltaIgnoresTheConventionEntirely()
{
    std::printf("cos(Delta) has no cut, so no setting can move it\n");

    Caller a = delta_fixture(TF_DELTA_PRINCIPAL, 0.0);
    a.tcode = { TF_TARGET_R_COSDELTA };
    a.run();

    Caller b = delta_fixture(TF_DELTA_POSITIVE, 0.0);
    b.tcode = { TF_TARGET_R_COSDELTA };
    b.run();

    Caller c = delta_fixture(TF_DELTA_SHORTEST_ARC, 0.0);
    c.tcode = { TF_TARGET_R_COSDELTA };
    c.run();

    ++g_checks;
    if (a.residual[0] != b.residual[0] || a.residual[0] != c.residual[0])
    {
        ++g_failures;
        std::printf("  FAIL  cos(Delta) moved with the convention: %.17g %.17g %.17g\n",
                    a.residual[0], b.residual[0], c.residual[0]);
    }
}

/* ---------------------------------------------------------------------------------------------
 * The polarisation mask, from the evaluator's side
 * ------------------------------------------------------------------------------------------- */

/* Skipping a polarisation must not change any number. The saving is only worth having if the
 * answer is identical, so it is compared bit for bit rather than within a tolerance. */
static void SkippingAPolarisationChangesNoAnswer()
{
    std::printf("solving one polarisation gives the same Rs as solving both did\n");

    Caller one = bare_substrate(1.0, 1.52, 40.0, TF_TARGET_RS);
    one.run();

    /* The same point, but with an Rp target beside it so the plan must solve both. */
    Caller both = bare_substrate(1.0, 1.52, 40.0, TF_TARGET_RS);
    both.tcode  = { TF_TARGET_RS, TF_TARGET_RP };
    both.tpoint = { 0, 0 };
    both.tvalue = { 0.0, 0.0 };
    both.tweight = { 1.0, 1.0 };
    both.run();

    expect_eq(one.polneed[0], TF_NEED_S, "the first plan asked for s alone");
    expect_eq(both.polneed[0], TF_NEED_S | TF_NEED_P, "the second for both");

    ++g_checks;
    if (one.residual[0] != both.residual[0])
    {
        ++g_failures;
        std::printf("  FAIL  Rs moved when p was skipped: %.17g vs %.17g\n",
                    one.residual[0], both.residual[0]);
    }
}

/* THE GUARD THAT MAKES THE MASK SAFE.
 *
 * The whole optimisation rests on the mask agreeing with the operands. It is derived from them, so
 * it cannot normally disagree - but if that derivation is ever broken, an unsolved amplitude of
 * ZERO would be a perfectly plausible reflectance and nothing would say so. It is left as a NaN
 * instead. This test breaks the mask on purpose, which the caller can do because the caller owns
 * the buffer, and checks that the answer is unmistakable rather than merely wrong. */
static void AWrongMaskGivesNaNRatherThanAPlausibleNumber()
{
    std::printf("a mask that disagrees with the operands yields NaN, not a believable reflectance\n");

    Caller c = bare_substrate(1.0, 1.52, 40.0, TF_TARGET_RP);
    c.run();
    ++g_checks;
    if (!(c.residual[0] > 0.0))
    {
        ++g_failures;
        std::printf("  FAIL  the fixture should give a positive Rp\n");
    }

    /* Now tell the evaluator that only s is needed, while the target still asks for Rp. */
    c.polneed[0] = TF_NEED_S;
    tf_evaluate(&c.problem, &c.operands, &c.pbuf, &c.ws, &c.out);

    ++g_checks;
    if (!std::isnan(c.residual[0]))
    {
        ++g_failures;
        std::printf("  FAIL  a broken mask produced %.17g, which looks like an answer\n",
                    c.residual[0]);
    }
}

int main()
{
    std::printf("\nthinfilm native: tf_evaluate\n");
    std::printf("-------------------------------------------------\n");

    ABareSurfaceGivesFresnel();
    BrewstersAngleKillsRp();
    AQuarterWaveLayerGivesTheTextbookResult();
    AHalfWaveLayerIsAbsent();
    ALosslessStackConservesEnergy();
    AnAbsorbingStackLosesEnergyButNeverGainsIt();
    TheTwoPathsAgreeOnTheSamePhysics();
    EllipsometricTargetsAreConsistentWithEachOther();

    SkippingAPolarisationChangesNoAnswer();
    AWrongMaskGivesNaNRatherThanAPlausibleNumber();

    TheWeightEntersAsItsSquareRoot();
    TheMeritIsAWeightedRms();

    TheThreeDeltaConventionsAgreeAboutTheAngle();
    TheShortestArcIsWhatStopsTheResidualJumping();
    CosDeltaIgnoresTheConventionEntirely();
    AnUnknownDeltaConventionIsRefused();

    std::printf("-------------------------------------------------\n");
    std::printf("%d checks, %d failures\n\n", g_checks, g_failures);
    return g_failures == 0 ? 0 : 1;
}
