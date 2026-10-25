/* test_fixtures.cpp — the engine against tests/fixtures/evaluation.fixtures.
 *
 * THE SAME FILE THE MANAGED SUITE READS. X1.3 exists to stop a second implementation growing in
 * C#, and the only thing that catches that is the same problem through both paths compared number
 * by number. So the fixtures are a file owned by neither side: this reads it and checks the
 * ENGINE; ThinFilm.Native.Tests reads it and checks DESIGN.
 *
 * That split is also a split of blame. If the engine changes, this fails and names the fixture. If
 * only the managed suite fails, the fault is in the binding and cannot be anywhere else. Two
 * suites with their own fixtures could never tell those apart.
 *
 * Run with --regenerate to rewrite the expectations from the current engine. That is a deliberate
 * act, not a fix: the numbers here are only as good as the 1750 checks that prove the engine
 * right, and regenerating on a red test would replace evidence with wishful thinking.
 */

#include "thinfilm.h"

#include <cmath>
#include <cstdio>
#include <cstring>
#include <cstdlib>
#include <string>
#include <vector>
#include <map>
#include <fstream>
#include <sstream>
#include <iomanip>

extern "C" {
tf_status tf_query_plan_sizes(int32_t, int32_t, int32_t, int32_t, tf_plan_sizes*);
tf_status tf_plan(const tf_problem*, const tf_operands*, const tf_variables*, int32_t,
                  tf_plan_buffers*);
tf_status tf_evaluate(const tf_problem*, const tf_operands*, const tf_plan_buffers*,
                      tf_workspace*, tf_output*);
tf_status tf_evaluate_jacobian(const tf_problem*, const tf_operands*, const tf_variables*,
                               const tf_plan_buffers*, tf_workspace*, tf_output*);
}

static int g_failures = 0;
static int g_checks   = 0;

/* TO 1e-12, NOT BIT FOR BIT. The fixtures were recorded with MSVC; GCC on Linux (2026-10-24) gave
 * six Jacobian entries differing in the 14th to 16th figure - the same arithmetic, ordered or
 * fused differently, which no compiler promises to repeat. A change in the engine or a binding
 * that computes something else moves numbers by far more than this, so it still names the
 * fixture; it no longer fails a correct build for being built elsewhere. */
static bool same(double got, double want)
{
    if (got == want) return true;
    double scale = std::fabs(got) > std::fabs(want) ? std::fabs(got) : std::fabs(want);
    return std::fabs(got - want) <= 1e-12 * scale;
}

static void exactly(double got, double want, const char* fixture, const char* what, int i)
{
    ++g_checks;
    if (!same(got, want))
    {
        ++g_failures;
        std::printf("  FAIL  %-22s %s[%d] is %.17g, recorded %.17g\n", fixture, what, i, got, want);
    }
}

/* ---------------------------------------------------------------------------------------------
 * The file. Line-oriented, whitespace-separated, '#' to end of line is a comment.
 * ------------------------------------------------------------------------------------------- */

struct Fixture
{
    std::string name;
    std::map<std::string, std::vector<double>>  d;
    std::map<std::string, std::vector<int32_t>> i;
    std::vector<std::string> order;   /* keys as they appeared, so a rewrite keeps the shape */
    std::vector<std::string> kind;    /* "d", "i" or "c", parallel to order */

    const std::vector<double>* dv(const char* k) const
    { auto it = d.find(k); return it == d.end() ? nullptr : &it->second; }
    const std::vector<int32_t>* iv(const char* k) const
    { auto it = i.find(k); return it == i.end() ? nullptr : &it->second; }
    int32_t scalar(const char* k, int32_t fallback) const
    { auto v = iv(k); return (v && !v->empty()) ? (*v)[0] : fallback; }
};

/* std::ifstream and std::istringstream rather than fopen and strtok. Not taste: this build makes
 * warnings errors, MSVC deprecates both of those, and turning the warning off for one file would
 * spend a standing guarantee on a convenience. */
static std::vector<Fixture> read_fixtures(const char* path, std::string* preamble)
{
    std::vector<Fixture> out;
    std::ifstream in(path);
    if (!in)
    {
        std::printf("  FAIL  cannot open %s\n", path);
        ++g_failures;
        return out;
    }

    std::string raw;
    bool started = false;
    while (std::getline(in, raw))
    {
        while (!raw.empty() && (raw.back() == '\n' || raw.back() == '\r')) raw.pop_back();

        std::string text = raw;
        const size_t hash = text.find('#');
        if (hash != std::string::npos) text = text.substr(0, hash);

        std::istringstream ls(text);
        std::string tok;
        if (!(ls >> tok))
        {
            if (!started && preamble) { *preamble += raw; *preamble += "\n"; }
            continue;
        }

        if (tok == "fixture")
        {
            started = true;
            Fixture fx;
            if (!(ls >> fx.name)) fx.name = "(unnamed)";
            out.push_back(fx);
            continue;
        }
        if (tok == "end") continue;
        if (out.empty())
        {
            if (preamble) { *preamble += raw; *preamble += "\n"; }
            continue;
        }

        std::string key;
        if (!(ls >> key)) continue;
        Fixture& fx = out.back();
        fx.order.push_back(key);
        fx.kind.push_back(tok);

        if (tok == "i")
        {
            std::vector<int32_t> v;
            long long n = 0;
            while (ls >> n) v.push_back((int32_t)n);
            fx.i[key] = v;
        }
        else
        {
            std::vector<double> v;
            double x = 0.0;
            while (ls >> x) v.push_back(x);
            fx.d[key] = v;
        }
    }
    return out;
}

