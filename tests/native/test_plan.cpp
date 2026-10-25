/* test_plan.cpp — the path decision, the ordering, the clamp, and the inert-variable report.
 *
 * The plan is where a wrong answer is cheapest to produce and most expensive to find: nothing it
 * decides produces a visible symptom, it just makes the evaluator do the wrong arithmetic quietly.
 * So every decision it makes has a test that could fail for the right reason.
 */

#include "thinfilm.h"
#include "tf_layer.h"

#include <cstdio>
#include <cmath>
#include <vector>

extern "C" {
tf_status tf_query_plan_sizes(int32_t, int32_t, int32_t, int32_t, tf_plan_sizes*);
tf_status tf_plan(const tf_problem*, const tf_operands*, const tf_variables*, int32_t,
                  tf_plan_buffers*);
int32_t   tf_abi_version(void);
}

static int g_failures = 0;
static int g_checks   = 0;

static void expect(bool ok, const char* what)
{
    ++g_checks;
    if (!ok) { ++g_failures; std::printf("  FAIL  %s\n", what); }
}

static void expect_eq(long long got, long long want, const char* what)
{
    ++g_checks;
    if (got != want)
    {
        ++g_failures;
        std::printf("  FAIL  %-58s got %lld  expected %lld\n", what, got, want);
    }
}

/* A small fixture. std::vector is used HERE, in the test, and never in the engine: the test is
 * the caller, and the caller is the side that owns memory. */
struct Fixture
{
    std::vector<double>     thickness;
    std::vector<int32_t>    layer_material;
    std::vector<int32_t>    layer_incoherent;
    std::vector<tf_complex> index;          /* material-major */
    std::vector<double>     wavelength;
    std::vector<int32_t>    point_wavelength;
    std::vector<double>     point_angle;
    std::vector<tf_complex> n_in, n_out;

    std::vector<int32_t> tcode, tpoint;
    std::vector<double>  tvalue, tweight;

    std::vector<int32_t> vkind, vlayer, vmaterial, vcoef;

    tf_problem   problem{};
    tf_operands   operands{};
    tf_variables variables{};

    void wire()
    {
        problem.n_layers      = (int32_t)thickness.size();
        problem.n_materials   = 2;
        problem.n_wavelengths = (int32_t)wavelength.size();
        problem.n_points      = (int32_t)point_angle.size();
        problem.layer_thickness     = thickness.data();
        problem.layer_material      = layer_material.data();
        problem.layer_is_incoherent = layer_incoherent.empty() ? nullptr : layer_incoherent.data();
        problem.material_index      = index.data();
        problem.wavelength          = wavelength.data();
        problem.point_wavelength    = point_wavelength.data();
        problem.point_angle         = point_angle.data();
        problem.index_incident      = n_in.data();
        problem.index_exit          = n_out.data();

        operands.n_operands = (int32_t)tcode.size();
        operands.code   = tcode.data();
        operands.point  = tpoint.data();
        operands.target = tvalue.data();
        operands.weight = tweight.data();

        variables.n_variables = (int32_t)vkind.size();
        variables.kind        = vkind.data();
        variables.layer       = vlayer.data();
        variables.material    = vmaterial.data();
        variables.coefficient = vcoef.data();
    }
};

/* Two lossless layers, one wavelength, one point at normal incidence, one target, one variable. */
static Fixture simple_lossless()
{
    Fixture f;
    f.thickness        = { 100.0, 150.0 };
    f.layer_material   = { 0, 1 };
    f.index            = { tf_complex{2.35, 0.0}, tf_complex{1.46, 0.0} };  /* 2 materials x 1 wv */
    f.wavelength       = { 550.0 };
    f.point_wavelength = { 0 };
    f.point_angle      = { 0.0 };
    f.n_in             = { tf_complex{1.0, 0.0} };
    f.n_out            = { tf_complex{1.52, 0.0} };
    f.tcode  = { TF_TARGET_RA }; f.tpoint = { 0 };
    f.tvalue = { 0.0 };          f.tweight = { 1.0 };
    f.vkind  = { TF_VARIABLE_LAYER_THICKNESS }; f.vlayer = { 0 };
    f.vmaterial = { 0 }; f.vcoef = { 0 };
    f.wire();
    return f;
}

struct PlanOut
{
    std::vector<int32_t> path, order, inert, polneed;
    std::vector<int32_t> opres, oporder, oplevel;
    std::vector<int32_t> spanfirst, spancount, spanop, spanslot;
    tf_plan_report report{};
    int32_t status = 0;
    tf_plan_buffers buf{};

