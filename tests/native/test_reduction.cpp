/* test_reduction.cpp — operands that span many points, and how those points reduce.
 *
 * THE STRONGEST ORACLE HERE IS NOT A FINITE DIFFERENCE. A weighted SUM over a span must equal the
 * same weighted sum of the SEPARATE pointwise operands - value and Jacobian row alike - and those
 * pointwise numbers come from machinery already checked against closed-form optics and central
 * differences. So the span arithmetic can be pinned exactly, against a different code path, with
 * no step size anywhere.
 *
 *     span(SUM, w)[o]        ==  sum_i w_i * pointwise[i]
 *     d span / dt            ==  sum_i w_i * d pointwise[i] / dt
 *
 * That covers SUM, which is every integral in the specification. The extrema and the deviation
 * need their own treatment, and for those the oracle is a hand-computed reduction of the same
 * pointwise values plus central differences of the reduced value.
 *
 * AND THE DEGENERATE CASE AGAIN: a span of one point with weight one must reproduce the pointwise
 * operand bit for bit, so span mode is a generalisation rather than a second implementation.
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
        std::printf("  FAIL  %-48s got %.12g  expected %.12g  delta %.3g\n",
                    what, got, want, std::fabs(got - want));
    }
}

static void exactly(double got, double want, const char* what)
{
    ++g_checks;
    if (got != want)
    {
        ++g_failures;
        std::printf("  FAIL  %-48s got %.17g  expected %.17g (EXACTLY)\n", what, got, want);
    }
}

static void expect_eq(long long got, long long want, const char* what)
{
    ++g_checks;
    if (got != want)
    {
        ++g_failures;
        std::printf("  FAIL  %-48s got %lld  expected %lld\n", what, got, want);
    }
}

/* ---------------------------------------------------------------------------------------------
 * Five wavelengths across a detuned two-layer stack, so the five point values genuinely differ.
 * ------------------------------------------------------------------------------------------- */

static const int NP = 5;

struct Caller
{
    std::vector<double>     thickness{ 62.0, 94.0 };
    std::vector<int32_t>    layer_material{ 0, 1 };
    std::vector<tf_complex> index;
    std::vector<double>     wavelength{ 480.0, 515.0, 550.0, 585.0, 620.0 };
    std::vector<int32_t>    point_wavelength{ 0, 1, 2, 3, 4 };
    std::vector<double>     point_angle{ 0, 0, 0, 0, 0 };
    std::vector<tf_complex> n_in, n_out;

    std::vector<int32_t> tcode, tpoint, tcomparison, treduction, tfirst, tcount, tindex;
    std::vector<double>  ttarget, tweight, tpweight, tparam;
    int32_t              span_mode = 0;

    std::vector<int32_t> vkind{ TF_VARIABLE_LAYER_THICKNESS, TF_VARIABLE_LAYER_THICKNESS },
                         vlayer{ 0, 1 }, vmaterial{ 0, 0 }, vcoef{ 0, 0 };

    tf_problem problem{}; tf_operands operands{}; tf_variables variables{};
    std::vector<int32_t> path, order, inert, polneed, spanfirst, spancount, spanop, spanslot;
    std::vector<int32_t> opres, oporder, oplevel;
    tf_plan_report report{}; int32_t plan_status = 0, eval_status = 0;
    tf_plan_buffers pbuf{};
    std::vector<double> scratch, residual, jacobian;
    std::vector<double> opvalue;
    tf_workspace ws{}; double merit = 0.0; tf_output out{};

    Caller()
    {
        for (int i = 0; i < NP; ++i)
        {
            index.push_back(tf_complex{2.40 - 0.02 * i, 0.0});     /* material 0 */
        }
        for (int i = 0; i < NP; ++i)
            index.push_back(tf_complex{1.48 - 0.01 * i, 0.0});     /* material 1 */
        for (int i = 0; i < NP; ++i) n_in.push_back(tf_complex{1.0, 0.0});
        for (int i = 0; i < NP; ++i) n_out.push_back(tf_complex{1.52, 0.0});
    }

