// SpectrumQuantitiesDialog.axaml.cs — choosing what a spectrum plots, by checkbox.
//
// The list is ThinFilm.Api's SpectrumQuantities, a row per group. ShowDialog returns the chosen
// codes in the list's order on OK, so the axes come out in the same order every time, and null on
// Cancel.

using Avalonia.Controls;
using Avalonia.Layout;
using ThinFilm.Api;

namespace ThinFilm.Gui;

public partial class SpectrumQuantitiesDialog : Window
{
    private readonly List<CheckBox> _boxes = [];

    public SpectrumQuantitiesDialog() : this([]) { }

    public SpectrumQuantitiesDialog(IEnumerable<string> chosen)
    {
        InitializeComponent();
        var on = new HashSet<string>(chosen, StringComparer.OrdinalIgnoreCase);

        foreach (var group in SpectrumQuantities.Groups)
        {
            int row = QuantityGrid.RowDefinitions.Count;
            QuantityGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var label = new TextBlock { Text = group, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 8, 6) };
            Grid.SetRow(label, row);
            QuantityGrid.Children.Add(label);

            var panel = new WrapPanel { Margin = new(0, 0, 0, 6) };
            foreach (var q in SpectrumQuantities.All.Where(q => q.Group == group))
            {
                var box = new CheckBox
                {
                    Name = "Quantity" + q.Code, Content = q.Label, Tag = q.Code,
                    IsChecked = on.Contains(q.Code), MinWidth = 120, Margin = new(0, 0, 8, 0),
                };
                ToolTip.SetTip(box, $"{q.Code}, drawn against {q.Axis}");
                box.IsCheckedChanged += (_, _) => ProblemText.Text = "";
                _boxes.Add(box);
                panel.Children.Add(box);
            }
            Grid.SetRow(panel, row);
            Grid.SetColumn(panel, 1);
            QuantityGrid.Children.Add(panel);
        }

        ClearButton.Click += (_, _) => { foreach (var b in _boxes) b.IsChecked = false; };
        OkButton.Click += (_, _) =>
        {
            var codes = Chosen;
            if (codes.Length == 0) { ProblemText.Text = "Tick at least one quantity."; return; }
            Close(codes);
        };
        CancelButton.Click += (_, _) => Close(null);
    }

    /// <summary>The ticked codes, in the list's order.</summary>
    public string[] Chosen => _boxes.Where(b => b.IsChecked == true).Select(b => (string)b.Tag!).ToArray();

    /// <summary>The checkbox for a code, for the tests.</summary>
    public CheckBox? Box(string code) =>
        _boxes.FirstOrDefault(b => string.Equals((string)b.Tag!, code, StringComparison.OrdinalIgnoreCase));
}
