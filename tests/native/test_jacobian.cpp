/* test_jacobian.cpp — the analytic Jacobian against central differences of the evaluator.
 *
 * WHY FINITE DIFFERENCES ARE THE RIGHT ORACLE HERE, when they are the wrong thing to ship. The
 * derivative has no closed form worth writing down for a six-layer stack, so there is no
 * independent formula to check against. But tf_evaluate is already checked against closed-form
 * optics - Fresnel, Brewster, quarter-wave, energy conservation - so it is trusted, and a central
 * difference of a trusted function is an independent computation of its slope. Independent in the
 * way that matters: it exercises none of the prefix chain, none of the suffix recursion and none
 * of the chain rule. If the sweep has the factors in the wrong order, or the chain rule is missing
 * a term, the difference shows it.
 *
 * AND IT CROSSES THE TWO PATHS. The derivative sweep is general complex arithmetic always; the
 * finite difference of a lossless fixture runs through the STRUCTURED evaluator. So agreement on a
 * lossless stack is simultaneously a check that the two paths still describe the same physics.
 *
 * THE STEP SIZE IS DERIVED, not guessed. Central difference error is
 *
 *     h^2 |f'''| / 6   +   eps |f| / h
 *
 * minimised near h = (3 eps |f| / |f'''|)^(1/3), which for quantities of order one and third
 * derivatives of order one is about 6e-6. The thicknesses are in nanometres and of order 100, and
 * the phase varies on that scale, so h = 1e-3 nm sits close to the optimum and leaves the expected
 * agreement near 1e-9 absolute. The tolerances below are set from that, not tuned until green.
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
        std::printf("  FAIL  %-54s got %.12g  expected %.12g  delta %.3g\n",
                    what, got, want, std::fabs(got - want));
    }
}

static void expect_eq(long long got, long long want, const char* what)
{
    ++g_checks;
    if (got != want)
    {
        ++g_failures;
        std::printf("  FAIL  %-54s got %lld  expected %lld\n", what, got, want);
    }
}

static const double PI = 3.14159265358979323846;
static const double FD_STEP = 1e-3;     /* nanometres; see the header comment */

/* ---------------------------------------------------------------------------------------------
 * A caller. Owns every buffer, as the contract requires.
 * ------------------------------------------------------------------------------------------- */

struct Caller
{
    std::vector<double>     thickness;
    std::vector<int32_t>    layer_material;
    std::vector<tf_complex> index;            /* [n_materials * n_wavelengths] */
    std::vector<double>     wavelength;
    std::vector<int32_t>    point_wavelength;
    std::vector<double>     point_angle;
    std::vector<tf_complex> n_in, n_out;
    std::vector<int32_t>    tcode, tpoint;
    int32_t                 delta_convention = TF_DELTA_PRINCIPAL;
    std::vector<double>     tvalue, tweight;
    std::vector<int32_t>    vkind, vlayer, vmaterial, vcoef;
    int32_t                 n_materials = 1;

    tf_problem   problem{};
    tf_operands   operands{};
    tf_variables variables{};

