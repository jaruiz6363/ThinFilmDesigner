/* test_spectral.cpp — the wavelength derivative against central differences.
 *
 * THE FINITE DIFFERENCE HAS TO MOVE THE INDICES TOO, and that is the whole difficulty of testing
 * this. Perturbing lambda while holding n fixed checks only the explicit 1/lambda in the phase
 * thickness - about a third of the chain - and would pass with every dispersion term missing. So
 * the fixtures carry an ANALYTIC dispersion law,
 *
 *     n(lambda) = A + B / lambda^2                  k(lambda) = k0 + k1 (lambda - 550)
 *
 * from which both the table and its exact derivative are generated. The difference then perturbs
 * the whole material state consistently and the comparison is real.
 *
 * THE FOUR FIXTURES ARE CHOSEN FOR THE TERMS THEY ISOLATE:
 *
 *   non-dispersive          dn/dlambda = 0 everywhere; only delta' survives. If this fails, the
 *                           geometry is wrong before dispersion is even involved.
 *   dispersive, normal      adds kappa' and eta' with u' still zero.
 *   immersed, oblique       n0 != 1 and dispersive, so u' = 2 n0 n0' sin^2(theta0) is non-zero.
 *                           This is the term that is exactly right in air and quietly wrong in
 *                           glass, and nothing else here would catch it.
 *   absorbing, oblique      complex kappa and eta, the branch of the square root in play. The
 *                           specification names absorbing layers and high AOI as where sign and
 *                           branch errors hide.
 *
 * STEP SIZE. Central difference error is h^2|f'''|/6 + eps|f|/h. A six-layer stack at 550 nm
 * varies on a scale of a few nanometres, so |f'''| ~ 1e-2 per nm^3 for a quantity of order one,
 * putting the optimum near (3 eps / 1e-2)^(1/3) = 3e-5 nm. h = 1e-3 nm sits above it, truncation
 * dominated, with an expected error near h^2/6 * 1e-2 = 1.7e-9. The tolerances below are set from
 * that rather than tuned until green.
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
tf_status tf_evaluate_wavelength_derivative(const tf_problem*, const tf_operands*,
                                            const tf_plan_buffers*, tf_workspace*,
                                            tf_spectral_output*);
}

static int g_failures = 0;
static int g_checks   = 0;

static void near_(double got, double want, double tol, const char* what)
{
    ++g_checks;
    if (!(std::fabs(got - want) <= tol))
    {
        ++g_failures;
        std::printf("  FAIL  %-50s got %.12g  expected %.12g  delta %.3g\n",
                    what, got, want, std::fabs(got - want));
    }
}

static void expect_eq(long long got, long long want, const char* what)
{
    ++g_checks;
    if (got != want)
    {
        ++g_failures;
        std::printf("  FAIL  %-50s got %lld  expected %lld\n", what, got, want);
    }
}

static const double PI = 3.14159265358979323846;
static const double H  = 1e-3;          /* nanometres; see the header */

/* An analytic dispersion law, so the derivative and the table come from the same place. */
struct Medium
{
    double A = 1.0, B = 0.0;     /* n = A + B / lambda^2   (Cauchy, lambda in nm) */
    double k0 = 0.0, k1 = 0.0;   /* k = k0 + k1 (lambda - 550) */

    tf_complex n(double l) const
    {
        return tf_complex{ A + B / (l * l), k0 + k1 * (l - 550.0) };
    }
    tf_complex dn(double l) const
    {
        return tf_complex{ -2.0 * B / (l * l * l), k1 };
    }
};

/* ---------------------------------------------------------------------------------------------
 * Caller
 * ------------------------------------------------------------------------------------------- */

struct Caller
{
    std::vector<Medium>  materials;
    Medium               incident, exit_;
    std::vector<double>  thickness;
    std::vector<int32_t> layer_material;
    double               lambda = 550.0;
    double               angle_deg = 0.0;
    std::vector<int32_t> tcode;

    /* generated */
    std::vector<tf_complex> index, dindex, n_in, dn_in, n_out, dn_out;
    std::vector<double>     wavelength, point_angle, tvalue, tweight;
    std::vector<int32_t>    point_wavelength, tpoint;
    std::vector<int32_t>    vkind, vlayer, vmaterial, vcoef;

