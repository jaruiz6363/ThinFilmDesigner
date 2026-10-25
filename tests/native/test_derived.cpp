/* test_derived.cpp — operands that read operands.
 *
 * THE EXACT ORACLE. A derived operand's value and gradient are an arithmetic function of its
 * inputs', and those inputs are ordinary operands whose numbers are already checked against
 * closed-form optics and central differences. So:
 *
 *     RATIO(Rs, Rp)      value == Rs/Rp
 *     d(RATIO)/dt        == (dRs/dt)/Rp - Rs*(dRp/dt)/Rp^2
 *
 * both computable from a flat table's own output, in the test, with no step size anywhere. That is
 * a far stronger check than a finite difference and it exercises the whole chain: the topological
 * order, the working matrix, the local factors and the comparison slope applied once at the end.
 *
 * AND THE ONE THAT ONLY A DAG CAN SAY. Rs and Rp here are UNSCORED - they exist to feed the ratio
 * and produce no residual at all. A flat table cannot express that, and it is the reason
 * n_residuals stopped being n_operands.
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
tf_status tf_evaluate_wavelength_derivative(const tf_problem*, const tf_operands*,
                                            const tf_plan_buffers*, tf_workspace*,
                                            tf_spectral_output*);
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

static const double PI = 3.14159265358979323846;

/* ---------------------------------------------------------------------------------------------
 * Three layers at 55 degrees, where Rs and Rp differ enough for a ratio to mean something.
 * ------------------------------------------------------------------------------------------- */

struct Caller
{
    std::vector<double>     thickness{ 71.0, 113.0, 52.0 };
    std::vector<int32_t>    layer_material{ 0, 1, 0 };
    std::vector<tf_complex> index{ tf_complex{2.35, 0.0}, tf_complex{1.46, 0.0} };
    std::vector<double>     wavelength{ 550.0 };
    std::vector<int32_t>    point_wavelength{ 0 };
    std::vector<double>     point_angle{ 55.0 * PI / 180.0 };
    std::vector<tf_complex> n_in{ tf_complex{1.0, 0.0} }, n_out{ tf_complex{1.52, 0.0} };

    std::vector<int32_t> tcode, tpoint, tcomparison, tinfirst, tincount, tinindex;
    std::vector<double>  ttarget, tweight;

    std::vector<int32_t> vkind{ TF_VARIABLE_LAYER_THICKNESS, TF_VARIABLE_LAYER_THICKNESS,
                                TF_VARIABLE_LAYER_THICKNESS },
                         vlayer{ 0, 1, 2 }, vmaterial{ 0, 0, 0 }, vcoef{ 0, 0, 0 };

    tf_problem problem{}; tf_operands operands{}; tf_variables variables{};
    std::vector<int32_t> path, order, inert, polneed, spanfirst, spancount, spanop, spanslot;
    std::vector<int32_t> opres, oporder, oplevel;
    tf_plan_report report{}; int32_t plan_status = 0, eval_status = 0;
    tf_plan_buffers pbuf{};
    std::vector<double> scratch, residual, jacobian, opvalue;
    tf_workspace ws{}; double merit = 0.0; tf_output out{};

    void wire()
    {
        problem.n_layers = 3; problem.n_materials = 2;
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
        operands.comparison = tcomparison.empty() ? nullptr : tcomparison.data();
        operands.target_hi = nullptr; operands.tolerance = nullptr; operands.scale = nullptr;
        operands.n_span = 0; operands.reduction = nullptr;
        operands.point_first = nullptr; operands.point_count = nullptr;
        operands.point_index = nullptr; operands.point_weight = nullptr;
        operands.reduction_param = nullptr;
        operands.n_input = (int32_t)tinindex.size();
        operands.input_first = tinfirst.empty() ? nullptr : tinfirst.data();
        operands.input_count = tincount.empty() ? nullptr : tincount.data();
        operands.input_index = tinindex.empty() ? nullptr : tinindex.data();

        variables.n_variables = 3;
        variables.kind = vkind.data(); variables.layer = vlayer.data();
        variables.material = vmaterial.data(); variables.coefficient = vcoef.data();
    }