    std::vector<int32_t> path, order, inert, polneed;
    std::vector<int32_t> opres, oporder, oplevel;
    std::vector<int32_t> spanfirst, spancount, spanop, spanslot;
    tf_plan_report  report{};
    int32_t         plan_status = 0, eval_status = 0;
    tf_plan_buffers pbuf{};
    std::vector<double> scratch;
    std::vector<double> opvalue;
    tf_workspace    ws{};
    std::vector<double> residual, jacobian;
    double          merit = 0.0;
    tf_output       out{};

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
        variables.kind     = vkind.empty() ? nullptr : vkind.data();
        variables.layer    = vlayer.empty() ? nullptr : vlayer.data();
        variables.material = vmaterial.empty() ? nullptr : vmaterial.data();
        variables.coefficient = vcoef.empty() ? nullptr : vcoef.data();
    }

    tf_status plan()
    {
        wire();
        tf_plan_sizes sizes{};
        tf_query_plan_sizes(problem.n_points, variables.n_variables, operands.n_operands, operands.n_operands, &sizes);
        path.assign((size_t)sizes.point_path_count, -1);
        order.assign((size_t)sizes.point_order_count, -1);
        inert.assign((size_t)sizes.variable_inert_count, 0);
        polneed.assign((size_t)sizes.point_polarisation_count, 0);
        pbuf.point_path     = path.data();
        pbuf.point_order    = order.data();
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
        jacobian.assign((size_t)report.n_residuals * (size_t)variables.n_variables, -12345.0);
        opvalue.assign((size_t)operands.n_operands, 0.0);

        out.value = opvalue.data();

        out.residual = residual.data();
        out.jacobian = jacobian.empty() ? nullptr : jacobian.data();
        out.merit    = &merit;
        out.status   = &eval_status;
        return TF_OK;
    }

    tf_status evaluate()        { wire(); return tf_evaluate(&problem, &operands, &pbuf, &ws, &out); }
    tf_status evaluate_jacobian(){ wire(); return tf_evaluate_jacobian(&problem, &operands, &variables,
                                                                      &pbuf, &ws, &out); }

    double J(int r, int v) const
    {
        return jacobian[(size_t)v * (size_t)operands.n_operands + (size_t)r];
    }

    /* Central difference of residual r with respect to variable v, through tf_evaluate. */
    double fd(int r, int v, double h)
    {
        const int32_t layer = vlayer[(size_t)v];
        const double  keep  = thickness[(size_t)layer];

        thickness[(size_t)layer] = keep + h;  evaluate();  const double plus  = residual[(size_t)r];
        thickness[(size_t)layer] = keep - h;  evaluate();  const double minus = residual[(size_t)r];
        thickness[(size_t)layer] = keep;      evaluate();

        return (plus - minus) / (2.0 * h);
    }
};

/* ---------------------------------------------------------------------------------------------
 * Fixtures
 * ------------------------------------------------------------------------------------------- */

/* Every target code, so the chain rule is exercised end to end rather than on R alone. */
static const int32_t ALL_TARGETS[] = {
    TF_TARGET_RS, TF_TARGET_RP, TF_TARGET_RA,
    TF_TARGET_TS, TF_TARGET_TP, TF_TARGET_TA,
    TF_TARGET_R_TANPSI, TF_TARGET_R_COSDELTA, TF_TARGET_R_PSI, TF_TARGET_R_DELTA,
    TF_TARGET_T_TANPSI, TF_TARGET_T_COSDELTA, TF_TARGET_T_PSI, TF_TARGET_T_DELTA
};
static const int N_ALL_TARGETS = (int)(sizeof(ALL_TARGETS) / sizeof(ALL_TARGETS[0]));

static const char* target_name(int32_t code)
{
    switch (code)
    {
        case TF_TARGET_RS: return "Rs";  case TF_TARGET_RP: return "Rp";
        case TF_TARGET_RA: return "Ra";  case TF_TARGET_TS: return "Ts";
        case TF_TARGET_TP: return "Tp";  case TF_TARGET_TA: return "Ta";
        case TF_TARGET_R_TANPSI:   return "R.tanPsi";
        case TF_TARGET_R_COSDELTA: return "R.cosDelta";
        case TF_TARGET_R_PSI:      return "R.Psi";
        case TF_TARGET_R_DELTA:    return "R.Delta";
        case TF_TARGET_T_TANPSI:   return "T.tanPsi";
        case TF_TARGET_T_COSDELTA: return "T.cosDelta";
        case TF_TARGET_T_PSI:      return "T.Psi";
        case TF_TARGET_T_DELTA:    return "T.Delta";
        default: return "?";
    }
}

/* A six-layer HLHLHL quarter-wave stack, deliberately DETUNED so that no derivative sits at a
 * stationary point. A stack exactly at quarter wave has dR/dt = 0 for every layer at normal
 * incidence, and a Jacobian routine that returned zeros would pass. */
