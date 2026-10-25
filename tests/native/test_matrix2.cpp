/* test_matrix2.cpp — the 2x2 kernels and the layer matrix.
 *
 * No test framework, on purpose: this is a public repository and a dependency that buys a nicer
 * assertion message is not worth the build friction. Failures print what was expected, what came
 * out, and by how much.
 *
 * THE TEST THAT MATTERS is TheStructuredKernelAgreesWithTheGeneralOne. The structured multiply
 * has two sign flips coming from i^2 = -1, and getting one wrong still produces a perfectly
 * well-formed matrix with a plausible determinant. Checking it against itself would pass. It is
 * checked against the general kernel on promoted inputs, which shares none of its arithmetic.
 */

#include "tf_layer.h"

#include <cstdio>
#include <cmath>
#include <cstdlib>

static int g_failures = 0;
static int g_checks   = 0;

static void check(bool ok, const char* what, double got, double expected)
{
    ++g_checks;
    if (!ok)
    {
        ++g_failures;
        std::printf("  FAIL  %-56s got %+.17g  expected %+.17g  delta %.3g\n",
                    what, got, expected, std::fabs(got - expected));
    }
}

static void near(double got, double expected, double tol, const char* what)
{
    check(std::fabs(got - expected) <= tol, what, got, expected);
}

static void near_c(tf_complex got, tf_complex expected, double tol, const char* what)
{
    char buf[256];
    std::snprintf(buf, sizeof buf, "%s (re)", what);
    near(got.re, expected.re, tol, buf);
    std::snprintf(buf, sizeof buf, "%s (im)", what);
    near(got.im, expected.im, tol, buf);
}

static void near_m2(tf_matrix2 got, tf_matrix2 expected, double tol, const char* what)
{
    char buf[256];
    std::snprintf(buf, sizeof buf, "%s m00", what); near_c(got.m00, expected.m00, tol, buf);
    std::snprintf(buf, sizeof buf, "%s m01", what); near_c(got.m01, expected.m01, tol, buf);
    std::snprintf(buf, sizeof buf, "%s m10", what); near_c(got.m10, expected.m10, tol, buf);
    std::snprintf(buf, sizeof buf, "%s m11", what); near_c(got.m11, expected.m11, tol, buf);
}

/* A spread of structured matrices that are not special cases of each other. */
static tf_matrix2s sample_s(int i)
{
    const double delta[] = { 0.0, 0.3, 1.0, 2.7, 3.14159, 5.5, 0.017 };
    const double eta[]   = { 1.0, 1.52, 0.31, 2.35, 4.0,   1.0, 0.77 };
    return tf_layer_matrix_s(delta[i % 7], eta[i % 7]);
}

/* ------------------------------------------------------------------------------------------- */

static void TheLayerMatrixIsUnimodular()
{
    std::printf("the layer matrix is unimodular, structured and general\n");

    for (int i = 0; i < 7; ++i)
    {
        near(tf_m2s_det(sample_s(i)), 1.0, 1e-14, "det of a structured layer matrix");
    }

    /* Complex delta and eta: an absorbing layer, where cos^2 + sin^2 = 1 still holds but nothing
     * else about the matrix is real. */
    const tf_complex delta = tf_c(1.3, 0.45);
    const tf_complex eta   = tf_c(2.1, -0.6);
    const tf_matrix2 m     = tf_layer_matrix(delta, eta);
    near_c(tf_m2_det(m), tf_c(1.0, 0.0), 1e-13, "det of an absorbing layer matrix");
}

static void UnimodularitySurvivesTheWholeChain()
{
    std::printf("det = 1 through a long product, and how far it can be trusted\n");

    tf_matrix2s acc_s = tf_m2s_identity();
    tf_matrix2  acc_g = tf_m2_identity();
    for (int i = 0; i < 40; ++i)
    {
        acc_s = tf_m2s_mul(acc_s, sample_s(i));
        acc_g = tf_m2_mul(acc_g, tf_m2s_promote(sample_s(i)));
    }

    /* THE TOLERANCE IS DERIVED, NOT GUESSED, because det = ad + bc is a difference of large
     * nearly-equal numbers once the accumulated entries grow. Forty layers with admittances
     * between 0.31 and 4 drive the entries into the thousands, so ad and bc are each of order
     * 1e6 while their difference is 1. The achievable absolute accuracy is about
     * (largest entry)^2 * epsilon, and asking for better is asking arithmetic to do something
     * it cannot.
     *
     * THIS IS A PROPERTY OF THE ENGINE, NOT A QUIRK OF THE TEST. det = 1 is a sharp check on one
     * layer and a blunt one on a long stack, and the same cancellation that blunts it also limits
     * the accuracy of the stack matrix itself on high-contrast designs. Measured here so the
     * limit is known rather than discovered later. */
    double largest = 0.0;
    if (std::fabs(acc_s.a) > largest) largest = std::fabs(acc_s.a);
    if (std::fabs(acc_s.b) > largest) largest = std::fabs(acc_s.b);
    if (std::fabs(acc_s.c) > largest) largest = std::fabs(acc_s.c);
    if (std::fabs(acc_s.d) > largest) largest = std::fabs(acc_s.d);

    const double eps = 2.220446049250313e-16;
    const double tol = 16.0 * largest * largest * eps;

    std::printf("    largest accumulated entry %.4g, so det is trustworthy to about %.2g\n",
                largest, tol);

    near(tf_m2s_det(acc_s), 1.0, tol, "det after 40 structured products");
    near_c(tf_m2_det(acc_g), tf_c(1.0, 0.0), tol, "det after 40 general products");

    /* The check that is NOT blunted: whatever the cancellation does to det, the two kernels must
     * still agree with each other. */
    near(acc_s.a, acc_g.m00.re, 1e-9, "the two kernels agree after 40 products (a)");
    near(acc_s.b, acc_g.m01.im, 1e-9, "the two kernels agree after 40 products (b)");
    near(acc_s.c, acc_g.m10.im, 1e-9, "the two kernels agree after 40 products (c)");
    near(acc_s.d, acc_g.m11.re, 1e-9, "the two kernels agree after 40 products (d)");
}

