// MeritTableTests.cs — building a merit function in bulk: Clear all, Delete operands, Adjust
// weight and the three Add operands, through the API (MeritTable.cs), the CLI and the MCP server.
//
// THE SAME ROUTINE BEHIND THREE FRONT ENDS, so the API is tested for what the filters mean and the
// CLI and MCP only for reaching it with the same answer.

using System.Text.Json.Nodes;
using ThinFilm.Api;
using ThinFilm.Cli;
using ThinFilm.Gui.Core;
using ThinFilm.Mcp;

namespace ThinFilm.Native.Tests;

public static class MeritTableTests
{
    /// <summary>AR-visible: 27 RA rows at most 0.005, 420 to 680 nm every 10 nm at 0°, and MINT and MAXT.</summary>
    private static (ThinFilmApi Api, string Id) ArVisible()
    {
        var api = new ThinFilmApi();
        return (api, api.OpenSample("AR-visible").Value!.Id);
    }

    public static void TheFiltersMeanWhatTheySay(Harness h)
    {
        h.Section("merit filters: a blank field filters nothing; wavelengths, codes, modes and AOI tests");

        var (api, id) = ArVisible();
        var rows = api.Operands(id).Value!;
        int optical = rows.Count(r => r.Wavelengths.Length > 0);
        h.Pin(rows.Length, 29, "AR-visible has 29 rows");
        h.Pin(optical, 27, "27 of them at a wavelength");

        int Count(OperandFilter f) => api.MatchOperands(id, f).Value?.Length ?? -1;
        h.Pin(Count(new OperandFilter()), 29, "no filter: every row, stack rows included");
        h.Pin(Count(new OperandFilter(500, 600)), 11, "500 to 600 nm: 11 rows, both ends included");
        h.Pin(Count(new OperandFilter(WavelengthFrom: 600)), 9, "from 600 nm, open above");
        h.Pin(Count(new OperandFilter(Codes: ["MINT"])), 1, "a code: MINT");
        h.Pin(Count(new OperandFilter(Codes: ["R"])), 27, "a type: R means RA, RS and RP");
        h.Pin(Count(new OperandFilter(Codes: [""])), 29, "a blank code filters nothing");
        h.Pin(Count(new OperandFilter(Modes: ["Max"])), 29, "Max, as the table names it: every row is at most");
        h.Pin(Count(new OperandFilter(Modes: ["LE"])), 29, "and LE, as the document does");
        h.Pin(Count(new OperandFilter(Modes: ["Min"])), 0, "Min: none");
        h.Pin(Count(new OperandFilter(Angle: new AngleTest("below", 1))), 27, "AOI < 1°: the optical rows, never a stack row");
        h.Pin(Count(new OperandFilter(Angle: new AngleTest("above", 0))), 0, "AOI > 0° is strict: none");
        h.Pin(Count(new OperandFilter(Angle: new AngleTest("between", 0, 0))), 27, "AOI between 0 and 0 is inclusive");

        var typo = api.MatchOperands(id, new OperandFilter(Codes: ["RX"]));
        h.Expect(!typo.Ok && typo.Error!.Contains("not a target code"), $"a mistyped code is refused, not matched to nothing: {typo.Error}");
        var reversed = api.MatchOperands(id, new OperandFilter(600, 500));
        h.Expect(!reversed.Ok, $"and so is a reversed band: {reversed.Error}");
    }