    tf_problem problem{}; tf_operands operands{}; tf_variables variables{};
    std::vector<int32_t> path, order, inert, polneed;
    std::vector<int32_t> opres, oporder, oplevel;
    std::vector<int32_t> spanfirst, spancount, spanop, spanslot;
    tf_plan_report report{}; int32_t plan_status = 0, eval_status = 0, spec_status = 0;
    tf_plan_buffers pbuf{};
    std::vector<double> scratch, residual, value, slope;
    std::vector<double> opvalue;
    tf_workspace ws{}; double merit = 0.0;
    tf_output out{}; tf_spectral_output sout{};

    /* Regenerate every wavelength-dependent quantity from the dispersion laws. This is what makes
     * the finite difference legitimate: lambda and n move together, as they do in reality. */
    void at(double l)
    {
        lambda = l;
        wavelength = { l };
        index.clear(); dindex.clear();
        for (size_t m = 0; m < materials.size(); ++m)
        {
            index.push_back(materials[m].n(l));
            dindex.push_back(materials[m].dn(l));
        }
        n_in   = { incident.n(l) };  dn_in  = { incident.dn(l) };
        n_out  = { exit_.n(l) };     dn_out = { exit_.dn(l) };
    }

    void wire()
    {
        problem.n_layers = (int32_t)thickness.size();
        problem.n_materials = (int32_t)materials.size();
        problem.n_wavelengths = 1;
        problem.n_points = 1;
        problem.layer_thickness = thickness.empty() ? nullptr : thickness.data();
        problem.layer_material = layer_material.empty() ? nullptr : layer_material.data();
        problem.layer_is_incoherent = nullptr;
        problem.material_index = index.data();
        problem.wavelength = wavelength.data();
        problem.point_wavelength = point_wavelength.data();
        problem.point_angle = point_angle.data();
        problem.index_incident = n_in.data();
        problem.index_exit = n_out.data();
        problem.material_dindex_dwavelength = dindex.data();
        problem.dindex_incident_dwavelength = dn_in.data();
        problem.dindex_exit_dwavelength = dn_out.data();

        operands.n_operands = (int32_t)tcode.size();
        operands.delta_convention = TF_DELTA_PRINCIPAL;
        operands.code = tcode.data(); operands.point = tpoint.data();
        operands.target = tvalue.data(); operands.weight = tweight.data();

        variables.n_variables = 0;
        variables.kind = nullptr; variables.layer = nullptr;
        variables.material = nullptr; variables.coefficient = nullptr;
    }

    tf_status setup()
    {
        at(lambda);
        point_wavelength = { 0 };
        point_angle = { angle_deg * PI / 180.0 };
        tpoint.assign(tcode.size(), 0);
        tvalue.assign(tcode.size(), 0.0);
        tweight.assign(tcode.size(), 1.0);
        wire();

        tf_plan_sizes s{};
        tf_query_plan_sizes(1, 0, operands.n_operands, operands.n_operands, &s);
        path.assign((size_t)s.point_path_count, -1);
        order.assign((size_t)s.point_order_count, -1);
        polneed.assign((size_t)s.point_polarisation_count, 0);
        pbuf.point_path = path.data();
        pbuf.point_order = order.data();
        pbuf.variable_inert = nullptr;
        pbuf.point_polarisation = polneed.data();

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
        pbuf.report = &report;
        pbuf.status = &plan_status;

        const tf_status ps = tf_plan(&problem, &operands, &variables, 1, &pbuf);
        if (ps != TF_OK) return ps;

        scratch.assign((size_t)report.scratch_doubles, 0.0);
        ws.scratch = scratch.data();
        ws.scratch_doubles = report.scratch_doubles;

        residual.assign((size_t)report.n_residuals, 0.0);
        value.assign(tcode.size(), 0.0);
        slope.assign(tcode.size(), 0.0);
        opvalue.assign((size_t)operands.n_operands, 0.0);

        out.value = opvalue.data();

        out.residual = residual.data(); out.jacobian = nullptr;
        out.merit = &merit; out.status = &eval_status;
        sout.value = value.data(); sout.dvalue_dwavelength = slope.data();
        sout.status = &spec_status;
        return TF_OK;
    }

