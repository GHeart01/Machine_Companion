using Xunit;
using Xunit.Abstractions;
using MakinoMCP.Core.Cell;
using MakinoMCP.Core.Settings;

namespace MakinoMCP.Tests;

// Reads a real cell. Skipped unless MAKINOMCP_LIVE_SETTINGS points at a settings file; run locally only.
// Assertions are structural: nothing here may name a device, count or value from a real cell.
public class LiveReaderTests(ITestOutputHelper output)
{
    private const string EnvVar = "MAKINOMCP_LIVE_SETTINGS";

    /// <summary>The cell settings the 2026-11 corrections depend on, as each tool reports them: printed (local runs
    /// only) so a developer learns the cell's values; asserted only for shape.</summary>
    [SkippableFact]
    public async Task Live_settings_behind_the_corrections_are_reported()
    {
        var reader = Reader();
        var overview = await reader.GetOverviewAsync(CancellationToken.None);
        var orders = await reader.GetOrdersAsync(activeOnly: true, false, 30, CancellationToken.None);
        var tools = await reader.GetToolsAsync(null, "all", null, CancellationToken.None);
        var attention = await reader.GetAttentionAsync(includeParked: false, CancellationToken.None);

        output.WriteLine($"historyDays {overview.HistoryDays}, assumed {overview.HistoryDaysAssumed}");
        output.WriteLine($"completeIncludesScrap {orders.CompleteIncludesScrap}; orders with machiningSuspended " +
                         $"{orders.Orders.Count(o => o.MachiningSuspended == true)}");
        output.WriteLine($"diameterIsRadius {tools.DiameterIsRadius}, assumed {tools.DiameterTypeAssumed}");
        output.WriteLine($"lackingToolScope {attention.LackingToolScope}; not computed " +
                         $"{attention.NotAvailable.Contains(SqlMakinoReader.LackingToolsNotComputed)}");

        output.WriteLine($"duty {overview.Duty}, schedule {overview.DutySchedule}, powerOff {overview.PowerOff?.Status}");
        output.WriteLine("recoveryPolicy " + string.Join("; ", (overview.RecoveryPolicy ?? new Dictionary<string, string>())
            .Select(p => $"{p.Key}: {p.Value}")));
        foreach (var d in overview.Devices.Where(d => d.Connected is not null))
        {
            output.WriteLine($"{d.Type}: open {d.Open}, instruction {d.Instruction}, since {d.Since}, planned {d.PlannedFinish}, " +
                             $"program {d.Program?.Name} ({d.Program?.MainIs}), disabled {string.Join("/", d.DisabledOperations ?? [])}");
        }

        Assert.True(overview.HistoryDays is null or > 0); // null: this login can't read the settings
        Assert.All(overview.Devices, d => Assert.True(d.Open is null or false));
        Assert.All(overview.Devices, d => Assert.True(d.PlannedFinish is null || d.PlannedFinish > d.Since));
        Assert.All(overview.Devices.Where(d => d.Instruction is not null), d => Assert.Equal("busy", d.Status));
        Assert.All(orders.Orders, o => Assert.True(o.MachiningSuspended is null or true));
        // The cell works out lacking tools (and says for which jobs), or notAvailable says it doesn't, or that its
        // settings can't be read; never two of these.
        Assert.Equal(1, new[]
        {
            attention.LackingToolScope is not null,
            attention.NotAvailable.Contains(SqlMakinoReader.LackingToolsNotComputed),
            attention.NotAvailable.Contains(SqlMakinoReader.LackingToolsSettingUnknown),
        }.Count(b => b));
        // Disconnected devices show no mode or emergency stop.
        Assert.All(overview.Devices.Where(d => d.Connected == false), d => Assert.True(d.Mode is null && d.Emergency is null));
    }

    private static McpSettings LiveSettings()
    {
        var path = Environment.GetEnvironmentVariable(EnvVar);
        Skip.If(string.IsNullOrWhiteSpace(path), $"{EnvVar} is not set");
        var load = SettingsStore.Load(path!);
        Assert.True(load.Settings is not null, load.Problem);
        return load.Settings!;
    }

    private static SqlMakinoReader Reader(int maxRows = 500)
    {
        var s = LiveSettings();
        return new SqlMakinoReader(s.Makino, new Limits(maxRows, s.Limits.MaxFileKB));
    }

