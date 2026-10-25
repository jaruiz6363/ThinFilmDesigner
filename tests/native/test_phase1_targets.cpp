/* test_phase1_targets.cpp — the Phase 1 spectral operands: A, OD, absolute phase, NPOL,
 * extinction in decibels, and diattenuation.
 *
 * WHERE AN IDENTITY EXISTS IT IS USED INSTEAD OF A TOLERANCE, and most of these have one, because
 * each is an arithmetic function of quantities the engine already computes correctly:
 *
 *     A  + R + T == 1        exactly, for a non-scattering stack
 *     OD         == -log10(T)
 *     NPOL       == |R_s - R_p|
 *     EXT_DB     == 10 log10(s/p)
 *     DIAT       == (p - s)/(p + s)
 *
 * The derivatives get the same treatment: every one is checked against a flat table's own Jacobian
 * rows for R and T, which are themselves checked against closed-form optics and central
 * differences. So nothing here rests on a step size.
 *
 * THE THREE SWITCHES MUST STAY IN STEP. A code added to tf_target_value and forgotten in
 * tf_target_derivative gives a value with no gradient; forgotten in tf_target_polarisations it is
 * refused at plan time. They are kept adjacent for that reason, and the last test here walks every
 * new code and asserts all three answer.
 */

#include "thinfilm.h"

#include <cstdio>
#include <cmath>
#include <vector>

extern "C" {
tf_status tf_query_plan_sizes(int32_t, int32_t, int32_t, int32_t, tf_plan_sizes*);
tf_status tf_plan(const tf_problem*, const tf_operands*, const tf_variables*, int32_t,
                  tf_plan_buffers*);
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
        std::printf("  FAIL  %-44s got %.12g  expected %.12g  delta %.3g\n",
                    what, got, want, std::fabs(got - want));
    }
}

static void expect_eq(long long got, long long want, const char* what)
{
    ++g_checks;
    if (got != want)
    {
        ++g_failures;
        std::printf("  FAIL  %-44s got %lld  expected %lld\n", what, got, want);
    }
}

static const double PI = 3.14159265358979323846;
static const int NV = 2;

/* Two layers at 50 degrees, the first absorbing - so A is non-zero, s and p differ, and every one
 * of these quantities has something to say. */
struct Caller
{
    std::vector<double>     thickness{ 83.0, 118.0 };
    std::vector<int32_t>    layer_material{ 0, 1 };
    std::vector<tf_complex> index{ tf_complex{2.30, 0.030}, tf_complex{1.46, 0.0} };
    std::vector<double>     wavelength{ 550.0 };
    std::vector<int32_t>    point_wavelength{ 0 };
    std::vector<double>     point_angle{ 50.0 * PI / 180.0 };
    std::vector<tf_complex> n_in{ tf_complex{1.0, 0.0} }, n_out{ tf_complex{1.52, 0.0} };

    std::vector<int32_t> tcode, tpoint;
    std::vector<double>  ttarget, tweight, tparam;
    std::vector<int32_t> vkind{ TF_VARIABLE_LAYER_THICKNESS, TF_VARIABLE_LAYER_THICKNESS },
                         vlayer{ 0, 1 }, vmaterial{ 0, 0 }, vcoef{ 0, 0 };

    tf_problem problem{}; tf_operands operands{}; tf_variables variables{};
    std::vector<int32_t> path, order, inert, polneed, spanfirst, spancount, spanop, spanslot;
    std::vector<int32_t> opres, oporder, oplevel;
    tf_plan_report report{}; int32_t plan_status = 0, eval_status = 0;
    tf_plan_buffers pbuf{};
    std::vector<double> scratch, residual, jacobian, opvalue;
    tf_workspace ws{}; double merit = 0.0; tf_output out{};

    void wire()
    {
        problem.n_layers = 2; problem.n_materials = 2;
        problem.n_wavelengths = 1; problem.n_points = 1;
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
        operands.code = tcode.data(); operands.point = tpoint.data();
        operands.target = ttarget.data(); operands.weight = tweight.data();
        operands.comparison = nullptr; operands.target_hi = nullptr;
        operands.tolerance = nullptr; operands.scale = nullptr;
        operands.n_span = 0; operands.reduction = nullptr;
        operands.point_first = nullptr; operands.point_count = nullptr;
        operands.point_index = nullptr; operands.point_weight = nullptr;
        operands.reduction_param = tparam.empty() ? nullptr : tparam.data();
        operands.n_input = 0; operands.input_first = nullptr;
        operands.input_count = nullptr; operands.input_index = nullptr;

        variables.n_variables = NV;
        variables.kind = vkind.data(); variables.layer = vlayer.data();
        variables.material = vmaterial.data(); variables.coefficient = vcoef.data();
    }

