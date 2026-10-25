/* test_comparison.cpp — one-sided targets, deadbands and normalisation.
 *
 * Step 2 of docs/evaluation-model-contract.md. Almost no coating specification is an equality -
 * they read "R below 0.5% across the band" - so this is the step that makes real specifications
 * expressible at all.
 *
 * THREE THINGS ARE WORTH TESTING AND ONLY ONE OF THEM IS THE VALUE.
 *
 *   The residual, which is easy and mostly arithmetic.
 *
 *   The DERIVATIVE, which is where a one-sided comparison earns its keep and where it can go
 *   quietly wrong. A satisfied constraint must contribute EXACTLY ZERO to the Jacobian, not a
 *   small number; a GE constraint's slope is NEGATIVE; and the two must be consistent with the
 *   residual, because a zero residual paired with a non-zero slope is a gradient pointing away
 *   from a satisfied requirement and nothing downstream could detect it.
 *
 *   The BOUNDARY, where value == target exactly. The derivative is undefined there in the
 *   mathematical sense and the engine has to pick; it picks zero, the satisfied side. That is a
 *   decision rather than a consequence, so it is pinned here rather than left to be discovered.
 *
 * And the degenerate case: with every new array left NULL the numbers must be what they were
 * before comparisons existed. That is contract §9.4, and it is checked here directly rather than
 * inferred from the other tests still passing.
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
}

static int g_failures = 0;
static int g_checks   = 0;

static void near_(double got, double want, double tol, const char* what)
{
    ++g_checks;
    if (!(std::fabs(got - want) <= tol))
    {
        ++g_failures;
        std::printf("  FAIL  %-50s got %.12g  expected %.12g\n", what, got, want);
    }
}

static void exactly(double got, double want, const char* what)
{
    ++g_checks;
    if (got != want)
    {
        ++g_failures;
        std::printf("  FAIL  %-50s got %.17g  expected %.17g (EXACTLY)\n", what, got, want);
    }
}

static void expect_eq(long long got, long long want, const char* what)
{
    ++g_checks;
    if (got != want)
    {
        ++g_failures;
        std::printf("  FAIL  %-50s got %lld  expected %lld\n", what, got, want);
    }
}

/* ---------------------------------------------------------------------------------------------
 * A single quarter-wave layer, whose Rs is a number we can aim a comparison at.
 * ------------------------------------------------------------------------------------------- */

struct Caller
{
    /* DELIBERATELY DETUNED, to 83% of a quarter wave. At an EXACT quarter wave the reflectance
     * is stationary in thickness, so dR/dt is zero - and every slope comparison below would then
     * be zero equals zero and would pass with the sign machinery entirely wrong. The closed-form
     * check has its own exact-quarter-wave fixture instead. */
    std::vector<double>     thickness{ 550.0 / (4.0 * 2.35) * 0.83 };
    std::vector<int32_t>    layer_material{ 0 };
    std::vector<tf_complex> index{ tf_complex{2.35, 0.0} };
    std::vector<double>     wavelength{ 550.0 };
    std::vector<int32_t>    point_wavelength{ 0 };
    std::vector<double>     point_angle{ 0.0 };
    std::vector<tf_complex> n_in{ tf_complex{1.0, 0.0} };
    std::vector<tf_complex> n_out{ tf_complex{1.52, 0.0} };
    std::vector<int32_t>    tcode{ TF_TARGET_RS }, tpoint{ 0 };
    std::vector<double>     ttarget{ 0.0 }, tweight{ 1.0 };
    std::vector<int32_t>    vkind{ TF_VARIABLE_LAYER_THICKNESS }, vlayer{ 0 },
                            vmaterial{ 0 }, vcoef{ 0 };

    /* left empty means NULL means the documented default */
    std::vector<int32_t> tcomparison;
    std::vector<double>  thi, ttol, tscale;

