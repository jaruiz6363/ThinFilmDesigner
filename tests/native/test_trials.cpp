/* test_trials.cpp — S6.4, the Monte Carlo trial axis.
 *
 * THE DESIGN CLAIMS THIS FILE EXISTS TO MEASURE, because each of them is the kind that is easy to
 * assert and easy to get backwards:
 *
 *   1. A trial is this problem with two pointers swapped. So a three-trial WORST must equal the
 *      largest residual of three SEPARATELY EVALUATED single-stack problems, exactly - not nearly.
 *      Everything below cross-checks against stacks built one at a time, never against a formula
 *      re-derived here, because a formula re-derived here would be the same mistake twice.
 *
 *   2. WORST means the largest RESIDUAL, not the largest value. Under a GE comparison the worst
 *      trial is the one with the SMALLEST value, and a test that only ever used LE would pass
 *      just as happily with the wrong definition.
 *
 *   3. No buffer scales with the trial count. Four trials and four hundred must report the
 *      identical scratch size, and tf_query_plan_sizes must not have needed a trial argument.
 *      That is the whole claim that the axis was put outside the point table.
 *
 *   4. The marginal is not the joint. TF_TRIAL_FRACTION_PASSING gives the fraction of trials one
 *      operand passes; the specification's YIELD is the fraction where a SET passes at once. On
 *      correlated failures those differ, and the file measures a case where multiplying the
 *      marginals gives half the right answer.
 *
 *   5. A yield has no gradient, and saying so is not a gap. It is refused by number.
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
tf_status tf_evaluate_needle(const tf_problem*, const tf_operands*, const tf_plan_buffers*,
                             const tf_needle_request*, tf_workspace*, tf_needle_output*);
tf_status tf_evaluate_index_derivative(const tf_problem*, const tf_operands*,
                                       const tf_plan_buffers*, tf_workspace*, tf_index_output*);
}

static int g_failures = 0;
static int g_checks   = 0;

static void exactly(double got, double want, const char* what)
{
    ++g_checks;
    if (got != want)
    {
        ++g_failures;
        std::printf("  FAIL  %-46s got %.17g  expected %.17g (EXACTLY)\n", what, got, want);
    }
}

static void near_(double got, double want, double tol, const char* what)
{
    ++g_checks;
    if (!(std::fabs(got - want) <= tol))
    {
        ++g_failures;
        std::printf("  FAIL  %-46s got %.17g  expected %.17g  (d %.3g)\n",
                    what, got, want, got - want);
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

static void expect(int cond, const char* what)
{
    ++g_checks;
    if (!cond) { ++g_failures; std::printf("  FAIL  %s\n", what); }
}

/* Three wavelengths, one angle. Two materials, three layers. The trials below move the middle
 * layer hard enough that the reflectance genuinely separates. */
static const int NW = 3;
static const int NL = 3;
static const int NM = 2;

struct Caller
{
    std::vector<double>     thickness{ 92.0, 140.0, 61.0 };
    std::vector<int32_t>    layer_material{ 0, 1, 0 };
    std::vector<tf_complex> index;
    std::vector<double>     wavelength{ 500.0, 550.0, 600.0 };
    std::vector<int32_t>    point_wavelength{ 0, 1, 2 };
    std::vector<double>     point_angle{ 0.0, 0.0, 0.0 };
    std::vector<tf_complex> n_in, n_out;

    /* The trial block. Empty means no trial axis at all. */
    std::vector<double>     trial_thickness;   /* [n_trials * NL] trial-major */
    std::vector<tf_complex> trial_index;       /* [n_trials * NM * NW]        */
    int32_t                 n_trials = 0;

    std::vector<int32_t> tcode, tcomp, tred;
    std::vector<double>  ttarget, tweight, tparam;
    std::vector<int32_t> spanf, spanc, spani;
    std::vector<int32_t> vkind, vlayer, vmaterial, vcoef;

