/* thinfilm.h — the native evaluation contract.
 *
 * This header is the ABI. docs/abi-and-allocation-contract.md is its prose; where the two
 * disagree, this file is what ships and the document is wrong.
 *
 * FIVE RULES, which are constraints rather than preferences:
 *
 *   1. All memory is dimensioned and allocated by the caller. Nothing below allocates — no
 *      malloc, no new, no std::vector, no RAII type that owns storage.
 *   2. No fixed-size array dimensioned by a constant. There is no TF_MAX_LAYERS and there will
 *      not be one. A reviewer who finds one should report it as a defect.
 *   3. The port to CUDA must be mechanical. Everything here is POD and C-linkage; the evaluation
 *      path has no exceptions, no virtuals and no recursion.
 *   4. Double precision throughout. Single was tried in the predecessor code and was not good
 *      enough; the change to double was forced. That question is settled.
 *   5. An unimplemented path is LOUD. See TF_NOT_IMPLEMENTED and tf_status.
 *
 * Rules 1 and 3 are the same rule. A function that allocates nothing, holds no state and touches
 * only caller-supplied buffers is what a kernel is.
 */

#ifndef THINFILM_H
#define THINFILM_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ---------------------------------------------------------------------------------------------
 * Portability markers
 *
 * TF_HD is the whole of the CUDA-portability discipline in one macro. Every function that will
 * eventually run on the device carries it from the day it is written, so the port does not have
 * to find them.
 * ------------------------------------------------------------------------------------------- */

#if defined(__CUDACC__)
#  define TF_HD __host__ __device__
#else
#  define TF_HD
#endif

#if defined(_WIN32)
#  define TF_API __declspec(dllexport)
#else
#  define TF_API __attribute__((visibility("default")))
#endif

/* ---------------------------------------------------------------------------------------------
 * Scalars
 *
 * tf_complex is laid out to match System.Numerics.Complex exactly — two adjacent doubles, real
 * first — so material arrays marshal with no transformation.
 * ------------------------------------------------------------------------------------------- */

typedef struct tf_complex
{
    double re;
    double im;
} tf_complex;

/* A general 2x2 characteristic matrix: eight doubles. */
typedef struct tf_matrix2
{
    tf_complex m00, m01, m10, m11;
} tf_matrix2;

/* The lossless form [[a, ib], [ic, d]] with a, b, c, d real: FOUR doubles, not eight.
 *
 * This form is closed under multiplication —
 *
 *     [ a1  ib1 ] [ a2  ib2 ]   [ a1a2 - b1c2      i(a1b2 + b1d2) ]
 *     [ ic1 d1  ] [ ic2 d2  ] = [ i(c1a2 + d1c2)   d1d2 - c1b2     ]
 *
 * so prefix products, suffix products and derivative products all stay in it. Eight real
 * multiplies against thirty-two, and half the storage; the storage is what will limit occupancy
 * on the GPU. det = ad + bc is preserved and equals 1 for a lossless stack, which every test
 * asserts. */
typedef struct tf_matrix2s
{
    double a, b, c, d;
} tf_matrix2s;

/* The layouts above are the ABI, so they are asserted rather than described. tf_complex matching
 * System.Numerics.Complex is what lets material arrays marshal untouched; the four-against-eight
 * is the whole point of the structured path. A change that breaks either should fail to compile,
 * not fail at run time in a wrapper. */
#if defined(__cplusplus)
static_assert(sizeof(tf_complex)  == 2 * sizeof(double), "tf_complex must be two adjacent doubles");
static_assert(sizeof(tf_matrix2)  == 8 * sizeof(double), "general 2x2 must be eight doubles");
static_assert(sizeof(tf_matrix2s) == 4 * sizeof(double), "structured 2x2 must be four doubles");
#else
typedef char tf_assert_complex_layout[(sizeof(tf_complex)  == 2 * sizeof(double)) ? 1 : -1];
typedef char tf_assert_matrix2_size  [(sizeof(tf_matrix2)  == 8 * sizeof(double)) ? 1 : -1];
typedef char tf_assert_matrix2s_size [(sizeof(tf_matrix2s) == 4 * sizeof(double)) ? 1 : -1];
#endif

/* ---------------------------------------------------------------------------------------------
 * Status
 *
 * Every entry point returns one of these AND writes it to out->status. A non-zero status means
 * the output buffers were NOT written and hold whatever the caller left in them. The managed
 * wrapper turns non-zero into an exception. Silence, or a plausible zero, is the failure mode
 * this project is most exposed to.
 * ------------------------------------------------------------------------------------------- */

typedef enum tf_status
{
    TF_OK = 0,

    /* Caller error: the contract was not met. */
    TF_NULL_POINTER            = 1,
    TF_BAD_COUNT               = 2,   /* a count is negative, or zero where it may not be */
    TF_SCRATCH_TOO_SMALL       = 3,   /* smaller than the size tf_plan asked for */
    TF_PLAN_NOT_RUN            = 4,   /* plan buffers do not carry a completed plan */
    TF_PLAN_STALE              = 5,   /* the problem changed shape since tf_plan ran */
    TF_UNKNOWN_TARGET_CODE     = 6,
    TF_UNKNOWN_VARIABLE_KIND   = 7,
    TF_INDEX_OUT_OF_RANGE      = 8,
    TF_TOO_MANY_CLAMPED_LAYERS = 9,   /* the accumulated product would overflow; see
                                       * tf_plan_report.max_clamped_layers */
    TF_NO_LIVE_VARIABLE        = 10,  /* every variable is inert: there is nothing to optimise */
    TF_UNKNOWN_DELTA_CONVENTION = 11, /* operands.delta_convention is not a tf_delta_convention */
    TF_UNKNOWN_COMPARISON      = 12,  /* comparison is not a tf_comparison */
    TF_BAD_OPERAND_SCALE       = 13,  /* scale is not finite and positive */
    TF_UNKNOWN_REDUCTION       = 15,  /* reduction is not a tf_reduction */
    TF_EMPTY_SPAN              = 16,  /* a scored operand spans no points at all */
    TF_OPERAND_CYCLE           = 17,  /* the input graph is not acyclic */
    TF_OPERAND_INPUT_KIND      = 18,  /* an operand's inputs are wrong for its code, or it reads
                                       * both points and operands */
    TF_NO_SCORED_OPERAND       = 19,  /* every operand is NONE: nothing to optimise */
    TF_BAD_OPERAND_RANGE       = 14,  /* a RANGE operand has target > target_hi, or a negative
                                       * tolerance */
    TF_NO_STRESS_DATA          = 20,  /* a STRESS operand, and problem.material_stress is NULL.
                                       * NULL is not zero - see tf_problem */
    TF_EXTRAPOLATED_INDEX      = 21,  /* an evaluated point rests on an index value the caller
                                       * marked as extrapolated, and the caller asked to refuse
                                       * rather than be told. See tf_extrapolation_policy */
    TF_UNKNOWN_EXTRAPOLATION_POLICY = 22, /* operands.extrapolation_policy is not one of the two */
    TF_UNKNOWN_TRIAL_REDUCTION = 23,  /* trial_reduction is not a tf_trial_reduction */
    TF_NO_TRIAL_DATA           = 24,  /* an operand reduces over trials and n_trials is 0, or
                                       * n_trials is set and both trial arrays are NULL - a table
                                       * of stacks identical to the nominal one, which is a
                                       * caller mistake rather than an expensive way to agree */
    TF_TRIAL_GRADIENT_UNDEFINED = 25, /* a gradient was asked for through a trial reduction that
                                       * does not have one. See TF_TRIAL_FRACTION_PASSING */
    TF_NO_DISPERSION_DATA      = 26,  /* a group delay, GDD or TOD operand, and the dn/dlambda (or,
                                       * for GDD, also d2n/dlambda2; for TOD, also d3n/dlambda3)
                                       * arrays it needs are NULL. NULL is not zero - see
                                       * tf_problem (ABI 11, 12) */



    /* Not a caller error: a path that exists in the design and not yet in the code.
     * These are the ones TF_NOT_IMPLEMENTED raises, and they are meant to be impossible to
     * mistake for a result. */
    TF_NOT_IMPLEMENTED_INCOHERENT  = 64,  /* massive / incoherent layers present */
    TF_NOT_IMPLEMENTED_MIXED       = 65,  /* clustered-absorber fast path */
    TF_NOT_IMPLEMENTED_TARGET      = 66,  /* target code understood but not yet evaluable */
    TF_NOT_IMPLEMENTED_VARIABLE    = 67,  /* variable kind understood but not yet differentiable */
    TF_NOT_IMPLEMENTED_DISPERSION  = 68,  /* dispersion model understood but not yet evaluable */
    TF_NOT_IMPLEMENTED_REVERSED      = 72,/* a reversed configuration where the meaning of an
                                           * index into the stack has not been settled */
    TF_NOT_IMPLEMENTED_REDUCTION     = 71,/* reduction understood, not yet evaluable */
    TF_NOT_IMPLEMENTED_TRIAL         = 73,/* an entry point other than tf_evaluate and
                                           * tf_evaluate_jacobian was asked for while a trial
                                           * reduction is live. A needle map, a group delay and an
                                           * index derivative are each defined on ONE stack, and
                                           * which stack a thousand of them means is a question
                                           * rather than an oversight. See tf_evaluate_needle. */
    TF_NOT_IMPLEMENTED_NEEDLE_OPAQUE = 69 /* a needle map was asked for across a layer thick enough

                                           * to clamp. See tf_evaluate_needle. */
} tf_status;

/* Rule 5, in one macro.
 *
 * Three things, all of them: name the path and the reason on stderr, set a distinct status, and
 * leave the output buffers untouched so a caller who ignores the status gets nothing that looks
 * like an answer.
 *
 * Deliberately NOT TF_HD. On the device this becomes a status write and a printf; a stub that
 * silently did less would defeat the purpose, so the device variant is written when the device
 * path is, not before. */
