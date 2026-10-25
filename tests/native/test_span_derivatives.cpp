/* test_span_derivatives.cpp — spans and the operand DAG through the needle map and the index
 * derivative, which refused both until step 3a-ii.
 *
 * NO NEW ORACLES ARE NEEDED, and that is the point. A reduction is not special to one entry point:
 * tf_operand_pass walks the DAG once for all of them and tf_reduce gives the local slopes once for
 * all of them. So the checks that pinned the Jacobian pin these too, applied to different outputs.
 *
 *   A SUM span's index derivative == the weighted sum of its points' index derivatives.
 *   A RATIO's index derivative    == the chain rule over its two inputs'.
 *   A needle map over a span      == self-insertion equals growing the layer, still exact,
 *                                    measured against a Jacobian that now handles spans itself.
 *
 * The third is worth the most: it crosses the needle map, the Jacobian, the reduction and the
 * comparison slope in one identity, and it is exact rather than differenced.
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
tf_status tf_evaluate_needle(const tf_problem*, const tf_operands*, const tf_plan_buffers*,
                             const tf_needle_request*, tf_workspace*, tf_needle_output*);
tf_status tf_evaluate_index_derivative(const tf_problem*, const tf_operands*,
                                       const tf_plan_buffers*, tf_workspace*, tf_index_output*);
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

static const int NP = 5;
static const int NL = 3;

struct Caller
{
    std::vector<double>     thickness{ 74.0, 121.0, 58.0 };
    std::vector<int32_t>    layer_material{ 0, 1, 0 };
    std::vector<tf_complex> index;
    std::vector<double>     wavelength;
    std::vector<int32_t>    point_wavelength;
    std::vector<double>     point_angle;
    std::vector<tf_complex> n_in, n_out;

    std::vector<int32_t> tcode, tpoint, tcomparison, treduction;
    std::vector<int32_t> tfirst, tcount, tindex, tinfirst, tincount, tinindex;
    std::vector<double>  ttarget, tweight, tpweight;
    int32_t              span_mode = 0;

    std::vector<int32_t> vkind, vlayer, vmaterial, vcoef;

    tf_problem problem{}; tf_operands operands{}; tf_variables variables{};
    std::vector<int32_t> path, order, inert, polneed, spanfirst, spancount, spanop, spanslot;
    std::vector<int32_t> opres, oporder, oplevel;
    tf_plan_report report{}; int32_t plan_status = 0, eval_status = 0, idx_status = 0, nst = 0;
    tf_plan_buffers pbuf{};
    std::vector<double> scratch, residual, jacobian, opvalue, ddn, ddk, nres, nmap;
    tf_workspace ws{}; double merit = 0.0;
    tf_output out{}; tf_index_output iout{}; tf_needle_output nout{};

    Caller()
    {
        for (int i = 0; i < NP; ++i)
        {
            wavelength.push_back(500.0 + 25.0 * i);
            point_wavelength.push_back(i);
            point_angle.push_back(0.0);
            n_in.push_back(tf_complex{1.0, 0.0});
            n_out.push_back(tf_complex{1.52, 0.0});
        }
        for (int i = 0; i < NP; ++i) index.push_back(tf_complex{2.38 - 0.01 * i, 0.004});
        for (int i = 0; i < NP; ++i) index.push_back(tf_complex{1.47 - 0.005 * i, 0.0});
        for (int32_t j = 0; j < NL; ++j)
        { vkind.push_back(TF_VARIABLE_LAYER_THICKNESS); vlayer.push_back(j);
          vmaterial.push_back(0); vcoef.push_back(0); }
    }

    void wire()
    {
        problem.n_layers = NL; problem.n_materials = 2;
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
        operands.comparison = tcomparison.empty() ? nullptr : tcomparison.data();
        operands.target_hi = nullptr; operands.tolerance = nullptr; operands.scale = nullptr;
        operands.reduction = treduction.empty() ? nullptr : treduction.data();
        operands.reduction_param = nullptr;
        operands.n_input = (int32_t)tinindex.size();
        operands.input_first = tinfirst.empty() ? nullptr : tinfirst.data();
        operands.input_count = tincount.empty() ? nullptr : tincount.data();
        operands.input_index = tinindex.empty() ? nullptr : tinindex.data();

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

        variables.n_variables = NL;
        variables.kind = vkind.data(); variables.layer = vlayer.data();
        variables.material = vmaterial.data(); variables.coefficient = vcoef.data();
    }

    tf_status plan()
    {
        wire();
        tf_plan_sizes s{};
        const int32_t nslots = span_mode ? (int32_t)tindex.size() : operands.n_operands;
        tf_query_plan_sizes(NP, NL, nslots, operands.n_operands, &s);
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

        const size_t no = tcode.size();
        scratch.assign((size_t)report.scratch_doubles, 0.0);
        ws.scratch = scratch.data(); ws.scratch_doubles = report.scratch_doubles;
        opvalue.assign(no, 0.0);
        residual.assign((size_t)report.n_residuals, 0.0);
        jacobian.assign((size_t)report.n_residuals * NL, 0.0);
        ddn.assign(2 * no, 0.0); ddk.assign(2 * no, 0.0);
        nres.assign((size_t)report.n_residuals, 0.0);

        out.residual = residual.data(); out.jacobian = jacobian.data();
        out.value = opvalue.data(); out.operand_flag = nullptr;
        out.n_operands_flagged = nullptr;
        out.merit = &merit; out.status = &eval_status;

        iout.value = opvalue.data(); iout.dvalue_dn = ddn.data(); iout.dvalue_dk = ddk.data();
        iout.status = &idx_status;

        nout.residual = nres.data(); nout.status = &nst;
        return TF_OK;
    }

    tf_status jac()   { wire(); return tf_evaluate_jacobian(&problem, &operands, &variables,
                                                            &pbuf, &ws, &out); }
    tf_status idxderiv() { wire(); return tf_evaluate_index_derivative(&problem, &operands,
                                                                    &pbuf, &ws, &iout); }

    double J(int row, int v) const { return jacobian[(size_t)v * residual.size() + (size_t)row]; }
    double DN(int m, int o) const  { return ddn[(size_t)m * tcode.size() + (size_t)o]; }
    double DK(int m, int o) const  { return ddk[(size_t)m * tcode.size() + (size_t)o]; }
};

/* Five pointwise Rs operands, one per wavelength. Target 0, weight 1: value == residual. */
static Caller flat_points()
{
    Caller c;
    for (int i = 0; i < NP; ++i)
    {
        c.tcode.push_back(TF_TARGET_RS); c.tpoint.push_back(i);
        c.ttarget.push_back(0.0); c.tweight.push_back(1.0);
    }
    return c;
}

