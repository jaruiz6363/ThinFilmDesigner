/* test_material_data.cpp — M3.4: per-material stress, and the provenance of an index value.
 *
 * TWO THINGS THE ABI HAD NO PLACE FOR, and they are here together because they are the same kind
 * of thing: data about a MATERIAL that the optics does not supply.
 *
 * STRESS is arithmetic and every number below is exact. sigma*d, summed. Its thickness derivative
 * is sigma of that layer's material - not 1, which is the whole reason it differs from TOTT - and
 * its needle contribution is sigma of the CANDIDATE, which makes it the first thing in the needle
 * map that tells two candidates apart by something other than optics.
 *
 * THE EXTRAPOLATION MARKS carry no arithmetic at all. The specification says thermal-band operands
 * need constants out to 50 um that most databases lack, and that the behaviour when they are
 * missing must be declared: flag or refuse, "but never extrapolate silently". The engine cannot
 * extrapolate and the caller cannot know which operand ended up resting on the guess, so the
 * caller marks and the engine propagates. These tests are about that propagation reaching every
 * place it has to reach - including through a RATIO, which is what AERATIO is.
 *
 * THE TEST THAT MATTERS MOST IS THE NULL ONE. A missing stress table must not read as zero,
 * because zero is a plausible stress AND it is exactly the target a stress-balanced design aims
 * at: silence would look like success.
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
        std::printf("  FAIL  %-46s got %.17g  expected %.17g (EXACTLY)\n", what, got, want);
    }
}

static void near_(double got, double want, double tol, const char* what)
{
    ++g_checks;
    if (!(std::fabs(got - want) <= tol))
    {
        ++g_failures;
        std::printf("  FAIL  %-46s got %.12g  expected %.12g\n", what, got, want);
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

/* Two materials with deliberately opposite-signed stress, which is what a designer balancing a
 * stack actually has: a compressive layer cancelling a tensile one. */
static const double SIGMA_H = -250.0;   /* MPa, compressive */
static const double SIGMA_L =  120.0;   /* MPa, tensile     */

struct Caller
{
    std::vector<double>     thickness{ 80.0, 130.0, 45.0 };
    std::vector<int32_t>    layer_material{ 0, 1, 0 };
    /* [n_materials * n_wavelengths], MATERIAL-MAJOR - one row of three per material, not one
     * value per material. Getting this wrong reads past the end, and the garbage that comes back
     * can look like a perfectly plausible opaque layer. */
    std::vector<tf_complex> index{ tf_complex{2.35, 0.0}, tf_complex{2.35, 0.0},
                                   tf_complex{2.35, 0.0},
                                   tf_complex{1.46, 0.0}, tf_complex{1.46, 0.0},
                                   tf_complex{1.46, 0.0} };
    std::vector<double>     stress{ SIGMA_H, SIGMA_L };
    std::vector<double>     wavelength{ 500.0, 550.0, 600.0 };
    std::vector<int32_t>    point_wavelength{ 0, 1, 2 };
    std::vector<double>     point_angle{ 0.0, 0.0, 0.0 };
    std::vector<tf_complex> n_in{ tf_complex{1.0, 0.0}, tf_complex{1.0, 0.0},
                                  tf_complex{1.0, 0.0} };
    std::vector<tf_complex> n_out{ tf_complex{1.52, 0.0}, tf_complex{1.52, 0.0},
                                   tf_complex{1.52, 0.0} };

    /* All three default to "every value measured". A test that wants a mark fills one in. */
    std::vector<int32_t> mat_extrap, in_extrap, out_extrap;

    bool have_stress = true;
    int32_t policy = TF_EXTRAPOLATION_FLAG;

    std::vector<int32_t> tcode, tpoint, tcomp;
    std::vector<double>  ttarget, tweight, tparam;
    std::vector<int32_t> spanf, spanc, spani;
    std::vector<int32_t> inf, inc, ini;
    std::vector<int32_t> vkind, vlayer, vmaterial, vcoef;