    tf_problem problem{}; tf_operands operands{}; tf_variables variables{};
    std::vector<int32_t> path, order, inert, polneed;
    std::vector<int32_t> opres, oporder, oplevel;
    std::vector<int32_t> spanfirst, spancount, spanop, spanslot;
    tf_plan_report report{}; int32_t plan_status = 0, eval_status = 0;
    tf_plan_buffers pbuf{};
    std::vector<double> scratch, residual, jacobian;
    std::vector<double> opvalue;
    tf_workspace ws{}; double merit = 0.0; tf_output out{};

    void wire()
    {
        problem.n_layers = (int32_t)thickness.size();
        problem.n_materials = 1;
        problem.n_wavelengths = 1;
        problem.n_points = 1;
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
        operands.code = tcode.data();   operands.point = tpoint.data();
        operands.target = ttarget.data(); operands.weight = tweight.data();
        operands.comparison = tcomparison.empty() ? nullptr : tcomparison.data();
        operands.target_hi  = thi.empty()         ? nullptr : thi.data();
        operands.tolerance  = ttol.empty()        ? nullptr : ttol.data();
        operands.scale      = tscale.empty()      ? nullptr : tscale.data();

        variables.n_variables = (int32_t)vkind.size();
        variables.kind = vkind.data(); variables.layer = vlayer.data();
        variables.material = vmaterial.data(); variables.coefficient = vcoef.data();
    }

    tf_status plan()
    {
        wire();
        tf_plan_sizes s{};
        tf_query_plan_sizes(1, variables.n_variables, operands.n_operands, operands.n_operands, &s);
        path.assign((size_t)s.point_path_count, -1);
        order.assign((size_t)s.point_order_count, -1);
        inert.assign((size_t)s.variable_inert_count, 0);
        polneed.assign((size_t)s.point_polarisation_count, 0);
        pbuf.point_path = path.data(); pbuf.point_order = order.data();
        pbuf.variable_inert = inert.data(); pbuf.point_polarisation = polneed.data();
 spanfirst.assign((size_t)s.point_span_first_count, 0);
 spancount.assign((size_t)s.point_span_count_count, 0);
 spanop.assign((size_t)s.point_span_operand_count, 0);
 spanslot.assign((size_t)s.point_span_slot_count, 0);
 pbuf.point_span_first = spanfirst.data();  pbuf.point_span_count = spancount.data();
 pbuf.point_span_operand = spanop.data();   pbuf.point_span_slot = spanslot.data();

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
        opvalue.assign((size_t)tcode.size(), 0.0);
        residual.assign((size_t)report.n_residuals, 0.0);
        jacobian.assign((size_t)report.n_residuals * vkind.size(), 0.0);
        opvalue.assign((size_t)operands.n_operands, 0.0);

        out.value = opvalue.data();

        out.residual = residual.data(); out.jacobian = jacobian.data();
        out.value = opvalue.data();
        out.merit = &merit; out.status = &eval_status;
        return TF_OK;
    }

    tf_status evaluate() { wire(); return tf_evaluate(&problem, &operands, &pbuf, &ws, &out); }
    tf_status jac()      { wire(); return tf_evaluate_jacobian(&problem, &operands, &variables,
                                                               &pbuf, &ws, &out); }
};

/* The unconstrained reflectance of the fixture, and its thickness slope, from an equality target
 * with target 0 and weight 1 - where residual == value and J == d(value)/dt. */
static void reference(double* R, double* dRdt)
{
    Caller c;
    c.plan(); c.jac();
    *R = c.residual[0];
    *dRdt = c.jacobian[0];
}

/* ---------------------------------------------------------------------------------------------
 * §9.4 — the degenerate case IS the old model
 * ------------------------------------------------------------------------------------------- */

