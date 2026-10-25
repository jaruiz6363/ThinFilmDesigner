/* tf_dispersion.cpp — group delay, GDD and TOD, and their thickness derivatives.
 *
 * WHAT IS COMPUTED. For the reflected (or transmitted) amplitude z of one point and polarisation,
 * with ' = d/d(lambda), the phase's slopes come from the logarithmic derivative q = z'/z:
 *
 *     phi' = Im q        phi'' = Im q'        phi''' = Im q''
 *
 * and the chain rule (Faa di Bruno) turns them into derivatives in omega:
 *
 *     GD  = -d(phi)/d(omega)   = -phi' L1
 *     GDD = -d2(phi)/d(omega)2 = -(phi'' L1^2 + phi' L2)
 *     TOD = -d3(phi)/d(omega)3 = -(phi''' L1^3 + 3 phi'' L1 L2 + phi' L3)
 *
 *     L1 = d(lambda)/d(omega) = -lambda^2/(2 pi c)     L2 = 2 lambda^3/(2 pi c)^2
 *     L3 = -6 lambda^4/(2 pi c)^3
 *
 * The minus sign is the engine's phase convention: Macleod's characteristic matrix belongs to
 * exp(i(omega t - k z)), in which a wave that has travelled further has the more NEGATIVE phase.
 * With it, a slab of air of thickness L in front of a mirror adds 2L/c to GD, which is what the
 * word delay means; test_dispersion pins exactly that.
 *
 * Working from z'/z means the phase itself is never formed, so there is no branch cut to unwrap -
 * the reason the specification's "unwrap on the dense grid" note does not apply here.
 *
 * TRUNCATED TAYLOR SERIES IN LAMBDA. Every quantity that moves with wavelength is carried as its
 * first four Taylor coefficients about the point's wavelength, c_k = f^(k)/k!, and every operation
 * on them - product, quotient, square root, sine and cosine - is the exact recurrence for the
 * truncated series. So the characteristic matrix of a layer is a 2x2 matrix of series, the stack's
 * is the ordered product of the layers' (the Cauchy product is associative, as matrix products
 * need), and r, t and q follow by the same arithmetic. Nothing is expanded by hand: the third
 * derivative TOD needs costs no more algebra than the first, and GD and GDD come out of the same
 * series as the lower coefficients. A coefficient never feeds a lower one, so a medium sent without
 * d3n/dlambda3 (as for GD and GDD alone) leaves GD and GDD exactly as they would be with it.
 *
 * THE THICKNESS DERIVATIVE, by the Jacobian's prefix/suffix scheme applied to series matrices. A
 * thickness appears in one layer only, through delta = a(lambda) d, so its derivative is the series
 * a(lambda) times the matrix's partial in delta, and
 *
 *     d(stack)/d(d_j) = prefix_j * dM_j/d(d_j) * suffix_j
 *
 * with one forward sweep storing prefixes and one backward sweep carrying the suffix. Everything
 * after the stack - B, C, r, t and q - is then pushed through with the tangent alongside, by the
 * product and quotient rules. Exact, O(n) per point, no differencing anywhere.
 *
 * GENERAL ARITHMETIC ON EVERY PATH, as the Jacobian. A clamped (opaque) layer's delta no longer
 * moves with wavelength or thickness, as in tf_spectral.cpp and tf_jacobian.cpp, so its series is
 * a constant and its thickness derivative zero, while eta's dispersion survives.
 */

#include "tf_internal.h"

#include <math.h>
#include <stdio.h>

