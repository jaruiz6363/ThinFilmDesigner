# The user guide's designs, from the command line

Each chapter of the [user guide](user-guide/README.md) builds a design in the GUI. This page
builds the same seven designs with `thinfilm`, the command line, and gives the result each step
should print. Every command was run as written here, and every result matches the GUI's.

The commands are one per line, with nothing to continue a line, so they work as they are in
PowerShell, cmd, bash and zsh. Run them in an empty folder: each chapter writes one design file
there. See [cli.md](cli.md) for the commands themselves and [building.md](building.md) for where
`thinfilm` is.

## Reading the results

- **Every command writes its result back into the design file**, the optimisations included.
  `-o other.tfd` writes it to another file instead, and `--revert` runs an optimisation and
  leaves the file as it was.
- **Optimisation commands** (`refine`, `needle`, `evolve`, `thin`) print a log as they run. The
  lines to check are `end` (the merit reached, and the layers) and, if there is one, `done` or
  `target met` (why it stopped).
- **The merit** is printed as `RMSE`. Smaller is better; under about 1E-6 the spec is met.
- **Results are deterministic.** The same commands give the same numbers every time, on every
  computer the program is built for; the last figure or two can differ between compilers.
- `thinfilm show design.tfd` prints the layers at any point, and `thinfilm evaluate design.tfd`
  prints every merit row and any place the spec is missed between sample points.

## 1. A narrow bandpass filter from one thick layer

[Chapter 1](user-guide/01-narrow-bandpass.md). TA at least 95% from 546 to 554 nm, at most 0.5%
from 450 to 535 and 565 to 650 nm, Ta2O5/SiO2 on N-BK7.

```
thinfilm new --materials Ta2O5,SiO2 --substrate N-BK7 --name "Narrow bandpass 550" --reference 550 --layers 1 -o bp.tfd
thinfilm units bp.tfd --maximum 5000
thinfilm layer set bp.tfd --layer 1 --material SiO2 --thickness 2000 --unit nm
thinfilm merit clear bp.tfd
thinfilm merit add bp.tfd --code TA --mode Min --target 0.95 --from 546 --to 554 --step 1
thinfilm merit add bp.tfd --code TA --mode Max --target 0.005 --from 450 --to 535 --step 1
thinfilm merit add bp.tfd --code TA --mode Max --target 0.005 --from 565 --to 650 --step 1
thinfilm needle bp.tfd --insertions 40 --iterations 50
thinfilm evolve bp.tfd --growth-steps 2 --min-thickness 15 --accept-merit 0.1
thinfilm spectrum bp.tfd --from 440 --to 680 --steps 24001 --quantities TA -o bp.csv
```

| Step | Expect |
|---|---|
| `needle` | `end RMSE=5.283714E-07 (28 inserted, 0 rejected, 57 layers)` |
| `evolve` | `end RMSE=1.677001E-02 (... 52 layers, ... 3 layers removed; merit 0.0168, under the 0.1 accepted)` |

## 2. A polarising beam splitter plate immersed in oil

[Chapter 2](user-guide/02-pbs-plate.md). Ts at most 0.1% and Tp at least 95% from 430 to 680 nm,
from 50° to 58° in BK7-matched oil (the medium on both sides is N-BK7).

```
thinfilm new --materials TiO2,SiO2 --incident N-BK7 --substrate N-BK7 --name "PBS plate in oil" --reference 550 --layers 1 -o pbs.tfd
thinfilm units pbs.tfd --maximum 5000
thinfilm layer set pbs.tfd --layer 1 --material SiO2 --thickness 2000 --unit nm
thinfilm merit clear pbs.tfd
thinfilm merit add pbs.tfd --code TS --mode Max --target 0.001 --from 430 --to 680 --step 2 --aoi-from 50 --aoi-to 58 --aoi-step 2
thinfilm merit add pbs.tfd --code TP --mode Min --target 0.95 --from 430 --to 680 --step 2 --aoi-from 50 --aoi-to 58 --aoi-step 2
thinfilm needle pbs.tfd --insertions 60 --iterations 50
thinfilm evolve pbs.tfd --growth-steps 12 --min-thickness 15 --iterations 50
thinfilm evaluate pbs.tfd
thinfilm spectrum pbs.tfd --from 430 --to 680 --steps 2501 --angle 50 --quantities TS,TP -o pbs-50.csv
```

