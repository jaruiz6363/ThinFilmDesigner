// ThinFilmApi.cs — the surface the CLI, the MCP server and the GUI all sit on.
//
// ONE API, THREE FRONT ENDS, AND THAT IS THE WHOLE POINT. X1.8, X1.9 and X1.10 all bind to this
// row. If each grew its own way of asking for a spectrum, three answers to the same question would
// drift apart and the one a user trusted would depend on which window they had open. So every
// operation lives here and the front ends do nothing but translate their own idea of a request
// into a call and their own idea of a reply out of a result.
//
// NOTHING IN THIS FILE COMPUTES A SPECTRUM. That is worth stating because Spectrum() looks exactly
// like the place it would happen: a caller asks for R from 400 to 700 nm at 1 nm, and the obvious
// implementation interpolates the operand points. That would be physics in C# and it would be
// wrong at every wavelength between two samples. Instead a REPORTING DESIGN is built with those
// points and handed to the engine, which is what G6.1 means by "there is no target grid at the
// ABI: every point is an evaluation point, and the density policy is managed-side".
//
// RESULTS, NOT EXCEPTIONS, at the boundary. A front end is a program that has to say something
// useful when a design will not plan, and catching a typed exception to build a message is a worse
// shape than reading one. Native status codes are carried through by name and number rather than
// flattened, because "TF_NO_STRESS_DATA" tells a user what to fix and "operation failed" does not.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using ThinFilm.Native;
using ThinFilm.Optimize;

namespace ThinFilm.Api;

// ---- results -------------------------------------------------------------------------------

public sealed record ApiResult(bool Ok, string? Error = null, string? Status = null)
{
    public static ApiResult Success() => new(true);
    public static ApiResult Failure(string error, string? status = null) => new(false, error, status);
}

public sealed record ApiResult<T>(bool Ok, T? Value = default, string? Error = null,
                                  string? Status = null)
{
    public static ApiResult<T> Success(T value) => new(true, value);
    public static ApiResult<T> Failure(string error, string? status = null)
        => new(false, default, error, status);
}

public sealed record DesignSummary(
    string Id, string Name, int Layers, int Materials, int Wavelengths, int Points,
    int Operands, int Variables, int Residuals, double TotalThickness, double Merit,
    string[] Warnings, MediumReport? Incident = null, MediumReport? Exit = null,
    string WavelengthUnit = "nm", string? Blocked = null, string ThicknessUnit = "nm",
    double ReferenceWavelength = 0.0, double UnconstrainedMaximum = 600.0,
    bool BackSurface = false, double SubstrateThicknessMm = 1.0, bool AngleInAir = false,
    string? Source = null, string? Notes = null, PlotDocument? Plot = null);

/// <summary>A medium as a person asks about it: what it is, and the range of its index over the
/// design's grid. A range rather than one value, so a dispersive substrate is not shown as
/// constant.</summary>
public sealed record MediumReport(string Name, double NMin, double NMax, double KMax);

/// <summary>A layer as a table shows it. Thickness is nm; Value is the same thickness in Unit, the
/// unit it is shown in (its own, or the design's when UnitSetting is null). Min and Max
/// are its bounds in that unit too, null where it has none.</summary>
public sealed record LayerReport(int Index, string Material, double Thickness, bool Variable,
                                 string Unit = "nm", string? UnitSetting = null, double Value = double.NaN,
                                 LayerPickup? Pickup = null, double? Min = null, double? Max = null);

/// <summary>Everything Layer properties sets, at once. Value is in Unit (or the design's unit when
/// Unit is null), and is ignored for a layer with a Pickup, which takes its thickness from it.</summary>
public sealed record LayerProperties(int Material, string? Unit, double Value, bool Variable,
                                     LayerPickup? Pickup, double? Min = null, double? Max = null);

/// <summary>Set vary constraints: the bounds of every layer from First to Last (counting from 0),
/// of one Material or all (null), in Unit - each layer's own index counting for QWOT and FWOT.
/// Min and Max null is unconstrained.</summary>
public sealed record BoundsRequest(int First, int Last, int? Material, string Unit, double? Min, double? Max);

/// <summary>What Set vary constraints did: the layers bounded, and the picked-up ones passed over.</summary>
public sealed record BoundsReport(int[] Set, int[] PickedUp);

public sealed record OperandReport(
    int Index, string Code, string Comparison, double Target, double Value, double Residual,
    double Weight, double Scale, string[] Flags, OperandPoints? At = null, double Threshold = 0.0,
    double TargetHi = 0.0, double Temperature = 0.0);

/// <summary>Where an operand is evaluated: the span of wavelengths (nm) and angles (degrees) of
/// the points it reads. Null for an operand that reads no point — a stack operand like MINT, or
/// one built from other operands. Without it a merit table of 27 pointwise rows reads as 27
/// copies of "RA LE 0.005".</summary>
/// <summary>
/// A merit row as an editor holds it: the code, how it is compared, what to, how much it counts,
/// and the wavelengths (nm) and angle it is evaluated at. A stack operand (MINT, TOTT, ...) has
/// no wavelengths; a pointwise one has one; a span has several. Threshold is the layer limit of
/// MINT and MAXT and zero otherwise; Temperature is the blackbody's, in kelvin, for EMIT and
/// AERATIO and zero otherwise.
///
/// SCALE IS CARRIED, NOT SHOWN. It divides the error before weighting, so that rows measured in
/// fractions and rows measured in nanometres can share weights of order one; the user chose to
/// have it set automatically rather than shown. Scale = 0 asks for one to be chosen (a new row,
/// or a changed code or limit); any other value is kept, INCLUDING WHEN THE TARGET CHANGES. The
/// samples set it as an error budget - 0.1 for T >= 0.9, 1e-4 for R = 0, 2 nm for an edge at
/// 580 nm - and a rule tying it to the target would have cut a Bandpass row's effective weight
/// eighty-fold the moment its target was retyped. How precise a target must be does not change
/// because the target moved.
/// </summary>
public sealed record OperandEdit(
    string Code, string Comparison, double Target, double TargetHi, double Weight,
    double[] Wavelengths, double AngleDegrees, double Threshold = 0.0, double Scale = 0.0,
    double Temperature = 0.0);

/// <summary>A design's own material copies: its coating materials, its media, and how many
/// layers use each material.</summary>
public sealed record MaterialSet(NamedMaterial[] Materials, NamedMaterial Incident,
                                 NamedMaterial Substrate, int[] LayersUsing);

/// <summary>One material as Edit > Materials leaves it, and which of the design's materials it
/// was before (-1 for one copied in from the database).</summary>
public sealed record MaterialEdit(NamedMaterial Material, int Was);

public sealed record OperandPoints(
    int Count, double WavelengthMin, double WavelengthMax, double AngleMin, double AngleMax);

public sealed record EvaluationReport(
    double Merit, OperandReport[] Operands, LayerReport[] Layers, int FlaggedOperands,
    string? Blocked = null);

/// <summary>VsAngle: From and To are angles of incidence in degrees, at the one wavelength
/// Wavelength (nm), and AngleDegrees is not read. Otherwise From and To are wavelengths in nm at
/// AngleDegrees. Angles are in air or in the incident medium as the design's AngleInAir says.</summary>
public sealed record SpectrumRequest(
    double From, double To, int Steps, double AngleDegrees = 0.0,
    string[]? Quantities = null, bool VsAngle = false, double Wavelength = 550.0);

/// <summary>Angle null: one value per wavelength. Angle set (a sweep in angle): one value per
/// angle, in degrees as requested, at the single wavelength in Wavelength. An angle that cannot
/// be reached in the incident medium has NaN for every quantity.</summary>
public sealed record SpectrumReport(double[] Wavelength, Dictionary<string, double[]> Quantity,
                                    double[]? Angle = null)
{
    /// <summary>Each material the plot reaches outside its valid range, in words: "Si3N4 is outside its
    /// valid range (51.7-826.6 nm) at 830-1000 nm". Empty when none is.</summary>
    public string[] Outside { get; init; } = [];
}

public sealed record OptimiseRequest(
    string Stages = "refine", string? Norm = null, int MaxIterations = 300,
    int GlobalStarts = 24, int NeedleInsertions = 6, double MinimumThickness = 2.0, double AcceptMerit = 0.1,
    string[]? NeedleMaterials = null);

/// <summary>PlacedOnBound: the layers (from 0) that started outside their own min or max and were
/// put on it before the first step - the one move that is not a reflection.</summary>
public sealed record OptimiseReport(
    double InitialMerit, double FinalMerit, string Outcome, int Layers,
    string[] StagesRun, double ElapsedMilliseconds, int[]? PlacedOnBound = null);

public sealed record NeedleReport(
    double[] Depth, string[] Candidate, double[] Map, int BestCandidate, double BestDepth,
    double BestSlope);

// ---- the API -------------------------------------------------------------------------------

public sealed partial class ThinFilmApi
{
    private sealed class Session
    {
        public required string Id { get; init; }
        public required DesignDocument Document { get; set; }
        public Design? Design { get; set; }

        /// <summary>Why this design cannot be evaluated - a material it names that is not here - or
        /// null. A blocked design is open: it can be shown, saved and edited, and any edit that makes
        /// it evaluable again (another substrate) unblocks it. It is never evaluated in the meantime.</summary>
        public string? Blocked { get; set; }
        public List<string> Warnings { get; } = new();
    }

    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private int _next;

    public int AbiVersion => NativeMethods.tf_abi_version();

    // ---- creating --------------------------------------------------------------------------

    /// <summary>A design from nothing: the media, materials and starting stack a person chose,
    /// with one placeholder merit row. See NewDesign.cs.</summary>
    public ApiResult<DesignSummary> CreateNew(NewDesignRequest request)
    {
        var problem = NewDesigns.Problem(request);
        if (problem is not null) return ApiResult<DesignSummary>.Failure(problem);
        return CreateFromDocument(NewDesigns.Document(request));
    }