    [SkippableFact]
    public async Task Live_overview_lists_devices()
    {
        var o = await Reader().GetOverviewAsync(CancellationToken.None);

        Assert.NotEmpty(o.Devices);
        Assert.All(o.Devices, d => Assert.False(string.IsNullOrWhiteSpace(d.Name)));
        Assert.Contains(o.Devices, d => d.Type == "machine" && d.Connected is not null);
        Assert.All(o.Devices, d => Assert.True(d.OpenAlarms >= 0 && d.OpenWarnings >= 0));
        Assert.True(o.ServerTime > DateTime.Now.AddDays(-2) && o.ServerTime < DateTime.Now.AddDays(2));
    }

    [SkippableFact]
    public async Task Live_overview_has_dashboard_state()
    {
        var o = await Reader().GetOverviewAsync(CancellationToken.None);

        Assert.True(o.HistoryDays is null or > 0);
        var machines = o.Devices.Where(d => d.Type == "machine").ToList();
        Assert.NotEmpty(machines);
        Assert.All(machines, m => Assert.True(m.Status is not null || m.NotAvailable.Contains("status")));
        Assert.All(machines, m => Assert.True(m.ToolStatus is not null || m.NotAvailable.Contains("tool status")));
        Assert.All(o.Devices.SelectMany(d => d.Pallets), p => Assert.False(string.IsNullOrWhiteSpace(p.Position)));
        // A vehicle is read from its status table or the vendor view, or says it can't be.
        Assert.All(o.Devices.Where(d => d.Type == "vehicle"),
            v => Assert.True(v.Status is not null || v.NotAvailable.Contains("vehicle status")));

        var pallets = await Reader().GetPalletsAsync(null, false, CancellationToken.None);
        Assert.All(pallets.Pallets, p => Assert.False(string.IsNullOrWhiteSpace(p.Status)));
    }

    [SkippableFact]
    public async Task Live_alarms_last_24h_capped()
    {
        var all = await Reader().GetAlarmsAsync(24, null, false, true, CancellationToken.None);
        Skip.If(all.Alarms.Count < 2, "fewer than two alarms in the last 24 hours");
        Skip.If(all.Capped, "more alarms than the default cap; the flip check needs an uncapped baseline");

        var n = all.Alarms.Count;
        var capped = await Reader(n - 1).GetAlarmsAsync(24, null, false, true, CancellationToken.None);
        Assert.True(capped.Capped);
        Assert.Equal(n - 1, capped.Alarms.Count);

        var exact = await Reader(n).GetAlarmsAsync(24, null, false, true, CancellationToken.None);
        Assert.False(exact.Capped);
        Assert.Equal(n, exact.Alarms.Count);

        Assert.All(all.Alarms, a => Assert.True(a.Raised >= all.ServerTime.AddHours(-24).AddMinutes(-1)));
        Assert.All(all.Alarms, a => Assert.True(a.Minutes is null or >= 0));
        Assert.Equal(all.Alarms.OrderByDescending(a => a.Raised).Select(a => a.Raised), all.Alarms.Select(a => a.Raised));
    }

    [SkippableFact]
    public async Task Live_activity_returns_newest_first()
    {
        var list = await Reader().GetRecentActivityAsync(24, null, CancellationToken.None);
        Skip.If(list.Items.Count == 0, "no activity in the last 24 hours");

        Assert.Equal(list.Items.OrderByDescending(i => i.Start).Select(i => i.Start), list.Items.Select(i => i.Start));
        Assert.All(list.Items, i => Assert.Contains(i.Kind, new[] { "machining", "work-set", "transport" }));
    }

    [SkippableFact]
    public async Task Live_every_read_runs()
    {
        var r = Reader(50);
        var ct = CancellationToken.None;
        var overview = await r.GetOverviewAsync(ct);
        var device = overview.Devices[0].Name;

        Assert.NotNull(await r.GetAlarmsAsync(24 * 7, device, true, false, ct));
        Assert.NotNull(await r.GetAlarmSummaryAsync(7, null, null, ct));
        var util = await r.GetUtilizationAsync(7, null, null, ct);
        Assert.All(util.Days, d => Assert.InRange(d.BusyPercent, 0, 100));
        Assert.NotNull(await r.GetRecentActivityAsync(24, device, ct));
        Assert.NotNull(await r.GetOrdersAsync(true, false, 30, ct));
        var pallets = await r.GetPalletsAsync(null, false, ct);
        Assert.All(pallets.Pallets, p => Assert.False(string.IsNullOrWhiteSpace(p.Location)));
        var rights = await r.GetLoginRightsAsync(ct);
        Assert.Equal(overview.Devices.Count, rights.DeviceCount);
        Assert.False(string.IsNullOrWhiteSpace(rights.Summary));

        var e = await Assert.ThrowsAsync<ArgumentException>(() => r.GetAlarmsAsync(24, "no-such-device-xyz", false, true, ct));
        Assert.Contains(device, e.Message);
    }

