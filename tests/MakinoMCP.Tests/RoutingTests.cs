using Xunit;
using Xunit.Abstractions;
using MakinoMCP.Core.Cell;
using MakinoMCP.Core.Settings;

namespace MakinoMCP.Tests;

// get_part_routing's 2026-11 additions (RT-2..RT-11 and the process diagnosis), without a database except the one Live
// test. Sample values only.
public class RoutingTests(ITestOutputHelper output)
{
    private static readonly Dictionary<string, string[]> Schema = new()
    {
        ["Devices"] = ["DeviceID", "DeviceName", "DeviceType", "DeviceNumber"],
        ["Parts"] = ["PartID", "PartName", "Revision", "Comment", "UpdateDate"],
        ["Processes"] = ["ProcessID", "PartID", "ProcessNumber", "ProcessName", "Status", "MaxFixtures", "OffDutyProcess"],
        ["Operations"] = ["OperationID", "ProcessID", "OperationSequence", "ClampQuantity", "UnclampMultiplier", "OperationName"],
        ["Jobs"] = ["JobID", "ProcessID", "JobNumber", "JobType"],
        ["MachineJobs"] = ["JobID", "NCProgramFileID", "MachiningTime", "OperatorCall", "TimeStudy", "OptionalBlockSkip", "ONumber"],
        ["WorkSetJobs"] = ["JobID", "OperationType", "InformationFileID", "WorkSetTime"],
        ["JobFeasibleDevices"] = ["JobID", "FeasibleDeviceID"],
        ["NCProgramFiles"] = ["NCProgramFileID", "Name", "UpdateDate"],
        ["ONumbers"] = ["ONumberID", "ONumber", "NCProgramFileID", "ProgramType", "FileIndex"],
        ["MainPrograms"] = ["NCProgramFileID", "studyMode", "StudiedDate", "DeviceID", "PalletID"],
        ["OffsetFiles"] = ["OffsetFileID", "Name"],
        ["FixtureProcesses"] = ["FixtureID", "ProcessID", "FixturePriority", "Enabled", "OffsetFileID"],
        ["Fixtures"] = ["FixtureID", "FixtureName", "OffsetFileID"],
        ["PalletFixtures"] = ["PalletFixtureID", "PalletID", "FixtureNumber", "FixtureID", "OffsetFileID", "Suspend",
            "WorkSetHold", "Damaged", "PreMcSuspend"],
        ["FixtureRun"] = ["PalletFixtureID", "CurProcessID", "CurOrderID"],
        ["Orders"] = ["OrderID", "PartID", "OrderName", "Status", "MachiningSuspended", "DueDate"],
        ["Pallets"] = ["PalletID", "PalletNumber"],
        ["IndividualToolData"] = ["ITNID", "ITN", "FTNID", "Status", "ProhibitTool", "AbsentTool", "AlarmTool"],
        ["IndividualToolCutterData"] = ["ITNID", "CutterNo", "TLWarning"],
        ["FunctionalToolData"] = ["FTNID", "FTN", "FTNComment"],
        ["ToolLocation"] = ["ITNID", "CurrentDeviceID"],
        ["MainProgramTools"] = ["NCProgramFileID", "ProgramToolNumber", "ToolCount", "CutterNo", "CuttingTime",
            "CuttingDistance", "CuttingCount", "ToolLifeType", "FTN"],
    };

    private static SchemaInfo SchemaWithout(params string[] tableDotColumn) =>
        new(Schema
            .SelectMany(t => t.Value.Select(c => (Table: t.Key, Column: c)))
            .Where(tc => !tableDotColumn.Contains(tc.Table + "." + tc.Column) && !tableDotColumn.Contains(tc.Table)));

    private static SqlMakinoReader.RoutingSettings Settings(params (string Name, string Value)[] values) =>
        SqlMakinoReader.RoutingSettingsFrom(values.ToDictionary(v => v.Name, v => v.Value, StringComparer.OrdinalIgnoreCase));

    // --- RT-2: the cell's own lacking rule ---

