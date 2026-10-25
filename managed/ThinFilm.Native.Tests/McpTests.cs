// McpTests.cs — X1.9, driven in process through the same entry point a client uses.
//
// McpServer.Serve takes a reader and a writer, so a test can feed it the exact bytes a client
// would and read back the exact bytes the client would get. Nothing is mocked and no process is
// spawned: the thing under test is the protocol implementation, and the only honest way to test a
// protocol is to speak it.
//
// THE CHECK THIS FILE EXISTS FOR is the distinction between a protocol error and a tool error,
// because getting it wrong is invisible until a model is confused by it. An unknown METHOD is the
// client being broken: a JSON-RPC error the model never sees. An unknown TEMPLATE is the model
// being wrong about something knowable: a successful result carrying isError, which the model
// reads and acts on. A server that returned a protocol error for the second would hide the one
// piece of information the model needed to recover.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ThinFilm.Mcp;

namespace ThinFilm.Native.Tests;

public static class McpTests
{
    /// <summary>One conversation: every line in, every line out, in order.</summary>
    private static List<JsonObject> Converse(params string[] requests)
    {
        var input = new StringReader(string.Join("\n", requests) + "\n");
        var output = new StringWriter();
        new McpServer().Serve(input, output);

        var replies = new List<JsonObject>();
        foreach (var line in output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries))
            replies.Add((JsonObject)JsonNode.Parse(line.Trim())!);
        return replies;
    }

    /// <summary>
    /// Built by concatenation rather than an interpolated raw string: the JSON ends in two closing
    /// braces right after an interpolation hole, and getting a raw string to emit that needs more
    /// dollar signs than the line is worth.
    /// </summary>
    private static string Call(string name, string argumentsJson = "{}", int id = 1) =>
        "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"tools/call\",\"params\":{\"name\":\""
        + name + "\",\"arguments\":" + argumentsJson + "}}";

    private static (JsonNode Payload, bool IsError) ToolResult(JsonObject reply)
    {
        var result = reply["result"] as JsonObject
                     ?? throw new InvalidOperationException("no result on the reply");
        bool isError = result["isError"]?.GetValue<bool>() ?? false;
        var text = result["content"]![0]!["text"]!.GetValue<string>();
        return (JsonNode.Parse(text)!, isError);
    }

    public static void TheHandshakeWorks(Harness h)
    {
        h.Section("initialize, the initialized notification, and tools/list");

        var replies = Converse(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""",
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");

        // TWO REPLIES, NOT THREE: a notification has no id and gets no answer, ever.
        h.Pin(replies.Count, 2, "the notification was not answered");

        var init = replies[0]["result"] as JsonObject;
        h.Expect(init is not null, "initialize returned a result");
        h.Expect(init!["protocolVersion"]!.GetValue<string>() == McpServer.ProtocolVersion,
                 "with the protocol version");
        h.Expect(init["capabilities"]!["tools"] is not null, "declaring the tools capability");
        h.Expect(init["serverInfo"]!["version"]!.GetValue<string>()
                     .Contains(TfAbi.TF_ABI_VERSION.ToString(), StringComparison.Ordinal),
                 "and naming the ABI it is bound to");
        h.Expect(init["instructions"]!.GetValue<string>().Contains("needle", StringComparison.Ordinal),
                 "and telling a model what the stages are before it has to guess");

        var tools = replies[1]["result"]!["tools"]!.AsArray();
        h.Expect(tools.Count >= 12, $"tools/list returns the tools ({tools.Count})");

        foreach (var t in tools)
        {
            var name = t!["name"]!.GetValue<string>();
            h.Expect(t["description"]!.GetValue<string>().Length > 40,
                     $"{name}: has a description worth reading");
            h.Expect(t["inputSchema"]!["type"]!.GetValue<string>() == "object",
                     $"{name}: has an object input schema");
        }
    }

    /// <summary>
    /// THE DISTINCTION. A broken client gets a protocol error; a model that asked for something
    /// impossible gets a result it can read.
    /// </summary>
    public static void AProtocolErrorAndAToolErrorAreDifferentThings(Harness h)
    {
        h.Section("an unknown method is a JSON-RPC error; a missing design file is a tool result");

        var replies = Converse(
            """{"jsonrpc":"2.0","id":1,"method":"nonsense"}""",
            Call("load_design", SampleFiles.LoadArgs("does-not-exist"), 2),
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{}}""",
            "this is not json at all");

        h.Expect(replies[0]["error"] is not null, "an unknown method is an error");
        h.Pin(replies[0]["error"]!["code"]!.GetValue<int>(), -32601, "method not found");
        h.Expect(replies[0]["result"] is null, "and carries no result");

        h.Expect(replies[1]["error"] is null,
                 "a missing file is NOT a protocol error — the call itself worked");
        var (payload, isError) = ToolResult((JsonObject)replies[1]);
        h.Expect(isError, "it is a result with isError set");
        h.Expect(payload["error"]!.GetValue<string>().Contains("does-not-exist", StringComparison.Ordinal),
                 "naming what was asked for, so the model can fix it");

        h.Pin(replies[2]["error"]!["code"]!.GetValue<int>(), -32602,
              "tools/call without a name is an invalid-params error");

        h.Pin(replies[3]["error"]!["code"]!.GetValue<int>(), -32700,
              "unparseable input is a parse error");
        h.Expect(replies.Count == 4, "and the server kept going after every one of them");
    }

    /// <summary>
    /// A conversation, which is what this front end is for: the session persists across calls and
    /// a design opened on turn one is still there on turn six.
    /// </summary>
    public static void ASessionSurvivesAcrossCalls(Harness h)
    {
        h.Section("open a design once, then work on it across several turns");

        var replies = Converse(
            Call("load_design", SampleFiles.LoadArgs("AR-visible"), 1),
            Call("describe_design", """{"design":"d1"}""", 2),
            Call("optimise_design", """{"design":"d1","stages":"refine","iterations":150}""", 3),
            Call("describe_design", """{"design":"d1"}""", 4),
            Call("set_thickness", """{"design":"d1","layer":1,"thickness":61.5}""", 5),
            Call("describe_design", """{"design":"d1"}""", 6),
            Call("save_design", """{"design":"d1"}""", 7));

        var (created, createdFailed) = ToolResult((JsonObject)replies[0]);
        h.Expect(!createdFailed, "the design opens");
        h.Expect(created["Id"]!.GetValue<string>() == "d1", "with an id the later calls use");

        double start = ToolResult((JsonObject)replies[1]).Payload["Merit"]!.GetValue<double>();
        var (optimised, optFailed) = ToolResult((JsonObject)replies[2]);
        h.Expect(!optFailed, "refine runs");
        double after = ToolResult((JsonObject)replies[3]).Payload["Merit"]!.GetValue<double>();

        h.Expect(after < start, $"and the SESSION holds the improved design ({start:G6} -> {after:G6})");
        h.PinExact(after, optimised["FinalMerit"]!.GetValue<double>(),
                   "describe agrees with what optimise reported");

        var (_, setFailed) = ToolResult((JsonObject)replies[4]);
        h.Expect(!setFailed, "a thickness can be set");
        double moved = ToolResult((JsonObject)replies[5]).Payload["Merit"]!.GetValue<double>();
        h.Expect(moved != after, "and the change is visible on the next call");

        var (saved, saveFailed) = ToolResult((JsonObject)replies[6]);
        h.Expect(!saveFailed, "and the design saves");
        h.Expect(saved.ToJsonString().Contains("Layers", StringComparison.Ordinal),
                 "to a document with a layer table in it");
    }

    /// <summary>
    /// The MCP layer computes nothing, so the only thing it can get wrong about a number is
    /// carrying a different one. Checked against the API directly.
    /// </summary>
    public static void TheNumbersAreTheApisOwn(Harness h)
    {
        h.Section("what the tools return is what the API returned, to the last bit");

        var replies = Converse(
            Call("load_design", SampleFiles.LoadArgs("Bandpass"), 1),
            Call("spectrum", """{"design":"d1","from":540,"to":560,"steps":5,"quantities":"TA"}""", 2),
            Call("evaluate_design", """{"design":"d1"}""", 3));

        var api = new Api.ThinFilmApi();
        var id = api.OpenSample("Bandpass").Value!.Id;
        var expectedSpectrum = api.Spectrum(id, new Api.SpectrumRequest(540, 560, 5, 0.0, ["TA"])).Value!;
        var expectedEval = api.Evaluate(id).Value!;

        var spectrum = ToolResult((JsonObject)replies[1]).Payload;
        var ta = spectrum["Quantity"]!["TA"]!.AsArray();
        h.Pin(ta.Count, 5, "five spectrum points");
        for (int i = 0; i < 5; ++i)
            h.PinExact(ta[i]!.GetValue<double>(), expectedSpectrum.Quantity["TA"][i],
                       $"spectrum point {i} matches the API exactly");

        var evaluation = ToolResult((JsonObject)replies[2]).Payload;
        h.PinExact(evaluation["Merit"]!.GetValue<double>(), expectedEval.Merit,
                   "the merit matches the API exactly");
    }

    public static void TheNeedleMapAndShapeChangesAreReachable(Harness h)
    {
        h.Section("a model can see where a layer would help, and add or remove one");

        var replies = Converse(
            Call("load_design", SampleFiles.LoadArgs("AR-visible"), 1),
            Call("needle_map", """{"design":"d1","samples":8}""", 2),
            Call("insert_layer", """{"design":"d1","before":2,"material":1,"thickness":30}""", 3),
            Call("remove_layer", """{"design":"d1","layer":2}""", 4),
            Call("remove_layer", """{"design":"d1","layer":99}""", 5),
            Call("remove_layer", """{"design":"d1","layer":0}""", 6));

        var before = ToolResult((JsonObject)replies[0]).Payload["Layers"]!.GetValue<int>();

        var (map, mapFailed) = ToolResult((JsonObject)replies[1]);
        h.Expect(!mapFailed, "the needle map comes back");
        h.Pin(map["Depth"]!.AsArray().Count, 8, "with the requested depth samples");
        h.Expect(map["BestDepth"] is not null, "and names where the best insertion would go");

        var (inserted, insertFailed) = ToolResult((JsonObject)replies[2]);
        h.Expect(!insertFailed, "a layer inserts");
        h.Pin(inserted["Layers"]!.GetValue<int>(), before + 1, "and the stack is one longer");

        var (removed, removeFailed) = ToolResult((JsonObject)replies[3]);
        h.Expect(!removeFailed, "and removes again");
        h.Pin(removed["Layers"]!.GetValue<int>(), before, "leaving it as it was");

        var (_, badRemove) = ToolResult((JsonObject)replies[4]);
        h.Expect(badRemove, "removing a layer that does not exist is a tool error, not a crash");
        var (zero, zeroFailed) = ToolResult((JsonObject)replies[5]);
        h.Expect(zeroFailed && zero.ToJsonString().Contains("counts from 1"),
                 "layer 0 is refused: layers count from 1, as the GUI's Design tab numbers them");
    }

    /// <summary>
    /// A missing or mistyped argument is the model's mistake and the model can fix it — so it has
    /// to be told, which means a tool result rather than a protocol error it never sees.
    /// </summary>
    public static void BadArgumentsComeBackAsSomethingAModelCanAct0n(Harness h)
    {
        h.Section("a missing or mistyped argument is a tool error naming the argument");

        var replies = Converse(
            Call("load_design", "{}", 1),
            Call("spectrum", """{"design":"d1","from":400,"to":700}""", 2),
            Call("no_such_tool", "{}", 3),
            Call("describe_design", """{"design":"d404"}""", 4));

        var (missing, missingFailed) = ToolResult((JsonObject)replies[0]);
        h.Expect(missingFailed, "load_design with neither document nor path fails");
        h.Expect(missing["error"]!.GetValue<string>().Contains("path", StringComparison.Ordinal),
                 $"naming the argument: {missing["error"]}");

        var (noSteps, noStepsFailed) = ToolResult((JsonObject)replies[1]);
        h.Expect(noStepsFailed, "spectrum without steps fails");
        h.Expect(noSteps["error"]!.GetValue<string>().Contains("steps", StringComparison.Ordinal),
                 $"naming the argument: {noSteps["error"]}");

        var (unknown, unknownFailed) = ToolResult((JsonObject)replies[2]);
        h.Expect(unknownFailed, "an unknown TOOL is a tool error, not a protocol error");
        h.Expect(unknown["error"]!.GetValue<string>().Contains("tools/list", StringComparison.Ordinal),
                 "telling the model how to find the real ones");

        var (noSession, noSessionFailed) = ToolResult((JsonObject)replies[3]);
        h.Expect(noSessionFailed, "an unknown design id fails");
        h.Expect(noSession["error"]!.GetValue<string>().Contains("d404", StringComparison.Ordinal),
                 "naming the id that was not found");
    }

    public static void ANumberIsANumberHoweverItIsHeld(Harness h)
    {
        h.Section("a numeric argument is read whether it holds an int, a long, a decimal, a double or a string");

        // Parsed JSON holds every number the same way, but a host that builds its arguments in
        // code holds what it was given: an Int32 for steps = 3 was refused as not a double.
        var tools = new Tools();
        string id = JsonNode.Parse(tools.Call("load_design", new JsonObject { ["path"] = SampleFiles.PathOf("AR-visible") }).Item1)!["Id"]!.GetValue<string>();
        foreach (var (kind, steps) in new (string, JsonNode)[]
                 { ("int", 3), ("long", 3L), ("decimal", 3m), ("double", 3.0), ("string", "3") })
        {
            var (text, failed) = tools.Call("spectrum", new JsonObject
                { ["design"] = id, ["from"] = 500, ["to"] = 600m, ["steps"] = steps, ["quantities"] = "RA" });
            h.Expect(!failed && JsonNode.Parse(text)!["Wavelength"]!.AsArray().Count == 3, $"steps as {kind} ({(failed ? text : "")})");
        }

        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var (text, failed) = tools.Call("spectrum", new JsonObject
                { ["design"] = id, ["from"] = "500.5", ["to"] = "600.5", ["steps"] = 2, ["quantities"] = "RA" });
            h.Expect(!failed && JsonNode.Parse(text)!["Wavelength"]![0]!.GetValue<double>() == 500.5,
                     $"and \"500.5\" is 500.5 on a German machine, not 5005 ({(failed ? text : "")})");
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    /// <summary>
    /// The transport is text a model reads. Escaping it twice makes it unreadable, which is a
    /// real defect rather than a cosmetic one.
    ///
    /// Checked against the bytes the server ACTUALLY WROTE, not against a re-serialisation of the
    /// parsed reply — re-serialising would test this test's own encoder settings and say nothing
    /// about the server's.
    /// </summary>
    public static void LayersAndRowsCountFromOne(Harness h)
    {
        h.Section("MCP counts layers and merit rows from 1, as the GUI and the CLI do, in and out");

        // The user (2026-10-25): "Most users are not used to 0-based layer index." Layer 2 given
        // to a tool is the Design tab's layer 2, and evaluate_design calls it 2.
        var replies = Converse(
            Call("new_design", """{"name":"n","materials":"TiO2,SiO2","substrate":"N-BK7","layers":4}""", 1),
            Call("edit_layer", """{"design":"d1","action":"set","layer":4,"pickup":2}""", 2),
            Call("edit_stack", """{"design":"d1","action":"bounds","first":2,"last":3,"min":10,"max":200}""", 3),
            Call("evaluate_design", """{"design":"d1"}""", 4));
        var (bounds, boundsFailed) = ToolResult((JsonObject)replies[2]);
        var (evaluated, _) = ToolResult((JsonObject)replies[3]);
        var layers = evaluated["Layers"]!.AsArray();
        h.Expect(layers.Select(l => l!["Index"]!.GetValue<int>()).SequenceEqual([1, 2, 3, 4]),
                 "evaluate_design numbers the layers 1 to 4");
        h.Expect(layers[3]!["Pickup"]!["Parent"]!.GetValue<int>() == 2 && layers[1]!["Pickup"] is null,
                 "layer 4 picks up layer 2, and says 2");
        h.Expect(evaluated["Operands"]![0]!["Index"]!.GetValue<int>() == 1, "the first merit row is row 1");
        h.Expect(!boundsFailed && bounds["Set"]!.AsArray().Select(n => n!.GetValue<int>()).SequenceEqual([2, 3]),
                 $"bounds on layers 2 to 3 report layers 2 and 3 ({bounds.ToJsonString()})");

        // ProfileLayer's Index is a refractive index, not a position: left as it is.
        var profile = JsonNode.Parse("""{"Layers":[{"Material":"TiO2","Thickness":50.0,"Index":2}]}""");
        h.Expect(Tools.Numbered(profile)!["Layers"]![0]!["Index"]!.GetValue<double>() == 2,
                 "a stack profile's refractive index is not renumbered");
    }

    public static void TheTextIsReadable(Harness h)
    {
        h.Section("tool text is not escaped into unreadability");

        var input = new StringReader(Call("load_design", SampleFiles.LoadArgs("AR-visible"), 1) + "\n"
                                     + Call("describe_design", """{"design":"d1"}""", 2) + "\n");
        var output = new StringWriter();
        new McpServer().Serve(input, output);
        var raw = output.ToString();

        h.Expect(raw.Length > 0, "the server wrote something");
        // The six characters backslash-u-0-0-2-2, built from a char code rather than written as
        // a literal: every escaping layer between here and the file wants to turn that sequence
        // into the quote it encodes, which is precisely the bug being tested for.
        string quoteEscape = (char)92 + "u0022";
        string apostropheEscape = (char)92 + "u0027";

        h.Expect(!raw.Contains(quoteEscape, StringComparison.Ordinal),
                 "quotes are not escaped into their unicode form");
        h.Expect(!raw.Contains(apostropheEscape, StringComparison.Ordinal),
                 "nor apostrophes");
        var lines = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        h.Pin(lines.Length, 2, "and each reply is one line, which is the entire stdio framing");

        var (payload, _) = ToolResult((JsonObject)JsonNode.Parse(lines[1].Trim())!);
        h.Expect(payload.ToJsonString().Contains("AR-visible", StringComparison.Ordinal),
                 "and the payload is the design described");
    }
}