    public ApiResult<DesignSummary> CreateFromDocument(DesignDocument document)
    {
        try { return Open(document, System.Array.Empty<string>()); }
        catch (Exception e) { return ApiResult<DesignSummary>.Failure(e.Message); }
    }

    public ApiResult<DesignSummary> Load(string json)
    {
        try { return Open(DesignDocument.FromJson(json), System.Array.Empty<string>()); }
        catch (Exception e) { return ApiResult<DesignSummary>.Failure(e.Message); }
    }

    public ApiResult<string> Save(string id)
    {
        if (!Find(id, out var s, out var fail)) return ApiResult<string>.Failure(fail!);
        return ApiResult<string>.Success(Current(s).ToJson());
    }

    public ApiResult<DesignDocument> Document(string id)
    {
        if (!Find(id, out var s, out var fail)) return ApiResult<DesignDocument>.Failure(fail!);
        return ApiResult<DesignDocument>.Success(Current(s));
    }

    public ApiResult Close(string id)
    {
        // A run still going on a design being closed has nothing left to be kept into.
        foreach (var r in _runs.Values.Where(r => r.Session == id && r.State == "running"))
            RevertRun(r.Id);
        return _sessions.Remove(id) ? ApiResult.Success() : ApiResult.Failure($"no session '{id}'");
    }

    public IReadOnlyList<string> Sessions => _sessions.Keys.ToArray();

    // ---- inspecting ------------------------------------------------------------------------

    public ApiResult<DesignSummary> Describe(string id)
    {
        if (!Find(id, out var s, out var fail)) return ApiResult<DesignSummary>.Failure(fail!);
        return ApiResult<DesignSummary>.Success(Summarise(s));
    }

    public ApiResult<EvaluationReport> Evaluate(string id) =>
        Find(id, out var s, out var fail) ? Evaluate(s) : ApiResult<EvaluationReport>.Failure(fail);

    private static ApiResult<EvaluationReport> Evaluate(Session s)
    {
        if (s.Blocked is { } blocked)
        {
            // REFUSED TO EVALUATE, BUT NOT EMPTY: the rows and the stack, with no numbers - NaN, not
            // the values the file last held, which would be a silent substitute - and the reason.
            var rows = s.Document.Operands.Select((o, i) => new OperandReport(
                i, o.Code, o.Comparison, o.Target, double.NaN, double.NaN, o.Weight, o.Scale, [],
                Where(s.Document, o), o.Threshold, o.TargetHi, o.Temperature)).ToArray();
            return ApiResult<EvaluationReport>.Success(
                new EvaluationReport(double.NaN, rows, Layers(s), 0, blocked));
        }
        try
        {
            var d = s.Design!;
            d.Evaluate();

            // The document's rows, not the engine's: an AERATIO row brings two hidden rows after them.
            var ops = new OperandReport[s.Document.Operands.Length];
            for (int o = 0; o < ops.Length; ++o)
            {
                int row = d.ResidualRow(o);
                var doc = s.Document.Operands[o];
                ops[o] = new OperandReport(
                    o, doc.Code, doc.Comparison, doc.Target, d.Value[o],
                    row >= 0 ? d.Residual[row] : 0.0, doc.Weight, doc.Scale,
                    FlagNames(d.OperandFlag[o]), Where(s.Document, doc), doc.Threshold, doc.TargetHi, doc.Temperature);
            }

            return ApiResult<EvaluationReport>.Success(
                new EvaluationReport(d.Merit, ops, Layers(s), ops.Count(o => o.Flags.Length > 0)));
        }
        catch (ThinFilmException e)
        { return ApiResult<EvaluationReport>.Failure(e.Message, e.Status.ToString()); }
    }

    /// <summary>
    /// A spectrum on any grid the caller likes — which is a REPORT and not the merit function.
    ///
    /// Built as its own Design over the requested points and evaluated by the engine. Nothing is
    /// interpolated from the operand points, because interpolating a coating spectrum is how a
    /// ripple between two samples becomes invisible, and a filter's ripple is usually the thing
    /// the person asking is looking for.
    /// </summary>
    public ApiResult<SpectrumReport> Spectrum(string id, SpectrumRequest request) =>
        Find(id, out var s, out var fail) ? Spectrum(s, request) : ApiResult<SpectrumReport>.Failure(fail);

    private static ApiResult<SpectrumReport> Spectrum(Session s, SpectrumRequest request)
    {
        // A spectrum does not read the merit function, so a table being built does not stop one.
        if (s.Blocked is { } why && !IsMeritBlock(why)) return ApiResult<SpectrumReport>.Failure(why, "BLOCKED");
        if (request.Steps < 2) return ApiResult<SpectrumReport>.Failure("a spectrum needs at least two steps");
        if (request.VsAngle)
        {
            if (!(request.From >= 0.0 && request.From < 90.0 && request.To >= 0.0 && request.To < 90.0))
                return ApiResult<SpectrumReport>.Failure("angles of incidence must be at least 0° and below 90°");
            if (!(request.Wavelength > 0.0 && double.IsFinite(request.Wavelength)))
                return ApiResult<SpectrumReport>.Failure("the wavelength must be positive");
        }

        try
        {
            var quantities = request.Quantities ?? ["RA", "TA"];
            if (quantities.FirstOrDefault(Codes.IsBand) is { } band)
                return ApiResult<SpectrumReport>.Failure(
                    $"{band.ToUpperInvariant()} is one number for a whole band, not a spectrum; plot what it averages "
                    + $"({(band.Trim().ToUpperInvariant() switch { "RSOL" => "RA", "TSOL" => "TA", _ => "AA" })}) instead");
            var doc = Current(s);
            var report = Documents.ReportingDocument(doc, request, quantities, out var reached);

            var design = new Design(report.ToSpec());
            design.Plan();
            design.Evaluate();

            // Unreachable points were left out of the reporting design; they read NaN, which a plot
            // draws as a gap.
            int n = request.Steps, kept = report.Points.Length;
            var byQuantity = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
            for (int q = 0; q < quantities.Length; ++q)
            {
                var values = new double[n];
                for (int i = 0, k = 0; i < n; ++i)
                    values[i] = reached[i] ? design.Value[q * kept + k++] : double.NaN;
                byQuantity[quantities[q]] = values;
            }

            return ApiResult<SpectrumReport>.Success((request.VsAngle
                ? new SpectrumReport(report.Wavelength, byQuantity, Documents.Grid(request))
                : new SpectrumReport(report.Wavelength, byQuantity)) with { Outside = OutsideRange(doc, report.Wavelength) });
        }
        catch (ThinFilmException e)
        { return ApiResult<SpectrumReport>.Failure(e.Message, e.Status.ToString()); }
        catch (Exception e) { return ApiResult<SpectrumReport>.Failure(e.Message); }
    }

    /// <summary>Where a layer would help, straight from the engine's needle map.</summary>
    public ApiResult<NeedleReport> NeedleMap(string id, int depthSamples = 64)
    {
        if (!Find(id, out var s, out var fail)) return ApiResult<NeedleReport>.Failure(fail!);
        if (s.Blocked is { } why) return ApiResult<NeedleReport>.Failure(why, "BLOCKED");
        try
        {
            var d = s.Design!;
            double total = 0.0;
            foreach (var t in d.LayerThickness) total += t;
            if (total <= 0.0) return ApiResult<NeedleReport>.Failure("the stack has no thickness");

            int nd = Math.Max(2, depthSamples);
            var depth = new double[nd];
            for (int i = 0; i < nd; ++i) depth[i] = total * i / (nd - 1.0);

            var candidates = Enumerable.Range(0, d.MaterialCount).ToArray();
            var map = d.NeedleMap(depth, candidates);

            int bestC = 0, bestI = 0;
            double best = double.PositiveInfinity;
            for (int c = 0; c < candidates.Length; ++c)
                for (int i = 0; i < nd; ++i)
                    if (map[c * nd + i] < best) { best = map[c * nd + i]; bestC = c; bestI = i; }

            var names = s.Document.Materials.Select(m => m.Name).ToArray();
            return ApiResult<NeedleReport>.Success(
                new NeedleReport(depth, names, map, bestC, depth[bestI], best));
        }
        catch (ThinFilmException e)
        { return ApiResult<NeedleReport>.Failure(e.Message, e.Status.ToString()); }
    }

    // ---- changing --------------------------------------------------------------------------

    public ApiResult SetThickness(string id, int layer, double thickness)
    {
        if (!Find(id, out var s, out var fail)) return ApiResult.Failure(fail!);
        if (layer >= 0 && layer < s.Document.Layers.Length && s.Document.Layers[layer].Pickup is { } p)
            return ApiResult.Failure(PickedUp(layer, p));
        if (s.Design is not { } d)
        {
            // Blocked: the thickness is still the person's to set, so it goes to the document.
            if (layer < 0 || layer >= s.Document.Layers.Length)
                return ApiResult.Failure($"there is no layer {layer + 1}; the stack has {s.Document.Layers.Length}");
            if (!(thickness >= 0.0)) return ApiResult.Failure($"a thickness of {thickness} is not a thickness");
            var layers = s.Document.Layers.ToArray();
            layers[layer] = layers[layer] with { Thickness = thickness };
            s.Document = s.Document with { Layers = layers };
            return ApiResult.Success();
        }
        if (layer < 0 || layer >= d.LayerCount)
            return ApiResult.Failure($"there is no layer {layer + 1}; the stack has {d.LayerCount}");
        if (!(thickness >= 0.0))
            return ApiResult.Failure($"a thickness of {thickness} is not a thickness");

        // In place: a thickness is a value, not a shape, so nothing is re-planned or re-allocated.
        d.LayerThickness[layer] = thickness;
        return ApiResult.Success();
    }