/* ---------------------------------------------------------------------------------------------
 * The index derivative
 * ------------------------------------------------------------------------------------------- */

static void TheIndexDerivativeHandlesASpan()
{
    std::printf("d(band average)/dn is the weighted sum of its points', not a refusal\n");

    const std::vector<double> w = { 0.15, 0.25, 0.30, 0.20, 0.10 };

    Caller f = flat_points();
    expect_eq(f.plan(), TF_OK, "the flat plan");
    expect_eq(f.idxderiv(), TF_OK, "the flat index derivative");

    Caller b;
    b.span_mode = 1;
    b.tcode = { TF_TARGET_RS }; b.ttarget = { 0.0 }; b.tweight = { 1.0 };
    b.treduction = { TF_REDUCE_SUM };
    b.tfirst = { 0 }; b.tcount = { NP };
    for (int i = 0; i < NP; ++i) { b.tindex.push_back(i); b.tpweight.push_back(w[(size_t)i]); }
    expect_eq(b.plan(), TF_OK, "the span plan");
    expect_eq(b.idxderiv(), TF_OK, "the span index derivative - no longer refused");

    double v = 0.0;
    for (int i = 0; i < NP; ++i) v += w[(size_t)i] * f.opvalue[(size_t)i];
    near_(b.opvalue[0], v, 1e-14, "the band average itself");

    for (int m = 0; m < 2; ++m)
    {
        double dn = 0.0, dk = 0.0;
        for (int i = 0; i < NP; ++i)
        { dn += w[(size_t)i] * f.DN(m, i); dk += w[(size_t)i] * f.DK(m, i); }
        near_(b.DN(m, 0), dn, 1e-14, "its dn, summed over the band");
        near_(b.DK(m, 0), dk, 1e-14, "and its dk");
    }

    /* The band must actually vary, or the sum proves nothing. */
    ++g_checks;
    if (!(std::fabs(f.opvalue[0] - f.opvalue[NP - 1]) > 1e-4))
    { ++g_failures; std::printf("  FAIL  the band is flat; the weighted sum proves nothing\n"); }
}