    tf_status plan()
    {
        wire();
        tf_plan_sizes s{};
        tf_query_plan_sizes(1, 3, operands.n_operands, operands.n_operands, &s);
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
        residual.assign((size_t)report.n_residuals, 0.0);
        jacobian.assign((size_t)report.n_residuals * 3, 0.0);
        out.residual = residual.data(); out.jacobian = jacobian.data();
        out.value = opvalue.data(); out.merit = &merit; out.status = &eval_status;
        return TF_OK;
    }

    tf_status jac() { wire(); return tf_evaluate_jacobian(&problem, &operands, &variables,
                                                          &pbuf, &ws, &out); }
    double J(int row, int v) const { return jacobian[(size_t)v * residual.size() + (size_t)row]; }
};

/* A flat table of Rs and Rp, both scored with target 0 and weight 1 - so residual == value and the
 * Jacobian row == d(value)/dt. This is the trusted side of every comparison below. */
static Caller flat()
{
    Caller c;
    c.tcode = { TF_TARGET_RS, TF_TARGET_RP };
    c.tpoint = { 0, 0 };
    c.ttarget = { 0.0, 0.0 }; c.tweight = { 1.0, 1.0 };
    return c;
}

/* Rs and Rp UNSCORED, feeding a scored derived operand. */
static Caller derived(int32_t code)
{
    Caller c;
    c.tcode = { TF_TARGET_RS, TF_TARGET_RP, code };
    c.tpoint = { 0, 0, 0 };
    c.ttarget = { 0.0, 0.0, 0.0 }; c.tweight = { 1.0, 1.0, 1.0 };
    c.tcomparison = { TF_COMPARE_NONE, TF_COMPARE_NONE, TF_COMPARE_EQUAL };
    c.tinfirst = { 0, 0, 0 }; c.tincount = { 0, 0, 2 }; c.tinindex = { 0, 1 };
    return c;
}

/* ---------------------------------------------------------------------------------------------
 * The exact oracle
 * ------------------------------------------------------------------------------------------- */

static void ARatioOfTwoOperandsAndItsChainRule()
{
    std::printf("a ratio of two operands, value and gradient, against the two taken separately\n");

    Caller f = flat();
    expect_eq(f.plan(), TF_OK, "the flat plan");
    expect_eq(f.jac(), TF_OK, "the flat Jacobian");
    const double Rs = f.residual[0], Rp = f.residual[1];

    Caller r = derived(TF_TARGET_RATIO);
    expect_eq(r.plan(), TF_OK, "the derived plan");
    expect_eq(r.report.n_residuals, 1, "one scored operand of the three");
    expect_eq(r.report.n_operands_unscored, 2, "and two that only feed it");
    expect_eq(r.report.n_operand_levels, 2, "two levels: the leaves and the ratio");
    expect_eq(r.jac(), TF_OK, "the derived Jacobian");

    near_(r.opvalue[2], Rs / Rp, 1e-14, "the ratio itself");
    near_(r.residual[0], Rs / Rp, 1e-14, "and it is the one residual");

    /* The unscored inputs still have values - that is the whole point of tf_output.value. */
    near_(r.opvalue[0], Rs, 1e-15, "the unscored Rs still has a value");
    near_(r.opvalue[1], Rp, 1e-15, "and so does the unscored Rp");

    /* d(a/b)/dt = a'/b - a b'/b^2, with a' and b' from the flat run. */
    for (int v = 0; v < 3; ++v)
    {
        const double da = f.J(0, v), db = f.J(1, v);
        near_(r.J(0, v), da / Rp - Rs * db / (Rp * Rp), 1e-13,
              "the chain rule through the ratio");
    }

    /* The fixture has to be one where this says something. */
    ++g_checks;
    if (!(std::fabs(Rs - Rp) > 1e-3))
    { ++g_failures; std::printf("  FAIL  Rs and Rp are equal here; the ratio proves nothing\n"); }
}

static void ADifferenceOfTwoOperands()
{
    std::printf("a difference, whose chain rule is a subtraction of two Jacobian rows\n");

    Caller f = flat();
    f.plan(); f.jac();

    Caller d = derived(TF_TARGET_DIFFERENCE);
    expect_eq(d.plan(), TF_OK, "plans");
    expect_eq(d.jac(), TF_OK, "evaluates");

    near_(d.opvalue[2], f.residual[0] - f.residual[1], 1e-15, "the difference");
    for (int v = 0; v < 3; ++v)
        near_(d.J(0, v), f.J(0, v) - f.J(1, v), 1e-15, "and its gradient");
}