    tf_status spectral() { wire(); return tf_evaluate_wavelength_derivative(&problem, &operands,
                                                                           &pbuf, &ws, &sout); }

    /* The target value at a displaced wavelength, with every index moved to match. Weight 1 and
     * target 0 make the residual the value. */
    double value_at(double l, int r)
    {
        at(l); wire();
        tf_evaluate(&problem, &operands, &pbuf, &ws, &out);
        return residual[(size_t)r];
    }

    double fd(int r)
    {
        const double keep = lambda;
        const double plus  = value_at(keep + H, r);
        const double minus = value_at(keep - H, r);
        at(keep); wire();
        return (plus - minus) / (2.0 * H);
    }
};

static const int32_t ALL[] = {
    TF_TARGET_RS, TF_TARGET_RP, TF_TARGET_RA,
    TF_TARGET_TS, TF_TARGET_TP, TF_TARGET_TA,
    TF_TARGET_R_TANPSI, TF_TARGET_R_COSDELTA, TF_TARGET_R_PSI, TF_TARGET_R_DELTA,
    TF_TARGET_T_TANPSI, TF_TARGET_T_COSDELTA, TF_TARGET_T_PSI, TF_TARGET_T_DELTA
};
static const int N_ALL = (int)(sizeof ALL / sizeof ALL[0]);

static const char* name_of(int32_t c)
{
    switch (c)
    {
        case TF_TARGET_RS: return "Rs"; case TF_TARGET_RP: return "Rp";
        case TF_TARGET_RA: return "Ra"; case TF_TARGET_TS: return "Ts";
        case TF_TARGET_TP: return "Tp"; case TF_TARGET_TA: return "Ta";
        case TF_TARGET_R_TANPSI: return "R.tanPsi";
        case TF_TARGET_R_COSDELTA: return "R.cosDelta";
        case TF_TARGET_R_PSI: return "R.Psi";
        case TF_TARGET_R_DELTA: return "R.Delta";
        case TF_TARGET_T_TANPSI: return "T.tanPsi";
        case TF_TARGET_T_COSDELTA: return "T.cosDelta";
        case TF_TARGET_T_PSI: return "T.Psi";
        case TF_TARGET_T_DELTA: return "T.Delta";
        default: return "?";
    }
}

/* Six detuned quarter waves, so no slope sits at a stationary point. */
static Caller six_layer()
{
    Caller c;
    const double qh = 550.0 / (4.0 * 2.35), ql = 550.0 / (4.0 * 1.46);
    c.thickness      = { qh * 1.13, ql * 0.87, qh * 0.94, ql * 1.21, qh * 1.07, ql * 0.79 };
    c.layer_material = { 0, 1, 0, 1, 0, 1 };
    for (int i = 0; i < N_ALL; ++i) c.tcode.push_back(ALL[i]);
    return c;
}

static void compare(Caller& c, const char* label, double tol)
{
    std::printf("%s\n", label);
    expect_eq(c.setup(), TF_OK, "plans");
    expect_eq(c.spectral(), TF_OK, "evaluates");

    /* The value must be the value tf_evaluate reports, or the two calls disagree about the point
     * at which the slope was taken. */
    int value_mismatch = 0;
    double worst = 0.0; int worst_r = -1;
    for (int r = 0; r < (int)c.tcode.size(); ++r)
    {
        const double analytic = c.slope[(size_t)r];
        const double v        = c.value[(size_t)r];
        const double numeric  = c.fd(r);
        if (std::fabs(v - c.value_at(c.lambda, r)) > 1e-14) ++value_mismatch;
        const double d = std::fabs(analytic - numeric);
        if (d > worst) { worst = d; worst_r = r; }
    }
    expect_eq(value_mismatch, 0, "the value agrees with tf_evaluate");

    ++g_checks;
    if (!(worst <= tol))
    {
        ++g_failures;
        std::printf("  FAIL  worst slope disagreement %.3g at %s; tolerance %.3g\n",
                    worst, worst_r >= 0 ? name_of(c.tcode[(size_t)worst_r]) : "?", tol);
    }
    std::printf("    %d operands, worst disagreement %.3g (tolerance %.3g)\n",
                (int)c.tcode.size(), worst, tol);
}

