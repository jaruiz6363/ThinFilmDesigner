// NativeMethods.cs — the eight exported calls, declared and not wrapped.
//
// EVERY SIGNATURE TAKES POINTERS AND RETURNS A STATUS. There is no convenience overload here, no
// array parameter, no ref/out: those all imply marshalling, and marshalling implies a copy or a
// transformation that this layer exists to avoid. A caller passes the address of a struct it has
// already filled with pointers to buffers it already owns. Making that awkward is the point — the
// awkwardness belongs in this file, once, and Design (X1.2) is where it is paid off.
//
// [LibraryImport] rather than [DllImport]: the source generator emits a direct call for a
// signature that is entirely blittable, and REFUSES TO COMPILE one that is not. That turns "these
// structs are blittable" from a claim in a comment into a build-time check.
//
// The native side never allocates. Every buffer these calls write to was dimensioned by
// tf_query_plan_sizes and allocated by the caller — constraint C0.1, which is the reason the
// signatures look like this at all.

using System.Runtime.InteropServices;

namespace ThinFilm.Native;

public static unsafe partial class NativeMethods
{
    /// <summary>
    /// The name P/Invoke resolves. Windows appends .dll, Linux prefixes lib and appends .so,
    /// macOS .dylib — all from this one string, which is why it has no extension or prefix.
    /// </summary>
    /// <remarks>
    /// NOT "thinfilm", and the collision was real: the CLI assembly is named thinfilm so that
    /// its executable is `thinfilm`, and .NET emits a managed thinfilm.dll beside thinfilm.exe.
    /// P/Invoke finds that one first and reports a library with no entry points - which reads
    /// like a build problem and is a naming one. Two artefacts cannot share a name, and the one
    /// that gives way is the library nobody types.
    /// </remarks>
    public const string Library = "thinfilm_native";

    /// <summary>
    /// How many elements each plan buffer needs. Called BEFORE anything is allocated — it is the
    /// only call that does not take buffers, because it is the one that says how big they are.
    /// </summary>
    [LibraryImport(Library)]
    public static partial TfStatus tf_query_plan_sizes(
        int n_points, int n_variables, int n_span, int n_operands, TfPlanSizes* out_sizes);

    /// <summary>
    /// Decides everything that does not change between iterations: the arithmetic path per point,
    /// which polarisations are needed, which variables are inert, the operand order, and how much
    /// scratch the evaluators want. Run once per problem SHAPE, not once per evaluation.
    /// </summary>
    [LibraryImport(Library)]
    public static partial TfStatus tf_plan(
        TfProblem* problem, TfOperands* operands, TfVariables* variables,
        int n_workers, TfPlanBuffers* plan);

    /// <summary>Values, residuals and merit. No Jacobian: pass out.jacobian = null.</summary>
    [LibraryImport(Library)]
    public static partial TfStatus tf_evaluate(
        TfProblem* problem, TfOperands* operands, TfPlanBuffers* plan,
        TfWorkspace* workspace, TfOutput* output);

    /// <summary>Everything tf_evaluate returns, and the Jacobian with it.</summary>
    [LibraryImport(Library)]
    public static partial TfStatus tf_evaluate_jacobian(
        TfProblem* problem, TfOperands* operands, TfVariables* variables,
        TfPlanBuffers* plan, TfWorkspace* workspace, TfOutput* output);

    /// <summary>d(value)/d(lambda) per operand. Needs the dn/dlambda arrays to be non-null.</summary>
    [LibraryImport(Library)]
    public static partial TfStatus tf_evaluate_wavelength_derivative(
        TfProblem* problem, TfOperands* operands, TfPlanBuffers* plan,
        TfWorkspace* workspace, TfSpectralOutput* output);

    /// <summary>The needle map: d(sum of squared residuals)/d(inserting a layer), per candidate and depth.</summary>
    [LibraryImport(Library)]
    public static partial TfStatus tf_evaluate_needle(
        TfProblem* problem, TfOperands* operands, TfPlanBuffers* plan,
        TfNeedleRequest* request, TfWorkspace* workspace, TfNeedleOutput* output);

    /// <summary>d(value)/dn and d(value)/dk per material — the optics half of the dispersion chain.</summary>
    [LibraryImport(Library)]
    public static partial TfStatus tf_evaluate_index_derivative(
        TfProblem* problem, TfOperands* operands, TfPlanBuffers* plan,
        TfWorkspace* workspace, TfIndexOutput* output);

    /// <summary>
    /// The ABI version the loaded binary was built with. Checked against TfAbi.TF_ABI_VERSION at
    /// startup, because a managed assembly and a native library that disagree about the layout is
    /// the one failure that produces plausible numbers instead of an error.
    /// </summary>
    [LibraryImport(Library)]
    public static partial int tf_abi_version();
}
