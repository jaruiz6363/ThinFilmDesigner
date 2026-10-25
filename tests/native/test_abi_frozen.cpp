/* test_abi_frozen.cpp — the ABI is frozen, and this is what that means mechanically.
 *
 * docs/scope.md says ABI_FROZEN: yes. A flag in a document is a promise; this file is the part
 * that cannot be forgotten. Every struct size, every field offset and every enumerator value is
 * pinned to the number it had at the freeze, because those are precisely the numbers a C#
 * P/Invoke layout is computed from. Insert a field in the middle of tf_problem and nothing in C++
 * breaks - the C++ is recompiled against the new header - while every managed caller silently
 * reads the wrong bytes. That is the failure this file exists to make impossible to ship.
 *
 * IF A CHECK HERE FAILS, THE FIX IS ALMOST NEVER TO EDIT THIS FILE. It is one of:
 *
 *   - the change was not meant to alter the ABI, and the header edit should be reverted or moved
 *     (a new field at the END of a struct is the additive change that costs nothing; a new field
 *     in the MIDDLE is an ABI break wearing the same clothes);
 *   - the change was meant, in which case it is a deliberate ABI bump: raise TF_ABI_VERSION, add
 *     a change-log row to docs/scope.md, and update the pins in the same commit, so that the
 *     three of them can never disagree.
 *
 * The failure messages print the value they found, so the new pins can be read straight off the
 * output. That is on purpose: the tedious part of a deliberate bump should be easy, and the
 * accidental one should be loud.
 *
 * WHAT THIS DOES NOT PIN, and why. Padding is a property of the platform's C ABI rather than of
 * this header, so on a machine with different alignment rules these offsets would differ and
 * SHOULD differ. Every supported target is LP64/LLP64 with 8-byte doubles and pointers, which is
 * also every target CUDA runs on, so the numbers below are the numbers everywhere this engine is
 * meant to run. A port to a platform where they differ is a port that needs its own pins, not a
 * bug in these.
 */

#include "thinfilm.h"

#include <cstdio>
#include <cstddef>

static int g_failures = 0;
static int g_checks   = 0;

static void pin(long long got, long long want, const char* what)
{
    ++g_checks;
    if (got != want)
    {
        ++g_failures;
        std::printf("  FAIL  %-44s is %lld, was %lld at the freeze\n", what, got, want);
    }
}

static void pin_size(long long got, long long want, const char* what)
{
    ++g_checks;
    if (got != want)
    {
        ++g_failures;
        std::printf("  FAIL  sizeof(%s) is %lld, was %lld at the freeze\n", what, got, want);
    }
}

