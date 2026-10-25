// DesignDocument.cs — the serialisable form of a design, and the one translation to the ABI's.
//
// WHY THIS IS NOT JUST DesignSpec. DesignSpec mirrors the ABI: flat arrays, material-major index
// tables, counts implied by lengths. That is exactly right for handing to the engine and exactly
// wrong for a file somebody opens, diffs, or writes by hand — a four-layer design over forty
// wavelengths stores its two materials as eighty interleaved doubles with no names on them.
//
// So there are two forms and ONE translation between them, here, tested by round-trip. Two
// translations would be two chances to get a stride wrong; the X1.2 header says the same thing
// about why DesignSpec does not model anything, and this is the other side of that line.
//
// THE ROUND-TRIP IS EXACT OR IT IS NOTHING. A document that reloads to a design that evaluates to
// a slightly different number is worse than one that fails to load: the failure is visible and the
// drift is not. The test asserts bit-identical merit across save, serialise, deserialise, load.

using System.Text.Json;
using System.Text.Json.Serialization;
using ThinFilm.Native;

namespace ThinFilm.Api;

/// <summary>A material as a document states it: a name and its optical constants per wavelength.</summary>
///
/// N and K are the material evaluated on the design's grid: what the engine is handed. Model is
/// what the material IS (MaterialModel.cs), and when it is present N and K are always computed from
/// it - on loading, and whenever the grid changes - so the two cannot disagree. A file written
/// before models existed has no Model, and gains one when it is opened (DesignDocument.Normalise).
public sealed record MaterialDocument(string Name, double[] N, double[] K)
{
    public MaterialModel? Model { get; init; }

    /// <summary>A constant-index material over a wavelength grid.</summary>
    public static MaterialDocument Constant(string name, double n, double k, int wavelengths)
    {
        var ns = new double[wavelengths];
        var ks = new double[wavelengths];
        System.Array.Fill(ns, n);
        System.Array.Fill(ks, k);
        return new MaterialDocument(name, ns, ks) { Model = MaterialModel.Constant(n, k) };
    }

    /// <summary>A material from its model, evaluated on <paramref name="gridNm"/>.</summary>
    public static MaterialDocument FromModel(string name, MaterialModel model, double[] gridNm)
    {
        var n = new double[gridNm.Length];
        var k = new double[gridNm.Length];
        for (int i = 0; i < gridNm.Length; ++i) (n[i], k[i], _) = model.At(gridNm[i], name);
        return new MaterialDocument(name, n, k) { Model = model };
    }

    /// <summary>This material on another grid: evaluated from its model, never interpolated
    /// between the old grid's points.</summary>
    public MaterialDocument OnGrid(double[] fromNm, double[] toNm) =>
        FromModel(Name, Model ?? MaterialModel.FromSamples(fromNm, N, K), toNm);

    /// <summary>This material's dn/dλ, d²n/dλ² and d³n/dλ³ on <paramref name="gridNm"/>, per nm,
    /// nm² and nm³, in the ABI's n + ik, for group delay, GDD (ABI 11) and TOD (ABI 12).
    /// <paramref name="transparent"/> zeroes the k terms, for the incident medium, which is sent with
    /// k = 0.</summary>
    public (TfComplex[] D1, TfComplex[] D2, TfComplex[] D3) SlopesOn(double[] gridNm, bool transparent = false)
    {
        var model = Model ?? MaterialModel.FromSamples(gridNm, N, K);
        var d1 = new TfComplex[gridNm.Length];
        var d2 = new TfComplex[gridNm.Length];
        var d3 = new TfComplex[gridNm.Length];
        for (int i = 0; i < gridNm.Length; ++i)
        {
            var s = model.Slopes(gridNm[i], Name);
            d1[i] = new TfComplex(s.Dn, transparent ? 0.0 : s.Dk);
            d2[i] = new TfComplex(s.D2n, transparent ? 0.0 : s.D2k);
            d3[i] = new TfComplex(s.D3n, transparent ? 0.0 : s.D3k);
        }
        return (d1, d2, d3);
    }

