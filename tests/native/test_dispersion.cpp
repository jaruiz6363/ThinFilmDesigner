/* test_dispersion.cpp — group delay, GDD (ABI 11) and TOD (ABI 12), against physics and against differences.
 *
 * WHAT IS CHECKED, and what each check could not survive:
 *
 *   an air gap        A slab of n = 1 of thickness L in front of a non-dispersive substrate adds
 *                     2L/c to the reflected GD and L/c to the transmitted, and nothing to either
 *                     GDD or TOD. This fixes the SIGN and the UNITS, which no comparison of the engine
 *                     against itself can.
 *   GD vs the phase   GD against a central difference of R_PHASE / T_PHASE in wavelength, with
 *                     every index moved by an analytic dispersion law - dispersive, absorbing,
 *                     immersed and oblique, s and p.
 *   GDD vs GD         GDD against a central difference of the engine's own GD, the same way.
 *                     This is what tests the series' second order and n''.
 *   TOD vs GDD        TOD against a central difference of the engine's own GDD: the third order
 *                     and n'''.
 *   the Jacobian      d(GD)/d(d_j), d(GDD)/d(d_j) and d(TOD)/d(d_j) against central differences in
 *                     thickness.
 *   s = p at 0        at normal incidence the two polarisations are the same wave.
 *   refusals          GD without dn/dlambda, GDD without d2n/dlambda2, TOD without d3n/dlambda3:
 *                     TF_NO_DISPERSION_DATA, and each order needs no higher derivative.
 *
 * STEPS. GD varies on the scale of the stack's spectral features, nanometres here, and is a
 * difference of phases of order one, so h = 1e-3 nm in wavelength leaves a truncation error near
 * h^2 times a few fs/nm^2 - far below the tolerances, which are set from that. Thickness moves the
 * same phases at the same rate, so the same step serves there.
 */

#include "thinfilm.h"

#include <cmath>
#include <cstdio>
#include <vector>

extern "C" {
tf_status tf_query_plan_sizes(int32_t, int32_t, int32_t, int32_t, tf_plan_sizes*);
tf_status tf_plan(const tf_problem*, const tf_operands*, const tf_variables*, int32_t, tf_plan_buffers*);
tf_status tf_evaluate(const tf_problem*, const tf_operands*, const tf_plan_buffers*, tf_workspace*, tf_output*);
tf_status tf_evaluate_needle(const tf_problem*, const tf_operands*, const tf_plan_buffers*,
                             const tf_needle_request*, tf_workspace*, tf_needle_output*);
tf_status tf_evaluate_jacobian(const tf_problem*, const tf_operands*, const tf_variables*,
                               const tf_plan_buffers*, tf_workspace*, tf_output*);
}

static int g_checks = 0, g_failures = 0;

static void expect(bool ok, const char* what)
{
    ++g_checks;
    if (!ok) { ++g_failures; std::printf("  FAIL  %s\n", what); }
}

static void near_(double got, double want, double tol, const char* what)
{
    ++g_checks;
    if (!(std::fabs(got - want) <= tol))
    {
        ++g_failures;
        std::printf("  FAIL  %-55s got %.12g  expected %.12g  delta %.3g\n", what, got, want, std::fabs(got - want));
    }
}

static const double PI = 3.14159265358979323846;
static const double C_NM_FS = 299.792458;

/* n = A + B/lambda^2, k = k0 + k1 (lambda - 550): the table, its slope and its curvature from one law. */
struct Medium
{
    double A = 1.0, B = 0.0, k0 = 0.0, k1 = 0.0;
    tf_complex n(double l) const { return tf_complex{ A + B / (l * l), k0 + k1 * (l - 550.0) }; }
    tf_complex dn(double l) const { return tf_complex{ -2.0 * B / (l * l * l), k1 }; }
    tf_complex d2n(double l) const { return tf_complex{ 6.0 * B / (l * l * l * l), 0.0 }; }
    tf_complex d3n(double l) const { return tf_complex{ -24.0 * B / (l * l * l * l * l), 0.0 }; }
};

struct Case
{
    std::vector<Medium>  materials;
    Medium               incident, exit_;
    std::vector<double>  thickness;
    std::vector<int32_t> layer_material;
    std::vector<int32_t> code;
    double lambda = 800.0, angle_deg = 0.0;
    bool with_first = true, with_second = true, with_third = true, with_variables = false;