/* The layout, as measured on the commit that set ABI_FROZEN: yes. */
static void TheLayoutIsTheOneCSharpWasPromised()
{
    std::printf("every struct size and field offset is the one the ABI froze at\n");

    pin_size(sizeof(tf_complex), 16, "tf_complex");
    pin(offsetof(tf_complex, re), 0, "tf_complex.re");
    pin(offsetof(tf_complex, im), 8, "tf_complex.im");

    pin_size(sizeof(tf_matrix2), 64, "tf_matrix2");
    pin(offsetof(tf_matrix2, m00), 0, "tf_matrix2.m00");
    pin(offsetof(tf_matrix2, m01), 16, "tf_matrix2.m01");
    pin(offsetof(tf_matrix2, m10), 32, "tf_matrix2.m10");
    pin(offsetof(tf_matrix2, m11), 48, "tf_matrix2.m11");

    pin_size(sizeof(tf_matrix2s), 32, "tf_matrix2s");
    pin(offsetof(tf_matrix2s, a), 0, "tf_matrix2s.a");
    pin(offsetof(tf_matrix2s, b), 8, "tf_matrix2s.b");
    pin(offsetof(tf_matrix2s, c), 16, "tf_matrix2s.c");
    pin(offsetof(tf_matrix2s, d), 24, "tf_matrix2s.d");

    pin_size(sizeof(tf_configurations), 64, "tf_configurations");
    pin(offsetof(tf_configurations, n_configurations), 0, "tf_configurations.n_configurations");
    pin(offsetof(tf_configurations, index_incident), 8, "tf_configurations.index_incident");
    pin(offsetof(tf_configurations, index_exit), 16, "tf_configurations.index_exit");
    pin(offsetof(tf_configurations, dindex_incident_dwavelength), 24, "tf_configurations.dindex_incident_dwavelength");
    pin(offsetof(tf_configurations, dindex_exit_dwavelength), 32, "tf_configurations.dindex_exit_dwavelength");
    pin(offsetof(tf_configurations, index_incident_is_extrapolated), 40, "tf_configurations.index_incident_is_extrapolated");
    pin(offsetof(tf_configurations, index_exit_is_extrapolated), 48, "tf_configurations.index_exit_is_extrapolated");
    pin(offsetof(tf_configurations, reversed), 56, "tf_configurations.reversed");

    pin_size(sizeof(tf_problem), 248, "tf_problem");
    pin(offsetof(tf_problem, n_layers), 0, "tf_problem.n_layers");
    pin(offsetof(tf_problem, n_materials), 4, "tf_problem.n_materials");
    pin(offsetof(tf_problem, n_wavelengths), 8, "tf_problem.n_wavelengths");
    pin(offsetof(tf_problem, n_points), 12, "tf_problem.n_points");
    pin(offsetof(tf_problem, layer_thickness), 16, "tf_problem.layer_thickness");
    pin(offsetof(tf_problem, layer_material), 24, "tf_problem.layer_material");
    pin(offsetof(tf_problem, layer_is_incoherent), 32, "tf_problem.layer_is_incoherent");
    pin(offsetof(tf_problem, material_index), 40, "tf_problem.material_index");
    pin(offsetof(tf_problem, wavelength), 48, "tf_problem.wavelength");
    pin(offsetof(tf_problem, point_wavelength), 56, "tf_problem.point_wavelength");
    pin(offsetof(tf_problem, point_angle), 64, "tf_problem.point_angle");
    pin(offsetof(tf_problem, index_incident), 72, "tf_problem.index_incident");
    pin(offsetof(tf_problem, index_exit), 80, "tf_problem.index_exit");
    pin(offsetof(tf_problem, configurations), 88, "tf_problem.configurations");
    pin(offsetof(tf_problem, point_configuration), 96, "tf_problem.point_configuration");
    pin(offsetof(tf_problem, material_dindex_dwavelength), 104, "tf_problem.material_dindex_dwavelength");
    pin(offsetof(tf_problem, dindex_incident_dwavelength), 112, "tf_problem.dindex_incident_dwavelength");
    pin(offsetof(tf_problem, dindex_exit_dwavelength), 120, "tf_problem.dindex_exit_dwavelength");
    pin(offsetof(tf_problem, material_stress), 128, "tf_problem.material_stress");
    pin(offsetof(tf_problem, material_index_is_extrapolated), 136, "tf_problem.material_index_is_extrapolated");
    pin(offsetof(tf_problem, index_incident_is_extrapolated), 144, "tf_problem.index_incident_is_extrapolated");
    pin(offsetof(tf_problem, index_exit_is_extrapolated), 152, "tf_problem.index_exit_is_extrapolated");
    pin(offsetof(tf_problem, n_trials), 160, "tf_problem.n_trials");
    pin(offsetof(tf_problem, trial_layer_thickness), 168, "tf_problem.trial_layer_thickness");
    pin(offsetof(tf_problem, trial_material_index), 176, "tf_problem.trial_material_index");
    pin(offsetof(tf_problem, substrate_thickness), 184, "tf_problem.substrate_thickness");
    pin(offsetof(tf_problem, index_back), 192, "tf_problem.index_back");
    pin(offsetof(tf_problem, material_d2index_dwavelength2), 200, "tf_problem.material_d2index_dwavelength2");
    pin(offsetof(tf_problem, d2index_incident_dwavelength2), 208, "tf_problem.d2index_incident_dwavelength2");
    pin(offsetof(tf_problem, d2index_exit_dwavelength2), 216, "tf_problem.d2index_exit_dwavelength2");
    pin(offsetof(tf_problem, material_d3index_dwavelength3), 224, "tf_problem.material_d3index_dwavelength3");
    pin(offsetof(tf_problem, d3index_incident_dwavelength3), 232, "tf_problem.d3index_incident_dwavelength3");
    pin(offsetof(tf_problem, d3index_exit_dwavelength3), 240, "tf_problem.d3index_exit_dwavelength3");

    pin_size(sizeof(tf_operands), 176, "tf_operands");
    pin(offsetof(tf_operands, n_operands), 0, "tf_operands.n_operands");
    pin(offsetof(tf_operands, delta_convention), 4, "tf_operands.delta_convention");
    pin(offsetof(tf_operands, extrapolation_policy), 8, "tf_operands.extrapolation_policy");
    pin(offsetof(tf_operands, trial_reduction), 16, "tf_operands.trial_reduction");
    pin(offsetof(tf_operands, code), 24, "tf_operands.code");
    pin(offsetof(tf_operands, point), 32, "tf_operands.point");
    pin(offsetof(tf_operands, target), 40, "tf_operands.target");
    pin(offsetof(tf_operands, weight), 48, "tf_operands.weight");
    pin(offsetof(tf_operands, comparison), 56, "tf_operands.comparison");
    pin(offsetof(tf_operands, target_hi), 64, "tf_operands.target_hi");
    pin(offsetof(tf_operands, tolerance), 72, "tf_operands.tolerance");
    pin(offsetof(tf_operands, scale), 80, "tf_operands.scale");
    pin(offsetof(tf_operands, n_span), 88, "tf_operands.n_span");
    pin(offsetof(tf_operands, reduction), 96, "tf_operands.reduction");
    pin(offsetof(tf_operands, point_first), 104, "tf_operands.point_first");
    pin(offsetof(tf_operands, point_count), 112, "tf_operands.point_count");
    pin(offsetof(tf_operands, point_index), 120, "tf_operands.point_index");
    pin(offsetof(tf_operands, point_weight), 128, "tf_operands.point_weight");
    pin(offsetof(tf_operands, reduction_param), 136, "tf_operands.reduction_param");
    pin(offsetof(tf_operands, n_input), 144, "tf_operands.n_input");
    pin(offsetof(tf_operands, input_first), 152, "tf_operands.input_first");
    pin(offsetof(tf_operands, input_count), 160, "tf_operands.input_count");
    pin(offsetof(tf_operands, input_index), 168, "tf_operands.input_index");

    pin_size(sizeof(tf_variables), 40, "tf_variables");
    pin(offsetof(tf_variables, n_variables), 0, "tf_variables.n_variables");
    pin(offsetof(tf_variables, kind), 8, "tf_variables.kind");
    pin(offsetof(tf_variables, layer), 16, "tf_variables.layer");
    pin(offsetof(tf_variables, material), 24, "tf_variables.material");
    pin(offsetof(tf_variables, coefficient), 32, "tf_variables.coefficient");

    pin_size(sizeof(tf_plan_report), 56, "tf_plan_report");
    pin(offsetof(tf_plan_report, n_structured), 0, "tf_plan_report.n_structured");
    pin(offsetof(tf_plan_report, n_general), 4, "tf_plan_report.n_general");
    pin(offsetof(tf_plan_report, n_incoherent), 8, "tf_plan_report.n_incoherent");
    pin(offsetof(tf_plan_report, n_unsupported), 12, "tf_plan_report.n_unsupported");
    pin(offsetof(tf_plan_report, n_variables_inert), 16, "tf_plan_report.n_variables_inert");
    pin(offsetof(tf_plan_report, n_residuals), 20, "tf_plan_report.n_residuals");
    pin(offsetof(tf_plan_report, n_operands_unscored), 24, "tf_plan_report.n_operands_unscored");
    pin(offsetof(tf_plan_report, n_operand_levels), 28, "tf_plan_report.n_operand_levels");
    pin(offsetof(tf_plan_report, max_operand_span), 32, "tf_plan_report.max_operand_span");
    pin(offsetof(tf_plan_report, max_clamped_layers), 36, "tf_plan_report.max_clamped_layers");
    pin(offsetof(tf_plan_report, n_points_one_polarisation), 40, "tf_plan_report.n_points_one_polarisation");
    pin(offsetof(tf_plan_report, scratch_doubles), 48, "tf_plan_report.scratch_doubles");

    pin_size(sizeof(tf_plan_buffers), 104, "tf_plan_buffers");
    pin(offsetof(tf_plan_buffers, point_path), 0, "tf_plan_buffers.point_path");
    pin(offsetof(tf_plan_buffers, point_order), 8, "tf_plan_buffers.point_order");
    pin(offsetof(tf_plan_buffers, variable_inert), 16, "tf_plan_buffers.variable_inert");
    pin(offsetof(tf_plan_buffers, operand_residual), 24, "tf_plan_buffers.operand_residual");
    pin(offsetof(tf_plan_buffers, operand_order), 32, "tf_plan_buffers.operand_order");
    pin(offsetof(tf_plan_buffers, operand_level), 40, "tf_plan_buffers.operand_level");
    pin(offsetof(tf_plan_buffers, point_span_first), 48, "tf_plan_buffers.point_span_first");
    pin(offsetof(tf_plan_buffers, point_span_count), 56, "tf_plan_buffers.point_span_count");
    pin(offsetof(tf_plan_buffers, point_span_operand), 64, "tf_plan_buffers.point_span_operand");
    pin(offsetof(tf_plan_buffers, point_span_slot), 72, "tf_plan_buffers.point_span_slot");
    pin(offsetof(tf_plan_buffers, point_polarisation), 80, "tf_plan_buffers.point_polarisation");
    pin(offsetof(tf_plan_buffers, report), 88, "tf_plan_buffers.report");
    pin(offsetof(tf_plan_buffers, status), 96, "tf_plan_buffers.status");

    pin_size(sizeof(tf_workspace), 16, "tf_workspace");
    pin(offsetof(tf_workspace, scratch), 0, "tf_workspace.scratch");
    pin(offsetof(tf_workspace, scratch_doubles), 8, "tf_workspace.scratch_doubles");

    pin_size(sizeof(tf_output), 64, "tf_output");
    pin(offsetof(tf_output, residual), 0, "tf_output.residual");
    pin(offsetof(tf_output, jacobian), 8, "tf_output.jacobian");
    pin(offsetof(tf_output, value), 16, "tf_output.value");
    pin(offsetof(tf_output, operand_flag), 24, "tf_output.operand_flag");
    pin(offsetof(tf_output, n_operands_flagged), 32, "tf_output.n_operands_flagged");
    pin(offsetof(tf_output, trial_value), 40, "tf_output.trial_value");
    pin(offsetof(tf_output, merit), 48, "tf_output.merit");
    pin(offsetof(tf_output, status), 56, "tf_output.status");

    pin_size(sizeof(tf_plan_sizes), 88, "tf_plan_sizes");
    pin(offsetof(tf_plan_sizes, point_path_count), 0, "tf_plan_sizes.point_path_count");
    pin(offsetof(tf_plan_sizes, point_order_count), 8, "tf_plan_sizes.point_order_count");
    pin(offsetof(tf_plan_sizes, variable_inert_count), 16, "tf_plan_sizes.variable_inert_count");
    pin(offsetof(tf_plan_sizes, point_polarisation_count), 24, "tf_plan_sizes.point_polarisation_count");
    pin(offsetof(tf_plan_sizes, point_span_first_count), 32, "tf_plan_sizes.point_span_first_count");
    pin(offsetof(tf_plan_sizes, point_span_count_count), 40, "tf_plan_sizes.point_span_count_count");
    pin(offsetof(tf_plan_sizes, point_span_operand_count), 48, "tf_plan_sizes.point_span_operand_count");
    pin(offsetof(tf_plan_sizes, point_span_slot_count), 56, "tf_plan_sizes.point_span_slot_count");
    pin(offsetof(tf_plan_sizes, operand_residual_count), 64, "tf_plan_sizes.operand_residual_count");
    pin(offsetof(tf_plan_sizes, operand_order_count), 72, "tf_plan_sizes.operand_order_count");
    pin(offsetof(tf_plan_sizes, operand_level_count), 80, "tf_plan_sizes.operand_level_count");

    pin_size(sizeof(tf_spectral_output), 24, "tf_spectral_output");
    pin(offsetof(tf_spectral_output, value), 0, "tf_spectral_output.value");
    pin(offsetof(tf_spectral_output, dvalue_dwavelength), 8, "tf_spectral_output.dvalue_dwavelength");
    pin(offsetof(tf_spectral_output, status), 16, "tf_spectral_output.status");

    pin_size(sizeof(tf_needle_request), 32, "tf_needle_request");
    pin(offsetof(tf_needle_request, n_depths), 0, "tf_needle_request.n_depths");
    pin(offsetof(tf_needle_request, depth), 8, "tf_needle_request.depth");
    pin(offsetof(tf_needle_request, n_candidates), 16, "tf_needle_request.n_candidates");
    pin(offsetof(tf_needle_request, candidate_material), 24, "tf_needle_request.candidate_material");

    pin_size(sizeof(tf_needle_output), 24, "tf_needle_output");
    pin(offsetof(tf_needle_output, residual), 0, "tf_needle_output.residual");
    pin(offsetof(tf_needle_output, dsumsq_dinsertion), 8, "tf_needle_output.dsumsq_dinsertion");
    pin(offsetof(tf_needle_output, status), 16, "tf_needle_output.status");

    pin_size(sizeof(tf_index_output), 32, "tf_index_output");
    pin(offsetof(tf_index_output, value), 0, "tf_index_output.value");
    pin(offsetof(tf_index_output, dvalue_dn), 8, "tf_index_output.dvalue_dn");
    pin(offsetof(tf_index_output, dvalue_dk), 16, "tf_index_output.dvalue_dk");
    pin(offsetof(tf_index_output, status), 24, "tf_index_output.status");

}