static Caller six_layer(bool absorbing, double angle_deg)
{
    Caller c;
    c.wavelength = { 510.0, 550.0, 610.0 };
    c.n_materials = 2;

    /* material 0 = high (2.35), material 1 = low (1.46); absorbing variant gives the high index a
     * real extinction coefficient so the general path is taken. */
    const double k = absorbing ? 0.02 : 0.0;
    c.index = {
        tf_complex{2.37, k}, tf_complex{2.35, k}, tf_complex{2.33, k},
        tf_complex{1.47, 0.0}, tf_complex{1.46, 0.0}, tf_complex{1.45, 0.0}
    };
    c.n_in  = { tf_complex{1.0, 0.0},  tf_complex{1.0, 0.0},  tf_complex{1.0, 0.0} };
    c.n_out = { tf_complex{1.53, 0.0}, tf_complex{1.52, 0.0}, tf_complex{1.51, 0.0} };

    /* detuned quarter waves: 550/(4n) scaled by an irrational-looking factor per layer */
    const double qh = 550.0 / (4.0 * 2.35), ql = 550.0 / (4.0 * 1.46);
    c.thickness      = { qh * 1.13, ql * 0.87, qh * 0.94, ql * 1.21, qh * 1.07, ql * 0.79 };
    c.layer_material = { 0, 1, 0, 1, 0, 1 };

    /* three points: three wavelengths at the same angle */
    c.point_wavelength = { 0, 1, 2 };
    c.point_angle      = { angle_deg * PI / 180.0, angle_deg * PI / 180.0,
                           angle_deg * PI / 180.0 };

    /* every target at every point */
    for (int32_t p = 0; p < 3; ++p)
        for (int i = 0; i < N_ALL_TARGETS; ++i)
        {
            c.tcode.push_back(ALL_TARGETS[i]);
            c.tpoint.push_back(p);
            c.tvalue.push_back(0.1);          /* a non-zero target, so residual != value */
            c.tweight.push_back(0.25 + 0.5 * (double)i);   /* varied, so sqrt(w) must be carried */
        }

    /* one variable per layer */
    for (int32_t j = 0; j < 6; ++j)
    {
        c.vkind.push_back(TF_VARIABLE_LAYER_THICKNESS);
        c.vlayer.push_back(j);
        c.vmaterial.push_back(0);
        c.vcoef.push_back(0);
    }
    return c;
}

/* ---------------------------------------------------------------------------------------------
 * The checks
 * ------------------------------------------------------------------------------------------- */

static void compare_against_fd(Caller& c, const char* label, double tol)
{
    std::printf("%s\n", label);

    expect_eq(c.plan(), TF_OK, "plans");
    expect_eq(c.evaluate_jacobian(), TF_OK, "jacobian evaluates");

    const int nr = c.operands.n_operands;
    const int nv = c.variables.n_variables;

    double worst = 0.0;
    int worst_r = -1, worst_v = -1;

    for (int v = 0; v < nv; ++v)
        for (int r = 0; r < nr; ++r)
        {
            const double analytic = c.J(r, v);
            const double numeric  = c.fd(r, v, FD_STEP);
            const double diff     = std::fabs(analytic - numeric);
            if (diff > worst) { worst = diff; worst_r = r; worst_v = v; }
        }

    ++g_checks;
    if (!(worst <= tol))
    {
        ++g_failures;
        std::printf("  FAIL  worst analytic-vs-difference disagreement %.3g at residual %d (%s, "
                    "point %d) variable %d; tolerance %.3g\n",
                    worst, worst_r, worst_r >= 0 ? target_name(c.tcode[(size_t)worst_r]) : "?",
                    worst_r >= 0 ? c.tpoint[(size_t)worst_r] : -1, worst_v, tol);
    }
    std::printf("    %d residuals x %d variables, worst disagreement %.3g (tolerance %.3g)\n",
                nr, nv, worst, tol);
}

static void LosslessStack()
{
    Caller c = six_layer(false, 0.0);
    compare_against_fd(c, "a lossless stack head-on: every target, every layer, against central "
                          "differences", 1e-8);
    expect_eq(c.report.n_structured, 3, "all three points took the structured path");
    expect_eq(c.report.n_general, 0, "and none the general one");
}

static void LosslessAtAngle()
{
    Caller c = six_layer(false, 45.0);
    compare_against_fd(c, "the same stack at 45 degrees, where s and p genuinely differ", 1e-8);
}

static void AbsorbingStack()
{
    Caller c = six_layer(true, 30.0);
    compare_against_fd(c, "an absorbing stack at 30 degrees - the general path, and the one where "
                          "a sign convention error hides", 1e-8);
    expect_eq(c.report.n_general, 3, "all three points took the general path");
}