| Step | Expect |
|---|---|
| `needle` | `stationary no insertion helps to first order`, then `end RMSE=5.084722E-01 (18 inserted, 0 rejected, 37 layers)` |
| `evolve` (one to two minutes) | several `came back` lines, then `end RMSE=2.993414E-01 (best at step 12 of 12: 29 layers, ...)` |
| `evaluate` | `merit 0.299341 1260 operands, 0 flagged` |

The spec is not met at the edges of the cone; chapter 2 explains why.

## 3. A dichroic edge filter at 45°

[Chapter 3](user-guide/03-dichroic-edge.md). Unpolarised light at 45°: TA at most 1% from 420
to 580 nm, at least 97% from 610 to 750 nm.

```
thinfilm new --materials TiO2,SiO2 --substrate N-BK7 --name "Dichroic edge filter" --reference 550 --layers 1 -o dichroic.tfd
thinfilm units dichroic.tfd --maximum 5000
thinfilm layer set dichroic.tfd --layer 1 --material SiO2 --thickness 3000 --unit nm
thinfilm merit clear dichroic.tfd
thinfilm merit add dichroic.tfd --code TA --mode Max --target 0.01 --from 420 --to 580 --step 2 --aoi 45
thinfilm merit add dichroic.tfd --code TA --mode Min --target 0.97 --from 610 --to 750 --step 2 --aoi 45
thinfilm needle dichroic.tfd --insertions 80 --iterations 50
thinfilm evolve dichroic.tfd --growth-steps 20 --min-thickness 10 --iterations 50
thinfilm evolve dichroic.tfd --growth-steps 20 --min-thickness 10 --iterations 50 --accept-merit 0
thinfilm thin dichroic.tfd --min-thickness 10 --iterations 50
thinfilm evaluate dichroic.tfd
thinfilm spectrum dichroic.tfd --from 400 --to 800 --steps 4001 --angle 45 --quantities TA,TS,TP -o dichroic-45.csv
```

| Step | Expect |
|---|---|
| `needle` | `end RMSE=1.976233E+00 (23 inserted, 0 rejected, 47 layers)` |
| first `evolve` | `end RMSE=5.402447E-02 (best at step 14 of 14: 35 layers, ...; merit 0.054, under the 0.1 accepted)` |
| second `evolve` | `end RMSE=7.065894E-12 (best at step 3 of 3: 49 layers, ...; target met)` |
| `thin` | `end RMSE=1.970418E-07 (6 removed, 6 with neighbours joined, 37 layers)` |

## 4. A 532 nm laser notch filter

[Chapter 4](user-guide/04-laser-notch.md). TA at most 0.1% from 527 to 537 nm, at least 99% from
420 to 510 and 555 to 700 nm.

```
thinfilm new --materials TiO2,SiO2 --substrate N-BK7 --name "Laser notch 532" --reference 532 --layers 1 -o notch.tfd
thinfilm units notch.tfd --maximum 10000
thinfilm layer set notch.tfd --layer 1 --material SiO2 --thickness 8000 --unit nm
thinfilm merit clear notch.tfd
thinfilm merit add notch.tfd --code TA --mode Min --target 0.99 --from 420 --to 510 --step 1
thinfilm merit add notch.tfd --code TA --mode Max --target 0.001 --from 527 --to 537 --step 1
thinfilm merit add notch.tfd --code TA --mode Min --target 0.99 --from 555 --to 700 --step 1
thinfilm needle notch.tfd --insertions 100 --iterations 50
thinfilm thin notch.tfd --min-thickness 5 --iterations 50 --revert
thinfilm thin notch.tfd --min-thickness 5 --raise --iterations 50
thinfilm evaluate notch.tfd
```

