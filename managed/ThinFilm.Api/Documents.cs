// Documents.cs — building documents: from a design, and for a report.
//
// The reporting document is the interesting one. A spectrum on an arbitrary grid is not the merit
// function, so it cannot be read off the operand points — and it must not be interpolated between
// them, because a coating's ripple lives between samples and interpolation is precisely how it
// disappears. So a second document is built with the requested points and its own operands, and
// the ENGINE evaluates it. That is what G6.1 settles: there is no target grid at the ABI, every
// point is an evaluation point, and how densely to sample is the caller's business.

using ThinFilm.Native;

namespace ThinFilm.Api;

public static class Documents
{
    /// <summary>
    /// A document matching a design whose SHAPE has changed — after a needle insertion or a layer
    /// removal, when the layer table no longer matches the document it came from.
    /// </summary>
    public static DesignDocument Rebuild(DesignDocument doc, Design design)
    {
        var spec = design.ToSpec();
        // THE SAME STACK, NEW THICKNESSES: every layer keeps its unit, pickup and vary flag.
        if (design.LayerCount == doc.Layers.Length
            && doc.Layers.Select(l => l.Material).SequenceEqual(spec.LayerMaterial ?? []))
        {
            // ...EXCEPT A LAYER THE RUN FIXED. Raising a sliver to the minimum fixes it there and
            // keeps the shape, and a design that still showed it varied would free it again at
            // the next refine. A pickup is never a variable, so it is left as it is.
            var varied = new HashSet<int>(spec.VariableLayer ?? []);
            var kept = doc.WithThickness(design.LayerThickness);
            var fixedNow = kept.Layers.Select((l, i) => l.Variable && l.Pickup is null && !varied.Contains(i)
                                                       ? l with { Variable = false } : l).ToArray();
            return kept with { Layers = fixedNow };
        }

        // THE VARY FLAG COMES FROM THE DESIGN, not a default: after a needle insertion the layer
        // count has changed and the document's flags no longer line up, but the design knows
        // which of its layers are variables. Defaulting every layer to variable here silently
        // freed every layer a user had fixed.
        //
        // A UNIT FOLLOWS THE MATERIAL: when every layer of a material named the same unit (an
        // aluminium layer kept in nm in a QWOT design), the layers of that material that come out
        // name it too; otherwise they follow the design's unit. Pickups name layers by position,
        // which a shape change scrambles, so the API refuses needle and evolve with pickups.
        var unitOf = doc.Layers.GroupBy(l => l.Material)
            .ToDictionary(g => g.Key, g => g.Select(l => l.Unit).Distinct().Count() == 1 ? g.First().Unit : null);
        var variable = new HashSet<int>(spec.VariableLayer ?? []);
        var layers = new LayerDocument[design.LayerCount];
        for (int i = 0; i < layers.Length; ++i)
            layers[i] = new LayerDocument(spec.LayerMaterial![i], design.LayerThickness[i],
                                          variable.Contains(i))
            { Unit = unitOf.GetValueOrDefault(spec.LayerMaterial![i]) };
        return doc with { Layers = layers };
    }

    /// <summary>
    /// A document over the requested grid, one operand per (quantity, point), ordered so that
    /// quantity q at point i is operand q * points + i — which is how the caller reads it back.
    /// </summary>
    public static DesignDocument ReportingDocument(DesignDocument source, SpectrumRequest request,
                                                   string[] quantities) =>
        ReportingDocument(source, request, quantities, out _);

    /// <summary>The request's grid: wavelengths in nm, or angles in degrees for a sweep in angle.</summary>
    public static double[] Grid(SpectrumRequest request)
    {
        int n = request.Steps;
        var x = new double[n];
        for (int i = 0; i < n; ++i)
            x[i] = request.From + (request.To - request.From) * i / (n - 1.0);
        return x;
    }