    void allocate(const Fixture& f)
    {
        tf_plan_sizes sizes{};
        tf_query_plan_sizes(f.problem.n_points, f.variables.n_variables, f.operands.n_operands, f.operands.n_operands, &sizes);
        path.assign((size_t)sizes.point_path_count, -1);
        order.assign((size_t)sizes.point_order_count, -1);
        inert.assign((size_t)sizes.variable_inert_count, -1);
        polneed.assign((size_t)sizes.point_polarisation_count, -1);
        buf.point_path     = path.data();
        buf.point_order    = order.data();
        buf.variable_inert = inert.empty() ? nullptr : inert.data();
        buf.point_polarisation = polneed.data();

        spanfirst.assign((size_t)sizes.point_span_first_count, 0);

        spancount.assign((size_t)sizes.point_span_count_count, 0);

        spanop.assign((size_t)sizes.point_span_operand_count, 0);

        spanslot.assign((size_t)sizes.point_span_slot_count, 0);

        buf.point_span_first = spanfirst.data();  buf.point_span_count = spancount.data();

        buf.point_span_operand = spanop.data();   buf.point_span_slot = spanslot.data();


        opres.assign((size_t)sizes.operand_residual_count, -1);


        oporder.assign((size_t)sizes.operand_order_count, 0);


        oplevel.assign((size_t)sizes.operand_level_count, 0);


        buf.operand_residual = opres.data(); buf.operand_order = oporder.data();


        buf.operand_level = oplevel.data();
        buf.report         = &report;
        buf.status         = &status;
    }
};

/* ------------------------------------------------------------------------------------------- */

static void ALosslessStackAtNormalIncidenceTakesTheStructuredPath()
{
    std::printf("a lossless stack at normal incidence takes the structured path\n");
    Fixture f = simple_lossless();
    PlanOut p; p.allocate(f);

    expect_eq(tf_plan(&f.problem, &f.operands, &f.variables, 1, &p.buf), TF_OK, "plan succeeds");
    expect_eq(p.path[0], TF_PATH_STRUCTURED, "path is structured");
    expect_eq(p.report.n_structured, 1, "one structured point");
    expect_eq(p.report.n_general, 0, "no general points");
    expect_eq(p.report.n_unsupported, 0, "nothing unsupported");
}

static void AnyAbsorptionAnywhereForcesTheGeneralPath()
{
    std::printf("any absorption at all forces the general path - k is tested exactly, not against\n"
                "a tolerance, because the structured form cannot hold an imaginary part at all\n");

    Fixture f = simple_lossless();
    f.index[1].im = 1e-12;          /* absurdly small, and still not representable */
    f.wire();

    PlanOut p; p.allocate(f);
    expect_eq(tf_plan(&f.problem, &f.operands, &f.variables, 1, &p.buf), TF_OK, "plan succeeds");
    expect_eq(p.path[0], TF_PATH_GENERAL, "k = 1e-12 is still general, not structured");
}

static void BeyondTheCriticalAngleIsGeneralEvenThoughEveryKIsZero()
{
    std::printf("beyond the critical angle the path is general although nothing absorbs -\n"
                "this is why the path belongs to the POINT and not to the stack\n");

    Fixture f = simple_lossless();
    /* Incident in a prism of index 2.0, layer of index 1.46, at 60 degrees:
     * (n0 sin th)^2 = 3.0 > 1.46^2 = 2.13, so that layer is beyond critical. */
    f.n_in[0]        = tf_complex{2.0, 0.0};
    f.point_angle[0] = 60.0 * 3.14159265358979323846 / 180.0;
    f.wire();

    PlanOut p; p.allocate(f);
    expect_eq(tf_plan(&f.problem, &f.operands, &f.variables, 1, &p.buf), TF_OK, "plan succeeds");
    expect_eq(p.path[0], TF_PATH_GENERAL, "beyond critical is general");

    /* And the same stack at normal incidence is structured, which is what makes the point the
     * unit of decision. */
    f.point_angle[0] = 0.0;
    f.wire();
    PlanOut q; q.allocate(f);
    expect_eq(tf_plan(&f.problem, &f.operands, &f.variables, 1, &q.buf), TF_OK, "plan succeeds");
    expect_eq(q.path[0], TF_PATH_STRUCTURED, "the same stack head-on is structured");
}

