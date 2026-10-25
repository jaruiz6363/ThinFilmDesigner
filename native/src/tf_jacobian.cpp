/* tf_jacobian.cpp — the analytic Jacobian.
 *
 * THE ALGORITHM, and the reason the scratch is shaped the way it is.
 *
 * The stack matrix is an ordered product, and a layer thickness appears in exactly one factor:
 *
 *     M = M_0 ... M_{j-1} * M_j * M_{j+1} ... M_{n-1}
 *     dM/dt_j = prefix[j] * (dM_j/dt_j) * suffix[j]
 *
 * Computing that per layer from scratch is O(n^2). One forward sweep storing prefix products and
 * one backward sweep carrying the suffix makes it O(n):
 *
 *     forward   j = 0..n-1 :  prefix[j+1] = prefix[j] * M_j            (stored, n+1 matrices)
 *     backward  j = n-1..0 :  dM/dt_j = prefix[j] * (dM_j/dt_j) * S    (S in registers)
 *                             S = M_j * S
 *
 * THE SUFFIX NEVER NEEDS STORING, which is the halving that lets the chain fit where two chains
 * would not. And the backward sweep gets M_j and dM_j/d(delta) from ONE tf_c_sincos - they share
 * the same four transcendentals - so recomputing M_j costs nothing over storing it.
 *
 * ONE PREFIX CHAIN SERVES BOTH POLARISATIONS, which is less obvious. Every target on the list is
 * LINEAR in (dr_s, dr_p, dt_s, dt_p) once the amplitudes themselves are known. Even tan(Psi),
 * which couples the two, separates:
 *
 *     rho = r_p / r_s      d rho = (1/r_s) d r_p  -  (r_p / r_s^2) d r_s
 *
 * so the s contribution and the p contribution can be accumulated in separate passes and summed.
 * That is why the amplitudes are computed first and the sweeps run one polarisation at a time.
 *
 * WHAT IS NOT DONE YET, stated rather than hidden: the derivative sweep uses GENERAL complex
 * arithmetic even for points the plan marked structured. The result is correct - it is checked
 * against the structured evaluation and against central differences - but it forfeits whatever
 * the structured path is worth on those points. The structured derivative sweep is the obvious
 * next optimisation and every piece it needs already exists in tf_matrix2.h. The value-only path,
 * which is what runs when no derivatives are wanted, already takes the fast branch.
 *
 * THIS SAID "THE FACTOR OF FOUR" AND THAT WAS WRONG. Four is the ratio of the matrix arithmetic:
 * four doubles against eight. It is not the ratio of a SOLVE, because the transcendentals are the
 * same four per layer on either path and do not halve with the matrix. Measured end to end on two
 * cases differing in nothing but k, the structured path is about 1.9x - see the X1.5 benchmark,
 * which exists to put a number on exactly this. The prize is still worth having: the sweep is 85%
 * of a Jacobian and a refinement is 76% Jacobians, so the sweep is roughly 30% of a run.
 */

#include "tf_internal.h"

#include <math.h>
#include <stdio.h>