/* ---------------------------------------------------------------------------------------------
 * Running one fixture. Everything the ABI needs is built from the file and nothing is assumed.
 * ------------------------------------------------------------------------------------------- */

struct Run
{
    std::vector<double>  value, residual, jacobian;
    std::vector<int32_t> flag;
    double merit = 0.0;
    int32_t n_residuals = 0, n_variables = 0;
    tf_status status = TF_OK;
};

static const tf_complex* cptr(const Fixture& fx, const char* k, std::vector<tf_complex>& store)
{
    auto v = fx.dv(k);
    if (!v || v->empty()) return nullptr;
    store.clear();
    for (size_t n = 0; n + 1 < v->size() + 1; n += 2)
    {
        if (n + 1 >= v->size()) break;
        store.push_back(tf_complex{ (*v)[n], (*v)[n + 1] });
    }
    return store.data();
}

static Run run_fixture(const Fixture& fx)
{
    Run r;

    std::vector<tf_complex> mi, ii, ie, cii, cie, tmi;
    tf_problem p{};
    tf_operands ops{};
    tf_variables vars{};
    tf_configurations cfg{};

    const auto* thickness = fx.dv("layer_thickness");
    const auto* material  = fx.iv("layer_material");
    const auto* wavelen   = fx.dv("wavelength");
    const auto* ptwl      = fx.iv("point_wavelength");
    const auto* ptang     = fx.dv("point_angle");

    p.n_layers      = thickness ? (int32_t)thickness->size() : 0;
    p.n_materials   = fx.scalar("n_materials", 1);
    p.n_wavelengths = wavelen ? (int32_t)wavelen->size() : 0;
    p.n_points      = ptang ? (int32_t)ptang->size() : 0;
    p.layer_thickness = thickness && !thickness->empty() ? thickness->data() : nullptr;
    p.layer_material  = material && !material->empty() ? material->data() : nullptr;
    p.material_index  = cptr(fx, "material_index", mi);
    p.wavelength      = wavelen->data();
    p.point_wavelength = ptwl->data();
    p.point_angle      = ptang->data();
    const auto* stress = fx.dv("material_stress");
    p.material_stress = stress && !stress->empty() ? stress->data() : nullptr;
    p.n_trials = fx.scalar("n_trials", 0);
    const auto* tlt = fx.dv("trial_layer_thickness");
    p.trial_layer_thickness = tlt && !tlt->empty() ? tlt->data() : nullptr;
    p.trial_material_index = cptr(fx, "trial_material_index", tmi);

    const auto* pcfg = fx.iv("point_configuration");
    if (pcfg && !pcfg->empty())
    {
        cfg.n_configurations = fx.scalar("n_configurations", 1);
        cfg.index_incident = cptr(fx, "cfg_index_incident", cii);
        cfg.index_exit     = cptr(fx, "cfg_index_exit", cie);
        const auto* rev = fx.iv("cfg_reversed");
        cfg.reversed = rev && !rev->empty() ? rev->data() : nullptr;
        p.configurations = &cfg;
        p.point_configuration = pcfg->data();
    }
    else
    {
        p.index_incident = cptr(fx, "index_incident", ii);
        p.index_exit     = cptr(fx, "index_exit", ie);
    }

    const auto* code = fx.iv("code");
    ops.n_operands = code ? (int32_t)code->size() : 0;
    ops.code   = code->data();
    ops.target = fx.dv("target")->data();
    ops.weight = fx.dv("weight")->data();
    ops.delta_convention = fx.scalar("delta_convention", TF_DELTA_PRINCIPAL);
    ops.extrapolation_policy = fx.scalar("extrapolation_policy", TF_EXTRAPOLATION_FLAG);

    const auto* point = fx.iv("point");
    ops.point = point && !point->empty() ? point->data() : nullptr;
    const auto* pf = fx.iv("point_first");
    const auto* pc = fx.iv("point_count");
    const auto* pi = fx.iv("point_index");
    ops.point_first = pf && !pf->empty() ? pf->data() : nullptr;
    ops.point_count = pc && !pc->empty() ? pc->data() : nullptr;
    ops.point_index = pi && !pi->empty() ? pi->data() : nullptr;
    ops.n_span      = pi ? (int32_t)pi->size() : 0;
    const auto* pw = fx.dv("point_weight");
    ops.point_weight = pw && !pw->empty() ? pw->data() : nullptr;
    const auto* cmp = fx.iv("comparison");
    ops.comparison = cmp && !cmp->empty() ? cmp->data() : nullptr;
    const auto* th = fx.dv("target_hi");
    ops.target_hi = th && !th->empty() ? th->data() : nullptr;
    const auto* tol = fx.dv("tolerance");
    ops.tolerance = tol && !tol->empty() ? tol->data() : nullptr;
    const auto* sc = fx.dv("scale");
    ops.scale = sc && !sc->empty() ? sc->data() : nullptr;
    const auto* red = fx.iv("reduction");
    ops.reduction = red && !red->empty() ? red->data() : nullptr;
    const auto* rp = fx.dv("reduction_param");
    ops.reduction_param = rp && !rp->empty() ? rp->data() : nullptr;
    const auto* inf = fx.iv("input_first");
    const auto* inc = fx.iv("input_count");
    const auto* ini = fx.iv("input_index");
    ops.input_first = inf && !inf->empty() ? inf->data() : nullptr;
    ops.input_count = inc && !inc->empty() ? inc->data() : nullptr;
    ops.input_index = ini && !ini->empty() ? ini->data() : nullptr;
    ops.n_input     = ini ? (int32_t)ini->size() : 0;
    const auto* tr = fx.iv("trial_reduction");
    ops.trial_reduction = tr && !tr->empty() ? tr->data() : nullptr;

    const auto* vk = fx.iv("variable_kind");
    const auto* vl = fx.iv("variable_layer");
    static std::vector<int32_t> zeros;
    vars.n_variables = vk ? (int32_t)vk->size() : 0;
    if (vars.n_variables > 0)
    {
        zeros.assign((size_t)vars.n_variables, 0);
        vars.kind = vk->data();
        vars.layer = vl->data();
        vars.material = zeros.data();
        vars.coefficient = zeros.data();
    }
    r.n_variables = vars.n_variables;

    tf_plan_sizes s{};
    tf_query_plan_sizes(p.n_points, vars.n_variables, ops.n_span, ops.n_operands, &s);

    std::vector<int32_t> path((size_t)s.point_path_count), order((size_t)s.point_order_count);
    std::vector<int32_t> inert((size_t)s.variable_inert_count);
    std::vector<int32_t> pol((size_t)s.point_polarisation_count);
    std::vector<int32_t> sf((size_t)s.point_span_first_count), scn((size_t)s.point_span_count_count);
    std::vector<int32_t> so((size_t)s.point_span_operand_count), ss((size_t)s.point_span_slot_count);
    std::vector<int32_t> ores((size_t)s.operand_residual_count);
    std::vector<int32_t> oord((size_t)s.operand_order_count), olev((size_t)s.operand_level_count);

    tf_plan_report rep{}; int32_t st = 0;
    tf_plan_buffers b{};
    b.point_path = path.data(); b.point_order = order.data();
    b.variable_inert = inert.empty() ? nullptr : inert.data();
    b.point_polarisation = pol.data();
    b.point_span_first = sf.data(); b.point_span_count = scn.data();
    b.point_span_operand = so.data(); b.point_span_slot = ss.data();
    b.operand_residual = ores.data(); b.operand_order = oord.data();
    b.operand_level = olev.data();
    b.report = &rep; b.status = &st;

    r.status = tf_plan(&p, &ops, &vars, 1, &b);
    if (r.status != TF_OK) return r;

    r.n_residuals = rep.n_residuals;
    std::vector<double> scratch((size_t)rep.scratch_doubles);
    r.value.assign((size_t)ops.n_operands, 0.0);
    r.flag.assign((size_t)ops.n_operands, 0);
    r.residual.assign((size_t)rep.n_residuals, 0.0);
    r.jacobian.assign((size_t)rep.n_residuals * (size_t)vars.n_variables, 0.0);
    int32_t nflag = 0, estat = 0;

    tf_workspace ws{}; ws.scratch = scratch.data(); ws.scratch_doubles = rep.scratch_doubles;
    tf_output out{};
    out.residual = r.residual.data();
    out.value = r.value.data();
    out.operand_flag = r.flag.data();
    out.n_operands_flagged = &nflag;
    out.merit = &r.merit;
    out.status = &estat;
    out.jacobian = nullptr;

    r.status = tf_evaluate(&p, &ops, &b, &ws, &out);
    if (r.status != TF_OK) return r;

    if (vars.n_variables > 0)
    {
        out.jacobian = r.jacobian.data();
        r.status = tf_evaluate_jacobian(&p, &ops, &vars, &b, &ws, &out);
    }
    return r;
}

