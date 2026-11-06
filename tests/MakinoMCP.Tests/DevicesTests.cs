using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;
using MakinoMCP.Core.Cell;
using MakinoMCP.Core.Settings;

namespace MakinoMCP.Tests;

// Devices and attention (0.6): the overview's cell settings and per-device extras, and get_attention's waiting work,
// holds, device waits, manual transport and station instruction details. Sample data only.
public class DevicesTests(ITestOutputHelper output)
{
    private static readonly DateTime Now = new(2026, 11, 6, 10, 0, 0);

    private static Dictionary<string, string> D(params (string Key, string Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);

    private static SqlMakinoReader.AttentionRows Rows(
        SqlMakinoReader.AttentionDevice[]? devices = null, SqlMakinoReader.AttentionFixture[]? fixtures = null,
        SqlMakinoReader.AttentionCommand[]? commands = null, SqlMakinoReader.AttentionTransport[]? transports = null) =>
        new(devices ?? [], fixtures ?? [], commands ?? [], transports ?? [], []);

    private static AttentionList Build(SqlMakinoReader.AttentionRows rows, bool includeParked = false) =>
        SqlMakinoReader.BuildAttention(Now, 500, rows, [], includeParked);

    // --- overview ---

    [Fact]
    public void Scheduling_rules_follow_the_stored_order_else_the_vendor_default()
    {
        var stock = SqlMakinoReader.SchedulingRulesFrom(D(), D());
        Assert.NotNull(stock);
        Assert.True(stock.Default);
        Assert.Equal("operator priority", stock.TransportOrder[0]);
        Assert.Equal(9, stock.TransportOrder.Count);
        Assert.Equal("measuring machine", stock.DestinationOrder[0]);
        Assert.Null(stock.NotUsed);
        Assert.Equal("highest process first, then highest machining job first", stock.ProcessSequence);

        // A cell without due dates in its rule doesn't schedule by them; order priority is process priority without
        // order management.
        var own = SqlMakinoReader.SchedulingRulesFrom(
            D(("TransPriorityRule", "1|4|9"), ("TransDevPriorityRule", "1|2"), ("TransPriorityProcessSequenceAscend", "1")),
            D(("OrderManagement", "0")));
        Assert.NotNull(own);
        Assert.Null(own.Default);
        Assert.Equal(["operator priority", "process priority (order management is off)", "wait time (longest waiting first)"], own.TransportOrder);
        Assert.Equal(["machine", "work-set station"], own.DestinationOrder);
        Assert.Contains("order due date", own.NotUsed!);
        Assert.Null(own.ProcessSequence); // not a term of this rule

        Assert.Equal("code 42", SqlMakinoReader.SchedulingRulesFrom(D(("TransPriorityRule", "42")), D())!.TransportOrder[0]);
        Assert.Null(SqlMakinoReader.SchedulingRulesFrom(null, D()));
    }

    [Fact]
    public void Retention_months_read_the_setting_else_the_purges_fallback()
    {
        Assert.Equal((6, false), SqlMakinoReader.KeepMonths(D(("PresetResultKeepMonth", "6")), "PresetResultKeepMonth", 12));
        Assert.Equal((12, true), SqlMakinoReader.KeepMonths(D(), "PresetResultKeepMonth", 12));
        Assert.Equal(((int?)null, false), SqlMakinoReader.KeepMonths(null, "PresetResultKeepMonth", 12));
    }

    [Fact]
    public void Nc_memory_inspection_countdown_and_transport_cancel()
    {
        var memory = SqlMakinoReader.NcMemoryFrom(512 * 1024, 40, D(("MaxNCMemorySize", "2097152"), ("MaxNCProgramFiles", "200")));
        Assert.NotNull(memory);
        Assert.Equal(512, memory.FreeKB);
        Assert.Equal(40, memory.FreeFiles);
        Assert.Equal(75, memory.MemoryUsedPercent);
        Assert.Equal(80, memory.FilesUsedPercent);
        // Sizes not set (0, the default): no share; nothing recorded: nothing.
        Assert.Null(SqlMakinoReader.NcMemoryFrom(1024, 3, D(("MaxNCMemorySize", "0")))!.MemoryUsedPercent);
        Assert.Null(SqlMakinoReader.NcMemoryFrom(0, 0, null));

        Assert.Equal(3, SqlMakinoReader.Countdown(true, 10, 7));
        Assert.Null(SqlMakinoReader.Countdown(false, 10, 7));
        Assert.Null(SqlMakinoReader.Countdown(true, 0, 0));

        Assert.True(SqlMakinoReader.TransportCancelledFlag(true, true, true));
        Assert.Null(SqlMakinoReader.TransportCancelledFlag(false, true, true)); // the cell doesn't use it
        Assert.Null(SqlMakinoReader.TransportCancelledFlag(true, false, true)); // closed says more
    }

    [Fact]
    public void Cell_files_list_only_what_is_set_and_exports_that_are_on()
    {
        var files = SqlMakinoReader.CellFilesFrom(D(("ADEFolder", @"\\server\ADE"), ("FTNOutFolder", @"C:\Out"),
            ("EnableFTNExport", "3"), ("EnableITNExport", "1")));
        Assert.NotNull(files);
        Assert.Equal(@"\\server\ADE", files.AdeFolder);
        Assert.Equal("Zoller", files.FtnExport!.Format);
        Assert.Equal(@"C:\Out", files.ItnExport!.Folder); // the ITN export falls back to the FTN folder
        Assert.Null(files.AdeErrorFolder);
        Assert.Null(SqlMakinoReader.CellFilesFrom(D(("FTNOutFolder", @"C:\Out"), ("EnableFTNExport", "0"))));
        Assert.Null(SqlMakinoReader.CellFilesFrom(null));
    }

    [Fact]
    public void Empty_positions_with_a_state_are_shown_unless_reserved_for_a_pallet_on_its_way()
    {
        Assert.True(SqlMakinoReader.EmptyPositionShown(3, reservedPallet: false));
        Assert.True(SqlMakinoReader.EmptyPositionShown(1, reservedPallet: false));
        Assert.False(SqlMakinoReader.EmptyPositionShown(1, reservedPallet: true));
        Assert.False(SqlMakinoReader.EmptyPositionShown(0, reservedPallet: false));
        Assert.False(SqlMakinoReader.EmptyPositionShown(null, reservedPallet: false));
    }

    [Fact]
    public void Device_codes_use_the_consoles_words_and_code_n_for_the_rest()
    {
        Assert.Equal("extra tool pallet", Codes.ManualTransportType(6));
        Assert.Equal("code 99", Codes.ManualTransportType(99));
        Assert.Equal("front", Codes.DevicePosition(2));
        Assert.Equal("", Codes.DevicePosition(0));
        Assert.Equal("waiting for a security login", Codes.MachineOperatorWait(8));
        Assert.Null(Codes.MachineOperatorWait(2));
        Assert.Equal("standalone", Codes.MachineSystemMode(false));
        Assert.StartsWith("not system", Codes.VehicleSystemMode(false));
        Assert.Equal("only when needed (the program stays in NC memory while it fits)", Codes.NcDownloadType(1));
        Assert.Equal("code 7", Codes.WssHelpRequest(7));
    }

    // --- attention: waiting work ---

    private static readonly SqlMakinoReader.AttentionSettings Plain = new();

    private static Dictionary<int, SqlMakinoReader.AttentionDevice> Devices(params SqlMakinoReader.AttentionDevice[] devices) =>
        devices.ToDictionary(d => d.Id);

    [Fact]
    public void Waiting_work_is_blocked_only_when_no_feasible_device_can_take_it_and_is_grouped()
    {
        var devices = Devices(
            new("MC1", 1) { Id = 1, Connected = true, Open = false },
            new("MC2", 1) { Id = 2, Connected = true, Open = true, PmcAlarms = 1 },
            new("MC3", 1) { Id = 3, Connected = true, Open = true },
            new("WSS1", 3) { Id = 4, Connected = true, Open = true, AllAlarms = 1 });
        var feasible = new Dictionary<int, IReadOnlyList<int>> { [10] = [1, 2], [11] = [1, 3], [12] = [4], [13] = [] };
        SqlMakinoReader.AttentionWaiting W(int pallet, int job, int type) => new(pallet, 1) { JobId = job, JobType = type };

        var analysis = SqlMakinoReader.AnalyzeWaiting(
            [W(3, 10, 2), W(4, 10, 2), W(5, 11, 2), W(6, 12, 1), W(7, 13, 2), W(8, 10, 2) with { Suspend = true }], feasible, devices, Plain);

        Assert.Equal(3, analysis.Blocked.Count);
        var machining = analysis.Blocked[0];
        Assert.Equal(2, machining.Fixtures); // pallet 8 is parked: its own reason stays visible there
        Assert.Equal("MC1 (closed), MC2 (open PMC alarm)", machining.Devices);
        Assert.Contains(analysis.Blocked, b => b is { What: "work setting", Devices: "WSS1 (open alarm)" });
        Assert.Contains(analysis.Blocked, b => b.Devices == "no feasible machine");

        var list = Build(Rows(devices: devices.Values.ToArray()) with
        {
            Waiting = analysis,
        });
        var item = list.Items.First(i => i.Kind == "waiting work");
        Assert.Equal("machining blocked: no feasible device can take it", item.What);
        Assert.Equal([3, 4], item.Pallets);
        Assert.Equal(4, list.Counts["waiting work blocked"]);
    }

    [Fact]
    public void Transport_cancel_and_system_mode_block_by_the_cells_settings()
    {
        var mc = new SqlMakinoReader.AttentionDevice("MC1", 1) { Id = 1, Connected = true, Open = true, TransProhibit = true };
        Assert.Null(SqlMakinoReader.DeviceBlock(mc, Plain));
        Assert.Equal("transport cancelled", SqlMakinoReader.DeviceBlock(mc, Plain with { TransportCancel = true }));
        Assert.Equal("not in system mode", SqlMakinoReader.DeviceBlock(mc with { SystemMode = false }, Plain));
        Assert.Equal("skipped", SqlMakinoReader.DeviceBlock(new("WSS1", 3) { Skip = true }, Plain));
        // A machine's warning or CNC alarm doesn't keep it from automatic work.
        Assert.Null(SqlMakinoReader.DeviceBlock(mc with { AllAlarms = 2, PmcAlarms = 0 }, Plain));
    }

    [Fact]
    public void Order_suspend_and_off_duty_rules_hold_fixtures_as_parked()
    {
        var devices = Devices(new SqlMakinoReader.AttentionDevice("MC1", 1) { Id = 1, Connected = true, Open = true });
        var feasible = new Dictionary<int, IReadOnlyList<int>> { [10] = [1] };
        SqlMakinoReader.AttentionWaiting W(int pallet) => new(pallet, 1) { JobId = 10, JobType = 2, Place = "pallet stocker", ProcessId = 5 };
        var waiting = new[]
        {
            W(1) with { OrderMachiningSuspended = true, Order = "ORDER-001", LackingTool = true },
            W(2) with { OffDutyProcess = true },
            W(3) with { JobOperatorCall = 1 },
            W(4) with { OperatorCallStatus = 2 },
            // Held at the work-set station: already parked by that flag, never counted twice.
            W(5) with { OrderMachiningSuspended = true, Order = "ORDER-001", Hold = true },
        };

        // Order machining suspend counts only where the cell's setting makes it count; on duty, the off-duty process waits.
        var onDuty = Plain with { OrderMachiningSuspend = true, OffDutyProcessManagement = 1, DutyOn = true };
        var holds = SqlMakinoReader.AnalyzeWaiting(waiting, feasible, devices, onDuty).Holds;
        Assert.Equal([(1, "order machining suspended: ORDER-001"), (2, SqlMakinoReader.OffDutyProcessHold)],
            holds.Select(h => (h.Pallet!.Value, h.Words)));
        Assert.Empty(SqlMakinoReader.AnalyzeWaiting(waiting, feasible, devices, Plain with { DutyOn = true }).Holds);

        // Off duty, operator-call jobs wait for duty (unless the cell transports them anyway).
        var offDuty = Plain with { DutyManagement = 1, DutyOn = false };
        Assert.Equal([3, 4], SqlMakinoReader.AnalyzeWaiting(waiting, feasible, devices, offDuty).Holds.Select(h => h.Pallet!.Value));
        Assert.Empty(SqlMakinoReader.AnalyzeWaiting(waiting, feasible, devices, offDuty with { OffDutyOperatorCallTransport = true }).Holds);

        // Holds are counted with the parked ones; a lacking-tool fixture that is held waits with them.
        var analysis = SqlMakinoReader.AnalyzeWaiting(waiting, feasible, devices, onDuty);
        var rows = Rows(devices: devices.Values.ToArray(), fixtures: [new(1, 1) { LackingTool = true, Order = "ORDER-001" }]) with
        {
            Waiting = analysis, Settings = onDuty,
        };
        var compact = Build(rows);
        Assert.DoesNotContain(compact.Items, i => i.Kind == "fixture");
        Assert.NotNull(compact.Parked);
        Assert.Equal(2, compact.Parked.Fixtures);
        Assert.Equal(2, compact.Parked.Blocking);
        Assert.Equal(1, compact.Parked.LackingTools);
        Assert.Equal(1, compact.Counts[SqlMakinoReader.OrderSuspendHold]);

        var full = Build(rows, includeParked: true);
        Assert.Contains(full.Items, i => i.What == "lacking tools, order machining suspended: ORDER-001");
        Assert.Contains(full.Items, i => i.Pallet == 2 && i.What == SqlMakinoReader.OffDutyProcessHold);
    }

    [Fact]
    public void One_fixture_lacking_tools_holds_its_pallet_when_the_cell_needs_all_tools()
    {
        var devices = Devices(new SqlMakinoReader.AttentionDevice("MC1", 1) { Id = 1, Connected = true, Open = true });
        var feasible = new Dictionary<int, IReadOnlyList<int>> { [10] = [1] };
        var waiting = new SqlMakinoReader.AttentionWaiting[]
        {
            new(7, 1) { JobId = 10, JobType = 2, LackingTool = true }, new(7, 2) { JobId = 10, JobType = 2 },
            new(7, 3) { JobId = 10, JobType = 2, Suspend = true }, new(8, 1) { JobId = 10, JobType = 2, LackingTool = true },
        };
        var all = Plain with { AllFixtureTsReady = true, ToolsHoldWork = true };

        var hold = Assert.Single(SqlMakinoReader.AnalyzeWaiting(waiting, feasible, devices, all).PalletHolds);
        Assert.Equal(7, hold.Pallet);
        Assert.Equal([2], hold.HeldBack);
        Assert.Empty(SqlMakinoReader.AnalyzeWaiting(waiting, feasible, devices, all with { ToolsHoldWork = false }).PalletHolds);

        var list = Build(Rows() with { Waiting = SqlMakinoReader.AnalyzeWaiting(waiting, feasible, devices, all), Settings = all });
        Assert.Equal("fixture 1 lacks tools and holds back fixture 2 (the cell machines the pallet only when every fixture has its tools)",
            list.Items.Single(i => i.Kind == "pallet").Detail);
        Assert.Contains(SqlMakinoReader.AllFixtureToolsNote, list.Notes!);
    }

    // --- attention: devices ---

    [Fact]
    public void Machines_waiting_on_a_person_feed_hold_help_requests_and_extra_tool_pallets_are_items()
    {
        var list = Build(Rows(devices:
        [
            new("MC1", 1) { Connected = true, Action = 2, SubAction = 6 },
            new("MC2", 1) { Connected = true, Action = 2, SubAction = 8, Recovery = true },
            new("MC3", 1) { Connected = true, Action = 1, CncRun = 2 },
            new("MC4", 1) { Connected = true, Action = 1, CncRun = 2, OperatorCall = 1 },
            new("MC5", 1) { Connected = true, ExtraToolPallet = 90, ExtraToolPalletOutside = true },
            new("WSS1", 3) { Connected = true, HelpRequest = 1, OpeTimeout = 1 },
        ]) with { Settings = Plain with { ExtraToolPalletTransport = true } });

        string? What(string device) => list.Items.FirstOrDefault(i => i.Device == device)?.What;
        Assert.Equal("waiting for the operator to confirm the 5-axis table is coupled", What("MC1"));
        Assert.Equal("recovery request", What("MC2")); // the recovery wins, as on the console
        Assert.Equal("feed hold (the dashboard still reads busy)", What("MC3"));
        Assert.Equal("operator call (M code)", What("MC4"));
        Assert.Equal("waiting for extra tool pallet 90", What("MC5"));
        Assert.Equal("help requested, operation timed out", What("WSS1"));
        Assert.Equal(1, list.Counts["feed hold"]);
    }

    [Fact]
    public void A_busy_table_with_no_pallet_is_a_possible_position_status_error()
    {
        var list = Build(Rows(
            devices:
            [
                new("MC1", 1) { Connected = true, ApcDirect = true, TableStatus = 3 },
                new("MC2", 1) { Connected = true, ApcDirect = false, TableStatus = 3 },  // turn APC: other rules
                new("MC3", 1) { Connected = true, ApcDirect = true, TableStatus = 4 },   // finished
                new("WSS1", 3) { Connected = true, TableStatus = 1 },                     // reserved, nothing coming
                new("WSS2", 3) { Connected = true, TableStatus = 1, TableReserved = true },
                new("WSS3", 3) { Connected = true, TableStatus = 1 },
                new("WSS4", 3) { Connected = true, TableStatus = 2, TablePallet = 5 },
            ],
            transports: [new(9, "WSS3", 1, null) { DestType = 3, DestDevice = "WSS3" }]));

        Assert.Equal(["MC1", "WSS1"], list.Items.Where(i => i.What == "position status error (possibly)").Select(i => i.Device));
    }

    [Fact]
    public void Manual_transports_say_their_kind_and_what_they_wait_for()
    {
        var devices = new SqlMakinoReader.AttentionDevice[]
        {
            new("MC1", 1) { Connected = true, Open = true }, new("WSS1", 3) { Connected = true, Open = false },
            new("WSS2", 3) { Connected = true, Open = true, ManualTransportSendCommand = 0 },
        };
        var closedStations = Plain with { ManualTransAvailType = 1 };
        var list = Build(Rows(devices: devices, transports:
        [
            new(1, "MC1, table", 1, Now) { Type = 1, DestType = 1, DestDevice = "MC1" },
            new(2, "MC1", 1, Now.AddMinutes(-1)) { Type = 6, DestType = 1, DestDevice = "MC1" },
            new(3, "WSS1", 1, Now.AddMinutes(-2)) { Type = 10, DestType = 3, DestDevice = "WSS1" },
            new(4, "WSS2", 3, Now.AddMinutes(-3)) { Type = 1, DestType = 3, DestDevice = "WSS2" },
            new(5, "WSS1", 3, Now.AddMinutes(-4)) { Type = 2, DestType = 3, DestDevice = "WSS1" },
        ]) with { Settings = closedStations });

        string? Detail(int pallet) => list.Items.Single(i => i.Kind == "manual transport" && i.Pallet == pallet).Detail;
        Assert.Equal("to MC1, table, not picked up yet (can still be cancelled), waits for MC1 to be closed " +
                     "(a manual transport goes only to a closed machine)", Detail(1));
        Assert.Equal("extra tool pallet, to MC1, not picked up yet (can still be cancelled)", Detail(2));
        Assert.Equal("reclamp, to WSS1, not picked up yet (can still be cancelled), waits for WSS1 to be opened " +
                     "(a reclamp needs the station open)", Detail(3));
        Assert.Equal("to WSS2, Complete it on the console's Manual Transport screen", Detail(4));
        Assert.Equal("pallet input, to WSS1, the operator presses FINISH at the station", Detail(5));
    }

    [Fact]
    public void Station_instructions_say_what_they_wait_for_and_merge_whole_pallet_transports()
    {
        var settings = Plain with { InspectionConfirmation = true, ProbePartCount = 500 };
        var list = Build(Rows(
            devices: [new("WSS1", 3) { Connected = true }, new("WSS9", 3) { Connected = true, VirtualStation = true }],
            commands:
            [
                new("WSS1", 3, 1, Operation: 4, Status: null, Order: null, Quantity: null) { InspectionCheck = true },
                new("WSS1", 4, 1, Operation: 5, Status: null, Order: null, Quantity: null) { InspectionConfirmed = true },
                new("WSS1", 5, 1, Operation: 2, Status: null, Order: null, Quantity: null)
                    { ReMachine = true, ReMachineJob = 3, ReMachineProgram = "O1234-OP10", OperatorCallStatus = 3 },
                new("WSS9", 6, null, Operation: 6, Status: null, Order: null, Quantity: null),
            ],
            transports: [new(6, "WSS9", 3, Now) { DestType = 3, DestDevice = "WSS9" }]) with { Settings = settings });

        string? Detail(int pallet) => list.Items.Single(i => i.Kind == "work set" && i.Pallet == pallet).Detail;
        Assert.Equal("waiting for inspection confirmation at the station", Detail(3));
        Assert.Null(Detail(4)); // confirmed
        Assert.Equal("re-machining from job 3 (O1234-OP10), the machine will wait for an operator to start it", Detail(5));
        Assert.StartsWith("pallet brought by manual transport or fixture change", Detail(6));
        Assert.Contains("manual transport waiting to finish", Detail(6));
        Assert.EndsWith("finished on the console (a virtual station)", Detail(6));
        Assert.DoesNotContain(list.Items, i => i.Kind == "manual transport");
        Assert.Equal(1, list.Counts["manual transports open"]);
        Assert.Contains(SqlMakinoReader.ProbePartCountNote, list.Notes!);
    }

    // --- SQL ---

    private static readonly Dictionary<string, string[]> Schema = new()
    {
        ["Devices"] = ["DeviceID", "DeviceName", "DeviceType", "DeviceNumber"],
        ["Pallets"] = ["PalletID", "PalletNumber"],
        ["PalletLocation"] = ["PalletID", "CurDeviceType", "CurDeviceNumber"],
        ["PalletFixtures"] = ["PalletFixtureID", "PalletID", "FixtureNumber", "Suspend", "WorkSetHold", "Damaged", "PreMcSuspend",
            "LackingTool", "OffDutyProcess", "OperatorCallStatus", "LastOneShotOpeCallProcessID", "ReclampStatus"],
        ["FixtureRun"] = ["PalletFixtureID", "CurOrderID", "CurProcessID", "CurJobID", "CurJobStatus", "FirstToolCheck", "McCMMCheck",
            "FixtureGageCheck"],
        ["FixtureQuantity"] = ["PalletFixtureID", "ClampedQuantity"],
        ["Jobs"] = ["JobID", "JobType", "JobNumber"],
        ["JobFeasibleDevices"] = ["JobID", "FeasibleDeviceID"],
        ["MachineJobs"] = ["JobID", "NCProgramFileID", "OperatorCall"],
        ["NCProgramFiles"] = ["NCProgramFileID", "Name"],
        ["Orders"] = ["OrderID", "OrderName", "MachiningSuspended"],
        ["WorkSetStatus"] = ["DeviceID", "TablePalletID"],
        ["WorkSetCommand"] = ["DeviceID", "PalletFixtureID", "FixtureNumber", "OperationType", "OperationStatus", "ClampQuantity",
            "MoveQuantity", "MovePalletFixtureID", "ReMachine", "ReMachineJobID", "InspectionConfirmation"],
        ["ManualTransport"] = ["PalletID", "DestDeviceType", "DestDeviceNumber", "DestDevicePosition", "Status", "TransportType"],
    };

    private static SchemaInfo SchemaWithout(params string[] tableDotColumn) =>
        new(Schema.SelectMany(t => t.Value.Select(c => (Table: t.Key, Column: c)))
            .Where(tc => !tableDotColumn.Contains(tc.Table + "." + tc.Column) && !tableDotColumn.Contains(tc.Table)));

    [Fact]
    public void Station_instructions_leave_out_clamps_the_console_hides_and_find_the_pallet_of_whole_pallet_rows()
    {
        var sql = SqlMakinoReader.WorkSetCommandsSql(SchemaWithout())!;
        Assert.Contains("AND NOT (c.OperationType = 1 AND (c.ClampQuantity + c.MoveQuantity = 0 OR (c.MovePalletFixtureID > 0", sql);
        Assert.Contains("m.OperationType NOT IN (2, 3, 4, 5) OR m.ReMachine = 1 OR m.OperationStatus = 4", sql);
        Assert.Contains("COALESCE((SELECT TOP 1 p.PalletNumber FROM PalletFixtures pf", sql);
        Assert.Contains("FROM WorkSetStatus w JOIN Pallets p ON p.PalletID = w.TablePalletID WHERE w.DeviceID = c.DeviceID", sql);
        Assert.Contains("fr.FirstToolCheck LIKE '%[1-9]%' OR fr.McCMMCheck LIKE '%[1-9]%' OR fr.FixtureGageCheck = 1", sql);
        Assert.Contains("AS ReMachineProgram", sql);
        Assert.Contains("COUNT(*) OVER () AS Total", sql); // the hidden rows are out of the total too

        var old = SqlMakinoReader.WorkSetCommandsSql(SchemaWithout("WorkSetCommand.MoveQuantity", "WorkSetCommand.MovePalletFixtureID",
            "WorkSetCommand.ReMachineJobID", "FixtureRun"))!;
        Assert.Contains("AND NOT (c.OperationType = 1 AND (c.ClampQuantity = 0))", old);
        Assert.Contains("CAST(0 AS int) AS InspectionCheck", old);
        Assert.Contains("CAST(NULL AS varchar(4000)) AS ReMachineProgram", old);

        Assert.Contains("t.TransportType AS TransportType", SqlMakinoReader.ManualTransportsSql(SchemaWithout()));
    }

    [Fact]
    public void Waiting_and_parked_sql_read_the_current_job_bounded()
    {
        var waiting = SqlMakinoReader.WaitingSql(SchemaWithout())!;
        Assert.Contains("SELECT TOP (@take)", waiting);
        Assert.Contains("WHERE fr.CurJobStatus = 1 AND fr.CurJobID IS NOT NULL", waiting);
        Assert.Contains("FROM JobFeasibleDevices f", waiting);
        Assert.Contains("o.MachiningSuspended AS MachiningSuspended", waiting);
        Assert.Contains("CAST(NULL AS varchar(4000)) AS OrderName", SqlMakinoReader.WaitingSql(SchemaWithout("Orders"))!);
        Assert.Null(SqlMakinoReader.WaitingSql(SchemaWithout("JobFeasibleDevices")));

        var parked = SqlMakinoReader.AttentionParkedSql(SchemaWithout())!;
        Assert.Contains("(pf.Suspend = 1 AND j.JobType IN (2, 4, 5)) OR (pf.WorkSetHold = 1 AND j.JobType = 1)", parked);
        Assert.Contains("SUM(q.ClampedQuantity) FROM FixtureQuantity q", parked);
        Assert.Contains("0 AS Blocking", SqlMakinoReader.AttentionParkedSql(SchemaWithout("Jobs"))!);

        var groups = SqlMakinoReader.MergeParked(
            [(4, 1, false, true, 3, 0, 1, 2), (4, 2, false, true, 1, 0, 0, 0)], [(Type: 1, Number: 1, Name: "MC1")]);
        var g = Assert.Single(groups);
        Assert.Equal((4, 1, 2), (g.Fixtures, g.Blocking, g.WithParts));
    }

    // --- logins (SI-3) ---

    // The console's user tables (and the optional CompSec module's user tables, none of which is in the probe list)
    // hold console passwords in plain text: no query names them.
    [SkippableFact]
    public void No_query_or_probed_table_names_the_consoles_user_tables()
    {
        string[] forbidden = ["Users", "UserCards", "Groups", "GroupMembers", "GroupPermissions", "Permissions"];
        Assert.DoesNotContain(SchemaInfo.Tables, t => forbidden.Contains(t, StringComparer.OrdinalIgnoreCase)
                                                      || t.Contains("User", StringComparison.OrdinalIgnoreCase) && t != "UserSysData");

        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "MakinoMCP.slnx"))) root = Path.GetDirectoryName(root);
        Skip.If(root is null, "source tree not found");
        var tables = string.Join("|", forbidden.Append(@"\w*_User\w*"));
        var pattern = new Regex($@"\b(FROM|JOIN)\s+(dbo\.)?\[?({tables})\]?\b", RegexOptions.IgnoreCase);
        var sources = Directory.GetFiles(Path.Combine(root, "src", "MakinoMCP.Core", "Cell"), "*.cs");
        Assert.NotEmpty(sources);
        foreach (var file in sources)
            Assert.False(pattern.IsMatch(File.ReadAllText(file)), $"{Path.GetFileName(file)} reads a console user table");
    }

    // --- live ---

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Prints what the 0.6 device and attention additions read on a real cell (local runs only); asserts
    /// only shape.</summary>
    [SkippableFact]
    public async Task Live_devices_and_attention_additions_are_read()
    {
        var path = Environment.GetEnvironmentVariable("MAKINOMCP_LIVE_SETTINGS");
        Skip.If(string.IsNullOrWhiteSpace(path), "MAKINOMCP_LIVE_SETTINGS is not set");
        var load = SettingsStore.Load(path!);
        Assert.True(load.Settings is not null, load.Problem);
        var reader = new SqlMakinoReader(load.Settings!.Makino, load.Settings.Limits);

        var overview = await reader.GetOverviewAsync(CancellationToken.None);
        output.WriteLine($"softwareVersion {overview.SoftwareVersion}; preset {overview.PresetHistoryMonths} " +
                         $"(assumed {overview.PresetHistoryMonthsAssumed}), tool moves {overview.ToolMoveHistoryMonths} " +
                         $"(assumed {overview.ToolMoveHistoryMonthsAssumed}); ncProgramDownload {overview.NcProgramDownload}");
        output.WriteLine("schedulingRules " + JsonSerializer.Serialize(overview.SchedulingRules, Json));
        output.WriteLine("cellFiles " + JsonSerializer.Serialize(overview.CellFiles, Json));
        output.WriteLine("notAvailable " + string.Join("; ", overview.NotAvailable ?? []));
        foreach (var d in overview.Devices.Where(d => d.Connected is not null))
        {
            output.WriteLine($"{d.Type}: mode {d.Mode}, systemMode {d.SystemMode}, transportCancelled {d.TransportCancelled}, " +
                             $"waitingFor {d.WaitingFor}, nc {JsonSerializer.Serialize(d.NcMemory, Json)}, cmm {d.NextCmmIn}, " +
                             $"gage {d.NextGageIn}, empty positions {string.Join("/", d.Pallets.Where(p => p.PalletNumber is null).Select(p => $"{p.Position}:{p.Status}"))}, " +
                             $"notAvailable {string.Join("/", d.NotAvailable)}");
        }

        var attention = await reader.GetAttentionAsync(includeParked: false, CancellationToken.None);
        var json = JsonSerializer.Serialize(attention, Json);
        output.WriteLine($"attention: {json.Length} bytes, {attention.Items.Count} items, capped {attention.Capped}");
        output.WriteLine("counts " + string.Join("; ", attention.Counts.Select(c => $"{c.Key}: {c.Value}")));
        foreach (var kind in attention.Items.GroupBy(i => (i.Kind, i.What)))
            output.WriteLine($"  {kind.Count()} × {kind.Key.Kind}: {kind.Key.What}");
        foreach (var i in attention.Items.Where(i => i.Kind is "waiting work" or "pallet" or "manual transport" or "work set").Take(8))
            output.WriteLine($"  detail [{i.Kind}] {i.Detail}");
        output.WriteLine("parked " + JsonSerializer.Serialize(attention.Parked, Json));
        output.WriteLine($"missingTools {attention.MissingTools?.Count} groups, tools {attention.MissingTools?.Sum(g => g.Tools.Count)}");
        output.WriteLine("notes " + string.Join("; ", attention.Notes ?? []));
        output.WriteLine("notAvailable " + string.Join("; ", attention.NotAvailable));

        Assert.All(overview.Devices, d => Assert.True(d.TransportCancelled is null or true));
        Assert.All(overview.Devices.Where(d => d.Connected == false), d => Assert.Null(d.SystemMode));
        Assert.True(overview.PresetHistoryMonths is null or > 0);
        Assert.True(overview.ToolMoveHistoryMonths is null or > 0);
        Assert.All(overview.Devices.Where(d => d.Type == "vehicle"), d => Assert.DoesNotContain("vehicle mode", d.NotAvailable));
        Assert.All(attention.Items.Where(i => i.Kind == "waiting work"), i => Assert.True(i.Fixtures > 0));
        Assert.All(attention.MissingTools ?? [], g => Assert.True(g.Tools.Count is > 0 and <= 10));
        Assert.True(attention.Parked is null || attention.Parked.Blocking is null || attention.Parked.Blocking <= attention.Parked.Fixtures);
    }
}
