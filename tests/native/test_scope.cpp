/* test_scope.cpp — the ledger, enforced.
 *
 * docs/scope.md lists every part of this system and the state it is in. Each row marked REFUSES
 * names a symbol that is supposed to refuse loudly with a particular status code. THIS FILE MAKES
 * THAT CLAIM EXECUTABLE.
 *
 * WHY IT EXISTS. Scope is not usually lost by a decision. It is lost because the built parts live
 * in code and tests, which the build checks every time, while the unbuilt parts live in prose,
 * which nothing checks at all. Months later the code is the only record anyone trusts and it
 * contains no trace of what was meant to be there. So the unbuilt parts are given a test too.
 *
 * WHAT IT CATCHES, specifically: a branch that quietly stops refusing. Somebody - most likely me -
 * writes half of an unimplemented feature, and the loud refusal that stood in its place disappears
 * along the way. From then on it returns plausible numbers and nothing says the feature is a
 * fragment. That is exactly the failure this project is most exposed to, and it is the one the
 * contract's rule 5 exists to prevent. Rule 5 is only a rule if something enforces it.
 *
 * Every check here is therefore INVERTED relative to a normal test: it asserts that something does
 * NOT work, and fails when it starts working. When a feature is genuinely implemented, the right
 * response is to move its ledger row to BUILT and delete the check here - deliberately, in a commit
 * that says so, rather than by accident.
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
int32_t tf_abi_version(void);
}

static int g_failures = 0;
static int g_checks   = 0;

static void expect_eq(long long got, long long want, const char* what)
{
    ++g_checks;
    if (got != want)
    {
        ++g_failures;
        std::printf("  FAIL  %-52s got %lld  expected %lld\n", what, got, want);
    }
}

static void expect(bool ok, const char* what)
{
    ++g_checks;
    if (!ok) { ++g_failures; std::printf("  FAIL  %s\n", what); }
}

/* ---------------------------------------------------------------------------------------------
 * A minimal working problem, which each check then breaks in exactly one way.
 * ------------------------------------------------------------------------------------------- */

struct Caller
{
    std::vector<double>     thickness{ 100.0 };
    std::vector<int32_t>    layer_material{ 0 };
    std::vector<tf_complex> index{ tf_complex{2.35, 0.0} };
    std::vector<double>     wavelength{ 550.0 };
    std::vector<int32_t>    point_wavelength{ 0 };
    std::vector<double>     point_angle{ 0.0 };
    std::vector<tf_complex> n_in{ tf_complex{1.0, 0.0} };
    std::vector<tf_complex> n_out{ tf_complex{1.52, 0.0} };
    std::vector<int32_t>    layer_incoherent;
    std::vector<int32_t>    tcode{ TF_TARGET_RS }, tpoint{ 0 };
    std::vector<double>     tvalue{ 0.0 }, tweight{ 1.0 };
    std::vector<int32_t>    vkind{ TF_VARIABLE_LAYER_THICKNESS }, vlayer{ 0 },
                            vmaterial{ 0 }, vcoef{ 0 };
    int32_t                 delta_convention = TF_DELTA_PRINCIPAL;

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
        problem.n_wavelengths = (int32_t)wavelength.size();
        problem.n_points = (int32_t)point_angle.size();
        problem.layer_thickness = thickness.data();
        problem.layer_material = layer_material.data();
        problem.layer_is_incoherent = layer_incoherent.empty() ? nullptr : layer_incoherent.data();
        problem.material_index = index.data();
        problem.wavelength = wavelength.data();
        problem.point_wavelength = point_wavelength.data();
        problem.point_angle = point_angle.data();
        problem.index_incident = n_in.data();
        problem.index_exit = n_out.data();

        operands.n_operands = (int32_t)tcode.size();
        operands.delta_convention = delta_convention;
        operands.code = tcode.data(); operands.point = tpoint.data();
        operands.target = tvalue.data(); operands.weight = tweight.data();