    [Fact]
    public void Tool_check_states_the_cells_own_lacking_rule()
    {
        var resource = SqlMakinoReader.ToolCheckWords(Settings(("ToolManagementType", "2"), ("ToolResourceManagementType", "1"),
            ("PTNDownloadType", "1"), ("DoNotShowlackingToolIfNoOrder", "1")));
        Assert.StartsWith("approximate", resource);
        Assert.Contains("also checks remaining life", resource);
        Assert.Contains("neededForType", resource);
        Assert.Contains("only for parts with an active order", resource);
        Assert.Contains("can be stricter", resource);

        Assert.Contains("schedules without checking tools", SqlMakinoReader.ToolCheckWords(Settings(("ToolManagementType", "1"))));
        Assert.Contains("only warns", SqlMakinoReader.ToolCheckWords(Settings(("ToolManagementType", "2"), ("ToolResourceManagementType", "2"))));
        Assert.Contains("unknown", SqlMakinoReader.ToolCheckWords(Settings()));             // no row: never assumed
        Assert.Contains("can't read", SqlMakinoReader.ToolCheckWords(SqlMakinoReader.RoutingSettingsFrom(null)));
    }

    [Fact]
    public void Tools_hold_work_only_on_a_tool_resource_cell_that_doesnt_only_inform()
    {
        Assert.True(Settings(("ToolManagementType", "2")).ToolsHoldWork);
        Assert.False(Settings(("ToolManagementType", "2"), ("ToolResourceManagementType", "2")).ToolsHoldWork);
        Assert.False(Settings(("ToolManagementType", "0")).ToolsHoldWork);
        Assert.Null(Settings().ToolsHoldWork);
        Assert.True(Settings(("PTNDownloadType", "2")).SumsPerType);
        Assert.False(Settings(("PTNDownloadType", "3")).SumsPerType);
    }

    // --- RT-6: program identity ---

    [Fact]
    public void Main_o_number_is_the_files_main_else_its_dataserver_start()
    {
        Assert.Equal(1001, SqlMakinoReader.MainONumber([(2, 0, 2001), (1, 0, 1001)], 7996));
        Assert.Equal(7996, SqlMakinoReader.MainONumber([(8, 0, 3001)], 7996)); // a dataserver file runs through M198
        Assert.Equal(4001, SqlMakinoReader.MainONumber([(8, 0, 3001), (64, 0, 4001)], 7996));
        Assert.Null(SqlMakinoReader.MainONumber([], 7996));
        Assert.Equal(["main", "private subprogram"], Codes.NcProgramTypes(3));
    }

    [Fact]
    public void Routing_sql_reads_the_new_columns_and_degrades()
    {
        var full = SqlMakinoReader.RoutingSql(SchemaWithout());
        Assert.Contains("mj.TimeStudy AS TimeStudy", full);
        Assert.Contains("mj.OptionalBlockSkip AS OptionalBlockSkip", full);
        Assert.Contains("LEFT JOIN MainPrograms mp ON mp.NCProgramFileID = mj.NCProgramFileID", full);
        Assert.Contains("o.UnclampMultiplier AS UnclampMultiplier", full);
        Assert.Contains("FROM ONumbers o", full);
        Assert.Contains("LEFT JOIN FixtureRun fr ON fr.PalletFixtureID = pf.PalletFixtureID", full);
        Assert.Contains("FROM FixtureRun fr WHERE fr.CurProcessID = pr.ProcessID", full); // inUse
        Assert.Contains("FROM OffsetFiles ofs", full);
        Assert.DoesNotContain("EXEC", full, StringComparison.OrdinalIgnoreCase);

        var old = SqlMakinoReader.RoutingSql(SchemaWithout("ONumbers", "MainPrograms", "OffsetFiles", "FixtureRun",
            "MachineJobs.TimeStudy", "FixtureProcesses"));
        Assert.Contains("CAST(NULL AS bit) AS TimeStudy", old);
        Assert.Contains("CAST(NULL AS int) AS StudyMode", old);
        Assert.DoesNotContain("FROM ONumbers", old);
        Assert.DoesNotContain("FixtureRun", old);
        Assert.Contains("CAST(NULL AS int) AS UseProcessID", old); // the empty fixture set keeps its columns
    }

    // --- RT-5, RT-7: job settings ---

    [Fact]
    public void Block_skips_and_tool_study_have_words()
    {
        Assert.Equal(["/2", "/5"], Codes.BlockSkips(0x9));
        Assert.Empty(Codes.BlockSkips(0));
        Assert.Equal(["/9", "code 256"], Codes.BlockSkips(0x180));
        Assert.Equal("one shot", Codes.ToolStudyMode(2));
        Assert.Equal("code 5", Codes.ToolStudyMode(5));
    }