/* Every enumerator. A code that changes value is worse than a struct that changes shape, because
 * it produces a plausible wrong answer rather than a crash: ask for TF_TARGET_RS and be given
 * transmission. */
static void TheNumbersAreTheOnesCSharpWasPromised()
{
    std::printf("every status, target, reduction and comparison keeps its number\n");

    pin(TF_OK, 0, "TF_OK");
    pin(TF_NULL_POINTER, 1, "TF_NULL_POINTER");
    pin(TF_BAD_COUNT, 2, "TF_BAD_COUNT");
    pin(TF_SCRATCH_TOO_SMALL, 3, "TF_SCRATCH_TOO_SMALL");
    pin(TF_PLAN_NOT_RUN, 4, "TF_PLAN_NOT_RUN");
    pin(TF_PLAN_STALE, 5, "TF_PLAN_STALE");
    pin(TF_UNKNOWN_TARGET_CODE, 6, "TF_UNKNOWN_TARGET_CODE");
    pin(TF_UNKNOWN_VARIABLE_KIND, 7, "TF_UNKNOWN_VARIABLE_KIND");
    pin(TF_INDEX_OUT_OF_RANGE, 8, "TF_INDEX_OUT_OF_RANGE");
    pin(TF_TOO_MANY_CLAMPED_LAYERS, 9, "TF_TOO_MANY_CLAMPED_LAYERS");
    pin(TF_NO_LIVE_VARIABLE, 10, "TF_NO_LIVE_VARIABLE");
    pin(TF_UNKNOWN_DELTA_CONVENTION, 11, "TF_UNKNOWN_DELTA_CONVENTION");
    pin(TF_UNKNOWN_COMPARISON, 12, "TF_UNKNOWN_COMPARISON");
    pin(TF_BAD_OPERAND_SCALE, 13, "TF_BAD_OPERAND_SCALE");
    pin(TF_UNKNOWN_REDUCTION, 15, "TF_UNKNOWN_REDUCTION");
    pin(TF_EMPTY_SPAN, 16, "TF_EMPTY_SPAN");
    pin(TF_OPERAND_CYCLE, 17, "TF_OPERAND_CYCLE");
    pin(TF_OPERAND_INPUT_KIND, 18, "TF_OPERAND_INPUT_KIND");
    pin(TF_NO_SCORED_OPERAND, 19, "TF_NO_SCORED_OPERAND");
    pin(TF_BAD_OPERAND_RANGE, 14, "TF_BAD_OPERAND_RANGE");
    pin(TF_NO_STRESS_DATA, 20, "TF_NO_STRESS_DATA");
    pin(TF_EXTRAPOLATED_INDEX, 21, "TF_EXTRAPOLATED_INDEX");
    pin(TF_UNKNOWN_EXTRAPOLATION_POLICY, 22, "TF_UNKNOWN_EXTRAPOLATION_POLICY");
    pin(TF_UNKNOWN_TRIAL_REDUCTION, 23, "TF_UNKNOWN_TRIAL_REDUCTION");
    pin(TF_NO_TRIAL_DATA, 24, "TF_NO_TRIAL_DATA");
    pin(TF_TRIAL_GRADIENT_UNDEFINED, 25, "TF_TRIAL_GRADIENT_UNDEFINED");
    pin(TF_NO_DISPERSION_DATA, 26, "TF_NO_DISPERSION_DATA");
    pin(TF_NOT_IMPLEMENTED_INCOHERENT, 64, "TF_NOT_IMPLEMENTED_INCOHERENT");
    pin(TF_NOT_IMPLEMENTED_MIXED, 65, "TF_NOT_IMPLEMENTED_MIXED");
    pin(TF_NOT_IMPLEMENTED_TARGET, 66, "TF_NOT_IMPLEMENTED_TARGET");
    pin(TF_NOT_IMPLEMENTED_VARIABLE, 67, "TF_NOT_IMPLEMENTED_VARIABLE");
    pin(TF_NOT_IMPLEMENTED_DISPERSION, 68, "TF_NOT_IMPLEMENTED_DISPERSION");
    pin(TF_NOT_IMPLEMENTED_REVERSED, 72, "TF_NOT_IMPLEMENTED_REVERSED");
    pin(TF_NOT_IMPLEMENTED_REDUCTION, 71, "TF_NOT_IMPLEMENTED_REDUCTION");
    pin(TF_NOT_IMPLEMENTED_TRIAL, 73, "TF_NOT_IMPLEMENTED_TRIAL");
    pin(TF_NOT_IMPLEMENTED_NEEDLE_OPAQUE, 69, "TF_NOT_IMPLEMENTED_NEEDLE_OPAQUE");

    /* tf_target_code */
    pin(TF_TARGET_RS, 0, "TF_TARGET_RS");
    pin(TF_TARGET_RP, 1, "TF_TARGET_RP");
    pin(TF_TARGET_TS, 2, "TF_TARGET_TS");
    pin(TF_TARGET_TP, 3, "TF_TARGET_TP");
    pin(TF_TARGET_R_TANPSI, 16, "TF_TARGET_R_TANPSI");
    pin(TF_TARGET_R_COSDELTA, 17, "TF_TARGET_R_COSDELTA");
    pin(TF_TARGET_T_TANPSI, 18, "TF_TARGET_T_TANPSI");
    pin(TF_TARGET_T_COSDELTA, 19, "TF_TARGET_T_COSDELTA");
    pin(TF_TARGET_RA, 36, "TF_TARGET_RA");
    pin(TF_TARGET_TA, 37, "TF_TARGET_TA");
    pin(TF_TARGET_R_PSI, 64, "TF_TARGET_R_PSI");
    pin(TF_TARGET_R_DELTA, 65, "TF_TARGET_R_DELTA");
    pin(TF_TARGET_T_PSI, 66, "TF_TARGET_T_PSI");
    pin(TF_TARGET_T_DELTA, 67, "TF_TARGET_T_DELTA");
    pin(TF_TARGET_DIFFERENCE, 68, "TF_TARGET_DIFFERENCE");
    pin(TF_TARGET_RATIO, 69, "TF_TARGET_RATIO");
    pin(TF_TARGET_AS, 70, "TF_TARGET_AS");
    pin(TF_TARGET_AP, 71, "TF_TARGET_AP");
    pin(TF_TARGET_AA, 72, "TF_TARGET_AA");
    pin(TF_TARGET_ODS, 73, "TF_TARGET_ODS");
    pin(TF_TARGET_ODP, 74, "TF_TARGET_ODP");
    pin(TF_TARGET_ODA, 75, "TF_TARGET_ODA");
    pin(TF_TARGET_R_PHASE_S, 76, "TF_TARGET_R_PHASE_S");
    pin(TF_TARGET_R_PHASE_P, 77, "TF_TARGET_R_PHASE_P");
    pin(TF_TARGET_T_PHASE_S, 78, "TF_TARGET_T_PHASE_S");
    pin(TF_TARGET_T_PHASE_P, 79, "TF_TARGET_T_PHASE_P");
    pin(TF_TARGET_R_NPOL, 80, "TF_TARGET_R_NPOL");
    pin(TF_TARGET_T_NPOL, 81, "TF_TARGET_T_NPOL");
    pin(TF_TARGET_R_EXT_DB, 82, "TF_TARGET_R_EXT_DB");
    pin(TF_TARGET_T_EXT_DB, 83, "TF_TARGET_T_EXT_DB");
    pin(TF_TARGET_R_DIAT, 84, "TF_TARGET_R_DIAT");
    pin(TF_TARGET_T_DIAT, 85, "TF_TARGET_T_DIAT");
    pin(TF_TARGET_TOTT, 86, "TF_TARGET_TOTT");
    pin(TF_TARGET_NLAY, 87, "TF_TARGET_NLAY");
    pin(TF_TARGET_MINT, 88, "TF_TARGET_MINT");
    pin(TF_TARGET_MAXT, 89, "TF_TARGET_MAXT");
    pin(TF_TARGET_STRESS, 90, "TF_TARGET_STRESS");
    pin(TF_TARGET_R_GD_S, 91, "TF_TARGET_R_GD_S");
    pin(TF_TARGET_R_GD_P, 92, "TF_TARGET_R_GD_P");
    pin(TF_TARGET_T_GD_S, 93, "TF_TARGET_T_GD_S");
    pin(TF_TARGET_T_GD_P, 94, "TF_TARGET_T_GD_P");
    pin(TF_TARGET_R_GDD_S, 95, "TF_TARGET_R_GDD_S");
    pin(TF_TARGET_R_GDD_P, 96, "TF_TARGET_R_GDD_P");
    pin(TF_TARGET_T_GDD_S, 97, "TF_TARGET_T_GDD_S");
    pin(TF_TARGET_T_GDD_P, 98, "TF_TARGET_T_GDD_P");
    pin(TF_TARGET_R_TOD_S, 99, "TF_TARGET_R_TOD_S");
    pin(TF_TARGET_R_TOD_P, 100, "TF_TARGET_R_TOD_P");
    pin(TF_TARGET_T_TOD_S, 101, "TF_TARGET_T_TOD_S");
    pin(TF_TARGET_T_TOD_P, 102, "TF_TARGET_T_TOD_P");

    /* tf_variable_kind */
    pin(TF_VARIABLE_LAYER_THICKNESS, 0, "TF_VARIABLE_LAYER_THICKNESS");
    pin(TF_VARIABLE_MATERIAL_COEFFICIENT, 1, "TF_VARIABLE_MATERIAL_COEFFICIENT");

    /* tf_path */
    pin(TF_PATH_STRUCTURED, 0, "TF_PATH_STRUCTURED");
    pin(TF_PATH_GENERAL, 1, "TF_PATH_GENERAL");
    pin(TF_PATH_INCOHERENT, 2, "TF_PATH_INCOHERENT");
    pin(TF_PATH_MIXED, 3, "TF_PATH_MIXED");

    /* tf_delta_convention */
    pin(TF_DELTA_PRINCIPAL, 0, "TF_DELTA_PRINCIPAL");
    pin(TF_DELTA_POSITIVE, 1, "TF_DELTA_POSITIVE");
    pin(TF_DELTA_SHORTEST_ARC, 2, "TF_DELTA_SHORTEST_ARC");

    /* tf_reduction */
    pin(TF_REDUCE_SUM, 0, "TF_REDUCE_SUM");
    pin(TF_REDUCE_MAX, 1, "TF_REDUCE_MAX");
    pin(TF_REDUCE_MIN, 2, "TF_REDUCE_MIN");
    pin(TF_REDUCE_PTV, 3, "TF_REDUCE_PTV");
    pin(TF_REDUCE_RMS_DEV, 4, "TF_REDUCE_RMS_DEV");
    pin(TF_REDUCE_CROSSING, 5, "TF_REDUCE_CROSSING");

    /* tf_trial_reduction */
    pin(TF_TRIAL_NOMINAL, 0, "TF_TRIAL_NOMINAL");
    pin(TF_TRIAL_WORST, 1, "TF_TRIAL_WORST");
    pin(TF_TRIAL_MEAN, 2, "TF_TRIAL_MEAN");
    pin(TF_TRIAL_FRACTION_PASSING, 3, "TF_TRIAL_FRACTION_PASSING");

    /* tf_extrapolation_policy */
    pin(TF_EXTRAPOLATION_FLAG, 0, "TF_EXTRAPOLATION_FLAG");
    pin(TF_EXTRAPOLATION_REFUSE, 1, "TF_EXTRAPOLATION_REFUSE");

    /* tf_comparison */
    pin(TF_COMPARE_NONE, 0, "TF_COMPARE_NONE");
    pin(TF_COMPARE_EQUAL, 1, "TF_COMPARE_EQUAL");
    pin(TF_COMPARE_LE, 2, "TF_COMPARE_LE");
    pin(TF_COMPARE_GE, 3, "TF_COMPARE_GE");
    pin(TF_COMPARE_RANGE, 4, "TF_COMPARE_RANGE");

    /* tf_polarisation_need */
    pin(TF_NEED_S, 1, "TF_NEED_S");
    pin(TF_NEED_P, 2, "TF_NEED_P");

}