static void PointsAreGroupedByPath()
{
    std::printf("points are ordered so that a warp does not diverge, and the order is stable\n");

    Fixture f = simple_lossless();
    /* Six points alternating structured / general via the angle. */
    f.n_in = { tf_complex{2.0, 0.0} };
    f.point_angle      = { 0.0, 1.05, 0.0, 1.05, 0.0, 1.05 };
    f.point_wavelength = { 0, 0, 0, 0, 0, 0 };
    f.tcode  = { TF_TARGET_RA }; f.tpoint = { 0 };
    f.tvalue = { 0.0 }; f.tweight = { 1.0 };
    f.wire();

    PlanOut p; p.allocate(f);
    expect_eq(tf_plan(&f.problem, &f.operands, &f.variables, 1, &p.buf), TF_OK, "plan succeeds");
    expect_eq(p.report.n_structured, 3, "three structured");
    expect_eq(p.report.n_general, 3, "three general");

    /* structured first, then general, and within each bucket the original order survives */
    expect_eq(p.order[0], 0, "order[0]"); expect_eq(p.order[1], 2, "order[1]");
    expect_eq(p.order[2], 4, "order[2]"); expect_eq(p.order[3], 1, "order[3]");
    expect_eq(p.order[4], 3, "order[4]"); expect_eq(p.order[5], 5, "order[5]");

    for (int i = 0; i < 6; ++i)
        expect(p.path[p.order[i]] == (i < 3 ? TF_PATH_STRUCTURED : TF_PATH_GENERAL),
               "every point in its bucket");
}

static void AMassiveLayerIsRefusedLoudlyRatherThanApproximated()
{
    std::printf("a massive layer is reported unsupported - not quietly treated as coherent\n");

    Fixture f = simple_lossless();
    f.layer_incoherent = { 0, 1 };
    f.wire();

    PlanOut p; p.allocate(f);
    expect_eq(tf_plan(&f.problem, &f.operands, &f.variables, 1, &p.buf), TF_OK,
              "planning still succeeds - finding it is the plan's job");
    expect_eq(p.path[0], TF_PATH_INCOHERENT, "path is incoherent");
    expect_eq(p.report.n_incoherent, 1, "one incoherent point");
    expect_eq(p.report.n_unsupported, 1, "and it is counted unsupported");
}

static void AnOpaqueLayerMakesItsThicknessInert()
{
    std::printf("a layer clamped opaque everywhere a residual looks makes its variable inert -\n"
                "a zero Jacobian column reported rather than left for the optimiser to trip on\n");

    Fixture f = simple_lossless();
    /* A metal: k = 3 at 550 nm, one micron thick. Im(delta) = 2 pi * 1000 * 3 / 550 = 34, well
     * past the clamp at 24. */
    f.index[0] = tf_complex{1.0, 3.0};
    f.thickness[0] = 1000.0;

    /* TWO variables, one on the opaque layer and one on the layer behind it. Inertness has to be
     * per-variable: with only the opaque one the plan would - correctly - refuse the whole problem
     * as having nothing to optimise, and that would be a different assertion than this one. */
    f.vkind  = { TF_VARIABLE_LAYER_THICKNESS, TF_VARIABLE_LAYER_THICKNESS };
    f.vlayer = { 0, 1 };
    f.vmaterial = { 0, 1 };
    f.vcoef  = { 0, 0 };
    f.wire();

    PlanOut p; p.allocate(f);
    expect_eq(tf_plan(&f.problem, &f.operands, &f.variables, 1, &p.buf), TF_OK, "plan succeeds");
    expect_eq(p.report.max_clamped_layers, 1, "one layer clamped");
    expect_eq(p.inert[0], 1, "the variable on the opaque layer is inert");
    expect_eq(p.inert[1], 0, "the variable on the layer behind it is not");
    expect_eq(p.report.n_variables_inert, 1, "and exactly one is counted");

    /* Thin enough and the same layer is live again: the clamp is about OPTICAL THICKNESS, not
     * about the material. Im(delta) = 2 pi * 100 * 3 / 550 = 3.4, far below the clamp. */
    f.thickness[0] = 100.0;
    f.wire();
    PlanOut q; q.allocate(f);
    expect_eq(tf_plan(&f.problem, &f.operands, &f.variables, 1, &q.buf), TF_OK, "plan succeeds");
    expect_eq(q.report.max_clamped_layers, 0, "nothing clamped when it is thin");
    expect_eq(q.inert[0], 0, "the same metal thin enough is live again");
    expect_eq(q.report.n_variables_inert, 0, "and nothing is inert");
}