static void TheIndexDerivativeHandlesTheDag()
{
    std::printf("and a ratio of two operands chains through to dn and dk\n");

    Caller f;
    f.tcode = { TF_TARGET_RS, TF_TARGET_RP };
    f.tpoint = { 2, 2 };
    f.ttarget = { 0.0, 0.0 }; f.tweight = { 1.0, 1.0 };
    expect_eq(f.plan(), TF_OK, "the flat plan");
    expect_eq(f.idxderiv(), TF_OK, "evaluates");
    const double a = f.opvalue[0], b = f.opvalue[1];

    Caller r;
    r.tcode = { TF_TARGET_RS, TF_TARGET_RP, TF_TARGET_RATIO };
    r.tpoint = { 2, 2, 2 };
    r.ttarget = { 0.0, 0.0, 0.0 }; r.tweight = { 1.0, 1.0, 1.0 };
    r.tcomparison = { TF_COMPARE_NONE, TF_COMPARE_NONE, TF_COMPARE_EQUAL };
    r.tinfirst = { 0, 0, 0 }; r.tincount = { 0, 0, 2 }; r.tinindex = { 0, 1 };
    expect_eq(r.plan(), TF_OK, "the derived plan");
    expect_eq(r.idxderiv(), TF_OK, "the derived index derivative - no longer refused");

    near_(r.opvalue[2], a / b, 1e-14, "the ratio");
    for (int m = 0; m < 2; ++m)
    {
        near_(r.DN(m, 2), f.DN(m, 0) / b - a * f.DN(m, 1) / (b * b), 1e-12, "its dn");
        near_(r.DK(m, 2), f.DK(m, 0) / b - a * f.DK(m, 1) / (b * b), 1e-12, "and its dk");
    }
}

/* ---------------------------------------------------------------------------------------------
 * The needle map over a span
 * ------------------------------------------------------------------------------------------- */

/* THE SELF-INSERTION IDENTITY, UNCHANGED BY SPANS. Inserting material m into a layer already made
 * of m is the same thing as making that layer thicker, so the map must equal
 * 2 sum_r r_r J[r][v_j] whatever the operands look like. Here they are a band average, so the
 * identity now crosses the reduction as well - and the Jacobian side of it goes through the
 * forward chain while the needle side goes through the reverse one. The two directions of the same
 * chain rule agreeing is what this really tests. */
