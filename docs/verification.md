# Verification

How we know the program computes the right numbers. There are three kinds of evidence:

1. **The engine against physics:** closed-form results, conservation of energy, two
   implementations of the same calculation, and derivatives checked against finite differences.
2. **The engine against an independent program.**
3. **The program against the published literature:** over 250 designs from published papers
   reproduce the results their authors reported.

Everything in parts 1 and 2 runs as automated tests, which anyone can run after building (see
[building.md](building.md)). The literature designs ship with the program, each with its check
written in its Notes.

## Summary

| Evidence | Extent | Result |
|---|---|---|
| Engine tests (C++) | 22 test programs, 2,018 checks | all pass |
| Program tests (C#: API, optimisers, CLI, MCP) | 2,969 checks | all pass |
| GUI tests (the window, run without a screen) | 467 checks | all pass |
| Independent program (Optiland) | R, T and A, s and p, 0° and 30°, absorbing layer | agree to 1×10⁻¹⁰ |
| Literature designs | 370 designs from 58 papers, 319 checked | 220 agree, 89 close, 10 differ (each explained) |

The tests pass on Windows 11 (MSVC 19.44, .NET SDK 9.0) and on Ubuntu 24.04 (GCC 13.3, .NET
SDK 8.0). Among the optimiser checks: bounds are enforced by reflection (an overshoot is mirrored back inside, a floor of zero stops at zero, an overshoot wider than the range is refused); refine keeps bounded layers inside their bounds; and needle, which adds layers, is refused while a layer has a minimum or maximum.

## 1. The engine against physics

The engine computes reflectance, transmittance, absorptance, phase and their derivatives by the
characteristic-matrix method. Its tests compare it with results that do not depend on the engine:

| Test | What the engine is compared with |
|---|---|
| `test_evaluate` | Closed-form optics: Fresnel reflectance of a bare surface; quarter-wave and half-wave (absentee) layers; Rp = 0 at Brewster's angle; R + T + A = 1. It also checks the fast evaluation path against the general one. |
| `test_matrix2` | The structured 2×2 kernel against the general complex kernel, which shares none of its arithmetic (653 checks). |
| `test_jacobian` | Every analytic derivative of the merit function against central finite differences; energy conservation. |
| `test_spectral` | Derivatives with respect to wavelength, against an analytic dispersion law. |
| `test_index_derivative` | Derivatives with respect to refractive index, and the exact identity ∂Rs/∂n + ∂Ts/∂n = 0 for a lossless stack. |
| `test_dispersion` | Group delay against the derivative of phase, GDD against GD, TOD against GDD; an air gap adds exactly 2L/c, which fixes signs and units. |
| `test_needle` | The needle map (the derivative of the merit with respect to inserting a layer) against two references: for a needle of the layer's own material, the thickness derivative, exactly; for another material, a real thin layer inserted and central-differenced. |
| `test_phase1_targets`, `test_derived` | Every optical operand against its definition, and energy conservation. |
| `test_comparison` | Min, Max and range targets: a satisfied target contributes exactly zero, and its slope is zero. |
| `test_material_data` | Film stress (σ·d summed over the layers) exactly, and that an index extrapolated beyond a material's data is flagged in every operand that uses it. |
| `test_fixtures` | Ten recorded problems, which the C# side reads too. If only the C# side disagrees, the fault is in the C# binding, not the engine. |

The other engine tests check its interface and its rules: that the binary interface is
unchanged, that every documented refusal still refuses, and that the source has no fixed-size
arrays and no single precision.

## 2. The engine against an independent program

On 21 October 2026 the engine was compared with [Optiland](https://github.com/HarrisonKramer/optiland),
an independent open-source optical design program written in Python. The test stack had an
absorbing Nb2O5 layer (measured n and k). R, T and A for s and p light, at 0° and 30°, agreed to
1×10⁻¹⁰.

To repeat it for any design, with Python and Optiland installed:

1. Export the coating with **File > Export Optiland coating** (or `thinfilm optiland export`).
2. Export its spectrum from the Spectrum tab with **Export…** (R, T and A, s and p).
3. Run `python tools/optiland_compare.py coating.json spectrum.txt`. Optiland computes the same
   quantities at the same wavelengths and angles; the script prints the largest difference in
   each column, and exits with status 0 when all agree within 1×10⁻⁶.

The automated tests check that the exported file is what Optiland reads and that the dispersion
formulas agree with Optiland's to 1×10⁻¹⁴. They do not run Python.

## 3. The program against the literature

### What was done

From 58 published papers, from 1952 to 2019, 370 designs were rebuilt exactly as printed: the
layers, the materials with the paper's own indices, the media, the angle and the polarisation.
They cover 16 kinds of coating: antireflection coatings, all-dielectric and metal-dielectric
bandpass filters, dielectric and metal mirrors, edge filters and dichroics, notch filters,
polarisers and beam splitters, retarders, chirped mirrors, absorbers and emitters, colour
filters, sensors, frustrated total reflection, and synthesis methods. The full list of papers
is in [references.md](references.md).

Each design was then computed with the program's command line, and what the paper reports was
recomputed: R, T or A at a wavelength, a band average, a peak, a minimum, a bandwidth or a total
thickness. Each check was given a tolerance suited to what was being compared. For example, 0.01
in absorptance, or 0.5 nm in thickness. The verdict, with the program's numbers and the paper's,
is written in the design's Notes. For example, from P. W. Baumeister, Appl. Opt. 8, 423 (1969),
Table III:

> Checked with ThinFilmDesigner (2026-10-21), agrees with the paper: ours A 94.20 % at 546.1 nm;
> paper A 94.20 %.

To see any design's check, open it with **File > Open sample… > Literature** and read its
Notes, or run `thinfilm describe` on it.

### Results

| Verdict | Designs | Meaning |
|---|---|---|
| **Agrees** | 220 | Within the check's tolerance of the number the paper prints. |
| **Close** | 89 | Near the paper's value but outside the strict tolerance, for a known reason: the paper's value was read off a printed figure, or was measured, or the paper did not give all of its material data. |
| **Differs** | 10 | Not reproduced; each has a stated reason, below. |
| Not checked | 51 | The paper gives no number for the coating to compare with: only plotted curves, or numbers for a whole device (a solar cell, a radiative cooler) that a coating calculation does not reproduce. |

So of the 319 designs with a published number to compare with, 309 reproduce it: 220 within the
tolerance, and 89 closely, their differences explained. **We reproduced the published results of
over 250 designs from the literature.**

A typical close result, from S. W. Warren, Infrared Phys. 8 (1968), Table 1: the program gives
a bandwidth of 0.642% of 4 µm where the paper prints 0.62%.

### The ten that differ

In every case the program reproduces what the paper printed as input. The difference is in
the paper's own result, its missing data, or a different method.

| Paper | Design | Program | Paper | Why |
|---|---|---|---|---|
| Baumeister 1969 | Filter 1 and Filter 3 (two designs) | T 0.50, R 0.18 at 253.6 nm | T 0.739, R 0 | Table VI as printed does not give the paper's own result; a misprinted thickness is the likely cause. |
| Bronnbauer 2017 | three quarter-wave mirrors | R 95.8%, T 4.2% | R 93.6% from its Eq. 2; T ≈ 5% measured | The program is nearer the measured T. The inks' indices are given only at 600 nm. |
| Raut 2011 | SiO2/Si/Ge on Si | T 69% at 480 nm, 99.9% at 720 nm | ≈ 96% at 480 nm | The stack is quarter waves at 720 nm; this review likely misread the original figure. |
| Yenisoy 2019 | Ge MWIR AR, as built | R max 0.68% at 3.4 µm | R < 0.5% (measured) | Measured thicknesses with printed dispersion; exceeds 0.5% only at the band edge, which a nanometre of thickness moves. |
| Berning 1957 | Fig. 14 multiplex | peak T 41% | 45% | The paper plots against phase thickness with the silver index held fixed, which a calculation against wavelength does not do. |
| Cojocaru 2004 | N = 48 wavelet mirror | R 0.99985 | R ≥ 0.9999 | The paper does not say where it samples its profile; its own equation gives N ≈ 48.5, so N ≥ 48 is probably rounded. |
| Shokooh-Saremi 2004 | C-band cube PBS | mean P 0.9916, min 0.971 | mean 0.9927, min 0.971 | The minimum agrees; the mean depends on ten wavelengths the paper does not list. |

## Running the tests yourself

After building (see [building.md](building.md)), from the repository's top folder:

```sh
ctest --test-dir build -C Release                                # the engine: 22 programs
dotnet run --project managed/ThinFilm.Native.Tests -c Release   # API, optimisers, CLI, MCP
dotnet run --project managed/ThinFilm.Gui.Tests -c Release      # the GUI, without a screen
```

Each C# suite ends with a line such as `=== 3009 checks, 0 failures ===`.
