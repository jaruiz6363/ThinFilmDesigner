// Tools.cs — the tools, their schemas, and the one place a name becomes a call.
//
// A SHORT LIST, NOT A HUNDRED. Every tool is a call into ThinFilm.Api; none computes anything. The
// temptation with a protocol like this is to expose every method something might want, and the
// cost is paid by the model: a tool list it has to read on every turn, full of things that are
// three ways of saying the same thing. These are the operations a coating design actually needs.
//
// DESCRIPTIONS ARE WRITTEN FOR A MODEL THAT HAS NOT READ THE REST OF THIS TREE. They say what a
// thing is FOR and what the units are, because "optimise a design" is useless and "stages run in
// order: refine, global, needle, evolve" is the difference between a model using this well and a
// model guessing. Where a tool cannot do something, the description says so: a model that learns
// that from a caveat wastes less time than one that learns it from three failed attempts.
//
// EVERY RESULT IS JSON TEXT. A model reads it fine and it is unambiguous; a prose summary would be
// another formatting layer with another chance to disagree with the API about a number.

using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ThinFilm.Api;

namespace ThinFilm.Mcp;

public sealed class Tools
{
    private readonly ThinFilmApi _api = new();
    /// <summary>
    /// Indented for a reader, and relaxed about escaping for the same reason the wire options are
    /// — see McpServer.Wire.
    /// </summary>
    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        // A design that is not evaluated - blocked on a material, or its merit function empty -
        // has a merit of NaN. Without this the reply was a serialiser error instead of the design.
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };


    public int AbiVersion => _api.AbiVersion;

    // ---- the catalogue -----------------------------------------------------------------------

    private static JsonObject Schema(params (string Name, string Type, string Description, bool Required)[] properties)
    {
        var props = new JsonObject();
        var required = new JsonArray();
        foreach (var (name, type, description, isRequired) in properties)
        {
            props[name] = new JsonObject { ["type"] = type, ["description"] = description };
            if (isRequired) required.Add(name);
        }
        var schema = new JsonObject { ["type"] = "object", ["properties"] = props };
        if (required.Count > 0) schema["required"] = required;
        return schema;
    }

    private static JsonObject Tool(string name, string description, JsonObject schema) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = schema,
    };

    public JsonArray Describe() =>
    [
        Tool("load_design",
             "Open a design and return its id, which every other tool takes. Give the JSON a "
             + "previous save_design returned as document, or a design file's path. list_samples "
             + "finds the sample designs that ship with the program - starting problems, each a stack "
             + "and the merit function it is designed against, and designs rebuilt from the "
             + "literature with their citations - and gives each one's path. new_design starts one "
             + "from database materials instead.",
             Schema(("document", "string", "The JSON a previous save_design returned.", false),
                    ("path", "string", "A design file (.tfd), e.g. a sample's.", false))),

        Tool("list_samples",
             "The sample designs that ship with the program, as File > Open sample shows them: "
             + "Examples (starting problems, each a stack and its merit function) and Literature "
             + "(designs rebuilt from papers, by category and paper, each with its citation as source "
             + "and how its recomputation compares with the paper in its notes). Each comes with its "
             + "path for load_design, its group (folder), source and the first line of its notes. "
             + "filter keeps those whose group, name, source or notes hold every word in it.",
             Schema(("filter", "string", "Words that narrow the list, e.g. \"polarizer\", \"Examples\" or "
                                         + "\"Warren 1968\". Without it, every sample: several hundred.", false))),

        Tool("save_design",
             "The design as a JSON document: layers, materials, sampling and the full merit "
             + "function. Reloads to identical numbers.",
             Schema(("design", "string", "A design id.", true))),

        Tool("describe_design",
             "Counts, total thickness and the current merit. Cheap; call it after anything that "
             + "changes the design.",
             Schema(("design", "string", "A design id.", true))),

        Tool("check_between_samples",
             "Checks every Min and Max row of R, T and A between its sample points, 20 times as "
             + "densely, and lists where the spec is missed there by clearly more than at any sample: "
             + "the narrow leak of a thick design that the merit function cannot see. Each entry gives "
             + "the code, angle, band, the worst wavelength and value, and the worst value at a sample. "
             + "Empty means none. Optimisation runs log the same check when they end.",
             Schema(("design", "string", "A design id.", true))),

        Tool("evaluate_design",
             "Every operand with its value and residual, plus the layer table. This is the merit "
             + "function itself - what the optimiser is being asked for, and how far off each "
             + "target currently is.",
             Schema(("design", "string", "A design id.", true))),

        Tool("spectrum",
             "Reflectance, transmittance or absorptance over a wavelength range, computed by the "
             + "engine on the grid you ask for - nothing is interpolated. Quantities are names "
             + "like RA (reflectance, both polarisations averaged), TA, RS, RP, TS, TP, AA, ODA; "
             + "phase, Psi and Delta (R_PHASE_S, R_PSI, R_DELTA, ...) are in degrees. "
             + "Keep steps modest unless the detail is needed: the result is returned in full. "
             + "With vs 'angle' it is over a range of angles of incidence at one wavelength instead: "
             + "from and to are degrees, the result has an Angle array, and an angle that cannot be "
             + "reached in the incident medium (given in air, see set_system) reads NaN.",
             Schema(("design", "string", "A design id.", true),
                    ("from", "number", "Start wavelength in nm, or start angle in degrees with vs 'angle'.", true),
                    ("to", "number", "End wavelength in nm, or end angle in degrees with vs 'angle'.", true),
                    ("steps", "integer", "How many points, at least 2.", true),
                    ("angle_degrees", "number", "Angle of incidence, default 0. Not read with vs 'angle'.", false),
                    ("quantities", "string", "Comma separated, default RA,TA. Any of " + string.Join(", ", ThinFilm.Api.SpectrumQuantities.All.Select(q => q.Code)) + ".", false),
                    ("vs", "string", "'wavelength' (default) or 'angle'.", false),
                    ("wavelength", "number", "With vs 'angle': the wavelength in nm, default 550.", false))),

        Tool("design_report",
             "File > Export design report: the design in physical units and the index of every material it uses, as "
             + "numbers for a program that has no dispersion equations. The layers incident side first with thicknesses "
             + "in nm, then an n,k table (N = n + ik, k >= 0 absorbing) for each layer material and both media, "
             + "tab-separated, over from_nm to to_nm every step_nm. Returns the text; with path, also writes it there.",
             Schema(("design", "string", "A design id.", true),
                    ("from_nm", "number", "First wavelength, nm; default the design's saved plot range, else its grid.", false),
                    ("to_nm", "number", "Last wavelength, nm; always included. Default as from_nm.", false),
                    ("step_nm", "number", "Wavelength step, nm; default 10.", false),
                    ("path", "string", "A .txt file to write the report to; omit to only return it.", false))),

        Tool("optimise_design",
             "Run optimisation stages in order. 'refine' is damped least squares and is what you "
             + "want most of the time. 'global' is multistart, for when refine has stalled in a "
             + "poor local minimum. 'needle' inserts layers where the analytic needle map says "
             + "they help, which is how a design GAINS layers. 'evolve' is gradual evolution, as optimise_start's "
             + "method of that name: it grows the design a layer at a time with needles and removals, the most "
             + "powerful synthesis. 'remove' removes layers under min_thickness while the merit allows; 'thin' "
             + "removes them whatever it costs. Changes the design in place; optimise_start is the same with a log.",
             Schema(("design", "string", "A design id.", true),
                    ("stages", "string", "Comma separated: refine, global, needle, evolve (gradual evolution), remove (layers under min_thickness, while the merit allows), thin (every layer under min_thickness, whatever it costs).", false),
                    ("norm", "string", "L2, minimax or L1. Minimax suits a spec that is a limit "
                                       + "across a band rather than a target.", false),
                    ("iterations", "integer", "Refinement budget per stage, default 300.", false),
                    ("insertions", "integer", "Maximum needle insertions, default 6.", false),
                    ("insert", "string", "needle: the materials a needle may be, comma separated; default every one.", false),
                    ("min_thickness", "number", "Layers thinner than this are candidates for "
                                                + "removal by evolve, remove and thin, in nm.", false),
                    ("accept_merit", "number", "evolve keeps a removal that leaves the merit at or under "
                                               + "this however much it cost; default 0.1, 0 is off.", false))),

        // A RUN: the same runner as the GUI's Refine window and `thinfilm refine`, with the same log.
        Tool("optimise_start",
             "Start an optimisation run in the background and return its run id at once. The run "
             + "works on a copy: the design is unchanged until optimise_keep, and optimise_revert "
             + "drops the result. Watch it with optimise_status. Every run logs its start and end "
             + "RMSE (the merit is a weighted RMS), elapsed time and each iteration, to a log file "
             + "as well, and a kept or reverted run is added to the design's saved history. "
             + "Methods: 'refine' is damped least squares, what you want most of the time; "
             + "'global' refines from many random starts, for a poor local minimum; 'needle' "
             + "inserts layers where the needle map says they help (how a design gains layers); "
             + "'evolve' is gradual evolution: it grows the design a layer at a time at the outside, "
             + "inserting needles and removing too-thin layers at each size, and keeps the best step "
             + "(the most powerful, for a design that needs more layers than it has). Needle and "
             + "evolve are refused while layers have pickups or their own min/max.",
             Schema(("design", "string", "A design id.", true),
                    ("method", "string", "refine (default), global, needle, evolve or thin (remove every layer under min_thickness, whatever it costs, logging each removal's merit).", false),
                    ("norm", "string", "refine: L2 (default), minimax or L1.", false),
                    ("iterations", "integer", "Maximum iterations of each refinement, default 300.", false),
                    ("starts", "integer", "global: starts (or hops), default 24.", false),
                    ("mode", "string", "global: random (each start drawn anew inside the bounds, the default) or hop "
                                       + "(basin hopping: each start perturbs the best design so far, which suits a "
                                       + "good structured design such as a bandpass).", false),
                    ("perturbation", "number", "global hop: how far each thickness may move, in percent of "
                                               + "itself; default 10.", false),
                    ("accept_worse", "number", "global hop: the walk also moves to a hop worse than the best by "
                                               + "up to this percent, to leave a basin it keeps falling back into; "
                                               + "default 0 (hop only from the best).", false),
                    ("range", "number", "global random: starts lie between 0 and this multiple of each "
                                        + "layer's thickness, inside its own bounds; default 2.", false),
                    ("seed", "integer", "global: the random seed, to repeat a run; default a new one each "
                                        + "run, given in the settings it logs.", false),
                    ("threads", "integer", "global: starts or hops refined at once, each on its own copy of "
                                           + "the design; default every core but one.", false),
                    ("insertions", "integer", "needle: maximum insertions; evolve: needles per step; default 6.", false),
                    ("growth_steps", "integer", "evolve: layers added one at a time, default 8; 0 only needles and removes.", false),
                    ("growth_qwot", "number", "evolve: each added layer, in quarter waves at the reference wavelength; default 1.", false),
                    ("grow_at", "string", "evolve: both (default; the better side each step), incident or substrate.", false),
                    ("max_layers", "integer", "evolve: the most layers the design may have - growth stops there and no needle passes it; default 0, no limit.", false),
                    ("insert", "string", "needle and evolve: the materials a needle (or evolve's grown layer) may be, "
                                         + "comma separated, e.g. TiO2,SiO2; default every material in the design. "
                                         + "Leave a metal out when a thin needle of it could not be made.", false),
                    ("raise", "boolean", "thin: raise every layer under min_thickness to it and fix it there, refining "
                                         + "the rest, instead of removing it; for thin layers that do real work.", false),
                    ("min_thickness", "number", "evolve and thin: layers thinner than this, in nm, are candidates "
                                                + "for removal (thin removes them all); default 2.", false),
                    ("degradation", "number", "evolve: how much worse the merit may get for a removal, "
                                              + "in percent; default 5.", false),
                    ("accept_merit", "number", "evolve: a removal that leaves the merit at or under this is kept "
                                               + "however much it cost, and growth stops once the merit is under it; "
                                               + "default 0.1, 0 is off.", false),
                    ("removals", "integer", "evolve: maximum removals per step, default 16.", false))),

        Tool("optimise_status",
             "A run as it stands: state (running, finished, stopped, failed, kept, reverted), "
             + "elapsed seconds, start and best RMSE, end RMSE once ended, iteration, outcome, the "
             + "log file, and the log lines from 'since' on (log_lines is how many there are in "
             + "all, so pass it back as 'since' to read only new lines).",
             Schema(("run", "string", "A run id from optimise_start.", true),
                    ("since", "integer", "First log line to return, default 0.", false),
                    ("wait_seconds", "number", "Wait up to this long for the run to end before "
                                               + "answering, default 0.", false))),

        Tool("optimise_stop",
             "Ask a running run to stop after its current iteration. The best design it reached "
             + "can still be kept with optimise_keep.",
             Schema(("run", "string", "A run id.", true))),

        Tool("optimise_keep",
             "Make an ended run's design the design's own, and record the run in its history as "
             + "kept. Refused while it is running, or if the design was changed after the run started.",
             Schema(("run", "string", "A run id.", true))),

        Tool("optimise_revert",
             "Drop a run's result, stopping it first if it is still going; the design stays as it "
             + "was, and the run is recorded in its history as reverted.",
             Schema(("run", "string", "A run id.", true))),

        Tool("optimise_compare",
             "An ended run's design beside the one it started from, before deciding to keep or "
             + "revert it - what the GUI window's Merit function, Spectrum and Layers tabs show: every "
             + "merit row's value before and after ('before' and 'after' evaluations, same rows in the "
             + "same order), the operands as written, and each stack as an index profile (material, nm, "
             + "real index at the reference wavelength). Give from, to and steps (nm) to have the same "
             + "spectrum of both as well. Refused while the run is going or if it failed.",
             Schema(("run", "string", "A run id.", true),
                    ("from", "number", "Spectrum: first wavelength, nm.", false),
                    ("to", "number", "Spectrum: last wavelength, nm.", false),
                    ("steps", "integer", "Spectrum: points, at least 2; default 201.", false),
                    ("angle_degrees", "number", "Spectrum: angle of incidence, default 0.", false),
                    ("quantities", "string", "Spectrum: comma-separated, default RA,TA.", false))),

        Tool("optimise_runs",
             "Every run started in this session, without their logs.",
             Schema()),

        Tool("needle_map",
             "Where inserting a layer would most improve the design: the analytic derivative of "
             + "the merit with respect to inserting each candidate material at each depth. A "
             + "NEGATIVE slope means an insertion there helps. All-positive means the design is "
             + "needle-stationary and no insertion helps to first order, which is a real answer.",
             Schema(("design", "string", "A design id.", true),
                    ("samples", "integer", "Depth samples, default 32.", false))),

        Tool("set_thickness",
             "Change one layer's thickness, in nm. Does not re-plan; use this to explore.",
             Schema(("design", "string", "A design id.", true),
                    ("layer", "integer", "The layer, counting from 1 at the incident side, as the Design tab numbers them.", true),
                    ("thickness", "number", "New thickness in nm.", true))),

        Tool("insert_layer",
             "Insert a layer. Changes the shape, so the design is rebuilt.",
             Schema(("design", "string", "A design id.", true),
                    ("before", "integer", "Insert before this layer, counting from 1; "
                                          + "the layer count + 1 appends.", true),
                    ("material", "integer", "The design's material, counting from 1 in the order list_materials gives.", true),
                    ("thickness", "number", "Thickness in nm.", true))),

        Tool("remove_layer",
             "Remove a layer. Changes the shape, so the design is rebuilt.",
             Schema(("design", "string", "A design id.", true),
                    ("layer", "integer", "The layer, counting from 1 at the incident side.", true))),

        // The rest of the GUI's stack and layer editing (the user, 2026-10-17: what the GUI can do,
        // the MCP must too), grouped by what they edit: each action one API call (StackEdits.cs),
        // the call the GUI's button or dialog makes.
        Tool("new_design",
             "Start a design from nothing, as File > New design does: the incident medium, substrate "
             + "and coating materials taken by name from the material database (list_materials with no "
             + "design lists it), a starting stack of alternating quarter-waves at the reference "
             + "wavelength, and one placeholder merit row. Returns its id.",
             Schema(("name", "string", "The design's name.", true),
                    ("materials", "string", "Coating materials, comma-separated, e.g. TiO2,SiO2.", true),
                    ("substrate", "string", "The substrate, e.g. BK7.", true),
                    ("incident", "string", "The incident medium, default air.", false),
                    ("reference_nm", "number", "Reference wavelength, nm, default 550.", false),
                    ("layers", "integer", "Starting layers, default 4.", false),
                    ("wavelength_unit", "string", "How wavelengths are shown: nm (default) or um.", false))),

        Tool("edit_stack",
             "The Stack menu. action 'string' replaces the whole stack with the layers a design string "
             + "spells, e.g. '(1H 1L)4 1H' or '2H 0.5L': a number then a letter per layer, groups in "
             + "brackets repeated by the number after them; the numbers are quarter waves (QWOT) or full "
             + "waves (FWOT) at the reference wavelength. Letters are mapped to the design's materials "
             + "by 'letters', or guessed: H the highest index, L the lowest, M the middle, any other the "
             + "first material starting with it. 'couple' picks every layer up from the first of its "
             + "material at its present ratio, so they move together; 'decouple' removes every pickup; "
             + "'clear' deletes every layer (the bare substrate); 'bounds' sets the min and max the "
             + "optimiser may give layers first..last (counting from 1) of a material, both left out meaning "
             + "unconstrained. 'reverse' puts the layers in the opposite order, pickups renumbered; "
             + "'reverse_media' also swaps the incident medium and the substrate (the design seen from "
             + "the other side; an incident medium's k is treated as 0, and the result's Warnings say so).",
             Schema(("design", "string", "A design id.", true),
                    ("action", "string", "string, couple, decouple, clear, bounds, reverse or reverse_media.", true),
                    ("text", "string", "string: the design string.", false),
                    ("letters", "string", "string: letter=material pairs, e.g. H=TiO2,L=SiO2.", false),
                    ("unit", "string", "string: QWOT (default) or FWOT. bounds: the unit of min and max, nm "
                                       + "(default), QWOT or FWOT, each layer's own index converting.", false),
                    ("substrate_first", "boolean", "string: the leftmost layer is next to the substrate; "
                                                   + "default false (next to the incident medium).", false),
                    ("pickups", "boolean", "string: repeated letters picked up from their first, so the "
                                           + "repeats move together; default true.", false),
                    ("first", "integer", "bounds: first layer, counting from 1; default 1.", false),
                    ("last", "integer", "bounds: last layer, counting from 1; default the last.", false),
                    ("material", "string", "bounds: only layers of this material; default all.", false),
                    ("min", "number", "bounds: the minimum, in unit.", false),
                    ("max", "number", "bounds: the maximum, in unit.", false))),

        Tool("edit_layer",
             "One layer, counting from 1 at the incident side as the Design tab numbers them. action 'insert' with a material inserts a new "
             + "layer of it before 'before' (default the end), its thickness 'value' in 'unit'; without "
             + "a material it inserts a copy of layer 'after' (default the last) after it, as the "
             + "Design tab's Insert does - material, thickness, unit, bounds and pickup. 'remove' deletes "
             + "'layer' (refused if another layer picks it up); 'move' moves 'layer' to position 'to'; "
             + "'set' changes any of its material, unit, value, vary, pickup and bounds and leaves the "
             + "rest, as Layer properties does. Values and bounds are in the layer's unit: nm, QWOT or "
             + "FWOT (a QWOT layer given a new material keeps its number of quarter waves). A pickup "
             + "makes the layer scale x parent + offset.",
             Schema(("design", "string", "A design id.", true),
                    ("action", "string", "insert, remove, move or set.", true),
                    ("layer", "integer", "remove, move, set: the layer, counting from 1.", false),
                    ("to", "integer", "move: its new position, counting from 1.", false),
                    ("before", "integer", "insert with a material: insert before this layer, counting from 1.", false),
                    ("after", "integer", "insert a copy: of this layer, counting from 1, after it.", false),
                    ("material", "string", "insert, set: a material of the design, by name.", false),
                    ("value", "number", "insert, set: the thickness, in the layer's unit.", false),
                    ("unit", "string", "insert, set: nm, QWOT, FWOT, or default (the design's).", false),
                    ("vary", "boolean", "insert, set: whether the optimiser may move it.", false),
                    ("pickup", "integer", "set: the layer it picks up from, counting from 1.", false),
                    ("scale", "number", "set: pickup scale, default 1.", false),
                    ("offset", "number", "set: pickup offset in the unit, default 0.", false),
                    ("free", "boolean", "set: remove its pickup.", false),
                    ("min", "number", "set: its minimum, in its unit.", false),
                    ("max", "number", "set: its maximum, in its unit.", false),
                    ("no_min", "boolean", "set: remove its minimum.", false),
                    ("no_max", "boolean", "set: remove its maximum.", false))),

        Tool("list_materials",
             "With a design: its coating materials (the names edit_layer and edit_stack take), how "
             + "many layers use each, its incident medium and substrate. Without: every material in the "
             + "material database, which new_design and edit_materials take from, each with what it is "
             + "(model, n at 550 nm, valid range) and its source.",
             Schema(("design", "string", "A design id; leave out to list the database.", false),
                    ("full", "boolean", "With a design: every material's whole definition (coefficients, a table's rows) instead of a line each.", false))),

        Tool("edit_materials",
             "Edit > Materials: add materials from the database, remove ones no layer uses, "
             + "replace one with another from the database (its layers follow it), and choose the "
             + "incident medium and substrate. All by name; applied together or not at all. rename with rename_to "
             + "renames one (Rename); upgrade replaces every constant-index material with the database material of "
             + "its name (Upgrade constant materials) - each of these two on its own.",
             Schema(("design", "string", "A design id.", true),
                    ("add", "string", "Comma-separated materials to add.", false),
                    ("remove", "string", "Comma-separated materials to remove.", false),
                    ("replace", "string", "A material of the design to replace...", false),
                    ("with", "string", "...with this one from the database.", false),
                    ("incident", "string", "The incident medium.", false),
                    ("substrate", "string", "The substrate.", false),
                    ("rename", "string", "A material of the design to rename, with rename_to.", false),
                    ("rename_to", "string", "Its new name.", false),
                    ("upgrade", "boolean", "Upgrade constant materials.", false))),

        Tool("set_description",
             "The design's Description, shown above its layer table: source, one line saying where the design came from "
             + "(authors, title, journal or book, year, figure or table), and notes - what it is for, what was changed "
             + "from the source, caveats. Give either; an empty string clears it. describe_design returns both.",
             Schema(("design", "string", "A design id.", true),
                    ("source", "string", "Where the design came from, one line.", false),
                    ("notes", "string", "Notes, any length.", false))),

        Tool("define_material",
             "Edit > Materials' Edit dispersion and New material: a material's dispersion given. role coating "
             + "(the default) replaces the definition of the design's material of that name - its layers follow - or "
             + "adds one; substrate or incident sets that medium. kind sellmeier takes coefficients B1,C1,B2,C2,... "
             + "with C in um^2 (a source that gives C as a wavelength, as refractiveindex.info's formula 1 does, "
             + "must be squared); cauchy A,B,C with lambda in um; constant n,k; table reads table_path, rows of "
             + "lambda n k, with table_unit nm or um (or named in the file's header). range_min and range_max are the valid range in um. Without kind, the material keeps its dispersion and only "
             + "source, range_min and range_max change (Edit dispersion with only those boxes changed).",
             Schema(("design", "string", "A design id.", true),
                    ("name", "string", "The material's name.", true),
                    ("kind", "string", "sellmeier, cauchy, constant or table; leave out to change only source or range.", false),
                    ("coefficients", "string", "Comma-separated numbers, for sellmeier, cauchy or constant.", false),
                    ("table_path", "string", "A file of lambda n k rows, for table.", false),
                    ("table_unit", "string", "nm or um: what the table's wavelengths are in; may be left out when the file's header names it (wavelength_nm).", false),
                    ("range_min", "number", "Valid range, lower end, um.", false),
                    ("range_max", "number", "Valid range, upper end, um.", false),
                    ("source", "string", "Where the numbers come from.", false),
                    ("role", "string", "coating (default), substrate or incident.", false))),

        Tool("save_material_to_database",
             "Writes one of the design's materials (a coating material, or the incident medium or substrate by "
             + "name) into the person's own material database, so later designs can take it, as Edit > Materials' "
             + "Save to database does. Only that material's lines of the file change; the shipped database is never "
             + "written, and a catalogue glass is not saved. A material of the same name already there is refused "
             + "unless replace is true. Designs already made keep their own copies.",
             Schema(("design", "string", "A design id.", true),
                    ("material", "string", "The design's material, by name.", true),
                    ("replace", "boolean", "Replace a database material of the same name.", false))),

        Tool("rename_design",
             "Renames the design: the name it is saved with and shown by. One line, not blank.",
             Schema(("design", "string", "A design id.", true),
                    ("name", "string", "The new name.", true))),

        Tool("set_plot",
             "The spectrum the design is saved with and opens to in the Spectrum tab: against wavelength (from_nm, to_nm, "
             + "steps, at angle_deg) or, vs_angle, against angle (angle_from, angle_to, angle_steps, at wavelength_nm), and "
             + "the quantity codes to plot (as spectrum takes them). Set it to what a paper plots, over its range. Omitted "
             + "values keep the design's current plot (or the Spectrum tab's defaults); clear removes it.",
             Schema(("design", "string", "A design id.", true),
                    ("from_nm", "number", "Wavelength axis: from, nm.", false),
                    ("to_nm", "number", "Wavelength axis: to, nm.", false),
                    ("steps", "number", "Wavelength axis: points.", false),
                    ("angle_deg", "number", "Wavelength axis: angle of incidence, degrees.", false),
                    ("quantities", "string", "Comma-separated quantity codes, e.g. RS,RP.", false),
                    ("vs_angle", "boolean", "Plot against angle instead of wavelength.", false),
                    ("angle_from", "number", "Angle axis: from, degrees.", false),
                    ("angle_to", "number", "Angle axis: to, degrees.", false),
                    ("angle_steps", "number", "Angle axis: points.", false),
                    ("wavelength_nm", "number", "Angle axis: the wavelength, nm.", false),
                    ("clear", "boolean", "Remove the saved plot.", false))),

        Tool("set_units",
             "Edit > System Parameters: the thickness unit layers are shown in unless they name their own "
             + "(nm, QWOT, FWOT), the reference wavelength QWOT and FWOT count at (a QWOT layer keeps "
             + "its number, so a new reference moves it), how wavelengths are shown (nm or um), and the "
             + "maximum the optimiser gives a layer with none of its own. Give any of them.",
             Schema(("design", "string", "A design id.", true),
                    ("thickness_unit", "string", "nm, QWOT or FWOT.", false),
                    ("reference_nm", "number", "Reference wavelength, nm.", false),
                    ("wavelength_unit", "string", "nm or um.", false),
                    ("maximum_nm", "number", "Maximum for unconstrained layers, nm.", false))),

        Tool("set_system",
             "Edit > System Parameters: whether reflectance and transmittance (and absorptance, OD and "
             + "the averages built on them) include the substrate's uncoated back surface, added "
             + "incoherently with the incident medium behind it; the substrate thickness in mm, which "
             + "matters only when the substrate absorbs; and whether angles of incidence are given in "
             + "air or in the incident medium (the medium for a cube beam splitter: 45 degrees in glass "
             + "cannot be reached from air). Psi, Delta and the phases always describe the coated "
             + "surface alone. Give any of them.",
             Schema(("design", "string", "A design id.", true),
                    ("back_surface", "boolean", "Include the back surface in R and T.", false),
                    ("substrate_mm", "number", "Substrate thickness, mm.", false),
                    ("angle_in_air", "boolean", "Angles are in air rather than the incident medium.", false))),

        Tool("edit_operand",
             "One merit row, counting from 1 as the merit table numbers them. action 'edit' changes any of its "
             + "cells and leaves the rest; a new code or mode resets its budget, a stack code (MINT, "
             + "MAXT, TOTT) has no wavelength, MINT and MAXT start at 8 and 300 nm. wavelength2 with "
             + "points makes the row a band of evenly spaced points. 'insert' puts a copy of row "
             + "'after' after it (default the last; RA at most 0.005 in an empty table); 'remove' "
             + "deletes 'row'. Rows in bulk: add_operands, delete_operands, set_operand_weights.",
             Schema(("design", "string", "A design id.", true),
                    ("action", "string", "edit, insert or remove.", true),
                    ("row", "integer", "edit, remove: the row, counting from 1.", false),
                    ("after", "integer", "insert: copy this row, counting from 1; default the last.", false),
                    ("code", "string", "RA, RS, RP, TA, TS, TP, AA, AS, AP, MINT, MAXT, TOTT, ASOL, EMIT...", false),
                    ("mode", "string", "Target, Min, Max, Range or Report.", false),
                    ("target", "number", "The target (Range: the lower).", false),
                    ("target2", "number", "Range: the upper target.", false),
                    ("weight", "number", "The weight.", false),
                    ("wavelength", "number", "The wavelength, or a band's first, nm.", false),
                    ("wavelength2", "number", "A band's last wavelength, nm.", false),
                    ("points", "integer", "A band's points, at least 2.", false),
                    ("angle_degrees", "number", "Angle of incidence.", false),
                    ("limit_nm", "number", "MINT, MAXT: the thickness limit, nm.", false),
                    ("temperature_k", "number", "EMIT, AERATIO: the blackbody temperature, K.", false))),

        // Building the merit function: the GUI's Clear all / Delete operands / Adjust weight / Add
        // operands, through the same API calls (MeritTable.cs).
        Tool("save_merit_function",
             "The merit function on its own, as a merit file (JSON, the GUI's .tfm): every row with "
             + "the wavelengths and angles it reads, and no layers, so it can be opened into another design.",
             Schema(("design", "string", "A design id.", true))),

        Tool("open_merit_function",
             "A merit file's rows (from save_merit_function) in place of the design's whole merit "
             + "function, or with append after its rows; the layers and media are kept. Returns how "
             + "many rows came from the file.",
             Schema(("design", "string", "A design id.", true),
                    ("document", "string", "The merit file's JSON text.", true),
                    ("append", "boolean", "Add the rows after the table's own instead of replacing it; default false.", false))),

        Tool("zemax_coatings",
             "The layer-stack coatings in an OpticStudio coating file (COATING.DAT): each one's name, "
             + "layer count, the reference wavelength it says it was designed at, and why it cannot be "
             + "imported if it cannot (a loop index, a material with no MATE).",
             Schema(("path", "string", "The coating file.", true))),

        Tool("import_zemax_coating",
             "File > Import ZEMAX coating: the design's layers replaced by one COAT from a coating file; "
             + "the media, wavelengths and merit function are kept. Its MATE materials are added under "
             + "their names (the design's own is used when the name and data agree), each as the equation "
             + "in its ThinFilmDesigner comment if that still reproduces its table, else as the table; "
             + "OpticStudio's negative k is read as absorption. Thicknesses in um come in as nm; in waves "
             + "they come in as QWOT, counted at the reference wavelength the file gives, else the "
             + "design's. Returns the design and notes on anything worth knowing.",
             Schema(("design", "string", "A design id.", true),
                    ("path", "string", "The coating file.", true),
                    ("coating", "string", "The COAT's name.", true))),

        Tool("export_zemax_coating",
             "File > Export to ZEMAX coating: the design written into a coating file as one COAT, "
             + "thicknesses in um, with a comment giving the reference wavelength, plus a MATE n,k table "
             + "for each material it needs (k negative for an absorber, as OpticStudio wants; an equation "
             + "is tabulated within 1e-5 and written in a comment above). Every other line of the file is "
             + "kept as it was, in its own encoding, and a backup is left beside it as .bak; a new file is "
             + "made when there is none. An existing MATE is never changed: one with the same name and "
             + "data is used, one with other data is left and ours written as NAME_2. A COAT of the same "
             + "name is refused unless replace is true, and is then replaced where it stands.",
             Schema(("design", "string", "A design id.", true),
                    ("path", "string", "The coating file.", true),
                    ("coating", "string", "The COAT's name; default the design's name.", false),
                    ("from_nm", "number", "The tables' first wavelength, nm; default the design's first.", false),
                    ("to_nm", "number", "The tables' last wavelength, nm; default the design's last.", false),
                    ("replace", "boolean", "Replace a COAT of the same name; default false.", false))),

        Tool("optiland_coatings",
             "The thin-film coatings (Optiland ThinFilmCoating) in an Optiland coating file or lens file: each one's number, "
             + "name ('coating', or 'surface 3 (comment)' in a lens), layer count, and why it cannot be imported if it cannot.",
             Schema(("path", "string", "The Optiland .json file.", true))),

        Tool("import_optiland_coating",
             "File > Import Optiland coating: the design's layers replaced by one of a file's thin-film coatings (numbered "
             + "from 1 as optiland_coatings lists them); the media, wavelengths and merit function are kept. Materials come in "
             + "as what they are: refractiveindex.info formulas 1 and 2 as Sellmeier, formula 5 with powers -2 and -4 as "
             + "Cauchy, tables as tables, IdealMaterial as a constant; a database Material through the YAML file it names, "
             + "when that file is on this computer; another formula as a fine table, with a note.",
             Schema(("design", "string", "A design id.", true),
                    ("path", "string", "The Optiland .json file.", true),
                    ("coating", "integer", "Which coating, from 1; default 1.", false))),

        Tool("export_optiland_coating",
             "File > Export Optiland coating: the design as a standalone Optiland ThinFilmCoating JSON file, which Optiland "
             + "reads with load_obj_from_json(BaseCoating, path). Layers incident side first in nm; the design's incident "
             + "medium and substrate as its media; Sellmeier as formula 2, Cauchy as formula 5, a catalogue glass as its "
             + "catalogue formula, tables as n and k tables (k positive). The file is written whole.",
             Schema(("design", "string", "A design id.", true),
                    ("path", "string", "The .json file to write.", true))),

        Tool("export_merit_csv",
             "The merit table as CSV, one line per row: the table's columns (wavelengths in the "
             + "design's unit) and the row's value, residual and flags.",
             Schema(("design", "string", "A design id.", true))),

        Tool("clear_operands",
             "Remove every merit row. The design is then not evaluated (merit NaN, a 'blocked' "
             + "reason saying so) until add_operands puts rows back; it can still be saved and "
             + "its spectrum still computed.",
             Schema(("design", "string", "A design id.", true))),

        Tool("delete_operands",
             "Remove the merit rows that match a filter. Every filter argument is optional and an "
             + "omitted one does not filter: with none, every row goes. Returns how many went.",
             FilterSchema()),

        Tool("set_operand_weights",
             "Give the merit rows that match a filter one new weight. Filter as delete_operands. "
             + "Each row's internal error budget (scale) is kept.",
             FilterSchema(("weight", "number", "The new weight, zero or positive.", true))),

        Tool("add_operands",
             "Append pointwise merit rows: one per wavelength from 'from' to 'to' every 'step' nm "
             + "(omit to and step for one wavelength), at each angle from 'aoi' to 'aoi_to' every "
             + "'aoi_step' degrees (omit those two for one angle). Optical codes only (RA, TA, RS, "
             + "...); MINT, MAXT and TOTT describe the stack and cannot be swept. Rows are ordered "
             + "angle by angle. A band code - ASOL, RSOL, TSOL (against AM1.5 global), EMIT (against a "
             + "blackbody at temperature_k, default 373.15) or AERATIO (ASOL / EMIT) - makes one row per "
             + "angle, averaging over every wavelength from 'from' to 'to'.",
             Schema(("design", "string", "A design id.", true),
                    ("code", "string", "RA, RS, RP, TA, TS, TP, AA, AS, AP, or a band code ASOL, RSOL, TSOL, EMIT, AERATIO.", true),
                    ("mode", "string", "Target (equal), Min (at least), Max (at most), Range or "
                                       + "Report (shown, not scored).", true),
                    ("target", "number", "The target, as a fraction for R, T and A.", true),
                    ("target2", "number", "The upper target of a Range.", false),
                    ("weight", "number", "Weight of each row, default 1.", false),
                    ("from", "number", "First wavelength, nm.", true),
                    ("to", "number", "Last wavelength, nm.", false),
                    ("step", "number", "Wavelength step, nm.", false),
                    ("aoi", "number", "Angle of incidence, or the first of several, degrees. Default 0.", false),
                    ("aoi_to", "number", "Last angle, degrees.", false),
                    ("aoi_step", "number", "Angle step, degrees.", false))),
    ];

    private static JsonObject FilterSchema(params (string Name, string Type, string Description, bool Required)[] extra) =>
        Schema([
            ("design", "string", "A design id.", true),
            ("from", "number", "Only rows whose wavelengths all lie at or above this, nm.", false),
            ("to", "number", "Only rows whose wavelengths all lie at or below this, nm.", false),
            ("codes", "string", "Comma separated codes, e.g. RA,TA; R, T or A means its family "
                                + "(R = RA, RS, RP).", false),
            ("modes", "string", "Comma separated: Target, Min, Max, Range, Report.", false),
            ("aoi_above", "number", "Only rows at an angle above this, degrees.", false),
            ("aoi_below", "number", "Only rows at an angle below this, degrees.", false),
            ("aoi_between", "string", "Only rows at an angle in this range, inclusive, e.g. \"10,30\".", false),
            .. extra]);

    private static OperandFilter Filter(JsonObject a)
    {
        AngleTest? angle = null;
        if (OptNum(a, "aoi_above") is { } above) angle = new AngleTest("above", above);
        else if (OptNum(a, "aoi_below") is { } below) angle = new AngleTest("below", below);
        else if (Opt(a, "aoi_between") is { } between)
        {
            var ab = between.Split(',', StringSplitOptions.TrimEntries);
            if (ab.Length != 2 || !double.TryParse(ab[0], out var lo) || !double.TryParse(ab[1], out var hi))
                throw new ArgumentException("'aoi_between' wants two angles, e.g. \"10,30\"");
            angle = new AngleTest("between", lo, hi);
        }
        static string[]? List(string? s) => s?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new OperandFilter(OptNum(a, "from"), OptNum(a, "to"), List(Opt(a, "codes")), List(Opt(a, "modes")), angle);
    }

    // ---- dispatch ----------------------------------------------------------------------------

    /// <summary>
    /// Returns the text to hand back and whether it represents a failure.
    ///
    /// EVERYTHING THAT GOES WRONG IN HERE IS A TOOL FAILURE, including a missing or mistyped
    /// argument. That is the point of catching here rather than letting it reach the server: an
    /// exception escaping this method becomes a JSON-RPC internal error, which the client reports
    /// and the model never sees — so a model that forgot `design` would be told nothing it could
    /// act on. It wrote the call and it can fix the call, but only if the reply says which
    /// argument was wrong.
    /// </summary>
    public (string Text, bool Failed) Call(string name, JsonObject a)
    {
        try { return Dispatch(name, a); }
        catch (ArgumentException e) { return Fail(e.Message, null); }
        catch (FormatException e) { return Fail(e.Message, null); }
        catch (InvalidOperationException e) { return Fail(e.Message, null); }
        catch (IOException e) { return Fail(e.Message, null); }
        catch (UnauthorizedAccessException e) { return Fail(e.Message, null); }
    }

    private (string Text, bool Failed) Dispatch(string name, JsonObject a)
    {
        switch (name)
        {
            case "load_design":
            {
                var path = Opt(a, "path");
                var document = Opt(a, "document");
                if ((path is null) == (document is null))
                    return Fail("load_design needs document or path, not both", null);
                return Wrap(_api.Load(document ?? File.ReadAllText(path!)));
            }

            case "list_samples":
                return Ok(Samples.Catalog(Opt(a, "filter"))
                                 .Select(e => new { e.Path, e.Group, e.Name, e.Source, e.Summary }).ToArray());

            case "save_design":
                return Wrap(_api.Save(Str(a, "design")));

            case "describe_design":
                return Wrap(_api.Describe(Str(a, "design")));

            case "check_between_samples":
                return Wrap(_api.CheckBetweenSamples(Str(a, "design")));

            case "evaluate_design":
                return Wrap(_api.Evaluate(Str(a, "design")));

            case "spectrum":
            {
                var quantities = Opt(a, "quantities");
                var vs = (Opt(a, "vs") ?? "wavelength").ToLowerInvariant();
                if (vs is not ("wavelength" or "angle"))
                    return Fail("vs takes 'wavelength' or 'angle'", null);
                return Wrap(SpectrumQuantities.InAxisUnits(_api.Spectrum(Str(a, "design"), new SpectrumRequest(
                    Num(a, "from"), Num(a, "to"), (int)Num(a, "steps"),
                    OptNum(a, "angle_degrees") ?? 0.0,
                    quantities is null ? null
                        : quantities.Split(',', StringSplitOptions.RemoveEmptyEntries
                                                | StringSplitOptions.TrimEntries),
                    vs == "angle", OptNum(a, "wavelength") ?? 550.0))));
            }

            case "optimise_design":
                return Wrap(_api.Optimise(Str(a, "design"), new OptimiseRequest(
                    Stages: Opt(a, "stages") ?? "refine",
                    Norm: Opt(a, "norm"),
                    MaxIterations: (int)(OptNum(a, "iterations") ?? 300),
                    NeedleInsertions: (int)(OptNum(a, "insertions") ?? 6),
                    MinimumThickness: OptNum(a, "min_thickness") ?? 2.0,
                    AcceptMerit: OptNum(a, "accept_merit") ?? 0.1,
                    NeedleMaterials: OptList(a, "insert"))));

            case "optimise_start":
                return Wrap(_api.StartRun(Str(a, "design"), new RunRequest(
                    Opt(a, "method") ?? "refine", Opt(a, "norm"), (int)(OptNum(a, "iterations") ?? 300),
                    Starts: (int)(OptNum(a, "starts") ?? 24),
                    Insertions: (int)(OptNum(a, "insertions") ?? 6),
                    MinimumThickness: OptNum(a, "min_thickness") ?? 2.0,
                    AllowedDegradation: (OptNum(a, "degradation") ?? 5.0) / 100.0,
                    AcceptMerit: OptNum(a, "accept_merit") ?? 0.1,
                    MaxRemovals: (int)(OptNum(a, "removals") ?? 16),
                    GlobalMode: Opt(a, "mode") ?? "random",
                    Perturbation: (OptNum(a, "perturbation") ?? 10.0) / 100.0,
                    RandomRange: OptNum(a, "range") ?? 2.0,
                    AcceptWorse: (OptNum(a, "accept_worse") ?? 0.0) / 100.0,
                    Seed: OptNum(a, "seed") is { } seed ? (int)seed : null,
                    Threads: OptNum(a, "threads") is { } threads ? (int)threads : null,
                    GrowthSteps: (int)(OptNum(a, "growth_steps") ?? 8),
                    GrowthQwot: OptNum(a, "growth_qwot") ?? 1.0,
                    GrowAt: Opt(a, "grow_at") ?? "both",
                    MaxLayers: (int)(OptNum(a, "max_layers") ?? 0),
                    Raise: OptBool(a, "raise") ?? false,
                    NeedleMaterials: OptList(a, "insert"))));

            case "optimise_status":
            {
                var run = Str(a, "run");
                if (OptNum(a, "wait_seconds") is { } wait && wait > 0)
                    _api.WaitRun(run, (int)Math.Min(wait * 1000.0, int.MaxValue));
                return Wrap(_api.RunStatus(run, (int)(OptNum(a, "since") ?? 0)));
            }

            case "optimise_stop":
                return Wrap(_api.StopRun(Str(a, "run")));

            case "optimise_keep":
                return Wrap(_api.KeepRun(Str(a, "run")));

            case "optimise_revert":
                return Wrap(_api.RevertRun(Str(a, "run")));

            case "optimise_compare":
            {
                string run = Str(a, "run");
                var c = _api.CompareRun(run);
                if (!c.Ok) return Wrap(c);
                if (OptNum(a, "from") is not { } from) return Ok(c.Value!);
                var quantities = Opt(a, "quantities");
                var s = _api.RunSpectrum(run, new SpectrumRequest(
                    from, Num(a, "to"), (int)(OptNum(a, "steps") ?? 201), OptNum(a, "angle_degrees") ?? 0.0,
                    quantities?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
                if (!s.Ok) return Wrap(s);
                return Ok(new { comparison = c.Value, spectra = s.Value });
            }

            case "optimise_runs":
                return Ok(_api.Runs());

            case "needle_map":
                return Wrap(_api.NeedleMap(Str(a, "design"), (int)(OptNum(a, "samples") ?? 32)));

            case "set_thickness":
            {
                var r = _api.SetThickness(Str(a, "design"), Position(a, "layer"),
                                          Num(a, "thickness"));
                return r.Ok ? Ok(new { changed = true }) : Fail(r.Error, r.Status);
            }

            case "insert_layer":
                return Wrap(_api.InsertLayer(Str(a, "design"), Position(a, "before"),
                                             Position(a, "material"), Num(a, "thickness")));

            case "remove_layer":
                return Wrap(_api.RemoveLayer(Str(a, "design"), Position(a, "layer")));

            case "new_design":
            {
                var database = MaterialDatabase.ForPerson(out _);
                return Wrap(_api.CreateNew(database, Str(a, "name"), Opt(a, "incident") ?? "air", Str(a, "substrate"),
                                           Names(Str(a, "materials")), OptNum(a, "reference_nm") ?? 550.0,
                                           (int)(OptNum(a, "layers") ?? 4), Opt(a, "wavelength_unit") ?? "nm"));
            }

            case "edit_stack":
            {
                string design = Str(a, "design");
                switch (Str(a, "action"))
                {
                    case "string":
                        Dictionary<char, string>? letters = null;
                        if (Opt(a, "letters") is { } map)
                        {
                            letters = new();
                            foreach (var pair in Names(map))
                            {
                                var kv = pair.Split('=', 2, StringSplitOptions.TrimEntries);
                                if (kv.Length != 2 || kv[0].Length != 1)
                                    return Fail($"letters wants letter=material pairs, e.g. H=TiO2,L=SiO2, not '{pair}'", null);
                                letters[char.ToUpperInvariant(kv[0][0])] = kv[1];
                            }
                        }
                        return Wrap(_api.ApplyDesignText(design, Str(a, "text"), letters, Opt(a, "unit") ?? "QWOT",
                                                         OptBool(a, "substrate_first") ?? false, OptBool(a, "pickups") ?? true));
                    case "couple": return Wrap(_api.CoupleLayers(design));
                    case "decouple": return Wrap(_api.DecoupleLayers(design));
                    case "clear": return Wrap(_api.ClearLayers(design));
                    case "reverse": return Wrap(_api.ReverseLayers(design, media: false));
                    case "reverse_media": return Wrap(_api.ReverseLayers(design, media: true));
                    case "bounds":
                    {
                        int count = _api.Describe(design).Value?.Layers ?? 0;
                        return Wrap(_api.SetVaryConstraints(design, OptPosition(a, "first") ?? 0,
                                                            OptPosition(a, "last") ?? count - 1, Opt(a, "material"),
                                                            Opt(a, "unit") ?? "nm", OptNum(a, "min"), OptNum(a, "max")));
                    }
                    default: return Fail("action is string, couple, decouple, clear, bounds, reverse or reverse_media", null);
                }
            }

            case "edit_layer":
            {
                string design = Str(a, "design");
                switch (Str(a, "action"))
                {
                    case "insert":
                        if (Opt(a, "material") is { } material)
                            return Wrap(_api.AddLayer(design, OptPosition(a, "before") ?? _api.Describe(design).Value?.Layers ?? 0,
                                                      material, Num(a, "value"), Opt(a, "unit"), OptBool(a, "vary") ?? true));
                        return Wrap(_api.InsertLayerCopy(design, OptPosition(a, "after") ?? -1));
                    case "remove": return Wrap(_api.RemoveLayer(design, Position(a, "layer")));
                    case "move": return Wrap(_api.MoveLayer(design, Position(a, "layer"), Position(a, "to")));
                    case "set":
                        return Wrap(_api.EditLayer(design, Position(a, "layer"), new LayerChange(
                            Opt(a, "material"), Opt(a, "unit"), OptNum(a, "value"), OptBool(a, "vary"),
                            OptPosition(a, "pickup") is { } parent ? new LayerPickup(parent, OptNum(a, "scale") ?? 1.0, OptNum(a, "offset") ?? 0.0) : null,
                            OptBool(a, "free") ?? false, OptNum(a, "min"), OptNum(a, "max"),
                            OptBool(a, "no_min") ?? false, OptBool(a, "no_max") ?? false)));
                    default: return Fail("action is insert, remove, move or set", null);
                }
            }

            case "list_materials":
                if (Opt(a, "design") is { } of)
                {
                    // Each material in a line, as the CLI lists them; the whole definitions (a table's every
                    // row) only when asked: 17 kB for the guide's silicon design otherwise.
                    var set = _api.MaterialSet(of);
                    if (!set.Ok || OptBool(a, "full") == true) return Wrap(set);
                    var v = set.Value!;
                    object Said(NamedMaterial m) => new { m.Name, Summary = MaterialSummary.Line(m), m.Model.Source };
                    return Ok(new
                    {
                        Materials = v.Materials.Select((m, i) => new { m.Name, Layers = v.LayersUsing[i], Summary = MaterialSummary.Line(m), m.Model.Source }).ToArray(),
                        Incident = Said(v.Incident),
                        Substrate = Said(v.Substrate),
                    });
                }
                return Ok(MaterialDatabase.ForPerson(out _).Materials
                    .Select(m => new { m.Name, Summary = MaterialSummary.Line(m), m.Model.Source }).ToArray());

            case "edit_materials":
                if (Opt(a, "rename") is { } renamed)
                    return Wrap(_api.RenameMaterial(Str(a, "design"), renamed, Str(a, "rename_to")));
                if (OptBool(a, "upgrade") == true)
                    return Wrap(_api.UpgradeConstantMaterials(Str(a, "design"), MaterialDatabase.ForPerson(out _)));
                return Wrap(_api.ChangeMaterials(Str(a, "design"), MaterialDatabase.ForPerson(out _), new MaterialChange(
                    Opt(a, "add") is { } add ? Names(add) : null, Opt(a, "remove") is { } remove ? Names(remove) : null,
                    Opt(a, "replace"), Opt(a, "with"), Opt(a, "incident"), Opt(a, "substrate"))));

            case "set_units":
                return Wrap(_api.SetUnits(Str(a, "design"), new UnitsChange(
                    Opt(a, "thickness_unit"), OptNum(a, "reference_nm"), Opt(a, "wavelength_unit"), OptNum(a, "maximum_nm"))));

            case "set_system":
                return Wrap(_api.SetSystem(Str(a, "design"), new SystemChange(
                    OptBool(a, "back_surface"), OptNum(a, "substrate_mm"), OptBool(a, "angle_in_air"))));

            case "edit_operand":
            {
                string design = Str(a, "design");
                switch (Str(a, "action"))
                {
                    case "insert": return Wrap(_api.InsertOperandCopy(design, OptPosition(a, "after") ?? -1));
                    case "remove": return Wrap(_api.RemoveOperand(design, Position(a, "row")));
                    case "edit":
                        return Wrap(_api.EditOperand(design, Position(a, "row"), new OperandChange(
                            Opt(a, "code"), Opt(a, "mode"), OptNum(a, "target"), OptNum(a, "target2"), OptNum(a, "weight"),
                            OptNum(a, "wavelength"), OptNum(a, "wavelength2"), OptNum(a, "points") is { } n ? (int)n : null,
                            OptNum(a, "angle_degrees"), OptNum(a, "limit_nm"), OptNum(a, "temperature_k"))));
                    default: return Fail("action is edit, insert or remove", null);
                }
            }

            case "save_merit_function":
                return Wrap(_api.SaveMeritFunction(Str(a, "design")));

            case "open_merit_function":
                return Wrap(_api.OpenMeritFunction(Str(a, "design"), Str(a, "document"), OptBool(a, "append") ?? false));

            case "zemax_coatings":
                return Wrap(ThinFilmApi.ZemaxCoatings(Str(a, "path")));

            case "import_zemax_coating":
                return Wrap(_api.ImportZemaxCoating(Str(a, "design"), Str(a, "path"), Str(a, "coating")));

            case "export_zemax_coating":
                return Wrap(_api.ExportZemaxCoating(Str(a, "design"), new ZemaxExportRequest(
                    Str(a, "path"), Opt(a, "coating"), OptNum(a, "from_nm"), OptNum(a, "to_nm"),
                    OptBool(a, "replace") ?? false)));

            case "optiland_coatings":
                return Wrap(ThinFilmApi.OptilandCoatings(Str(a, "path")));

            case "import_optiland_coating":
                return Wrap(_api.ImportOptilandCoating(Str(a, "design"), Str(a, "path"), (int)(OptNum(a, "coating") ?? 1) - 1));

            case "design_report":
                return Wrap(_api.DesignReport(Str(a, "design"), new DesignReportRequest(
                    OptNum(a, "from_nm"), OptNum(a, "to_nm"), OptNum(a, "step_nm"), Opt(a, "path"))));

            case "export_optiland_coating":
                return Wrap(_api.ExportOptilandCoating(Str(a, "design"), Str(a, "path")));

            case "set_plot":
            {
                string id = Str(a, "design");
                if (OptBool(a, "clear") == true) return Wrap(_api.SetPlot(id, null));
                var now = _api.Describe(id).Value?.Plot ?? PlotDefaults.Plot;
                var p = new PlotDocument(
                    OptNum(a, "from_nm") ?? now.From, OptNum(a, "to_nm") ?? now.To, (int)(OptNum(a, "steps") ?? now.Steps),
                    OptNum(a, "angle_deg") ?? now.Angle,
                    Opt(a, "quantities")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? now.Quantities,
                    OptBool(a, "vs_angle") ?? now.VsAngle, OptNum(a, "angle_from") ?? now.AngleFrom, OptNum(a, "angle_to") ?? now.AngleTo,
                    (int)(OptNum(a, "angle_steps") ?? now.AngleSteps), OptNum(a, "wavelength_nm") ?? now.Wavelength);
                return Wrap(_api.SetPlot(id, p));
            }

            case "define_material":
            {
                if (Opt(a, "kind") is not { } kind)
                    return Wrap(_api.DescribeMaterial(Str(a, "design"), Str(a, "name"), Opt(a, "source"),
                                                      OptNum(a, "range_min"), OptNum(a, "range_max")));
                string? text = null;
                if (kind.Equals("table", StringComparison.OrdinalIgnoreCase))
                {
                    if (Opt(a, "table_path") is not { } tp || !File.Exists(tp)) return Fail($"table needs table_path, a file that exists ({Opt(a, "table_path")})", null);
                    text = File.ReadAllText(tp);
                }
                var model = MaterialTables.Model(kind, Opt(a, "coefficients"), text, Opt(a, "table_unit"),
                                                 OptNum(a, "range_min"), OptNum(a, "range_max"), Opt(a, "source"), out var bad);
                if (model is null) return Fail(bad, null);
                return Wrap(_api.DefineMaterial(Str(a, "design"), Str(a, "name"), model, Opt(a, "role") ?? "coating"));
            }

            case "save_material_to_database":
                return Wrap(_api.SaveToDatabase(Str(a, "design"), Str(a, "material"), OptBool(a, "replace") ?? false));

            case "rename_design":
                return Wrap(_api.SetName(Str(a, "design"), Str(a, "name")));

            case "set_description":
                return Wrap(_api.SetDescription(Str(a, "design"), Opt(a, "source"), Opt(a, "notes")));

            case "export_merit_csv":
                return Wrap(_api.ExportMeritCsv(Str(a, "design")));

            case "clear_operands":
                return Wrap(_api.ClearOperands(Str(a, "design")));

            case "delete_operands":
                return Wrap(_api.DeleteOperands(Str(a, "design"), Filter(a)));

            case "set_operand_weights":
                return Wrap(_api.SetOperandWeights(Str(a, "design"), Filter(a), Num(a, "weight")));

            case "add_operands":
                return Wrap(_api.AddOperands(Str(a, "design"), new OperandSweep(
                    Str(a, "code"), Str(a, "mode"), Num(a, "target"), OptNum(a, "weight") ?? 1.0,
                    Num(a, "from"), OptNum(a, "to"), OptNum(a, "step"),
                    OptNum(a, "aoi") ?? 0.0, OptNum(a, "aoi_to"), OptNum(a, "aoi_step"),
                    OptNum(a, "target2") ?? 0.0, OptNum(a, "temperature_k") ?? 0.0)));

            default:
                return Fail($"'{name}' is not a tool this server provides. Call tools/list.", null);
        }
    }

    // ---- results -------------------------------------------------------------------------

    private static (string, bool) Ok(object value) =>
        (Numbered(JsonSerializer.SerializeToNode(value, Pretty)) is { } node ? node.ToJsonString(Pretty) : "null", false);

    // ---- counting from 1 ---------------------------------------------------------------------
    //
    // LAYERS AND MERIT ROWS COUNT FROM 1 HERE, as the GUI's tables and the CLI number them (the user,
    // 2026-10-25: "Most users are not used to 0-based layer index"). The API counts from 0, as code
    // does; this edge converts both ways, as the CLI's does. Arguments go through Position; results
    // through Numbered, which shifts each field that is a position - recognised by the record it is
    // in, because ProfileLayer's Index is a refractive index, not a position.

    /// <summary>A layer, merit row or material number given counting from 1, as the API's position.</summary>
    private static int Position(JsonObject a, string name)
    {
        double v = Num(a, name);
        if (v < 1 || v != Math.Floor(v))
            throw new ArgumentException($"'{name}' counts from 1, as the GUI's tables number layers and merit rows; {v} is not one");
        return (int)v - 1;
    }

    private static int? OptPosition(JsonObject a, string name) => a[name] is null ? null : Position(a, name);

    private static readonly string[] PositionLists = ["Set", "PickedUp", "PlacedOnBound"];

    /// <summary>The result with every position counted from 1.</summary>
    public static JsonNode? Numbered(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                bool layer = o.ContainsKey("Index") && o.ContainsKey("Variable") && o.ContainsKey("Thickness");
                bool row = o.ContainsKey("Index") && o.ContainsKey("Code") && o.ContainsKey("Comparison");
                bool coating = o.ContainsKey("Index") && o.ContainsKey("Name") && o.ContainsKey("Layers") && o.ContainsKey("Problem");
                if (layer || row || coating) Shift(o, "Index");
                if (layer && o["Pickup"] is JsonObject pickup) Shift(pickup, "Parent");
                foreach (var name in PositionLists)
                    if (o[name] is JsonArray list)
                        for (int i = 0; i < list.Count; ++i)
                            if (list[i] is JsonValue v && v.TryGetValue(out int p)) list[i] = p + 1;
                foreach (var (_, child) in o.ToList())
                    if (child is JsonObject or JsonArray) Numbered(child);
                return o;
            case JsonArray arr:
                foreach (var child in arr) Numbered(child);
                return arr;
            default:
                return node;
        }

        static void Shift(JsonObject o, string name)
        {
            if (o[name] is JsonValue v && v.TryGetValue(out int p)) o[name] = p + 1;
        }
    }

    private static (string, bool) Fail(string? error, string? status) =>
        (JsonSerializer.Serialize(new
        {
            error = error ?? "the call failed",
            status,
        }, Pretty), true);

    /// <summary>
    /// An API failure becomes a RESULT with isError set, never a protocol error. The model asked
    /// for something reasonable and it could not be done; that is an answer it should read. The
    /// engine's own status travels with it, because TF_NO_STRESS_DATA says what to fix.
    /// </summary>
    private static (string, bool) Wrap<T>(ApiResult<T> r) =>
        r.Ok ? Ok(r.Value!) : Fail(r.Error, r.Status);

    // ---- arguments ---------------------------------------------------------------------------
    //
    // These throw plain messages; Call catches them and turns them into isError text. A missing
    // argument is the model's mistake and the model can fix it, which it can only do if told.

    private static string Str(JsonObject a, string name) =>
        a[name]?.GetValue<string>()
        ?? throw new ArgumentException($"'{name}' is required and was not supplied");

    private static string? Opt(JsonObject a, string name) =>
        a[name] is { } n && n.GetValueKind() == JsonValueKind.String ? n.GetValue<string>() : null;

    private static double Num(JsonObject a, string name)
    {
        var n = a[name] ?? throw new ArgumentException($"'{name}' is required and was not supplied");
        // A number is read from its JSON text, not GetValue<double>(): a node built in code from
        // an int (steps = 3) holds an Int32, which GetValue<double>() refuses. A string is read
        // in the invariant culture, so "1.5" is one and a half whatever the machine's locale.
        return n.GetValueKind() switch
        {
            JsonValueKind.Number => double.Parse(n.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture),
            JsonValueKind.String when double.TryParse(n.GetValue<string>(), NumberStyles.Float,
                                                      CultureInfo.InvariantCulture, out var v) => v,
            _ => throw new ArgumentException($"'{name}' must be a number"),
        };
    }

    private static double? OptNum(JsonObject a, string name) =>
        a[name] is null ? null : Num(a, name);

    private static bool? OptBool(JsonObject a, string name) => a[name]?.GetValueKind() switch
    {
        null => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new ArgumentException($"'{name}' is true or false"),
    };

    /// <summary>A comma-separated list, or null when not given.</summary>
    private static string[]? OptList(JsonObject a, string name) =>
        Opt(a, name)?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string[] Names(string list) =>
        list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
