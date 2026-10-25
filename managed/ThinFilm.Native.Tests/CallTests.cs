// CallTests.cs — proof that the binding reaches the real engine, with the right fields.
//
// A layout test can pass while every call still fails, and a call can succeed while returning
// garbage: a pointer aimed one field early produces numbers, not a crash. So this does the whole
// sequence the ABI requires — ask for the buffer sizes, allocate them in C#, pin them, fill the
// structs, plan, evaluate — and checks the answer against a number that did not come from C#.
//
// THE GOLDEN IS FRESNEL AT NORMAL INCIDENCE ON A BARE SUBSTRATE, which is the one case in this
// whole engine whose answer can be written down by hand: R = ((1 - 1.52) / (1 + 1.52))^2. It is a
// literal below, not an expression — the rule for X1.1-X1.3 is that no number is computed in C#
// that could have been computed in C++, and that applies to tests as much as to the wrapper.
// Checking against a constant is a check; evaluating the formula here would be a second
// implementation, however small.
//
// Agreement on that one number means the managed struct really did put layer counts, media, the
// point table and the operand table where the engine looks for them. Fixture-level agreement with
// the native suite across the whole operand library is X1.3, which is its own row.

using ThinFilm.Native;

namespace ThinFilm.Native.Tests;

public static unsafe class CallTests
{
    /// <summary>
    /// R for an uncoated n = 1.52 substrate in air at normal incidence, to the last bit a double
    /// holds. Anyone can check it with a calculator; nothing in C# computes it.
    /// </summary>
    private const double BareSubstrateReflectance = 0.042579994960947345;

