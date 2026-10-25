# The merit function

The merit function is the specification written as a table: what the coating must do, at which
wavelengths and angles, and how much each part matters. Every optimiser works to lower one
number computed from it, the **merit**. It is on the **Merit Function** tab.

## How the merit is computed

Each row computes a **value** from the design (a reflectance, a transmittance, a thickness) and
compares it with its **target** by its **mode**. How far it misses is its **error**:

- A row that meets its spec has an error of zero. A **Max** row whose value is under its target
  contributes nothing, however far under.
- The error is divided by the row's **budget**, a size chosen for it automatically, so that rows
  in different units can be added together (see below).
- The **weight** says how much the row matters relative to the others.

The merit is the weighted RMS of the rows:

    merit = sqrt( Σ weightᵢ × (errorᵢ / budgetᵢ)² / Σ weightᵢ )

Because it is divided by the sum of the weights, doubling every weight leaves the merit
unchanged: only the weights' ratios matter. A merit of 0 means every row is met. The optimisation
windows call it RMSE.

**The budget.** It is the room the row has: a reflectance held at most 0.5% has a budget of
0.005, and one held at least 95% has 0.05. A Target or Range row on R, T or A has 1% absolute. A
group delay has 1 fs, a GDD 10 fs², a TOD 100 fs³. MINT and MAXT use their limit. Anything else
uses the size of its target, or 1. So a row 0.5% over its limit counts the same whether the limit
is 0.5% reflectance or 95% transmittance. Changing a row's code, mode or limit picks a new budget.
The budget is not shown in the table.

## Modes

| Mode | The row is met when the value is | Error when it is not |
|---|---|---|
| **Target** | equal to the target | value − target |
| **Min** | at least the target | target − value |
| **Max** | at most the target | value − target |
| **Range** | between Target and Target 2 | the distance to the nearer end |
| **Report** | (never scored) | none; the value is shown but does not count |

Min, Max and Range are what a specification usually says ("R at most 0.5%"), and they stop
pulling on the design once they are met. Target pulls in both directions all the time; use it
for a value you want exactly, such as a 50% beam splitter. Report rows are for watching a
quantity without asking anything of it.

## Codes

### Reflectance, transmittance, absorptance

| Code | Quantity |
|---|---|
| **RS, RP, RA** | Reflectance: s-polarised, p-polarised, and the average of s and p (unpolarised light). |
| **TS, TP, TA** | Transmittance into the substrate: s, p, and the average. |
| **AS, AP, AA** | Absorptance, 1 − R − T: s, p, and the average. Zero unless a material absorbs. |

All are fractions: 0.005 is 0.5%. At 0° s and p are the same. With **Include the substrate's back
surface** ticked in [System Parameters](system-parameters.md), R and T include the back surface.

### Group delay and dispersion

For ultrafast mirrors and other designs where the phase matters. They need materials with
dispersion data (a model, not a table of constants).

| Code | Quantity |
|---|---|
| **R_GD_S, R_GD_P, T_GD_S, T_GD_P** | Group delay on reflection or transmission, s or p, in fs: dφ/dω, positive for a delay. |
| **R_GDD_S … T_GDD_P** | Group delay dispersion, in fs²: d²φ/dω². |
| **R_TOD_S … T_TOD_P** | Third-order dispersion, in fs³: d³φ/dω³. |

### Band averages

One row for a whole band: the quantity averaged over the band's points, weighted by a spectrum.
These rows have a second wavelength (Wv2) and a number of points (Pts).

| Code | Quantity |
|---|---|
| **ASOL** | Solar absorptance: A averaged against the AM1.5 global solar spectrum (ASTM G173-03). |
| **RSOL** | Solar reflectance, the same for R. |
| **TSOL** | Solar transmittance, the same for T. |
| **EMIT** | Thermal emittance: A averaged against a blackbody at the row's temperature (Temp, default 373.15 K). By Kirchhoff's law, absorptance is emittance. |
| **AERATIO** | ASOL / EMIT on the same points: the figure of merit of a solar absorber. |

### The stack

These describe the layers, not the light, so they have no wavelength or angle.

| Code | Quantity |
|---|---|
| **TOTT** | Total physical thickness of the stack. |
| **MINT** | Thin-layer penalty: how far each layer is below the row's **Limit**, summed. Zero when no layer is thinner. Starts at a limit of 8 nm, held at most 0. |
| **MAXT** | Thick-layer penalty: how far each layer is above the row's **Limit**, summed. Starts at 300 nm, held at most 0. |

## The table's columns

