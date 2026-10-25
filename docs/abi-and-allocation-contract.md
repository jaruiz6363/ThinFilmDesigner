# The ABI and allocation contract

This is the load-bearing document. Everything else in the evaluator is downstream of it, and it is
the most expensive thing to change later, so it is written and agreed before any evaluation code
exists.

Nothing here is implemented yet. Where a decision is still open it says so.

## The rules this obeys

These are not preferences. They are constraints on every line of native code.

1. **All memory is dimensioned and allocated in C# and passed to C++.** The native evaluator
   allocates nothing — no `new`, no `malloc`, no `std::vector`, no RAII type that owns storage.
2. **No fixed-size arrays dimensioned by a constant.** There is no `MAX_LAYERS`, no
   `MAX_SURFACES`, no `MAX_TARGETS`, and there never will be. A reviewer finding one should treat
   it as a defect, not a shortcut.
3. **The port to CUDA must be mechanical.** Nothing may be written whose translation to a kernel
   requires a redesign.
4. **Double precision throughout.** Single was tried in the legacy code and was not good enough;
   the switch to double was forced. That is a settled question and the reason is recorded here so
   it is not re-litigated.
5. **A code path that is not implemented says so loudly.** See *Unimplemented paths*.

**Rules 1 and 3 are the same rule.** A function that allocates nothing, holds no state, and
touches only caller-supplied buffers is exactly what a kernel is. Everything below follows from
taking that seriously.

## Three calls, in order

The native side never allocates, so it cannot decide sizes and then fill them in one step. The
sequence is:

```
1.  C#   computes the PLAN buffer sizes from counts alone (formulae below)
2.  C#   allocates the plan buffers
3.  C++  tf_plan(...)              fills path selection, point ordering, and SCRATCH SIZE
4.  C#   allocates scratch
5.  C++  tf_evaluate(...)          or  tf_evaluate_jacobian(...)      — called many times
```

Steps 1 to 4 happen once per optimisation run. Step 5 happens in the inner loop and must not
allocate, branch unpredictably, or touch anything the plan did not already decide.

**Two evaluation calls, and the cheap one is the common one.** `tf_evaluate` returns the values,
the residuals and the merit, and no derivatives. `tf_evaluate_jacobian` returns the same and the
Jacobian of the residuals as well, at roughly the cost of a forward and a backward sweep. Who calls
which:

| Caller | Call |
|---|---|
| Damped least squares (Refine, and every refinement inside the other methods) | `tf_evaluate_jacobian` once per **accepted** step; each **trial** step, as the damping is raised until one goes downhill, is judged by `tf_evaluate` alone |
| Global search, needle, gradual evolution, remove thin layers | `tf_evaluate` to judge a start, an insertion or a removal; the Jacobian only through the refinements they run |
| Needle map | `tf_evaluate_needle` (its own sweep, P(z) at every depth) |
| The merit table, the Spectrum tab, `evaluate`, `spectrum`, reports | `tf_evaluate` only |

So no caller pays for derivatives it does not use. The Merit Function Specification asks each
operand for "a value and an analytic gradient in one pass"; that is what `tf_evaluate_jacobian` is,
and `tf_evaluate` is the same pass with the derivative half left out.

**Why `tf_plan` is native rather than managed.** The path decision depends on optical conditions —
whether every material is lossless at this wavelength, whether any layer is beyond the critical
angle at this angle. If C# decided that and C++ assumed something else, the disagreement would be
silent and would show up as a wrong derivative. There is exactly one implementation of the
decision, and it lives beside the code that depends on it.

## Precision and complex layout

- **Every floating-point quantity crossing the boundary is `double`.**
- **Complex quantities are interleaved `(re, im)`**, which is the memory layout of
  `System.Numerics.Complex`, so material arrays marshal with no transformation.
- **The Jacobian is real** — it holds `∂r/∂x` — so the interleave question does not arise where
  the large arrays are.

## The Jacobian is column-major, and that is deliberate

