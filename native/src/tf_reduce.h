/* tf_reduce.h — how an operand's points combine into its value, and what that does to a derivative.
 *
 * ONE FUNCTION RETURNS THE VALUE AND EVERY LOCAL SLOPE, for the same reason tf_score_of returns a
 * residual and its slope together: the two share decisions. A maximum's derivative depends on
 * WHICH point won, and a deviation's on where the mean sits; recomputing either separately is an
 * invitation for the value and the gradient to disagree about a design they were both given.
 *
 *     value          = reduce(x_0 .. x_{k-1}, w_0 .. w_{k-1})
 *     dvalue_dx[p]   = the local factor for slot p
 *
 * Everything above this reads x_p from the solver; everything below multiplies dvalue_dx[p] by
 * whatever dx_p/d(parameter) it happens to have. The reduction never learns what the parameter is,
 * which is why one implementation serves thickness, wavelength, index and needle insertion alike.
 */

#ifndef TF_REDUCE_H
#define TF_REDUCE_H

#include "thinfilm.h"
#include <math.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Smooth maximum: (1/beta) log sum exp(beta x), computed about the true maximum so that exp never
 * overflows. As beta grows this tends to max(x) and its gradient tends to the indicator of the
 * argmax; below that the gradient is a softmax, spread over the near-winners. */
TF_HD static inline double tf_smoothmax(const double* x, int32_t k, double beta, double* dvdx)
{
    double m = x[0];
    for (int32_t i = 1; i < k; ++i) if (x[i] > m) m = x[i];

    double sum = 0.0;
    for (int32_t i = 0; i < k; ++i) { dvdx[i] = exp(beta * (x[i] - m)); sum += dvdx[i]; }

    for (int32_t i = 0; i < k; ++i) dvdx[i] /= sum;
    return m + log(sum) / beta;
}

/* The true maximum, with the derivative on the winner alone.
 *
 * TIES SHARE IT EQUALLY rather than going to the first index. A tie is exactly where the argmax is
 * about to move, so giving the whole derivative to whichever point happened to be scanned first
 * makes the gradient depend on array order - and two designs that differ by a rounding error would
 * then get different search directions. */
TF_HD static inline double tf_hardmax(const double* x, int32_t k, double* dvdx)
{
    double m = x[0];
    for (int32_t i = 1; i < k; ++i) if (x[i] > m) m = x[i];

    int32_t winners = 0;
    for (int32_t i = 0; i < k; ++i) { dvdx[i] = (x[i] == m) ? 1.0 : 0.0; if (x[i] == m) ++winners; }
    if (winners > 1)
        for (int32_t i = 0; i < k; ++i) dvdx[i] /= (double)winners;
    return m;
}

/* ---------------------------------------------------------------------------------------------
 * CROSSING — where a spectrum passes through a level.
 *
 * THE DERIVATIVE IS EXACT AND NEEDS NOTHING NEW, which is a better answer than the contract
 * sketched. It expected the implicit function theorem with d(f)/d(lambda) supplied by
 * tf_evaluate_wavelength_derivative, and the obstacle to that is fatal: a crossing does not land on
 * a grid point, and material_index is a TABLE, so nothing here can evaluate at a wavelength the
 * caller did not sample.
 *
 * So the crossing is defined on the SAMPLED spectrum. Between the two bracketing samples
 *
 *     lambda_e = la + (L - fa) (lb - la) / (fb - fa)
 *
 * and its derivative in the two sample values is the exact derivative of that expression:
 *
 *     d(lambda_e)/d(fa) = W (u - D) / D^2        u = L - fa
 *     d(lambda_e)/d(fb) = -W u / D^2             D = fb - fa,   W = lb - la
 *
 * THAT *IS* THE IMPLICIT FUNCTION THEOREM. Put d(fa)/dx = d(fb)/dx = g into the chain rule and the
 * two terms collapse to -g W / D = -(df/dx) / (df/dlambda). The theorem and the interpolation agree
 * because the interpolation is what defines df/dlambda here. Every other slot's slope is exactly
 * zero: a sample the crossing does not sit between cannot move it.
 *
 * Accuracy is therefore a function of grid density, which is managed-side policy (ledger G6.2). A
 * cubic refinement would widen the stencil to four samples without changing any of the above, and
 * is recorded as a refinement rather than taken.
 *
 * THE ABSCISSA IS THE POINT'S WAVELENGTH. A crossing in ANGLE - an edge moving through a cone -
 * would want this reduction over a different abscissa, and that wants a flag rather than a guess.
 * Not built.
 * ------------------------------------------------------------------------------------------- */

/* Interpolate - or extrapolate - the crossing from one pair of samples, with the two slopes. */
TF_HD static inline double tf_cross_pair(const double* x, const double* a, double level,
                                         int32_t i, int32_t j, double* dvi, double* dvj)
{
    const double u = level - x[i];
    const double D = x[j] - x[i];
    const double W = a[j] - a[i];

    if (D == 0.0) { *dvi = 0.0; *dvj = 0.0; return a[i]; }

    *dvi = W * (u - D) / (D * D);
    *dvj = -W * u / (D * D);
    return a[i] + u * W / D;
}

