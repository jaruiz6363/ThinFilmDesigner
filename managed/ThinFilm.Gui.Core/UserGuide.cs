// UserGuide.cs — where Help > User guide finds the guide.
//
// The packages carry it as web pages, docs/user-guide/README.html beside the programs (made by
// tools/DocsToHtml when a release is built). Run from the source tree there are no pages, so the
// guide's own Markdown is opened instead. The user, 2026-10-25, after installing 1.0.0: "I didn't
// see any user guide files. These would be the best for testing and user first trying the program."

namespace ThinFilm.Gui.Core;

public static class UserGuide
{
    /// <summary>The guide's first page: the packaged web page, or the source tree's Markdown, or
    /// null when neither is there.</summary>
    public static string? Find(string baseDirectory)
    {
        string[] candidates =
        [
            Path.Combine(baseDirectory, "docs", "user-guide", "README.html"),
            // From managed/ThinFilm.Gui/bin/<config>/net8.0 to the repository's docs.
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "..", "..", "docs", "user-guide", "README.md")),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }
}