    tf_problem problem{}; tf_operands operands{}; tf_variables variables{};
    std::vector<int32_t> path, order, inert, polneed, sf, sc, so, ss;
    std::vector<int32_t> opres, oporder, oplevel;
    tf_plan_report report{}; int32_t plan_status = 0, eval_status = 0;
    tf_plan_buffers pbuf{};
    std::vector<double> scratch, residual, jacobian, opvalue, tvalue, nres, nmap;
    std::vector<int32_t> opflag;
    int32_t nflagged = 0;
    tf_workspace ws{}; double merit = 0.0;
    tf_output out{}; tf_needle_output nout{};

    Caller()
    {
        for (int m = 0; m < NM; ++m)
        {
            const double nn = (m == 0) ? 2.35 : 1.46;
            for (int w = 0; w < NW; ++w) index.push_back(tf_complex{ nn, 0.0 });
        }
        for (int w = 0; w < NW; ++w)
        { n_in.push_back(tf_complex{1.0, 0.0}); n_out.push_back(tf_complex{1.52, 0.0}); }
        for (int32_t j = 0; j < NL; ++j)
        { vkind.push_back(TF_VARIABLE_LAYER_THICKNESS); vlayer.push_back(j);
          vmaterial.push_back(0); vcoef.push_back(0); }
    }

    void wire()
    {
        problem.n_layers = NL; problem.n_materials = NM;
        problem.n_wavelengths = NW; problem.n_points = NW;
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
        problem.material_stress = nullptr;
        problem.material_index_is_extrapolated = nullptr;
        problem.index_incident_is_extrapolated = nullptr;
        problem.index_exit_is_extrapolated = nullptr;
        problem.n_trials = n_trials;
        problem.trial_layer_thickness = trial_thickness.empty() ? nullptr
                                                                : trial_thickness.data();
        problem.trial_material_index = trial_index.empty() ? nullptr : trial_index.data();

        operands.n_operands = (int32_t)tcode.size();
        operands.delta_convention = TF_DELTA_PRINCIPAL;
        operands.extrapolation_policy = TF_EXTRAPOLATION_FLAG;
        operands.trial_reduction = tred.empty() ? nullptr : tred.data();
        operands.code = tcode.data();
        operands.point = nullptr;
        operands.target = ttarget.data(); operands.weight = tweight.data();
        operands.comparison = tcomp.empty() ? nullptr : tcomp.data();
        operands.target_hi = nullptr; operands.tolerance = nullptr; operands.scale = nullptr;
        operands.n_span = (int32_t)spani.size();
        operands.reduction = nullptr;
        operands.point_first = spanf.data(); operands.point_count = spanc.data();
        operands.point_index = spani.data(); operands.point_weight = nullptr;
        operands.reduction_param = tparam.data();
        operands.n_input = 0; operands.input_first = nullptr;
        operands.input_count = nullptr; operands.input_index = nullptr;

        variables.n_variables = (int32_t)vkind.size();
        variables.kind = vkind.data(); variables.layer = vlayer.data();
        variables.material = vmaterial.data(); variables.coefficient = vcoef.data();
    }