```
J[v * n_residuals + r]        v = variable index, r = residual index
```

Variable-major, also called Fortran order. Two independent reasons:

- **GPU coalescing.** One thread per residual. Thread `r` writes `J[v*n_residuals + r]` for each
  `v`, so consecutive threads write consecutive addresses. Row-major would have consecutive
  threads writing addresses `n_variables` apart, which is the difference between one memory
  transaction and thirty-two.
- **It is what least-squares code already wants.** MINPACK and everything descended from it take
  column-major Jacobians, and the legacy tree already links `MINPACK_OPT.dll`.

## Residuals, not just a merit number

`tf_evaluate_jacobian` returns **residuals and the Jacobian of the residuals**, not the scalar
merit and its gradient. **This follows LensHH-LT**, which optimises by damped least squares over a
residual vector; the two programs now mean the same thing by a residual, a weight and a merit.

```
r_i   = sqrt(w_i) * (value_i - target_i)
merit = sqrt( sum r_i^2 / sum w_i )
gradient = 2 * J^T r          (one reduction away, if a gradient method ever wants it)
```

A Levenberg-Marquardt or Gauss-Newton step needs `J` itself rather than `J^T r`; returning the
scalar only would throw away the structure the optimiser exists to use. `tf_evaluate` returns the
residuals too, and the scalar merit as a convenience.

**Both conventions were established by measuring LensHH-LT, not by reading it.** Its source reads
`(op.Value - op.Target) * op.Weight` at every display site, which says the weight enters linearly.
Running it says otherwise:

| user weight | residual reported |
|---|---|
| 1 | 4.390469e+01 |
| 2 | 6.209105e+01 |

The ratio is √2, not 2 — the `Weight` the core exposes is already a root. The merit was pinned the
same way: unchanged at 43.9047 when a single operand's weight went from 1 to 2, and 31.045 =
43.9047/√2 once a second, already-satisfied operand of equal weight was added. That fixes the
denominator as Σw rather than Σw², and makes the merit a **weighted RMS**: scaling every weight
leaves it alone, so a weight expresses relative importance and nothing else.

`tests/native/test_evaluate.cpp` repeats that experiment, so the two programs cannot drift apart
without it failing.

## The evaluation paths

Selected per evaluation point by `tf_plan`, never re-decided in the inner loop.

| code | path | when |
|---|---|---|
| 0 | `STRUCTURED` | every material lossless at this wavelength **and** no layer beyond critical at this angle |
| 1 | `GENERAL` | anything else |
| 2 | `INCOHERENT` | one or more massive layers present — **not implemented** |
| 3 | `MIXED` | absorbing layers present but clustered — **not implemented**, see below |

### Why STRUCTURED is worth a separate path

For a lossless layer at a real angle below critical the phase thickness `δ = 2π n d cos θ / λ` is
real and the Abeles matrix is

```
M = [  cos δ       i sin δ / η ]
    [  i η sin δ   cos δ       ]
```

— real diagonal, purely imaginary off-diagonal. That form is **closed under multiplication**:

```
[ a₁  ib₁ ] [ a₂  ib₂ ]   [ a₁a₂ − b₁c₂      i(a₁b₂ + b₁d₂) ]
[ ic₁ d₁  ] [ ic₂ d₂  ] = [ i(c₁a₂ + d₁c₂)   d₁d₂ − c₁b₂     ]
```

so the whole chain — prefix products, suffix products and the derivative products — stays in it.
Four real doubles per matrix rather than four complex.

| | GENERAL | STRUCTURED |
|---|---|---|
| real multiplies per 2×2 product | 32 | **8** |
| real adds | 24 | **4** |
| storage per matrix | 8 doubles | **4 doubles** |

The derivative matrix `∂M/∂δ = [[−sin δ, i cos δ/η], [i η cos δ, −sin δ]]` has the same form, so
the Jacobian stays cheap too. And `det M = ad + bc = 1` is preserved by the product — a free
invariant, asserted in every test, which catches a transcription error at the surface that made it
rather than twenty steps downstream.