    public static void ClearDeleteWeightAndAddEditTheTable(Harness h)
    {
        h.Section("Clear all, Delete operands, Adjust weight, and Add operands (one AOI, many AOI, one wavelength)");

        var (api, id) = ArVisible();

        var weighted = api.SetOperandWeights(id, new OperandFilter(500, 600), 4.0);
        h.Expect(weighted.Ok && weighted.Value!.Rows == 11, $"11 rows reweighted ({weighted.Value?.Rows})");
        var rows = api.Operands(id).Value!;
        h.Expect(rows.Count(r => r.Weight == 4.0) == 11 && rows.Count(r => r.Weight != 4.0) == 18, "and only those");
        h.Expect(!api.SetOperandWeights(id, new OperandFilter(), -1).Ok, "a negative weight is refused");

        var deleted = api.DeleteOperands(id, new OperandFilter(Codes: ["MINT", "MAXT"]));
        h.Expect(deleted.Ok && deleted.Value!.Rows == 2 && deleted.Value.Design.Operands == 27,
                 $"MINT and MAXT deleted, 27 left ({deleted.Value?.Design.Operands})");

        var cleared = api.ClearOperands(id);
        h.Expect(cleared.Ok && cleared.Value!.Rows == 27 && cleared.Value.Design.Operands == 0, "Clear all removes the other 27");
        h.Expect(cleared.Value!.Design.Blocked == ThinFilmApi.EmptyMeritReason && double.IsNaN(cleared.Value.Design.Merit),
                 $"and the design is held, not evaluated: {cleared.Value.Design.Blocked}");
        h.Expect(api.Spectrum(id, new SpectrumRequest(400, 700, 11)).Ok, "its spectrum still computes");
        h.Expect(!api.Optimise(id, new OptimiseRequest()).Ok, "but there is nothing to optimise");
        var saved = api.Save(id).Value!;
        var reopened = new ThinFilmApi().Load(saved);
        h.Expect(reopened.Ok && reopened.Value!.Blocked == ThinFilmApi.EmptyMeritReason, "saved empty, it reopens empty");

        var band = api.AddOperands(id, new OperandSweep("RA", "Max", 0.005, 1.0, 420, 680, 20));
        h.Expect(band.Ok && band.Value!.Rows == 14, $"Add operands: 420 to 680 nm every 20 nm is 14 rows ({band.Value?.Rows})");
        h.Expect(band.Value!.Design.Blocked is null && band.Value.Design.Merit >= 0, "and the design is evaluated again");

        var many = api.AddOperands(id, new OperandSweep("TA", "Min", 0.95, 2.0, 450, 650, 50, Angle: 0, AngleTo: 60, AngleStep: 30));
        h.Expect(many.Ok && many.Value!.Rows == 15, $"Add multiple AOI: 5 wavelengths at 0, 30 and 60° is 15 rows ({many.Value?.Rows})");
        var added = api.Operands(id).Value!.Skip(14).ToArray();
        h.Expect(added.Take(5).All(r => r.AngleDegrees == 0) && added.Skip(10).All(r => r.AngleDegrees == 60),
                 "ordered angle by angle");
        h.Expect(added.All(r => r.Code == "TA" && r.Comparison == "GE" && r.Target == 0.95 && r.Weight == 2.0),
                 "each with the code, mode, target and weight asked for");

        var single = api.AddOperands(id, new OperandSweep("RP", "Range", 0.0, 1.0, 550, Angle: 10, AngleTo: 40, AngleStep: 10, TargetHi: 0.01));
        h.Expect(single.Ok && single.Value!.Rows == 4, $"Add single Wv: 550 nm at 10, 20, 30, 40° is 4 rows ({single.Value?.Rows})");
        var last = api.Operands(id).Value![^1];
        h.Expect(last.Wavelengths.SequenceEqual([550.0]) && last.AngleDegrees == 40 && last.TargetHi == 0.01, "a Range keeps its upper target");

        h.Expect(api.AddOperands(id, new OperandSweep("MINT", "Max", 0, 1, 550)).Error?.Contains("Insert") == true,
                 "a stack code cannot be swept");
        h.Expect(api.AddOperands(id, new OperandSweep("RA", "Max", 0, 1, 400, 700, 0.01)).Error?.Contains("at most") == true,
                 "a sweep of 30001 rows is refused as the typing mistake it is");
        h.Expect(api.AddOperands(id, new OperandSweep("RA", "Max", 0, 1, 550, Angle: 95)).Error?.Contains("angle") == true,
                 "an AOI of 95° is refused");
        h.Pin(api.Operands(id).Value!.Length, 33, "and none of the refusals changed the table");
    }