/* ------------------------------------------------------------------------------------------- */

static void NonDispersive()
{
    Caller c = six_layer();
    c.materials = { Medium{2.35, 0.0, 0.0, 0.0}, Medium{1.46, 0.0, 0.0, 0.0} };
    c.incident  = Medium{1.0, 0.0, 0.0, 0.0};
    c.exit_     = Medium{1.52, 0.0, 0.0, 0.0};
    compare(c, "no dispersion at all: only the explicit 1/lambda in the phase thickness", 1e-8);
}

static void DispersiveNormal()
{
    Caller c = six_layer();
    /* B chosen so n(550) is about 2.35 and 1.46 with a realistic slope. */
    c.materials = { Medium{2.28, 21175.0, 0.0, 0.0}, Medium{1.44, 6050.0, 0.0, 0.0} };
    c.incident  = Medium{1.0, 0.0, 0.0, 0.0};
    c.exit_     = Medium{1.50, 6050.0, 0.0, 0.0};
    compare(c, "dispersive, head-on: kappa' and eta' now carry most of the answer", 1e-8);
}

/* The one that catches u' = 2 n0 n0' sin^2(theta0). In air n0' is zero and the term vanishes; in
 * glass it does not, and a derivative that dropped it would be wrong only here. */
static void ImmersedAndOblique()
{
    Caller c = six_layer();
    c.angle_deg = 45.0;
    c.materials = { Medium{2.28, 21175.0, 0.0, 0.0}, Medium{1.44, 6050.0, 0.0, 0.0} };
    c.incident  = Medium{1.50, 6050.0, 0.0, 0.0};     /* immersed, and dispersive */
    c.exit_     = Medium{1.48, 5200.0, 0.0, 0.0};
    compare(c, "immersed at 45 degrees: the transverse wavevector itself now disperses", 1e-8);
}

static void AbsorbingAndOblique()
{
    Caller c = six_layer();
    c.angle_deg = 60.0;
    c.materials = { Medium{2.28, 21175.0, 0.02, 1e-5}, Medium{1.44, 6050.0, 0.0, 0.0} };
    c.incident  = Medium{1.0, 0.0, 0.0, 0.0};
    c.exit_     = Medium{1.50, 6050.0, 0.0, 0.0};
    compare(c, "absorbing at 60 degrees - where the spec says branch errors hide", 1e-8);
}

/* A slope is only useful if its sign and units are right. Checked on a case whose answer can be
 * written down: a bare substrate has R independent of wavelength when nothing disperses, and
 * strictly increasing with n when the substrate does. */
static void SignAndUnitsOnACaseWithAKnownAnswer()
{
    std::printf("sign and magnitude on a bare surface, where the answer is closed form\n");

    Caller c;
    c.materials = { Medium{1.52, 0.0, 0.0, 0.0} };   /* unused - no layers */
    c.incident  = Medium{1.0, 0.0, 0.0, 0.0};
    c.exit_     = Medium{1.50, 6050.0, 0.0, 0.0};    /* n(550) = 1.52, dn/dl = -2B/l^3 < 0 */
    c.tcode = { TF_TARGET_RS };
    expect_eq(c.setup(), TF_OK, "plans");
    expect_eq(c.spectral(), TF_OK, "evaluates");

    const double l  = 550.0;
    const double n  = 1.50 + 6050.0 / (l * l);
    const double dn = -2.0 * 6050.0 / (l * l * l);

    /* R = ((1-n)/(1+n))^2  ->  dR/dn = 2 (1-n)/(1+n) * (-2)/(1+n)^2 */
    const double rr    = (1.0 - n) / (1.0 + n);
    const double dR_dn = 2.0 * rr * (-2.0) / ((1.0 + n) * (1.0 + n));
    near_(c.slope[0], dR_dn * dn, 1e-15, "dR/dlambda equals dR/dn times dn/dlambda");

    /* n falls with wavelength here, and R rises as n falls, so the slope must be negative. */
    ++g_checks;
    if (!(c.slope[0] < 0.0))
    {
        ++g_failures;
        std::printf("  FAIL  the sign is wrong: %.12g\n", c.slope[0]);
    }

    /* And a genuinely non-dispersive bare surface has exactly zero slope - no layers, no phase. */
    Caller z;
    z.materials = { Medium{1.52, 0.0, 0.0, 0.0} };
    z.incident  = Medium{1.0, 0.0, 0.0, 0.0};
    z.exit_     = Medium{1.52, 0.0, 0.0, 0.0};
    z.tcode = { TF_TARGET_RS };
    z.setup(); z.spectral();
    near_(z.slope[0], 0.0, 0.0, "a bare non-dispersive surface has exactly zero slope");
}

