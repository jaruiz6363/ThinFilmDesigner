// NewDesignDialog.axaml.cs — File > New design.
//
// Shows a NewDesignForm and returns its NewDesignRequest from ShowDialog on Create, or null on
// Cancel. Wiring only: what makes a request acceptable is the form's business, and the form is
// tested in Gui.Core.

using Avalonia.Controls;
using ThinFilm.Gui.Core;

namespace ThinFilm.Gui;

public partial class NewDesignDialog : Window
{
    private readonly NewDesignForm _form;
    private readonly List<CheckBox> _materials = new();

    public NewDesignDialog() : this(new NewDesignForm(ThinFilm.Api.MaterialDatabase.Shipped())) { }

    public NewDesignDialog(NewDesignForm form)
    {
        InitializeComponent();
        _form = form;

        NameBox.Text = form.Name;
        IncidentBox.ItemsSource = form.IncidentChoices;
        SubstrateBox.ItemsSource = form.SubstrateChoices;
        IncidentBox.SelectedItem = form.IncidentName;
        SubstrateBox.SelectedItem = form.SubstrateName;

        // The incident medium: air or a database material (copied), or a catalogue glass (named).
        IncidentGlassChoice.IsChecked = form.IncidentIsGlass;
        IncidentDatabaseChoice.IsChecked = !form.IncidentIsGlass;
        IncidentCatalogueBox.ItemsSource = ThinFilm.Api.GlassCatalogs.All.Select(c => c.Name).ToArray();
        IncidentCatalogueBox.SelectionChanged += (_, _) =>
        {
            var catalog = ThinFilm.Api.GlassCatalogs.All.FirstOrDefault(c => c.Name == IncidentCatalogueBox.SelectedItem as string);
            IncidentGlassBox.ItemsSource = catalog?.Glasses.Select(g => g.Name).ToArray() ?? [];
            IncidentGlassBox.SelectedItem = catalog?.Find(_form.IncidentGlass)?.Name ?? catalog?.Glasses.FirstOrDefault()?.Name;
        };
        IncidentCatalogueBox.SelectedItem = form.IncidentCatalog;

        // The substrate: a catalogue glass (named) or a database material (copied).
        GlassChoice.IsChecked = form.SubstrateIsGlass;
        DatabaseChoice.IsChecked = !form.SubstrateIsGlass;
        CatalogueBox.ItemsSource = ThinFilm.Api.GlassCatalogs.All.Select(c => c.Name).ToArray();
        CatalogueBox.SelectionChanged += (_, _) =>
        {
            var catalog = ThinFilm.Api.GlassCatalogs.All.FirstOrDefault(c => c.Name == CatalogueBox.SelectedItem as string);
            GlassBox.ItemsSource = catalog?.Glasses.Select(g => g.Name).ToArray() ?? [];
            GlassBox.SelectedItem = catalog?.Find(_form.SubstrateGlass)?.Name ?? catalog?.Glasses.FirstOrDefault()?.Name;
        };
        CatalogueBox.SelectedItem = form.SubstrateCatalog;
        GlassBox.SelectionChanged += (_, _) => ShowCaution();
        GlassChoice.IsCheckedChanged += (_, _) => ShowCaution();
        ReferenceBox.Text = form.ReferenceNm;
        ReferenceLabel.Text = $"Reference ({MeritRow.UnitLabel(form.WavelengthUnit)})";
        LayersBox.Text = form.Layers;

        foreach (var m in form.CoatingChoices)
        {
            var n = m.Model.At(550, m.Name).N;
            var box = new CheckBox
            {
                Content = $"{m.Name}  (n {n:0.###} at {MeritRow.ShowWavelength(550, form.WavelengthUnit)}, {m.Model.Kind.ToLowerInvariant()})",
                Tag = m.Name,
                IsChecked = form.Materials.Contains(m.Name),
                Margin = new Avalonia.Thickness(0, 0, 12, 0),
            };
            _materials.Add(box);
            MaterialChoices.Children.Add(box);
        }

        // ONLY THE CHOSEN ROW IS LIVE. The catalogue boxes show N-BK7 from the start, so with
        // "Air or database" chosen they read as if the glass were picked; a PBS was made in air
        // that way (2026-10-24). The other row's boxes are disabled, which shows which one counts.
        void Live()
        {
            IncidentBox.IsEnabled = IncidentDatabaseChoice.IsChecked == true;
            IncidentCatalogueBox.IsEnabled = IncidentGlassBox.IsEnabled = IncidentGlassChoice.IsChecked == true;
            SubstrateBox.IsEnabled = DatabaseChoice.IsChecked == true;
            CatalogueBox.IsEnabled = GlassBox.IsEnabled = GlassChoice.IsChecked == true;
        }
        IncidentGlassChoice.IsCheckedChanged += (_, _) => Live();
        IncidentDatabaseChoice.IsCheckedChanged += (_, _) => Live();
        GlassChoice.IsCheckedChanged += (_, _) => Live();
        DatabaseChoice.IsCheckedChanged += (_, _) => Live();
        Live();

        CreateButton.Click += (_, _) => OnCreate();
        CancelButton.Click += (_, _) => Close(null);
    }

    /// <summary>A glass whose catalogue disagrees with itself says so before it is chosen.</summary>
    private void ShowCaution()
    {
        var g = GlassChoice.IsChecked == true && CatalogueBox.SelectedItem is string c && GlassBox.SelectedItem is string n
            ? ThinFilm.Api.GlassCatalogs.Find(c, n) : null;
        SubstrateCautionText.Text = g?.Caution ?? "";
        SubstrateCautionText.IsVisible = g?.Caution is not null;
    }

    private void OnCreate()
    {
        _form.Name = NameBox.Text ?? "";
        _form.IncidentName = IncidentBox.SelectedItem as string ?? "";
        _form.IncidentIsGlass = IncidentGlassChoice.IsChecked == true;
        _form.IncidentCatalog = IncidentCatalogueBox.SelectedItem as string ?? "";
        _form.IncidentGlass = IncidentGlassBox.SelectedItem as string ?? "";
        _form.SubstrateName = SubstrateBox.SelectedItem as string ?? "";
        _form.SubstrateIsGlass = GlassChoice.IsChecked == true;
        _form.SubstrateCatalog = CatalogueBox.SelectedItem as string ?? "";
        _form.SubstrateGlass = GlassBox.SelectedItem as string ?? "";
        _form.ReferenceNm = ReferenceBox.Text ?? "";
        _form.Layers = LayersBox.Text ?? "";
        _form.Materials.Clear();
        _form.Materials.AddRange(_materials.Where(b => b.IsChecked == true).Select(b => (string)b.Tag!));

        var request = _form.ToRequest(out var problem);
        if (request is null)
        {
            ProblemText.Text = $"Cannot create it: {problem}.";
            ProblemText.IsVisible = true;
            return;
        }
        Close(request);
    }
}