    public static void TheDialogFormsReadTheirBoxes(Harness h)
    {
        h.Section("the dialogs' forms: blank boxes, µm, and which box is wrong");

        var f = new OperandFilterForm("um") { Wavelength1 = "0.5", Wavelength2 = "", CodeChoice = "R (RA, RS, RP)" };
        var filter = f.ToFilter(out var problem);
        h.Expect(filter is not null && filter.WavelengthFrom == 500 && filter.WavelengthTo is null
                 && filter.Codes!.SequenceEqual(["R"]) && filter.Modes is null && filter.Angle is null,
                 $"0.5 µm is 500 nm, a blank WV2 is open, R is a type, (any) is no filter ({problem})");
        f.AngleTest = "AOI between"; f.Angle1 = "10"; f.Angle2 = "";
        h.Expect(f.ToFilter(out problem) is null && problem!.Contains("second angle"), $"between needs two angles: {problem}");

        var add = new OperandSweepForm(SweepKind.Band, "nm") { Wavelength1 = "420", Wavelength2 = "680", WavelengthStep = "10" };
        h.Expect(add.RowCount() == 27, $"Add operands, 420 to 680 every 10: 27 rows ({add.RowCount()})");
        add.WavelengthStep = "";
        h.Expect(add.ToSweep(out problem) is null && problem!.Contains("Step WV"), $"a blank Step WV is named: {problem}");

        var one = new OperandSweepForm(SweepKind.OneWavelengthManyAngles, "nm")
            { Wavelength1 = "550", Angle1 = "0", Angle2 = "60", AngleStep = "15" };
        h.Expect(one.RowCount() == 5 && !one.HasBand, $"Add single Wv: 550 nm at 0 to 60 every 15 is 5 rows, no WV2 asked ({one.RowCount()})");
    }

    public static void TheCliAndMcpReachTheSameRoutine(Harness h)
    {
        h.Section("thinfilm merit and the MCP operand tools: the same edits, the same counts");

        var dir = Path.Combine(Path.GetTempPath(), "thinfilm-cli-tests");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "merit.tfd");
        File.Delete(path);
        (int, string, string) Run(params string[] args)
        {
            var o = new StringWriter(); var e = new StringWriter();
            return (CommandLine.Run(args, o, e), o.ToString(), e.ToString());
        }

        File.Copy(SampleFiles.PathOf("AR-visible"), path, overwrite: true);
        var (code, output, error) = Run("merit", "delete", path, "--from", "600", "--codes", "RA");
        h.Expect(code == 0 && output.Contains("9 rows deleted"), $"merit delete --from 600 --codes RA: {output}{error}");
        (code, output, error) = Run("merit", "weight", path, "--weight", "5", "--aoi-between", "0,0");
        h.Expect(code == 0 && output.Contains("18 rows reweighted"), $"merit weight, written back to the file it read: {output}{error}");
        (code, output, error) = Run("merit", "add", path, "--code", "RA", "--mode", "Max", "--target", "0.01",
                                    "--from", "600", "--to", "680", "--step", "20", "--aoi-from", "0", "--aoi-to", "45", "--aoi-step", "45");
        h.Expect(code == 0 && output.Contains("10 rows added") && output.Contains("30 operands"), $"merit add: {output}{error}");
        (code, output, error) = Run("merit", "clear", path);
        h.Expect(code == 0 && output.Contains("not evaluated"), $"merit clear: {output}{error}");
        h.Expect(new ThinFilmApi().Load(File.ReadAllText(path)).Value?.Operands == 0, "and the file holds the empty table");
        (code, output, error) = Run("show", path, "--json");
        h.Expect(code == 0 && output.Contains("\"Merit\": \"NaN\""), $"show --json of it writes its merit as NaN, not an error: {error}");
        h.Expect(Run("merit", "weight", path).Item1 == 1, "merit weight without --weight exits non-zero");