    tf_problem problem{}; tf_operands operands{}; tf_variables variables{};
    std::vector<int32_t> path, order, inert, polneed, sf, sc, so, ss;
    std::vector<int32_t> opres, oporder, oplevel;
    tf_plan_report report{}; int32_t plan_status = 0, eval_status = 0;
    tf_plan_buffers pbuf{};
    std::vector<double> scratch, residual, jacobian, opvalue, nmap, nres;
    std::vector<int32_t> opflag;
    int32_t nflagged = 0;
    tf_workspace ws{}; double merit = 0.0;
    tf_output out{}; tf_needle_output nout{};

    Caller()
    {
        for (int32_t j = 0; j < 3; ++j)
        { vkind.push_back(TF_VARIABLE_LAYER_THICKNESS); vlayer.push_back(j);
          vmaterial.push_back(0); vcoef.push_back(0); }
    }

    void wire()
    {
        problem.n_layers = (int32_t)thickness.size();
        problem.n_wavelengths = (int32_t)wavelength.size();
        problem.n_materials = (int32_t)(index.size() / wavelength.size());
        problem.n_points = (int32_t)point_wavelength.size();
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
        problem.material_stress = have_stress ? stress.data() : nullptr;
        problem.material_index_is_extrapolated = mat_extrap.empty() ? nullptr : mat_extrap.data();
        problem.index_incident_is_extrapolated = in_extrap.empty()  ? nullptr : in_extrap.data();
        problem.index_exit_is_extrapolated     = out_extrap.empty() ? nullptr : out_extrap.data();

        operands.n_operands = (int32_t)tcode.size();
        operands.delta_convention = TF_DELTA_PRINCIPAL;
        operands.extrapolation_policy = policy;
        operands.code = tcode.data();
        /* Span mode owns the points; the pointwise `point` array must be absent. */
        operands.point = spani.empty() ? tpoint.data() : nullptr;
        operands.target = ttarget.data(); operands.weight = tweight.data();
        operands.comparison = tcomp.empty() ? nullptr : tcomp.data();
        operands.target_hi = nullptr; operands.tolerance = nullptr; operands.scale = nullptr;
        operands.n_span = (int32_t)spani.size();
        operands.reduction = nullptr;
        operands.point_first = spanf.empty() ? nullptr : spanf.data();
        operands.point_count = spanc.empty() ? nullptr : spanc.data();
        operands.point_index = spani.empty() ? nullptr : spani.data();
        operands.point_weight = nullptr;
        operands.reduction_param = tparam.empty() ? nullptr : tparam.data();
        operands.n_input = (int32_t)ini.size();
        operands.input_first = inf.empty() ? nullptr : inf.data();
        operands.input_count = inc.empty() ? nullptr : inc.data();
        operands.input_index = ini.empty() ? nullptr : ini.data();

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
        residual.assign((size_t)report.n_residuals, 0.0);
        jacobian.assign((size_t)report.n_residuals * vkind.size(), 0.0);
        out.residual = residual.data(); out.jacobian = jacobian.data();
        out.value = opvalue.data();
        out.operand_flag = opflag.data();
        out.n_operands_flagged = &nflagged;
        out.merit = &merit; out.status = &eval_status;

        nres.assign((size_t)report.n_residuals, 0.0);
        nout.residual = nres.data(); nout.status = &eval_status;
        return TF_OK;
    }

    tf_status jac()
    {
        nflagged = 0;
        return tf_evaluate_jacobian(&problem, &operands, &variables, &pbuf, &ws, &out);
    }

    tf_status ev()
    {
        nflagged = 0;
        double* j = out.jacobian; out.jacobian = nullptr;
        const tf_status s = tf_evaluate(&problem, &operands, &pbuf, &ws, &out);
        out.jacobian = j;
        return s;
    }
};

