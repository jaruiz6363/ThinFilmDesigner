// OptilandCommands.cs — thinfilm optiland list|import|export: Optiland coatings (OptilandCoating.cs).

using System.Text.Json;
using ThinFilm.Api;

namespace ThinFilm.Cli;

public static partial class CommandLine
{
    private static int Optiland(ThinFilmApi api, Options o, TextWriter output, TextWriter error)
    {
        string? action = o.Positional.Count > 0 ? o.Positional[0] : null;
        switch (action)
        {
            case "list":
            {
                var file = o.String("file") ?? (o.Positional.Count > 1 ? o.Positional[1] : null);
                if (file is null) return Fail(error, "optiland list needs an Optiland file: thinfilm optiland list coating.json");
                var r = ThinFilmApi.OptilandCoatings(file);
                if (!r.Ok) return Fail(error, r);
                if (o.Json) { output.WriteLine(JsonSerializer.Serialize(r.Value, Json)); return 0; }
                foreach (var c in r.Value!)
                    output.WriteLine($"  {c.Index + 1,3}  {c.Name,-28} {c.Layers,4} layer{(c.Layers == 1 ? " " : "s")}"
                                     + (c.Problem is { } p ? $"  cannot import: {p}" : ""));
                return 0;
            }
            case "import":
            {
                if (!OpenFile(api, o, 1, error, out var path, out var id)) return 1;
                var file = o.String("file");
                if (file is null) return Fail(error, "optiland import needs --file coating.json [--coating N]");
                int number = (int)(o.Double("coating") ?? 1);
                var r = api.ImportOptilandCoating(id, file, number - 1);
                if (!r.Ok) return Fail(error, r);
                foreach (var n in r.Value!.Notes) error.WriteLine($"thinfilm: {n}");
                return Wrote(api, o, id, path, $"imported coating {number} from {file}", output);
            }
            case "export":
            {
                if (!OpenFile(api, o, 1, error, out _, out var id)) return 1;
                var file = o.String("file");
                if (file is null) return Fail(error, "optiland export needs --file coating.json");
                var r = api.ExportOptilandCoating(id, file);
                if (!r.Ok) return Fail(error, r);
                if (o.Json) { output.WriteLine(JsonSerializer.Serialize(r.Value, Json)); return 0; }
                output.WriteLine($"  wrote {r.Value!.Layers} layers to {file}, for load_obj_from_json(BaseCoating, path)");
                foreach (var n in r.Value.Notes) output.WriteLine($"  note: {n}");
                return 0;
            }
            default:
                return Fail(error, "optiland needs an action: thinfilm optiland list|import|export ...");
        }
    }
}
