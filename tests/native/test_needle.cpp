/* test_needle.cpp — the needle perturbation function, against two independent oracles.
 *
 * ORACLE ONE, AND IT IS EXACT. Inserting material m into a layer ALREADY MADE OF m is the same
 * thing as making that layer thicker. So for any depth z inside layer j,
 *
 *     needle[host material][z]  ==  d(sum r^2)/d(thickness_j)  ==  2 sum_r r_r J[r][v_j]
 *
 * and the right-hand side comes from tf_evaluate_jacobian, which is already checked against central
 * differences. Two implementations that share no code path beyond the layer matrix itself must
 * agree to roundoff. This is a far better test than a finite difference, because it is exact, and
 * because it holds at EVERY depth in the layer - so it also proves the map is flat across a layer
 * of its own material, which is a real physical statement and a strong constraint on the sweep.
 *
 * ORACLE TWO, for a FOREIGN material, where oracle one says nothing. Physically build the split
 * stack - layer j cut at depth t with a real layer of thickness eps between the halves - and
 * central-difference the sum of squares in eps. The layer matrix is analytic in thickness, so a
 * negative eps is perfectly well defined and the difference can be centred.
 *
 * Between them the two cover the whole map: oracle one pins the sweep's bookkeeping exactly,
 * oracle two pins the insertion generator G_m that oracle one can never exercise.
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
        std::printf("  FAIL  %-48s got %.12g  expected %.12g  delta %.3g\n",
                    what, got, want, std::fabs(got - want));
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

static const double PI = 3.14159265358979323846;

/* ---------------------------------------------------------------------------------------------
 * Caller
 * ------------------------------------------------------------------------------------------- */

struct Caller
{
    std::vector<double>     thickness;
    std::vector<int32_t>    layer_material;
    std::vector<tf_complex> index;
    std::vector<double>     wavelength;
    std::vector<int32_t>    point_wavelength;
    std::vector<double>     point_angle;
    std::vector<tf_complex> n_in, n_out;
    std::vector<int32_t>    tcode, tpoint;
    std::vector<double>     tvalue, tweight;
    std::vector<int32_t>    vkind, vlayer, vmaterial, vcoef;
    int32_t                 n_materials = 2;

    std::vector<double>  depth;
    std::vector<int32_t> candidate;

    tf_problem problem{}; tf_operands operands{}; tf_variables variables{};
    tf_needle_request req{};
    std::vector<int32_t> path, order, inert, polneed;
    std::vector<int32_t> opres, oporder, oplevel;
    std::vector<int32_t> spanfirst, spancount, spanop, spanslot;
    tf_plan_report report{}; int32_t plan_status = 0, eval_status = 0, needle_status = 0;
    tf_plan_buffers pbuf{};
    std::vector<double> scratch, residual, jacobian, nresidual, map;
    std::vector<double> opvalue;
    tf_workspace ws{}; double merit = 0.0;
    tf_output out{}; tf_needle_output nout{};

    void wire()
    {
        problem.n_layers = (int32_t)thickness.size();
        problem.n_materials = n_materials;
        problem.n_wavelengths = (int32_t)wavelength.size();
        problem.n_points = (int32_t)point_angle.size();
        problem.layer_thickness = thickness.empty() ? nullptr : thickness.data();
        problem.layer_material = layer_material.empty() ? nullptr : layer_material.data();
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
        operands.code = tcode.data(); operands.point = tpoint.data();
        operands.target = tvalue.data(); operands.weight = tweight.data();

        variables.n_variables = (int32_t)vkind.size();
        variables.kind = vkind.empty() ? nullptr : vkind.data();
        variables.layer = vlayer.empty() ? nullptr : vlayer.data();
        variables.material = vmaterial.empty() ? nullptr : vmaterial.data();
        variables.coefficient = vcoef.empty() ? nullptr : vcoef.data();

        req.n_depths = (int32_t)depth.size();
        req.depth = depth.empty() ? nullptr : depth.data();
        req.n_candidates = (int32_t)candidate.size();
        req.candidate_material = candidate.empty() ? nullptr : candidate.data();
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
        pbuf.point_path = path.data(); pbuf.point_order = order.data();
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
        pbuf.report = &report; pbuf.status = &plan_status;

        const tf_status ps = tf_plan(&problem, &operands, &variables, 1, &pbuf);
        if (ps != TF_OK) return ps;

        scratch.assign((size_t)report.scratch_doubles, 0.0);
        ws.scratch = scratch.data(); ws.scratch_doubles = report.scratch_doubles;

        residual.assign((size_t)report.n_residuals, 0.0);
        jacobian.assign((size_t)report.n_residuals * vkind.size(), 0.0);
        nresidual.assign((size_t)report.n_residuals, 0.0);
        map.assign(candidate.size() * depth.size(), 0.0);

        opvalue.assign((size_t)operands.n_operands, 0.0);


        out.value = opvalue.data();


        out.residual = residual.data();
        out.jacobian = jacobian.empty() ? nullptr : jacobian.data();
        out.merit = &merit; out.status = &eval_status;

        nout.residual = nresidual.data();
        nout.dsumsq_dinsertion = map.empty() ? nullptr : map.data();
        nout.status = &needle_status;
        return TF_OK;
    }