    /// <summary>Which grid wavelengths fall outside the model's valid range, 1 per wavelength.</summary>
    public int[] ExtrapolatedOn(double[] gridNm)
    {
        var model = Model ?? MaterialModel.FromSamples(gridNm, N, K);
        return gridNm.Select(w => model.At(w, Name).Extrapolated ? 1 : 0).ToArray();
    }
}

/// <summary>One layer. Thickness is ALWAYS physical, in nanometres; Unit is how a person reads it
/// (LayerUnits.cs) - "nm", "QWOT" or "FWOT", or null to follow the design's ThicknessUnit. A layer
/// with a Pickup takes its thickness from another layer and is never a variable itself.
/// Min and Max bound the thickness the optimiser may give the layer, in nanometres like Thickness;
/// null is no bound on that side (LayerUnits.cs, "BOUNDS").</summary>
public sealed record LayerDocument(int Material, double Thickness, bool Variable = true)
{
    public string? Unit { get; init; }
    public LayerPickup? Pickup { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
}

public sealed record PointDocument(int Wavelength, double AngleDegrees);

/// <summary>One row of the merit function, in the terms a user would write it.</summary>
public sealed record OperandDocument(
    string Code,
    string Comparison,
    double Target,
    double Weight,
    double Scale = 1.0,
    double Tolerance = 0.0,
    double TargetHi = 0.0,
    int[]? Points = null,
    string Reduction = "SUM",
    double Sharpness = 0.0,
    double Threshold = 0.0,
    double Temperature = 0.0);

/// <summary>One optimisation run in a design's history. Started is local time, ISO 8601; the merits
/// are the weighted RMS the engine reports; Result is "kept" or "reverted".</summary>
public sealed record RunRecord(
    string Started, string Method, string Settings, double StartMerit, double EndMerit,
    double ElapsedSeconds, string Outcome, string Result);

/// <summary>The Spectrum tab as a design was saved with it: against wavelength (From, To, Steps in
/// nm, at Angle degrees) or, VsAngle, against angle (AngleFrom, AngleTo, AngleSteps, at Wavelength
/// nm), and the quantity codes plotted ("RA", "TS", ...). Both axes' settings are kept, so switching
/// the axis after opening finds the other one as it was.</summary>
public sealed record PlotDocument(
    double From, double To, int Steps, double Angle, string[] Quantities,
    bool VsAngle = false, double AngleFrom = 0.0, double AngleTo = 89.0, int AngleSteps = 90,
    double Wavelength = 550.0)
{
    /// <summary>Why these settings cannot be plotted, or null.</summary>
    public string? Problem()
    {
        if (!(From > 0.0 && To > From)) return "the wavelength range must be positive and increasing";
        if (Steps < 2 || AngleSteps < 2) return "a spectrum needs at least two steps";
        if (!(Angle >= 0.0 && Angle < 90.0)) return "the angle of incidence must be at least 0° and below 90°";
        if (!(AngleFrom >= 0.0 && AngleTo < 90.0 && AngleTo > AngleFrom)) return "the angle range must be increasing, from 0° up to below 90°";
        if (!(Wavelength > 0.0)) return "the wavelength must be positive";
        if (Quantities is not { Length: > 0 }) return "choose at least one quantity to plot";
        foreach (var q in Quantities)
            if (SpectrumQuantities.Find(q) is null) return $"'{q}' is not a quantity a spectrum plots";
        return null;
    }
}

/// <summary>The Spectrum tab's settings before a design says otherwise.</summary>
public static class PlotDefaults
{
    public static readonly PlotDocument Plot = new(400.0, 800.0, 241, 0.0, ["RA", "TA"]);
}

public sealed record DesignDocument
{
    /// <summary>Bumped when this format changes in a way an old file cannot survive.</summary>
    public int Version { get; init; } = CurrentVersion;

    public string Name { get; init; } = "untitled";