    std::vector<tf_complex> index, dindex, d2index, d3index, n_in, dn_in, d2n_in, d3n_in, n_out, dn_out, d2n_out, d3n_out;
    std::vector<double> wavelength, point_angle, target, weight;
    std::vector<int32_t> point_wavelength, point, vkind, vlayer;

    tf_problem problem{}; tf_operands operands{}; tf_variables variables{};
    std::vector<int32_t> path, order, inert, polneed, spanfirst, spancount, spanop, spanslot, opres, oporder, oplevel;
    tf_plan_report report{}; int32_t plan_status = 0, eval_status = 0;
    tf_plan_buffers pbuf{};
    std::vector<double> scratch, residual, value, jacobian;
    tf_workspace ws{}; double merit = 0.0; tf_output out{};

    void at(double l)
    {
        lambda = l;
        wavelength = { l };
        index.clear(); dindex.clear(); d2index.clear(); d3index.clear();
        for (const Medium& m : materials)
        { index.push_back(m.n(l)); dindex.push_back(m.dn(l)); d2index.push_back(m.d2n(l)); d3index.push_back(m.d3n(l)); }
        n_in = { incident.n(l) }; dn_in = { incident.dn(l) }; d2n_in = { incident.d2n(l) }; d3n_in = { incident.d3n(l) };
        n_out = { exit_.n(l) };   dn_out = { exit_.dn(l) };   d2n_out = { exit_.d2n(l) };   d3n_out = { exit_.d3n(l) };
        wire();
    }

    void wire()
    {
        problem.n_layers = (int32_t)thickness.size();
        problem.n_materials = (int32_t)materials.size();
        problem.n_wavelengths = 1;
        problem.n_points = 1;
        problem.layer_thickness = thickness.empty() ? nullptr : thickness.data();
        problem.layer_material = layer_material.empty() ? nullptr : layer_material.data();
        problem.material_index = index.empty() ? nullptr : index.data();
        problem.wavelength = wavelength.data();
        problem.point_wavelength = point_wavelength.data();
        problem.point_angle = point_angle.data();
        problem.index_incident = n_in.data();
        problem.index_exit = n_out.data();
        problem.material_dindex_dwavelength = with_first && !dindex.empty() ? dindex.data() : nullptr;
        problem.dindex_incident_dwavelength = with_first ? dn_in.data() : nullptr;
        problem.dindex_exit_dwavelength = with_first ? dn_out.data() : nullptr;
        problem.material_d2index_dwavelength2 = with_second && !d2index.empty() ? d2index.data() : nullptr;
        problem.d2index_incident_dwavelength2 = with_second ? d2n_in.data() : nullptr;
        problem.d2index_exit_dwavelength2 = with_second ? d2n_out.data() : nullptr;
        problem.material_d3index_dwavelength3 = with_third && !d3index.empty() ? d3index.data() : nullptr;
        problem.d3index_incident_dwavelength3 = with_third ? d3n_in.data() : nullptr;
        problem.d3index_exit_dwavelength3 = with_third ? d3n_out.data() : nullptr;

        operands.n_operands = (int32_t)code.size();
        operands.delta_convention = TF_DELTA_PRINCIPAL;
        operands.code = code.data(); operands.point = point.data();
        operands.target = target.data(); operands.weight = weight.data();

        variables.n_variables = (int32_t)vkind.size();
        variables.kind = vkind.empty() ? nullptr : vkind.data();
        variables.layer = vlayer.empty() ? nullptr : vlayer.data();
    }

