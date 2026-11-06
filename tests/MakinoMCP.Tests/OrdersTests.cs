using System.Text.Json;
using Xunit;
using MakinoMCP.Core.Cell;
using MakinoMCP.Core.Folders;
using MakinoMCP.Core.Settings;
using MakinoMCP.Tools;

namespace MakinoMCP.Tests;

// Pallets, orders and production (the 2026-11 manual review: PA-*, OR-*, PR-*), without a database.
public class OrdersTests
{
    private static readonly Dictionary<string, string[]> Schema = new()
    {
        ["Devices"] = ["DeviceID", "DeviceName", "DeviceType", "DeviceNumber"],
        ["Orders"] = ["OrderID", "PartID", "OrderName", "Comment", "Quantity", "Priority", "Status", "Status2", "StartDate", "DueDate",
            "CompleteDate", "RegistrationDate", "UpdateDate", "MachiningSuspended"],
        ["Parts"] = ["PartID", "PartName", "Revision", "QuickEntry"],
        ["Processes"] = ["ProcessID", "PartID", "ProcessNumber"],
        ["Quantities"] = ["OrderID", "ProcessID", "RemainingQuantity", "NormalQuantity", "ScrapQuantity", "OutProcessQuantity"],
        ["FixtureQuantity"] = ["PalletFixtureID", "ClampedQuantity"],
        ["Pallets"] = ["PalletID", "PalletNumber", "PalletName", "PalletStatus", "WorkStatus", "PalletType", "Priority", "OperatorPriority"],
        ["PalletLocation"] = ["PalletID", "CurDeviceType", "CurDeviceNumber", "CurDevicePosition"],
        ["PalletFixtures"] = ["PalletFixtureID", "PalletID", "FixtureNumber", "FixtureID", "Comment", "Suspend", "WorkSetHold", "Damaged",
            "PreMcSuspend", "PreMcSuspendReason", "OffDutyProcess", "ReclampStatus", "LackingTool", "OperatorCallStatus",
            "LastOneShotOpeCallProcessID", "ClampWaitingSuspendDisable"],
        ["Fixtures"] = ["FixtureID", "FixtureName"],
        ["FixtureProcesses"] = ["FixtureID", "ProcessID"],
        ["FixtureRun"] = ["PalletFixtureID", "CurOrderID", "CurProcessID", "CurJobID", "CurJobStatus", "WorkStatus",
            "MachiningFinishStatus", "CleaningFinishStatus", "DeburringFinishStatus"],
        ["Jobs"] = ["JobID", "ProcessID", "JobNumber", "JobType"],
        ["MachineJobs"] = ["JobID", "OperatorCall"],
        ["PalletFeasibleDevice"] = ["PalletID", "FeasibleDeviceID"],
        ["OrderResults"] = ["OrderName", "PartName", "Revision", "RequiredQuantity", "NormalQuantity", "ScrapQuantity",
            "RegistrationDate", "CompleteDate", "ResultDate", "IsDelete"],
        ["MachineResults"] = ["StartDateTime", "FinishDateTime", "OrderName", "PartName", "Revision"],
        ["WorkSetResults"] = ["StartDateTime", "FinishDateTime", "ClampOrderName", "ClampPartName", "ClampRevision"],
        ["ViewOrders"] = ["OrderName", "PartName", "PartRevision", "NormalQty", "ScrapQty", "RemainQty", "InProcQty"],
    };

    private static SchemaInfo SchemaWithout(params string[] tableDotColumn) =>
        new(Schema
            .SelectMany(t => t.Value.Select(c => (Table: t.Key, Column: c)))
            .Where(tc => !tableDotColumn.Contains(tc.Table + "." + tc.Column) && !tableDotColumn.Contains(tc.Table)));

    private static readonly SqlMakinoReader.FixtureSettings AllOn = new(false, true, true, true);

    // --- codes ---