#ifndef TF_NOT_IMPLEMENTED
#include <stdio.h>
#define TF_NOT_IMPLEMENTED(status_out, code, what)                                             \
    do {                                                                                       \
        fprintf(stderr,                                                                        \
                "\n*** THINFILM: NOT IMPLEMENTED ***\n"                                        \
                "    %s\n"                                                                     \
                "    at %s:%d in %s\n"                                                         \
                "    status %d. Output buffers were not written.\n\n",                         \
                (what), __FILE__, __LINE__, __func__, (int)(code));                            \
        if ((status_out) != 0) *(status_out) = (int32_t)(code);                                \
    } while (0)
#endif

/* ---------------------------------------------------------------------------------------------
 * Codes
 * ------------------------------------------------------------------------------------------- */

/* Target codes. 0..38 keep the predecessor's numbering so existing data files stay readable;
 * anything this project adds starts at 64 so an addition is obvious on sight. */
typedef enum tf_target_code
{
    TF_TARGET_RS          = 0,
    TF_TARGET_RP          = 1,
    TF_TARGET_TS          = 2,
    TF_TARGET_TP          = 3,

    TF_TARGET_R_TANPSI    = 16,   /* tan(Psi) in reflection */
    TF_TARGET_R_COSDELTA  = 17,   /* cos(Delta) in reflection */
    TF_TARGET_T_TANPSI    = 18,
    TF_TARGET_T_COSDELTA  = 19,

    TF_TARGET_RA          = 36,   /* (Rs + Rp) / 2 */
    TF_TARGET_TA          = 37,   /* (Ts + Tp) / 2 */

    /* Added here. Psi and Delta themselves, which the predecessor carried only through their
     * tangent and cosine.
     *
     * TF_TARGET_R_DELTA WRAPS. arg(rho) has a branch cut, so the residual is discontinuous across
     * it even though the derivative is not. cos(Delta) avoids the problem entirely, which is
     * presumably why the predecessor stopped there. What this code does at the cut is an open
     * question in the contract document and is NOT settled by its presence here. */
    TF_TARGET_R_PSI       = 64,
    TF_TARGET_R_DELTA     = 65,
    TF_TARGET_T_PSI       = 66,
    TF_TARGET_T_DELTA     = 67,

    /* DERIVED OPERANDS: these read other operands rather than points.
     *
     * Generic on purpose. The specification asks for TSP = Ts/Tp, RSP = Rs/Rp, SHAPE = BW/BW,
     * DEDGE = edge - edge and DCOL = a colour difference across two configurations; four of the
     * five ARE a ratio or a difference of two operands, and giving each its own code would be
     * five implementations of two pieces of arithmetic. A named operand becomes one of these with
     * the right inputs. The colorimetric ones need their own codes because their local
     * derivatives are not two lines. */
    TF_TARGET_DIFFERENCE  = 68,   /* input[0] - input[1] */
    TF_TARGET_RATIO       = 69,   /* input[0] / input[1] */

    /* ABSORPTANCE, as its own code rather than as 1 - R - T at the call site.
     *
     * For a non-scattering stack the two are the same number, and this computes it that way. They
     * part company the day a scatter model arrives, and a design whose loss budget was written as
     * `1 - R - T` would then silently start counting scattered light as absorbed. The code costs
     * nothing now and is the difference between a number that stays true and one that does not. */
    TF_TARGET_AS          = 70,
    TF_TARGET_AP          = 71,
    TF_TARGET_AA          = 72,

    /* OPTICAL DENSITY, -log10(T).
     *
     * Blocking specifications are written in OD and cannot be written in T: linear transmittance
     * is numerically blind below about 1e-4, where the difference between OD 5 and OD 7 is two
     * orders of magnitude of blocking and four decimal places of T. An optimiser handed T sees
     * nothing to push on; handed OD it sees a straight line.
     *
     * THE FLOOR. -log10(T) runs away as T underflows, so it is clamped, and reduction_param[1]
     * carries the ceiling in OD. Zero means the default derived below. Above the clamp the
     * derivative is zero, for the same reason the absorption clamp's is: the value no longer
     * depends on the design. */
    TF_TARGET_ODS         = 73,
    TF_TARGET_ODP         = 74,
    TF_TARGET_ODA         = 75,

    /* ABSOLUTE PHASE on reflection and transmission - arg(r) and arg(t).
     *
     * These are ANGLES and obey targets.delta_convention exactly as Psi's Delta does. There is no
     * "average" variant: averaging two angles is not an operation, and offering one would invite
     * the answer nobody wants at the branch cut. */
    TF_TARGET_R_PHASE_S   = 76,
    TF_TARGET_R_PHASE_P   = 77,
    TF_TARGET_T_PHASE_S   = 78,
    TF_TARGET_T_PHASE_P   = 79,

    /* NON-POLARISATION: |Rs - Rp| and |Ts - Tp|.
     *
     * Worth its own code rather than a pair of Rs and Rp targets, and the specification says why:
     * as a DIFFERENCE it has a well-conditioned gradient toward the balanced solution, whereas two
     * independent targets fight each other and converge on a compromise satisfying neither.
     *
     * The absolute value has a kink at balance. That is where a design wants to be, and the
     * subgradient there is taken as ZERO - the same choice as a comparison exactly on its limit,
     * and for the same reason: a requirement that is met exerts no force. */
    TF_TARGET_R_NPOL      = 80,
    TF_TARGET_T_NPOL      = 81,

    /* EXTINCTION RATIO in decibels, 10 log10(s/p). The plain ratio is TF_TARGET_RATIO over two
     * operands and needs no code; the decibel form does, because a logarithm is not a ratio. */
    TF_TARGET_R_EXT_DB    = 82,
    TF_TARGET_T_EXT_DB    = 83,

    /* DIATTENUATION, (p - s)/(p + s) - bounded in [-1, 1] and well behaved where a ratio is not. */
    TF_TARGET_R_DIAT      = 84,
    TF_TARGET_T_DIAT      = 85,

    /* OPERANDS THAT READ THE STACK RATHER THAN THE SPECTRUM.
     *
     * A THIRD KIND, and the evaluation model did not have one. Every operand until now reads
     * points or reads operands; these read the LAYER TABLE and have neither. `TOTT` is the sum of
     * layer_thickness - it has no wavelength, no angle and no configuration, and asking which
     * point it belongs to has no answer.
     *
     * THEY ARE PENALTIES, NOT BOUNDS, and the specification says why: "a bound stops the optimizer
     * at a wall with no gradient information; a penalty term gives it a direction well before it
     * arrives." A variable bounded at 1 nm tells the optimiser nothing until it is at 1 nm; a
     * hinge penalty tells it from 3 nm away which way to go.
     *
     * MINT and MAXT take their threshold in reduction_param[0], which is otherwise the
     * smooth-extremum sharpness and is unused by an operand with no span. */
    TF_TARGET_TOTT        = 86,   /* sum of layer_thickness                                   */
    TF_TARGET_NLAY        = 87,   /* how many layers. Constant in every thickness, so its
                                   * Jacobian row is exactly zero - which is honest rather than
                                   * useless: it is what a discrete stage and a report read. */
    TF_TARGET_MINT        = 88,   /* sum over layers of max(0, threshold - d)                  */
    TF_TARGET_MAXT        = 89,   /* sum over layers of max(0, d - threshold)                  */

    /* SIGMA-d. The one stack operand whose value depends on WHAT a layer is made of and not only
     * on how thick it is, which is why it waited for material_stress to have a seat.
     *
     * It is also the first operand whose needle contribution differs BY CANDIDATE. TOTT charges
     * the same for a nanometre of anything; STRESS charges sigma_m, so the needle map can prefer
     * a low-stress material at the same depth. That distinction is the whole point of carrying it
     * as a penalty rather than as a post-hoc report. */
    TF_TARGET_STRESS      = 90,   /* sum over layers of material_stress[m(j)] * d_j            */

    /* GROUP DELAY, GROUP DELAY DISPERSION (ABI 11) AND THIRD-ORDER DISPERSION (ABI 12), on
     * reflection and on transmission.
     *
     *     GD  = d(phi)/d(omega)            femtoseconds
     *     GDD = d2(phi)/d(omega)2          femtoseconds squared
     *     TOD = d3(phi)/d(omega)3          femtoseconds cubed
     *
     * phi is arg(r) or arg(t) of the coated surface, so like the phases these ignore the back
     * surface. Signed so that a delay is positive: light that turns back deeper in the stack has
     * the larger GD, and a slab of air of thickness L in front of a mirror adds 2L/c.
     *
     * ANALYTIC, NOT DIFFERENCED. The first three wavelength derivatives of the amplitudes are
     * carried through the characteristic matrix alongside it, as D2.3 does for the first, and the
     * thickness derivatives come from the same prefix/suffix scheme as the Jacobian - see
     * tf_dispersion.cpp. Neither involves the phase itself, so neither has a branch cut.
     *
     * The material's own dispersion goes in through material_dindex_dwavelength (all three),
     * material_d2index_dwavelength2 (GDD and TOD) and material_d3index_dwavelength3 (TOD), and an
     * operand that needs them refuses with TF_NO_DISPERSION_DATA when they are NULL. */
    TF_TARGET_R_GD_S      = 91,
    TF_TARGET_R_GD_P      = 92,
    TF_TARGET_T_GD_S      = 93,
    TF_TARGET_T_GD_P      = 94,
    TF_TARGET_R_GDD_S     = 95,
    TF_TARGET_R_GDD_P     = 96,
    TF_TARGET_T_GDD_S     = 97,
    TF_TARGET_T_GDD_P     = 98,
    TF_TARGET_R_TOD_S     = 99,
    TF_TARGET_R_TOD_P     = 100,
    TF_TARGET_T_TOD_S     = 101,
    TF_TARGET_T_TOD_P     = 102
} tf_target_code;


/* What an active variable is. */
typedef enum tf_variable_kind
{
    TF_VARIABLE_LAYER_THICKNESS      = 0,  /* uses variable_layer */
    TF_VARIABLE_MATERIAL_COEFFICIENT = 1   /* uses variable_material and variable_coefficient;
                                            * this is the seam where automatic differentiation
                                            * takes over from analytic — see the contract */
} tf_variable_kind;