    tf_status evaluate()  { wire(); return tf_evaluate(&problem, &operands, &pbuf, &ws, &out); }
    tf_status jac()       { wire(); return tf_evaluate_jacobian(&problem, &operands, &variables,
                                                                &pbuf, &ws, &out); }
    tf_status needle()    { wire(); return tf_evaluate_needle(&problem, &operands, &pbuf, &req,
                                                              &ws, &nout); }

    double J(int r, int v) const { return jacobian[(size_t)v * tcode.size() + (size_t)r]; }
    double N(int c, int i) const { return map[(size_t)c * depth.size() + (size_t)i]; }

    double sumsq()
    {
        evaluate();
        double s = 0.0;
        for (size_t i = 0; i < residual.size(); ++i) s += residual[i] * residual[i];
        return s;
    }

    /* d(sum r^2)/d(thickness_j), from the Jacobian: 2 sum_r r_r J[r][v]. */
    double dsumsq_dthickness(int v)
    {
        jac();
        double s = 0.0;
        for (size_t r = 0; r < residual.size(); ++r) s += 2.0 * residual[r] * J((int)r, v);
        return s;
    }
};

/* Six detuned quarter waves of two materials. */
static Caller six_layer(double angle_deg, bool absorbing = false)
{
    Caller c;
    c.wavelength = { 510.0, 550.0, 610.0 };
    c.n_materials = 3;   /* 0 high, 1 low, 2 a third, never used as a host */
    const double k = absorbing ? 0.02 : 0.0;
    c.index = {
        tf_complex{2.37, k},   tf_complex{2.35, k},   tf_complex{2.33, k},
        tf_complex{1.47, 0.0}, tf_complex{1.46, 0.0}, tf_complex{1.45, 0.0},
        tf_complex{1.90, 0.0}, tf_complex{1.88, 0.0}, tf_complex{1.86, 0.0}
    };
    c.n_in  = { tf_complex{1.0, 0.0},  tf_complex{1.0, 0.0},  tf_complex{1.0, 0.0} };
    c.n_out = { tf_complex{1.53, 0.0}, tf_complex{1.52, 0.0}, tf_complex{1.51, 0.0} };

    const double qh = 550.0 / (4.0 * 2.35), ql = 550.0 / (4.0 * 1.46);
    c.thickness      = { qh * 1.13, ql * 0.87, qh * 0.94, ql * 1.21, qh * 1.07, ql * 0.79 };
    c.layer_material = { 0, 1, 0, 1, 0, 1 };

    c.point_wavelength = { 0, 1, 2 };
    for (int i = 0; i < 3; ++i) c.point_angle.push_back(angle_deg * PI / 180.0);

    const int32_t codes[] = { TF_TARGET_RS, TF_TARGET_RP, TF_TARGET_TA, TF_TARGET_R_PSI };
    for (int32_t p = 0; p < 3; ++p)
        for (int i = 0; i < 4; ++i)
        {
            c.tcode.push_back(codes[i]); c.tpoint.push_back(p);
            c.tvalue.push_back(0.1);     c.tweight.push_back(0.5 + 0.25 * i);
        }

    for (int32_t j = 0; j < 6; ++j)
    {
        c.vkind.push_back(TF_VARIABLE_LAYER_THICKNESS); c.vlayer.push_back(j);
        c.vmaterial.push_back(0); c.vcoef.push_back(0);
    }
    return c;
}