    [Fact]
    public void New_code_tables_give_words_and_code_N_for_unknown_values()
    {
        Assert.Equal("table", Codes.DevicePosition(1));
        Assert.Equal("buffer 2", Codes.DevicePosition(5));
        Assert.Equal("code 9", Codes.DevicePosition(9));
        Assert.Equal("standard", Codes.PalletType(0));
        Assert.Equal("undefined", Codes.PalletType(-1));
        Assert.Equal("material", Codes.PalletType(4));
        Assert.Equal("rotary work head (5-axis table), material", Codes.PalletType(5));
        Assert.Equal("material, code 64", Codes.PalletType(68));
        Assert.Equal("one-shot (fixture setting)", Codes.FixtureOperatorCall(3));
        Assert.Equal("skipped", Codes.CommandFinish(13));
        Assert.Equal("code 99", Codes.CommandFinish(99));
    }

    // --- PA-4 ---

    [Fact]
    public void Positions_are_words_at_machines_stations_and_the_vehicle_and_shelf_numbers_elsewhere()
    {
        Assert.Equal("MC1, table", SqlMakinoReader.PlaceWords("MC1", 1, 1));
        Assert.Equal("WSS1, front", SqlMakinoReader.PlaceWords("WSS1", 3, 2));
        Assert.Equal("VHC1, buffer 1", SqlMakinoReader.PlaceWords("VHC1", 2, 4));
        Assert.Equal("pallet stocker 1, position 12", SqlMakinoReader.PlaceWords("pallet stocker 1", 4, 12));
        Assert.Equal("MC1", SqlMakinoReader.PlaceWords("MC1", 1, 0));
    }

    // --- PA-5 ---

    [Fact]
    public void A_nameless_fixture_of_one_process_is_labelled_by_its_part_and_otherwise_by_its_comment()
    {
        var f = new SqlMakinoReader.FixtureRow(1);
        Assert.Equal("FIXTURE-A", SqlMakinoReader.FixtureLabel(f with { Name = "FIXTURE-A", ProcessCount = 1, FixturePart = "X" }));
        Assert.Equal("CONTOSO-BRACKET | B | process 2",
            SqlMakinoReader.FixtureLabel(f with { ProcessCount = 1, FixturePart = "CONTOSO-BRACKET", FixtureRevision = "B", FixtureProcess = 2 }));
        Assert.Equal("CONTOSO-BRACKET | --- | process 1",
            SqlMakinoReader.FixtureLabel(f with { ProcessCount = 1, FixturePart = "CONTOSO-BRACKET", FixtureProcess = 1 }));
        Assert.Equal("vise left", SqlMakinoReader.FixtureLabel(f with { ProcessCount = 2, FixturePart = "X", Comment = "vise   left" }));
        Assert.Equal("", SqlMakinoReader.FixtureLabel(f));
    }

    // --- PA-1, PA-7 ---

    [Fact]
    public void Fixture_states_follow_the_console_order_then_the_additions()
    {
        var row = new SqlMakinoReader.FixtureRow(2)
        {
            Name = "FIXTURE-B", JobId = 10, JobStatus = 1, JobType = 2, JobNumber = 2, Order = "ORDER-001", Part = "CONTOSO-BRACKET",
            Revision = "A", Process = 1, Damaged = true, Suspend = true, Hold = true, PreMcSuspend = true, PreMcReason = 2,
            OperatorCallStatus = 2, MachiningAhead = true, OffDutyProcess = true, CleaningBefore = true, CleaningFinish = 13,
            Reclamp = 1, LackingTool = true, ClampWaitDisabled = true,
        };
        var d = SqlMakinoReader.FixtureOf(row, AllOn);

        Assert.Equal(
            ["damaged", "machining suspended", "work-set hold", "pre-machining suspended (CSG error)", "operator call",
             "off-duty process", "cleaning skipped", "reclamp timeout alarm", "lacking tools", "clamp-wait hold disabled"],
            d.States);
        Assert.Equal("permanent (fixture setting)", d.OperatorCall);
        Assert.Equal("ORDER-001", d.Order);
        Assert.Equal("CONTOSO-BRACKET rev A", d.Part);
        Assert.Equal("machining, job 2", d.Job);
        Assert.Equal("waiting for processing", d.JobStatus);
        Assert.Null(d.Empty);
        Assert.Null(d.WorkStatus);
    }