/* The residuals tf_evaluate_jacobian reports must be the residuals tf_evaluate reports - bit for
 * bit, since both go through tf_amplitudes and tf_target_value. An optimiser whose merit and
 * gradient come from different arithmetic will chase a minimum that is not there. */
static void ResidualsAgreeExactly()
{
    std::printf("the two entry points report identical residuals, not merely close ones\n");

    Caller c = six_layer(true, 30.0);
    expect_eq(c.plan(), TF_OK, "plans");

    expect_eq(c.evaluate(), TF_OK, "evaluates");
    const std::vector<double> from_evaluate = c.residual;
    const double merit_evaluate = c.merit;

    expect_eq(c.evaluate_jacobian(), TF_OK, "jacobian evaluates");

    int mismatches = 0;
    for (size_t i = 0; i < from_evaluate.size(); ++i)
        if (from_evaluate[i] != c.residual[i]) ++mismatches;

    expect_eq(mismatches, 0, "every residual identical");
    ++g_checks;
    if (merit_evaluate != c.merit)
    {
        ++g_failures;
        std::printf("  FAIL  merit differs: %.17g vs %.17g\n", merit_evaluate, c.merit);
    }
}

/* A layer thick enough to clamp no longer depends on its thickness. The column must be EXACTLY
 * zero - not 1e-18 - because that is the physical statement, and tf_plan must have said so in
 * advance rather than leaving an optimiser to discover a rank-deficient normal matrix. */
static void ClampedLayerGivesAnExactlyZeroColumn()
{
    std::printf("an opaque layer's column is exactly zero, and the plan said so first\n");

    Caller c;
    c.wavelength = { 550.0 };
    c.n_materials = 2;
    c.index = { tf_complex{0.15, 3.5},      /* silver-like: opaque in a few hundred nm */
                tf_complex{1.46, 0.0} };
    c.n_in  = { tf_complex{1.0, 0.0} };
    c.n_out = { tf_complex{1.52, 0.0} };
    c.thickness      = { 137.0, 900.0 };    /* the metal is BEHIND the dielectric, or the test
                                             * would only be showing that light cannot reach a
                                             * buried layer - which is true but is not the clamp */
    c.layer_material = { 1, 0 };
    c.point_wavelength = { 0 };
    c.point_angle      = { 0.0 };
    c.tcode  = { TF_TARGET_RS, TF_TARGET_TS };
    c.tpoint = { 0, 0 };
    c.tvalue = { 0.0, 0.0 };
    c.tweight = { 1.0, 1.0 };
    c.vkind  = { TF_VARIABLE_LAYER_THICKNESS, TF_VARIABLE_LAYER_THICKNESS };
    c.vlayer = { 0, 1 };
    c.vmaterial = { 0, 0 };
    c.vcoef  = { 0, 0 };

    expect_eq(c.plan(), TF_OK, "plans");
    expect_eq(c.inert[1], 1, "the plan marked the opaque layer's variable inert");
    expect_eq(c.inert[0], 0, "and left the live one alone");

    expect_eq(c.evaluate_jacobian(), TF_OK, "jacobian evaluates");

    ++g_checks;
    if (c.J(0, 1) != 0.0 || c.J(1, 1) != 0.0)
    {
        ++g_failures;
        std::printf("  FAIL  clamped column not exactly zero: %.17g, %.17g\n",
                    c.J(0, 1), c.J(1, 1));
    }

    /* The live layer must still move, or the fixture proves nothing. */
    ++g_checks;
    if (c.J(0, 0) == 0.0) { ++g_failures; std::printf("  FAIL  the live layer has no effect\n"); }
    near_(c.J(0, 0), c.fd(0, 0, FD_STEP), 1e-8, "the live column matches finite differences");
}

/* A material-coefficient variable has to refuse, and the refusal has to be LOUD. There is no
 * dispersion model to differentiate yet, and returning a zero column would look like a material
 * that does not matter. */
static void MaterialCoefficientVariablesRefuse()
{
    std::printf("a material-coefficient variable is refused, not silently zeroed\n");

    Caller c = six_layer(false, 0.0);
    c.vkind[2] = TF_VARIABLE_MATERIAL_COEFFICIENT;

    expect_eq(c.plan(), TF_OK, "plans - the plan does not have to refuse it, the Jacobian does");
    std::printf("    the diagnostic below is expected:\n");
    expect_eq(c.evaluate_jacobian(), TF_NOT_IMPLEMENTED_VARIABLE, "refuses");
    expect_eq(c.eval_status, TF_NOT_IMPLEMENTED_VARIABLE, "and says so in status");
}

