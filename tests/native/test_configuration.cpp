/* test_configuration.cpp — many evaluation contexts over one stack.
 *
 * TWO EXACT ORACLES, both of which beat a tolerance.
 *
 *   A configuration table whose entries are all the SAME must reproduce the one-configuration
 *   form bit for bit. That makes the old model the degenerate case of the new one rather than an
 *   approximation of it, and it is the §9.4 test applied to this step.
 *
 *   REVERSAL HAS A PHYSICAL IDENTITY TO CHECK AGAINST: a stack traversed back-to-front with its
 *   media swapped is the same optical system seen from the other side, and transmittance is
 *   RECIPROCAL - T is identical in both directions, for any stack, lossless or not. So a reversed
 *   configuration's T must equal the forward T of the same design with the layer array physically
 *   reversed and the media exchanged. Two code paths, one answer, no step size.
 *
 * Reflectance is NOT reciprocal, so R differing between the two directions is expected and is
 * itself worth asserting - a fixture where the two agreed would prove nothing about reversal.
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

static const double PI = 3.14159265358979323846;

/* ---------------------------------------------------------------------------------------------
 * A four-layer asymmetric stack. Asymmetric on purpose: a symmetric one reads the same in both
 * directions and would let a reversal bug through.
 * ------------------------------------------------------------------------------------------- */

struct Caller
{
    std::vector<double>     thickness{ 41.0, 88.0, 127.0, 63.0 };
    std::vector<int32_t>    layer_material{ 0, 1, 0, 1 };
    std::vector<tf_complex> index{ tf_complex{2.35, 0.0}, tf_complex{1.46, 0.0} };
    std::vector<double>     wavelength{ 550.0 };
    std::vector<int32_t>    point_wavelength;
    std::vector<double>     point_angle;
    std::vector<tf_complex> n_in, n_out;             /* one-configuration form */
    std::vector<int32_t>    point_config;
    std::vector<tf_complex> cfg_in, cfg_out;
    std::vector<int32_t>    cfg_rev;
    int32_t                 n_config = 0;            /* 0 = one-configuration form */

    std::vector<int32_t> tcode, tpoint;
    std::vector<double>  ttarget, tweight;
    std::vector<int32_t> vkind, vlayer, vmaterial, vcoef;

    tf_problem problem{}; tf_configurations cfg{}; tf_operands operands{}; tf_variables variables{};
    std::vector<int32_t> path, order, inert, polneed, spanfirst, spancount, spanop, spanslot;
    std::vector<int32_t> opres, oporder, oplevel;
    tf_plan_report report{}; int32_t plan_status = 0, eval_status = 0;
    tf_plan_buffers pbuf{};
    std::vector<double> scratch, residual, jacobian;
    std::vector<double> opvalue;
    tf_workspace ws{}; double merit = 0.0; tf_output out{};