| Step | Expect |
|---|---|
| `needle` | `end RMSE=1.956245E-06 (28 inserted, 0 rejected, 57 layers)` |
| `thin --revert` | `end RMSE=4.790260E-01 (6 removed, 6 with neighbours joined, 45 layers)`: removing the thin layers costs the spec, so the design is left as it was |
| `thin --raise` | `end RMSE=8.260983E-06 (8 raised to 5 nm and fixed, in 2 rounds)` |

## 5. An induced-transmission filter: light through silver

[Chapter 5](user-guide/05-induced-transmission.md). TA at least 60% from 530 to 570 nm, at most
1% from 400 to 480 and 640 to 1000 nm, two silver layers between TiO2/SiO2.

```
thinfilm new --materials TiO2,SiO2,Ag --substrate N-BK7 --name "Induced transmission 550" --reference 550 --layers 1 -o itf.tfd
thinfilm stack string itf.tfd --text "1H 1L 1H 1A 2H 1A 1H 1L 1H" --letters H=TiO2,L=SiO2,A=Ag --no-pickups
thinfilm layer set itf.tfd --layer 4 --thickness 35 --unit nm
thinfilm layer set itf.tfd --layer 6 --thickness 35 --unit nm
thinfilm merit clear itf.tfd
thinfilm merit add itf.tfd --code TA --mode Max --target 0.01 --from 400 --to 480 --step 5
thinfilm merit add itf.tfd --code TA --mode Min --target 0.6 --from 530 --to 570 --step 2
thinfilm merit add itf.tfd --code TA --mode Max --target 0.01 --from 640 --to 1000 --step 10
thinfilm refine itf.tfd --iterations 300
thinfilm needle itf.tfd --insertions 10 --iterations 100 --insert TiO2,SiO2
thinfilm thin itf.tfd --min-thickness 5
thinfilm spectrum itf.tfd --from 400 --to 2500 --steps 2101 --quantities TA,RA,AA -o itf.csv
```

| Step | Expect |
|---|---|
| `refine` | `end RMSE=6.038189E-03 (Converged, 212 iterations)` |
| `needle` | `insert 1 TiO2 ...`, `insert 2 SiO2 ...`, then `end RMSE=4.031684E-11 (2 inserted, 0 rejected, 13 layers)`. Without `--insert TiO2,SiO2` needle inserts a 2 nm silver layer, which cannot be made. |
| `thin` | `end RMSE=8.187374E-11 (1 removed, 1 with neighbours joined, 11 layers)` |

## 6. An antireflection coating for a wide range of angles

[Chapter 6](user-guide/06-wide-angle-ar.md). RA at most 0.5% from 420 to 680 nm at 0°, 15°, 30°
and 45°, with MgF2 only as the outer layer.

```
thinfilm new --materials MgF2,SiO2,TiO2 --substrate N-BK7 --name "Wide-angle AR" --reference 550 --layers 1 -o ar.tfd
thinfilm merit clear ar.tfd
thinfilm merit add ar.tfd --code RA --mode Max --target 0.005 --from 420 --to 680 --step 5 --aoi-from 0 --aoi-to 45 --aoi-step 15
thinfilm evolve ar.tfd --grow-at substrate --insert TiO2,SiO2 --accept-merit 0
thinfilm spectrum ar.tfd --vs angle --from 0 --to 70 --steps 71 --wavelength 550 --quantities RA,RS,RP -o ar-angle.csv
```

| Step | Expect |
|---|---|
| `new` | layer 1 is a quarter wave of MgF2, 99.7 nm |
| `evolve` | a `came back` line, then `end RMSE=2.143081E-07 (best at step 3 of 3: 14 layers, ...; target met)` |

`--grow-at substrate` grows new layers next to the glass, away from the MgF2, and
`--insert TiO2,SiO2` keeps needles and grown layers to those two materials, so MgF2 stays the
outer layer only.