    [Fact]
    public void One_shot_operator_call_follows_the_cells_setting()
    {
        Assert.Equal("permanent (every cycle)", SqlMakinoReader.OperatorCallWords(1, Settings()));
        Assert.Contains("once per fixture", SqlMakinoReader.OperatorCallWords(2, Settings()));
        Assert.Contains("next pallet only", SqlMakinoReader.OperatorCallWords(2, Settings(("ProcessOpeCallOneshotType", "1"))));
        Assert.Equal("one-shot", SqlMakinoReader.OperatorCallWords(2, SqlMakinoReader.RoutingSettingsFrom(null)));
    }

    // --- RT-9: effective offset file ---

    [Fact]
    public void Offset_file_follows_the_cells_priority_and_skips_the_process_level()
    {
        int[] standard = [8, 2, 1, 4];
        // Defaults: fixture and pallet fixture enabled; the fixture-process file is ignored.
        Assert.Equal(("FIX-A", false), SqlMakinoReader.OffsetFileFor(standard, 1 | 4, false, "FP-A", "FIX-A", "PF-A"));
        Assert.Equal(("PF-A", false), SqlMakinoReader.OffsetFileFor(standard, 1 | 4, false, "FP-A", null, "PF-A"));
        Assert.Equal(("FP-A", false), SqlMakinoReader.OffsetFileFor(standard, 1 | 2 | 4, false, "FP-A", "FIX-A", null));
        Assert.Equal(("PF-A", false), SqlMakinoReader.OffsetFileFor([4, 1], 1 | 4, false, null, "FIX-A", "PF-A"));
        // External pallet-fixture offsets end the search there with no file.
        Assert.Equal((null, true), SqlMakinoReader.OffsetFileFor([4, 1], 1 | 4, true, null, "FIX-A", "PF-A"));
        Assert.Equal((null, false), SqlMakinoReader.OffsetFileFor(standard, 8, false, "FP-A", "FIX-A", "PF-A"));
    }

    [Fact]
    public void Offset_settings_default_when_the_cell_has_no_row()
    {
        var none = Settings();
        Assert.Equal([8, 2, 1, 4], none.OffsetPriority);
        Assert.Equal(5, none.OffsetTypes);
        Assert.Equal([4, 1], Settings(("OffsetFileTypePriority", "4, 1")).OffsetPriority);
        Assert.True(Settings(("PalletFixtureOffsetType", "1")).PalletFixtureExternal);
    }

    // --- RT-4: fixture places ---

    [Fact]
    public void Fixture_places_say_what_they_hold_now()
    {
        Assert.Equal("free", SqlMakinoReader.PlaceUse(true, null, 7, "", null));
        Assert.Equal("this process", SqlMakinoReader.PlaceUse(true, 7, 7, "CONTOSO-100", 1));
        Assert.Equal("in use: FABRIKAM-200 process 2", SqlMakinoReader.PlaceUse(true, 9, 7, "FABRIKAM-200", 2));
        Assert.Null(SqlMakinoReader.PlaceUse(false, 9, 7, "FABRIKAM-200", 2));
        Assert.Equal("pallet 12 fixture 1: this process, suspended; offset file OFS-12",
            SqlMakinoReader.FixturePlaceState("pallet 12 fixture 1", "this process", ["suspended"], "offset file OFS-12"));
        Assert.Equal("pallet 12 fixture 1", SqlMakinoReader.FixturePlaceState("pallet 12 fixture 1", null, [], null));
    }

    // --- RT-10, RT-11: tool sets and elsewhere ---

    [Fact]
    public void Tool_set_status_uses_the_consoles_rules()
    {
        SqlMakinoReader.ToolState T(int unknown = 0, int warning = 0, int offset = 0, int alarm = 0) =>
            new(2, 0, unknown, warning, offset, alarm);
        Assert.Equal("ready", SqlMakinoReader.ToolSetWords([T(), T()], lacking: false));
        Assert.Equal("lacking, life warning", SqlMakinoReader.ToolSetWords([T(warning: 1), null], lacking: true));
        Assert.Equal("unknown", SqlMakinoReader.ToolSetWords([T(unknown: 1), T(alarm: 1)], lacking: true));
        Assert.Equal("alarm, offset error", SqlMakinoReader.ToolSetWords([T(alarm: 1), T(offset: 2)], lacking: false));
        Assert.Equal("lacking", SqlMakinoReader.ToolSetWords([null], lacking: true)); // no tools of the type there at all
        Assert.Null(SqlMakinoReader.ToolSetWords([new SqlMakinoReader.ToolState(1, 0, null, null, null, null)], false));
    }

