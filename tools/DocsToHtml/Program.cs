// DocsToHtml — the person-facing documents as web pages, for the packages.
//
//   dotnet run --project tools/DocsToHtml -- <repo> <out>
//
// writes <out>/docs/... : the user guide (every page, its images and its data), and the documents a
// person reads beside it - installing, the command line, the MCP server, the guide from the command
// line, verification, references and the release notes. The design documents (scope, the
// contracts, the GUI backlog) are for developers and stay out; the merit specification is in, because
// the guide links to it.
//
// Links between documents are rewritten from .md to .html; a link to a sample design keeps working
// because the package has samples/ beside docs/, as the source tree does. A link to anything else
// in the source tree that the package does not hold is left as it is.

using System.Text;
using System.Text.RegularExpressions;
using Markdig;

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: DocsToHtml <repo> <out>");
    return 2;
}
string repo = Path.GetFullPath(args[0]);
string outRoot = Path.GetFullPath(args[1]);
string docs = Path.Combine(repo, "docs");

string[] pages =
[
    "building.md", "cli.md", "cli-examples.md", "mcp.md", "verification.md", "references.md", "release-notes.md",
    // Developer reading, but the merit-function page and cli.md send a person to it for the operands.
    "Thin Film Design Software - Merit Function Specification.md",
    "abi-and-allocation-contract.md",   // which the specification's as-built note sends a reader to
];
var guide = Directory.GetFiles(Path.Combine(docs, "user-guide"), "*.md").Select(p => Path.Combine("user-guide", Path.GetFileName(p)));

var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
int written = 0;
foreach (var rel in pages.Concat(guide))
{
    string source = Path.Combine(docs, rel);
    if (!File.Exists(source)) { Console.Error.WriteLine($"missing {rel}"); return 1; }
    string md = File.ReadAllText(source);
    string body = Markdown.ToHtml(md, pipeline);
    // .md links to .html, keeping any #anchor; README.md stays README.html.
    body = Regex.Replace(body, "href=\"([^\":#]+?)\\.md(#[^\"]*)?\"", m => $"href=\"{m.Groups[1].Value}.html{m.Groups[2].Value}\"");
    string title = Regex.Match(md, @"^#\s+(.+)$", RegexOptions.Multiline) is { Success: true } t ? t.Groups[1].Value.Trim() : rel;
    int depth = rel.Count(c => c == Path.DirectorySeparatorChar || c == '/');
    string home = string.Concat(Enumerable.Repeat("../", depth)) + "user-guide/README.html";
    string target = Path.Combine(outRoot, "docs", Path.ChangeExtension(rel, ".html"));
    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
    File.WriteAllText(target, Page(title, body, home), new UTF8Encoding(false));
    ++written;
}

// The guide's screenshots and its data (the silicon table chapter 7 imports).
foreach (var folder in new[] { "images", "data" })
{
    string from = Path.Combine(docs, "user-guide", folder);
    if (!Directory.Exists(from)) continue;
    foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
    {
        string to = Path.Combine(outRoot, "docs", "user-guide", folder, Path.GetRelativePath(from, f));
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Copy(f, to, overwrite: true);
    }
}
Console.WriteLine($"{written} pages into {Path.Combine(outRoot, "docs")}");
return 0;

static string Page(string title, string body, string home) => $$"""
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>{{System.Net.WebUtility.HtmlEncode(title)}} - ThinFilmDesigner</title>
<style>
:root { --bg: #ffffff; --fg: #1f2328; --muted: #59636e; --line: #d1d9e0; --code: #f6f8fa; --link: #0969da; }
@media (prefers-color-scheme: dark) {
  :root { --bg: #0d1117; --fg: #e6edf3; --muted: #9198a1; --line: #3d444d; --code: #151b23; --link: #4493f8; }
}
body { background: var(--bg); color: var(--fg); margin: 0; font: 16px/1.6 -apple-system, "Segoe UI", Helvetica, Arial, sans-serif; }
nav { border-bottom: 1px solid var(--line); padding: 10px 16px; font-size: 14px; }
main { max-width: 980px; margin: 0 auto; padding: 16px 16px 48px; }
a { color: var(--link); }
h1, h2 { border-bottom: 1px solid var(--line); padding-bottom: .3em; }
img { max-width: 100%; height: auto; border: 1px solid var(--line); }
table { border-collapse: collapse; display: block; overflow-x: auto; margin: 1em 0; }
th, td { border: 1px solid var(--line); padding: 6px 12px; vertical-align: top; }
code { background: var(--code); padding: .1em .35em; border-radius: 4px; font-size: 90%; }
pre { background: var(--code); padding: 12px 16px; overflow-x: auto; border-radius: 6px; }
pre code { padding: 0; background: none; }
blockquote { color: var(--muted); border-left: 4px solid var(--line); margin: 0; padding: 0 1em; }
</style>
</head>
<body>
<nav><a href="{{home}}">ThinFilmDesigner user guide</a></nav>
<main>
{{body}}
</main>
</body>
</html>
""";
