using Xunit;
using MakinoMCP.Core.Cell;
using MakinoMCP.Core.Settings;

namespace MakinoMCP.Tests;

// Reads a real cell. Skipped unless MAKINOMCP_LIVE_SETTINGS points at a settings file; run locally only.
// Assertions are structural: nothing here may name a device, count or value from a real cell.
public class LiveReaderTests
{
    private const string EnvVar = "MAKINOMCP_LIVE_SETTINGS";

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

        var pallets = await Reader().GetPalletsAsync(CancellationToken.None);
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
        Assert.NotNull(await r.GetAlarmSummaryAsync(7, null, ct));
        var util = await r.GetUtilizationAsync(7, null, ct);
        Assert.All(util.Days, d => Assert.InRange(d.BusyPercent, 0, 100));
        Assert.NotNull(await r.GetRecentActivityAsync(24, device, ct));
        Assert.NotNull(await r.GetOrdersAsync(true, ct));
        var pallets = await r.GetPalletsAsync(ct);
        Assert.All(pallets.Pallets, p => Assert.False(string.IsNullOrWhiteSpace(p.Location)));
        var rights = await r.GetLoginRightsAsync(ct);
        Assert.Equal(overview.Devices.Count, rights.DeviceCount);
        Assert.False(string.IsNullOrWhiteSpace(rights.Summary));

        var e = await Assert.ThrowsAsync<ArgumentException>(() => r.GetAlarmsAsync(24, "no-such-device-xyz", false, true, ct));
        Assert.Contains(device, e.Message);
    }
}