    /// <summary>Where the design came from, in one line: a paper, a book's figure, a vendor's
    /// sheet. Shown with Notes in the Design tab's Description; null in a file that has none.</summary>
    public string? Source { get; init; }

    /// <summary>What the design is for, what was changed from its source, its caveats.</summary>
    public string? Notes { get; init; }

    public required double[] Wavelength { get; init; }
    public required MaterialDocument[] Materials { get; init; }
    public required LayerDocument[] Layers { get; init; }
    public required PointDocument[] Points { get; init; }

    /// <summary>Incident and exit media, by the same MaterialDocument shape.</summary>
    public required MaterialDocument Incident { get; init; }
    public required MaterialDocument Exit { get; init; }

    public required OperandDocument[] Operands { get; init; }

    public string Norm { get; init; } = "L2";
    /// <summary>No longer used: an unconstrained layer has no minimum but zero (the user,
    /// 2026-10-16). Kept so older files still read.</summary>
    public double LowerBound { get; init; } = 1.0;

    /// <summary>The maximum for unconstrained layers, nm: the ceiling the optimiser gives a layer
    /// with no maximum of its own. Shown and set in Edit > System Parameters.</summary>
    public double UpperBound { get; init; } = 600.0;

    /// <summary>
    /// How a person wants wavelengths SHOWN: "nm" or "um". A display preference, saved with the
    /// design so a file reopens the way it was left. Every wavelength in this document is in
    /// nanometres whatever this says; only front ends convert.
    /// </summary>
    public string WavelengthUnit { get; init; } = "nm";

    /// <summary>The unit a layer is shown in unless it names its own: "nm", "QWOT" or "FWOT".</summary>
    public string ThicknessUnit { get; init; } = "nm";

    /// <summary>
    /// The wavelength (nm) QWOT and FWOT are counted at. Unlike the two units above it is part of
    /// the design: changing it moves every QWOT and FWOT layer (LayerUnits.cs). A file written
    /// before it existed gets the middle of its wavelength grid, where its layers are all in nm.
    /// </summary>
    public double ReferenceWavelength { get; init; }

    /// <summary>Every optimisation run on this design that was kept or reverted, oldest first, so a
    /// design file carries how it was reached. Null in a file that has none.</summary>
    public RunRecord[]? History { get; init; }

    /// <summary>The spectrum the design is looked at with: what a paper plots, over its range.
    /// Applied when the design is opened; null in a file that has none, which leaves the Spectrum
    /// tab as it was.</summary>
    public PlotDocument? Plot { get; init; }

    // ---- Edit > System Parameters: how the design is evaluated ----------------------------

    /// <summary>Include the substrate's uncoated back surface in R and T. The substrate is then
    /// SubstrateThicknessMm thick, incoherent, with the incident medium behind it; its k
    /// attenuates each pass. Psi, Delta and the phases always describe the coated surface alone.</summary>
    public bool BackSurface { get; init; }

    /// <summary>Substrate thickness in millimetres. Used only with BackSurface, and it matters only
    /// when the substrate absorbs.</summary>
    public double SubstrateThicknessMm { get; init; } = 1.0;

    /// <summary>Angles of incidence are given in air rather than in the incident medium. In air,
    /// sin(angle) / n0 is the sine in the medium. Off, the default, an angle is the angle in the
    /// incident medium itself, which is what a cube beam splitter needs: 45 degrees in glass
    /// cannot be reached from air.</summary>
    public bool AngleInAir { get; init; }

    // ------------------------------------------------------------------------------------

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static DesignDocument FromJson(string json) =>
        (JsonSerializer.Deserialize<DesignDocument>(json, Json)
         ?? throw new FormatException("the document deserialised to null")).Normalise();