## 7. Materials: an antireflection coating on silicon

[Chapter 7](user-guide/07-materials.md). Silicon as a new substrate from a measured n,k table,
ZnSe typed in from published Sellmeier coefficients, both kept in your material database, then RA
at most 2% from 450 to 800 nm on silicon. Copy
[Si-Green-2008.csv](user-guide/data/Si-Green-2008.csv) into the folder first.

```
thinfilm new --materials Si3N4,SiO2 --substrate N-BK7 --name "Si photodiode AR" --reference 650 --layers 2 -o si.tfd
thinfilm materials si.tfd
thinfilm materials si.tfd --define Si --as substrate --table Si-Green-2008.csv
thinfilm materials si.tfd --define Si --source "M. A. Green, Sol. Energy Mater. Sol. Cells 92, 1305 (2008)"
thinfilm database --save si.tfd --material Si
thinfilm materials si.tfd --define ZnSe --sellmeier 4.45813734,0.04034468055,0.467216334,0.1531713896,2.89566290,2221.822369 --range 0.54,18.2 --source "Connolly 1979; Tatian 1984 (CVD ZnSe), C squared from the published wavelengths"
thinfilm database --save si.tfd --material ZnSe
thinfilm materials si.tfd --remove ZnSe
thinfilm spectrum si.tfd --from 400 --to 1000 --steps 61 -o past.csv
thinfilm materials si.tfd --add TiO2
thinfilm stack string si.tfd --text 1L --letters L=SiO2
thinfilm merit clear si.tfd
thinfilm merit add si.tfd --code RA --mode Max --target 0.02 --from 450 --to 800 --step 5
thinfilm evolve si.tfd --min-thickness 10
```

| Step | Expect |
|---|---|
| `materials` | each material with what it is: `Si3N4  1 layer   measured table, n = 2.0327 at 550 nm, 51.7–826.6 nm; Palik, ...` |
| `--define Si --as substrate` | `Si: table, the substrate`. The file's header says `wavelength_nm`, so no `--table-unit` is needed; `--table-unit um` would be refused. |
| `--define Si --source` | `Si: its dispersion kept, source set`: only the source changes, as in Edit dispersion |
| `database --save` | `Si saved to ...materials.txt`; then `ZnSe saved to ...` |
| `spectrum` to 1000 nm | `caution: Si3N4 is outside its valid range (51.7–826.6 nm) at 830–1000 nm; ...` |
| `merit add` | `71 rows added; 71 operands, merit 5.87072` |
| `evolve` | `end RMSE=8.953337E-03 (best at step 1 of 1: 7 layers, 8 needles kept, 9 layers removed; ...)` |

The Materials window's other buttons have their commands too: `--rename SiO2 --to Silica` (Rename),
`--upgrade` (Upgrade constant materials), and `--replace X --with Y` (Replace with database
material). `thinfilm database` lists your database the same way, each material with what it is.

## The same with the MCP server

An AI assistant connected to the MCP server ([mcp.md](mcp.md)) does the same with tools instead
of commands. They correspond one to one:

| Command | MCP tool |
|---|---|
| `new` | `new_design` |
| `materials` (list, `--add`, `--remove`, `--replace`, `--rename`, `--upgrade`) | `list_materials`, `edit_materials` |
| `materials --define` | `define_material` |
| `database --save` | `save_material_to_database` |
| `units` | `set_units` |
| `layer set`, `layer insert` | `edit_layer` |
| `stack string` | `edit_stack` with `action` "string" |
| `merit clear`, `merit add` | `clear_operands`, `add_operands` |
| `refine`, `needle`, `evolve`, `thin` | `optimise_start` with that `method`, then `optimise_status` and `optimise_keep` |
| `evaluate`, `spectrum` | `evaluate_design`, `spectrum` |
| `show` | `describe_design` |

One difference: a design lives in the server's memory until `save_design` returns it. Both
count layers and merit rows from 1.