    [Fact]
    public void Settings_gate_the_states_that_mean_nothing_without_them()
    {
        var row = new SqlMakinoReader.FixtureRow(1)
        {
            JobId = 10, JobStatus = 1, OffDutyProcess = true, LackingTool = true, ClampWaitDisabled = true,
            CleaningFinish = 13, // skipped, but no cleaning job came before the current one
        };
        var off = new SqlMakinoReader.FixtureSettings(false, OffDutyProcesses: false, ClampWaitHold: false, LackingComputed: false);
        Assert.Null(SqlMakinoReader.FixtureOf(row, off).States);
        Assert.Equal(["off-duty process", "lacking tools", "clamp-wait hold disabled"], SqlMakinoReader.FixtureOf(row, AllOn).States);
    }

    [Fact]
    public void Operator_call_comes_from_the_fixture_setting_only_with_a_machining_job_ahead_else_from_the_jobs()
    {
        var row = new SqlMakinoReader.FixtureRow(1) { JobId = 10, JobStatus = 1, OperatorCallStatus = 3 };
        Assert.Null(SqlMakinoReader.FixtureOf(row, AllOn).OperatorCall);
        Assert.Equal("one-shot (fixture setting)", SqlMakinoReader.FixtureOf(row with { MachiningAhead = true }, AllOn).OperatorCall);
        Assert.Equal("a remaining machining job calls the operator",
            SqlMakinoReader.FixtureOf(row with { OperatorCallStatus = 0, JobCallAhead = true }, AllOn).OperatorCall);
        // Nothing to do (job status nothing): no call.
        Assert.Null(SqlMakinoReader.FixtureOf(row with { JobStatus = 0, JobCallAhead = true }, AllOn).OperatorCall);
    }

    [Fact]
    public void Work_status_shows_only_scrap_with_how_the_cycle_ended_or_a_cancel()
    {
        var row = new SqlMakinoReader.FixtureRow(1) { JobId = 10, JobStatus = 1, WorkStatus = 2, MachiningFinish = 0x10000 };
        Assert.Equal("scrap (machining: BTS alarm (broken tool))", SqlMakinoReader.FixtureOf(row, AllOn).WorkStatus);
        Assert.Equal("scrap (machining: BTS alarm (broken tool); deburring: tool error)",
            SqlMakinoReader.FixtureOf(row with { DeburringBefore = true, DeburringFinish = 12 }, AllOn).WorkStatus);
        Assert.Equal("order cancelled", SqlMakinoReader.FixtureOf(row with { WorkStatus = 4 }, AllOn).WorkStatus);
        Assert.Null(SqlMakinoReader.FixtureOf(row with { WorkStatus = 1 }, AllOn).WorkStatus);
    }

    [Fact]
    public void A_fixture_without_work_is_empty_and_keeps_its_stored_flags()
    {
        var d = SqlMakinoReader.FixtureOf(new SqlMakinoReader.FixtureRow(3) { Name = "FIXTURE-C", Suspend = true, Order = "stale" }, AllOn);
        Assert.True(d.Empty);
        Assert.Null(d.Order);
        Assert.Equal(["machining suspended"], d.States);
        Assert.Equal(["fixture 3 FIXTURE-C: empty, machining suspended"], SqlMakinoReader.FixtureLines([(d, "FIXTURE-C")]));
        Assert.Equal(["fixture 4 FIXTURE-D"], SqlMakinoReader.FixtureLines([(new FixtureDetail(4, "FIXTURE-D"), "FIXTURE-D")]));
    }