    /// <summary>
    /// Every material with a model, and its values recomputed from that model on this grid. A
    /// file written before models existed (version 1) has samples only: each gains the model its
    /// samples imply - CONSTANT when they agree, which is what those designs meant, TABLE
    /// otherwise. A hand-edited file whose N and K disagree with its model is corrected to the
    /// model, because the model is the definition and the samples are only its evaluation.
    /// </summary>
    public DesignDocument Normalise() => this with
    {
        Version = CurrentVersion,
        Materials = Materials.Select(Reachable).ToArray(),
        Incident = Reachable(Incident),
        Exit = Reachable(Exit),
        ReferenceWavelength = ReferenceWavelength > 0.0 || Wavelength.Length == 0
            ? ReferenceWavelength
            : Math.Round((Wavelength.Min() + Wavelength.Max()) / 2.0),
    };

    /// <summary>2: materials carry their models (MaterialModel). 1: samples only.</summary>
    public const int CurrentVersion = 2;

    /// <summary>
    /// A material on this grid - or, if it names something not here (a catalogue glass with no
    /// catalogue), exactly as the file holds it. The design still opens; ToSpec then refuses with
    /// MaterialUnavailableException, and the API holds the design blocked until it can be evaluated.
    /// The values the file held are never used in the meantime: that would be a silent substitute.
    /// </summary>
    private MaterialDocument Reachable(MaterialDocument m)
    {
        try { return m.OnGrid(Wavelength, Wavelength); }
        catch (MaterialUnavailableException) { return m; }
    }

    private static int[]? AnyMarked(int[] marks) => marks.Any(v => v != 0) ? marks : null;

    /// <summary>An angle of incidence as the engine takes it: radians, in the incident medium. With
    /// AngleInAir the angle is converted by Snell's law at the point's wavelength.</summary>
    /// <summary>Said whenever the incident medium has a k, which every calculation treats as 0;
    /// null when it has none.</summary>
    public string? IncidentKWarning =>
        Incident.K.Any(k => k != 0.0)
            ? $"the incident medium {Incident.Name} has k up to {Incident.K.Max():G2}; it is treated as "
              + "0 in every calculation, since the angle in an absorbing incident medium is complex"
            : null;

    /// <summary>Whether MediumAngle can convert this angle: always, unless it is given in air and
    /// the incident medium is less dense than air at that wavelength.</summary>
    public bool Reaches(double degrees, int wavelength) =>
        !AngleInAir || Math.Abs(Math.Sin(degrees * Math.PI / 180.0) / Incident.N[wavelength]) <= 1.0;

    public double MediumAngle(double degrees, int wavelength)
    {
        double radians = degrees * Math.PI / 180.0;
        if (!AngleInAir) return radians;
        double n0 = Incident.N[wavelength];
        double s = Math.Sin(radians) / n0;
        if (Math.Abs(s) > 1.0)
            throw new FormatException(
                $"{degrees:0.##}° in air cannot be reached in {Incident.Name} (n = {n0:0.####}) at " +
                $"{Wavelength[wavelength]:0.##} nm; give the angle in the incident medium instead " +
                "(Edit > System Parameters)");
        return Math.Asin(s);
    }

    // ------------------------------------------------------------------------------------