/* A table whose spectral operands span all three points, and whose stack operands span none. */
static Caller table(std::vector<int32_t> codes)
{
    Caller c;
    for (size_t i = 0; i < codes.size(); ++i)
    {
        c.tcode.push_back(codes[i]);
        c.tpoint.push_back(0);
        c.ttarget.push_back(0.0);
        c.tweight.push_back(1.0);
        c.tparam.push_back(0.0); c.tparam.push_back(0.0);
        c.spanf.push_back((int32_t)c.spani.size());
        if (codes[i] == TF_TARGET_RS)
        {
            c.spanc.push_back(3);
            c.spani.push_back(0); c.spani.push_back(1); c.spani.push_back(2);
        }
        else c.spanc.push_back(0);           /* a stack operand reads no points */
    }
    return c;
}

/* --------------------------------------------------------------------------------------------- */

static void StressIsSigmaDotThickness()
{
    std::printf("STRESS sums sigma*d, and its gradient is sigma - not one, which is the point\n");

    Caller c = table({ TF_TARGET_RS, TF_TARGET_STRESS, TF_TARGET_TOTT });
    expect_eq(c.plan(), TF_OK, "plans");
    expect_eq(c.jac(), TF_OK, "evaluates");

    const double want = SIGMA_H * 80.0 + SIGMA_L * 130.0 + SIGMA_H * 45.0;
    exactly(c.opvalue[1], want, "sigma_H*80 + sigma_L*130 + sigma_H*45");
    exactly(c.opvalue[2], 80.0 + 130.0 + 45.0, "and TOTT is still the plain sum");

    /* Column-major: jacobian[v * n_residuals + r]. */
    const int32_t nr = c.report.n_residuals;
    const int32_t rs = c.pbuf.operand_residual[1], rt = c.pbuf.operand_residual[2];

    exactly(c.jacobian[(size_t)0 * nr + rs], SIGMA_H, "d(STRESS)/dd_0 is sigma of material 0");
    exactly(c.jacobian[(size_t)1 * nr + rs], SIGMA_L, "d(STRESS)/dd_1 is sigma of material 1");
    exactly(c.jacobian[(size_t)2 * nr + rs], SIGMA_H, "d(STRESS)/dd_2 follows its material too");

    for (int v = 0; v < 3; ++v)
        exactly(c.jacobian[(size_t)v * nr + rt], 1.0, "while TOTT charges one for every layer");

    /* THE DISTINCTION WORTH HAVING. A stress-balanced design is not a thin one, and the two
     * gradients point opposite ways for two layers of the same stack. */
    expect(c.jacobian[(size_t)0 * nr + rs] * c.jacobian[(size_t)1 * nr + rs] < 0.0,
           "a compressive and a tensile layer pull opposite ways, which TOTT cannot express");
}

/* THE ONE THAT MATTERS. Silence here would look exactly like success. */
static void NoStressTableIsNotZeroStress()
{
    std::printf("a missing stress table refuses; a table of zeros evaluates to zero\n");

    Caller missing = table({ TF_TARGET_RS, TF_TARGET_STRESS });
    missing.have_stress = false;
    expect_eq(missing.plan(), TF_NO_STRESS_DATA,
              "tf_plan refuses a STRESS operand with no table");
    expect_eq(missing.plan_status, TF_NO_STRESS_DATA, "and says so in the plan status");

    /* And NOT as an unrecognised code - the caller who has no data and the caller who mistyped a
     * code need different answers. */
    expect(missing.plan_status != TF_NOT_IMPLEMENTED_TARGET &&
           missing.plan_status != TF_UNKNOWN_TARGET_CODE,
           "and not as an unknown or unimplemented target code");

    /* A table with no stress operand does not care that the stress table is absent. */
    Caller nostress = table({ TF_TARGET_RS });
    nostress.have_stress = false;
    expect_eq(nostress.plan(), TF_OK, "a table with no STRESS operand plans without one");

    /* Declared zeros are a measurement of zero, and evaluate. */
    Caller zeros = table({ TF_TARGET_RS, TF_TARGET_STRESS });
    zeros.stress = { 0.0, 0.0 };
    expect_eq(zeros.plan(), TF_OK, "a declared table of zeros plans");
    expect_eq(zeros.jac(), TF_OK, "and evaluates");
    exactly(zeros.opvalue[1], 0.0, "to exactly zero");
}

