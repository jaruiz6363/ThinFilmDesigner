# The MCP server: designing coatings with an AI assistant

`thinfilm-mcp` is a [Model Context Protocol](https://modelcontextprotocol.io) server. It lets an
AI assistant such as Claude open designs, build merit functions, run the optimisers and read
spectra, through the same API the GUI and the command line use. Ask in plain language — "design
an AR coating for 420 to 680 nm on N-BK7, under 0.5% from 0 to 45 degrees" — and the assistant
calls the program's tools to do it. The numbers are the program's, not the assistant's.

## Connecting it, the easy way

**Help > Connect to Claude (MCP)…** in the GUI opens `thinfilm-mcp-setup`, a small program beside
it. It finds `thinfilm-mcp`, and connects it to Claude Desktop or Claude Code, or removes it,
with one button each:

- **Claude Desktop:** the server is added to its settings file, and every other server and
  setting in that file is kept. The file as it was is kept beside it as `.bak`, and a file that
  is not valid JSON is left alone rather than overwritten. Restart Claude Desktop afterwards.
- **Claude Code:** it runs `claude mcp add` for you. If `claude` cannot be found, **Copy the
  command** puts the command on the clipboard to run in a terminal.
- **Test the server** starts `thinfilm-mcp` as a client would, and reports how many tools it
  lists. That shows the server really runs, which being configured does not.

The rest of this section does the same by hand.

## Connecting it by hand

The server speaks MCP over standard input and output. The client starts it; there is nothing
to run by hand. Point the client at the `thinfilm-mcp` program:

| Platform | Installed | Built from source |
|---|---|---|
| Windows | `C:\Program Files\ThinFilmDesigner\thinfilm-mcp.exe` | `managed\ThinFilm.Mcp\bin\Release\net8.0\thinfilm-mcp.exe` |
| macOS | `/Applications/ThinFilmDesigner.app/Contents/MacOS/thinfilm-mcp` | `managed/ThinFilm.Mcp/bin/Release/net8.0/thinfilm-mcp` |
| Linux | the AppImage with `--mcp`: command `/path/to/ThinFilmDesigner-x86_64.AppImage`, argument `--mcp` | `managed/ThinFilm.Mcp/bin/Release/net8.0/thinfilm-mcp` |

**Claude Code:**

```sh
claude mcp add thinfilm -- /full/path/to/thinfilm-mcp
```

**Claude Desktop** and other clients that read an `mcpServers` setting:

```json
{
  "mcpServers": {
    "thinfilm": {
      "command": "C:\\Program Files\\ThinFilmDesigner\\thinfilm-mcp.exe"
    }
  }
}
```

Use the full path to `thinfilm-mcp` on your machine. With the Linux AppImage, the command is the
AppImage and `"args": ["--mcp"]`, or for Claude Code
`claude mcp add thinfilm -- /path/to/ThinFilmDesigner-x86_64.AppImage --mcp`. Once connected, the assistant sees about 45
tools, each with a description of what it does and what it takes.

## How the tools fit together

- **A design is opened or made, and gets an id.** `load_design` opens a design file (or a JSON
  document), `new_design` makes one from the material database, and both return an id that every
  other tool takes. `list_samples` finds the designs that ship with the program: worked examples,
  and 370 designs from the literature.
- **Changes are made in memory.** `save_design` returns the design as a JSON document, the same
  format as a `.tfd` file. Save it to a file to open it in the GUI or the command line.
- **Layers and merit rows count from 1**, layer 1 next to the incident medium, as the GUI's tables
  and the command line number them. Results count the same way: `evaluate_design` calls the first
  layer 1. The one exception is the design document `save_design` returns: it is the `.tfd` file
  format, which numbers from 0 inside.
- **Optimisation runs in the background.** `optimise_start` returns a run id at once.
  `optimise_status` reports progress and the log; `optimise_compare` shows every merit row and
  every layer before and after; `optimise_keep` makes the result the design's, and
  `optimise_revert` drops it. A run works on a copy, so nothing changes until it is kept.

## A session

What an assistant does for "design a 532 nm notch filter":

1. `new_design` with `materials` "TiO2,SiO2", `substrate` "N-BK7", `reference_nm` 532, `layers` 1.
2. `edit_layer` to make layer 0 8000 nm of SiO2, and `set_units` to allow layers that thick.
3. `clear_operands`, then `add_operands` three times: TA Min 0.99 from 420 to 510 nm, TA Max
   0.001 from 527 to 537 nm, TA Min 0.99 from 555 to 700 nm, `step` 1.
4. `optimise_start` with `method` "needle", then `optimise_status` until it ends, then
   `optimise_keep`.
5. `optimise_start` with `method` "thin", `min_thickness` 5 and `raise` true, so no layer is
   thinner than 5 nm.
6. `check_between_samples` and `spectrum` to confirm the result, and `save_design`.

This is [chapter 4 of the user guide](user-guide/04-laser-notch.md), done by the assistant.

## The tools

### Designs

| Tool | What it does |
|---|---|
| `list_samples` | The sample designs; `filter` narrows them, e.g. "polarizer" or "Warren 1968". |
| `load_design`, `save_design` | Open a design from a file or JSON; return it as JSON. |
| `new_design` | A design from the material database. |
| `describe_design` | Counts, total thickness, merit, name, source and notes. |
| `rename_design`, `set_description` | The name; the source and notes. |
| `list_materials`, `edit_materials` | The database, or a design's materials and media, each in a line (model, n at 550 nm, valid range, source); `full` gives the whole definitions. `edit_materials` also renames (`rename`, `rename_to`) and upgrades constant materials (`upgrade`). |
| `define_material` | A material's dispersion (Sellmeier, Cauchy, constant, or a table file), as a coating material, the substrate or the incident medium. Without `kind`, only its `source` and range change. |
| `save_material_to_database` | One of the design's materials into your own material database, for later designs. |
| `set_units`, `set_system` | Thickness unit, reference wavelength, layer maximum; back surface, substrate thickness, angles in air or the medium. |

### The stack

| Tool | What it does |
|---|---|
| `edit_stack` | A design string such as "(1H 1L)4 1H"; couple, decouple, clear, reverse; bounds. |
| `edit_layer`, `insert_layer`, `remove_layer`, `set_thickness` | One layer. |

### The merit function

| Tool | What it does |
|---|---|
| `add_operands` | Rows over a wavelength band, at one angle or several. |
| `edit_operand`, `clear_operands`, `delete_operands`, `set_operand_weights` | Edit, remove or reweight rows. |
| `save_merit_function`, `open_merit_function`, `export_merit_csv` | Reuse a merit function in another design; the table as CSV. |

### Optimisation

| Tool | What it does |
|---|---|
| `optimise_start` | A run: `method` refine, global, needle, evolve or thin, with that method's settings. `insert` limits the materials needle and evolve may add. |
| `optimise_status`, `optimise_stop`, `optimise_compare`, `optimise_keep`, `optimise_revert`, `optimise_runs` | Watch, stop, compare, keep or drop a run. |
| `optimise_design` | Several stages in one call, without a log: refine, global, needle, evolve (gradual evolution, as `optimise_start`'s method), remove (layers under `min_thickness` while the merit allows) and thin (all of them, whatever it costs). |
| `needle_map` | Where a new layer would help. |

### Results

| Tool | What it does |
|---|---|
| `evaluate_design` | Every merit row's value and residual. |
| `spectrum` | R, T, A, phases and more, against wavelength or angle. |
| `check_between_samples` | Where a Min or Max spec is missed between the merit function's sample points. |
| `design_report` | The layers in nm and an n,k table for each material. |
| `set_plot` | The spectrum the design opens to in the GUI. |

### Other programs' files

| Tool | What it does |
|---|---|
| `optiland_coatings`, `import_optiland_coating`, `export_optiland_coating` | Optiland coating files. |
| `zemax_coatings`, `import_zemax_coating`, `export_zemax_coating` | OpticStudio coating files (`COATING.DAT`). |

## Notes

- Every run is logged, to the run's log and a log file, and recorded in the design's history,
  just as a run from the GUI is.
- The server writes nothing to standard output except protocol messages. Diagnostics go to
  standard error, which most clients show in their MCP log.