    tf_status plan()
    {
        wire();
        tf_plan_sizes s{};
        tf_query_plan_sizes(problem.n_points, (int32_t)vkind.size(), operands.n_span,
                            operands.n_operands, &s);
        path.assign((size_t)s.point_path_count, -1);
        order.assign((size_t)s.point_order_count, -1);
        inert.assign((size_t)s.variable_inert_count, 0);
        polneed.assign((size_t)s.point_polarisation_count, 0);
        sf.assign((size_t)s.point_span_first_count, 0);
        sc.assign((size_t)s.point_span_count_count, 0);
        so.assign((size_t)s.point_span_operand_count, 0);
        ss.assign((size_t)s.point_span_slot_count, 0);
        opres.assign((size_t)s.operand_residual_count, -1);
        oporder.assign((size_t)s.operand_order_count, 0);
        oplevel.assign((size_t)s.operand_level_count, 0);
        pbuf.point_path = path.data(); pbuf.point_order = order.data();
        pbuf.variable_inert = inert.data(); pbuf.point_polarisation = polneed.data();
        pbuf.point_span_first = sf.data(); pbuf.point_span_count = sc.data();
        pbuf.point_span_operand = so.data(); pbuf.point_span_slot = ss.data();
        pbuf.operand_residual = opres.data(); pbuf.operand_order = oporder.data();
        pbuf.operand_level = oplevel.data();
        pbuf.report = &report; pbuf.status = &plan_status;

        const tf_status ps = tf_plan(&problem, &operands, &variables, 1, &pbuf);
        if (ps != TF_OK) return ps;

        scratch.assign((size_t)report.scratch_doubles, 0.0);
        ws.scratch = scratch.data(); ws.scratch_doubles = report.scratch_doubles;
        opvalue.assign(tcode.size(), 0.0);
        opflag.assign(tcode.size(), 0);
        if (n_trials > 0) tvalue.assign((size_t)n_trials * tcode.size(), 0.0);
        residual.assign((size_t)report.n_residuals, 0.0);
        jacobian.assign((size_t)report.n_residuals * vkind.size(), 0.0);
        nres.assign((size_t)report.n_residuals, 0.0);
        out.residual = residual.data(); out.jacobian = jacobian.data();
        out.value = opvalue.data();
        out.trial_value = tvalue.empty() ? nullptr : tvalue.data();
        out.operand_flag = opflag.data(); out.n_operands_flagged = &nflagged;
        out.merit = &merit; out.status = &eval_status;
        nout.residual = nres.data(); nout.status = &eval_status;
        return TF_OK;
    }

    tf_status ev()
    {
        double* j = out.jacobian; out.jacobian = nullptr;
        const tf_status s = tf_evaluate(&problem, &operands, &pbuf, &ws, &out);
        out.jacobian = j;
        return s;
    }
    tf_status jac()
    { return tf_evaluate_jacobian(&problem, &operands, &variables, &pbuf, &ws, &out); }
};

/* The four stacks every test below uses. Trial 1 is deliberately far from nominal. */
static std::vector<double> trial_stacks()
{
    return std::vector<double>{
         92.0, 140.0,  61.0,     /* trial 0: the nominal stack exactly                */
         92.0,  40.0,  61.0,     /* trial 1: a badly short middle layer               */
         96.0, 145.0,  58.0,     /* trial 2: a small, realistic error                 */
         88.0, 200.0,  66.0      /* trial 3: long the other way                       */
    };
}

/* One operand over all three points, plus optional siblings. */
static void add_operand(Caller& c, int32_t code, int32_t comparison, double target,
                        int32_t reduction)
{
    c.tcode.push_back(code);
    c.tcomp.push_back(comparison);
    c.ttarget.push_back(target);
    c.tweight.push_back(1.0);
    c.tred.push_back(reduction);
    c.tparam.push_back(0.0); c.tparam.push_back(0.0);
    c.spanf.push_back((int32_t)c.spani.size());
    c.spanc.push_back(NW);
    for (int32_t p = 0; p < NW; ++p) c.spani.push_back(p);
}

/* THE INDEPENDENT ORACLE: one stack, no trial axis, evaluated on its own. Everything the trial
 * machinery produces is checked against a set of these. */
static double single_stack_value(const std::vector<double>& thick, int32_t comparison,
                                 double target)
{
    Caller c;
    c.thickness = thick;
    add_operand(c, TF_TARGET_RS, comparison, target, TF_TRIAL_NOMINAL);
    if (c.plan() != TF_OK) { std::printf("  FAIL  oracle would not plan\n"); ++g_failures; return 0.0; }
    if (c.ev() != TF_OK)   { std::printf("  FAIL  oracle would not evaluate\n"); ++g_failures; return 0.0; }
    return c.opvalue[0];
}

static void single_stack_row(const std::vector<double>& thick, double* row3)
{
    Caller c;
    c.thickness = thick;
    add_operand(c, TF_TARGET_RS, TF_COMPARE_EQUAL, 0.0, TF_TRIAL_NOMINAL);
    if (c.plan() != TF_OK || c.jac() != TF_OK)
    { std::printf("  FAIL  oracle jacobian\n"); ++g_failures; return; }
    for (int v = 0; v < 3; ++v) row3[v] = c.jacobian[(size_t)v * c.report.n_residuals + 0];
}