    public static void ABareSubstrateGivesTheFresnelAnswer(Harness h)
    {
        h.Section("the whole sequence: size, allocate, pin, plan, evaluate — against a hand answer");

        // ---- inputs, allocated and pinned in C#. The native side allocates nothing. --------
        var wavelength = PinnedBuffer<double>.From(550.0);
        var pointWavelength = PinnedBuffer<int>.From(0);
        var pointAngle = PinnedBuffer<double>.From(0.0);
        var incident = PinnedBuffer<TfComplex>.From(new TfComplex(1.00, 0.0));
        var exit = PinnedBuffer<TfComplex>.From(new TfComplex(1.52, 0.0));
        var materialIndex = PinnedBuffer<TfComplex>.From(new TfComplex(1.46, 0.0));

        var code = PinnedBuffer<int>.From((int)TfTargetCode.TF_TARGET_RS);
        var point = PinnedBuffer<int>.From(0);
        var target = PinnedBuffer<double>.From(0.0);
        var weight = PinnedBuffer<double>.From(1.0);

        var problem = new TfProblem
        {
            n_layers = 0,                 // a bare substrate: two media and no film
            n_materials = 1,              // unused by a stack with no layers, but a count of zero
                                          // is refused, and rightly — see TF_BAD_COUNT
            n_wavelengths = 1,
            n_points = 1,
            material_index = materialIndex.Pointer,
            wavelength = wavelength.Pointer,
            point_wavelength = pointWavelength.Pointer,
            point_angle = pointAngle.Pointer,
            index_incident = incident.Pointer,
            index_exit = exit.Pointer,
        };

        var operands = new TfOperands
        {
            n_operands = 1,
            delta_convention = (int)TfDeltaConvention.TF_DELTA_PRINCIPAL,
            extrapolation_policy = (int)TfExtrapolationPolicy.TF_EXTRAPOLATION_FLAG,
            code = code.Pointer,
            point = point.Pointer,
            target = target.Pointer,
            weight = weight.Pointer,
        };

        var variables = new TfVariables { n_variables = 0 };

        // ---- the sizes come from the engine, not from arithmetic here ----------------------
        TfPlanSizes sizes;
        var st = NativeMethods.tf_query_plan_sizes(
            problem.n_points, variables.n_variables, operands.n_span, operands.n_operands, &sizes);
        h.ExpectStatus(st, TfStatus.TF_OK, "tf_query_plan_sizes");

        var pointPath = new PinnedBuffer<int>(sizes.point_path_count);
        var pointOrder = new PinnedBuffer<int>(sizes.point_order_count);
        var variableInert = new PinnedBuffer<int>(sizes.variable_inert_count);
        var pointPolarisation = new PinnedBuffer<int>(sizes.point_polarisation_count);
        var spanFirst = new PinnedBuffer<int>(sizes.point_span_first_count);
        var spanCount = new PinnedBuffer<int>(sizes.point_span_count_count);
        var spanOperand = new PinnedBuffer<int>(sizes.point_span_operand_count);
        var spanSlot = new PinnedBuffer<int>(sizes.point_span_slot_count);
        var operandResidual = new PinnedBuffer<int>(sizes.operand_residual_count);
        var operandOrder = new PinnedBuffer<int>(sizes.operand_order_count);
        var operandLevel = new PinnedBuffer<int>(sizes.operand_level_count);

        var report = new PinnedBuffer<TfPlanReport>(1);
        var planStatus = new PinnedBuffer<int>(1);

        var plan = new TfPlanBuffers
        {
            point_path = pointPath.Pointer,
            point_order = pointOrder.Pointer,
            variable_inert = variableInert.Pointer,
            operand_residual = operandResidual.Pointer,
            operand_order = operandOrder.Pointer,
            operand_level = operandLevel.Pointer,
            point_span_first = spanFirst.Pointer,
            point_span_count = spanCount.Pointer,
            point_span_operand = spanOperand.Pointer,
            point_span_slot = spanSlot.Pointer,
            point_polarisation = pointPolarisation.Pointer,
            report = report.Pointer,
            status = planStatus.Pointer,
        };

        st = NativeMethods.tf_plan(&problem, &operands, &variables, 1, &plan);
        h.ExpectStatus(st, TfStatus.TF_OK, "tf_plan");
        h.ExpectStatus((TfStatus)planStatus.Span[0], TfStatus.TF_OK, "and the plan's own status");

        var rep = report.Span[0];
        h.Pin(rep.n_residuals, 1, "the plan reports one residual");
        h.Pin(rep.n_structured, 1, "and one point on the structured path, both media being lossless");

        // ---- evaluate ----------------------------------------------------------------------
        var scratch = new PinnedBuffer<double>(rep.scratch_doubles);
        var residual = new PinnedBuffer<double>(rep.n_residuals);
        var value = new PinnedBuffer<double>(operands.n_operands);
        var merit = new PinnedBuffer<double>(1);
        var evalStatus = new PinnedBuffer<int>(1);

        var workspace = new TfWorkspace
        {
            scratch = scratch.Pointer,
            scratch_doubles = rep.scratch_doubles,
        };

        var output = new TfOutput
        {
            residual = residual.Pointer,
            jacobian = null,             // tf_evaluate wants no Jacobian, and says so with null
            value = value.Pointer,
            merit = merit.Pointer,
            status = evalStatus.Pointer,
        };

        st = NativeMethods.tf_evaluate(&problem, &operands, &plan, &workspace, &output);
        h.ExpectStatus(st, TfStatus.TF_OK, "tf_evaluate");

        // THE CHECK THE WHOLE FILE IS FOR. Exact: the engine computes in double and so does the
        // hand calculation, and there is no summation order for a single interface to disagree
        // about. A tolerance here would hide the pointer-off-by-one-field failure this catches.
        h.PinExact(value.Span[0], BareSubstrateReflectance,
                   "R of a bare 1.52 substrate, through the binding");

        // The residual of an EQUAL target of zero with weight one is the value itself.
        h.PinExact(residual.Span[0], BareSubstrateReflectance, "and the residual it produced");
    }