extern "C" {

/* ---------------------------------------------------------------------------------------------
 * Truncated series: c[k] = f^(k)/k! about the point's wavelength, k = 0..3.
 * ------------------------------------------------------------------------------------------- */

#define TF_SERIES 4   /* the value and three derivatives: TOD is the third */

typedef struct tf_s4 { tf_complex c[TF_SERIES]; } tf_s4;
typedef struct tf_sm { tf_s4 m00, m01, m10, m11; } tf_sm;   /* a 2x2 matrix of series */

static tf_complex tf_ci(tf_complex z) { return tf_c(-z.im, z.re); }   /* i z */

static tf_s4 tf_s_const(tf_complex z)
{
    tf_s4 r;
    r.c[0] = z;
    for (int k = 1; k < TF_SERIES; ++k) r.c[k] = tf_c(0.0, 0.0);
    return r;
}

static tf_s4 tf_s_zero(void) { return tf_s_const(tf_c(0.0, 0.0)); }

static tf_s4 tf_s_add(tf_s4 a, tf_s4 b)
{
    for (int k = 0; k < TF_SERIES; ++k) a.c[k] = tf_c_add(a.c[k], b.c[k]);
    return a;
}

static tf_s4 tf_s_sub(tf_s4 a, tf_s4 b)
{
    for (int k = 0; k < TF_SERIES; ++k) a.c[k] = tf_c_sub(a.c[k], b.c[k]);
    return a;
}

static tf_s4 tf_s_scale(tf_s4 a, tf_complex s)
{
    for (int k = 0; k < TF_SERIES; ++k) a.c[k] = tf_c_mul(a.c[k], s);
    return a;
}

static tf_s4 tf_s_i(tf_s4 a)
{
    for (int k = 0; k < TF_SERIES; ++k) a.c[k] = tf_ci(a.c[k]);
    return a;
}

/* The Cauchy product, truncated. */
static tf_s4 tf_s_mul(const tf_s4* a, const tf_s4* b)
{
    tf_s4 r;
    for (int k = 0; k < TF_SERIES; ++k)
    {
        tf_complex z = tf_c(0.0, 0.0);
        for (int j = 0; j <= k; ++j) z = tf_c_add(z, tf_c_mul(a->c[j], b->c[k - j]));
        r.c[k] = z;
    }
    return r;
}

static tf_s4 tf_s_mulv(tf_s4 a, tf_s4 b) { return tf_s_mul(&a, &b); }

/* a/b: q_k = (a_k - sum_{j<k} q_j b_{k-j}) / b_0. */
static tf_s4 tf_s_div(tf_s4 a, tf_s4 b)
{
    tf_s4 q;
    for (int k = 0; k < TF_SERIES; ++k)
    {
        tf_complex z = a.c[k];
        for (int j = 0; j < k; ++j) z = tf_c_sub(z, tf_c_mul(q.c[j], b.c[k - j]));
        q.c[k] = tf_c_div(z, b.c[0]);
    }
    return q;
}

/* sqrt(a) on the branch whose value is root0: r_k = (a_k - sum_{0<j<k} r_j r_{k-j}) / (2 r_0). */
static tf_s4 tf_s_sqrt(tf_s4 a, tf_complex root0)
{
    tf_s4 r;
    r.c[0] = root0;
    const tf_complex two_r0 = tf_c_scale(root0, 2.0);
    for (int k = 1; k < TF_SERIES; ++k)
    {
        tf_complex z = a.c[k];
        for (int j = 1; j < k; ++j) z = tf_c_sub(z, tf_c_mul(r.c[j], r.c[k - j]));
        r.c[k] = tf_c_div(z, two_r0);
    }
    return r;
}

/* sin and cos of a series: s' = c d', c' = -s d', so k s_k = sum_j j d_j c_{k-j} and
 * k c_k = -sum_j j d_j s_{k-j}. */
static void tf_s_sincos(const tf_s4* d, tf_s4* s, tf_s4* c)
{
    tf_c_sincos(d->c[0], &s->c[0], &c->c[0]);
    for (int k = 1; k < TF_SERIES; ++k)
    {
        tf_complex zs = tf_c(0.0, 0.0), zc = tf_c(0.0, 0.0);
        for (int j = 1; j <= k; ++j)
        {
            const tf_complex jd = tf_c_scale(d->c[j], (double)j);
            zs = tf_c_add(zs, tf_c_mul(jd, c->c[k - j]));
            zc = tf_c_sub(zc, tf_c_mul(jd, s->c[k - j]));
        }
        s->c[k] = tf_c_scale(zs, 1.0 / k);
        c->c[k] = tf_c_scale(zc, 1.0 / k);
    }
}

/* The series of f', one order shorter: its last coefficient is unknown and left zero, which is
 * harmless because only the coefficients below it are read. */
static tf_s4 tf_s_deriv(tf_s4 a)
{
    tf_s4 r;
    for (int k = 0; k + 1 < TF_SERIES; ++k) r.c[k] = tf_c_scale(a.c[k + 1], (double)(k + 1));
    r.c[TF_SERIES - 1] = tf_c(0.0, 0.0);
    return r;
}

static tf_sm tf_sm_identity(void)
{
    tf_sm m = { tf_s_const(tf_c(1.0, 0.0)), tf_s_zero(), tf_s_zero(), tf_s_const(tf_c(1.0, 0.0)) };
    return m;
}

static tf_sm tf_sm_mul(const tf_sm* x, const tf_sm* y)
{
    tf_sm r;
    r.m00 = tf_s_add(tf_s_mul(&x->m00, &y->m00), tf_s_mul(&x->m01, &y->m10));
    r.m01 = tf_s_add(tf_s_mul(&x->m00, &y->m01), tf_s_mul(&x->m01, &y->m11));
    r.m10 = tf_s_add(tf_s_mul(&x->m10, &y->m00), tf_s_mul(&x->m11, &y->m10));
    r.m11 = tf_s_add(tf_s_mul(&x->m10, &y->m01), tf_s_mul(&x->m11, &y->m11));
    return r;
}

/* ---------------------------------------------------------------------------------------------
 * The chain through one medium
 *
 *     u     = (n0 sin theta0)^2
 *     kappa = sqrt(n^2 - u)          on tf_layer_kappa's branch
 *     eta_s = kappa,  eta_p = n^2 / kappa
 * ------------------------------------------------------------------------------------------- */

/* An index as a series, from the ABI's arrays: internal sign, as everywhere. A higher derivative is
 * zero when its array is absent, which only the lower orders allow - the check at the entry points
 * refuses an operand without the derivatives it reads. */
static tf_s4 tf_index_series(tf_complex n, tf_complex dn, const tf_complex* d2n, const tf_complex* d3n,
                             int64_t at)
{
    tf_s4 r;
    r.c[0] = tf_index_internal(n);
    r.c[1] = tf_index_internal(dn);
    r.c[2] = (d2n != 0) ? tf_c_scale(tf_index_internal(d2n[at]), 0.5) : tf_c(0.0, 0.0);
    r.c[3] = (d3n != 0) ? tf_c_scale(tf_index_internal(d3n[at]), 1.0 / 6.0) : tf_c(0.0, 0.0);
    return r;
}

static tf_s4 tf_kappa_series(const tf_s4* n, const tf_s4* u)
{
    return tf_s_sqrt(tf_s_sub(tf_s_mul(n, n), *u), tf_layer_kappa(n->c[0], u->c[0]));
}

static tf_s4 tf_eta_series(const tf_s4* n, const tf_s4* k, int pol)
{
    if (pol == TF_POL_S) return *k;
    return tf_s_div(tf_s_mul(n, n), *k);
}

typedef struct tf_point_media
{
    tf_s4  u;        /* the conserved transverse quantity */
    tf_s4  eta0;     /* the incident medium's admittance */
    tf_s4  etas;     /* the exit medium's */
    double wv;       /* the wavelength, nm */
    int32_t wi;
} tf_point_media;

static tf_point_media tf_media(const tf_problem* p, int32_t point, int pol)
{
    tf_point_media m;
    m.wi = p->point_wavelength[point];
    m.wv = p->wavelength[m.wi];
    const double s0 = sin(p->point_angle[point]);

    /* The one-configuration form only: the entry points refuse configurations. */
    const tf_s4 n0 = tf_index_series(p->index_incident[m.wi], p->dindex_incident_dwavelength[m.wi],
                                     p->d2index_incident_dwavelength2, p->d3index_incident_dwavelength3, m.wi);
    const tf_s4 ns = tf_index_series(p->index_exit[m.wi], p->dindex_exit_dwavelength[m.wi],
                                     p->d2index_exit_dwavelength2, p->d3index_exit_dwavelength3, m.wi);

    m.u = tf_s_scale(tf_s_mul(&n0, &n0), tf_c(s0 * s0, 0.0));
    const tf_s4 k0 = tf_kappa_series(&n0, &m.u), ks = tf_kappa_series(&ns, &m.u);
    m.eta0 = tf_eta_series(&n0, &k0, pol);
    m.etas = tf_eta_series(&ns, &ks, pol);
    return m;
}

/* ---------------------------------------------------------------------------------------------
 * One layer's matrix, and its derivative with respect to the layer's thickness.
 *
 *     delta = a d,   a = 2 pi kappa / lambda          (series)
 *     M     = [[c, i s/eta], [i eta s, c]]             s = sin delta, c = cos delta
 *     dM/dd = a [[-s, i c/eta], [i eta c, -s]]
 * ------------------------------------------------------------------------------------------- */

static void tf_material_jets(const tf_problem* p, const tf_point_media* md, int pol, int32_t m,
                             double d, tf_sm* J, tf_sm* dJ)
{
    const int64_t ix = (int64_t)m * p->n_wavelengths + md->wi;
    const tf_s4 n = tf_index_series(p->material_index[ix], p->material_dindex_dwavelength[ix],
                                    p->material_d2index_dwavelength2, p->material_d3index_dwavelength3, ix);
    const tf_s4 k = tf_kappa_series(&n, &md->u);
    const tf_s4 e = tf_eta_series(&n, &k, pol);

    /* 1/lambda about the point: (-1)^j / lambda^(j+1). */
    tf_s4 inv;
    {
        double f = 1.0 / md->wv;
        for (int j = 0; j < TF_SERIES; ++j) { inv.c[j] = tf_c(f, 0.0); f *= -1.0 / md->wv; }
    }
    tf_s4 a = tf_s_scale(tf_s_mul(&k, &inv), tf_c(TF_TWO_PI, 0.0));
    tf_s4 delta = tf_s_scale(a, tf_c(d, 0.0));

    tf_complex d0 = delta.c[0];
    if (tf_layer_clamp_delta(&d0))
    {
        /* Opaque: delta no longer moves with wavelength or thickness; eta still disperses. */
        delta = tf_s_const(d0);
        a = tf_s_zero();
    }

    tf_s4 s, c;
    tf_s_sincos(&delta, &s, &c);

    J->m00 = c;
    J->m01 = tf_s_i(tf_s_div(s, e));
    J->m10 = tf_s_i(tf_s_mul(&e, &s));
    J->m11 = c;

    if (dJ == 0) return;

    const tf_s4 ms = tf_s_scale(s, tf_c(-1.0, 0.0));
    dJ->m00 = tf_s_mul(&a, &ms);
    dJ->m01 = tf_s_mulv(a, tf_s_i(tf_s_div(c, e)));
    dJ->m10 = tf_s_mulv(a, tf_s_i(tf_s_mul(&e, &c)));
    dJ->m11 = dJ->m00;
}

/* A layer of the stack: its own material and thickness. */
static void tf_layer_jets(const tf_problem* p, const tf_point_media* md, int pol, int32_t j,
                          tf_sm* J, tf_sm* dJ)
{
    tf_material_jets(p, md, pol, p->layer_material[j], p->layer_thickness[j], J, dJ);
}

/* ---------------------------------------------------------------------------------------------
 * From the stack's matrix to GD, GDD and TOD, with the thickness tangent alongside.
 * ------------------------------------------------------------------------------------------- */

/* The six readings: GD, GDD, TOD on reflection, then on transmission, and their tangents per nm of
 * the perturbed layer. */
typedef struct tf_gd
{
    double v[TF_DISPERSION_SLOTS];     /* fs, fs^2, fs^3 */
    double dv[TF_DISPERSION_SLOTS];
} tf_gd;

/* The three omega-derivatives of the phase from q = z'/z (and its tangent qt), into out[0..2]. */
static void tf_omega_slopes(const tf_s4* q, double wv, double out[3])
{
    const double w = TF_TWO_PI * TF_C_NM_PER_FS;
    const double L1 = -(wv * wv) / w;
    const double L2 = 2.0 * wv * wv * wv / (w * w);
    const double L3 = -6.0 * wv * wv * wv * wv / (w * w * w);
    const double p1 = q->c[0].im, p2 = q->c[1].im, p3 = 2.0 * q->c[2].im;   /* phi', phi'', phi''' */
    out[0] = -p1 * L1;
    out[1] = -(p2 * L1 * L1 + p1 * L2);
    out[2] = -(p3 * L1 * L1 * L1 + 3.0 * p2 * L1 * L2 + p1 * L3);
}

/* q = z'/z, and its tangent (zt' - q zt)/z. */
static void tf_log_slope(const tf_s4* z, const tf_s4* zt, tf_s4* q, tf_s4* qt)
{
    *q = tf_s_div(tf_s_deriv(*z), *z);
    if (zt != 0) *qt = tf_s_div(tf_s_sub(tf_s_deriv(*zt), tf_s_mul(q, zt)), *z);
}

static void tf_finish(const tf_point_media* md, const tf_sm* P, const tf_sm* T, tf_gd* out)
{
    const tf_s4* es = &md->etas;
    const tf_s4* e0 = &md->eta0;

    /* [B; C] = M [1; eta_sub], N = e0 B - C, D = e0 B + C, r = N/D, t = 2 e0/D. */
    const tf_s4 B = tf_s_add(P->m00, tf_s_mul(&P->m01, es));
    const tf_s4 C = tf_s_add(P->m10, tf_s_mul(&P->m11, es));
    const tf_s4 e0B = tf_s_mul(e0, &B);
    const tf_s4 N = tf_s_sub(e0B, C), D = tf_s_add(e0B, C);
    const tf_s4 r = tf_s_div(N, D);
    const tf_s4 t = tf_s_div(tf_s_scale(*e0, tf_c(2.0, 0.0)), D);

    tf_s4 rt, tt;
    if (T != 0)
    {
        /* Linear in the matrix, so the tangent goes through B and C as they do; then the
         * quotient rule: rt = (Nt - r Dt)/D, tt = -t Dt/D. */
        const tf_s4 Bt = tf_s_add(T->m00, tf_s_mul(&T->m01, es));
        const tf_s4 Ct = tf_s_add(T->m10, tf_s_mul(&T->m11, es));
        const tf_s4 e0Bt = tf_s_mul(e0, &Bt);
        const tf_s4 Nt = tf_s_sub(e0Bt, Ct), Dt = tf_s_add(e0Bt, Ct);
        rt = tf_s_div(tf_s_sub(Nt, tf_s_mul(&r, &Dt)), D);
        tt = tf_s_div(tf_s_scale(tf_s_mul(&t, &Dt), tf_c(-1.0, 0.0)), D);
    }

    tf_s4 q, qt;
    tf_log_slope(&r, T != 0 ? &rt : 0, &q, &qt);
    tf_omega_slopes(&q, md->wv, &out->v[0]);
    if (T != 0) tf_omega_slopes(&qt, md->wv, &out->dv[0]);

    tf_log_slope(&t, T != 0 ? &tt : 0, &q, &qt);
    tf_omega_slopes(&q, md->wv, &out->v[3]);
    if (T != 0) tf_omega_slopes(&qt, md->wv, &out->dv[3]);
}

/* ---------------------------------------------------------------------------------------------
 * What the evaluators call
 * ------------------------------------------------------------------------------------------- */

int tf_is_dispersion_code(int32_t code)
{
    return (code >= TF_TARGET_R_GD_S && code <= TF_TARGET_T_TOD_P) ? 1 : 0;
}

/* The codes run in blocks of four - R s, R p, T s, T p - one block per order: GD, GDD, TOD. */
static int tf_dispersion_order(int32_t code) { return (code - TF_TARGET_R_GD_S) / 4; }

int tf_dispersion_polarisation(int32_t code)
{
    return ((code - TF_TARGET_R_GD_S) % 2 == 0) ? TF_POL_S : TF_POL_P;
}

/* Which of the six readings a code reads: GD, GDD, TOD on r, then on t. */
static int tf_dispersion_slot(int32_t code)
{
    const int transmission = ((code - TF_TARGET_R_GD_S) % 4) >= 2;
    return (transmission ? 3 : 0) + tf_dispersion_order(code);
}

int tf_dispersion_value(const tf_problem* p, int32_t point, int32_t code, double* out)
{
    const int pol = tf_dispersion_polarisation(code);
    const tf_point_media md = tf_media(p, point, pol);
    const int rev = tf_reversed_at(p, point);

    tf_sm P = tf_sm_identity(), J;
    for (int32_t i = 0; i < p->n_layers; ++i)
    {
        tf_layer_jets(p, &md, pol, tf_layer_at(p, rev, i), &J, 0);
        P = tf_sm_mul(&P, &J);
    }

    tf_gd g;
    tf_finish(&md, &P, 0, &g);
    *out = g.v[tf_dispersion_slot(code)];
    return 1;
}

/* Every layer's derivative of the six readings at one point and polarisation, by physical layer:
 * d[TF_DISPERSION_SLOTS j + slot]. `prefix` is scratch for n_layers + 1 matrices. */
void tf_dispersion_sweep(const tf_problem* p, int32_t point, int pol, void* prefix_scratch, double* d)
{
    tf_sm* prefix = (tf_sm*)prefix_scratch;
    const int32_t n = p->n_layers;
    const tf_point_media md = tf_media(p, point, pol);
    const int rev = tf_reversed_at(p, point);

    prefix[0] = tf_sm_identity();
    tf_sm J, dJ;
    for (int32_t i = 0; i < n; ++i)
    {
        tf_layer_jets(p, &md, pol, tf_layer_at(p, rev, i), &J, 0);
        prefix[i + 1] = tf_sm_mul(&prefix[i], &J);
    }

    tf_sm S = tf_sm_identity();
    for (int32_t i = n - 1; i >= 0; --i)
    {
        const int32_t j = tf_layer_at(p, rev, i);
        tf_layer_jets(p, &md, pol, j, &J, &dJ);

        const tf_sm L = tf_sm_mul(&prefix[i], &dJ);
        const tf_sm T = tf_sm_mul(&L, &S);
        tf_gd g;
        tf_finish(&md, &prefix[n], &T, &g);
        for (int k = 0; k < TF_DISPERSION_SLOTS; ++k)
            d[TF_DISPERSION_SLOTS * (int64_t)j + k] = g.dv[k];

        S = tf_sm_mul(&J, &S);
    }
}

/* THE NEEDLE MAP'S DISPERSION PART, at one point and polarisation.
 *
 * A needle of material c and thickness eps at depth z inside layer j splits it, as in
 * tf_needle.cpp: M_j(d) = M_j(t) M_j(d - t), so the stack is
 *
 *     prefix_j  M_j(t)  M_c(eps)  M_j(d - t)  suffix_{j+1}
 *
 * and its derivative at eps = 0 puts the needle's thickness derivative, dM_c at zero thickness,
 * between the two halves. The readings' tangents follow by tf_finish, and each dispersion operand
 * reading this point adds its slot's adjoint times its reading's slope to the map, exactly as the
 * amplitude operands do in tf_needle_sweep. `scratch` holds 2 (n_layers + 1) matrices: the prefix
 * and the suffix chains. The caller has refused reversal, trials and opaque layers. */
void tf_dispersion_needle(const tf_problem* p, const tf_operands* ops, const tf_plan_buffers* plan,
                          const tf_needle_request* req, const double* dres, int32_t point, int pol,
                          void* scratch, double* map)
{
    const int32_t n = p->n_layers;
    tf_sm* prefix = (tf_sm*)scratch;
    tf_sm* suffix = prefix + (n + 1);
    const tf_point_media md = tf_media(p, point, pol);

    tf_sm J;
    prefix[0] = tf_sm_identity();
    for (int32_t j = 0; j < n; ++j)
    {
        tf_layer_jets(p, &md, pol, j, &J, 0);
        prefix[j + 1] = tf_sm_mul(&prefix[j], &J);
    }
    suffix[n] = tf_sm_identity();
    for (int32_t j = n - 1; j >= 0; --j)
    {
        tf_layer_jets(p, &md, pol, j, &J, 0);
        suffix[j] = tf_sm_mul(&J, &suffix[j + 1]);
    }

    const int32_t ef = plan->point_span_first[point], ec = plan->point_span_count[point];

    /* The same layer boundaries the sweep uses, accumulated forward. */
    int32_t j = 0;
    double z_front = 0.0;
    for (int32_t di = 0; di < req->n_depths; ++di)
    {
        const double z = req->depth[di];
        while (j < n - 1 && z >= z_front + p->layer_thickness[j]) { z_front += p->layer_thickness[j]; ++j; }
        const int32_t m = p->layer_material[j];
        const double tt = z - z_front, dj = p->layer_thickness[j];

        tf_sm A, B, dummy;
        tf_material_jets(p, &md, pol, m, tt, &A, 0);
        tf_material_jets(p, &md, pol, m, dj - tt, &B, 0);
        const tf_sm L = tf_sm_mul(&prefix[j], &A);
        const tf_sm R = tf_sm_mul(&B, &suffix[j + 1]);

        for (int32_t c = 0; c < req->n_candidates; ++c)
        {
            tf_sm G;
            tf_material_jets(p, &md, pol, req->candidate_material[c], 0.0, &dummy, &G);
            const tf_sm LG = tf_sm_mul(&L, &G);
            const tf_sm T = tf_sm_mul(&LG, &R);
            tf_gd g;
            tf_finish(&md, &prefix[n], &T, &g);

            for (int32_t e = 0; e < ec; ++e)
            {
                const int32_t k = plan->point_span_operand[ef + e];
                const int32_t code = ops->code[k];
                if (!tf_is_dispersion_code(code) || tf_dispersion_polarisation(code) != pol) continue;
                const double w = dres[plan->point_span_slot[ef + e]];
                if (w != 0.0) map[(int64_t)c * req->n_depths + di] += w * g.dv[tf_dispersion_slot(code)];
            }
        }
    }
}

double tf_dispersion_slope(const double* d, int32_t layer, int32_t code)
{
    return d[TF_DISPERSION_SLOTS * (int64_t)layer + tf_dispersion_slot(code)];
}

/* Doubles of scratch the sweeps need: two chains of series matrices (the needle map keeps the
 * suffix as well as the prefix) and the Jacobian's six readings per layer. */
int64_t tf_dispersion_scratch(int32_t n_layers)
{
    return 2 * ((int64_t)n_layers + 1) * (int64_t)(sizeof(tf_sm) / sizeof(double))
         + TF_DISPERSION_SLOTS * (int64_t)n_layers;
}

int tf_uses_dispersion(const tf_operands* ops)
{
    for (int32_t o = 0; o < ops->n_operands; ++o)
        if (!tf_is_derived(ops, o) && !tf_is_stack(ops, o) && tf_is_dispersion_code(ops->code[o]))
            return 1;
    return 0;
}

/* The data a table of dispersion operands needs, checked before anything is computed. */
tf_status tf_dispersion_check(const tf_problem* p, const tf_operands* ops, int32_t* status)
{
    int any = 0, order = 0;
    for (int32_t o = 0; o < ops->n_operands; ++o)
    {
        if (tf_is_derived(ops, o) || tf_is_stack(ops, o) || !tf_is_dispersion_code(ops->code[o])) continue;
        any = 1;
        if (tf_dispersion_order(ops->code[o]) > order) order = tf_dispersion_order(ops->code[o]);
    }
    if (!any) return TF_OK;

    if (tf_has_configs(p))
    {
        TF_NOT_IMPLEMENTED(status, TF_NOT_IMPLEMENTED_TARGET,
            "a group delay, GDD or TOD operand with configurations. The higher wavelength "
            "derivatives of the media have a seat only in the one-configuration form.");
        return TF_NOT_IMPLEMENTED_TARGET;
    }

    const char* missing = 0;
    if (p->material_dindex_dwavelength == 0 || p->dindex_incident_dwavelength == 0 || p->dindex_exit_dwavelength == 0)
        missing = "dn/dlambda";
    else if (order >= 1 && (p->material_d2index_dwavelength2 == 0 || p->d2index_incident_dwavelength2 == 0 ||
                            p->d2index_exit_dwavelength2 == 0))
        missing = "d2n/dlambda2";
    else if (order >= 2 && (p->material_d3index_dwavelength3 == 0 || p->d3index_incident_dwavelength3 == 0 ||
                            p->d3index_exit_dwavelength3 == 0))
        missing = "d3n/dlambda3";
    if (missing != 0)
    {
        static const char* const names[] = { "group delay", "GDD", "TOD" };
        *status = TF_NO_DISPERSION_DATA;
        fprintf(stderr,
                "\n*** THINFILM: no dispersion data ***\n"
                "    A %s operand was asked for and %s was not supplied.\n"
                "    A NULL is NOT read as zero: a material's own dispersion is a large part of a\n"
                "    coating's group delay, and the answer would be smooth, plausible and wrong.\n"
                "    A medium that really is non-dispersive is declared with an array of zeros.\n\n",
                names[order], missing);
        return TF_NO_DISPERSION_DATA;
    }
    return TF_OK;
}

} /* extern "C" */