    tf_status setup()
    {
        point_wavelength = { 0 };
        point_angle = { angle_deg * PI / 180.0 };
        point.assign(code.size(), 0);
        target.assign(code.size(), 0.0);
        weight.assign(code.size(), 1.0);
        vkind.clear(); vlayer.clear();
        if (with_variables)
            for (int32_t j = 0; j < (int32_t)thickness.size(); ++j) { vkind.push_back(TF_VARIABLE_LAYER_THICKNESS); vlayer.push_back(j); }
        at(lambda);

        tf_plan_sizes s{};
        tf_query_plan_sizes(1, variables.n_variables, operands.n_operands, operands.n_operands, &s);
        path.assign((size_t)s.point_path_count, -1);
        order.assign((size_t)s.point_order_count, -1);
        inert.assign((size_t)s.variable_inert_count, 0);
        polneed.assign((size_t)s.point_polarisation_count, 0);
        spanfirst.assign((size_t)s.point_span_first_count, 0);
        spancount.assign((size_t)s.point_span_count_count, 0);
        spanop.assign((size_t)s.point_span_operand_count, 0);
        spanslot.assign((size_t)s.point_span_slot_count, 0);
        opres.assign((size_t)s.operand_residual_count, -1);
        oporder.assign((size_t)s.operand_order_count, 0);
        oplevel.assign((size_t)s.operand_level_count, 0);
        pbuf.point_path = path.data(); pbuf.point_order = order.data();
        pbuf.variable_inert = inert.empty() ? nullptr : inert.data();
        pbuf.point_polarisation = polneed.data();
        pbuf.point_span_first = spanfirst.data(); pbuf.point_span_count = spancount.data();
        pbuf.point_span_operand = spanop.data(); pbuf.point_span_slot = spanslot.data();
        pbuf.operand_residual = opres.data(); pbuf.operand_order = oporder.data();
        pbuf.operand_level = oplevel.data();
        pbuf.report = &report; pbuf.status = &plan_status;

        const tf_status ps = tf_plan(&problem, &operands, &variables, 1, &pbuf);
        if (ps != TF_OK) return ps;

        scratch.assign((size_t)report.scratch_doubles, 0.0);
        ws.scratch = scratch.data(); ws.scratch_doubles = report.scratch_doubles;
        residual.assign((size_t)report.n_residuals, 0.0);
        value.assign(code.size(), 0.0);
        jacobian.assign((size_t)report.n_residuals * (size_t)variables.n_variables, 0.0);
        out.value = value.data(); out.residual = residual.data();
        out.jacobian = jacobian.empty() ? nullptr : jacobian.data();
        out.merit = &merit; out.status = &eval_status;
        return TF_OK;
    }

    tf_status evaluate() { wire(); return tf_evaluate(&problem, &operands, &pbuf, &ws, &out); }
    tf_status evaluate_jacobian() { wire(); return tf_evaluate_jacobian(&problem, &operands, &variables, &pbuf, &ws, &out); }

    /* Every operand's value at another wavelength, with every index moved to match. */
    std::vector<double> values_at(double l)
    {
        const double keep = lambda;
        at(l); evaluate();
        std::vector<double> v = value;
        at(keep);
        return v;
    }

    std::vector<double> values_with_thickness(int32_t j, double d)
    {
        const double keep = thickness[(size_t)j];
        thickness[(size_t)j] = d; evaluate();
        std::vector<double> v = value;
        thickness[(size_t)j] = keep; evaluate();
        return v;
    }
};

static double unwrap(double a, double b) { while (b - a > PI) b -= 2.0 * PI; while (a - b > PI) b += 2.0 * PI; return b; }

static Medium constant(double n, double k = 0.0) { Medium m; m.A = n; m.k0 = k; return m; }
static Medium cauchy(double A, double B, double k0 = 0.0, double k1 = 0.0) { Medium m; m.A = A; m.B = B; m.k0 = k0; m.k1 = k1; return m; }

/* Eight detuned layers of two dispersive materials, one weakly absorbing. */
static Case chirped(double angle, Medium incident)
{
    Case c;
    c.materials = { cauchy(2.20, 3.0e4, 0.002, 1e-6), cauchy(1.44, 3.5e3) };
    c.incident = incident;
    c.exit_ = cauchy(1.50, 4.2e3);
    c.thickness = { 95.0, 141.0, 82.0, 160.0, 101.0, 128.0, 77.0, 150.0 };
    c.layer_material = { 0, 1, 0, 1, 0, 1, 0, 1 };
    c.angle_deg = angle;
    return c;
}