    [Fact]
    public void Short_lines_leave_derived_labels_to_the_details_and_share_a_line_for_nameless_fixtures_in_a_row()
    {
        FixtureDetail Parked(int n) => new(n, "CONTOSO-BRACKET | --- | process 1") { States = ["machining suspended"] };
        var lines = SqlMakinoReader.FixtureLines(
        [
            (Parked(1), ""), (Parked(2), ""), (Parked(3), ""),
            (new FixtureDetail(4, "x") { States = ["lacking tools"] }, ""),
            (Parked(5), "FIXTURE-E"), (Parked(6), ""),
        ]);
        Assert.Equal(
            ["fixtures 1-3: machining suspended", "fixture 4: lacking tools", "fixture 5 FIXTURE-E: machining suspended", "fixture 6: machining suspended"],
            lines);
    }

    // --- pallets SQL (PA-1, PA-2, PA-5, PA-6) ---

    [Fact]
    public void Pallets_sql_reads_priority_type_fixture_work_labels_and_feasible_devices_for_the_picked_pallets()
    {
        var sql = SqlMakinoReader.PalletsSql(SchemaWithout());
        Assert.Contains("p.Priority AS Priority", sql);
        Assert.Contains("p.OperatorPriority AS OperatorPriority", sql);
        Assert.Contains("LEFT JOIN FixtureRun fr ON fr.PalletFixtureID = pf.PalletFixtureID", sql);
        Assert.Contains("LEFT JOIN Jobs j ON j.JobID = fr.CurJobID", sql);
        Assert.Contains("JOIN MachineJobs mj2", sql);
        Assert.Contains("ISNULL(pf.LastOneShotOpeCallProcessID, 0) <> fr.CurProcessID", sql);
        Assert.Contains("FROM FixtureProcesses fp", sql);
        Assert.Contains("FROM PalletFeasibleDevice d", sql);
        Assert.Contains("SELECT TOP (@take) p.PalletID FROM Pallets p ORDER BY", sql);
        Assert.DoesNotContain("@pallet", sql);

        var one = SqlMakinoReader.PalletsSql(SchemaWithout(), onePallet: true, oneShotFirstPallet: true);
        Assert.Contains("WHERE p.PalletNumber = @pallet", one);
        Assert.DoesNotContain("LastOneShotOpeCallProcessID <>", one); // the first pallet's call always counts
    }

    [Fact]
    public void Pallets_sql_without_the_run_or_optional_tables_selects_nulls_and_leaves_their_result_sets_out()
    {
        var sql = SqlMakinoReader.PalletsSql(SchemaWithout("FixtureRun", "FixtureProcesses", "PalletFeasibleDevice", "Pallets.Priority"));
        Assert.DoesNotContain("JOIN FixtureRun", sql);
        Assert.DoesNotContain("fr.", sql);
        Assert.Contains("CAST(NULL AS int) AS CurJobID", sql);
        Assert.Contains("CAST(NULL AS varchar(4000)) AS OrderName", sql);
        Assert.Contains("0 AS JobCallAhead", sql);
        Assert.DoesNotContain("FixtureProcesses", sql);
        Assert.DoesNotContain("PalletFeasibleDevice", sql);
        Assert.Contains("CAST(NULL AS int) AS Priority", sql);

        Assert.Equal(new SqlMakinoReader.PalletParts(false, false, false),
            SqlMakinoReader.PalletParts.Of(SchemaWithout("FixtureRun", "FixtureProcesses", "PalletFeasibleDevice")));
    }

    // --- OR-2, OR-7 ---

    private static OrderInfo Order(int? remaining = 5, int? inProcess = 0) =>
        new("ORDER-001", "CONTOSO-BRACKET", "active", 10, null, null, null) { RemainingQty = remaining, InProcessQty = inProcess };

    private static readonly DateTime Now = new(2026, 11, 6, 10, 0, 0);