    /// <summary>
    /// The same call twice must give the same bytes. Not a physics check — a memory check: a
    /// buffer that was read before it was written, or a scratch that the engine assumed zeroed,
    /// shows up here as a second answer that differs from the first.
    /// </summary>
    public static void TheSameInputGivesTheSameBytesTwice(Harness h)
    {
        h.Section("evaluating twice through the binding returns identical bits");

        var first = new List<double>();
        var second = new List<double>();

        for (int pass = 0; pass < 2; ++pass)
        {
            var sink = pass == 0 ? first : second;

            var wavelength = PinnedBuffer<double>.From(450.0, 550.0, 650.0);
            var pointWavelength = PinnedBuffer<int>.From(0, 1, 2);
            var pointAngle = PinnedBuffer<double>.From(0.0, 0.3, 0.6);
            var incident = PinnedBuffer<TfComplex>.From(
                new TfComplex(1.0, 0.0), new TfComplex(1.0, 0.0), new TfComplex(1.0, 0.0));
            var exit = PinnedBuffer<TfComplex>.From(
                new TfComplex(1.52, 0.0), new TfComplex(1.52, 0.0), new TfComplex(1.52, 0.0));
            var materialIndex = PinnedBuffer<TfComplex>.From(
                new TfComplex(2.35, 0.0), new TfComplex(2.35, 0.0), new TfComplex(2.35, 0.0),
                new TfComplex(1.46, 0.0), new TfComplex(1.46, 0.0), new TfComplex(1.46, 0.0));
            var thickness = PinnedBuffer<double>.From(92.0, 140.0, 61.0);
            var layerMaterial = PinnedBuffer<int>.From(0, 1, 0);

            var code = PinnedBuffer<int>.From(
                (int)TfTargetCode.TF_TARGET_RS, (int)TfTargetCode.TF_TARGET_TP);
            var point = PinnedBuffer<int>.From(0, 2);
            var target = PinnedBuffer<double>.From(0.0, 1.0);
            var weight = PinnedBuffer<double>.From(1.0, 2.0);

            var problem = new TfProblem
            {
                n_layers = 3,
                n_materials = 2,
                n_wavelengths = 3,
                n_points = 3,
                layer_thickness = thickness.Pointer,
                layer_material = layerMaterial.Pointer,
                material_index = materialIndex.Pointer,
                wavelength = wavelength.Pointer,
                point_wavelength = pointWavelength.Pointer,
                point_angle = pointAngle.Pointer,
                index_incident = incident.Pointer,
                index_exit = exit.Pointer,
            };

            var operands = new TfOperands
            {
                n_operands = 2,
                code = code.Pointer,
                point = point.Pointer,
                target = target.Pointer,
                weight = weight.Pointer,
            };

            var variables = new TfVariables { n_variables = 0 };

            TfPlanSizes sizes;
            h.ExpectStatus(
                NativeMethods.tf_query_plan_sizes(3, 0, 0, 2, &sizes),
                TfStatus.TF_OK, "sizes");

            var pointPath = new PinnedBuffer<int>(sizes.point_path_count);
            var pointOrder = new PinnedBuffer<int>(sizes.point_order_count);
            var variableInert = new PinnedBuffer<int>(sizes.variable_inert_count);
            var pointPolarisation = new PinnedBuffer<int>(sizes.point_polarisation_count);
            var spanFirst = new PinnedBuffer<int>(sizes.point_span_first_count);
            var spanCount = new PinnedBuffer<int>(sizes.point_span_count_count);
            var spanOperand = new PinnedBuffer<int>(sizes.point_span_operand_count);
            var spanSlot = new PinnedBuffer<int>(sizes.point_span_slot_count);
            var operandResidual = new PinnedBuffer<int>(sizes.operand_residual_count);
            var operandOrder = new PinnedBuffer<int>(sizes.operand_order_count);
            var operandLevel = new PinnedBuffer<int>(sizes.operand_level_count);
            var report = new PinnedBuffer<TfPlanReport>(1);
            var planStatus = new PinnedBuffer<int>(1);

            var plan = new TfPlanBuffers
            {
                point_path = pointPath.Pointer,
                point_order = pointOrder.Pointer,
                variable_inert = variableInert.Pointer,
                operand_residual = operandResidual.Pointer,
                operand_order = operandOrder.Pointer,
                operand_level = operandLevel.Pointer,
                point_span_first = spanFirst.Pointer,
                point_span_count = spanCount.Pointer,
                point_span_operand = spanOperand.Pointer,
                point_span_slot = spanSlot.Pointer,
                point_polarisation = pointPolarisation.Pointer,
                report = report.Pointer,
                status = planStatus.Pointer,
            };

            h.ExpectStatus(NativeMethods.tf_plan(&problem, &operands, &variables, 1, &plan),
                           TfStatus.TF_OK, "plan");

            var rep = report.Span[0];
            var scratch = new PinnedBuffer<double>(rep.scratch_doubles);
            var residual = new PinnedBuffer<double>(rep.n_residuals);
            var value = new PinnedBuffer<double>(2);
            var merit = new PinnedBuffer<double>(1);
            var evalStatus = new PinnedBuffer<int>(1);

            var workspace = new TfWorkspace { scratch = scratch.Pointer, scratch_doubles = rep.scratch_doubles };
            var output = new TfOutput
            {
                residual = residual.Pointer,
                value = value.Pointer,
                merit = merit.Pointer,
                status = evalStatus.Pointer,
            };

            h.ExpectStatus(NativeMethods.tf_evaluate(&problem, &operands, &plan, &workspace, &output),
                           TfStatus.TF_OK, "evaluate");

            sink.Add(value.Span[0]);
            sink.Add(value.Span[1]);
            sink.Add(merit.Span[0]);

            // A coating on glass reflects something, and not everything. If this fixture ever
            // degenerated into zeros the identity check below would still pass, and would mean
            // nothing — so the fixture is checked for being alive.
            if (pass == 0)
            {
                h.Expect(value.Span[0] > 0.0 && value.Span[0] < 1.0,
                         "the fixture produces a real reflectance, so the comparison has content");
                h.Expect(merit.Span[0] > 0.0, "and a non-zero merit");
            }
        }

        for (int i = 0; i < first.Count; ++i)
            h.PinExact(second[i], first[i], $"value {i} is bit-identical on the second call");
    }