    /// <summary>Adding or removing a layer is a SHAPE change, so the design is rebuilt. Pickups
    /// follow their layers to their new numbers; <paramref name="pickup"/> names its parent as the
    /// stack was numbered BEFORE the insertion.</summary>
    public ApiResult<DesignSummary> InsertLayer(string id, int before, int material,
                                                double thickness, bool variable = true,
                                                string? unit = null, LayerPickup? pickup = null,
                                                double? min = null, double? max = null)
    {
        if (!Find(id, out var s, out var fail)) return ApiResult<DesignSummary>.Failure(fail!);
        var doc = Current(s);
        if (before < 0 || before > doc.Layers.Length)
            return ApiResult<DesignSummary>.Failure(
                $"cannot insert before layer {before}; the stack has {doc.Layers.Length}");
        if (material < 0 || material >= doc.Materials.Length)
            return ApiResult<DesignSummary>.Failure($"there is no material {material}");

        var newIndexOf = Enumerable.Range(0, doc.Layers.Length).Select(i => i < before ? i : i + 1).ToArray();
        var layers = LayerUnits.Renumber(doc.Layers, newIndexOf).ToList();
        if (pickup is not null && (pickup.Parent < 0 || pickup.Parent >= doc.Layers.Length))
            return ApiResult<DesignSummary>.Failure($"there is no layer {pickup.Parent + 1} to pick up");
        layers.Insert(before, new LayerDocument(material, thickness, variable)
        {
            Unit = unit,
            Min = min,
            Max = max,
            Pickup = pickup is null ? null : pickup with { Parent = newIndexOf[pickup.Parent] },
        });
        return ReplaceLayers(s, doc with { Layers = layers.ToArray() });
    }

    /// <summary>A layer another picks up cannot be deleted: the refusal names its children.</summary>
    public ApiResult<DesignSummary> RemoveLayer(string id, int layer)
    {
        if (!Find(id, out var s, out var fail)) return ApiResult<DesignSummary>.Failure(fail!);
        var doc = Current(s);
        if (layer < 0 || layer >= doc.Layers.Length)
            return ApiResult<DesignSummary>.Failure($"there is no layer {layer + 1}");
        var children = Children(doc, layer);
        if (children.Length > 0)
            return ApiResult<DesignSummary>.Failure(
                $"layer {layer + 1} is picked up by {LayerUnits.Named(children)}; remove "
                + (children.Length == 1 ? "that pickup" : "those pickups") + " first");

        var newIndexOf = Enumerable.Range(0, doc.Layers.Length)
            .Select(i => i < layer ? i : i == layer ? -1 : i - 1).ToArray();
        var layers = LayerUnits.Renumber(doc.Layers, newIndexOf).ToList();
        layers.RemoveAt(layer);
        return Replace(s, doc with { Layers = layers.ToArray() });
    }

    /// <summary>Layer <paramref name="from"/> moved to position <paramref name="to"/>, the rest
    /// closing up around it. A shape change in the engine's eyes, so the design is rebuilt.</summary>
    public ApiResult<DesignSummary> MoveLayer(string id, int from, int to)
    {
        if (!Find(id, out var s, out var fail)) return ApiResult<DesignSummary>.Failure(fail!);
        var doc = Current(s);
        if (from < 0 || from >= doc.Layers.Length || to < 0 || to >= doc.Layers.Length)
            return ApiResult<DesignSummary>.Failure(
                $"cannot move layer {from + 1} to {to + 1}; the stack has {doc.Layers.Length}");
        var order = Enumerable.Range(0, doc.Layers.Length).ToList();
        order.RemoveAt(from);
        order.Insert(to, from);
        var newIndexOf = new int[order.Count];
        for (int i = 0; i < order.Count; ++i) newIndexOf[order[i]] = i;
        var renumbered = LayerUnits.Renumber(doc.Layers, newIndexOf);
        return Replace(s, doc with { Layers = order.Select(i => renumbered[i]).ToArray() });
    }

    /// <summary>A QWOT or FWOT layer keeps its number in its new material, so its nanometres
    /// change; a layer in nm keeps its nanometres (LayerUnits.cs).</summary>
    public ApiResult<DesignSummary> SetLayerMaterial(string id, int layer, int material)
    {
        if (!Find(id, out var s, out var fail)) return ApiResult<DesignSummary>.Failure(fail!);
        var doc = Current(s);
        if (layer < 0 || layer >= doc.Layers.Length)
            return ApiResult<DesignSummary>.Failure($"there is no layer {layer + 1}");
        if (material < 0 || material >= doc.Materials.Length)
            return ApiResult<DesignSummary>.Failure($"there is no material {material + 1}");
        var layers = doc.Layers.ToArray();
        layers[layer] = layers[layer] with { Material = material };
        return ReplaceKeepingOptical(s, doc, doc with { Layers = layers });
    }

    /// <summary>Whether the optimiser may move a layer's thickness. A fixed layer is held where
    /// it is and is not a variable at all, so the variable count changes with it.</summary>
    public ApiResult<DesignSummary> SetLayerVariable(string id, int layer, bool variable)
    {
        if (!Find(id, out var s, out var fail)) return ApiResult<DesignSummary>.Failure(fail!);
        var doc = Current(s);
        if (layer < 0 || layer >= doc.Layers.Length)
            return ApiResult<DesignSummary>.Failure($"there is no layer {layer + 1}");
        if (doc.Layers[layer].Pickup is { } p) return ApiResult<DesignSummary>.Failure(PickedUp(layer, p));
        var layers = doc.Layers.ToArray();
        layers[layer] = layers[layer] with { Variable = variable };
        return Replace(s, doc with { Layers = layers });
    }

    // ---- thickness units and pickups (LayerUnits.cs) ------------------------------------------

    /// <summary>
    /// The design's thickness unit and reference wavelength (nm), as Edit > System Parameters sets them.
    /// A layer that is in QWOT or FWOT before and after keeps its number, so a new reference moves
    /// it; every other layer keeps its nanometres. Refused if a pickup would join two units.
    /// </summary>
    public ApiResult<DesignSummary> SetThicknessUnits(string id, string unit, double referenceNm)
    {
        if (!Find(id, out var s, out var fail)) return ApiResult<DesignSummary>.Failure(fail!);
        if (LayerUnits.Parse(unit) is not { } u)
            return ApiResult<DesignSummary>.Failure($"'{unit}' is not a thickness unit; use nm, QWOT or FWOT");
        var doc = Current(s);
        return ReplaceKeepingOptical(s, doc, doc with { ThicknessUnit = u, ReferenceWavelength = referenceNm });
    }

    /// <summary>One layer's unit: "nm", "QWOT", "FWOT", or null to follow the design's. The layer
    /// keeps its nanometres; a layer picked up from it, or that it picks up, must move with it.</summary>
    public ApiResult<DesignSummary> SetLayerUnit(string id, int layer, string? unit)
    {
        if (!Find(id, out var s, out var fail)) return ApiResult<DesignSummary>.Failure(fail!);
        var doc = Current(s);
        if (layer < 0 || layer >= doc.Layers.Length)
            return ApiResult<DesignSummary>.Failure($"there is no layer {layer + 1}");
        string? u = null;
        if (unit is not null && (u = LayerUnits.Parse(unit)) is null)
            return ApiResult<DesignSummary>.Failure($"'{unit}' is not a thickness unit; use nm, QWOT or FWOT");
        var layers = doc.Layers.ToArray();
        layers[layer] = layers[layer] with { Unit = u };
        return ReplaceLayers(s, doc with { Layers = layers });
    }

    /// <summary>The design's maximum for unconstrained layers, in nm: the ceiling the optimiser
    /// gives a layer with no maximum of its own (LayerUnits.cs, "UNCONSTRAINED").</summary>
    public ApiResult<DesignSummary> SetUnconstrainedMaximum(string id, double nm)
    {
        if (!Find(id, out var s, out var fail)) return ApiResult<DesignSummary>.Failure(fail!);
        if (!(nm > 0.0) || !double.IsFinite(nm))
            return ApiResult<DesignSummary>.Failure("the maximum for unconstrained layers must be a positive thickness");
        return Replace(s, Current(s) with { UpperBound = nm });
    }

    /// <summary>One layer's thickness, in the unit it is shown in.</summary>
    public ApiResult<DesignSummary> SetLayerValue(string id, int layer, double value)
    {
        if (!Find(id, out var s, out var fail)) return ApiResult<DesignSummary>.Failure(fail!);
        var doc = Current(s);
        if (layer < 0 || layer >= doc.Layers.Length)
            return ApiResult<DesignSummary>.Failure($"there is no layer {layer + 1}");
        if (doc.Layers[layer].Pickup is { } p) return ApiResult<DesignSummary>.Failure(PickedUp(layer, p));
        if (!(value >= 0.0) || !double.IsFinite(value))
            return ApiResult<DesignSummary>.Failure("a thickness must be zero or a positive number");
        try
        {
            double perNm = LayerUnits.PerNm(doc, doc.Layers[layer].Material, LayerUnits.UnitOf(doc, layer));
            var layers = doc.Layers.ToArray();
            layers[layer] = layers[layer] with { Thickness = value / perNm };
            return ReplaceLayers(s, doc with { Layers = layers });
        }
        catch (FormatException e) { return ApiResult<DesignSummary>.Failure($"layer {layer + 1}: {e.Message}"); }
    }