/* Which arithmetic an evaluation point takes. Decided once by tf_plan, never re-decided in the
 * inner loop.
 *
 * The path is a property of the POINT, not of the stack: k = 0 everywhere still gives a complex
 * phase beyond the critical angle. Air incident with higher-index layers never sees it; a
 * coupling prism does. */
typedef enum tf_path
{
    TF_PATH_STRUCTURED = 0,  /* every material lossless here AND no layer beyond critical */
    TF_PATH_GENERAL    = 1,
    TF_PATH_INCOHERENT = 2,  /* massive layers present — not implemented */
    TF_PATH_MIXED      = 3   /* absorbers present but clustered — not implemented */
} tf_path;

/* ---------------------------------------------------------------------------------------------
 * The buffers
 *
 * Every pointer below is owned by the caller. const marks read-only to native code. Lengths are
 * exact; none of them is an upper bound, and nothing here is permitted to grow one.
 * ------------------------------------------------------------------------------------------- */

/* The stack, the materials, and the points at which it is evaluated. */

/* WHAT A STACK IS BEING MEASURED IN.
 *
 * Until now the incident and exit media were problem-wide: one set for every point. A
 * configuration is exactly the set of things that were problem-wide and should not have been -
 * a design has to meet its specification in air AND cemented, on BK7 AND on silica, from the
 * front AND from the back, and those are different evaluation contexts over the SAME stack.
 *
 * TWO MODES, AND THEY DO NOT MIX, as with operand spans. When tf_problem.configurations is NULL
 * there is one implicit configuration and tf_problem.index_incident / index_exit describe it,
 * exactly as before. When it is given, those two must be NULL and point_configuration says which
 * configuration each point belongs to - so there is one statement of a point's media and no way
 * for two of them to disagree.
 *
 * IT HANGS OFF tf_problem RATHER THAN BEING A SIXTH ARGUMENT, which departs from the sketch in
 * evaluation-model-contract.md §9 and does so to keep that section's own promise: "tf_evaluate /
 * tf_evaluate_jacobian - the inner loop, unchanged". Every evaluator already takes a tf_problem,
 * and the media are part of what a problem IS. Passing them separately would add an argument to
 * five entry points to say something the problem already has to know. */
typedef struct tf_configurations
{
    int32_t n_configurations;

    /* [n_configurations * n_wavelengths], configuration-major - the same convention as
     * material_index, so one indexing habit serves the whole ABI. */
    const tf_complex* index_incident;
    const tf_complex* index_exit;

    /* dn/dlambda for the two media, same shape. Required by tf_evaluate_wavelength_derivative and
     * ignored by everything else, exactly as their problem-wide predecessors were. */
    const tf_complex* dindex_incident_dwavelength;
    const tf_complex* dindex_exit_dwavelength;

    /* Extrapolation marks for the two media, [n_configurations * n_wavelengths], exactly as their
     * problem-wide predecessors. NULL means measured. */
    const int32_t*    index_incident_is_extrapolated;
    const int32_t*    index_exit_is_extrapolated;

    /* [n_configurations] non-zero traverses the layers back to front: the same stack seen from the
     * other side. May be NULL, which means none of them are.
     *
     * WHY THIS RATHER THAN A coated_side ENUM. Front and back are the same stack traversed in
     * opposite directions, which costs the solver nothing - it already walks the layer list in an
     * order. "Both sides coated" is NOT a third value of the same setting: it is two coherent
     * stacks separated by a substrate thick enough to be incoherent, which is TF_PATH_INCOHERENT
     * and is not built. An enum with a value that refuses would promise something the engine
     * cannot do; two booleans that mean what they say do not.
     *
     * The caller states the media as light meets them. A coating on glass, illuminated through the
     * glass, is index_incident = glass, index_exit = air, reversed = 1. */
    const int32_t*    reversed;
} tf_configurations;

typedef struct tf_problem
{
    int32_t n_layers;
    int32_t n_materials;
    int32_t n_wavelengths;
    int32_t n_points;

    const double*     layer_thickness;      /* [n_layers] */
    const int32_t*    layer_material;       /* [n_layers] -> material index */
    const int32_t*    layer_is_incoherent;  /* [n_layers] non-zero marks a massive / incoherent
                                             * layer, which breaks the coherent chain: intensities
                                             * multiply there, not amplitudes. May be NULL, which
                                             * means every layer is coherent. */

    const tf_complex* material_index;    /* [n_materials * n_wavelengths], material-major:
                                          * material_index[m * n_wavelengths + w] */
    const double*     wavelength;        /* [n_wavelengths] */

    const int32_t*    point_wavelength;  /* [n_points] -> wavelength index */
    const double*     point_angle;       /* [n_points] radians, in the incident medium */

    /* THE ONE-CONFIGURATION FORM. Both NULL when `configurations` is given; both required when
     * it is not. See tf_configurations for why the two do not mix. */
    const tf_complex* index_incident;    /* [n_wavelengths] */
    const tf_complex* index_exit;        /* [n_wavelengths] */

    /* Many evaluation contexts over one stack. NULL means one implicit configuration described by
     * the two fields above. */
    const tf_configurations* configurations;
    const int32_t*    point_configuration; /* [n_points] -> configuration. NULL in the one-
                                            * configuration form; required otherwise */

    /* dn/dlambda FOR EVERY MEDIUM, in units of index per nanometre. Required by
     * tf_evaluate_wavelength_derivative; ignored entirely by tf_evaluate and
     * tf_evaluate_jacobian.
     *
     * THESE MAY NOT BE NULL WHEN A WAVELENGTH DERIVATIVE IS ASKED FOR, and that is deliberate.
     * material_index is a table of values, so the engine has no way to differentiate it; the
     * caller owns the material model and is the only one that can. Letting NULL mean "zero" would
     * make a missing dispersion model indistinguishable from a genuinely non-dispersive medium,
     * and dispersion is the DOMINANT term in group delay for a real coating - the wrong answer
     * would be smooth, plausible and wrong by a factor. A medium that really is non-dispersive is
     * declared with an array of zeros, which costs nothing and cannot be an oversight.
     *
     * The engine differentiates nothing numerically. It propagates exactly what it is handed. */
    const tf_complex* material_dindex_dwavelength;   /* [n_materials * n_wavelengths] */
    const tf_complex* dindex_incident_dwavelength;   /* [n_wavelengths] */
    const tf_complex* dindex_exit_dwavelength;       /* [n_wavelengths] */

    /* FILM STRESS PER MATERIAL, [n_materials]. Units are the caller's and so is the sign; the
     * engine only sums sigma_m * d_j, so whatever convention goes in comes out. (The usual one is
     * MPa with compressive negative, which makes a stress-balanced design a target of zero and
     * lets a tensile layer cancel a compressive one - the thing a designer is actually doing.)
     *
     * NULL IS NOT ZERO. A STRESS operand against a NULL table refuses with TF_NO_STRESS_DATA
     * rather than scoring zero, for the same reason the dn/dlambda arrays refuse: a missing
     * measurement and a genuinely stress-free material must not produce the same number. Silence
     * here is worse than elsewhere, because zero is a PLAUSIBLE stress and it is also exactly the
     * target a stress-balanced design aims at - a missing table would read as a design that had
     * already met its specification. A material that really carries no stress is declared with a
     * zero, which costs one double and cannot be an oversight. */
    const double*     material_stress;               /* [n_materials] */

    /* WHICH INDEX VALUES THE CALLER MADE UP. Same shape and order as their unmarked twins;
     * non-zero marks an entry produced by extrapolation rather than measurement. All NULL, the
     * default, means every value is measured.
     *
     * WHY THE ENGINE CARRIES PROVENANCE IT NEVER USES ARITHMETICALLY. The specification is
     * explicit that thermal-band operands need optical constants out to 50 um "which most optical
     * constant databases do not carry", and that the behaviour when data is missing must be
     * declared: flag the operand or refuse, "but never extrapolate silently".
     *
     * The engine cannot extrapolate - material_index is a table and M3.1 keeps it that way, so
     * whoever synthesised the value is the only one who knows it was synthesised. But the engine
     * is the only one that knows WHICH OPERAND ended up resting on it, because that depends on the
     * points an operand spans, the configuration each point uses and the materials the stack is
     * made of. Neither side can answer alone. So the caller marks the data, the engine propagates
     * the mark to the operands it reaches, and "silently" becomes impossible by construction
     * rather than by discipline.
     *
     * The marks cost nothing when absent and are never read arithmetically: an extrapolated value
     * is used exactly as a measured one. This says only that somebody should not believe the
     * fourth digit. */
    const int32_t*    material_index_is_extrapolated;  /* [n_materials * n_wavelengths] */
    const int32_t*    index_incident_is_extrapolated;  /* [n_wavelengths] */
    const int32_t*    index_exit_is_extrapolated;      /* [n_wavelengths] */

    /* THE MONTE CARLO TRIAL AXIS - the fifth axis of the evaluation space, and the only one that
     * is not a property of a POINT.
     *
     * Wavelength, angle, polarisation and configuration all describe what is asked of ONE stack,
     * which is why they live in the point table. A trial describes WHICH STACK, so putting it in
     * the point table would multiply every point, every span and every plan buffer by the trial
     * count and would make layer_thickness a per-point quantity, which is nonsense. It sits here
     * instead, outside the point table, and the point table is untouched by it.
     *
     * A TRIAL IS THIS tf_problem WITH TWO POINTERS SWAPPED. That is the whole mechanism: trial t
     * is evaluated by pointing layer_thickness at trial_layer_thickness + t*n_layers and
     * material_index at trial_material_index + t*n_materials*n_wavelengths, and running the
     * solver that already exists, unmodified. Nothing in the characteristic-matrix code knows
     * trials exist. That is deliberate on two counts: the solver cannot drift away from the
     * nominal one because it IS the nominal one, and the CUDA port stays a port - one trial per
     * block, the same kernel, a different base pointer.
     *
     * n_trials = 0 means there is no trial axis. Any other value means there are exactly that
     * many perturbed stacks; an array left NULL means that quantity is not perturbed and every
     * trial sees the nominal one.
     *
     * THE CALLER OWNS THE ERROR MODEL, all of it. Per-layer sigma, tooling-factor offsets, index
     * error correlated across layers of one material, and the correlated error from optical
     * monitoring that the specification singles out as the one worth building properly - every
     * one of those is a rule for filling this table, and none of them is visible here. The engine
     * receives stacks. It does not know whether they were drawn independently or by simulating a
     * monitoring strategy, and it does not need to: that is precisely why a correlated model can
     * be added later without touching the ABI. */
    int32_t           n_trials;

    /* [n_trials * n_layers], TRIAL-MAJOR: trial_layer_thickness[t * n_layers + j]. NULL means no
     * thickness is perturbed. */
    const double*     trial_layer_thickness;

    /* [n_trials * n_materials * n_wavelengths], trial-major, each trial a complete copy of the
     * material_index table in its own layout. NULL means no index is perturbed.
     *
     * A WHOLE TABLE PER TRIAL RATHER THAN AN OFFSET PER MATERIAL, and it is worth saying why,
     * because the offset form is smaller and is the obvious thing to reach for. An offset
     * presumes the error model is additive in n and uniform across the band; a real index error
     * is a change in a DEPOSITION CONDITION, which moves the whole dispersion curve and does not
     * move it rigidly. Presuming otherwise here would build a model into the ABI and it would be
     * the wrong one. The table costs what it costs, the caller allocates it, and a caller with no
     * index error passes NULL and pays nothing. */
    const tf_complex* trial_material_index;

    /* THE SUBSTRATE'S BACK SURFACE (ABI 10). substrate_thickness is in nanometres; zero, the
     * default, means the exit medium is semi-infinite exactly as before, and everything below is
     * ignored.
     *
     * When it is positive the exit medium becomes a substrate of that thickness, thick enough to
     * be incoherent, with an uncoated back surface onto index_back. Light reflected at the back
     * adds in intensity, not amplitude, and the substrate's k attenuates each pass:
     *
     *     R = Rf + Tf^2 Rb a^2 / (1 - Rf' Rb a^2)        T = Tf Tb a / (1 - Rf' Rb a^2)
     *
     * Rf, Tf are the coating seen from the incident side, Rf' the coating seen from inside the
     * substrate, Rb and Tb the back surface, and a the single-pass intensity transmission of the
     * substrate. This changes every quantity built from R or T. It does NOT change the
     * amplitudes: Psi, Delta and the phases describe the coated surface alone whatever this
     * says, since an incoherent sum has no phase.
     *
     * index_back is [n_wavelengths]; NULL means the incident medium, the usual case of a plate in
     * air. Refused with a reversed configuration. tf_evaluate_wavelength_derivative and
     * tf_evaluate_index_derivative refuse it as well, with TF_NOT_IMPLEMENTED_INCOHERENT. */
    double            substrate_thickness;
    const tf_complex* index_back;

    /* d2n/dlambda2 FOR EVERY MEDIUM (ABI 11), in index per nanometre squared, the second
     * derivatives of the arrays material_dindex_dwavelength and its two siblings hold the first.
     * Read only by the GDD operands, which refuse with TF_NO_DISPERSION_DATA when they are NULL,
     * for the reason those do: a material's own dispersion is a large part of a coating's GDD,
     * and NULL read as zero would give a smooth, plausible and wrong answer. A non-dispersive
     * medium is declared with zeros. The one-configuration form only: a GDD operand with
     * configurations refuses. */
    const tf_complex* material_d2index_dwavelength2;   /* [n_materials * n_wavelengths] */
    const tf_complex* d2index_incident_dwavelength2;   /* [n_wavelengths] */
    const tf_complex* d2index_exit_dwavelength2;       /* [n_wavelengths] */

    /* d3n/dlambda3 FOR EVERY MEDIUM (ABI 12), in index per nanometre cubed, for the TOD operands
     * alone, on the same terms as the second derivatives above: NULL refuses, zeros mean a medium
     * whose third derivative really is zero. */
    const tf_complex* material_d3index_dwavelength3;   /* [n_materials * n_wavelengths] */
    const tf_complex* d3index_incident_dwavelength3;   /* [n_wavelengths] */
    const tf_complex* d3index_exit_dwavelength3;       /* [n_wavelengths] */
} tf_problem;