    /// <summary>
    /// As above, for a sweep in wavelength or in angle. A point whose angle cannot be reached in
    /// the incident medium (given in air, into a medium less dense than air) is left out, and
    /// reached[i] is false for it, so the operands are q * (points kept) + k for the k-th point
    /// kept. Refused when no point can be reached.
    /// </summary>
    public static DesignDocument ReportingDocument(DesignDocument source, SpectrumRequest request,
                                                   string[] quantities, out bool[] reached)
    {
        int n = request.Steps;
        var x = Grid(request);
        double[] wl = request.VsAngle ? new[] { request.Wavelength } : x;

        // THE MATERIALS ARE EVALUATED ON THE NEW GRID FROM THEIR MODELS - not interpolated between
        // the design's own grid points, which is what this did while materials were samples. A
        // dispersive material plotted from 400 to 800 nm is computed at every one of the plotted
        // wavelengths, and one plotted outside its valid range is flagged there (C0.8).
        var materials = new MaterialDocument[source.Materials.Length];
        for (int m = 0; m < materials.Length; ++m)
            materials[m] = source.Materials[m].OnGrid(source.Wavelength, wl);

        var incident = source.Incident.OnGrid(source.Wavelength, wl);
        var probe = source with { Wavelength = wl, Incident = incident };
        reached = new bool[n];
        var points = new List<PointDocument>(n);
        for (int i = 0; i < n; ++i)
        {
            var p = request.VsAngle ? new PointDocument(0, x[i]) : new PointDocument(i, request.AngleDegrees);
            if (!(reached[i] = probe.Reaches(p.AngleDegrees, p.Wavelength))) continue;
            points.Add(p);
        }
        if (points.Count == 0)
        {
            // Every point is out of reach; say why with the first one's reason.
            var p = request.VsAngle ? new PointDocument(0, x[0]) : new PointDocument(0, request.AngleDegrees);
            probe.MediumAngle(p.AngleDegrees, p.Wavelength);
        }

        var operands = new List<OperandDocument>(quantities.Length * points.Count);
        foreach (var q in quantities)
            for (int i = 0; i < points.Count; ++i)
                operands.Add(new OperandDocument(
                    Code: q, Comparison: "NONE", Target: 0.0, Weight: 1.0, Scale: 1.0,
                    Points: [i]));

        // A table of nothing but NONE is refused - there would be nothing to optimise - so one
        // scored operand rides along. It is the first quantity at the first point, scored against
        // itself with zero weight so it cannot change any number that is read back.
        operands[0] = operands[0] with { Comparison = "EQUAL", Weight = 0.0 };

        // A spectrum moves nothing, so its layers are fixed: a film opaque at every plotted
        // wavelength - 125 nm of silicon in the extreme ultraviolet - is then plotted, where as a
        // variable it was refused as having nothing to optimise.
        return source with
        {
            Name = source.Name + " (spectrum)",
            Wavelength = wl,
            Materials = materials,
            Layers = source.Layers.Select(l => l with { Variable = false }).ToArray(),
            Points = points.ToArray(),
            Incident = incident,
            Exit = source.Exit.OnGrid(source.Wavelength, wl),
            Operands = operands.ToArray(),
        };
    }

    // ------------------------------------------------------------------------------------

    /// <summary>
    /// A document with a new operand table, each operand given the (wavelength nm, angle degrees)
    /// points it reads. THE GRID AND THE POINTS ARE REBUILT FROM THE OPERANDS, because a row
    /// whose wavelength was edited to 555 nm needs a grid that has 555 in it: the grid becomes
    /// the sorted union of every wavelength a row reads, the points the distinct (wavelength,
    /// angle) pairs, and the materials and media are resampled onto the new grid. A table with
    /// no optical row at all keeps the old grid, since the materials still need one.
    /// </summary>
    public static DesignDocument WithOperands(
        DesignDocument doc, IReadOnlyList<(OperandDocument Op, (double Wl, double Angle)[] At)> rows)
    {
        static double Key(double v) => Math.Round(v, 9);

        var wls = rows.SelectMany(r => r.At.Select(a => Key(a.Wl))).Distinct().OrderBy(w => w)
                      .ToArray();
        if (wls.Length == 0) wls = (double[])doc.Wavelength.Clone();

        var wlIndex = new Dictionary<double, int>();
        for (int i = 0; i < wls.Length; ++i) wlIndex[wls[i]] = i;

        var points = new List<PointDocument>();
        var pointIndex = new Dictionary<(int, double), int>();
        var operands = new OperandDocument[rows.Count];
        for (int o = 0; o < rows.Count; ++o)
        {
            var at = rows[o].At;
            int[]? ps = at.Length == 0 ? null : new int[at.Length];
            for (int j = 0; j < at.Length; ++j)
            {
                var key = (wlIndex[Key(at[j].Wl)], Key(at[j].Angle));
                if (!pointIndex.TryGetValue(key, out int p))
                {
                    p = points.Count;
                    points.Add(new PointDocument(key.Item1, key.Item2));
                    pointIndex[key] = p;
                }
                ps![j] = p;
            }
            operands[o] = rows[o].Op with { Points = ps };
        }

        return doc with
        {
            Wavelength = wls,
            Materials = doc.Materials.Select(m => m.OnGrid(doc.Wavelength, wls)).ToArray(),
            Incident = doc.Incident.OnGrid(doc.Wavelength, wls),
            Exit = doc.Exit.OnGrid(doc.Wavelength, wls),
            Points = points.ToArray(),
            Operands = operands,
        };
    }

    /// <summary>The (wavelength nm, angle degrees) of every point an operand reads.</summary>
    public static (double Wl, double Angle)[] PointsOf(DesignDocument doc, OperandDocument op) =>
        (op.Points ?? []).Select(p => (doc.Wavelength[doc.Points[p].Wavelength],
                                       doc.Points[p].AngleDegrees)).ToArray();
}