static void an_air_gap_is_a_delay()
{
    std::printf("an air gap of L in front of a mirror: GD 2L/c reflected, L/c transmitted, GDD and TOD zero\n");
    Case c;
    c.materials = { constant(1.0) };
    c.incident = constant(1.0);
    c.exit_ = constant(4.0);
    c.thickness = { 3000.0 };
    c.layer_material = { 0 };
    c.code = { TF_TARGET_R_GD_S, TF_TARGET_R_GD_P, TF_TARGET_T_GD_S, TF_TARGET_R_GDD_S, TF_TARGET_T_GDD_P,
               TF_TARGET_R_TOD_S, TF_TARGET_T_TOD_P };
    expect(c.setup() == TF_OK && c.evaluate() == TF_OK, "plans and evaluates");
    near_(c.value[0], 2.0 * 3000.0 / C_NM_FS, 1e-9, "R_GD_S = 2L/c (fs)");
    near_(c.value[1], 2.0 * 3000.0 / C_NM_FS, 1e-9, "R_GD_P = 2L/c (fs)");
    near_(c.value[2], 3000.0 / C_NM_FS, 1e-9, "T_GD_S = L/c (fs)");
    near_(c.value[3], 0.0, 1e-9, "R_GDD_S = 0");
    near_(c.value[4], 0.0, 1e-9, "T_GDD_P = 0");
    near_(c.value[5], 0.0, 1e-9, "R_TOD_S = 0");
    near_(c.value[6], 0.0, 1e-9, "T_TOD_P = 0");
}

static void gd_is_the_phase_slope(double angle, Medium incident, const char* label)
{
    std::printf("GD against the phase's slope, GDD against GD's, TOD against GDD's: %s\n", label);
    Case c = chirped(angle, incident);
    c.code = { TF_TARGET_R_GD_S, TF_TARGET_R_GD_P, TF_TARGET_T_GD_S, TF_TARGET_T_GD_P,
               TF_TARGET_R_GDD_S, TF_TARGET_R_GDD_P, TF_TARGET_T_GDD_S, TF_TARGET_T_GDD_P,
               TF_TARGET_R_PHASE_S, TF_TARGET_R_PHASE_P, TF_TARGET_T_PHASE_S, TF_TARGET_T_PHASE_P,
               TF_TARGET_R_TOD_S, TF_TARGET_R_TOD_P, TF_TARGET_T_TOD_S, TF_TARGET_T_TOD_P };
    expect(c.setup() == TF_OK && c.evaluate() == TF_OK, "plans and evaluates");
    const std::vector<double> v = c.value;

    const double h = 1e-3, l = c.lambda;
    const std::vector<double> up = c.values_at(l + h), dn = c.values_at(l - h);
    const double L1 = -(l * l) / (2.0 * PI * C_NM_FS);   /* d(lambda)/d(omega) */

    const char* gd_names[] = { "R_GD_S", "R_GD_P", "T_GD_S", "T_GD_P" };
    const char* gdd_names[] = { "R_GDD_S", "R_GDD_P", "T_GDD_S", "T_GDD_P" };
    const char* tod_names[] = { "R_TOD_S", "R_TOD_P", "T_TOD_S", "T_TOD_P" };
    for (int q = 0; q < 4; ++q)
    {
        const double pu = unwrap(v[8 + q], up[8 + q]), pd = unwrap(v[8 + q], dn[8 + q]);
        const double gd_fd = -(pu - pd) / (2.0 * h) * L1;
        near_(v[q], gd_fd, 1e-6 * (1.0 + std::fabs(gd_fd)), gd_names[q]);

        const double gdd_fd = (up[q] - dn[q]) / (2.0 * h) * L1;   /* d(GD)/d(omega) */
        near_(v[4 + q], gdd_fd, 1e-5 * (1.0 + std::fabs(gdd_fd)), gdd_names[q]);

        const double tod_fd = (up[4 + q] - dn[4 + q]) / (2.0 * h) * L1;   /* d(GDD)/d(omega) */
        near_(v[12 + q], tod_fd, 1e-5 * (1.0 + std::fabs(tod_fd)), tod_names[q]);
    }
    std::printf("    R_GD_S %.6g fs, R_GDD_S %.6g fs^2, R_TOD_S %.6g fs^3\n", v[0], v[4], v[12]);
}

