// DesignStringForm.cs — the Input design string dialog, with no window in it.
//
// The string, how to read it (which end is layer 1, QWOT or FWOT, pickups or not) and a material
// for each letter it uses. The letters are found as the string is typed; a letter seen before keeps
// the material chosen for it. A new letter gets a guess: H the design's highest-index material at
// the reference, L the lowest, M the one nearest the middle - the usual meaning of those letters -
// and any other letter the first material whose name starts with it, else none, to be chosen.

using ThinFilm.Api;

namespace ThinFilm.Gui.Core;

/// <summary>One letter of the string and the material it stands for.</summary>
public sealed class LetterChoice
{
    public required char Letter { get; init; }
    public required IReadOnlyList<string> MaterialOptions { get; init; }
    public string? Material { get; set; }
    public string Label => $"{Letter} =";
}

public sealed class DesignStringForm
{
    private readonly IReadOnlyList<string> _materials;
    private readonly double[] _index;
    private readonly Dictionary<char, string?> _chosen = new();

    /// <param name="materials">The design's materials, in index order.</param>
    /// <param name="perNm">DesignWorkspace.UnitsPerNm: QWOT per nm is 4n/λ, so it ranks the
    /// materials by index at the reference. NaN for one it cannot rank.</param>
    /// <param name="designUnit">The design's thickness unit: the numbers' unit to begin with when it
    /// is QWOT or FWOT.</param>
    public DesignStringForm(IReadOnlyList<string> materials, Func<int, string, double> perNm,
                            string designUnit, int layerCount, string text = "")
    {
        _materials = materials;
        _index = materials.Select((_, i) => perNm(i, LayerUnits.Qwot)).ToArray();
        Unit = designUnit == LayerUnits.Fwot ? LayerUnits.Fwot : LayerUnits.Qwot;
        LayerCount = layerCount;
        Text = text;
        Update();
    }

    public string Text { get; set; }
    public string Unit { get; set; }
    public bool FirstNextToSubstrate { get; set; }
    public bool Pickups { get; set; } = true;
    public int LayerCount { get; }

    /// <summary>One row per letter the string uses, in the order they first appear.</summary>
    public IReadOnlyList<LetterChoice> Letters { get; private set; } = [];

    /// <summary>What the string makes, or why it makes nothing.</summary>
    public string Preview { get; private set; } = "";
    public bool Parses { get; private set; }

    public string ReplaceNote =>
        $"OK replaces all {LayerCount} layer{(LayerCount == 1 ? "" : "s")} of the design. The incident medium, "
        + "substrate, materials and merit function are kept.";

    /// <summary>Read the string again: the letters it uses and what it makes.</summary>
    public void Update()
    {
        foreach (var l in Letters) _chosen[l.Letter] = l.Material;
        StringLayer[] layers;
        try { layers = DesignString.Expand(Text); }
        catch (FormatException e)
        {
            Parses = false;
            Preview = string.IsNullOrWhiteSpace(Text) ? "" : Capital(e.Message) + ".";
            return;   // the letters stay as they were while the string is half typed
        }
        Parses = true;
        // A letter still in the string keeps its row, so a box showing it stays connected to it.
        var had = Letters.ToDictionary(l => l.Letter);
        Letters = layers.Select(l => l.Letter).Distinct().Select(c => had.TryGetValue(c, out var kept) ? kept : new LetterChoice
        {
            Letter = c,
            MaterialOptions = _materials,
            Material = _chosen.TryGetValue(c, out var m) ? m : Guess(c),
        }).ToArray();
        var counts = Letters.Select(l => $"{layers.Count(x => x.Letter == l.Letter)} {l.Letter}");
        Preview = $"{layers.Length} layer{(layers.Length == 1 ? "" : "s")}: {string.Join(", ", counts)}.";
    }

    private string? Guess(char letter) =>
        DesignString.Guess(_materials, _index, letter) is { } i ? _materials[i] : null;

    /// <summary>The request for the API, or null with the reason.</summary>
    public DesignStringRequest? ToRequest(out string? problem)
    {
        Update();
        problem = null;
        if (!Parses) { problem = string.IsNullOrWhiteSpace(Text) ? "type a design string, e.g. (1H 1L)12" : Preview.TrimEnd('.'); return null; }
        var map = new Dictionary<char, int>();
        foreach (var l in Letters)
        {
            int i = l.Material is null ? -1 : _materials.ToList().IndexOf(l.Material);
            if (i < 0) { problem = $"choose a material for {l.Letter}"; return null; }
            map[l.Letter] = i;
        }
        return new DesignStringRequest(Text, map, Unit, FirstNextToSubstrate, Pickups);
    }

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