    void wire()
    {
        problem.n_layers = (int32_t)thickness.size();
        problem.n_materials = 2; problem.n_wavelengths = 1;
        problem.n_points = (int32_t)point_angle.size();
        problem.layer_thickness = thickness.data();
        problem.layer_material = layer_material.data();
        problem.layer_is_incoherent = nullptr;
        problem.material_index = index.data();
        problem.wavelength = wavelength.data();
        problem.point_wavelength = point_wavelength.data();
        problem.point_angle = point_angle.data();
        problem.material_dindex_dwavelength = nullptr;
        problem.dindex_incident_dwavelength = nullptr;
        problem.dindex_exit_dwavelength = nullptr;

        if (n_config > 0)
        {
            cfg.n_configurations = n_config;
            cfg.index_incident = cfg_in.data();
            cfg.index_exit = cfg_out.data();
            cfg.dindex_incident_dwavelength = nullptr;
            cfg.dindex_exit_dwavelength = nullptr;
            cfg.reversed = cfg_rev.empty() ? nullptr : cfg_rev.data();
            problem.configurations = &cfg;
            problem.point_configuration = point_config.data();
            problem.index_incident = nullptr; problem.index_exit = nullptr;
        }
        else
        {
            problem.configurations = nullptr; problem.point_configuration = nullptr;
            problem.index_incident = n_in.data(); problem.index_exit = n_out.data();
        }

        operands.n_operands = (int32_t)tcode.size();
        operands.delta_convention = TF_DELTA_PRINCIPAL;
        operands.code = tcode.data(); operands.point = tpoint.data();
        operands.target = ttarget.data(); operands.weight = tweight.data();
        operands.comparison = nullptr; operands.target_hi = nullptr;
        operands.tolerance = nullptr; operands.scale = nullptr;
        operands.n_span = 0; operands.reduction = nullptr;
        operands.point_first = nullptr; operands.point_count = nullptr;
        operands.point_index = nullptr; operands.point_weight = nullptr;
        operands.reduction_param = nullptr;

        variables.n_variables = (int32_t)vkind.size();
        variables.kind = vkind.empty() ? nullptr : vkind.data();
        variables.layer = vlayer.empty() ? nullptr : vlayer.data();
        variables.material = vmaterial.empty() ? nullptr : vmaterial.data();
        variables.coefficient = vcoef.empty() ? nullptr : vcoef.data();
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
        spanfirst.assign((size_t)s.point_span_first_count, 0);
        spancount.assign((size_t)s.point_span_count_count, 0);
        spanop.assign((size_t)s.point_span_operand_count, 0);
        spanslot.assign((size_t)s.point_span_slot_count, 0);
        pbuf.point_path = path.data(); pbuf.point_order = order.data();
        pbuf.variable_inert = inert.empty() ? nullptr : inert.data();
        pbuf.point_polarisation = polneed.data();
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
        jacobian.assign((size_t)report.n_residuals * (vkind.empty() ? 1 : vkind.size()), 0.0);
        opvalue.assign((size_t)operands.n_operands, 0.0);

        out.value = opvalue.data();

        out.residual = residual.data();
        out.jacobian = vkind.empty() ? nullptr : jacobian.data();
        out.merit = &merit; out.status = &eval_status;
        return TF_OK;
    }

    tf_status evaluate() { wire(); return tf_evaluate(&problem, &operands, &pbuf, &ws, &out); }
    tf_status jac()      { wire(); return tf_evaluate_jacobian(&problem, &operands, &variables,
                                                               &pbuf, &ws, &out); }
    double J(int r, int v) const { return jacobian[(size_t)v * residual.size() + (size_t)r]; }

    /* One point at one angle, scoring the given target with target 0 and weight 1. */
    void one_point(double angle_deg, int32_t code)
    {
        point_wavelength = { 0 }; point_angle = { angle_deg * PI / 180.0 };
        tcode = { code }; tpoint = { 0 }; ttarget = { 0.0 }; tweight = { 1.0 };
    }
    void vary_all()
    {
        for (int32_t j = 0; j < (int32_t)thickness.size(); ++j)
        { vkind.push_back(TF_VARIABLE_LAYER_THICKNESS); vlayer.push_back(j);
          vmaterial.push_back(0); vcoef.push_back(0); }
    }
};

/* ---------------------------------------------------------------------------------------------
 * §9.4 for this step
 * ------------------------------------------------------------------------------------------- */

static void OneConfigurationReproducesTheOldFormExactly()
{
    std::printf("a configuration table of one entry is the old problem-wide form, bit for bit\n");

    Caller a;
    a.one_point(35.0, TF_TARGET_RS); a.vary_all();
    a.n_in = { tf_complex{1.0, 0.0} }; a.n_out = { tf_complex{1.52, 0.0} };
    expect_eq(a.plan(), TF_OK, "the one-configuration plan");
    expect_eq(a.jac(), TF_OK, "and its Jacobian");

    Caller b;
    b.one_point(35.0, TF_TARGET_RS); b.vary_all();
    b.n_config = 1;
    b.cfg_in = { tf_complex{1.0, 0.0} }; b.cfg_out = { tf_complex{1.52, 0.0} };
    b.point_config = { 0 };
    expect_eq(b.plan(), TF_OK, "the configured plan");
    expect_eq(b.jac(), TF_OK, "and its Jacobian");

    exactly(b.residual[0], a.residual[0], "the same residual");
    for (int v = 0; v < 4; ++v)
        exactly(b.J(0, v), a.J(0, v), "the same Jacobian column");
}