static void the_jacobian_is_the_thickness_slope()
{
    std::printf("d(GD)/d(d_j), d(GDD)/d(d_j) and d(TOD)/d(d_j) against central differences in thickness\n");
    Case c = chirped(30.0, cauchy(1.0, 0.0));
    c.code = { TF_TARGET_R_GD_S, TF_TARGET_R_GDD_P, TF_TARGET_T_GD_P, TF_TARGET_T_GDD_S, TF_TARGET_RS,
               TF_TARGET_R_TOD_P, TF_TARGET_T_TOD_S };
    c.with_variables = true;
    expect(c.setup() == TF_OK, "plans");
    expect(c.evaluate_jacobian() == TF_OK, "the Jacobian evaluates");
    const std::vector<double> J = c.jacobian;
    const int nres = c.report.n_residuals, nv = c.variables.n_variables;

    double worst = 0.0;
    for (int j = 0; j < nv; ++j)
    {
        const double d = c.thickness[(size_t)j], h = 1e-3;
        const std::vector<double> up = c.values_with_thickness(j, d + h), dn = c.values_with_thickness(j, d - h);
        for (int r = 0; r < nres; ++r)
        {
            const double fd = (up[(size_t)r] - dn[(size_t)r]) / (2.0 * h);
            const double an = J[(size_t)j * (size_t)nres + (size_t)r];
            worst = std::fmax(worst, std::fabs(an - fd) / (1.0 + std::fabs(fd)));
        }
    }
    near_(worst, 0.0, 1e-6, "worst relative disagreement over every layer and operand");
}

static void s_and_p_agree_at_normal_incidence()
{
    std::printf("at normal incidence s and p are the same wave\n");
    Case c = chirped(0.0, cauchy(1.0, 0.0));
    c.code = { TF_TARGET_R_GD_S, TF_TARGET_R_GD_P, TF_TARGET_R_GDD_S, TF_TARGET_R_GDD_P,
               TF_TARGET_R_TOD_S, TF_TARGET_R_TOD_P };
    expect(c.setup() == TF_OK && c.evaluate() == TF_OK, "plans and evaluates");
    near_(c.value[0], c.value[1], 1e-9 * (1.0 + std::fabs(c.value[0])), "R_GD_S = R_GD_P");
    near_(c.value[2], c.value[3], 1e-9 * (1.0 + std::fabs(c.value[2])), "R_GDD_S = R_GDD_P");
    near_(c.value[4], c.value[5], 1e-9 * (1.0 + std::fabs(c.value[4])), "R_TOD_S = R_TOD_P");
}

static void missing_dispersion_is_refused()
{
    std::printf("missing dispersion data is refused, never read as zero\n");
    {
        Case c = chirped(0.0, cauchy(1.0, 0.0));
        c.code = { TF_TARGET_R_GD_S };
        c.with_first = false; c.with_second = false;
        expect(c.setup() == TF_OK, "a GD table plans without dn/dlambda");
        expect(c.evaluate() == TF_NO_DISPERSION_DATA, "and evaluating it is TF_NO_DISPERSION_DATA");
    }
    {
        Case c = chirped(0.0, cauchy(1.0, 0.0));
        c.code = { TF_TARGET_R_GDD_S };
        c.with_second = false;
        expect(c.setup() == TF_OK && c.evaluate() == TF_NO_DISPERSION_DATA,
               "GDD without d2n/dlambda2 is TF_NO_DISPERSION_DATA");
        c.with_variables = true;
        expect(c.setup() == TF_OK && c.evaluate_jacobian() == TF_NO_DISPERSION_DATA, "and so is its Jacobian");
    }
    {
        Case c = chirped(0.0, cauchy(1.0, 0.0));
        c.code = { TF_TARGET_R_GD_S, TF_TARGET_RS };
        c.with_second = false; c.with_third = false;
        expect(c.setup() == TF_OK && c.evaluate() == TF_OK, "GD needs no second derivative");
    }
    {
        Case c = chirped(0.0, cauchy(1.0, 0.0));
        c.code = { TF_TARGET_T_TOD_S };
        c.with_third = false;
        expect(c.setup() == TF_OK && c.evaluate() == TF_NO_DISPERSION_DATA,
               "TOD without d3n/dlambda3 is TF_NO_DISPERSION_DATA");
        c.with_variables = true;
        expect(c.setup() == TF_OK && c.evaluate_jacobian() == TF_NO_DISPERSION_DATA, "and so is its Jacobian");
    }
    {
        Case c = chirped(30.0, cauchy(1.0, 0.0));
        c.code = { TF_TARGET_R_GD_S, TF_TARGET_R_GDD_P };
        expect(c.setup() == TF_OK && c.evaluate() == TF_OK, "with every derivative");
        const std::vector<double> full = c.value;
        c.with_third = false;
        expect(c.evaluate() == TF_OK, "GDD needs no third derivative");
        near_(c.value[0], full[0], 0.0, "and GD is the same without it, to the bit");
        near_(c.value[1], full[1], 0.0, "and so is GDD");
    }
}

