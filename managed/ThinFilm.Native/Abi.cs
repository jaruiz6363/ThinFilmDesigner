// Abi.cs — a mirror of native/include/thinfilm.h, and nothing else.
//
// THE FIELD NAMES ARE THE C NAMES, DELIBERATELY. This file is meant to be read side by side with
// thinfilm.h and diffed by eye; every rename is an opportunity for a field to end up in the wrong
// place, and a wrong place here is not a compile error — it is a pointer aimed at the neighbouring
// field, which produces numbers rather than a crash. Idiomatic C# naming belongs in the Design
// layer above this one (X1.2), where it costs nothing.
//
// EVERY STRUCT HERE IS BLITTABLE. No [MarshalAs], no bool, no string, no arrays-in-structs — just
// integers, doubles and raw pointers, so that the runtime passes the address of the struct and
// performs no transformation at all. ThinFilm.Native.Tests proves this rather than asserting it:
// it checks sizeof(T) against Marshal.SizeOf<T>() for every struct, and those two agree only when
// the marshalled layout and the managed layout are the same thing.
//
// THE LAYOUT IS FROZEN. docs/scope.md says ABI_FROZEN: yes, and tests/native/test_abi_frozen.cpp
// pins every size, offset and enumerator. The managed test reads THAT FILE — the actual pins, not
// a copy of them — and checks this mirror against it, so there is one source of truth for the
// layout and C# is checked against it rather than trusted alongside it.

using System.Runtime.InteropServices;

namespace ThinFilm.Native;

/// <summary>Two adjacent doubles, real first — the layout of System.Numerics.Complex.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct TfComplex
{
    public double re;
    public double im;

    public TfComplex(double re, double im) { this.re = re; this.im = im; }
}

/// <summary>The general 2x2 characteristic matrix: eight doubles.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct TfMatrix2
{
    public TfComplex m00, m01, m10, m11;
}

/// <summary>The structured 2x2: four doubles, for the lossless case.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct TfMatrix2s
{
    public double a, b, c, d;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct TfConfigurations
{
    public int n_configurations;
    public TfComplex* index_incident;
    public TfComplex* index_exit;
    public TfComplex* dindex_incident_dwavelength;
    public TfComplex* dindex_exit_dwavelength;
    public int* index_incident_is_extrapolated;
    public int* index_exit_is_extrapolated;
    public int* reversed;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct TfProblem
{
    public int n_layers;
    public int n_materials;
    public int n_wavelengths;
    public int n_points;

    public double* layer_thickness;
    public int* layer_material;
    public int* layer_is_incoherent;

    public TfComplex* material_index;
    public double* wavelength;

    public int* point_wavelength;
    public double* point_angle;

    public TfComplex* index_incident;
    public TfComplex* index_exit;

    public TfConfigurations* configurations;
    public int* point_configuration;

    public TfComplex* material_dindex_dwavelength;
    public TfComplex* dindex_incident_dwavelength;
    public TfComplex* dindex_exit_dwavelength;

    public double* material_stress;
    public int* material_index_is_extrapolated;
    public int* index_incident_is_extrapolated;
    public int* index_exit_is_extrapolated;

    public int n_trials;
    public double* trial_layer_thickness;
    public TfComplex* trial_material_index;

    public double substrate_thickness;
    public TfComplex* index_back;

    public TfComplex* material_d2index_dwavelength2;
    public TfComplex* d2index_incident_dwavelength2;
    public TfComplex* d2index_exit_dwavelength2;

    public TfComplex* material_d3index_dwavelength3;
    public TfComplex* d3index_incident_dwavelength3;
    public TfComplex* d3index_exit_dwavelength3;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct TfOperands
{
    public int n_operands;
    public int delta_convention;
    public int extrapolation_policy;
    public int* trial_reduction;

    public int* code;
    public int* point;
    public double* target;
    public double* weight;

    public int* comparison;
    public double* target_hi;
    public double* tolerance;
    public double* scale;

    public int n_span;
    public int* reduction;
    public int* point_first;
    public int* point_count;
    public int* point_index;
    public double* point_weight;
    public double* reduction_param;

    public int n_input;
    public int* input_first;
    public int* input_count;
    public int* input_index;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct TfVariables
{
    public int n_variables;
    public int* kind;
    public int* layer;
    public int* material;
    public int* coefficient;
}

[StructLayout(LayoutKind.Sequential)]
public struct TfPlanReport
{
    public int n_structured;
    public int n_general;
    public int n_incoherent;
    public int n_unsupported;
    public int n_variables_inert;
    public int n_residuals;
    public int n_operands_unscored;
    public int n_operand_levels;
    public int max_operand_span;
    public int max_clamped_layers;
    public int n_points_one_polarisation;
    public long scratch_doubles;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct TfPlanBuffers
{
    public int* point_path;
    public int* point_order;
    public int* variable_inert;
    public int* operand_residual;
    public int* operand_order;
    public int* operand_level;
    public int* point_span_first;
    public int* point_span_count;
    public int* point_span_operand;
    public int* point_span_slot;
    public int* point_polarisation;
    public TfPlanReport* report;
    public int* status;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct TfWorkspace
{
    public double* scratch;
    public long scratch_doubles;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct TfOutput
{
    public double* residual;
    public double* jacobian;
    public double* value;
    public int* operand_flag;
    public int* n_operands_flagged;
    public double* trial_value;
    public double* merit;
    public int* status;
}

/// <summary>Element COUNTS, not byte sizes — every one is a number of array elements.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct TfPlanSizes
{
    public long point_path_count;
    public long point_order_count;
    public long variable_inert_count;
    public long point_polarisation_count;
    public long point_span_first_count;
    public long point_span_count_count;
    public long point_span_operand_count;
    public long point_span_slot_count;
    public long operand_residual_count;
    public long operand_order_count;
    public long operand_level_count;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct TfSpectralOutput
{
    public double* value;
    public double* dvalue_dwavelength;
    public int* status;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct TfNeedleRequest
{
    public int n_depths;
    public double* depth;
    public int n_candidates;
    public int* candidate_material;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct TfNeedleOutput
{
    public double* residual;
    public double* dsumsq_dinsertion;
    public int* status;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct TfIndexOutput
{
    public double* value;
    public double* dvalue_dn;
    public double* dvalue_dk;
    public int* status;
}