    [SkippableFact]
    public async Task Live_tools_summary_filters_and_cap()
    {
        var ct = CancellationToken.None;
        var problems = await Reader().GetToolsAsync(null, null, null, ct);

        Assert.Equal("problems", problems.Filter);
        Assert.NotEmpty(problems.Summary);
        Assert.All(problems.Summary, d => Assert.Equal(d.Tools, d.ByStatus.Values.Sum()));
        Assert.True(problems.Summary.Sum(d => d.Tools) >= problems.Tools.Count);
        var lives = problems.Tools.SelectMany(t => t.Cutters).Select(c => c.Life).OfType<LifeInfo>().ToList();
        Assert.All(lives, l => Assert.True(l.Note is null || l.UsedPercent is null)); // not life-managed: never a %
        Assert.All(lives, l => Assert.True(l.UsedPercent is null or >= 0));
        Assert.All(problems.Tools, t => Assert.False(string.IsNullOrWhiteSpace(t.Location)));

        var all = await Reader().GetToolsAsync(null, "all", null, ct);
        Skip.If(all.Tools.Count < 4, "fewer than four tools on this cell");
        Assert.True(all.Tools.Count >= problems.Tools.Count || all.Capped);
        var capped = await Reader(3).GetToolsAsync(null, "all", null, ct);
        Assert.True(capped.Capped);
        Assert.Equal(3, capped.Tools.Count);
        Assert.All(capped.Tools, t => Assert.NotEmpty(t.Cutters)); // the cutters of exactly the tools shown

        var one = all.Tools[0];
        var byNumber = await Reader().GetToolsAsync(null, null, one.Itn, ct);
        Assert.Equal("all", byNumber.Filter);
        Assert.Contains(byNumber.Tools, t => t.Itn == one.Itn);

        var ftns = all.Tools.Select(t => t.Ftn).OfType<int>().Distinct().Take(20).ToList();
        var usable = await Reader().GetUsableToolCountsAsync(ftns, ct);
        Assert.NotNull(usable);
        Assert.All(usable, u => Assert.Contains(u.Key.Ftn, ftns));
        Assert.All(usable.Values, n => Assert.True(n > 0));

        // The summary counts the same derived status the list shows: every tool, uncapped, matches it per device.
        var everything = await Reader(100000).GetToolsAsync(null, "all", null, ct);
        Assert.False(everything.Capped);
        foreach (var d in everything.Summary)
        {
            var mine = everything.Tools.Where(t => t.Location == d.Device || t.Location.StartsWith(d.Device + " ", StringComparison.Ordinal));
            foreach (var (status, n) in d.ByStatus) Assert.Equal(n, mine.Count(t => t.Status == status));
        }
        // An alarm bit always reads as alarm, whatever the stored status says.
        Assert.All(everything.Tools.Where(t => (t.Flags?.Contains("alarm tool") ?? false)
                                               || t.Cutters.Any(c => c.Alarms?.Any(a => a != "tool life warning") ?? false)),
            t => Assert.Equal("alarm", t.Status));

        foreach (var filter in new[] { "near_end", "alarm", "absent" })
            Assert.Equal(filter, (await Reader().GetToolsAsync(null, filter, null, ct)).Filter);
    }