    /// <summary>Layer <paramref name="layer"/> picked up from another layer, or freed (null).
    /// A freed layer keeps the thickness the pickup last gave it.</summary>
    public ApiResult<DesignSummary> SetLayerPickup(string id, int layer, LayerPickup? pickup)
    {
        if (!Find(id, out var s, out var fail)) return ApiResult<DesignSummary>.Failure(fail!);
        var doc = Current(s);
        if (layer < 0 || layer >= doc.Layers.Length)
            return ApiResult<DesignSummary>.Failure($"there is no layer {layer + 1}");
        if (pickup is not null && Children(doc, layer) is { Length: > 0 } children)
            return ApiResult<DesignSummary>.Failure(
                $"layer {layer + 1} is picked up by {LayerUnits.Named(children)}, so it cannot pick up "
                + "another layer itself; a pickup names a free layer");
        var layers = doc.Layers.ToArray();
        layers[layer] = layers[layer] with { Pickup = pickup };
        return ReplaceLayers(s, doc with { Layers = layers });
    }

    /// <summary>
    /// Everything Layer properties sets, checked and applied together, so a dialog that changes
    /// the material, the unit and the pickup at once is never refused halfway through. The value
    /// is taken in the unit given, in the material given.
    /// </summary>
    public ApiResult<DesignSummary> SetLayerProperties(string id, int layer, LayerProperties p)
    {
        if (!Find(id, out var s, out var fail)) return ApiResult<DesignSummary>.Failure(fail!);
        var doc = Current(s);
        if (layer < 0 || layer >= doc.Layers.Length)
            return ApiResult<DesignSummary>.Failure($"there is no layer {layer + 1}");
        if (p.Material < 0 || p.Material >= doc.Materials.Length)
            return ApiResult<DesignSummary>.Failure($"there is no material {p.Material + 1}");
        string? unit = null;
        if (p.Unit is not null && (unit = LayerUnits.Parse(p.Unit)) is null)
            return ApiResult<DesignSummary>.Failure($"'{p.Unit}' is not a thickness unit; use nm, QWOT or FWOT");
        if (p.Pickup is not null && Children(doc, layer) is { Length: > 0 } children)
            return ApiResult<DesignSummary>.Failure(
                $"layer {layer + 1} is picked up by {LayerUnits.Named(children)}, so it cannot pick up "
                + "another layer itself; a pickup names a free layer");
        if (p.Pickup is null && (!(p.Value >= 0.0) || !double.IsFinite(p.Value)))
            return ApiResult<DesignSummary>.Failure("a thickness must be zero or a positive number");

        var layers = doc.Layers.ToArray();
        layers[layer] = layers[layer] with { Material = p.Material, Unit = unit, Variable = p.Variable, Pickup = p.Pickup };
        var next = doc with { Layers = layers };
        try
        {
            double perNm = LayerUnits.PerNm(next, p.Material, LayerUnits.UnitOf(next, layer));
            if (p.Pickup is null) layers[layer] = layers[layer] with { Thickness = p.Value / perNm };
            layers[layer] = layers[layer] with { Min = p.Min / perNm, Max = p.Max / perNm };
        }
        catch (FormatException e) { return ApiResult<DesignSummary>.Failure($"layer {layer + 1}: {e.Message}"); }
        return ReplaceLayers(s, next with { Layers = layers });
    }

    /// <summary>
    /// Set vary constraints on a range of layers: each layer from First to Last, of the material
    /// asked (or every material), given the bounds in the request's unit, converted in its own
    /// index. A picked-up layer follows its parent, so it is passed over and named in the report.
    /// </summary>
    public ApiResult<BoundsReport> SetLayerBounds(string id, BoundsRequest r)
    {
        if (!Find(id, out var s, out var fail)) return ApiResult<BoundsReport>.Failure(fail!);
        var doc = Current(s);
        int n = doc.Layers.Length;
        if (n == 0) return ApiResult<BoundsReport>.Failure("the design has no layers");
        if (r.First < 0 || r.Last >= n || r.First > r.Last)
            return ApiResult<BoundsReport>.Failure($"the layers must run from 1 to {n}, the first no later than the last");
        if (r.Material is { } m && (m < 0 || m >= doc.Materials.Length))
            return ApiResult<BoundsReport>.Failure($"there is no material {m + 1}");
        if (LayerUnits.Parse(r.Unit) is not { } unit)
            return ApiResult<BoundsReport>.Failure($"'{r.Unit}' is not a thickness unit; use nm, QWOT or FWOT");

        var layers = doc.Layers.ToArray();
        var set = new List<int>();
        var picked = new List<int>();
        try
        {
            for (int i = r.First; i <= r.Last; ++i)
            {
                if (r.Material is { } only && layers[i].Material != only) continue;
                if (layers[i].Pickup is not null) { picked.Add(i); continue; }
                double perNm = LayerUnits.PerNm(doc, layers[i].Material, unit);
                layers[i] = layers[i] with { Min = r.Min / perNm, Max = r.Max / perNm };
                set.Add(i);
            }
        }
        catch (FormatException e) { return ApiResult<BoundsReport>.Failure(e.Message); }
        var replaced = ReplaceLayers(s, doc with { Layers = layers });
        return replaced.Ok ? ApiResult<BoundsReport>.Success(new BoundsReport(set.ToArray(), picked.ToArray()))
                           : ApiResult<BoundsReport>.Failure(replaced.Error!, replaced.Status);
    }

    /// <summary>
    /// Couple all layers: every layer picked up from the first layer of its material in the stack,
    /// at the ratio of their thicknesses, so nothing moves. Existing pickups are replaced. A pickup
    /// joins two layers in one unit, so a material whose layers are in more than one unit gets a
    /// parent per unit; a parent is never a zero-thickness layer. Answers with how many were coupled.
    /// </summary>
    public ApiResult<int> CoupleLayers(string id)
    {
        if (!Find(id, out var s, out var fail)) return ApiResult<int>.Failure(fail!);
        var doc = Current(s);
        var values = LayerUnits.Values(doc);
        var parentOf = new Dictionary<(int, string), int>();
        var layers = doc.Layers.ToArray();
        int coupled = 0;
        for (int i = 0; i < layers.Length; ++i)
        {
            var key = (layers[i].Material, LayerUnits.UnitOf(doc, i));
            layers[i] = layers[i] with { Pickup = null };
            if (!parentOf.TryGetValue(key, out int parent))
            {
                if (values[i] > 0.0) parentOf[key] = i;
                continue;
            }
            layers[i] = layers[i] with { Variable = true, Pickup = new LayerPickup(parent, values[i] / values[parent]) };
            ++coupled;
        }
        var r = ReplaceLayers(s, doc with { Layers = layers });
        return r.Ok ? ApiResult<int>.Success(coupled) : ApiResult<int>.Failure(r.Error!, r.Status);
    }

    /// <summary>Decouple all layers: every pickup removed, each layer keeping its thickness and
    /// becoming a variable of its own. Answers with how many were picked up.</summary>
    public ApiResult<int> DecoupleLayers(string id)
    {
        if (!Find(id, out var s, out var fail)) return ApiResult<int>.Failure(fail!);
        var doc = Current(s);
        int freed = doc.Layers.Count(l => l.Pickup is not null);
        var layers = doc.Layers.Select(l => l.Pickup is null ? l : l with { Pickup = null, Variable = true }).ToArray();
        var r = ReplaceLayers(s, doc with { Layers = layers });
        return r.Ok ? ApiResult<int>.Success(freed) : ApiResult<int>.Failure(r.Error!, r.Status);
    }

    /// <summary>Delete all layers: the bare substrate, which the engine evaluates like any stack.
    /// Insert layer or Input design string builds it up again.</summary>
    /// <summary>The design's Description: where it came from and notes on it. Null leaves a field
    /// as it is; empty or blank clears it.</summary>
    public ApiResult<DesignSummary> SetDescription(string id, string? source, string? notes)
    {
        if (!Find(id, out var s, out var fail)) return ApiResult<DesignSummary>.Failure(fail!);
        var doc = Current(s);
        static string? Clean(string text) => string.IsNullOrWhiteSpace(text) ? null : text.ReplaceLineEndings("\n").Trim();
        return Replace(s, doc with
        {
            Source = source is null ? doc.Source : Clean(source.ReplaceLineEndings(" ")),
            Notes = notes is null ? doc.Notes : Clean(notes),
        });
    }

    /// <summary>Renames the design: one line, not blank. Nothing computed depends on the name, so
    /// the design is not rebuilt, and a blocked one can be renamed too. (The user, 2026-10-24: "we
    /// need to be able to rename" - a name mistyped in New design could not be put right.)</summary>
    public ApiResult<DesignSummary> SetName(string id, string name)
    {
        if (!Find(id, out var s, out var fail)) return ApiResult<DesignSummary>.Failure(fail!);
        string clean = (name ?? "").ReplaceLineEndings(" ").Trim();
        if (clean.Length == 0) return ApiResult<DesignSummary>.Failure("a design needs a name; this one is blank");
        s.Document = Current(s) with { Name = clean };
        return ApiResult<DesignSummary>.Success(Summarise(s));
    }

    /// <summary>The spectrum the design is saved with and opened to; null removes it.</summary>
    public ApiResult<DesignSummary> SetPlot(string id, PlotDocument? plot)
    {
        if (!Find(id, out var s, out var fail)) return ApiResult<DesignSummary>.Failure(fail!);
        if (plot?.Problem() is { } problem) return ApiResult<DesignSummary>.Failure($"Plot not set: {problem}.");
        if (plot is not null) plot = plot with { Quantities = plot.Quantities.Select(q => q.Trim().ToUpperInvariant()).ToArray() };
        // Nothing computed depends on it, so the design is not rebuilt (and a blocked one can still
        // be given one).
        s.Document = Current(s) with { Plot = plot };
        return ApiResult<DesignSummary>.Success(Summarise(s));
    }

