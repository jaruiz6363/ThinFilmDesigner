/* tf_complex_math.h — complex scalar arithmetic on tf_complex.
 *
 * Header-only and TF_HD throughout: these are the innermost operations, they must inline, and
 * they must compile for the device unchanged. No std::complex — it is not usable in device code
 * on every toolchain, its operator overloads hide the flop count, and the ABI already fixes the
 * layout as two adjacent doubles.
 *
 * Naive four-multiply complex product, deliberately. The three-multiply Karatsuba form trades a
 * multiply for three adds, which is a loss on any modern pipeline for doubles and costs accuracy.
 */

#ifndef TF_COMPLEX_MATH_H
#define TF_COMPLEX_MATH_H

#include "thinfilm.h"
#include <math.h>

#ifdef __cplusplus
extern "C" {
#endif

TF_HD static inline tf_complex tf_c(double re, double im)
{
    tf_complex z; z.re = re; z.im = im; return z;
}

TF_HD static inline tf_complex tf_c_add(tf_complex a, tf_complex b)
{
    return tf_c(a.re + b.re, a.im + b.im);
}

TF_HD static inline tf_complex tf_c_sub(tf_complex a, tf_complex b)
{
    return tf_c(a.re - b.re, a.im - b.im);
}

/* 4 real multiplies, 2 real adds. */
TF_HD static inline tf_complex tf_c_mul(tf_complex a, tf_complex b)
{
    return tf_c(a.re * b.re - a.im * b.im,
                a.re * b.im + a.im * b.re);
}

TF_HD static inline tf_complex tf_c_scale(tf_complex a, double s)
{
    return tf_c(a.re * s, a.im * s);
}

/* Smith's algorithm: scaling by the larger denominator component avoids the overflow and
 * underflow that the textbook form suffers when |b| is very large or very small. This matters —
 * admittances of a metal in the infrared are large, and the naive form loses them. */
TF_HD static inline tf_complex tf_c_div(tf_complex a, tf_complex b)
{
    if (fabs(b.re) >= fabs(b.im))
    {
        const double r = b.im / b.re;
        const double den = b.re + b.im * r;
        return tf_c((a.re + a.im * r) / den, (a.im - a.re * r) / den);
    }
    else
    {
        const double r = b.re / b.im;
        const double den = b.re * r + b.im;
        return tf_c((a.re * r + a.im) / den, (a.im * r - a.re) / den);
    }
}

TF_HD static inline double tf_c_abs2(tf_complex a)
{
    return a.re * a.re + a.im * a.im;
}

TF_HD static inline double tf_c_abs(tf_complex a)
{
    return hypot(a.re, a.im);   /* hypot, not sqrt(re*re+im*im): it does not overflow on the way */
}

TF_HD static inline double tf_c_arg(tf_complex a)
{
    return atan2(a.im, a.re);
}

/* Principal square root, taken on the branch with NON-POSITIVE IMAGINARY PART.
 *
 * THIS BRANCH CHOICE IS A PHYSICAL COMMITMENT, not a convention to be picked at random. The
 * normal wavevector component is kappa = sqrt(N^2 - N0^2 sin^2(theta0)), and the layer phase is
 * delta = (2 pi / lambda) d kappa. The engine works INTERNALLY in Macleod's convention,
 * N = n - i k with k >= 0 absorbing, because that is the convention his characteristic matrix
 *
 *     M = [[cos delta, (i/eta) sin delta], [i eta sin delta, cos delta]]
 *
 * belongs to. Under it a decaying wave requires Im(kappa) <= 0. Taking the other root gives a
 * wave that GROWS exponentially through the film - a plausible number, wrong sign of physics, no
 * run-time error, and R + T above one as the only symptom.
 *
 * THE ABI IS THE OTHER CONVENTION. Callers supply n + i k, which is what catalogues and
 * System.Numerics users expect; tf_index_internal converts once at the read boundary. Mixing the
 * two is exactly the mistake that produced R + T = 1.2466 the first time this was run. */
TF_HD static inline tf_complex tf_c_sqrt_decaying(tf_complex a)
{
    if (a.re == 0.0 && a.im == 0.0) return tf_c(0.0, 0.0);

    const double m = tf_c_abs(a);

    double re = sqrt(0.5 * (m + a.re));
    double im = -sqrt(0.5 * (m - a.re));    /* non-positive by construction */

    /* The pair must satisfy 2 re im = Im(a). With im <= 0 that fixes the sign of re. */
    if (a.im > 0.0) re = -re;

    /* A real non-negative argument has im == 0, and then the positive real root is wanted. */
    if (im == 0.0 && re < 0.0) re = -re;

    return tf_c(re, im);
}

TF_HD static inline int tf_c_is_real(tf_complex a, double tol)
{
    return (fabs(a.im) <= tol) ? 1 : 0;
}

#ifdef __cplusplus
} /* extern "C" */
#endif

#endif /* TF_COMPLEX_MATH_H */
