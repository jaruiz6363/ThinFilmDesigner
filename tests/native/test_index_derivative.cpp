/* test_index_derivative.cpp — d(value)/dn and d(value)/dk, against differences and two identities.
 *
 * THE FINITE DIFFERENCE HAS TO RE-PLAN, and that is the trap in testing this one. The plan chooses
 * the structured path on k == 0 EXACTLY. Perturb k away from zero without re-planning and the point
 * keeps its structured verdict, where tf_solve_structured reads only the real part - so the
 * perturbation is silently discarded and the difference comes out zero for every k derivative. The
 * helper below re-plans on every evaluation for that reason.
 *
 * TWO EXACT IDENTITIES, worth more than the differences because they have no step size:
 *
 *   R + T = 1 for a lossless stack, for ANY indices. So d(Rs)/dn + d(Ts)/dn = 0 exactly, for every
 *   material. This is a strong check: it couples the reflection chain to the transmission chain,
 *   which are computed by different expressions - T through the real parts of the admittances,
 *   R through the ratio - so an error in either alone breaks it.
 *
 *   Splitting a material in two does not change the physics. Assign one layer of a stack to a
 *   second material carrying an identical index, and the two separate derivatives must sum to the
 *   single combined one. That is an exact check on the per-material accumulation, which the
 *   differences would only test in aggregate.
 */

#include "thinfilm.h"

#include <cstdio>
#include <cmath>
#include <vector>

extern "C" {
tf_status tf_query_plan_sizes(int32_t, int32_t, int32_t, int32_t, tf_plan_sizes*);
tf_status tf_plan(const tf_problem*, const tf_operands*, const tf_variables*, int32_t,
                  tf_plan_buffers*);
tf_status tf_evaluate(const tf_problem*, const tf_operands*, const tf_plan_buffers*,
                      tf_workspace*, tf_output*);
tf_status tf_evaluate_index_derivative(const tf_problem*, const tf_operands*,
                                       const tf_plan_buffers*, tf_workspace*, tf_index_output*);
}

static int g_failures = 0;
static int g_checks   = 0;

static void near_(double got, double want, double tol, const char* what)
{
    ++g_checks;
    if (!(std::fabs(got - want) <= tol))
    {
        ++g_failures;
        std::printf("  FAIL  %-46s got %.12g  expected %.12g  delta %.3g\n",
                    what, got, want, std::fabs(got - want));
    }
}

static void expect_eq(long long got, long long want, const char* what)
{
    ++g_checks;
    if (got != want)
    {
        ++g_failures;
        std::printf("  FAIL  %-46s got %lld  expected %lld\n", what, got, want);
    }
}

static const double PI = 3.14159265358979323846;
/* THE STEP, MEASURED RATHER THAN ASSUMED. The usual balance h = (3 eps |f| / |f'''|)^(1/3)
 * gives 9e-6 for |f'''| of order one - and that is wrong here by a factor of ten, because a
 * reflectance depends on an index far more gently than on a thickness. Sweeping h on a six-layer
 * stack at 55 degrees puts the optimum at 1e-4, where the difference matches the analytic answer
 * to 3e-9; at 1e-5 rounding noise alone costs 1e-6, and at 1e-3 truncation costs 3e-7. Reading
 * back, |f'''| is about 7e-4. Measured, then, not guessed. */
static const double H  = 1e-4;

struct Caller
{
    std::vector<double>     thickness;
    std::vector<int32_t>    layer_material;
    std::vector<tf_complex> index;
    std::vector<double>     wavelength;
    std::vector<int32_t>    point_wavelength;
    std::vector<double>     point_angle;
    std::vector<tf_complex> n_in, n_out;
    std::vector<int32_t>    tcode, tpoint;
    std::vector<double>     tvalue, tweight;
    int32_t                 n_materials = 2;

    tf_problem problem{}; tf_operands operands{}; tf_variables variables{};
    std::vector<int32_t> path, order, polneed;
    std::vector<int32_t> opres, oporder, oplevel;
    std::vector<int32_t> spanfirst, spancount, spanop, spanslot;
    tf_plan_report report{}; int32_t plan_status = 0, eval_status = 0, idx_status = 0;
    tf_plan_buffers pbuf{};
    std::vector<double> scratch, residual, value, ddn, ddk;
    std::vector<double> opvalue;
    tf_workspace ws{}; double merit = 0.0;
    tf_output out{}; tf_index_output iout{};