    [Fact]
    public void Elsewhere_lists_three_tools_off_the_lacking_machines_and_counts_the_rest()
    {
        var usable = new List<(int, string, int)>
        {
            (101, "MC1", 1), (102, "CRIB1 (tool crib)", 9), (103, "CRIB1 (tool crib)", 9), (104, "MC2", 2), (105, "CRIB1 (tool crib)", 9),
        };
        var (shown, more) = SqlMakinoReader.Elsewhere(usable, new HashSet<int> { 1 }, total: 4);
        Assert.Equal(["ITN 102 at CRIB1 (tool crib)", "ITN 103 at CRIB1 (tool crib)", "ITN 104 at MC2"], shown);
        Assert.Equal(1, more);
        Assert.Equal((null, null), SqlMakinoReader.Elsewhere([(101, "MC1", 1)], new HashSet<int> { 1 }, total: 0));
    }

    [Fact]
    public void Tool_state_sql_counts_stored_statuses_and_flags_per_device()
    {
        var sql = SqlMakinoReader.ToolStatesSql(SchemaWithout(), 2);
        Assert.NotNull(sql);
        Assert.Contains("WHERE f.FTN IN (@ftn0, @ftn1)", sql);
        Assert.Contains("x.Stored = 0", sql);
        Assert.Contains("i.ProhibitTool = 1 OR i.AbsentTool = 1", sql);
        // Without a stored status nothing can be counted (the usable count needs it too): not available, never zero.
        Assert.Null(SqlMakinoReader.ToolStatesSql(SchemaWithout("IndividualToolData.Status"), 1));
        var list = SqlMakinoReader.UsableToolListSql(SchemaWithout(), 1);
        Assert.Contains("ROW_NUMBER() OVER (PARTITION BY f.FTN", list);
        Assert.Null(SqlMakinoReader.UsableToolListSql(SchemaWithout("IndividualToolData.ITN"), 1));
        Assert.Contains("AS FtnKnown", SqlMakinoReader.ProgramToolsSql(SchemaWithout()));
    }

    // --- the process diagnosis ---

    private static readonly SqlMakinoReader.DiagSettings OnDuty = new() { DutyModeOff = false };

    private static SqlMakinoReader.BlockerProcess Process(int id = 7, int number = 1, params SqlMakinoReader.BlockerPlace[] places) =>
        new(id, number, 1) { Assigned = places.Length, Places = places, Jobs = [new SqlMakinoReader.BlockerJob(2, 2, 0, [1, 2])] };

    private static SqlMakinoReader.BlockerPlace Place(string place, int? use, params string[] holds) => new(place, true, holds, use);

    private static readonly Dictionary<int, SqlMakinoReader.DiagDevice> Ready = new()
    {
        [1] = new("MC1", 1) { Connected = true, Emergency = false, SystemMode = true, Open = true },
        [2] = new("MC2", 1) { Connected = true, Emergency = false, SystemMode = true, Open = true },
    };

    [Fact]
    public void Nothing_blocks_a_process_with_an_order_a_free_place_and_a_ready_machine()
    {
        var (reasons, devices) = SqlMakinoReader.Blockers([Process(places: Place("pallet 1 fixture 1", 0))],
            [new("ORDER-001", 3, false)], Ready, null, Settings(), OnDuty, false);
        Assert.Equal([SqlMakinoReader.NothingBlocks], reasons);
        Assert.Equal("can run: nothing found that blocks it", devices!["MC1"]);
    }

    [Fact]
    public void Empty_cases_are_named_no_order_no_fixture_no_pallet_no_machine()
    {
        var noFixture = new SqlMakinoReader.BlockerProcess(7, 1, 1) { Assigned = 0, Jobs = [new(2, 2, 0, [])] };
        var noPallet = new SqlMakinoReader.BlockerProcess(8, 2, 1) { Assigned = 2 };
        var (reasons, _) = SqlMakinoReader.Blockers([noFixture, noPallet], [], Ready, null, Settings(), OnDuty, false);
        Assert.Contains(reasons, r => r.StartsWith("no active order", StringComparison.Ordinal));
        Assert.Contains("process 1: not assigned to any fixture", reasons);
        Assert.Contains("process 2: no pallet carries a fixture assigned to it", reasons);
        Assert.Contains("process 1 job 2 (machining): not assigned to any machine", reasons);
    }

