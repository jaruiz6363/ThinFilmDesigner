// DesignTests.cs — X1.2: the layer that owns every buffer.
//
// THE CLAIM THIS FILE EXISTS TO MEASURE is the allocation contract's own sentence: "Step 5 happens
// in the inner loop and must not allocate." That is testable rather than arguable —
// GC.GetAllocatedBytesForCurrentThread() before and after a few thousand evaluations — and a
// design that quietly allocates one object per call would still pass every value test ever
// written while making the optimiser it was built for unusable.
//
// The rest checks that owning the buffers did not change any answer: Design must reproduce, to the
// last bit, what the hand-wired sequence in CallTests produces. The moment those two disagree, one
// of them is wrong about a stride and the values are the only thing that will say so.

using ThinFilm.Native;

namespace ThinFilm.Native.Tests;

public static class DesignTests
{
    /// <summary>R for an uncoated n = 1.52 substrate in air at normal incidence — the same hand
    /// answer CallTests uses, so the two paths are compared against one constant and not against
    /// each other's mistakes.</summary>
    private const double BareSubstrateReflectance = 0.042579994960947345;

    private static DesignSpec BareSubstrate() => new()
    {
        MaterialCount = 1,
        MaterialIndex = [new TfComplex(1.46, 0.0)],
        Wavelength = [550.0],
        PointWavelength = [0],
        PointAngle = [0.0],
        IndexIncident = [new TfComplex(1.0, 0.0)],
        IndexExit = [new TfComplex(1.52, 0.0)],
        Operands = new OperandSpec
        {
            Code = [(int)TfTargetCode.TF_TARGET_RS],
            Point = [0],
            Target = [0.0],
            Weight = [1.0],
        },
    };

    /// <summary>Two lossless layers on glass, three wavelengths, three variables.</summary>
    private static DesignSpec TwoLayerStack(double k = 0.0) => new()
    {
        LayerThickness = [92.0, 140.0, 61.0],
        LayerMaterial = [0, 1, 0],
        MaterialCount = 2,
        MaterialIndex =
        [
            new TfComplex(2.35, k), new TfComplex(2.35, k), new TfComplex(2.35, k),
            new TfComplex(1.46, 0.0), new TfComplex(1.46, 0.0), new TfComplex(1.46, 0.0),
        ],
        Wavelength = [450.0, 550.0, 650.0],
        PointWavelength = [0, 1, 2],
        PointAngle = [0.0, 0.0, 0.0],
        IndexIncident = [new TfComplex(1.0, 0.0), new TfComplex(1.0, 0.0), new TfComplex(1.0, 0.0)],
        IndexExit = [new TfComplex(1.52, 0.0), new TfComplex(1.52, 0.0), new TfComplex(1.52, 0.0)],
        Operands = new OperandSpec
        {
            Code = [(int)TfTargetCode.TF_TARGET_RS, (int)TfTargetCode.TF_TARGET_RS,
                    (int)TfTargetCode.TF_TARGET_RS],
            Point = [0, 1, 2],
            Target = [0.0, 0.0, 0.0],
            Weight = [1.0, 1.0, 1.0],
        },
        VariableKind =
        [
            (int)TfVariableKind.TF_VARIABLE_LAYER_THICKNESS,
            (int)TfVariableKind.TF_VARIABLE_LAYER_THICKNESS,
            (int)TfVariableKind.TF_VARIABLE_LAYER_THICKNESS,
        ],
        VariableLayer = [0, 1, 2],
        VariableMaterial = [0, 0, 0],
        VariableCoefficient = [0, 0, 0],
    };

    public static void ADesignReproducesTheHandWiredAnswer(Harness h)
    {
        h.Section("Design gives the same number the hand-wired sequence does");

        var d = new Design(BareSubstrate());
        var rep = d.Plan();
        h.Pin(rep.n_residuals, 1, "one residual");
        h.Pin(rep.n_structured, 1, "one structured point");

        d.Evaluate();
        h.PinExact(d.Value[0], BareSubstrateReflectance, "R through Design");
        h.PinExact(d.Residual[0], BareSubstrateReflectance, "and its residual");
    }