| Column | What it holds |
|---|---|
| **#** | The row number. Select a row to see it described in words above the table. |
| **Code** | The quantity, chosen from the list above. |
| **Wv** | The wavelength, in the design's length unit. Blank for a stack row. |
| **Wv2, Pts** | A band row's last wavelength and its number of points. Blank for a single wavelength. A band row of an ordinary code (RA, TA, …) is its average over the points, as a band code is: "RA at most 1% from 500 to 600 nm" in one row means the mean reflectance there. To hold every wavelength, use one row per wavelength (Add operands). |
| **AOI (°)** | The angle of incidence, in the incident medium or in air as [System Parameters](system-parameters.md) says. |
| **Mode** | Target, Min, Max, Range or Report. |
| **Target, Target 2** | The target; Target 2 is the upper end of a Range. |
| **Limit** | MINT and MAXT's thickness limit. |
| **Temp (K)** | EMIT and AERATIO's blackbody temperature. |
| **Weight** | Relative importance. 1 to begin with. |
| **Value** | What the design gives now. |

A cell that does not apply to its row is blank and cannot be edited.

## Building a merit function

A specification is usually many rows of one kind, one per wavelength. The buttons above the
table make them:

| Button | What it does |
|---|---|
| **Add operands…** | One row per wavelength from WV1 to WV2 every Step WV, all with the same code, mode, target and weight, at one angle. A band code (ASOL, EMIT, …) makes one row over the whole range instead. |
| **Add multiple AOI…** | The same, at every angle from AOI to End AOI every Step AOI: a row for each wavelength at each angle. For a cone of light, or a coating used over a range of angles. |
| **Add single Wv…** | One wavelength at many angles. |
| **Insert** | A copy of the selected row after it, to edit. The way to add a stack row (TOTT, MINT, MAXT): insert, then change its code. |
| **Remove** | The selected row. |
| **Clear all** | Every row. |
| **Delete operands…** | Every row a filter matches: a wavelength range, a code or family (R, T, A), a mode, an angle test (AOI above, below or between). |
| **Adjust weight…** | Gives every row the filter matches a new weight. |

The **Merit function** menu on the tab opens, appends, saves and exports a merit function on its
own, to reuse in another design. **Export as CSV with values** writes the table with each row's
value.

**Sample densely enough.** A Min or Max row is checked only at its own wavelength. A design can
meet every row and still miss its spec between them; chapter 2 shows a spike between sample
points. `thinfilm evaluate` lists any place a Min or Max row's spec is missed between its points.
Sample a steep edge or a narrow band every 1 nm or closer.

## More codes, from a file, the command line or the MCP server

The table's Code list holds the codes above. The engine has more, which a design file, the
`thinfilm merit` commands and the MCP server's `edit_operand` accept. They are for special
cases, and their values are in the engine's units: fractions, and angles in radians.

| Code | Quantity |
|---|---|
| **ODS, ODP, ODA** | Optical density, −log₁₀ T. For blocking specs: OD 5 and OD 7 differ by 100× in T but only 0.00001 in it. |
| **R_NPOL, T_NPOL** | Non-polarisation: \|Rs − Rp\|, \|Ts − Tp\|. For a non-polarising beam splitter. |
| **R_DIAT, T_DIAT** | Diattenuation, (p − s)/(p + s). |
| **R_EXT_DB, T_EXT_DB** | Extinction ratio in decibels, 10 log₁₀(s/p). For a polariser. |
| **R_PHASE_S … T_PHASE_P** | Absolute phase of r or t, s or p, in radians. |
| **R_TANPSI, R_COSDELTA, T_TANPSI, T_COSDELTA** | Ellipsometric tan Ψ and cos Δ. |
| **R_PSI, R_DELTA, T_PSI, T_DELTA** | Ψ and Δ in radians. Δ runs from −π to π; a Δ target is missed by the shortest way round, so a value of −179° against a target of +179° is 2° out. The absolute phases above are compared directly, so keep their targets away from ±π. |
| **NLAY** | The number of layers. |

The full description of every operand, including these, is
[the merit function specification](../Thin%20Film%20Design%20Software%20-%20Merit%20Function%20Specification.md).
It is the design document the program was built from, and it also describes operands that are
planned but not built.

## From the command line and the MCP server

| Table action | Command line | MCP |
|---|---|---|
| Add operands, Add multiple AOI, Add single Wv | `thinfilm merit add d.tfd --code TA --mode Min --target 0.99 --from 420 --to 510 --step 1` (`--aoi`, or `--aoi-from --aoi-to --aoi-step`) | `add_operands` |
| Edit a cell | `thinfilm merit edit d.tfd --row N --code RA --mode Max --target 0.005 ...` | `edit_operand` |
| Insert, Remove, Clear all | `thinfilm merit insert`, `merit remove`, `merit clear` | `edit_operand`, `clear_operands` |
| Delete operands, Adjust weight | `thinfilm merit delete`, `merit weight` with `--from --to --codes --modes --aoi-above --aoi-below` | `delete_operands`, `set_operand_weights` |
| Open, Save, Export | `thinfilm merit open`, `merit save`, `merit export` | `open_merit_function`, `save_merit_function`, `export_merit_csv` |
| See the values, and misses between points | `thinfilm evaluate d.tfd` | `evaluate_design`, `check_between_samples` |