/* A bare substrate has nothing to vary. The answer is a Jacobian with no columns and TF_OK, not
 * an error and not a crash - the same edge that bit the evaluator. */
static void BareSubstrateHasAnEmptyJacobian()
{
    std::printf("a bare substrate differentiates to nothing at all, without complaint\n");

    Caller c;
    c.wavelength = { 550.0 };
    c.index = { tf_complex{1.52, 0.0} };
    c.n_in  = { tf_complex{1.0, 0.0} };
    c.n_out = { tf_complex{1.52, 0.0} };
    c.point_wavelength = { 0 };
    c.point_angle = { 0.0 };
    c.tcode = { TF_TARGET_RS }; c.tpoint = { 0 };
    c.tvalue = { 0.0 }; c.tweight = { 1.0 };
    /* no layers, so no variables */

    expect_eq(c.plan(), TF_OK, "plans");
    expect_eq(c.evaluate_jacobian(), TF_OK, "evaluates");
    near_(c.residual[0], std::pow(0.52 / 2.52, 2.0), 1e-14, "and still gets the residual right");
}

/* Two variables on the SAME layer must each get the full derivative, not half of it. A sweep that
 * broke out of the variable loop on the first match, or accumulated into the wrong column, passes
 * every other test here. */
static void TwoVariablesOnOneLayerEachGetTheWholeDerivative()
{
    std::printf("two variables pointing at one layer each carry the whole derivative\n");

    Caller c = six_layer(false, 20.0);
    c.vlayer[5] = c.vlayer[0];       /* variable 5 now also drives layer 0 */

    expect_eq(c.plan(), TF_OK, "plans");
    expect_eq(c.evaluate_jacobian(), TF_OK, "evaluates");

    int mismatches = 0;
    for (int r = 0; r < c.operands.n_operands; ++r)
        if (c.J(r, 0) != c.J(r, 5)) ++mismatches;
    expect_eq(mismatches, 0, "the duplicated columns are identical");

    near_(c.J(0, 0), c.fd(0, 0, FD_STEP), 1e-8, "and both equal the finite difference");
}

/* The weight enters as sqrt(w) on the residual, so it must enter the Jacobian the same way. A
 * routine that forgot it, or applied w instead, gives a gradient that points in the right
 * direction with the wrong length - and a line search that never converges. */
static void WeightsScaleTheJacobianAsSqrt()
{
    std::printf("the Jacobian carries sqrt(weight), the same factor the residual carries\n");

    Caller a = six_layer(false, 0.0);
    Caller b = six_layer(false, 0.0);
    for (size_t i = 0; i < b.tweight.size(); ++i) b.tweight[i] *= 9.0;

    expect_eq(a.plan(), TF_OK, "plans a");
    expect_eq(b.plan(), TF_OK, "plans b");
    a.evaluate_jacobian();
    b.evaluate_jacobian();

    double worst = 0.0;
    for (int v = 0; v < a.variables.n_variables; ++v)
        for (int r = 0; r < a.operands.n_operands; ++r)
            worst = std::fmax(worst, std::fabs(b.J(r, v) - 3.0 * a.J(r, v)));

    near_(worst, 0.0, 1e-12, "nine times the weight is three times the Jacobian");
}

/* The plan is the only thing entitled to say how much scratch is needed, and the Jacobian is the
 * only thing that knows what it will actually use. This test stands between them: it recomputes
 * the requirement from the documented layout and checks that the plan covers it AND that a buffer
 * below it is refused. If either side's formula is edited alone, this fails - which is the point.
 * The plan deliberately reserves a little headroom beyond the requirement, so "one double short of
 * what the plan reported" is NOT short at all, and a test written that way would prove nothing. */