    [SkippableFact]
    public async Task Live_tool_history_reads_stay_inside_their_window()
    {
        var ct = CancellationToken.None;
        var r = Reader();

        var changes = await r.GetToolChangesAsync(7, null, null, false, ct);
        Assert.True(changes.LifeUpdatesLeftOut);
        Assert.All(changes.Changes, c => Assert.True(c.Time >= changes.ServerTime.AddDays(-7).AddMinutes(-1)));
        Assert.All(changes.Changes, c => Assert.NotEmpty(c.Changed)); // routine life ticks left out
        Assert.Equal(changes.Changes.OrderByDescending(c => c.Time).Select(c => c.Time), changes.Changes.Select(c => c.Time));
        var withTicks = await Reader(5).GetToolChangesAsync(7, null, null, true, ct);
        Assert.False(withTicks.LifeUpdatesLeftOut);
        Assert.Null(withTicks.LifeWarningTransitions);

        // Routine wear (a tool stepping to life warning and nothing else) is counted, not listed. The count matches the
        // rows includeLifeUpdates lists that are only that step (two calls a moment apart, so a row may slip in or out).
        static bool OnlyWarning(ToolChange c) =>
            c.Changed.All(w => (w.StartsWith("status ", StringComparison.Ordinal) && w.EndsWith("→ life warning", StringComparison.Ordinal))
                               || w == "tool life warning on");
        Assert.NotNull(changes.LifeWarningTransitions);
        Assert.DoesNotContain(changes.Changes, OnlyWarning);
        var day = await Reader(20000).GetToolChangesAsync(1, null, null, false, ct);
        var dayAll = await Reader(20000).GetToolChangesAsync(1, null, null, true, ct);
        if (!dayAll.Capped) Assert.InRange(day.LifeWarningTransitions ?? -1, dayAll.Changes.Count(OnlyWarning) - 1, dayAll.Changes.Count(OnlyWarning) + 1);

        var alarms = await r.GetToolAlarmsAsync(7, null, null, ct);
        Assert.False(string.IsNullOrWhiteSpace(alarms.Source));
        Assert.All(alarms.Alarms, a => Assert.NotEmpty(a.Alarms));
        Assert.All(alarms.Alarms, a => Assert.True(a.Time >= alarms.ServerTime.AddDays(-7).AddMinutes(-1)));

        var moves = await r.GetToolMovementsAsync(30, null, null, ct);
        Assert.All(moves.Movements, m => Assert.Contains(m.Action, new[] { "in", "out" }));
        Assert.All(moves.Movements, m => Assert.True(m.Time >= moves.ServerTime.AddDays(-30).AddMinutes(-1)));
    }

    [SkippableFact]
    public async Task Live_part_routing_finds_candidates_then_one_part()
    {
        var ct = CancellationToken.None;
        PartRouting? several = null;
        foreach (var text in new[] { "-", "0", "1", "A", "E" })
        {
            several = await Reader().GetPartRoutingAsync(text, null, false, ct);
            if (several.Candidates is { Count: > 1 }) break;
        }
        Skip.If(several?.Candidates is not { Count: > 1 }, "no text matched several parts");
        Assert.Null(several!.Part); // several matches: candidates only, never a silent pick
        Assert.All(several.Candidates!, c => Assert.True(c.Processes >= 0));

        var pick = several.Candidates![0];
        var one = await Reader().GetPartRoutingAsync(pick.Name, pick.Revision, true, ct);
        Assert.NotNull(one.Part);
        Assert.Equal(pick.Name, one.Part.Name, ignoreCase: true);
        Assert.Equal(one.Part.Processes.Select(p => p.Number).Order(), one.Part.Processes.Select(p => p.Number));
        Assert.All(one.Part.Processes, p => Assert.Equal(p.Jobs.Select(j => j.Number).Order(), p.Jobs.Select(j => j.Number)));
        Assert.StartsWith("approximate", one.ToolCheck);
        foreach (var tool in one.Part.Processes.SelectMany(p => p.Jobs).SelectMany(j => j.Tools ?? []))
        {
            Assert.True(tool.Needed >= 1);
            if (tool.Usable is null) continue;
            Assert.All(tool.MissingOn ?? [], m => Assert.True(tool.Usable[m] < tool.Needed));
            Assert.All(tool.Usable.Where(u => u.Value < tool.Needed), u => Assert.Contains(u.Key, tool.MissingOn ?? []));
        }

        var none = await Reader().GetPartRoutingAsync("no-such-part-xyz-0000", null, true, ct);
        Assert.Null(none.Part);
        Assert.Empty(none.Candidates!);
        await Assert.ThrowsAsync<ArgumentException>(() => Reader().GetPartRoutingAsync(" ", null, true, ct));
    }