/* ------------------------------------------------------------------------------------------- */

static void write_fixtures(const char* path, const std::string& preamble,
                           const std::vector<Fixture>& fx)
{
    std::ostringstream o;
    o << preamble;

    for (const auto& x : fx)
    {
        o << "fixture " << x.name << "\n";
        for (size_t k = 0; k < x.order.size(); ++k)
        {
            const std::string& key = x.order[k];
            if (key.rfind("expect_", 0) == 0) continue;   /* rewritten below */
            o << x.kind[k] << " " << key;
            if (x.kind[k] == "i")
                for (int32_t v : x.i.at(key)) o << " " << (int)v;
            else
                for (double v : x.d.at(key))
                    o << " " << std::setprecision(17) << std::defaultfloat << v;
            o << "\n";
        }

        const Run r = run_fixture(x);
        if (r.status != TF_OK)
        {
            o << "i expect_status " << (int)r.status << "\n";
        }
        else
        {
            o << std::setprecision(17) << std::defaultfloat;
            o << "d expect_value";
            for (double v : r.value) o << " " << v;
            o << "\nd expect_residual";
            for (double v : r.residual) o << " " << v;
            o << "\nd expect_merit " << r.merit << "\n";
            if (!r.jacobian.empty())
            {
                o << "d expect_jacobian";
                for (double v : r.jacobian) o << " " << v;
                o << "\n";
            }
        }
        o << "end\n\n";
    }

    std::ofstream out(path, std::ios::binary);
    if (!out) { std::printf("cannot write %s\n", path); return; }
    out << o.str();
    std::printf("regenerated %s with %d fixtures\n", path, (int)fx.size());
}