/* HOW Delta IS COMPARED WITH ITS TARGET.
 *
 * Delta = arg(rho) is an ANGLE, and an angle has no single right representative. atan2 returns
 * the principal value in (-pi, pi]; ellipsometers conventionally report [0, 2pi). Either is
 * defensible and neither can be assumed, so it is a setting rather than a decision taken here.
 *
 * THE REASON IT MATTERS IS THE RESIDUAL, not the display. A model at -179 degrees and a target at
 * +179 degrees are two degrees apart, but subtracted they are 358 degrees apart. An optimiser
 * handed that residual will drive hard in the wrong direction across the whole width of the
 * branch cut, and the merit will jump discontinuously as the design crosses it. SHORTEST_ARC is
 * the option that removes the problem rather than relocating it, and it is the one to use unless
 * a target really is expressed on a fixed interval.
 *
 * THE DERIVATIVE IS THE SAME UNDER ALL THREE. Every one of these maps is locally the identity -
 * adding a multiple of 2 pi changes the value, never the slope - so d(Delta)/dx is d(arg rho)/dx
 * whichever is chosen, and the Jacobian needs no branch at all. The only exception is the measure
 * zero set exactly on the cut, where the value is discontinuous and the derivative is undefined
 * in any convention.
 *
 * Applies to TF_TARGET_R_DELTA and TF_TARGET_T_DELTA only. cos(Delta) has no branch-cut problem
 * by construction and ignores this entirely. */
typedef enum tf_delta_convention
{
    /* atan2's principal value, (-pi, pi], compared directly with the target. */
    TF_DELTA_PRINCIPAL     = 0,

    /* [0, 2 pi), the usual ellipsometric reporting interval, compared directly. */
    TF_DELTA_POSITIVE      = 1,

    /* The value is principal, but the RESIDUAL is the shortest signed arc from target to value,
     * in (-pi, pi]. Continuous everywhere except where the two are exactly antipodal, which is
     * the farthest a target can be from the answer rather than the nearest. */
    TF_DELTA_SHORTEST_ARC  = 2
} tf_delta_convention;



/* HOW THE POINTS OF AN OPERAND COMBINE.
 *
 * Today an operand is one point. Every band-, shape- or extremum-valued operand in the
 * specification is many: RAVG integrates over a band, RIPPV takes a peak-to-valley across one,
 * TMAX is an extreme within one. The span says WHICH points; this says how they reduce.
 *
 * THE PER-POINT WEIGHT IS THE WHOLE OF SPECTRAL WEIGHTING. A flat band average, a luminous
 * average against V(lambda), a solar average against AM1.5, a tristimulus integral against the
 * CIE observer and a cone integral with sin(theta) d(theta) solid-angle weighting are ONE
 * operation with different numbers in point_weight. That is why the standards live in a managed
 * library and never in this engine: nothing here has to be kept current with a CIE revision. */
/* WHAT A VALUE IS, WHEN IT IS NOT A MEASUREMENT. Read through tf_output.operand_flag.
 *
 * A CROSSING is the one reduction that can fail to find what it was asked for, and refusing the
 * whole evaluation over it would stop a run at the moment the optimiser most needs a number - a
 * design early in a run routinely has no edge yet. So it returns a defined substitute and says so,
 * which is the absorption clamp's pattern rather than a hole in rule 5: the clamp too substitutes
 * a defined value for one that would overflow, and reports how often it fired. */
#define TF_FLAG_EXTRAPOLATED 1   /* the level is not attained in the span; the value is a penalty */
#define TF_FLAG_AMBIGUOUS    2   /* more than one crossing existed; one was chosen */
/* THE INDEX ITSELF WAS SYNTHESISED, which is a different statement from the three below. Those
 * describe what happened to a CROSSING inside a spectrum that was itself trustworthy; this one
 * says the spectrum rests on optical constants nobody measured. A caller that conflated them
 * would read "extrapolated" on a thermal-emittance operand and think the band edge had run off
 * the end of its span. They are separate bits because they are separate facts. */
#define TF_FLAG_SYNTHETIC_INDEX 8

#define TF_FLAG_PINNED       4   /* the extrapolation ran past its guard and was clamped, so the

                                  * value no longer depends on the design and its gradient is 0 */

typedef enum tf_reduction
{
    /* Weighted sum, and therefore every integral. A band AVERAGE is this with weights that sum
     * to one - normalisation is the caller's, because only the caller knows whether it wanted a
     * mean or a total. */
    TF_REDUCE_SUM     = 0,

    /* Extremes. THESE IGNORE point_weight: a quadrature weight has no meaning for a maximum, and
     * pretending otherwise would silently return max(w*x) for an operand that means max(x). */
    TF_REDUCE_MAX     = 1,
    TF_REDUCE_MIN     = 2,
    TF_REDUCE_PTV     = 3,   /* max - min, the peak-to-valley ripple */

    /* Weighted RMS about the weighted mean - ripple that is not dominated by one outlier. */
    TF_REDUCE_RMS_DEV = 4,

    /* Crossing extraction, for the edge and bandwidth operands. Reserved, and refused: its value
     * comes from root-finding rather than arithmetic and its derivative from the implicit function
     * theorem, which is a different mechanism from everything above. It also carries two
     * unsettled policy questions - what a missing crossing returns, and which crossing is meant
     * when there are several. See TF_NOT_IMPLEMENTED_REDUCTION. */
    TF_REDUCE_CROSSING = 5
} tf_reduction;

