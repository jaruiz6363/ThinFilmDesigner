/* tf_spectral.cpp — the wavelength derivative, analytically.
 *
 * THE CHAIN, written out because every term of it is a place to be wrong.
 *
 * Write ' for d/d(lambda). The conserved transverse quantity, the normal component, the
 * admittance and the phase thickness all move with wavelength:
 *
 *     u      = (n0 sin theta0)^2                u' = 2 n0 n0' sin^2(theta0)
 *     kappa  = sqrt(n^2 - u)                    kappa' = (2 n n' - u') / (2 kappa)
 *     eta_s  = kappa                            eta_s' = kappa'
 *     eta_p  = n^2 / kappa                      eta_p' = 2 n n' / kappa  -  n^2 kappa' / kappa^2
 *     delta  = (2 pi d / lambda) kappa          delta' = (2 pi d / lambda) (kappa' - kappa/lambda)
 *
 * THE ANGLE IS FIXED, NOT THE TRANSVERSE WAVEVECTOR. theta0 is a property of the experiment and
 * does not change with colour, but n0 may, so u' is not zero for an immersed or cemented stack. A
 * derivative that assumed u' = 0 would be exactly right in air and quietly wrong in glass.
 *
 * Both terms of the matrix derivative are needed:
 *
 *     dM/dlambda = (dM/d delta) delta'  +  (dM/d eta) eta'
 *
 * and the second one is why tf_layer_matrix_deta had to be written. A thickness moves delta alone;
 * a wavelength moves both.
 *
 * FORWARD ACCUMULATION. There is one parameter, so reverse mode buys nothing and forward mode
 * costs no storage:
 *
 *     P_{k+1} = P_k M_k                     A_{k+1} = A_k M_k + P_k (dM_k/dlambda)
 *
 * Three matrix products per layer, one pass, not a byte of scratch. Compare the thickness
 * Jacobian, which needs n+1 stored matrices because it has n parameters to serve at once.
 *
 * GENERAL ARITHMETIC ON EVERY PATH, as in tf_jacobian.cpp. A structured stack still disperses -
 * dn/dlambda is real there, and the algebra would stay real - so a structured spectral sweep is a
 * genuine future optimisation, not a correctness gap. Stated here so it is not mistaken for done.
 */

#include "tf_internal.h"

#include <math.h>
#include <stdio.h>