    /// <summary>
    /// The native side reports caller mistakes by number rather than by crashing, and the binding
    /// has to carry those numbers through unchanged. A wrapper that swallowed them — or mapped
    /// them onto a generic failure — would turn a precise diagnosis into a shrug.
    /// </summary>
    public static void ARefusalArrivesAsItsOwnStatus(Harness h)
    {
        h.Section("a refusal crosses the boundary as the number the engine chose");

        var wavelength = PinnedBuffer<double>.From(550.0);
        var pointWavelength = PinnedBuffer<int>.From(0);
        var pointAngle = PinnedBuffer<double>.From(0.0);
        var incident = PinnedBuffer<TfComplex>.From(new TfComplex(1.0, 0.0));
        var exit = PinnedBuffer<TfComplex>.From(new TfComplex(1.52, 0.0));
        var materialIndex = PinnedBuffer<TfComplex>.From(new TfComplex(1.46, 0.0));

        // A STRESS operand with no stress table: TF_NO_STRESS_DATA, refused at plan time.
        var code = PinnedBuffer<int>.From((int)TfTargetCode.TF_TARGET_STRESS);
        var point = PinnedBuffer<int>.From(0);
        var target = PinnedBuffer<double>.From(0.0);
        var weight = PinnedBuffer<double>.From(1.0);

        var problem = new TfProblem
        {
            n_layers = 0,
            n_materials = 1,
            n_wavelengths = 1,
            n_points = 1,
            material_index = materialIndex.Pointer,
            wavelength = wavelength.Pointer,
            point_wavelength = pointWavelength.Pointer,
            point_angle = pointAngle.Pointer,
            index_incident = incident.Pointer,
            index_exit = exit.Pointer,
            material_stress = null,      // the thing being refused
        };

        var operands = new TfOperands
        {
            n_operands = 1,
            code = code.Pointer,
            point = point.Pointer,
            target = target.Pointer,
            weight = weight.Pointer,
        };
        var variables = new TfVariables { n_variables = 0 };

        TfPlanSizes sizes;
        NativeMethods.tf_query_plan_sizes(1, 0, 0, 1, &sizes);

        var pointPath = new PinnedBuffer<int>(sizes.point_path_count);
        var pointOrder = new PinnedBuffer<int>(sizes.point_order_count);
        var variableInert = new PinnedBuffer<int>(sizes.variable_inert_count);
        var pointPolarisation = new PinnedBuffer<int>(sizes.point_polarisation_count);
        var spanFirst = new PinnedBuffer<int>(sizes.point_span_first_count);
        var spanCount = new PinnedBuffer<int>(sizes.point_span_count_count);
        var spanOperand = new PinnedBuffer<int>(sizes.point_span_operand_count);
        var spanSlot = new PinnedBuffer<int>(sizes.point_span_slot_count);
        var operandResidual = new PinnedBuffer<int>(sizes.operand_residual_count);
        var operandOrder = new PinnedBuffer<int>(sizes.operand_order_count);
        var operandLevel = new PinnedBuffer<int>(sizes.operand_level_count);
        var report = new PinnedBuffer<TfPlanReport>(1);
        var planStatus = new PinnedBuffer<int>(1);

        var plan = new TfPlanBuffers
        {
            point_path = pointPath.Pointer,
            point_order = pointOrder.Pointer,
            variable_inert = variableInert.Pointer,
            operand_residual = operandResidual.Pointer,
            operand_order = operandOrder.Pointer,
            operand_level = operandLevel.Pointer,
            point_span_first = spanFirst.Pointer,
            point_span_count = spanCount.Pointer,
            point_span_operand = spanOperand.Pointer,
            point_span_slot = spanSlot.Pointer,
            point_polarisation = pointPolarisation.Pointer,
            report = report.Pointer,
            status = planStatus.Pointer,
        };

        var st = NativeMethods.tf_plan(&problem, &operands, &variables, 1, &plan);
        h.ExpectStatus(st, TfStatus.TF_NO_STRESS_DATA,
                       "a STRESS operand with no stress table is refused by name");
        h.ExpectStatus((TfStatus)planStatus.Span[0], TfStatus.TF_NO_STRESS_DATA,
                       "and the status buffer carries the same number");
    }
}