static std::vector<double> stack_of(const std::vector<double>& all, int t)
{
    return std::vector<double>{ all[(size_t)t * 3 + 0], all[(size_t)t * 3 + 1],
                                all[(size_t)t * 3 + 2] };
}

/* --------------------------------------------------------------------------------------------- */

static void AnUntouchedTableDoesNotNoticeTheAxis()
{
    std::printf("a table with no trial reduction is unchanged by trials existing\n");

    Caller off;
    add_operand(off, TF_TARGET_RS, TF_COMPARE_EQUAL, 0.0, TF_TRIAL_NOMINAL);
    expect_eq(off.plan(), TF_OK, "plans with no trials");
    expect_eq(off.ev(), TF_OK, "evaluates");

    Caller on;
    on.n_trials = 4; on.trial_thickness = trial_stacks();
    add_operand(on, TF_TARGET_RS, TF_COMPARE_EQUAL, 0.0, TF_TRIAL_NOMINAL);
    expect_eq(on.plan(), TF_OK, "plans with four trials it does not read");
    expect_eq(on.ev(), TF_OK, "evaluates");

    exactly(on.opvalue[0], off.opvalue[0], "and returns the identical value");
    exactly(on.merit, off.merit, "and the identical merit");
}

static void MeanIsTheMeanOfSeparatelyEvaluatedStacks()
{
    std::printf("MEAN equals the average of four stacks evaluated one at a time\n");

    const std::vector<double> all = trial_stacks();
    double sum = 0.0;
    for (int t = 0; t < 4; ++t) sum += single_stack_value(stack_of(all, t), TF_COMPARE_EQUAL, 0.0);

    Caller c;
    c.n_trials = 4; c.trial_thickness = all;
    add_operand(c, TF_TARGET_RS, TF_COMPARE_EQUAL, 0.0, TF_TRIAL_MEAN);
    expect_eq(c.plan(), TF_OK, "plans");
    expect_eq(c.ev(), TF_OK, "evaluates");

    exactly(c.opvalue[0], sum / 4.0, "the mean over trials");

    /* And the per-trial table matches those same four numbers, one for one. */
    for (int t = 0; t < 4; ++t)
        exactly(c.tvalue[(size_t)t], single_stack_value(stack_of(all, t), TF_COMPARE_EQUAL, 0.0),
                "trial_value holds each trial's own number");
}

/* CLAIM 2, AND THE ONE A CARELESS TEST WOULD MISS. */
static void WorstIsTheLargestResidualAndNotTheLargestValue()
{
    std::printf("WORST follows the comparison: max value under LE, MIN value under GE\n");

    const std::vector<double> all = trial_stacks();
    double v[4];
    for (int t = 0; t < 4; ++t) v[t] = single_stack_value(stack_of(all, t), TF_COMPARE_EQUAL, 0.0);

    double vmax = v[0], vmin = v[0];
    for (int t = 1; t < 4; ++t) { if (v[t] > vmax) vmax = v[t]; if (v[t] < vmin) vmin = v[t]; }
    expect(vmax != vmin, "the four trials genuinely differ, or this test proves nothing");

    /* LE: a reflectance held BELOW a limit is worst where it is highest. */
    {
        Caller c;
        c.n_trials = 4; c.trial_thickness = all;
        add_operand(c, TF_TARGET_RS, TF_COMPARE_LE, 0.0, TF_TRIAL_WORST);
        expect_eq(c.plan(), TF_OK, "plans under LE");
        expect_eq(c.ev(), TF_OK, "evaluates");
        exactly(c.opvalue[0], vmax, "LE picks the largest value");
    }

    /* GE: the same quantity held ABOVE a limit is worst where it is LOWEST. A WORST defined as
     * "the largest value" would return vmax here and be wrong without ever saying so. */
    {
        Caller c;
        c.n_trials = 4; c.trial_thickness = all;
        add_operand(c, TF_TARGET_RS, TF_COMPARE_GE, 1.0, TF_TRIAL_WORST);
        expect_eq(c.plan(), TF_OK, "plans under GE");
        expect_eq(c.ev(), TF_OK, "evaluates");
        exactly(c.opvalue[0], vmin, "GE picks the SMALLEST value - the residual decides, not the value");
        expect(vmin != vmax, "and the two answers differ, so the test can tell them apart");
    }
}