    [Fact]
    public void The_dashboard_word_shows_only_when_it_differs_and_is_derived_the_console_way_without_status2()
    {
        var running = SqlMakinoReader.OrderNotes(Order(), new(2, 3, null, null, null), Now, false);
        Assert.Equal("running", running.DashboardStatus);
        Assert.Null(running.WaitingFor);

        var same = SqlMakinoReader.OrderNotes(Order(), new(2, 2, null, null, null), Now, false);
        Assert.Null(same.DashboardStatus);
        Assert.StartsWith("has material, but no fixture has taken it yet", same.WaitingFor);

        var material = SqlMakinoReader.OrderNotes(Order(remaining: 0), new(2, null, null, null, null), Now, false);
        Assert.Equal("waiting for material", material.DashboardStatus);
        Assert.Equal("waiting for material input", material.WaitingFor);

        Assert.Equal("running", SqlMakinoReader.OrderNotes(Order(inProcess: 2), new(2, null, null, null, null), Now, false).DashboardStatus);
    }

    [Fact]
    public void Start_shows_only_when_still_to_come_or_inactive_and_comparing_dates()
    {
        Assert.Null(SqlMakinoReader.OrderNotes(Order(), new(2, 2, Now.Date.AddHours(23), null, null), Now, false).Start);
        var later = SqlMakinoReader.OrderNotes(Order(), new(1, 1, Now.Date.AddDays(2), null, null), Now, false);
        Assert.Equal(Now.Date.AddDays(2), later.Start);
        Assert.Equal("starts 2026-11-08", later.WaitingFor);
        // Inactive with a start already past: the cell activates it at its next check; no "starts" note.
        var due = SqlMakinoReader.OrderNotes(Order(), new(1, 1, Now.Date.AddDays(-1), null, null), Now, false);
        Assert.Equal(Now.Date.AddDays(-1), due.Start);
        Assert.Null(due.WaitingFor);
        Assert.Null(SqlMakinoReader.OrderNotes(Order(), new(2, 2, new DateTime(1900, 1, 1), null, null), Now, false).Start);
    }

    [Fact]
    public void A_suspended_order_untouched_since_creation_is_hedged_only_when_new_orders_start_suspended()
    {
        var created = Now.AddDays(-3);
        var facts = new SqlMakinoReader.OrderFacts(4, 4, null, created, created.AddSeconds(5));
        Assert.Contains("possibly since it was created", SqlMakinoReader.OrderNotes(Order(), facts, Now, newOrdersSuspended: true).WaitingFor);
        Assert.Equal("suspended by an operator (release it from the console's order menu)",
            SqlMakinoReader.OrderNotes(Order(), facts, Now, newOrdersSuspended: false).WaitingFor);
        Assert.DoesNotContain("possibly", SqlMakinoReader.OrderNotes(Order(), facts with { Updated = created.AddHours(2) }, Now, true).WaitingFor);
        Assert.Equal("no required quantity", SqlMakinoReader.OrderNotes(Order(), new(7, 7, null, null, null), Now, false).WaitingFor);
    }

    [Fact]
    public void Orders_sql_reads_the_dashboard_status_start_priority_and_quick_data()
    {
        var sql = SqlMakinoReader.OrdersSql(SchemaWithout(), activeOnly: true);
        Assert.Contains("o.Status2 AS Status2", sql);
        Assert.Contains("o.StartDate AS StartDate", sql);
        Assert.Contains("o.Priority AS Priority", sql);
        Assert.Contains("pa.QuickEntry AS QuickEntry", sql);
        var old = SqlMakinoReader.OrdersSql(SchemaWithout("Orders.Status2", "Parts.QuickEntry"), activeOnly: true);
        Assert.Contains("CAST(NULL AS int) AS Status2", old);
        Assert.Contains("CAST(NULL AS int) AS QuickEntry", old);
    }

    // --- OR-4 ---

