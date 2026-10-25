// ConfirmDialog.axaml.cs — a yes-or-no for an action with no undo. True when the action's button
// is pressed; false on Cancel, Escape or close. Cancel is the default, so Enter does not destroy.

using Avalonia.Controls;

namespace ThinFilm.Gui;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog() : this("Confirm", "Are you sure?", "OK") { }

    public ConfirmDialog(string title, string message, string action)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        YesButton.Content = action;
        YesButton.Click += (_, _) => Close(true);
        NoButton.Click += (_, _) => Close(false);
    }
}