static void WorstDifferentiatesTheWinningTrialAndNothingElse()
{
    std::printf("the WORST gradient is the winning trial's gradient, exactly\n");

    const std::vector<double> all = trial_stacks();
    double v[4];
    for (int t = 0; t < 4; ++t) v[t] = single_stack_value(stack_of(all, t), TF_COMPARE_EQUAL, 0.0);
    int win = 0;
    for (int t = 1; t < 4; ++t) if (v[t] > v[win]) win = t;

    double oracle[3];
    single_stack_row(stack_of(all, win), oracle);

    Caller c;
    c.n_trials = 4; c.trial_thickness = all;
    add_operand(c, TF_TARGET_RS, TF_COMPARE_EQUAL, 0.0, TF_TRIAL_WORST);
    expect_eq(c.plan(), TF_OK, "plans");
    expect_eq(c.jac(), TF_OK, "differentiates");

    exactly(c.opvalue[0], v[win], "the value is the winner's value");
    for (int vi = 0; vi < 3; ++vi)
        exactly(c.jacobian[(size_t)vi * c.report.n_residuals + 0], oracle[vi],
                "and every column is the winner's column");

    /* AND NOT SOMEBODY ELSE'S. A gradient averaged over trials would also be smooth and
     * plausible, so the test has to show the losing trials contributed nothing. */
    int other = (win == 0) ? 1 : 0;
    double loser[3];
    single_stack_row(stack_of(all, other), loser);
    expect(loser[1] != oracle[1], "a losing trial's column really is a different number");
}

static void MeanDifferentiatesEveryTrialEvenly()
{
    std::printf("the MEAN gradient is the mean of the per-trial gradients\n");

    const std::vector<double> all = trial_stacks();
    double acc[3] = { 0.0, 0.0, 0.0 };
    for (int t = 0; t < 4; ++t)
    {
        double row[3];
        single_stack_row(stack_of(all, t), row);
        for (int v = 0; v < 3; ++v) acc[v] += row[v] / 4.0;
    }

    Caller c;
    c.n_trials = 4; c.trial_thickness = all;
    add_operand(c, TF_TARGET_RS, TF_COMPARE_EQUAL, 0.0, TF_TRIAL_MEAN);
    expect_eq(c.plan(), TF_OK, "plans");
    expect_eq(c.jac(), TF_OK, "differentiates");

    for (int v = 0; v < 3; ++v)
        near_(c.jacobian[(size_t)v * c.report.n_residuals + 0], acc[v], 1e-15,
              "each column is the average of the four");
}

static void AYieldCountsTrialsAndRefusesToBeDifferentiated()
{
    std::printf("FRACTION_PASSING counts, and says outright that it has no gradient\n");

    const std::vector<double> all = trial_stacks();
    double v[4];
    for (int t = 0; t < 4; ++t) v[t] = single_stack_value(stack_of(all, t), TF_COMPARE_EQUAL, 0.0);

    /* A limit that at least one trial meets and at least one does not, computed from the four
     * values rather than guessed, so the fixture cannot drift into being vacuous. */
    double lo = v[0], hi = v[0];
    for (int t = 1; t < 4; ++t) { if (v[t] < lo) lo = v[t]; if (v[t] > hi) hi = v[t]; }
    const double limit = 0.5 * (lo + hi);
    int passing = 0;
    for (int t = 0; t < 4; ++t) if (v[t] <= limit) ++passing;
    expect(passing > 0 && passing < 4, "some trials pass and some fail, or this proves nothing");

    Caller c;
    c.n_trials = 4; c.trial_thickness = all;
    add_operand(c, TF_TARGET_RS, TF_COMPARE_LE, limit, TF_TRIAL_FRACTION_PASSING);
    expect_eq(c.plan(), TF_OK, "plans");
    expect_eq(c.ev(), TF_OK, "tf_evaluate returns the value");
    exactly(c.opvalue[0], (double)passing / 4.0, "the fraction of trials meeting the limit");

    /* The gradient is refused by number, not returned as zero. */
    expect_eq(c.jac(), TF_TRIAL_GRADIENT_UNDEFINED,
              "tf_evaluate_jacobian refuses rather than returning a zero row");
    expect_eq(c.eval_status, TF_TRIAL_GRADIENT_UNDEFINED, "and says so in the status");
}