    [Fact]
    public void Suspended_orders_held_places_and_the_fixture_limit_block()
    {
        var p = Process(places: [Place("pallet 1 fixture 1", 9), Place("pallet 2 fixture 1", 0, "suspended")]) with { MaxFixtures = 2, InUse = 2 };
        var (reasons, _) = SqlMakinoReader.Blockers([p],
            [new("ORDER-001", 4, false), new("ORDER-002", 3, true)], Ready, null, Settings(("OrderMachiningSuspend", "1")), OnDuty, false);
        Assert.Contains("no active order can run: ORDER-001: suspended; ORDER-002: running, machining suspended", reasons);
        Assert.Contains(reasons, r => r.StartsWith("process 1: no fixture place can take it now: 1 in use by other processes, 1 held", StringComparison.Ordinal));
        Assert.Contains("process 1: at its limit of 2 fixtures at once (2 in use)", reasons);

        // Without the cell's order machining suspend the flag doesn't count.
        var (plain, _) = SqlMakinoReader.Blockers([Process(places: Place("pallet 1 fixture 1", 0))],
            [new("ORDER-002", 3, true)], Ready, null, Settings(), OnDuty, false);
        Assert.Equal([SqlMakinoReader.NothingBlocks], plain);
    }

    [Fact]
    public void Machines_are_judged_with_the_pallet_diagnosis_rules_and_lacking_tools_on_a_tool_resource_cell()
    {
        var devices = new Dictionary<int, SqlMakinoReader.DiagDevice>(Ready)
        {
            [1] = Ready[1] with { Open = false },
        };
        var lacking = new Dictionary<(int, int), IReadOnlyList<int>> { [(1, 2)] = [2] };
        var resource = Settings(("ToolManagementType", "2"));
        var (reasons, states) = SqlMakinoReader.Blockers([Process(places: Place("pallet 1 fixture 1", 0))],
            [new("ORDER-001", 3, false)], devices, lacking, resource, OnDuty, false);
        Assert.Contains("process 1 job 2 (machining): no assigned machine can take it: MC1 machine closed; MC2 machine lacks the tools for the program", reasons);
        Assert.Equal("machine closed", states!["MC1"]);

        // A cell that doesn't check tools isn't held by them.
        var (data, _) = SqlMakinoReader.Blockers([Process(places: Place("pallet 1 fixture 1", 0))],
            [new("ORDER-001", 3, false)], devices, lacking, Settings(("ToolManagementType", "1")), OnDuty, false);
        Assert.Equal([SqlMakinoReader.NothingBlocks], data);
    }

    [Fact]
    public void An_off_duty_process_waits_while_the_cell_is_on_duty()
    {
        var p = Process(places: Place("pallet 1 fixture 1", 0)) with { OffDuty = true };
        var (reasons, states) = SqlMakinoReader.Blockers([p], [new("ORDER-001", 3, false)], Ready, null, Settings(), OnDuty, false);
        Assert.Contains(reasons, r => r.Contains("off-duty process", StringComparison.Ordinal));
        Assert.StartsWith("can run", states!["MC1"]); // the device itself is fine
    }

    [Fact]
    public void Not_checked_lists_what_the_console_keeps_in_memory()
    {
        var list = SqlMakinoReader.BlockersNotChecked([Process()], includeTools: false);
        Assert.Contains(list, l => l.Contains("system ready", StringComparison.Ordinal));
        Assert.Contains("tools (includeTools=false)", list);
        Assert.DoesNotContain(list, l => l.Contains("material groups", StringComparison.Ordinal));
    }

    [Fact]
    public void Part_orders_sql_is_parameterized_and_bounded()
    {
        var sql = SqlMakinoReader.PartOrdersSql(SchemaWithout());
        Assert.Contains("TOP (@take)", sql);
        Assert.Contains("o.PartID = @partId", sql);
        Assert.Contains("NOT IN (0, 5, 6)", sql);
        Assert.Null(SqlMakinoReader.PartOrdersSql(SchemaWithout("Orders.PartID")));
    }

    // --- live ---

