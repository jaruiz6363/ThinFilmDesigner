// DesignWorkspace.cs — everything the window does, with no window in it.
//
// THE VIEW MODEL HOLDS THE LOGIC AND THE VIEW HOLDS NOTHING. That is worth doing anywhere and it
// is worth doing here specifically, because a GUI cannot be tested without a display and this can:
// open a design, refine it, edit a thickness, ask for a spectrum, and check the numbers. The
// Avalonia layer binds to these properties and calls these methods, and if it has a bug it is a
// binding bug, which is the kind you can see.
//
// NOTHING HERE COMPUTES ANYTHING EITHER. Every number comes from ThinFilm.Api, the same one the
// CLI and the MCP server call, so a spectrum in the window is the spectrum in the CSV is the
// spectrum the model was given. That was the point of X1.7 and this is the third front end to
// prove it.
//
// STATUS IS A STRING AND FAILURE IS NOT AN EXCEPTION. A window that throws is a window that
// closes; a window that puts "TF_NO_STRESS_DATA: material_stress is null" in a status bar is one
// somebody can use. Every operation returns a bool and leaves a message behind.

using System.ComponentModel;
using System.Runtime.CompilerServices;
using ThinFilm.Api;

namespace ThinFilm.Gui.Core;

public sealed class LayerRow
{
    public required int Index { get; init; }
    public required string Material { get; init; }
    /// <summary>Physical thickness, nm - what the engine is given.</summary>
    public required double Thickness { get; set; }
    public required bool Variable { get; init; }

    /// <summary>The unit the row is shown in ("nm", "QWOT", "FWOT"); UnitSetting is null when it
    /// follows the design's unit, which <see cref="DesignUnit"/> names.</summary>
    public string Unit { get; init; } = "nm";
    public string? UnitSetting { get; init; }
    public string DesignUnit { get; init; } = "nm";

    /// <summary>The thickness in <see cref="Unit"/> (nm for a physical layer, whatever is shown).</summary>
    public double Value { get; init; } = double.NaN;

    /// <summary>The design's length unit, "nm" or "um": a physical layer is SHOWN and typed in it
    /// (Lengths.cs), though it is held, and named in the file, in nm.</summary>
    public string LengthUnit { get; init; } = "nm";

    /// <summary>The row's unit as shown: "µm" for a physical layer in a µm design.</summary>
    public string UnitLabel => Lengths.Label(Unit, LengthUnit);

    /// <summary>The bounds the optimiser keeps the layer within, in <see cref="Unit"/> like
    /// <see cref="Value"/>; null where it has none.</summary>
    public double? Min { get; init; }
    public double? Max { get; init; }

    /// <summary>The bounds in the Bounds column: "0.5 – 2", "≥ 0.5", "≤ 2", or nothing.</summary>
    public string BoundsText => Bounds(Min, Max, Unit, LengthUnit);

    /// <summary>A pair of bounds in a unit, as the table and the status line say them.</summary>
    public static string Bounds(double? min, double? max, string unit, string lengthUnit)
    {
        string F(double v) => unit == "nm"
            ? Lengths.Shown(v, lengthUnit).ToString(lengthUnit == "um" ? "0.#####" : "0.###", System.Globalization.CultureInfo.InvariantCulture)
            : v.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
        return (min, max) switch
        {
            ({ } lo, { } hi) => $"{F(lo)} – {F(hi)}",
            ({ } lo, null) => $"≥ {F(lo)}",
            (null, { } hi) => $"≤ {F(hi)}",
            _ => "",
        };
    }

    public LayerPickup? Pickup { get; init; }
    public bool IsPickedUp => Pickup is not null;
    public bool CanEditThickness => Pickup is null;
    public bool CanVary => Pickup is null;

    /// <summary>The thickness as typed and shown, in the row's unit: a thousandth of a nm in nm or
    /// µm, four decimals in QWOT and FWOT, where 1.0000 is a quarter- or full wave. A typed value
    /// goes to the design.</summary>
    public string ValueText
    {
        get => double.IsNaN(Value) ? "—"
             : Unit == "nm" ? Lengths.Cell(Value, LengthUnit)
             : Value.ToString("F4", System.Globalization.CultureInfo.InvariantCulture);
        set
        {
            if (value == ValueText) return;
            if (!double.TryParse(value, System.Globalization.NumberStyles.Float,
                                 System.Globalization.CultureInfo.InvariantCulture, out double v))
            { Owner?.Refused($"'{value}' is not a thickness."); return; }
            if (Live) Owner!.SetLayerValue(Index, Unit == "nm" ? Lengths.ToNm(v, LengthUnit) : v);
        }
    }

    /// <summary>The physical thickness beside a QWOT or FWOT value, in the design's length unit.</summary>
    public string PhysicalText => Lengths.Cell(Thickness, LengthUnit);

    /// <summary>The unit drop-down: following the design, or a unit of the layer's own. The
    /// physical choice reads "µm" in a µm design.</summary>
    ///
    /// ONE LIST FOR EVERY ROW, AS MaterialOptions IS. A list made per row is a new ItemsSource each
    /// time the table is rebuilt, and a recycled drop-down then writes its old row's choice into
    /// the new row: a layer set to QWOT came back as Default. Seen in a rendered frame.
    public IReadOnlyList<string> UnitOptions { get; init; } = Options("nm", "nm");

    /// <summary>The unit drop-down's choices for a design unit and a length unit.</summary>
    /// The same instance every time for the same units, so a rebuilt table keeps its ItemsSource.
    public static IReadOnlyList<string> Options(string designUnit, string lengthUnit) =>
        OptionCache.GetOrAdd((designUnit, lengthUnit), k =>
            [DefaultLabel(Lengths.Label(k.Item1, k.Item2)), .. LayerUnits.All.Select(u => Lengths.Label(u, k.Item2))]);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, string), string[]> OptionCache = new();

    public static string DefaultLabel(string designUnit) => $"Default ({designUnit})";

    public string UnitChoice
    {
        get => UnitSetting is { } u ? Lengths.Label(u, LengthUnit) : DefaultLabel(Lengths.Label(DesignUnit, LengthUnit));
        set
        {
            if (value is null || value == UnitChoice || !Live) return;
            Owner!.SetLayerUnit(Index, value.StartsWith("Default", StringComparison.Ordinal) ? null : Lengths.Code(value));
        }
    }

    /// <summary>"= 3 × L1", "= 0.5 × L1 + 2", or nothing. A physical offset is in the length unit.</summary>
    public string PickupText => Pickup is not { } p ? ""
        : string.Format(System.Globalization.CultureInfo.InvariantCulture, "= {0:G6} × L{1}{2}", p.Scale, p.Parent + 1,
                        p.Offset == 0.0 ? "" : string.Format(System.Globalization.CultureInfo.InvariantCulture,
                                                             p.Offset > 0 ? " + {0:G6}" : " − {0:G6}",
                                                             Math.Abs(Unit == "nm" ? Lengths.Shown(p.Offset, LengthUnit) : p.Offset)));

    /// <summary>What a person calls this layer. Index is what the API calls it; the two are kept
    /// apart so that no call site has to remember which one it is holding.</summary>
    public int Number => Index + 1;

    internal DesignWorkspace? Owner { get; init; }

    /// <summary>
    /// Whether this row is still in the table. A rebuilt table hands its drop-downs new rows, and a
    /// drop-down given a new list while it still holds an old row writes that list's first entry
    /// into the old row - which, passed on, reset a layer just set to QWOT back to Default (seen in
    /// a rendered frame). Only a row the table is showing may change the design.
    /// </summary>
    private bool Live => Owner is { } o && o.Layers.Contains(this);

    /// <summary>The materials this design has, for the row's drop-down.</summary>
    public IReadOnlyList<string> MaterialOptions { get; init; } = [];

    /// <summary>The material, as the drop-down sets it: a change goes straight to the design.</summary>
    public string MaterialChoice
    {
        get => Material;
        set
        {
            int m = MaterialOptions is IList<string> list ? list.IndexOf(value) : -1;
            if (value is null || value == Material || m < 0 || !Live) return;
            Owner!.SetLayerMaterial(Index, m);
        }
    }

    /// <summary>Whether the optimiser may move this layer's thickness, as the checkbox sets it.</summary>
    public bool Vary
    {
        get => Variable;
        set { if (value != Variable && Live) Owner!.SetLayerVariable(Index, value); }
    }
}

public sealed class OperandRow
{
    public required int Index { get; init; }
    public required string Code { get; init; }
    public required string Comparison { get; init; }
    public required double Target { get; init; }
    public required double Value { get; init; }
    public required double Residual { get; init; }
    public required string Flags { get; init; }

    /// <summary>Where the operand is evaluated, as a person reads it: "550 nm", "420–680 nm",
    /// "550 nm, 45°", or "stack" for one that reads no point.</summary>
    public string At { get; init; } = "";

