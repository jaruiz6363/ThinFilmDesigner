// SampleBrowserDialog.axaml.cs — File > Open sample: the sample catalogue as a tree, with the
// selected design's source and notes beside it.
//
// The tree is SampleTree's, from Samples.Catalog; Find narrows it to the designs whose name, folder,
// source or notes hold every word typed, and opens every folder that is left. Open (or a double
// click on a design) closes with the design's path. Wiring only.

using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Styling;
using ThinFilm.Api;
using ThinFilm.Gui.Core;

namespace ThinFilm.Gui;

public partial class SampleBrowserDialog : Window
{
    private readonly Func<string?, IReadOnlyList<SampleEntry>> _catalog;
    private readonly Style _expandAll = new(x => x.OfType<TreeViewItem>())
    {
        Setters = { new Setter(TreeViewItem.IsExpandedProperty, true) },
    };

    // Examples and Literature open, so the examples and the categories are in view from the start.
    private readonly Style _expandTop = new(x => x.OfType<TreeView>().Child().OfType<TreeViewItem>())
    {
        Setters = { new Setter(TreeViewItem.IsExpandedProperty, true) },
    };

    public SampleBrowserDialog() : this(Samples.Catalog) { }

    /// <summary><paramref name="catalog"/> is Samples.Catalog, or a test's stand-in.</summary>
    public SampleBrowserDialog(Func<string?, IReadOnlyList<SampleEntry>> catalog)
    {
        InitializeComponent();
        _catalog = catalog;

        SampleTreeView.ItemTemplate = new FuncTreeDataTemplate<SampleNode>(
            (node, _) => new TextBlock { Text = node.Header, FontWeight = node.Entry is null ? Avalonia.Media.FontWeight.SemiBold : Avalonia.Media.FontWeight.Normal },
            node => node.Children);
        SampleTreeView.Styles.Add(_expandTop);
        SampleTreeView.SelectionChanged += (_, _) => Show(SampleTreeView.SelectedItem as SampleNode);
        SampleTreeView.DoubleTapped += (_, e) =>
        {
            if (Selected is not null && e.Source is Control { DataContext: SampleNode { Entry: not null } }) Close(Selected.Path);
        };
        FilterBox.TextChanged += (_, _) => Fill();
        FilterBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Down) { SampleTreeView.Focus(); e.Handled = true; }
        };
        OkButton.Click += (_, _) => { if (Selected is { } s) Close(s.Path); };
        CancelButton.Click += (_, _) => Close(null);
        Opened += (_, _) => FilterBox.Focus();
        Fill();
    }

    /// <summary>The selected design, or null when nothing or a folder is selected.</summary>
    public SampleEntry? Selected => (SampleTreeView.SelectedItem as SampleNode)?.Entry;

    /// <summary>The tree's top level, as shown.</summary>
    public IReadOnlyList<SampleNode> Roots { get; private set; } = [];

    private void Fill()
    {
        string filter = (FilterBox.Text ?? "").Trim();
        Roots = SampleTree.Build(_catalog(filter));
        SampleTreeView.ItemsSource = Roots;

        // A search opens every folder it leaves, so what it found is in view.
        SampleTreeView.Styles.Remove(_expandAll);
        if (filter.Length > 0) SampleTreeView.Styles.Add(_expandAll);

        int count = Roots.Sum(n => n.Count);
        CountText.Text = count == 1 ? "1 design" : $"{count} designs";
        Show(null);
    }

    /// <summary>A design: its name, folder, source and notes. A folder: what it holds.</summary>
    private void Show(SampleNode? node)
    {
        var entry = node?.Entry;
        OkButton.IsEnabled = entry is not null;
        NameText.Text = node?.Name ?? (Roots.Count == 0 ? "No sample matches." : "Choose a design on the left.");
        GroupText.Text = entry?.Group.Replace("/", " › ") ?? (node is null ? "" : FolderSummary(node));
        SourceLabel.IsVisible = SourceText.IsVisible = entry?.Source is not null;
        SourceText.Text = entry?.Source ?? "";
        NotesLabel.IsVisible = NotesText.IsVisible = entry?.Notes is not null;
        NotesText.Text = entry?.Notes ?? "";
    }

    private static string FolderSummary(SampleNode folder)
    {
        int designs = folder.Count;
        int papers = folder.Designs.Where(e => e.Group.StartsWith("Literature/", StringComparison.Ordinal))
                                   .Select(e => e.Group).Distinct().Count();
        string s = designs == 1 ? "1 design" : $"{designs} designs";
        return papers > 1 ? $"{s} from {papers} papers" : s;
    }

    /// <summary>Selects <paramref name="entry"/>'s node, as a click on it would (the tests' way in).</summary>
    public bool Select(SampleEntry entry)
    {
        var node = Find(Roots, entry);
        if (node is null) return false;
        SampleTreeView.SelectedItem = node;
        return true;
    }

    private static SampleNode? Find(IEnumerable<SampleNode> nodes, SampleEntry entry) =>
        nodes.Select(n => n.Entry == entry ? n : Find(n.Children, entry)).FirstOrDefault(n => n is not null);
}
