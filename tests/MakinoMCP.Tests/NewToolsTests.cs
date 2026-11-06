using System.Text.Json;
using MakinoMCP.Core.Cell;
using MakinoMCP.Core.Folders;
using MakinoMCP.Core.Settings;
using MakinoMCP.Tools;
using Xunit;
using Xunit.Abstractions;
using static MakinoMCP.Core.Cell.SqlMakinoReader;

namespace MakinoMCP.Tests;

/// <summary>get_macro_values, get_work_set, find_serial and explain_ade_file: the instruction rules, the SQL shapes,
/// the ADE rules and the tools' JSON.</summary>
public sealed class NewToolsTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "makinomcp-newtools-" + Guid.NewGuid().ToString("N"));
    private readonly string _ade;

    public NewToolsTests()
    {
        _ade = Path.Combine(_base, "ade");
        Directory.CreateDirectory(_ade);
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, true); }
        catch (Exception) { /* temp folder; leaving it behind is harmless */ }
    }

    // ------------------------------------------------------------------ work-set instruction rules

    private static WsCommand Cmd(int type, int status, int clamp = 1) => new(4, 10, type, status) { ClampQuantity = clamp };

    [Theory]
    // type, status, clamp qty, expected instruction, expected leaves-as (0 = none)
    [InlineData(1, 2, 1, WsShowClamp, WsLeavesMachining)]
    [InlineData(1, 3, 1, WsShowClamp, WsLeavesMachiningSuspend)]
    [InlineData(1, 1, 1, WsShowClampCancel, WsLeavesHoldClamp)]
    [InlineData(1, 2, 0, WsShowNothing, 0)]
    [InlineData(4, 1, 0, WsShowUnclamp, 0)]
    [InlineData(4, 4, 0, WsShowUnclampCancel, WsLeavesHoldUnclamp)]
    [InlineData(5, 5, 0, WsShowUnclampCancel, WsLeavesHoldUnclamp)]
    [InlineData(2, 2, 1, WsShowUnclampClamp, WsLeavesMachining)]
    [InlineData(2, 3, 1, WsShowUnclampClamp, WsLeavesMachiningSuspend)]
    [InlineData(2, 2, 0, WsShowUnclamp, 0)]
    [InlineData(2, 1, 1, WsShowUnclampClampCancel, WsLeavesHoldClamp)]
    [InlineData(11, 0, 0, WsShowExchangeFixture, 0)]
    [InlineData(10, 0, 0, WsShowInspection, 0)]
    [InlineData(6, 6, 0, WsShowOther, 0)]
    [InlineData(6, 7, 0, WsShowOtherCancel, WsLeavesHoldOther)]
    [InlineData(0, 0, 0, WsShowNothing, 0)]
    public void Instruction_follows_the_monitor_rules(int type, int status, int clamp, int show, int leaves)
    {
        var i = WorkSetInstruction(Cmd(type, status, clamp), moveCancelled: false);

        Assert.Equal(show, i.Show);
        Assert.Equal(leaves == 0 ? null : leaves, i.LeavesAs);
    }

    [Fact]
    public void Scrap_clamp_cancel_remachining_and_reunclamp_change_the_instruction()
    {
        Assert.Equal(WsShowUnclampScrap, WorkSetInstruction(Cmd(5, 1) with { Scrap = true }, false).Show);
        Assert.Equal(WsShowUnclampScrapClamp, WorkSetInstruction(Cmd(3, 2) with { Scrap = true }, false).Show);
        Assert.Equal(WsShowUnclampScrapClampCancel, WorkSetInstruction(Cmd(3, 1) with { Scrap = true }, false).Show);

        var clampCancelled = WorkSetInstruction(Cmd(2, 1) with { ClampCancel = true }, false);
        Assert.Equal(WsShowUnclampClamp, clampCancelled.Show);
        Assert.Equal(WsLeavesMachining, clampCancelled.LeavesAs);

        var remachine = WorkSetInstruction(Cmd(4, 1) with { ReMachine = true }, false);
        Assert.Equal(WsShowReMachining, remachine.Show);
        Assert.Equal(WsLeavesMachining, remachine.LeavesAs);

        var reUnclamp = WorkSetInstruction(Cmd(4, 1) with { ReUnclamp = 1 }, false);
        Assert.Equal(WsShowReUnclamp, reUnclamp.Show);
        Assert.True(reUnclamp.WaitWorkSetting);
        Assert.Equal(WsShowUnclamp, WorkSetInstruction(Cmd(2, 2) with { ReUnclamp = 1 }, true).Show); // only unclamp rows re-unclamp

        Assert.Equal(WsShowReCleaning, WorkSetInstruction(Cmd(6, 6) with { ReCleaning = 1 }, false).Show);
        Assert.Equal(WsShowNothing, WorkSetInstruction(Cmd(1, 2), moveCancelled: true).Show);
        Assert.Null(WorkSetInstruction(Cmd(7, 8), false).Show); // pallet input: no words of its own, shown raw
    }

    [Fact]
    public void A_move_is_cancelled_when_its_partner_is_gone_cancelled_or_remachined()
    {
        var clamp = new WsCommand(4, 10, 1, 2) { MoveKey = 11, MoveQuantity = 1 };
        var partner = new WsCommand(4, 11, 4, 1);

        Assert.False(MoveCancelled(clamp, [clamp, partner]));
        Assert.True(MoveCancelled(clamp, [clamp]));
        Assert.True(MoveCancelled(clamp, [clamp, partner with { Status = 4 }]));
        Assert.True(MoveCancelled(clamp, [clamp, partner with { ReMachine = true }]));
        Assert.True(MoveCancelled(clamp, [clamp, partner with { Type = 6 }]));
        Assert.False(MoveCancelled(clamp with { MoveKey = 0 }, [clamp]));
    }

    [Fact]
    public void Unclamp_quantities_read_per_operation()
    {
        Assert.Null(UnclampQuantityWords([]));
        Assert.Equal("normal 2", UnclampQuantityWords([(2, 0, 0)]));
        Assert.Equal("normal 2/1, scrap 0/1", UnclampQuantityWords([(2, 0, 0), (1, 1, 0)]));
        Assert.Null(UnclampQuantityWords([(0, 0, 0)]));
    }

    [Fact]
    public void New_codes_have_words_and_unknown_values_read_code_n()
    {
        Assert.Equal("machining", Codes.SerialJobType(1));
        Assert.Equal("work-set unclamp", Codes.SerialJobType(3));
        Assert.Equal("code 9", Codes.SerialJobType(9));
        Assert.Equal("work-set hold (clamp)", Codes.WorkSetLeavesAs(3));
        Assert.Equal("code 42", Codes.WorkSetLeavesAs(42));
        Assert.Equal("re-machining", Codes.WssInstruction(WsShowReMachining));
    }

    // ------------------------------------------------------------------ SQL shapes

    private static SchemaInfo Schema(params (string Table, string[] Columns)[] tables) =>
        new(tables.SelectMany(t => t.Columns.Select(c => (t.Table, c))));

    private static void AssertSelectOnly(string sql)
    {
        Assert.DoesNotMatch(@"(?i)\b(EXEC|EXECUTE|INSERT|UPDATE|DELETE|MERGE|DROP|ALTER|CREATE)\b", sql);
        Assert.DoesNotContain("1 = 0", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Macro_sql_matches_results_by_time_and_degrades_without_them()
    {
        var full = MacroValuesSql(Schema(
            ("CommonValues", ["ExecDateTime", "DeviceID", "Number", "Value", "PalletNumber", "MainONumber", "ExecONUmber", "SequenceNumber"]),
            ("MachineResults", ["DeviceID", "StartDateTime", "FinishDateTime", "PartName", "Revision", "ProcessNumber", "NCProgramFileName", "FixtureNumber"])));
        Assert.Contains("OUTER APPLY", full);
        Assert.Contains("c.ExecONUmber AS ExecONumber", full);
        Assert.Contains("@variable", full);
        Assert.Contains("@pallet", full);
        Assert.Contains("TOP (@take)", full);
        AssertSelectOnly(full);

        var old = MacroValuesSql(Schema(("CommonValues", ["ExecDateTime", "DeviceID", "Number", "Value"])));
        Assert.DoesNotContain("OUTER APPLY", old);
        Assert.DoesNotContain("@pallet", old); // no column: the filter is named in notAvailable instead
        Assert.Contains("CAST(NULL AS int) AS PalletNumber", old);
    }

    [Fact]
    public void Serial_sql_searches_every_slot_with_an_escaped_pattern()
    {
        string[] columns = ["FinishDateTime", "DeviceID", "DeviceJobType", "FixtureNumber", "OperationSequence", "Quantity",
            .. Enumerable.Range(1, 10).SelectMany(n => new[] { $"Code{n}", $"Status{n}" })];
        var sql = SerialSql(Schema(("PartSerialNumberTracking", columns),
            ("MachineResults", ["DeviceID", "FinishDateTime", "FixtureNumber", "PalletID", "OrderName", "PartName"]),
            ("WorkSetResults", ["DeviceID", "FinishDateTime", "FixtureNumber", "PalletID", "ClampOrderName"]),
            ("Pallets", ["PalletID", "PalletNumber"])));
        Assert.Contains("t.Code10 LIKE @pattern ESCAPE '\\'", sql);
        Assert.Contains("t.DeviceJobType = 1 AND", sql);
        Assert.Contains("FROM WorkSetResults x", sql);
        AssertSelectOnly(sql);

        Assert.Equal("%SN-0001%", ContainsPattern("SN-0001"));
        Assert.Equal(@"%50\%\_A\[1]\\%", ContainsPattern(@"50%_A[1]\"));
    }

    [Fact]
    public void Work_set_sql_joins_only_what_the_cell_has()
    {
        var bare = WorkSetCommandsDetailSql(Schema(("WorkSetCommand", ["DeviceID", "OperationType"])));
        Assert.DoesNotContain("JOIN", bare);
        Assert.Contains("CAST(NULL AS int) AS PalletNumber", bare);
        AssertSelectOnly(bare);

        var full = WorkSetCommandsDetailSql(Schema(
            ("WorkSetCommand", ["DeviceID", "PalletFixtureID", "FixtureNumber", "OperationType", "OperationStatus", "UnclampJobID",
                "UnclampOrderID", "ClampJobID", "ClampOrderID", "ClampQuantity", "ReMachine", "ReMachineJobID", "LastMachiningJobID"]),
            ("PalletFixtures", ["PalletFixtureID", "PalletID", "FixtureID", "FixtureNumber", "Comment", "StatusReason"]),
            ("Pallets", ["PalletID", "PalletNumber"]), ("Fixtures", ["FixtureID", "FixtureName"]),
            ("FixtureRun", ["PalletFixtureID", "ClampCancel", "MachineDeviceID", "MachiningFinishStatus", "LastMachiningJobID"]),
            ("Orders", ["OrderID", "OrderName", "Comment"]), ("Jobs", ["JobID", "ProcessID", "JobNumber", "Comment"]),
            ("Processes", ["ProcessID", "PartID", "ProcessNumber", "Comment"]), ("Parts", ["PartID", "PartName", "Revision"]),
            ("WorkSetJobs", ["JobID", "InformationFileID"]), ("InformationFiles", ["InformationFileID", "Name"]),
            ("MachineJobs", ["JobID", "NCProgramFileID", "ONumber"]), ("NCProgramFiles", ["NCProgramFileID", "Name"])));
        Assert.Contains("LEFT JOIN Processes kpr ON kpr.ProcessID = kj.ProcessID", full);
        Assert.Contains("LEFT JOIN InformationFiles ui", full);
        Assert.Contains("COALESCE(NULLIF(c.LastMachiningJobID, 0), fr.LastMachiningJobID)", full);
        Assert.Contains("ORDER BY c.DeviceID, p.PalletNumber, c.FixtureNumber", full);
        AssertSelectOnly(full);

        Assert.Null(WorkSetQuantitiesSql(Schema(("WorkSetCommand", ["PalletFixtureID"]))));
        Assert.NotNull(WorkSetQuantitiesSql(Schema(("WorkSetCommand", ["PalletFixtureID"]),
            ("WorkSetCommandQuantity", ["PalletFixtureID", "OperationSequence", "UnclampNormalQuantity", "UnclampScrapQuantity"]))));
    }

    // ------------------------------------------------------------------ ADE files

    private static AdeExplanation Ade(string xml, string path = "orders.xml", int max = 500) => AdeFile.Explain(path, xml, false, max);

    [Fact]
    public void Ade_good_file_lists_items_with_action_key_and_effect()
    {
        var e = Ade("""
            <MASData>
              <Orders>
                <Order action="ADD" name="ORDER-001"><PartName>CONTOSO-BRACKET</PartName><Quantity>10</Quantity><Priority>5</Priority></Order>
              </Orders>
              <Parts><Part action="update" name="CONTOSO-BRACKET" revision="B" /></Parts>
              <FunctionalTools><FunctionalTool action="delete" number="501" /></FunctionalTools>
              <OrderQuantities><OrderQuantity orderName="ORDER-001"><ProcessNumber>1</ProcessNumber></OrderQuantity></OrderQuantities>
            </MASData>
            """);

        Assert.StartsWith("no problem found", e.Verdict);
        Assert.Equal(4, e.Items.Count);
        var order = e.Items[0];
        Assert.Equal(("Orders", "Order", "add", "name=ORDER-001"), (order.Section, order.Element, order.Action, order.Key));
        Assert.Contains("updates it when it already exists", order.Effect);
        Assert.Null(order.Problems);
        Assert.Equal("name=CONTOSO-BRACKET, revision=B", e.Items[1].Key);
        Assert.Equal("deletes it", e.Items[2].Effect);
        Assert.Null(e.Items[3].Action);
        Assert.NotEmpty(e.NotChecked);
        Assert.Null(e.Note);
    }

    [Fact]
    public void Ade_confirmed_rules_fail_items_and_reject_the_file()
    {
        var e = Ade("""
            <MASData>
              <Orders>
                <!-- a comment -->
                <Order name="ORDER-002" />
                <Order action="delete" name="ORDER-003" />
                <Order action="add" name="ORDER-004"><PartName>P</PartName><Priority>1000</Priority><Status>6</Status></Order>
                <Order action="add" name="ORDER-005" />
                <Order action="add"><PartName>P</PartName></Order>
              </Orders>
              <NCPrograms><NCProgram action="add" name="CONTOSO-OP1"><Type>2</Type></NCProgram></NCPrograms>
              <FunctionalTools><FunctionalTool action="add" number="abc" /></FunctionalTools>
              <Pallets><Pallet /></Pallets>
            </MASData>
            """, path: @"ADE_Error\[11_6_2026_10_05_00]orders.xml");

        Assert.StartsWith("rejected: 8 items fail", e.Verdict);
        string Problems(int i) => string.Join(" | ", e.Items[i].Problems ?? []);
        Assert.Contains("XML comment", Problems(0));
        Assert.Contains("no action attribute", Problems(1));
        Assert.Contains("can't be deleted", Problems(2));
        Assert.Contains("above 999", Problems(3));
        Assert.Contains("Status 6", Problems(3));
        Assert.Contains("PartName", Problems(4));
        Assert.Contains("no name attribute", Problems(5));
        Assert.Contains("1 (main program), 4 (shared subprogram) or 8", Problems(6));
        Assert.Contains("Filename", Problems(6));
        Assert.Contains("positive whole number", Problems(7));
        Assert.Contains(e.Sections, s => s.Name == "Pallets" && s.Ignored == true);
        Assert.Contains("rejected", e.Note);
    }

    [Fact]
    public void Ade_offset_files_are_judged_by_their_last_item()
    {
        var early = Ade("""<MASData><OffsetFiles><OffsetFile name="A" /><OffsetFile action="update" name="B" /></OffsetFiles></MASData>""");
        Assert.StartsWith("no problem found", early.Verdict);
        Assert.Contains("only the section's last item decides", early.Items[0].Effect);

        var last = Ade("""<MASData><OffsetFiles><OffsetFile action="update" name="B" /><OffsetFile action="add" name="A" /></OffsetFiles></MASData>""");
        Assert.StartsWith("rejected: 1 item fails", last.Verdict);
    }

    [Fact]
    public void Ade_unreadable_files_say_why()
    {
        Assert.StartsWith("rejected: not well-formed", Ade("<MASData><Orders>").Verdict);
        Assert.StartsWith("rejected: the root element", Ade("<Data />").Verdict);
        Assert.StartsWith("rejected: not well-formed", Ade("""<!DOCTYPE x [<!ENTITY e "boom">]><MASData>&e;</MASData>""").Verdict);
        Assert.StartsWith("not explained", AdeFile.Explain("big.xml", "<MASData>", truncated: true, 500).Verdict);
        Assert.Equal("nothing to do", Ade("<MASData />").Verdict);
    }

    [Fact]
    public void Ade_items_are_capped_but_counted()
    {
        var items = string.Concat(Enumerable.Range(1, 5).Select(i => $"""<Part action="add" name="P{i}" />"""));
        var e = Ade($"<MASData><Parts>{items}</Parts></MASData>", max: 2);

        Assert.Equal(2, e.Items.Count);
        Assert.True(e.Capped);
        Assert.Equal(5, e.Sections.Single().Items);
    }

    // ------------------------------------------------------------------ tools

    private ToolContext Context()
    {
        var s = new McpSettings(new MakinoLogin("MAKINO-CELL-PC", "Makino", false, "your-login", "Pa55-w0rd-xyzzy"),
            [new SharedFolder("ade", _ade, "ADE drop folder")], new Limits());
        return new ToolContext(new SettingsLoad(s, null), new FakeMakinoReader(), new SharedFolders(s.Folders, s.Limits, []));
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public async Task Explain_ade_file_reads_through_the_shared_folder_rules()
    {
        File.WriteAllText(Path.Combine(_ade, "orders.xml"), """<MASData><Orders><Order action="add" name="ORDER-001"><PartName>P</PartName></Order></Orders></MASData>""");
        File.WriteAllText(Path.Combine(_ade, "AlarmNotification.xml"), "<MASData />");
        var context = Context();

        var ok = Parse(await CellTools.ExplainAdeFile(context, "ade", "orders.xml", CancellationToken.None));
        Assert.Equal("orders.xml", ok.GetProperty("path").GetString());
        Assert.Equal("add", ok.GetProperty("items")[0].GetProperty("action").GetString());
        Assert.False(ok.GetProperty("items")[0].TryGetProperty("problems", out _));

        var refused = Parse(await CellTools.ExplainAdeFile(context, "ade", "AlarmNotification.xml", CancellationToken.None));
        Assert.Contains("never shared", refused.GetProperty("error").GetString());
        var outside = Parse(await CellTools.ExplainAdeFile(context, "ade", @"..\secret.xml", CancellationToken.None));
        Assert.Contains("outside shared folder", outside.GetProperty("error").GetString());
        Assert.False(File.Exists(Path.Combine(_base, "secret.xml")));
        Assert.True(File.Exists(Path.Combine(_ade, "orders.xml"))); // never moved
    }

    [Fact]
    public async Task Database_tools_json_has_their_fields()
    {
        var context = Context();

        var macro = Parse(await CellTools.GetMacroValues(context, ct: CancellationToken.None));
        Assert.Equal(500, macro.GetProperty("values")[0].GetProperty("variable").GetInt32());
        Assert.Equal(7, macro.GetProperty("days").GetInt32());
        Assert.False(macro.TryGetProperty("note", out _));

        var work = Parse(await CellTools.GetWorkSet(context, ct: CancellationToken.None));
        var fixture = work.GetProperty("stations")[0].GetProperty("fixtures")[0];
        Assert.Equal("machining", fixture.GetProperty("leavesAs").GetString());
        Assert.Equal("MC1", fixture.GetProperty("unclamp").GetProperty("machine").GetString());
        Assert.Equal(1, fixture.GetProperty("clamp").GetProperty("quantity").GetInt32());

        var serial = Parse(await CellTools.FindSerial(context, "SN-0001", ct: CancellationToken.None));
        Assert.Equal("SN-0001", serial.GetProperty("matches")[0].GetProperty("serials")[0].GetProperty("serial").GetString());
        var empty = Parse(await CellTools.FindSerial(context, " ", ct: CancellationToken.None));
        Assert.Equal(SqlMakinoReader.NoSerialGiven, empty.GetProperty("note").GetString());

        var unknown = Parse(await CellTools.GetWorkSet(context, "WSS9", CancellationToken.None));
        Assert.StartsWith("No device named 'WSS9'", unknown.GetProperty("error").GetString());
    }
}

// Reads a real cell. Skipped unless MAKINOMCP_LIVE_SETTINGS points at a settings file; run locally only.
// Assertions are structural: nothing here may name a device, count or value from a real cell.
public class NewToolsLiveTests(ITestOutputHelper output)
{
    private const string EnvVar = "MAKINOMCP_LIVE_SETTINGS";

    private static SqlMakinoReader Reader()
    {
        var path = Environment.GetEnvironmentVariable(EnvVar);
        Skip.If(string.IsNullOrWhiteSpace(path), $"{EnvVar} is not set");
        var load = SettingsStore.Load(path!);
        Assert.True(load.Settings is not null, load.Problem);
        return new SqlMakinoReader(load.Settings!.Makino, load.Settings.Limits);
    }

    [SkippableFact]
    public async Task Live_macro_values_read()
    {
        var m = await Reader().GetMacroValuesAsync(30, null, null, null, CancellationToken.None);

        output.WriteLine($"macro values: {m.Values.Count} (capped {m.Capped}); note: {m.Note}; not available: {string.Join(" | ", m.NotAvailable ?? [])}");
        foreach (var v in m.Values.Take(5))
            output.WriteLine($"  {v.Time:s} {v.Machine} #{v.Variable} = {v.Value} pallet {v.Pallet} {v.MainProgram} part {v.Part} program {v.Program}");

        Assert.True(m.Values.Count > 0 || m.Note is not null);
        Assert.All(m.Values, v => Assert.False(string.IsNullOrWhiteSpace(v.Machine)));
    }

    [SkippableFact]
    public async Task Live_work_set_reads_stations_and_instructions()
    {
        var w = await Reader().GetWorkSetAsync(null, CancellationToken.None);

        output.WriteLine($"not available: {string.Join(" | ", w.NotAvailable ?? [])}");
        foreach (var st in w.Stations)
        {
            output.WriteLine($"station: {st.Status}, mode {st.Mode}, open {st.Open}, instruction {st.Instruction}, since {st.Since}, " +
                             $"planned {st.PlannedFinish}, pallets {string.Join("/", st.Pallets.Select(p => $"{p.Position}:{p.Status}"))}");
            foreach (var f in st.Fixtures)
            {
                output.WriteLine($"  fixture {f.Fixture}: {f.Instruction} -> {f.LeavesAs} {f.Detail} remachining {f.ReMachining} " +
                                 $"inspect {f.WaitingForInspectionConfirmation} off-duty {f.OffDutyProcess}");
                if (f.Unclamp is { } u)
                    output.WriteLine($"    unclamp: process {u.Process}, job {u.Job}, info {u.InfoFile is not null}, last program {u.LastProgram is not null}, " +
                                     $"machine {u.Machine is not null}, finish {u.MachiningFinish}, work {u.WorkStatus}, qty {u.Quantities}");
                if (f.Clamp is { } k)
                    output.WriteLine($"    clamp: process {k.Process}, job {k.Job}, info {k.InfoFile is not null}, qty {k.Quantity}");
            }
        }

        Assert.NotEmpty(w.Stations);
        Assert.All(w.Stations, st => Assert.False(string.IsNullOrWhiteSpace(st.Station)));
        Assert.All(w.Stations.SelectMany(st => st.Fixtures), f => Assert.False(string.IsNullOrWhiteSpace(f.Instruction)));
        Assert.All(w.Stations.SelectMany(st => st.Fixtures), f => Assert.True(f.Detail is not null || !f.Instruction.StartsWith("code ", StringComparison.Ordinal)));
    }

    [SkippableFact]
    public async Task Live_find_serial_reads()
    {
        var reader = Reader();
        var none = await reader.FindSerialAsync("zz-no-such-serial-zz", 90, null, CancellationToken.None);
        var any = await reader.FindSerialAsync("0", 400, null, CancellationToken.None);

        output.WriteLine($"no-such serial: {none.Matches.Count}; note: {none.Note}");
        output.WriteLine($"serials containing 0: {any.Matches.Count} (capped {any.Capped}); note: {any.Note}");
        foreach (var m in any.Matches.Take(5))
            output.WriteLine($"  {m.Time:s} {m.Kind} fixture {m.Fixture} qty {m.Quantity} slots {string.Join(",", m.Serials.Select(c => c.Slot))} part {m.Part is not null}");

        Assert.Empty(none.Matches);
        Assert.NotNull(none.Note);
        Assert.All(any.Matches, m => Assert.NotEmpty(m.Serials));
    }
}
