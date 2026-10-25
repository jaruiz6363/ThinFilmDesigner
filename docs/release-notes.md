# ThinFilmDesigner 1.0.0

The first release. ThinFilmDesigner designs thin-film optical coatings: antireflection coatings,
bandpass, edge and notch filters, mirrors, polarisers and beam splitters. You describe what the
coating must do, and the program builds and refines the layer stack that does it.

It comes as three programs that share one engine and one set of features:

- **the GUI**, for designing by hand;
- **`thinfilm`**, the command line, for scripts;
- **`thinfilm-mcp`**, an MCP server, so an AI assistant such as Claude can design coatings with
  the program. **Help > Connect to Claude (MCP)…** sets it up.

## Downloads

| Platform | File | |
|---|---|---|
| Windows 10 and 11 (x64) | `ThinFilmDesigner-Setup-1.0.0.exe` | An installer. It can add the command line to your PATH. |
| macOS 11 or later, Apple Silicon | `ThinFilmDesigner-1.0.0-macOS-arm64.zip` | Native for Apple Silicon (M1 and later). Drag the app into Applications. |
| Linux (x86-64) | `ThinFilmDesigner-1.0.0-x86_64.AppImage` | One file: `chmod +x` and run. `--cli` and `--mcp` run the other two programs. |

Each download includes everything it needs, the .NET runtime among them. On macOS the app is signed
but not notarised, so the first time, Control-click it and choose **Open**. See
[Installing and building](building.md).

## What it does

**Designing**
- A merit function of targets: reflectance, transmittance and absorptance for s, p or unpolarised
  light; optical density; phase; ellipsometric Ψ and Δ; polarisation splitting and extinction
  ratio; group delay, GDD and third-order dispersion; and band averages such as solar absorptance
  and thermal emittance. Each target can be an exact value, a minimum, a maximum or a range, at one
  wavelength or across a band, at any angle or over a cone of angles.
- Five ways to optimise: **Refine** (damped least squares with analytic derivatives), **Global
  search**, **Needle optimisation**, **Gradual evolution**, which grows a design from almost
  nothing, and **Remove thin layers**. Every run shows its log and a before-and-after comparison
  before you keep it.
- Layer bounds, fixed layers and linked (picked-up) thicknesses; thicknesses in nm, QWOT or FWOT;
  stacks typed as design strings such as `(1H 1L)12 1H`.
- The substrate's back surface, angles given in air or in the incident medium, and designs
  immersed in glass or oil.

**Materials**
- A material database with Sellmeier, Cauchy and tabulated n,k materials, each with its valid range
  and its source, plus the SCHOTT, OHARA, HOYA, CDGM and SUMITA glass catalogues for substrates.
- New materials from published coefficients or from a measured n,k file; your own materials saved
  to your database for later designs.
- A design keeps its own copy of every material it uses, so it opens the same anywhere.
- The program says when a design reaches outside a material's measured range, on the plot and in
  the merit function, rather than extrapolating quietly.

**Exchanging designs**
- Import and export **OpticStudio** coating files (`COATING.DAT`), written into the shared file
  without touching its other coatings and materials.
- Import and export **Optiland** coatings, from a coating file or from any surface of a lens file.
- A design report: the layers in nm and every material as an n,k table, for any other program.

**Samples and documentation**
- 405 sample designs: 10 examples, the user guide's 25 designs in a folder per chapter, and 370
  designs rebuilt from 58 published papers (1952 to 2019), each with its citation.
- A user guide of seven worked designs from scratch, with screenshots, and a reference page for
  every window. It comes with the program: **Help > User guide** opens it in your browser. The same seven designs from the command line, each command with the result to
  expect.

## How we know the numbers are right

- 22 tests of the C++ engine, 3,009 program checks and 467 GUI checks, passing on Windows and on
  Linux, run before every release.
- Every derivative is checked against finite differences, with absorbing layers and at high
  angles.
- Reflectance and transmittance agree with an independent program, Optiland, to 1×10⁻¹⁰.
- Of the 370 literature designs, 319 are checked against the published results: 220 agree, 89
  are close, and the 10 that differ are each explained.

See [Verification](verification.md).

## Not in this release

- **Tolerancing** - what layer-thickness and index errors do to a design, by sensitivity and Monte
  Carlo - is planned for the next release.
- **Faster optimisation of lossless designs.** Spectra of lossless stacks already use a faster
  path (about 1.9×); the derivatives behind Refine do not yet, and would save about 14% of an
  optimisation. A single calculation for s and p at normal incidence is planned too.
- **Oscillator material models** (Lorentz, Drude, Tauc–Lorentz). Such a material can be imported
  as a measured n,k table.
- **Intel Macs** have no ready-made package; the program can be built from source for them.
