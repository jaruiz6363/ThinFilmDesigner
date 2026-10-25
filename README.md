# ThinFilmDesigner

A thin-film optical coating design program for Windows, macOS on Apple Silicon (native, M1 and
later) and Linux. Design antireflection coatings, bandpass, edge and notch filters, mirrors,
polarisers and beam splitters: build a merit function, optimise with damped least squares, global
search, needle optimisation and gradual evolution, and read the spectrum. One shared API drives three programs:

- **the GUI**, `thinfilm-gui`;
- **the command line**, `thinfilm`, for scripts;
- **an MCP server**, `thinfilm-mcp`, so an AI assistant can design coatings with the program.

It ships with 370 designs rebuilt from 58 published papers, and reproduces the published
results of over 250 of them.

## Documents

| | |
|---|---|
| [Release notes](docs/release-notes.md) | What 1.0.0 does, the downloads, and what is left for later. |
| [User guide](docs/user-guide/README.md) | Seven real designs from scratch, click by click, and a page for every window. |
| [Installing and building](docs/building.md) | The installers, and building on Windows, macOS and Linux, the C++ engine included. |
| [The command line](docs/cli.md) | `thinfilm`. |
| [The user guide from the command line](docs/cli-examples.md) | The seven designs as command scripts, each with the results to expect. |
| [The MCP server](docs/mcp.md) | Connecting `thinfilm-mcp` to an AI assistant, and its tools. |
| [Verification](docs/verification.md) | How we know the numbers are right: tests, an independent program, and the literature. |
| [References](docs/references.md) | The papers, methods and material data. |

## Shape

| | |
|---|---|
| **C#** | GUI (Avalonia), CLI, MCP server, API, and the whole optimiser |
| **C++** | the calculation engine, later ported to CUDA |

## The rules

These are constraints on every line of native code, not preferences.

1. **All memory is dimensioned and allocated in C# and passed to C++.** The native evaluator
   allocates nothing.
2. **No fixed-size arrays dimensioned by a constant.** No `MAX_LAYERS`, no `MAX_SURFACES`. Finding
   one is a defect report, not a shortcut taken.
3. **The port to CUDA must be mechanical.** Nothing may be written whose translation to a kernel
   requires a redesign.
4. **Double precision.** Single was tried in the predecessor code and was not good enough.
5. **An unimplemented path is loud** — a diagnostic, a distinct status code, and output buffers
   left untouched.

Rules 1 and 3 are the same rule. A function that allocates nothing, holds no state and touches
only caller-supplied buffers is what a kernel is.

## Where to start reading

[docs/abi-and-allocation-contract.md](docs/abi-and-allocation-contract.md) — the three calls, the
buffer list, the evaluation paths, why the Jacobian is column-major, and where analytic
differentiation stops and automatic differentiation begins.

**Merit and derivatives are two calls, not one.** `tf_evaluate` returns the merit and the residuals
without derivatives, and is what runs most often: every trial step of the optimiser, every spectrum,
the merit table. `tf_evaluate_jacobian` adds the Jacobian, and runs once per accepted optimiser step.
The contract document says which part of the program uses which.

## Layout

```
docs/       documents, the user guide, and the design documents
native/     C++ evaluation engine
managed/    C# — optimiser, GUI, CLI, MCP, API, and their tests
tests/      the engine's tests, and fixtures both sides read
samples/    worked examples and the designs from the literature
materials/  the material database and glass catalogues
```

## Authors

Javier Ruiz and Claude Code. See [AUTHORS](AUTHORS).

## Licence

MIT, copyright (c) 2026 Javier Ruiz. See [LICENSE](LICENSE).