    /// <summary>
    /// THE ONE TRANSLATION. Everything that knows how the ABI lays arrays out is here, so a stride
    /// mistake has one place to be rather than one place per front end.
    /// </summary>
    public DesignSpec ToSpec()
    {
        int nw = Wavelength.Length;
        if (nw == 0) throw new FormatException("a design needs at least one wavelength");
        if (Materials.Length == 0) throw new FormatException("a design needs at least one material");
        if (Points.Length == 0) throw new FormatException("a design needs at least one point");
        if (Operands.Length == 0) throw new FormatException("a design needs at least one operand");
        if (BackSurface && !(SubstrateThicknessMm > 0.0 && double.IsFinite(SubstrateThicknessMm)))
            throw new FormatException("the substrate thickness must be a positive number of millimetres " +
                                      "when the back surface is included");

        foreach (var m in Materials)
            if (m.N.Length != nw || m.K.Length != nw)
                throw new FormatException(
                    $"material '{m.Name}' has {m.N.Length} index values for {nw} wavelengths");

        // MATERIAL-MAJOR, which is the stride the ABI documents and the one thing this method
        // exists to get right exactly once. The ABI takes n + ik with k >= 0 absorbing (tf_layer.h),
        // so K goes in as it is; until X1.42 it went in negated, which made every absorber a gain
        // medium.
        var index = new TfComplex[Materials.Length * nw];
        for (int m = 0; m < Materials.Length; ++m)
            for (int w = 0; w < nw; ++w)
                index[m * nw + w] = new TfComplex(Materials[m].N[w], Materials[m].K[w]);

        var inc = new TfComplex[nw];
        var exi = new TfComplex[nw];
        for (int w = 0; w < nw; ++w)
        {
            // THE INCIDENT MEDIUM IS SENT TRANSPARENT. The engine needs a real eta0, or the angle in
            // it is complex and R + T + A = 1 fails. The document keeps the medium's own k (a
            // reversed stack puts the substrate here); IncidentKWarning says it is not used.
            inc[w] = new TfComplex(Incident.N[w], 0.0);
            exi[w] = new TfComplex(Exit.N[w], Exit.K[w]);
        }

        var thickness = new double[Layers.Length];
        var material = new int[Layers.Length];
        var variableLayers = new List<int>();
        for (int i = 0; i < Layers.Length; ++i)
        {
            thickness[i] = Layers[i].Thickness;
            material[i] = Layers[i].Material;
            if (material[i] < 0 || material[i] >= Materials.Length)
                throw new FormatException($"layer {i + 1} names material {material[i]}, which does not exist");
            // A picked-up layer follows its parent, so it is never a variable of its own.
            if (Layers[i].Variable && Layers[i].Pickup is null) variableLayers.Add(i);
        }

        // Pickups become the engine's ties, in nanometres (LayerUnits.Ties).
        int[]? tieParent = null;
        double[]? tieScale = null, tieOffset = null;
        if (Layers.Any(l => l.Pickup is not null))
        {
            if (LayerUnits.Problem(this) is { } problem) throw new FormatException(problem);
            (tieParent, tieScale, tieOffset) = LayerUnits.Ties(this);
        }

        var kind = new int[variableLayers.Count];
        var layerOf = new int[variableLayers.Count];
        for (int i = 0; i < variableLayers.Count; ++i)
        {
            kind[i] = (int)TfVariableKind.TF_VARIABLE_LAYER_THICKNESS;
            layerOf[i] = variableLayers[i];
        }

        // AN AERATIO ROW IS A RATIO OF TWO ROWS NOBODY TYPED: an ASOL and an EMIT over its points,
        // unscored, appended after the document's own rows so that row o of the document is still
        // operand o of the engine.
        var ratios = Operands.Select((d, o) => (d, o))
                             .Where(x => x.d.Code.Trim().Equals("AERATIO", StringComparison.OrdinalIgnoreCase))
                             .ToArray();
        var rows = Operands.Concat(ratios.SelectMany(x => new[]
        {
            x.d with { Code = "ASOL", Comparison = "NONE", Target = 0.0, Weight = 0.0, Scale = 1.0, Tolerance = 0.0 },
            x.d with { Code = "EMIT", Comparison = "NONE", Target = 0.0, Weight = 0.0, Scale = 1.0, Tolerance = 0.0 },
        })).ToArray();
        int n = rows.Length;

        var code = new int[n];
        var comparison = new int[n];
        var target = new double[n];
        var targetHi = new double[n];
        var weight = new double[n];
        var scale = new double[n];
        var tolerance = new double[n];
        var reduction = new int[n];
        var reductionParam = new double[n * 2];
        var pointFirst = new int[n];
        var pointCount = new int[n];
        var pointIndex = new List<int>();
        var pointWeight = new List<double>();
        bool weighted = false;

        for (int o = 0; o < n; ++o)
        {
            var d = rows[o];
            code[o] = Codes.Target(d.Code);
            comparison[o] = Codes.Comparison(d.Comparison);
            target[o] = d.Target;
            targetHi[o] = d.TargetHi;
            weight[o] = d.Weight;
            scale[o] = d.Scale;
            tolerance[o] = d.Tolerance;
            reduction[o] = Codes.IsBand(d.Code) ? (int)TfReduction.TF_REDUCE_SUM : Codes.Reduction(d.Reduction);

            // A stack operand takes its threshold where a span takes its sharpness: the ABI gives
            // reduction_param[0] both jobs, because an operand with no span has no sharpness.
            reductionParam[o * 2] = Codes.IsStack(code[o]) ? d.Threshold : Codes.IsBand(d.Code) ? 0.0 : d.Sharpness;
            reductionParam[o * 2 + 1] = 0.0;

            pointFirst[o] = pointIndex.Count;
            if (Codes.IsStack(code[o]) || code[o] == (int)TfTargetCode.TF_TARGET_RATIO)
            {
                pointCount[o] = 0;
            }
            else
            {
                var pts = d.Points ?? Enumerable.Range(0, Points.Length).ToArray();
                foreach (var p in pts)
                    if (p < 0 || p >= Points.Length)
                        throw new FormatException($"operand {o} names point {p}, which does not exist");
                pointCount[o] = pts.Length;
                pointIndex.AddRange(pts);
                if (Codes.IsBand(d.Code))
                {
                    var w = Spectra.Weights(d.Code, pts.Select(p => Wavelength[Points[p].Wavelength]).ToArray(),
                                            d.Temperature, out var problem)
                            ?? throw new FormatException($"operand {o} ({d.Code}): {problem}");
                    pointWeight.AddRange(w);
                    weighted = true;
                }
                else if (pts.Length > 1 && reduction[o] == (int)TfReduction.TF_REDUCE_SUM)
                {
                    // A SPAN OF AN ORDINARY CODE IS ITS AVERAGE, as a band row is: "RA at most 1%
                    // from 500 to 600 nm" means the mean reflectance there. Summed, eleven points
                    // of 0.65 read 7.2 against a 1% target (the user, 2026-10-25, chose the mean).
                    pointWeight.AddRange(Enumerable.Repeat(1.0 / pts.Length, pts.Length));
                    weighted = true;
                }
                else pointWeight.AddRange(Enumerable.Repeat(1.0, pts.Length));
            }
        }

        int[]? inputFirst = null, inputCount = null, inputIndex = null;
        if (ratios.Length > 0)
        {
            inputFirst = new int[n];
            inputCount = new int[n];
            inputIndex = new int[ratios.Length * 2];
            for (int k = 0; k < ratios.Length; ++k)
            {
                int o = ratios[k].o;
                inputFirst[o] = 2 * k;
                inputCount[o] = 2;
                inputIndex[2 * k] = Operands.Length + 2 * k;
                inputIndex[2 * k + 1] = Operands.Length + 2 * k + 1;
            }
        }

        // GROUP DELAY, GDD AND TOD read the media's dispersion (ABI 11, 12): dn/dλ and d²n/dλ²,
        // and d³n/dλ³ for TOD, from each material's model. Sent only when such an operand is
        // present - the third derivative only with a TOD row - so every other design is handed
        // exactly what it was before.
        TfComplex[]? dIndex = null, d2Index = null, d3Index = null, dInc = null, d2Inc = null, d3Inc = null,
                     dExi = null, d2Exi = null, d3Exi = null;
        if (code.Any(Codes.IsDispersion))
        {
            bool third = code.Any(Codes.IsThirdOrder);
            dIndex = new TfComplex[Materials.Length * nw];
            d2Index = new TfComplex[Materials.Length * nw];
            if (third) d3Index = new TfComplex[Materials.Length * nw];
            for (int m = 0; m < Materials.Length; ++m)
            {
                var (d1, d2, d3) = Materials[m].SlopesOn(Wavelength);
                System.Array.Copy(d1, 0, dIndex, m * nw, nw);
                System.Array.Copy(d2, 0, d2Index, m * nw, nw);
                if (third) System.Array.Copy(d3, 0, d3Index!, m * nw, nw);
            }
            (dInc, d2Inc, d3Inc) = Incident.SlopesOn(Wavelength, transparent: true);
            (dExi, d2Exi, d3Exi) = Exit.SlopesOn(Wavelength);
            if (!third) d3Inc = d3Exi = null;
        }

        return new DesignSpec
        {
            MaterialDIndexDWavelength = dIndex,
            MaterialD2IndexDWavelength2 = d2Index,
            MaterialD3IndexDWavelength3 = d3Index,
            DIndexIncidentDWavelength = dInc,
            D2IndexIncidentDWavelength2 = d2Inc,
            DIndexExitDWavelength = dExi,
            D2IndexExitDWavelength2 = d2Exi,
            D3IndexIncidentDWavelength3 = d3Inc,
            D3IndexExitDWavelength3 = d3Exi,
            LayerThickness = thickness,
            LayerMaterial = material,
            MaterialCount = Materials.Length,
            MaterialIndex = index,
            Wavelength = (double[])Wavelength.Clone(),
            PointWavelength = Points.Select(p => p.Wavelength).ToArray(),
            PointAngle = Points.Select(p => MediumAngle(p.AngleDegrees, p.Wavelength)).ToArray(),
            SubstrateThickness = BackSurface ? SubstrateThicknessMm * 1e6 : 0.0,
            IndexIncident = inc,
            IndexExit = exi,
            // Out-of-range marks, from each material's model (C0.8, M3.4). Null when nothing is out
            // of range, so a design entirely inside its materials' ranges is handed exactly what it
            // was handed before models existed.
            MaterialIndexIsExtrapolated = AnyMarked(Materials.SelectMany(m => m.ExtrapolatedOn(Wavelength)).ToArray()),
            IndexIncidentIsExtrapolated = AnyMarked(Incident.ExtrapolatedOn(Wavelength)),
            IndexExitIsExtrapolated = AnyMarked(Exit.ExtrapolatedOn(Wavelength)),
            Operands = new OperandSpec
            {
                Code = code,
                Comparison = comparison,
                Target = target,
                TargetHi = targetHi,
                Weight = weight,
                Scale = scale,
                Tolerance = tolerance,
                Reduction = reduction,
                ReductionParam = reductionParam,
                PointFirst = pointFirst,
                PointCount = pointCount,
                PointIndex = pointIndex.ToArray(),
                // Weights only when a band row needs them, so every other design is handed what
                // it always was.
                PointWeight = weighted ? pointWeight.ToArray() : null,
                InputFirst = inputFirst,
                InputCount = inputCount,
                InputIndex = inputIndex,
                // A Delta target is missed by the shortest arc to it: -179° against a target of
                // +179° is 2° out, not 358°, and the merit has no jump at ±180°. Plain subtraction
                // printed the engine's branch-cut warning on every Delta row.
                DeltaConvention = (int)TfDeltaConvention.TF_DELTA_SHORTEST_ARC,
            },
            VariableKind = kind,
            VariableLayer = layerOf,
            VariableMaterial = new int[variableLayers.Count],
            VariableCoefficient = new int[variableLayers.Count],
            LayerTieParent = tieParent,
            LayerTieScale = tieScale,
            LayerTieOffset = tieOffset,
        };
    }

    /// <summary>The same document with new thicknesses — what an optimisation produces.</summary>
    public DesignDocument WithThickness(ReadOnlySpan<double> thickness)
    {
        if (thickness.Length != Layers.Length)
            throw new ArgumentException(
                $"expected {Layers.Length} thicknesses, got {thickness.Length}", nameof(thickness));
        var layers = new LayerDocument[Layers.Length];
        for (int i = 0; i < Layers.Length; ++i)
            layers[i] = Layers[i] with { Thickness = thickness[i] };
        return this with { Layers = layers };
    }
}