        variables.n_variables = (int32_t)vkind.size();
        variables.kind = vkind.data(); variables.layer = vlayer.data();
        variables.material = vmaterial.data(); variables.coefficient = vcoef.data();
    }

    tf_status plan()
    {
        wire();
        tf_plan_sizes s{};
        tf_query_plan_sizes(problem.n_points, variables.n_variables, operands.n_operands, operands.n_operands, &s);
        path.assign((size_t)s.point_path_count, -1);
        order.assign((size_t)s.point_order_count, -1);
        inert.assign((size_t)s.variable_inert_count, 0);
        polneed.assign((size_t)s.point_polarisation_count, 0);
        pbuf.point_path = path.data();
        pbuf.point_order = order.data();
        pbuf.variable_inert = inert.empty() ? nullptr : inert.data();
        pbuf.point_polarisation = polneed.data();

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
        pbuf.report = &report;
        pbuf.status = &plan_status;

        const tf_status ps = tf_plan(&problem, &operands, &variables, 1, &pbuf);

        scratch.assign((size_t)(report.scratch_doubles > 0 ? report.scratch_doubles : 64), 0.0);
        ws.scratch = scratch.data();
        ws.scratch_doubles = (int64_t)scratch.size();

        /* Poisoned, so that "left untouched" is a claim this file can actually check. */
        residual.assign((size_t)report.n_residuals, -12345.0);
        jacobian.assign((size_t)report.n_residuals * (size_t)variables.n_variables, -12345.0);
        opvalue.assign((size_t)operands.n_operands, 0.0);

        out.value = opvalue.data();

        out.residual = residual.data();
        out.jacobian = jacobian.empty() ? nullptr : jacobian.data();
        out.merit = &merit;
        out.status = &eval_status;
        return ps;
    }

    tf_status evaluate()          { wire(); return tf_evaluate(&problem, &operands, &pbuf, &ws, &out); }
    tf_status evaluate_jacobian() { wire(); return tf_evaluate_jacobian(&problem, &operands,
                                                                       &variables, &pbuf, &ws, &out); }

    bool outputs_untouched() const
    {
        for (size_t i = 0; i < residual.size(); ++i) if (residual[i] != -12345.0) return false;
        for (size_t i = 0; i < jacobian.size(); ++i) if (jacobian[i] != -12345.0) return false;
        return true;
    }
};

/* ---------------------------------------------------------------------------------------------
 * N1.8 — incoherent / massive layers
 * ------------------------------------------------------------------------------------------- */

static void N1_8_IncoherentLayersRefuse()
{
    std::printf("N1.8  incoherent layers: classified by the plan, refused by both evaluators\n");

    Caller c;
    c.layer_incoherent = { 1 };
    expect_eq(c.plan(), TF_OK, "the plan accepts it and classifies the point");
    expect_eq(c.path[0], TF_PATH_INCOHERENT, "as TF_PATH_INCOHERENT");
    expect(c.report.n_incoherent == 1, "and counts it");
    expect(c.report.n_unsupported == 1, "and marks it unsupported");

    std::printf("      the two diagnostics below are expected:\n");
    std::fflush(stdout);
    expect_eq(c.evaluate(), TF_NOT_IMPLEMENTED_INCOHERENT, "tf_evaluate refuses");
    expect_eq(c.evaluate_jacobian(), TF_NOT_IMPLEMENTED_INCOHERENT, "tf_evaluate_jacobian refuses");
    expect(c.outputs_untouched(), "and neither wrote an output buffer");
}

/* ---------------------------------------------------------------------------------------------
 * N2.5 / N3.1 / N3.2 — material coefficients and dispersion models
 * ------------------------------------------------------------------------------------------- */

static void N2_5_MaterialCoefficientVariablesRefuse()
{
    std::printf("N2.5  a material-coefficient variable is refused, not given a zero column\n");

    Caller c;
    c.vkind = { TF_VARIABLE_MATERIAL_COEFFICIENT };
    expect_eq(c.plan(), TF_OK, "the plan accepts the kind - it is the Jacobian that cannot");

    std::printf("      the diagnostic below is expected:\n");
    std::fflush(stdout);
    expect_eq(c.evaluate_jacobian(), TF_NOT_IMPLEMENTED_VARIABLE, "tf_evaluate_jacobian refuses");
    expect(c.outputs_untouched(), "and wrote nothing");

    /* A zero column would be the dangerous answer: it reads as a coefficient that does not matter,
     * and an optimiser would simply never move it. */
}