**The path is a property of the POINT, not of the stack.** `k = 0` everywhere still gives complex
`δ` beyond the critical angle. With air incident and every layer of higher index that never
happens; with a coupling prism or an immersion medium it does. So it is decided per
(wavelength, angle) pair and stored.

**`tf_plan` sorts points by path.** Grouping points that take the same branch costs nothing at plan
time and removes warp divergence entirely when this becomes a kernel. Rule 3 paying off before any
CUDA exists.

### Why MIXED is designed for and not built

Structured × general = general, so the structure dies on first contact with an absorbing layer.
With prefix and suffix sweeps that is less bad than it sounds — prefix products stay cheap up to
the *first* absorbing layer and suffix products stay cheap back to the *last* one, so a single
metal layer in the middle leaves both ends cheap. It is worth having eventually.

It is not worth having first. A third branch is a third thing to get wrong, and the two clean paths
cover nearly all real work. The plan structure and the scratch layout can express it, so adding it
later does not touch this ABI.

## The derivative cache, and why the scratch size is a plan output

`M = M_before · M_layer · M_after` evaluated per layer is O(n²). Done as one forward sweep storing
prefix products and one backward sweep carrying the suffix in registers it is O(n):

```
forward   j = 0..n−1 :  prefix[j+1] = prefix[j] · M_j              (stored, n+1 matrices)
backward  j = n−1..0 :  ∂M/∂t_j = prefix[j] · (∂M_j/∂t_j) · S      (S in registers)
                        S = M_j · S
```

Each thickness derivative then costs two 2×2 products. **The whole Jacobian is O(n) matrix
products rather than O(n²)**, which is the single most important algorithmic fact in the evaluator.

Three things make it fit:

- **The suffix is never stored.** The backward sweep only needs the running product at the layer it
  is looking at, so the storage is `n+1` matrices rather than `2n`. On the GPU that halving is the
  difference between fitting in shared memory and not.
- **`M_j` costs nothing to recompute** in the backward sweep, because `M_j` and `∂M_j/∂δ` come out
  of the same `sin`/`cos` pair. Recomputing is free; storing would not be.
- **One chain serves both polarisations.** Every target is *linear* in `(∂r_s, ∂r_p, ∂t_s, ∂t_p)`
  once the amplitudes are known — even `tan Ψ`, because `ρ = r_p/r_s` gives
  `∂ρ = ∂r_p/r_s − (r_p/r_s²)∂r_s`, whose two halves can be accumulated in separate passes. So the
  sweeps run one polarisation at a time over the same buffer.

The cache is therefore `(n_layers + 1)` matrices plus a per-layer derivative record, per worker.
**The size still comes back from `tf_plan` and callers must not compute it themselves** — it is the
shape of an internal cache, not a property of the problem, and it changes when the engine does.
Today it happens to be a function of `n_layers` and `n_workers` alone, because the derivative sweep
uses general complex arithmetic on every path; when the structured derivative sweep is written it
will depend on the path again, and a caller that had hard-coded the formula would then under-
allocate. This is the concrete form of "the pre-optimization phase determines the required
allocation".

### What the derivative sweep does not do yet

The sweep is **general complex arithmetic on every path, including points the plan marked
structured**. The answer is correct — it is checked against the structured evaluator and against
central differences — but it forfeits the structured path's gain on lossless points. The value-only
path, which is what runs when no derivatives are wanted, already takes the fast branch. Writing the
structured sweep is a self-contained next step; everything it needs is in `tf_matrix2.h`.

### Lossless points: what the fast branch is worth

The plan decides the path **per point** - each wavelength and angle - not per stack: a point is
STRUCTURED when every material is lossless there and no layer (nor the substrate) is beyond the
critical angle, GENERAL otherwise. So a stack that absorbs only in the ultraviolet still takes the
fast branch at its visible points, and an absorbing layer anywhere costs only the points where it
absorbs.

