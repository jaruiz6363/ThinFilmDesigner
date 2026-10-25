# GUI backlog

**Direction (2026-10-01): templates are to be phased out.** Done 2026-10-20 (X1.47): the ten are
sample design files in `samples/` (Examples and Literature, X1.56), opened with File > Open sample, and the template code is gone.

Findings from using the GUI by hand, in the order they will be built. Each item becomes a row in
[scope.md](scope.md) when it is built, and is struck through here.

1. ~~**Say what is shown**: substrate, which end is which, 1-based layers, what R and T mean~~ (X1.13)
2. ~~**Layout of the two tables**: merit table cut off whatever the window or splitter did,
   truncated headers, substrate label floating, operands not saying their wavelength, `met` as
   `True`/`False`~~ (X1.14)
3. ~~**AR-visible starts from a reflector.** Its starting stack is two quarter-wave H/L pairs at
   520 nm, and HLHL is a mirror: R reaches 0.65. The default stage, `refine`, stalls in a local
   minimum at 15.7% peak R against a 0.5% target. `global,refine` reaches 5.1%, and
   `refine,needle,evolve` meets it (0.50%, 8 layers). A person who opens the template and presses
   Optimise sees it fail. Wants a start that is already an AR (e.g. a quarter-half-quarter
   layout), or a default stage that can escape.~~ (X1.15)
4. ~~**A tabbed window**, one panel at a time, after LensHH-LT: a menu bar, and Design / Merit
   Function / Spectrum tabs each with its own button strip, and a help bar on the merit table~~
   (X1.17). Still to come, each with the editor that needs it:
   - Modal dialogs for the editors, each taking a snapshot on opening so that Cancel really
     reverts. LensHH-LT's Variable Editor writes through, and its Cancel undoes nothing.
   - Analysis tabs (R/T vs angle, n and k, needle map) opened from the Analysis menu, closable.
   - Optimisation off the UI thread, in an Optimise dialog with progress and Accept / Revert.
5. **Explain what is on screen**, the small items from the third round of use:
   - Plot quantities should suit the design (AR: RA only, not RA,TA) - but NOT per template, since
     templates are being phased out; to be decided with the user.
   - ~~An angle-of-incidence box for the plot~~ (AOI box on the Spectrum tab). Still: s/p/average
     without typing RS/RP into "show".
   - ~~Stack operands say what they hold~~ (X1.17 help bar; X1.18 Limit column)
   - Tooltips for the Stages list (refine, global, needle, evolve) and the Norm list (L2, L1,
     minimax). Minimax is labelled for what it is: L2 plus worst-case weighting to p = 3.
   - ~~L2 as every template's default norm~~ (X1.16); ~~"as template" removed~~
6. ~~**Layer editing** (Design tab): vary checkbox, material dropdown, add / insert / delete / move
   up / move down~~ (X1.19). ~~A `...` per row opening a Layer properties dialog~~ (X1.25), with
   ~~thicknesses in nm, QWOT or FWOT at a reference wavelength~~ (X1.25)
