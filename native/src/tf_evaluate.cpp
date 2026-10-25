/* tf_evaluate.cpp — residuals and merit, no derivatives.
 *
 * ADMITTANCE FORMULATION, Macleod's. The stack matrix M is the ordered product of the layer
 * characteristic matrices, and the stack is summarised by
 *
 *     [ B ]        [   1     ]
 *     [   ]  =  M  [         ]          Y = C / B is the input admittance
 *     [ C ]        [ eta_sub ]
 *
 *     r = (eta0 B - C) / (eta0 B + C)
 *     t = 2 eta0 / (eta0 B + C)
 *
 *     R = |r|^2
 *     T = 4 eta0 Re(eta_sub) / |eta0 B + C|^2
 *
 * T IS WRITTEN IN THAT FORM ON PURPOSE rather than as (eta_sub/eta0)|t|^2. The two agree for a
 * transparent substrate and the second is wrong for an absorbing one, because the transmitted
 * power depends on the REAL part of the substrate admittance. The difference is invisible on
 * every test a lossless design would suggest.
 *
 * The incident medium is assumed transparent - eta0 real. An absorbing incident medium makes T
 * ill-defined in this formulation, and the plan refuses it rather than returning a number.
 */

#include "tf_internal.h"

#include <math.h>

extern "C" {

/* ---------------------------------------------------------------------------------------------
 * The conserved transverse quantity, once per point.
 * ------------------------------------------------------------------------------------------- */

static tf_complex tf_n0_sin_squared(const tf_problem* p, int32_t point)
{
    const tf_complex n0 = tf_index_internal(tf_incident_at(p, point));
    const tf_complex n0s = tf_c_scale(n0, sin(p->point_angle[point]));
    return tf_c_mul(n0s, n0s);
}

/* ---------------------------------------------------------------------------------------------
 * General path
 * ------------------------------------------------------------------------------------------- */

static void tf_solve_general(const tf_problem* p, int32_t point, int pol,
                             tf_complex* out_r, tf_complex* out_t, double* out_T,
                             tf_complex* out_rr)
{
    const int32_t wi = p->point_wavelength[point];
    const double  wv = p->wavelength[wi];
    const tf_complex n0_sin_sq = tf_n0_sin_squared(p, point);

    const tf_complex n0   = tf_index_internal(tf_incident_at(p, point));
    const tf_complex nsub = tf_index_internal(tf_exit_at(p, point));

    const tf_complex kappa0   = tf_layer_kappa(n0,   n0_sin_sq);
    const tf_complex kappasub = tf_layer_kappa(nsub, n0_sin_sq);
    const tf_complex eta0     = tf_layer_admittance(n0,   kappa0,   pol);
    const tf_complex etasub   = tf_layer_admittance(nsub, kappasub, pol);

    /* Forward product. No prefix chain is stored: tf_evaluate does not differentiate, so the
     * running product is all it needs and the scratch goes untouched. */
    const int rev = tf_reversed_at(p, point);

    tf_matrix2 M = tf_m2_identity();
    for (int32_t i = 0; i < p->n_layers; ++i)
    {
        const int32_t j = tf_layer_at(p, rev, i);
        const int32_t m = p->layer_material[j];
        const tf_complex n = tf_index_internal(p->material_index[(int64_t)m * p->n_wavelengths + wi]);
        const tf_complex kappa = tf_layer_kappa(n, n0_sin_sq);
        const tf_complex eta   = tf_layer_admittance(n, kappa, pol);

        tf_complex delta = tf_c_scale(kappa, TF_TWO_PI * p->layer_thickness[j] / wv);
        tf_layer_clamp_delta(&delta);

        M = tf_m2_mul(M, tf_layer_matrix(delta, eta));
    }

    const tf_complex B = tf_c_add(M.m00, tf_c_mul(M.m01, etasub));
    const tf_complex C = tf_c_add(M.m10, tf_c_mul(M.m11, etasub));

    const tf_complex e0B  = tf_c_mul(eta0, B);
    const tf_complex den  = tf_c_add(e0B, C);
    const tf_complex num  = tf_c_sub(e0B, C);

    *out_r = tf_c_div(num, den);
    *out_t = tf_c_div(tf_c_scale(eta0, 2.0), den);
    *out_T = 4.0 * eta0.re * etasub.re / tf_c_abs2(den);
    if (out_rr != 0) { tf_complex d; *out_rr = tf_reverse_r(M, eta0, etasub, &d); }
}

/* ---------------------------------------------------------------------------------------------
 * Structured path — every index real and no layer beyond critical, so kappa, eta and delta are
 * all real and the chain carries four doubles per matrix.
 *
 * B and C are still complex: [B; C] = M [1; eta_sub] with M = [[a, ib], [ic, d]] gives
 *
 *     B = a + i (b eta_sub)          C = (d eta_sub) + i c
 *
 * but every component is a real expression, so R and T come out of pure real arithmetic. That is
 * the point of the path, and it is why it is written out rather than promoted and handed to the
 * general routine.
 * ------------------------------------------------------------------------------------------- */

static void tf_solve_structured(const tf_problem* p, int32_t point, int pol,
                                tf_complex* out_r, tf_complex* out_t, double* out_T,
                                tf_complex* out_rr)
{
    const int32_t wi = p->point_wavelength[point];
    const double  wv = p->wavelength[wi];

    const double n0v = tf_incident_at(p, point).re;
    const double s0  = sin(p->point_angle[point]);
    const double n0_sin_sq = (n0v * s0) * (n0v * s0);

    const double nsub = tf_exit_at(p, point).re;

    const double kappa0   = sqrt(n0v * n0v - n0_sin_sq);
    const double kappasub = sqrt(nsub * nsub - n0_sin_sq);
    const double eta0   = (pol == TF_POL_S) ? kappa0   : (n0v  * n0v)  / kappa0;
    const double etasub = (pol == TF_POL_S) ? kappasub : (nsub * nsub) / kappasub;

    const int rev = tf_reversed_at(p, point);

    tf_matrix2s M = tf_m2s_identity();
    for (int32_t i = 0; i < p->n_layers; ++i)
    {
        const int32_t j = tf_layer_at(p, rev, i);
        const int32_t m = p->layer_material[j];
        const double  n = p->material_index[(int64_t)m * p->n_wavelengths + wi].re;
        const double  kappa = sqrt(n * n - n0_sin_sq);
        const double  eta   = (pol == TF_POL_S) ? kappa : (n * n) / kappa;
        const double  delta = TF_TWO_PI * p->layer_thickness[j] * kappa / wv;

        M = tf_m2s_mul(M, tf_layer_matrix_s(delta, eta));
    }

    /* B = a + i(b etasub),  C = (d etasub) + i c */
    const double Br = M.a,            Bi = M.b * etasub;
    const double Cr = M.d * etasub,   Ci = M.c;

    const double denr = eta0 * Br + Cr,  deni = eta0 * Bi + Ci;
    const double numr = eta0 * Br - Cr,  numi = eta0 * Bi - Ci;
    const double den2 = denr * denr + deni * deni;

    *out_r = tf_c_div(tf_c(numr, numi), tf_c(denr, deni));
    *out_t = tf_c_div(tf_c(2.0 * eta0, 0.0), tf_c(denr, deni));
    *out_T = 4.0 * eta0 * etasub / den2;
    if (out_rr != 0)
    {
        tf_complex d;
        *out_rr = tf_reverse_r(tf_m2s_promote(M), tf_c(eta0, 0.0), tf_c(etasub, 0.0), &d);
    }
}

/* ---------------------------------------------------------------------------------------------
 * Targets
 * ------------------------------------------------------------------------------------------- */

/* Which polarisations a target is built from.
 *
 * Kept immediately beside tf_target_value so that a new target code which forgets its mask is
 * visibly wrong in the same screenful rather than three modules away. A code with no entry
 * returns 0, and tf_plan turns that into TF_NOT_IMPLEMENTED_TARGET at plan time - a point that
 * needed nothing would otherwise be solved for nothing and report a NaN. */
int tf_target_polarisations(int32_t code)
{
    switch (code)
    {
        case TF_TARGET_RS:
        case TF_TARGET_TS: return TF_NEED_S;

        case TF_TARGET_RP:
        case TF_TARGET_TP: return TF_NEED_P;

        /* The averages and every ellipsometric quantity are built from the pair. */
        case TF_TARGET_RA:
        case TF_TARGET_TA:
        case TF_TARGET_R_TANPSI:
        case TF_TARGET_T_TANPSI:
        case TF_TARGET_R_COSDELTA:
        case TF_TARGET_T_COSDELTA:
        case TF_TARGET_R_PSI:
        case TF_TARGET_T_PSI:
        case TF_TARGET_R_DELTA:
        case TF_TARGET_T_DELTA: return TF_NEED_S | TF_NEED_P;

        case TF_TARGET_AS:
        case TF_TARGET_ODS:
        case TF_TARGET_R_PHASE_S:
        case TF_TARGET_T_PHASE_S: return TF_NEED_S;

        case TF_TARGET_AP:
        case TF_TARGET_ODP:
        case TF_TARGET_R_PHASE_P:
        case TF_TARGET_T_PHASE_P: return TF_NEED_P;

        case TF_TARGET_AA:
        case TF_TARGET_ODA:
        case TF_TARGET_R_NPOL:
        case TF_TARGET_T_NPOL:
        case TF_TARGET_R_EXT_DB:
        case TF_TARGET_T_EXT_DB:
        case TF_TARGET_R_DIAT:
        case TF_TARGET_T_DIAT:  return TF_NEED_S | TF_NEED_P;

        case TF_TARGET_R_GD_S:
        case TF_TARGET_T_GD_S:
        case TF_TARGET_R_GDD_S:
        case TF_TARGET_T_GDD_S:
        case TF_TARGET_R_TOD_S:
        case TF_TARGET_T_TOD_S: return TF_NEED_S;

        case TF_TARGET_R_GD_P:
        case TF_TARGET_T_GD_P:
        case TF_TARGET_R_GDD_P:
        case TF_TARGET_T_GDD_P:
        case TF_TARGET_R_TOD_P:
        case TF_TARGET_T_TOD_P: return TF_NEED_P;

        default: return 0;
    }
}

void tf_amplitudes(const tf_problem* p, int32_t point, int32_t path, int32_t need,
                   tf_point_amplitudes* a)
{
    /* WHAT WAS NOT ASKED FOR IS LEFT AS A NaN, deliberately, rather than zero.
     *
     * The mask comes from tf_plan and the operands come from the caller, and the whole design rests
     * on those two agreeing. If they ever do not - a point marked s-only whose residual asks for
     * Rp - a zero here would be a plausible reflectance and the error would be invisible. A NaN
     * propagates through every arithmetic operation to the residual and the merit, where it cannot
     * be mistaken for an answer. This costs two stores per point and buys a whole class of silent
     * wrongness. */
    const double nan_ = nan("");
    a->r_s = a->r_p = a->t_s = a->t_p = tf_c(nan_, nan_);
    a->R_s = a->R_p = a->T_s = a->T_p = nan_;

    const int back = tf_has_back(p);
    tf_complex rr_s = tf_c(nan_, nan_), rr_p = rr_s;
    tf_complex* want_s = back ? &rr_s : 0;
    tf_complex* want_p = back ? &rr_p : 0;

    if (path == TF_PATH_STRUCTURED)
    {
        if (need & TF_NEED_S) tf_solve_structured(p, point, TF_POL_S, &a->r_s, &a->t_s, &a->T_s, want_s);
        if (need & TF_NEED_P) tf_solve_structured(p, point, TF_POL_P, &a->r_p, &a->t_p, &a->T_p, want_p);
    }
    else
    {
        if (need & TF_NEED_S) tf_solve_general(p, point, TF_POL_S, &a->r_s, &a->t_s, &a->T_s, want_s);
        if (need & TF_NEED_P) tf_solve_general(p, point, TF_POL_P, &a->r_p, &a->t_p, &a->T_p, want_p);
    }

    a->R_s = tf_c_abs2(a->r_s);
    a->R_p = tf_c_abs2(a->r_p);

    /* The back surface changes R and T and nothing else: the amplitudes stay the coated
     * surface's, which is what Psi, Delta and the phases describe. */
    if (back)
    {
        const tf_complex u = tf_n0_sin_squared(p, point);
        if (need & TF_NEED_S)
        {
            const tf_incoherent s = tf_incoherent_sum(a->R_s, a->T_s, tf_c_abs2(rr_s),
                                                      tf_back_surface(p, point, TF_POL_S, u));
            a->R_s = s.R; a->T_s = s.T;
        }
        if (need & TF_NEED_P)
        {
            const tf_incoherent s = tf_incoherent_sum(a->R_p, a->T_p, tf_c_abs2(rr_p),
                                                      tf_back_surface(p, point, TF_POL_P, u));
            a->R_p = s.R; a->T_p = s.T;
        }
    }
}

/* rho = r_p / r_s for reflection, t_p / t_s for transmission. tan(Psi) = |rho|, Delta = arg(rho). */
static tf_complex tf_rho(const tf_point_amplitudes* a, int transmission)
{
    return transmission ? tf_c_div(a->t_p, a->t_s) : tf_c_div(a->r_p, a->r_s);
}

int tf_is_delta_target(int32_t code)
{
    return (code == TF_TARGET_R_DELTA || code == TF_TARGET_T_DELTA) ? 1 : 0;
}

/* Fold an angle into (-pi, pi].
 *
 * remainder() rather than fmod(): it returns the IEEE nearest-multiple remainder, already in
 * [-pi, pi], with no sign surprise for negative arguments and no cancellation for large ones.
 * Only the closed end needs fixing, and -pi is moved to +pi so the interval matches atan2's. */
static double tf_wrap_pi(double x)
{
    double y = remainder(x, TF_TWO_PI);
    if (y <= -TF_PI) y += TF_TWO_PI;
    return y;
}

int tf_target_value(int32_t code, const tf_point_amplitudes* a, int32_t delta_convention,
                    double floor_od, double* out)
{
    switch (code)
    {
        case TF_TARGET_RS: *out = a->R_s; return 1;
        case TF_TARGET_RP: *out = a->R_p; return 1;
        case TF_TARGET_RA: *out = 0.5 * (a->R_s + a->R_p); return 1;

        case TF_TARGET_TS: *out = a->T_s; return 1;
        case TF_TARGET_TP: *out = a->T_p; return 1;
        case TF_TARGET_TA: *out = 0.5 * (a->T_s + a->T_p); return 1;

        case TF_TARGET_R_TANPSI:   *out = tf_c_abs(tf_rho(a, 0)); return 1;
        case TF_TARGET_T_TANPSI:   *out = tf_c_abs(tf_rho(a, 1)); return 1;
        case TF_TARGET_R_COSDELTA: *out = cos(tf_c_arg(tf_rho(a, 0))); return 1;
        case TF_TARGET_T_COSDELTA: *out = cos(tf_c_arg(tf_rho(a, 1))); return 1;

        case TF_TARGET_R_PSI: *out = atan(tf_c_abs(tf_rho(a, 0))); return 1;
        case TF_TARGET_T_PSI: *out = atan(tf_c_abs(tf_rho(a, 1))); return 1;

        /* The two with a branch cut. The convention is the caller's - see tf_delta_convention in
         * the header for why this is a setting and not a decision taken here. SHORTEST_ARC leaves
         * the VALUE principal and acts on the residual instead, which is where the discontinuity
         * actually does damage; that happens in tf_score_of. */
        case TF_TARGET_R_DELTA:
        case TF_TARGET_T_DELTA:
        {
            const int trans = (code == TF_TARGET_T_DELTA);
            *out = tf_angle_in(tf_c_arg(tf_rho(a, trans)), delta_convention);
            return 1;
        }

        /* ---- Phase 1 additions ------------------------------------------------------------ */

        case TF_TARGET_AS: *out = tf_absorptance(a, TF_POL_S); return 1;
        case TF_TARGET_AP: *out = tf_absorptance(a, TF_POL_P); return 1;
        case TF_TARGET_AA: *out = 0.5 * (tf_absorptance(a, TF_POL_S) +
                                         tf_absorptance(a, TF_POL_P)); return 1;

        case TF_TARGET_ODS: *out = tf_optical_density(a->T_s, floor_od); return 1;
        case TF_TARGET_ODP: *out = tf_optical_density(a->T_p, floor_od); return 1;
        /* ODA IS THE OD OF THE AVERAGE TRANSMITTANCE, not the average of two ODs. The two differ,
         * and only the first is what a blocking specification means: a filter blocking one
         * polarisation and passing the other blocks nothing. Averaging in log space would report
         * that it half-blocks. */
        case TF_TARGET_ODA: *out = tf_optical_density(0.5 * (a->T_s + a->T_p), floor_od); return 1;

        case TF_TARGET_R_PHASE_S: *out = tf_angle_in(tf_c_arg(a->r_s), delta_convention); return 1;
        case TF_TARGET_R_PHASE_P: *out = tf_angle_in(tf_c_arg(a->r_p), delta_convention); return 1;
        case TF_TARGET_T_PHASE_S: *out = tf_angle_in(tf_c_arg(a->t_s), delta_convention); return 1;
        case TF_TARGET_T_PHASE_P: *out = tf_angle_in(tf_c_arg(a->t_p), delta_convention); return 1;

        case TF_TARGET_R_NPOL: *out = fabs(a->R_s - a->R_p); return 1;
        case TF_TARGET_T_NPOL: *out = fabs(a->T_s - a->T_p); return 1;

        case TF_TARGET_R_EXT_DB:
            return tf_decibel_ratio(a->R_s, a->R_p, out);
        case TF_TARGET_T_EXT_DB:
            return tf_decibel_ratio(a->T_s, a->T_p, out);

        case TF_TARGET_R_DIAT:
            return tf_diattenuation(a->R_s, a->R_p, out);
        case TF_TARGET_T_DIAT:
            return tf_diattenuation(a->T_s, a->T_p, out);

        default: return 0;
    }
}

/* THE VALUE OF A DERIVED OPERAND, and its slope in each input.
 *
 * Kept beside tf_target_value and tf_target_derivative for the reason they are kept beside each
 * other: a code added to one and forgotten in the others is a silent wrong answer, and the only
 * defence that works is adjacency.
 *
 * Returns 0 for a code that is not derived, or one handed the wrong number of inputs - tf_plan
 * turns both into an error before this ever runs. */
int tf_derived_value(int32_t code, const double* in, int32_t k, double* dvdin, double* out)
{
    switch (code)
    {
        case TF_TARGET_DIFFERENCE:
            if (k != 2) return 0;
            *out = in[0] - in[1];
            dvdin[0] = 1.0; dvdin[1] = -1.0;
            return 1;

        case TF_TARGET_RATIO:
        {
            if (k != 2) return 0;
            const double d = in[1];
            if (d == 0.0) return 0;      /* tf_plan cannot see this; the caller gets a refusal */
            *out = in[0] / d;
            dvdin[0] = 1.0 / d;
            dvdin[1] = -in[0] / (d * d);
            return 1;
        }

        default:
            return 0;
    }
}

/* Is this code one that reads operands rather than points? Asked by tf_plan so that a mismatch
 * between a code and its inputs is refused once, at plan time. */
int tf_code_is_derived(int32_t code)
{
    return (code == TF_TARGET_DIFFERENCE || code == TF_TARGET_RATIO) ? 1 : 0;
}

/* THE VALUE OF ONE OPERAND: solve every point of its span, then reduce.
 *
 * Returns the reduced value and, when dvdx is given, the local slope at each slot - which is what
 * every derivative entry point multiplies its own dx_p/d(parameter) by. The reduction never learns
 * what the parameter is, which is why one implementation serves thickness, wavelength, index and
 * needle insertion alike.
 *
 * `x` is scratch of at least 4 * k doubles, laid out as [ values | weights | abscissa | slopes ].
 * The fourth block is used only when dvdx is NULL. The abscissa is each point's WAVELENGTH, which
 * only CROSSING reads - a crossing is a wavelength, and locating one needs to know where the
 * samples are, not only what they are.
 */
int tf_operand_value(const tf_problem* p, const tf_operands* ops, const tf_plan_buffers* plan,
                     int32_t o, double* x, double* dvdx, double* out_value, int32_t* out_flag)
{
    const int32_t f = tf_slot_first(ops, o), k = tf_slot_count(ops, o);
    double* w = x + k;
    double* a = x + 2 * (int64_t)k;                 /* the abscissa: each point's wavelength */
    double* d = (dvdx != 0) ? dvdx : (x + 3 * (int64_t)k);

    int synthetic = 0;

    for (int32_t i = 0; i < k; ++i)
    {
        const int32_t pt = tf_slot_point(ops, f + i);
        if (tf_is_dispersion_code(ops->code[o]))
        {
            /* Group delay, GDD and TOD read the stack's wavelength derivatives, not its amplitudes. */
            if (!tf_dispersion_value(p, pt, ops->code[o], &x[i])) return 0;
        }
        else
        {
            tf_point_amplitudes amp;
            tf_amplitudes(p, pt, plan->point_path[pt], plan->point_polarisation[pt], &amp);
            if (!tf_target_value(ops->code[o], &amp, ops->delta_convention, tf_param1_of(ops, o), &x[i])) return 0;
        }
        w[i] = tf_slot_weight(ops, f + i);
        a[i] = p->wavelength[p->point_wavelength[pt]];

        /* ONE TAINTED POINT TAINTS THE OPERAND. A band average over forty samples of which one
         * rests on synthesised constants is not a measured number, and there is no fraction of a
         * flag to set. Costs two null checks when no caller ever marks anything. */
        if (!synthetic) synthetic = tf_point_index_is_extrapolated(p, pt);
    }

    /* A one-point SUM with weight one is the identity, so a pointwise operand comes through this
     * with one extra multiply and its old number intact. */
    {
        double zero[2] = { 0.0, 0.0 };   /* TF_FIXED_OK: two reduction parameters, fixed by the ABI */
        const double* par = (ops->reduction_param != 0)
                          ? &ops->reduction_param[(int64_t)o * 2] : zero;
        if (!tf_reduce(tf_reduction_of(ops, o), par, x, w, a, k, d, out_value, out_flag))
            return 0;
    }
    /* AFTER the reduction, which writes out_flag rather than adding to it. Its flags describe what
     * happened to a crossing inside the spectrum; this one describes the spectrum. */
    if (synthetic && out_flag != 0) *out_flag |= TF_FLAG_SYNTHETIC_INDEX;
    return 1;
}

/* THE ONE DEFINITION OF A SCORE. See tf_internal.h for why the residual and its slope come back
 * together rather than from two functions.
 *
 * The sqrt on the weight is LensHH-LT's convention, established by measuring it: entering weight 2
 * instead of 1 multiplies the reported residual by sqrt(2), not by 2. */
tf_score tf_score_of(const tf_operands* ops, int32_t o, double value)
{
    const int32_t cmp = tf_comparison_of(ops, o);
    const double  tol = (ops->tolerance  != 0) ? ops->tolerance[o]  : 0.0;
    const double  scl = (ops->scale      != 0) ? ops->scale[o]      : 1.0;
    const double  lo  = ops->target[o];

    double e = 0.0;     /* the raw error */
    double de = 0.0;    /* d(raw error) / d(value), before the deadband */

    switch (cmp)
    {
        case TF_COMPARE_EQUAL:
        {
            e = value - lo;
            de = 1.0;

            /* The delta targets are angles, and an angle's error is the shortest arc when the
             * caller has asked for that. The slope is unaffected: wrapping shifts the error by a
             * multiple of 2 pi, and a constant has no slope. */
            if (ops->delta_convention == TF_DELTA_SHORTEST_ARC && tf_is_delta_target(ops->code[o]))
                e = tf_wrap_pi(e);
            break;
        }

        case TF_COMPARE_LE:
            /* Active only above the target. Below it the constraint is met and exerts nothing. */
            if (value > lo) { e = value - lo; de = 1.0; }
            break;

        case TF_COMPARE_GE:
            if (value < lo) { e = lo - value; de = -1.0; }
            break;

        case TF_COMPARE_RANGE:
        {
            const double hi = ops->target_hi[o];   /* tf_plan refuses a NULL with any RANGE */
            if (value > hi)      { e = value - hi; de =  1.0; }
            else if (value < lo) { e = lo - value; de = -1.0; }
            break;
        }

        default:
            break;   /* NONE, and anything unknown, is refused by tf_plan before this runs */
    }

    /* The deadband. e shrinks toward zero by tol and is exactly zero inside it, taking its slope
     * with it - a requirement met to within its tolerance is met. */
    {
        const double mag = fabs(e);
        if (mag <= tol) { e = 0.0; de = 0.0; }
        else            { e = (e > 0.0) ? (mag - tol) : -(mag - tol); }
    }

    tf_score s;
    s.residual         = sqrt(ops->weight[o]) * e  / scl;
    s.dresidual_dvalue = sqrt(ops->weight[o]) * de / scl;
    return s;
}

/* merit = sqrt( sum residual^2 / sum weight ).
 *
 * LensHH-LT's again, and the division matters: doubling every weight leaves the merit unchanged,
 * so a weight expresses relative importance and nothing else. A zero weight sum means nothing is
 * being asked for, and the merit of no requirements is zero rather than a division by it. */
double tf_merit_of(const tf_operands* t, const tf_plan_buffers* plan, const double* residual)
{
    /* THE SUM IS OVER SCORED OPERANDS ONLY. An unscored one has a weight in the table like any
     * other - the caller need not zero it - but it contributes no residual, so counting its
     * weight in the denominator would shrink the merit for a row that is not being asked for. */
    double sum = 0.0, weight = 0.0;
    for (int32_t o = 0; o < t->n_operands; ++o)
    {
        const int32_t r = plan->operand_residual[o];
        if (r < 0) continue;
        sum    += residual[r] * residual[r];
        weight += t->weight[o];
    }
    return (weight > 0.0) ? sqrt(sum / weight) : 0.0;
}

/* PASS A, SHARED BY EVERY DERIVATIVE ENTRY POINT: every operand's value, and the local slope of
 * each point that feeds one.
 *
 * All four derivative calls need exactly this before they can start: the values, because a
 * reduction's local slope depends on where the design currently sits, and the DAG, because a
 * derived operand's inputs must be computed before it is. Having it in one place is the same
 * discipline as tf_score_of and tf_reduce - four copies of a topological walk is four chances for
 * one of them to evaluate an operand before its input.
 *
 *   value       [n_operands]  every operand's value, scored or not
 *   slot_slope  [n_slots]     d(value_o)/d(x_p) for each (operand, point) pair, zero for a
 *                             derived operand, which owns no slots
 *   flag        [n_operands]  may be NULL
 *   x           scratch of 4 * max_span doubles; dvdx of max_span
 *
 * Returns 0 for a target code it cannot evaluate, which the caller turns into a refusal. */
int tf_operand_pass(const tf_problem* p, const tf_operands* ops, const tf_plan_buffers* plan,
                    double* x, double* dvdx, double* value, double* slot_slope,
                    int32_t* flag, int32_t* n_flagged)
{
    for (int32_t k = 0; k < ops->n_operands; ++k)
    {
        const int32_t o = plan->operand_order[k];
        double  v = 0.0;
        int32_t fl = 0;

        if (tf_is_stack(ops, o))
        {
            /* Reads the layer table and no points at all. It owns no slots, so nothing is written
             * to slot_slope for it and the point sweeps never see it - its Jacobian row is filled
             * directly instead. */
            if (tf_stack_value(p, ops->code[o], tf_sharpness_of(ops, o), &v) != 1) return 0;
        }
        else if (tf_is_derived(ops, o))
        {
            const int32_t f = tf_input_first(ops, o), ic = tf_input_count(ops, o);
            double in[2], d[2];   /* TF_FIXED_OK: two, because every derived code takes two */
            if (ic > 2) return 0;
            for (int32_t i = 0; i < ic; ++i) in[i] = value[ops->input_index[f + i]];
            if (!tf_derived_value(ops->code[o], in, ic, d, &v)) return 0;
            /* A DERIVED OPERAND INHERITS ITS INPUTS' FLAGS. AERATIO is the reason: it is a ratio
             * of a solar operand over a thermal one, and the thermal one is precisely the half
             * resting on constants out to 50 um that nobody measured. If the ratio came back
             * clean, the flag would be set on an operand the user never scored and absent from
             * the one they did - which is the silent extrapolation the specification forbids,
             * reintroduced one level up.
             *
             * Only when the caller asked for flags; with no flag array there is nowhere to
             * accumulate, and nothing has asked to be told. */
            if (flag != 0)
                for (int32_t i = 0; i < ic; ++i) fl |= flag[ops->input_index[f + i]];
        }
        else
        {
            if (!tf_operand_value(p, ops, plan, o, x, dvdx, &v, &fl)) return 0;
            /* SLOPES ONLY IF SOMEBODY IS GOING TO USE THEM. The value-only callers - tf_evaluate,
             * and the trial loop inside it - want the operand values and nothing else, and asking
             * them to carry an n_slots buffer to throw away would be a buffer per trial. */
            if (slot_slope != 0 && dvdx != 0)
            {
                const int32_t f = tf_slot_first(ops, o), c = tf_slot_count(ops, o);
                for (int32_t i = 0; i < c; ++i) slot_slope[f + i] = dvdx[i];
            }
        }

        value[o] = v;
        if (flag != 0) flag[o] = fl;
        if (fl != 0 && n_flagged != 0) ++(*n_flagged);
    }
    return 1;
}

/* THE REVERSE PASS, for an entry point whose answer is ONE SCALAR rather than a row per variable.
 *
 * adjoint[o] = d(sum of squared residuals) / d(value_o), accumulated backwards through the DAG:
 * a scored operand seeds 2 r slope, and every derived operand pushes its own adjoint onto its
 * inputs through the local factors. One pass, no storage per depth or per candidate.
 *
 * Forward accumulation is right for the Jacobian, which wants a row for each of many variables;
 * reverse is right here, where many operands feed one number. Both directions of the same chain
 * rule, and which one wins is decided by the shape of the answer rather than by taste. */
int tf_operand_adjoint(const tf_operands* ops, const tf_plan_buffers* plan,
                       const double* value, const double* residual, double* adjoint)
{
    for (int32_t o = 0; o < ops->n_operands; ++o)
    {
        const int32_t row = plan->operand_residual[o];
        adjoint[o] = (row >= 0)
                   ? 2.0 * residual[row] * tf_score_of(ops, o, value[o]).dresidual_dvalue
                   : 0.0;
    }

    for (int32_t k = ops->n_operands - 1; k >= 0; --k)
    {
        const int32_t o = plan->operand_order[k];
        if (!tf_is_derived(ops, o)) continue;

        const int32_t f = tf_input_first(ops, o), ic = tf_input_count(ops, o);
        double in[2], d[2], v = 0.0;   /* TF_FIXED_OK: two, as every derived code takes two */
        if (ic > 2) return 0;
        for (int32_t i = 0; i < ic; ++i) in[i] = value[ops->input_index[f + i]];
        if (!tf_derived_value(ops->code[o], in, ic, d, &v)) return 0;

        for (int32_t i = 0; i < ic; ++i)
            adjoint[ops->input_index[f + i]] += adjoint[o] * d[i];
    }
    return 1;
}

/* ---------------------------------------------------------------------------------------------
 * tf_evaluate
 * ------------------------------------------------------------------------------------------- */

TF_API tf_status tf_evaluate(const tf_problem*      problem,
                             const tf_operands*      operands,
                             const tf_plan_buffers* plan,
                             tf_workspace*          workspace,
                             tf_output*             out)
{
    if (problem == 0 || operands == 0 || plan == 0 || out == 0) return TF_NULL_POINTER;
    if (out->status == 0 || out->residual == 0 || out->merit == 0 || out->value == 0)
        return TF_NULL_POINTER;
    if (plan->point_path == 0 || plan->point_polarisation == 0 || plan->report == 0)
        return TF_PLAN_NOT_RUN;

    int32_t* status = out->status;
    *status = TF_OK;

    if (plan->report->n_unsupported > 0)
    {
        TF_NOT_IMPLEMENTED(status, TF_NOT_IMPLEMENTED_INCOHERENT,
            "the plan found points needing a path that is not implemented - massive layers. "
            "See tf_plan_report.n_incoherent.");
        return TF_NOT_IMPLEMENTED_INCOHERENT;
    }

    {
        const tf_status ds = tf_dispersion_check(problem, operands, status);
        if (ds != TF_OK) return ds;
    }

    /* THE SPAN SCRATCH. tf_evaluate needed none before spans existed - a running product in
     * registers was the whole of it - and needs 3 doubles per slot of the widest operand now.
     * tf_plan sizes for that; the check is here so a caller that passed a stale workspace is
     * refused rather than writing past it. */
    const int64_t widest = plan->report->max_operand_span;
    if (widest > 1)
    {
        if (workspace == 0 || workspace->scratch == 0 || workspace->scratch_doubles < widest * 4)
        {
            *status = TF_SCRATCH_TOO_SMALL;
            fprintf(stderr,
                    "\n*** THINFILM: scratch too small ***\n"
                    "    have %lld doubles, need %lld for an operand spanning %lld points.\n\n",
                    (long long)(workspace != 0 ? workspace->scratch_doubles : 0),
                    (long long)(widest * 4), (long long)widest);
            return TF_SCRATCH_TOO_SMALL;
        }
    }

    /* THE TRIAL AXIS, when any operand asks for it.
     *
     * The nominal stack is evaluated first and unconditionally: operands marked TF_TRIAL_NOMINAL
     * take their value from it, and it is also where the flags come from, since whether a value
     * rests on a synthesised index is a property of the material data and not of how thick a
     * layer came out.
     *
     * Then one pass per trial against the shadow problem. Note which value each pass reads for a
     * NOMINAL operand that some trial-reduced operand CONSUMES: the trial's, not the nominal's.
     * That is deliberate. NOMINAL says "report me on the nominal stack"; it does not say "pretend
     * the stack did not move" to an operand downstream, because inside trial t the stack really
     * is thicker and a ratio taken against the nominal total would be a ratio of two different
     * designs. */
    if (tf_uses_trials(problem, operands))
    {
        const int32_t nr = operands->n_operands;
        const int64_t wide = (widest > 1) ? widest : 1;
        const int64_t need = wide * 4 + (int64_t)nr * 4;
        if (workspace == 0 || workspace->scratch == 0 || workspace->scratch_doubles < need)
        {
            *status = TF_SCRATCH_TOO_SMALL;
            fprintf(stderr,
                    "\n*** THINFILM: scratch too small ***\n"
                    "    have %lld doubles, need %lld for %d trials over %d operands.\n\n",
                    (long long)(workspace != 0 ? workspace->scratch_doubles : 0),
                    (long long)need, problem->n_trials, nr);
            return TF_SCRATCH_TOO_SMALL;
        }

        double* cur    = workspace->scratch;
        double* x      = cur;  cur += wide * 4;   /* the span scratch one operand needs */
        double* tval   = cur;  cur += nr;         /* this trial's value for every operand */
        double* acc    = cur;  cur += nr;         /* the running reduction */
        double* winner = cur;  cur += nr;         /* which trial a WORST reduction chose */
        double* worst  = cur;                     /* and the value it had there */

        /* The nominal pass. Flags and the NOMINAL operands' values both come from here. */
        if (!tf_operand_pass(problem, operands, plan, x, 0, out->value, 0,
                             out->operand_flag, out->n_operands_flagged))
        {
            TF_NOT_IMPLEMENTED(status, TF_NOT_IMPLEMENTED_TARGET,
                "an operand the evaluator does not recognise, on the nominal stack");
            return TF_NOT_IMPLEMENTED_TARGET;
        }

        for (int32_t o = 0; o < nr; ++o)
        {
            tf_trial_seed(tf_trial_reduction_of(operands, o), &acc[o], &winner[o]);
            worst[o] = out->value[o];   /* a WORST over zero trials would still have a value */
        }

        for (int32_t t = 0; t < problem->n_trials; ++t)
        {
            tf_problem shadow;
            tf_problem_for_trial(problem, t, &shadow);

            if (!tf_operand_pass(&shadow, operands, plan, x, 0, tval, 0, 0, 0))
            {
                TF_NOT_IMPLEMENTED(status, TF_NOT_IMPLEMENTED_TARGET,
                    "an operand the evaluator does not recognise, on a perturbed stack");
                return TF_NOT_IMPLEMENTED_TARGET;
            }

            for (int32_t o = 0; o < nr; ++o)
            {
                if (out->trial_value != 0) out->trial_value[(int64_t)t * nr + o] = tval[o];
                const int32_t red = tf_trial_reduction_of(operands, o);
                if (red == TF_TRIAL_NOMINAL) continue;
                tf_trial_accumulate(red, tval[o], tf_score_of(operands, o, tval[o]).residual,
                                    t, &acc[o], &winner[o], &worst[o]);
            }
        }

        for (int32_t o = 0; o < nr; ++o)
        {
            const int32_t red = tf_trial_reduction_of(operands, o);
            if (red == TF_TRIAL_NOMINAL) continue;
            out->value[o] = tf_trial_finish(red, acc[o], worst[o], problem->n_trials);
        }

        for (int32_t o = 0; o < nr; ++o)
        {
            const int32_t row = plan->operand_residual[o];
            if (row >= 0) out->residual[row] = tf_score_of(operands, o, out->value[o]).residual;
        }

        *out->merit = tf_merit_of(operands, plan, out->residual);
        return TF_OK;
    }

    /* IN TOPOLOGICAL ORDER, so that every input is written before the operand that reads it. The
     * order is the plan's; walking the table in index order would work only for a table whose
     * derived operands happened to be listed after their inputs, which is a property no caller
     * should have to maintain. */
    for (int32_t k = 0; k < operands->n_operands; ++k)
    {
        const int32_t o = plan->operand_order[k];
        double  value = 0.0;
        int32_t fl = 0;

        if (tf_is_stack(operands, o))
        {
            /* Reads the layer table and no points. It owns no slots, so the sweeps never see it
             * and its Jacobian row is filled directly further down. */
            const int sv = tf_stack_value(problem, operands->code[o],
                                          tf_sharpness_of(operands, o), &value);
            if (sv == -1) return TF_NO_STRESS_DATA;   /* not a missing branch: missing DATA */
            if (sv != 1)
            {
                TF_NOT_IMPLEMENTED(status, TF_NOT_IMPLEMENTED_TARGET,
                    "a stack code with no value written");
                return TF_NOT_IMPLEMENTED_TARGET;
            }
        }
        else if (tf_is_derived(operands, o))
        {
            const int32_t f = tf_input_first(operands, o), ic = tf_input_count(operands, o);
            double in[2], d[2];   /* TF_FIXED_OK: two, because every derived code takes two */
            if (ic > 2)
            {
                TF_NOT_IMPLEMENTED(status, TF_NOT_IMPLEMENTED_TARGET,
                    "a derived operand with more than two inputs. Every code that reads operands "
                    "today takes exactly two; a colour difference will take three and will widen "
                    "this when it arrives.");
                return TF_NOT_IMPLEMENTED_TARGET;
            }
            for (int32_t i = 0; i < ic; ++i) in[i] = out->value[operands->input_index[f + i]];

            if (!tf_derived_value(operands->code[o], in, ic, d, &value))
            {
                TF_NOT_IMPLEMENTED(status, TF_NOT_IMPLEMENTED_TARGET,
                    "a derived operand whose value is not written, or a ratio by zero");
                return TF_NOT_IMPLEMENTED_TARGET;
            }
            if (out->operand_flag != 0)
                for (int32_t i = 0; i < ic; ++i)
                    fl |= out->operand_flag[operands->input_index[f + i]];
        }
        else
        {
            double one[4];   /* TF_FIXED_OK: the pointwise case, four doubles, not a problem size */
            double* x = (widest > 1) ? workspace->scratch : one;
            if (!tf_operand_value(problem, operands, plan, o, x, 0, &value, &fl))
            {
                TF_NOT_IMPLEMENTED(status, TF_NOT_IMPLEMENTED_TARGET,
                    "a target code the evaluator does not recognise");
                return TF_NOT_IMPLEMENTED_TARGET;
            }
        }

        /* tf_evaluate USED TO COMPUTE THIS AND THROW IT AWAY. The value-only entry point reported
         * no flags at all while tf_evaluate_jacobian reported them, so a caller that only wanted
         * numbers was told nothing about a crossing that never happened or an index nobody
         * measured. Same silence as the one M3.4 exists to prevent, at a different door. */
        if (out->operand_flag != 0) out->operand_flag[o] = fl;
        if (fl != 0 && out->n_operands_flagged != 0) ++(*out->n_operands_flagged);

        out->value[o] = value;

        const int32_t row = plan->operand_residual[o];
        if (row >= 0) out->residual[row] = tf_score_of(operands, o, value).residual;
    }

    *out->merit = tf_merit_of(operands, plan, out->residual);
    return TF_OK;
}

} /* extern "C" */