| | Value-only (`tf_evaluate`) | Jacobian (`tf_evaluate_jacobian`) |
|---|---|---|
| Lossless point | **structured**: real diagonal, imaginary off-diagonal, 4 doubles a matrix | general (not yet structured) |
| Absorbing or beyond critical | general complex, 8 doubles a matrix | general |

The structured form halves the matrix arithmetic - a 2×2 product is 8 real multiplies instead of
32 - but a solve also spends four transcendentals per layer on either path, which do not halve.
**Measured** (X1.5 benchmark), end to end on two cases differing in nothing but k: **22 ns** per
point per layer structured against **43 ns** general, about **1.9×**. The "factor of four" this
document once gave is the arithmetic alone, not a solve.

What the missing structured Jacobian sweep would be worth: the sweep is about 85% of a Jacobian and
a refinement is about 76% Jacobians, so the sweep is roughly 30% of a run; at 1.9× it would save
about 14% of a refinement on a lossless design, and nothing on an absorbing one. It is planned for a
later release (scope S1.10); the first release does without it.

Material-coefficient variables are **refused**, not zeroed, because there is no dispersion model to
differentiate yet. See *Where the derivatives come from*.

## Buffers

All are owned by C#. `const` marks read-only to native code. Sizes are exact, not upper bounds.

### Problem description (input, read-only)

| buffer | type | length |
|---|---|---|
| `layer_thickness` | `double` | `n_layers` |
| `layer_material` | `int32` | `n_layers` |
| `material_index` | `complex double` | `n_materials * n_wavelengths` |
| `wavelength` | `double` | `n_wavelengths` |
| `point_wavelength` | `int32` | `n_points` |
| `point_angle` | `double` | `n_points` (radians, in the incident medium) |
| `index_incident` | `complex double` | `n_wavelengths` |
| `index_exit` | `complex double` | `n_wavelengths` |

### Targets (input, read-only)

| buffer | type | length |
|---|---|---|
| `target_code` | `int32` | `n_residuals` |
| `target_point` | `int32` | `n_residuals` |
| `target_value` | `double` | `n_residuals` |
| `target_weight` | `double` | `n_residuals` |

### Active variables (input, read-only)

| buffer | type | length |
|---|---|---|
| `variable_kind` | `int32` | `n_variables` |
| `variable_layer` | `int32` | `n_variables` |
| `variable_material` | `int32` | `n_variables` |
| `variable_coefficient` | `int32` | `n_variables` |

### Plan (filled by `tf_plan`, read-only thereafter)

| buffer | type | length | filled by C# or native |
|---|---|---|---|
| `point_path` | `int32` | `n_points` | native |
| `point_order` | `int32` | `n_points` | native — points sorted by path |
| `scratch_doubles` | `int64` | 1 | native — **the allocation size C# then honours** |
| `plan_status` | `int32` | 1 | native |

### Scratch (allocated by C# after `tf_plan`, written by native)

A single `double*` of length `scratch_doubles`. Its internal layout is native's business and is
documented in the header beside the code that carves it up, not here — it is not part of the
contract, only its size is.

### Output (written by native)

| buffer | type | length |
|---|---|---|
| `residual` | `double` | `n_residuals` |
| `jacobian` | `double` | `n_residuals * n_variables` — only for `tf_evaluate_jacobian` |
| `merit` | `double` | 1 |
| `status` | `int32` | 1 |

## Where the derivatives come from

**Analytic through the optics. Automatic through the material models.**

Every merit function on the list reduces to `r` and `t`, and each has a clean hand-derivative once
`∂r/∂x` and `∂t/∂x` are known — `R = |r|²`, `T = (η_exit/η_inc)|t|²`, `ρ = r_p/r_s`,
`tan Ψ = |ρ|`, `Δ = arg ρ`. No automatic differentiation belongs anywhere in that layer, and using
it there would cost speed for nothing.

