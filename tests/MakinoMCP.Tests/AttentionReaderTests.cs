using Xunit;
using MakinoMCP.Core.Cell;

namespace MakinoMCP.Tests;

// get_attention: items built from sample rows, and the SQL behind them, without a database.
public class AttentionReaderTests
{
    private static readonly DateTime Now = new(2026, 11, 2, 10, 30, 0);

    private static readonly Dictionary<string, string[]> Schema = new()
    {
        ["Devices"] = ["DeviceID", "DeviceName", "DeviceType", "DeviceNumber"],
        ["PalletFixtures"] = ["PalletFixtureID", "PalletID", "FixtureNumber", "Suspend", "WorkSetHold", "Damaged", "StatusReason",
            "PreMcSuspend", "PreMcSuspendReason", "LackingTool", "ReclampStatus"],
        ["Pallets"] = ["PalletID", "PalletNumber"],
        ["PalletLocation"] = ["PalletID", "CurDeviceType", "CurDeviceNumber"],
        ["FixtureRun"] = ["PalletFixtureID", "CurOrderID", "CurProcessID"],
        ["Orders"] = ["OrderID", "OrderName"],
        ["Processes"] = ["ProcessID", "ProcessNumber"],
        ["WorkSetCommand"] = ["DeviceID", "PalletFixtureID", "FixtureNumber", "OperationType", "OperationStatus", "ClampOrderID",
            "UnclampOrderID", "ClampQuantity"],
        ["ManualTransport"] = ["PalletID", "DestDeviceType", "DestDeviceNumber", "DestDevicePosition", "Status", "EntryDate"],
        ["DeviceParameters"] = ["DeviceID", "ParameterName", "ParameterValue"],
    };

    private static SchemaInfo SchemaWithout(params string[] tableDotColumn) =>
        new(Schema
            .SelectMany(t => t.Value.Select(c => (Table: t.Key, Column: c)))
            .Where(tc => !tableDotColumn.Contains(tc.Table + "." + tc.Column) && !tableDotColumn.Contains(tc.Table)));

    private static SqlMakinoReader.AttentionRows Rows(
        SqlMakinoReader.AttentionDevice[]? devices = null, SqlMakinoReader.AttentionFixture[]? fixtures = null,
        SqlMakinoReader.AttentionCommand[]? commands = null, SqlMakinoReader.AttentionTransport[]? transports = null,
        SqlMakinoReader.AttentionTools[]? tools = null) =>
        new(devices ?? [], fixtures ?? [], commands ?? [], transports ?? [], tools ?? []);

    private static AttentionList Build(SqlMakinoReader.AttentionRows rows, int cap = 500, bool includeParked = true) =>
        SqlMakinoReader.BuildAttention(Now, cap, rows, [], includeParked);

    [Fact]
    public void A_device_needing_recovery_is_one_item_with_every_reason()
    {
        var list = Build(Rows(devices:
        [
            new("MC1", 1) { Connected = true, Emergency = true, Recovery = true, Status = 4, OpenAlarms = 2 },
            new("MC2", 1) { Connected = true, Emergency = false, Recovery = false, Status = 2 },
            new("WSS1", 3) { Connected = false, Status = 0 },
        ]));

        Assert.Equal(2, list.Items.Count);
        var mc1 = list.Items[0];
        Assert.Equal("device", mc1.Kind);
        Assert.Equal("MC1", mc1.Device);
        Assert.Equal("recovery request, emergency stop, error", mc1.What);
        Assert.Equal("2 open alarms", mc1.Detail);
        Assert.Equal("disconnected", list.Items[1].What);
    }

    [Fact]
    public void Closed_and_skipped_devices_are_low_rank_items_and_all_closed_is_one_item()
    {
        var some = Build(Rows(devices:
        [
            new("MC1", 1) { Connected = true, Open = false, Status = 1 },
            new("MC2", 1) { Connected = true, Open = true, Status = 2, Recovery = true },
            new("WSS1", 3) { Connected = true, Open = true, Skip = true },
        ]));
        Assert.Equal(["recovery request", "closed", "skipped"], some.Items.Select(i => i.What));
        Assert.Equal("MC1", some.Items[1].Device);
        Assert.Equal(1, some.Counts["closed"]);
        Assert.Equal(1, some.Counts["skipped"]);

        var all = Build(Rows(devices:
        [
            new("MC1", 1) { Connected = true, Open = false }, new("WSS1", 3) { Connected = true, Open = false },
            new("VHC1", 2) { Connected = true, Open = false },
        ]));
        var item = Assert.Single(all.Items);
        Assert.Equal("cell", item.Kind);
        Assert.Equal(SqlMakinoReader.AllClosed, item.What);
        Assert.Equal(3, all.Counts["closed"]);
    }