/* CLAIM 4: the whole reason tf_output.trial_value exists. */
static void TheMarginalIsNotTheJoint()
{
    std::printf("a spec SET needs the per-trial table: marginals multiplied give the wrong yield\n");

    /* Four trials in which two operands fail TOGETHER - which is what a thickness error does. */
    std::vector<double> all{
         92.0, 140.0,  61.0,
         92.0, 140.0,  61.0,
         92.0,  40.0,  61.0,
         92.0,  40.0,  61.0
    };

    double vgood = single_stack_value(stack_of(all, 0), TF_COMPARE_EQUAL, 0.0);
    double vbad  = single_stack_value(stack_of(all, 2), TF_COMPARE_EQUAL, 0.0);
    expect(vgood != vbad, "the good and bad stacks differ");
    const double limit = 0.5 * (vgood + vbad);
    const int good_is_lower = (vgood < vbad) ? 1 : 0;
    expect(good_is_lower == 1 || good_is_lower == 0, "orientation known");

    Caller c;
    c.n_trials = 4; c.trial_thickness = all;
    /* Two operands with the same limit: perfectly correlated, since they read the same stack. */
    add_operand(c, TF_TARGET_RS, good_is_lower ? TF_COMPARE_LE : TF_COMPARE_GE, limit,
                TF_TRIAL_FRACTION_PASSING);
    add_operand(c, TF_TARGET_RS, good_is_lower ? TF_COMPARE_LE : TF_COMPARE_GE, limit,
                TF_TRIAL_FRACTION_PASSING);
    expect_eq(c.plan(), TF_OK, "plans");
    expect_eq(c.ev(), TF_OK, "evaluates");

    exactly(c.opvalue[0], 0.5, "operand 0 passes half the trials");
    exactly(c.opvalue[1], 0.5, "operand 1 passes half the trials");

    /* The joint, computed the only way it can be: by walking the per-trial table. */
    int both = 0;
    for (int t = 0; t < 4; ++t)
    {
        const double a = c.tvalue[(size_t)t * 2 + 0];
        const double b = c.tvalue[(size_t)t * 2 + 1];
        const int pa = good_is_lower ? (a <= limit) : (a >= limit);
        const int pb = good_is_lower ? (b <= limit) : (b >= limit);
        if (pa && pb) ++both;
    }
    exactly((double)both / 4.0, 0.5, "and BOTH pass on half the trials - the joint is 0.5");
    exactly(c.opvalue[0] * c.opvalue[1], 0.25,
            "while multiplying the marginals gives 0.25, which is half the right answer");
}

/* CLAIM 3: the axis is outside the point table, so nothing grows with it. */
static void NoBufferGrowsWithTheTrialCount()
{
    std::printf("four trials and four hundred need the identical scratch\n");

    std::vector<double> few, many;
    for (int t = 0; t < 4; ++t)
    { few.push_back(92.0 + t); few.push_back(140.0 - t); few.push_back(61.0); }
    for (int t = 0; t < 400; ++t)
    { many.push_back(92.0 + (t % 7)); many.push_back(140.0 - (t % 5)); many.push_back(61.0); }

    Caller a; a.n_trials = 4;   a.trial_thickness = few;
    add_operand(a, TF_TARGET_RS, TF_COMPARE_LE, 0.02, TF_TRIAL_WORST);
    expect_eq(a.plan(), TF_OK, "four trials plan");

    Caller b; b.n_trials = 400; b.trial_thickness = many;
    add_operand(b, TF_TARGET_RS, TF_COMPARE_LE, 0.02, TF_TRIAL_WORST);
    expect_eq(b.plan(), TF_OK, "four hundred trials plan");

    expect_eq(b.report.scratch_doubles, a.report.scratch_doubles,
              "the scratch does not grow with the trial count");
    expect_eq((long long)b.path.size(), (long long)a.path.size(),
              "and neither does point_path - the point table never learned about trials");
    expect_eq(b.report.n_residuals, a.report.n_residuals, "nor the residual count");

    expect_eq(b.ev(), TF_OK, "and four hundred trials evaluate");
}