    public ApiResult<DesignSummary> ClearLayers(string id)
    {
        if (!Find(id, out var s, out var fail)) return ApiResult<DesignSummary>.Failure(fail!);
        return Replace(s, Current(s) with { Layers = [] });
    }

    /// <summary>
    /// Reverse layers: the last layer becomes the first, each keeping its material, thickness,
    /// unit, vary flag and bounds, and pickups renumbered so the same layers stay coupled. With
    /// media, the incident medium and the substrate change places too - the design seen from the
    /// other side. The merit function and System Parameters are kept. Reversing twice is the
    /// design it started as.
    /// </summary>
    public ApiResult<DesignSummary> ReverseLayers(string id, bool media)
    {
        if (!Find(id, out var s, out var fail)) return ApiResult<DesignSummary>.Failure(fail!);
        var doc = Current(s);
        int n = doc.Layers.Length;
        var layers = doc.Layers.Reverse()
            .Select(l => l.Pickup is { } p ? l with { Pickup = p with { Parent = n - 1 - p.Parent } } : l)
            .ToArray();
        var next = doc with { Layers = layers };
        if (media) next = next with { Incident = doc.Exit, Exit = doc.Incident };
        return ReplaceLayers(s, next);
    }

    /// <summary>Input design string: the whole stack replaced by the layers a string spells, read
    /// as DesignString.cs describes. The media, materials and merit function are kept.</summary>
    public ApiResult<DesignSummary> ApplyDesignString(string id, DesignStringRequest request)
    {
        if (!Find(id, out var s, out var fail)) return ApiResult<DesignSummary>.Failure(fail!);
        var doc = Current(s);
        if (LayerUnits.Parse(request.Unit) is not { } unit || unit == LayerUnits.Nm)
            return ApiResult<DesignSummary>.Failure($"'{request.Unit}' is not QWOT or FWOT");
        foreach (var (letter, material) in request.Materials)
            if (material < 0 || material >= doc.Materials.Length)
                return ApiResult<DesignSummary>.Failure($"there is no material {material + 1} for {letter}");
        try
        {
            var plan = DesignString.Plan(DesignString.Expand(request.Text), request);
            // A layer follows the design's unit when it is the one chosen, so a later change of the
            // default carries it; otherwise it names its own.
            string? layerUnit = doc.ThicknessUnit == unit ? null : unit;
            var layers = plan.Select(p => new LayerDocument(p.Material, p.Count / LayerUnits.PerNm(doc, p.Material, unit))
            {
                Unit = layerUnit,
                Pickup = p.Pickup,
            }).ToArray();
            return ReplaceLayers(s, doc with { Layers = layers });
        }
        catch (FormatException e) { return ApiResult<DesignSummary>.Failure(e.Message); }
    }

    /// <summary>How many <paramref name="unit"/> one nanometre of a material is in this design, at
    /// its reference wavelength - what a dialog needs to convert as a person types.</summary>
    public ApiResult<double> UnitsPerNm(string id, int material, string unit)
    {
        if (!Find(id, out var s, out var fail)) return ApiResult<double>.Failure(fail!);
        if (material < 0 || material >= s.Document.Materials.Length)
            return ApiResult<double>.Failure($"there is no material {material + 1}");
        if (LayerUnits.Parse(unit) is not { } u)
            return ApiResult<double>.Failure($"'{unit}' is not a thickness unit; use nm, QWOT or FWOT");
        try { return ApiResult<double>.Success(LayerUnits.PerNm(s.Document, material, u)); }
        catch (FormatException e) { return ApiResult<double>.Failure(e.Message); }
    }

    private static int[] Children(DesignDocument doc, int layer) =>
        doc.Layers.Select((l, i) => (l, i)).Where(x => x.l.Pickup?.Parent == layer).Select(x => x.i).ToArray();

    private static string PickedUp(int layer, LayerPickup p) =>
        $"layer {layer + 1} is picked up from layer {p.Parent + 1}; change layer {p.Parent + 1}, "
        + "or remove the pickup in Layer properties";

    /// <summary>A layer edit: pickups re-applied, then the units and pickups checked.</summary>
    private ApiResult<DesignSummary> ReplaceLayers(Session s, DesignDocument doc)
    {
        if (LayerUnits.Problem(doc) is { } problem) return ApiResult<DesignSummary>.Failure(problem);
        return Replace(s, LayerUnits.ApplyPickups(doc));
    }

    /// <summary>A change of reference, unit or material: QWOT and FWOT layers keep their numbers.</summary>
    private ApiResult<DesignSummary> ReplaceKeepingOptical(Session s, DesignDocument before, DesignDocument next)
    {
        if (LayerUnits.Problem(next) is { } problem) return ApiResult<DesignSummary>.Failure(problem);
        try { return Replace(s, LayerUnits.KeepOpticalThickness(before, next)); }
        catch (FormatException e) { return ApiResult<DesignSummary>.Failure(e.Message); }
    }

    /// <summary>The design's own copies of its materials and media, as definitions.</summary>
    public ApiResult<MaterialSet> MaterialSet(string id)
    {
        if (!Find(id, out var s, out var fail)) return ApiResult<MaterialSet>.Failure(fail!);
        var doc = s.Document;
        NamedMaterial Named(MaterialDocument m) =>
            new(m.Name, m.Model ?? MaterialModel.FromSamples(doc.Wavelength, m.N, m.K));
        var used = doc.Materials.Select((_, i) => doc.Layers.Count(l => l.Material == i)).ToArray();
        return ApiResult<MaterialSet>.Success(new MaterialSet(
            doc.Materials.Select(Named).ToArray(), Named(doc.Incident), Named(doc.Exit), used));
    }

