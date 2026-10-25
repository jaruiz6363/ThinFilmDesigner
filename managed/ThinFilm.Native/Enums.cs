// Enums.cs — GENERATED from native/include/thinfilm.h. Do not hand-edit.
//
// Regenerate with tools/gen-managed-enums.pl when the ABI changes, which — the ABI being frozen —
// means in the same commit that bumps TF_ABI_VERSION, moves the pins in
// tests/native/test_abi_frozen.cpp and adds a change-log row to docs/scope.md.
//
// THE MEMBER NAMES ARE THE C NAMES. They are what the pins are keyed by, so the managed layout
// test can look each one up without a translation table — and a translation table is the thing
// that would silently go stale. The unusual C# casing is the price, and it is cheap: only the
// Design layer above this one is meant to be read as C#.

namespace ThinFilm.Native;

public enum TfComparison : int
{
    TF_COMPARE_NONE                   = 0,
    TF_COMPARE_EQUAL                  = 1,
    TF_COMPARE_LE                     = 2,
    TF_COMPARE_GE                     = 3,
    TF_COMPARE_RANGE                  = 4
}

public enum TfDeltaConvention : int
{
    TF_DELTA_PRINCIPAL                = 0,
    TF_DELTA_POSITIVE                 = 1,
    TF_DELTA_SHORTEST_ARC             = 2
}

public enum TfExtrapolationPolicy : int
{
    TF_EXTRAPOLATION_FLAG             = 0,
    TF_EXTRAPOLATION_REFUSE           = 1
}

public enum TfPath : int
{
    TF_PATH_STRUCTURED                = 0,
    TF_PATH_GENERAL                   = 1,
    TF_PATH_INCOHERENT                = 2,
    TF_PATH_MIXED                     = 3
}

public enum TfPolarisationNeed : int
{
    TF_NEED_S                         = 1,
    TF_NEED_P                         = 2
}

public enum TfReduction : int
{
    TF_REDUCE_SUM                     = 0,
    TF_REDUCE_MAX                     = 1,
    TF_REDUCE_MIN                     = 2,
    TF_REDUCE_PTV                     = 3,
    TF_REDUCE_RMS_DEV                 = 4,
    TF_REDUCE_CROSSING                = 5
}

public enum TfStatus : int
{
    TF_OK                             = 0,
    TF_NULL_POINTER                   = 1,
    TF_BAD_COUNT                      = 2,
    TF_SCRATCH_TOO_SMALL              = 3,
    TF_PLAN_NOT_RUN                   = 4,
    TF_PLAN_STALE                     = 5,
    TF_UNKNOWN_TARGET_CODE            = 6,
    TF_UNKNOWN_VARIABLE_KIND          = 7,
    TF_INDEX_OUT_OF_RANGE             = 8,
    TF_TOO_MANY_CLAMPED_LAYERS        = 9,
    TF_NO_LIVE_VARIABLE               = 10,
    TF_UNKNOWN_DELTA_CONVENTION       = 11,
    TF_UNKNOWN_COMPARISON             = 12,
    TF_BAD_OPERAND_SCALE              = 13,
    TF_UNKNOWN_REDUCTION              = 15,
    TF_EMPTY_SPAN                     = 16,
    TF_OPERAND_CYCLE                  = 17,
    TF_OPERAND_INPUT_KIND             = 18,
    TF_NO_SCORED_OPERAND              = 19,
    TF_BAD_OPERAND_RANGE              = 14,
    TF_NO_STRESS_DATA                 = 20,
    TF_EXTRAPOLATED_INDEX             = 21,
    TF_UNKNOWN_EXTRAPOLATION_POLICY   = 22,
    TF_UNKNOWN_TRIAL_REDUCTION        = 23,
    TF_NO_TRIAL_DATA                  = 24,
    TF_TRIAL_GRADIENT_UNDEFINED       = 25,
    TF_NO_DISPERSION_DATA             = 26,
    TF_NOT_IMPLEMENTED_INCOHERENT     = 64,
    TF_NOT_IMPLEMENTED_MIXED          = 65,
    TF_NOT_IMPLEMENTED_TARGET         = 66,
    TF_NOT_IMPLEMENTED_VARIABLE       = 67,
    TF_NOT_IMPLEMENTED_DISPERSION     = 68,
    TF_NOT_IMPLEMENTED_REVERSED       = 72,
    TF_NOT_IMPLEMENTED_REDUCTION      = 71,
    TF_NOT_IMPLEMENTED_TRIAL          = 73,
    TF_NOT_IMPLEMENTED_NEEDLE_OPAQUE  = 69
}