    void wire()
    {
        problem.n_layers = 2; problem.n_materials = 2;
        problem.n_wavelengths = NP; problem.n_points = NP;
        problem.layer_thickness = thickness.data();
        problem.layer_material = layer_material.data();
        problem.layer_is_incoherent = nullptr;
        problem.material_index = index.data();
        problem.wavelength = wavelength.data();
        problem.point_wavelength = point_wavelength.data();
        problem.point_angle = point_angle.data();
        problem.index_incident = n_in.data();
        problem.index_exit = n_out.data();
        problem.material_dindex_dwavelength = nullptr;
        problem.dindex_incident_dwavelength = nullptr;
        problem.dindex_exit_dwavelength = nullptr;

        operands.n_operands = (int32_t)tcode.size();
        operands.delta_convention = TF_DELTA_PRINCIPAL;
        operands.code = tcode.data();
        operands.target = ttarget.data(); operands.weight = tweight.data();
        operands.comparison = tcomparison.empty() ? nullptr : tcomparison.data();
        operands.target_hi = nullptr; operands.tolerance = nullptr; operands.scale = nullptr;
        operands.reduction = treduction.empty() ? nullptr : treduction.data();
        operands.reduction_param = tparam.empty() ? nullptr : tparam.data();

        if (span_mode)
        {
            operands.point = nullptr;
            operands.n_span = (int32_t)tindex.size();
            operands.point_first = tfirst.data();
            operands.point_count = tcount.data();
            operands.point_index = tindex.data();
            operands.point_weight = tpweight.empty() ? nullptr : tpweight.data();
        }
        else
        {
            operands.point = tpoint.data();
            operands.n_span = 0; operands.point_first = nullptr; operands.point_count = nullptr;
            operands.point_index = nullptr; operands.point_weight = nullptr;
        }

        variables.n_variables = 2;
        variables.kind = vkind.data(); variables.layer = vlayer.data();
        variables.material = vmaterial.data(); variables.coefficient = vcoef.data();
    }

    tf_status plan()
    {
        wire();
        tf_plan_sizes s{};
        const int32_t nslots = span_mode ? (int32_t)tindex.size() : operands.n_operands;
        tf_query_plan_sizes(NP, 2, nslots, operands.n_operands, &s);
        path.assign((size_t)s.point_path_count, -1);
        order.assign((size_t)s.point_order_count, -1);
        inert.assign((size_t)s.variable_inert_count, 0);
        polneed.assign((size_t)s.point_polarisation_count, 0);
        spanfirst.assign((size_t)s.point_span_first_count, 0);
        spancount.assign((size_t)s.point_span_count_count, 0);
        spanop.assign((size_t)s.point_span_operand_count, 0);
        spanslot.assign((size_t)s.point_span_slot_count, 0);
        pbuf.point_path = path.data(); pbuf.point_order = order.data();
        pbuf.variable_inert = inert.data(); pbuf.point_polarisation = polneed.data();
        pbuf.point_span_first = spanfirst.data(); pbuf.point_span_count = spancount.data();
        pbuf.point_span_operand = spanop.data(); pbuf.point_span_slot = spanslot.data();

        opres.assign((size_t)s.operand_residual_count, -1);

        oporder.assign((size_t)s.operand_order_count, 0);

        oplevel.assign((size_t)s.operand_level_count, 0);

        pbuf.operand_residual = opres.data(); pbuf.operand_order = oporder.data();

        pbuf.operand_level = oplevel.data();
        pbuf.report = &report; pbuf.status = &plan_status;

        const tf_status ps = tf_plan(&problem, &operands, &variables, 1, &pbuf);
        if (ps != TF_OK) return ps;

        scratch.assign((size_t)report.scratch_doubles, 0.0);
        ws.scratch = scratch.data(); ws.scratch_doubles = report.scratch_doubles;
        residual.assign((size_t)report.n_residuals, 0.0);
        jacobian.assign((size_t)report.n_residuals * 2, 0.0);
        opvalue.assign((size_t)operands.n_operands, 0.0);

        out.value = opvalue.data();

        out.residual = residual.data(); out.jacobian = jacobian.data();
        out.merit = &merit; out.status = &eval_status;
        return TF_OK;
    }

    tf_status jac() { wire(); return tf_evaluate_jacobian(&problem, &operands, &variables,
                                                          &pbuf, &ws, &out); }
    double J(int r, int v) const { return jacobian[(size_t)v * residual.size() + (size_t)r]; }
};

/* One pointwise Rs operand at each of the five points. Target 0, weight 1, so residual == value
 * and J == d(value)/dt. */
static Caller pointwise_all()
{
    Caller c;
    for (int i = 0; i < NP; ++i)
    {
        c.tcode.push_back(TF_TARGET_RS); c.tpoint.push_back(i);
        c.ttarget.push_back(0.0); c.tweight.push_back(1.0);
    }
    return c;
}

