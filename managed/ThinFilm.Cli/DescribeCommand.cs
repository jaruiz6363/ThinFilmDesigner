// DescribeCommand.cs — thinfilm describe: the design's Description, where it came from and notes on it.

using System.Text.Json;
using ThinFilm.Api;

namespace ThinFilm.Cli;

public static partial class CommandLine
{
    private static int Describe(ThinFilmApi api, Options o, TextWriter output, TextWriter error)
    {
        if (!OpenFile(api, o, 0, error, out var path, out var id)) return 1;
        string? source = o.String("source"), notes = o.String("notes"), name = o.String("name");
        if (o.String("notes-file") is { } file) notes = File.ReadAllText(file);
        bool edit = source is not null || notes is not null || name is not null;
        if (name is not null && api.SetName(id, name) is { Ok: false } renamed) return Fail(error, renamed);
        if (source is not null || notes is not null)
        {
            var r = api.SetDescription(id, source, notes);
            if (!r.Ok) return Fail(error, r);
        }
        if (edit) Save(o, o.Output ?? path, api.Document(id).Value!.ToJson());
        var d = api.Describe(id).Value!;
        if (o.Json) { output.WriteLine(JsonSerializer.Serialize(new { d.Name, d.Source, d.Notes }, Json)); return 0; }
        output.WriteLine($"  {d.Name}");
        output.WriteLine($"  source: {d.Source ?? "(none)"}");
        if (d.Notes is { } n)
            foreach (var line in n.Split('\n')) output.WriteLine($"  {line}");
        if (edit) output.WriteLine($"  wrote {o.Output ?? path}");
        return 0;
    }
}