static void TheStructuredKernelAgreesWithTheGeneralOne()
{
    std::printf("the structured multiply against the general one - the sign check\n");

    for (int i = 0; i < 7; ++i)
        for (int j = 0; j < 7; ++j)
        {
            const tf_matrix2s x = sample_s(i);
            const tf_matrix2s y = sample_s(j);

            const tf_matrix2 viaStructured = tf_m2s_promote(tf_m2s_mul(x, y));
            const tf_matrix2 viaGeneral    = tf_m2_mul(tf_m2s_promote(x), tf_m2s_promote(y));

            near_m2(viaStructured, viaGeneral, 1e-14, "structured vs general product");
        }
}

static void TheStructuredFormIsClosed()
{
    std::printf("the product of two structured matrices is structured - promoted, the real parts\n"
                "of the off-diagonal and the imaginary parts of the diagonal must vanish exactly\n");

    for (int i = 0; i < 7; ++i)
        for (int j = 0; j < 7; ++j)
        {
            const tf_matrix2 p = tf_m2_mul(tf_m2s_promote(sample_s(i)), tf_m2s_promote(sample_s(j)));
            near(p.m00.im, 0.0, 0.0, "diagonal stays real (m00)");
            near(p.m11.im, 0.0, 0.0, "diagonal stays real (m11)");
            near(p.m01.re, 0.0, 0.0, "off-diagonal stays imaginary (m01)");
            near(p.m10.re, 0.0, 0.0, "off-diagonal stays imaginary (m10)");
        }
}

static void AZeroThicknessLayerIsTheIdentity()
{
    std::printf("a layer of no thickness is the identity, both paths\n");

    const tf_matrix2s s = tf_layer_matrix_s(0.0, 1.7);
    near(s.a, 1.0, 0.0, "structured identity a");
    near(s.b, 0.0, 0.0, "structured identity b");
    near(s.c, 0.0, 0.0, "structured identity c");
    near(s.d, 1.0, 0.0, "structured identity d");

    near_m2(tf_layer_matrix(tf_c(0.0, 0.0), tf_c(2.1, -0.6)), tf_m2_identity(), 0.0,
            "general identity");
}

static void TheDerivativeMatchesACentralDifference()
{
    std::printf("dM/d(delta) against a central difference - the analytic derivative checked\n"
                "against arithmetic that shares none of its algebra\n");

    const double h = 1e-6;

    /* structured */
    {
        const double delta = 1.1, eta = 1.46;
        const tf_matrix2s analytic = tf_layer_matrix_s_ddelta(delta, eta);
        const tf_matrix2s plus  = tf_layer_matrix_s(delta + h, eta);
        const tf_matrix2s minus = tf_layer_matrix_s(delta - h, eta);
        near(analytic.a, (plus.a - minus.a) / (2 * h), 1e-8, "structured dM/ddelta a");
        near(analytic.b, (plus.b - minus.b) / (2 * h), 1e-8, "structured dM/ddelta b");
        near(analytic.c, (plus.c - minus.c) / (2 * h), 1e-8, "structured dM/ddelta c");
        near(analytic.d, (plus.d - minus.d) / (2 * h), 1e-8, "structured dM/ddelta d");
    }

    /* general, with a complex delta so the absorbing path is exercised too */
    {
        const tf_complex delta = tf_c(1.1, 0.35);
        const tf_complex eta   = tf_c(2.1, -0.6);
        const tf_matrix2 analytic = tf_layer_matrix_ddelta(delta, eta);
        const tf_matrix2 plus  = tf_layer_matrix(tf_c(delta.re + h, delta.im), eta);
        const tf_matrix2 minus = tf_layer_matrix(tf_c(delta.re - h, delta.im), eta);

        tf_matrix2 fd;
        fd.m00 = tf_c_scale(tf_c_sub(plus.m00, minus.m00), 1.0 / (2 * h));
        fd.m01 = tf_c_scale(tf_c_sub(plus.m01, minus.m01), 1.0 / (2 * h));
        fd.m10 = tf_c_scale(tf_c_sub(plus.m10, minus.m10), 1.0 / (2 * h));
        fd.m11 = tf_c_scale(tf_c_sub(plus.m11, minus.m11), 1.0 / (2 * h));
        near_m2(analytic, fd, 1e-7, "general dM/ddelta");
    }
}

