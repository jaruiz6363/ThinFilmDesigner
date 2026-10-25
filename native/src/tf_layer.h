/* tf_layer.h — the characteristic matrix of one layer, and its derivative with respect to
 * thickness.
 *
 * CONVENTIONS, stated once and relied on everywhere. Getting any of these wrong produces a
 * well-formed matrix and a wrong answer, so they are written down rather than remembered.
 *
 *   Time dependence      exp(-i omega t). This fixes the sign of the phase and, with it, the
 *                        branch of the square root in tf_c_sqrt_decaying.
 *   Refractive index     THE ABI takes n + i k with k >= 0 absorbing; the engine works
 *                        INTERNALLY in Macleod's N = n - i k, which is the convention the matrix
 *                        below belongs to. tf_index_internal converts, once, at the read boundary.
 *   Normal component     kappa = sqrt(n^2 - n0^2 sin^2(theta0)), the conserved transverse
 *                        wavevector making Snell's law implicit. Real and positive for a lossless
 *                        layer below the critical angle; on the NEGATIVE imaginary axis beyond it,
 *                        and for any absorbing layer - see tf_c_sqrt_decaying.
 *   Phase thickness      delta = (2 pi / lambda) d kappa
 *   Admittance           eta_s = kappa                (TE)
 *                        eta_p = n^2 / kappa          (TM)
 *                        in units where the admittance of free space is factored out.
 *
 * The characteristic matrix is then
 *
 *     M = [  cos delta            (i / eta) sin delta ]
 *         [  i eta sin delta      cos delta           ]
 *
 * which is unimodular for real OR complex delta, since cos^2 + sin^2 = 1 either way.
 */

#ifndef TF_LAYER_H
#define TF_LAYER_H

#include "tf_matrix2.h"