static void NothingToOptimiseIsAnError()
{
    std::printf("every variable inert is an error, not an empty success\n");

    Fixture f = simple_lossless();
    f.index[0] = tf_complex{1.0, 3.0};
    f.thickness[0] = 1000.0;
    f.wire();
    /* the only variable moves the opaque layer */

    PlanOut p; p.allocate(f);
    expect_eq(tf_plan(&f.problem, &f.operands, &f.variables, 1, &p.buf), TF_NO_LIVE_VARIABLE,
              "refuses when the Jacobian would be identically zero");
    expect_eq(p.status, TF_NO_LIVE_VARIABLE, "and says so in the status");
}

static void ScratchGrowsWithLayersAndWorkers()
{
    std::printf("the scratch size scales with layers and workers, and does NOT depend on the path\n");

    Fixture f = simple_lossless();
    PlanOut p1; p1.allocate(f);
    tf_plan(&f.problem, &f.operands, &f.variables, 1, &p1.buf);

    PlanOut p4; p4.allocate(f);
    tf_plan(&f.problem, &f.operands, &f.variables, 4, &p4.buf);

    expect(p1.report.scratch_doubles > 0, "structured scratch is positive");
    expect_eq(p4.report.scratch_doubles, 4 * p1.report.scratch_doubles,
              "four workers need four times the scratch");

    /* THE SAME PROBLEM WITH ABSORPTION NEEDS THE SAME SCRATCH, and that is a deliberate change
     * rather than an oversight. The scratch exists only for the derivative sweep, and the
     * derivative sweep is general complex arithmetic on every path - see tf_jacobian.cpp. Sizing
     * it by the evaluation path would hand a structured caller half of what tf_evaluate_jacobian
     * then asks for. When the structured derivative sweep is written, this test is where the
     * difference must reappear, and it will fail until it is updated - which is the intent. */
    Fixture g = simple_lossless();
    g.index[1].im = 0.1;
    g.wire();
    PlanOut pg; pg.allocate(g);
    tf_plan(&g.problem, &g.operands, &g.variables, 1, &pg.buf);
    expect_eq(pg.report.scratch_doubles, p1.report.scratch_doubles,
              "absorption does not change the scratch requirement today");

    /* And it must cover what the sweep actually carves: the prefix chain plus the per-layer
     * derivative cache. The formula is repeated here on purpose - if either side is edited alone
     * the two stop agreeing here rather than in a caller's heap. */
    const long long n = f.problem.n_layers;
    expect(p1.report.scratch_doubles >= (n + 1) * 8 + n * 5,
           "and covers the prefix chain and the derivative cache");
}

static void BadInputIsRefusedRatherThanRead()
{
    std::printf("malformed input is refused with a specific code\n");

    Fixture f = simple_lossless();
    PlanOut p; p.allocate(f);

    expect_eq(tf_plan(nullptr, &f.operands, &f.variables, 1, &p.buf), TF_NULL_POINTER, "null problem");
    expect_eq(tf_plan(&f.problem, &f.operands, &f.variables, 0, &p.buf), TF_BAD_COUNT, "zero workers");

    Fixture bad = simple_lossless();
    bad.layer_material[0] = 7;          /* only two materials exist */
    bad.wire();
    PlanOut q; q.allocate(bad);
    expect_eq(tf_plan(&bad.problem, &bad.operands, &bad.variables, 1, &q.buf),
              TF_INDEX_OUT_OF_RANGE, "layer pointing at a material that does not exist");

    Fixture bad2 = simple_lossless();
    bad2.vlayer[0] = 9;                 /* only two layers exist */
    bad2.wire();
    PlanOut r; r.allocate(bad2);
    expect_eq(tf_plan(&bad2.problem, &bad2.operands, &bad2.variables, 1, &r.buf),
              TF_INDEX_OUT_OF_RANGE, "variable pointing at a layer that does not exist");
}

/* ------------------------------------------------------------------------------------------- */

/* ---------------------------------------------------------------------------------------------
 * The polarisation mask
 * ------------------------------------------------------------------------------------------- */