static void TheVersionAndTheFreezeAgree()
{
    std::printf("the header, the ledger and this file agree on which ABI is frozen\n");

    /* The version this file was written against. It rises only in a commit that also moves the
     * pins above and adds a change-log row - that is what makes the three inseparable. */
    pin(TF_ABI_VERSION, 12, "TF_ABI_VERSION");

    /* A LAST GUARD AGAINST THE QUIET KIND OF DRIFT. tf_problem is the struct most likely to grow,
     * and a field appended to its end moves nothing above it - so the size check is the only
     * thing that notices. It is stated separately here, rather than trusted to the block above,
     * because an appended field is the change most likely to be believed harmless. It is harmless
     * for C++ and it is not harmless for a managed struct that was compiled against the old size.
     */
    pin_size(sizeof(tf_problem), 248, "tf_problem");
}

int main()
{
    setvbuf(stdout, NULL, _IONBF, 0);
    std::printf("\n=== the frozen ABI ===\n\n");

    TheLayoutIsTheOneCSharpWasPromised();  std::printf("\n");
    TheNumbersAreTheOnesCSharpWasPromised(); std::printf("\n");
    TheVersionAndTheFreezeAgree();         std::printf("\n");

    std::printf("=== %d checks, %d failures ===\n\n", g_checks, g_failures);
    return g_failures != 0 ? 1 : 0;
}