/* The first needle contribution that distinguishes candidates by something other than optics. */
static void TheNeedleMapRanksCandidatesByStress()
{
    std::printf("the needle map charges sigma of the CANDIDATE, so it can prefer a material\n");

    std::vector<double>  depth = { 20.0, 90.0, 200.0 };
    std::vector<int32_t> cand  = { 0, 1 };           /* compressive, then tensile */
    tf_needle_request req{};
    req.n_depths = 3; req.depth = depth.data();
    req.n_candidates = 2; req.candidate_material = cand.data();

    Caller bare = table({ TF_TARGET_RS });
    expect_eq(bare.plan(), TF_OK, "plans");
    bare.nmap.assign(6, 0.0); bare.nout.dsumsq_dinsertion = bare.nmap.data();
    bare.wire();
    expect_eq(tf_evaluate_needle(&bare.problem, &bare.operands, &bare.pbuf, &req, &bare.ws,
                                 &bare.nout), TF_OK, "the bare needle map");

    Caller st = table({ TF_TARGET_RS, TF_TARGET_STRESS });
    expect_eq(st.plan(), TF_OK, "plans with STRESS");
    expect_eq(st.jac(), TF_OK, "evaluates");
    st.nmap.assign(6, 0.0); st.nout.dsumsq_dinsertion = st.nmap.data();
    st.wire();
    expect_eq(tf_evaluate_needle(&st.problem, &st.operands, &st.pbuf, &req, &st.ws, &st.nout),
              TF_OK, "the needle map with STRESS");

    /* Candidate-major: [c * n_depths + d]. Within one candidate the charge is constant - a
     * nanometre of one material costs the same wherever it lands - and BETWEEN candidates it is
     * not, which is the whole of the news. */
    const double charge0 = st.nmap[0] - bare.nmap[0];
    const double charge1 = st.nmap[3] - bare.nmap[3];

    for (int d = 1; d < 3; ++d)
    {
        near_(st.nmap[(size_t)d]     - bare.nmap[(size_t)d],     charge0, 1e-9,
              "candidate 0 pays the same at every depth");
        near_(st.nmap[(size_t)(3 + d)] - bare.nmap[(size_t)(3 + d)], charge1, 1e-9,
              "candidate 1 pays the same at every depth");
    }

    expect(charge0 != charge1,
           "and the two candidates are charged DIFFERENTLY - the first non-optical distinction "
           "the needle map draws");

    /* Exactly: the contribution is adjoint * sigma_m, and both candidates share the adjoint, so
     * the ratio of the charges is the ratio of the stresses. */
    near_(charge0 / charge1, SIGMA_H / SIGMA_L, 1e-12,
          "and in the exact ratio of their stresses");
}

/* --------------------------------------------------------------------------------------------- */

static void AnUnmarkedProblemIsFlaggedNowhere()
{
    std::printf("with nothing marked, nothing is flagged\n");

    Caller c = table({ TF_TARGET_RS });
    expect_eq(c.plan(), TF_OK, "plans");
    expect_eq(c.jac(), TF_OK, "evaluates");
    expect_eq(c.opflag[0], 0, "no flag");
    expect_eq(c.nflagged, 0, "and nothing counted");
}

static void AMarkedMaterialFlagsTheOperandsThatReachIt()
{
    std::printf("a marked material flags the operands whose points touch it, and only those\n");

    /* Material 0 is synthesised at 600 nm only - point 2. */
    Caller c = table({ TF_TARGET_RS, TF_TARGET_RS });
    c.mat_extrap.assign(2 * 3, 0);
    c.mat_extrap[(size_t)0 * 3 + 2] = 1;

    /* Operand 0 spans all three points; narrow operand 1 to the clean pair. */
    c.spanf[1] = (int32_t)c.spani.size();
    c.spanc[1] = 2;
    c.spani.push_back(0); c.spani.push_back(1);

    expect_eq(c.plan(), TF_OK, "plans under the FLAG policy");
    expect_eq(c.jac(), TF_OK, "evaluates rather than refusing");

    expect_eq(c.opflag[0] & TF_FLAG_SYNTHETIC_INDEX, TF_FLAG_SYNTHETIC_INDEX,
              "the operand spanning 600 nm is flagged");
    expect_eq(c.opflag[1] & TF_FLAG_SYNTHETIC_INDEX, 0,
              "the operand that stops at 550 nm is not");
    expect_eq(c.nflagged, 1, "and exactly one operand is counted");

    /* THE VALUE IS STILL RETURNED. An emittance good to the first digit still decides a design;
     * the flag is what stops it being read as one good to the fourth. */
    ++g_checks;
    if (!(c.opvalue[0] > 0.0 && c.opvalue[0] < 3.0))
    { ++g_failures; std::printf("  FAIL  the value should be a sum of three real reflectances, "
                                "not a sentinel: %.17g\n", c.opvalue[0]); }
}