static void EachTargetDeclaresThePolarisationsItNeeds()
{
    std::printf("every target says which of s and p it is built from, and the plan collects them\n");

    struct { int32_t code; int32_t need; const char* name; } cases[] = {
        { TF_TARGET_RS,         TF_NEED_S,               "Rs"         },
        { TF_TARGET_TS,         TF_NEED_S,               "Ts"         },
        { TF_TARGET_RP,         TF_NEED_P,               "Rp"         },
        { TF_TARGET_TP,         TF_NEED_P,               "Tp"         },
        { TF_TARGET_RA,         TF_NEED_S | TF_NEED_P,   "Ra"         },
        { TF_TARGET_TA,         TF_NEED_S | TF_NEED_P,   "Ta"         },
        { TF_TARGET_R_TANPSI,   TF_NEED_S | TF_NEED_P,   "R.tanPsi"   },
        { TF_TARGET_T_TANPSI,   TF_NEED_S | TF_NEED_P,   "T.tanPsi"   },
        { TF_TARGET_R_PSI,      TF_NEED_S | TF_NEED_P,   "R.Psi"      },
        { TF_TARGET_T_PSI,      TF_NEED_S | TF_NEED_P,   "T.Psi"      },
        { TF_TARGET_R_COSDELTA, TF_NEED_S | TF_NEED_P,   "R.cosDelta" },
        { TF_TARGET_T_COSDELTA, TF_NEED_S | TF_NEED_P,   "T.cosDelta" },
        { TF_TARGET_R_DELTA,    TF_NEED_S | TF_NEED_P,   "R.Delta"    },
        { TF_TARGET_T_DELTA,    TF_NEED_S | TF_NEED_P,   "T.Delta"    },
    };

    for (size_t i = 0; i < sizeof cases / sizeof cases[0]; ++i)
    {
        Fixture f = simple_lossless();
        f.tcode = { cases[i].code };
        f.wire();
        PlanOut p; p.allocate(f);
        tf_plan(&f.problem, &f.operands, &f.variables, 1, &p.buf);
        expect_eq(p.polneed[0], cases[i].need, cases[i].name);
    }
}

static void TheMaskIsTheUnionOverAPointsTargets()
{
    std::printf("a point wanting Rs and Rp needs both; two points can differ\n");

    /* Two points. Point 0 gets Rs only, point 1 gets Rs and Rp. */
    Fixture f = simple_lossless();
    f.point_wavelength = { 0, 0 };
    f.point_angle      = { 0.0, 0.7 };
    f.tcode  = { TF_TARGET_RS, TF_TARGET_RS, TF_TARGET_RP };
    f.tpoint = { 0, 1, 1 };
    f.tvalue = { 0.0, 0.0, 0.0 };
    f.tweight = { 1.0, 1.0, 1.0 };
    f.wire();

    PlanOut p; p.allocate(f);
    expect_eq(tf_plan(&f.problem, &f.operands, &f.variables, 1, &p.buf), TF_OK, "plans");

    expect_eq(p.polneed[0], TF_NEED_S, "point 0 needs s alone");
    expect_eq(p.polneed[1], TF_NEED_S | TF_NEED_P, "point 1 needs both");
    expect_eq(p.report.n_points_one_polarisation, 1, "one point of the two is halved");
}

static void APointNoResidualMentionsNeedsNothing()
{
    std::printf("a point nothing asks about is solved for nothing at all\n");

    Fixture f = simple_lossless();
    f.point_wavelength = { 0, 0 };
    f.point_angle      = { 0.0, 0.7 };
    f.tcode  = { TF_TARGET_RS };
    f.tpoint = { 0 };
    f.tvalue = { 0.0 }; f.tweight = { 1.0 };
    f.wire();

    PlanOut p; p.allocate(f);
    expect_eq(tf_plan(&f.problem, &f.operands, &f.variables, 1, &p.buf), TF_OK, "plans");
    expect_eq(p.polneed[1], 0, "the unmentioned point needs neither polarisation");
}

/* THE SIZE QUERY AND THE PLAN MUST AGREE, and for a pointwise table they did not.
 *
 * point_span_operand and point_span_slot hold one entry per (operand, point) SLOT. In span mode
 * that count is n_span; in pointwise mode every operand owns exactly one slot, so it is
 * n_operands - and tf_query_plan_sizes returned n_span, which is zero there. A caller that
 * allocated exactly what it was told handed tf_plan two buffers of length zero, and the transpose
 * wrote past both of them.
 *
 * NO TEST HERE CAUGHT IT because several of them pass n_operands as the n_span argument and
 * over-allocate into the right answer by accident. The managed binding (X1.1) asked for the sizes
 * the way the ABI says to, allocated them, and got an access violation. C0.1 puts allocation on
 * the caller, so a wrong size is not a detail - it is memory corruption in somebody else's
 * process, and it is the one class of mistake the caller cannot defend against.
 *
 * This test allocates EXACTLY what the query returns and nothing more, which is the only way to
 * notice. Every buffer is sized from tf_plan_sizes with no rounding up and no guessing. */
