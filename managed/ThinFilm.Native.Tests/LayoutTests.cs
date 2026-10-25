// LayoutTests.cs — the managed mirror checked against the pins, not alongside them.
//
// tests/native/test_abi_frozen.cpp holds 249 pins: every struct size, every field offset and every
// enumerator value, measured on the commit that froze the ABI. This file reads THAT FILE — it is
// embedded in this assembly at build time — and checks the managed declarations against it.
//
// SO THERE IS ONE SOURCE OF TRUTH AND C# IS CHECKED AGAINST IT. The alternative, a second copy of
// the numbers written in C#, would pass on the day it was written and would be the thing that goes
// stale: somebody bumps the ABI, updates the native pins because the native test failed, and never
// learns that the managed copy disagrees until a caller reads the wrong bytes.
//
// The check runs in both directions. Every pin must have a managed counterpart with the same
// number, AND every pinned field must exist — a mirror that quietly omitted a field would
// otherwise pass every offset it did declare.

using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using ThinFilm.Native;

namespace ThinFilm.Native.Tests;

public static class LayoutTests
{
    private static string ReadPins()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var s = asm.GetManifestResourceStream("abi_pins.cpp")
            ?? throw new InvalidOperationException(
                "tests/native/test_abi_frozen.cpp is not embedded in this assembly. Without it " +
                "there is nothing to check the managed layout against, and passing would mean " +
                "nothing — see ThinFilm.Native.Tests.csproj.");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    /// <summary>tf_problem -> TfProblem. Mechanical, so no table can go stale.</summary>
    private static string ManagedTypeName(string cName) =>
        string.Concat(cName.Split('_').Select(p => p.Length == 0 ? p : char.ToUpperInvariant(p[0]) + p[1..]));

    private static Type? FindStruct(string cName)
    {
        var want = ManagedTypeName(cName);
        return typeof(TfProblem).Assembly.GetTypes().FirstOrDefault(t => t.Name == want);
    }

    public static void EveryStructHasTheFrozenLayout(Harness h)
    {
        h.Section("every struct size and field offset matches the pins in test_abi_frozen.cpp");

        var text = ReadPins();

        // pin_size(sizeof(tf_problem), 184, "tf_problem");
        var sizes = Regex.Matches(text, @"pin_size\(sizeof\((\w+)\),\s*(\d+),");
        h.Expect(sizes.Count > 0, "the embedded pin file actually contains size pins");

        var seenTypes = new HashSet<string>();
        foreach (Match m in sizes)
        {
            var cName = m.Groups[1].Value;
            var want = long.Parse(m.Groups[2].Value);
            seenTypes.Add(cName);

            var t = FindStruct(cName);
            if (t is null)
            {
                h.Fail($"{cName} is pinned in the ABI but has no managed mirror " +
                       $"(expected a struct named {ManagedTypeName(cName)})");
                continue;
            }

            long got = Marshal.SizeOf(t);
            h.Pin(got, want, $"sizeof({t.Name})");
        }

        // pin(offsetof(tf_problem, wavelength), 48, "tf_problem.wavelength");
        var offsets = Regex.Matches(text, @"pin\(offsetof\((\w+),\s*(\w+)\),\s*(\d+),");
        h.Expect(offsets.Count > 0, "and offset pins");

        foreach (Match m in offsets)
        {
            var cName = m.Groups[1].Value;
            var field = m.Groups[2].Value;
            var want = long.Parse(m.Groups[3].Value);

            var t = FindStruct(cName);
            if (t is null) continue;   // already reported by the size pass

            if (t.GetField(field, BindingFlags.Public | BindingFlags.Instance) is null)
            {
                h.Fail($"{t.Name} has no field named '{field}', which the frozen ABI says is at " +
                       $"offset {want}. The C field names are mirrored verbatim on purpose.");
                continue;
            }

            long got = (long)Marshal.OffsetOf(t, field);
            h.Pin(got, want, $"{t.Name}.{field}");
        }

        h.Note($"{sizes.Count} struct sizes and {offsets.Count} field offsets checked against the pins");
    }

    /// <summary>
    /// THE BLITTABILITY PROOF. Marshal.SizeOf is the size the marshaller would use; sizeof(T) is
    /// the size the runtime lays out. They agree exactly when no transformation is needed, which is
    /// the definition of blittable — so this compares the two rather than asserting the property.
    /// </summary>
    public static unsafe void EveryStructIsBlittable(Harness h)
    {
        h.Section("no struct needs marshalling: the managed and native layouts are one layout");

        void Check<T>() where T : unmanaged
        {
            h.Pin(sizeof(T), Marshal.SizeOf<T>(),
                  $"sizeof({typeof(T).Name}) == Marshal.SizeOf<{typeof(T).Name}>()");
        }

        Check<TfComplex>();
        Check<TfMatrix2>();
        Check<TfMatrix2s>();
        Check<TfConfigurations>();
        Check<TfProblem>();
        Check<TfOperands>();
        Check<TfVariables>();
        Check<TfPlanReport>();
        Check<TfPlanBuffers>();
        Check<TfWorkspace>();
        Check<TfOutput>();
        Check<TfPlanSizes>();
        Check<TfSpectralOutput>();
        Check<TfNeedleRequest>();
        Check<TfNeedleOutput>();
        Check<TfIndexOutput>();
    }

    public static void EveryEnumeratorKeepsItsNumber(Harness h)
    {
        h.Section("every status, target code and reduction has the number the ABI froze");

        var text = ReadPins();
        var values = new Dictionary<string, long>();

        // Every enum member of the binding assembly, by its C name.
        foreach (var t in typeof(TfStatus).Assembly.GetTypes().Where(t => t.IsEnum))
            foreach (var name in Enum.GetNames(t))
                values[name] = Convert.ToInt64(Enum.Parse(t, name));

        // And the loose #defines.
        foreach (var f in typeof(TfAbi).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (f.IsLiteral && f.GetRawConstantValue() is int iv)
                values[f.Name] = iv;

        // pin(TF_OK, 0, "TF_OK");
        var pins = Regex.Matches(text, @"pin\((TF_\w+),\s*(-?\d+),");
        h.Expect(pins.Count > 0, "the embedded pin file contains enumerator pins");

        int missing = 0;
        foreach (Match m in pins)
        {
            var name = m.Groups[1].Value;
            var want = long.Parse(m.Groups[2].Value);

            if (!values.TryGetValue(name, out var got))
            {
                ++missing;
                h.Fail($"{name} is part of the frozen ABI and the managed binding does not declare it");
                continue;
            }
            h.Pin(got, want, name);
        }

        h.Note($"{pins.Count} enumerators checked, {missing} missing from the binding");
    }

    /// <summary>
    /// The header, the pins and the loaded binary must all name the same ABI. A managed assembly
    /// and a native library that disagree about the layout is the one failure mode that produces
    /// plausible numbers rather than an error, so it is checked before anything else runs.
    /// </summary>
    public static void TheBinaryAndTheBindingAgreeOnTheAbi(Harness h)
    {
        h.Section("the loaded library, the binding and the pins name the same ABI version");

        var text = ReadPins();
        var m = Regex.Match(text, @"pin\(TF_ABI_VERSION,\s*(\d+),");
        h.Expect(m.Success, "the pins state an ABI version");
        var pinned = long.Parse(m.Groups[1].Value);

        h.Pin(TfAbi.TF_ABI_VERSION, pinned, "the binding's TF_ABI_VERSION matches the pins");
        h.Pin(NativeMethods.tf_abi_version(), pinned, "and so does the loaded native library");
    }
}
