# Edit > System Parameters

**Edit > System Parameters…** holds the settings that decide how a design is shown, how its
layers may be optimised, and how its spectrum is calculated. Every setting but the last belongs
to the open design and is saved with it. The material database is one setting for all designs.

![System Parameters](images/01-system-parameters.png)

**OK** makes the changes and closes the window. **Apply** makes them and leaves it open, so you
can see the effect before going on. **Cancel** closes it without making the changes on screen;
anything already applied stays.

## Length unit

**Nanometres** or **micrometres.** The unit every wavelength and physical thickness is shown and
typed in: the spectrum's axis, the layer table, Layer properties, and the merit table's limits.
Micrometres suit infrared designs. This changes how numbers are shown, never the design itself.

## Layer thickness

**Physical, QWOT or FWOT.** The unit layer thicknesses are shown and typed in, unless a layer
names its own unit in Layer properties.

- **Physical:** the thickness in nm (or µm).
- **QWOT** (quarter-wave optical thickness): 4nd/λ. A layer of 1 QWOT is a quarter wave thick at
  the reference wavelength.
- **FWOT** (full-wave optical thickness): nd/λ, so 1 FWOT is 4 QWOT.

Here n is the layer's own index at the reference wavelength and d its physical thickness.

**Reference wavelength.** The wavelength QWOT and FWOT are counted at. Changing it keeps every
QWOT and FWOT *number*, so those layers change physical thickness and the spectrum moves with
them. That is how you shift a quarter-wave design to a new wavelength. To keep the spectrum where
it is instead: choose **physical**, press **Apply**, set the new reference, choose **QWOT** again
and press **OK**.

**Maximum for unconstrained layers.** The thickest an optimiser may make a layer that has no
maximum of its own. It is 600 nm in a new design, or the reference wavelength if that is longer.
Needle and evolve start from thick layers, so the chapters of this guide raise it first. If a
layer is already thicker than the maximum, the optimisation windows refuse to start and say which
layer and what to do. A layer with no minimum of its own has none but zero.

A layer's own minimum and maximum are set in Layer properties (**Set vary constraints**). Both
kinds of limit are true bounds: an optimiser step that would cross one is reflected back inside
by the amount it overshot (see [references](../references.md#methods)).

## Substrate and angle of incidence

**Include the substrate's back surface.** Off, the substrate is taken as infinitely thick: the
light that enters it never comes back, which is what a coating on one face of a thick block
gives. On, R and T include the uncoated back face too, with the incident medium behind it. The
two faces are added incoherently, as for any substrate much thicker than the light's coherence
length, so there are no fringes. Absorptance, optical density and the s/p averages follow from
the new R and T. Turn it on to compare with a measured plate. Psi, Delta and the phases always
describe the coated surface alone.

**Substrate thickness (mm).** Used only with the back surface on, and it matters only when the
substrate absorbs: each pass through it is attenuated by its k.

**Angles of incidence are given in air.** Off (the default), every angle in the design, in the
merit function and on the spectrum, is the angle in the incident medium itself. On, an angle is
the angle in air, and the program converts it by Snell's law at each wavelength: sin θ(medium) =
sin θ(air) / n(medium).

- With air as the incident medium, the setting makes no difference.
- With glass or oil as the incident medium, use off when you know the angle inside it. Light
  coming from air reaches at most about 41° inside N-BK7, so angles given in air cannot
  describe a cube beam splitter's 45° in glass; a cube needs off. Chapter 2 gives its angles in
  the oil, so it leaves this off too.
- Use on when the specification gives the angle in air, for example a plate tilted in a beam
  in air but measured in glass.

## Material database

**All designs.** The file that **File > New design** and **Edit > Materials** take materials
from. Choosing a material copies it into the design, so changing or replacing this file never
alters a design already made. `thinfilm database` prints which file is in use; see
[building.md](../building.md#where-the-program-keeps-your-files) for where it is kept.

## From the command line and the MCP server

| Setting | Command line | MCP |
|---|---|---|
| Length unit | `thinfilm units d.tfd --wavelength nm\|um` | `set_units`, `wavelength_unit` |
| Thickness unit, reference | `thinfilm units d.tfd --thickness QWOT --reference 550` | `set_units`, `thickness_unit`, `reference_nm` |
| Maximum for unconstrained layers | `thinfilm units d.tfd --maximum 5000` | `set_units`, `maximum_nm` |
| Back surface, substrate thickness | `thinfilm system d.tfd --back-surface yes --substrate-mm 1` | `set_system`, `back_surface`, `substrate_mm` |
| Angles in air | `thinfilm system d.tfd --angles air` (or `medium`) | `set_system`, `angle_in_air` |
| Material database | `thinfilm database` (lists it; the GUI chooses the file) | `list_materials` with no design (lists it) |