    public double Weight { get; init; }
    /// <summary>The MINT/MAXT limit, nm; shown in <see cref="LengthUnit"/>.</summary>
    public double Threshold { get; init; }
    public string LengthUnit { get; init; } = "nm";
    public double TargetHi { get; init; }
    /// <summary>The blackbody temperature of EMIT and AERATIO, K.</summary>
    public double Temperature { get; init; }

    public int Number => Index + 1;

    /// <summary>The row in words, for the help bar: what the code measures, where, and what it is
    /// held to. "MINT stack LE 0" is three codes; "no layer thinner than 8 nm" is a sentence.</summary>
    public string Help => DesignWorkspace.DescribeOperand(this);

    /// <summary>
    /// Met when the residual is within 1e-4 of the operand's scale — a ten-thousandth of its error
    /// budget. The residual is already divided by the scale, so this is a fraction of what the
    /// row says one unit of error is worth, not an absolute number.
    ///
    /// NOT EXACTLY ZERO, which is what it was. An L2 refinement approaches a one-sided limit
    /// asymptotically, so AR-visible stopped with two points at R = 0.5000029% against 0.5% - a
    /// residual of 5.8e-6 - and the table showed a cross beside a value printed as 0.005, the
    /// same as its target. A mark that disagrees with the numbers next to it is worse than none.
    /// </summary>
    public bool Met => Residual <= MetTolerance;

    public const double MetTolerance = 1e-4;

    /// <summary>The table's mark for Met. An operand with no comparison is not scored, so it is
    /// neither met nor missed.</summary>
    public string MetMark => Comparison == "NONE" ? "" : Met ? "✓" : "✗";
}

public sealed class DesignWorkspace : INotifyPropertyChanged
{
    private readonly ThinFilmApi _api = new();
    private string? _id;

    public event PropertyChangedEventHandler? PropertyChanged;

    public int AbiVersion => _api.AbiVersion;

    public bool IsOpen => _id is not null;

    private string _status = "No design open. Open one, or a sample (File > Open sample).";
    public string Status { get => _status; private set => Set(ref _status, value); }

    private string _title = "ThinFilm";
    public string Title { get => _title; private set => Set(ref _title, value); }

    private double _merit;
    public double Merit { get => _merit; private set => Set(ref _merit, value); }

    private string _summary = "";
    public string Summary { get => _summary; private set => Set(ref _summary, value); }

    // THE TWO ENDS OF THE STACK, named in the window rather than left to be inferred. Layer 1 is
    // next to the incident medium and the last layer is next to the substrate; a layer table
    // that does not say which end is which is a table read upside down half the time.
    private string _incident = "";
    public string IncidentText { get => _incident; private set => Set(ref _incident, value); }

    private string _substrate = "";
    public string SubstrateText { get => _substrate; private set => Set(ref _substrate, value); }