/* THE NON-SMOOTHNESS OF AN EXTREMUM, AND WHAT TO DO ABOUT IT.
 *
 * max() is not differentiable where the argmax moves from one point to the next: the derivative
 * jumps from one point to another as the design changes. An optimiser can chatter on that. So
 * reduction_param[0] carries a SHARPNESS: zero means the true extremum, and any positive value
 * selects the smooth form
 *
 *     smoothmax(x) = (1/beta) log sum_p exp(beta x_p)
 *
 * which tends to the true max as beta grows and spreads the derivative over the near-winners
 * below that. The specification asks for exactly this - a smooth option for gradient stages and
 * the true extremum for reporting and minimax - and it is a per-operand choice because a design
 * may want both of the same quantity.
 *
 * The scale matters: beta has units of 1/value, so beta = 1000 on a reflectance of order 0.01 is
 * a sharp maximum while beta = 1 is nearly a mean. */

/* HOW AN OPERAND'S VALUE IS COMPARED WITH ITS TARGET.
 *
 * ALMOST NO COATING SPECIFICATION IS AN EQUALITY. They read "R below 0.5% across the band" and
 * "OD at least 6 in the block" - Chebyshev statements, not least-squares ones. Expressing those as
 * equalities is what makes an optimiser fight satisfied requirements: an R = 0 target keeps pulling
 * at a surface already at 0.2%, spending effort that some other target needed.
 *
 * A ONE-SIDED RESIDUAL IS EXACTLY ZERO WHEN SATISFIED, AND SO IS ITS DERIVATIVE. That is the
 * property worth having and it is why the derivative is defined here rather than left implicit: a
 * met constraint must exert NO force, not a small one. */
/* WHAT TO DO WITH AN OPERAND THAT RESTS ON AN INDEX VALUE NOBODY MEASURED.
 *
 * The specification offers exactly two acceptable answers and forbids a third: "extrapolate with a
 * declared model and flag the operand, or refuse to evaluate - but never extrapolate silently."
 * So this is the setting, and the option that does not appear in it is the one that matters.
 *
 * IT IS A SYSTEM SETTING AND NOT PER OPERAND, deliberately. Whether you are willing to believe
 * synthesised optical constants is a property of what the number is FOR - a feasibility sweep or a
 * production sign-off - not a property of one row in a table. A per-operand switch would let a
 * table refuse in one row and shrug in the next over the identical datum, which is not a
 * distinction anyone means to draw.
 *
 * FLAG IS ZERO so that a caller who has no extrapolated data, and never thinks about this, gets
 * the pre-existing behaviour exactly. That is safe here only because FLAG is not silent: with no
 * marks supplied nothing is flagged, and with marks supplied something always is. */
/* HOW ONE OPERAND COLLAPSES THE TRIAL AXIS.
 *
 * Per operand, not per table, because a table that scores yield also scores things that have
 * nothing to do with it: a total-thickness budget is a property of the design being built, not of
 * any one trial, and averaging it over a thousand perturbed stacks would answer a question nobody
 * asked. So each row says what it does with the axis, and most rows ignore it.
 *
 * THE COLLAPSE HAPPENS AFTER THE DAG, NOT BEFORE. Every operand - derived ones included - is
 * evaluated on trial t against trial t's stack, and only then is the axis reduced. A ratio of two
 * operands is therefore a ratio WITHIN one trial, which is the only reading that is statistically
 * meaningful: the two quantities are perturbed together because they were measured on the same
 * perturbed stack, and collapsing first would silently replace a correlated pair by an
 * independent one. It is also what makes a yield over a SET of specs expressible later - that is
 * an AND across operands inside a trial, which needs the axis still open when the DAG runs. */
typedef enum tf_trial_reduction
{
    /* The NOMINAL stack, and no trial is evaluated for this operand at all. The default, so a
     * table written before trials existed keeps every number it had. */
    TF_TRIAL_NOMINAL          = 0,

    /* WCASE. The worst trial, where worst means THE LARGEST RESIDUAL - which makes it obey the
     * operand's own comparison without being told. A reflectance held below a limit is worst at
     * its maximum, an optical density held above one is worst at its minimum, and an equality is
     * worst wherever it is furthest out. Defining it as "the largest value" would have been right
     * for the first of those and wrong for the second, silently.
     *
     * Its derivative is the derivative of the winning trial, which is a subgradient of a max and
     * is exactly the minimax inner loop. Ties take the first winner, the same choice the hard
     * maximum already makes over a span. */
    TF_TRIAL_WORST            = 1,

    /* The mean over trials. Differentiable, being linear, and the cheapest useful robustness
     * term: it pulls a design toward one whose AVERAGE manufactured performance is good, where
     * WORST pulls toward one whose floor is good. They are different designs and both are asked
     * for. */
    TF_TRIAL_MEAN             = 2,

    /* YIELD: the fraction of trials in which this operand MEETS ITS COMPARISON - residual exactly
     * zero, so the tolerance deadband and the one-sided comparisons all count correctly.
     *
     * ITS VALUE IS WELL DEFINED AND ITS GRADIENT IS NOT, and that is a statement about the
     * quantity rather than about this implementation. Counting trials is a step function of every
     * thickness: the count is unchanged by a small move and then jumps when one trial crosses its
     * limit, so the derivative is zero almost everywhere and undefined on a set of measure zero.
     * A zero row would be a lie an optimiser would believe. tf_evaluate_jacobian therefore
     * REFUSES with TF_TRIAL_GRADIENT_UNDEFINED rather than returning one - the same judgement
     * made for MINT's step in the needle map, and required by C0.7.
     *
     * The specification asks for exactly the two roles this supports: a refinement objective in a
     * final stage, and a selection criterion when ranking a global search population. Both read
     * the value. Neither needs the gradient. */
    TF_TRIAL_FRACTION_PASSING = 3
} tf_trial_reduction;

typedef enum tf_extrapolation_policy
{

    /* Evaluate, and set TF_FLAG_SYNTHETIC_INDEX on every operand that touched a marked value.
     * The number is returned because it is often the number you want - an emittance good to the
     * first digit still decides a design - and the flag is what stops it being mistaken for one
     * that is good to the fourth. */
    TF_EXTRAPOLATION_FLAG   = 0,

    /* Refuse the whole call with TF_EXTRAPOLATED_INDEX. tf_plan raises it, not tf_evaluate: the
     * marks and the points are both fixed at plan time, so the answer cannot change between one
     * evaluation and the next, and an optimiser must not discover this on iteration four hundred.
     * The refusal names the operand and the material in the plan report's diagnostic. */
    TF_EXTRAPOLATION_REFUSE = 1
} tf_extrapolation_policy;

typedef enum tf_comparison
{

    /* Evaluated but not scored. Reserved for an operand that exists only to feed another - a
     * tristimulus value that only a colour difference consumes. Nothing consumes one yet and
     * It produces a value in tf_output.value and no residual row; tf_plan_buffers.operand_residual
     * says which row an operand produced, or -1. A table where EVERY operand is NONE is refused
     * with TF_NO_SCORED_OPERAND - there would be nothing to optimise. */
    TF_COMPARE_NONE  = 0,

    TF_COMPARE_EQUAL = 1,   /* e = value - target                                  */
    TF_COMPARE_LE    = 2,   /* e = max(0, value - target)                          */
    TF_COMPARE_GE    = 3,   /* e = max(0, target - value)                          */
    TF_COMPARE_RANGE = 4    /* e = max(0, value - target_hi) + max(0, target - value) */
} tf_comparison;

/* The deadband, then the normalisation:
 *
 *     e        shrinks toward zero by `tolerance`, and is zero inside it
 *     residual = sqrt(weight) * e / scale
 *
 * WHY `scale` IS NOT FOLDED INTO `weight`. The merit is pinned to LensHH-LT as
 * sqrt(sum r^2 / sum w), and that denominator is what makes doubling every weight leave the merit
 * alone. A normalisation hidden inside the weight would enter the denominator too, so changing an
 * operand's UNITS would change the merit - the exact property the measured convention was chosen
 * to avoid. Kept separate, `scale` divides the error and never reaches the denominator: a weight
 * stays relative importance, and `scale` is the unit conversion that makes a phase target in
 * degrees comparable with a reflectance in fractions.
 *
 * AT THE BOUNDARY the derivative is taken as ZERO - the satisfied side. A constraint sitting
 * exactly on its limit exerts no force, and gains one the moment it is crossed. The alternative
 * would have a met constraint push at the design forever.
 */

/* What the optimiser is trying to hit.
 *
 * AN OPERAND, NOT A TARGET, and the rename is the first step of the evaluation model in
 * docs/evaluation-model-contract.md rather than a change of behaviour. The distinction it makes
 * room for: an operand is a QUANTITY, and a residual is a SCORED COMPARISON of one. Today every
 * operand is scored and the two are the same list, which is why nothing here has moved yet. They
 * stop being the same list as soon as an operand can exist to feed another - a tristimulus value
 * that only DELE consumes - and at that point `residual` is shorter than `value` and the mapping
 * between them is the plan's to report.
 *
 * That is also why the count is now `n_operands` and the plan reports `n_residuals`. Nothing is
 * gained by it today; what is gained is that no caller writes code that assumes they are equal.
 *
 *     residual_i = sqrt(weight_i) * (value_i - target_i)
 *     merit      = sqrt( sum residual_i^2 / sum weight_i )
 *
 * THAT IS LensHH-LT's CONVENTION, adopted so that the two programs mean the same thing by a
 * weight and by a merit. The root on the weight means merit is the weighted RMS of the errors,
 * and the division by the weight sum means doubling every weight leaves the merit alone - a
 * weight expresses relative importance and nothing else. Both were established by measuring
 * LensHH-LT rather than by reading it; its display code prints weight * error, and the Weight it
 * exposes is already a root. */