/* ---------------------------------------------------------------------------------------------
 * Oracle one — self-insertion equals growing the layer
 * ------------------------------------------------------------------------------------------- */

static void run_self_insertion(Caller& c, const char* label, double rel_tol)
{
    std::printf("%s\n", label);

    /* Sample three depths inside every layer, plus both its boundaries. Candidates are the two
     * host materials, so each layer is covered by one of them. */
    std::vector<double> starts;
    double z = 0.0;
    for (size_t j = 0; j < c.thickness.size(); ++j) { starts.push_back(z); z += c.thickness[j]; }

    struct Sample { double z; int layer; };
    std::vector<Sample> samples;
    for (size_t j = 0; j < c.thickness.size(); ++j)
        for (int s = 0; s <= 4; ++s)
            samples.push_back(Sample{ starts[j] + c.thickness[j] * (double)s / 4.0, (int)j });

    /* A boundary depth belongs to either neighbour and both must give the same answer; keep the
     * duplicates so that is actually tested. */
    c.depth.clear();
    for (size_t i = 0; i < samples.size(); ++i) c.depth.push_back(samples[i].z);
    /* sort ascending, as the contract requires */
    for (size_t i = 1; i < c.depth.size(); ++i)
        for (size_t k = i; k > 0 && c.depth[k] < c.depth[k - 1]; --k)
        {
            const double t = c.depth[k]; c.depth[k] = c.depth[k - 1]; c.depth[k - 1] = t;
            const Sample s = samples[k]; samples[k] = samples[k - 1]; samples[k - 1] = s;
        }

    c.candidate = { 0, 1 };
    expect_eq(c.plan(), TF_OK, "plans");
    expect_eq(c.needle(), TF_OK, "needle map evaluates");

    /* The thickness answer per layer, from the Jacobian. */
    std::vector<double> want(c.thickness.size(), 0.0);
    for (size_t j = 0; j < c.thickness.size(); ++j) want[j] = c.dsumsq_dthickness((int)j);

    double worst = 0.0; int worst_i = -1;
    int compared = 0;
    for (size_t i = 0; i < c.depth.size(); ++i)
    {
        const int j = samples[i].layer;
        const int host = c.layer_material[(size_t)j];
        /* candidate index equals the material index here, because candidates are {0,1} */
        const double got = c.N(host, (int)i);
        const double ref = want[(size_t)j];
        const double d = std::fabs(got - ref) / (std::fabs(ref) + 1e-30);
        if (d > worst) { worst = d; worst_i = (int)i; }
        ++compared;
    }

    ++g_checks;
    if (!(worst <= rel_tol))
    {
        ++g_failures;
        std::printf("  FAIL  worst relative disagreement %.3g at depth %d (%.4f nm); tol %.3g\n",
                    worst, worst_i, worst_i >= 0 ? c.depth[(size_t)worst_i] : 0.0, rel_tol);
    }
    std::printf("    %d depths across %d layers, worst relative error %.3g\n",
                compared, (int)c.thickness.size(), worst);
}

static void SelfInsertionHeadOn()  { Caller c = six_layer(0.0);
                                     run_self_insertion(c, "inserting a material into itself is "
                                        "growing the layer - head-on", 1e-11); }
static void SelfInsertionOblique() { Caller c = six_layer(50.0);
                                     run_self_insertion(c, "the same at 50 degrees, where s and p "
                                        "differ", 1e-11); }
/* six_layer had an absorbing option no test turned on, so the needle map was checked on
 * transparent layers only (found reviewing scope D2.8, 2026-10-25). */
static void SelfInsertionAbsorbing() { Caller c = six_layer(50.0, true);
                                       run_self_insertion(c, "the same with absorbing layers (k = 0.02), "
                                          "at 50 degrees", 1e-11); }

/* ---------------------------------------------------------------------------------------------
 * Oracle two — a foreign material against the physically split stack
 * ------------------------------------------------------------------------------------------- */

