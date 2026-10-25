# ThinFilmDesigner user guide

This guide teaches the program by designing real coatings from scratch. Each chapter starts
from an empty design and ends with a finished one you can open. Every step was run on the
program as it is now.

The emphasis is on what is hard: filters with many cavities, steep edges and wide blocking,
and polarising beam splitters. These are designs you synthesise, not ones you write down.

| Chapter | Design | What it shows |
|---|---|---|
| [1. A narrow bandpass filter from one thick layer](01-narrow-bandpass.md) | 8 nm bandpass at 550 nm, Ta2O5/SiO2 on N-BK7 | the merit function, needle optimisation from a single layer, removing thin layers |
| [2. A polarising beam splitter plate immersed in oil](02-pbs-plate.md) | Ts ≤ 0.1%, Tp ≥ 95% from 430 to 680 nm over an f/4.8 cone at 54°, TiO2/SiO2 in BK7-matched oil | s and p operands, glass on both sides, a cone of angles, when needle is stationary, leaks between sample points |
| [3. A dichroic edge filter at 45°](03-dichroic-edge.md) | reflect 420–580 nm, transmit 610–750 nm, unpolarised at 45° in air, TiO2/SiO2 on N-BK7 | unpolarised light (TA), s and p edges, evolving to the spec, removing thin layers so it can be made, the edge against angle |
| [4. A laser notch filter](04-laser-notch.md) | block 527–537 nm to OD 3, pass 99% from 420–510 and 555–700 nm, TiO2/SiO2 on N-BK7 | how edge steepness costs layers, thin layers that do real work, raising them to the minimum and fixing them, tilt tuning |
| [5. An induced-transmission filter](05-induced-transmission.md) | pass 530–570 nm at 60%, block 400–480 and 640–1000 nm to 1%, two silver layers between TiO2/SiO2 | a metal in the design, a structured start from a design string, absorption (AA), keeping needle from inserting silver, blocking into the infrared |
| [6. An antireflection coating for a wide range of angles](06-wide-angle-ar.md) | R ≤ 0.5% from 420 to 680 nm at 0° to 45°, TiO2/SiO2 under an outer MgF2 layer on N-BK7 | Add multiple AOI, gradual evolution from one layer, when a step comes back, keeping a material to the outer layer, reflectance against angle, what the angles cost |
| [7. Materials: an antireflection coating on silicon](07-materials.md) | R ≤ 2% from 450 to 800 nm on silicon, Si3N4/SiO2/TiO2 | the material database and the design's copies, a new substrate from a measured n,k table, a coating material from published Sellmeier coefficients (squaring C), saving to your database, valid ranges |

The designs from each chapter come with the program: **File > Open sample…** lists them under
**User guide**, a folder per chapter, each with what it is. They are also in
[samples/User guide](../../samples/User%20guide) in the source.

[Layer properties](layer-properties.md) explains a layer's material, unit, thickness, Vary, min/max and pickup, and Set vary constraints for many layers. [Input design string](design-string.md) explains how to write a stack as text, such as (1H 1L)12, and every choice in the dialog. [The merit function](merit-function.md) explains how the merit is computed, every operand code and mode, and the table's buttons. [The optimisation windows](optimisation-windows.md) explains every setting of Refine, Global search, Needle optimisation, Gradual evolution and Remove thin layers, and what the windows show. [The File menu](file-menu.md) explains the sample designs, exchanging coatings with OpticStudio and Optiland, and the design report. [The Spectrum tab](spectrum.md) explains the plot's settings, the note under it, every quantity it can show, plotting against angle, and Export. [Edit > System Parameters](system-parameters.md) explains each of the design-wide settings: units, the reference wavelength, the maximum for unconstrained layers, the substrate's back surface, angles in air, and the material database.

In the program, **Help > User guide** opens this guide in your browser.

Everything shown in the GUI can also be done with the `thinfilm` command line and the MCP
server. Each chapter ends with its command-line version.