#ifdef __cplusplus
extern "C" {
#endif

#ifndef TF_PI
#define TF_PI     3.1415926535897932384626433832795
#endif

#ifndef TF_TWO_PI
#define TF_TWO_PI 6.283185307179586476925286766559
#endif

typedef enum tf_polarisation
{
    TF_POL_S = 0,
    TF_POL_P = 1
} tf_polarisation;

/* kappa = sqrt(n^2 - n0^2 sin^2 theta0).
 *
 * sin_theta0_n0_squared is (n0 sin theta0)^2, which is conserved across every interface and is
 * therefore computed ONCE per point rather than once per layer. That is not a micro-optimisation:
 * it is what makes Snell's law implicit and removes a complex arcsine from the inner loop. */
/* Convert a caller-supplied index into the engine's internal convention.
 *
 * THE ABI TAKES n + i k, with k >= 0 for an absorbing medium, because that is what catalogues
 * carry and what a System.Numerics user will write. The engine works internally in Macleod's
 * N = n - i k, because that is the convention the characteristic matrix above belongs to.
 *
 * One negation, at one boundary, and it is a FUNCTION rather than an inline minus sign so that
 * every read site is greppable. Mixing the two conventions produced R + T = 1.2466 on the first
 * run of the absorbing test - energy created, no error, and nothing but the conservation check to
 * say so. */
TF_HD static inline tf_complex tf_index_internal(tf_complex n_abi)
{
    return tf_c(n_abi.re, -n_abi.im);
}

TF_HD static inline tf_complex tf_layer_kappa(tf_complex n, tf_complex n0_sin_theta0_squared)
{
    return tf_c_sqrt_decaying(tf_c_sub(tf_c_mul(n, n), n0_sin_theta0_squared));
}

TF_HD static inline tf_complex tf_layer_admittance(tf_complex n, tf_complex kappa, int pol)
{
    if (pol == TF_POL_S) return kappa;
    return tf_c_div(tf_c_mul(n, n), kappa);     /* n^2 / kappa */
}

/* ---------------------------------------------------------------------------------------------
 * General path
 * ------------------------------------------------------------------------------------------- */

/* cos and sin of a complex argument, computed together because they share the same four
 * transcendentals and the layer matrix always wants both. */
TF_HD static inline void tf_c_sincos(tf_complex z, tf_complex* out_sin, tf_complex* out_cos)
{
    const double sr = sin(z.re), cr = cos(z.re);
    const double sh = sinh(z.im), ch = cosh(z.im);
    out_sin->re = sr * ch;  out_sin->im = cr * sh;
    out_cos->re = cr * ch;  out_cos->im = -sr * sh;
}

TF_HD static inline tf_matrix2 tf_layer_matrix(tf_complex delta, tf_complex eta)
{
    tf_complex s, c;
    tf_c_sincos(delta, &s, &c);

    tf_matrix2 m;
    m.m00 = c;
    m.m11 = c;
    /* (i / eta) sin delta  and  i eta sin delta. Multiplying by i is a swap and a negation, not a
     * complex multiply, so it is written out. */
    {
        const tf_complex s_over_eta = tf_c_div(s, eta);
        m.m01 = tf_c(-s_over_eta.im, s_over_eta.re);
        const tf_complex eta_s = tf_c_mul(eta, s);
        m.m10 = tf_c(-eta_s.im, eta_s.re);
    }
    return m;
}

/* d M / d delta. Same shape as M with cos and sin exchanged and the diagonal negated, which is
 * why the derivative costs nothing beyond the sincos already computed. */
TF_HD static inline tf_matrix2 tf_layer_matrix_ddelta(tf_complex delta, tf_complex eta)
{
    tf_complex s, c;
    tf_c_sincos(delta, &s, &c);

    tf_matrix2 m;
    m.m00 = tf_c(-s.re, -s.im);
    m.m11 = m.m00;
    {
        const tf_complex c_over_eta = tf_c_div(c, eta);
        m.m01 = tf_c(-c_over_eta.im, c_over_eta.re);
        const tf_complex eta_c = tf_c_mul(eta, c);
        m.m10 = tf_c(-eta_c.im, eta_c.re);
    }
    return m;
}


/* dM / d(eta), at fixed delta.
 *
 *     M       = [[ cos d,        (i/eta) sin d ],
 *                [ i eta sin d,  cos d        ]]
 *     dM/deta = [[ 0,           -(i/eta^2) sin d ],
 *                [ i sin d,      0             ]]
 *
 * NEEDED ONLY BY THE WAVELENGTH DERIVATIVE, and that is why it did not exist before. A thickness
 * moves delta and leaves eta alone. A wavelength moves BOTH - through dn/dlambda into eta, and
 * through kappa and the explicit 1/lambda into delta - so the total derivative is the sum of two
 * terms and this is the second one. Omitting it produces a derivative that is correct for a
 * dispersionless stack at normal incidence and wrong everywhere else, which is the worst possible
 * kind of wrong. */
TF_HD static inline tf_matrix2 tf_layer_matrix_deta(tf_complex delta, tf_complex eta)
{
    tf_complex s, c;
    tf_c_sincos(delta, &s, &c);
    (void)c;

    tf_matrix2 m;
    m.m00 = tf_c(0.0, 0.0);
    m.m11 = tf_c(0.0, 0.0);
    {
        /* -(i / eta^2) sin delta : form sin/eta^2, multiply by i, negate. */
        const tf_complex z = tf_c_div(s, tf_c_mul(eta, eta));
        m.m01 = tf_c(z.im, -z.re);
        /* i sin delta */
        m.m10 = tf_c(-s.im, s.re);
    }
    return m;
}

/* ---------------------------------------------------------------------------------------------
 * Structured path — kappa and eta real, delta real.
 * ------------------------------------------------------------------------------------------- */

TF_HD static inline tf_matrix2s tf_layer_matrix_s(double delta, double eta)
{
    const double s = sin(delta), c = cos(delta);
    tf_matrix2s m;
    m.a = c;
    m.b = s / eta;
    m.c = eta * s;
    m.d = c;
    return m;
}

TF_HD static inline tf_matrix2s tf_layer_matrix_s_ddelta(double delta, double eta)
{
    const double s = sin(delta), c = cos(delta);
    tf_matrix2s m;
    m.a = -s;
    m.b = c / eta;
    m.c = eta * c;
    m.d = -s;
    return m;
}

/* d delta / d(thickness) = (2 pi / lambda) kappa, and it does NOT depend on the thickness, so it
 * is a constant of the layer. The thickness derivative of the matrix is therefore
 *
 *     dM/dd = (dM/d delta) * (2 pi kappa / lambda)
 *
 * with the scalar real on the structured path and complex on the general one. */
TF_HD static inline double tf_layer_ddelta_dthickness_s(double kappa, double wavelength)
{
    return TF_TWO_PI * kappa / wavelength;
}

TF_HD static inline tf_complex tf_layer_ddelta_dthickness(tf_complex kappa, double wavelength)
{
    return tf_c_scale(kappa, TF_TWO_PI / wavelength);
}

/* ---------------------------------------------------------------------------------------------
 * The absorption clamp
 *
 * cos(delta) and sin(delta) grow like exp(Im delta)/2. A thick metal layer reaches Im delta of
 * fifty or a hundred without difficulty, and cosh overflows a double outright above about 710.
 * Long before that, r and t become ratios of enormous numbers and lose every significant digit.
 * So Im(delta) is clamped, and the layer is thereafter treated as what it physically is: opaque.
 *
 * WHERE THE THRESHOLD SITS, derived rather than chosen. Intensity transmission through the layer
 * is about exp(-2 Im delta). For that to be below double epsilon relative to unity,
 *
 *     2 Im delta > ln(1 / 2.22e-16) = 36.05      so      Im delta > 18.02
 *
 * At 18.02 the layer is already indistinguishable from semi-infinite AT DOUBLE PRECISION.
 * Everything above that is margin, bounded from above by overflow in the accumulated product:
 * each clamped layer multiplies it by about exp(24) = 2.6e10, so thirty of them in one stack is
 * the limit before a double overflows.
 *
 *     Im delta   intensity exp(-2 Im delta)   clamped layers before overflow
 *         18              2.3e-16                        41
 *         24              1.4e-21                        30      <- chosen
 *         32              1.6e-28                        23
 *
 * Twenty-four gives five orders of margin on precision and thirty layers of headroom, and
 * tf_plan counts clamped layers so that the headroom cannot be exhausted quietly.
 *
 * THE PROPER FIX IS TRUNCATION, not a bigger double. Once a layer is opaque, nothing behind it
 * can contribute to reflection or transmission from the front, so the stack could be cut there
 * and the clamp would never be reached. That is a later optimisation and is recorded here so it
 * is not reinvented.
 *
 * WHAT THE CLAMP DOES TO DERIVATIVES, which is the part with teeth: once delta is clamped it no
 * longer depends on the layer thickness, so d(anything)/d(thickness) is exactly zero. That is
 * physically right - an opaque layer's thickness does not matter - but it puts a zero column in
 * the Jacobian, which can make the normal equations singular. tf_plan reports such variables as
 * inert rather than letting an optimiser discover it as a failure to converge.
 * ------------------------------------------------------------------------------------------- */

#ifndef TF_DELTA_IMAG_CLAMP
#define TF_DELTA_IMAG_CLAMP 24.0
#endif

/* Returns non-zero if the clamp bit. */
TF_HD static inline int tf_layer_clamp_delta(tf_complex* delta)
{
    /* Written on the MAGNITUDE so that it is independent of which sign convention the imaginary
     * part carries. Under Macleod's N = n - i k the phase of an absorbing layer has Im(delta) < 0;
     * a test written as "> clamp" would silently never fire. */
    if (delta->im < -TF_DELTA_IMAG_CLAMP) { delta->im = -TF_DELTA_IMAG_CLAMP; return 1; }
    if (delta->im >  TF_DELTA_IMAG_CLAMP) { delta->im =  TF_DELTA_IMAG_CLAMP; return 1; }
    return 0;
}

/* Would this layer clamp, without modifying anything? Used by tf_plan, which must reach the same
 * verdict as the evaluator and therefore asks the same function rather than repeating the test. */
TF_HD static inline int tf_layer_would_clamp(tf_complex kappa, double thickness, double wavelength)
{
    const double im = (TF_TWO_PI * thickness / wavelength) * kappa.im;
    return (fabs(im) > TF_DELTA_IMAG_CLAMP) ? 1 : 0;
}

#ifdef __cplusplus
} /* extern "C" */
#endif

#endif /* TF_LAYER_H */