    tf_status plan()
    {
        wire();
        tf_plan_sizes s{};
        tf_query_plan_sizes(1, NV, operands.n_operands, operands.n_operands, &s);
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
        opvalue.assign(tcode.size(), 0.0);
        residual.assign((size_t)report.n_residuals, 0.0);
        jacobian.assign((size_t)report.n_residuals * NV, 0.0);
        out.residual = residual.data(); out.jacobian = jacobian.data();
        out.value = opvalue.data(); out.operand_flag = nullptr;
        out.n_operands_flagged = nullptr;
        out.merit = &merit; out.status = &eval_status;
        return TF_OK;
    }

    tf_status jac() { wire(); return tf_evaluate_jacobian(&problem, &operands, &variables,
                                                          &pbuf, &ws, &out); }
    double J(int r, int v) const { return jacobian[(size_t)v * residual.size() + (size_t)r]; }
};

/* A table of the given codes, all scored with target 0 and weight 1: value == residual and the
 * Jacobian row IS d(value)/d(thickness). */
static Caller table(const std::vector<int32_t>& codes)
{
    Caller c;
    for (size_t i = 0; i < codes.size(); ++i)
    {
        c.tcode.push_back(codes[i]); c.tpoint.push_back(0);
        c.ttarget.push_back(0.0); c.tweight.push_back(1.0);
        c.tparam.push_back(0.0); c.tparam.push_back(0.0);
    }
    return c;
}

/* ---------------------------------------------------------------------------------------------
 * Absorptance
 * ------------------------------------------------------------------------------------------- */

static void AbsorptanceClosesTheEnergyBudget()
{
    std::printf("A + R + T = 1 exactly, and its gradient sums to zero with theirs\n");

    Caller c = table({ TF_TARGET_RS, TF_TARGET_TS, TF_TARGET_AS,
                       TF_TARGET_RP, TF_TARGET_TP, TF_TARGET_AP,
                       TF_TARGET_RA, TF_TARGET_TA, TF_TARGET_AA });
    expect_eq(c.plan(), TF_OK, "plans");
    expect_eq(c.jac(), TF_OK, "evaluates");

    const char* who[3] = { "s", "p", "average" };
    for (int g = 0; g < 3; ++g)
    {
        const int R = g * 3, T = g * 3 + 1, A = g * 3 + 2;
        near_(c.opvalue[(size_t)R] + c.opvalue[(size_t)T] + c.opvalue[(size_t)A], 1.0, 1e-14,
              "R + T + A sums to one");
        for (int v = 0; v < NV; ++v)
            near_(c.J(R, v) + c.J(T, v) + c.J(A, v), 0.0, 1e-14,
                  "and the three gradients cancel");
        (void)who[g];
    }

    /* The fixture has to absorb something, or the identity is trivially 1 = R + T. */
    ++g_checks;
    if (!(c.opvalue[2] > 1e-3))
    { ++g_failures; std::printf("  FAIL  nothing is absorbed; A proves nothing\n"); }
}

/* ---------------------------------------------------------------------------------------------
 * Optical density
 * ------------------------------------------------------------------------------------------- */

static void OpticalDensityIsMinusLogTAndItsSlopeFollows()
{
    std::printf("OD is -log10(T), and d(OD)/dt is -(dT/dt)/(T ln10)\n");

    Caller c = table({ TF_TARGET_TS, TF_TARGET_ODS, TF_TARGET_TP, TF_TARGET_ODP,
                       TF_TARGET_TA, TF_TARGET_ODA });
    expect_eq(c.plan(), TF_OK, "plans");
    expect_eq(c.jac(), TF_OK, "evaluates");

    for (int g = 0; g < 3; ++g)
    {
        const int T = g * 2, D = g * 2 + 1;
        near_(c.opvalue[(size_t)D], -std::log10(c.opvalue[(size_t)T]), 1e-13, "OD is -log10 T");
        for (int v = 0; v < NV; ++v)
            near_(c.J(D, v), -c.J(T, v) / (c.opvalue[(size_t)T] * std::log(10.0)), 1e-12,
                  "and its slope is the chain rule");
    }

    /* ODA IS THE OD OF THE AVERAGE, NOT THE AVERAGE OF THE ODs, and the two differ whenever the
     * two polarisations differ. Asserting they differ is what stops the wrong one passing. */
    ++g_checks;
    const double mean_of_od = 0.5 * (c.opvalue[1] + c.opvalue[3]);
    if (!(std::fabs(c.opvalue[5] - mean_of_od) > 1e-6))
    {
        ++g_failures;
        std::printf("  FAIL  ODA equals the mean of the ODs here; the fixture cannot tell them "
                    "apart\n");
    }
}

