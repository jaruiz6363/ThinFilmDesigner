# The `thinfilm` command line

`thinfilm` does everything the GUI does, from a terminal or a script: create a design, build its
merit function, optimise it, and read its spectrum. It works on the same design files (`.tfd`)
as the GUI, and its optimisation runs print the same log lines the GUI's windows show. The CLI,
the GUI and the MCP server all call one shared API, so a design made in one opens in the others
and gives the same numbers.

This page is a guide. `thinfilm help` prints the full list of commands and options.

## Running it

| Platform | Installed | Built from source |
|---|---|---|
| Windows | `C:\Program Files\ThinFilmDesigner\thinfilm.exe`, or `thinfilm` if the installer added it to your PATH | `managed\ThinFilm.Cli\bin\Release\net8.0\thinfilm.exe` |
| macOS | `/Applications/ThinFilmDesigner.app/Contents/MacOS/thinfilm` | `managed/ThinFilm.Cli/bin/Release/net8.0/thinfilm` |
| Linux | `ThinFilmDesigner-x86_64.AppImage --cli` followed by the command | `managed/ThinFilm.Cli/bin/Release/net8.0/thinfilm` |

On Linux, to type `thinfilm` by name, put a two-line wrapper on your PATH, for example
`~/.local/bin/thinfilm`:

```sh
#!/bin/sh
exec /path/to/ThinFilmDesigner-x86_64.AppImage --cli "$@"
```

See [building.md](building.md) for building from source.

## How it works

- **One design file per command.** Most commands take a design file as their first argument,
  change it, and write it back. `-o other.tfd` writes the result to another file instead and
  leaves the original as it was.
- **Layers count from 1**, next to the incident medium, as the GUI's Design tab numbers them.
  So do merit rows. The MCP server counts the same way.
- **An option the command does not use is an error**, exit code 2, naming it: a misspelt
  `--renam` is said, not quietly dropped. A command that would write a file writes nothing.
- **Lengths** are in the design's length unit, nm unless `thinfilm units --wavelength um` was set.
- **`--json`** prints JSON instead of a table, for scripts.
- **Exit status** is 0 on success and 1 on any failure; the reason goes to standard error.

## A design from start to finish

The 532 nm laser notch of [chapter 4 of the user guide](user-guide/04-laser-notch.md):

```sh
thinfilm new --materials TiO2,SiO2 --substrate N-BK7 --name "Laser notch 532" \
             --reference 532 --layers 1 -o notch.tfd
thinfilm units notch.tfd --maximum 10000
thinfilm layer set notch.tfd --layer 1 --material SiO2 --thickness 8000 --unit nm
thinfilm merit clear notch.tfd
thinfilm merit add notch.tfd --code TA --mode Min --target 0.99  --from 420 --to 510 --step 1
thinfilm merit add notch.tfd --code TA --mode Max --target 0.001 --from 527 --to 537 --step 1
thinfilm merit add notch.tfd --code TA --mode Min --target 0.99  --from 555 --to 700 --step 1
thinfilm needle notch.tfd --insertions 100 --iterations 50
thinfilm thin notch.tfd --min-thickness 5 --raise --iterations 50
thinfilm evaluate notch.tfd
thinfilm spectrum notch.tfd --from 400 --to 720 --steps 3201 --quantities TA -o notch.csv
```

Every chapter of the [user guide](user-guide/README.md) ends with its command-line version, and
[cli-examples.md](cli-examples.md) has all six as scripts, each step with the result to expect.

## Designs

| Command | What it does |
|---|---|
| `new` | A design from the material database: `--materials`, `--substrate`, `--incident` (default air), `--name`, `--reference` (nm), `--layers` (alternating quarter waves). |
| `show` | The layers, counts and merit. |
| `describe` | The name, source and notes; `--name`, `--source`, `--notes` change them. |
| `samples [words]` | The sample designs that ship with the program, narrowed by words: `thinfilm samples polarizer`. |
| `database` | The materials a design can take, each with what it is (model, n at 550 nm, valid range, source). `database --save d.tfd --material HfO2` saves one of a design's materials into your own database for later designs (`--replace` to replace one of the same name). |
| `materials` | List a design's materials, each with what it is; add, remove or replace them, or change its media. `--rename OLD --to NEW` renames one, and `--upgrade` replaces every constant-index material with the database material of its name. `--define NAME --source TEXT` (or `--range lo,hi`) with no model changes only those and keeps the dispersion. `--define NAME` gives a material its dispersion, as Edit dispersion and New material do: `--sellmeier B1,C1,…` (C in µm²), `--cauchy A,B,C`, `--constant n,k` or `--table FILE --table-unit nm` (the unit may be left out when the file's header names it, such as `wavelength_nm`; a unit that contradicts the header is refused), with `--range lo,hi` (µm), `--source` and `--as substrate` or `--as incident`. |
| `units` | Thickness unit (nm, QWOT, FWOT), reference wavelength, wavelength unit, and the maximum for unconstrained layers. |
| `system` | Include the substrate's back surface; its thickness; angles given in air or in the incident medium. |