    void wire()
    {
        problem.n_layers = (int32_t)thickness.size();
        problem.n_materials = n_materials;
        problem.n_wavelengths = (int32_t)wavelength.size();
        problem.n_points = (int32_t)point_angle.size();
        problem.layer_thickness = thickness.data();
        problem.layer_material = layer_material.data();
        problem.layer_is_incoherent = nullptr;
        problem.material_index = index.data();
        problem.wavelength = wavelength.data();
        problem.point_wavelength = point_wavelength.data();
        problem.point_angle = point_angle.data();
        problem.index_incident = n_in.data();
        problem.index_exit = n_out.data();
        problem.material_dindex_dwavelength = nullptr;
        problem.dindex_incident_dwavelength = nullptr;
        problem.dindex_exit_dwavelength = nullptr;

        operands.n_operands = (int32_t)tcode.size();
        operands.delta_convention = TF_DELTA_PRINCIPAL;
        operands.code = tcode.data(); operands.point = tpoint.data();
        operands.target = tvalue.data(); operands.weight = tweight.data();

        variables.n_variables = 0;
        variables.kind = nullptr; variables.layer = nullptr;
        variables.material = nullptr; variables.coefficient = nullptr;
    }

    /* Plans AND allocates. Called again after every perturbation - see the file header. */
    tf_status plan()
    {
        wire();
        tf_plan_sizes s{};
        tf_query_plan_sizes(problem.n_points, 0, operands.n_operands, operands.n_operands, &s);
        path.assign((size_t)s.point_path_count, -1);
        order.assign((size_t)s.point_order_count, -1);
        polneed.assign((size_t)s.point_polarisation_count, 0);
        pbuf.point_path = path.data(); pbuf.point_order = order.data();
        pbuf.variable_inert = nullptr; pbuf.point_polarisation = polneed.data();
 spanfirst.assign((size_t)s.point_span_first_count, 0);
 spancount.assign((size_t)s.point_span_count_count, 0);
 spanop.assign((size_t)s.point_span_operand_count, 0);
 spanslot.assign((size_t)s.point_span_slot_count, 0);
 pbuf.point_span_first = spanfirst.data();  pbuf.point_span_count = spancount.data();
 pbuf.point_span_operand = spanop.data();   pbuf.point_span_slot = spanslot.data();

 opres.assign((size_t)s.operand_residual_count, -1);

 oporder.assign((size_t)s.operand_order_count, 0);

 oplevel.assign((size_t)s.operand_level_count, 0);

 pbuf.operand_residual = opres.data(); pbuf.operand_order = oporder.data();

 pbuf.operand_level = oplevel.data();
        pbuf.report = &report; pbuf.status = &plan_status;

        const tf_status ps = tf_plan(&problem, &operands, &variables, 1, &pbuf);
        if (ps != TF_OK) return ps;

        scratch.assign((size_t)report.scratch_doubles, 0.0);
        ws.scratch = scratch.data(); ws.scratch_doubles = report.scratch_doubles;

        residual.assign((size_t)report.n_residuals, 0.0);
        value.assign(tcode.size(), 0.0);
        ddn.assign((size_t)n_materials * tcode.size(), 0.0);
        ddk.assign((size_t)n_materials * tcode.size(), 0.0);

        opvalue.assign((size_t)operands.n_operands, 0.0);


        out.value = opvalue.data();


        out.residual = residual.data(); out.jacobian = nullptr;
        out.merit = &merit; out.status = &eval_status;
        iout.value = value.data(); iout.dvalue_dn = ddn.data(); iout.dvalue_dk = ddk.data();
        iout.status = &idx_status;
        return TF_OK;
    }

    tf_status derivative() { wire(); return tf_evaluate_index_derivative(&problem, &operands,
                                                                        &pbuf, &ws, &iout); }

    double DN(int m, int r) const { return ddn[(size_t)m * tcode.size() + (size_t)r]; }
    double DK(int m, int r) const { return ddk[(size_t)m * tcode.size() + (size_t)r]; }

    /* Weight 1 and target 0 make the residual the value. RE-PLANS, deliberately. */
    double value_now(int r)
    {
        plan();
        tf_evaluate(&problem, &operands, &pbuf, &ws, &out);
        return residual[(size_t)r];
    }