static void KappaTakesTheDecayingBranch()
{
    std::printf("kappa is on the branch that decays - the choice that has no run-time symptom\n");

    /* THE ENGINE WORKS INTERNALLY IN MACLEOD'S N = n - i k, which is the convention the
     * characteristic matrix belongs to, so tf_layer_kappa takes an INTERNAL index and a decaying
     * wave has Im(kappa) <= 0. The ABI takes n + i k and tf_index_internal converts.
     *
     * These assertions were written the other way round first, and passed, while the evaluator
     * produced R + T = 1.2466 on an absorbing stack - energy created out of nothing. The
     * conservation check in test_evaluate is what caught it, not this file, because a sign
     * convention is only wrong RELATIVE to the formula it is used with. */
    {
        const tf_complex n_abi = tf_c(2.0, 0.5);           /* what a caller supplies */
        const tf_complex kappa = tf_layer_kappa(tf_index_internal(n_abi), tf_c(0.0, 0.0));
        near_c(kappa, tf_c(2.0, -0.5), 1e-14, "kappa at normal incidence is the internal N");
        check(kappa.im <= 0.0, "kappa has non-positive imaginary part (absorbing)",
              kappa.im, 0.0);
    }

    /* Beyond the critical angle: lossless layer, n = 1.0, illuminated from n0 = 1.5 at 60 deg, so
     * (n0 sin theta0)^2 = 1.6875 > n^2 = 1. kappa must be purely imaginary and NEGATIVE - an
     * evanescent wave decaying into the layer. The other root grows, and nothing in the physics
     * would otherwise complain. */
    {
        const double n0s = 1.5 * std::sin(60.0 * 3.14159265358979323846 / 180.0);
        const tf_complex kappa = tf_layer_kappa(tf_c(1.0, 0.0), tf_c(n0s * n0s, 0.0));
        near(kappa.re, 0.0, 1e-15, "kappa is purely imaginary beyond critical");
        check(kappa.im < 0.0, "kappa decays rather than grows beyond critical",
              kappa.im, -1.0);
        near(kappa.im, -std::sqrt(n0s * n0s - 1.0), 1e-14, "kappa magnitude beyond critical");
    }

    /* And the square of the root is the argument, whichever branch was taken - the check that
     * does not depend on the convention at all. */
    {
        const tf_complex a[] = { tf_c(2.2475, -0.15), tf_c(-1.6875, 0.0), tf_c(4.0, 0.0),
                                 tf_c(0.3, -2.7), tf_c(-0.5, -0.001) };
        for (int i = 0; i < 5; ++i)
        {
            const tf_complex root = tf_c_sqrt_decaying(a[i]);
            near_c(tf_c_mul(root, root), a[i], 1e-13, "root squared is the argument");
            check(root.im <= 0.0, "and it is on the decaying branch", root.im, 0.0);
        }
    }
}

static void SmithDivisionSurvivesExtremeAdmittances()
{
    std::printf("complex division at the magnitudes a metal actually produces\n");

    const tf_complex big   = tf_c(1e160, -3e159);
    const tf_complex small = tf_c(2e-160, 5e-161);
    const tf_complex q     = tf_c_div(big, big);
    near_c(q, tf_c(1.0, 0.0), 1e-14, "z / z at 1e160");

    const tf_complex r = tf_c_div(small, small);
    near_c(r, tf_c(1.0, 0.0), 1e-14, "z / z at 1e-160");
}

/* ------------------------------------------------------------------------------------------- */

int main()
{
    std::printf("\nthinfilm native: 2x2 kernels and the layer matrix\n");
    std::printf("-------------------------------------------------\n");

    TheLayerMatrixIsUnimodular();
    UnimodularitySurvivesTheWholeChain();
    TheStructuredKernelAgreesWithTheGeneralOne();
    TheStructuredFormIsClosed();
    AZeroThicknessLayerIsTheIdentity();
    TheDerivativeMatchesACentralDifference();
    KappaTakesTheDecayingBranch();
    SmithDivisionSurvivesExtremeAdmittances();

    std::printf("-------------------------------------------------\n");
    std::printf("%d checks, %d failures\n\n", g_checks, g_failures);
    return g_failures == 0 ? 0 : 1;
}
