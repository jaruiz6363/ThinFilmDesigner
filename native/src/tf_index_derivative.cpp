/* tf_index_derivative.cpp — d(value)/dn and d(value)/dk, per material.
 *
 * THE CHAIN. Write ' for d/dN, the complex index of the layer's material. The conserved transverse
 * quantity u = (n0 sin theta0)^2 does NOT move - it belongs to the incident medium, not to a layer
 * material - so this chain is shorter than the wavelength one:
 *
 *     kappa  = sqrt(N^2 - u)            kappa' = N / kappa
 *     eta_s  = kappa                    eta_s' = N / kappa
 *     eta_p  = N^2 / kappa              eta_p' = 2N/kappa - N^3/kappa^3
 *     delta  = (2 pi d / lambda) kappa  delta' = (2 pi d / lambda) N / kappa
 *
 *     dM_j/dN = (dM/d delta) delta' + (dM/d eta) eta'
 *
 * the same two terms as the wavelength derivative, reusing the same two matrix helpers.
 *
 * REVERSE SWEEP, ACCUMULATED PER MATERIAL. There are n_materials parameters, not one, so forward
 * mode would cost a chain per material and reverse mode wins as it does for thickness:
 *
 *     dM/dN_m = sum over every layer j made of m  of  prefix[j] (dM_j/dN) S
 *
 * One forward pass storing prefix, one backward pass carrying S, and each layer adds into the
 * accumulator belonging to its material. A material in ten layers costs no more than one in one.
 *
 * TWO REAL ANSWERS FROM ONE COMPLEX SWEEP. dN/dn = 1 and dN/dk = -i, because the ABI takes n + ik
 * while the engine works in n - ik. Everything downstream of dB and dC is linear, so the whole
 * amplitude-to-target chain is simply run twice - once with (dB, dC) and once with (-i dB, -i dC).
 */

#include "tf_internal.h"

#include <math.h>
#include <stdio.h>