    [Fact]
    public void A_recovery_item_says_what_the_device_was_doing()
    {
        var list = Build(Rows(
            devices:
            [
                new("MC1", 1) { Connected = true, Recovery = true, OpenAlarms = 1, Action = 1, TablePallet = 31 },
                new("VHC1", 2) { Connected = true, Recovery = true, From = "MC1", To = "STK1", Command = 1 },
                new("WSS1", 3) { Connected = true, Recovery = true },
            ],
            commands: [new("WSS1", 4, 1, Operation: 2, Status: null, Order: null, Quantity: null)]));

        Assert.Equal("1 open alarm, was in machining, table pallet 31", list.Items[0].Detail);
        Assert.Equal("transport MC1 → STK1, load/unload", list.Items[1].Detail);
        Assert.Equal("instruction unclamp and clamp", list.Items[2].Detail);
    }

    [Fact]
    public void A_machine_recovery_with_a_lacking_pallet_on_the_table_is_a_lacking_tool_recovery()
    {
        var rows = Rows(
            devices: [new("MC1", 1) { Connected = true, Recovery = true, TablePallet = 7 }],
            fixtures: [new(7, 1) { LackingTool = true }]);

        var on = Build(rows with { LackingToolRecovery = true });
        Assert.Equal("lacking-tool recovery request", on.Items[0].What);
        Assert.EndsWith("then Retry at the console", on.Items[0].Detail);
        Assert.Equal(1, on.Counts["recovery request"]);
        Assert.False(on.Counts.ContainsKey("lacking-tool recovery request"));

        // The cell doesn't raise recoveries for missing tools: a plain recovery.
        Assert.Equal("recovery request", Build(rows).Items[0].What);

        Assert.True(SqlMakinoReader.LackingToolRecoveryOn(new Dictionary<string, string> { ["LackingToolsMachineRecovery"] = "3" }));
        Assert.True(SqlMakinoReader.LackingToolRecoveryOn(new Dictionary<string, string> { ["ToolResourceManagementType"] = "2" }));
        Assert.False(SqlMakinoReader.LackingToolRecoveryOn(new Dictionary<string, string>()));
        Assert.False(SqlMakinoReader.LackingToolRecoveryOn(null));
    }

    [Fact]
    public void A_disconnected_devices_emergency_flag_is_stale_and_not_reported()
    {
        var list = Build(Rows(devices: [new("VHC1", 2) { Connected = false, Emergency = true, Status = 0 }]));

        Assert.Equal("disconnected", Assert.Single(list.Items).What);
        Assert.False(list.Counts.ContainsKey("emergency stop"));
    }

    [Fact]
    public void Lacking_tools_count_only_when_the_cell_works_them_out()
    {
        // Off (0, or no row: the vendor's default) clears the flag, so "none" would be a false all-clear.
        Assert.Equal((false, null), SqlMakinoReader.LackingToolSetting(new Dictionary<string, string>()));
        Assert.Equal((false, null), SqlMakinoReader.LackingToolSetting(new Dictionary<string, string> { ["FixtureToolLackInfo"] = "0" }));
        Assert.Equal((true, "its next machining job"),
            SqlMakinoReader.LackingToolSetting(new Dictionary<string, string> { ["FixtureToolLackInfo"] = "1" }));
        Assert.Equal((true, "its machining jobs before the next work setting"), SqlMakinoReader.LackingToolSetting(
            new Dictionary<string, string> { ["FixtureToolLackInfo"] = "2", ["CheckLackToolsAllRemainingJobs"] = "1" }));
        // Settings this login can't read: nothing is known.
        Assert.Equal((null, null), SqlMakinoReader.LackingToolSetting(null));

        // Off, the fixture queries act as if the column weren't there.
        var off = SchemaWithout().Without("PalletFixtures", "LackingTool");
        Assert.DoesNotContain("LackingTool = 1", SqlMakinoReader.AttentionFixturesSql(off, includeParked: true));
        Assert.DoesNotContain("LackingTool = 1", SqlMakinoReader.AttentionFixtureCountsSql(off));
        Assert.Contains("LackingTool = 1", SqlMakinoReader.AttentionFixturesSql(SchemaWithout(), includeParked: true));
        Assert.True(off.Has("PalletFixtures", "Suspend"));
    }