static void TheOpticalDensityFloorClampsValueAndSlopeTogether()
{
    std::printf("past its ceiling OD stops moving, and so does its gradient\n");

    /* A ceiling low enough that this stack is already past it. */
    Caller c = table({ TF_TARGET_ODS });
    c.tparam = { 0.0, 0.05 };          /* param[1] = the OD ceiling */
    expect_eq(c.plan(), TF_OK, "plans");
    expect_eq(c.jac(), TF_OK, "evaluates");

    near_(c.opvalue[0], 0.05, 0.0, "pinned at the ceiling, exactly");
    for (int v = 0; v < NV; ++v)
        near_(c.J(0, v), 0.0, 0.0, "with no gradient - the value no longer depends on the design");

    /* And a generous ceiling leaves it alone. */
    Caller o = table({ TF_TARGET_ODS });
    o.tparam = { 0.0, 30.0 };
    o.plan(); o.jac();
    ++g_checks;
    if (!(o.J(0, 0) != 0.0))
    { ++g_failures; std::printf("  FAIL  an unclamped OD should still have a gradient\n"); }
}

/* ---------------------------------------------------------------------------------------------
 * Phase, non-polarisation, extinction, diattenuation
 * ------------------------------------------------------------------------------------------- */

static void PhaseObeysTheDeltaConvention()
{
    std::printf("absolute phase is an angle, and answers to the same convention Delta does\n");

    Caller p = table({ TF_TARGET_R_PHASE_S, TF_TARGET_R_PHASE_P,
                       TF_TARGET_T_PHASE_S, TF_TARGET_T_PHASE_P });
    expect_eq(p.plan(), TF_OK, "plans");
    expect_eq(p.jac(), TF_OK, "evaluates");

    for (int i = 0; i < 4; ++i)
    {
        ++g_checks;
        if (!(p.opvalue[(size_t)i] > -PI && p.opvalue[(size_t)i] <= PI))
        {
            ++g_failures;
            std::printf("  FAIL  phase %d outside (-pi, pi]: %.12g\n", i, p.opvalue[(size_t)i]);
        }
    }

    Caller q = table({ TF_TARGET_R_PHASE_S, TF_TARGET_R_PHASE_P,
                       TF_TARGET_T_PHASE_S, TF_TARGET_T_PHASE_P });
    q.wire(); q.operands.delta_convention = TF_DELTA_POSITIVE;
    q.plan(); q.wire(); q.operands.delta_convention = TF_DELTA_POSITIVE; q.jac();

    for (int i = 0; i < 4; ++i)
    {
        ++g_checks;
        if (!(q.opvalue[(size_t)i] >= 0.0 && q.opvalue[(size_t)i] < 2.0 * PI))
        {
            ++g_failures;
            std::printf("  FAIL  phase %d outside [0, 2pi): %.12g\n", i, q.opvalue[(size_t)i]);
        }
        near_(std::cos(q.opvalue[(size_t)i]), std::cos(p.opvalue[(size_t)i]), 1e-14,
              "the same angle either way");
        /* The convention shifts the value by a constant, so the SLOPE must not move at all. */
        for (int v = 0; v < NV; ++v)
            near_(q.J(i, v), p.J(i, v), 0.0, "and the slope is untouched by the interval");
    }
}