/* The conversion to d/d(omega), which is what group delay is expressed in. */
static void TheOmegaConversionIsExact()
{
    std::printf("d/dlambda converts to d/domega exactly, in femtoseconds\n");

    const double l = 550.0;
    const double f = tf_dlambda_to_domega(l);

    /* omega = 2 pi c / lambda, so dlambda/domega = -lambda^2/(2 pi c). */
    const double expected = -(l * l) / (2.0 * PI * 299.792458);
    near_(f, expected, 1e-15, "the factor is -lambda^2/(2 pi c)");

    ++g_checks;
    if (!(f < 0.0)) { ++g_failures; std::printf("  FAIL  omega decreases with lambda; the factor "
                                                "must be negative\n"); }

    /* THE CONVERSION CHECKED AGAINST A TRANSIT TIME, which is what a group delay is.
     *
     * A non-dispersive slab of index n and thickness d has phase phi = 2 pi n d / lambda, so
     * dphi/dlambda = -2 pi n d / lambda^2. Converting:
     *
     *     GD = (-lambda^2 / (2 pi c)) * (-2 pi n d / lambda^2)  =  n d / c
     *
     * exactly the one-way transit time. For 1 micron of n = 1.5 that is 5.00 fs, a number that can
     * be checked by hand and that fixes the sign, the factor of 2 pi and the units all at once -
     * which a bare constant would not. */
    {
        const double n = 1.5, d = 1000.0;            /* nanometres */
        const double dphi_dlambda = -2.0 * PI * n * d / (l * l);
        const double gd = f * dphi_dlambda;
        near_(gd, n * d / 299.792458, 1e-12, "1 um of n=1.5 gives its 5.00 fs transit time");
        near_(gd, 5.0034, 1e-4, "which is 5.00 fs, checkable by hand");
    }
}

/* Rule C0.7, at its sharpest. dn/dlambda cannot be invented by the engine, and a NULL must not be
 * read as zero: dispersion dominates group delay, so the wrong answer would be smooth, plausible
 * and out by a factor. */
static void MissingDispersionDataIsRefused()
{
    std::printf("no dn/dlambda means a refusal, not an assumption of zero\n");

    Caller c = six_layer();
    c.materials = { Medium{2.35, 0.0, 0.0, 0.0}, Medium{1.46, 0.0, 0.0, 0.0} };
    c.incident  = Medium{1.0, 0.0, 0.0, 0.0};
    c.exit_     = Medium{1.52, 0.0, 0.0, 0.0};
    expect_eq(c.setup(), TF_OK, "plans");

    c.wire();
    c.problem.material_dindex_dwavelength = nullptr;
    std::printf("    the diagnostic below is expected:\n");
    std::fflush(stdout);
    expect_eq(tf_evaluate_wavelength_derivative(&c.problem, &c.operands, &c.pbuf, &c.ws, &c.sout),
              TF_NULL_POINTER, "refused");
    expect_eq(c.spec_status, TF_NULL_POINTER, "and said so in status");
}

int main()
{
    std::printf("\n=== tf_evaluate_wavelength_derivative ===\n\n");

    NonDispersive();                        std::printf("\n");
    DispersiveNormal();                     std::printf("\n");
    ImmersedAndOblique();                   std::printf("\n");
    AbsorbingAndOblique();                  std::printf("\n");
    SignAndUnitsOnACaseWithAKnownAnswer();  std::printf("\n");
    TheOmegaConversionIsExact();            std::printf("\n");
    MissingDispersionDataIsRefused();       std::printf("\n");

    std::printf("=== %d checks, %d failures ===\n\n", g_checks, g_failures);
    return g_failures == 0 ? 0 : 1;
}