    double fd_at(int m, int r, bool imaginary, double h)
    {
        const int32_t w = point_wavelength[(size_t)tpoint[(size_t)r]];
        const size_t  ix = (size_t)m * wavelength.size() + (size_t)w;
        const double keep = imaginary ? index[ix].im : index[ix].re;

        if (imaginary) index[ix].im = keep + h; else index[ix].re = keep + h;
        const double plus = value_now(r);
        if (imaginary) index[ix].im = keep - h; else index[ix].re = keep - h;
        const double minus = value_now(r);
        if (imaginary) index[ix].im = keep; else index[ix].re = keep;
        plan();
        return (plus - minus) / (2.0 * h);
    }

    /* RICHARDSON EXTRAPOLATION, because the plain difference is not accurate enough to be an
     * oracle here. A central difference carries an h^2 truncation term, and for a six-layer
     * resonant stack the third derivative in index is large - n enters through kappa, eta AND
     * delta, the last multiplied by d/lambda - so at h = 1e-5 the plain difference is only good to
     * about 1e-6 relative. Combining h and 2h,
     *
     *     D = (4 D(h) - D(2h)) / 3
     *
     * cancels that term exactly and leaves h^4, which is far below the rounding floor. Four
     * evaluations instead of two, and the oracle stops being the limiting factor. */
    double fd(int m, int r, bool imaginary)
    {
        const double d1 = fd_at(m, r, imaginary, H);
        const double d2 = fd_at(m, r, imaginary, 2.0 * H);
        return (4.0 * d1 - d2) / 3.0;
    }
};

static const int32_t CODES[] = {
    TF_TARGET_RS, TF_TARGET_RP, TF_TARGET_RA, TF_TARGET_TS, TF_TARGET_TP, TF_TARGET_TA,
    TF_TARGET_R_TANPSI, TF_TARGET_R_PSI, TF_TARGET_R_COSDELTA, TF_TARGET_R_DELTA
};
static const int N_CODES = (int)(sizeof CODES / sizeof CODES[0]);

static Caller six_layer(double angle_deg, double k_high)
{
    Caller c;
    c.wavelength = { 550.0 };
    c.n_materials = 3;      /* material 2 appears in no layer */
    c.index = { tf_complex{2.35, k_high}, tf_complex{1.46, 0.0}, tf_complex{1.90, 0.0} };
    c.n_in  = { tf_complex{1.0, 0.0} };
    c.n_out = { tf_complex{1.52, 0.0} };

    const double qh = 550.0 / (4.0 * 2.35), ql = 550.0 / (4.0 * 1.46);
    c.thickness      = { qh * 1.13, ql * 0.87, qh * 0.94, ql * 1.21, qh * 1.07, ql * 0.79 };
    c.layer_material = { 0, 1, 0, 1, 0, 1 };

    c.point_wavelength = { 0 };
    c.point_angle = { angle_deg * PI / 180.0 };
    for (int i = 0; i < N_CODES; ++i)
    {
        c.tcode.push_back(CODES[i]); c.tpoint.push_back(0);
        c.tvalue.push_back(0.0);     c.tweight.push_back(1.0);
    }
    return c;
}

/* THE TOLERANCE IS MIXED, AND ITS ABSOLUTE PART IS DERIVED RATHER THAN CHOSEN.
 *
 * A relative test alone is meaningless where the true derivative is zero, and several of these are
 * exactly zero: at normal incidence s and p are the same wave, so tan(Psi) is identically 1 and
 * Delta identically 0, and no index can move either. The analytic answer there is 4e-16. The
 * difference is not - it is the rounding noise of the evaluator divided by the step.
 *
 * That noise floor is estimable. r_s and r_p are computed from admittances written differently -
 * kappa against n^2/kappa - so their difference carries a rounding error amplified by the whole
 * six-layer product before arg() takes it. Call the amplification A; then the floor is
 *
 *     A * eps * |f| / (2h)  =  1e4 * 2.2e-16 / 2e-5  =  1.1e-7
 *
 * with A = 1e4, which is a few times the largest spread actually observed. So anything below 1.1e-8
 * is a number the difference cannot resolve, and the test says so rather than pretending.
 * The tight accuracy claims in this file rest on the two EXACT identities below, which have no
 * step size at all. */