typedef struct tf_operands
{
    int32_t         n_operands;
    int32_t         delta_convention;  /* tf_delta_convention; 0 is the principal value */
    int32_t         extrapolation_policy; /* tf_extrapolation_policy; 0 flags and evaluates */
    /* [n_operands] tf_trial_reduction. NULL => TF_TRIAL_NOMINAL everywhere, which is the whole of
     * the pre-trial behaviour, so a table that never heard of trials is unchanged. */
    const int32_t*  trial_reduction;


    const int32_t*  code;    /* [n_operands] tf_target_code */
    const int32_t*  point;   /* [n_operands] -> point index */
    const double*   target;  /* [n_operands] the value being aimed at */
    const double*   weight;  /* [n_operands] */

    /* THE FOUR BELOW MAY ALL BE NULL, and each null means the value that reproduces an equality
     * target exactly. That is not a convenience: it is what makes the pre-comparison behaviour the
     * DEGENERATE CASE of this one rather than an approximation of it, so every existing test keeps
     * its numbers bit-for-bit. Each default is also the strictest reading available, so a caller
     * that forgets a field is never given something more permissive than it asked for. */
    const int32_t*  comparison; /* [n_operands] tf_comparison.   NULL => EQUAL everywhere */
    const double*   target_hi;  /* [n_operands] RANGE only.      NULL is an error IF any
                                 * comparison is RANGE - a range has no safe default bound */
    const double*   tolerance;  /* [n_operands] deadband, in operand units, >= 0. NULL => 0 */
    const double*   scale;      /* [n_operands] normalisation, > 0.             NULL => 1 */
    /* WHICH POINTS AN OPERAND READS, in flat CSR form - rule 2 compliant and unchanged by a port
     * to a kernel.
     *
     * TWO MODES, AND THEY DO NOT MIX. When point_count is NULL the operand list is POINTWISE and
     * `point` names one point each, exactly as before spans existed. When point_count is given,
     * every operand spans a run of point_index / point_weight, and `point` MUST be NULL - so
     * there is exactly one statement of which points an operand reads, and no way for two of them
     * to disagree. A pointwise operand in span mode is simply count 1.
     *
     * n_span is the length of point_index and point_weight. It is a count the caller has and
     * native cannot derive without trusting the spans it is meant to be validating. */
    int32_t         n_span;
    const int32_t*  reduction;     /* [n_operands] tf_reduction.  NULL => SUM everywhere */
    const int32_t*  point_first;   /* [n_operands] first slot in point_index */
    const int32_t*  point_count;   /* [n_operands] how many slots.  NULL => pointwise mode */
    const int32_t*  point_index;   /* [n_span] -> point index */
    const double*   point_weight;  /* [n_span] quadrature weight; unused by MAX, MIN and PTV */
    const double*   reduction_param; /* [n_operands * 2]. NULL => all zero.
                                      * [0] smooth-extremum sharpness, 0 = the true extremum
                                      * [1] reserved for CROSSING */

    /* OPERANDS THIS OPERAND CONSUMES. An operand reads POINTS OR OPERANDS, never both, and
     * tf_plan refuses a mixture rather than guessing which was meant.
     *
     * This is what makes the merit function a DAG rather than a list, and the reason is in the
     * specification: a colour difference is a function of three colorimetric operands, an edge
     * shift is a difference of two edges in different configurations, a shape factor is a ratio of
     * two bandwidths. None of those can be said with a flat table.
     *
     * n_input is the length of input_index. NULL input_count means no operand has inputs and the
     * table is flat, which is every design that existed before this. */
    int32_t         n_input;
    const int32_t*  input_first;   /* [n_operands] */
    const int32_t*  input_count;   /* [n_operands]. NULL => nothing is derived */
    const int32_t*  input_index;   /* [n_input] -> operand */
} tf_operands;

/* What the optimiser is allowed to move. */
typedef struct tf_variables
{
    int32_t         n_variables;
    const int32_t*  kind;         /* [n_variables] tf_variable_kind */
    const int32_t*  layer;        /* [n_variables] */
    const int32_t*  material;     /* [n_variables] */
    const int32_t*  coefficient;  /* [n_variables] */
} tf_variables;

/* What tf_plan found. Counts the caller will want to surface, and the one number this whole
 * contract exists to deliver — scratch_doubles.
 *
 * WHY THE CALLER IS NOT ASKED TO WORK IT OUT. The engine's working set is a cache whose shape is
 * an implementation detail: today (n_layers + 1) general matrices plus a per-layer derivative
 * record, per worker; tomorrow, when the structured derivative sweep lands, half that on a
 * lossless stack. A caller that computed the number itself would be pinned to this version's
 * internals, and would get TF_SCRATCH_TOO_SMALL from a plan that had just told it the answer. */
typedef struct tf_plan_report
{
    int32_t n_structured;        /* points taking the structured path */
    int32_t n_general;
    int32_t n_incoherent;        /* points that would need the incoherent path */
    int32_t n_unsupported;       /* points whose path is not implemented — non-zero means the
                                  * evaluators will refuse */
    int32_t n_variables_inert;   /* see variable_inert */

    /* HOW MANY OPERANDS PRODUCE A RESIDUAL. Today every one does, so this equals n_operands - and
     * it is reported anyway, because the moment an operand can be unscored (comparison NONE, for
     * an operand that exists only to feed a derived one) the caller cannot work it out without
     * re-deciding what the plan already decided. Sizing `residual` and `jacobian` from a count the
     * caller derived itself is the duplicated decision this contract exists to prevent. */
    int32_t n_residuals;
    int32_t n_operands_unscored;
    int32_t n_operand_levels;    /* 1 means nothing is derived */
    int32_t max_operand_span;    /* the widest span, in points. 1 means every operand is
                                  * pointwise, which is what the scratch is sized against */
    int32_t max_clamped_layers;  /* worst case over points. Each clamped layer multiplies the
                                  * accumulated matrix by about e^24 = 1.3e10, so beyond roughly
                                  * thirty of them in one stack the product overflows a double.
                                  * tf_plan raises TF_TOO_MANY_CLAMPED_LAYERS rather than let
                                  * that happen quietly. */
    int32_t n_points_one_polarisation;  /* points needing s or p but not both - the work the
                                        * polarisation mask saves */
    int64_t scratch_doubles;
} tf_plan_report;

/* Filled by tf_plan, read-only to everything afterwards. */
/* WHICH POLARISATIONS A POINT ACTUALLY NEEDS, as a bit mask.
 *
 * Rs and Ts want s alone. Rp and Tp want p alone. Ra, Ta and every ellipsometric target want
 * both, because they are built from the pair. Solving both regardless - which is what the first
 * implementation did - throws away half the work on a design targeting one polarisation, and that
 * is the common case at oblique incidence where s and p are specified separately.
 *
 * THE MASK IS DERIVED BY tf_plan FROM THE TARGETS, never supplied by the caller. A caller-supplied
 * mask is one more thing that can disagree with the operands it is supposed to describe, and the
 * disagreement would be silent: a point marked s-only whose residual asks for Rp. Derived, it
 * cannot be wrong. An amplitude that was not asked for is left as a quiet NaN, so that if the two
 * ever do come apart the residual is NaN rather than plausible. */
typedef enum tf_polarisation_need
{
    TF_NEED_S = 1,
    TF_NEED_P = 2
} tf_polarisation_need;

typedef struct tf_plan_buffers
{
    int32_t*         point_path;     /* [n_points] tf_path */
    int32_t*         point_order;    /* [n_points] points sorted by path, so that a warp does not
                                      * diverge once this is a kernel */
    int32_t*         variable_inert; /* [n_variables] non-zero marks a variable whose derivative
                                      * is identically zero at EVERY point that any residual
                                      * depends on — a layer clamped opaque everywhere it is
                                      * looked at.
                                      *
                                      * This is not a curiosity. Such a variable puts a zero
                                      * column in the Jacobian, which makes the normal equations
                                      * singular and can stall Levenberg-Marquardt outright. The
                                      * caller is told so it can drop the variable rather than
                                      * discover it as a failure to converge. */
    /* [n_operands] which residual row an operand produces, or -1 if it is unscored. The caller
     * needs it to put a number back beside the row a user typed, and n_residuals < n_operands as
     * soon as anything is unscored. */
    int32_t*         operand_residual;
    int32_t*         operand_order;       /* [n_operands] topological evaluation order */
    int32_t*         operand_level;       /* [n_operands] 0 for a point operand, else 1 + the
                                           * deepest input. Derived, and reported. */

    int32_t*         point_span_first;    /* [n_points] first entry in the transpose */
    int32_t*         point_span_count;    /* [n_points] how many operand slots read this point */
    int32_t*         point_span_operand;  /* [n_span] -> operand, grouped by point */
    int32_t*         point_span_slot;     /* [n_span] -> slot in point_index / point_weight */

    int32_t*         point_polarisation; /* [n_points] tf_polarisation_need bits, derived
                                          * from the operands - see the enum above */
    tf_plan_report*  report;         /* [1] */
    int32_t*         status;         /* [1] */
} tf_plan_buffers;

/* Allocated by the caller AFTER tf_plan, to the size tf_plan asked for. The internal layout is
 * native's business and is documented beside the code that carves it up; only the size is part of
 * this contract. */
typedef struct tf_workspace
{
    double*  scratch;
    int64_t  scratch_doubles;
} tf_workspace;