    /// <summary>
    /// The design's materials and media replaced, all at once - what OK in Edit > Materials
    /// does. Each entry says which old material it was (<see cref="MaterialEdit.Was"/>; -1 for one
    /// copied in), so layers follow their material through a rename and a reordering, and a
    /// material still used by a layer cannot be dropped: the refusal names the layer.
    /// </summary>
    public ApiResult<DesignSummary> ReplaceMaterials(string id, MaterialEdit[] materials,
                                                     NamedMaterial incident, NamedMaterial substrate)
    {
        if (!Find(id, out var s, out var fail)) return ApiResult<DesignSummary>.Failure(fail!);
        var doc = Current(s);
        if (materials.Length == 0) return ApiResult<DesignSummary>.Failure("a design needs at least one material");
        var names = materials.Select(m => m.Material.Name.Trim()).ToArray();
        if (names.Any(n => n.Length == 0)) return ApiResult<DesignSummary>.Failure("every material needs a name");
        var twice = names.GroupBy(n => n, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (twice is not null)
            return ApiResult<DesignSummary>.Failure($"two materials are called '{twice.Key}'; names must differ");

        var remap = new int[doc.Materials.Length];
        Array.Fill(remap, -1);
        for (int i = 0; i < materials.Length; ++i)
            if (materials[i].Was >= 0 && materials[i].Was < remap.Length) remap[materials[i].Was] = i;
        for (int l = 0; l < doc.Layers.Length; ++l)
            if (remap[doc.Layers[l].Material] < 0)
                return ApiResult<DesignSummary>.Failure(
                    $"layer {l + 1} is made of {doc.Materials[doc.Layers[l].Material].Name}, which "
                    + "would be removed; change that layer's material first");

        try
        {
            var next = doc with
            {
                Materials = materials.Select(m => MaterialDocument.FromModel(
                    m.Material.Name.Trim(), m.Material.Model, doc.Wavelength)).ToArray(),
                Layers = doc.Layers.Select(l => l with { Material = remap[l.Material] }).ToArray(),
                Incident = MaterialDocument.FromModel(incident.Name, incident.Model, doc.Wavelength),
                Exit = MaterialDocument.FromModel(substrate.Name, substrate.Model, doc.Wavelength),
            };
            return ReplaceKeepingOptical(s, doc, next);
        }
        catch (FormatException e) { return ApiResult<DesignSummary>.Failure(e.Message); }
    }

    /// <summary>The design's materials, by name, in the order a layer's material index counts.</summary>
    public ApiResult<string[]> MaterialNames(string id)
    {
        if (!Find(id, out var s, out var fail)) return ApiResult<string[]>.Failure(fail!);
        return ApiResult<string[]>.Success(s.Document.Materials.Select(m => m.Name).ToArray());
    }

    // ---- editing the merit function ---------------------------------------------------------

    /// <summary>Every merit row as an editor holds it, in table order.</summary>
    public ApiResult<OperandEdit[]> Operands(string id)
    {
        if (!Find(id, out var s, out var fail)) return ApiResult<OperandEdit[]>.Failure(fail!);
        var doc = Current(s);
        return ApiResult<OperandEdit[]>.Success(doc.Operands.Select(o => ToEdit(doc, o)).ToArray());
    }

    public ApiResult<DesignSummary> ReplaceOperand(string id, int index, OperandEdit edit) =>
        EditOperands(id, (rows, doc) =>
        {
            if (index < 0 || index >= rows.Count) return $"there is no merit row {index + 1}";
            var built = FromEdit(edit, doc.Operands[index]);
            if (built.Error is not null) return built.Error;
            rows[index] = built.Row;
            return null;
        });

    public ApiResult<DesignSummary> InsertOperand(string id, int before, OperandEdit edit) =>
        EditOperands(id, (rows, doc) =>
        {
            if (before < 0 || before > rows.Count)
                return $"cannot insert before row {before + 1}; the table has {rows.Count}";
            var built = FromEdit(edit, null);
            if (built.Error is not null) return built.Error;
            rows.Insert(before, built.Row);
            return null;
        });

    /// <summary>The last row may go too: an empty table is held, not evaluated (EmptyMeritReason).</summary>
    public ApiResult<DesignSummary> RemoveOperand(string id, int index) =>
        EditOperands(id, (rows, _) =>
        {
            if (index < 0 || index >= rows.Count) return $"there is no merit row {index + 1}";
            rows.RemoveAt(index);
            return null;
        });

    // ---- building the merit function in bulk (MeritTable.cs) --------------------------------

    /// <summary>The rows a filter matches, by index - what a dialog shows before it acts.</summary>
    public ApiResult<int[]> MatchOperands(string id, OperandFilter filter)
    {
        if (!Find(id, out var s, out var fail)) return ApiResult<int[]>.Failure(fail!);
        if (MeritTable.Problem(filter) is { } problem) return ApiResult<int[]>.Failure(problem);
        var doc = Current(s);
        return ApiResult<int[]>.Success(doc.Operands
            .Select((o, i) => (o, i)).Where(x => MeritTable.Matches(ToEdit(doc, x.o), filter))
            .Select(x => x.i).ToArray());
    }

    /// <summary>Every row removed. The design is then held, not evaluated, until rows are added.</summary>
    public ApiResult<MeritEditReport> ClearOperands(string id) => Bulk(id, rows =>
    {
        int n = rows.Count;
        rows.Clear();
        return (n, null);
    });

    /// <summary>The rows the filter matches, removed.</summary>
    public ApiResult<MeritEditReport> DeleteOperands(string id, OperandFilter filter) => Bulk(id, rows =>
    {
        if (MeritTable.Problem(filter) is { } problem) return (0, problem);
        int n = rows.RemoveAll(r => MeritTable.Matches(EditOf(r.Op, r.At), filter));
        return (n, null);
    });

    /// <summary>The rows the filter matches, given one weight. Their budgets (Scale) are kept: how
    /// much a row counts is a different question from how precisely it is measured.</summary>
    public ApiResult<MeritEditReport> SetOperandWeights(string id, OperandFilter filter, double weight) => Bulk(id, rows =>
    {
        if (MeritTable.Problem(filter) is { } problem) return (0, problem);
        if (!(weight >= 0.0) || !double.IsFinite(weight)) return (0, "a weight must be zero or positive");
        int n = 0;
        for (int i = 0; i < rows.Count; ++i)
            if (MeritTable.Matches(EditOf(rows[i].Op, rows[i].At), filter))
            {
                rows[i] = (rows[i].Op with { Weight = weight }, rows[i].At);
                ++n;
            }
        return (n, null);
    });

    /// <summary>A sweep of pointwise rows appended to the table, each checked as Insert checks one.</summary>
    public ApiResult<MeritEditReport> AddOperands(string id, OperandSweep sweep) => Bulk(id, rows =>
    {
        var edits = MeritTable.Rows(sweep, out var problem);
        if (problem is not null) return (0, problem);
        foreach (var e in edits)
        {
            var built = FromEdit(e, null);
            if (built.Error is not null) return (0, built.Error);
            rows.Add(built.Row);
        }
        return (edits.Length, null);
    });

    private ApiResult<MeritEditReport> Bulk(
        string id, Func<List<(OperandDocument Op, (double Wl, double Angle)[] At)>, (int Rows, string? Error)> change)
    {
        int count = 0;
        var r = EditOperands(id, (rows, _) =>
        {
            var (n, error) = change(rows);
            count = n;
            return error;
        });
        return r.Ok ? ApiResult<MeritEditReport>.Success(new MeritEditReport(count, r.Value!))
                    : ApiResult<MeritEditReport>.Failure(r.Error!, r.Status);
    }

    private static OperandEdit EditOf(OperandDocument o, (double Wl, double Angle)[] at) =>
        new(o.Code, o.Comparison, o.Target, o.TargetHi, o.Weight,
            at.Select(a => a.Wl).ToArray(), at.Length > 0 ? at[0].Angle : 0.0, o.Threshold, o.Scale, o.Temperature);

    // ---- a merit table the engine cannot take ------------------------------------------------

    /// <summary>Why a design with this merit table is held rather than evaluated. The engine
    /// refuses a table with no rows, or with no row at a wavelength (it needs a point); the
    /// person is building one, so the design stays open and says what to add.</summary>
    public const string EmptyMeritReason = "the merit function is empty - add operands to evaluate the design";
    public const string NoWavelengthReason =
        "no merit row reads a wavelength - add an optical operand (RA, TA, ...) to evaluate the design";

    public static bool IsMeritBlock(string? reason) => reason is EmptyMeritReason or NoWavelengthReason;

    private static string? MeritBlock(DesignDocument doc) =>
        doc.Operands.Length == 0 ? EmptyMeritReason
        : doc.Points.Length == 0 ? NoWavelengthReason
        : null;

    /// <summary>"nm" or "um": how wavelengths are SHOWN. Saved with the design; changes no number.</summary>
    public ApiResult<DesignSummary> SetWavelengthUnit(string id, string unit)
    {
        if (!Find(id, out var s, out var fail)) return ApiResult<DesignSummary>.Failure(fail!);
        if (unit is not ("nm" or "um"))
            return ApiResult<DesignSummary>.Failure($"'{unit}' is not a wavelength unit; use nm or um");
        s.Document = Current(s) with { WavelengthUnit = unit };
        return ApiResult<DesignSummary>.Success(Summarise(s));
    }

    private ApiResult<DesignSummary> EditOperands(
        string id,
        Func<List<(OperandDocument Op, (double Wl, double Angle)[] At)>, DesignDocument, string?> change)
    {
        if (!Find(id, out var s, out var fail)) return ApiResult<DesignSummary>.Failure(fail!);
        var doc = Current(s);
        var rows = doc.Operands.Select(o => (o, Documents.PointsOf(doc, o))).ToList();
        var error = change(rows, doc);
        if (error is not null) return ApiResult<DesignSummary>.Failure(error);
        return Replace(s, Documents.WithOperands(doc, rows));
    }

    private static OperandEdit ToEdit(DesignDocument doc, OperandDocument o)
    {
        var at = Documents.PointsOf(doc, o);
        return new OperandEdit(o.Code, o.Comparison, o.Target, o.TargetHi, o.Weight,
                               at.Select(a => a.Wl).ToArray(), at.Length > 0 ? at[0].Angle : 0.0,
                               o.Threshold, o.Scale, o.Temperature);
    }

    /// <summary>
    /// A budget for a row that has none yet, chosen the way the samples choose theirs: the
    /// room between the target and the side it cannot pass. A reflectance held at most t can
    /// fall t, so its budget is t; one held at least t can rise 1 - t. An equality or a range has
    /// no such side and gets 1% absolute. MINT and MAXT are budgeted by their limit, as the
    /// samples do. Anything else by the target's size, or 1.
    /// </summary>
    internal static double AutomaticScale(int code, string comparison, double target, double threshold)
    {
        const double floor = 1e-4;
        if (code is (int)TfTargetCode.TF_TARGET_MINT or (int)TfTargetCode.TF_TARGET_MAXT)
            return threshold > 0.0 ? threshold : 1.0;

        bool fraction = code is (int)TfTargetCode.TF_TARGET_RS or (int)TfTargetCode.TF_TARGET_RP
            or (int)TfTargetCode.TF_TARGET_RA or (int)TfTargetCode.TF_TARGET_TS
            or (int)TfTargetCode.TF_TARGET_TP or (int)TfTargetCode.TF_TARGET_TA
            or (int)TfTargetCode.TF_TARGET_AS or (int)TfTargetCode.TF_TARGET_AP
            or (int)TfTargetCode.TF_TARGET_AA;
        if (fraction)
            return comparison.ToUpperInvariant() switch
            {
                "LE" => Math.Max(target, floor),
                "GE" => Math.Max(1.0 - target, floor),
                _ => 0.01,
            };

        // GROUP DELAY, GDD AND TOD are budgeted absolutely, in their own units: 1 fs of GD, 10 fs²
        // of GDD and 100 fs³ of TOD, the size of error a short-pulse specification is written to. A
        // target of zero has no size to be budgeted by, and it is the commonest target there is.
        if (Codes.IsDispersion(code))
            return Codes.IsThirdOrder(code) ? 100.0 : code >= (int)TfTargetCode.TF_TARGET_R_GDD_S ? 10.0 : 1.0;

        return Math.Abs(target) > 0.0 ? Math.Abs(target) : 1.0;
    }

    private static ((OperandDocument Op, (double Wl, double Angle)[] At) Row, string? Error) FromEdit(
        OperandEdit e, OperandDocument? existing)
    {
        var none = ((existing ?? new OperandDocument("RA", "NONE", 0, 0), Array.Empty<(double, double)>()),
                    (string?)null);
        int code;
        try { code = Codes.Target(e.Code); }
        catch (FormatException x) { return (none.Item1, x.Message); }
        try { Codes.Comparison(e.Comparison); }
        catch (FormatException x) { return (none.Item1, x.Message); }

        bool stack = Codes.IsStack(code);
        if (!stack && e.Wavelengths.Length == 0) return (none.Item1, $"{e.Code} needs a wavelength");
        if (e.Wavelengths.Any(w => !(w > 0.0) || !double.IsFinite(w)))
            return (none.Item1, "a wavelength must be a positive number of nanometres");
        if (!(e.AngleDegrees >= 0.0 && e.AngleDegrees < 90.0))
            return (none.Item1, "the angle of incidence must be at least 0 and below 90 degrees");
        if (!(e.Weight >= 0.0) || !double.IsFinite(e.Weight))
            return (none.Item1, "a weight must be zero or positive");
        bool range = string.Equals(e.Comparison, "RANGE", StringComparison.OrdinalIgnoreCase);
        if (range && e.TargetHi < e.Target)
            return (none.Item1, "a range needs its upper target at or above its lower one");

        bool limited = code is (int)TfTargetCode.TF_TARGET_MINT or (int)TfTargetCode.TF_TARGET_MAXT;
        double threshold = limited ? e.Threshold : 0.0;
        if (limited && !(threshold > 0.0)) return (none.Item1, "a thickness limit must be positive");

        // A BAND ROW averages over its band, so it needs one, and its spectrum must reach it.
        bool thermal = Codes.IsThermal(e.Code);
        double temperature = thermal ? e.Temperature : 0.0;
        if (Codes.IsBand(e.Code))
        {
            if (thermal && !(temperature > 0.0 && double.IsFinite(temperature)))
                return (none.Item1, $"{e.Code.ToUpperInvariant()} needs a temperature above 0 K");
            foreach (var weighting in Spectra.WeightingsOf(e.Code))
                if (Spectra.Weights(weighting, e.Wavelengths, temperature, out var why) is null)
                    return (none.Item1, why);
        }

        // THE HIDDEN SCALE travels with the row. Only a row that asks for one (Scale = 0: new, or
        // its code or limit changed) gets an automatic budget; see OperandEdit.
        double scale = e.Scale > 0.0 && double.IsFinite(e.Scale) ? e.Scale
                     : AutomaticScale(code, e.Comparison, e.Target, threshold);

        var op = (existing ?? new OperandDocument(e.Code, e.Comparison, e.Target, e.Weight)) with
        {
            Code = e.Code, Comparison = e.Comparison, Target = e.Target,
            TargetHi = range ? e.TargetHi : 0.0, Weight = e.Weight, Scale = scale,
            Threshold = threshold, Temperature = temperature,
            // A span keeps its reduction; a row that reads one point or none sums, which for one
            // point is the point itself.
            Reduction = e.Wavelengths.Length > 1 ? existing?.Reduction ?? "SUM" : "SUM",
        };
        var at = stack ? Array.Empty<(double, double)>()
                       : e.Wavelengths.Select(w => (w, e.AngleDegrees)).ToArray();
        return ((op, at), null);
    }

    // ---- optimising ------------------------------------------------------------------------

    /// <summary>
    /// Runs the named stages in order: "refine", "global", "needle", "evolve" (gradual evolution, as
    /// the run of that name), "remove" (layers under the minimum, each only while the merit stays within
    /// the allowed degradation) and "thin" (every layer under the minimum, whatever it costs). The
    /// vocabulary the specification and the windows use, because a front end should pass a user's
    /// words through rather than invent its own.
    /// </summary>
    public ApiResult<OptimiseReport> Optimise(string id, OptimiseRequest request)
    {
        if (!Find(id, out var s, out var fail)) return ApiResult<OptimiseReport>.Failure(fail!);
        if (s.Blocked is { } why) return ApiResult<OptimiseReport>.Failure(why, "BLOCKED");

        // new[] rather than [',', ' ']: the .NET 8 SDK's compiler finds a collection expression
        // ambiguous between Split(char[]) and Split(string), where the .NET 9 SDK's does not.
        var stages = request.Stages.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries
                                                      | StringSplitOptions.TrimEntries);
        if (stages.Length == 0)
            return ApiResult<OptimiseReport>.Failure("no stages named; try \"refine\" or \"refine,needle,remove\"");

        var doc = Current(s);
        var design = s.Design!;
        if (CannotOptimise(doc, design, stages) is { } refused)
            return ApiResult<OptimiseReport>.Failure(refused);
        if (stages.Any(st => st.Equals("refine", StringComparison.OrdinalIgnoreCase))
            && NormProblem(request.Norm ?? doc.Norm) is { } bad)
            return ApiResult<OptimiseReport>.Failure(bad);
        var placed = OutsideOwnBounds(doc);
        design.Evaluate();
        double initial = design.Merit;
        var run = new List<string>();
        string outcome = "none";

        // EACH LAYER'S OWN BOUNDS (LayerUnits.Bounds), the design-wide ones where it has none.
        var (lower, upper) = LayerUnits.Bounds(doc);
        if (lower.Length != design.VariableCount)
            return ApiResult<OptimiseReport>.Failure(
                $"internal: {lower.Length} bounds for {design.VariableCount} variables");
        var bounds = new DlsOptions
        {
            Lower = lower,
            Upper = upper,
            MaxIterations = request.MaxIterations,
        };

        var candidates = NeedleCandidates(doc, request.NeedleMaterials, out var unknown);
        if (unknown is not null) return ApiResult<OptimiseReport>.Failure(unknown);

        var sw = Stopwatch.StartNew();
        try
        {
            foreach (var stage in stages)
            {
                switch (stage.ToLowerInvariant())
                {
                    case "refine":
                    {
                        outcome = Refine(design, request.Norm ?? doc.Norm, bounds);
                        break;
                    }

                    case "global":
                    {
                        var r = GlobalSearch.Run(design, new GlobalSearchOptions
                        {
                            Starts = request.GlobalStarts,
                            Lower = bounds.Lower,
                            Upper = SearchBox(bounds, design),
                            Refinement = bounds,
                        });
                        outcome = $"{r.Improvements} improvements over {r.Starts} starts";
                        break;
                    }

                    case "needle":
                    {
                        var r = NeedleOptimiser.Run(design, new NeedleOptions
                        {
                            MaxInsertions = request.NeedleInsertions,
                            Candidates = candidates,
                            Refinement = bounds,
                        });
                        design = r.Design;
                        outcome = $"{r.Insertions} inserted, {r.Rejected} rejected";
                        bounds = Bounds.For(bounds, design.VariableCount);
                        break;
                    }

                    // ONE MEANING OF "evolve" (X1.80): gradual evolution, as `thinfilm evolve`, optimise_start's
                    // method and the GUI's window run it. This stage was once the thin-layer clean-up; that
                    // is "remove" now, and an agent asked to evolve a design no longer gets it unawares.
                    case "evolve":
                    {
                        var r = GradualEvolution.Evolve(design, new GrowthOptions
                        {
                            GrowthThickness = doc.Materials.Select((_, m) => 1.0 / LayerUnits.PerNm(doc, m, LayerUnits.Qwot)).ToArray(),
                            InsertionsPerStep = request.NeedleInsertions,
                            Candidates = candidates,
                            MinimumThickness = request.MinimumThickness,
                            AcceptMerit = request.AcceptMerit,
                            Refinement = bounds,
                        });
                        design = r.Design;
                        outcome = $"best at step {r.BestStep} of {r.Steps}: {r.Design.LayerCount} layers, "
                                  + $"{r.Inserted} needles kept, {r.Removed} layers removed; {r.Stopped}";
                        bounds = Bounds.For(bounds, design.VariableCount);
                        break;
                    }

                    case "remove":
                    {
                        var r = GradualEvolution.RemoveThinLayers(design, new EvolutionOptions
                        {
                            MinimumThickness = request.MinimumThickness,
                            AcceptMerit = request.AcceptMerit,
                            Refinement = bounds,
                        });
                        design = r.Design;
                        outcome = $"{r.Removed} removed, {r.Merged} merged";
                        bounds = Bounds.For(bounds, design.VariableCount);
                        break;
                    }

                    case "thin":
                    {
                        var r = GradualEvolution.RemoveThinLayers(design, new EvolutionOptions
                        {
                            MinimumThickness = request.MinimumThickness,
                            Force = true,
                            MaxRemovals = int.MaxValue,
                            Refinement = bounds,
                        });
                        design = r.Design;
                        outcome = $"{r.Removed} removed, {r.Merged} merged";
                        bounds = Bounds.For(bounds, design.VariableCount);
                        break;
                    }

                    default:
                        return ApiResult<OptimiseReport>.Failure(
                            $"'{stage}' is not a stage; try refine, global, needle, evolve, remove or thin");
                }
                run.Add(stage);
            }
        }
        catch (ThinFilmException e)
        { return ApiResult<OptimiseReport>.Failure(e.Message, e.Status.ToString()); }
        sw.Stop();

        // Needle and evolve change the SHAPE, so the session's document has to be rebuilt from
        // the design that came back rather than patched with new thicknesses.
        s.Design = design;
        s.Document = Documents.Rebuild(doc, design);
        design.Evaluate();

        return ApiResult<OptimiseReport>.Success(new OptimiseReport(
            initial, design.Merit, outcome, design.LayerCount, run.ToArray(),
            sw.Elapsed.TotalMilliseconds, placed));
    }