    [Fact]
    public void A_machine_lacking_tools_is_context_not_an_item_and_an_operator_call_is_an_item()
    {
        var list = Build(Rows(devices:
        [
            new("MC1", 1) { Connected = true, Status = 2, ToolStatus = 2 },
            new("MC2", 1) { Connected = true, Status = 2, ToolStatus = 1, OperatorCall = 1 },
        ]));

        var item = Assert.Single(list.Items);
        Assert.Equal("MC2", item.Device);
        Assert.Equal("operator call (M code)", item.What);
        Assert.Equal(new Dictionary<string, string> { ["MC1"] = "lacking tools", ["MC2"] = "ready" }, list.MachineToolStatus);
    }

    [Fact]
    public void A_fixture_is_one_item_with_its_conditions_reason_order_and_process()
    {
        var list = Build(Rows(fixtures:
        [
            new(12, 2) { Suspend = true, LackingTool = true, Reason = "waiting for material", Order = "ORDER-001", Process = 1 },
        ]));

        var item = Assert.Single(list.Items);
        Assert.Equal("fixture", item.Kind);
        Assert.Equal(12, item.Pallet);
        Assert.Equal(2, item.Fixture);
        Assert.Equal("lacking tools, machining suspended", item.What);
        Assert.Equal("waiting for material", item.Detail);
        Assert.Equal("ORDER-001", item.Order);
        Assert.Equal(1, item.Process);
        Assert.Equal(1, list.Counts["lacking tools"]);
        Assert.Equal(1, list.Counts["machining suspended"]);
    }

    [Fact]
    public void Pre_machining_suspend_says_why_and_reclamp_timeouts_are_named()
    {
        var list = Build(Rows(fixtures:
        [
            new(3, 1) { PreMcSuspend = true, PreMcReason = 2 },
            new(4, 1) { Reclamp = 1 },
            new(5, 1) { Reclamp = 0, Damaged = true, Hold = true },
            new(6, 1) { Reclamp = 2 },
            new(7, 1) { Reclamp = 9 },
        ]));

        Assert.Equal(
            ["pre-machining suspended (CSG error)", "reclamp timeout alarm", "damaged, work-set hold", "reclamp timeout warning",
                "reclamp code 9"],
            list.Items.Select(i => i.What));
    }

    [Fact]
    public void Most_severe_first_and_parked_fixtures_last()
    {
        var list = Build(Rows(
            devices: [new("MC1", 1) { Connected = true, Recovery = true, Status = 4 }],
            fixtures: [new(1, 1) { Suspend = true }, new(2, 1) { LackingTool = true }, new(3, 1) { Damaged = true }],
            commands: [new("WSS1", 4, 1, Operation: 2, Status: 0, Order: "ORDER-002", Quantity: 1)],
            transports:
            [
                new(6, "MC2", 1, Now.AddMinutes(-40)),
                new(7, "WSS1", 2, Now.AddMinutes(-5)),
            ],
            tools: [new("MC2", Alarm: 3, OffsetError: 1)]));

        Assert.Equal(
            ["device", "work set", "manual transport", "manual transport", "fixture", "tools", "fixture", "fixture"],
            list.Items.Select(i => i.Kind));
        Assert.Equal(7, list.Items[2].Pallet); // newer transport first
        Assert.Equal("waiting for transport", list.Items[3].What);
        Assert.Equal("to MC2", list.Items[3].Detail);
        Assert.Equal(Now.AddMinutes(-40), list.Items[3].Since);
        Assert.Equal("unclamp and clamp", list.Items[1].What);
        Assert.Equal("quantity 1", list.Items[1].Detail);
        Assert.Equal("3 tools in alarm, 1 with an offset error", list.Items[5].What);
        Assert.Equal("machining suspended", list.Items[^1].What);
    }