/* ---------------------------------------------------------------------------------------------
 * Many contexts over one stack
 * ------------------------------------------------------------------------------------------- */

static void PointsInDifferentConfigurationsSeeDifferentMedia()
{
    std::printf("two points, two substrates, one stack - and one Jacobian over both\n");

    Caller c;
    c.point_wavelength = { 0, 0 };
    c.point_angle = { 0.0, 0.0 };
    c.n_config = 2;
    c.cfg_in  = { tf_complex{1.0, 0.0},  tf_complex{1.0, 0.0} };
    c.cfg_out = { tf_complex{1.52, 0.0}, tf_complex{3.42, 0.0} };   /* glass, then silicon */
    c.point_config = { 0, 1 };
    c.tcode = { TF_TARGET_RS, TF_TARGET_RS }; c.tpoint = { 0, 1 };
    c.ttarget = { 0.0, 0.0 }; c.tweight = { 1.0, 1.0 };
    c.vary_all();
    expect_eq(c.plan(), TF_OK, "plans");
    expect_eq(c.jac(), TF_OK, "evaluates");

    /* Each must equal the same design evaluated alone against that substrate. */
    for (int k = 0; k < 2; ++k)
    {
        Caller s;
        s.one_point(0.0, TF_TARGET_RS); s.vary_all();
        s.n_in = { tf_complex{1.0, 0.0} };
        s.n_out = { k == 0 ? tf_complex{1.52, 0.0} : tf_complex{3.42, 0.0} };
        s.plan(); s.jac();
        exactly(c.residual[(size_t)k], s.residual[0], "the residual matches the single-substrate run");
        exactly(c.J(k, 0), s.J(0, 0), "and so does its Jacobian row");
    }

    ++g_checks;
    if (!(std::fabs(c.residual[0] - c.residual[1]) > 1e-3))
    {
        ++g_failures;
        std::printf("  FAIL  the two substrates give the same answer; nothing is proved\n");
    }
}

/* ---------------------------------------------------------------------------------------------
 * Reversal, against reciprocity
 * ------------------------------------------------------------------------------------------- */

/* T IS RECIPROCAL. Light through a stack one way and the other way sees the same transmittance,
 * for any stack - it is a consequence of the transfer matrix being unimodular and holds whether or
 * not anything absorbs. So a reversed configuration must give the transmittance of the physically
 * reversed stack, and that is an exact identity between two code paths. */
static void ReversalAgreesWithAPhysicallyReversedStack()
{
    std::printf("a reversed configuration equals the same stack built back to front\n");

    /* Forward, through glass into air, with the layer array reversed BY HAND. */
    Caller manual;
    manual.thickness = { 63.0, 127.0, 88.0, 41.0 };
    manual.layer_material = { 1, 0, 1, 0 };
    manual.one_point(28.0, TF_TARGET_TS);
    manual.n_in = { tf_complex{1.52, 0.0} }; manual.n_out = { tf_complex{1.0, 0.0} };
    expect_eq(manual.plan(), TF_OK, "the hand-reversed plan");
    expect_eq(manual.evaluate(), TF_OK, "and its evaluation");

    /* The same thing said with a reversed configuration over the original layer order. */
    Caller rev;
    rev.one_point(28.0, TF_TARGET_TS);
    rev.n_config = 1;
    rev.cfg_in = { tf_complex{1.52, 0.0} }; rev.cfg_out = { tf_complex{1.0, 0.0} };
    rev.cfg_rev = { 1 };
    rev.point_config = { 0 };
    expect_eq(rev.plan(), TF_OK, "the reversed-configuration plan");
    expect_eq(rev.evaluate(), TF_OK, "and its evaluation");

    exactly(rev.residual[0], manual.residual[0], "identical transmittance, two code paths");

    /* And the Jacobian, which is where the traversal-versus-physical indexing lives. A variable
     * names a LAYER, so the reversed run's column for layer j must match the hand-reversed run's
     * column for the position layer j ended up in. */
    Caller revj;  revj.one_point(28.0, TF_TARGET_TS);  revj.vary_all();
    revj.n_config = 1; revj.cfg_in = { tf_complex{1.52, 0.0} };
    revj.cfg_out = { tf_complex{1.0, 0.0} }; revj.cfg_rev = { 1 }; revj.point_config = { 0 };
    revj.plan(); revj.jac();

    Caller manj;  manj.thickness = { 63.0, 127.0, 88.0, 41.0 };
    manj.layer_material = { 1, 0, 1, 0 };
    manj.one_point(28.0, TF_TARGET_TS); manj.vary_all();
    manj.n_in = { tf_complex{1.52, 0.0} }; manj.n_out = { tf_complex{1.0, 0.0} };
    manj.plan(); manj.jac();

    for (int j = 0; j < 4; ++j)
        exactly(revj.J(0, j), manj.J(0, 3 - j),
                "the column for a layer follows the layer, not the position");
}