static void TheSizeQueryIsEnoughOnItsOwn()
{
    Fixture f = simple_lossless();   /* wire() is called inside; the counts come from it */

    tf_plan_sizes s{};
    expect_eq(tf_query_plan_sizes(f.problem.n_points, f.variables.n_variables,
                                  f.operands.n_span, f.operands.n_operands, &s),
              TF_OK, "the size query answers");

    /* Pointwise, so n_span is zero - and the slot buffers must still be sized for the slots that
     * exist, which is one per operand. */
    expect_eq(f.operands.n_span, 0, "this fixture really is pointwise, or the test proves nothing");
    expect_eq(s.point_span_operand_count, f.operands.n_operands,
              "point_span_operand is sized by SLOTS, not by n_span");
    expect_eq(s.point_span_slot_count, f.operands.n_operands,
              "and so is point_span_slot");

    std::vector<int32_t> path((size_t)s.point_path_count);
    std::vector<int32_t> order((size_t)s.point_order_count);
    std::vector<int32_t> inert((size_t)s.variable_inert_count);
    std::vector<int32_t> polneed((size_t)s.point_polarisation_count);
    std::vector<int32_t> spanf((size_t)s.point_span_first_count);
    std::vector<int32_t> spanc((size_t)s.point_span_count_count);
    std::vector<int32_t> spano((size_t)s.point_span_operand_count);
    std::vector<int32_t> spans((size_t)s.point_span_slot_count);
    std::vector<int32_t> opres((size_t)s.operand_residual_count);
    std::vector<int32_t> oporder((size_t)s.operand_order_count);
    std::vector<int32_t> oplevel((size_t)s.operand_level_count);

    /* A CANARY AFTER THE TWO BUFFERS THAT WERE MIS-SIZED. Writing past a std::vector is undefined
     * rather than detected, so the old bug could have gone on being silent here too; this gives it
     * something to land on that the test can read back. */
    std::vector<int32_t> canary(16, 0x5A5A5A5A);

    tf_plan_report rep{}; int32_t st = 0;
    tf_plan_buffers b{};
    b.point_path = path.data(); b.point_order = order.data();
    b.variable_inert = inert.empty() ? nullptr : inert.data();
    b.point_polarisation = polneed.data();
    b.point_span_first = spanf.data(); b.point_span_count = spanc.data();
    b.point_span_operand = spano.data(); b.point_span_slot = spans.data();
    b.operand_residual = opres.data(); b.operand_order = oporder.data();
    b.operand_level = oplevel.data();
    b.report = &rep; b.status = &st;

    expect_eq(tf_plan(&f.problem, &f.operands, &f.variables, 1, &b), TF_OK,
              "a plan over exactly the sizes it asked for");

    int intact = 1;
    for (size_t i = 0; i < canary.size(); ++i) if (canary[i] != 0x5A5A5A5A) intact = 0;
    expect(intact != 0, "and nothing was written past the buffers");
}

int main()
{
    std::printf("\nthinfilm native: tf_plan\n");
    std::printf("-------------------------------------------------\n");
    expect_eq(tf_abi_version(), TF_ABI_VERSION, "abi version matches the header");

    ALosslessStackAtNormalIncidenceTakesTheStructuredPath();
    AnyAbsorptionAnywhereForcesTheGeneralPath();
    BeyondTheCriticalAngleIsGeneralEvenThoughEveryKIsZero();
    PointsAreGroupedByPath();
    AMassiveLayerIsRefusedLoudlyRatherThanApproximated();
    AnOpaqueLayerMakesItsThicknessInert();
    NothingToOptimiseIsAnError();
    EachTargetDeclaresThePolarisationsItNeeds();
    TheMaskIsTheUnionOverAPointsTargets();
    APointNoResidualMentionsNeedsNothing();
    ScratchGrowsWithLayersAndWorkers();
    BadInputIsRefusedRatherThanRead();
    TheSizeQueryIsEnoughOnItsOwn();

    std::printf("-------------------------------------------------\n");
    std::printf("%d checks, %d failures\n\n", g_checks, g_failures);
    return g_failures == 0 ? 0 : 1;
}