static void AMarkedMediumTaintsToo()
{
    std::printf("the media count: at 20 um the substrate is what nobody measured\n");

    Caller c = table({ TF_TARGET_RS });
    c.out_extrap.assign(3, 0);
    c.out_extrap[1] = 1;                      /* 550 nm, which operand 0 spans */
    expect_eq(c.plan(), TF_OK, "plans");
    expect_eq(c.jac(), TF_OK, "evaluates");
    expect_eq(c.opflag[0] & TF_FLAG_SYNTHETIC_INDEX, TF_FLAG_SYNTHETIC_INDEX,
              "a marked exit medium flags the operand");

    Caller d = table({ TF_TARGET_RS });
    d.in_extrap.assign(3, 0);
    d.in_extrap[0] = 1;
    expect_eq(d.plan(), TF_OK, "plans");
    expect_eq(d.jac(), TF_OK, "evaluates");
    expect_eq(d.opflag[0] & TF_FLAG_SYNTHETIC_INDEX, TF_FLAG_SYNTHETIC_INDEX,
              "and so does a marked incident medium");
}

/* AERATIO IS A RATIO of a solar operand over a thermal one, and the thermal half is exactly the
 * half resting on constants nobody has. If the ratio came back clean, the flag would sit on an
 * operand nobody scored and be missing from the one they did. */
static void ARatioInheritsTheTaintOfItsInputs()
{
    std::printf("a derived operand inherits its inputs' flags - the AERATIO case\n");

    Caller c = table({ TF_TARGET_RS, TF_TARGET_RS, TF_TARGET_RATIO });
    c.mat_extrap.assign(2 * 3, 0);
    c.mat_extrap[(size_t)0 * 3 + 2] = 1;      /* material 0 at 600 nm */

    /* Operand 0: the clean pair. Operand 1: the tainted point alone. */
    c.spani.clear();
    c.spanf[0] = 0; c.spanc[0] = 2;
    c.spani.push_back(0); c.spani.push_back(1);
    c.spanf[1] = 2; c.spanc[1] = 1; c.spani.push_back(2);
    c.spanf[2] = 3; c.spanc[2] = 0;           /* the ratio reads operands, not points */

    c.inf = { 0, 0, 0 }; c.inc = { 0, 0, 2 }; c.ini = { 0, 1 };

    /* Only the ratio is scored; its two inputs exist to feed it. */
    c.tcomp = { TF_COMPARE_NONE, TF_COMPARE_NONE, TF_COMPARE_EQUAL };

    expect_eq(c.plan(), TF_OK, "plans");
    expect_eq(c.jac(), TF_OK, "evaluates");

    expect_eq(c.opflag[0] & TF_FLAG_SYNTHETIC_INDEX, 0, "the clean input is clean");
    expect_eq(c.opflag[1] & TF_FLAG_SYNTHETIC_INDEX, TF_FLAG_SYNTHETIC_INDEX,
              "the tainted input is marked");
    expect_eq(c.opflag[2] & TF_FLAG_SYNTHETIC_INDEX, TF_FLAG_SYNTHETIC_INDEX,
              "AND THE RATIO IS MARKED, though it has no points of its own to be tainted by");
    expect_eq(c.nflagged, 2, "two operands flagged: the input and the ratio");
}