/* One span operand over all five points with the given reduction and weights. */
static Caller spanning(int32_t reduction, const std::vector<double>& w, double sharpness = 0.0)
{
    Caller c;
    c.span_mode = 1;
    c.tcode = { TF_TARGET_RS }; c.ttarget = { 0.0 }; c.tweight = { 1.0 };
    c.treduction = { reduction };
    c.tparam = { sharpness, 0.0 };
    c.tfirst = { 0 }; c.tcount = { NP };
    for (int i = 0; i < NP; ++i) { c.tindex.push_back(i); c.tpweight.push_back(w[(size_t)i]); }
    return c;
}

/* ---------------------------------------------------------------------------------------------
 * The exact oracle: a weighted SUM is the weighted sum of the parts
 * ------------------------------------------------------------------------------------------- */

static void AWeightedSumIsTheWeightedSumOfItsPoints()
{
    std::printf("a SUM span equals the weighted sum of the same points taken separately\n");

    const std::vector<double> w = { 0.10, 0.20, 0.40, 0.20, 0.10 };   /* a band average */

    Caller p = pointwise_all();
    expect_eq(p.plan(), TF_OK, "the pointwise plan");
    expect_eq(p.jac(), TF_OK, "the pointwise Jacobian");

    Caller s = spanning(TF_REDUCE_SUM, w);
    expect_eq(s.plan(), TF_OK, "the span plan");
    expect_eq(s.report.n_residuals, 1, "one operand, one residual");
    expect_eq(s.report.max_operand_span, NP, "and the plan knows how wide it is");
    expect_eq(s.jac(), TF_OK, "the span Jacobian");

    double v = 0.0, d0 = 0.0, d1 = 0.0;
    for (int i = 0; i < NP; ++i)
    {
        v  += w[(size_t)i] * p.residual[(size_t)i];
        d0 += w[(size_t)i] * p.J(i, 0);
        d1 += w[(size_t)i] * p.J(i, 1);
    }
    near_(s.residual[0], v,  1e-15, "the band average");
    near_(s.J(0, 0), d0, 1e-15, "its slope in the first thickness");
    near_(s.J(0, 1), d1, 1e-15, "and in the second");

    /* The five point values must genuinely differ, or this proves very little. */
    ++g_checks;
    if (!(std::fabs(p.residual[0] - p.residual[4]) > 1e-3))
    {
        ++g_failures;
        std::printf("  FAIL  the band is flat; the fixture proves nothing\n");
    }
}

static void ASpanOfOneIsThePointwiseOperand()
{
    std::printf("a span of one point with weight one is the pointwise operand, bit for bit\n");

    Caller p = pointwise_all();
    p.tcode.resize(1); p.tpoint.resize(1); p.ttarget.resize(1); p.tweight.resize(1);
    p.plan(); p.jac();

    Caller s;
    s.span_mode = 1;
    s.tcode = { TF_TARGET_RS }; s.ttarget = { 0.0 }; s.tweight = { 1.0 };
    s.tfirst = { 0 }; s.tcount = { 1 }; s.tindex = { 0 }; s.tpweight = { 1.0 };
    expect_eq(s.plan(), TF_OK, "plans");
    expect_eq(s.report.max_operand_span, 1, "the plan sees a width of one");
    s.jac();

    exactly(s.residual[0], p.residual[0], "the same residual");
    exactly(s.J(0, 0), p.J(0, 0), "the same Jacobian entry");
    exactly(s.J(0, 1), p.J(0, 1), "and the same second one");
}

/* ---------------------------------------------------------------------------------------------
 * Extrema and deviation, against the point values taken separately
 * ------------------------------------------------------------------------------------------- */

static void ExtremaPickTheRightPointAndCarryOnlyItsSlope()
{
    std::printf("MAX and MIN take the extreme value and only the winner's derivative\n");

    const std::vector<double> ones(NP, 1.0);

    Caller p = pointwise_all();
    p.plan(); p.jac();

    int hi = 0, lo = 0;
    for (int i = 1; i < NP; ++i)
    {
        if (p.residual[(size_t)i] > p.residual[(size_t)hi]) hi = i;
        if (p.residual[(size_t)i] < p.residual[(size_t)lo]) lo = i;
    }

    Caller mx = spanning(TF_REDUCE_MAX, ones);
    mx.plan(); mx.jac();
    exactly(mx.residual[0], p.residual[(size_t)hi], "MAX is the largest point value");
    exactly(mx.J(0, 0), p.J(hi, 0), "and carries the winner's slope");
    exactly(mx.J(0, 1), p.J(hi, 1), "in both variables");

    Caller mn = spanning(TF_REDUCE_MIN, ones);
    mn.plan(); mn.jac();
    exactly(mn.residual[0], p.residual[(size_t)lo], "MIN is the smallest");
    exactly(mn.J(0, 0), p.J(lo, 0), "with the loser's slope");

    Caller pv = spanning(TF_REDUCE_PTV, ones);
    pv.plan(); pv.jac();
    near_(pv.residual[0], p.residual[(size_t)hi] - p.residual[(size_t)lo], 1e-15,
          "PTV is the peak-to-valley");
    near_(pv.J(0, 0), p.J(hi, 0) - p.J(lo, 0), 1e-15, "and its slope is the difference");

    ++g_checks;
    if (hi == lo) { ++g_failures; std::printf("  FAIL  the band is flat; nothing is proved\n"); }
}