    [SkippableFact]
    public async Task Live_production_agrees_with_orders_and_stays_in_its_window()
    {
        var ct = CancellationToken.None;
        var production = await Reader(100000).GetProductionAsync(7, null, null, true, false, ct);
        var orders = await Reader(100000).GetOrdersAsync(true, false, 30, ct);

        Assert.Equal(orders.Orders.Count, production.Orders.Count); // includeIdle: the same active orders
        Assert.Null(production.OtherActiveOrders);
        var busy = await Reader(100000).GetProductionAsync(7, null, null, false, false, ct);
        Assert.Equal(production.Orders.Count, busy.Orders.Count + (busy.OtherActiveOrders ?? 0));
        Assert.All(busy.Orders, o => Assert.Contains(production.Orders, p => p.Name == o.Name));
        var byName = orders.Orders.GroupBy(o => o.Name).ToDictionary(g => g.Key, g => g.First());
        foreach (var o in production.Orders)
        {
            var other = byName[o.Name];
            Assert.Equal(other.CompletedQty, o.CompletedQty);
            Assert.Equal(other.ScrapQty, o.ScrapQty);
            Assert.Equal(other.RemainingQty, o.RemainingQty);
            Assert.True(o.InProcessQty is null || o.ClampedQty is null || o.InProcessQty >= o.ClampedQty);
            Assert.Null(o.Daily); // per-order days only when an order or part is named
        }

        // Default: one total per day across the listed orders, inside the window, newest first.
        var totals = busy.Daily ?? [];
        var from = DateOnly.FromDateTime(production.ServerTime).AddDays(-6);
        Assert.True(totals.Count <= 7);
        Assert.All(totals, d => Assert.True(d.Date >= from));
        Assert.Equal(totals.OrderByDescending(d => d.Date).Select(d => d.Date), totals.Select(d => d.Date));
        if (!busy.Capped && busy.DailyCapped != true)
            Assert.Equal(busy.Orders.Sum(o => o.CompletedInWindow ?? 0), totals.Sum(d => d.Completed));

        Skip.If(production.Orders.Count < 2, "fewer than two active orders");
        var named = await Reader().GetProductionAsync(30, production.Orders[0].Name, null, false, false, ct);
        Assert.Contains(named.Orders, o => o.Name == production.Orders[0].Name);
        Assert.Null(named.Daily);
        foreach (var o in named.Orders)
        {
            var daily = o.Daily ?? [];
            Assert.All(daily, d => Assert.True(d.Date >= DateOnly.FromDateTime(named.ServerTime).AddDays(-29)));
            if (named.DailyCapped != true) Assert.Equal(o.CompletedInWindow ?? 0, daily.Sum(d => d.Completed));
        }
        var capped = await Reader(1).GetProductionAsync(7, null, null, true, false, ct);
        Assert.True(capped.Capped);
        Assert.Single(capped.Orders);
    }

    /// <summary>Diagnoses a waiting pallet and a machining one on the real cell; prints the verdicts (local runs only),
    /// asserts only their shape.</summary>
    [SkippableFact]
    public async Task Live_pallet_diagnosis_gives_a_verdict_per_fixture()
    {
        var reader = Reader();
        var pallets = (await reader.GetPalletsAsync(null, false, CancellationToken.None)).Pallets;
        var picks = new[] { "waiting for machining", "machining", "idle" }
            .Select(status => pallets.FirstOrDefault(p => p.Status == status)).OfType<PalletInfo>().ToList();
        Skip.If(picks.Count == 0, "no pallet waiting, machining or idle");

        foreach (var p in picks)
        {
            var d = await reader.GetPalletDiagnosisAsync(p.Number, CancellationToken.None);
            output.WriteLine($"pallet ({d.Status}, {d.WorkStatus}, {d.Location}):");
            foreach (var f in d.Fixtures)
                output.WriteLine($"  fixture {f.Fixture}: {f.Job} [{f.JobStatus}] -> {f.Verdict}; " +
                                 string.Join("; ", (f.Devices ?? []).Select(v => $"{v.Device}: {v.Verdict}")));
            output.WriteLine("  not checked: " + string.Join(" | ", d.NotChecked));
            if (d.NotAvailable is { } na) output.WriteLine("  not available: " + string.Join(" | ", na));

            Assert.Equal(p.Number, d.Pallet);
            Assert.All(d.Fixtures, f => Assert.False(string.IsNullOrWhiteSpace(f.Verdict)));
            Assert.All(d.Fixtures, f => Assert.DoesNotContain("code ", f.Verdict, StringComparison.Ordinal));
            Assert.NotEmpty(d.NotChecked);
        }
        await Assert.ThrowsAsync<ArgumentException>(() => reader.GetPalletDiagnosisAsync(-12345, CancellationToken.None));
    }