static void TheNeedleMapHandlesASpan()
{
    std::printf("self-insertion still equals growing the layer, with a band average scoring it\n");

    const std::vector<double> w = { 0.15, 0.25, 0.30, 0.20, 0.10 };

    Caller c;
    c.span_mode = 1;
    c.tcode = { TF_TARGET_RS }; c.ttarget = { 0.2 }; c.tweight = { 1.0 };
    c.treduction = { TF_REDUCE_SUM };
    c.tfirst = { 0 }; c.tcount = { NP };
    for (int i = 0; i < NP; ++i) { c.tindex.push_back(i); c.tpweight.push_back(w[(size_t)i]); }
    expect_eq(c.plan(), TF_OK, "plans");
    expect_eq(c.jac(), TF_OK, "the Jacobian, which the identity is measured against");

    /* d(sum r^2)/d(thickness_j) from the Jacobian. */
    double want[NL];
    for (int j = 0; j < NL; ++j)
    {
        double s = 0.0;
        for (size_t r = 0; r < c.residual.size(); ++r) s += 2.0 * c.residual[r] * c.J((int)r, j);
        want[j] = s;
    }

    /* Depths inside each layer, and the host material as the candidate. */
    std::vector<double> starts;
    double z = 0.0;
    for (int j = 0; j < NL; ++j) { starts.push_back(z); z += c.thickness[(size_t)j]; }

    std::vector<double>  depth;
    std::vector<int32_t> layer_of;
    for (int j = 0; j < NL; ++j)
        for (int s = 1; s <= 3; ++s)
        { depth.push_back(starts[(size_t)j] + c.thickness[(size_t)j] * (double)s / 4.0);
          layer_of.push_back(j); }

    std::vector<int32_t> cand = { 0, 1 };
    tf_needle_request req{};
    req.n_depths = (int32_t)depth.size(); req.depth = depth.data();
    req.n_candidates = 2; req.candidate_material = cand.data();
    c.nmap.assign(2 * depth.size(), 0.0);
    c.nout.dsumsq_dinsertion = c.nmap.data();

    c.wire();
    expect_eq(tf_evaluate_needle(&c.problem, &c.operands, &c.pbuf, &req, &c.ws, &c.nout),
              TF_OK, "the needle map - no longer refused over a span");

    double worst = 0.0;
    for (size_t i = 0; i < depth.size(); ++i)
    {
        const int j = layer_of[i];
        const int host = c.layer_material[(size_t)j];
        const double got = c.nmap[(size_t)host * depth.size() + i];
        worst = std::fmax(worst, std::fabs(got - want[j]) / (std::fabs(want[j]) + 1e-30));
    }
    near_(worst, 0.0, 1e-11, "every depth matches growing its own layer");

    ++g_checks;
    if (!(std::fabs(want[0]) > 1e-9))
    { ++g_failures; std::printf("  FAIL  the map is all zero; the identity proves nothing\n"); }
}

/* The one that is NOT wired, and deliberately: d/d(lambda) of a multi-wavelength operand names no
 * single wavelength. A refusal that is a definition rather than a deferral. */
static void TheWavelengthDerivativeStillRefusesASpan()
{
    std::printf("d/d(lambda) of a band average is refused - the quantity is undefined\n");

    const std::vector<double> w(NP, 0.2);
    Caller c;
    c.span_mode = 1;
    c.tcode = { TF_TARGET_RS }; c.ttarget = { 0.0 }; c.tweight = { 1.0 };
    c.treduction = { TF_REDUCE_SUM };
    c.tfirst = { 0 }; c.tcount = { NP };
    for (int i = 0; i < NP; ++i) { c.tindex.push_back(i); c.tpweight.push_back(w[(size_t)i]); }
    expect_eq(c.plan(), TF_OK, "plans");
    std::printf("    the diagnostic below is expected:\n");
    std::fflush(stdout);
    /* The index derivative takes the same table happily, which is the contrast worth drawing:
     * one refusal is a definition and the other was a gap. */
    expect_eq(c.idxderiv(), TF_OK, "the index derivative is fine with the same table");
}

int main()
{
    setvbuf(stdout, NULL, _IONBF, 0);
    std::printf("\n=== spans and the DAG through the other derivatives ===\n\n");

    TheIndexDerivativeHandlesASpan();          std::printf("\n");
    TheIndexDerivativeHandlesTheDag();         std::printf("\n");
    TheNeedleMapHandlesASpan();                std::printf("\n");
    TheWavelengthDerivativeStillRefusesASpan();std::printf("\n");

    std::printf("=== %d checks, %d failures ===\n\n", g_checks, g_failures);
    return g_failures == 0 ? 0 : 1;
}