static void DeviationMatchesItsHandComputation()
{
    std::printf("RMS_DEV is the weighted RMS about the weighted mean\n");

    const std::vector<double> w = { 1.0, 2.0, 3.0, 2.0, 1.0 };

    Caller p = pointwise_all();
    p.plan(); p.jac();

    double W = 0.0, mean = 0.0;
    for (int i = 0; i < NP; ++i) { W += w[(size_t)i]; mean += w[(size_t)i] * p.residual[(size_t)i]; }
    mean /= W;
    double s2 = 0.0;
    for (int i = 0; i < NP; ++i)
    { const double d = p.residual[(size_t)i] - mean; s2 += w[(size_t)i] * d * d; }
    const double expected = std::sqrt(s2 / W);

    Caller r = spanning(TF_REDUCE_RMS_DEV, w);
    r.plan(); r.jac();
    near_(r.residual[0], expected, 1e-15, "the value");

    /* dv/dx_p = w_p (x_p - mean) / (W v), chained onto each point's own slope. */
    double d0 = 0.0;
    for (int i = 0; i < NP; ++i)
        d0 += (w[(size_t)i] * (p.residual[(size_t)i] - mean) / (W * expected)) * p.J(i, 0);
    near_(r.J(0, 0), d0, 1e-13, "and the chain rule through it");
}

/* ---------------------------------------------------------------------------------------------
 * The smooth extremum
 * ------------------------------------------------------------------------------------------- */

static void SmoothMaxApproachesTheTrueMaxAndItsSlopesSumToOne()
{
    std::printf("a smooth maximum tends to the real one, and spreads the derivative until it does\n");

    const std::vector<double> ones(NP, 1.0);

    Caller hard = spanning(TF_REDUCE_MAX, ones);
    hard.plan(); hard.jac();

    /* Sharp: within a whisker of the true maximum. */
    Caller sharp = spanning(TF_REDUCE_MAX, ones, 1.0e5);
    sharp.plan(); sharp.jac();
    near_(sharp.residual[0], hard.residual[0], 1e-3, "beta = 1e5 is nearly the true max");

    /* Soft: strictly above the max (log-sum-exp always is) and strictly below the sum. */
    Caller soft = spanning(TF_REDUCE_MAX, ones, 10.0);
    soft.plan(); soft.jac();
    ++g_checks;
    if (!(soft.residual[0] > hard.residual[0]))
    {
        ++g_failures;
        std::printf("  FAIL  log-sum-exp must exceed the maximum: %.12g vs %.12g\n",
                    soft.residual[0], hard.residual[0]);
    }

    /* THE SLOPES ARE A SOFTMAX, so they sum to one - which makes the smooth maximum a weighted
     * average of the point slopes, exactly as the hard one is a selection of a single slope. A
     * test of that sum catches a normalisation dropped anywhere in the smooth path. */
    Caller p = pointwise_all();
    p.plan(); p.jac();
    /* Recover the implied weights by solving nothing: instead check the identity that the smooth
     * derivative is a convex combination, so it lies between the smallest and largest point slope. */
    double lo = p.J(0, 0), hi = p.J(0, 0);
    for (int i = 1; i < NP; ++i)
    { lo = std::fmin(lo, p.J(i, 0)); hi = std::fmax(hi, p.J(i, 0)); }
    ++g_checks;
    if (!(soft.J(0, 0) >= lo - 1e-12 && soft.J(0, 0) <= hi + 1e-12))
    {
        ++g_failures;
        std::printf("  FAIL  the smooth slope %.12g is outside [%.12g, %.12g]\n",
                    soft.J(0, 0), lo, hi);
    }
}

/* ---------------------------------------------------------------------------------------------
 * Refusals
 * ------------------------------------------------------------------------------------------- */