static void NonPolarisationExtinctionAndDiattenuation()
{
    std::printf("NPOL, extinction in dB and diattenuation, each against its own definition\n");

    Caller c = table({ TF_TARGET_RS, TF_TARGET_RP, TF_TARGET_TS, TF_TARGET_TP,
                       TF_TARGET_R_NPOL, TF_TARGET_T_NPOL,
                       TF_TARGET_R_EXT_DB, TF_TARGET_T_EXT_DB,
                       TF_TARGET_R_DIAT, TF_TARGET_T_DIAT });
    expect_eq(c.plan(), TF_OK, "plans");
    expect_eq(c.jac(), TF_OK, "evaluates");

    const double Rs = c.opvalue[0], Rp = c.opvalue[1];
    const double Ts = c.opvalue[2], Tp = c.opvalue[3];

    near_(c.opvalue[4], std::fabs(Rs - Rp), 1e-15, "R NPOL is |Rs - Rp|");
    near_(c.opvalue[5], std::fabs(Ts - Tp), 1e-15, "T NPOL is |Ts - Tp|");
    near_(c.opvalue[6], 10.0 * std::log10(Rs / Rp), 1e-13, "R extinction in dB");
    near_(c.opvalue[7], 10.0 * std::log10(Ts / Tp), 1e-13, "T extinction in dB");
    near_(c.opvalue[8], (Rp - Rs) / (Rp + Rs), 1e-15, "R diattenuation");
    near_(c.opvalue[9], (Tp - Ts) / (Tp + Ts), 1e-15, "T diattenuation");

    for (int v = 0; v < NV; ++v)
    {
        const double dRs = c.J(0, v), dRp = c.J(1, v);
        const double dTs = c.J(2, v), dTp = c.J(3, v);

        near_(c.J(4, v), (Rs > Rp ? 1.0 : -1.0) * (dRs - dRp), 1e-14, "d|Rs - Rp|");
        near_(c.J(6, v), (10.0 / std::log(10.0)) * (dRs / Rs - dRp / Rp), 1e-12, "d(R dB)");
        near_(c.J(8, v), 2.0 * (Rs * dRp - Rp * dRs) / ((Rp + Rs) * (Rp + Rs)), 1e-14,
              "d(R diattenuation)");
        near_(c.J(9, v), 2.0 * (Ts * dTp - Tp * dTs) / ((Tp + Ts) * (Tp + Ts)), 1e-14,
              "d(T diattenuation)");
    }

    /* The two polarisations must genuinely differ, or every one of these is zero. */
    ++g_checks;
    if (!(std::fabs(Rs - Rp) > 1e-3))
    { ++g_failures; std::printf("  FAIL  s and p agree here; nothing above says anything\n"); }
}

/* ---------------------------------------------------------------------------------------------
 * The three switches
 * ------------------------------------------------------------------------------------------- */

/* A code added to tf_target_value and forgotten in tf_target_derivative gives a value with no
 * gradient - and a gradient of zero is a perfectly plausible number. This walks every code added
 * in this step and checks all three switches answer: the plan accepts it (polarisations), it has
 * a value, and it has a gradient somewhere. */
static void EveryNewCodeAnswersInAllThreeSwitches()
{
    std::printf("every new code is present in all three switches, not just the first\n");

    const int32_t codes[] = {
        TF_TARGET_AS, TF_TARGET_AP, TF_TARGET_AA,
        TF_TARGET_ODS, TF_TARGET_ODP, TF_TARGET_ODA,
        TF_TARGET_R_PHASE_S, TF_TARGET_R_PHASE_P,
        TF_TARGET_T_PHASE_S, TF_TARGET_T_PHASE_P,
        TF_TARGET_R_NPOL, TF_TARGET_T_NPOL,
        TF_TARGET_R_EXT_DB, TF_TARGET_T_EXT_DB,
        TF_TARGET_R_DIAT, TF_TARGET_T_DIAT
    };
    const int n = (int)(sizeof codes / sizeof codes[0]);

    for (int i = 0; i < n; ++i)
    {
        Caller c = table({ codes[i] });
        expect_eq(c.plan(), TF_OK, "the plan accepts it - the polarisation switch answered");
        expect_eq(c.jac(), TF_OK, "it evaluates - the value switch answered");

        ++g_checks;
        if (c.J(0, 0) == 0.0 && c.J(0, 1) == 0.0)
        {
            ++g_failures;
            std::printf("  FAIL  code %d has no gradient in either variable - the derivative "
                        "switch is probably missing it\n", (int)codes[i]);
        }
    }
}

int main()
{
    setvbuf(stdout, NULL, _IONBF, 0);
    std::printf("\n=== Phase 1 spectral operands ===\n\n");

    AbsorptanceClosesTheEnergyBudget();                 std::printf("\n");
    OpticalDensityIsMinusLogTAndItsSlopeFollows();      std::printf("\n");
    TheOpticalDensityFloorClampsValueAndSlopeTogether();std::printf("\n");
    PhaseObeysTheDeltaConvention();                     std::printf("\n");
    NonPolarisationExtinctionAndDiattenuation();        std::printf("\n");
    EveryNewCodeAnswersInAllThreeSwitches();            std::printf("\n");

    std::printf("=== %d checks, %d failures ===\n\n", g_checks, g_failures);
    return g_failures == 0 ? 0 : 1;
}