int main(int argc, char** argv)
{
    setvbuf(stdout, NULL, _IONBF, 0);

    const char* path = "tests/fixtures/evaluation.fixtures";
    bool regenerate = false;
    for (int a = 1; a < argc; ++a)
    {
        if (std::strcmp(argv[a], "--regenerate") == 0) regenerate = true;
        else path = argv[a];
    }

    std::string preamble;
    std::vector<Fixture> fx = read_fixtures(path, &preamble);

    if (regenerate)
    {
        write_fixtures(path, preamble, fx);
        return 0;
    }

    std::printf("\n=== shared fixtures: the engine ===\n\n");
    std::printf("%d fixtures from %s\n\n", (int)fx.size(), path);

    for (const auto& x : fx)
    {
        const Run r = run_fixture(x);

        const auto* wantStatus = x.iv("expect_status");
        if (wantStatus && !wantStatus->empty())
        {
            ++g_checks;
            if ((int32_t)r.status != (*wantStatus)[0])
            {
                ++g_failures;
                std::printf("  FAIL  %-22s status is %d, recorded %d\n",
                            x.name.c_str(), (int)r.status, (int)(*wantStatus)[0]);
            }
            continue;
        }

        ++g_checks;
        if (r.status != TF_OK)
        {
            ++g_failures;
            std::printf("  FAIL  %-22s did not evaluate: status %d\n", x.name.c_str(), (int)r.status);
            continue;
        }

        const auto* wv = x.dv("expect_value");
        const auto* wr = x.dv("expect_residual");
        const auto* wm = x.dv("expect_merit");
        const auto* wj = x.dv("expect_jacobian");

        if (!wv || !wr || !wm)
        {
            ++g_failures;
            std::printf("  FAIL  %-22s has no recorded expectation - run --regenerate once, "
                        "deliberately\n", x.name.c_str());
            continue;
        }

        ++g_checks;
        if (wv->size() != r.value.size())
        {
            ++g_failures;
            std::printf("  FAIL  %-22s produced %d values, %d recorded\n",
                        x.name.c_str(), (int)r.value.size(), (int)wv->size());
            continue;
        }
        for (size_t k = 0; k < r.value.size(); ++k)
            exactly(r.value[k], (*wv)[k], x.name.c_str(), "value", (int)k);
        for (size_t k = 0; k < r.residual.size() && k < wr->size(); ++k)
            exactly(r.residual[k], (*wr)[k], x.name.c_str(), "residual", (int)k);
        exactly(r.merit, (*wm)[0], x.name.c_str(), "merit", 0);
        if (wj)
            for (size_t k = 0; k < r.jacobian.size() && k < wj->size(); ++k)
                exactly(r.jacobian[k], (*wj)[k], x.name.c_str(), "jacobian", (int)k);
    }

    std::printf("\n=== %d checks, %d failures ===\n\n", g_checks, g_failures);
    return g_failures != 0 ? 1 : 0;
}