    [Fact]
    public void Auto_delete_says_what_the_cell_deletes_and_when()
    {
        Assert.Null(SqlMakinoReader.AutoDeleteWords(null));
        Assert.StartsWith("The cell deletes no orders automatically.", SqlMakinoReader.AutoDeleteWords(new Dictionary<string, string>()));
        var words = SqlMakinoReader.AutoDeleteWords(new Dictionary<string, string>
        {
            ["AutoOrderDelete"] = "48", ["AutoOrderDeleteForCancel"] = "1", ["AutoDeleteWaitMaterial"] = "24", ["ComprehensiveDelete"] = "1",
        })!;
        Assert.Contains("complete orders 48 h after they complete", words);
        Assert.Contains("cancelled orders 48 h after their due date", words);
        Assert.Contains("24 h after their start date (cancelled, then deleted)", words);
        Assert.Contains("never for quick-data parts", words);
        Assert.Contains("also deletes its part data", words);
        Assert.Contains("includeArchive", words);
    }

    // --- OR-5 ---

    [Fact]
    public void The_archive_is_windowed_capped_and_merges_a_completed_then_deleted_order()
    {
        var sql = SqlMakinoReader.ArchiveSql(SchemaWithout())!;
        Assert.Contains("SELECT TOP (@take)", sql);
        Assert.Contains("WHERE r.ResultDate >= @from", sql);
        Assert.Contains("GROUP BY r.OrderName, r.PartName, r.Revision, r.RegistrationDate", sql);
        Assert.Contains("MAX(CAST(r.IsDelete AS int)) AS IsDelete", sql);
        Assert.Contains("ORDER BY MAX(r.ResultDate) DESC", sql);
        Assert.Null(SqlMakinoReader.ArchiveSql(SchemaWithout("OrderResults")));
        Assert.Null(SqlMakinoReader.ArchiveSql(SchemaWithout("OrderResults.ResultDate")));
    }

    // --- PR-2, PR-4 ---

    [Fact]
    public void Production_reads_parts_at_an_external_process_from_the_table_only()
    {
        Assert.Contains("SUM(q.OutProcessQuantity)", SqlMakinoReader.ProductionSql(SchemaWithout(), false, false, false));
        var view = SqlMakinoReader.ProductionSql(SchemaWithout("Quantities"), false, false, false);
        Assert.Contains("CAST(NULL AS int) AS OutProcessQty", view);
    }

    [Fact]
    public void Cycle_minutes_are_only_read_when_asked_and_matched_by_name_part_and_revision_since_registration()
    {
        Assert.DoesNotContain("MachiningMinutes", SqlMakinoReader.ProductionSql(SchemaWithout(), false, false, false));
        var sql = SqlMakinoReader.ProductionSql(SchemaWithout(), false, false, false, includeCycleTime: true);
        Assert.Contains("FROM MachineResults x WHERE x.OrderName = o.OrderName AND x.PartName = pa.PartName", sql);
        Assert.Contains("FROM WorkSetResults x WHERE x.ClampOrderName = o.OrderName", sql);
        Assert.Contains("x.StartDateTime >= DATEADD(day, -1, CAST(o.RegistrationDate AS date))", sql);
        Assert.Contains("CAST(NULL AS int) AS WorkSetMinutes",
            SqlMakinoReader.ProductionSql(SchemaWithout("WorkSetResults.ClampRevision"), false, false, false, includeCycleTime: true));
    }

    // --- JSON ---

    private static ToolContext Context()
    {
        var s = new McpSettings(new MakinoLogin("MAKINO-CELL-PC", "Makino", false, "your-login", "not-a-real-password"), [], new Limits());
        return new ToolContext(new SettingsLoad(s, null), new FakeMakinoReader(), new SharedFolders(s.Folders, s.Limits, []));
    }