/* THE COMPARISON SLOPE IS APPLIED ONCE, AT THE SCORED OPERAND - not at the points, which is where
 * it lived before a DAG existed. A satisfied one-sided constraint on a DERIVED operand must
 * therefore zero the whole column, however many operands fed it. */
static void ASatisfiedDerivedConstraintZeroesEverythingBeneathIt()
{
    std::printf("a met limit on a derived operand silences every input beneath it\n");

    Caller probe = derived(TF_TARGET_RATIO);
    probe.plan(); probe.jac();
    const double ratio = probe.opvalue[2];

    Caller met = derived(TF_TARGET_RATIO);
    met.tcomparison = { TF_COMPARE_NONE, TF_COMPARE_NONE, TF_COMPARE_LE };
    met.ttarget = { 0.0, 0.0, ratio + 0.5 };
    expect_eq(met.plan(), TF_OK, "plans");
    expect_eq(met.jac(), TF_OK, "evaluates");

    ++g_checks;
    if (met.residual[0] != 0.0 || met.J(0, 0) != 0.0 || met.J(0, 1) != 0.0 || met.J(0, 2) != 0.0)
    {
        ++g_failures;
        std::printf("  FAIL  a met derived limit still pushes: r=%.17g J=[%.17g %.17g %.17g]\n",
                    met.residual[0], met.J(0, 0), met.J(0, 1), met.J(0, 2));
    }

    /* Violated, and the column comes back. */
    Caller broken = derived(TF_TARGET_RATIO);
    broken.tcomparison = { TF_COMPARE_NONE, TF_COMPARE_NONE, TF_COMPARE_LE };
    broken.ttarget = { 0.0, 0.0, ratio - 0.5 };
    broken.plan(); broken.jac();
    for (int v = 0; v < 3; ++v)
        near_(broken.J(0, v), probe.J(0, v), 1e-15, "a violated limit carries the full gradient");
}

/* ---------------------------------------------------------------------------------------------
 * Ordering and refusals
 * ------------------------------------------------------------------------------------------- */

/* The table may list a derived operand BEFORE its inputs. Evaluating in table order would read an
 * unwritten value; the plan's topological order is what makes the listing order irrelevant. */
static void TheTableOrderDoesNotMatter()
{
    std::printf("a derived operand listed before its inputs still evaluates correctly\n");

    Caller normal = derived(TF_TARGET_RATIO);
    normal.plan(); normal.jac();

    Caller shuffled;
    shuffled.tcode = { TF_TARGET_RATIO, TF_TARGET_RS, TF_TARGET_RP };
    shuffled.tpoint = { 0, 0, 0 };
    shuffled.ttarget = { 0.0, 0.0, 0.0 }; shuffled.tweight = { 1.0, 1.0, 1.0 };
    shuffled.tcomparison = { TF_COMPARE_EQUAL, TF_COMPARE_NONE, TF_COMPARE_NONE };
    shuffled.tinfirst = { 0, 0, 0 }; shuffled.tincount = { 2, 0, 0 };
    shuffled.tinindex = { 1, 2 };
    expect_eq(shuffled.plan(), TF_OK, "plans");
    expect_eq(shuffled.oporder[0] != 0, 1, "the ratio is not evaluated first");
    expect_eq(shuffled.oplevel[0], 1, "it is at level one");
    expect_eq(shuffled.oplevel[1], 0, "and its inputs at level zero");
    expect_eq(shuffled.jac(), TF_OK, "evaluates");

    near_(shuffled.opvalue[0], normal.opvalue[2], 1e-15, "the same ratio either way round");
    for (int v = 0; v < 3; ++v)
        near_(shuffled.J(0, v), normal.J(0, v), 1e-15, "and the same gradient");
}