static void ScratchShortfallIsCaught()
{
    std::printf("the plan's size and the sweep's requirement are checked against each other\n");

    Caller c = six_layer(false, 0.0);
    expect_eq(c.plan(), TF_OK, "plans");

    const int64_t n    = c.problem.n_layers;
    const int64_t ns   = c.operands.n_operands;    /* pointwise: one slot per operand */
    const int64_t ms   = c.report.max_operand_span;
    const int64_t need = (n + 1) * 8      /* the prefix chain, 8 doubles a matrix */
                       + n * 6            /* per layer: dr and dt complex, dR and dT real */
                       + ns               /* per slot: d(residual)/d(that point's value) */
                       + ms * 5           /* the span scratch and its slopes */
                       + ns * (int64_t)c.variables.n_variables;  /* d(value)/dx per operand */

    ++g_checks;
    if (c.report.scratch_doubles < need)
    {
        ++g_failures;
        std::printf("  FAIL  the plan reported %lld doubles, below the %lld the sweep uses\n",
                    (long long)c.report.scratch_doubles, (long long)need);
    }

    const int64_t full = c.ws.scratch_doubles;

    c.ws.scratch_doubles = need;
    expect_eq(c.evaluate_jacobian(), TF_OK, "exactly the requirement is enough");

    c.ws.scratch_doubles = need - 1;
    std::fflush(stdout);
    std::printf("    the diagnostic below is expected:\n");
    std::fflush(stdout);
    expect_eq(c.evaluate_jacobian(), TF_SCRATCH_TOO_SMALL, "one below it is refused");

    c.ws.scratch_doubles = full;
}

/* The Delta convention must not reach the Jacobian at all. All three maps differ from arg(rho) by
 * a locally constant multiple of 2 pi, and a constant has no slope - so one expression serves them
 * all and cannot fall out of step with the value when the setting changes. */
static void TheDeltaConventionDoesNotChangeTheDerivative()
{
    std::printf("the Delta convention moves the value and leaves the slope alone\n");

    const int32_t conventions[3] = { TF_DELTA_PRINCIPAL, TF_DELTA_POSITIVE, TF_DELTA_SHORTEST_ARC };
    double column[3][6];

    for (int k = 0; k < 3; ++k)
    {
        Caller c = six_layer(true, 55.0);
        c.delta_convention = conventions[k];
        c.tcode.assign(c.tcode.size(), TF_TARGET_R_DELTA);
        expect_eq(c.plan(), TF_OK, "plans");
        expect_eq(c.evaluate_jacobian(), TF_OK, "evaluates");
        for (int v = 0; v < 6; ++v) column[k][v] = c.J(0, v);
    }

    int mismatches = 0;
    for (int v = 0; v < 6; ++v)
        if (column[1][v] != column[0][v] || column[2][v] != column[0][v]) ++mismatches;
    expect_eq(mismatches, 0, "all three give bit-identical derivatives");

    /* And the derivative is right, not merely consistent. Checked under SHORTEST_ARC, the one
     * convention whose residual is continuous, so the finite difference is meaningful wherever
     * the design happens to sit. */
    Caller c = six_layer(true, 55.0);
    c.delta_convention = TF_DELTA_SHORTEST_ARC;
    c.tcode.assign(c.tcode.size(), TF_TARGET_R_DELTA);
    c.plan();
    c.evaluate_jacobian();
    double worst = 0.0;
    for (int v = 0; v < 6; ++v)
        worst = std::fmax(worst, std::fabs(c.J(0, v) - c.fd(0, v, FD_STEP)));
    near_(worst, 0.0, 1e-8, "and matches central differences");
}

int main()
{
    std::printf("\n=== tf_evaluate_jacobian ===\n\n");

    LosslessStack();                                  std::printf("\n");
    LosslessAtAngle();                                std::printf("\n");
    AbsorbingStack();                                 std::printf("\n");
    ResidualsAgreeExactly();                          std::printf("\n");
    ClampedLayerGivesAnExactlyZeroColumn();           std::printf("\n");
    TwoVariablesOnOneLayerEachGetTheWholeDerivative(); std::printf("\n");
    WeightsScaleTheJacobianAsSqrt();                  std::printf("\n");
    BareSubstrateHasAnEmptyJacobian();                std::printf("\n");
    MaterialCoefficientVariablesRefuse();             std::printf("\n");
    TheDeltaConventionDoesNotChangeTheDerivative();   std::printf("\n");
    ScratchShortfallIsCaught();                       std::printf("\n");

    std::printf("=== %d checks, %d failures ===\n\n", g_checks, g_failures);
    return g_failures == 0 ? 0 : 1;
}