## The stack

| Command | What it does |
|---|---|
| `stack string --text "(1H 1L)4 1H"` | Replace the stack with a design string. `--letters H=TiO2,L=SiO2` names the materials; `--no-pickups` keeps each layer independent (needle and evolve need this). |
| `stack couple`, `decouple`, `clear` | Pick every layer of a material up from the first; free them; remove all layers. |
| `stack reverse [--media]` | Reverse the layers, and the media too with `--media`. |
| `stack bounds --min X --max Y` | Bounds on the thicknesses an optimiser may use, for some or all layers. They are true bounds, enforced by reflection: a step past a bound is mirrored back inside by the amount it overshot, not clamped to it (see [references.md](references.md#methods)). |
| `layer insert`, `remove`, `move`, `set` | One layer. `layer set --vary no` fixes a layer: no optimiser will change it. |

## The merit function

| Command | What it does |
|---|---|
| `merit add` | Rows from `--from` to `--to` every `--step` nm, at one angle (`--aoi`) or several (`--aoi-from --aoi-to --aoi-step`). `--code` RA, TA, RS, TP, AA, ODA and the rest; `--mode` Target, Min, Max, Range or Report. |
| `merit clear`, `delete`, `weight` | Remove all rows, or remove or reweight the rows a filter matches (`--from --to`, `--codes`, `--modes`, `--aoi-above`, `--aoi-below`). |
| `merit edit`, `insert`, `remove` | One row. |
| `merit save`, `open` | A merit function on its own, to reuse in another design. |
| `merit export` | The table as CSV, with each row's value and residual. |

The operand codes and modes are explained in [the user guide](user-guide/merit-function.md), and in full in
[the merit function specification](Thin%20Film%20Design%20Software%20-%20Merit%20Function%20Specification.md).

## Optimisation

| Command | Method |
|---|---|
| `refine` | Damped least squares on the layer thicknesses. `--norm L2`, `L1` or `minimax`. |
| `global` | Many starts, the best kept: `--mode random` or `--mode hop` (basin hopping). |
| `needle` | Inserts thin layers where the needle map says they help. `--insert TiO2,SiO2` limits the materials. |
| `evolve` | Gradual evolution: grows the design a layer at a time, needles and removes thin layers at each size. |
| `thin` | Removes every layer under `--min-thickness`, or with `--raise` raises each to it and fixes it. |
| `optimise --stages refine,needle,remove` | Several stages in one go, without a log: refine, global, needle, evolve (gradual evolution, as `thinfilm evolve`), remove (layers under `--min-thickness` while the merit allows) and thin (all of them, whatever it costs). |
| `needle-map` | Where a new layer would help, without inserting one. |

While a run goes, its log prints line by line, and it is also written to a log file. **Ctrl+C**
stops it and keeps the best design so far. `--revert` runs and logs but leaves the design as it
was, and `--compare` prints every merit row and every layer before and after. Each run is
recorded in the design's history.

## Results

| Command | What it does |
|---|---|
| `evaluate` | Every merit row's value and residual, and any place a Min or Max row's spec is missed between its sample points. |
| `spectrum` | R, T, A and more on a grid, as CSV, against wavelength or (`--vs angle --wavelength 550`) against angle. |
| `plot` | The spectrum the design opens to in the GUI's Spectrum tab. |
| `report` | The layers in nm and an n,k table for every material, for another program. |

## Other programs' files

| Command | What it does |
|---|---|
| `optiland list`, `import`, `export` | Optiland `ThinFilmCoating` JSON files. |
| `zemax list`, `import`, `export` | OpticStudio coating files (`COATING.DAT`). |