static void EveryNewFieldNullReproducesTheOldNumbers()
{
    std::printf("with comparison, tolerance and scale all NULL, nothing has changed\n");

    double R = 0.0, dRdt = 0.0;
    reference(&R, &dRdt);

    /* An exact quarter wave, where the answer can be written down - and where dR/dt must
     * vanish, because a quarter wave is an extremum. Both are checked: the value proves the
     * evaluator, the stationary slope proves the Jacobian, and together they are the reason the
     * main fixture above is detuned instead. */
    {
        Caller q;
        q.thickness = { 550.0 / (4.0 * 2.35) };
        q.plan(); q.jac();
        const double n1sq = 2.35 * 2.35;
        const double expected = std::pow((1.0 * 1.52 - n1sq) / (1.0 * 1.52 + n1sq), 2.0);
        near_(q.residual[0], expected, 1e-12, "the quarter-wave reflectance is the textbook one");
        near_(q.jacobian[0], 0.0, 1e-12, "and a quarter wave is stationary in thickness");
    }

    /* The detuned fixture, by contrast, must have a slope worth comparing against. */
    ++g_checks;
    if (!(std::fabs(dRdt) > 1e-5))
    {
        ++g_failures;
        std::printf("  FAIL  the detuned fixture has no slope: %.3g\n", dRdt);
    }

    /* And setting the three explicitly to their defaults must give the SAME BITS. */
    Caller e;
    e.tcomparison = { TF_COMPARE_EQUAL };
    e.ttol = { 0.0 };
    e.tscale = { 1.0 };
    e.plan(); e.jac();
    exactly(e.residual[0], R,    "explicit EQUAL/0/1 gives the identical residual");
    exactly(e.jacobian[0], dRdt, "and the identical Jacobian entry");
}

/* ---------------------------------------------------------------------------------------------
 * One-sided comparisons
 * ------------------------------------------------------------------------------------------- */

static void ASatisfiedLimitScoresNothingAndPushesNothing()
{
    std::printf("a met constraint contributes exactly zero to both residual and Jacobian\n");

    double R = 0.0, dRdt = 0.0;
    reference(&R, &dRdt);

    /* R <= something comfortably above it. */
    Caller c;
    c.tcomparison = { TF_COMPARE_LE };
    c.ttarget = { R + 0.05 };
    expect_eq(c.plan(), TF_OK, "plans");
    expect_eq(c.jac(), TF_OK, "evaluates");

    exactly(c.residual[0], 0.0, "the residual of a met limit");
    exactly(c.jacobian[0], 0.0, "and its Jacobian entry - zero, not small");
    exactly(c.merit, 0.0, "so the merit is zero");

    /* The same limit violated: residual and slope both come back, and the slope is the
     * unconstrained one because d(residual)/d(value) is +1 above the limit. */
    Caller v;
    v.tcomparison = { TF_COMPARE_LE };
    v.ttarget = { R - 0.05 };
    v.plan(); v.jac();
    near_(v.residual[0], 0.05, 1e-12, "a violated limit scores its excess");
    near_(v.jacobian[0], dRdt, 1e-15, "and carries the full slope");
}

static void AGreaterThanConstraintHasANegativeSlope()
{
    std::printf("GE reverses the sign of the slope - and getting that wrong optimises backwards\n");

    double R = 0.0, dRdt = 0.0;
    reference(&R, &dRdt);

    Caller c;
    c.tcomparison = { TF_COMPARE_GE };
    c.ttarget = { R + 0.05 };          /* asking for MORE reflectance than we have */
    expect_eq(c.plan(), TF_OK, "plans");
    expect_eq(c.jac(), TF_OK, "evaluates");

    near_(c.residual[0], 0.05, 1e-12, "the shortfall is the residual");
    near_(c.jacobian[0], -dRdt, 1e-15, "and the slope is the NEGATIVE of the value's");

    /* Met the other way round. */
    Caller m;
    m.tcomparison = { TF_COMPARE_GE };
    m.ttarget = { R - 0.05 };
    m.plan(); m.jac();
    exactly(m.residual[0], 0.0, "a met GE scores nothing");
    exactly(m.jacobian[0], 0.0, "and pushes nothing");
}