static void TheValueOnlyEntryPointReportsFlagsToo()
{
    std::printf("tf_evaluate reports flags - it used to compute them and throw them away\n");

    Caller c = table({ TF_TARGET_RS });
    c.mat_extrap.assign(2 * 3, 0);
    c.mat_extrap[(size_t)0 * 3 + 1] = 1;
    expect_eq(c.plan(), TF_OK, "plans");
    expect_eq(c.ev(), TF_OK, "tf_evaluate returns OK");
    expect_eq(c.opflag[0] & TF_FLAG_SYNTHETIC_INDEX, TF_FLAG_SYNTHETIC_INDEX,
              "and a caller who wanted only values is still told");
    expect_eq(c.nflagged, 1, "and counted");
}

static void TheRefusePolicyRefusesAtPlanTime()
{
    std::printf("REFUSE refuses once, at plan time, not on iteration four hundred\n");

    Caller c = table({ TF_TARGET_RS });
    c.mat_extrap.assign(2 * 3, 0);
    c.mat_extrap[(size_t)1 * 3 + 0] = 1;      /* material 1 at 500 nm */
    c.policy = TF_EXTRAPOLATION_REFUSE;
    expect_eq(c.plan(), TF_EXTRAPOLATED_INDEX, "tf_plan refuses");
    expect_eq(c.plan_status, TF_EXTRAPOLATED_INDEX, "and says which kind of refusal");

    /* The same table with nothing marked plans under the same strict policy. */
    Caller d = table({ TF_TARGET_RS });
    d.policy = TF_EXTRAPOLATION_REFUSE;
    expect_eq(d.plan(), TF_OK, "and permits a problem with nothing marked");

    /* A policy that is neither is a caller error with its own name, not a silent default. */
    Caller e = table({ TF_TARGET_RS });
    e.policy = 7;
    expect_eq(e.plan(), TF_UNKNOWN_EXTRAPOLATION_POLICY, "an unknown policy is refused by name");
}

/* The two flag families must not be confused: one is about a crossing inside the spectrum, the
 * other about the spectrum itself. */
static void TheIndexFlagIsADifferentBitFromTheCrossingFlags()
{
    std::printf("SYNTHETIC_INDEX is its own bit, distinct from the three crossing flags\n");

    expect(TF_FLAG_SYNTHETIC_INDEX != TF_FLAG_EXTRAPOLATED, "not the same bit as EXTRAPOLATED");
    expect(TF_FLAG_SYNTHETIC_INDEX != TF_FLAG_AMBIGUOUS,    "nor AMBIGUOUS");
    expect(TF_FLAG_SYNTHETIC_INDEX != TF_FLAG_PINNED,       "nor PINNED");
    expect((TF_FLAG_SYNTHETIC_INDEX &
            (TF_FLAG_EXTRAPOLATED | TF_FLAG_AMBIGUOUS | TF_FLAG_PINNED)) == 0,
           "and it overlaps none of them, so both facts can be reported together");
}

int main()
{
    setvbuf(stdout, NULL, _IONBF, 0);
    std::printf("\n=== material data: stress, and the provenance of an index ===\n\n");

    StressIsSigmaDotThickness();                       std::printf("\n");
    NoStressTableIsNotZeroStress();                    std::printf("\n");
    TheNeedleMapRanksCandidatesByStress();             std::printf("\n");
    AnUnmarkedProblemIsFlaggedNowhere();               std::printf("\n");
    AMarkedMaterialFlagsTheOperandsThatReachIt();      std::printf("\n");
    AMarkedMediumTaintsToo();                          std::printf("\n");
    ARatioInheritsTheTaintOfItsInputs();               std::printf("\n");
    TheValueOnlyEntryPointReportsFlagsToo();           std::printf("\n");
    TheRefusePolicyRefusesAtPlanTime();                std::printf("\n");
    TheIndexFlagIsADifferentBitFromTheCrossingFlags(); std::printf("\n");

    std::printf("=== %d checks, %d failures ===\n\n", g_checks, g_failures);
    return g_failures != 0 ? 1 : 0;
}