        string Call(string name, string args, int n) =>
            "{\"jsonrpc\":\"2.0\",\"id\":" + n + ",\"method\":\"tools/call\",\"params\":{\"name\":\"" + name + "\",\"arguments\":" + args + "}}";
        var input = new StringReader(string.Join("\n",
            Call("load_design", SampleFiles.LoadArgs("AR-visible"), 1),
            Call("delete_operands", """{"design":"d1","from":500,"to":600}""", 2),
            Call("set_operand_weights", """{"design":"d1","codes":"MINT,MAXT","weight":3}""", 3),
            Call("add_operands", """{"design":"d1","code":"TA","mode":"Min","target":0.9,"from":550,"aoi":0,"aoi_to":30,"aoi_step":15}""", 4),
            Call("clear_operands", """{"design":"d1"}""", 5),
            Call("add_operands", """{"design":"d1","code":"MINT","mode":"Max","target":0,"from":550}""", 6)) + "\n");
        var writer = new StringWriter();
        new McpServer().Serve(input, writer);
        var replies = writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => (JsonObject)JsonNode.Parse(l.Trim())!).ToArray();
        (JsonNode, bool) Result(int i)
        {
            var r = replies[i]["result"]!;
            return (JsonNode.Parse(r["content"]![0]!["text"]!.GetValue<string>())!, r["isError"]?.GetValue<bool>() ?? false);
        }
        h.Expect(Result(1).Item1["Rows"]!.GetValue<int>() == 11, "delete_operands: 11 rows, 500 to 600 nm");
        h.Expect(Result(2).Item1["Rows"]!.GetValue<int>() == 2, "set_operand_weights: MINT and MAXT");
        h.Expect(Result(3).Item1["Rows"]!.GetValue<int>() == 3, "add_operands: 550 nm at 0, 15 and 30°");
        var (clearedReply, clearFailed) = Result(4);
        h.Expect(!clearFailed && clearedReply["Design"]?["Blocked"]?.GetValue<string>() == ThinFilmApi.EmptyMeritReason
                 && clearedReply["Design"]?["Merit"]?.GetValue<string>() == "NaN",
                 $"clear_operands: the design held, its merit NaN - not a serialiser error ({clearedReply.ToJsonString()[..Math.Min(160, clearedReply.ToJsonString().Length)]})");
        h.Expect(Result(5).Item2, "and a stack code swept is a tool error a model can read");
    }

    public static void ASpanIsItsAverageAndDeltaTheShortestArc(Harness h)
    {
        h.Section("a row over a span of an ordinary code is its average; a Delta target is missed by the shortest arc");

        var (api, id) = ArVisible();
        api.ClearOperands(id);
        h.Expect(api.AddOperands(id, new OperandSweep("RA", "Max", 0.01, 1.0, 550)).Ok, "one RA row at 550 nm");
        h.Expect(api.EditOperand(id, 0, new OperandChange(Wavelength: 500, Wavelength2: 600, Points: 11)).Ok,
                 "made a span, 500 to 600 nm in 11 points");
        double value = api.Evaluate(id).Value!.Operands[0].Value;
        var ra = api.Spectrum(id, new SpectrumRequest(500, 600, 11, Quantities: ["RA"])).Value!.Quantity["RA"];
        h.Expect(Math.Abs(value - ra.Average()) < 1e-12,
                 $"its value is the mean RA over the 11 points ({value:G6} against {ra.Average():G6}), not their sum ({ra.Sum():G6})");

        // A Delta target 2π - 0.01 rad above the value: by plain subtraction nearly 2π out, by
        // the shortest arc 0.01.
        api.ClearOperands(id);
        h.Expect(api.AddOperands(id, new OperandSweep("RA", "Target", 0.0, 1.0, 550, Angle: 60)).Ok
                 && api.EditOperand(id, 0, new OperandChange(Code: "R_DELTA")).Ok, "a Delta row at 60°");
        double delta = api.Evaluate(id).Value!.Operands[0].Value;
        double target = delta + 2.0 * Math.PI - 0.01;
        h.Expect(api.EditOperand(id, 0, new OperandChange(Mode: "Target", Target: target)).Ok, "held equal to Delta + 2π - 0.01");
        var row = api.Evaluate(id).Value!.Operands[0];
        h.Expect(Math.Abs(Math.Abs(row.Residual * row.Scale) - 0.01) < 1e-9,
                 $"the miss is 0.01 rad, not 2π - 0.01 (residual {row.Residual:G6}, scale {row.Scale:G6})");
    }
}