    [Fact]
    public void Lacking_tools_on_a_parked_fixture_waits_with_the_parked_ones_and_reasons_are_one_line()
    {
        var list = Build(Rows(
            fixtures:
            [
                new(1, 1) { Suspend = true, LackingTool = true, Reason = "  no   material\r\n since monday " },
                new(2, 1) { LackingTool = true },
            ],
            tools: [new("MC1", Alarm: 1, OffsetError: 0)]));

        Assert.Equal([(null, "1 tool in alarm"), (2, "lacking tools"), (1, "lacking tools, machining suspended")],
            list.Items.Select(i => (i.Pallet, i.What)));
        Assert.Equal("no material since monday", list.Items[2].Detail);
        Assert.Equal(2, list.Counts["lacking tools"]);
    }

    [Fact]
    public void Parked_fixtures_are_counts_by_default_and_items_on_request()
    {
        var rows = Rows(fixtures:
        [
            new(1, 1) { Suspend = true, Reason = "no material" },
            new(2, 1) { Suspend = true, LackingTool = true },
            new(3, 1) { Suspend = true, Damaged = true },
            new(4, 1) { Suspend = true, PreMcSuspend = true, PreMcReason = 1 },
            new(5, 1) { LackingTool = true },
            new(6, 1) { Hold = true, Reclamp = 1 },
        ]) with
        {
            Parked = [new ParkedGroup("pallet stocker", "machining suspended", 2) { LackingTools = 1 }],
        };

        var compact = Build(rows, includeParked: false);
        Assert.Equal([3, 4, 6, 5], compact.Items.Select(i => i.Pallet!.Value)); // damaged, pre-machining, reclamp alarm, lacking
        Assert.NotNull(compact.Parked);
        Assert.Equal(2, compact.Parked.Fixtures);
        Assert.Equal(1, compact.Parked.LackingTools);
        Assert.Equal("pallet stocker", Assert.Single(compact.Parked.Groups).Place);
        Assert.Equal(4, compact.Counts["machining suspended"]); // counts still cover every fixture

        var full = Build(rows, includeParked: true);
        Assert.Equal(6, full.Items.Count);
        Assert.Equal([1, 2], full.Items.TakeLast(2).Select(i => i.Pallet!.Value));
        Assert.NotNull(full.Parked);

        Assert.Null(Build(Rows(fixtures: [new(5, 1) { LackingTool = true }]), includeParked: false).Parked);
    }

    [Fact]
    public void Parked_groups_merge_stocker_shelves_and_name_devices()
    {
        var devices = new[] { (Type: 1, Number: 1, Name: "MC1") };
        var groups = SqlMakinoReader.MergeParked(
        [
            (4, 23, true, false, 5, 1), (4, 5, true, false, 3, 2), (1, 1, true, false, 2, 0), (4, 7, false, true, 1, 0),
            (null, null, true, true, 1, 0), (0, 0, true, false, 1, 0),
        ], devices);

        // Place type 0 is a pallet taken out of the system; no place at all is unknown.
        Assert.Equal(
            [("pallet stocker", "machining suspended", 8, (int?)3), ("MC1", "machining suspended", 2, null),
                ("pallet stocker", "work-set hold", 1, null), ("unknown", "machining suspended, work-set hold", 1, null),
                (SqlMakinoReader.OutOfSystemWords, "machining suspended", 1, null)],
            groups.Select(g => (g.Place, g.Condition, g.Fixtures, g.LackingTools)));
    }

    [Fact]
    public void A_station_instructions_progress_reads_as_the_work_set_operation_list()
    {
        var list = Build(Rows(commands: [new("WSS1", 4, 1, Operation: 2, Status: 2, Order: null, Quantity: 0)]));

        Assert.Equal("status clamp", Assert.Single(list.Items).Detail);
        // Set at the station, not known to be waiting on the operator: the count says no more than that.
        Assert.Equal(1, list.Counts["work-set instructions"]);

        // Rows cut at the cap still count the cell's total.
        var cut = Build(Rows(commands: [new("WSS1", 4, 1, 2, null, null, null)], transports: [new(7, "", 1, null)]) with
        {
            CommandTotal = 9, TransportTotal = 6,
        });
        Assert.Equal(9, cut.Counts["work-set instructions"]);
        Assert.Equal(6, cut.Counts["manual transports open"]);
    }