    /// <summary>Why this design cannot be optimised by these stages, or null. The same checks for
    /// Optimise and for a run, whichever front end asked.</summary>
    private static string? CannotOptimise(DesignDocument doc, Design design, IReadOnlyList<string> stages)
    {
        // NEEDLE AND EVOLVE CHANGE THE LAYER COUNT, and a pickup names its layers by number, so
        // after either one "layer 4 = layer 1" would name different layers. Refused rather than
        // guessed; pickups are for structured stacks, needle and evolve for synthesis.
        if (doc.Layers.Any(l => l.Pickup is not null)
            && stages.FirstOrDefault(st => st.ToLowerInvariant() is "needle" or "evolve" or "remove" or "thin") is { } shape)
            return $"the {shape} stage adds or removes layers, which would scramble the layer numbers "
                   + "the pickups name; use refine or global, or remove the pickups first";
        // The same for bounds: they belong to layers by number, and the layers needle inserts or
        // evolve merges would come out with none, or with another layer's.
        if (LayerUnits.AnyBounds(doc)
            && stages.FirstOrDefault(st => st.ToLowerInvariant() is "needle" or "evolve" or "remove" or "thin") is { } reshape)
            return $"the {reshape} stage adds or removes layers, and the per-layer min and max would not follow "
                   + "them; use refine or global, or make the layers unconstrained first";
        // A LAYER ALREADY ABOVE THE CEILING would be pulled down to it by the first step, a
        // constraint the person never sees; refused instead, naming it and the way out.
        if (LayerUnits.AboveMaximum(doc) is { Length: > 0 } above)
            return $"{LayerUnits.Named(above)} {(above.Length == 1 ? "is" : "are")} thicker than the design's maximum for "
                   + $"unconstrained layers, {doc.UpperBound:0.###} nm; raise it in Edit > System Parameters, or give "
                   + (above.Length == 1 ? "the layer a maximum of its own" : "those layers maxima of their own");
        if (design.VariableCount == 0)
            return doc.Layers.Length == 0
                ? "the design has no layers to optimise; add some with Insert layer or Input design string"
                : "no layer thickness is varied; tick Vary on the layers the optimiser may move";
        return null;
    }