    /// <summary>The vendor-view fallback (for a login that can't read Quantities or OrderCompletedQtyResults) run for
    /// real: matched on order name, part and revision, from the order's registration on, it must give the same window
    /// sums as the table path.</summary>
    [SkippableFact]
    public async Task Live_view_fallback_production_sql_runs_and_agrees_with_the_tables()
    {
        var ct = CancellationToken.None;
        var s = LiveSettings();
        var viewSchema = new SchemaInfo(new Dictionary<string, string[]>
        {
            ["Orders"] = ["OrderID", "PartID", "OrderName", "Quantity", "Status", "DueDate", "RegistrationDate"],
            ["Parts"] = ["PartID", "PartName", "Revision"],
            ["ViewOrders"] = ["OrderName", "PartName", "PartRevision", "NormalQty", "ScrapQty", "RemainQty", "InProcQty"],
            ["ViewDayOrderCompletedQty"] = ["ProductionDate", "OrderName", "PartName", "PartRevision", "NormalQtyDay", "ScrapQtyDay"],
        }.SelectMany(t => t.Value.Select(c => (t.Key, c))));
        var sql = SqlMakinoReader.ProductionSql(viewSchema, byOrder: false, byPart: false, includeIdle: true);

        var fromView = new Dictionary<int, int?>();
        var production = await Reader(100000).GetProductionAsync(7, null, null, true, false, ct);
        await using (var connection = new Microsoft.Data.SqlClient.SqlConnection(s.Makino.ConnectionString()))
        {
            await connection.OpenAsync(ct);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlMakinoReader.Isolation + sql;
            cmd.Parameters.Add("@take", System.Data.SqlDbType.Int).Value = 100001;
            cmd.Parameters.Add("@from", System.Data.SqlDbType.DateTime).Value = production.ServerTime.Date.AddDays(-6);
            try
            {
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    var window = r.GetOrdinal("WindowCompleted");
                    fromView[Convert.ToInt32(r.GetValue(r.GetOrdinal("OrderID")))] = r.IsDBNull(window) ? null : Convert.ToInt32(r.GetValue(window));
                }
            }
            catch (Microsoft.Data.SqlClient.SqlException e) when (e.Number == 229) // permission denied
            {
                Skip.If(true, "the order views aren't readable by this login");
            }
        }

