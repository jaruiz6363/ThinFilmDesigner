// MaterialsDialog.axaml.cs — Edit > Materials.
//
// Wiring only: the working copy, the checks and the preview numbers are MaterialsForm's (Gui.Core,
// tested). Returns true from ShowDialog on OK - the caller then hands the form to the workspace -
// and false on Cancel, when the form is simply dropped.

using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using ThinFilm.Api;
using ThinFilm.Gui.Core;

namespace ThinFilm.Gui;

public partial class MaterialsDialog : Window
{
    private readonly MaterialsForm _form;

    public MaterialsDialog() : this(new MaterialsForm(MaterialDatabase.Shipped(),
        new MaterialSet([new NamedMaterial("SiO2", MaterialModel.Constant(1.46))],
                        MaterialDatabase.Air, MaterialDatabase.Air, [0]))) { }

    public MaterialsDialog(MaterialsForm form)
    {
        InitializeComponent();
        _form = form;

        ShowDatabase();
        // Each database material says what it is under its name - the kind, n at 550 nm and the
        // range - so "database SiO2" is never a mystery. The source is the tooltip.
        DatabaseList.ItemTemplate = new FuncDataTemplate<string>((name, _) =>
        {
            var m = form.Offered.FirstOrDefault(o => o.Name == name);
            var panel = new StackPanel { Spacing = 1 };
            panel.Children.Add(new TextBlock { Text = name, FontWeight = FontWeight.SemiBold });
            if (m is null) return panel;
            panel.Children.Add(new TextBlock
            {
                Text = MaterialsForm.Summary(m, form.WavelengthUnit), FontSize = 10, TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60)),
            });
            if (m.Model.Source is { } source) ToolTip.SetTip(panel, "Source: " + source);
            return panel;
        });
        ShowDesign();


        // THE TWO SOURCE LISTS ARE ONE CHOICE: selecting in either clears the other.
        DatabaseList.SelectionChanged += (_, _) =>
        {
            if (DatabaseList.SelectedIndex < 0) return;
            GlassList.SelectedItem = null;
            ShowPreview(form.Offered[DatabaseList.SelectedIndex], "Database: ");
            ShowSource();
        };
        GlassList.SelectionChanged += (_, _) =>
        {
            if (SelectedGlass() is not { } g) return;
            DatabaseList.SelectedItem = null;
            ShowPreview(new NamedMaterial(g.FullName, MaterialModel.CatalogueGlass(g.Catalog, g.Name)), "Catalogue: ");
            ShowSource();
        };
        DesignList.SelectionChanged += (_, _) => { ShowEntry(); ShowReplace(); };

        CatalogueBox.ItemsSource = form.Catalogues.Select(c => c.Name).ToArray();
        CatalogueBox.SelectedItem = form.Catalogues.Any(c => c.Name == "SCHOTT") ? "SCHOTT" : form.Catalogues.FirstOrDefault()?.Name;
        CatalogueBox.SelectionChanged += (_, _) => ShowGlasses();
        GlassFilterBox.TextChanged += (_, _) => ShowGlasses();
        ShowGlasses();

        CopyButton.Click += (_, _) =>
        {
            string? warning = Source().Name is { } n ? form.AddWarning(n) : null;
            if (Act(name => form.CopyIntoDesign(name), null, "added to the design as a new material", selectLast: true)
                && warning is not null)
                Tell(ProblemText.Text + " Note: " + warning);
        };
        UseSubstrateButton.Click += (_, _) => Act(name => form.UseAsSubstrate(name),
            g => form.UseGlassAsSubstrate(g.Catalog, g.Name, out _), "is now the substrate", selectLast: false);
        UseIncidentButton.Click += (_, _) => Act(name => form.UseAsIncident(name), null, "is now the incident medium", selectLast: false);
        ReplaceButton.Click += (_, _) => OnReplace();
        UpgradeButton.Click += (_, _) =>
        {
            int keep = DesignList.SelectedIndex;
            string said = form.UpgradeAll();
            ShowDesign();
            DesignList.SelectedIndex = keep;
            Tell(said + " Press OK to apply it to the design.");
        };
        RemoveButton.Click += (_, _) =>
        {
            Say(form.Remove(DesignList.SelectedIndex));
            ShowDesign();
        };
        RenameButton.Click += (_, _) => OnRename();
        EditDispersionButton.Click += (_, _) => OnEditDispersion();
        SaveToDatabaseButton.Click += (_, _) => OnSaveToDatabase();
        NewCoatingItem.Click += (_, _) => OnNewMaterial(MaterialRole.Coating);
        NewSubstrateItem.Click += (_, _) => OnNewMaterial(MaterialRole.Substrate);
        NewIncidentItem.Click += (_, _) => OnNewMaterial(MaterialRole.Incident);
        OkButton.Click += (_, _) => Close(true);
        CancelButton.Click += (_, _) => Close(false);
        ShowSource();
        ShowEntry();   // nothing selected yet: the editor starts disabled, not looking editable
    }

    /// <summary>The selected source: a database material's name, or a catalogue glass; never both.</summary>
    private (string? Name, Glass? Glass) Source() =>
        SelectedGlass() is { } g ? (g.FullName, g) : (DatabaseList.SelectedItem as string, null);

    /// <summary>
    /// The buttons say what they will do with the selection, and are off when it does not apply:
    /// a catalogue glass can only be a substrate (it is named, not copied, so it is not a coating
    /// material, and an absorbing incident medium is not allowed anyway).
    /// </summary>
    private void ShowSource()
    {
        var (name, glass) = Source();
        bool any = name is not null;
        CopyButton.IsEnabled = any && glass is null;
        UseSubstrateButton.IsEnabled = any;
        UseIncidentButton.IsEnabled = any && glass is null;
        CopyButton.Content = any && glass is null ? $"Add {name} as a new material →" : "Add as a new material →";
        UseSubstrateButton.Content = any ? $"Use {name} as substrate →" : "Use as substrate →";
        UseIncidentButton.Content = any && glass is null ? $"Use {name} as incident medium →" : "Use as incident medium →";
        SourceHint.Text = !any ? "Select a material in the database or a catalogue glass, then choose what to do with it."
            : glass is not null ? $"{name}: a catalogue glass - it can be the substrate, named by the design and not copied."
            : $"{name}: from the material database. Add makes it a new, separate material in the design; "
              + "to change an existing one to it, select that one in the middle and press Replace."
              + (_form.AddWarning(name!) is { } w ? " " + w : "");
        ShowReplace();
    }

    /// <summary>
    /// Replace is live when both sides are chosen - a design material in the middle, a database
    /// material (or, for the substrate, a catalogue glass) on the left - and says exactly what it
    /// will do: "Replace SiO2 with database SiO2".
    /// </summary>
    private void ShowReplace()
    {
        int i = DesignList.SelectedIndex;
        var (name, glass) = Source();
        var entry = i >= 0 && i < _form.Entries.Count ? _form.Entries[i] : null;
        bool fits = entry is not null && name is not null
                    && (glass is null || entry.Role == MaterialRole.Substrate)
                    && !(entry.Role == MaterialRole.Incident && glass is not null);
        string? preview = null;
        bool changes = false;
        if (fits)
        {
            var replacement = glass is not null
                ? new NamedMaterial(glass.FullName, MaterialModel.CatalogueGlass(glass.Catalog, glass.Name))
                : _form.Offered.First(m => m.Name == name);
            preview = _form.ReplacePreview(i, replacement, glass is null ? "database" : "catalogue glass", out changes);
        }
        ReplaceHint.Text = preview ?? "";
        DesignHint.Text = _form.ConstantAdvice(i) ?? "";
        ReplaceButton.IsEnabled = fits && changes;
        ReplaceButton.Content = fits
            ? $"Replace {entry!.Material.Name} with {(glass is null ? "database " : "")}{name}"
            : "Replace with database material";
        UpgradeButton.IsEnabled = _form.UpgradePlan().Any(u => u.To is not null);
    }

    private void OnReplace()
    {
        int i = DesignList.SelectedIndex;
        var (name, glass) = Source();
        if (i < 0 || name is null) { Say("Select a design material in the middle and a material on the left."); return; }
        string was = _form.Entries[i].Material.Name;
        string? problem = glass is not null
            ? _form.UseGlassAsSubstrate(glass.Catalog, glass.Name, out _)
            : _form.ReplaceWithDatabase(i, name);
        ShowDesign();
        DesignList.SelectedIndex = i;
        if (problem is not null) { Say(problem); return; }
        int layers = _form.Entries[i].LayersUsing;
        Tell($"{was} replaced by {name}"
             + (layers > 0 ? $"; its {layers} layer{(layers == 1 ? "" : "s")} follow{(layers == 1 ? "s" : "")}" : "")
             + ". Press OK to apply it to the design.");
    }

    private bool Act(Func<string, string?> database, Func<Glass, string?>? catalogue, string done, bool selectLast)
    {
        var (name, glass) = Source();
        if (name is null) { Say("Select a material in the database or a catalogue glass first."); return false; }
        string? problem = glass is not null
            ? (catalogue is null ? $"{name} is a catalogue glass; it can only be the substrate" : catalogue(glass))
            : database(name);
        int keep = DesignList.SelectedIndex;
        ShowDesign();
        DesignList.SelectedIndex = selectLast ? _form.Entries.Count - 1 : keep;
        if (problem is not null) { Say(problem); return false; }
        // What happened, and that it is not yet the design's: nothing is applied until OK.
        Tell($"{name} {done}. Press OK to apply it to the design."
             + (glass?.Caution is { } c ? " " + c : ""));
        return true;
    }

    private void OnRename()
    {
        int i = DesignList.SelectedIndex;
        if (i < 0) { Say("Select a design material to rename."); return; }
        var e = _form.Entries[i];
        string? problem = _form.SetDefinition(i, NameBox.Text ?? "", e.Material.Model);
        ShowDesign();
        DesignList.SelectedIndex = i;
        if (problem is not null) Say(problem);
        else Tell($"Renamed to {_form.Entries[i].Material.Name}. Press OK to apply it to the design.");
    }

    /// <summary>The dispersion form for the selected design material, modal over this one.</summary>
    private async void OnEditDispersion()
    {
        int i = DesignList.SelectedIndex;
        if (i < 0) { Say("Select a design material to edit."); return; }
        var e = _form.Entries[i];
        var form = new CoefficientsForm(e.Material.Name, e.Material.Model, _form.Database) { WavelengthUnit = _form.WavelengthUnit };
        var model = await new CoefficientsDialog(form).ShowDialog<MaterialModel?>(this);
        if (model is null) return;
        string? problem = _form.SetDefinition(i, e.Material.Name, model);
        ShowDesign();
        DesignList.SelectedIndex = i;
        if (problem is not null) Say(problem);
        else Tell($"{e.Material.Name}: dispersion changed to {MaterialsForm.Describe(model)}. Press OK to apply it to the design.");
    }

    /// <summary>The database on the left, read again after Save to database.</summary>
    private void ShowDatabase()
    {
        DatabasePathText.Text = _form.Database.Path ?? "the shipped database";
        DatabaseList.ItemsSource = _form.Offered.Select(m => m.Name).ToArray();
    }

    /// <summary>
    /// The selected design material into the person's database, as it is now in this dialog. One of
    /// the same name there is replaced only after asking, with both definitions said.
    /// </summary>
    private async void OnSaveToDatabase()
    {
        int i = DesignList.SelectedIndex;
        if (_form.SaveRefusal(i) is { } refusal) { Say(Capital(refusal) + "."); return; }
        var mine = _form.Entries[i].Material;
        bool replace = false;
        if (_form.SaveWouldReplace(i) is { } there)
        {
            replace = await new ConfirmDialog("Replace in the database",
                $"The material database already has {there.Name}: {MaterialsForm.Summary(there, _form.WavelengthUnit)}.\n\n"
                + $"Replace it with this design's {mine.Name}: {MaterialsForm.Summary(mine, _form.WavelengthUnit)}?\n\n"
                + "Designs already made keep their own copies; only designs that add it later get the new one. "
                + "To keep both, rename this one first.",
                "Replace").ShowDialog<bool>(this);
            if (!replace) { Tell("Nothing saved; the database is as it was."); return; }
        }
        string? done = _form.SaveToDatabase(i, replace, out var problem);
        if (done is null) { Say(Capital(problem ?? "it could not be saved") + "."); return; }
        ShowDatabase();
        ShowEntry();
        Tell(done);
    }

    /// <summary>
    /// A material the database does not have: the dispersion dialog opened empty, asking for a name
    /// too, and on OK the material added as a coating material or put in as the substrate or
    /// incident medium.
    /// </summary>
    private async void OnNewMaterial(MaterialRole role)
    {
        string what = role switch
        {
            MaterialRole.Substrate => "substrate",
            MaterialRole.Incident => "incident medium",
            _ => "coating material",
        };
        var form = new CoefficientsForm(what, new MaterialModel { Kind = "SELLMEIER" }, _form.Database)
            { WavelengthUnit = _form.WavelengthUnit, IsNew = true };
        var model = await new CoefficientsDialog(form).ShowDialog<MaterialModel?>(this);
        if (model is null) return;
        int i = _form.NewMaterial(role, form.NewName, model, out var problem);
        if (i < 0) { Say(Capital(problem ?? "it could not be made") + "."); return; }
        ShowDesign();
        DesignList.SelectedIndex = i;
        var made = _form.Entries[i].Material;
        Tell(role == MaterialRole.Coating
            ? $"{made.Name} added to the design; give layers this material on the Design tab. "
              + "Save to database keeps it for other designs. OK applies."
            : $"{made.Name} is now the {what}. Save to database keeps it for other designs. OK applies.");
    }

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private void ShowDesign()
    {
        int keep = DesignList.SelectedIndex;
        DesignList.ItemsSource = _form.Entries.Select(e => e.Label).ToArray();
        if (keep >= 0 && keep < _form.Entries.Count) DesignList.SelectedIndex = keep;
        ShowReplace();
    }

    private void ShowEntry()
    {
        int i = DesignList.SelectedIndex;
        bool any = i >= 0 && i < _form.Entries.Count;
        EditorPanel.IsEnabled = any;
        if (!any) { EditorTitle.Text = "Select a material in the design"; ModelSummary.Text = ""; NameBox.Text = ""; return; }

        var e = _form.Entries[i];
        NameBox.Text = e.Material.Name;
        ModelSummary.Text = MaterialsForm.Describe(e.Material.Model);
        SaveHint.Text = _form.SaveRefusal(i) is { } cannot ? Capital(cannot) + "."
            : _form.SaveWouldReplace(i) is { } there
                ? (MaterialsForm.SameDefinition(there.Model, e.Material.Model)
                    ? $"The database's {there.Name} is the same."
                    : $"The database has a different {there.Name}; saving asks before replacing it.")
                : $"The database has no {e.Material.Name}: save it to use it in other designs.";
        SaveToDatabaseButton.IsEnabled = _form.SaveRefusal(i) is null
            && !(_form.SaveWouldReplace(i) is { } same && MaterialsForm.SameDefinition(same.Model, e.Material.Model));
        // A CATALOGUE GLASS IS SHOWN, NOT EDITED: the design names it and the vendor's data defines it.
        if (e.Material.Model.IsCatalogueGlass)
        {
            EditorPanel.IsEnabled = false;
            EditorTitle.Text = $"{e.Material.Name}: a catalogue glass, named by the design - not editable";
        }
        else EditorTitle.Text = $"The design's copy: {e.Label}";
        ShowPreview(e.Material, "");
    }

    private void ShowPreview(NamedMaterial m, string prefix)
    {
        var p = MaterialsForm.Preview(m, out var problem, _form.WavelengthUnit);
        Preview.XTitle = $"wavelength ({MeritRow.UnitLabel(_form.WavelengthUnit)})";
        if (p is null)
        {
            Preview.Series = Array.Empty<PlotSeries>();
            PreviewNote.Text = $"{prefix}{m.Name}: {problem}";
            return;
        }
        var (nm, n, k, span) = p.Value;
        // A clear material has k = 0 everywhere, and a flat k line at zero pins the axis to zero and
        // flattens the dispersion that is the point of looking; so k is drawn only when there is some.
        bool absorbing = k.Any(v => v > 0.0);
        Preview.Series = absorbing
            ? [new PlotSeries("n", nm, n, 0xFF1F77B4), new PlotSeries("k", nm, k, 0xFFD62728)]
            : [new PlotSeries("n", nm, n, 0xFF1F77B4)];
        Preview.YTitle = absorbing ? "n (blue), k (red)" : "n";
        span += absorbing ? "" : "; k = 0 throughout";
        PreviewNote.Text = $"{prefix}{m.Name}, {m.Model.Kind.ToLowerInvariant()}: {span}."
                           + (m.Model.Source is { } s ? $" Source: {s}." : "");
    }

    /// <summary>The selected catalogue's glasses, filtered by the text typed ("BK7" finds N-BK7).</summary>
    private void ShowGlasses()
    {
        var catalog = _form.Catalogues.FirstOrDefault(c => c.Name == CatalogueBox.SelectedItem as string);
        string filter = GlassFilterBox.Text?.Trim() ?? "";
        GlassList.ItemsSource = catalog?.Glasses
            .Where(g => filter.Length == 0 || g.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Select(g => g.Name).ToArray() ?? [];
    }

    private Glass? SelectedGlass() =>
        GlassList.SelectedItem is string name && CatalogueBox.SelectedItem is string catalog
            ? GlassCatalogs.Find(catalog, name) : null;

    /// <summary>A refusal, in red.</summary>
    private void Say(string? problem)
    {
        ProblemText.Foreground = Avalonia.Media.Brushes.Firebrick;
        ProblemText.Text = problem ?? "";
    }

    /// <summary>What just happened, in green: not an error, and not yet applied.</summary>
    private void Tell(string message)
    {
        ProblemText.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(0x1B, 0x6E, 0x2E));
        ProblemText.Text = message;
    }
}