/* Build the stack with `eps` of material m inserted at depth z, and return its sum of squares. */
static double sumsq_with_insertion(const Caller& base, double z, int32_t m, double eps)
{
    Caller c = base;

    /* Locate z. A depth on a boundary lands at the end of the earlier layer, which is the same
     * insertion either way. */
    double zf = 0.0;
    size_t j = 0;
    for (; j < c.thickness.size(); ++j)
    {
        if (z <= zf + c.thickness[j] + 1e-12) break;
        zf += c.thickness[j];
    }
    const double tt = z - zf;

    std::vector<double>  th;
    std::vector<int32_t> mat;
    for (size_t k = 0; k < c.thickness.size(); ++k)
    {
        if (k == j)
        {
            th.push_back(tt);                      mat.push_back(c.layer_material[k]);
            th.push_back(eps);                     mat.push_back(m);
            th.push_back(c.thickness[k] - tt);     mat.push_back(c.layer_material[k]);
        }
        else { th.push_back(c.thickness[k]); mat.push_back(c.layer_material[k]); }
    }
    c.thickness = th;
    c.layer_material = mat;
    c.vkind.clear(); c.vlayer.clear(); c.vmaterial.clear(); c.vcoef.clear();
    c.depth.clear(); c.candidate.clear();
    c.depth.push_back(0.0); c.candidate.push_back(0);   /* unused, but the plan wants them valid */

    c.plan();
    return c.sumsq();
}

static void ForeignMaterialAgainstAPhysicalInsertion()
{
    std::printf("a foreign material against a stack with the layer physically inserted\n");

    Caller c = six_layer(35.0);
    /* Material 2 is in none of the layers, so oracle one says nothing about it. */
    c.candidate = { 2 };

    double z = 0.0;
    std::vector<double> total;
    for (size_t j = 0; j < c.thickness.size(); ++j) total.push_back(z += c.thickness[j]);

    /* Depths scattered through the stack, avoiding exact boundaries so the split is unambiguous. */
    c.depth = { 12.0, 40.0, 95.0, 160.0, 240.0, 330.0 };
    expect_eq(c.plan(), TF_OK, "plans");
    expect_eq(c.needle(), TF_OK, "needle map evaluates");

    /* h from the usual balance: sum-of-squares is order one, its third derivative in eps is order
     * one per nm^3, so h near (3 eps)^(1/3) = 1e-5. 1e-4 sits just above, truncation dominated. */
    const double h = 1e-4;
    double worst = 0.0;
    for (size_t i = 0; i < c.depth.size(); ++i)
    {
        const double plus  = sumsq_with_insertion(c, c.depth[i], 2,  h);
        const double minus = sumsq_with_insertion(c, c.depth[i], 2, -h);
        const double fd = (plus - minus) / (2.0 * h);
        const double got = c.N(0, (int)i);
        worst = std::fmax(worst, std::fabs(got - fd) / (std::fabs(fd) + 1e-30));
    }

    ++g_checks;
    if (!(worst <= 1e-6))
    {
        ++g_failures;
        std::printf("  FAIL  worst relative disagreement with the split stack %.3g\n", worst);
    }
    std::printf("    %d depths, worst relative error against a real inserted layer %.3g\n",
                (int)c.depth.size(), worst);
}

/* ---------------------------------------------------------------------------------------------
 * Structure
 * ------------------------------------------------------------------------------------------- */

/* The map must not step at a layer boundary. At t = d_j the two halves give prefix[j+1] and
 * suffix[j], which is exactly what the start of layer j+1 gives - so a discontinuity here would
 * mean the sweep's bookkeeping is wrong at the seam. */
static void TheMapIsContinuousAcrossALayerBoundary()
{
    std::printf("the map has no step at a layer boundary\n");

    Caller c = six_layer(25.0);
    c.candidate = { 2 };

    double b = c.thickness[0] + c.thickness[1];      /* the seam between layers 1 and 2 */
    const double e = 1e-6;
    c.depth = { b - e, b, b + e };

    expect_eq(c.plan(), TF_OK, "plans");
    expect_eq(c.needle(), TF_OK, "evaluates");

    const double before = c.N(0, 0), at = c.N(0, 1), after = c.N(0, 2);
    near_(at, before, 1e-6 * (std::fabs(at) + 1.0), "the seam agrees with the layer before it");
    near_(at, after,  1e-6 * (std::fabs(at) + 1.0), "and with the layer after it");
}