Automatic differentiation earns its place in **dispersion**: `n(λ)` and `k(λ)` from Sellmeier,
Cauchy, tabulated and oscillator models. Many forms, user-extensible, and evaluated once per
material per wavelength rather than once per layer per interface — so the cost is negligible and
the maintenance saving is real. When a *material coefficient* is an active variable, hand-deriving
every model would be a liability with no performance payoff.

That is the seam, and it is chosen for a reason rather than by habit.

**Where the seam stands today.** The optics half is written and checked: `tf_evaluate_jacobian`
handles layer-thickness variables analytically for all ten target families, verified against central
differences of `tf_evaluate` on lossless, off-normal and absorbing stacks. The dispersion half does
not exist yet, because there are no dispersion models yet — `material_index` is a table of values,
not a function of coefficients. A `MATERIAL_COEFFICIENT` variable is therefore **refused** with
`TF_NOT_IMPLEMENTED_VARIABLE`, loudly, rather than given a zero column that would read as a material
that does not matter.

## Unimplemented paths must be loud

A branch that is not implemented does three things, all of them:

1. writes a diagnostic naming the path and the reason to `stderr`;
2. sets `status` to a distinct non-zero code;
3. leaves the output buffers **untouched**, so a caller that ignores the status gets nothing that
   looks like an answer.

The managed wrapper turns a non-zero status into an exception. Silence, or a plausible zero, is the
failure mode this project is most exposed to, and it is cheaper to make noise now than to find a
wrong number later.

## Merit functions in the first implementation

`Ts`, `Tp`, `Ta`, `Rs`, `Rp`, `Ra`, `Psi`, `Delta`, `tan(Psi)`, `cos(Delta)`.

Numbering follows the legacy `TargetCode` enum where the codes coincide, so existing data files
stay readable.

### The Delta branch cut is a system setting

`Delta = arg(rho)` is an angle, and an angle has no single right representative. `targets.delta_convention`
picks one, once, for the whole system:

| value | name | the value | the residual |
|---|---|---|---|
| 0 | `TF_DELTA_PRINCIPAL` | `atan2`'s principal value, `(-π, π]` | plain subtraction |
| 1 | `TF_DELTA_POSITIVE` | `[0, 2π)`, the usual ellipsometric interval | plain subtraction |
| 2 | `TF_DELTA_SHORTEST_ARC` | principal | the shortest signed arc, in `(-π, π]` |

**The problem is the residual, not the display.** A model at −179° and a target at +179° are two
degrees apart; subtracted they are 358° apart. An optimiser handed that drives hard in the wrong
direction across the whole width of the cut, and the merit jumps discontinuously as the design
crosses it. `TF_DELTA_SHORTEST_ARC` removes the problem rather than relocating it; the other two
relocate it, which is the right answer when a target genuinely lives on a fixed interval.

`tf_plan` **warns once**, on `stderr`, when a `Delta` target is present under a convention that
subtracts — once, at plan time, rather than on every evaluation, because a diagnostic printed a
million times is a diagnostic nobody reads. It is a warning and not an error: a caller whose
targets sit nowhere near the cut is doing nothing wrong. An out-of-range value **is** an error,
`TF_UNKNOWN_DELTA_CONVENTION`.

**The Jacobian needs no branch on this at all.** All three maps differ from `arg(rho)` by a locally
constant multiple of 2π, and a constant has no slope — so `∂Δ/∂x = ∂arg(ρ)/∂x` under every setting,
and the derivative cannot fall out of step with the value when the setting changes. The one
exception is exactly on the cut, where the value jumps and no convention has a derivative.

`cos(Delta)` has no cut by construction and ignores the setting entirely.

## Not yet decided

- The native/managed marshalling shape: one POD struct of pointers passed by pointer, or a flat
  pointer table as the legacy `function_arrays[]` used. The struct is type-safe and equally
  portable; the table has the advantage of already being proven here.
- Whether `n_points` is a full (wavelength × angle) grid or an arbitrary list. The list is
  strictly more general and costs nothing, which argues for it, but the grid admits a factorisation
  the list does not.
- Threading on the CPU path: where the parallel-for sits, and whether it is managed or native.