extern "C" {

/* dM_j/dN for one layer. */
static tf_matrix2 tf_layer_matrix_dindex(tf_complex n, tf_complex u, tf_complex kappa,
                                         tf_complex eta, tf_complex delta,
                                         double thickness, double wavelength, int pol,
                                         int delta_is_clamped)
{
    (void)u;
    const tf_complex dkappa = tf_c_div(n, kappa);          /* N / kappa */

    tf_complex deta;
    if (pol == TF_POL_S)
    {
        deta = dkappa;
    }
    else
    {
        /* 2N/kappa - N^3/kappa^3 */
        const tf_complex a = tf_c_div(tf_c_scale(n, 2.0), kappa);
        const tf_complex n3 = tf_c_mul(tf_c_mul(n, n), n);
        const tf_complex k3 = tf_c_mul(tf_c_mul(kappa, kappa), kappa);
        deta = tf_c_sub(a, tf_c_div(n3, k3));
    }

    /* delta' = (2 pi d / lambda) kappa'. A clamped layer's phase has stopped depending on
     * anything - the common exponential factor cancels out of C/B - so its delta term goes, for
     * exactly the reason the thickness Jacobian drops its own. eta' survives: an opaque layer
     * still presents a semi-infinite Fresnel coefficient, and that coefficient is governed by its
     * index. */
    tf_complex ddelta = tf_c(0.0, 0.0);
    if (!delta_is_clamped)
        ddelta = tf_c_scale(dkappa, TF_TWO_PI * thickness / wavelength);

    const tf_matrix2 a = tf_layer_matrix_ddelta(delta, eta);
    const tf_matrix2 b = tf_layer_matrix_deta(delta, eta);

    tf_matrix2 m;
    m.m00 = tf_c_add(tf_c_mul(a.m00, ddelta), tf_c_mul(b.m00, deta));
    m.m01 = tf_c_add(tf_c_mul(a.m01, ddelta), tf_c_mul(b.m01, deta));
    m.m10 = tf_c_add(tf_c_mul(a.m10, ddelta), tf_c_mul(b.m10, deta));
    m.m11 = tf_c_add(tf_c_mul(a.m11, ddelta), tf_c_mul(b.m11, deta));
    return m;
}

/* Everything downstream of (dB, dC) — shared by the n pass and the k pass, which differ only by a
 * factor of -i applied before entry. */
typedef struct tf_amp_slope { tf_complex d_r, d_t; double d_T; } tf_amp_slope;

static tf_amp_slope tf_amplitudes_from_dBC(tf_complex dB, tf_complex dC,
                                           tf_complex eta0, tf_complex D,
                                           tf_complex r, tf_complex t, double T)
{
    const tf_complex e0dB = tf_c_mul(eta0, dB);
    const tf_complex dD = tf_c_add(e0dB, dC);
    const tf_complex dN = tf_c_sub(e0dB, dC);

    tf_amp_slope s;
    s.d_r = tf_c_div(tf_c_sub(dN, tf_c_mul(r, dD)), D);
    s.d_t = tf_c_div(tf_c_scale(tf_c_mul(t, dD), -1.0), D);
    s.d_T = -T * 2.0 * (D.re * dD.re + D.im * dD.im) / tf_c_abs2(D);
    return s;
}

/* ---------------------------------------------------------------------------------------------
 * One point, one polarisation: fill acc[m] with dM/dN_m.
 * ------------------------------------------------------------------------------------------- */

static void tf_index_sweep(const tf_problem* p, int32_t point, int pol,
                           tf_matrix2* prefix, tf_matrix2* acc,
                           tf_complex* out_eta0, tf_complex* out_etasub)
{
    const int32_t n  = p->n_layers;
    const int32_t wi = p->point_wavelength[point];
    const double  wv = p->wavelength[wi];

    const tf_complex n0   = tf_index_internal(tf_incident_at(p, point));
    const tf_complex nsub = tf_index_internal(tf_exit_at(p, point));
    const tf_complex n0s  = tf_c_scale(n0, sin(p->point_angle[point]));
    const tf_complex u    = tf_c_mul(n0s, n0s);

    *out_eta0   = tf_layer_admittance(n0,   tf_layer_kappa(n0,   u), pol);
    *out_etasub = tf_layer_admittance(nsub, tf_layer_kappa(nsub, u), pol);

    for (int32_t m = 0; m < p->n_materials; ++m)
    {
        acc[m].m00 = tf_c(0.0, 0.0); acc[m].m01 = tf_c(0.0, 0.0);
        acc[m].m10 = tf_c(0.0, 0.0); acc[m].m11 = tf_c(0.0, 0.0);
    }

    /* THE CHAIN IS INDEXED BY TRAVERSAL POSITION; THE LAYER IT READS IS PHYSICAL. Under a
     * reversed configuration those differ, and conflating them would silently multiply the stack
     * in the wrong order while every index still looked plausible. */
    const int rev = tf_reversed_at(p, point);

    prefix[0] = tf_m2_identity();
    for (int32_t i = 0; i < n; ++i)
    {
        const int32_t j = tf_layer_at(p, rev, i);
        const int32_t mi = p->layer_material[j];
        const tf_complex nj = tf_index_internal(p->material_index[(int64_t)mi * p->n_wavelengths + wi]);
        const tf_complex kappa = tf_layer_kappa(nj, u);
        const tf_complex eta   = tf_layer_admittance(nj, kappa, pol);
        tf_complex delta = tf_c_scale(kappa, TF_TWO_PI * p->layer_thickness[j] / wv);
        tf_layer_clamp_delta(&delta);
        prefix[i + 1] = tf_m2_mul(prefix[i], tf_layer_matrix(delta, eta));
    }

    tf_matrix2 S = tf_m2_identity();
    for (int32_t i = n - 1; i >= 0; --i)
    {
        const int32_t j = tf_layer_at(p, rev, i);
        const int32_t mi = p->layer_material[j];
        const tf_complex nj = tf_index_internal(p->material_index[(int64_t)mi * p->n_wavelengths + wi]);
        const tf_complex kappa = tf_layer_kappa(nj, u);
        const tf_complex eta   = tf_layer_admittance(nj, kappa, pol);
        tf_complex delta = tf_c_scale(kappa, TF_TWO_PI * p->layer_thickness[j] / wv);
        const int clamped = tf_layer_clamp_delta(&delta);

        const tf_matrix2 dMj = tf_layer_matrix_dindex(nj, u, kappa, eta, delta,
                                                      p->layer_thickness[j], wv, pol, clamped);
        const tf_matrix2 term = tf_m2_mul(tf_m2_mul(prefix[i], dMj), S);

        /* EVERY LAYER OF A MATERIAL ADDS INTO THE SAME ACCUMULATOR. Varying a material moves all
         * of its layers at once, which is what a real deposition does. */
        acc[mi].m00 = tf_c_add(acc[mi].m00, term.m00);
        acc[mi].m01 = tf_c_add(acc[mi].m01, term.m01);
        acc[mi].m10 = tf_c_add(acc[mi].m10, term.m10);
        acc[mi].m11 = tf_c_add(acc[mi].m11, term.m11);

        S = tf_m2_mul(tf_layer_matrix(delta, eta), S);
    }
}

/* ---------------------------------------------------------------------------------------------
 * tf_evaluate_index_derivative
 * ------------------------------------------------------------------------------------------- */

TF_API tf_status tf_evaluate_index_derivative(const tf_problem*      problem,
                                              const tf_operands*      operands,
                                              const tf_plan_buffers* plan,
                                              tf_workspace*          workspace,
                                              tf_index_output*       out)
{
    if (problem == 0 || operands == 0 || plan == 0 || workspace == 0 || out == 0)
        return TF_NULL_POINTER;
    if (out->status == 0 || out->value == 0 || out->dvalue_dn == 0 || out->dvalue_dk == 0)
        return TF_NULL_POINTER;
    if (plan->point_path == 0 || plan->point_polarisation == 0 || plan->report == 0)
        return TF_PLAN_NOT_RUN;

    int32_t* status = out->status;
    *status = TF_OK;

    /* ONE STACK, AND A TRIAL AXIS DOES NOT SAY WHICH. An index derivative is dv/dn of one design. The nominal stack is the obvious
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

    const int32_t n  = problem->n_layers;
    const int32_t nm = problem->n_materials;
    const int32_t nr = operands->n_operands;

    const int32_t n_slots  = tf_n_slots(operands);
    const int32_t max_span = plan->report->max_operand_span;
    const int64_t need = (int64_t)(n + 1) * 8 + (int64_t)nm * 8
                       + (int64_t)n_slots + (int64_t)max_span * 5;
    if (workspace->scratch == 0 || workspace->scratch_doubles < need)
    {
        *status = TF_SCRATCH_TOO_SMALL;
        fprintf(stderr,
                "\n*** THINFILM: scratch too small ***\n"
                "    have %lld doubles, need %lld for %d materials over %d layers.\n\n",
                (long long)workspace->scratch_doubles, (long long)need, (int)nm, (int)n);
        return TF_SCRATCH_TOO_SMALL;
    }

    double* cursor = workspace->scratch;
    tf_matrix2* prefix = (tf_matrix2*)cursor;  cursor += (int64_t)(n + 1) * 8;
    tf_matrix2* acc    = (tf_matrix2*)cursor;  cursor += (int64_t)nm * 8;
    double*     slope  = cursor;               cursor += (int64_t)n_slots;
    double*     xbuf   = cursor;               cursor += (int64_t)max_span * 4;
    double*     dvdx   = cursor;

    for (int64_t i = 0; i < (int64_t)nm * nr; ++i) { out->dvalue_dn[i] = 0.0; out->dvalue_dk[i] = 0.0; }

    /* Pass A gives every operand's value and the reduction's local slope at every slot. The two
     * output matrices then ARE the working matrices - they are operand-indexed, so the DAG pass
     * below can accumulate into them in place, in topological order. */
    if (!tf_operand_pass(problem, operands, plan, xbuf, dvdx, out->value, slope, 0, 0))
    {
        TF_NOT_IMPLEMENTED(status, TF_NOT_IMPLEMENTED_TARGET, "unrecognised target code");
        return TF_NOT_IMPLEMENTED_TARGET;
    }

    if (n == 0 || nm == 0) return TF_OK;   /* no layers: no material can move anything */

    for (int32_t point = 0; point < problem->n_points; ++point)
    {
        const int32_t need_pol = plan->point_polarisation[point];
        if (need_pol == 0) continue;

        tf_point_amplitudes amp;
        tf_amplitudes(problem, point, plan->point_path[point], need_pol, &amp);

        for (int pol = 0; pol < 2; ++pol)
        {
            if (!(need_pol & ((pol == TF_POL_S) ? TF_NEED_S : TF_NEED_P))) continue;

            tf_complex eta0, etasub;
            tf_index_sweep(problem, point, pol, prefix, acc, &eta0, &etasub);

            const tf_matrix2 M = prefix[n];
            const tf_complex B = tf_c_add(M.m00, tf_c_mul(M.m01, etasub));
            const tf_complex C = tf_c_add(M.m10, tf_c_mul(M.m11, etasub));
            const tf_complex D = tf_c_add(tf_c_mul(eta0, B), C);
            const tf_complex r_ = tf_c_div(tf_c_sub(tf_c_mul(eta0, B), C), D);
            const tf_complex t_ = tf_c_div(tf_c_scale(eta0, 2.0), D);
            const double     T_ = 4.0 * eta0.re * etasub.re / tf_c_abs2(D);

            for (int32_t m = 0; m < nm; ++m)
            {
                const tf_complex dB = tf_c_add(acc[m].m00, tf_c_mul(acc[m].m01, etasub));
                const tf_complex dC = tf_c_add(acc[m].m10, tf_c_mul(acc[m].m11, etasub));

                /* dN/dn = 1, dN/dk = -i. Multiplying a complex by -i is a swap and a negation. */
                const tf_complex dB_k = tf_c(dB.im, -dB.re);
                const tf_complex dC_k = tf_c(dC.im, -dC.re);

                const tf_amp_slope sn = tf_amplitudes_from_dBC(dB,   dC,   eta0, D, r_, t_, T_);
                const tf_amp_slope sk = tf_amplitudes_from_dBC(dB_k, dC_k, eta0, D, r_, t_, T_);

                const int32_t ef = plan->point_span_first[point];
                const int32_t ec = plan->point_span_count[point];

                for (int32_t e = 0; e < ec; ++e)
                {
                    const int32_t r    = plan->point_span_operand[ef + e];
                    const int32_t slot = plan->point_span_slot[ef + e];
                    const double  lw   = slope[slot];   /* the reduction's local weight */

                    const int s_pol = (pol == TF_POL_S);
                    const tf_complex zero = tf_c(0.0, 0.0);

                    double dn = 0.0, dk = 0.0;
                    if (!tf_target_derivative(operands->code[r], &amp,
                            s_pol ? sn.d_r : zero, s_pol ? zero : sn.d_r,
                            s_pol ? sn.d_t : zero, s_pol ? zero : sn.d_t,
                            s_pol ? tf_dabs2(amp.r_s, sn.d_r) : 0.0, s_pol ? 0.0 : tf_dabs2(amp.r_p, sn.d_r),
                            s_pol ? sn.d_T : 0.0,  s_pol ? 0.0  : sn.d_T,
                            tf_param1_of(operands, r), &dn) ||
                        !tf_target_derivative(operands->code[r], &amp,
                            s_pol ? sk.d_r : zero, s_pol ? zero : sk.d_r,
                            s_pol ? sk.d_t : zero, s_pol ? zero : sk.d_t,
                            s_pol ? tf_dabs2(amp.r_s, sk.d_r) : 0.0, s_pol ? 0.0 : tf_dabs2(amp.r_p, sk.d_r),
                            s_pol ? sk.d_T : 0.0,  s_pol ? 0.0  : sk.d_T,
                            tf_param1_of(operands, r), &dk))
                    {
                        TF_NOT_IMPLEMENTED(status, TF_NOT_IMPLEMENTED_TARGET,
                            "a target whose derivative is not written");
                        return TF_NOT_IMPLEMENTED_TARGET;
                    }

                    out->dvalue_dn[(int64_t)m * nr + r] += lw * dn;
                    out->dvalue_dk[(int64_t)m * nr + r] += lw * dk;
                }
            }
        }
    }


    /* THE CHAIN RULE THROUGH THE DAG, in the plan's order so an operand's inputs are already
     * accumulated when it is reached. The two output matrices are operand-indexed, so this
     * accumulates into them in place and needs no working copy. */
    for (int32_t kk = 0; kk < nr; ++kk)
    {
        const int32_t o = plan->operand_order[kk];
        if (!tf_is_derived(operands, o)) continue;

        const int32_t f = tf_input_first(operands, o), ic = tf_input_count(operands, o);
        double in[2], d[2], v = 0.0;   /* TF_FIXED_OK: two, as every derived code takes two */
        for (int32_t i = 0; i < ic; ++i) in[i] = out->value[operands->input_index[f + i]];
        if (!tf_derived_value(operands->code[o], in, ic, d, &v))
        {
            TF_NOT_IMPLEMENTED(status, TF_NOT_IMPLEMENTED_TARGET,
                "a derived operand whose derivative is not written");
            return TF_NOT_IMPLEMENTED_TARGET;
        }

        for (int32_t m = 0; m < nm; ++m)
        {
            double an = 0.0, ak = 0.0;
            for (int32_t i = 0; i < ic; ++i)
            {
                const int32_t src = operands->input_index[f + i];
                an += d[i] * out->dvalue_dn[(int64_t)m * nr + src];
                ak += d[i] * out->dvalue_dk[(int64_t)m * nr + src];
            }
            out->dvalue_dn[(int64_t)m * nr + o] = an;
            out->dvalue_dk[(int64_t)m * nr + o] = ak;
        }
    }

    return TF_OK;
}

} /* extern "C" */