    [Fact]
    public void Missing_columns_name_the_parts_of_a_section_that_cant_be_shown()
    {
        var schema = new SchemaInfo(new[]
        {
            ("MachineStatus", "DeviceID"), ("MachineStatus", "Connected"), ("MachineStatus", "Status"),
            ("WorkSetStatus", "DeviceID"), ("WorkSetStatus", "Recovery"), ("WorkSetStatus", "Status"), ("WorkSetStatus", "Connected"),
            ("WorkSetStatus", "Emergency"),
            ("ViewVehicleStatus", "DeviceNumber"), ("ViewVehicleStatus", "Status"), ("ViewVehicleStatus", "Recovery"),
            ("ViewVehicleStatus", "Connected"), ("ViewVehicleStatus", "Emergency"),
            ("PalletFixtures", "PalletFixtureID"), ("PalletFixtures", "Suspend"), ("PalletFixtures", "Damaged"),
        });

        var missing = SqlMakinoReader.AttentionColumnsMissing(schema);

        Assert.Contains("machine recovery requests", missing);
        Assert.Contains("machine emergency stop", missing);
        Assert.Contains("machine operator calls", missing);
        Assert.Contains("machine tool status", missing);
        Assert.Contains("work-set station cycle counts", missing);
        Assert.Contains("vehicle cycle count", missing);
        Assert.Contains("fixtures on hold", missing);
        Assert.Contains("pre-machining suspended fixtures", missing);
        Assert.Contains("fixtures lacking tools", missing);
        Assert.Contains("reclamp timeouts", missing);
        Assert.DoesNotContain("suspended fixtures", missing);
        Assert.DoesNotContain("damaged fixtures", missing);
        Assert.DoesNotContain(missing, m => m.StartsWith("work-set station recovery", StringComparison.Ordinal));

        var bare = SqlMakinoReader.AttentionColumnsMissing(new SchemaInfo(new[] { ("PalletFixtures", "PalletFixtureID") }));
        Assert.Contains("fixtures (suspended, damaged, lacking tools, …)", bare);
    }

    [Fact]
    public void Cycle_count_over_a_set_limit_is_maintenance_and_no_limit_is_nothing()
    {
        var list = Build(Rows(devices:
        [
            new("WSS1", 3) { Connected = true, Status = 1, Cycles = 50_100, CycleLimit = 50_000 },
            new("WSS2", 3) { Connected = true, Status = 1, Cycles = 90_000, CycleLimit = 0 },
            new("Vehicle 1", 2) { Connected = true, Status = 1, Cycles = 10, CycleLimit = null },
        ]));

        var item = Assert.Single(list.Items);
        Assert.Equal("maintenance", item.Kind);
        Assert.Equal("WSS1", item.Device);
        Assert.Equal("cycle count over its maintenance limit", item.What);
        Assert.Equal("50100 cycles, limit 50000", item.Detail);
    }

    [Fact]
    public void Capped_keeps_the_most_severe_and_counts_stay_complete()
    {
        var fixtures = Enumerable.Range(1, 5).Select(n => new SqlMakinoReader.AttentionFixture(n, 1) { Suspend = true }).ToArray();
        var list = Build(Rows(devices: [new("MC1", 1) { Connected = false }], fixtures: fixtures) with
        {
            FixtureCounts = new Dictionary<string, int> { ["machining suspended"] = 40 },
        }, cap: 3);

        Assert.True(list.Capped);
        Assert.Equal(3, list.Items.Count);
        Assert.Equal("device", list.Items[0].Kind);
        Assert.Equal(40, list.Counts["machining suspended"]); // the cell's full count, not the shown rows
        Assert.Equal(1, list.Counts["disconnected"]);
    }