static void RangeIsSilentInsideAndSignedOutside()
{
    std::printf("a range scores nothing inside it and knows which side it left\n");

    double R = 0.0, dRdt = 0.0;
    reference(&R, &dRdt);

    Caller in;
    in.tcomparison = { TF_COMPARE_RANGE };
    in.ttarget = { R - 0.02 }; in.thi = { R + 0.02 };
    expect_eq(in.plan(), TF_OK, "plans");
    in.jac();
    exactly(in.residual[0], 0.0, "inside the range");
    exactly(in.jacobian[0], 0.0, "no force inside the range");

    Caller hi;
    hi.tcomparison = { TF_COMPARE_RANGE };
    hi.ttarget = { R - 0.10 }; hi.thi = { R - 0.03 };     /* we are above the top */
    hi.plan(); hi.jac();
    near_(hi.residual[0], 0.03, 1e-12, "above the top, by the excess");
    near_(hi.jacobian[0], dRdt, 1e-15, "with a positive slope");

    Caller lo;
    lo.tcomparison = { TF_COMPARE_RANGE };
    lo.ttarget = { R + 0.03 }; lo.thi = { R + 0.10 };     /* we are below the bottom */
    lo.plan(); lo.jac();
    near_(lo.residual[0], 0.03, 1e-12, "below the bottom, by the shortfall");
    near_(lo.jacobian[0], -dRdt, 1e-15, "with a negative slope");
}

/* THE BOUNDARY. value == target exactly, where the derivative is mathematically undefined and the
 * engine must choose. It chooses the satisfied side: zero. Pinned here because it is a decision. */
static void ExactlyOnTheLimitIsTreatedAsSatisfied()
{
    std::printf("sitting exactly on a limit counts as met: no residual, no force\n");

    double R = 0.0, dRdt = 0.0;
    reference(&R, &dRdt);
    (void)dRdt;

    Caller c;
    c.tcomparison = { TF_COMPARE_LE };
    c.ttarget = { R };                       /* bit-identical to the value */
    expect_eq(c.plan(), TF_OK, "plans");
    expect_eq(c.jac(), TF_OK, "evaluates");
    exactly(c.residual[0], 0.0, "LE exactly on its limit");
    exactly(c.jacobian[0], 0.0, "and no gradient there");

    Caller g;
    g.tcomparison = { TF_COMPARE_GE };
    g.ttarget = { R };
    g.plan(); g.jac();
    exactly(g.residual[0], 0.0, "GE exactly on its limit");
    exactly(g.jacobian[0], 0.0, "and no gradient there either");
}

/* ---------------------------------------------------------------------------------------------
 * Deadband and normalisation
 * ------------------------------------------------------------------------------------------- */

static void ToleranceShrinksTheErrorRatherThanClippingIt()
{
    std::printf("a deadband moves the error toward zero, it does not truncate it\n");

    double R = 0.0, dRdt = 0.0;
    reference(&R, &dRdt);

    /* An equality target 0.10 away, with a tolerance of 0.04: the error is 0.06, not 0.10. */
    Caller c;
    c.ttarget = { R - 0.10 };
    c.ttol = { 0.04 };
    expect_eq(c.plan(), TF_OK, "plans");
    c.jac();
    near_(c.residual[0], 0.06, 1e-12, "0.10 out with a 0.04 deadband is 0.06");
    near_(c.jacobian[0], dRdt, 1e-15, "the slope outside the deadband is untouched");

    /* Inside it, nothing at all. */
    Caller d;
    d.ttarget = { R - 0.02 };
    d.ttol = { 0.04 };
    d.plan(); d.jac();
    exactly(d.residual[0], 0.0, "inside the deadband");
    exactly(d.jacobian[0], 0.0, "and no force inside it");

    /* The sign survives: an equality target on the other side. */
    Caller u;
    u.ttarget = { R + 0.10 };
    u.ttol = { 0.04 };
    u.plan(); u.jac();
    near_(u.residual[0], -0.06, 1e-12, "and it keeps the sign of the error");
}