extern "C" {

/* ---------------------------------------------------------------------------------------------
 * One point, one polarisation: r, t, T and each of their wavelength slopes.
 * ------------------------------------------------------------------------------------------- */

typedef struct tf_slope
{
    tf_complex r, t, d_r, d_t;
    double     T, d_T;
} tf_slope;

/* kappa' = (2 n n' - u') / (2 kappa). */
static tf_complex tf_dkappa(tf_complex n, tf_complex dn, tf_complex kappa, tf_complex du)
{
    const tf_complex two_n_dn = tf_c_scale(tf_c_mul(n, dn), 2.0);
    return tf_c_div(tf_c_sub(two_n_dn, du), tf_c_scale(kappa, 2.0));
}

static tf_complex tf_deta(tf_complex n, tf_complex dn, tf_complex kappa, tf_complex dkappa, int pol)
{
    if (pol == TF_POL_S) return dkappa;

    /* eta_p = n^2/kappa  ->  2 n n'/kappa - n^2 kappa'/kappa^2 */
    const tf_complex a = tf_c_div(tf_c_scale(tf_c_mul(n, dn), 2.0), kappa);
    const tf_complex b = tf_c_div(tf_c_mul(tf_c_mul(n, n), dkappa), tf_c_mul(kappa, kappa));
    return tf_c_sub(a, b);
}

static void tf_solve_slope(const tf_problem* p, int32_t point, int pol, tf_slope* out)
{
    const int32_t wi = p->point_wavelength[point];
    const double  wv = p->wavelength[wi];
    const double  s0 = sin(p->point_angle[point]);

    const tf_complex n0  = tf_index_internal(tf_incident_at(p, point));
    const tf_complex dn0 = tf_index_internal(tf_dincident_at(p, point));

    /* u = (n0 sin theta0)^2,  u' = 2 n0 n0' sin^2 theta0 */
    const tf_complex n0s = tf_c_scale(n0, s0);
    const tf_complex u   = tf_c_mul(n0s, n0s);
    const tf_complex du  = tf_c_scale(tf_c_mul(n0, dn0), 2.0 * s0 * s0);

    const tf_complex k0  = tf_layer_kappa(n0, u);
    const tf_complex dk0 = tf_dkappa(n0, dn0, k0, du);
    const tf_complex eta0  = tf_layer_admittance(n0, k0, pol);
    const tf_complex deta0 = tf_deta(n0, dn0, k0, dk0, pol);

    const tf_complex ns  = tf_index_internal(tf_exit_at(p, point));
    const tf_complex dns = tf_index_internal(tf_dexit_at(p, point));
    const tf_complex ks  = tf_layer_kappa(ns, u);
    const tf_complex dks = tf_dkappa(ns, dns, ks, du);
    const tf_complex etas  = tf_layer_admittance(ns, ks, pol);
    const tf_complex detas = tf_deta(ns, dns, ks, dks, pol);

    /* Forward accumulation of the product and its derivative together. */
    tf_matrix2 P = tf_m2_identity();
    tf_matrix2 A = { tf_c(0.0, 0.0), tf_c(0.0, 0.0), tf_c(0.0, 0.0), tf_c(0.0, 0.0) };

    const int rev = tf_reversed_at(p, point);
    for (int32_t i = 0; i < p->n_layers; ++i)
    {
        const int32_t   j  = tf_layer_at(p, rev, i);
        const int32_t   m  = p->layer_material[j];
        const int64_t   ix = (int64_t)m * p->n_wavelengths + wi;
        const tf_complex n  = tf_index_internal(p->material_index[ix]);
        const tf_complex dn = tf_index_internal(p->material_dindex_dwavelength[ix]);

        const tf_complex kappa  = tf_layer_kappa(n, u);
        const tf_complex dkappa = tf_dkappa(n, dn, kappa, du);
        const tf_complex eta    = tf_layer_admittance(n, kappa, pol);
        const tf_complex deta   = tf_deta(n, dn, kappa, dkappa, pol);

        const double scale = TF_TWO_PI * p->layer_thickness[j] / wv;
        tf_complex delta = tf_c_scale(kappa, scale);

        /* delta' = (2 pi d / lambda)(kappa' - kappa/lambda) */
        tf_complex ddelta = tf_c_scale(tf_c_sub(dkappa, tf_c_scale(kappa, 1.0 / wv)), scale);

        if (tf_layer_clamp_delta(&delta))
        {
            /* AN OPAQUE LAYER'S PHASE NO LONGER MOVES. Once the clamp fires, r is the Fresnel
             * coefficient of a semi-infinite medium: the common exponential factor cancels out of
             * the ratio C/B, so delta drops out of the answer entirely and its derivative with it.
             * eta does NOT drop out - a thick metal's reflectance still varies with colour through
             * n(lambda) - so deta survives. Zeroing both would lose the dispersion of every mirror
             * in the stack; zeroing neither would report the derivative of a clamping artifact. */
            ddelta = tf_c(0.0, 0.0);
        }

        const tf_matrix2 Mj = tf_layer_matrix(delta, eta);

        /* dM_j/dlambda = (dM/d delta) delta' + (dM/d eta) eta' */
        tf_matrix2 dMj;
        {
            const tf_matrix2 a = tf_layer_matrix_ddelta(delta, eta);
            const tf_matrix2 b = tf_layer_matrix_deta(delta, eta);
            dMj.m00 = tf_c_add(tf_c_mul(a.m00, ddelta), tf_c_mul(b.m00, deta));
            dMj.m01 = tf_c_add(tf_c_mul(a.m01, ddelta), tf_c_mul(b.m01, deta));
            dMj.m10 = tf_c_add(tf_c_mul(a.m10, ddelta), tf_c_mul(b.m10, deta));
            dMj.m11 = tf_c_add(tf_c_mul(a.m11, ddelta), tf_c_mul(b.m11, deta));
        }

        /* A <- A M + P dM,  then  P <- P M. In that order: A needs the OLD P. */
        {
            const tf_matrix2 AM  = tf_m2_mul(A, Mj);
            const tf_matrix2 PdM = tf_m2_mul(P, dMj);
            A.m00 = tf_c_add(AM.m00, PdM.m00);  A.m01 = tf_c_add(AM.m01, PdM.m01);
            A.m10 = tf_c_add(AM.m10, PdM.m10);  A.m11 = tf_c_add(AM.m11, PdM.m11);
        }
        P = tf_m2_mul(P, Mj);
    }

    /* [B; C] = M [1; eta_sub], and its derivative carries eta_sub' as well as M'. */
    const tf_complex B  = tf_c_add(P.m00, tf_c_mul(P.m01, etas));
    const tf_complex C  = tf_c_add(P.m10, tf_c_mul(P.m11, etas));
    const tf_complex dB = tf_c_add(tf_c_add(A.m00, tf_c_mul(A.m01, etas)), tf_c_mul(P.m01, detas));
    const tf_complex dC = tf_c_add(tf_c_add(A.m10, tf_c_mul(A.m11, etas)), tf_c_mul(P.m11, detas));

    const tf_complex e0B = tf_c_mul(eta0, B);
    const tf_complex D   = tf_c_add(e0B, C);
    const tf_complex N   = tf_c_sub(e0B, C);

    /* D' and N' pick up eta0' too - the incident medium disperses like any other. */
    const tf_complex e0dB_de0B = tf_c_add(tf_c_mul(eta0, dB), tf_c_mul(deta0, B));
    const tf_complex dD = tf_c_add(e0dB_de0B, dC);
    const tf_complex dN = tf_c_sub(e0dB_de0B, dC);

    const tf_complex r = tf_c_div(N, D);
    const tf_complex t = tf_c_div(tf_c_scale(eta0, 2.0), D);
    const double     d2 = tf_c_abs2(D);
    const double     T = 4.0 * eta0.re * etas.re / d2;

    out->r = r;
    out->t = t;
    out->T = T;

    /* r = N/D  ->  (N' - r D')/D */
    out->d_r = tf_c_div(tf_c_sub(dN, tf_c_mul(r, dD)), D);
    /* t = 2 eta0 / D  ->  (2 eta0' - t D')/D */
    out->d_t = tf_c_div(tf_c_sub(tf_c_scale(deta0, 2.0), tf_c_mul(t, dD)), D);
    /* T = 4 Re(eta0) Re(eta_sub) / |D|^2 */
    {
        const double dnum = 4.0 * (deta0.re * etas.re + eta0.re * detas.re);
        const double dden = 2.0 * (D.re * dD.re + D.im * dD.im);   /* d|D|^2 */
        out->d_T = dnum / d2 - T * dden / d2;
    }
}

/* ---------------------------------------------------------------------------------------------
 * tf_evaluate_wavelength_derivative
 * ------------------------------------------------------------------------------------------- */

TF_API tf_status tf_evaluate_wavelength_derivative(const tf_problem*      problem,
                                                   const tf_operands*      operands,
                                                   const tf_plan_buffers* plan,
                                                   tf_workspace*          workspace,
                                                   tf_spectral_output*    out)
{
    (void)workspace;   /* forward accumulation needs none; see the header */

    if (problem == 0 || operands == 0 || plan == 0 || out == 0) return TF_NULL_POINTER;
    if (out->status == 0 || out->value == 0 || out->dvalue_dwavelength == 0) return TF_NULL_POINTER;
    if (plan->point_path == 0 || plan->point_polarisation == 0 || plan->report == 0)
        return TF_PLAN_NOT_RUN;

    int32_t* status = out->status;
    *status = TF_OK;

    /* SPANS ARE NOT WIRED THROUGH THIS ENTRY POINT YET. Step 3a of the evaluation model took
     * them through tf_evaluate and tf_evaluate_jacobian - the two an optimiser needs - and the
     * chain through a reduction is the same here, but it is not written. Reading slot zero and
     * calling it the operand would be a plausible number for a band average, which is exactly the
     * kind of answer this engine refuses to give. */
    /* d/d(lambda) OF A MULTI-WAVELENGTH OPERAND NAMES NO SINGLE WAVELENGTH, and that is a
     * DEFINITION rather than a gap. The needle map and the index derivative took spans and the
     * operand DAG in step 3a-ii; this one did not, because the quantity is not defined.
     *
     * A pointwise operand has one wavelength and d(value)/d(lambda) means what it says. A band
     * average over forty samples does not: moving "the" wavelength could mean shifting every
     * sample rigidly or moving one of them, and those are different numbers with different uses.
     * The chain rule would compute SOMETHING either way, which is exactly the trap.
     *
     * Nothing needs it. This call exists for group delay and its higher orders, which are
     * pointwise phase derivatives. CROSSING was the other candidate and step 3b showed it does
     * not need this at all - a crossing is defined on the sampled spectrum. So the refusal is the
     * right answer rather than a deferred one, and it stands until something asks for a spectral
     * shift and says which kind it means.
     *
     * It is checked BEFORE the dispersion data below: a table shape that cannot be answered should
     * not first be asked for data it would never use. */
    if (plan->report->max_operand_span > 1 || plan->report->n_operand_levels > 1)
    {
        TF_NOT_IMPLEMENTED(status, TF_NOT_IMPLEMENTED_REDUCTION,
            "d/d(lambda) of an operand spanning several wavelengths, or of one that reads operands "
            "that do. The quantity is undefined rather than unimplemented: shifting every sample "
            "rigidly and moving one sample are different numbers, and nothing here can tell which "
            "was meant. Group delay, which is what this call is for, is pointwise.");
        return TF_NOT_IMPLEMENTED_REDUCTION;
    }

    if (problem->material_dindex_dwavelength == 0 ||
        problem->dindex_incident_dwavelength == 0 ||
        problem->dindex_exit_dwavelength == 0)
    {
        *status = TF_NULL_POINTER;
        fprintf(stderr,
                "\n*** THINFILM: no dispersion data ***\n"
                "    A wavelength derivative was asked for and dn/dlambda was not supplied.\n"
                "    material_index is a table of values, so nothing here can differentiate it -\n"
                "    the caller owns the material model and is the only one that can.\n"
                "    A NULL is NOT read as zero on purpose: dispersion is the dominant term in\n"
                "    group delay for a real coating, and the answer would be smooth, plausible\n"
                "    and wrong by a factor. A medium that really is non-dispersive is declared\n"
                "    with an array of zeros.\n\n");
        return TF_NULL_POINTER;
    }


    /* ONE STACK, AND A TRIAL AXIS DOES NOT SAY WHICH. A group delay is d/dlambda of one design's spectrum. The nominal stack is the obvious
     * guess and that is why it is refused: it would produce a plausible number. Averaging over
     * trials is equally defensible and gives a different one, and nothing has said which is
     * meant. tf_evaluate and tf_evaluate_jacobian define it because an operand's trial reduction
     * says what to do with the axis; nothing here has that. See tf_trial_reduction. */
    if (tf_uses_trials(problem, operands))
    {
        TF_NOT_IMPLEMENTED(status, TF_NOT_IMPLEMENTED_TRIAL,
            "this entry point was asked for while an operand reduces over the Monte Carlo trial "
            "axis. It is defined on one stack and which of the trials it means has not been "
            "settled.");
        return TF_NOT_IMPLEMENTED_TRIAL;
    }

    if (tf_has_back(problem))
    {
        TF_NOT_IMPLEMENTED(status, TF_NOT_IMPLEMENTED_INCOHERENT,
            "this derivative with the substrate's back surface included. The thickness Jacobian "
            "and the needle map handle it; this entry point does not yet.");
        return TF_NOT_IMPLEMENTED_INCOHERENT;
    }

    if (plan->report->n_unsupported > 0)
    {
        TF_NOT_IMPLEMENTED(status, TF_NOT_IMPLEMENTED_INCOHERENT,
            "the plan found points needing a path that is not implemented - massive layers.");
        return TF_NOT_IMPLEMENTED_INCOHERENT;
    }

    for (int32_t r = 0; r < operands->n_operands; ++r)
    {
        const int32_t point = tf_slot_point(operands, r);
        const int32_t need  = plan->point_polarisation[point];

        /* The amplitudes and their slopes, for the polarisations this point actually needs. What
         * was not asked for stays NaN, exactly as in tf_amplitudes - a mask that disagrees with
         * the operands must produce something unmistakable rather than a plausible slope. */
        const double nan_ = nan("");
        tf_point_amplitudes a;
        a.r_s = a.r_p = a.t_s = a.t_p = tf_c(nan_, nan_);
        a.R_s = a.R_p = a.T_s = a.T_p = nan_;

        tf_complex dr_s = tf_c(nan_, nan_), dr_p = tf_c(nan_, nan_);
        tf_complex dt_s = tf_c(nan_, nan_), dt_p = tf_c(nan_, nan_);
        double     dT_s = nan_,             dT_p = nan_;

        if (need & TF_NEED_S)
        {
            tf_slope s;
            tf_solve_slope(problem, point, TF_POL_S, &s);
            a.r_s = s.r; a.t_s = s.t; a.T_s = s.T; a.R_s = tf_c_abs2(s.r);
            dr_s = s.d_r; dt_s = s.d_t; dT_s = s.d_T;
        }
        if (need & TF_NEED_P)
        {
            tf_slope s;
            tf_solve_slope(problem, point, TF_POL_P, &s);
            a.r_p = s.r; a.t_p = s.t; a.T_p = s.T; a.R_p = tf_c_abs2(s.r);
            dr_p = s.d_r; dt_p = s.d_t; dT_p = s.d_T;
        }

        double value = 0.0;
        if (!tf_target_value(operands->code[r], &a, operands->delta_convention, tf_param1_of(operands, r), &value))
        {
            TF_NOT_IMPLEMENTED(status, TF_NOT_IMPLEMENTED_TARGET, "unrecognised target code");
            return TF_NOT_IMPLEMENTED_TARGET;
        }

        /* THE SAME CHAIN RULE THE JACOBIAN USES. d(Rs) = 2 Re(conj(r_s) d r_s) whether d r_s came
         * from a thickness or from a wavelength - the rule does not care what was perturbed. One
         * definition means a target added later cannot be wired into one derivative and not the
         * other. */
        double slope = 0.0;
        if (!tf_target_derivative(operands->code[r], &a, dr_s, dr_p, dt_s, dt_p,
                                  tf_dabs2(a.r_s, dr_s), tf_dabs2(a.r_p, dr_p), dT_s, dT_p,
                                        tf_param1_of(operands, r), &slope))
        {
            TF_NOT_IMPLEMENTED(status, TF_NOT_IMPLEMENTED_TARGET,
                "a target whose derivative is not written");
            return TF_NOT_IMPLEMENTED_TARGET;
        }

        out->value[r]              = value;
        out->dvalue_dwavelength[r] = slope;
    }

    return TF_OK;
}

} /* extern "C" */
