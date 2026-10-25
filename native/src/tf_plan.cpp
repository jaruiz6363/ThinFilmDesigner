/* tf_plan.cpp — decide, once, what the inner loop will do.
 *
 * Three jobs, and everything expensive is here so that the evaluators can be branch-free:
 *
 *   1. Choose the arithmetic each point will use, and refuse loudly if it is not implemented.
 *   2. Order the points so that a warp does not diverge once this is a kernel.
 *   3. Report the scratch size, which the caller cannot compute from counts alone.
 *
 * It also finds what the caller could not: which variables are inert because the layer they move
 * is opaque everywhere anyone is looking.
 *
 * THIS IS NATIVE AND NOT MANAGED ON PURPOSE. The path decision depends on optical conditions -
 * whether every material is lossless at this wavelength, whether any layer is beyond the critical
 * angle at this angle, whether the absorption clamp will engage. If the managed side decided that
 * and the native side assumed something else, the disagreement would be silent and would surface
 * as a wrong derivative rather than as an error. There is one implementation, and the evaluator
 * calls the same predicates.
 */

#include "tf_internal.h"

#include <math.h>

extern "C" {

/* ------------------------------------------------------------------------------------------- */

static int tf_all_null_free(const void* const* p, int n)
{
    for (int i = 0; i < n; ++i) if (p[i] == 0) return 0;
    return 1;
}

TF_API int32_t tf_abi_version(void)
{
    return TF_ABI_VERSION;
}

TF_API tf_status tf_query_plan_sizes(int32_t n_points, int32_t n_variables, int32_t n_span,
                                     int32_t n_operands, tf_plan_sizes* out_sizes)
{
    if (out_sizes == 0) return TF_NULL_POINTER;
    if (n_points < 0 || n_variables < 0 || n_span < 0 || n_operands < 0) return TF_BAD_COUNT;

    out_sizes->point_path_count     = (int64_t)n_points;
    out_sizes->point_order_count    = (int64_t)n_points;
    out_sizes->variable_inert_count = (int64_t)n_variables;
    out_sizes->point_polarisation_count = (int64_t)n_points;
    out_sizes->point_span_first_count   = (int64_t)n_points;
    out_sizes->point_span_count_count   = (int64_t)n_points;
    /* ONE ENTRY PER SLOT, AND A SLOT IS NOT A SPAN ENTRY IN POINTWISE MODE.
     *
     * The plan transposes (operand -> points) into (point -> operands) by counting sort, writing
     * one entry per (operand, point) SLOT. In span mode that is n_span of them; in pointwise mode
     * every operand owns exactly one slot, so it is n_operands - and this said n_span, which is
     * zero there. A caller who allocated exactly what it was told then handed tf_plan a null, or
     * worse a buffer of zero length that tf_plan dutifully wrote past.
     *
     * The native tests never saw it because several of them pass n_operands as the n_span
     * argument, which over-allocates into the right answer by accident. The managed binding
     * (X1.1) had no such habit: it asked, allocated, and got an access violation. That is the
     * wrapper doing the job the ledger says it is for - C0.1 makes the caller allocate, so a
     * wrong size here is not a detail, it is memory corruption in someone else's process. */
    {
        const int64_t n_slots = (n_span > 0) ? (int64_t)n_span : (int64_t)n_operands;
        out_sizes->point_span_operand_count = n_slots;
        out_sizes->point_span_slot_count    = n_slots;
    }
    out_sizes->operand_residual_count   = (int64_t)n_operands;
    out_sizes->operand_order_count      = (int64_t)n_operands;
    out_sizes->operand_level_count      = (int64_t)n_operands;
    return TF_OK;
}

/* ---------------------------------------------------------------------------------------------
 * Scratch
 *
 * ONE prefix chain per worker, not two. The obvious layout stores prefix[j] and suffix[j] and
 * costs 2(n+1) matrices; but the backward sweep only ever needs the running suffix at the layer
 * it is looking at, which lives in registers:
 *
 *     forward   j = 1..n :   prefix[j] = prefix[j-1] * M_{j-1}          (stored)
 *     backward  j = n..1 :   dM_j = prefix[j] * (dM_j/dt_j) * S         (S in registers)
 *                            S    = M_j * S
 *
 * so the storage is (n+1) matrices and the arithmetic is unchanged. On the GPU that halving is
 * the difference between fitting in shared memory and not.
 *
 * Polarisations are done one after the other and reuse the same chain, because a point that needs
 * both Ts and Tp needs them at different times and nothing is gained by holding both.
 * ------------------------------------------------------------------------------------------- */

/* THE SIZE IS THE GENERAL SIZE EVEN WHEN EVERY POINT IS STRUCTURED, and that is deliberate rather
 * than lazy. The scratch exists only for the derivative sweep, and the derivative sweep is written
 * in general complex arithmetic on every path - see the note at the top of tf_jacobian.cpp. Sizing
 * it by the evaluation path would hand back a buffer half the size of what tf_evaluate_jacobian
 * then asks for, and the caller would get TF_SCRATCH_TOO_SMALL from a plan that had just told it
 * the number. A structured derivative sweep is future work; when it lands, this is where the
 * halving reappears, and the two must land together. */

static int64_t tf_scratch_doubles_for(int32_t n_layers, int32_t n_materials, int32_t n_slots,
                                      int32_t max_span, int32_t n_operands, int32_t n_variables,
                                      int32_t n_workers, int uses_trials, int uses_dispersion)
{
    const int64_t prefix  = ((int64_t)n_layers + 1) * 8;  /* the prefix chain, 8 doubles a matrix */
    /* EVERY TERM HERE IS THE MAXIMUM OVER THE ENTRY POINTS, NOT THE JACOBIAN'S ALONE.
     *
     * One scratch size serves tf_evaluate, tf_evaluate_jacobian, tf_evaluate_needle,
     * tf_evaluate_wavelength_derivative and tf_evaluate_index_derivative, because the caller
     * allocates once from tf_plan_report and then calls whichever it likes. So a term sized for
     * the Jacobian and smaller than what the needle wants is not a tight fit - it is a refusal
     * waiting for a design with few enough variables.
     *
     * That is what it was. The needle map wants two doubles per operand where the Jacobian wants
     * n_variables, and two per slot where the Jacobian wants one; with three or more variables
     * the Jacobian term happened to cover it, which is why every native needle test passed and
     * an AR coating with ONE variable did not. Found by the optimiser (X1.4) doing the obvious
     * thing: refine a single layer, then ask where to put the next one. */
    const int64_t derivs  = (int64_t)n_layers * 6;        /* per layer: dr and dt complex, dR and dT real */
    const int64_t spans   = (int64_t)max_span * 5;        /* values, weights, abscissa and two
                                                           * slope buffers */
    const int64_t seeds   = (int64_t)n_materials * 8;     /* one insertion generator per material,
                                                           * for the needle map - see tf_needle.cpp */
    const int64_t slopes  = (int64_t)n_slots * 2;         /* d(residual)/d(x_p) per (operand, point)
                                                           * slot, kept so the sweeps need not
                                                           * solve the stack again to recover it.
                                                           * TWICE: the needle keeps the folded
                                                           * slopes alongside the raw ones. */
    /* d(value)/dx for every operand needs n_variables columns; the needle needs the operand
     * values and their adjoints, which is two. Whichever is larger. */
    const int64_t work    = (int64_t)n_operands * (n_variables > 2 ? n_variables : 2);  /* for EVERY operand,
                                                           * not only the scored ones - the chain
                                                           * rule needs the rows of inputs that
                                                           * produce no residual */
    const int64_t working = 8 + (int64_t)n_layers + 1;    /* a scratch matrix per worker, and the
                                                           * needle's per-boundary depth walk */
    /* THE TRIAL AXIS COSTS NOTHING PER TRIAL, which is the property worth having: a second copy
     * of the working matrix to accumulate the weighted sum into, and four scalars an operand -
     * its value on this pass, its running reduction, its winning trial and that trial's value.
     * Ten thousand trials need exactly what two need, because the reduction streams. */
    const int64_t trials  = uses_trials ? ((int64_t)n_operands * n_variables
                                        +  (int64_t)n_operands * 4) : 0;
    /* GROUP DELAY, GDD AND TOD: prefix matrices and six readings per layer, for their own sweep. */
    const int64_t disp    = uses_dispersion ? tf_dispersion_scratch(n_layers) : 0;
    return (int64_t)n_workers * (prefix + derivs + seeds + slopes + spans + work + working + trials + disp);
}


/* ---------------------------------------------------------------------------------------------
 * The path decision for one point
 * ------------------------------------------------------------------------------------------- */

typedef struct tf_point_verdict
{
    int32_t path;
    int32_t clamped_layers;
    int     any_layer_live;   /* at least one layer whose thickness still matters here */
} tf_point_verdict;

/* Lossless means k == 0 EXACTLY, not k below some tolerance.
 *
 * A tolerance here would be a trap. The structured path is not an approximation that degrades
 * gracefully as k grows; it is a different representation that cannot hold an imaginary part at
 * all. A layer with k = 1e-12 taking the structured path does not lose a little accuracy - it
 * loses the absorption entirely, and reports a lossless stack for a design that was deliberately
 * given a small loss. If a caller wants to neglect a tiny k, it can set it to zero itself and own
 * the decision. */
static int tf_is_lossless(tf_complex n)
{
    return (n.im == 0.0) ? 1 : 0;
}

static void tf_verdict_for_point(const tf_problem* p, int32_t point, tf_point_verdict* out)
{
    const int32_t wi = p->point_wavelength[point];
    const double  wv = p->wavelength[wi];
    const double  theta0 = p->point_angle[point];

    const tf_complex n0 = tf_index_internal(tf_incident_at(p, point));

    /* (n0 sin theta0)^2 — conserved across every interface, so computed once per point. This is
     * what makes Snell's law implicit and keeps a complex arcsine out of the inner loop. */
    const double s0 = sin(theta0);
    const tf_complex n0_sin = tf_c_scale(n0, s0);
    const tf_complex n0_sin_sq = tf_c_mul(n0_sin, n0_sin);

    int lossless = tf_is_lossless(n0) && tf_is_lossless(tf_index_internal(tf_exit_at(p, point)));
    int beyond_critical = 0;
    int clamped = 0;
    int any_live = 0;
    int any_incoherent = 0;

    for (int32_t j = 0; j < p->n_layers; ++j)
    {
        if (p->layer_is_incoherent != 0 && p->layer_is_incoherent[j] != 0) any_incoherent = 1;

        const int32_t m = p->layer_material[j];
        const tf_complex n = tf_index_internal(p->material_index[(int64_t)m * p->n_wavelengths + wi]);

        if (!tf_is_lossless(n)) lossless = 0;

        const tf_complex kappa = tf_layer_kappa(n, n0_sin_sq);

        /* Beyond the critical angle kappa leaves the positive real axis, which the structured
         * form cannot represent even though every k is zero. This is the case that makes the
         * path a property of the POINT and not of the stack. */
        if (kappa.im != 0.0) beyond_critical = 1;

        if (tf_layer_would_clamp(kappa, p->layer_thickness[j], wv)) ++clamped;
        else any_live = 1;
    }

    /* The substrate too: glass into air past the critical angle has every layer propagating and
     * only the exit medium evanescent, and the structured form returned NaN for it. */
    if (tf_layer_kappa(tf_index_internal(tf_exit_at(p, point)), n0_sin_sq).im != 0.0) beyond_critical = 1;

    out->clamped_layers = clamped;
    out->any_layer_live = any_live;

    if (any_incoherent)          out->path = TF_PATH_INCOHERENT;
    else if (lossless && !beyond_critical) out->path = TF_PATH_STRUCTURED;
    else                         out->path = TF_PATH_GENERAL;
}

/* ---------------------------------------------------------------------------------------------
 * tf_plan
 * ------------------------------------------------------------------------------------------- */

TF_API tf_status tf_plan(const tf_problem*   problem,
                         const tf_operands*   operands,
                         const tf_variables* variables,
                         int32_t             n_workers,
                         tf_plan_buffers*    plan)
{
    if (problem == 0 || operands == 0 || variables == 0 || plan == 0) return TF_NULL_POINTER;
    if (plan->status == 0 || plan->report == 0) return TF_NULL_POINTER;

    int32_t* status = plan->status;
    *status = TF_OK;

    {
        const void* const required[] = {
            problem->material_index,
            problem->wavelength, problem->point_wavelength, problem->point_angle,
            operands->code, operands->target, operands->weight,
            plan->point_path, plan->point_order, plan->point_polarisation,
            plan->point_span_first, plan->point_span_count,
            plan->point_span_operand, plan->point_span_slot,
            plan->operand_residual, plan->operand_order, plan->operand_level
        };
        if (!tf_all_null_free(required, (int)(sizeof required / sizeof required[0])))
            { *status = TF_NULL_POINTER; return TF_NULL_POINTER; }


        /* `point` is required only in pointwise mode. In span mode it must be absent, and
         * the span validation below says so; demanding it here would refuse the very shape the
         * span form exists to express. */
        if (!tf_is_span_mode(operands) && operands->point == 0)
            { *status = TF_NULL_POINTER; return TF_NULL_POINTER; }
    }
    /* A stack of NO layers is legitimate - it is a bare substrate, and the Fresnel reflectance of
     * one is a perfectly good thing to ask for. The layer arrays are only required when there are
     * layers to describe. Demanding them unconditionally rejected the simplest valid problem
     * there is, which is the kind of thing a test of the easy case finds and a test of the
     * interesting case never does. */
    if (problem->n_layers > 0 &&
        (problem->layer_thickness == 0 || problem->layer_material == 0))
        { *status = TF_NULL_POINTER; return TF_NULL_POINTER; }

    if (variables->n_variables > 0 &&
        (variables->kind == 0 || variables->layer == 0 || plan->variable_inert == 0))
        { *status = TF_NULL_POINTER; return TF_NULL_POINTER; }

    if (problem->n_layers < 0 || problem->n_materials <= 0 ||
        problem->n_wavelengths <= 0 || problem->n_points <= 0 ||
        operands->n_operands <= 0 || variables->n_variables < 0 || n_workers <= 0)
        { *status = TF_BAD_COUNT; return TF_BAD_COUNT; }

    /* CONFIGURATIONS, and the mode exclusivity that keeps a point's media unambiguous. */
    if (tf_has_configs(problem))
    {
        const tf_configurations* cfg = problem->configurations;

        if (problem->index_incident != 0 || problem->index_exit != 0)
            { *status = TF_BAD_COUNT; return TF_BAD_COUNT; }
        if (problem->point_configuration == 0 ||
            cfg->index_incident == 0 || cfg->index_exit == 0)
            { *status = TF_NULL_POINTER; return TF_NULL_POINTER; }
        if (cfg->n_configurations <= 0) { *status = TF_BAD_COUNT; return TF_BAD_COUNT; }

        for (int32_t i = 0; i < problem->n_points; ++i)
            if (problem->point_configuration[i] < 0 ||
                problem->point_configuration[i] >= cfg->n_configurations)
                { *status = TF_INDEX_OUT_OF_RANGE; return TF_INDEX_OUT_OF_RANGE; }
    }
    else
    {
        if (problem->index_incident == 0 || problem->index_exit == 0)
            { *status = TF_NULL_POINTER; return TF_NULL_POINTER; }
        /* No configurations means no point can name one. */
        if (problem->point_configuration != 0) { *status = TF_BAD_COUNT; return TF_BAD_COUNT; }
    }

    /* Index validation, once, so the evaluators never have to. */
    for (int32_t j = 0; j < problem->n_layers; ++j)
        if (problem->layer_material[j] < 0 || problem->layer_material[j] >= problem->n_materials)
            { *status = TF_INDEX_OUT_OF_RANGE; return TF_INDEX_OUT_OF_RANGE; }

    for (int32_t i = 0; i < problem->n_points; ++i)
        if (problem->point_wavelength[i] < 0 || problem->point_wavelength[i] >= problem->n_wavelengths)
            { *status = TF_INDEX_OUT_OF_RANGE; return TF_INDEX_OUT_OF_RANGE; }

    for (int32_t r = 0; r < operands->n_operands; ++r)
        if (!tf_is_span_mode(operands) &&
            (operands->point[r] < 0 || operands->point[r] >= problem->n_points))
            { *status = TF_INDEX_OUT_OF_RANGE; return TF_INDEX_OUT_OF_RANGE; }

    if (operands->delta_convention != TF_DELTA_PRINCIPAL &&
        operands->delta_convention != TF_DELTA_POSITIVE &&
        operands->delta_convention != TF_DELTA_SHORTEST_ARC)
        { *status = TF_UNKNOWN_DELTA_CONVENTION; return TF_UNKNOWN_DELTA_CONVENTION; }

    /* A STRESS OPERAND AGAINST NO STRESS TABLE, caught here rather than on the first evaluation.
     * The table is a property of the problem and does not change between iterations, so there is
     * exactly one moment at which the answer can be given, and it is this one. */
    if (problem->material_stress == 0)
        for (int32_t o = 0; o < operands->n_operands; ++o)
            if (operands->code[o] == TF_TARGET_STRESS)
                { *status = TF_NO_STRESS_DATA; return TF_NO_STRESS_DATA; }

    /* A Delta target compared by plain subtraction is a trap, and this is the one place that can
     * say so before the optimiser has wasted an hour on it. A model at -179 degrees and a target
     * at +179 are two degrees apart; subtracted they are 358 apart, and the merit jumps by that
     * much as the design crosses the cut. The warning is raised ONCE, here, rather than by the
     * evaluator on every call - a diagnostic printed a million times is a diagnostic nobody
     * reads. It is a warning and not an error because a caller whose operands sit nowhere near
     * the cut is doing nothing wrong. */
    if (operands->delta_convention != TF_DELTA_SHORTEST_ARC)
    {
        int32_t n_delta = 0;
        for (int32_t r = 0; r < operands->n_operands; ++r)
            if (operands->code[r] == TF_TARGET_R_DELTA || operands->code[r] == TF_TARGET_T_DELTA)
                ++n_delta;

        if (n_delta > 0)
            fprintf(stderr,
                    "\n*** THINFILM: %d Delta target%s compared across a branch cut ***\n"
                    "    operands.delta_convention is %s, so the residual is a plain subtraction\n"
                    "    of two angles. Near the cut that reports an error of up to 2 pi where the\n"
                    "    true discrepancy is nearly zero, and the merit is discontinuous there.\n"
                    "    Set TF_DELTA_SHORTEST_ARC (2) unless the operands are known to sit well\n"
                    "    away from it, or use cos(Delta), which has no cut at all.\n\n",
                    n_delta, (n_delta == 1) ? "" : "s",
                    (operands->delta_convention == TF_DELTA_PRINCIPAL)
                        ? "TF_DELTA_PRINCIPAL (-pi, pi]" : "TF_DELTA_POSITIVE [0, 2 pi)");
    }

    /* THE SPANS AND THEIR REDUCTIONS. */
    if (tf_is_span_mode(operands))
    {
        if (operands->point != 0) { *status = TF_BAD_COUNT; return TF_BAD_COUNT; }
        if (operands->point_first == 0 || operands->point_index == 0)
            { *status = TF_NULL_POINTER; return TF_NULL_POINTER; }
        if (operands->n_span <= 0) { *status = TF_BAD_COUNT; return TF_BAD_COUNT; }

        for (int32_t o = 0; o < operands->n_operands; ++o)
        {
            const int32_t f = operands->point_first[o], c = operands->point_count[o];
            /* A SCORED OPERAND WITH AN EMPTY SPAN reduces nothing and would report whatever a
             * reduction over zero points happens to produce. It is a caller mistake, not a value. */
            if (c <= 0 && !tf_code_is_stack(operands->code[o]) && !tf_is_derived(operands, o))
                { *status = TF_EMPTY_SPAN; return TF_EMPTY_SPAN; }
            if (c <= 0) continue;
            if (f < 0 || (int64_t)f + c > operands->n_span)
                { *status = TF_INDEX_OUT_OF_RANGE; return TF_INDEX_OUT_OF_RANGE; }
        }
        for (int32_t s = 0; s < operands->n_span; ++s)
            if (operands->point_index[s] < 0 || operands->point_index[s] >= problem->n_points)
                { *status = TF_INDEX_OUT_OF_RANGE; return TF_INDEX_OUT_OF_RANGE; }
    }

    for (int32_t o = 0; o < operands->n_operands; ++o)
    {
        switch (tf_reduction_of(operands, o))
        {
            case TF_REDUCE_SUM: case TF_REDUCE_MAX: case TF_REDUCE_MIN:
            case TF_REDUCE_PTV: case TF_REDUCE_RMS_DEV:
                break;

            case TF_REDUCE_CROSSING:
                /* A crossing needs two samples to interpolate between. One is not an edge. */
                if (tf_slot_count(operands, o) < 2)
                    { *status = TF_EMPTY_SPAN; return TF_EMPTY_SPAN; }
                /* The level lives in reduction_param[0] and must be a number. A level the
                 * quantity can never reach - 1.5 for a reflectance - is a caller error too, but
                 * knowing that needs a range per target code, which is recorded as a refinement
                 * rather than guessed at here. */
                if (!(tf_sharpness_of(operands, o) == tf_sharpness_of(operands, o)))
                    { *status = TF_BAD_OPERAND_RANGE; return TF_BAD_OPERAND_RANGE; }
                break;

            default:
                { *status = TF_UNKNOWN_REDUCTION; return TF_UNKNOWN_REDUCTION; }
        }

        /* The sharpness is only a sharpness for an extremum; for CROSSING param[0] is the level,
         * which may legitimately be negative. */
        if (tf_reduction_of(operands, o) != TF_REDUCE_CROSSING &&
            tf_sharpness_of(operands, o) < 0.0)
            { *status = TF_BAD_OPERAND_RANGE; return TF_BAD_OPERAND_RANGE; }
    }

    for (int32_t v = 0; v < variables->n_variables; ++v)
    {
        if (variables->kind[v] != TF_VARIABLE_LAYER_THICKNESS &&
            variables->kind[v] != TF_VARIABLE_MATERIAL_COEFFICIENT)
            { *status = TF_UNKNOWN_VARIABLE_KIND; return TF_UNKNOWN_VARIABLE_KIND; }

        if (variables->kind[v] == TF_VARIABLE_LAYER_THICKNESS &&
            (variables->layer[v] < 0 || variables->layer[v] >= problem->n_layers))
            { *status = TF_INDEX_OUT_OF_RANGE; return TF_INDEX_OUT_OF_RANGE; }
    }

    /* THE REFUSE HALF OF tf_extrapolation_policy.
     *
     * Marks and points are both fixed now, so whether any scored operand rests on a synthesised
     * index cannot change between one evaluation and the next. Refusing here means an optimiser
     * never discovers it on iteration four hundred, having spent four hundred iterations chasing
     * a number the caller had already said it would not accept.
     *
     * PLACED AFTER EVERY INDEX RANGE CHECK, not with the other early validation: it dereferences
     * point indices, and an out-of-range one must be reported as out of range rather than read.
     *
     * UNSCORED OPERANDS COUNT. An operand with TF_COMPARE_NONE exists only to feed another, and
     * the tainted value reaches the merit through it just the same. */
    if (operands->extrapolation_policy == TF_EXTRAPOLATION_REFUSE)
    {
        for (int32_t o = 0; o < operands->n_operands; ++o)
        {
            const int32_t first = tf_slot_first(operands, o), n = tf_slot_count(operands, o);
            for (int32_t i = 0; i < n; ++i)
                if (tf_point_index_is_extrapolated(problem, tf_slot_point(operands, first + i)))
                    { *status = TF_EXTRAPOLATED_INDEX; return TF_EXTRAPOLATED_INDEX; }
        }
    }
    else if (operands->extrapolation_policy != TF_EXTRAPOLATION_FLAG)
        { *status = TF_UNKNOWN_EXTRAPOLATION_POLICY; return TF_UNKNOWN_EXTRAPOLATION_POLICY; }

    /* THE TRIAL AXIS, validated here for the reason everything else is: the answer cannot change
     * between one evaluation and the next, so there is one moment to give it. */
    if (problem->n_trials < 0) { *status = TF_BAD_COUNT; return TF_BAD_COUNT; }

    if (operands->trial_reduction != 0)
        for (int32_t o = 0; o < operands->n_operands; ++o)
        {
            const int32_t red = operands->trial_reduction[o];
            if (red != TF_TRIAL_NOMINAL && red != TF_TRIAL_WORST &&
                red != TF_TRIAL_MEAN    && red != TF_TRIAL_FRACTION_PASSING)
                { *status = TF_UNKNOWN_TRIAL_REDUCTION; return TF_UNKNOWN_TRIAL_REDUCTION; }

            /* AN OPERAND THAT REDUCES OVER NO TRIALS is a caller mistake, not an empty average.
             * A mean over zero stacks has no value and a worst case over none has no argument;
             * returning the nominal value would answer a different question in silence. */
            if (red != TF_TRIAL_NOMINAL && problem->n_trials <= 0)
                { *status = TF_NO_TRIAL_DATA; return TF_NO_TRIAL_DATA; }
        }

    /* TRIALS THAT PERTURB NOTHING. Every stack would equal the nominal one, every worst case
     * would be the nominal value and every yield would be exactly zero or one - an expensive way
     * to learn nothing, and far more likely a caller who built the table and forgot to attach it. */
    if (problem->n_trials > 0 && tf_uses_trials(problem, operands) &&
        problem->trial_layer_thickness == 0 && problem->trial_material_index == 0)
        { *status = TF_NO_TRIAL_DATA; return TF_NO_TRIAL_DATA; }

    /* THE BACK SURFACE. A thickness that is negative or not a number is a caller mistake; a
     * reversed configuration with a back surface would have light enter through the substrate's
     * uncoated face, which the sum in tf_internal.h does not describe, so it refuses. */
    if (!(problem->substrate_thickness >= 0.0) || isinf(problem->substrate_thickness))
        { *status = TF_BAD_COUNT; return TF_BAD_COUNT; }
    if (tf_has_back(problem))
        for (int32_t i = 0; i < problem->n_points; ++i)
            if (tf_reversed_at(problem, i))
            {
                TF_NOT_IMPLEMENTED(status, TF_NOT_IMPLEMENTED_REVERSED,
                    "a substrate back surface with a reversed configuration: light entering "
                    "through the uncoated face is not described by the incoherent sum.");
                return TF_NOT_IMPLEMENTED_REVERSED;
            }

    tf_plan_report rep;
    rep.n_structured = 0; rep.n_general = 0; rep.n_incoherent = 0; rep.n_unsupported = 0;
    rep.n_variables_inert = 0; rep.max_clamped_layers = 0; rep.n_points_one_polarisation = 0;
    rep.max_operand_span = 1;
    for (int32_t o = 0; o < operands->n_operands; ++o)
    {
        const int32_t c = tf_slot_count(operands, o);
        if (c > rep.max_operand_span) rep.max_operand_span = c;
    }
    rep.scratch_doubles = 0;

    /* Pass one: a verdict per point. */
    for (int32_t i = 0; i < problem->n_points; ++i)
    {
        tf_point_verdict v;
        tf_verdict_for_point(problem, i, &v);

        /* THE PATH MUST BE VALID FOR EVERY TRIAL, NOT ONLY THE NOMINAL STACK.
         *
         * A perturbed layer is thicker or its index is different, so a stack that is lossless and
         * unclamped nominally can need the general path, or clamp, on trial four hundred. The
         * plan chooses ONE path per point - point_path is [n_points] and deliberately does not
         * grow with trials - so the one it chooses has to be the most general any trial needs.
         * The path codes are ordered by generality, so a maximum is the merge.
         *
         * Being conservative costs a trial that did not need it the general path, which is
         * slower and never wrong. Choosing per trial instead would mean a point_path per trial,
         * and that is the buffer-multiplying design this axis exists to avoid.
         *
         * This walks every trial at PLAN time. That is n_points * n_trials verdicts, paid once,
         * against n_points * n_trials full stack solves paid on every evaluation - so it is a
         * rounding error on the thing it protects. */
        for (int32_t t = 0; t < problem->n_trials; ++t)
        {
            tf_problem shadow;
            tf_point_verdict vt;
            tf_problem_for_trial(problem, t, &shadow);
            tf_verdict_for_point(&shadow, i, &vt);
            if (vt.path > v.path) v.path = vt.path;
            if (vt.clamped_layers > v.clamped_layers) v.clamped_layers = vt.clamped_layers;
            if (vt.any_layer_live) v.any_layer_live = 1;
        }

        plan->point_path[i] = v.path;
        if (v.clamped_layers > rep.max_clamped_layers) rep.max_clamped_layers = v.clamped_layers;

        switch (v.path)
        {
            case TF_PATH_STRUCTURED: ++rep.n_structured; break;
            case TF_PATH_GENERAL:    ++rep.n_general; break;
            case TF_PATH_INCOHERENT: ++rep.n_incoherent; ++rep.n_unsupported; break;
            default:                 ++rep.n_unsupported; break;
        }
    }

    /* Each clamped layer multiplies the accumulated product by about exp(24); beyond thirty of
     * them a double overflows. Refusing here is the difference between an error and an infinity
     * appearing somewhere downstream with nothing to say where it came from. */
    if (rep.max_clamped_layers > 30)
    {
        *plan->report = rep;
        TF_NOT_IMPLEMENTED(status, TF_TOO_MANY_CLAMPED_LAYERS,
            "more opaque layers in one stack than the accumulated product can hold. "
            "Stack truncation at the first opaque layer is the fix and is not written yet.");
        return TF_TOO_MANY_CLAMPED_LAYERS;
    }

    /* Pass two: order the points by path.
     *
     * Counting sort over four buckets - stable, deterministic, allocation-free, and it ports to a
     * kernel unchanged. A comparison sort would need scratch and a comparator, and would buy
     * nothing over four buckets. */
    {
        /* TF_FIXED_OK: four is the number of tf_path codes, a property of the enum and not of
         * the problem. C0.2 forbids arrays sized by a PROBLEM dimension - layers, points,
         * wavelengths - which is the thing that silently caps what the engine can accept. This
         * one cannot grow with any input, and tf_plan asserts the enum has not outgrown it. */
        int32_t bucket_start[4];   /* TF_FIXED_OK: bounded by the enum, not by the problem */
        int32_t count[4] = { 0, 0, 0, 0 };
        for (int32_t i = 0; i < problem->n_points; ++i) ++count[plan->point_path[i] & 3];

        int32_t running = 0;
        for (int b = 0; b < 4; ++b) { bucket_start[b] = running; running += count[b]; }

        for (int32_t i = 0; i < problem->n_points; ++i)
            plan->point_order[bucket_start[plan->point_path[i] & 3]++] = i;
    }

    /* Pass three: which variables are inert?
     *
     * A layer-thickness variable is inert when the layer is clamped opaque at EVERY point that a
     * residual actually looks at. Points nobody asks about do not count - a variable is not alive
     * because of a wavelength that appears in no target. */
    for (int32_t v = 0; v < variables->n_variables; ++v)
    {
        plan->variable_inert[v] = 0;

        if (variables->kind[v] != TF_VARIABLE_LAYER_THICKNESS) continue;

        const int32_t j = variables->layer[v];
        int live_somewhere = 0;

        /* A STACK OPERAND MAKES EVERY THICKNESS LIVE. TOTT counts a layer that no light reaches,
         * and a clamped opaque layer still costs deposition time - so a variable that is inert
         * optically is not inert at all once the design is being scored on its thickness. Missing
         * this would report a variable inert and invite a caller to drop the one that controls
         * the cost. */
        for (int32_t o = 0; o < operands->n_operands && !live_somewhere; ++o)
            if (tf_is_stack(operands, o) &&
                tf_stack_thickness_slope(problem, operands->code[o],
                                         tf_sharpness_of(operands, o), j) != 0.0)
                live_somewhere = 1;

        /* EVERY POINT ANY OPERAND READS, not one per operand. A variable is live if it still
         * moves the answer ANYWHERE the design is being scored, and with spans an operand reads
         * many points - so this walks slots. It also has to, because `point` is NULL in span
         * mode and this loop used to dereference it. */
        for (int32_t s = 0; s < tf_n_slots(operands) && !live_somewhere; ++s)
        {
            const int32_t i  = tf_slot_point(operands, s);
            const int32_t wi = problem->point_wavelength[i];
            const double  wv = problem->wavelength[wi];

            const tf_complex n0 = tf_index_internal(tf_incident_at(problem, i));
            const double s0 = sin(problem->point_angle[i]);
            const tf_complex n0_sin = tf_c_scale(n0, s0);
            const tf_complex n0_sin_sq = tf_c_mul(n0_sin, n0_sin);

            const int32_t m = problem->layer_material[j];
            const tf_complex n = tf_index_internal(problem->material_index[(int64_t)m * problem->n_wavelengths + wi]);
            const tf_complex kappa = tf_layer_kappa(n, n0_sin_sq);

            if (!tf_layer_would_clamp(kappa, problem->layer_thickness[j], wv)) live_somewhere = 1;
        }

        /* AND ON EVERY TRIAL, because a layer opaque on the nominal stack need not be opaque on a
         * perturbed one - a thinner draw, or a lower k, and the light gets through. Calling that
         * variable inert would zero a column of the Jacobian that is not zero, and the optimiser
         * would be told the layer cannot help when on most of the trials it can.
         *
         * Reached only when the nominal stack already said inert, which is rare, so the loop that
         * would be expensive is the one that almost never runs. */
        for (int32_t t = 0; t < problem->n_trials && !live_somewhere; ++t)
        {
            tf_problem shadow;
            tf_problem_for_trial(problem, t, &shadow);

            for (int32_t s = 0; s < tf_n_slots(operands) && !live_somewhere; ++s)
            {
                const int32_t i  = tf_slot_point(operands, s);
                const int32_t wi = shadow.point_wavelength[i];
                const double  wv = shadow.wavelength[wi];

                const tf_complex n0 = tf_index_internal(tf_incident_at(&shadow, i));
                const double s0 = sin(shadow.point_angle[i]);
                const tf_complex n0_sin = tf_c_scale(n0, s0);
                const tf_complex n0_sin_sq = tf_c_mul(n0_sin, n0_sin);

                const int32_t m = shadow.layer_material[j];
                const tf_complex n = tf_index_internal(
                    shadow.material_index[(int64_t)m * shadow.n_wavelengths + wi]);
                const tf_complex kappa = tf_layer_kappa(n, n0_sin_sq);

                if (!tf_layer_would_clamp(kappa, shadow.layer_thickness[j], wv))
                    live_somewhere = 1;
            }
        }

        if (!live_somewhere) { plan->variable_inert[v] = 1; ++rep.n_variables_inert; }
    }

    if (variables->n_variables > 0 && rep.n_variables_inert == variables->n_variables)
    {
        rep.scratch_doubles = 0;
        *plan->report = rep;
        *status = TF_NO_LIVE_VARIABLE;
        fprintf(stderr,
                "\n*** THINFILM: nothing to optimise ***\n"
                "    All %d variables are inert - every layer they move is opaque at every point\n"
                "    a residual looks at, so the Jacobian would be identically zero.\n\n",
                (int)variables->n_variables);
        return TF_NO_LIVE_VARIABLE;
    }

    /* THE COMPARISON MACHINERY, validated once here so the evaluators never test it again.
     *
     * Every one of these is a mistake that would otherwise produce a number rather than an error:
     * a scale of zero divides, a negative tolerance widens the error instead of shrinking it, and
     * an inverted range makes every value simultaneously too high and too low. */
    for (int32_t r = 0; r < operands->n_operands; ++r)
    {
        const int32_t cmp = (operands->comparison != 0) ? operands->comparison[r]
                                                        : TF_COMPARE_EQUAL;
        switch (cmp)
        {
            case TF_COMPARE_EQUAL: case TF_COMPARE_LE:
            case TF_COMPARE_GE:    case TF_COMPARE_RANGE:
                break;

            case TF_COMPARE_NONE:
                break;   /* evaluated, not scored: it exists to feed another operand */

            default:
                { *status = TF_UNKNOWN_COMPARISON; return TF_UNKNOWN_COMPARISON; }
        }

        if (cmp == TF_COMPARE_RANGE)
        {
            /* A range has no safe default upper bound, so a NULL here is an error rather than a
             * default - unlike the other three optional arrays. */
            if (operands->target_hi == 0) { *status = TF_NULL_POINTER; return TF_NULL_POINTER; }
            if (!(operands->target_hi[r] >= operands->target[r]))
                { *status = TF_BAD_OPERAND_RANGE; return TF_BAD_OPERAND_RANGE; }
        }

        if (operands->tolerance != 0 && !(operands->tolerance[r] >= 0.0))
            { *status = TF_BAD_OPERAND_RANGE; return TF_BAD_OPERAND_RANGE; }

        if (operands->scale != 0)
        {
            const double s = operands->scale[r];
            if (!(s > 0.0) || !(s < 1.0e308))
                { *status = TF_BAD_OPERAND_SCALE; return TF_BAD_OPERAND_SCALE; }
        }
    }

    /* THE INPUT GRAPH: validate it, level it, order it.
     *
     * Derived by tf_plan for the reason the polarisation mask and the span transpose are: a
     * caller-supplied evaluation order is one more thing that can silently disagree with the graph
     * it describes, and the disagreement would show up as an operand read before it was written.
     * A cycle is an error HERE, at plan time, never a hang in the inner loop. */
    {
        for (int32_t o = 0; o < operands->n_operands; ++o)
        {
            const int32_t ic = tf_input_count(operands, o);
            const int32_t pc = tf_is_span_mode(operands) ? operands->point_count[o] : 1;

            /* A STACK OPERAND READS NEITHER. It must say so by having no inputs, and in span mode
             * no points either - otherwise a table could claim TOTT covers a wavelength, which is
             * a sentence with no meaning. */
            if (tf_code_is_stack(operands->code[o]))
            {
                if (ic != 0 || (tf_is_span_mode(operands) && pc != 0))
                    { *status = TF_OPERAND_INPUT_KIND; return TF_OPERAND_INPUT_KIND; }
                if (tf_reduction_of(operands, o) != TF_REDUCE_SUM)
                    { *status = TF_OPERAND_INPUT_KIND; return TF_OPERAND_INPUT_KIND; }
                continue;
            }

            /* Points or operands, never both, and never neither on a scored operand. */
            if (ic > 0 && tf_has_derived(operands) && tf_is_span_mode(operands) && pc > 0)
                { *status = TF_OPERAND_INPUT_KIND; return TF_OPERAND_INPUT_KIND; }
            if (ic > 0 && !tf_code_is_derived(operands->code[o]))
                { *status = TF_OPERAND_INPUT_KIND; return TF_OPERAND_INPUT_KIND; }
            if (ic == 0 && tf_code_is_derived(operands->code[o]))
                { *status = TF_OPERAND_INPUT_KIND; return TF_OPERAND_INPUT_KIND; }

            if (ic > 0)
            {
                const int32_t f = tf_input_first(operands, o);
                if (f < 0 || (int64_t)f + ic > operands->n_input)
                    { *status = TF_INDEX_OUT_OF_RANGE; return TF_INDEX_OUT_OF_RANGE; }
                for (int32_t i = 0; i < ic; ++i)
                {
                    const int32_t src = operands->input_index[f + i];
                    if (src < 0 || src >= operands->n_operands)
                        { *status = TF_INDEX_OUT_OF_RANGE; return TF_INDEX_OUT_OF_RANGE; }
                    /* A self-reference is the shortest possible cycle and is worth its own early
                     * exit, because the levelling pass below would otherwise report it as an
                     * unresolvable graph without saying why. */
                    if (src == o) { *status = TF_OPERAND_CYCLE; return TF_OPERAND_CYCLE; }
                }
            }
        }

        /* LEVELS, by repeated relaxation. An operand with no inputs is level 0; any other is one
         * deeper than its deepest input. A full pass that settles nothing while operands remain
         * unsettled means a cycle - which is the whole detection, and needs no colour marks, no
         * recursion and no stack. */
        for (int32_t o = 0; o < operands->n_operands; ++o) plan->operand_level[o] = -1;

        int32_t settled = 0, guard = 0;
        while (settled < operands->n_operands)
        {
            int32_t progress = 0;
            for (int32_t o = 0; o < operands->n_operands; ++o)
            {
                if (plan->operand_level[o] >= 0) continue;
                const int32_t f = tf_input_first(operands, o), ic = tf_input_count(operands, o);

                int32_t deepest = -1, ready = 1;
                for (int32_t i = 0; i < ic && ready; ++i)
                {
                    const int32_t lv = plan->operand_level[operands->input_index[f + i]];
                    if (lv < 0) ready = 0; else if (lv > deepest) deepest = lv;
                }
                if (!ready) continue;

                plan->operand_level[o] = deepest + 1;
                ++settled; ++progress;
            }
            if (progress == 0) { *status = TF_OPERAND_CYCLE; return TF_OPERAND_CYCLE; }
            if (++guard > operands->n_operands + 1)
                { *status = TF_OPERAND_CYCLE; return TF_OPERAND_CYCLE; }
        }

        rep.n_operand_levels = 1;
        for (int32_t o = 0; o < operands->n_operands; ++o)
            if (plan->operand_level[o] + 1 > rep.n_operand_levels)
                rep.n_operand_levels = plan->operand_level[o] + 1;

        /* Order by level - a counting sort, stable, allocation-free, the same shape as
         * point_order and the span transpose. Evaluating in this order means every input is
         * already written when the operand that reads it runs. */
        {
            int32_t at = 0;
            for (int32_t lv = 0; lv < rep.n_operand_levels; ++lv)
                for (int32_t o = 0; o < operands->n_operands; ++o)
                    if (plan->operand_level[o] == lv) plan->operand_order[at++] = o;
        }

        /* WHICH OPERANDS PRODUCE A RESIDUAL. An unscored one exists to feed another; it has a
         * value and no row. */
        rep.n_residuals = 0;
        rep.n_operands_unscored = 0;
        for (int32_t o = 0; o < operands->n_operands; ++o)
        {
            if (tf_is_scored(operands, o)) plan->operand_residual[o] = rep.n_residuals++;
            else { plan->operand_residual[o] = -1; ++rep.n_operands_unscored; }
        }
        if (rep.n_residuals == 0)
        {
            /* The operand-side twin of TF_NO_LIVE_VARIABLE, and it exists for the same reason: an
             * optimisation with nothing to score otherwise presents as instant convergence. */
            *status = TF_NO_SCORED_OPERAND;
            return TF_NO_SCORED_OPERAND;
        }
    }

    /* THE TRANSPOSE OF THE SPANS: for each point, which operand slots read it.
     *
     * Built by counting sort, stable, allocation-free and unchanged by a port to a kernel - the
     * same shape as point_order two passes above. Derived rather than supplied for the reason the
     * polarisation mask is: an inverse index the caller built is one more thing that can disagree
     * with the forward one it describes, and the disagreement would be silent.
     *
     * Without it a point-major sweep would have to ask every operand whether it reads this point,
     * and that product is exactly what a dense evaluation grid makes large. */
    {
        for (int32_t i = 0; i < problem->n_points; ++i) plan->point_span_count[i] = 0;
        /* Counted by walking OPERANDS, not slot indices: in pointwise mode the slot index is
         * the operand index, and a derived operand owns no slot at all - so walking 0..ns would
         * read a point that operand never named. The fill pass below walks operands for the same
         * reason, and the two must agree or the counts and the contents part company. */
        for (int32_t o = 0; o < operands->n_operands; ++o)
        {
            const int32_t f = tf_slot_first(operands, o), c = tf_slot_count(operands, o);
            for (int32_t i = 0; i < c; ++i)
                ++plan->point_span_count[tf_slot_point(operands, f + i)];
        }

        int32_t running = 0;
        for (int32_t i = 0; i < problem->n_points; ++i)
        {
            plan->point_span_first[i] = running;
            running += plan->point_span_count[i];
        }

        /* Fill, walking the operands so that each slot's owner is known without a search. The
         * cursor is point_span_first advanced in place and then restored, which is why the counts
         * are recomputed rather than kept. */
        for (int32_t i = 0; i < problem->n_points; ++i) plan->point_span_count[i] = 0;
        for (int32_t o = 0; o < operands->n_operands; ++o)
        {
            const int32_t f = tf_slot_first(operands, o), c = tf_slot_count(operands, o);
            for (int32_t i = 0; i < c; ++i)
            {
                const int32_t slot = f + i;
                const int32_t pt   = tf_slot_point(operands, slot);
                const int32_t at   = plan->point_span_first[pt] + plan->point_span_count[pt]++;
                plan->point_span_operand[at] = o;
                plan->point_span_slot[at]    = slot;
            }
        }
    }

    /* THE POLARISATION MASK, derived from the operands rather than taken from the caller.
     *
     * A point is solved for s only if nothing asked about p there, and vice versa. On a design
     * specified separately in s and p at oblique incidence - the common case - that halves the
     * work at every point. The mask cannot disagree with the operands because it is computed FROM
     * them, which is the reason it is an output of the plan and not an input to it.
     *
     * A target code with no polarisation entry is refused HERE, at plan time, rather than leaving
     * a point that needs nothing to be solved for nothing and report a NaN. */
    for (int32_t i = 0; i < problem->n_points; ++i) plan->point_polarisation[i] = 0;

    for (int32_t o = 0; o < operands->n_operands; ++o)
    {
        /* A DERIVED OPERAND READS NO POINTS, so it needs no polarisation of its own - whatever its
         * inputs need is already recorded against the points THEY read. Asking it would refuse a
         * perfectly good table, because a ratio has no entry in tf_target_polarisations and never
         * will have one. */
        /* Neither a derived operand nor a stack operand reads a point, so neither needs a
         * polarisation - and neither has an entry in tf_target_polarisations, nor ever will. */
        if (tf_is_derived(operands, o) || tf_is_stack(operands, o)) continue;

        const int need = tf_target_polarisations(operands->code[o]);
        if (need == 0)
        {
            TF_NOT_IMPLEMENTED(status, TF_NOT_IMPLEMENTED_TARGET,
                "a target code with no polarisation entry. Every target must say which of s and p "
                "it is built from, beside tf_target_value in tf_evaluate.cpp.");
            return TF_NOT_IMPLEMENTED_TARGET;
        }
        /* EVERY point the operand spans needs it, not just the first. */
        const int32_t f = tf_slot_first(operands, o), c = tf_slot_count(operands, o);
        for (int32_t i = 0; i < c; ++i)
            plan->point_polarisation[tf_slot_point(operands, f + i)] |= need;
    }

    /* A point no residual mentions is solved for nothing at all - the evaluators skip it. */
    rep.n_points_one_polarisation = 0;
    for (int32_t i = 0; i < problem->n_points; ++i)
        if (plan->point_polarisation[i] == TF_NEED_S || plan->point_polarisation[i] == TF_NEED_P)
            ++rep.n_points_one_polarisation;

    rep.scratch_doubles = tf_scratch_doubles_for(problem->n_layers, problem->n_materials, tf_n_slots(operands),
                                                rep.max_operand_span, operands->n_operands,
                                                variables->n_variables, n_workers,
                                                tf_uses_trials(problem, operands),
                                                tf_uses_dispersion(operands));
    *plan->report = rep;

    /* An unsupported path is not an error in the plan - the plan's job was to find it. It is an
     * error when an evaluator is asked to run one, and the evaluators check
     * report.n_unsupported. Saying so here, loudly, is how the caller learns before it wastes an
     * optimisation run. */
    if (rep.n_unsupported > 0)
    {
        fprintf(stderr,
                "\n*** THINFILM: %d of %d points need a path that is not implemented ***\n"
                "    incoherent (massive layers): %d\n"
                "    Evaluation will refuse. This is a plan result, not a plan failure.\n\n",
                (int)rep.n_unsupported, (int)problem->n_points, (int)rep.n_incoherent);
    }

    return TF_OK;
}

} /* extern "C" */