    [Fact]
    public async Task Pallets_list_fixtures_as_lines_by_default_and_as_objects_for_one_pallet()
    {
        var context = Context();
        var all = JsonDocument.Parse(await CellTools.GetPallets(context, ct: CancellationToken.None)).RootElement.GetProperty("pallets")[0];
        Assert.Equal("fixture 1: machining suspended", all.GetProperty("fixtures")[0].GetString());
        Assert.False(all.TryGetProperty("fixtureDetails", out _));
        Assert.False(all.TryGetProperty("priority", out _));

        var one = JsonDocument.Parse(await CellTools.GetPallets(context, pallet: 7, ct: CancellationToken.None)).RootElement.GetProperty("pallets")[0];
        Assert.False(one.TryGetProperty("fixtures", out _));
        var fixture = one.GetProperty("fixtureDetails")[0];
        Assert.Equal("FIXTURE-A", fixture.GetProperty("label").GetString());
        Assert.Equal("machining suspended", fixture.GetProperty("states")[0].GetString());
        Assert.False(fixture.TryGetProperty("operatorCall", out _));
    }
}

// Reads a real cell (skipped unless MAKINOMCP_LIVE_SETTINGS is set). Prints what it reads; asserts only shape: nothing
// here may name a device, count or value from a real cell.
public class OrdersLiveTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static SqlMakinoReader Reader()
    {
        var path = Environment.GetEnvironmentVariable("MAKINOMCP_LIVE_SETTINGS");
        Skip.If(string.IsNullOrWhiteSpace(path), "MAKINOMCP_LIVE_SETTINGS is not set");
        var load = SettingsStore.Load(path!);
        Assert.True(load.Settings is not null, load.Problem);
        return new SqlMakinoReader(load.Settings!.Makino, load.Settings.Limits);
    }

    private static string Json(object value) => JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    private static string Tally(IEnumerable<string?> values) =>
        string.Join(" | ", values.OfType<string>().GroupBy(x => x).Select(g => $"{g.Key} x{g.Count()}"));

    [SkippableFact]
    public async Task Live_pallets_orders_and_production_read_the_new_fields()
    {
        var reader = Reader();
        var ct = CancellationToken.None;

        var pallets = await reader.GetPalletsAsync(null, false, ct);
        var lines = pallets.Pallets.SelectMany(p => p.Fixtures ?? []).ToList();
        output.WriteLine($"get_pallets default: {pallets.Pallets.Count} pallets, {lines.Count} fixture lines, {Json(pallets).Length} chars JSON; " +
                         $"notAvailable: {string.Join("; ", pallets.NotAvailable ?? [])}");
        output.WriteLine($"  types: {Tally(pallets.Pallets.Select(p => p.Type))}; priority set {pallets.Pallets.Count(p => p.Priority is not null)}, " +
                         $"operator priority {pallets.Pallets.Count(p => p.OperatorPriority is not null)}");
        output.WriteLine($"  notFeasibleFor on {pallets.Pallets.Count(p => p.NotFeasibleFor is not null)} pallets; list sizes " +
                         Tally(pallets.Pallets.Select(p => p.NotFeasibleFor?.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        output.WriteLine($"  locations: {Tally(pallets.Pallets.Select(p => p.Location))}");
        output.WriteLine($"  states: {Tally(lines.SelectMany(l => l.Contains(": ", StringComparison.Ordinal) ? l[(l.IndexOf(": ", StringComparison.Ordinal) + 2)..].Split(", ") : []))}");
        foreach (var line in lines.Take(8)) output.WriteLine($"  e.g. {line}");

        Assert.All(pallets.Pallets, p => Assert.NotNull(p.Fixtures));
        Assert.All(pallets.Pallets, p => Assert.Null(p.FixtureDetails));
        Assert.All(pallets.Pallets, p => Assert.True(p.Priority is null or not 0 && p.OperatorPriority is null or not 0));
        Assert.All(pallets.Pallets, p => Assert.True(p.NotFeasibleFor is null || p.NotFeasibleFor.Count > 0));
        Assert.All(lines, l => Assert.False(string.IsNullOrWhiteSpace(l)));

        var busy = pallets.Pallets.FirstOrDefault(p => p.Fixtures!.Any(f => !f.Contains("empty", StringComparison.Ordinal)))
                   ?? pallets.Pallets.FirstOrDefault();
        if (busy is not null)
        {
            var one = await reader.GetPalletsAsync(busy.Number, false, ct);
            Assert.Single(one.Pallets);
            Assert.Null(one.Pallets[0].Fixtures);
            Assert.NotNull(one.Pallets[0].FixtureDetails);
            output.WriteLine($"one pallet ({one.Pallets[0].Status}, {one.Pallets[0].Location}): {Json(one.Pallets[0].FixtureDetails!)}");
        }
        var detailed = await reader.GetPalletsAsync(null, true, ct);
        output.WriteLine($"get_pallets includeFixtures: {Json(detailed).Length} chars JSON");
        await Assert.ThrowsAsync<ArgumentException>(() => reader.GetPalletsAsync(-12345, false, ct));

        var orders = await reader.GetOrdersAsync(false, true, 30, ct);
        output.WriteLine($"get_orders (all + archive): {orders.Orders.Count} orders, {Json(orders).Length} chars; dashboardStatusFrom: {orders.DashboardStatusFrom}");
        output.WriteLine($"  autoDelete: {orders.AutoDelete}");
        output.WriteLine($"  dashboardStatus: {Tally(orders.Orders.Select(o => o.DashboardStatus))}");
        output.WriteLine($"  waitingFor: {Tally(orders.Orders.Select(o => o.WaitingFor))}");
        output.WriteLine($"  start set {orders.Orders.Count(o => o.Start is not null)}, priority set {orders.Orders.Count(o => o.Priority is not null)}, " +
                         $"comment {orders.Orders.Count(o => o.Comment is not null)}, quickData {orders.Orders.Count(o => o.QuickData == true)}");
        output.WriteLine($"  archive: {orders.Archive?.Count} rows (capped {orders.ArchiveCapped}), deleted {orders.Archive?.Count(a => a.Deleted == true)}; " +
                         $"notAvailable: {string.Join("; ", orders.NotAvailable ?? [])}");
        var active = await reader.GetOrdersAsync(true, false, 30, ct);
        output.WriteLine($"get_orders default: {active.Orders.Count} orders, {Json(active).Length} chars");
        Assert.Null(active.AutoDelete);
        Assert.Null(active.Archive);
        Assert.True(orders.Archive is not null || (orders.NotAvailable ?? []).Contains(SqlMakinoReader.ArchiveNotAvailable));
        Assert.All(orders.Orders, o => Assert.True(o.DashboardStatus is null || o.DashboardStatus != o.Status));
        Assert.All(orders.Orders, o => Assert.True(o.Priority is null or not 0));

        var production = await reader.GetProductionAsync(7, null, null, false, true, ct);
        output.WriteLine($"get_production (cycle time): {production.Orders.Count} orders, {Json(production).Length} chars; " +
                         $"lastOutProc {production.CompletedIncludesLastOutProcess}, probePartCount {production.ProbePartCount}, " +
                         $"outProcess set {production.Orders.Count(o => o.OutProcessQty is not null)}; notAvailable: {string.Join("; ", production.NotAvailable ?? [])}");
        foreach (var o in production.Orders.Take(5))
            output.WriteLine($"  order: machining {o.MachiningMinutes} min, work-set {o.WorkSetMinutes} min, per part {o.MachiningMinutesPerPart}");
        var plain = await reader.GetProductionAsync(7, null, null, false, false, ct);
        output.WriteLine($"get_production default: {Json(plain).Length} chars");
        Assert.Equal(SqlMakinoReader.CycleTimeNote, production.CycleTimeNote);
        Assert.Null(plain.CycleTimeNote);
        Assert.All(plain.Orders, o => Assert.Null(o.MachiningMinutes));
        Assert.All(production.Orders, o => Assert.True(o.MachiningMinutesPerPart is null or > 0));
    }
}