7. **Merit-function editing** (Merit Function tab):
   - ~~Fixed columns after LensHH-LT, cells blank and disabled where they do not apply, Code and
     Mode drop-downs, Insert / Remove, wavelength unit in Edit > Preferences~~ (X1.18)
   - ~~**Weight is always visible and editable** in the table, and every target generator takes a
     weight as one of its inputs. (The user's rule, 2026-09-28.)~~ (Adjust weight, 2026-10-13)
   - ~~Target generators: helpers that fill rows, e.g. over a wavelength range x angle range (as
     ARCoatingDesigner's GenerateMeritDialog).~~ (Add operands / Add multiple AOI / Add single Wv,
     2026-10-13)
   - ~~A New design dialog with the media and materials chosen by the user~~ (X1.20); ~~add a
     material to a design after it is made~~ (Editors > Materials, Copy into design, X1.22)
8. ~~**Per-layer thickness bounds**: in Layer properties and a Variable Editor dialog, saved in the
   design file~~ (X1.29: Layer properties, and Stack ▸ Set vary constraints for a range)
9. ~~**Thickness pickups**: layer *n* = factor x layer *m*, set in Layer properties, handled by the
   optimiser~~ (X1.25: scale and offset, both layers in one unit)
10. **Real materials** - decided with the user 2026-10-02, to be built in this order:
    1. ~~**A design carries its materials' definitions, not samples.**~~ (X1.21) A material in the design file
       holds its model - SELLMEIER (n^2 = 1 + sum B lambda^2/(lambda^2 - C), lambda in um, C in
       um^2), CAUCHY (n = A + B/lambda^2 + C/lambda^4), TABLE (lambda, n, k), or AGF (formula number
       and coefficients) - and a valid range, and is evaluated onto whatever grid the design has.
       Outside its range it is FLAGGED, never silently extended (C0.8). Old files open, their
       sampled values becoming TABLE materials.
    2. ~~**One material database file, our own text format**~~ (X1.22) (all materials in one file, in the
       spirit of ZEMAX catalogues). **Choosing a material copies it into the design**, and the user
       edits the copy: the design and its materials live in one file, so changing the database
       later can never break a design. Editors > Materials... shows database and design side by
       side, with Copy into design and an n,k preview. The working database lives in the user
       profile, its path in Preferences. **Ships with the seven ARCoatingDesigner materials** -
       MgF2, SiO2, Al2O3 (Sellmeier), TiO2, Ta2O5, ZrO2, HfO2 (Cauchy) - and **no constant-n,k
       material**. **Air is the one built-in exception**, n = 1 exactly.
    3. ~~**A substrate is an AGF glass or any database material.**~~ (X1.23) **An AGF glass is NOT copied and
       NOT editable** (the user, 2026-10-02): the design merely names it - catalogue and glass, e.g.
       SCHOTT N-BK7 - and its index always comes from the catalogue; Editors > Materials shows it
       read-only. (Database materials, by contrast, are copied in and edited there.) The vendor AGF catalogues
       (SCHOTT, HOYA, OHARA, CDGM, SUMITA, downloaded from the vendors and already distributed by
       the user) are committed to the repository. A glass that cannot be computed is refused with
       a reason - ARCoatingDesigner's parser silently substitutes n = 1.5, which is not copied.
    4. **File > Export > ZEMAX coating file**: MATE blocks with n,k tables only (all ZEMAX needs),
       and the stack, at wavelengths the user chooses.
    5. **An n,k analysis tab** (as ARCoatingDesigner's IndexPlotDialog).
    6. **Metals must ship** (Ag, Al, Au, ...), as TABLE materials from Palik data the user
       provides. Done 2026-10-23: Ag Al Au Cr Cu CuO CdTe CdSe GaAs Ge InAs InP MgO Mo Si3N4 SiO Y2O3 Pt Pd TiN LiNbO3 MgF2, and a
       person's unchanged working copy picks up what a new release ships.
11. **A step limit in refine, so the maximum for unconstrained layers can go** (decided with the
    user 2026-10-16, "option 3": the ceiling is shown in Preferences now, the optimiser fix later).
    Refine scales each step by diag(J^T J), near zero at a flat start (a quarter-wave stack at its
    own reference), so one step runs hundreds of nm into another basin; the ceiling is what stops
    it today. Tried: shorten any step longer than reference/8 along its own direction, with no
    ceiling. Every test passed, and the templates changed both ways - Dichroic 1.33 -> 0.10,
    Reverse-engineering 40.6 -> 4.4, Bandpass 53 -> 26, but Chirped-mirror 1.41 -> 1.74 and PBS
    global,refine 2.6e-15 -> 0.0027 (iteration limit). Raising the damping instead of shortening
    stalled the chirped mirror. To be evaluated on the user's own designs before it ships.
12. **Each optimisation in a window of its own** (the user, 2026-10-17, after LensHH-LT; replaces
    the Stages box and Optimise button on the Design tab). Decided: one modal window per method;
    the log, elapsed time, start and end RMSE shown live, written to a file per run, and every kept
    or reverted run saved in the design's History; per-iteration lines for refine, improvements and
    events for the others; one runner in ThinFilmApi (Runs.cs) under the GUI, the CLI and the MCP.
    - ~~Refine~~ (X1.32)
    - ~~Global search, Needle, Gradual evolution: a window each, then the Stages box and Optimise
      button go~~ (X1.33)
    - ~~Before and after: Log, Merit function, Spectrum and Layers tabs; Revert keeps the window
      open to start again; Close, Escape and the title bar's X stop the run~~ (X1.39)
    - ~~Then the stack and layer operations in the CLI and MCP (design string, couple, decouple,
      delete all, vary constraints, layer properties, units, unconstrained maximum, materials,
      per-row operand edits)~~ (X1.40)