    [Fact]
    public void Fixture_sql_checks_only_the_flags_this_cell_has_and_reads_the_current_order()
    {
        var full = SqlMakinoReader.AttentionFixturesSql(SchemaWithout(), includeParked: true);
        Assert.Contains("TOP (@take)", full);
        Assert.Contains("pf.Suspend = 1", full);
        Assert.Contains("pf.LackingTool = 1", full);
        Assert.Contains("pf.ReclampStatus IN (1, 2)", full);
        Assert.Contains("WHEN pf.LackingTool = 1 AND ISNULL(pf.Suspend, 0) = 0 AND ISNULL(pf.WorkSetHold, 0) = 0 THEN 1", full);
        // Parked fixtures sort after everything else, so the cap drops them first (never a reclamp warning).
        Assert.Contains("ISNULL(pf.ReclampStatus, 0) = 0 THEN 3 ELSE 2 END", full);
        Assert.Contains("LEFT JOIN FixtureRun fr ON fr.PalletFixtureID = pf.PalletFixtureID", full);
        Assert.Contains("o.OrderName AS OrderName", full);

        var old = SqlMakinoReader.AttentionFixturesSql(SchemaWithout("PalletFixtures.LackingTool", "PalletFixtures.ReclampStatus", "FixtureRun"), true);
        Assert.DoesNotContain("LackingTool = 1", old);
        Assert.DoesNotContain("ReclampStatus IN", old);
        Assert.DoesNotContain("FixtureRun", old);
        Assert.Contains("CAST(NULL AS varchar(4000)) AS OrderName", old);

        var bare = SqlMakinoReader.AttentionFixturesSql(SchemaWithout("PalletFixtures.Suspend", "PalletFixtures.WorkSetHold",
            "PalletFixtures.Damaged", "PalletFixtures.PreMcSuspend", "PalletFixtures.LackingTool", "PalletFixtures.ReclampStatus"), false);
        Assert.Contains("WHERE 1 = 0", bare);
        // An integer constant in ORDER BY is a column position (SQL Server error 108), so no "ORDER BY 0".
        Assert.DoesNotContain("ORDER BY 0", bare);
        Assert.Contains("ORDER BY (SELECT 0)", bare);
    }

    [Fact]
    public void Parked_fixtures_are_left_out_of_the_items_sql_and_grouped_by_place()
    {
        const string parked = "(pf.Suspend = 1 OR pf.WorkSetHold = 1) AND ISNULL(pf.Damaged, 0) = 0 AND ISNULL(pf.PreMcSuspend, 0) = 0 AND ISNULL(pf.ReclampStatus, 0) = 0";
        Assert.Contains($"AND NOT ({parked})", SqlMakinoReader.AttentionFixturesSql(SchemaWithout(), includeParked: false));
        Assert.DoesNotContain("AND NOT", SqlMakinoReader.AttentionFixturesSql(SchemaWithout(), includeParked: true));

        var groups = SqlMakinoReader.AttentionParkedSql(SchemaWithout());
        Assert.NotNull(groups);
        Assert.Contains($"WHERE {parked}", groups);
        Assert.Contains("FROM PalletLocation l WHERE l.PalletID = pf.PalletID", groups);
        Assert.Contains("GROUP BY", groups);
        Assert.Contains("CAST(NULL AS int) AS CurDeviceType", SqlMakinoReader.AttentionParkedSql(SchemaWithout("PalletLocation")));
        Assert.Null(SqlMakinoReader.AttentionParkedSql(SchemaWithout("PalletFixtures.Suspend", "PalletFixtures.WorkSetHold")));
    }

    [Fact]
    public void Pending_station_instructions_transports_and_cycle_limits_are_read_bounded()
    {
        var commands = SqlMakinoReader.WorkSetCommandsSql(SchemaWithout());
        Assert.Contains("TOP (@take)", commands);
        Assert.Contains("c.OperationType <> 0", commands);

        var transports = SqlMakinoReader.ManualTransportsSql(SchemaWithout());
        Assert.Contains("TOP (@take)", transports);
        Assert.Contains("t.Status NOT IN (4, 5)", transports);
        Assert.Contains("ORDER BY t.EntryDate DESC", transports);
        // Each carries the total past the row cap, so counts stay whole when the list is cut.
        Assert.Contains("COUNT(*) OVER () AS Total", commands);
        Assert.Contains("COUNT(*) OVER () AS Total", transports);

        var limits = SqlMakinoReader.CycleLimitSql(SchemaWithout());
        Assert.Contains("ParameterName = 'WarningCycleCount'", limits);
        Assert.Null(SqlMakinoReader.CycleLimitSql(SchemaWithout("DeviceParameters")));
    }
}