/* A candidate identical to the host makes the map FLAT across that layer - adding the same
 * material anywhere inside it is the same change. A sweep that mislocated depths would break this
 * even while its layer-average stayed right. */
static void TheMapIsFlatAcrossALayerOfItsOwnMaterial()
{
    std::printf("across a layer of its own material the map is flat, not merely correct on average\n");

    Caller c = six_layer(0.0);
    c.candidate = { 0 };
    const double z0 = c.thickness[0] + c.thickness[1];   /* layer 2 is material 0 */
    const double d2 = c.thickness[2];
    c.depth = { z0 + 0.05 * d2, z0 + 0.35 * d2, z0 + 0.65 * d2, z0 + 0.95 * d2 };

    expect_eq(c.plan(), TF_OK, "plans");
    expect_eq(c.needle(), TF_OK, "evaluates");

    double lo = c.N(0, 0), hi = c.N(0, 0);
    for (int i = 1; i < 4; ++i) { lo = std::fmin(lo, c.N(0, i)); hi = std::fmax(hi, c.N(0, i)); }
    near_(hi - lo, 0.0, 1e-11 * (std::fabs(hi) + 1.0), "flat to roundoff across the layer");
}

/* ---------------------------------------------------------------------------------------------
 * Refusals
 * ------------------------------------------------------------------------------------------- */

static void AnOpaqueLayerIsRefusedNotApproximated()
{
    std::printf("a stack with an opaque layer is refused - the map there would be an artifact\n");

    Caller c;
    c.wavelength = { 550.0 };
    c.n_materials = 2;
    c.index = { tf_complex{0.15, 3.5}, tf_complex{1.46, 0.0} };
    c.n_in  = { tf_complex{1.0, 0.0} };
    c.n_out = { tf_complex{1.52, 0.0} };
    c.thickness = { 137.0, 900.0 };          /* the metal is thick enough to clamp */
    c.layer_material = { 1, 0 };
    c.point_wavelength = { 0 }; c.point_angle = { 0.0 };
    c.tcode = { TF_TARGET_RS }; c.tpoint = { 0 };
    c.tvalue = { 0.0 }; c.tweight = { 1.0 };
    c.vkind = { TF_VARIABLE_LAYER_THICKNESS }; c.vlayer = { 0 };
    c.vmaterial = { 0 }; c.vcoef = { 0 };
    c.depth = { 50.0 }; c.candidate = { 1 };

    expect_eq(c.plan(), TF_OK, "plans");
    ++g_checks;
    if (!(c.report.max_clamped_layers > 0))
    { ++g_failures; std::printf("  FAIL  the fixture should clamp\n"); }

    std::printf("    the diagnostic below is expected:\n");
    std::fflush(stdout);
    expect_eq(c.needle(), TF_NOT_IMPLEMENTED_NEEDLE_OPAQUE, "refused");
    expect_eq(c.needle_status, TF_NOT_IMPLEMENTED_NEEDLE_OPAQUE, "and said so in status");
}

static void MalformedRequestsAreRefused()
{
    std::printf("an unsorted, out-of-range or unknown request is refused, not walked off the end\n");

    Caller base = six_layer(0.0);
    double total = 0.0;
    for (size_t j = 0; j < base.thickness.size(); ++j) total += base.thickness[j];

    { Caller c = base; c.depth = { 50.0, 20.0 }; c.candidate = { 0 };
      c.plan(); expect_eq(c.needle(), TF_INDEX_OUT_OF_RANGE, "unsorted depths"); }

    { Caller c = base; c.depth = { total + 1.0 }; c.candidate = { 0 };
      c.plan(); expect_eq(c.needle(), TF_INDEX_OUT_OF_RANGE, "a depth past the back"); }

    { Caller c = base; c.depth = { -1.0 }; c.candidate = { 0 };
      c.plan(); expect_eq(c.needle(), TF_INDEX_OUT_OF_RANGE, "a depth in front of the stack"); }

    { Caller c = base; c.depth = { 50.0 }; c.candidate = { 99 };
      c.plan(); expect_eq(c.needle(), TF_INDEX_OUT_OF_RANGE, "an unknown candidate material"); }

    { Caller c = base; c.depth = { 50.0 }; c.candidate = { 0 };
      c.plan(); c.req.n_depths = 0;
      expect_eq(tf_evaluate_needle(&c.problem, &c.operands, &c.pbuf, &c.req, &c.ws, &c.nout),
                TF_BAD_COUNT, "no depths at all"); }
}