/* Reduce one operand's span. x and w are its k point values and weights; dvdx receives k slopes.
 * Returns non-zero on success, zero for a reduction code with no implementation - which tf_plan
 * has already refused, so it cannot happen here. */
TF_HD static inline int tf_reduce(int32_t code, const double* param,
                                  const double* x, const double* w, const double* abscissa,
                                  int32_t k, double* dvdx, double* out_value, int32_t* out_flag)
{
    const double beta = param[0];
    *out_flag = 0;
    switch (code)
    {
        case TF_REDUCE_SUM:
        {
            double v = 0.0;
            for (int32_t i = 0; i < k; ++i) { v += w[i] * x[i]; dvdx[i] = w[i]; }
            *out_value = v;
            return 1;
        }

        case TF_REDUCE_MAX:
            *out_value = (beta > 0.0) ? tf_smoothmax(x, k, beta, dvdx) : tf_hardmax(x, k, dvdx);
            return 1;

        case TF_REDUCE_MIN:
        {
            /* min(x) = -max(-x), and the chain rule puts the two negations back together into
             * the identity - so the slopes come out unnegated. Written this way rather than as a
             * second loop so that the smooth and hard forms cannot drift apart. */
            double v;
            if (beta > 0.0)
            {
                /* smoothmin needs -x; reuse dvdx as the scratch it would have needed anyway by
                 * negating in place, then restoring. */
                for (int32_t i = 0; i < k; ++i) dvdx[i] = -x[i];
                /* dvdx currently holds -x; tf_smoothmax must not read and write the same array, so
                 * the negated values are re-derived from x inside the loop below instead. */
                double m = dvdx[0];
                for (int32_t i = 1; i < k; ++i) if (dvdx[i] > m) m = dvdx[i];
                double sum = 0.0;
                for (int32_t i = 0; i < k; ++i) { const double e = exp(beta * (-x[i] - m)); sum += e; }
                for (int32_t i = 0; i < k; ++i) dvdx[i] = exp(beta * (-x[i] - m)) / sum;
                v = -(m + log(sum) / beta);
            }
            else
            {
                double m = x[0];
                for (int32_t i = 1; i < k; ++i) if (x[i] < m) m = x[i];
                int32_t winners = 0;
                for (int32_t i = 0; i < k; ++i) { dvdx[i] = (x[i] == m) ? 1.0 : 0.0;
                                                  if (x[i] == m) ++winners; }
                if (winners > 1) for (int32_t i = 0; i < k; ++i) dvdx[i] /= (double)winners;
                v = m;
            }
            *out_value = v;
            return 1;
        }

        case TF_REDUCE_PTV:
        {
            /* max - min. The two slope sets add: a point that is the maximum contributes +1, one
             * that is the minimum contributes -1, and a span of one point gives zero, correctly -
             * a single sample has no peak-to-valley. */
            double hi, lo;
            if (beta > 0.0)
            {
                hi = tf_smoothmax(x, k, beta, dvdx);       /* dvdx now holds the max slopes */
                double m = -x[0];
                for (int32_t i = 1; i < k; ++i) if (-x[i] > m) m = -x[i];
                double sum = 0.0;
                for (int32_t i = 0; i < k; ++i) sum += exp(beta * (-x[i] - m));
                lo = -(m + log(sum) / beta);
                for (int32_t i = 0; i < k; ++i) dvdx[i] -= exp(beta * (-x[i] - m)) / sum;
            }
            else
            {
                hi = tf_hardmax(x, k, dvdx);
                double m = x[0];
                for (int32_t i = 1; i < k; ++i) if (x[i] < m) m = x[i];
                int32_t losers = 0;
                for (int32_t i = 0; i < k; ++i) if (x[i] == m) ++losers;
                for (int32_t i = 0; i < k; ++i) if (x[i] == m) dvdx[i] -= 1.0 / (double)losers;
                lo = m;
            }
            *out_value = hi - lo;
            return 1;
        }

        case TF_REDUCE_RMS_DEV:
        {
            /* v = sqrt( sum w (x - xbar)^2 / sum w ),  xbar = sum w x / sum w
             *
             * dv/dx_p = w_p (x_p - xbar) / (W v). The term that would come from xbar moving
             * VANISHES, because sum w (x - xbar) is zero by construction - which is worth writing
             * down, since deriving it the long way and dropping that term by accident gives the
             * same expression for the wrong reason. */
            double W = 0.0, mean = 0.0;
            for (int32_t i = 0; i < k; ++i) { W += w[i]; mean += w[i] * x[i]; }
            if (W <= 0.0) { *out_value = 0.0; for (int32_t i = 0; i < k; ++i) dvdx[i] = 0.0; return 1; }
            mean /= W;

            double s = 0.0;
            for (int32_t i = 0; i < k; ++i) { const double d = x[i] - mean; s += w[i] * d * d; }
            const double v = sqrt(s / W);
            *out_value = v;

            if (v == 0.0)
            {
                /* Every point equal: the RMS is at its minimum and flat there. Zero is the honest
                 * slope, not a division by zero. */
                for (int32_t i = 0; i < k; ++i) dvdx[i] = 0.0;
            }
            else
            {
                for (int32_t i = 0; i < k; ++i) dvdx[i] = w[i] * (x[i] - mean) / (W * v);
            }
            return 1;
        }

        case TF_REDUCE_CROSSING:
        {
            const double level = param[0];
            const double hint  = param[1];

            for (int32_t i = 0; i < k; ++i) dvdx[i] = 0.0;
            if (k < 2) return 0;   /* tf_plan refuses a one-sample crossing */

            /* Every bracket the spectrum passes the level in. A sample sitting exactly ON the
             * level opens a bracket with its neighbour, so an exact hit is found rather than
             * stepped over. */
            int32_t chosen = -1, found = 0;
            for (int32_t i = 0; i + 1 < k; ++i)
            {
                const double da = x[i] - level, db = x[i + 1] - level;
                if ((da <= 0.0 && db >= 0.0) || (da >= 0.0 && db <= 0.0))
                {
                    ++found;
                    if (chosen < 0) { chosen = i; }
                    else if (hint > 0.0)
                    {
                        /* NEAREST THE HINT MEANS NEAREST THE CROSSING, not nearest the sample the
                         * bracket starts at. Two crossings a few nanometres apart can sit in
                         * adjacent intervals whose left edges are a whole grid step apart, and
                         * comparing the edges then picks the wrong one - which is exactly what
                         * this did until a test with two close crossings said so. Locating each
                         * candidate costs two divisions. */
                        double ia, ib, ja, jb;
                        const double ci = tf_cross_pair(x, abscissa, level, chosen, chosen + 1,
                                                        &ia, &ib);
                        const double cj = tf_cross_pair(x, abscissa, level, i, i + 1, &ja, &jb);
                        if (fabs(cj - hint) < fabs(ci - hint)) chosen = i;
                    }
                }
            }

            if (found > 0)
            {
                /* WHICH CROSSING, when there are several. A positive hint in param[1] takes the
                 * one nearest it, which is how an optimiser keeps hold of the same edge from one
                 * iteration to the next: the specification asks for "the crossing nearest the
                 * previous value", this engine holds no state between calls, so the caller carries
                 * it. A hint of zero or less takes the first in span order - deterministic, and
                 * needing no bookkeeping from a caller that has only one edge anyway. */
                if (found > 1) *out_flag |= TF_FLAG_AMBIGUOUS;
                *out_value = tf_cross_pair(x, abscissa, level, chosen, chosen + 1,
                                           &dvdx[chosen], &dvdx[chosen + 1]);
                return 1;
            }

            /* NO CROSSING AT ALL. Refusing here would stop the run at the moment the optimiser
             * most needs a number to move on - a design early in a run routinely has no edge yet.
             * So the value is the linear EXTRAPOLATION from the sample that comes nearest the
             * level: the same formula over a pair that does not bracket, whose gradient therefore
             * still points toward making a crossing exist.
             *
             * THIS IS THE ABSORPTION CLAMP'S PATTERN, NOT A HOLE IN RULE 5. Rule 5 governs
             * unimplemented paths returning plausible numbers. This is an implemented, defined
             * behaviour of a defined reduction, flagged beside the value, and never silently a
             * measurement - exactly as the clamp substitutes a defined value for one that would
             * overflow and reports how often it fired. */
            {
                int32_t near = 0;
                for (int32_t i = 1; i < k; ++i)
                    if (fabs(x[i] - level) < fabs(x[near] - level)) near = i;

                const int32_t other = (near + 1 < k) ? (near + 1) : (near - 1);
                *out_flag |= TF_FLAG_EXTRAPOLATED;
                *out_value = tf_cross_pair(x, abscissa, level, near, other,
                                           &dvdx[near], &dvdx[other]);

                /* A GUARD, because extrapolating from a nearly flat spectrum runs away and a
                 * residual of 1e12 nanometres would swamp every other operand in the merit.
                 * Clamped to one span width outside the span, and THE DERIVATIVE GOES WITH IT: a
                 * pinned value does not depend on the data, and saying otherwise would hand the
                 * optimiser a direction that changes nothing. */
                {
                    const double lo = (abscissa[0] < abscissa[k-1]) ? abscissa[0] : abscissa[k-1];
                    const double hi = (abscissa[0] < abscissa[k-1]) ? abscissa[k-1] : abscissa[0];
                    const double wide = hi - lo;
                    if (*out_value < lo - wide || *out_value > hi + wide)
                    {
                        *out_value = (*out_value < lo) ? (lo - wide) : (hi + wide);
                        for (int32_t i = 0; i < k; ++i) dvdx[i] = 0.0;
                        *out_flag |= TF_FLAG_PINNED;
                    }
                }
                return 1;
            }
        }

        default:
            return 0;   /* anything unknown; refused at plan time */
    }
}

#ifdef __cplusplus
} /* extern "C" */
#endif

#endif /* TF_REDUCE_H */