    /// <summary>What the plotted numbers mean: the angle or wavelength held fixed, where angles are
    /// measured, and whether the substrate's back surface is in R and T (System Parameters).</summary>
    public string PlotNote
    {
        get
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string where = AngleInAir ? "in air" : "in the incident medium";
            string at = _vsAngle
                ? $"at {WavelengthText(_wavelength)}, angles of incidence {where}"
                : _angle == 0.0 ? "at normal incidence"
                : $"at {_angle.ToString("0.##", inv)}° incidence {where}";
            string back = BackSurface
                ? $"R and T include the uncoated back surface of a {SubstrateThicknessMm.ToString("0.###", inv)} mm substrate."
                : "R: coated surface only. T: into the substrate — its back surface is not included.";
            string gap = _vsAngle && AngleInAir ? " Angles that cannot be reached in the incident medium are left as a gap." : "";
            return $"R and T as fractions (1 = 100%) {at}. {back}{gap}"
                   + (_outside.Length > 0 ? " Caution: " + string.Join("; ", _outside) + ", so the curve there rests on an extrapolated index." : "");
        }
    }

    /// <summary>The materials the last plot reached outside their valid range (ValidRange.cs).</summary>
    private string[] _outside = [];

    /// <summary>"1 operand", "3 operands".</summary>
    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

    private string WavelengthText(double nm) =>
        WavelengthUnit == "um"
            ? (nm / 1000.0).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) + " µm"
            : nm.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " nm";

    public List<LayerRow> Layers { get; private set; } = new();
    public List<OperandRow> Operands { get; private set; } = new();

    /// <summary>The merit table as the editor shows it: one editable row per operand.</summary>
    public List<MeritRow> MeritRows { get; private set; } = new();

    /// <summary>"nm" or "um", from the design: how wavelengths are shown, never how they are held.</summary>
    public string WavelengthUnit { get; private set; } = "nm";
    public List<PlotSeries> Spectrum { get; private set; } = new();

    // ---- spectrum controls -------------------------------------------------------------------

    private double _from = 400, _to = 800;
    private int _steps = 241;
    private double _angle;
    private string _quantities = "RA,TA";

    public double SpectrumFrom { get => _from; set { Set(ref _from, value); } }
    public double SpectrumTo { get => _to; set { Set(ref _to, value); } }
    public int SpectrumSteps { get => _steps; set { Set(ref _steps, value); } }

    /// <summary>The codes plotted, comma separated, as the API takes them ("RA,TA").</summary>
    public string SpectrumQuantities
    {
        get => _quantities;
        set { Set(ref _quantities, value); Raise(nameof(SpectrumQuantityText)); }
    }

    /// <summary>The codes plotted, one each.</summary>
    public string[] SpectrumQuantityCodes =>
        _quantities.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>What is plotted, in words: "R (avg s,p), T (avg s,p)".</summary>
    public string SpectrumQuantityText =>
        SpectrumQuantityCodes.Length == 0 ? "nothing chosen"
            : string.Join(", ", SpectrumQuantityCodes.Select(QuantityLabel));

    /// <summary>The angle of incidence the spectrum is computed at, in degrees from the normal.
    /// The merit rows carry their own angles; this is only what is plotted.</summary>
    public double SpectrumAngle { get => _angle; set { Set(ref _angle, value); Raise(nameof(PlotNote)); } }

    // The sweep in angle keeps its own range, so switching the x axis back and forth loses nothing.
    private bool _vsAngle;
    private double _angleFrom = 0, _angleTo = 89, _wavelength = 550;
    private int _angleSteps = 90;

    /// <summary>The x axis: angle of incidence (degrees) at SpectrumWavelength, or wavelength.</summary>
    public bool SpectrumVsAngle { get => _vsAngle; set { Set(ref _vsAngle, value); Raise(nameof(PlotNote)); } }
    public double SpectrumAngleFrom { get => _angleFrom; set { Set(ref _angleFrom, value); } }
    public double SpectrumAngleTo { get => _angleTo; set { Set(ref _angleTo, value); } }
    public int SpectrumAngleSteps { get => _angleSteps; set { Set(ref _angleSteps, value); } }

    /// <summary>The wavelength (nm) of a sweep in angle.</summary>
    public double SpectrumWavelength { get => _wavelength; set { Set(ref _wavelength, value); Raise(nameof(PlotNote)); } }

    // ---- operations --------------------------------------------------------------------------

    /// <summary>A design from nothing, as File > New design describes it.</summary>
    public bool CreateNew(NewDesignRequest request)
    {
        var r = _api.CreateNew(request);
        if (!r.Ok) return Failed(r.Error, r.Status);
        _id = r.Value!.Id;
        FileName = null;
        Title = $"ThinFilm — {r.Value.Name}";
        Status = $"New design {r.Value.Name}: {request.Layers} layers on {request.Substrate.Name}. "
                 + "The merit function has one placeholder row - edit it or replace it.";
        Refresh();
        Unchanged();
        return true;
    }

    /// <summary>A saved design, opened. <paramref name="fileName"/> is what the window calls it:
    /// a copy of a sample still carries the sample's name inside it, and a title
    /// that says "AR-visible" for a file called coat-7.json names the wrong thing.</summary>
    public bool Open(string json, string? fileName = null)
    {
        var r = _api.Load(json);
        if (!r.Ok) return Failed(r.Error, r.Status);
        _id = r.Value!.Id;
        if (r.Value.Plot is { } plot) ApplyPlot(plot);
        FileName = fileName;
        Title = fileName is null ? $"ThinFilm — {r.Value.Name}" : $"ThinFilm — {fileName}";
        Status = fileName is null ? $"Opened {r.Value.Name}."
               : string.Equals(System.IO.Path.GetFileNameWithoutExtension(fileName), r.Value.Name,
                               StringComparison.OrdinalIgnoreCase)
                   ? $"Opened {fileName}."
                   : $"Opened {fileName} (design name: {r.Value.Name}).";
        Refresh();
        Unchanged();
        return true;
    }

    /// <summary>Which design is open: a new value whenever another is opened or made, so the window
    /// can tell its boxes hold the last design's text.</summary>
    public string? DesignId => _id;

    /// <summary>The design as it was last opened, made or saved, to tell whether it has changed.</summary>
    private string? _saved;

    private void Unchanged() => _saved = _id is null ? null : _api.Save(_id).Value;

    /// <summary>Whether the design differs from what was last opened, made or saved - anything a
    /// saved file would hold, a kept or reverted run's history included. What the window asks
    /// about before it opens another design or closes.</summary>
    public bool IsModified => _id is not null && _api.Save(_id) is { Ok: true } r && r.Value != _saved;

    // ---- the Description: where the design came from --------------------------------------

    /// <summary>The design's name, as New design gave it or a rename since.</summary>
    public string DesignName { get; private set; } = "";

    /// <summary>Renames the design. Unchanged text is no edit. The title follows the name while
    /// the design has no file; once saved, the title is the file's name, as it always was.</summary>
    public bool Rename(string name)
    {
        if (_id is null) return Failed("No design open.", null);
        if ((name ?? "").Trim() == DesignName) return true;
        var r = _api.SetName(_id, name ?? "");
        if (!r.Ok) return Failed(r.Error, r.Status);
        DesignName = r.Value!.Name;
        if (FileName is null) Title = $"ThinFilm — {DesignName}";
        Status = $"Renamed {DesignName}.";
        Raise(nameof(DesignName));
        return true;
    }

    /// <summary>Where the design came from, one line; empty when it does not say.</summary>
    public string DesignSource { get; private set; } = "";

    /// <summary>What it is for, what was changed from its source, its caveats.</summary>
    public string DesignNotes { get; private set; } = "";

    /// <summary>The Description's one-line summary, for its header when folded: the source, or
    /// the notes' first line, or an invitation.</summary>
    public string DescriptionSummary =>
        DesignSource.Length > 0 ? DesignSource
        : DesignNotes.Length > 0 ? DesignNotes.Split('\n')[0]
        : "none yet - where the design came from, and notes on it";

    /// <summary>The Description edited: null leaves a field as it is. Unchanged text is no edit,
    /// so leaving a box without typing does not mark the design changed.</summary>
    public bool SetDescription(string? source, string? notes)
    {
        if (_id is null) return Failed("No design open.", null);
        static string Norm(string? s) => (s ?? "").ReplaceLineEndings("\n").Trim();
        if ((source is null || Norm(source) == DesignSource) && (notes is null || Norm(notes) == DesignNotes)) return true;
        var r = _api.SetDescription(_id, source, notes);
        if (!r.Ok) return Failed(r.Error, r.Status);
        DesignSource = r.Value!.Source ?? "";
        DesignNotes = r.Value.Notes ?? "";
        Status = "Description saved with the design.";
        Raise(nameof(DesignSource));
        return true;
    }

    /// <summary>The file the design was opened from or last saved to, or null.</summary>
    public string? FileName { get; private set; }

    /// <summary>Why the open design cannot be evaluated (a catalogue glass not found), or null.</summary>
    public string? Blocked { get; private set; }

    /// <summary>The window has written the design to <paramref name="fileName"/>.</summary>
    public void SavedAs(string fileName)
    {
        FileName = fileName;
        Title = $"ThinFilm — {fileName}";
        Status = $"Saved {fileName}.";
        Unchanged();
    }

    /// <summary>The Spectrum tab's settings, both axes', as a design saves them.</summary>
    public PlotDocument Plot =>
        new(_from, _to, _steps, _angle, SpectrumQuantityCodes, _vsAngle, _angleFrom, _angleTo, _angleSteps, _wavelength);

    /// <summary>A design's saved spectrum, shown: the axis, both ranges and the quantities.</summary>
    private void ApplyPlot(PlotDocument p)
    {
        if (p.Problem() is not null) return;
        SpectrumFrom = p.From; SpectrumTo = p.To; SpectrumSteps = p.Steps; SpectrumAngle = p.Angle;
        SpectrumAngleFrom = p.AngleFrom; SpectrumAngleTo = p.AngleTo; SpectrumAngleSteps = p.AngleSteps;
        SpectrumWavelength = p.Wavelength;
        SpectrumQuantities = string.Join(",", p.Quantities);
        SpectrumVsAngle = p.VsAngle;
    }

    /// <summary>The design as a file, with the spectrum it is being looked at with - so it reopens
    /// to the same plot. Changing the plot alone is not an edit: it does not ask to be saved.</summary>
    public string? Save()
    {
        if (_id is null) { Status = "Nothing to save."; return null; }
        if (Plot.Problem() is null) _api.SetPlot(_id, Plot);
        var r = _api.Save(_id);
        if (!r.Ok) { Failed(r.Error, r.Status); return null; }
        Status = "Saved.";
        return r.Value;
    }

    public bool SetThickness(int layer, double thickness)
    {
        if (_id is null) return Failed("No design open.", null);
        var r = _api.SetThickness(_id, layer, thickness);
        if (!r.Ok) return Failed(r.Error, r.Status);
        Refresh();
        Status = $"Layer {layer + 1} set to {Lengths.Say(thickness, WavelengthUnit)}.";
        return true;
    }

    // ---- materials ---------------------------------------------------------------------------

    /// <summary>
    /// The material database New design and Edit > Materials choose from. The shipped one by
    /// default - which is what a test sees - and the window replaces it with the person's own
    /// working copy (MaterialDatabase.OpenOrCreate) when it starts.
    /// </summary>
    public MaterialDatabase Database { get; set; } = MaterialDatabase.Shipped();

    /// <summary>The design's own copies of its materials and media, or null with nothing open.</summary>
    public MaterialSet? Materials => _id is null ? null : _api.MaterialSet(_id).Value;

    /// <summary>What OK in Edit > Materials does: the design's materials and media replaced at once.</summary>
    public bool ApplyMaterials(MaterialEdit[] materials, NamedMaterial incident, NamedMaterial substrate)
    {
        if (_id is null) return Failed("No design open.", null);
        var r = _api.ReplaceMaterials(_id, materials, incident, substrate);
        if (!r.Ok) return Failed(r.Error, r.Status);
        Refresh();
        Status = $"Materials updated: {materials.Length} in the design, on {substrate.Name}.";
        return true;
    }

    // ---- the layer stack ---------------------------------------------------------------------

    /// <summary>Insert layer: a copy of layer <paramref name="after"/> after it, or a first layer in
    /// an empty stack - decided in the API (InsertLayerCopy), which the CLI and the MCP call too.</summary>
    public bool InsertLayer(int after)
    {
        if (_id is null) return Failed("No design open.", null);
        bool empty = Layers.Count == 0;
        int source = after >= 0 && after < Layers.Count ? after : Layers.Count - 1;
        var r = _api.InsertLayerCopy(_id, after);
        if (!r.Ok) return Failed(r.Error, r.Status);
        Refresh();
        Status = empty ? $"Layer 1 inserted: {Layers[0].Material}, {Layers[0].ValueText} {Layers[0].UnitLabel}."
                       : $"Layer {source + 2} inserted, a copy of layer {source + 1}.";
        return true;
    }

    public bool RemoveLayer(int index)
    {
        if (_id is null) return Failed("No design open.", null);
        var r = _api.RemoveLayer(_id, index);
        if (!r.Ok) return Failed(r.Error, r.Status);
        Refresh();
        Status = $"Layer {index + 1} deleted.";
        return true;
    }

    /// <summary>Layer <paramref name="index"/> moved one place toward the incident side
    /// (<paramref name="delta"/> = -1) or the substrate (+1).</summary>
    public bool MoveLayer(int index, int delta)
    {
        if (_id is null) return Failed("No design open.", null);
        int to = index + delta;
        if (index < 0 || index >= Layers.Count) return Failed("Select the layer to move.", null);
        if (to < 0 || to >= Layers.Count)
            return Failed(delta < 0 ? "Layer 1 is already next to the incident medium."
                                    : "The last layer is already next to the substrate.", null);
        var r = _api.MoveLayer(_id, index, to);
        if (!r.Ok) return Failed(r.Error, r.Status);
        Refresh();
        Status = $"Layer {index + 1} moved to {to + 1}.";
        return true;
    }

    public bool SetLayerMaterial(int index, int material)
    {
        if (_id is null) return Failed("No design open.", null);
        var r = _api.SetLayerMaterial(_id, index, material);
        if (!r.Ok) { Failed(r.Error, r.Status); Raise(nameof(Layers)); return false; }
        Refresh();
        Status = $"Layer {index + 1} is now {Layers[index].Material}.";
        return true;
    }

    public bool SetLayerVariable(int index, bool variable)
    {
        if (_id is null) return Failed("No design open.", null);
        var r = _api.SetLayerVariable(_id, index, variable);
        if (!r.Ok) { Failed(r.Error, r.Status); Raise(nameof(Layers)); return false; }
        Refresh();
        Status = variable ? $"Layer {index + 1} will be optimised."
                          : $"Layer {index + 1} is fixed at {Layers[index].ValueText} {Layers[index].UnitLabel}.";
        return true;
    }

    // ---- thickness units and pickups ----------------------------------------------------------

    /// <summary>The design's thickness unit ("nm", "QWOT", "FWOT") and the reference wavelength
    /// (nm) QWOT and FWOT are counted at.</summary>
    public string ThicknessUnit { get; private set; } = "nm";
    public double ReferenceWavelength { get; private set; } = 550.0;

    /// <summary>The design's maximum for unconstrained layers, nm (Edit > System Parameters).</summary>
    public double UnconstrainedMaximum { get; private set; } = 600.0;

    /// <summary>Edit > System Parameters: R and T include the substrate's back surface; the
    /// substrate's thickness, mm; angles of incidence are given in air.</summary>
    public bool BackSurface { get; private set; }
    public double SubstrateThicknessMm { get; private set; } = 1.0;
    public bool AngleInAir { get; private set; }

    /// <summary>What OK in Edit > System Parameters does with those three.</summary>
    public bool SetSystem(bool backSurface, double substrateMm, bool angleInAir)
    {
        if (_id is null) return Failed("No design open.", null);
        var r = _api.SetSystem(_id, new SystemChange(backSurface, substrateMm, angleInAir));
        if (!r.Ok) return Failed(r.Error, r.Status);
        Refresh();
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        Status = (backSurface
                     ? $"R and T include the back surface of a {substrateMm.ToString("0.###", inv)} mm substrate"
                     : "R and T are the coated surface alone")
                 + (angleInAir ? "; angles are in air." : "; angles are in the incident medium.");
        return true;
    }

    /// <summary>A cell edit that could not be read: said, and the table put back.</summary>
    internal void Refused(string why) { Status = why; Raise(nameof(Layers)); }

    /// <summary>A thickness typed in the layer's own unit.</summary>
    public bool SetLayerValue(int index, double value)
    {
        if (_id is null) return Failed("No design open.", null);
        var r = _api.SetLayerValue(_id, index, value);
        if (!r.Ok) { Failed(r.Error, r.Status); Raise(nameof(Layers)); return false; }
        Refresh();
        Status = $"Layer {index + 1} set to {Layers[index].ValueText} {Layers[index].UnitLabel}"
                 + (Layers[index].Unit == "nm" ? "." : $" ({Lengths.Say(Layers[index].Thickness, WavelengthUnit)}).");
        return true;
    }

    /// <summary>One layer's unit, or null to follow the design's. The layer keeps its nanometres.</summary>
    public bool SetLayerUnit(int index, string? unit)
    {
        if (_id is null) return Failed("No design open.", null);
        var r = _api.SetLayerUnit(_id, index, unit);
        if (!r.Ok) { Failed(r.Error, r.Status); Raise(nameof(Layers)); return false; }
        Refresh();
        Status = $"Layer {index + 1} is now in {Layers[index].UnitLabel}"
                 + (unit is null ? ", the design's unit." : ".");
        return true;
    }

    /// <summary>What OK in Edit > System Parameters does with the thickness settings. A QWOT or FWOT
    /// layer keeps its number through a new reference, so the design moves; the status says so.</summary>
    public bool SetThicknessUnits(string unit, double referenceNm)
    {
        if (_id is null) return Failed("No design open.", null);
        bool moved = referenceNm != ReferenceWavelength;
        var r = _api.SetThicknessUnits(_id, unit, referenceNm);
        if (!r.Ok) return Failed(r.Error, r.Status);
        Refresh();
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string at = MeritRow.UnitLabel(WavelengthUnit) == "µm"
            ? (referenceNm / 1000.0).ToString("0.#####", inv) + " µm" : referenceNm.ToString("0.###", inv) + " nm";
        Status = $"Layer thicknesses in {Lengths.Label(unit, WavelengthUnit)}, reference {at}."
                 + (moved && Layers.Any(l => l.Unit != "nm")
                     ? " The QWOT and FWOT layers kept their numbers, so they moved to the new reference."
                     : "");
        return true;
    }

    /// <summary>The ceiling the optimiser gives a layer with no maximum of its own, nm.</summary>
    public bool SetUnconstrainedMaximum(double nm)
    {
        if (_id is null) return Failed("No design open.", null);
        var r = _api.SetUnconstrainedMaximum(_id, nm);
        if (!r.Ok) return Failed(r.Error, r.Status);
        Refresh();
        Status = $"Unconstrained layers may now be up to {Lengths.Say(nm, WavelengthUnit)} thick.";
        return true;
    }

    /// <summary>What OK in Layer properties does: everything at once, or nothing and the reason.</summary>
    public bool SetLayerProperties(int index, LayerProperties properties)
    {
        if (_id is null) return Failed("No design open.", null);
        var r = _api.SetLayerProperties(_id, index, properties);
        if (!r.Ok) return Failed(r.Error, r.Status);
        Refresh();
        Status = $"Layer {index + 1}: {Layers[index].Material}, {Layers[index].ValueText} {Layers[index].UnitLabel}"
                 + (Layers[index].PickupText is { Length: > 0 } p ? $" {p}." : ".");
        return true;
    }

    /// <summary>What OK in Set vary constraints does: the bounds of a range of layers, or nothing and the reason.</summary>
    public bool SetLayerBounds(BoundsRequest request)
    {
        if (_id is null) return Failed("No design open.", null);
        var r = _api.SetLayerBounds(_id, request);
        if (!r.Ok) return Failed(r.Error, r.Status);
        Refresh();
        var set = r.Value!.Set;
        string bounds = LayerRow.Bounds(request.Min, request.Max, request.Unit, WavelengthUnit);
        string what = bounds.Length == 0 ? "unconstrained" : $"bounded {bounds} {Lengths.Label(request.Unit, WavelengthUnit)}";
        Status = set.Length == 0 ? "No layer matched; nothing changed."
                 : $"{(set.Length == 1 ? "Layer" : "Layers")} {Ranges(set)} {what}.";
        if (r.Value.PickedUp.Length > 0)
            Status += $" {(r.Value.PickedUp.Length == 1 ? "Layer" : "Layers")} {Ranges(r.Value.PickedUp)} passed over: picked up, so {(r.Value.PickedUp.Length == 1 ? "it follows its" : "they follow their")} parent.";
        return true;
    }

    /// <summary>Layer numbers from 1 as runs: "1–4, 7, 9–12".</summary>
    public static string Ranges(IReadOnlyList<int> layers)
    {
        var parts = new List<string>();
        for (int i = 0; i < layers.Count;)
        {
            int j = i;
            while (j + 1 < layers.Count && layers[j + 1] == layers[j] + 1) ++j;
            parts.Add(j == i ? $"{layers[i] + 1}" : $"{layers[i] + 1}–{layers[j] + 1}");
            i = j + 1;
        }
        return string.Join(", ", parts);
    }

    /// <summary>Couple all layers: each picked up from the first layer of its material.</summary>
    public bool CoupleLayers()
    {
        if (_id is null) return Failed("No design open.", null);
        var r = _api.CoupleLayers(_id);
        if (!r.Ok) return Failed(r.Error, r.Status);
        Refresh();
        Status = r.Value == 0
            ? "Nothing to couple: no material has a second layer in the same unit."
            : $"{r.Value} layer{(r.Value == 1 ? "" : "s")} coupled to the first layer of {(r.Value == 1 ? "its" : "their")} material.";
        return true;
    }

    /// <summary>Decouple all layers: every pickup removed.</summary>
    public bool DecoupleLayers()
    {
        if (_id is null) return Failed("No design open.", null);
        var r = _api.DecoupleLayers(_id);
        if (!r.Ok) return Failed(r.Error, r.Status);
        Refresh();
        Status = r.Value == 0 ? "No layer was coupled." : $"{r.Value} pickup{(r.Value == 1 ? "" : "s")} removed; every layer is varied on its own.";
        return true;
    }

    /// <summary>Delete all layers: the bare substrate.</summary>
    public bool ClearLayers()
    {
        if (_id is null) return Failed("No design open.", null);
        var r = _api.ClearLayers(_id);
        if (!r.Ok) return Failed(r.Error, r.Status);
        Refresh();
        Status = "All layers deleted: the bare substrate. Insert layer or Input design string builds a stack again.";
        return true;
    }

    /// <summary>Reverse layers, and with media the incident medium and substrate swapped too.</summary>
    public bool ReverseLayers(bool media)
    {
        if (_id is null) return Failed("No design open.", null);
        var r = _api.ReverseLayers(_id, media);
        if (!r.Ok) return Failed(r.Error, r.Status);
        Refresh();
        Status = (media
                     ? $"Layers and media reversed: {r.Value!.Incident?.Name} is now the incident medium and {r.Value.Exit?.Name} the substrate."
                     : "Layers reversed; the incident medium and the substrate are where they were.")
                 + (media && r.Value!.Incident is { KMax: > 0.0 } inc
                     ? $" Its k (up to {inc.KMax.ToString("G2", System.Globalization.CultureInfo.InvariantCulture)}) is treated as 0 in every calculation; the design keeps it."
                     : "");
        return true;
    }

    // ---- OpticStudio coating files ----------------------------------------------------------

    /// <summary>The layer-stack coatings in a coating file, or null with the reason in Status.</summary>
    public ZemaxCoatingInfo[]? ZemaxCoatings(string path)
    {
        var r = ThinFilmApi.ZemaxCoatings(path);
        if (!r.Ok) { Failed(r.Error, r.Status); return null; }
        return r.Value;
    }

    /// <summary>File > Import ZEMAX coating: the stack replaced by one COAT from a file.</summary>
    public bool ImportZemaxCoating(string path, string coating)
    {
        if (_id is null) return Failed("No design open.", null);
        var r = _api.ImportZemaxCoating(_id, path, coating);
        if (!r.Ok) return Failed(r.Error, r.Status);
        Refresh();
        Status = $"Imported {coating} from {Path.GetFileName(path)}: {Layers.Count} layers."
                 + string.Concat(r.Value!.Notes.Select(n => $" {char.ToUpperInvariant(n[0])}{n[1..]}."));
        return true;
    }

    /// <summary>The export form, filled in from the design: its name, its wavelengths, and the file
    /// last written to, if any.</summary>
    /// <summary>File > Export design report's form: the Spectrum tab's range, every 10 nm.</summary>
    public DesignReportForm? DesignReportForm()
    {
        if (_id is null || _api.Document(_id).Value is not { } doc) return null;
        return new DesignReportForm(doc.Name, SpectrumFrom, SpectrumTo, WavelengthUnit);
    }

    /// <summary>File > Export design report: written to the request's path, or false with the
    /// reason in Status.</summary>
    public bool ExportDesignReport(DesignReportRequest request)
    {
        if (_id is null) return Failed("No design open.", null);
        var r = _api.DesignReport(_id, request);
        if (!r.Ok) return Failed(r.Error, r.Status);
        var w = r.Value!.Wavelengths;
        Status = $"Wrote {Path.GetFileName(request.Path)}: {Layers.Count} layers in nm, n and k at {w.Length} wavelengths "
                 + $"from {Lengths.Say(w[0], WavelengthUnit)} to {Lengths.Say(w[^1], WavelengthUnit)}."
                 + string.Concat(r.Value.Notes.Select(n => $" {char.ToUpperInvariant(n[0])}{n[1..]}."));
        return true;
    }

    public ZemaxExportForm? ZemaxExportForm(string? lastPath)
    {
        if (_id is null || _api.Document(_id).Value is not { } doc) return null;
        return new ZemaxExportForm(lastPath, doc.Name, doc.Wavelength.Min(), doc.Wavelength.Max(), WavelengthUnit);
    }

    /// <summary>File > Export to ZEMAX coating. Null with the reason in Status when refused;
    /// <paramref name="exists"/> says the refusal was a COAT of that name already in the file,
    /// which the window asks about and then sends again with Replace.</summary>
    public ZemaxExportReport? ExportZemaxCoating(ZemaxExportRequest request, out bool exists)
    {
        exists = false;
        if (_id is null) { Failed("No design open.", null); return null; }
        var r = _api.ExportZemaxCoating(_id, request);
        if (!r.Ok)
        {
            exists = r.Status == ThinFilmApi.CoatingExists;
            Status = r.Error ?? "The export failed.";
            return null;
        }
        var e = r.Value!;
        Status = $"{(e.Replaced ? "Replaced" : "Wrote")} COAT {e.Coating} in {Path.GetFileName(request.Path)}"
                 + (e.Added.Length > 0 ? $", adding MATE {string.Join(", ", e.Added)}" : ", using materials already there")
                 + "." + string.Concat(e.Notes.Select(n => $" {char.ToUpperInvariant(n[0])}{n[1..]}."));
        return e;
    }

    /// <summary>The thin-film coatings in an Optiland coating or lens file, or null with the reason in Status.</summary>
    public OptilandCoatingInfo[]? OptilandCoatings(string path)
    {
        var r = ThinFilmApi.OptilandCoatings(path);
        if (!r.Ok) { Failed(r.Error, r.Status); return null; }
        return r.Value;
    }

    /// <summary>File > Import Optiland coating: the stack replaced by one of a file's coatings.</summary>
    public bool ImportOptilandCoating(string path, int index, string name)
    {
        if (_id is null) return Failed("No design open.", null);
        var r = _api.ImportOptilandCoating(_id, path, index);
        if (!r.Ok) return Failed(r.Error, r.Status);
        Refresh();
        Status = $"Imported {name} from {Path.GetFileName(path)}: {Layers.Count} layers."
                 + string.Concat(r.Value!.Notes.Select(n => $" {char.ToUpperInvariant(n[0])}{n[1..]}."));
        return true;
    }

    /// <summary>File > Export Optiland coating: the design as an Optiland ThinFilmCoating file.</summary>
    public bool ExportOptilandCoating(string path)
    {
        if (_id is null) return Failed("No design open.", null);
        var r = _api.ExportOptilandCoating(_id, path);
        if (!r.Ok) return Failed(r.Error, r.Status);
        Status = $"Wrote {Path.GetFileName(path)}: {r.Value!.Layers} layer{(r.Value!.Layers == 1 ? "" : "s")}, for Optiland's load_obj_from_json(BaseCoating, path)."
                 + string.Concat(r.Value.Notes.Select(n => $" {char.ToUpperInvariant(n[0])}{n[1..]}."));
        return true;
    }

    /// <summary>What OK in Input design string does: the stack replaced, or nothing and the reason.</summary>
    public bool ApplyDesignString(DesignStringRequest request)
    {
        if (_id is null) return Failed("No design open.", null);
        var r = _api.ApplyDesignString(_id, request);
        if (!r.Ok) return Failed(r.Error, r.Status);
        Refresh();
        int picked = Layers.Count(l => l.IsPickedUp);
        Status = $"{request.Text.Trim()}: {Layers.Count} layers in {request.Unit}"
                 + (picked > 0 ? $", {picked} picked up from the first layer of their letter." : ".");
        return true;
    }

    /// <summary>How many of <paramref name="unit"/> one nanometre of a material is, or NaN.</summary>
    public double UnitsPerNm(int material, string unit) =>
        _id is null ? double.NaN : _api.UnitsPerNm(_id, material, unit) is { Ok: true } r ? r.Value : double.NaN;

    /// <summary>The design's materials, in the order a layer's material index counts.</summary>
    public IReadOnlyList<string> MaterialNames => _id is null ? [] : _api.MaterialNames(_id).Value ?? [];

    // ---- the merit function ------------------------------------------------------------------

    /// <summary>One row replaced. Called by MeritRow's setters; the table is rebuilt from the
    /// API's answer, so a refused edit leaves the row as the design holds it.</summary>
    public bool EditOperand(int index, OperandEdit edit)
    {
        if (_id is null) return Failed("No design open.", null);
        var r = _api.ReplaceOperand(_id, index, edit);
        if (!r.Ok) { Failed(r.Error, r.Status); Raise(nameof(MeritRows)); return false; }
        Refresh();
        Status = $"Merit row {index + 1} changed.";
        return true;
    }

    /// <summary>A copy of row <paramref name="after"/> inserted below it, or a reflectance row at
    /// the middle of the grid when the table is empty or nothing is selected.</summary>
    public bool InsertOperand(int after)
    {
        if (_id is null) return Failed("No design open.", null);
        int count = _api.Operands(_id).Value?.Length ?? 0;
        int at = after >= 0 && after < count ? after + 1 : count;
        var r = _api.InsertOperandCopy(_id, after);
        if (!r.Ok) return Failed(r.Error, r.Status);
        Refresh();
        Status = $"Merit row {at + 1} inserted.";
        return true;
    }

    public bool RemoveOperand(int index)
    {
        if (_id is null) return Failed("No design open.", null);
        var r = _api.RemoveOperand(_id, index);
        if (!r.Ok) return Failed(r.Error, r.Status);
        Refresh();
        Status = $"Merit row {index + 1} removed.";
        return true;
    }

    // ---- building the merit function in bulk (ThinFilm.Api MeritTable) ------------------------

    /// <summary>How many rows a filter matches, for a dialog to say before it acts; null with the
    /// reason in Status when the filter itself is wrong.</summary>
    public int? MatchCount(OperandFilter filter)
    {
        if (_id is null) return null;
        var r = _api.MatchOperands(_id, filter);
        if (!r.Ok) { Status = r.Error ?? ""; return null; }
        return r.Value!.Length;
    }

    // ---- the merit function as a file of its own (MeritFiles.cs) ----------------------------

    /// <summary>The merit function as a merit file (JSON), or null with the reason in Status.</summary>
    public string? SaveMeritFunction()
    {
        if (_id is null) { Failed("No design open.", null); return null; }
        var r = _api.SaveMeritFunction(_id);
        if (!r.Ok) { Failed(r.Error, r.Status); return null; }
        return r.Value;
    }

    /// <summary>Said once the file is written.</summary>
    public void SavedMeritFunction(string fileName) =>
        Status = $"Merit function saved to {fileName}: {Rows(MeritRows.Count)}.";

    /// <summary>A merit file's rows in place of the whole merit function, or after its rows.</summary>
    public bool OpenMeritFunction(string json, string fileName, bool append = false)
    {
        if (_id is null) return Failed("No design open.", null);
        var r = _api.OpenMeritFunction(_id, json, append);
        if (!r.Ok) return Failed($"{fileName}: {r.Error}", r.Status);
        Refresh();
        Status = append
            ? $"{Rows(r.Value!.Rows)} from {fileName} appended: the merit function now has {Rows(MeritRows.Count)}."
            : $"Merit function opened from {fileName}: {Rows(r.Value!.Rows)}, in place of the table there was.";
        return true;
    }

    /// <summary>The merit table with its values as CSV, or null with the reason in Status.</summary>
    public string? ExportMeritCsv()
    {
        if (_id is null) { Failed("No design open.", null); return null; }
        var r = _api.ExportMeritCsv(_id);
        if (!r.Ok) { Failed(r.Error, r.Status); return null; }
        return r.Value;
    }

    public bool ClearOperands()
    {
        if (_id is null) return Failed("No design open.", null);
        var r = _api.ClearOperands(_id);
        if (!r.Ok) return Failed(r.Error, r.Status);
        Refresh();
        Status = $"Merit function cleared: {Rows(r.Value!.Rows)} removed. Add operands to evaluate the design.";
        return true;
    }

    public bool DeleteOperands(OperandFilter filter)
    {
        if (_id is null) return Failed("No design open.", null);
        var r = _api.DeleteOperands(_id, filter);
        if (!r.Ok) return Failed(r.Error, r.Status);
        Refresh();
        Status = $"{Rows(r.Value!.Rows)} deleted ({MeritTable.Describe(filter)})." + BlockedNote();
        return true;
    }

    public bool SetOperandWeights(OperandFilter filter, double weight)
    {
        if (_id is null) return Failed("No design open.", null);
        var r = _api.SetOperandWeights(_id, filter, weight);
        if (!r.Ok) return Failed(r.Error, r.Status);
        Refresh();
        Status = $"Weight {weight.ToString("G6", System.Globalization.CultureInfo.InvariantCulture)} set on "
                 + $"{Rows(r.Value!.Rows)} ({MeritTable.Describe(filter)}).";
        return true;
    }

    public bool AddOperands(OperandSweep sweep)
    {
        if (_id is null) return Failed("No design open.", null);
        var r = _api.AddOperands(_id, sweep);
        if (!r.Ok) return Failed(r.Error, r.Status);
        Refresh();
        Status = $"{Rows(r.Value!.Rows)} added at the end of the table.";
        return true;
    }

    /// <summary>
    /// An Add dialog starts on the band the table already covers, at the spacing it already uses:
    /// a design scored 420-680 nm every 10 nm offers 420, 680 and 10. With nothing to go by,
    /// 400-700 nm every 10 nm. Angles start at 0, and a many-angle sweep at 0 to 45 every 15.
    /// </summary>
    public void PrefillSweep(OperandSweepForm form)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var wls = (_id is null ? [] : _api.Operands(_id).Value ?? [])
            .SelectMany(e => e.Wavelengths).Select(w => Math.Round(w, 6)).Distinct().OrderBy(w => w).ToArray();
        double lo = wls.Length > 0 ? wls[0] : 400.0, hi = wls.Length > 1 ? wls[^1] : 700.0;
        double step = wls.Length > 1 ? wls.Zip(wls.Skip(1), (a, b) => b - a).Min() : 10.0;
        if (hi <= lo) (lo, hi) = (400.0, 700.0);
        double f = WavelengthUnit == "um" ? 1000.0 : 1.0;
        string Show(double nm) => (nm / f).ToString(f > 1 ? "0.#####" : "0.###", inv);
        form.Wavelength1 = Show(lo);
        form.Wavelength2 = Show(hi);
        form.WavelengthStep = Show(step);
        form.Angle1 = "0";
        if (form.HasManyAngles) { form.Angle2 = "45"; form.AngleStep = "15"; }
    }

    private static string Rows(int n) => n == 1 ? "1 row" : $"{n} rows";

    private string BlockedNote() => Blocked is { } b && ThinFilmApi.IsMeritBlock(b) ? $" Not evaluated: {b}." : "";

    public bool SetWavelengthUnit(string unit)
    {
        if (_id is null) return Failed("No design open.", null);
        var r = _api.SetWavelengthUnit(_id, unit);
        if (!r.Ok) return Failed(r.Error, r.Status);
        Refresh();
        Status = $"Wavelengths shown in {MeritRow.UnitLabel(unit)}.";
        return true;
    }

    /// <summary>Where a new optical row goes when there is nothing to copy: the middle of the
    /// wavelengths the design already uses, or 550 nm.</summary>
    public double DefaultWavelength => _id is null ? 550.0 : _api.DefaultWavelength(_id);

    public bool Optimise(string stages, string? norm = null, int iterations = 300)
    {
        if (_id is null) return Failed("No design open.", null);

        var r = _api.Optimise(_id, new OptimiseRequest(
            Stages: stages, Norm: norm, MaxIterations: iterations));
        if (!r.Ok) return Failed(r.Error, r.Status);

        var v = r.Value!;
        Refresh();
        Status = $"{string.Join(" then ", v.StagesRun)}: merit {v.InitialMerit:G6} → "
                 + $"{v.FinalMerit:G6}, {v.Layers} layer{(v.Layers == 1 ? "" : "s")}, {v.ElapsedMilliseconds:F0} ms "
                 + $"({v.Outcome}).";
        if (v.PlacedOnBound is { Length: > 0 } placed)
            Status += $" {(placed.Length == 1 ? "Layer" : "Layers")} {Ranges(placed)} started outside "
                      + $"{(placed.Length == 1 ? "its" : "their")} own min or max and {(placed.Length == 1 ? "was" : "were")} "
                      + "placed on it first; every step after that reflects at the bounds.";
        // A LOCAL MINIMUM IS NOT A FAILURE, BUT IT LOOKS LIKE ONE. A quarter-wave start targeted
        // only at its own reference wavelength is a local minimum in every thickness, so refine
        // finds no downhill step and the merit does not move; a person seeing that thinks the
        // button is broken. Say what happened and what can escape it.
        if (v.Outcome == "NoDownhillStep" && v.FinalMerit >= v.InitialMerit)
            Status += " No small change to any thickness lowers the merit: this design is at a local "
                      + "minimum. Try the global,refine stage, or refine,needle to add layers.";
        return true;
    }

    // ---- runs: the optimisation windows (RunMonitor) -------------------------------------------

    /// <summary>Starts a run on the open design; null, with the reason in Status, when refused.</summary>
    public RunReport? StartRun(RunRequest request)
    {
        if (_id is null) { Failed("No design open.", null); return null; }
        var r = _api.StartRun(_id, request);
        if (!r.Ok) { Failed(r.Error, r.Status); return null; }
        return r.Value;
    }

    public RunReport? RunStatus(string run, int since) => _api.RunStatus(run, since).Value;

    public void StopRun(string run) => _api.StopRun(run);

    /// <summary>Waits up to <paramref name="ms"/> for a run to end; for tests and the CLI's way of
    /// watching, never the window's.</summary>
    public RunReport? WaitRun(string run, int ms) => _api.WaitRun(run, ms).Value;

    /// <summary>The run's design becomes the open one.</summary>
    public bool KeepRun(string run)
    {
        var r = _api.KeepRun(run);
        if (!r.Ok) return Failed(r.Error, r.Status);
        Refresh();
        if (_api.RunStatus(run).Value is { } v)
            Status = $"{v.Method} kept: RMSE {ThinFilmApi.Rms(v.StartMerit)} → {ThinFilmApi.Rms(v.EndMerit ?? v.BestMerit)} "
                     + $"in {v.ElapsedSeconds:0.0##} s ({v.Outcome}).";
        return true;
    }

    /// <summary>The run's start design beside its result; null, with the reason in Status, when the run
    /// has not ended or has nothing to compare.</summary>
    public RunComparison? CompareRun(string run)
    {
        var r = _api.CompareRun(run);
        if (!r.Ok) { Failed(r.Error, r.Status); return null; }
        return r.Value;
    }

    public RunSpectra? RunSpectrum(string run, SpectrumRequest request)
    {
        var r = _api.RunSpectrum(run, request);
        if (!r.Ok) { Failed(r.Error, r.Status); return null; }
        return r.Value;
    }

    /// <summary>The run's result is dropped; the design is as it was.</summary>
    public bool RevertRun(string run)
    {
        var r = _api.RevertRun(run);
        if (!r.Ok) return Failed(r.Error, r.Status);
        Status = "Optimisation reverted; the design is as it was.";
        return true;
    }

    /// <summary>The needle map's best insertion, as a sentence — what a designer wants from it.</summary>
    /// <remarks>QUESTIONABLE USEFULNESS, MAY BE DELETED IN THE FINAL VERSION. Its menu item and button
    /// ("Where would a layer help?") were removed at the user's request (2026-10-13); nothing in the
    /// GUI calls this now. The needle STAGE of Optimise is separate and stays.</remarks>
    public bool ShowNeedleAdvice()
    {
        if (_id is null) return Failed("No design open.", null);
        var r = _api.NeedleMap(_id, 64);
        if (!r.Ok) return Failed(r.Error, r.Status);

        var v = r.Value!;
        Status = v.BestSlope >= 0.0
            ? "No insertion helps to first order — this design is needle-stationary."
            : $"Best insertion: {v.Candidate[v.BestCandidate]} at {v.BestDepth:F1} nm "
              + $"below the incident side, in layer {LayerAtDepth(v.BestDepth)} "
              + $"(slope {v.BestSlope:G3}).";
        return true;
    }

    public bool RefreshSpectrum()
    {
        if (_id is null) return Failed("No design open.", null);
        if ((_vsAngle ? _angleSteps : _steps) < 2) return Failed("A spectrum needs at least two steps.", null);
        if (_vsAngle
                ? !(_angleFrom >= 0.0 && _angleFrom < 90.0 && _angleTo >= 0.0 && _angleTo < 90.0)
                : !(_angle >= 0.0 && _angle < 90.0))
            return Failed("The angle of incidence must be at least 0° and below 90°.", null);
        if (_vsAngle && !(_wavelength > 0.0)) return Failed("The wavelength must be positive.", null);

        var quantities = SpectrumQuantityCodes;
        if (quantities.Length == 0) return Failed("Choose at least one quantity to plot.", null);

        var r = _api.Spectrum(_id, _vsAngle
            ? new SpectrumRequest(_angleFrom, _angleTo, _angleSteps, 0.0, quantities, VsAngle: true, Wavelength: _wavelength)
            : new SpectrumRequest(_from, _to, _steps, _angle, quantities));
        if (!r.Ok) return Failed(r.Error, r.Status);

        double[] x = r.Value!.Angle
                     ?? (WavelengthUnit == "um" ? r.Value.Wavelength.Select(w => w / 1000.0).ToArray()
                                                : r.Value.Wavelength);

        var series = new List<PlotSeries>();
        for (int i = 0; i < quantities.Length; ++i)
            if (r.Value!.Quantity.TryGetValue(quantities[i], out var values))
                series.Add(QuantitySeries(quantities[i], QuantityLabel(quantities[i]), x, values,
                                          Palette[i % Palette.Length]));

        Spectrum = series;
        _outside = r.Value!.Outside;
        Raise(nameof(PlotNote));
        _plotted = (r.Value!, quantities, PlotNote);
        Raise(nameof(Spectrum));
        return true;
    }

    /// <summary>The spectrum last plotted, the quantities in it, and what its note said then.</summary>
    private (SpectrumReport Report, string[] Quantities, string Note)? _plotted;

    /// <summary>
    /// Spectrum > Export: the curves last plotted as tab-separated text - a column for the
    /// wavelength (in the design's unit) or the angle, and one per quantity, every number written
    /// to round-trip - under a few # lines saying what they are. Null, with the reason in Status,
    /// when nothing has been plotted.
    /// </summary>
    public string? SpectrumText()
    {
        if (_plotted is not { } p) { Status = "Plot a spectrum first: Export writes what is plotted."; return null; }
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var s = new System.Text.StringBuilder();
        s.AppendLine($"# {Title.Replace("ThinFilm — ", "")}");
        s.AppendLine($"# {p.Note}");
        s.AppendLine("# Phases, Psi and Delta in degrees, as plotted; group delay in fs, GDD in fs^2, TOD in fs^3. NaN: an angle that cannot be reached.");
        // IN THE PLOT'S UNITS: a phase drawn in degrees was exported in radians, while the CLI and
        // MCP already wrote degrees.
        var report = ThinFilm.Api.SpectrumQuantities.InAxisUnits(p.Report);
        bool angle = report.Angle is not null;
        bool um = WavelengthUnit == "um";
        var x = report.Angle ?? (um ? report.Wavelength.Select(w => w / 1000.0).ToArray() : report.Wavelength);
        var columns = p.Quantities.Where(q => report.Quantity.ContainsKey(q)).ToArray();
        s.AppendLine(string.Join("\t", [angle ? "angle_deg" : um ? "wavelength_um" : "wavelength_nm", .. columns]));
        for (int i = 0; i < x.Length; ++i)
            s.AppendLine(string.Join("\t", [x[i].ToString("R", inv), .. columns.Select(q => report.Quantity[q][i].ToString("R", inv))]));
        return s.ToString();
    }

    // ---- plumbing ----------------------------------------------------------------------------

    private void Refresh()
    {
        if (_id is null) return;

        var e = _api.Evaluate(_id);
        if (!e.Ok) { Failed(e.Error, e.Status); return; }

        var materials = _api.MaterialNames(_id).Value ?? [];
        var described = _api.Describe(_id).Value;
        ThicknessUnit = described?.ThicknessUnit ?? "nm";
        ReferenceWavelength = described?.ReferenceWavelength ?? 550.0;
        UnconstrainedMaximum = described?.UnconstrainedMaximum ?? 600.0;
        BackSurface = described?.BackSurface ?? false;
        SubstrateThicknessMm = described?.SubstrateThicknessMm ?? 1.0;
        AngleInAir = described?.AngleInAir ?? false;
        DesignName = described?.Name ?? "";
        DesignSource = described?.Source ?? "";
        DesignNotes = described?.Notes ?? "";
        WavelengthUnit = described?.WavelengthUnit ?? "nm";
        var unitOptions = LayerRow.Options(ThicknessUnit, WavelengthUnit);
        Layers = e.Value!.Layers
            .Select(l => new LayerRow
            {
                Index = l.Index, Material = l.Material,
                Thickness = l.Thickness, Variable = l.Variable,
                Unit = l.Unit, UnitSetting = l.UnitSetting, DesignUnit = ThicknessUnit,
                Value = l.Value, Pickup = l.Pickup, Min = l.Min, Max = l.Max,
                LengthUnit = WavelengthUnit, UnitOptions = unitOptions,
                Owner = this, MaterialOptions = materials,
            }).ToList();

        Operands = e.Value.Operands
            .Select(o => new OperandRow
            {
                Index = o.Index, Code = o.Code, Comparison = o.Comparison, Target = o.Target,
                Value = o.Value, Residual = o.Residual, Flags = string.Join(",", o.Flags),
                At = Where(o.At, WavelengthUnit), Weight = o.Weight, Threshold = o.Threshold, Temperature = o.Temperature, LengthUnit = WavelengthUnit,
                TargetHi = o.TargetHi,
            }).ToList();

        Merit = e.Value.Merit;

        var edits = _api.Operands(_id).Value ?? [];
        MeritRows = Operands.Zip(edits, (row, edit) => new MeritRow(this, row, edit, WavelengthUnit))
                            .ToList();

        var d = _api.Describe(_id).Value;
        Summary = d is null ? ""
            : $"{Count(d.Layers, "layer")}, {Lengths.Say(d.TotalThickness, WavelengthUnit)} · {Count(d.Operands, "operand")} over "
              + $"{Count(d.Points, "point")} · {Count(d.Variables, "variable")}";
        IncidentText = d?.Incident is { } inc
            ? $"Incident medium: {Describe(inc)} — next to layer 1"
              + (inc.KMax > 0.0 ? ". ⚠ Its k is treated as 0: an absorbing incident medium makes the angle complex." : "")
            : "";
        SubstrateText = d?.Exit is { } sub
            ? $"Substrate: {Describe(sub)} — next to layer {Layers.Count}" : "";

        Raise(nameof(Layers));
        Raise(nameof(Operands));
        Raise(nameof(MeritRows));
        Raise(nameof(IsOpen));
        RefreshSpectrum();
        // BLOCKED: said in the status line - never a dialog, the person asked for nothing modal - and
        // after the spectrum, whose own refusal would otherwise be the last word.
        Blocked = e.Value.Blocked;
        if (Blocked is not null)
            Status = ThinFilmApi.IsMeritBlock(Blocked) ? $"Not evaluated: {Blocked}."
                : $"Not evaluated: {Blocked}. Choose another substrate in Edit > Materials.";
    }

    /// <summary>"SCHOTT N-BK7, n = 1.519", with a range when the index varies over the grid and k only
    /// when there is some.</summary>
    public static string Describe(MediumReport m)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        // A medium already named by its value ("n=1.520000") is not followed by the same value again.
        if (m.Name.StartsWith("n=", StringComparison.Ordinal)) return m.Name;
        string n = Math.Abs(m.NMax - m.NMin) < 5e-4
            ? string.Format(inv, "n = {0:F3}", m.NMin)
            : string.Format(inv, "n = {0:F3}–{1:F3}", m.NMin, m.NMax);
        string k = m.KMax <= 0.0 ? ""
            : m.KMax < 1e-3 ? string.Format(inv, ", k ≤ {0:G2}", m.KMax)
            : string.Format(inv, ", k ≤ {0:F3}", m.KMax);
        return $"{m.Name}, {n}{k}";
    }

    public static string Where(OperandPoints? at, string unit = "nm")
    {
        if (at is null) return "stack";
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        bool um = unit == "um";
        string u = um ? "µm" : "nm", f = um ? "0.####" : "0.#";
        double lo = um ? at.WavelengthMin / 1000.0 : at.WavelengthMin;
        double hi = um ? at.WavelengthMax / 1000.0 : at.WavelengthMax;
        string wl = at.WavelengthMax - at.WavelengthMin < 0.05
            ? lo.ToString(f, inv) + " " + u
            : lo.ToString(f, inv) + "–" + hi.ToString(f, inv) + " " + u;
        // Normal incidence is the default and says nothing; any other angle is worth the space.
        string angle = Math.Abs(at.AngleMax) < 1e-9 && Math.Abs(at.AngleMin) < 1e-9 ? ""
            : at.AngleMax - at.AngleMin < 0.05 ? string.Format(inv, ", {0:0.#}°", at.AngleMin)
            : string.Format(inv, ", {0:0.#}–{1:0.#}°", at.AngleMin, at.AngleMax);
        return wl + angle;
    }

    public static string DescribeOperand(OperandRow o)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string what = o.Code.ToUpperInvariant() switch
        {
            "RS" => "Reflectance, s-polarised", "RP" => "Reflectance, p-polarised",
            "RA" => "Reflectance, average of s and p",
            "TS" => "Transmittance, s-polarised", "TP" => "Transmittance, p-polarised",
            "TA" => "Transmittance into the substrate, average of s and p",
            "AS" => "Absorptance, s-polarised", "AP" => "Absorptance, p-polarised",
            "AA" => "Absorptance, average of s and p",
            "R_GD_S" or "R_GD_P" or "T_GD_S" or "T_GD_P" => string.Format(inv,
                "Group delay on {0}, {1}-polarised, in fs: dφ/dω, positive for a delay",
                o.Code[0] == 'R' ? "reflection" : "transmission", char.ToLowerInvariant(o.Code[^1])),
            "R_GDD_S" or "R_GDD_P" or "T_GDD_S" or "T_GDD_P" => string.Format(inv,
                "Group delay dispersion on {0}, {1}-polarised, in fs²: d²φ/dω²",
                o.Code[0] == 'R' ? "reflection" : "transmission", char.ToLowerInvariant(o.Code[^1])),
            "R_TOD_S" or "R_TOD_P" or "T_TOD_S" or "T_TOD_P" => string.Format(inv,
                "Third-order dispersion on {0}, {1}-polarised, in fs³: d³φ/dω³",
                o.Code[0] == 'R' ? "reflection" : "transmission", char.ToLowerInvariant(o.Code[^1])),
            // The band averages: one number for the whole band, weighted by a spectrum.
            "ASOL" => "Solar absorptance: A averaged over the band against AM1.5 global (ASTM G173-03)",
            "RSOL" => "Solar reflectance: R averaged over the band against AM1.5 global (ASTM G173-03)",
            "TSOL" => "Solar transmittance: T averaged over the band against AM1.5 global (ASTM G173-03)",
            "EMIT" => string.Format(inv,
                "Thermal emittance: A averaged over the band against a blackbody at {0:0.##} K " +
                "(by Kirchhoff, the emittance)", o.Temperature),
            "AERATIO" => string.Format(inv,
                "Solar absorptance over thermal emittance, ASOL / EMIT on the same points, " +
                "with the blackbody at {0:0.##} K", o.Temperature),
            "TOTT" => "Total physical thickness of the stack",
            "NLAY" => "Number of layers",
            // The stack operands carry their meaning in the threshold, which the table does not
            // show, so the sentence has to.
            "MINT" => string.Format(inv,
                "Thin-layer penalty: how far each layer falls below {0}, summed. " +
                "Zero means no layer is thinner than {0}", Lengths.Say(o.Threshold, o.LengthUnit)),
            "MAXT" => string.Format(inv,
                "Thick-layer penalty: how far each layer exceeds {0}, summed. " +
                "Zero means no layer is thicker than {0}", Lengths.Say(o.Threshold, o.LengthUnit)),
            _ => o.Code,
        };
        string held = o.Comparison.ToUpperInvariant() switch
        {
            "LE" => "at most", "GE" => "at least", "EQUAL" or "EQ" => "equal to",
            "RANGE" => "between", "NONE" => "", _ => o.Comparison,
        };
        string target = held.Length == 0 ? "not scored"
            : o.Comparison.ToUpperInvariant() == "RANGE"
                ? string.Format(inv, "held between {0:G4} and {1:G4}, weight {2:G3}", o.Target, o.TargetHi, o.Weight)
                : string.Format(inv, "held {0} {1:G4}, weight {2:G3}", held, o.Target, o.Weight);
        string where = o.At == "stack" ? "" : $" at {o.At}";
        // No met / not met: the user asked for rows not to be tallied as met, and the value is
        // in the row beside the target already.
        // The engine marks a row that used a material outside its valid range (C0.8); the table
        // does not show flags, so the help bar says it in words.
        string range = o.Flags.Contains("SYNTHETIC_INDEX", StringComparison.Ordinal)
            ? " Caution: a material is outside its valid range here, so this value rests on an "
              + "extrapolated index."
            : "";
        return $"{o.Number}. {what}{where}; {target}.{range}";
    }

    /// <summary>The legend's words for a quantity code. RA is what the API is asked for; "R (avg
    /// s,p)" is what it means.</summary>
    public static string QuantityLabel(string code) => ThinFilm.Api.SpectrumQuantities.Label(code);

    /// <summary>A palette, not a rainbow: these are read side by side and have to stay
    /// distinguishable when there are two of them, which is the usual case.</summary>
    public static readonly uint[] Palette =
        [0xFF1F77B4, 0xFFD62728, 0xFF2CA02C, 0xFFFF7F0E, 0xFF9467BD, 0xFF8C564B, 0xFFE377C2, 0xFF17BECF];

    /// <summary>A quantity's series in its axis's unit (an angle in degrees, not the engine's
    /// radians), drawn against that axis.</summary>
    public static PlotSeries QuantitySeries(string code, string name, double[] x, double[] values,
                                            uint colour, bool dashed = false)
    {
        var q = ThinFilm.Api.SpectrumQuantities.Find(code);
        double scale = q?.Scale ?? 1.0;
        var y = scale == 1.0 ? values : values.Select(v => v * scale).ToArray();
        string axis = q?.Axis ?? ThinFilm.Api.SpectrumQuantities.Fraction;
        if (axis == ThinFilm.Api.SpectrumQuantities.Degrees) (x, y) = BreakAtWraps(x, y);
        return new PlotSeries(name, x, y, colour, dashed, axis);
    }

    /// <summary>A phase or a Δ wraps from +180° to −180°. A jump of more than 180° between two
    /// points is that wrap, not the coating, so a NaN goes between them and the line breaks there
    /// instead of crossing the plot.</summary>
    public static (double[] X, double[] Y) BreakAtWraps(double[] x, double[] y)
    {
        var xs = new List<double>(x.Length);
        var ys = new List<double>(y.Length);
        for (int i = 0; i < Math.Min(x.Length, y.Length); ++i)
        {
            if (i > 0 && Math.Abs(y[i] - y[i - 1]) > 180.0)
            {
                xs.Add((x[i] + x[i - 1]) / 2.0);
                ys.Add(double.NaN);
            }
            xs.Add(x[i]);
            ys.Add(y[i]);
        }
        return (xs.ToArray(), ys.ToArray());
    }

    /// <summary>The 1-based layer a depth from the incident side falls in; the last layer takes
    /// its closing boundary, as the needle map does.</summary>
    private int LayerAtDepth(double depth)
    {
        double z = 0.0;
        for (int i = 0; i < Layers.Count; ++i)
        {
            z += Layers[i].Thickness;
            if (depth < z) return i + 1;
        }
        return Layers.Count;
    }

    private bool Failed(string? error, string? status)
    {
        Status = status is null ? (error ?? "Something went wrong.")
                                : $"{error} [{status}]";
        return false;
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Raise(name);
    }

    private void Raise(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
