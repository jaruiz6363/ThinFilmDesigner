// CoatingChoice.cs — one coating a file offers, as the import dialog lists it.

namespace ThinFilm.Gui.Core;

/// <summary>A coating in a file: its name, its layer count, anything else worth showing ("at 550 nm"),
/// and why it cannot be imported, or null when it can.</summary>
public sealed record CoatingChoice(string Name, int Layers, string? Detail, string? Problem);