    /// <summary>Routes a few real parts: prints what the routing and its blockers say (local runs only), asserts only
    /// their shape.</summary>
    [SkippableFact]
    public async Task Live_routing_shows_places_programs_and_blockers()
    {
        var path = Environment.GetEnvironmentVariable("MAKINOMCP_LIVE_SETTINGS");
        Skip.If(string.IsNullOrWhiteSpace(path), "MAKINOMCP_LIVE_SETTINGS is not set");
        var load = SettingsStore.Load(path!);
        Assert.True(load.Settings is not null, load.Problem);
        var reader = new SqlMakinoReader(load.Settings!.Makino, new Limits(500, load.Settings.Limits.MaxFileKB));
        var ct = CancellationToken.None;

        var orders = await reader.GetOrdersAsync(true, false, 30, ct);
        var parts = orders.Orders.Select(o => o.Part.Split(" rev ")[0]).Distinct().Take(8).ToList();
        Skip.If(parts.Count == 0, "no active orders");
        foreach (var name in parts)
        {
            var routing = await reader.GetPartRoutingAsync(name, null, true, ct);
            if (routing.Part is null) continue;
            var json = System.Text.Json.JsonSerializer.Serialize(routing);
            output.WriteLine($"part ({json.Length} bytes, capped {routing.Capped}, updated {routing.Part.Updated}):");
            output.WriteLine("  toolCheck: " + routing.ToolCheck);
            foreach (var p in routing.Part.Processes)
            {
                output.WriteLine($"  process {p.Number} [{p.Status}] max {p.MaxFixtures} inUse {p.InUse} {p.Note}");
                foreach (var f in (p.Fixtures ?? []).Take(4)) output.WriteLine("    place " + f);
                foreach (var o in p.Operations) output.WriteLine($"    op {o.Sequence}: clamp {o.ClampQuantity}, unclamp x{o.UnclampPerClamp}");
                foreach (var j in p.Jobs)
                {
                    output.WriteLine($"    job {j.Number} {j.Type}: O{j.ONumber} [{string.Join("/", j.ProgramType ?? [])}] updated {j.ProgramUpdated} " +
                                     $"call {j.OperatorCall} timeStudy {j.TimeStudy} skips {string.Join("", j.BlockSkips ?? [])} " +
                                     $"study {j.ToolStudy?.Mode}/{j.ToolStudy?.Studied}/{j.ToolStudy?.Note} note {j.Note} " +
                                     $"devices {(j.FeasibleDevices is null ? "-" : j.FeasibleDevices.Count.ToString())}");
                    output.WriteLine("      toolSet " + string.Join("; ", (j.ToolSet ?? new Dictionary<string, string>()).Select(t => $"{t.Key}: {t.Value}")));
                    foreach (var t in (j.Tools ?? []).Where(t => t.MissingOn is not null || t.Note is not null || t.UsableButFlagged is not null).Take(3))
                        output.WriteLine($"      tool: needed {t.Needed}/{t.NeededForType} missing {t.MissingOn?.Count} elsewhere {t.Elsewhere?.Count}+{t.ElsewhereMore} flagged {t.UsableButFlagged?.Count} {t.Note}");
                }
            }
            var b = routing.Blockers!;
            output.WriteLine("  reasons: " + string.Join(" | ", b.Reasons));
            output.WriteLine($"  orders {b.Orders?.Count} (+{b.OtherOrders}); devices: " +
                             string.Join("; ", (b.Devices ?? new Dictionary<string, string>()).Select(d => $"{d.Key}: {d.Value}")));
            output.WriteLine("  notAvailable: " + string.Join(" | ", routing.NotAvailable ?? []));

            Assert.NotEmpty(b.Reasons);
            Assert.NotEmpty(b.NotChecked);
            Assert.All(b.Reasons, r => Assert.False(string.IsNullOrWhiteSpace(r)));
            Assert.All(routing.Part.Processes.SelectMany(p => p.Jobs).Where(j => j.FeasibleDevices is { Count: 0 }),
                j => Assert.Contains("not assigned", j.Note));
            foreach (var j in routing.Part.Processes.SelectMany(p => p.Jobs).Where(j => j.ToolSet is not null))
            {
                Assert.All(j.ToolSet!.Keys, m => Assert.Contains(m, j.FeasibleDevices!));
                foreach (var t in j.Tools ?? [])
                    Assert.All(t.MissingOn ?? [], m => Assert.True(j.ToolSet[m].Contains("lacking") || j.ToolSet[m] == "unknown"));
            }
            Assert.All(routing.Part.Processes, p => Assert.True(p.InUse is null or > 0));
        }
    }
}
