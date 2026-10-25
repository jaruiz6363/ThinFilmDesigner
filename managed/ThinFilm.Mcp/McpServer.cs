// McpServer.cs — the Model Context Protocol over stdio, by hand.
//
// STATEFUL, WHICH IS WHY THE API IS SESSION-BASED. The CLI is file in, file out because a shell
// process does not survive between invocations; an MCP server does, and a conversation with a
// model is exactly the case the session model was built for — open a design once, then refine it,
// look at its spectrum, insert a layer, look again, across a dozen turns without reloading. X1.7
// carries both shapes so neither front end has to fake the other.
//
// JSON-RPC 2.0, NEWLINE DELIMITED, no SDK. One JSON object per line in, one per line out. That is
// the whole framing, and writing it by hand costs about forty lines and keeps the no-package
// discipline the rest of the tree holds: this builds on a machine with no network.
//
// THE TWO KINDS OF FAILURE ARE NOT THE SAME KIND, and conflating them is the usual mistake.
//
//   A PROTOCOL error — unparseable JSON, an unknown method, a malformed request — is a JSON-RPC
//   `error` object. The client is broken and the model should never see it.
//
//   A TOOL error — a design that will not plan, a file that does not exist, a stage nobody
//   named — is a successful JSON-RPC RESULT carrying isError: true. The call worked; the answer
//   is that it could not be done, and the model is supposed to read that, understand it, and try
//   something else. Returning a protocol error there would hide the one thing the model needed.
//
// Everything that computes is behind ThinFilmApi. This file owns framing, schemas, and turning a
// result into text.

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace ThinFilm.Mcp;

public sealed class McpServer
{
    public const string ProtocolVersion = "2024-11-05";

    private readonly Tools _tools = new();

    /// <summary>
    /// RELAXED ESCAPING, BECAUSE A MODEL READS THIS. The default encoder escapes conservatively
    /// for contexts this is not in — HTML, mostly — so a quote becomes " and an apostrophe
    /// '. The output is valid JSON either way and readable only one way: a tool result is a
    /// JSON document embedded as a string inside another, so every quote in it was being escaped
    /// twice. Relaxed escaping is right for a stdio transport, where nothing downstream is going
    /// to interpret the text as markup.
    ///
    /// The TypeInfoResolver is what JsonNode.ToJsonString demands the moment any options are
    /// supplied at all. Without it the first non-primitive node throws and takes the server with
    /// it — which is what happened when the options asked only for the compact output that was
    /// already the default, and it happened on the SECOND message, after initialize had answered.
    /// </summary>
    public static readonly JsonSerializerOptions Wire = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        WriteIndented = false,
    };


    /// <summary>
    /// Reads requests until the input ends. Takes its reader and writer so the suite can drive it
    /// in process — a server whose only interface is a pipe is a server nobody tests.
    /// </summary>
    public void Serve(TextReader input, TextWriter output)
    {
        string? line;
        while ((line = input.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;

            JsonNode? request;
            try { request = JsonNode.Parse(line); }
            catch (JsonException e)
            {
                // No id is recoverable here: the request could not be read at all.
                Write(output, Error(null, -32700, $"parse error: {e.Message}"));
                continue;
            }

            if (request is not JsonObject obj)
            {
                Write(output, Error(null, -32600, "a request must be a JSON object"));
                continue;
            }

            var response = Handle(obj);
            if (response is not null) Write(output, response);
        }
    }

    /// <summary>Null when the message was a notification, which by definition gets no reply.</summary>
    public JsonObject? Handle(JsonObject request)
    {
        var id = request["id"];
        var method = request["method"]?.GetValue<string>();

        if (method is null) return Error(id, -32600, "a request needs a method");

        // A notification has no id. The protocol says: do not answer, ever, even to complain.
        bool isNotification = !request.ContainsKey("id");

        try
        {
            switch (method)
            {
                case "initialize":
                    return Result(id, Initialize());

                case "notifications/initialized":
                case "initialized":
                    return null;

                case "ping":
                    return isNotification ? null : Result(id, new JsonObject());

                case "tools/list":
                    return Result(id, new JsonObject { ["tools"] = _tools.Describe() });

                case "tools/call":
                {
                    var p = request["params"] as JsonObject;
                    var name = p?["name"]?.GetValue<string>();
                    if (name is null) return Error(id, -32602, "tools/call needs params.name");

                    var arguments = p?["arguments"] as JsonObject ?? new JsonObject();

                    // FROM HERE ON, FAILURE IS A RESULT AND NOT AN ERROR. The model asked for
                    // something reasonable and it did not work; that is an answer.
                    var (text, failed) = _tools.Call(name, arguments);
                    return Result(id, new JsonObject
                    {
                        ["content"] = new JsonArray(
                            new JsonObject { ["type"] = "text", ["text"] = text }),
                        ["isError"] = failed,
                    });
                }

                default:
                    return isNotification ? null
                         : Error(id, -32601, $"'{method}' is not a method this server implements");
            }
        }
        catch (Exception e)
        {
            // An unexpected throw is the server's fault, not the model's: a protocol error.
            return isNotification ? null : Error(id, -32603, $"internal error: {e.Message}");
        }
    }

    private JsonObject Initialize() => new()
    {
        ["protocolVersion"] = ProtocolVersion,
        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
        ["serverInfo"] = new JsonObject
        {
            ["name"] = "thinfilm",
            ["version"] = $"abi-{_tools.AbiVersion}",
        },
        // Read by a model before it calls anything, so the things it would otherwise have to
        // discover by failing are said up front.
        ["instructions"] =
            "Thin film coating design. Start with load_design on a sample design (list_samples "
            + "finds them) or new_design; either returns a design id every other tool "
            + "takes. Optimise with stages: refine (damped least "
            + "squares), global (multistart), needle (insert layers where the analytic needle map "
            + "says they help), evolve (gradual evolution: grows the design a layer at a time), thin (remove "
            + "layers too thin to deposit). Layers and merit rows count from 1, layer 1 next to the incident "
            + "medium, as the GUI's tables number them. Designs are JSON "
            + "documents: save_design to keep one, load_design to bring it back. Spectra are "
            + "computed by the engine on whatever grid you ask for, so keep steps modest unless "
            + "you need the detail.",
    };

    // ---- framing ---------------------------------------------------------------------------

    private static void Write(TextWriter output, JsonObject message)
    {
        // One object per line, and no embedded newlines: that is the entire stdio framing.
        //
        output.WriteLine(message.ToJsonString(Wire));
        output.Flush();
    }

    private static JsonObject Result(JsonNode? id, JsonNode result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["result"] = result,
    };

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };
}