        Assert.NotEmpty(fromView);
        // Same orders, same output in the window (the table path counts by order id, the view by name, part and date).
        var tableSums = production.Orders.Where(o => o.CompletedInWindow is > 0).Select(o => o.CompletedInWindow).Order().ToList();
        var viewSums = fromView.Values.Where(v => v is > 0).Order().ToList();
        Assert.Equal(tableSums, viewSums);
    }

    [SkippableFact]
    public async Task Live_quantities_equal_the_cells_own_order_view()
    {
        var ct = CancellationToken.None;
        var s = LiveSettings();
        var view = new Dictionary<string, (int? Normal, int? Scrap, int? Remain, int? InProc)>();
        try
        {
            await using var connection = new Microsoft.Data.SqlClient.SqlConnection(s.Makino.ConnectionString());
            await connection.OpenAsync(ct);
            await using var cmd = connection.CreateCommand();
            // The view's InProcQty also holds the parts a work-set station is about to clamp (WorkSetCommand), which
            // the console's order screen (GetOrders_V2: clamped + waiting between processes) shows apart; add them here.
            cmd.CommandText = SqlMakinoReader.Isolation + "SELECT v.OrderName, v.NormalQty, v.ScrapQty, v.RemainQty, " +
                "v.InProcQty - ISNULL((SELECT SUM(c.ClampQuantity) FROM WorkSetCommand c JOIN Orders o ON o.OrderID = c.ClampOrderID " +
                "WHERE o.OrderName = v.OrderName), 0) FROM ViewOrders v;";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            static int? I(Microsoft.Data.SqlClient.SqlDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToInt32(r.GetValue(i));
            while (await r.ReadAsync(ct)) view.TryAdd(r.GetString(0), (I(r, 1), I(r, 2), I(r, 3), I(r, 4)));
        }
        catch (Microsoft.Data.SqlClient.SqlException)
        {
            Skip.If(true, "ViewOrders isn't readable by this login");
        }

        var production = await Reader(100000).GetProductionAsync(7, null, null, true, false, ct);
        var orders = await Reader(100000).GetOrdersAsync(false, false, 30, ct);
        var compared = 0;
        foreach (var o in production.Orders.Where(o => view.ContainsKey(o.Name)))
        {
            var v = view[o.Name];
            Assert.Equal((v.Normal, v.Scrap, v.Remain, v.InProc), (o.CompletedQty, o.ScrapQty, o.RemainingQty, o.InProcessQty));
            compared++;
        }
        foreach (var o in orders.Orders.Where(o => view.ContainsKey(o.Name)))
            Assert.Equal((view[o.Name].Normal, view[o.Name].Scrap, view[o.Name].Remain), (o.CompletedQty, o.ScrapQty, o.RemainingQty));
        Skip.If(compared == 0, "no active order in ViewOrders");
    }

    [SkippableFact]
    public async Task Live_attention_is_ordered_counted_and_capped()
    {
        var ct = CancellationToken.None;
        var overview = await Reader().GetOverviewAsync(ct);
        var list = await Reader(100000).GetAttentionAsync(true, ct);

        string[] kinds = ["device", "work set", "manual transport", "fixture", "tools", "maintenance"];
        Assert.All(list.Items, i => Assert.Contains(i.Kind, kinds));
        Assert.All(list.Items, i => Assert.False(string.IsNullOrWhiteSpace(i.What)));
        Assert.All(list.Items.Where(i => i.Device is not null), i => Assert.Contains(overview.Devices, d => d.Name == i.Device));
        Assert.All(list.Counts.Values, n => Assert.True(n > 0));
        Assert.All(list.MachineToolStatus ?? new Dictionary<string, string>(),
            m => Assert.Contains(overview.Devices, d => d.Name == m.Key && d.Type == "machine"));
        // A machine's "lacking tools" is context, never an item of its own.
        Assert.DoesNotContain(list.Items, i => i.Kind == "device" && i.What.Contains("lacking", StringComparison.Ordinal));
        if (!list.Capped)
        {
            Assert.Equal(list.Counts.GetValueOrDefault("machining suspended"),
                list.Items.Count(i => i.Kind == "fixture" && i.What.Contains("machining suspended", StringComparison.Ordinal)));
        }
        // Parked fixtures (only suspended or on hold) come after everything else.
        var firstParked = list.Items.ToList().FindIndex(i => i.Kind == "fixture" && IsParked(i.What));
        if (firstParked >= 0) Assert.All(list.Items.Skip(firstParked), i => Assert.True(i.Kind == "fixture" && IsParked(i.What)));

        Skip.If(list.Items.Count < 4, "fewer than four things need attention");
        var capped = await Reader(3).GetAttentionAsync(true, ct);
        Assert.True(capped.Capped);
        Assert.Equal(list.Items.Take(3).Select(i => (i.Kind, i.What)), capped.Items.Select(i => (i.Kind, i.What)));
        Assert.Equal(list.Counts, capped.Counts);

        // By default the parked fixtures are only counted, per place and condition.
        var compact = await Reader(100000).GetAttentionAsync(false, ct);
        Assert.DoesNotContain(compact.Items, i => i.Kind == "fixture" && IsParked(i.What));
        Assert.Equal(list.Items.Count(i => i.Kind == "fixture" && IsParked(i.What)), compact.Parked?.Fixtures ?? 0);
        Assert.Equal(list.Items.Count(i => !(i.Kind == "fixture" && IsParked(i.What))), compact.Items.Count);
        Assert.Equal(list.Counts, compact.Counts);
    }

    private static bool IsParked(string what) =>
        what.Split(", ").All(w => w is "machining suspended" or "work-set hold" or "lacking tools")
        && (what.Contains("machining suspended", StringComparison.Ordinal) || what.Contains("work-set hold", StringComparison.Ordinal));
}