static void ScaleDividesTheErrorAndLeavesTheMeritDenominatorAlone()
{
    std::printf("scale normalises the error without touching what weight means\n");

    double R = 0.0, dRdt = 0.0;
    reference(&R, &dRdt);

    Caller c;
    c.ttarget = { 0.0 };
    c.tscale  = { 0.01 };            /* express R in units of 1% */
    expect_eq(c.plan(), TF_OK, "plans");
    c.jac();
    near_(c.residual[0], R / 0.01, 1e-9,  "the residual is the error over the scale");
    near_(c.jacobian[0], dRdt / 0.01, 1e-9, "and so is the Jacobian");

    /* THE POINT OF KEEPING scale OUT OF THE WEIGHT. Scaling every weight leaves the merit alone -
     * that is the measured LensHH-LT property - while scaling the units does change it, because a
     * merit expressed in percent is a different number from one expressed in fractions. If `scale`
     * had been folded into `weight` it would sit in the denominator too and cancel, which would
     * make a unit change invisible. */
    Caller w;
    w.tweight = { 9.0 };
    w.plan(); w.evaluate();
    Caller w1;
    w1.plan(); w1.evaluate();
    near_(w.merit, w1.merit, 1e-15, "nine times the weight leaves the merit alone");

    Caller s;
    s.tscale = { 0.01 };
    s.plan(); s.evaluate();
    near_(s.merit, w1.merit / 0.01, 1e-9, "but a hundredth of the scale multiplies it by a hundred");
}

/* ---------------------------------------------------------------------------------------------
 * The needle map, which the contract flagged as wanting a test
 * ------------------------------------------------------------------------------------------- */

/* §12: "a satisfied operand contributes zero to that sum and therefore zero to the needle map,
 * which is correct - a needle that improves an already-satisfied constraint should not be rewarded
 * for it. I believe that falls out with no code change, but it is the kind of thing that is obvious
 * in a document and wrong in practice, so it wants a test." It does fall out, twice over: the
 * residual is zero AND its slope is zero. Here is the test. */
static void ASatisfiedOperandEarnsNoNeedle()
{
    std::printf("a needle earns nothing for improving an already-met constraint\n");

    double R = 0.0, dRdt = 0.0;
    reference(&R, &dRdt);

    /* inside the 48.6 nm layer: a depth past the back of the stack is refused, correctly */
    std::vector<double>  depth = { 5.0, 20.0, 40.0 };
    std::vector<int32_t> cand  = { 0 };

    /* Violated: the map must be alive. */
    Caller v;
    v.tcomparison = { TF_COMPARE_LE };
    v.ttarget = { R - 0.05 };
    v.plan();
    std::vector<double> vres((size_t)v.report.n_residuals, 0.0), vmap(3, -1.0);
    int32_t vst = 0;
    tf_needle_request req{};
    req.n_depths = 3; req.depth = depth.data();
    req.n_candidates = 1; req.candidate_material = cand.data();
    tf_needle_output nout{};
    nout.residual = vres.data(); nout.dsumsq_dinsertion = vmap.data(); nout.status = &vst;
    v.wire();
    expect_eq(tf_evaluate_needle(&v.problem, &v.operands, &v.pbuf, &req, &v.ws, &nout),
              TF_OK, "the violated case evaluates");
    ++g_checks;
    if (vmap[0] == 0.0 && vmap[1] == 0.0 && vmap[2] == 0.0)
    { ++g_failures; std::printf("  FAIL  the violated map is all zero; the fixture proves nothing\n"); }

    /* Satisfied: exactly zero everywhere. */
    Caller s;
    s.tcomparison = { TF_COMPARE_LE };
    s.ttarget = { R + 0.05 };
    s.plan();
    std::vector<double> sres((size_t)s.report.n_residuals, 0.0), smap(3, -1.0);
    int32_t sst = 0;
    tf_needle_output snout{};
    snout.residual = sres.data(); snout.dsumsq_dinsertion = smap.data(); snout.status = &sst;
    s.wire();
    expect_eq(tf_evaluate_needle(&s.problem, &s.operands, &s.pbuf, &req, &s.ws, &snout),
              TF_OK, "the satisfied case evaluates");
    exactly(smap[0], 0.0, "no needle value at the first depth");
    exactly(smap[1], 0.0, "nor the second");
    exactly(smap[2], 0.0, "nor the third");
}

/* ---------------------------------------------------------------------------------------------
 * Refusals
 * ------------------------------------------------------------------------------------------- */