    /// <summary>
    /// THE ONE THAT MATTERS. The allocation contract says the inner loop must not allocate; this
    /// counts the bytes. A Design that allocated one small object per evaluation would pass every
    /// other test in this file and would put the collector in the middle of the optimiser.
    /// </summary>
    public static void TheInnerLoopAllocatesNothing(Harness h)
    {
        h.Section("ten thousand evaluations allocate exactly zero bytes");

        var d = new Design(TwoLayerStack());
        d.Plan();

        // Warm up: the first call through each path JITs, and JIT allocates. What is being
        // measured is the steady state, which is what an optimiser spends its time in.
        for (int i = 0; i < 64; ++i) { d.LayerThickness[0] = 92.0 + i * 1e-3; d.Evaluate(); }
        for (int i = 0; i < 64; ++i) { d.LayerThickness[0] = 92.0 + i * 1e-3; d.EvaluateJacobian(); }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; ++i)
        {
            d.LayerThickness[0] = 92.0 + (i % 100) * 0.01;
            d.Evaluate();
        }
        long afterEvaluate = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < 10_000; ++i)
        {
            d.LayerThickness[1] = 140.0 + (i % 100) * 0.01;
            d.EvaluateJacobian();
        }
        long afterJacobian = GC.GetAllocatedBytesForCurrentThread();

        h.Pin(afterEvaluate - before, 0, "bytes allocated by 10,000 Evaluate calls");
        h.Pin(afterJacobian - afterEvaluate, 0, "bytes allocated by 10,000 EvaluateJacobian calls");

        // And the loop actually did something, so the zero above is not the zero of a no-op.
        h.Expect(d.Merit > 0.0, "the loop produced a real merit, so it was not optimised away");
    }

    public static void MovingAThicknessNeedsNoReplan(Harness h)
    {
        h.Section("a thickness moves, the answer moves, and nothing is re-planned or re-allocated");

        var a = new Design(TwoLayerStack());
        a.Plan();
        a.Evaluate();
        double before = a.Value[0];

        a.LayerThickness[1] = 175.0;
        a.Evaluate();
        double after = a.Value[0];
        h.Expect(before != after, "changing a layer changed the answer");

        // The same stack built from scratch with that thickness must give the identical number.
        // If the in-place path and the fresh path disagreed, one of them is reading stale memory.
        var spec = TwoLayerStack();
        spec.LayerThickness![1] = 175.0;
        var b = new Design(spec);
        b.Plan();
        b.Evaluate();

        h.PinExact(after, b.Value[0], "and matches a Design built with that thickness from the start");
        h.PinExact(a.Merit, b.Merit, "merit likewise");
    }

    /// <summary>
    /// TF_PLAN_STALE is declared in the header and raised nowhere, so nothing in the engine refuses
    /// an evaluation against a plan whose assumptions expired. They can: a layer driven thick
    /// enough to absorb everything clamps, which changes both the path decision and which
    /// variables are inert. RefreshPlan is the managed answer, and this measures that it works in
    /// both directions — quiet when nothing moved, loud when something did.
    /// </summary>
    public static void RefreshPlanNoticesWhenTheGroundMoves(Harness h)
    {
        h.Section("RefreshPlan is quiet for an ordinary move and loud when a layer goes opaque");

        var d = new Design(TwoLayerStack(k: 0.5));   // absorbing, so thickness can drive clamping
        var rep = d.Plan();
        h.Pin(rep.max_clamped_layers, 0, "nothing is clamped to begin with");

        d.LayerThickness[0] = 120.0;
        h.Expect(!d.RefreshPlan(), "an ordinary thickness change moves no plan decision");

        // 2*pi*d*k/lambda > 24 is the clamp, so with k = 0.5 at 450 nm that is d > ~3400 nm.
        d.LayerThickness[0] = 50_000.0;
        h.Expect(d.RefreshPlan(),
                 "a layer driven opaque DOES move one, and the engine would not have said so");
        h.Expect(d.Report.max_clamped_layers > 0, "and the report now shows the clamp");

        // Having noticed, the plan is current again.
        h.Expect(!d.RefreshPlan(), "and refreshing again is quiet, the plan now matching the stack");
    }

    public static void EveryBufferIsSizedByTheEngine(Harness h)
    {
        h.Section("the plan's numbers decide the buffer sizes, not arithmetic in C#");

        var d = new Design(TwoLayerStack());
        var rep = d.Plan();

        h.Pin(d.Residual.Length, rep.n_residuals, "the residual buffer is n_residuals long");
        h.Pin(d.Jacobian.Length, (long)rep.n_residuals * d.VariableCount,
              "the Jacobian is n_residuals * n_variables, column-major");
        h.Pin(d.Value.Length, d.OperandCount, "one value per operand, scored or not");
        h.Pin(d.PointPath.Length, d.PointCount, "one path per point");
        h.Pin(d.VariableInert.Length, d.VariableCount, "one inert flag per variable");

        d.EvaluateJacobian();
        h.Expect(rep.scratch_doubles > 0, "the plan asked for scratch and the Jacobian used it");
    }

    /// <summary>
    /// A column of the Jacobian belongs to a variable — that is what column-major means — so an
    /// inert variable has an all-zero column. Structural, not physical: nothing here recomputes a
    /// derivative, which would be the second implementation the X1.1-X1.3 rule forbids.
    /// </summary>
    public static void AnInertVariableOwnsAZeroColumn(Harness h)
    {
        h.Section("an inert variable's Jacobian column is exactly zero, which locates the columns");

        var spec = TwoLayerStack(k: 0.5);
        spec.LayerThickness![0] = 50_000.0;    // opaque: nothing behind it can be seen
        var d = new Design(spec);
        var rep = d.Plan();

        h.Expect(rep.n_variables_inert > 0, "the opaque layer made at least one variable inert");
        d.EvaluateJacobian();

        int nres = rep.n_residuals;
        for (int v = 0; v < d.VariableCount; ++v)
        {
            if (d.VariableInert[v] == 0) continue;
            bool allZero = true;
            for (int r = 0; r < nres; ++r)
                if (d.Jacobian[v * nres + r] != 0.0) allZero = false;
            h.Expect(allZero, $"variable {v} is inert, so its whole column is zero");
        }
    }

    public static void AShapeMistakeIsRefusedBeforeAnythingIsAllocated(Harness h)
    {
        h.Section("a malformed spec is refused by name, not discovered as a wrong number");

        void Refuses(Func<DesignSpec> make, string what)
        {
            try { _ = new Design(make()); h.Fail($"{what} was accepted"); }
            catch (ArgumentException) { h.Expect(true, what); }
        }

        Refuses(() =>
        {
            var s = BareSubstrate();
            return new DesignSpec
            {
                MaterialCount = 2,                       // says two, supplies one
                MaterialIndex = s.MaterialIndex,
                Wavelength = s.Wavelength,
                PointWavelength = s.PointWavelength,
                PointAngle = s.PointAngle,
                IndexIncident = s.IndexIncident,
                IndexExit = s.IndexExit,
                Operands = s.Operands,
            };
        }, "a MaterialIndex table that is not [n_materials * n_wavelengths]");

        Refuses(() =>
        {
            var s = BareSubstrate();
            return new DesignSpec
            {
                MaterialCount = 1,
                MaterialIndex = s.MaterialIndex,
                Wavelength = s.Wavelength,
                PointWavelength = s.PointWavelength,
                PointAngle = s.PointAngle,
                IndexIncident = s.IndexIncident,
                IndexExit = s.IndexExit,
                Operands = new OperandSpec
                {
                    Code = [(int)TfTargetCode.TF_TARGET_RS],
                    Point = [0],                          // pointwise...
                    PointCount = [1],                     // ...and span
                    PointFirst = [0],
                    PointIndex = [0],
                    Target = [0.0],
                    Weight = [1.0],
                },
            };
        }, "an operand table that is both pointwise and span");

        Refuses(() =>
        {
            var s = BareSubstrate();
            return new DesignSpec
            {
                MaterialCount = 1,
                MaterialIndex = s.MaterialIndex,
                Wavelength = s.Wavelength,
                PointWavelength = s.PointWavelength,
                PointAngle = s.PointAngle,
                Operands = s.Operands,                    // no media at all
            };
        }, "the one-configuration form with no media");
    }

    public static void ANativeRefusalArrivesAsAnException(Harness h)
    {
        h.Section("a refusal from the engine becomes an exception carrying its status");

        var s = BareSubstrate();
        var spec = new DesignSpec
        {
            MaterialCount = 1,
            MaterialIndex = s.MaterialIndex,
            Wavelength = s.Wavelength,
            PointWavelength = s.PointWavelength,
            PointAngle = s.PointAngle,
            IndexIncident = s.IndexIncident,
            IndexExit = s.IndexExit,
            Operands = new OperandSpec
            {
                Code = [(int)TfTargetCode.TF_TARGET_STRESS],   // with no stress table
                Point = [0],
                Target = [0.0],
                Weight = [1.0],
            },
        };

        var d = new Design(spec);
        try
        {
            d.Plan();
            h.Fail("a STRESS operand with no stress table should not have planned");
        }
        catch (ThinFilmException e)
        {
            h.ExpectStatus(e.Status, TfStatus.TF_NO_STRESS_DATA,
                           "the exception carries the engine's own status");
        }
    }

    public static void EvaluatingBeforePlanningIsRefused(Harness h)
    {
        h.Section("evaluating before planning is refused, since no buffer has been sized yet");

        var d = new Design(BareSubstrate());
        try { d.Evaluate(); h.Fail("Evaluate before Plan was accepted"); }
        catch (InvalidOperationException) { h.Expect(true, "Evaluate before Plan is refused"); }
    }

    /// <summary>
    /// THE NEEDLE MAP'S OWN BUFFERS LIVE UNTIL THE ENGINE IS DONE WITH THEM. NeedleMap allocates
    /// its depths, candidates, residual and status per call, hands the engine their addresses and
    /// then never mentions them again - so once the JIT has optimised it, those buffers were dead
    /// from the collector's point of view for the whole of tf_evaluate_needle. A collection from
    /// any other thread in that window freed them while the engine read and wrote them, and the
    /// next pinned allocation found a corrupt heap. The needle stage died that way at ~145 layers
    /// (2026-10-24, Al2O3/SiO2 notch): "Internal CLR error" in Design.Plan, two calls after the
    /// damage.
    ///
    /// So the collector is made to run constantly beside a stream of maps, and every collection is
    /// followed by pinned arrays of the same sizes, filled with a pattern that is valid but WRONG
    /// as input - depth ~0 and candidate 1 - so a freed buffer is reused at once and its reuse
    /// shows in the map rather than as a wild read. Each map must be the reference to the bit,
    /// and no array of the pattern may have been written into.
    /// </summary>
    public static void ANeedleMapKeepsItsBuffersAliveThroughTheCall(Harness h)
    {
        h.Section("a needle map survives the collector running on another thread throughout");

        // Enough layers and points that one call takes long enough for collections to land in it.
        const int layers = 40, points = 120;
        var thickness = new double[layers];
        var material = new int[layers];
        for (int i = 0; i < layers; ++i) { thickness[i] = 80.0 + 3.0 * (i % 7); material[i] = i % 2; }
        var wl = new double[points];
        var index = new TfComplex[2 * points];
        var air = new TfComplex[points];
        var glass = new TfComplex[points];
        var pw = new int[points];
        var code = new int[points];
        var target = new double[points];
        var weight = new double[points];
        for (int i = 0; i < points; ++i)
        {
            wl[i] = 420.0 + 2.0 * i;
            index[i] = new TfComplex(1.65, 0.0);            // material 0
            index[points + i] = new TfComplex(1.46, 0.0);   // material 1
            air[i] = new TfComplex(1.0, 0.0);
            glass[i] = new TfComplex(1.52, 0.0);
            pw[i] = i;
            code[i] = (int)TfTargetCode.TF_TARGET_TS;
            target[i] = 1.0;
            weight[i] = 1.0;
        }
        var d = new Design(new DesignSpec
        {
            LayerThickness = thickness,
            LayerMaterial = material,
            MaterialCount = 2,
            MaterialIndex = index,
            Wavelength = wl,
            PointWavelength = pw,
            PointAngle = new double[points],
            IndexIncident = air,
            IndexExit = glass,
            Operands = new OperandSpec { Code = code, Point = pw, Target = target, Weight = weight },
        });
        d.Plan();

        double total = thickness.Sum();
        var depths = new double[64];
        for (int i = 0; i < depths.Length; ++i) depths[i] = total * i / (depths.Length - 1.0);
        int[] candidates = [0, 1];
        var reference = d.NeedleMap(depths, candidates);

        // Depth 2e-314 nm and candidate 1, whichever way the bytes are read: a reused buffer is
        // still a legal request, so the engine answers it and the answer is visibly not the map.
        double decoy = BitConverter.Int64BitsToDouble(0x0000_0001_0000_0001);
        long overwritten = 0;
        bool stop = false;
        var collector = new Thread(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                GC.Collect();
                var held = new double[8][];
                for (int k = 0; k < held.Length; ++k)
                {
                    held[k] = GC.AllocateArray<double>(k % 2 == 0 ? depths.Length : points, pinned: true);
                    held[k].AsSpan().Fill(decoy);
                }
                Thread.Yield();
                foreach (var a in held)
                    foreach (var x in a)
                        if (BitConverter.DoubleToInt64Bits(x) != BitConverter.DoubleToInt64Bits(decoy))
                        { Interlocked.Increment(ref overwritten); break; }
            }
        }) { IsBackground = true };
        collector.Start();

        // TIME, NOT A COUNT: the bug needs the optimised NeedleMap, and tiered compilation
        // promotes it only after enough calls and a quiet moment. Two seconds covers both.
        int calls = 0, wrong = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            while (clock.ElapsedMilliseconds < 2000)
            {
                var map = d.NeedleMap(depths, candidates);
                ++calls;
                for (int i = 0; i < map.Length; ++i)
                    if (BitConverter.DoubleToInt64Bits(map[i]) != BitConverter.DoubleToInt64Bits(reference[i]))
                    { ++wrong; break; }
            }
        }
        catch (ThinFilmException e) { h.Fail($"a map was refused under collection: {e.Message}"); }
        finally
        {
            Volatile.Write(ref stop, true);
            collector.Join();
        }

        h.Pin(wrong, 0, $"maps that differ from the reference, of {calls}");
        h.Pin(Interlocked.Read(ref overwritten), 0, "pinned arrays the engine wrote into after they were freed");
    }
}