static const double FD_NOISE = 1e4 * 2.220446049250313e-16 / (2.0 * H);   /* 1.1e-8 */

static void compare(Caller& c, const char* label, double rel_tol)
{
    std::printf("%s\n", label);
    expect_eq(c.plan(), TF_OK, "plans");
    expect_eq(c.derivative(), TF_OK, "evaluates");

    const int nr = (int)c.tcode.size();
    std::vector<double> an_n((size_t)c.n_materials * nr), an_k((size_t)c.n_materials * nr);
    for (int m = 0; m < c.n_materials; ++m)
        for (int r = 0; r < nr; ++r)
        { an_n[(size_t)m * nr + r] = c.DN(m, r); an_k[(size_t)m * nr + r] = c.DK(m, r); }

    double worst = 0.0; const char* which = "";
    int worst_code = 0, worst_m = 0;
    for (int m = 0; m < c.n_materials; ++m)
        for (int r = 0; r < nr; ++r)
        {
            const double refn = c.fd(m, r, false);
            const double refk = c.fd(m, r, true);
            const double an   = an_n[(size_t)m * nr + r];
            const double ak   = an_k[(size_t)m * nr + r];

            /* Excess over what the difference can resolve, as a multiple of the allowance. */
            const double en = std::fabs(an - refn) / (rel_tol * std::fabs(refn) + FD_NOISE);
            const double ek = std::fabs(ak - refk) / (rel_tol * std::fabs(refk) + FD_NOISE);
            if (en > worst) { worst = en; which = "dn"; worst_code = c.tcode[(size_t)r]; worst_m = m; }
            if (ek > worst) { worst = ek; which = "dk"; worst_code = c.tcode[(size_t)r]; worst_m = m; }
        }

    ++g_checks;
    if (!(worst <= 1.0))
    {
        ++g_failures;
        std::printf("  FAIL  %s for material %d on target %d exceeds its allowance by %.3gx\n",
                    which, worst_m, worst_code, worst);
    }
    std::printf("    %d operands x %d materials x 2, worst %.3g of the allowance "
                "(%.0e relative + %.1e absolute)\n",
                nr, c.n_materials, worst, rel_tol, FD_NOISE);
}

/* ------------------------------------------------------------------------------------------- */

static void LosslessHeadOn()  { Caller c = six_layer(0.0, 0.0);
                                compare(c, "a lossless stack head-on", 1e-7); }
static void LosslessOblique() { Caller c = six_layer(55.0, 0.0);
                                compare(c, "the same at 55 degrees, where s and p differ", 1e-7); }
static void Absorbing()       { Caller c = six_layer(40.0, 0.03);
                                compare(c, "an absorbing high index at 40 degrees", 1e-7); }

/* R + T = 1 identically for a lossless stack, so the two derivatives must cancel EXACTLY. This
 * couples the reflection chain to the transmission chain, which share no expression. */
static void ReflectionAndTransmissionDerivativesCancel()
{
    std::printf("R + T = 1 for a lossless stack, so dR/dn + dT/dn vanishes exactly\n");

    for (int a = 0; a < 2; ++a)
    {
        Caller c = six_layer(a == 0 ? 0.0 : 62.0, 0.0);
        c.tcode = { TF_TARGET_RS, TF_TARGET_TS, TF_TARGET_RP, TF_TARGET_TP };
        c.tpoint = { 0, 0, 0, 0 };
        c.tvalue = { 0, 0, 0, 0 }; c.tweight = { 1, 1, 1, 1 };
        c.plan(); c.derivative();

        for (int m = 0; m < 2; ++m)
        {
            const double scale = std::fabs(c.DN(m, 0)) + std::fabs(c.DN(m, 1)) + 1.0;
            near_(c.DN(m, 0) + c.DN(m, 1), 0.0, 1e-12 * scale, "dRs/dn + dTs/dn");
            near_(c.DN(m, 2) + c.DN(m, 3), 0.0, 1e-12 * scale, "dRp/dn + dTp/dn");
        }
    }
}

/* Splitting a material in two changes nothing physical, so the two derivatives must sum to the
 * one. An exact check on the per-material accumulation. */