public enum TfTargetCode : int
{
    TF_TARGET_RS                      = 0,
    TF_TARGET_RP                      = 1,
    TF_TARGET_TS                      = 2,
    TF_TARGET_TP                      = 3,
    TF_TARGET_R_TANPSI                = 16,
    TF_TARGET_R_COSDELTA              = 17,
    TF_TARGET_T_TANPSI                = 18,
    TF_TARGET_T_COSDELTA              = 19,
    TF_TARGET_RA                      = 36,
    TF_TARGET_TA                      = 37,
    TF_TARGET_R_PSI                   = 64,
    TF_TARGET_R_DELTA                 = 65,
    TF_TARGET_T_PSI                   = 66,
    TF_TARGET_T_DELTA                 = 67,
    TF_TARGET_DIFFERENCE              = 68,
    TF_TARGET_RATIO                   = 69,
    TF_TARGET_AS                      = 70,
    TF_TARGET_AP                      = 71,
    TF_TARGET_AA                      = 72,
    TF_TARGET_ODS                     = 73,
    TF_TARGET_ODP                     = 74,
    TF_TARGET_ODA                     = 75,
    TF_TARGET_R_PHASE_S               = 76,
    TF_TARGET_R_PHASE_P               = 77,
    TF_TARGET_T_PHASE_S               = 78,
    TF_TARGET_T_PHASE_P               = 79,
    TF_TARGET_R_NPOL                  = 80,
    TF_TARGET_T_NPOL                  = 81,
    TF_TARGET_R_EXT_DB                = 82,
    TF_TARGET_T_EXT_DB                = 83,
    TF_TARGET_R_DIAT                  = 84,
    TF_TARGET_T_DIAT                  = 85,
    TF_TARGET_TOTT                    = 86,
    TF_TARGET_NLAY                    = 87,
    TF_TARGET_MINT                    = 88,
    TF_TARGET_MAXT                    = 89,
    TF_TARGET_STRESS                  = 90,
    TF_TARGET_R_GD_S                  = 91,
    TF_TARGET_R_GD_P                  = 92,
    TF_TARGET_T_GD_S                  = 93,
    TF_TARGET_T_GD_P                  = 94,
    TF_TARGET_R_GDD_S                 = 95,
    TF_TARGET_R_GDD_P                 = 96,
    TF_TARGET_T_GDD_S                 = 97,
    TF_TARGET_T_GDD_P                 = 98,
    TF_TARGET_R_TOD_S                 = 99,
    TF_TARGET_R_TOD_P                 = 100,
    TF_TARGET_T_TOD_S                 = 101,
    TF_TARGET_T_TOD_P                 = 102
}

public enum TfTrialReduction : int
{
    TF_TRIAL_NOMINAL                  = 0,
    TF_TRIAL_WORST                    = 1,
    TF_TRIAL_MEAN                     = 2,
    TF_TRIAL_FRACTION_PASSING         = 3
}

public enum TfVariableKind : int
{
    TF_VARIABLE_LAYER_THICKNESS       = 0,
    TF_VARIABLE_MATERIAL_COEFFICIENT  = 1
}

/// <summary>Loose #defines from the header that are part of the ABI.</summary>
public static class TfAbi
{
    public const int TF_ABI_VERSION = 12;
    public const int TF_FLAG_EXTRAPOLATED = 1;
    public const int TF_FLAG_AMBIGUOUS = 2;
    public const int TF_FLAG_SYNTHETIC_INDEX = 8;
    public const int TF_FLAG_PINNED = 4;
}
