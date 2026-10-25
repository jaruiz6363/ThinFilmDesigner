/* tf_internal.h — the seam between the evaluator and the Jacobian.
 *
 * NOT PART OF THE ABI. Nothing here is exported and nothing here is stable; thinfilm.h is the
 * contract and this is an implementation detail shared by two translation units.
 *
 * It exists for one reason: tf_evaluate_jacobian must report the SAME residuals tf_evaluate does.
 * If each had its own copy of "what a target value is" the two would drift, and the drift would
 * show up as an optimiser whose merit disagrees with its own gradient - the hardest kind of bug to
 * see, because both halves look right in isolation. One definition, used twice.
 */

#ifndef TF_INTERNAL_H
#define TF_INTERNAL_H

#include "tf_layer.h"
#include "tf_reduce.h"

#ifdef __cplusplus
extern "C" {
#endif

/* Everything one point needs, for both polarisations. */
typedef struct tf_point_amplitudes
{
    tf_complex r_s, r_p, t_s, t_p;   /* the coated surface alone, always */
    double     R_s, R_p;          /* |r|^2, or the incoherent sum with the substrate's back surface */
    double     T_s, T_p;          /* computed with the real-part rule; see tf_evaluate.cpp. The
                                   * incoherent sum too, when there is a back surface */
} tf_point_amplitudes;



/* ---------------------------------------------------------------------------------------------
 * Configuration accessors — the one place that knows about the two forms.
 *
 * Each takes a POINT, because a point already determines both its wavelength and its
 * configuration, and making the caller unpack that twice is how the two come apart.
 * ------------------------------------------------------------------------------------------- */

TF_HD static inline int tf_has_configs(const tf_problem* p)
{
    return (p->configurations != 0) ? 1 : 0;
}

TF_HD static inline tf_complex tf_incident_at(const tf_problem* p, int32_t point)
{
    const int32_t wi = p->point_wavelength[point];
    if (!tf_has_configs(p)) return p->index_incident[wi];
    return p->configurations->index_incident[
        (int64_t)p->point_configuration[point] * p->n_wavelengths + wi];
}

TF_HD static inline tf_complex tf_exit_at(const tf_problem* p, int32_t point)
{
    const int32_t wi = p->point_wavelength[point];
    if (!tf_has_configs(p)) return p->index_exit[wi];
    return p->configurations->index_exit[
        (int64_t)p->point_configuration[point] * p->n_wavelengths + wi];
}

TF_HD static inline tf_complex tf_dincident_at(const tf_problem* p, int32_t point)
{
    const int32_t wi = p->point_wavelength[point];
    if (!tf_has_configs(p)) return p->dindex_incident_dwavelength[wi];
    return p->configurations->dindex_incident_dwavelength[
        (int64_t)p->point_configuration[point] * p->n_wavelengths + wi];
}

TF_HD static inline tf_complex tf_dexit_at(const tf_problem* p, int32_t point)
{
    const int32_t wi = p->point_wavelength[point];
    if (!tf_has_configs(p)) return p->dindex_exit_dwavelength[wi];
    return p->configurations->dindex_exit_dwavelength[
        (int64_t)p->point_configuration[point] * p->n_wavelengths + wi];
}

TF_HD static inline int tf_reversed_at(const tf_problem* p, int32_t point)
{
    if (!tf_has_configs(p) || p->configurations->reversed == 0) return 0;
    return p->configurations->reversed[p->point_configuration[point]] ? 1 : 0;
}

/* ---------------------------------------------------------------------------------------------
 * The substrate's back surface - see tf_problem.substrate_thickness.
 *
 * The coating is solved as before, against a semi-infinite substrate. What the back surface adds
 * is three numbers per point and polarisation - the back interface's Rb and Tb and the substrate's
 * single-pass transmission a - and one more amplitude from the coating's own matrix: its
 * reflectance seen from inside the substrate, Rf'. The incoherent sum is then
 *
 *     R = Rf + Tf^2 x / (1 - Rf' x)      T = Tf Tb a / (1 - Rf' x)      x = Rb a^2
 *
 * using Tf both ways: transmittance is the same from either side of a reciprocal stack.
 * ------------------------------------------------------------------------------------------- */

TF_HD static inline int tf_has_back(const tf_problem* p)
{
    return (p->substrate_thickness > 0.0) ? 1 : 0;
}

typedef struct tf_back
{
    double Rb, Tb, a;
} tf_back;

TF_HD static inline tf_back tf_back_surface(const tf_problem* p, int32_t point, int pol,
                                            tf_complex n0_sin_sq)
{
    const int32_t wi = p->point_wavelength[point];
    const tf_complex ns = tf_index_internal(tf_exit_at(p, point));
    const tf_complex nb = tf_index_internal(p->index_back != 0 ? p->index_back[wi]
                                                               : tf_incident_at(p, point));
    const tf_complex ks = tf_layer_kappa(ns, n0_sin_sq);
    const tf_complex es = tf_layer_admittance(ns, ks, pol);
    const tf_complex eb = tf_layer_admittance(nb, tf_layer_kappa(nb, n0_sin_sq), pol);
    const tf_complex den = tf_c_add(es, eb);

    tf_back b;
    b.Rb = tf_c_abs2(tf_c_div(tf_c_sub(es, eb), den));
    b.Tb = 4.0 * es.re * eb.re / tf_c_abs2(den);
    /* Im(kappa) from Im(kappa^2) / (2 Re kappa) rather than from the root itself: for a substrate
     * with k ~ 1e-5 the root's imaginary part comes from a difference of two nearly equal numbers
     * and keeps only ten digits, which a millimetre of substrate turns into a visible error. */
    const tf_complex k2 = tf_c_sub(tf_c_mul(ns, ns), n0_sin_sq);
    const double kim = (fabs(ks.re) > fabs(ks.im)) ? k2.im / (2.0 * ks.re) : ks.im;
    b.a  = exp(-2.0 * TF_TWO_PI * fabs(kim) * p->substrate_thickness / p->wavelength[wi]);
    return b;
}

/* The coating's reflection amplitude seen from inside the substrate, from the same matrix M.
 * Every layer matrix has equal diagonal entries, so the product taken in reverse order is M with
 * its diagonal swapped; with the substrate as the incident medium and eta0 as the exit,
 *
 *     B' = m11 + m01 eta0     C' = m10 + m00 eta0     r' = (eta_s B' - C') / (eta_s B' + C')
 *
 * *den receives eta_s B' + C' for the derivative below. */
TF_HD static inline tf_complex tf_reverse_r(tf_matrix2 M, tf_complex eta0, tf_complex etasub,
                                            tf_complex* den)
{
    const tf_complex B = tf_c_add(M.m11, tf_c_mul(M.m01, eta0));
    const tf_complex C = tf_c_add(M.m10, tf_c_mul(M.m00, eta0));
    const tf_complex sB = tf_c_mul(etasub, B);
    *den = tf_c_add(sB, C);
    return tf_c_div(tf_c_sub(sB, C), *den);
}

/* d r' for a change dM in the stack matrix: the same algebra as dr going forwards. */
TF_HD static inline tf_complex tf_reverse_dr(tf_matrix2 dM, tf_complex eta0, tf_complex etasub,
                                             tf_complex rr, tf_complex den)
{
    const tf_complex dB = tf_c_add(dM.m11, tf_c_mul(dM.m01, eta0));
    const tf_complex dC = tf_c_add(dM.m10, tf_c_mul(dM.m00, eta0));
    const tf_complex dD = tf_c_add(tf_c_mul(etasub, dB), dC);
    const tf_complex dN = tf_c_sub(tf_c_mul(etasub, dB), dC);
    return tf_c_div(tf_c_sub(dN, tf_c_mul(rr, dD)), den);
}

/* The incoherent sum and its slopes in the coating's quantities, so that
 *     dR = dRf + R_Tf dTf + R_Rr dRr        dT = T_Tf dTf + T_Rr dRr */
typedef struct tf_incoherent
{
    double R, T;
    double R_Tf, R_Rr, T_Tf, T_Rr;
} tf_incoherent;

TF_HD static inline tf_incoherent tf_incoherent_sum(double Rf, double Tf, double Rr, tf_back b)
{
    const double x   = b.Rb * b.a * b.a;
    const double den = 1.0 - Rr * x;
    tf_incoherent s;
    s.R    = Rf + Tf * Tf * x / den;
    s.T    = Tf * b.Tb * b.a / den;
    s.R_Tf = 2.0 * Tf * x / den;
    s.R_Rr = Tf * Tf * x * x / (den * den);
    s.T_Tf = b.Tb * b.a / den;
    s.T_Rr = Tf * b.Tb * b.a * x / (den * den);
    return s;
}

/* d|z|^2 = 2 Re(conj(z) dz) */
TF_HD static inline double tf_dabs2(tf_complex z, tf_complex dz)
{
    return 2.0 * (z.re * dz.re + z.im * dz.im);
}

/* THE PHYSICAL LAYER AT TRAVERSAL POSITION i.
 *
 * Reversal is an index transform and nothing else - the solver already walks the layer list in an
 * order, so walking it the other way costs nothing. The distinction that matters is that DERIVATIVE
 * RESULTS ARE STORED BY PHYSICAL LAYER while the sweep runs in traversal order: a variable names a
 * layer of the stack, not a position in a traversal, and a design evaluated from both sides has
 * one thickness per layer shared between the two. */
TF_HD static inline int32_t tf_layer_at(const tf_problem* p, int reversed, int32_t i)
{
    return reversed ? (p->n_layers - 1 - i) : i;
}

/* ---------------------------------------------------------------------------------------------
 * Span accessors — the one place that knows about the two modes.
 *
 * A SLOT is one (operand, point) pair. In span mode the slots of operand o are
 * point_first[o] .. +point_count[o], indexing point_index and point_weight. In pointwise mode
 * there is one slot per operand, slot o IS operand o, the point is point[o] and the weight is 1.
 *
 * Every loop below iterates slots, so neither mode appears anywhere else. Getting that wrong once
 * here is a compile-time-visible bug; getting it wrong in five evaluators is five silent ones.
 * ------------------------------------------------------------------------------------------- */

/* ---------------------------------------------------------------------------------------------
 * The trial axis
 *
 * THE WHOLE MECHANISM IS BELOW AND IT IS SIX LINES. A trial is this problem with two pointers
 * moved; everything else in the engine - the solver, the sweeps, the reductions, the DAG - runs
 * against it without knowing anything has changed. That is what keeps the CUDA port a port.
 * ------------------------------------------------------------------------------------------- */

TF_HD static inline int32_t tf_trial_reduction_of(const tf_operands* ops, int32_t o)
{
    return (ops->trial_reduction != 0) ? ops->trial_reduction[o] : TF_TRIAL_NOMINAL;
}

/* Does ANY operand in the table look at the trial axis? A table where none does skips the trial
 * machinery entirely and costs exactly what it cost before trials existed. */
TF_HD static inline int tf_uses_trials(const tf_problem* p, const tf_operands* ops)
{
    if (p->n_trials <= 0 || ops->trial_reduction == 0) return 0;
    for (int32_t o = 0; o < ops->n_operands; ++o)
        if (ops->trial_reduction[o] != TF_TRIAL_NOMINAL) return 1;
    return 0;
}

/* The problem as trial t sees it. A by-value struct copy, which on a GPU is a handful of
 * registers and on a CPU is a memcpy the optimiser removes. */
TF_HD static inline void tf_problem_for_trial(const tf_problem* p, int32_t t, tf_problem* out)
{
    *out = *p;
    if (p->trial_layer_thickness != 0)
        out->layer_thickness = p->trial_layer_thickness + (int64_t)t * p->n_layers;
    if (p->trial_material_index != 0)
        out->material_index = p->trial_material_index
                            + (int64_t)t * p->n_materials * p->n_wavelengths;

    /* A SHADOW IS ONE STACK AND MUST NOT CARRY THE AXIS. If it did, anything handed the shadow
     * could open a second trial loop inside the first. */
    out->n_trials = 0;
    out->trial_layer_thickness = 0;
    out->trial_material_index  = 0;
}

/* The running collapse. `acc` carries the reduction's state, `winner` the trial index a WORST
 * reduction chose, and both are seeded by tf_trial_seed before the first trial.
 *
 * STREAMING, NOT MATERIALISED: no buffer here scales with the trial count, so ten thousand trials
 * cost the same memory as two. The per-trial table is written only when the caller asked for it. */
TF_HD static inline void tf_trial_seed(int32_t reduction, double* acc, double* winner)
{
    *winner = 0.0;
    switch (reduction)
    {
        case TF_TRIAL_WORST: *acc = -1.0; break;   /* no residual seen yet; every |r| beats it */
        default:             *acc =  0.0; break;
    }
}

TF_HD static inline void tf_trial_accumulate(int32_t reduction, double value, double residual,
                                             int32_t t, double* acc, double* winner,
                                             double* worst_value)
{
    switch (reduction)
    {
        case TF_TRIAL_WORST:
        {
            /* THE LARGEST RESIDUAL, not the largest value - see tf_trial_reduction. Strictly
             * greater, so a tie keeps the first winner. */
            const double m = (residual < 0.0) ? -residual : residual;
            if (m > *acc) { *acc = m; *winner = (double)t; *worst_value = value; }
            break;
        }
        case TF_TRIAL_MEAN:             *acc += value; break;
        case TF_TRIAL_FRACTION_PASSING: if (residual == 0.0) *acc += 1.0; break;
        default:                        break;
    }
}

TF_HD static inline double tf_trial_finish(int32_t reduction, double acc, double worst_value,
                                           int32_t n_trials)
{
    switch (reduction)
    {
        case TF_TRIAL_WORST:            return worst_value;
        case TF_TRIAL_MEAN:             return acc / (double)n_trials;
        case TF_TRIAL_FRACTION_PASSING: return acc / (double)n_trials;
        default:                        return worst_value;
    }
}

TF_HD static inline int tf_code_is_stack(int32_t code)

{
    return (code == TF_TARGET_TOTT || code == TF_TARGET_NLAY ||
            code == TF_TARGET_MINT || code == TF_TARGET_MAXT ||
            code == TF_TARGET_STRESS) ? 1 : 0;
}

/* IS THIS POINT RESTING ON A VALUE SOMEBODY MADE UP?
 *
 * Every optical constant the point touches: both media of its configuration, and every material
 * the stack is built from. Reversal does not enter - it permutes the traversal order and the set
 * of materials is the same set either way - and neither does thickness: a zero-thickness layer of
 * a synthesised material contributes nothing to the answer, but a design that is one optimiser
 * step away from making it matter should not switch from flagged to clean and back.
 *
 * O(n_layers) per point, alongside a solve that is already O(n_layers). It is computed rather than
 * cached because a cache would be a buffer, and a buffer here would have to be dimensioned and
 * passed in for a fact that costs less to recompute than to hand over. */
TF_HD static inline int tf_point_index_is_extrapolated(const tf_problem* p, int32_t point)
{
    const int32_t wi = p->point_wavelength[point];

    if (p->material_index_is_extrapolated != 0)
        for (int32_t j = 0; j < p->n_layers; ++j)
            if (p->material_index_is_extrapolated[
                    (int64_t)p->layer_material[j] * p->n_wavelengths + wi]) return 1;

    if (!tf_has_configs(p))
    {
        if (p->index_incident_is_extrapolated != 0 && p->index_incident_is_extrapolated[wi])
            return 1;
        if (p->index_exit_is_extrapolated != 0 && p->index_exit_is_extrapolated[wi]) return 1;
        return 0;
    }
    {
        const int64_t k = (int64_t)p->point_configuration[point] * p->n_wavelengths + wi;
        const tf_configurations* c = p->configurations;
        if (c->index_incident_is_extrapolated != 0 && c->index_incident_is_extrapolated[k])
            return 1;
        if (c->index_exit_is_extrapolated != 0 && c->index_exit_is_extrapolated[k]) return 1;
    }
    return 0;
}


TF_HD static inline int tf_is_span_mode(const tf_operands* ops)
{
    return (ops->point_count != 0) ? 1 : 0;
}

/* The number of (operand, point) pairs in the whole table. */
TF_HD static inline int32_t tf_n_slots(const tf_operands* ops)
{
    return tf_is_span_mode(ops) ? ops->n_span : ops->n_operands;
}

TF_HD static inline int32_t tf_slot_first(const tf_operands* ops, int32_t o)
{
    return tf_is_span_mode(ops) ? ops->point_first[o] : o;
}

TF_HD static inline int32_t tf_slot_count(const tf_operands* ops, int32_t o)
{
    /* A DERIVED OPERAND READS NO POINTS. In pointwise mode every operand would otherwise be given
     * a phantom slot pointing at point[o], and the sweeps would ask a ratio for its reflectance
     * derivative at a point it never mentioned. */
    if (ops->input_count != 0 && ops->input_count[o] > 0) return 0;
    if (tf_code_is_stack(ops->code[o])) return 0;   /* it reads the layer table, not points */
    return tf_is_span_mode(ops) ? ops->point_count[o] : 1;
}

TF_HD static inline int32_t tf_slot_point(const tf_operands* ops, int32_t slot)
{
    return tf_is_span_mode(ops) ? ops->point_index[slot] : ops->point[slot];
}

TF_HD static inline double tf_slot_weight(const tf_operands* ops, int32_t slot)
{
    if (!tf_is_span_mode(ops) || ops->point_weight == 0) return 1.0;
    return ops->point_weight[slot];
}


/* An operand reads points or operands, never both. tf_plan refuses a mixture. */
TF_HD static inline int32_t tf_input_count(const tf_operands* ops, int32_t o)
{
    return (ops->input_count != 0) ? ops->input_count[o] : 0;
}

TF_HD static inline int32_t tf_input_first(const tf_operands* ops, int32_t o)
{
    return (ops->input_count != 0) ? ops->input_first[o] : 0;
}

TF_HD static inline int tf_is_derived(const tf_operands* ops, int32_t o)
{
    return (tf_input_count(ops, o) > 0) ? 1 : 0;
}

TF_HD static inline int32_t tf_comparison_of(const tf_operands* ops, int32_t o)
{
    return (ops->comparison != 0) ? ops->comparison[o] : TF_COMPARE_EQUAL;
}

TF_HD static inline int tf_is_scored(const tf_operands* ops, int32_t o)
{
    return (tf_comparison_of(ops, o) != TF_COMPARE_NONE) ? 1 : 0;
}

/* Does any operand in the table read another? A flat table skips the DAG machinery entirely. */
TF_HD static inline int tf_has_derived(const tf_operands* ops)
{
    return (ops->input_count != 0) ? 1 : 0;
}

TF_HD static inline int32_t tf_reduction_of(const tf_operands* ops, int32_t o)
{
    return (ops->reduction != 0) ? ops->reduction[o] : TF_REDUCE_SUM;
}

/* reduction_param[1]: the crossing hint for a CROSSING, the OD ceiling for an optical-density
 * code, unused otherwise. One slot, two meanings, and they cannot collide because no operand is
 * both - but the header says which is which per code, because "it depends" in a struct comment is
 * how a field ends up meaning three things. */
TF_HD static inline double tf_param1_of(const tf_operands* ops, int32_t o)
{
    return (ops->reduction_param != 0) ? ops->reduction_param[(int64_t)o * 2 + 1] : 0.0;
}

TF_HD static inline double tf_sharpness_of(const tf_operands* ops, int32_t o)
{
    return (ops->reduction_param != 0) ? ops->reduction_param[(int64_t)o * 2] : 0.0;
}

/* ---------------------------------------------------------------------------------------------
 * The Phase 1 quantities, each with its derivative next to it.
 *
 * Every one is a two-line hand derivative over r, t and T, which is the reason the optics half of
 * the analytic/automatic seam stays analytic: there is nothing here automatic differentiation
 * would do better, and it would do it once per layer per interface instead of once.
 * ------------------------------------------------------------------------------------------- */

/* A = 1 - R - T, for a stack that does not scatter. See TF_TARGET_AS for why it is its own code. */
TF_HD static inline double tf_absorptance(const tf_point_amplitudes* a, int pol)
{
    return (pol == TF_POL_S) ? (1.0 - a->R_s - a->T_s)
                             : (1.0 - a->R_p - a->T_p);
}

/* THE OPTICAL-DENSITY FLOOR, derived rather than chosen.
 *
 * R and T are computed as ratios of numbers of order one, so a transmittance below about double
 * epsilon carries no significant digits at all - it is rounding, not physics. That puts the
 * arithmetic limit at OD 16, and this is the default.
 *
 * IT IS NOT THE RIGHT ANSWER FOR A DESIGN, and the specification says so: OD 8 is the sensible
 * default there, because below T = 1e-8 scatter and pinholes dominate a real filter and the
 * coherent model stops describing it. That is a statement about films, not about doubles, and
 * this engine has no business making it - so the caller sets it in reduction_param[1] and the
 * default here only guarantees the arithmetic stays finite. */
#define TF_OD_FLOOR_DEFAULT 16.0

TF_HD static inline double tf_optical_density(double T, double floor_od)
{
    const double cap = (floor_od > 0.0) ? floor_od : TF_OD_FLOOR_DEFAULT;
    const double tmin = pow(10.0, -cap);
    if (!(T > tmin)) return cap;                 /* clamped, and NaN lands here too */
    return -log10(T);
}

/* d(OD)/dT = -1/(T ln10), and exactly zero above the clamp - where the value has stopped
 * depending on the design, as at the absorption clamp. */
TF_HD static inline double tf_optical_density_slope(double T, double floor_od)
{
    const double cap = (floor_od > 0.0) ? floor_od : TF_OD_FLOOR_DEFAULT;
    const double tmin = pow(10.0, -cap);
    if (!(T > tmin)) return 0.0;
    return -1.0 / (T * 2.302585092994045684017991454684);
}

/* An angle placed in the interval the caller asked for. Shared by Delta and by the absolute
 * phases, so a project that reports one in [0, 2pi) reports them all that way. */
TF_HD static inline double tf_angle_in(double a, int32_t convention)
{
    if (convention == TF_DELTA_POSITIVE && a < 0.0) a += 6.283185307179586476925286766559;
    return a;
}

/* 10 log10(s/p), refusing rather than returning an infinity when either side has vanished. */
TF_HD static inline int tf_decibel_ratio(double s, double p, double* out)
{
    if (!(s > 0.0) || !(p > 0.0)) return 0;
    *out = 10.0 * log10(s / p);
    return 1;
}

/* (p - s)/(p + s), bounded in [-1, 1] wherever anything gets through at all. */
TF_HD static inline int tf_diattenuation(double s, double p, double* out)
{
    const double sum = s + p;
    if (!(sum > 0.0)) return 0;
    *out = (p - s) / sum;
    return 1;
}


/* ---------------------------------------------------------------------------------------------
 * Stack operands — the third kind, which read the layer table and no points at all.
 * ------------------------------------------------------------------------------------------- */

TF_HD static inline int tf_is_stack(const tf_operands* ops, int32_t o)
{
    return tf_code_is_stack(ops->code[o]);
}

/* The value, straight off the layer table. */
TF_HD static inline int tf_stack_value(const tf_problem* p, int32_t code, double threshold,
                                       double* out)
{
    double v = 0.0;
    switch (code)
    {
        case TF_TARGET_TOTT:
            for (int32_t j = 0; j < p->n_layers; ++j) v += p->layer_thickness[j];
            break;
        case TF_TARGET_NLAY:
            v = (double)p->n_layers;
            break;
        case TF_TARGET_MINT:
            for (int32_t j = 0; j < p->n_layers; ++j)
                if (p->layer_thickness[j] < threshold) v += threshold - p->layer_thickness[j];
            break;
        case TF_TARGET_MAXT:
            for (int32_t j = 0; j < p->n_layers; ++j)
                if (p->layer_thickness[j] > threshold) v += p->layer_thickness[j] - threshold;
            break;
        case TF_TARGET_STRESS:
            /* TRI-STATE, AND THE THIRD STATE IS THE POINT. Returning 0 here would report this as
             * an unrecognised code, which is what a caller who mistyped a code deserves and is
             * exactly the wrong thing to tell one who simply has no stress table. */
            if (p->material_stress == 0) return -1;
            for (int32_t j = 0; j < p->n_layers; ++j)
                v += p->material_stress[p->layer_material[j]] * p->layer_thickness[j];
            break;
        default:
            return 0;

    }
    *out = v;
    return 1;
}

/* d(value)/d(thickness of layer j). Every one of these is a constant or a hinge, so there is no
 * chain rule to run - which is why a stack operand's Jacobian row is filled directly rather than
 * accumulated through the point sweeps it takes no part in.
 *
 * ON THE HINGE BOUNDARY the slope is ZERO, the satisfied side - the same choice made for a
 * one-sided comparison sitting exactly on its limit, and for the same reason. */
TF_HD static inline double tf_stack_thickness_slope(const tf_problem* p, int32_t code,
                                                    double threshold, int32_t j)
{
    switch (code)
    {
        case TF_TARGET_TOTT: return 1.0;
        case TF_TARGET_NLAY: return 0.0;
        case TF_TARGET_MINT: return (p->layer_thickness[j] < threshold) ? -1.0 : 0.0;
        case TF_TARGET_MAXT: return (p->layer_thickness[j] > threshold) ?  1.0 : 0.0;
        /* Exact, and NOT 1: the gradient tells the optimiser that a nanometre of a high-stress
         * material costs more than a nanometre of a low-stress one, which is the whole reason a
         * designer scores stress instead of reading it afterwards. */
        case TF_TARGET_STRESS: return (p->material_stress != 0)
                                    ? p->material_stress[p->layer_material[j]] : 0.0;
        default:             return 0.0;

    }
}

/* d(value)/d(eps) for a needle insertion. Independent of DEPTH - these operands do not care where
 * in the stack a nanometre sits - but NOT independent of what the needle is made of, which is why
 * the candidate material is an argument. TOTT charges the same for a nanometre of anything;
 * STRESS charges sigma_m, so the map can rank two candidates at one depth.

 *
 * ONLY TOTT CONTRIBUTES, AND MINT DELIBERATELY DOES NOT. That is the opposite of what it looks
 * like, so here is the reasoning - it was written the other way round first and the test said so.
 *
 * Inserting eps of material adds max(0, t_min - eps) to MINT. At eps = 0 that is the WHOLE of
 * t_min - the layer is born in full violation - and it then DECREASES as eps grows. So MINT's
 * slope at the insertion point is MINUS one, and feeding that into the map would lower every
 * depth by a constant and make every insertion look CHEAPER. The exact opposite of the operand's
 * purpose.
 *
 * The discouragement MINT provides is a STEP, not a slope: the merit jumps the instant the layer
 * exists. A first-order map cannot see a step, and letting the slope stand in for it would be a
 * plausible number pointing the wrong way. So MINT contributes nothing here, and the step belongs
 * to the discrete stage that decides whether to accept an insertion - the stage that can weigh a
 * jump.
 *
 * TOTT has no step: adding eps adds exactly eps, so its slope of one is the whole story, and it
 * makes an insertion more expensive whenever the design is already over its thickness budget.
 * MAXT is not violated by a layer of zero thickness, so its slope there is genuinely zero. NLAY
 * jumps by one and has no continuous derivative at all. */
TF_HD static inline double tf_stack_insertion_slope(const tf_problem* p, int32_t code,
                                                    double threshold, int32_t candidate_material)
{
    (void)threshold;
    switch (code)
    {
        case TF_TARGET_TOTT: return 1.0;
        /* Continuous like TOTT and with no step of its own: a layer of zero thickness carries
         * zero stress, so nothing jumps when it is born. The needle map can therefore say
         * honestly that the same depth costs less in the low-stress material. */
        case TF_TARGET_STRESS:
            return (p->material_stress != 0) ? p->material_stress[candidate_material] : 0.0;
        case TF_TARGET_MINT:   /* a step, not a slope - see above */
        case TF_TARGET_NLAY:
        case TF_TARGET_MAXT:
        default:             return 0.0;
    }
}


/* Solve one point on the path the plan chose. Defined in tf_evaluate.cpp. */
void tf_amplitudes(const tf_problem* p, int32_t point, int32_t path, int32_t need,
                   tf_point_amplitudes* a);

/* Which polarisations a target code is built from: TF_NEED_S, TF_NEED_P, or both. Defined beside
 * tf_target_value on purpose - a new target that forgets its mask is then visibly wrong in the
 * same file, not three modules away. Returns 0 for a code it does not know. */
int tf_target_polarisations(int32_t code);

/* The value of one target from a solved point. Returns 0 for a code it does not know, which the
 * caller must turn into TF_NOT_IMPLEMENTED_TARGET rather than a zero.
 *
 * delta_convention is tf_operands.delta_convention and reaches only the two Delta operands. */
int tf_target_value(int32_t code, const tf_point_amplitudes* a, int32_t delta_convention,
                    double floor_od, double* out);

/* THE SCORE OF ONE OPERAND: its residual AND the slope of that residual in the operand's value.
 *
 * ONE FUNCTION RETURNS BOTH ON PURPOSE. With a one-sided comparison the residual and its
 * derivative share a decision - is this constraint active? - and two functions that each decided
 * it separately would eventually disagree at the boundary, where a residual of exactly zero would
 * be paired with a non-zero slope. That is a gradient pointing away from a satisfied constraint,
 * and nothing downstream could detect it.
 *
 *     residual         = sqrt(weight) * e / scale
 *     dresidual_dvalue = sqrt(weight) * de/dvalue / scale
 *
 * The second is what every derivative entry point multiplies its d(value)/dx by. For an EQUAL
 * operand with no tolerance it is exactly sqrt(weight), which is what the code did before
 * comparisons existed.
 */
typedef struct tf_score
{
    double residual;
    double dresidual_dvalue;
} tf_score;

tf_score tf_score_of(const tf_operands* ops, int32_t o, double value);

/* merit = sqrt( sum residual^2 / sum weight ), LensHH-LT's weighted RMS. */
double tf_merit_of(const tf_operands* t, const tf_plan_buffers* plan, const double* residual);

/* Solve every point of an operand's span and reduce. x is scratch of at least 3 * slot_count
 * doubles: the point values, the gathered weights, and room for the slopes when none are wanted.
 * Returns 0 for an unrecognised target code. */
int tf_operand_value(const tf_problem* p, const tf_operands* ops, const tf_plan_buffers* plan,
                     int32_t o, double* x, double* dvdx, double* out_value, int32_t* out_flag);

/* A derived operand's value and its slope in each input. Defined beside tf_target_value. */
int tf_derived_value(int32_t code, const double* in, int32_t k, double* dvdin, double* out);
int tf_code_is_derived(int32_t code);

/* Pass A: every operand value and every slot slope, in topological order. Shared by all four
 * derivative entry points - see tf_evaluate.cpp. */
int tf_operand_pass(const tf_problem* p, const tf_operands* ops, const tf_plan_buffers* plan,
                    double* x, double* dvdx, double* value, double* slot_slope,
                    int32_t* flag, int32_t* n_flagged);

/* The reverse pass: d(sum r^2)/d(value_o) for every operand. For an entry point whose answer is
 * one scalar rather than a row per variable. */
int tf_operand_adjoint(const tf_operands* ops, const tf_plan_buffers* plan,
                       const double* value, const double* residual, double* adjoint);

/* Is this target code an angle with a branch cut? */
int tf_is_delta_target(int32_t code);

/* The chain rule from amplitude derivatives to a target-value derivative. Defined in
 * tf_jacobian.cpp and shared with the wavelength derivative, because THE CHAIN RULE DOES NOT CARE
 * WHAT WAS PERTURBED. d(Rs) = 2 Re(conj(r_s) d r_s) whether d r_s came from a thickness or from a
 * wavelength. Two copies of it would be two places for a target to be added to only one. */
int tf_target_derivative(int32_t code, const tf_point_amplitudes* a,
                         tf_complex dr_s, tf_complex dr_p,
                         tf_complex dt_s, tf_complex dt_p,
                         double dR_s, double dR_p,
                         double dT_s, double dT_p, double floor_od, double* out);

/* GROUP DELAY, GDD (ABI 11) AND TOD (ABI 12), in tf_dispersion.cpp. A dispersion code's value at a
 * point; every layer's derivative of the six readings (GD, GDD, TOD on r, then on t) at a point and
 * polarisation, into d[TF_DISPERSION_SLOTS * layer + slot], with tf_dispersion_scratch(n_layers)
 * doubles of which the prefix matrices take all but the last TF_DISPERSION_SLOTS n; the slope a code
 * reads from that; and the data check the entry points run first. */
#define TF_DISPERSION_SLOTS 6
int       tf_is_dispersion_code(int32_t code);
int       tf_dispersion_polarisation(int32_t code);
int       tf_dispersion_value(const tf_problem* p, int32_t point, int32_t code, double* out);
void      tf_dispersion_sweep(const tf_problem* p, int32_t point, int pol, void* prefix_scratch, double* d);
double    tf_dispersion_slope(const double* d, int32_t layer, int32_t code);
int64_t   tf_dispersion_scratch(int32_t n_layers);
int       tf_uses_dispersion(const tf_operands* ops);
tf_status tf_dispersion_check(const tf_problem* p, const tf_operands* ops, int32_t* status);
void      tf_dispersion_needle(const tf_problem* p, const tf_operands* ops, const tf_plan_buffers* plan,
                               const tf_needle_request* req, const double* dres, int32_t point, int pol,
                               void* scratch, double* map);

#ifdef __cplusplus
} /* extern "C" */
#endif

#endif /* TF_INTERNAL_H */