    /// <summary>Free layers outside their own min or max, which the solver places on it before the
    /// first step; named in the report.</summary>
    private static int[] OutsideOwnBounds(DesignDocument doc) =>
        doc.Layers.Select((l, i) => (l, i))
            .Where(x => x.l.Variable && x.l.Pickup is null
                        && (x.l.Thickness < x.l.Min || x.l.Thickness > x.l.Max))
            .Select(x => x.i).ToArray();

    private static string? NormProblem(string norm) =>
        norm.ToUpperInvariant() is "L2" || Enum.TryParse<Norm>(norm, ignoreCase: true, out _)
            ? null : $"'{norm}' is not a norm; try L2, L1 or minimax";

    /// <summary>The refine stage: damped least squares under L2, IRLS around it under L1 or minimax.
    /// Returns the outcome in words.</summary>
    private static string Refine(Design design, string norm, DlsOptions options, Action<NormPass>? passDone = null)
    {
        if (string.Equals(norm, "L2", StringComparison.OrdinalIgnoreCase))
            return new DampedLeastSquares(design.VariableCount, design.Report.n_residuals, options)
                .Minimise(design, options).Outcome.ToString();
        var n = Enum.Parse<Norm>(norm, ignoreCase: true);
        var r = Norms.Minimise(design, n, new NormOptions { Refinement = options, PassDone = passDone });
        return options.Cancellation.IsCancellationRequested ? "Cancelled" : $"{n}, {r.Passes} passes";
    }

    // ---- plumbing --------------------------------------------------------------------------

    private ApiResult<DesignSummary> Open(DesignDocument doc, IReadOnlyList<string> warnings)
    {
        // Every design, however it arrived, holds its materials as models from here on.
        try { doc = doc.Normalise(); }
        catch (FormatException e) { return ApiResult<DesignSummary>.Failure(e.Message); }

        var id = $"d{++_next}";
        var session = new Session { Id = id, Document = doc };
        session.Warnings.AddRange(warnings);

        // A design saved while its merit function was being built opens as it was left.
        if (MeritBlock(doc) is { } held)
        {
            session.Blocked = held;
            _sessions[id] = session;
            return ApiResult<DesignSummary>.Success(Summarise(session));
        }

        try
        {
            var design = new Design(doc.ToSpec());
            design.Plan();
            design.Evaluate();
            session.Design = design;
        }
        catch (ThinFilmException e)
        { return ApiResult<DesignSummary>.Failure(e.Message, e.Status.ToString()); }
        // A MATERIAL IT NAMES IS NOT HERE: the design opens, blocked, and says why. Not a failure to
        // open - the person may want to see it, or choose another substrate - and not evaluated.
        catch (MaterialUnavailableException e) { session.Blocked = e.Message; }

        _sessions[id] = session;
        return ApiResult<DesignSummary>.Success(Summarise(session));
    }

    private ApiResult<DesignSummary> Replace(Session s, DesignDocument doc)
    {
        if (MeritBlock(doc) is { } held)
        {
            s.Document = doc;
            s.Design = null;
            s.Blocked = held;
            return ApiResult<DesignSummary>.Success(Summarise(s));
        }
        try
        {
            var design = new Design(doc.ToSpec());
            design.Plan();
            design.Evaluate();
            s.Document = doc;
            s.Design = design;
            s.Blocked = null;
            return ApiResult<DesignSummary>.Success(Summarise(s));
        }
        catch (ThinFilmException e)
        { return ApiResult<DesignSummary>.Failure(e.Message, e.Status.ToString()); }
        // Still (or newly) unreachable: the edit is kept, and the design stays blocked.
        catch (MaterialUnavailableException e)
        {
            s.Document = doc;
            s.Design = null;
            s.Blocked = e.Message;
            return ApiResult<DesignSummary>.Success(Summarise(s));
        }
        catch (Exception e) { return ApiResult<DesignSummary>.Failure(e.Message); }
    }

    /// <summary>The document with the design's CURRENT thicknesses, which is what a save means.</summary>
    private static DesignDocument Current(Session s) =>
        s.Design is null ? s.Document : s.Document.WithThickness(s.Design.LayerThickness);

    /// <summary>NotNullWhen so the compiler knows what the true branch already guarantees.</summary>
    private bool Find(string id, [NotNullWhen(true)] out Session? session,
                      [NotNullWhen(false)] out string? failure)
    {
        if (_sessions.TryGetValue(id, out session)) { failure = null; return true; }
        failure = _sessions.Count == 0
            ? $"no session '{id}'; nothing is open"
            : $"no session '{id}'; open are {string.Join(", ", _sessions.Keys)}";
        return false;
    }

    private static LayerReport[] Layers(Session s)
    {
        // From the document, with the current thicknesses - so a blocked design, which has no
        // evaluated Design, still shows its stack.
        var doc = Current(s);
        var values = LayerUnits.Values(doc);
        return doc.Layers.Select((l, i) => new LayerReport(i, doc.Materials[l.Material].Name,
                                                           l.Thickness, l.Variable && l.Pickup is null,
                                                           LayerUnits.UnitOf(doc, i), l.Unit, values[i],
                                                           l.Pickup, Bound(doc, i, l.Min), Bound(doc, i, l.Max))).ToArray();
    }

    /// <summary>A bound in the layer's unit for a report; null when it has none or cannot be converted.</summary>
    private static double? Bound(DesignDocument doc, int layer, double? nm)
    {
        try { return LayerUnits.InUnit(doc, layer, nm); }
        catch (FormatException) { return null; }
    }

    /// <summary>The session's warnings, and the incident medium's k while it has one.</summary>
    private static string[] Warned(Session s) =>
        s.Document.IncidentKWarning is { } k ? [.. s.Warnings, k] : s.Warnings.ToArray();

    private static DesignSummary Summarise(Session s)
    {
        if (s.Design is not { } d)
        {
            // BLOCKED: what the document says, and no merit - there is none to report.
            var doc = s.Document;
            return new DesignSummary(
                s.Id, doc.Name, doc.Layers.Length, doc.Materials.Length, doc.Wavelength.Length,
                doc.Points.Length, doc.Operands.Length, doc.Layers.Count(l => l.Variable), 0,
                doc.Layers.Sum(l => l.Thickness), double.NaN, [.. Warned(s), s.Blocked!],
                Medium(doc.Incident), Medium(doc.Exit), doc.WavelengthUnit, s.Blocked,
                doc.ThicknessUnit, doc.ReferenceWavelength, doc.UpperBound,
                doc.BackSurface, doc.SubstrateThicknessMm, doc.AngleInAir, doc.Source, doc.Notes, doc.Plot);
        }
        double total = 0.0;
        foreach (var t in d.LayerThickness) total += t;
        d.Evaluate();
        return new DesignSummary(
            s.Id, s.Document.Name, d.LayerCount, d.MaterialCount, d.WavelengthCount,
            d.PointCount, s.Document.Operands.Length, d.VariableCount, d.Report.n_residuals,
            total, d.Merit, Warned(s),
            Medium(s.Document.Incident), Medium(s.Document.Exit), s.Document.WavelengthUnit, null,
            s.Document.ThicknessUnit, s.Document.ReferenceWavelength, s.Document.UpperBound,
            s.Document.BackSurface, s.Document.SubstrateThicknessMm, s.Document.AngleInAir,
            s.Document.Source, s.Document.Notes, s.Document.Plot);
    }

    private static OperandPoints? Where(DesignDocument doc, OperandDocument op)
    {
        if (op.Points is not { Length: > 0 } points) return null;
        double wlo = double.MaxValue, whi = double.MinValue;
        double alo = double.MaxValue, ahi = double.MinValue;
        foreach (int p in points)
        {
            var point = doc.Points[p];
            double wl = doc.Wavelength[point.Wavelength];
            wlo = Math.Min(wlo, wl); whi = Math.Max(whi, wl);
            alo = Math.Min(alo, point.AngleDegrees); ahi = Math.Max(ahi, point.AngleDegrees);
        }
        return new OperandPoints(points.Length, wlo, whi, alo, ahi);
    }

    private static MediumReport Medium(MaterialDocument m) =>
        new(m.Name,
            m.N.Length > 0 ? m.N.Min() : double.NaN,
            m.N.Length > 0 ? m.N.Max() : double.NaN,
            m.K.Length > 0 ? m.K.Max() : 0.0);

    private static string[] FlagNames(int flag)
    {
        var names = new List<string>();
        if ((flag & TfAbi.TF_FLAG_EXTRAPOLATED) != 0) names.Add("EXTRAPOLATED");
        if ((flag & TfAbi.TF_FLAG_AMBIGUOUS) != 0) names.Add("AMBIGUOUS");
        if ((flag & TfAbi.TF_FLAG_PINNED) != 0) names.Add("PINNED");
        if ((flag & TfAbi.TF_FLAG_SYNTHETIC_INDEX) != 0) names.Add("SYNTHETIC_INDEX");
        return names.ToArray();
    }
}