static void ReflectanceIsNotReciprocalAndTheTestSaysSo()
{
    std::printf("R differs between the two directions, which is why the T identity means something\n");

    Caller fwd;
    fwd.one_point(28.0, TF_TARGET_RS);
    fwd.n_in = { tf_complex{1.0, 0.0} }; fwd.n_out = { tf_complex{1.52, 0.0} };
    fwd.plan(); fwd.evaluate();

    Caller back;
    back.one_point(28.0, TF_TARGET_RS);
    back.n_config = 1; back.cfg_in = { tf_complex{1.52, 0.0} };
    back.cfg_out = { tf_complex{1.0, 0.0} }; back.cfg_rev = { 1 }; back.point_config = { 0 };
    back.plan(); back.evaluate();

    ++g_checks;
    if (!(std::fabs(fwd.residual[0] - back.residual[0]) > 1e-4))
    {
        ++g_failures;
        std::printf("  FAIL  the two directions reflect identically (%.12g); the fixture is "
                    "symmetric and proves nothing\n", fwd.residual[0]);
    }
}

/* ---------------------------------------------------------------------------------------------
 * Refusals
 * ------------------------------------------------------------------------------------------- */

static void TheTwoFormsMayNotBeMixed()
{
    std::printf("a point's media are stated once, and saying it twice is refused\n");

    { Caller c; c.one_point(0.0, TF_TARGET_RS);
      c.n_config = 1; c.cfg_in = { tf_complex{1.0,0.0} }; c.cfg_out = { tf_complex{1.52,0.0} };
      c.point_config = { 0 };
      c.wire(); c.problem.index_incident = c.n_in.data();   /* both forms at once */
      c.n_in = { tf_complex{1.0, 0.0} }; c.problem.index_incident = c.n_in.data();
      tf_plan_sizes s{}; tf_query_plan_sizes(1, 0, 1, 1, &s);
      c.path.assign(1,-1); c.order.assign(1,-1); c.polneed.assign(1,0);
      c.spanfirst.assign(1,0); c.spancount.assign(1,0); c.spanop.assign(1,0); c.spanslot.assign(1,0);
      c.pbuf.point_path=c.path.data(); c.pbuf.point_order=c.order.data();
      c.pbuf.variable_inert=nullptr; c.pbuf.point_polarisation=c.polneed.data();
      c.pbuf.point_span_first=c.spanfirst.data(); c.pbuf.point_span_count=c.spancount.data();
      c.pbuf.point_span_operand=c.spanop.data(); c.pbuf.point_span_slot=c.spanslot.data();
      c.opres.assign(1,-1); c.oporder.assign(1,0); c.oplevel.assign(1,0);
      c.pbuf.operand_residual=c.opres.data(); c.pbuf.operand_order=c.oporder.data();
      c.pbuf.operand_level=c.oplevel.data();
      c.pbuf.report=&c.report; c.pbuf.status=&c.plan_status;
      expect_eq(tf_plan(&c.problem, &c.operands, &c.variables, 1, &c.pbuf), TF_BAD_COUNT,
                "problem-wide media alongside a configuration table"); }

    { Caller c; c.one_point(0.0, TF_TARGET_RS);
      c.n_config = 1; c.cfg_in = { tf_complex{1.0,0.0} }; c.cfg_out = { tf_complex{1.52,0.0} };
      c.point_config = { 7 };                       /* out of range */
      expect_eq(c.plan(), TF_INDEX_OUT_OF_RANGE, "a point naming a configuration that is not there"); }

    { Caller c; c.one_point(0.0, TF_TARGET_RS);
      c.n_in = { tf_complex{1.0,0.0} }; c.n_out = { tf_complex{1.52,0.0} };
      c.point_config = { 0 };
      c.wire(); c.problem.point_configuration = c.point_config.data();
      tf_plan_sizes s{}; tf_query_plan_sizes(1, 0, 1, 1, &s);
      c.path.assign(1,-1); c.order.assign(1,-1); c.polneed.assign(1,0);
      c.spanfirst.assign(1,0); c.spancount.assign(1,0); c.spanop.assign(1,0); c.spanslot.assign(1,0);
      c.pbuf.point_path=c.path.data(); c.pbuf.point_order=c.order.data();
      c.pbuf.variable_inert=nullptr; c.pbuf.point_polarisation=c.polneed.data();
      c.pbuf.point_span_first=c.spanfirst.data(); c.pbuf.point_span_count=c.spancount.data();
      c.pbuf.point_span_operand=c.spanop.data(); c.pbuf.point_span_slot=c.spanslot.data();
      c.opres.assign(1,-1); c.oporder.assign(1,0); c.oplevel.assign(1,0);
      c.pbuf.operand_residual=c.opres.data(); c.pbuf.operand_order=c.oporder.data();
      c.pbuf.operand_level=c.oplevel.data();
      c.pbuf.report=&c.report; c.pbuf.status=&c.plan_status;
      expect_eq(tf_plan(&c.problem, &c.operands, &c.variables, 1, &c.pbuf), TF_BAD_COUNT,
                "a point naming a configuration when there are none"); }
}