/* THE NEEDLE MAP with GD, GDD and TOD rows: d(sum of squared residuals)/d(eps) for a needle of eps
 * inserted at a depth, against inserting one. eps cannot go negative, so the difference is
 * one-sided and Richardson-extrapolated: (4 f(eps) - f(2 eps) - 3 f(0)) / (2 eps). */
static double sumsq(Case& c, const std::vector<double>& targets)
{
    c.target = targets;
    c.evaluate();
    double s = 0.0;
    for (double r : c.residual) s += r * r;
    return s;
}

static double sumsq_with_needle(const Case& base, const std::vector<double>& targets, double z, int32_t material, double eps)
{
    Case c = base;
    std::vector<double> t; std::vector<int32_t> m;
    double front = 0.0; bool done = false;
    for (size_t j = 0; j < base.thickness.size(); ++j)
    {
        const double d = base.thickness[j];
        if (!done && z >= front && z < front + d)
        {
            t.push_back(z - front); m.push_back(base.layer_material[j]);
            t.push_back(eps);       m.push_back(material);
            t.push_back(front + d - z); m.push_back(base.layer_material[j]);
            done = true;
        }
        else { t.push_back(d); m.push_back(base.layer_material[j]); }
        front += d;
    }
    c.thickness = t; c.layer_material = m;
    c.setup();
    return sumsq(c, targets);
}

static void the_needle_map_counts_them()
{
    std::printf("the needle map with GD, GDD and TOD rows, against inserting a needle\n");
    Case c = chirped(20.0, cauchy(1.0, 0.0));
    c.code = { TF_TARGET_R_GD_S, TF_TARGET_R_GDD_S, TF_TARGET_T_GDD_P, TF_TARGET_RS, TF_TARGET_R_TOD_S };
    const std::vector<double> targets = { 3.0, -40.0, 5.0, 1.0, 20.0 };
    expect(c.setup() == TF_OK, "plans");
    const double base = sumsq(c, targets);

    const double depth[] = { 37.0, 300.0, 512.5, 930.0 };
    const int32_t cand[] = { 0, 1 };
    const int nd = 4, nc = 2;
    tf_needle_request req{ nd, depth, nc, cand };
    std::vector<double> map((size_t)(nd * nc), 0.0), res(c.residual.size(), 0.0);
    int32_t st = 0;
    tf_needle_output nout{ res.data(), map.data(), &st };
    c.target = targets; c.wire();
    expect(tf_evaluate_needle(&c.problem, &c.operands, &c.pbuf, &req, &c.ws, &nout) == TF_OK, "the needle map evaluates");

    double worst = 0.0;
    for (int ci = 0; ci < nc; ++ci)
        for (int di = 0; di < nd; ++di)
        {
            const double e = 1e-3;
            const double f1 = sumsq_with_needle(c, targets, depth[di], cand[ci], e);
            const double f2 = sumsq_with_needle(c, targets, depth[di], cand[ci], 2.0 * e);
            const double fd = (4.0 * f1 - f2 - 3.0 * base) / (2.0 * e);
            worst = std::fmax(worst, std::fabs(map[(size_t)(ci * nd + di)] - fd) / (1.0 + std::fabs(fd)));
        }
    near_(worst, 0.0, 1e-5, "worst relative disagreement over every depth and candidate");
}

int main()

{
    an_air_gap_is_a_delay();
    gd_is_the_phase_slope(0.0, cauchy(1.0, 0.0), "normal incidence from air");
    gd_is_the_phase_slope(45.0, cauchy(1.0, 0.0), "45 degrees from air");
    gd_is_the_phase_slope(35.0, cauchy(1.52, 4.5e3), "35 degrees from dispersive glass (u' and u'' live)");
    the_jacobian_is_the_thickness_slope();
    s_and_p_agree_at_normal_incidence();
    the_needle_map_counts_them();
    missing_dispersion_is_refused();

    std::printf("=== %d checks, %d failures ===\n", g_checks, g_failures);
    return g_failures == 0 ? 0 : 1;
}