/* Written by the evaluators. Untouched if the status is non-zero. */
typedef struct tf_output
{
    double*   residual;  /* [n_residuals]  sqrt(w_i) * (value_i - target_i) */
    double*   jacobian;  /* [n_residuals * n_variables] COLUMN-MAJOR:
                          *     jacobian[v * n_residuals + r] = d(residual_r) / d(variable_v)
                          * Variable-major for two independent reasons: one thread per residual
                          * then writes consecutive addresses, which is one memory transaction
                          * rather than thirty-two; and it is the order MINPACK and its
                          * descendants already take. NULL for tf_evaluate. */
    /* [n_operands] EVERY operand's value, scored or not.
     *
     * Not a convenience. An unscored operand exists only to feed another, and its value is the
     * only thing it produces - without this it would compute something no caller could read. It
     * is also the merit function table a user debugs with, and the raw figure the specification
     * asks for alongside the normalised contribution. */
    double*   value;

    /* [n_operands] WHY A VALUE IS NOT A MEASUREMENT, when it is not one.
     *
     * Bit 0 says a CROSSING found no crossing and the value is an extrapolated penalty; bit 1 that
     * several crossings existed and one was chosen; bit 2 that the extrapolation hit its guard and
     * the value is pinned. BIT 3 IS DIFFERENT IN KIND from the other three: they describe what
     * happened to a crossing inside a spectrum that was itself trustworthy, while bit 3 says the
     * spectrum rests on optical constants nobody measured. May be NULL if the caller does not
     * want them.
     *
     * A DERIVED OPERAND INHERITS THE BITS OF ITS INPUTS, so a ratio of a measured quantity over a
     * synthesised one is marked even though neither of ITS own points is.
     *
     * A PER-ELEMENT FLAG IS EASY TO IGNORE, so n_operands_flagged in tf_plan_report's companion
     * below counts them. That is the absorption clamp's precedent: the clamp substitutes a defined
     * value AND reports max_clamped_layers, because a caller that never reads the array still sees
     * the scalar. */
    int32_t*  operand_flag;
    int32_t*  n_operands_flagged;   /* [1] how many of the above are non-zero. May be NULL. */

    /* [n_trials * n_operands] TRIAL-MAJOR, every operand's value on every trial, before the trial
     * axis is collapsed. May be NULL, and usually is.
     *
     * WITHOUT THIS THE SPECIFICATION'S YIELD IS NOT REACHABLE, which is the reason it exists.
     * TF_TRIAL_FRACTION_PASSING gives the fraction of trials in which ONE operand meets its
     * comparison - a marginal. The specification asks for "the fraction of trials meeting a named
     * spec SET", which is a joint: the trials in which every member passes AT ONCE. Those two are
     * equal only if the operands fail independently, and on one perturbed stack they emphatically
     * do not - a thickness error that breaks the band edge usually breaks the ripple with it.
     * Multiplying marginals would be a plausible, cheap and wrong number.
     *
     * The joint cannot be computed from anything smaller than the per-trial table, so the engine
     * hands over the table and lets the caller AND across it. The alternative was a named
     * spec-set object in the ABI, which is M3.3's argument in a different costume and belongs
     * above the ABI for the same reason. */
    double*   trial_value;


    double*   merit;     /* [1] sqrt( sum residual^2 / sum weight ) - LensHH-LT's weighted RMS,
                          * defined beside tf_operands and enforced by test_evaluate. A
                          * convenience, not the primary result: the residual vector is.
                          *
                          * NOT the plain sum of squares. This comment said so until 2026-09-18,
                          * which was wrong in the one place it costs a user something - merit is
                          * the number they read. The needle map returns d(sum r^2)/d(eps) and says
                          * so explicitly, which is a different quantity on purpose. */
    int32_t*  status;    /* [1] */
} tf_output;

/* The element counts of the plan buffers. Returned by tf_query_plan_sizes so the formulae live in
 * exactly one place and the two sides cannot drift. */
typedef struct tf_plan_sizes
{
    int64_t point_path_count;
    int64_t point_order_count;
    int64_t variable_inert_count;
    int64_t point_polarisation_count;

    /* THE TRANSPOSE OF THE OPERAND SPANS: for each point, which operand slots read it.
     *
     * The spans run operand -> points; the sweeps run point-major, one prefix chain per point, so
     * they need points -> operand slots. Scanning every operand at every point would be the
     * product of two numbers a dense evaluation grid makes large, so tf_plan inverts it once - by
     * the same counting sort that produces point_order, and for the same reason the polarisation
     * mask is derived rather than supplied: a caller-built inverse index is one more thing that
     * can silently disagree with the forward one. */
    int64_t point_span_first_count;   /* [n_points] */
    int64_t point_span_count_count;   /* [n_points] */
    int64_t point_span_operand_count; /* [n_span] */
    int64_t point_span_slot_count;    /* [n_span] */
    int64_t operand_residual_count;   /* [n_operands] */
    int64_t operand_order_count;      /* [n_operands] */
    int64_t operand_level_count;      /* [n_operands] */
} tf_plan_sizes;

/* ---------------------------------------------------------------------------------------------
 * The calls, in the order they must be made
 *
 *   1. caller  tf_query_plan_sizes   -> how big the plan buffers are
 *   2. caller  allocates them
 *   3. native  tf_plan               -> path per point, point order, AND the scratch size
 *   4. caller  allocates scratch
 *   5. native  tf_evaluate / tf_evaluate_jacobian    — the inner loop, allocating nothing
 *
 * Steps 1 to 4 run once per optimisation. Step 5 runs many thousands of times and must not
 * allocate, must not branch on anything the plan did not already decide, and must not touch
 * anything it was not handed.
 * ------------------------------------------------------------------------------------------- */

/* Step 1. Pure function of the counts. */
TF_API tf_status tf_query_plan_sizes(int32_t n_points, int32_t n_variables, int32_t n_span,
                                     int32_t n_operands, tf_plan_sizes* out_sizes);

/* Step 3. Decides the arithmetic each point will use, orders the points by it, and reports the
 * scratch the evaluators will need.
 *
 * This is native rather than managed on purpose. The decision depends on optical conditions —
 * whether every material is lossless at this wavelength, whether any layer is beyond the critical
 * angle at this angle. If the managed side decided it and the native side assumed something else,
 * the disagreement would be silent and would surface as a wrong derivative. There is one
 * implementation and it lives beside the code that depends on it. */
TF_API tf_status tf_plan(const tf_problem*   problem,
                         const tf_operands*   operands,
                         const tf_variables* variables,
                         int32_t             n_workers,
                         tf_plan_buffers*    plan);

/* Step 5a. Residuals and merit, no derivatives. */
TF_API tf_status tf_evaluate(const tf_problem*      problem,
                             const tf_operands*      operands,
                             const tf_plan_buffers* plan,
                             tf_workspace*          workspace,
                             tf_output*             out);

/* Step 5b. Residuals, merit, and the Jacobian of the residuals with respect to the active
 * variables.
 *
 * Returns J rather than the gradient because Levenberg-Marquardt and Gauss-Newton need J itself;
 * the scalar and the gradient (2 J^T r) are each one reduction away from what is returned, and
 * the reverse is not true.
 *
 * WHAT IS IMPLEMENTED. Layer-thickness variables, analytically, for all ten target families. A
 * material-coefficient variable is REFUSED with TF_NOT_IMPLEMENTED_VARIABLE rather than given a
 * zero column: differentiating one needs a dispersion model, and material_index is still a table
 * of values rather than a function of coefficients. That is where automatic differentiation will
 * go when the models arrive — once per material per wavelength, outside the inner loop. The
 * optics stay analytic, because a hand-derivative of |z|^2, |z| or arg(z) is two lines and runs
 * at the innermost level.
 *
 * There is NO finite-difference fallback, deliberately. A slow answer that is silently a few
 * digits short is worse than a refusal, because an optimiser cannot tell the difference and the
 * user cannot either.
 *
 * The cache that makes them cheap is a forward sweep of prefix products and a backward sweep of
 * suffix products carried in registers, so that
 *
 *     dM/dt_j = prefix[j] * (dM_j/dt_j) * suffix[j]
 *
 * costs two 2x2 products per layer and the whole Jacobian is O(n) matrix products rather than
 * O(n^2). That cache is what workspace.scratch holds, and tf_plan is the only thing that says
 * how big it must be.
 *
 * The derivative sweep uses general complex arithmetic on every path, including points the plan
 * marked structured. Correct, and four times slower than it needs to be on a lossless stack; the
 * structured sweep is the next optimisation and tf_jacobian.cpp says so at the top. */
TF_API tf_status tf_evaluate_jacobian(const tf_problem*      problem,
                                      const tf_operands*      operands,
                                      const tf_variables*    variables,
                                      const tf_plan_buffers* plan,
                                      tf_workspace*          workspace,
                                      tf_output*             out);


/* ---------------------------------------------------------------------------------------------
 * The wavelength derivative
 *
 * WHY IT IS A SEPARATE CALL AND NOT A COLUMN OF THE JACOBIAN. Wavelength is not a variable. The
 * optimiser never moves it; the merit function READS it. d(value)/d(lambda) is wanted for two
 * quite different reasons, and neither is optimisation:
 *
 *   Edge, crossing and bandwidth operands  -  LEDGE, SEDGE, SLOPE, CWL, BW - locate a wavelength
 *   where the spectrum crosses a level, and differentiate it with respect to LAYER THICKNESS by
 *   the implicit function theorem:
 *
 *       d(lambda_e)/d(d_i)  =  - (dT/d(d_i)) / (dT/d(lambda))
 *
 *   so the wavelength slope is the denominator of a thickness derivative. It is needed in the
 *   FIRST phase of the program, not alongside dispersion control.
 *
 *   Group delay and its higher orders  -  GD = d(phi)/d(omega), GDD, TOD (tf_dispersion.cpp).
 *
 * WHY IT IS ANALYTIC. Finite-differencing phase on a wavelength grid loses roughly half the
 * significant digits per derivative order, which puts TOD beyond double precision at practical
 * grid spacings, and it couples the answer to the grid in a way no user can debug. So d/d(lambda)
 * is propagated through the characteristic matrix alongside the matrix itself.
 *
 * FORWARD ACCUMULATION, not the reverse sweep the Jacobian uses, and the difference is structural:
 * there is exactly ONE parameter here. Carrying the pair
 *
 *     P_{k+1} = P_k * M_k                          (the product so far)
 *     A_{k+1} = A_k * M_k  +  P_k * dM_k/dlambda   (its derivative, by the product rule)
 *
 * gives the whole derivative in ONE forward pass with NO STORAGE AT ALL - three matrix products
 * per layer and not a byte of scratch. Reverse mode wins for thickness because there are n
 * parameters; forward mode wins here because there is one.
 * ------------------------------------------------------------------------------------------- */

/* The speed of light in the units this ABI uses: nanometres per femtosecond. Exact by definition
 * of the metre. */
#define TF_C_NM_PER_FS 299.792458