static void TheNeedleRefusesAReversedConfiguration()
{
    std::printf("a needle map over a reversed stack is refused: which side is depth zero?\n");

    Caller c;
    c.one_point(0.0, TF_TARGET_RS); c.vary_all();
    c.n_config = 1; c.cfg_in = { tf_complex{1.52, 0.0} };
    c.cfg_out = { tf_complex{1.0, 0.0} }; c.cfg_rev = { 1 }; c.point_config = { 0 };
    expect_eq(c.plan(), TF_OK, "plans - it is only the needle that cannot say");

    std::vector<double> depth = { 20.0, 100.0 };
    std::vector<int32_t> cand = { 0 };
    tf_needle_request req{};
    req.n_depths = 2; req.depth = depth.data();
    req.n_candidates = 1; req.candidate_material = cand.data();
    std::vector<double> nres((size_t)c.report.n_residuals, 0.0), map(2, -1.0);
    int32_t st = 0;
    tf_needle_output nout{};
    nout.residual = nres.data(); nout.dsumsq_dinsertion = map.data(); nout.status = &st;

    std::printf("    the diagnostic below is expected:\n");
    std::fflush(stdout);
    c.wire();
    expect_eq(tf_evaluate_needle(&c.problem, &c.operands, &c.pbuf, &req, &c.ws, &nout),
              TF_NOT_IMPLEMENTED_REVERSED, "refused");
    exactly(map[0], -1.0, "and the map was left untouched");
}

int main()
{
    setvbuf(stdout, NULL, _IONBF, 0);
    std::printf("\n=== configurations ===\n\n");

    OneConfigurationReproducesTheOldFormExactly();   std::printf("\n");
    PointsInDifferentConfigurationsSeeDifferentMedia(); std::printf("\n");
    ReversalAgreesWithAPhysicallyReversedStack();    std::printf("\n");
    ReflectanceIsNotReciprocalAndTheTestSaysSo();    std::printf("\n");
    TheTwoFormsMayNotBeMixed();                      std::printf("\n");
    TheNeedleRefusesAReversedConfiguration();        std::printf("\n");

    std::printf("=== %d checks, %d failures ===\n\n", g_checks, g_failures);
    return g_failures == 0 ? 0 : 1;
}
