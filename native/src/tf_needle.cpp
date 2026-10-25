/* tf_needle.cpp — the needle perturbation function.
 *
 * The algebra is in the header. What follows is the sweep, which is the thickness Jacobian's
 * backward sweep with a second job: at each layer it also walks the insertion depths that fall
 * inside that layer, in descending order, because the sweep runs back to front.
 *
 * ONE PREFIX CHAIN, TWO POLARISATIONS, as everywhere else in this engine. Every target is linear
 * in the amplitude derivatives once the amplitudes are known, so the s contribution and the p
 * contribution accumulate into the same map in separate passes.
 *
 * THE RESIDUALS ARE NEEDED BEFORE THE SWEEP, because d(sum r^2)/d(eps) = 2 sum_r r_r dr_r/d(eps)
 * weights each target's slope by where that target currently stands. So the residual pass runs
 * first, through the same tf_score_of every other entry point uses.
 */

#include "tf_internal.h"

#include <math.h>
#include <stdio.h>

extern "C" {

/* ---------------------------------------------------------------------------------------------
 * The insertion generator.
 *
 *     G_m = (2 pi kappa_m / lambda) [[0, i/eta_m], [i eta_m, 0]]
 *
 * which is dM_m/d(thickness) evaluated at zero thickness: at delta = 0 the cosines are one and the
 * sines are zero, so the diagonal of dM/d(delta) vanishes and only the off-diagonal survives.
 * Depends on the material and the point; never on the depth. That is why it is computed once per
 * candidate per point and kept, rather than rebuilt at every sample.
 * ------------------------------------------------------------------------------------------- */

static tf_matrix2 tf_insertion_generator(tf_complex n, tf_complex n0_sin_sq, double wavelength,
                                         int pol)
{
    const tf_complex kappa = tf_layer_kappa(n, n0_sin_sq);
    const tf_complex eta   = tf_layer_admittance(n, kappa, pol);
    const tf_complex c     = tf_c_scale(kappa, TF_TWO_PI / wavelength);

    tf_matrix2 g;
    g.m00 = tf_c(0.0, 0.0);
    g.m11 = tf_c(0.0, 0.0);
    {
        const tf_complex a = tf_c_div(c, eta);    g.m01 = tf_c(-a.im, a.re);   /* i c / eta */
        const tf_complex b = tf_c_mul(c, eta);    g.m10 = tf_c(-b.im, b.re);   /* i c eta   */
    }
    return g;
}

/* ---------------------------------------------------------------------------------------------
 * One point, one polarisation.
 * ------------------------------------------------------------------------------------------- */

typedef struct tf_needle_ctx
{
    const tf_problem*        p;
    const tf_operands*        t;
    const tf_needle_request* req;
    const double*            residual;     /* [n_operands] */
    const double*            dres;         /* [n_operands] d(residual)/d(value) */
    const int32_t*           row;          /* [n_operands] -> residual row, or -1 */
    double*                  map;          /* [n_candidates * n_depths] accumulated */
    const tf_plan_buffers*   plan;
    tf_matrix2*              prefix;       /* [n_layers + 1] */
    tf_matrix2*              gen;          /* [n_candidates] */
    double*                  zfront;       /* [n_layers + 1] layer boundaries, forward-accumulated */
} tf_needle_ctx;

static void tf_needle_sweep(tf_needle_ctx* ctx, int32_t point, int pol,
                            const tf_point_amplitudes* amp)
{
    const tf_problem* p = ctx->p;
    const int32_t n  = p->n_layers;
    const int32_t wi = p->point_wavelength[point];
    const double  wv = p->wavelength[wi];

    const tf_complex n0   = tf_index_internal(tf_incident_at(p, point));
    const tf_complex nsub = tf_index_internal(tf_exit_at(p, point));
    const tf_complex n0s  = tf_c_scale(n0, sin(p->point_angle[point]));
    const tf_complex u    = tf_c_mul(n0s, n0s);

    const tf_complex eta0   = tf_layer_admittance(n0,   tf_layer_kappa(n0,   u), pol);
    const tf_complex etasub = tf_layer_admittance(nsub, tf_layer_kappa(nsub, u), pol);

    /* The insertion generators, once per candidate. */
    for (int32_t c = 0; c < ctx->req->n_candidates; ++c)
    {
        const int32_t m = ctx->req->candidate_material[c];
        const tf_complex nm = tf_index_internal(p->material_index[(int64_t)m * p->n_wavelengths + wi]);
        ctx->gen[c] = tf_insertion_generator(nm, u, wv, pol);
    }

    /* Forward sweep: the prefix chain, and the per-layer phase constants reused going back. */
    ctx->prefix[0] = tf_m2_identity();
    for (int32_t j = 0; j < n; ++j)
    {
        const int32_t m = p->layer_material[j];
        const tf_complex nj = tf_index_internal(p->material_index[(int64_t)m * p->n_wavelengths + wi]);
        const tf_complex kappa = tf_layer_kappa(nj, u);
        const tf_complex eta   = tf_layer_admittance(nj, kappa, pol);
        const tf_complex delta = tf_c_scale(kappa, TF_TWO_PI * p->layer_thickness[j] / wv);
        ctx->prefix[j + 1] = tf_m2_mul(ctx->prefix[j], tf_layer_matrix(delta, eta));
    }

    const tf_matrix2 M = ctx->prefix[n];
    const tf_complex B = tf_c_add(M.m00, tf_c_mul(M.m01, etasub));
    const tf_complex C = tf_c_add(M.m10, tf_c_mul(M.m11, etasub));
    const tf_complex D = tf_c_add(tf_c_mul(eta0, B), C);
    const tf_complex r = tf_c_div(tf_c_sub(tf_c_mul(eta0, B), C), D);
    const tf_complex t = tf_c_div(tf_c_scale(eta0, 2.0), D);
    const double     T = 4.0 * eta0.re * etasub.re / tf_c_abs2(D);

    /* The back surface, as in the thickness Jacobian's sweep. */
    const int back = tf_has_back(p);
    tf_complex rr = tf_c(0.0, 0.0), Dr = tf_c(1.0, 0.0);
    tf_incoherent inc = { 0.0, 0.0, 0.0, 0.0, 0.0, 0.0 };
    if (back)
    {
        rr  = tf_reverse_r(M, eta0, etasub, &Dr);
        inc = tf_incoherent_sum(tf_c_abs2(r), T, tf_c_abs2(rr), tf_back_surface(p, point, pol, u));
    }

    /* WHERE EACH LAYER BEGINS, ACCUMULATED FORWARD AND STORED.
     *
     * The obvious economy - carry one running z_back and subtract each thickness going backwards -
     * is wrong, and wrong in a way that shows up at exactly one depth. Summing forward and then
     * subtracting back does not retrace the same floating-point values, so the computed front of
     * layer 0 comes out as a few times 1e-14 rather than zero. A caller asking for a sample at
     * depth 0.0 then fails "depth >= z_front" in every layer and falls through the whole sweep,
     * leaving that entry of the map silently at zero. Every interior seam has an earlier layer to
     * catch it; the front of the stack has nothing behind it.
     *
     * Accumulating forward puts the boundaries on exactly the values a caller computes when it
     * chooses its depths, which is the only way the comparison can be reliable. */
    ctx->zfront[0] = 0.0;
    for (int32_t j = 0; j < n; ++j) ctx->zfront[j + 1] = ctx->zfront[j] + p->layer_thickness[j];
    double z_back = ctx->zfront[n];

    /* The depths are ascending, so walking layers back to front walks them from the end. */
    int32_t di = ctx->req->n_depths - 1;

    tf_matrix2 S = tf_m2_identity();
    for (int32_t j = n - 1; j >= 0; --j)
    {
        const int32_t m = p->layer_material[j];
        const tf_complex nj = tf_index_internal(p->material_index[(int64_t)m * p->n_wavelengths + wi]);
        const tf_complex kappa = tf_layer_kappa(nj, u);
        const tf_complex eta   = tf_layer_admittance(nj, kappa, pol);
        const double     dj    = p->layer_thickness[j];
        const double     z_front = ctx->zfront[j];

        /* Depths inside [z_front, z_back). The last layer takes its closing endpoint as well, so
         * that a depth exactly at the back of the stack is not silently dropped. */
        while (di >= 0 && ctx->req->depth[di] >= z_front &&
               (ctx->req->depth[di] < z_back || (j == n - 1 && ctx->req->depth[di] <= z_back)))
        {
            const double tt = ctx->req->depth[di] - z_front;

            /* L = prefix[j] M_j(t),  R = M_j(d - t) S. Two layer matrices, two products. */
            const tf_complex d_a = tf_c_scale(kappa, TF_TWO_PI * tt / wv);
            const tf_complex d_b = tf_c_scale(kappa, TF_TWO_PI * (dj - tt) / wv);
            const tf_matrix2 L = tf_m2_mul(ctx->prefix[j], tf_layer_matrix(d_a, eta));
            const tf_matrix2 R = tf_m2_mul(tf_layer_matrix(d_b, eta), S);

            for (int32_t c = 0; c < ctx->req->n_candidates; ++c)
            {
                const tf_matrix2 dM = tf_m2_mul(tf_m2_mul(L, ctx->gen[c]), R);

                const tf_complex dB = tf_c_add(dM.m00, tf_c_mul(dM.m01, etasub));
                const tf_complex dC = tf_c_add(dM.m10, tf_c_mul(dM.m11, etasub));
                const tf_complex dD = tf_c_add(tf_c_mul(eta0, dB), dC);
                const tf_complex dN = tf_c_sub(tf_c_mul(eta0, dB), dC);

                const tf_complex d_r = tf_c_div(tf_c_sub(dN, tf_c_mul(r, dD)), D);
                const tf_complex d_t = tf_c_div(tf_c_scale(tf_c_mul(t, dD), -1.0), D);
                double           d_T = -T * 2.0 * (D.re * dD.re + D.im * dD.im) / tf_c_abs2(D);
                double           d_R = tf_dabs2((pol == TF_POL_S) ? amp->r_s : amp->r_p, d_r);
                if (back)
                {
                    const double dRr = tf_dabs2(rr, tf_reverse_dr(dM, eta0, etasub, rr, Dr));
                    d_R += inc.R_Tf * d_T + inc.R_Rr * dRr;
                    d_T  = inc.T_Tf * d_T + inc.T_Rr * dRr;
                }

                /* WHICH SLOTS READ THIS POINT, from the plan's transpose - the same inversion the
                 * Jacobian walks. Before spans this was a scan over operands testing point
                 * equality, which a band operand makes wrong as well as slow. */
                const int32_t ef = ctx->plan->point_span_first[point];
                const int32_t ec = ctx->plan->point_span_count[point];

                for (int32_t e = 0; e < ec; ++e)
                {
                    const int32_t k    = ctx->plan->point_span_operand[ef + e];
                    const int32_t slot = ctx->plan->point_span_slot[ef + e];

                    /* The adjoint already folded onto this slot: zero for a satisfied one-sided
                     * constraint, and zero for anything that feeds nothing scored. */
                    if (ctx->dres[slot] == 0.0) continue;

                    const tf_complex dr_s = (pol == TF_POL_S) ? d_r : tf_c(0.0, 0.0);
                    const tf_complex dr_p = (pol == TF_POL_P) ? d_r : tf_c(0.0, 0.0);
                    const tf_complex dt_s = (pol == TF_POL_S) ? d_t : tf_c(0.0, 0.0);
                    const tf_complex dt_p = (pol == TF_POL_P) ? d_t : tf_c(0.0, 0.0);
                    const double     dR_s = (pol == TF_POL_S) ? d_R : 0.0;
                    const double     dR_p = (pol == TF_POL_P) ? d_R : 0.0;
                    const double     dT_s = (pol == TF_POL_S) ? d_T : 0.0;
                    const double     dT_p = (pol == TF_POL_P) ? d_T : 0.0;

                    /* Group delay, GDD and TOD have no amplitude chain rule; tf_dispersion_needle adds them. */
                    if (tf_is_dispersion_code(ctx->t->code[k])) continue;

                    double dvalue = 0.0;
                    if (!tf_target_derivative(ctx->t->code[k], amp,
                                              dr_s, dr_p, dt_s, dt_p, dR_s, dR_p, dT_s, dT_p,
                                              tf_param1_of(ctx->t, k), &dvalue))
                        continue;   /* validated before the sweep; cannot happen here */

                    /* d(sum r^2)/d(eps) = 2 sum_r r_r dr_r/d(eps), with dr_r the operand's
                     * residual slope rather than sqrt(weight) - see tf_score_of. A SATISFIED
                     * ONE-SIDED CONSTRAINT DROPS OUT TWICE OVER: its residual is zero and so is
                     * its slope, which is the behaviour wanted. A needle that would improve an
                     * already-met requirement earns nothing for it. */
                    ctx->map[(int64_t)c * ctx->req->n_depths + di] += ctx->dres[slot] * dvalue;
                }
            }
            --di;
        }

        {
            const tf_complex delta = tf_c_scale(kappa, TF_TWO_PI * dj / wv);
            S = tf_m2_mul(tf_layer_matrix(delta, eta), S);
        }
        z_back = z_front;
    }
}

/* ---------------------------------------------------------------------------------------------
 * tf_evaluate_needle
 * ------------------------------------------------------------------------------------------- */

TF_API tf_status tf_evaluate_needle(const tf_problem*        problem,
                                    const tf_operands*        operands,
                                    const tf_plan_buffers*   plan,
                                    const tf_needle_request* request,
                                    tf_workspace*            workspace,
                                    tf_needle_output*        out)
{
    if (problem == 0 || operands == 0 || plan == 0 || request == 0 || workspace == 0 || out == 0)
        return TF_NULL_POINTER;
    if (out->status == 0 || out->residual == 0 || out->dsumsq_dinsertion == 0)
        return TF_NULL_POINTER;
    if (request->depth == 0 || request->candidate_material == 0) return TF_NULL_POINTER;
    if (plan->point_path == 0 || plan->point_polarisation == 0 || plan->report == 0)
        return TF_PLAN_NOT_RUN;

    int32_t* status = out->status;
    *status = TF_OK;

    if (request->n_depths <= 0 || request->n_candidates <= 0)
        { *status = TF_BAD_COUNT; return TF_BAD_COUNT; }

    /* A REVERSED CONFIGURATION MAKES A NEEDLE DEPTH AMBIGUOUS, which is a question rather than
     * an oversight. A depth is measured in nanometres "from the front of the stack", and a
     * reversed configuration is the same stack seen from the other side - so does z = 0 mean the
     * physical front, or the side the light arrives from in THIS configuration? A design
     * evaluated from both sides would want one map over one stack, which argues for the physical
     * front; a user reading a single configuration would expect the illuminated side. The two
     * give different maps and the difference is silent, so this refuses until the contract says
     * which. */
    for (int32_t i = 0; i < problem->n_points; ++i)
        if (tf_reversed_at(problem, i))
        {
            TF_NOT_IMPLEMENTED(status, TF_NOT_IMPLEMENTED_REVERSED,
                "a needle map over a reversed configuration. A depth is measured from the front of "
                "the stack, and which side that is has not been settled for a reversed traversal - "
                "see tf_needle.cpp. Every other entry point handles reversal.");
            return TF_NOT_IMPLEMENTED_REVERSED;
        }

    if (plan->report->n_unsupported > 0)
    {
        TF_NOT_IMPLEMENTED(status, TF_NOT_IMPLEMENTED_INCOHERENT,
            "the plan found points needing a path that is not implemented - massive layers.");
        return TF_NOT_IMPLEMENTED_INCOHERENT;
    }

    /* ONE STACK, AND A TRIAL AXIS DOES NOT SAY WHICH. A needle map says where to insert a layer in THE stack.
     *
     * The nominal stack is the obvious guess and that is exactly why it is refused: it is a guess
     * that would produce a plausible number. Averaging over trials is equally defensible and
     * gives a different one. Nothing in the specification says which, so this refuses until
     * something does - the same judgement as a needle depth over a reversed configuration.
     *
     * tf_evaluate and tf_evaluate_jacobian are the two entry points that DO define it, because an
     * operand's trial reduction says what to do with the axis. Nothing here has that. */
    if (tf_uses_trials(problem, operands))
    {
        TF_NOT_IMPLEMENTED(status, TF_NOT_IMPLEMENTED_TRIAL,
            "this entry point was asked for while an operand reduces over the Monte Carlo trial "
            "axis. It is defined on one stack and which of the trials it means has not been "
            "settled - see tf_extrapolation_policy's neighbour, tf_trial_reduction.");
        return TF_NOT_IMPLEMENTED_TRIAL;
    }

    /* AN OPAQUE LAYER MAKES THE MAP DISHONEST RATHER THAN MERELY INACCURATE.
     *
     * The clamp pins the phase of a layer thick enough to absorb everything, and in the value that
     * is harmless - the common factor cancels out of C/B. A needle map cannot shrug it off the same
     * way: the FRONT of such a layer is still illuminated and an insertion there genuinely matters,
     * but the clamped product cannot represent the difference between front and back. Any number
     * produced here would be an artifact of the clamp with the shape of physics.
     *
     * Truncating the stack at the first opaque layer is the real fix - scope ledger S6.3 - and
     * until it exists this refuses. */
    if (plan->report->max_clamped_layers > 0)
    {
        TF_NOT_IMPLEMENTED(status, TF_NOT_IMPLEMENTED_NEEDLE_OPAQUE,
            "a needle map was asked for on a stack containing a layer thick enough to clamp. The "
            "front of an opaque layer is still illuminated and an insertion there matters, but the "
            "clamped product cannot tell front from back, so any map inside it would be an "
            "artifact. Stack truncation at the first opaque layer is the fix (ledger S6.3).");
        return TF_NOT_IMPLEMENTED_NEEDLE_OPAQUE;
    }

    {
        const tf_status ds = tf_dispersion_check(problem, operands, status);
        if (ds != TF_OK) return ds;
    }
    const int uses_disp = tf_uses_dispersion(operands);

    const int32_t n  = problem->n_layers;
    const int32_t nr = operands->n_operands;
    const int32_t n_slots  = tf_n_slots(operands);
    const int32_t max_span = plan->report->max_operand_span;

    /* Validate the request rather than trusting it: an unsorted or out-of-range depth would walk
     * off the end of the sweep's cursor and produce a map with holes in it. */
    {
        double total = 0.0;
        for (int32_t j = 0; j < n; ++j) total += problem->layer_thickness[j];

        for (int32_t i = 0; i < request->n_depths; ++i)
        {
            if (!(request->depth[i] >= 0.0 && request->depth[i] <= total))
                { *status = TF_INDEX_OUT_OF_RANGE; return TF_INDEX_OUT_OF_RANGE; }
            if (i > 0 && !(request->depth[i] >= request->depth[i - 1]))
                { *status = TF_INDEX_OUT_OF_RANGE; return TF_INDEX_OUT_OF_RANGE; }
        }
        for (int32_t c = 0; c < request->n_candidates; ++c)
            if (request->candidate_material[c] < 0 ||
                request->candidate_material[c] >= problem->n_materials)
                { *status = TF_INDEX_OUT_OF_RANGE; return TF_INDEX_OUT_OF_RANGE; }
    }

    const int64_t need = (int64_t)(n + 1) * 8 + (int64_t)request->n_candidates * 8
                       + (int64_t)n + 1
                       + (int64_t)nr * 2            /* the operand values and their adjoints */
                       + (int64_t)n_slots * 2       /* the reduction slopes, and the folded ones */
                       + (int64_t)max_span * 5      /* the span scratch */
                       + (uses_disp ? tf_dispersion_scratch(n) : 0);   /* group delay, GDD and TOD */
    if (workspace->scratch == 0 || workspace->scratch_doubles < need)
    {
        *status = TF_SCRATCH_TOO_SMALL;
        fprintf(stderr,
                "\n*** THINFILM: scratch too small ***\n"
                "    have %lld doubles, need %lld for a needle map over %d candidates.\n\n",
                (long long)workspace->scratch_doubles, (long long)need,
                (int)request->n_candidates);
        return TF_SCRATCH_TOO_SMALL;
    }

    double* cursor = workspace->scratch;
    tf_matrix2* prefix = (tf_matrix2*)cursor;  cursor += (int64_t)(n + 1) * 8;
    tf_matrix2* gen    = (tf_matrix2*)cursor;  cursor += (int64_t)request->n_candidates * 8;
    double*     zfront = cursor;               cursor += (int64_t)n + 1;
    double*     opval  = cursor;               cursor += (int64_t)nr;
    double*     adj    = cursor;               cursor += (int64_t)nr;
    double*     slope  = cursor;               cursor += (int64_t)n_slots;
    double*     xbuf   = cursor;               cursor += (int64_t)max_span * 4;
    double*     dvdx   = cursor;               cursor += (int64_t)max_span;
    double*     dres   = cursor;               /* [n_slots] the adjoint folded onto each slot */
    double*     disp   = uses_disp ? dres + n_slots : 0;   /* the dispersion jets, after dres */

    /* PASS A, then the REVERSE PASS. The needle map is one scalar per (candidate, depth), so the
     * chain rule runs backwards: each scored operand seeds 2 r slope, every derived operand pushes
     * its adjoint onto its inputs, and what finally lands on a SLOT is that adjoint times the
     * reduction's local weight. The sweep then needs nothing per operand at all - one multiply per
     * slot per depth - where a forward pass would have wanted a row of n_operands for every
     * candidate and every depth. */
    if (!tf_operand_pass(problem, operands, plan, xbuf, dvdx, opval, slope, 0, 0))
    {
        TF_NOT_IMPLEMENTED(status, TF_NOT_IMPLEMENTED_TARGET, "unrecognised target code");
        return TF_NOT_IMPLEMENTED_TARGET;
    }
    for (int32_t o = 0; o < nr; ++o)
    {
        const int32_t row = plan->operand_residual[o];
        if (row >= 0) out->residual[row] = tf_score_of(operands, o, opval[o]).residual;
    }
    if (!tf_operand_adjoint(operands, plan, opval, out->residual, adj))
    {
        TF_NOT_IMPLEMENTED(status, TF_NOT_IMPLEMENTED_TARGET, "a derived operand");
        return TF_NOT_IMPLEMENTED_TARGET;
    }
    for (int32_t o = 0; o < nr; ++o)
    {
        const int32_t f = tf_slot_first(operands, o), c = tf_slot_count(operands, o);
        for (int32_t i = 0; i < c; ++i) dres[f + i] = adj[o] * slope[f + i];
    }

    for (int64_t i = 0; i < (int64_t)request->n_candidates * request->n_depths; ++i)
        out->dsumsq_dinsertion[i] = 0.0;

    if (n == 0) return TF_OK;   /* nowhere to insert: the zero map is the answer */

    tf_needle_ctx ctx;
    ctx.p = problem; ctx.t = operands; ctx.req = request;
    ctx.dres = dres; ctx.map = out->dsumsq_dinsertion; ctx.plan = plan;
    ctx.prefix = prefix; ctx.gen = gen; ctx.zfront = zfront;

    for (int32_t point = 0; point < problem->n_points; ++point)
    {
        const int32_t need_pol = plan->point_polarisation[point];
        if (need_pol == 0) continue;

        tf_point_amplitudes a;
        tf_amplitudes(problem, point, plan->point_path[point], need_pol, &a);

        if (need_pol & TF_NEED_S) tf_needle_sweep(&ctx, point, TF_POL_S, &a);
        if (need_pol & TF_NEED_P) tf_needle_sweep(&ctx, point, TF_POL_P, &a);

        /* Group delay, GDD and TOD rows, through their own series. */
        if (uses_disp)
        {
            if (need_pol & TF_NEED_S)
                tf_dispersion_needle(problem, operands, plan, request, dres, point, TF_POL_S, disp, out->dsumsq_dinsertion);
            if (need_pol & TF_NEED_P)
                tf_dispersion_needle(problem, operands, plan, request, dres, point, TF_POL_P, disp, out->dsumsq_dinsertion);
        }
    }


    /* THE STACK OPERANDS' CONTRIBUTION: independent of DEPTH, but not of the CANDIDATE.
     *
     * Inserting a nanometre adds a nanometre to the total thickness wherever it goes and whatever
     * it is made of, so TOTT contributes the same number to every cell of the map. STRESS does
     * not: it contributes sigma of the candidate material, so the map ranks a low-stress
     * candidate above a high-stress one at the identical depth. That is the first thing in the
     * needle map that distinguishes candidates by anything other than optics, and it is the
     * reason this loop is per-candidate rather than a single scalar added at the end.
     *
     * MINT CONTRIBUTES NOTHING, WHICH IS NOT AN OMISSION. A needle is born at zero thickness, so
     * it starts in full violation of any minimum and MINT's slope there is MINUS one. Adding that
     * would lower the whole map and make every insertion look a constant amount CHEAPER - the
     * opposite of the operand's purpose. MINT's discouragement is a STEP at eps = 0+, and a
     * first-order map cannot represent a step; it belongs to the discrete stage that accepts or
     * rejects an insertion, which is the stage able to weigh a jump. See tf_stack_insertion_slope.
     */
    {
        int any = 0;
        for (int32_t o = 0; o < nr && !any; ++o) any = tf_is_stack(operands, o);

        if (any)
            for (int32_t c = 0; c < request->n_candidates; ++c)
            {
                double offset = 0.0;
                for (int32_t o = 0; o < nr; ++o)
                    if (tf_is_stack(operands, o))
                        offset += adj[o] * tf_stack_insertion_slope(
                                               problem, operands->code[o],
                                               tf_sharpness_of(operands, o),
                                               request->candidate_material[c]);

                if (offset != 0.0)
                    for (int32_t d = 0; d < request->n_depths; ++d)
                        out->dsumsq_dinsertion[(int64_t)c * request->n_depths + d] += offset;
            }
    }

    return TF_OK;
}

} /* extern "C" */