/* The residuals this call reports must be the residuals tf_evaluate reports, or the map is
 * weighted by a different design than the one being measured. */
static void ResidualsAgreeWithTheEvaluator()
{
    std::printf("the residuals it weights the map by are the evaluator's own\n");

    Caller c = six_layer(40.0);
    c.depth = { 30.0, 90.0 }; c.candidate = { 2 };
    expect_eq(c.plan(), TF_OK, "plans");
    expect_eq(c.evaluate(), TF_OK, "evaluates");
    const std::vector<double> ref = c.residual;
    expect_eq(c.needle(), TF_OK, "needle evaluates");

    int mismatches = 0;
    for (size_t i = 0; i < ref.size(); ++i) if (ref[i] != c.nresidual[i]) ++mismatches;
    expect_eq(mismatches, 0, "identical, not merely close");
}

/* THE SCRATCH MUST SERVE EVERY ENTRY POINT, NOT THE JACOBIAN ALONE.
 *
 * One scratch is allocated from tf_plan_report.scratch_doubles and then used by whichever call the
 * caller makes. The needle map wants two doubles per operand where the Jacobian wants n_variables,
 * and two per slot where the Jacobian wants one - so with three or more variables the Jacobian's
 * term happened to cover the needle's, and every fixture in this file has six. A single-layer AR
 * coating has ONE, and the needle map refused it with TF_SCRATCH_TOO_SMALL.
 *
 * Found by the optimiser (X1.4) doing the obvious thing: refine one layer, then ask where the next
 * one should go. C0.1 puts allocation on the caller, so a plan that under-states a size is not a
 * tight fit - it is a refusal, or worse, in somebody else's process.
 *
 * ONE LAYER AND ONE VARIABLE, allocating exactly what the plan asks for. */
static void TheScratchThePlanAsksForIsEnoughForANeedleMap()
{
    std::printf("the plan's scratch serves the needle map too, even with a single variable\n");

    Caller c;
    c.wavelength = { 550.0 };
    c.n_materials = 2;
    c.index = { tf_complex{2.35, 0.0}, tf_complex{1.38, 0.0} };
    c.n_in  = { tf_complex{1.0, 0.0} };
    c.n_out = { tf_complex{1.52, 0.0} };
    c.thickness      = { 99.6 };
    c.layer_material = { 1 };
    c.point_wavelength = { 0 };
    c.point_angle      = { 0.0 };
    c.tcode = { TF_TARGET_RS }; c.tpoint = { 0 };
    c.tvalue = { 0.0 };         c.tweight = { 1.0 };
    c.vkind = { TF_VARIABLE_LAYER_THICKNESS }; c.vlayer = { 0 };
    c.vmaterial = { 0 }; c.vcoef = { 0 };
    c.depth = { 20.0, 50.0, 80.0 };
    c.candidate = { 0, 1 };

    expect_eq(c.plan(), TF_OK, "plans");

    expect_eq(c.variables.n_variables, 1,
              "one variable, which is what made the old sizing too small");
    expect_eq(c.needle(), TF_OK, "and the needle map fits in the scratch the plan asked for");
}

int main()
{
    std::printf("\n=== tf_evaluate_needle ===\n\n");

    SelfInsertionHeadOn();                        std::printf("\n");
    SelfInsertionOblique();                       std::printf("\n");
    SelfInsertionAbsorbing();                     std::printf("\n");
    ForeignMaterialAgainstAPhysicalInsertion();   std::printf("\n");
    TheMapIsContinuousAcrossALayerBoundary();     std::printf("\n");
    TheMapIsFlatAcrossALayerOfItsOwnMaterial();
    TheScratchThePlanAsksForIsEnoughForANeedleMap();   std::printf("\n");
    ResidualsAgreeWithTheEvaluator();             std::printf("\n");
    AnOpaqueLayerIsRefusedNotApproximated();      std::printf("\n");
    MalformedRequestsAreRefused();                std::printf("\n");

    std::printf("=== %d checks, %d failures ===\n\n", g_checks, g_failures);
    return g_failures == 0 ? 0 : 1;
}
