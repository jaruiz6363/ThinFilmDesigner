"""Compare an Optiland coating export with the spectrum ThinFilmDesigner computed for it.

    python optiland_compare.py design-coating.json spectrum.txt [--optiland C:\\GIT\\optiland\\optiland]

design-coating.json is what File > Export Optiland coating wrote; spectrum.txt is what Export...
on the Spectrum tab wrote for the same design. Optiland loads the coating, computes the same
quantities at the same wavelengths and angles, and the largest difference in each column is
printed. The exit status is 0 when every column agrees within --tolerance (default 1e-6).

The spectrum's own header says what it is: against wavelength at one angle of incidence, or
against angle at one wavelength, and whether that angle is in air or in the incident medium.
Optiland's angle is in the incident medium, so an angle in air is converted. Columns that are not
reflectance, transmittance or absorptance (phases, Psi, Delta, OD, group delay) are skipped.
A spectrum that includes the substrate's back surface is refused: an Optiland coating is the
coated surface alone.
"""

from __future__ import annotations

import argparse
import re
import sys

QUANTITIES = {  # our column: (Optiland's quantity, polarisation)
    "RS": ("R", "s"), "RP": ("R", "p"), "RA": ("R", "u"),
    "TS": ("T", "s"), "TP": ("T", "p"), "TA": ("T", "u"),
    "AS": ("A", "s"), "AP": ("A", "p"), "AA": ("A", "u"),
}


def read_spectrum(path):
    notes, rows, header = [], [], None
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.rstrip("\r\n")
            if not line:
                continue
            if line.startswith("#"):
                notes.append(line[1:].strip())
            elif header is None:
                header = line.split("\t")
            else:
                rows.append([float(v) for v in line.split("\t")])
    if header is None or not rows:
        sys.exit(f"{path}: no table under its # lines; is it a Spectrum > Export file?")
    return " ".join(notes), header, rows


def main():
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("coating", help="the .json File > Export Optiland coating wrote")
    p.add_argument("spectrum", help="the .txt Spectrum > Export wrote for the same design")
    p.add_argument("--optiland", help="an optiland source folder to import from, instead of the installed one")
    p.add_argument("--tolerance", type=float, default=1e-6)
    args = p.parse_args()

    if args.optiland:
        sys.path.insert(0, args.optiland)
    import numpy as np
    from optiland.coatings import BaseCoating
    from optiland.fileio.optiland_handler import load_obj_from_json

    note, header, rows = read_spectrum(args.spectrum)
    if "back surface of a" in note:
        sys.exit("the spectrum includes the substrate's back surface; turn it off in Edit > System Parameters "
                 "and export again - an Optiland coating is the coated surface alone")

    in_air = "in air" in note
    table = np.array(rows)
    x = table[:, 0]
    if header[0] == "angle_deg":
        m = re.search(r"at ([0-9.]+) (nm|µm|um)", note)
        if not m:
            sys.exit("the spectrum's header does not say its wavelength")
        wavelength = float(m.group(1)) * (1000.0 if m.group(2) != "nm" else 1.0)
        wl_nm = np.full_like(x, wavelength)
        aoi = x
        print(f"against angle, {x[0]:g} to {x[-1]:g} degrees {'in air' if in_air else 'in the incident medium'}, at {wavelength:g} nm")
    else:
        wl_nm = x * (1000.0 if header[0] == "wavelength_um" else 1.0)
        m = re.search(r"at ([0-9.]+)° incidence", note)
        angle = float(m.group(1)) if m else 0.0
        aoi = np.full_like(x, angle)
        print(f"against wavelength, {wl_nm[0]:g} to {wl_nm[-1]:g} nm, at {angle:g} degrees"
              + (f" {'in air' if in_air else 'in the incident medium'}" if angle else ""))

    coat = load_obj_from_json(BaseCoating, args.coating)
    stack = coat.stack
    if in_air:
        # Optiland's angle is in the incident medium: sin(medium) = sin(air) / n0.
        n0 = np.ravel(stack.incident_material.n(wl_nm / 1000.0)) * np.ones_like(wl_nm)
        s = np.sin(np.radians(aoi)) / n0
        reachable = np.abs(s) < 1.0
        aoi = np.degrees(np.arcsin(np.clip(s, -1.0, 1.0)))
    else:
        reachable = np.ones_like(x, dtype=bool)

    worst_all = 0.0
    for c, name in enumerate(header[1:], start=1):
        if name not in QUANTITIES:
            print(f"  {name:4} skipped: only R, T and A are compared")
            continue
        q, pol = QUANTITIES[name]
        ours = table[:, c]
        theirs = np.array([
            float(np.ravel(stack.compute_rtRAT_nm_deg(np.array([w]), float(a), pol)[q])[0]) if ok else np.nan
            for w, a, ok in zip(wl_nm, aoi, reachable)
        ])
        both = np.isfinite(ours) & np.isfinite(theirs)
        diff = np.abs(ours[both] - theirs[both])
        worst = float(diff.max()) if diff.size else 0.0
        at = x[both][int(diff.argmax())] if diff.size else float("nan")
        worst_all = max(worst_all, worst)
        print(f"  {name:4} largest |ThinFilmDesigner - Optiland| = {worst:.2e}  (at {at:g})")

    ok = worst_all <= args.tolerance
    print(f"{'agree' if ok else 'DIFFER'}: largest difference {worst_all:.2e}, tolerance {args.tolerance:g}")
    sys.exit(0 if ok else 1)


if __name__ == "__main__":
    main()