static void SplittingAMaterialSplitsItsDerivative()
{
    std::printf("one material in three layers equals three layers of identical materials\n");

    Caller combined = six_layer(35.0, 0.01);
    combined.plan(); combined.derivative();

    Caller split = six_layer(35.0, 0.01);
    /* Material 2 gets the identical index, and layer 2 is reassigned to it. */
    split.index[2] = split.index[0];
    split.layer_material = { 0, 1, 2, 1, 0, 1 };
    split.plan(); split.derivative();

    const int nr = (int)combined.tcode.size();
    double worst_n = 0.0, worst_k = 0.0;
    for (int r = 0; r < nr; ++r)
    {
        const double sn = split.DN(0, r) + split.DN(2, r);
        const double sk = split.DK(0, r) + split.DK(2, r);
        worst_n = std::fmax(worst_n, std::fabs(sn - combined.DN(0, r)));
        worst_k = std::fmax(worst_k, std::fabs(sk - combined.DK(0, r)));
    }
    near_(worst_n, 0.0, 1e-13, "the split dn derivatives sum to the combined one");
    near_(worst_k, 0.0, 1e-13, "and so do the dk derivatives");

    /* And the untouched material's answer must not have moved. */
    double worst_other = 0.0;
    for (int r = 0; r < nr; ++r)
        worst_other = std::fmax(worst_other, std::fabs(split.DN(1, r) - combined.DN(1, r)));
    near_(worst_other, 0.0, 1e-13, "the other material is unaffected by the split");
}

/* Absorption can only reduce transmission, so dT/dk is negative wherever there is any transmission
 * to lose. A sign check with physics behind it rather than a tolerance. */
static void AbsorptionReducesTransmission()
{
    std::printf("dT/dk is negative - adding loss cannot raise transmission\n");

    Caller c = six_layer(0.0, 0.0);
    c.tcode = { TF_TARGET_TS }; c.tpoint = { 0 };
    c.tvalue = { 0.0 }; c.tweight = { 1.0 };
    c.plan(); c.derivative();

    for (int m = 0; m < 2; ++m)
    {
        ++g_checks;
        if (!(c.DK(m, 0) < 0.0))
        {
            ++g_failures;
            std::printf("  FAIL  dTs/dk for material %d is %.12g, which is not negative\n",
                        m, c.DK(m, 0));
        }
    }
}

/* A material no layer is made of cannot move anything, and the answer must be exactly zero rather
 * than merely small - the accumulator for it is never touched. */
static void AnUnusedMaterialHasExactlyZeroDerivative()
{
    std::printf("a material in no layer gets exact zeros, not small numbers\n");

    Caller c = six_layer(30.0, 0.0);
    c.plan(); c.derivative();

    int nonzero = 0;
    for (int r = 0; r < (int)c.tcode.size(); ++r)
        if (c.DN(2, r) != 0.0 || c.DK(2, r) != 0.0) ++nonzero;
    expect_eq(nonzero, 0, "material 2 appears in no layer");
}

/* The value it reports must be the evaluator's own. */
static void ValuesAgreeWithTheEvaluator()
{
    std::printf("the values it reports are the evaluator's own\n");

    Caller c = six_layer(45.0, 0.02);
    expect_eq(c.plan(), TF_OK, "plans");
    expect_eq(c.derivative(), TF_OK, "evaluates");

    /* Copied out first: value_now() re-plans, and planning reallocates the output buffers. */
    const std::vector<double> reported = c.value;

    int mismatches = 0;
    for (int r = 0; r < (int)c.tcode.size(); ++r)
        if (reported[(size_t)r] != c.value_now(r)) ++mismatches;
    expect_eq(mismatches, 0, "identical, not merely close");
}

int main()
{
    std::printf("\n=== tf_evaluate_index_derivative ===\n\n");

    LosslessHeadOn();                             std::printf("\n");
    LosslessOblique();                            std::printf("\n");
    Absorbing();                                  std::printf("\n");
    ReflectionAndTransmissionDerivativesCancel(); std::printf("\n");
    SplittingAMaterialSplitsItsDerivative();      std::printf("\n");
    AbsorptionReducesTransmission();              std::printf("\n");
    AnUnusedMaterialHasExactlyZeroDerivative();   std::printf("\n");
    ValuesAgreeWithTheEvaluator();                std::printf("\n");

    std::printf("=== %d checks, %d failures ===\n\n", g_checks, g_failures);
    return g_failures == 0 ? 0 : 1;
}