static void MalformedComparisonsAreRefused()
{
    std::printf("every way of getting the new fields wrong is refused, not evaluated\n");

    { Caller c; c.tcomparison = { 99 };
      expect_eq(c.plan(), TF_UNKNOWN_COMPARISON, "an unknown comparison code"); }

    { Caller c; c.tcomparison = { TF_COMPARE_RANGE }; c.thi = { -1.0 }; c.ttarget = { 1.0 };
      expect_eq(c.plan(), TF_BAD_OPERAND_RANGE, "a range whose top is below its bottom"); }

    { Caller c; c.tcomparison = { TF_COMPARE_RANGE };   /* target_hi left NULL */
      expect_eq(c.plan(), TF_NULL_POINTER, "a range with no upper bound at all"); }

    { Caller c; c.ttol = { -0.1 };
      expect_eq(c.plan(), TF_BAD_OPERAND_RANGE, "a negative tolerance"); }

    { Caller c; c.tscale = { 0.0 };
      expect_eq(c.plan(), TF_BAD_OPERAND_SCALE, "a scale of zero"); }

    { Caller c; c.tscale = { -1.0 };
      expect_eq(c.plan(), TF_BAD_OPERAND_SCALE, "a negative scale"); }
}

/* NONE was refused at step 2 because nothing could consume an unscored operand's value. Step 5
 * gave it both a consumer and a place to be read - tf_output.value - so this test was inverted and
 * is now the right way up: an unscored operand is evaluated and produces no residual.
 *
 * A table where EVERY operand is unscored is still an error, and a different one: there is nothing
 * to optimise. That is the operand-side twin of TF_NO_LIVE_VARIABLE. */
static void AnUnscoredOperandIsEvaluatedButNotScored()
{
    std::printf("comparison NONE produces a value and no residual\n");

    Caller c;
    c.tcode = { TF_TARGET_RS, TF_TARGET_RP };
    c.tpoint = { 0, 0 };
    c.ttarget = { 0.0, 0.0 };
    c.tweight = { 1.0, 1.0 };
    c.tcomparison = { TF_COMPARE_NONE, TF_COMPARE_EQUAL };
    expect_eq(c.plan(), TF_OK, "plans");
    expect_eq(c.report.n_residuals, 1, "one of the two is scored");
    expect_eq(c.report.n_operands_unscored, 1, "and one is not");
    expect_eq(c.opres[0], -1, "the unscored one has no row");
    expect_eq(c.opres[1], 0, "and the scored one is row zero");

    expect_eq(c.jac(), TF_OK, "evaluates");
    ++g_checks;
    if (!(c.opvalue[0] > 0.0))
    {
        ++g_failures;
        std::printf("  FAIL  the unscored operand still has to have a value, got %.12g\n",
                    c.opvalue[0]);
    }
}

static void ATableWithNothingScoredIsRefused()
{
    std::printf("every operand unscored is a caller mistake, not instant convergence\n");

    Caller c;
    c.tcomparison = { TF_COMPARE_NONE };
    expect_eq(c.plan(), TF_NO_SCORED_OPERAND, "refused");
}

int main()
{
    std::printf("\n=== comparisons, deadbands and normalisation ===\n\n");

    EveryNewFieldNullReproducesTheOldNumbers();      std::printf("\n");
    ASatisfiedLimitScoresNothingAndPushesNothing();  std::printf("\n");
    AGreaterThanConstraintHasANegativeSlope();       std::printf("\n");
    RangeIsSilentInsideAndSignedOutside();           std::printf("\n");
    ExactlyOnTheLimitIsTreatedAsSatisfied();         std::printf("\n");
    ToleranceShrinksTheErrorRatherThanClippingIt();  std::printf("\n");
    ScaleDividesTheErrorAndLeavesTheMeritDenominatorAlone(); std::printf("\n");
    ASatisfiedOperandEarnsNoNeedle();                std::printf("\n");
    MalformedComparisonsAreRefused();                std::printf("\n");
    AnUnscoredOperandIsEvaluatedButNotScored();      std::printf("\n");
    ATableWithNothingScoredIsRefused();              std::printf("\n");

    std::printf("=== %d checks, %d failures ===\n\n", g_checks, g_failures);
    return g_failures == 0 ? 0 : 1;
}
