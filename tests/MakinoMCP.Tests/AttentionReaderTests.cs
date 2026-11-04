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

    private static AttentionList Build(SqlMakinoReader.AttentionRows rows, int cap = 500) =>
        SqlMakinoReader.BuildAttention(Now, cap, rows, []);

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
        var full = SqlMakinoReader.AttentionFixturesSql(SchemaWithout());
        Assert.Contains("TOP (@take)", full);
        Assert.Contains("pf.Suspend = 1", full);
        Assert.Contains("pf.LackingTool = 1", full);
        Assert.Contains("pf.ReclampStatus IN (1, 2)", full);
        Assert.Contains("WHEN pf.LackingTool = 1 AND ISNULL(pf.Suspend, 0) = 0 AND ISNULL(pf.WorkSetHold, 0) = 0 THEN 1", full);
        Assert.Contains("LEFT JOIN FixtureRun fr ON fr.PalletFixtureID = pf.PalletFixtureID", full);
        Assert.Contains("o.OrderName AS OrderName", full);

        var old = SqlMakinoReader.AttentionFixturesSql(SchemaWithout("PalletFixtures.LackingTool", "PalletFixtures.ReclampStatus", "FixtureRun"));
        Assert.DoesNotContain("LackingTool = 1", old);
        Assert.DoesNotContain("ReclampStatus IN", old);
        Assert.DoesNotContain("FixtureRun", old);
        Assert.Contains("CAST(NULL AS varchar(4000)) AS OrderName", old);

        var bare = SqlMakinoReader.AttentionFixturesSql(SchemaWithout("PalletFixtures.Suspend", "PalletFixtures.WorkSetHold",
            "PalletFixtures.Damaged", "PalletFixtures.PreMcSuspend", "PalletFixtures.LackingTool", "PalletFixtures.ReclampStatus"));
        Assert.Contains("WHERE 1 = 0", bare);
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

        var limits = SqlMakinoReader.CycleLimitSql(SchemaWithout());
        Assert.Contains("ParameterName = 'WarningCycleCount'", limits);
        Assert.Null(SqlMakinoReader.CycleLimitSql(SchemaWithout("DeviceParameters")));
    }
}