static void MalformedGraphsAreRefused()
{
    std::printf("a cycle, a self-reference and a mismatched code are all refused at plan time\n");

    /* Two operands feeding each other. */
    { Caller c;
      c.tcode = { TF_TARGET_RATIO, TF_TARGET_RATIO };
      c.tpoint = { 0, 0 }; c.ttarget = { 0, 0 }; c.tweight = { 1, 1 };
      c.tcomparison = { TF_COMPARE_EQUAL, TF_COMPARE_EQUAL };
      c.tinfirst = { 0, 2 }; c.tincount = { 2, 2 }; c.tinindex = { 1, 1, 0, 0 };
      expect_eq(c.plan(), TF_OPERAND_CYCLE, "a two-operand cycle"); }

    /* An operand that reads itself. */
    { Caller c;
      c.tcode = { TF_TARGET_RS, TF_TARGET_RATIO };
      c.tpoint = { 0, 0 }; c.ttarget = { 0, 0 }; c.tweight = { 1, 1 };
      c.tcomparison = { TF_COMPARE_NONE, TF_COMPARE_EQUAL };
      c.tinfirst = { 0, 0 }; c.tincount = { 0, 2 }; c.tinindex = { 0, 1 };
      expect_eq(c.plan(), TF_OPERAND_CYCLE, "an operand reading itself"); }

    /* A pointwise code handed inputs. */
    { Caller c;
      c.tcode = { TF_TARGET_RS, TF_TARGET_RP };
      c.tpoint = { 0, 0 }; c.ttarget = { 0, 0 }; c.tweight = { 1, 1 };
      c.tinfirst = { 0, 0 }; c.tincount = { 0, 1 }; c.tinindex = { 0 };
      expect_eq(c.plan(), TF_OPERAND_INPUT_KIND, "Rs given an input"); }

    /* A derived code handed none. */
    { Caller c;
      c.tcode = { TF_TARGET_RS, TF_TARGET_RATIO };
      c.tpoint = { 0, 0 }; c.ttarget = { 0, 0 }; c.tweight = { 1, 1 };
      c.tinfirst = { 0, 0 }; c.tincount = { 0, 0 }; c.tinindex = { 0 };
      expect_eq(c.plan(), TF_OPERAND_INPUT_KIND, "a ratio with no inputs"); }

    /* An input naming an operand that is not there. */
    { Caller c;
      c.tcode = { TF_TARGET_RS, TF_TARGET_RATIO };
      c.tpoint = { 0, 0 }; c.ttarget = { 0, 0 }; c.tweight = { 1, 1 };
      c.tcomparison = { TF_COMPARE_NONE, TF_COMPARE_EQUAL };
      c.tinfirst = { 0, 0 }; c.tincount = { 0, 2 }; c.tinindex = { 0, 9 };
      expect_eq(c.plan(), TF_INDEX_OUT_OF_RANGE, "an input that does not exist"); }
}

static void TheOtherEntryPointsRefuseADerivedTable()
{
    std::printf("the derivative entry points not yet taken through the DAG refuse it\n");

    Caller c = derived(TF_TARGET_RATIO);
    expect_eq(c.plan(), TF_OK, "plans");

    std::vector<double> val((size_t)c.operands.n_operands, 0.0),
                        slope((size_t)c.operands.n_operands, 0.0);
    int32_t st = 0;
    tf_spectral_output so{};
    so.value = val.data(); so.dvalue_dwavelength = slope.data(); so.status = &st;

    std::printf("    the diagnostic below is expected:\n");
    std::fflush(stdout);
    c.wire();
    expect_eq(tf_evaluate_wavelength_derivative(&c.problem, &c.operands, &c.pbuf, &c.ws, &so),
              TF_NOT_IMPLEMENTED_REDUCTION, "the wavelength derivative refuses");
}

int main()
{
    setvbuf(stdout, NULL, _IONBF, 0);
    std::printf("\n=== derived operands ===\n\n");

    ARatioOfTwoOperandsAndItsChainRule();                std::printf("\n");
    ADifferenceOfTwoOperands();                          std::printf("\n");
    ASatisfiedDerivedConstraintZeroesEverythingBeneathIt(); std::printf("\n");
    TheTableOrderDoesNotMatter();                        std::printf("\n");
    MalformedGraphsAreRefused();                         std::printf("\n");
    TheOtherEntryPointsRefuseADerivedTable();            std::printf("\n");

    std::printf("=== %d checks, %d failures ===\n\n", g_checks, g_failures);
    return g_failures == 0 ? 0 : 1;
}