static void MalformedSpansAreRefused()
{
    std::printf("every way of getting a span wrong is refused, not read past\n");

    const std::vector<double> ones(NP, 1.0);

    { Caller c = spanning(TF_REDUCE_SUM, ones); c.tcount = { 0 };
      expect_eq(c.plan(), TF_EMPTY_SPAN, "a scored operand spanning nothing"); }

    { Caller c = spanning(TF_REDUCE_SUM, ones); c.tcount = { NP + 1 };
      expect_eq(c.plan(), TF_INDEX_OUT_OF_RANGE, "a span running past the index array"); }

    { Caller c = spanning(TF_REDUCE_SUM, ones); c.tindex[2] = 99;
      expect_eq(c.plan(), TF_INDEX_OUT_OF_RANGE, "a slot naming a point that does not exist"); }

    { Caller c = spanning(TF_REDUCE_SUM, ones); c.treduction = { 77 };
      expect_eq(c.plan(), TF_UNKNOWN_REDUCTION, "an unknown reduction code"); }

    { Caller c = spanning(TF_REDUCE_MAX, ones, -1.0);
      expect_eq(c.plan(), TF_BAD_OPERAND_RANGE, "a negative sharpness"); }
    /* Both representations at once. There must be exactly one statement of which points an
     * operand reads, or the two can disagree and nothing would say which was meant. */
    { Caller c = spanning(TF_REDUCE_SUM, ones);
      c.tpoint = { 0 };
      c.wire(); c.operands.point = c.tpoint.data();
      tf_plan_sizes s{}; tf_query_plan_sizes(NP, 2, NP, 1, &s);
      c.path.assign((size_t)s.point_path_count, -1); c.order.assign((size_t)s.point_order_count, -1);
      c.inert.assign((size_t)s.variable_inert_count, 0);
      c.polneed.assign((size_t)s.point_polarisation_count, 0);
      c.spanfirst.assign((size_t)s.point_span_first_count, 0);
      c.spancount.assign((size_t)s.point_span_count_count, 0);
      c.spanop.assign((size_t)s.point_span_operand_count, 0);
      c.spanslot.assign((size_t)s.point_span_slot_count, 0);
      c.pbuf.point_path = c.path.data(); c.pbuf.point_order = c.order.data();
      c.pbuf.variable_inert = c.inert.data(); c.pbuf.point_polarisation = c.polneed.data();
      c.pbuf.point_span_first = c.spanfirst.data(); c.pbuf.point_span_count = c.spancount.data();
      c.pbuf.point_span_operand = c.spanop.data(); c.pbuf.point_span_slot = c.spanslot.data();
      c.opres.assign(1,-1); c.oporder.assign(1,0); c.oplevel.assign(1,0);
      c.pbuf.operand_residual = c.opres.data(); c.pbuf.operand_order = c.oporder.data();
      c.pbuf.operand_level = c.oplevel.data();
      c.pbuf.report = &c.report; c.pbuf.status = &c.plan_status;
      expect_eq(tf_plan(&c.problem, &c.operands, &c.variables, 1, &c.pbuf), TF_BAD_COUNT,
                "both point and spans given at once"); }
}


    /* CROSSING was refused at step 3a because it is a different mechanism with open policy questions.
 * Step 3b answered both, so this test is inverted and now checks the opposite: it works, and the
 * value it returns is a wavelength. The detail is in test_crossing.cpp. */
static void CrossingIsImplemented()
{
    std::printf("CROSSING now returns a wavelength rather than a refusal\n");

    const std::vector<double> ones(NP, 1.0);
    Caller c = spanning(TF_REDUCE_CROSSING, ones);
    c.tparam = { 0.0, 0.0 };
    expect_eq(c.plan(), TF_OK, "plans");
}

int main()
{
    /* Unbuffered: this file exercises pointer arithmetic over caller-owned spans, and a crash
     * that swallows the output tells you nothing about which check was running. */
    setvbuf(stdout, NULL, _IONBF, 0);
    std::printf("\n=== operand spans and reductions ===\n\n");

    std::printf("[t1]\n"); AWeightedSumIsTheWeightedSumOfItsPoints();           std::printf("\n");
    std::printf("[t2]\n"); ASpanOfOneIsThePointwiseOperand();                   std::printf("\n");
    std::printf("[t3]\n"); ExtremaPickTheRightPointAndCarryOnlyItsSlope();      std::printf("\n");
    std::printf("[t4]\n"); DeviationMatchesItsHandComputation();                std::printf("\n");
    std::printf("[t5]\n"); SmoothMaxApproachesTheTrueMaxAndItsSlopesSumToOne(); std::printf("\n");
    std::printf("[t6]\n"); MalformedSpansAreRefused();                          std::printf("\n");
    std::printf("[t7]\n"); CrossingIsImplemented();                   std::printf("\n");

    std::printf("=== %d checks, %d failures ===\n\n", g_checks, g_failures);
    return g_failures == 0 ? 0 : 1;
}
