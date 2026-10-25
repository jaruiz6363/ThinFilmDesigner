/* tf_matrix2.h — the 2x2 multiply kernels.
 *
 * These are the innermost loops of the entire engine. Everything else is bookkeeping around them,
 * and the factor of four between the two paths lives here.
 *
 * Both forms are unimodular: det = 1 for a characteristic matrix, and the product of unimodular
 * matrices is unimodular, so det = 1 holds for a whole stack. That is a free invariant over the
 * complete chain and every test asserts it.
 */

#ifndef TF_MATRIX2_H
#define TF_MATRIX2_H

#include "tf_complex_math.h"

#ifdef __cplusplus
extern "C" {
#endif

/* ---------------------------------------------------------------------------------------------
 * General: eight doubles, 32 real multiplies and 24 real adds per product.
 * ------------------------------------------------------------------------------------------- */

TF_HD static inline tf_matrix2 tf_m2_identity(void)
{
    tf_matrix2 m;
    m.m00 = tf_c(1.0, 0.0); m.m01 = tf_c(0.0, 0.0);
    m.m10 = tf_c(0.0, 0.0); m.m11 = tf_c(1.0, 0.0);
    return m;
}

TF_HD static inline tf_matrix2 tf_m2_mul(tf_matrix2 a, tf_matrix2 b)
{
    tf_matrix2 r;
    r.m00 = tf_c_add(tf_c_mul(a.m00, b.m00), tf_c_mul(a.m01, b.m10));
    r.m01 = tf_c_add(tf_c_mul(a.m00, b.m01), tf_c_mul(a.m01, b.m11));
    r.m10 = tf_c_add(tf_c_mul(a.m10, b.m00), tf_c_mul(a.m11, b.m10));
    r.m11 = tf_c_add(tf_c_mul(a.m10, b.m01), tf_c_mul(a.m11, b.m11));
    return r;
}

TF_HD static inline tf_complex tf_m2_det(tf_matrix2 m)
{
    return tf_c_sub(tf_c_mul(m.m00, m.m11), tf_c_mul(m.m01, m.m10));
}

/* ---------------------------------------------------------------------------------------------
 * Structured: [[a, ib], [ic, d]] with a, b, c, d real. FOUR doubles, 8 real multiplies and
 * 4 real adds.
 *
 * The form is closed under multiplication, which is the entire reason this path exists:
 *
 *     [ a1  ib1 ] [ a2  ib2 ]   [ a1a2 - b1c2       i(a1b2 + b1d2) ]
 *     [ ic1 d1  ] [ ic2 d2  ] = [ i(c1a2 + d1c2)    d1d2 - c1b2     ]
 *
 * The two products of imaginary entries each contribute a factor of i^2 = -1, which is where the
 * subtractions come from. Get one of those signs wrong and the result is still a perfectly
 * well-formed matrix, so the cross-check against the general kernel below is not optional.
 * ------------------------------------------------------------------------------------------- */

TF_HD static inline tf_matrix2s tf_m2s_identity(void)
{
    tf_matrix2s m; m.a = 1.0; m.b = 0.0; m.c = 0.0; m.d = 1.0; return m;
}

TF_HD static inline tf_matrix2s tf_m2s_mul(tf_matrix2s x, tf_matrix2s y)
{
    tf_matrix2s r;
    r.a = x.a * y.a - x.b * y.c;
    r.b = x.a * y.b + x.b * y.d;
    r.c = x.c * y.a + x.d * y.c;
    r.d = x.d * y.d - x.c * y.b;
    return r;
}

/* det [[a, ib],[ic, d]] = ad - (ib)(ic) = ad + bc, and it is REAL. */
TF_HD static inline double tf_m2s_det(tf_matrix2s m)
{
    return m.a * m.d + m.b * m.c;
}

/* Promotion, for the mixed path and for cross-checking the structured kernel against the general
 * one. There is deliberately no demotion: a general matrix is not in this form in general, and a
 * function that quietly discarded the parts that do not fit would be exactly the kind of silent
 * wrong answer this project is built to avoid. */
TF_HD static inline tf_matrix2 tf_m2s_promote(tf_matrix2s m)
{
    tf_matrix2 r;
    r.m00 = tf_c(m.a, 0.0);  r.m01 = tf_c(0.0, m.b);
    r.m10 = tf_c(0.0, m.c);  r.m11 = tf_c(m.d, 0.0);
    return r;
}

#ifdef __cplusplus
} /* extern "C" */
#endif

#endif /* TF_MATRIX2_H */
