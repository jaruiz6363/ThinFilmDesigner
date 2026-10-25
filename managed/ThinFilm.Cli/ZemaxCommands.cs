// ZemaxCommands.cs — thinfilm zemax list|import|export: OpticStudio coating files (ZemaxCoating.cs).

using System.Text.Json;
using ThinFilm.Api;

namespace ThinFilm.Cli;

public static partial class CommandLine
{
    private static int Zemax(ThinFilmApi api, Options o, TextWriter output, TextWriter error)
    {
        string? action = o.Positional.Count > 0 ? o.Positional[0] : null;
        switch (action)
        {
            case "list":
            {
                var file = o.String("file") ?? (o.Positional.Count > 1 ? o.Positional[1] : null);
                if (file is null) return Fail(error, "zemax list needs a coating file: thinfilm zemax list COATING.DAT");
                var r = ThinFilmApi.ZemaxCoatings(file);
                if (!r.Ok) return Fail(error, r);
                if (o.Json) { output.WriteLine(JsonSerializer.Serialize(r.Value, Json)); return 0; }
                foreach (var c in r.Value!)
                    output.WriteLine($"  {c.Name,-28} {c.Layers,4} layer{(c.Layers == 1 ? " " : "s")}"
                                     + (c.ReferenceNm is { } w ? $"  designed at {w:0.###} nm" : "")
                                     + (c.Problem is { } p ? $"  cannot import: {p}" : ""));
                return 0;
            }
            case "import":
            {
                if (!OpenFile(api, o, 1, error, out var path, out var id)) return 1;
                var file = o.String("file");
                var coating = o.String("coating");
                if (file is null || coating is null)
                    return Fail(error, "zemax import needs --file COATING.DAT and --coating NAME");
                var r = api.ImportZemaxCoating(id, file, coating);
                if (!r.Ok) return Fail(error, r);
                foreach (var n in r.Value!.Notes) error.WriteLine($"thinfilm: {n}");
                return Wrote(api, o, id, path, $"imported {coating} from {file}", output);
            }
            case "export":
            {
                if (!OpenFile(api, o, 1, error, out _, out var id)) return 1;
                var file = o.String("file");
                if (file is null) return Fail(error, "zemax export needs --file COATING.DAT");
                var r = api.ExportZemaxCoating(id, new ZemaxExportRequest(
                    file, o.String("coating"), o.Double("from"), o.Double("to"), o.Flag("replace")));
                if (!r.Ok)
                    return r.Status == ThinFilmApi.CoatingExists
                        ? Fail(error, $"{r.Error}; add --replace to replace it")
                        : Fail(error, r);
                var e = r.Value!;
                if (o.Json) { output.WriteLine(JsonSerializer.Serialize(e, Json)); return 0; }
                output.WriteLine($"  {(e.Replaced ? "replaced" : "wrote")} COAT {e.Coating} in {file}");
                foreach (var (ours, theirs) in e.Materials)
                    output.WriteLine($"  {ours,-20} as MATE {theirs}{(e.Added.Contains(theirs) ? " (added)" : " (already there)")}");
                foreach (var n in e.Notes) output.WriteLine($"  note: {n}");
                if (e.Backup is not null) output.WriteLine($"  the file as it was is in {e.Backup}");
                return 0;
            }
            default:
                return Fail(error, "zemax needs an action: thinfilm zemax list|import|export ...");
        }
    }
}