static void TrialsMayPerturbTheIndexToo()
{
    std::printf("a trial may move the index as well as the thickness\n");

    /* Two trials whose stacks are nominal but whose high index differs. */
    std::vector<tf_complex> ti;
    for (int t = 0; t < 2; ++t)
    {
        const double nh = (t == 0) ? 2.30 : 2.40;
        for (int w = 0; w < NW; ++w) ti.push_back(tf_complex{ nh, 0.0 });
        for (int w = 0; w < NW; ++w) ti.push_back(tf_complex{ 1.46, 0.0 });
    }

    Caller c;
    c.n_trials = 2; c.trial_index = ti;
    add_operand(c, TF_TARGET_RS, TF_COMPARE_EQUAL, 0.0, TF_TRIAL_MEAN);
    expect_eq(c.plan(), TF_OK, "plans with an index perturbation and no thickness one");
    expect_eq(c.ev(), TF_OK, "evaluates");

    expect(c.tvalue[0] != c.tvalue[1], "the two index draws give different reflectances");
    exactly(c.opvalue[0], 0.5 * (c.tvalue[0] + c.tvalue[1]), "and MEAN averages them");

    /* The nominal index is 2.35, between the two draws, so the mean is not the nominal value. */
    Caller nom;
    add_operand(nom, TF_TARGET_RS, TF_COMPARE_EQUAL, 0.0, TF_TRIAL_NOMINAL);
    expect_eq(nom.plan(), TF_OK, "the nominal plan");
    expect_eq(nom.ev(), TF_OK, "the nominal value");
    expect(c.opvalue[0] != nom.opvalue[0],
           "and the averaged answer is not the nominal one, so the swap really happened");
}

/* A stack that is lossless nominally and absorbing on one trial. The plan must choose the path
 * that every trial can use. */
static void ThePlanChoosesAPathEveryTrialCanUse()
{
    std::printf("a trial that introduces absorption forces the general path for that point\n");

    Caller nominal;
    add_operand(nominal, TF_TARGET_RS, TF_COMPARE_EQUAL, 0.0, TF_TRIAL_NOMINAL);
    expect_eq(nominal.plan(), TF_OK, "the lossless plan");
    expect_eq(nominal.report.n_general, 0, "which needs no general point at all");
    expect(nominal.report.n_structured > 0, "and is entirely structured");

    std::vector<tf_complex> ti;
    for (int t = 0; t < 2; ++t)
    {
        const double k = (t == 1) ? 0.01 : 0.0;   /* trial 1 absorbs, trial 0 does not */
        for (int w = 0; w < NW; ++w) ti.push_back(tf_complex{ 2.35, k });
        for (int w = 0; w < NW; ++w) ti.push_back(tf_complex{ 1.46, 0.0 });
    }

    Caller c;
    c.n_trials = 2; c.trial_index = ti;
    add_operand(c, TF_TARGET_RS, TF_COMPARE_EQUAL, 0.0, TF_TRIAL_WORST);
    expect_eq(c.plan(), TF_OK, "plans");
    expect_eq(c.report.n_structured, 0, "no point is left on the structured path");
    expect(c.report.n_general > 0,
           "because one trial absorbs, and the plan chooses ONE path per point");
    expect_eq(c.ev(), TF_OK, "and it evaluates");
}