extern "C" {

/* ---------------------------------------------------------------------------------------------
 * Small helpers
 * ------------------------------------------------------------------------------------------- */

/* ---------------------------------------------------------------------------------------------
 * One polarisation, one point: the derivative of r, t, R and T with respect to every layer
 * thickness. R and T include the substrate's back surface when there is one; r and t never do.
 *
 * Writes n_layers entries into d_r, d_t, d_R and d_T. The caller owns those buffers; they are
 * carved out of the workspace tf_plan sized.
 * ------------------------------------------------------------------------------------------- */

static void tf_sweep(const tf_problem* p, int32_t point, int pol,
                     tf_matrix2* prefix,          /* [n_layers + 1] */
                     tf_complex* d_r, tf_complex* d_t, double* d_R, double* d_T)
{
    const int32_t n  = p->n_layers;
    const int32_t wi = p->point_wavelength[point];
    const double  wv = p->wavelength[wi];

    const tf_complex n0   = tf_index_internal(tf_incident_at(p, point));
    const tf_complex nsub = tf_index_internal(tf_exit_at(p, point));
    const tf_complex n0s  = tf_c_scale(n0, sin(p->point_angle[point]));
    const tf_complex n0_sin_sq = tf_c_mul(n0s, n0s);

    const tf_complex eta0   = tf_layer_admittance(n0,   tf_layer_kappa(n0,   n0_sin_sq), pol);
    const tf_complex etasub = tf_layer_admittance(nsub, tf_layer_kappa(nsub, n0_sin_sq), pol);

    /* Forward sweep. */
    /* THE CHAIN IS INDEXED BY TRAVERSAL POSITION; THE LAYER IT READS IS PHYSICAL. Under a
     * reversed configuration those differ, and conflating them would silently multiply the stack
     * in the wrong order while every index still looked plausible. */
    const int rev = tf_reversed_at(p, point);

    prefix[0] = tf_m2_identity();
    for (int32_t i = 0; i < n; ++i)
    {
        const int32_t j = tf_layer_at(p, rev, i);
        const int32_t m = p->layer_material[j];
        const tf_complex nj = tf_index_internal(p->material_index[(int64_t)m * p->n_wavelengths + wi]);
        const tf_complex kappa = tf_layer_kappa(nj, n0_sin_sq);
        const tf_complex eta   = tf_layer_admittance(nj, kappa, pol);

        tf_complex delta = tf_c_scale(kappa, TF_TWO_PI * p->layer_thickness[j] / wv);
        tf_layer_clamp_delta(&delta);

        prefix[i + 1] = tf_m2_mul(prefix[i], tf_layer_matrix(delta, eta));
    }

    /* The stack, and the quantities every derivative is expressed through. */
    const tf_matrix2 M = prefix[n];
    const tf_complex B = tf_c_add(M.m00, tf_c_mul(M.m01, etasub));
    const tf_complex C = tf_c_add(M.m10, tf_c_mul(M.m11, etasub));
    const tf_complex D = tf_c_add(tf_c_mul(eta0, B), C);
    const tf_complex r = tf_c_div(tf_c_sub(tf_c_mul(eta0, B), C), D);
    const tf_complex t = tf_c_div(tf_c_scale(eta0, 2.0), D);
    const double     T = 4.0 * eta0.re * etasub.re / tf_c_abs2(D);

    /* The back surface: the coating's reflectance from inside the substrate, and the slopes of
     * the incoherent sum in it, so that each layer below costs one more amplitude derivative. */
    const int back = tf_has_back(p);
    tf_complex rr = tf_c(0.0, 0.0), Dr = tf_c(1.0, 0.0);
    tf_incoherent inc = { 0.0, 0.0, 0.0, 0.0, 0.0, 0.0 };
    if (back)
    {
        rr  = tf_reverse_r(M, eta0, etasub, &Dr);
        inc = tf_incoherent_sum(tf_c_abs2(r), T, tf_c_abs2(rr),
                                tf_back_surface(p, point, pol, n0_sin_sq));
    }

    /* Backward sweep. S is the running suffix and never leaves registers. */
    tf_matrix2 S = tf_m2_identity();
    for (int32_t i = n - 1; i >= 0; --i)
    {
        const int32_t j = tf_layer_at(p, rev, i);
        const int32_t m = p->layer_material[j];
        const tf_complex nj = tf_index_internal(p->material_index[(int64_t)m * p->n_wavelengths + wi]);
        const tf_complex kappa = tf_layer_kappa(nj, n0_sin_sq);
        const tf_complex eta   = tf_layer_admittance(nj, kappa, pol);

        tf_complex delta = tf_c_scale(kappa, TF_TWO_PI * p->layer_thickness[j] / wv);
        const int clamped = tf_layer_clamp_delta(&delta);

        const tf_matrix2 Mj = tf_layer_matrix(delta, eta);

        if (clamped)
        {
            /* A clamped layer no longer depends on its thickness, so the derivative is exactly
             * zero - not small, zero. tf_plan has already reported such a variable as inert. */
            d_r[j] = tf_c(0.0, 0.0);
            d_t[j] = tf_c(0.0, 0.0);
            d_R[j] = 0.0;
            d_T[j] = 0.0;
        }
        else
        {
            /* dM_j/dt_j = (dM_j/d delta)(d delta/dt_j), and d delta/dt_j = 2 pi kappa / lambda
             * does not depend on the thickness at all. */
            const tf_matrix2 dMd = tf_layer_matrix_ddelta(delta, eta);
            const tf_complex sc  = tf_layer_ddelta_dthickness(kappa, wv);

            tf_matrix2 dMj;
            dMj.m00 = tf_c_mul(dMd.m00, sc);  dMj.m01 = tf_c_mul(dMd.m01, sc);
            dMj.m10 = tf_c_mul(dMd.m10, sc);  dMj.m11 = tf_c_mul(dMd.m11, sc);

            const tf_matrix2 dM = tf_m2_mul(tf_m2_mul(prefix[i], dMj), S);

            const tf_complex dB = tf_c_add(dM.m00, tf_c_mul(dM.m01, etasub));
            const tf_complex dC = tf_c_add(dM.m10, tf_c_mul(dM.m11, etasub));

            const tf_complex dD = tf_c_add(tf_c_mul(eta0, dB), dC);
            const tf_complex dN = tf_c_sub(tf_c_mul(eta0, dB), dC);

            /* r = N/D  ->  dr = (dN - r dD) / D */
            d_r[j] = tf_c_div(tf_c_sub(dN, tf_c_mul(r, dD)), D);
            /* t = 2 eta0 / D  ->  dt = -t dD / D */
            d_t[j] = tf_c_div(tf_c_scale(tf_c_mul(t, dD), -1.0), D);
            /* T = k / |D|^2  ->  dT = -T d|D|^2 / |D|^2 */
            d_T[j] = -T * tf_dabs2(D, dD) / tf_c_abs2(D);
            d_R[j] = tf_dabs2(r, d_r[j]);

            if (back)
            {
                const double dRr = tf_dabs2(rr, tf_reverse_dr(dM, eta0, etasub, rr, Dr));
                const double dTf = d_T[j];
                d_R[j] += inc.R_Tf * dTf + inc.R_Rr * dRr;
                d_T[j]  = inc.T_Tf * dTf + inc.T_Rr * dRr;
            }
        }

        S = tf_m2_mul(Mj, S);
    }
}

/* ---------------------------------------------------------------------------------------------
 * The target chain rule.
 *
 * Every one of these is analytic and none of them wants automatic differentiation: the whole set
 * reduces to r and t, and a hand-derivative of |z|^2, |z| or arg(z) is two lines. Automatic
 * differentiation earns its place in the dispersion models, where the forms are many and the cost
 * is once per material per wavelength - not here, in the innermost loop, where it would buy
 * nothing and cost speed.
 * ------------------------------------------------------------------------------------------- */

/* d(arg z) = Im(conj(z) dz) / |z|^2. Undefined at the origin, where an amplitude has no phase, and
 * refused there rather than returned as a large number. */
static int tf_darg(tf_complex z, tf_complex dz, double* out)
{
    const double m2 = tf_c_abs2(z);
    if (m2 == 0.0) return 0;
    *out = (z.re * dz.im - z.im * dz.re) / m2;
    return 1;
}

static int tf_ddecibel(double s, double ds, double p, double dp, double* out)
{
    if (!(s > 0.0) || !(p > 0.0)) return 0;
    *out = (10.0 / 2.302585092994045684017991454684) * (ds / s - dp / p);
    return 1;
}

static int tf_ddiat(double s, double ds, double p, double dp, double* out)
{
    const double sum = s + p;
    if (!(sum > 0.0)) return 0;
    *out = 2.0 * (s * dp - p * ds) / (sum * sum);
    return 1;
}

int tf_target_derivative(int32_t code,
                         const tf_point_amplitudes* a,
                         tf_complex dr_s, tf_complex dr_p,
                         tf_complex dt_s, tf_complex dt_p,
                         double dR_s, double dR_p,
                         double dT_s, double dT_p,
                         double floor_od, double* out)
{
    switch (code)
    {
        case TF_TARGET_RS: *out = dR_s; return 1;
        case TF_TARGET_RP: *out = dR_p; return 1;
        case TF_TARGET_RA: *out = 0.5 * (dR_s + dR_p); return 1;

        case TF_TARGET_TS: *out = dT_s; return 1;
        case TF_TARGET_TP: *out = dT_p; return 1;
        case TF_TARGET_TA: *out = 0.5 * (dT_s + dT_p); return 1;

        /* ---- Phase 1 additions. dR = 2 Re(conj(r) dr) throughout. ------------------------- */

        case TF_TARGET_AS: *out = -dR_s - dT_s; return 1;
        case TF_TARGET_AP: *out = -dR_p - dT_p; return 1;
        case TF_TARGET_AA: *out = 0.5 * (-dR_s - dT_s
                                         -dR_p - dT_p); return 1;

        case TF_TARGET_ODS: *out = tf_optical_density_slope(a->T_s, floor_od) * dT_s; return 1;
        case TF_TARGET_ODP: *out = tf_optical_density_slope(a->T_p, floor_od) * dT_p; return 1;
        case TF_TARGET_ODA:
        {
            const double Tm = 0.5 * (a->T_s + a->T_p);
            *out = tf_optical_density_slope(Tm, floor_od) * 0.5 * (dT_s + dT_p);
            return 1;
        }

        /* d(arg z) = Im(conj(z) dz)/|z|^2, and the interval the caller chose shifts the value by a
         * constant multiple of 2 pi - which has no slope. So one expression serves all three
         * conventions here exactly as it does for Delta. */
        case TF_TARGET_R_PHASE_S: return tf_darg(a->r_s, dr_s, out);
        case TF_TARGET_R_PHASE_P: return tf_darg(a->r_p, dr_p, out);
        case TF_TARGET_T_PHASE_S: return tf_darg(a->t_s, dt_s, out);
        case TF_TARGET_T_PHASE_P: return tf_darg(a->t_p, dt_p, out);

        /* d|x| = sign(x) dx, and ZERO at x = 0 - the balanced point, where a met requirement
         * exerts no force. Same choice as a comparison sitting exactly on its limit. */
        case TF_TARGET_R_NPOL:
        {
            const double d = a->R_s - a->R_p;
            const double dd = dR_s - dR_p;
            *out = (d > 0.0) ? dd : ((d < 0.0) ? -dd : 0.0);
            return 1;
        }
        case TF_TARGET_T_NPOL:
        {
            const double d = a->T_s - a->T_p, dd = dT_s - dT_p;
            *out = (d > 0.0) ? dd : ((d < 0.0) ? -dd : 0.0);
            return 1;
        }

        /* d[10 log10(s/p)] = (10/ln10)(ds/s - dp/p) */
        case TF_TARGET_R_EXT_DB:
            return tf_ddecibel(a->R_s, dR_s,
                               a->R_p, dR_p, out);
        case TF_TARGET_T_EXT_DB:
            return tf_ddecibel(a->T_s, dT_s, a->T_p, dT_p, out);

        /* d[(p-s)/(p+s)] = 2 (s dp - p ds)/(p+s)^2 */
        case TF_TARGET_R_DIAT:
            return tf_ddiat(a->R_s, dR_s,
                            a->R_p, dR_p, out);
        case TF_TARGET_T_DIAT:
            return tf_ddiat(a->T_s, dT_s, a->T_p, dT_p, out);

        default: break;
    }

    /* The ellipsometric group: rho = p / s, and d rho separates into an s part and a p part, which
     * is what lets the two polarisations be swept independently. */
    {
        const int trans = (code == TF_TARGET_T_TANPSI || code == TF_TARGET_T_COSDELTA ||
                           code == TF_TARGET_T_PSI    || code == TF_TARGET_T_DELTA);

        const tf_complex num  = trans ? a->t_p : a->r_p;
        const tf_complex den  = trans ? a->t_s : a->r_s;
        const tf_complex dnum = trans ? dt_p   : dr_p;
        const tf_complex dden = trans ? dt_s   : dr_s;

        const tf_complex rho  = tf_c_div(num, den);
        /* d(p/s) = dp/s - (p/s^2) ds = (dp - rho ds) / s */
        const tf_complex drho = tf_c_div(tf_c_sub(dnum, tf_c_mul(rho, dden)), den);

        const double mag2 = tf_c_abs2(rho);
        if (mag2 == 0.0) return 0;
        const double mag = sqrt(mag2);

        /* d|rho| = Re(conj(rho) d rho) / |rho|      d arg(rho) = Im(conj(rho) d rho) / |rho|^2 */
        const double dmag = (rho.re * drho.re + rho.im * drho.im) / mag;
        const double darg = (rho.re * drho.im - rho.im * drho.re) / mag2;

        switch (code)
        {
            case TF_TARGET_R_TANPSI:
            case TF_TARGET_T_TANPSI:   *out = dmag; return 1;

            /* Psi = atan(|rho|)  ->  dPsi = d|rho| / (1 + |rho|^2) */
            case TF_TARGET_R_PSI:
            case TF_TARGET_T_PSI:      *out = dmag / (1.0 + mag2); return 1;

            /* cos(Delta) -> -sin(Delta) dDelta. sin and cos of arg(rho) come from rho itself
             * without a trigonometric call: sin = Im(rho)/|rho|. */
            case TF_TARGET_R_COSDELTA:
            case TF_TARGET_T_COSDELTA: *out = -(rho.im / mag) * darg; return 1;

            /* Delta itself, and THE SAME EXPRESSION SERVES ALL THREE CONVENTIONS. Every one of
             * them differs from arg(rho) by a locally constant multiple of 2 pi - PRINCIPAL and
             * POSITIVE in the value, SHORTEST_ARC in the residual - and a constant has no slope.
             * So the Jacobian needs no branch on operands.delta_convention at all, and cannot fall
             * out of step with the value when the setting changes. The one exception is exactly
             * on the cut, where the value jumps and no convention has a derivative. */
            case TF_TARGET_R_DELTA:
            case TF_TARGET_T_DELTA:    *out = darg; return 1;

            default: return 0;
        }
    }
}

/* ---------------------------------------------------------------------------------------------
 * tf_evaluate_jacobian
 * ------------------------------------------------------------------------------------------- */

/* HOW MUCH OF ONE PASS'S VALUE-DERIVATIVE BELONGS IN THE ANSWER.
 *
 * d(value_o)/dx = sum over passes of w * d(value_o on that pass)/dx, and the reduction is
 * entirely contained in w. Pass 0 is the nominal stack and pass t+1 is trial t.
 *
 * WORST picks out one pass, which is a subgradient of a maximum and is the minimax inner loop
 * the ledger names; MEAN spreads evenly, being linear; NOMINAL ignores every trial.
 * FRACTION_PASSING never reaches here - it is refused before any of this runs, because a step
 * function's zero gradient is a lie rather than an answer. */
static double tf_trial_weight(int32_t reduction, int32_t pass, double winner, int32_t n_trials)
{
    switch (reduction)
    {
        case TF_TRIAL_NOMINAL: return (pass == 0) ? 1.0 : 0.0;
        case TF_TRIAL_WORST:   return (pass == (int32_t)winner + 1) ? 1.0 : 0.0;
        case TF_TRIAL_MEAN:    return (pass == 0) ? 0.0 : 1.0 / (double)n_trials;
        default:               return 0.0;
    }
}

TF_API tf_status tf_evaluate_jacobian(const tf_problem*      problem,

                                      const tf_operands*      operands,
                                      const tf_variables*    variables,
                                      const tf_plan_buffers* plan,
                                      tf_workspace*          workspace,
                                      tf_output*             out)
{
    if (problem == 0 || operands == 0 || variables == 0 || plan == 0 || workspace == 0 || out == 0)
        return TF_NULL_POINTER;
    if (out->status == 0 || out->residual == 0 || out->merit == 0 || out->value == 0)
        return TF_NULL_POINTER;
    if (plan->point_path == 0 || plan->point_polarisation == 0 || plan->report == 0)
        return TF_PLAN_NOT_RUN;

    int32_t* status = out->status;
    *status = TF_OK;

    if (plan->report->n_unsupported > 0)
    {
        TF_NOT_IMPLEMENTED(status, TF_NOT_IMPLEMENTED_INCOHERENT,
            "the plan found points needing a path that is not implemented - massive layers.");
        return TF_NOT_IMPLEMENTED_INCOHERENT;
    }

    {
        const tf_status ds = tf_dispersion_check(problem, operands, status);
        if (ds != TF_OK) return ds;
    }

    for (int32_t v = 0; v < variables->n_variables; ++v)
        if (variables->kind[v] != TF_VARIABLE_LAYER_THICKNESS)
        {
            TF_NOT_IMPLEMENTED(status, TF_NOT_IMPLEMENTED_VARIABLE,
                "only layer-thickness variables are differentiable so far. A material-coefficient "
                "variable needs a dispersion model to differentiate, and no dispersion model "
                "exists yet - material_index is still a table of values, not a function.");
            return TF_NOT_IMPLEMENTED_VARIABLE;
        }

    const int32_t n  = problem->n_layers;
    const int32_t nv = variables->n_variables;
    const int32_t nr = operands->n_operands;

    /* nr IS THE OPERAND COUNT; THE JACOBIAN HAS ONLY SCORED ROWS. Those were the same number
     * until an operand could be unscored, and conflating them writes past the caller's buffer -
     * which is exactly what it did the first time this was run. The working matrix below is
     * operand-sized; out->jacobian is residual-sized; they are never interchangeable. */
    const int32_t nres = plan->report->n_residuals;

    if (nres > 0 && nv > 0 && out->jacobian == 0) return TF_NULL_POINTER;
    for (int64_t k = 0; k < (int64_t)nres * nv; ++k) out->jacobian[k] = 0.0;
    /* work is zeroed after the carve, below, since it lives in the scratch. */

    /* THE WORKSPACE IS CARVED ONLY IF THERE IS A JACOBIAN TO COMPUTE. A bare substrate with no
     * variables needs none, and demanding one would make the simplest legitimate call an error -
     * so the pointers stay null and the residual pass below checks before writing. */
    tf_matrix2* prefix = 0;
    tf_complex* d_r = 0, *d_t = 0;
    double*     d_R = 0, *d_T = 0;
    double*     slot_factor = 0;   /* [n_slots] d(residual_o)/d(x_p) for each (operand, point) */
    double*     xbuf = 0;          /* [3 * max_span] the span scratch tf_operand_value wants */
    double*     dvdx = 0;          /* [max_span] the reduction's local slopes */
    /* THE WORKING MATRIX: d(value_o)/dx for EVERY operand, scored or not.
     *
     * The Jacobian has a row only for scored operands, but the chain rule needs the rows of an
     * operand's inputs - which are often unscored, existing only to be consumed. So the derivative
     * is built here first and the scored rows are copied out at the end.
     *
     * It is the same size as the Jacobian, which is the honest cost of letting operands read
     * operands. A table with nothing derived could write straight into out->jacobian and skip it;
     * that is a worthwhile optimisation and it is not taken here, because one path that is always
     * right beats two that agree only when tested. */
    double* work = 0;

    /* THE TRIAL ACCUMULATOR. `work` holds ONE pass's value-derivatives; `wacc` holds the weighted
     * sum over passes that the comparison is finally applied to. Two buffers rather than one
     * because a pass overwrites work entirely, and a table with no trial reduction never carves
     * wacc at all - it points straight at work and the sum is a no-op. */
    double* wacc = 0;
    double* tval = 0;     /* [n_operands] one pass's values                    */
    double* tacc = 0;     /* [n_operands] the running trial reduction          */
    double* twin = 0;     /* [n_operands] which trial a WORST reduction chose  */
    double* twv  = 0;     /* [n_operands] and the value it had there           */

    const int uses_trials = tf_uses_trials(problem, operands);
    /* GROUP DELAY, GDD AND TOD carry their own sweep: prefix matrices and six readings per layer. */
    const int uses_disp = tf_uses_dispersion(operands);
    double* disp_prefix = 0;
    double* disp_d = 0;

    /* REFUSED BEFORE ANYTHING IS COMPUTED, because the refusal is about the QUESTION and not
     * about the answer. A yield is a count of trials that pass; it does not move when a thickness
     * moves a little, and then it jumps. There is no slope to return and a zero one would be
     * believed. C0.7 forbids the silent alternative. */
    if (uses_trials && nv > 0)
        for (int32_t o = 0; o < nr; ++o)
            if (tf_trial_reduction_of(operands, o) == TF_TRIAL_FRACTION_PASSING &&
                plan->operand_residual[o] >= 0)
            {
                *status = TF_TRIAL_GRADIENT_UNDEFINED;
                fprintf(stderr,
                        "\n*** THINFILM: no such derivative ***\n"
                        "    Operand %d is scored through TF_TRIAL_FRACTION_PASSING, and a\n"
                        "    fraction of passing trials is a STEP in every thickness: constant\n"
                        "    under a small move, then a jump when one trial crosses its limit.\n"
                        "    Its derivative is zero almost everywhere and undefined on the set\n"
                        "    that matters, so this refuses rather than return a zero row an\n"
                        "    optimiser would believe.\n"
                        "    Use it as a final-stage objective or a selection criterion, which\n"
                        "    is what the specification asks of it, and both read the VALUE -\n"
                        "    tf_evaluate returns it.\n\n", o);
                return TF_TRIAL_GRADIENT_UNDEFINED;
            }

    const int32_t n_slots  = tf_n_slots(operands);

    const int32_t max_span = plan->report->max_operand_span;

    if ((n > 0 && nv > 0) || uses_trials)
    {
        const int64_t need = (int64_t)(n + 1) * 8       /* prefix chain, 8 doubles a matrix */
                           + (int64_t)n * (2 + 2 + 1 + 1) /* d_r, d_t complex; d_R, d_T real */
                           + (int64_t)n_slots           /* the per-slot residual slope */
                           + (int64_t)max_span * 5      /* values, weights, abscissa, two slopes */
                           + (int64_t)nr * nv          /* d(value)/dx for every operand */
                           + (uses_trials ? (int64_t)nr * nv + (int64_t)nr * 4 : 0);
        const int64_t need_all = need + (uses_disp ? tf_dispersion_scratch(n) : 0);

        if (workspace->scratch == 0 || workspace->scratch_doubles < need_all)
        {
            *status = TF_SCRATCH_TOO_SMALL;
            fprintf(stderr,
                    "\n*** THINFILM: scratch too small ***\n"
                    "    have %lld doubles, need %lld. tf_plan is the only thing entitled to say\n"
                    "    how much this is; if they disagree the two have drifted apart.\n\n",
                    (long long)workspace->scratch_doubles, (long long)need_all);
            return TF_SCRATCH_TOO_SMALL;
        }

        double* cursor = workspace->scratch;
        prefix = (tf_matrix2*)cursor;  cursor += (int64_t)(n + 1) * 8;
        d_r    = (tf_complex*)cursor;  cursor += (int64_t)n * 2;
        d_t    = (tf_complex*)cursor;  cursor += (int64_t)n * 2;
        d_R    = cursor;               cursor += (int64_t)n;
        d_T    = cursor;               cursor += (int64_t)n;
        slot_factor = cursor;          cursor += (int64_t)n_slots;
        xbuf   = cursor;               cursor += (int64_t)max_span * 4;
        dvdx   = cursor;               cursor += (int64_t)max_span;
        work   = cursor;               cursor += (int64_t)nr * nv;
        if (uses_disp)
        {
            disp_prefix = cursor;  cursor += tf_dispersion_scratch(n) - TF_DISPERSION_SLOTS * (int64_t)n;
            disp_d      = cursor;  cursor += TF_DISPERSION_SLOTS * (int64_t)n;
        }
        if (uses_trials)
        {
            wacc = cursor;  cursor += (int64_t)nr * nv;
            tval = cursor;  cursor += nr;
            tacc = cursor;  cursor += nr;
            twin = cursor;  cursor += nr;
            twv  = cursor;
        }
        else wacc = work;   /* one pass, so the accumulator IS the pass */
    }


    /* Residuals first, from the same routines tf_evaluate uses, so that the two entry points
     * can never disagree about a value, a weight, a wrap or whether a constraint is active.
     *
     * AND THE PER-SLOT FACTOR, which is the whole of what a reduction does to a derivative:
     *
     *     d(residual_o)/dx = sum over the operand's slots of
     *                        d(residual_o)/d(value_o) * d(value_o)/d(x_p) * d(x_p)/dx
     *                        \___ tf_score_of ____/     \___ tf_reduce ___/
     *
     * The first two factors depend only on where the design currently sits, so they are computed
     * once here and multiplied by the third inside the sweep. Recomputing them there would mean
     * solving the stack again for every point of every span. */
    /* PHASE A: THE VALUES, COLLAPSED OVER THE TRIAL AXIS.
     *
     * Done before any derivative because TF_TRIAL_WORST cannot be differentiated until it is
     * known WHICH trial won, and that is a fact about values. It costs a second solve of every
     * point on every trial, which is the honest price of a minimax: the inner problem has to be
     * solved before the outer one can be differentiated.
     *
     * The collapse itself is the same code tf_evaluate runs, so the two entry points cannot
     * disagree about what a worst case or a mean is. */
    if (uses_trials)
    {
        if (!tf_operand_pass(problem, operands, plan, xbuf, 0, out->value, 0,
                             out->operand_flag, out->n_operands_flagged))
        {
            TF_NOT_IMPLEMENTED(status, TF_NOT_IMPLEMENTED_TARGET,
                "an operand the evaluator does not recognise, on the nominal stack");
            return TF_NOT_IMPLEMENTED_TARGET;
        }
        for (int32_t o = 0; o < nr; ++o)
        {
            tf_trial_seed(tf_trial_reduction_of(operands, o), &tacc[o], &twin[o]);
            twv[o] = out->value[o];
        }
        for (int32_t t = 0; t < problem->n_trials; ++t)
        {
            tf_problem shadow;
            tf_problem_for_trial(problem, t, &shadow);
            if (!tf_operand_pass(&shadow, operands, plan, xbuf, 0, tval, 0, 0, 0))
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
                                    t, &tacc[o], &twin[o], &twv[o]);
            }
        }
        for (int32_t o = 0; o < nr; ++o)
        {
            const int32_t red = tf_trial_reduction_of(operands, o);
            if (red != TF_TRIAL_NOMINAL)
                out->value[o] = tf_trial_finish(red, tacc[o], twv[o], problem->n_trials);
            const int32_t row = plan->operand_residual[o];
            if (row >= 0) out->residual[row] = tf_score_of(operands, o, out->value[o]).residual;
        }
        *out->merit = tf_merit_of(operands, plan, out->residual);

        if (n == 0 || nv == 0) return TF_OK;
        for (int64_t k = 0; k < (int64_t)nr * nv; ++k) wacc[k] = 0.0;
    }

    /* PHASE B: ONE PASS PER STACK, each contributing its value-derivatives with the weight its
     * operand's reduction gives it. Without trials there is exactly one pass, wacc IS work, every
     * weight is one, and what follows is what was here before, line for line. */
    const int32_t n_pass = uses_trials ? problem->n_trials + 1 : 1;
    for (int32_t pass = 0; pass < n_pass; ++pass)
    {
    tf_problem shadow_;
    const tf_problem* P = problem;
    if (pass > 0) { tf_problem_for_trial(problem, pass - 1, &shadow_); P = &shadow_; }
    double* V = uses_trials ? tval : out->value;

    double one[4];   /* TF_FIXED_OK: the no-Jacobian case, four doubles, never a P size */
    for (int32_t kk = 0; kk < nr; ++kk)
    {
        const int32_t o = plan->operand_order[kk];
        double  value = 0.0;
        int32_t fl = 0;

        if (tf_is_stack(operands, o))
        {
            /* Reads the layer table and no points. It owns no slots, so the sweeps never see it
             * and its Jacobian row is filled directly further down. */
            const int sv = tf_stack_value(P, operands->code[o],
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
            double in[2], dd[2];   /* TF_FIXED_OK: two, because every derived code takes two */
            if (ic > 2) { TF_NOT_IMPLEMENTED(status, TF_NOT_IMPLEMENTED_TARGET,
                              "a derived operand with more than two inputs"); 
                          return TF_NOT_IMPLEMENTED_TARGET; }
            for (int32_t i = 0; i < ic; ++i) in[i] = V[operands->input_index[f + i]];
            if (!tf_derived_value(operands->code[o], in, ic, dd, &value))
            {
                TF_NOT_IMPLEMENTED(status, TF_NOT_IMPLEMENTED_TARGET,
                    "a derived operand whose value is not written, or a ratio by zero");
                return TF_NOT_IMPLEMENTED_TARGET;
            }
            /* A DERIVED OPERAND INHERITS ITS INPUTS' FLAGS. AERATIO is the reason: it is a ratio
             * of a solar operand over a thermal one, and the thermal one is precisely the half
             * resting on constants out to 50 um that nobody measured. If the ratio came back
             * clean, the flag would be set on an operand the user never scored and absent from
             * the one they did - which is the silent extrapolation the specification forbids,
             * reintroduced one level up.
             *
             * Only when the caller asked for flags; with no flag array there is nowhere to
             * accumulate, and nothing has asked to be told. */
            if (out->operand_flag != 0)
                for (int32_t i = 0; i < ic; ++i)
                    fl |= out->operand_flag[operands->input_index[f + i]];
        }
        else
        {
        double* x = (xbuf != 0) ? xbuf : one;
        double* d = (dvdx != 0) ? dvdx : 0;
        if (!tf_operand_value(P, operands, plan, o, x, d, &value, &fl))
        {
            TF_NOT_IMPLEMENTED(status, TF_NOT_IMPLEMENTED_TARGET, "unrecognised target code");
            return TF_NOT_IMPLEMENTED_TARGET;
        }
        }
        if (!uses_trials)
        {
            if (out->operand_flag != 0) out->operand_flag[o] = fl;
            if (fl != 0 && out->n_operands_flagged != 0) ++(*out->n_operands_flagged);
        }
        V[o] = value;
        if (!uses_trials)
        {
            const int32_t row = plan->operand_residual[o];
            if (row >= 0) out->residual[row] = tf_score_of(operands, o, value).residual;
        }

        /* THE SLOT FACTOR IS NOW THE REDUCTION'S SLOPE ALONE. Before operands could read
         * operands, the comparison slope could be folded in here because the operand that owned
         * the point WAS the scored one. With a DAG it is not: the comparison belongs to whatever
         * is scored downstream, which may be several levels away, so it is applied once at the
         * end rather than at the point. */
        if (slot_factor != 0 && !tf_is_derived(operands, o))
        {
            const int32_t f = tf_slot_first(operands, o), k = tf_slot_count(operands, o);
            for (int32_t i = 0; i < k; ++i) slot_factor[f + i] = dvdx[i];
        }
    }
    if (!uses_trials) *out->merit = tf_merit_of(operands, plan, out->residual);

    /* Nothing can move, so the zero Jacobian is the whole answer. */
    if (n == 0 || nv == 0) return TF_OK;

    for (int64_t k = 0; k < (int64_t)nr * nv; ++k) work[k] = 0.0;

    /* One point at a time; one polarisation at a time within it - and only the polarisations
     * the plan says anything asked about. A sweep is the expensive thing here, so skipping one is
     * the same saving in the derivative as the mask buys in the value. */
    for (int32_t point = 0; point < P->n_points; ++point)
    {
        const int32_t pol_need = plan->point_polarisation[point];
        if (pol_need == 0) continue;      /* no residual mentions this point */

        tf_point_amplitudes a;
        tf_amplitudes(P, point, plan->point_path[point], pol_need, &a);

        for (int pol = 0; pol < 2; ++pol)
        {
            if (!(pol_need & ((pol == TF_POL_S) ? TF_NEED_S : TF_NEED_P))) continue;

            tf_sweep(P, point, pol, prefix, d_r, d_t, d_R, d_T);

            /* Group delay, GDD and TOD at this point and polarisation get their own sweep, once. */
            if (uses_disp)
            {
                int wanted = 0;
                for (int32_t e = 0; e < plan->point_span_count[point] && !wanted; ++e)
                {
                    const int32_t code = operands->code[plan->point_span_operand[plan->point_span_first[point] + e]];
                    wanted = tf_is_dispersion_code(code) && tf_dispersion_polarisation(code) == pol;
                }
                if (wanted) tf_dispersion_sweep(P, point, pol, disp_prefix, disp_d);
            }

            /* WHICH OPERANDS READ THIS POINT, from the plan's transpose. Before spans this was a
             * scan over operands testing point equality; with a dense grid and band operands that
             * product is exactly the thing the transpose exists to avoid. */
            const int32_t ef = plan->point_span_first[point];
            const int32_t ec = plan->point_span_count[point];

            for (int32_t e = 0; e < ec; ++e)
            {
                const int32_t r    = plan->point_span_operand[ef + e];
                const int32_t slot = plan->point_span_slot[ef + e];

                /* d(residual)/d(this point's value): the comparison's slope times the
                 * reduction's local weight. For a pointwise equality target with no tolerance it
                 * is sqrt(weight), which is what this used to be written as. It parts company the
                 * moment a comparison is one-sided - a satisfied constraint is zero, a GE is
                 * negative - or the operand spans more than one point. */
                const double sw = slot_factor[slot];

                for (int32_t v = 0; v < nv; ++v)
                {
                    const int32_t j = variables->layer[v];

                    const tf_complex dr_s = (pol == TF_POL_S) ? d_r[j] : tf_c(0.0, 0.0);
                    const tf_complex dr_p = (pol == TF_POL_P) ? d_r[j] : tf_c(0.0, 0.0);
                    const tf_complex dt_s = (pol == TF_POL_S) ? d_t[j] : tf_c(0.0, 0.0);
                    const tf_complex dt_p = (pol == TF_POL_P) ? d_t[j] : tf_c(0.0, 0.0);
                    /* Without a back surface, the evaluated r, so the Jacobian is bit for bit what
                     * it was before the back surface existed. */
                    const int        bk   = tf_has_back(P);
                    const double     dR_s = (pol == TF_POL_S) ? (bk ? d_R[j] : tf_dabs2(a.r_s, dr_s)) : 0.0;
                    const double     dR_p = (pol == TF_POL_P) ? (bk ? d_R[j] : tf_dabs2(a.r_p, dr_p)) : 0.0;
                    const double     dT_s = (pol == TF_POL_S) ? d_T[j] : 0.0;
                    const double     dT_p = (pol == TF_POL_P) ? d_T[j] : 0.0;

                    double dvalue = 0.0;
                    if (tf_is_dispersion_code(operands->code[r]))
                    {
                        if (tf_dispersion_polarisation(operands->code[r]) == pol)
                            dvalue = tf_dispersion_slope(disp_d, j, operands->code[r]);
                    }
                    else if (!tf_target_derivative(operands->code[r], &a,
                                              dr_s, dr_p, dt_s, dt_p, dR_s, dR_p, dT_s, dT_p,
                                              tf_param1_of(operands, r), &dvalue))
                    {
                        TF_NOT_IMPLEMENTED(status, TF_NOT_IMPLEMENTED_TARGET,
                            "a target whose derivative is not written");
                        return TF_NOT_IMPLEMENTED_TARGET;
                    }

                    /* COLUMN-MAJOR: variable-major, so that one thread per residual writes
                     * consecutive addresses. Accumulated because the two polarisation passes each
                     * contribute a part. */
                    work[(int64_t)v * nr + r] += sw * dvalue;
                }
            }
        }
    }



    /* STACK OPERAND ROWS, filled directly. They own no slots, so the point sweeps above never
     * touched them, and their derivatives are constants or hinges rather than a chain rule. Placed
     * before the DAG pass so that a derived operand may read one. */
    for (int32_t o = 0; o < nr; ++o)
    {
        if (!tf_is_stack(operands, o)) continue;
        const double thr = tf_sharpness_of(operands, o);
        for (int32_t vi = 0; vi < nv; ++vi)
        {
            if (variables->kind[vi] != TF_VARIABLE_LAYER_THICKNESS) continue;
            work[(int64_t)vi * nr + o] =
                tf_stack_thickness_slope(P, operands->code[o], thr, variables->layer[vi]);
        }
    }

    /* THE CHAIN RULE THROUGH THE DAG, in the plan's topological order so that an operand's inputs
     * are already differentiated when it is reached.
     *
     *     d(value_o)/dx = sum over inputs i of  d(value_o)/d(value_i) * d(value_i)/dx
     *
     * The local factors are the same two-line hand derivatives tf_derived_value already returns
     * for the value - one call gives both, which is the discipline everywhere else in this engine
     * and here it also means a derived operand's value and gradient cannot be computed from
     * different inputs. */
    for (int32_t kk = 0; kk < nr; ++kk)
    {
        const int32_t o = plan->operand_order[kk];
        if (!tf_is_derived(operands, o)) continue;

        const int32_t f = tf_input_first(operands, o), ic = tf_input_count(operands, o);
        double in[2], d[2], v = 0.0;   /* TF_FIXED_OK: two, as every derived code takes two */
        for (int32_t i = 0; i < ic; ++i) in[i] = V[operands->input_index[f + i]];
        if (!tf_derived_value(operands->code[o], in, ic, d, &v))
        {
            TF_NOT_IMPLEMENTED(status, TF_NOT_IMPLEMENTED_TARGET,
                "a derived operand whose derivative is not written");
            return TF_NOT_IMPLEMENTED_TARGET;
        }

        for (int32_t vi = 0; vi < nv; ++vi)
        {
            double acc = 0.0;
            for (int32_t i = 0; i < ic; ++i)
                acc += d[i] * work[(int64_t)vi * nr + operands->input_index[f + i]];
            work[(int64_t)vi * nr + o] = acc;
        }
    }

    /* THIS PASS, FOLDED IN. Without trials there is one pass, wacc IS work, and this is skipped
     * entirely - so the pre-trial path does not pay for the axis it does not use. */
    if (uses_trials)
        for (int32_t o = 0; o < nr; ++o)
        {
            const double w = tf_trial_weight(tf_trial_reduction_of(operands, o), pass,
                                             twin[o], problem->n_trials);
            if (w == 0.0) continue;
            for (int32_t vi = 0; vi < nv; ++vi)
                wacc[(int64_t)vi * nr + o] += w * work[(int64_t)vi * nr + o];
        }
    }   /* end of the pass loop */

    /* THE COMPARISON SLOPE, APPLIED ONCE, WHERE THE SCORING HAPPENS. A satisfied one-sided
     * constraint is zero here and therefore zero in every column, however many operands fed it. */
    for (int32_t o = 0; o < nr; ++o)
    {
        const int32_t row = plan->operand_residual[o];
        if (row < 0) continue;
        const double slope = tf_score_of(operands, o, out->value[o]).dresidual_dvalue;
        for (int32_t vi = 0; vi < nv; ++vi)
            out->jacobian[(int64_t)vi * plan->report->n_residuals + row] =
                slope * wacc[(int64_t)vi * nr + o];
    }

    return TF_OK;
}

} /* extern "C" */
