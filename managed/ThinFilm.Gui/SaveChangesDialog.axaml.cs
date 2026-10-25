// SaveChangesDialog.axaml.cs — "Save changes to X before ...?" Answers Save, Discard or Cancel.

using Avalonia.Controls;

namespace ThinFilm.Gui;

public enum SaveChoice { Cancel, Save, Discard }

public partial class SaveChangesDialog : Window
{
    public SaveChangesDialog() : this("", "") { }

    /// <param name="name">The design as the title names it.</param>
    /// <param name="before">What is about to happen: "opening another design", "closing".</param>
    public SaveChangesDialog(string name, string before)
    {
        InitializeComponent();
        MessageText.Text = $"{name} has changed since it was opened or last saved. Save it before {before}?";
        SaveButton.Click += (_, _) => Close(SaveChoice.Save);
        DiscardButton.Click += (_, _) => Close(SaveChoice.Discard);
        CancelButton.Click += (_, _) => Close(SaveChoice.Cancel);
    }
}