static void TheOtherEntryPointsRefuseRatherThanGuess()
{
    std::printf("a needle map and an index derivative refuse while the axis is live\n");

    Caller c;
    c.n_trials = 4; c.trial_thickness = trial_stacks();
    add_operand(c, TF_TARGET_RS, TF_COMPARE_LE, 0.02, TF_TRIAL_WORST);
    expect_eq(c.plan(), TF_OK, "plans");

    std::vector<double>  depth = { 30.0, 120.0 };
    std::vector<int32_t> cand  = { 0, 1 };
    tf_needle_request req{};
    req.n_depths = 2; req.depth = depth.data();
    req.n_candidates = 2; req.candidate_material = cand.data();
    std::vector<double> map(4, 0.0);
    c.nout.dsumsq_dinsertion = map.data();
    expect_eq(tf_evaluate_needle(&c.problem, &c.operands, &c.pbuf, &req, &c.ws, &c.nout),
              TF_NOT_IMPLEMENTED_TRIAL, "the needle map refuses");

    std::vector<double> dn((size_t)NM * c.tcode.size(), 0.0), dk(dn.size(), 0.0);
    tf_index_output io{};
    io.dvalue_dn = dn.data(); io.dvalue_dk = dk.data();
    io.value = c.opvalue.data(); io.status = &c.eval_status;
    expect_eq(tf_evaluate_index_derivative(&c.problem, &c.operands, &c.pbuf, &c.ws, &io),
              TF_NOT_IMPLEMENTED_TRIAL, "and so does the index derivative");

    /* The same call on the same table with NOMINAL reductions is fine, so the refusal is about
     * the axis and not about anything else in the fixture. */
    Caller d;
    d.n_trials = 4; d.trial_thickness = trial_stacks();
    add_operand(d, TF_TARGET_RS, TF_COMPARE_LE, 0.02, TF_TRIAL_NOMINAL);
    expect_eq(d.plan(), TF_OK, "plans");
    d.nout.dsumsq_dinsertion = map.data();
    expect_eq(tf_evaluate_needle(&d.problem, &d.operands, &d.pbuf, &req, &d.ws, &d.nout),
              TF_OK, "while the same trials, unread, leave the needle map alone");
}

static void TheCallerIsToldWhenTheAxisIsIncoherent()
{
    std::printf("an axis asked for and not supplied is refused, not quietly ignored\n");

    Caller a;
    add_operand(a, TF_TARGET_RS, TF_COMPARE_EQUAL, 0.0, TF_TRIAL_WORST);   /* no trials at all */
    expect_eq(a.plan(), TF_NO_TRIAL_DATA, "a WORST over zero trials is refused");

    Caller b;
    b.n_trials = 4;                                     /* trials declared, nothing perturbed */
    add_operand(b, TF_TARGET_RS, TF_COMPARE_EQUAL, 0.0, TF_TRIAL_MEAN);
    expect_eq(b.plan(), TF_NO_TRIAL_DATA, "and so are trials that perturb nothing");

    Caller c;
    c.n_trials = 4; c.trial_thickness = trial_stacks();
    add_operand(c, TF_TARGET_RS, TF_COMPARE_EQUAL, 0.0, 11);
    expect_eq(c.plan(), TF_UNKNOWN_TRIAL_REDUCTION, "an unknown reduction is refused by name");
}

int main()
{
    setvbuf(stdout, NULL, _IONBF, 0);
    std::printf("\n=== the Monte Carlo trial axis ===\n\n");

    AnUntouchedTableDoesNotNoticeTheAxis();           std::printf("\n");
    MeanIsTheMeanOfSeparatelyEvaluatedStacks();       std::printf("\n");
    WorstIsTheLargestResidualAndNotTheLargestValue(); std::printf("\n");
    WorstDifferentiatesTheWinningTrialAndNothingElse(); std::printf("\n");
    MeanDifferentiatesEveryTrialEvenly();             std::printf("\n");
    AYieldCountsTrialsAndRefusesToBeDifferentiated(); std::printf("\n");
    TheMarginalIsNotTheJoint();                       std::printf("\n");
    NoBufferGrowsWithTheTrialCount();                 std::printf("\n");
    TrialsMayPerturbTheIndexToo();                    std::printf("\n");
    ThePlanChoosesAPathEveryTrialCanUse();            std::printf("\n");
    TheOtherEntryPointsRefuseRatherThanGuess();       std::printf("\n");
    TheCallerIsToldWhenTheAxisIsIncoherent();         std::printf("\n");

    std::printf("=== %d checks, %d failures ===\n\n", g_checks, g_failures);
    return g_failures != 0 ? 1 : 0;
}