/* Convert a d/d(lambda) into a d/d(omega).
 *
 *     omega = 2 pi c / lambda    =>    d/d(omega) = -(lambda^2 / (2 pi c)) d/d(lambda)
 *
 * Given lambda in nanometres this returns a factor in nanometre-femtoseconds, so a phase slope in
 * radians per nanometre becomes a group delay in FEMTOSECONDS with no constant for the caller to
 * supply and get wrong. The sign is real: omega decreases as lambda increases. */
TF_HD static inline double tf_dlambda_to_domega(double lambda_nm)
{
    return -(lambda_nm * lambda_nm) / (6.283185307179586476925286766559 * TF_C_NM_PER_FS);
}

/* Written by tf_evaluate_wavelength_derivative. Both arrays are [n_residuals].
 *
 * THE VALUE COMES BACK TOO, and not as a convenience: the implicit function theorem needs the
 * value and the slope at the same point, and computing them in one pass is the entire reason this
 * call exists rather than two. */
typedef struct tf_spectral_output
{
    double*  value;               /* [n_operands] every operand's value */
    double*  dvalue_dwavelength;  /* [n_operands] its slope, per nanometre. Only a POINTWISE
                                   * operand has one - see tf_spectral.cpp. */
    int32_t* status;
} tf_spectral_output;

/* d(value)/d(lambda) for every residual, analytically.
 *
 * Needs no workspace - see the note above on forward accumulation - but takes one so the call
 * shape matches its siblings and so a future structured path has somewhere to put a cache.
 *
 * Refuses with TF_NULL_POINTER if the dn/dlambda arrays are absent. See tf_problem. */
TF_API tf_status tf_evaluate_wavelength_derivative(const tf_problem*      problem,
                                                   const tf_operands*      operands,
                                                   const tf_plan_buffers* plan,
                                                   tf_workspace*          workspace,
                                                   tf_spectral_output*    out);


/* ---------------------------------------------------------------------------------------------
 * The needle perturbation function
 *
 * WHAT IT IS. The derivative of the merit with respect to inserting an infinitesimally thin layer
 * of a NEW material at depth z in the stack. Scanned across z it is the needle map, and its
 * negative minima are where inserting a layer would improve the design. Needle optimisation and
 * gradual evolution are built on it, and those are the algorithms that make thin film SYNTHESIS
 * work rather than merely refinement.
 *
 * WHY IT COSTS ALMOST NOTHING. A homogeneous layer's phase adds, so the layer splits exactly:
 *
 *     M_j(d) = M_j(t) M_j(d - t)
 *
 * and inserting thickness eps of material m at depth t inside layer j gives
 *
 *     dM/d(eps)|_0  =  prefix[j] M_j(t)  G_m  M_j(d-t) S        G_m = (2 pi kappa_m / lambda) *
 *                      \________ L ______/     \____ R ____/          [[0, i/eta_m], [i eta_m, 0]]
 *
 * G_m is dM_m/d(eps) at zero thickness - the generator of the insertion - and depends only on the
 * material and the point, never on the depth. The backward sweep of the thickness Jacobian already
 * holds prefix[j] and the running suffix S AT THE SAME MOMENT, so L and R cost one layer matrix
 * each and the whole map costs no extra storage whatever. That was checked before this was written
 * rather than assumed.
 *
 * P(z) IS CONTINUOUS ACROSS A LAYER BOUNDARY, which is worth knowing because it means the map has
 * no artificial steps: at t = d_j, L = prefix[j+1] and R = suffix[j], exactly what the start of
 * layer j+1 gives.
 *
 * WHAT IS RETURNED. d(sum of squared residuals)/d(eps), not d(merit)/d(eps). The sum of squares
 * has no singularity at a perfect design, where merit = 0 and its derivative is 0/0. The two have
 * the same sign and the same ranking, which is all a needle scan uses, and the conversion is
 * exact:
 *
 *     d(merit)/d(eps) = d(sumsq)/d(eps) / (2 * merit * sum of weights)
 *
 * Units are per nanometre, since eps is a thickness.
 *
 * INSERTING A MATERIAL INTO A LAYER ALREADY MADE OF IT is not a degenerate case - it is the exact
 * consistency check between this and the thickness Jacobian, because adding eps of the same
 * material anywhere inside layer j is the same thing as making layer j thicker by eps. The test
 * suite uses it as an oracle.
 * ------------------------------------------------------------------------------------------- */

/* What to compute a needle map over. Depths are measured in nanometres from the FRONT of the
 * stack (the incident side), must be sorted ascending, and must lie within it.
 *
 * THE CALLER CHOOSES THE DEPTHS rather than asking for a sample count, because the caller is the
 * one that has to allocate the result (rule 1) and because a sampling policy - uniform per layer,
 * uniform in optical thickness, refined where the map is steep - belongs above this boundary. */
typedef struct tf_needle_request
{
    int32_t         n_depths;
    const double*   depth;              /* [n_depths] nm from the front, ascending */
    int32_t         n_candidates;
    const int32_t*  candidate_material; /* [n_candidates] -> material index */
} tf_needle_request;

typedef struct tf_needle_output
{
    double*  residual;           /* [n_residuals], as tf_evaluate would report them */
    double*  dsumsq_dinsertion;  /* [n_candidates * n_depths], CANDIDATE-MAJOR:
                                  *     dsumsq_dinsertion[c * n_depths + i] */
    int32_t* status;
} tf_needle_output;

/* The needle map.
 *
 * Refuses with TF_NOT_IMPLEMENTED_NEEDLE_OPAQUE if any layer is thick enough to clamp. Inside a
 * clamped layer the model cannot answer honestly: the front of such a layer is still visible and
 * a needle there genuinely matters, but the clamped product cannot represent it, so a number here
 * would be an artifact of the clamp rather than physics. Stack truncation at the first opaque
 * layer is the fix, and it is recorded in the scope ledger as S6.3. */
TF_API tf_status tf_evaluate_needle(const tf_problem*        problem,
                                    const tf_operands*        operands,
                                    const tf_plan_buffers*   plan,
                                    const tf_needle_request* request,
                                    tf_workspace*            workspace,
                                    tf_needle_output*        out);


/* ---------------------------------------------------------------------------------------------
 * The index derivative — and the seam it sits on
 *
 * d(value)/dn and d(value)/dk for every MATERIAL, at the wavelength each residual is evaluated at.
 *
 * THIS IS THE ENGINE HALF OF "ANALYTIC THROUGH THE OPTICS, AUTOMATIC THROUGH THE MATERIALS". When
 * a dispersion coefficient c is an optimisation variable, the chain is
 *
 *     d(value)/dc  =  d(value)/dn * dn/dc  +  d(value)/dk * dk/dc
 *                     \_____ here _____/     \___ the material model's job ___/
 *
 * The left factor is optics and is derived by hand, once, below. The right factor is whatever
 * Sellmeier, Cauchy or oscillator form the user chose, and is where automatic differentiation
 * earns its place - many shapes, user-extensible, evaluated once per material per wavelength
 * rather than once per layer per interface. Splitting the chain here is what lets the two halves
 * be written in the styles that suit them.
 *
 * It is also the whole derivative needed for REVERSE ENGINEERING, where n and k themselves are the
 * variables being fitted to measured R, T or Psi/Delta.
 *
 * TWO REAL DERIVATIVES, NOT ONE COMPLEX ONE. The amplitudes are holomorphic in the index, but a
 * target value is not - it passes through |r|^2 and arg(r) - so n and k are two independent real
 * parameters. Internally one complex dr/dN is formed and then resolved:
 *
 *     dN/dn = 1        dN/dk = -i        (the ABI takes n + ik; the engine works in n - ik)
 *
 * so both fall out of the same sweep at no extra cost.
 *
 * A MATERIAL, NOT A LAYER. Several layers may share one material, and varying that material moves
 * all of them at once - which is exactly what a real deposition does and what a fit must assume.
 * The contributions are summed over every layer of that material.
 *
 * WEIGHTS ARE NOT APPLIED. These are d(value)/dn, not d(residual)/dn. A Jacobian column is
 * sqrt(weight) times what is returned here; leaving the weight off keeps the value usable in the
 * dispersion chain above, where a weight has no business.
 * ------------------------------------------------------------------------------------------- */

typedef struct tf_index_output
{
    double*  value;       /* [n_operands] every operand's value, scored or not - the same array
                           * tf_output.value carries, and operand-indexed for the same reason: an
                           * unscored operand exists to feed another and its value is the only
                           * thing it produces. */
    double*  dvalue_dn;   /* [n_materials * n_operands] MATERIAL-MAJOR:
                           *     dvalue_dn[m * n_operands + o]
                           * is d(value_o)/dn for material m. For a POINTWISE operand that is at
                           * the wavelength of its point; for a SPAN it is the reduction's chain
                           * over every wavelength the operand covers, which is exactly what a
                           * dispersion fit against a band average needs.
                           * A material in no layer gets exact zeros. */
    double*  dvalue_dk;   /* [n_materials * n_operands] likewise, with respect to .im */
    int32_t* status;
} tf_index_output;

/* Refuses with TF_NOT_IMPLEMENTED_INCOHERENT on an unsupported path, as its siblings do. Unlike
 * the needle map this is defined on a clamped stack: an opaque layer's index still governs the
 * semi-infinite Fresnel coefficient it presents, so the derivative is real physics rather than an
 * artifact. The phase contribution of a clamped layer is dropped for the same reason it is dropped
 * in the thickness Jacobian. */
TF_API tf_status tf_evaluate_index_derivative(const tf_problem*      problem,
                                              const tf_operands*      operands,
                                              const tf_plan_buffers* plan,
                                              tf_workspace*          workspace,
                                              tf_index_output*       out);

/* Version of this ABI. Bumped whenever a struct layout, an enum value or a call signature
 * changes, so a managed wrapper built against an older header fails loudly instead of reading
 * fields that moved. */
#define TF_ABI_VERSION 12

TF_API int32_t tf_abi_version(void);

#ifdef __cplusplus
} /* extern "C" */
#endif

#endif /* THINFILM_H */
