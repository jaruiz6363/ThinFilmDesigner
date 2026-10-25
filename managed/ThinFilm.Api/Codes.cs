// Codes.cs — names to numbers, and the reason a document does not store numbers.
//
// The ABI is frozen, so TF_TARGET_RS is 0 forever and a document COULD store 0. It stores "RS",
// because a file somebody edits should say what it means, and because a number in a file is a
// number nobody can check. The mapping is generated from the enum rather than written out, so it
// cannot drift from the binding — and the binding's enumerators are themselves generated from the
// header, which is where the numbers actually live.
//
// AN UNKNOWN NAME IS AN ERROR WITH A LIST. "TF_TARGET_RZ is not a target code" is a bad message;
// the good one names the nearest few, because the person reading it is looking at a file they
// typed and wants to know what they should have typed instead.

using ThinFilm.Native;

namespace ThinFilm.Api;

public static class Codes
{
    private static readonly Dictionary<string, int> Targets = Build<TfTargetCode>("TF_TARGET_");
    private static readonly Dictionary<string, int> Comparisons = Build<TfComparison>("TF_COMPARE_");
    private static readonly Dictionary<string, int> Reductions = Build<TfReduction>("TF_REDUCE_");
    private static readonly Dictionary<string, int> TrialReductions = Build<TfTrialReduction>("TF_TRIAL_");
    private static readonly Dictionary<string, int> Norms =
        new(StringComparer.OrdinalIgnoreCase) { ["L2"] = 0, ["MINIMAX"] = 1, ["L1"] = 2 };

    /// <summary>
    /// Both the full enumerator name and its short tail, so a document may say "TF_TARGET_RS" or
    /// "RS" and a person writing one by hand is not punished for the readable choice.
    /// </summary>
    private static Dictionary<string, int> Build<T>(string prefix) where T : struct, Enum
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in Enum.GetNames<T>())
        {
            int value = Convert.ToInt32(Enum.Parse<T>(name));
            map[name] = value;
            if (name.StartsWith(prefix, StringComparison.Ordinal))
                map[name[prefix.Length..]] = value;
        }
        return map;
    }

    /// <summary>
    /// THE BAND OPERANDS (M3.5) are names a document uses and the engine never sees: each is the
    /// engine quantity it averages, reduced over the row's points with weights from Spectra. ASOL,
    /// RSOL and TSOL average A, R and T against AM1.5; EMIT averages A - which is the emittance, by
    /// Kirchhoff - against a blackbody; AERATIO is ASOL / EMIT, a ratio of two hidden rows.
    /// </summary>
    private static readonly Dictionary<string, int> BandTargets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ASOL"] = (int)TfTargetCode.TF_TARGET_AA,
        ["RSOL"] = (int)TfTargetCode.TF_TARGET_RA,
        ["TSOL"] = (int)TfTargetCode.TF_TARGET_TA,
        ["EMIT"] = (int)TfTargetCode.TF_TARGET_AA,
        ["AERATIO"] = (int)TfTargetCode.TF_TARGET_RATIO,
    };

    public static IReadOnlyCollection<string> BandNames => BandTargets.Keys;

    /// <summary>A band operand: one row averaging over its band against a named spectrum.</summary>
    public static bool IsBand(string? name) => name is not null && BandTargets.ContainsKey(name.Trim());

    /// <summary>A band operand weighted by a blackbody, and so with a temperature.</summary>
    public static bool IsThermal(string? name) =>
        name?.Trim().ToUpperInvariant() is "EMIT" or "AERATIO";

    public static int Target(string name) =>
        name is not null && BandTargets.TryGetValue(name.Trim(), out int band) ? band : Look(Targets, name!, "target code");
    public static int Comparison(string name) => Look(Comparisons, name, "comparison");
    public static int Reduction(string name) => Look(Reductions, name, "reduction");
    public static int TrialReduction(string name) => Look(TrialReductions, name, "trial reduction");
    public static int Norm(string name) => Look(Norms, name, "norm");

    public static string TargetName(int code) =>
        Enum.IsDefined(typeof(TfTargetCode), code)
            ? ((TfTargetCode)code).ToString()["TF_TARGET_".Length..]
            : code.ToString();

    public static IReadOnlyCollection<string> TargetNames =>
        Enum.GetNames<TfTargetCode>().Select(n => n["TF_TARGET_".Length..]).Concat(BandTargets.Keys).ToArray();

    public static bool IsStack(int code) =>
        code is (int)TfTargetCode.TF_TARGET_TOTT or (int)TfTargetCode.TF_TARGET_NLAY
             or (int)TfTargetCode.TF_TARGET_MINT or (int)TfTargetCode.TF_TARGET_MAXT
             or (int)TfTargetCode.TF_TARGET_STRESS;

    /// <summary>Group delay, GDD (ABI 11) and TOD (ABI 12), which need the media's dispersion.</summary>
    public static bool IsDispersion(int code) =>
        code >= (int)TfTargetCode.TF_TARGET_R_GD_S && code <= (int)TfTargetCode.TF_TARGET_T_TOD_P;

    /// <summary>TOD (ABI 12), the one order that needs d³n/dλ³.</summary>
    public static bool IsThirdOrder(int code) =>
        code >= (int)TfTargetCode.TF_TARGET_R_TOD_S && code <= (int)TfTargetCode.TF_TARGET_T_TOD_P;

    private static int Look(Dictionary<string, int> map, string name, string what)
    {
        if (name is null) throw new FormatException($"a {what} is missing");
        if (map.TryGetValue(name, out var v)) return v;

        // The nearest few by simple prefix and substring, which is enough to catch a typo and
        // cheap enough not to need a distance metric.
        var near = map.Keys
            .Where(k => !k.StartsWith("TF_", StringComparison.Ordinal))
            .Where(k => k.StartsWith(name[..Math.Min(2, name.Length)], StringComparison.OrdinalIgnoreCase)
                        || k.Contains(name, StringComparison.OrdinalIgnoreCase))
            .Take(6).ToArray();

        throw new FormatException(
            near.Length > 0
                ? $"'{name}' is not a {what}. Did you mean {string.Join(", ", near)}?"
                : $"'{name}' is not a {what}.");
    }
}