/* ---------------------------------------------------------------------------------------------
 * N4.5 — target codes
 * ------------------------------------------------------------------------------------------- */

static void N4_5_UnknownTargetCodesRefuse()
{
    std::printf("N4.5  a target code with no implementation is refused at plan time\n");

    Caller c;
    c.tcode = { 9999 };
    std::printf("      the diagnostic below is expected:\n");
    std::fflush(stdout);
    expect_eq(c.plan(), TF_NOT_IMPLEMENTED_TARGET, "tf_plan refuses");

    /* Refused by the PLAN, not left to the evaluator, because the polarisation mask cannot be
     * derived for a code nobody has declared - and a point needing neither polarisation would be
     * solved for nothing and quietly report a NaN. */
}

/* ---------------------------------------------------------------------------------------------
 * The reserved seats. These are compile-time: if a symbol is deleted this file stops building,
 * which is the loudest possible way to notice that a future feature has lost its place.
 * ------------------------------------------------------------------------------------------- */

static void ReservedSeatsStillExist()
{
    std::printf("every unbuilt feature still has its reserved place in the ABI\n");

    expect(TF_PATH_INCOHERENT == 2, "N1.8 path code");
    expect(TF_PATH_MIXED == 3, "N1.9 path code");
    expect(TF_VARIABLE_MATERIAL_COEFFICIENT == 1, "N2.5 / N3.2 variable kind");
    expect(TF_NOT_IMPLEMENTED_MIXED == 65, "N1.9 status code");
    expect(TF_NOT_IMPLEMENTED_DISPERSION == 68, "N3.1 status code");

    /* Target codes above 63 are reserved for additions; the legacy numbering owns 0..38. */
    expect(TF_TARGET_R_PSI >= 64, "N4.5 additions are numbered clear of the legacy range");
    expect(TF_TARGET_RA == 36 && TF_TARGET_TA == 37, "N4.2 legacy numbering preserved");

    /* C0.1: the plan reports every size the caller must allocate. If a buffer is ever added
     * without a matching count, that is an allocation the caller cannot make. */
    tf_plan_sizes s{};
    expect_eq(tf_query_plan_sizes(7, 3, 11, 5, &s), TF_OK, "C0.1 sizes query");
    expect_eq(s.point_path_count, 7, "point_path sized");
    expect_eq(s.point_order_count, 7, "point_order sized");
    expect_eq(s.point_polarisation_count, 7, "point_polarisation sized");
    expect_eq(s.variable_inert_count, 3, "variable_inert sized");
    expect_eq(s.point_span_operand_count, 11, "the span transpose sized");
    expect_eq(s.operand_residual_count, 5, "the operand-to-residual map sized");
    expect_eq(s.operand_order_count, 5, "the topological order sized");
}

/* The ledger's change log and the header must agree about the ABI version. The cmake constraint
 * check enforces this from the other side; this one catches a header edited without a rebuild of
 * the docs check. */
static void TheAbiVersionIsReportedHonestly()
{
    std::printf("the compiled library reports the ABI version its header declares\n");
    expect_eq(tf_abi_version(), TF_ABI_VERSION, "tf_abi_version matches the header");
    expect(TF_ABI_VERSION == 12, "and docs/scope.md has a change-log row for version 12");
}

int main()
{
    std::printf("\n=== scope ledger: every REFUSES row still refuses ===\n\n");

    N1_8_IncoherentLayersRefuse();          std::printf("\n");
    N2_5_MaterialCoefficientVariablesRefuse(); std::printf("\n");
    N4_5_UnknownTargetCodesRefuse();        std::printf("\n");
    ReservedSeatsStillExist();              std::printf("\n");
    TheAbiVersionIsReportedHonestly();      std::printf("\n");

    std::printf("=== %d checks, %d failures ===\n\n", g_checks, g_failures);
    if (g_failures == 0)
        std::printf("If one of these ever fails because a feature was IMPLEMENTED, move its row in\n"
                    "docs/scope.md to BUILT and delete the check - in a commit that says so.\n\n");
    return g_failures == 0 ? 0 : 1;
}
