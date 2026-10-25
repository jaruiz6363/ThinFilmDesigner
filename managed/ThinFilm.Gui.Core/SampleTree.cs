// SampleTree.cs — the sample catalogue as File > Open sample shows it, built without a window.
//
// The folders under samples/ become the branches (Examples; Literature > Antireflection > Young 1961)
// and the designs the leaves. A folder holding a single design shows the design in its place, so the
// examples (Examples/AR-visible/AR-visible.tfd) and one-design papers are not a click deeper than
// the rest.

using ThinFilm.Api;

namespace ThinFilm.Gui.Core;

/// <summary>A folder (Entry null, with Children) or a design (Entry set, no Children).</summary>
public sealed class SampleNode
{
    public required string Name { get; init; }
    public SampleEntry? Entry { get; init; }
    public IReadOnlyList<SampleNode> Children { get; init; } = [];

    /// <summary>The designs at and under this node.</summary>
    public int Count => Entry is not null ? 1 : Children.Sum(c => c.Count);

    /// <summary>What the tree shows: a design's name, a folder's name and how many designs it holds.</summary>
    public string Header => Entry is not null ? Name : $"{Name} ({Count})";

    /// <summary>Every design at and under this node, in tree order.</summary>
    public IEnumerable<SampleEntry> Designs =>
        Entry is not null ? [Entry] : Children.SelectMany(c => c.Designs);
}

public static class SampleTree
{
    /// <summary>The tree of <paramref name="entries"/>, in their order (Samples.Catalog's: Examples
    /// first, then by group and name).</summary>
    public static IReadOnlyList<SampleNode> Build(IEnumerable<SampleEntry> entries) =>
        Level(entries.Select(e => (Path: e.Group.Split('/', StringSplitOptions.RemoveEmptyEntries), Entry: e)).ToList(), 0);

    private static IReadOnlyList<SampleNode> Level(List<(string[] Path, SampleEntry Entry)> items, int depth)
    {
        var nodes = new List<SampleNode>();
        foreach (var group in items.GroupBy(i => i.Path.Length > depth ? i.Path[depth] : null))
        {
            if (group.Key is null)
            {
                nodes.AddRange(group.Select(i => new SampleNode { Name = i.Entry.Name, Entry = i.Entry }));
                continue;
            }
            var children = Level(group.ToList(), depth + 1);
            nodes.Add(children is [{ Entry: not null } only] ? only : new SampleNode { Name = group.Key, Children = children });
        }
        return nodes;
    }
}
