using MakinoMCP.Core.Cell;

namespace MakinoMCP.Tests;

/// <summary>A cell with sample data, for tool tests. <see cref="Fail"/> makes every call throw;
/// <see cref="Hang"/> makes every call wait until cancelled.</summary>
internal sealed class FakeMakinoReader : IMakinoReader
{
    public static readonly DateTime Now = new(2026, 11, 2, 10, 30, 0);

    private static readonly Dictionary<int, string> Devices = new()
    {
        [1] = "MC1",
        [2] = "MC2",
        [3] = "Vehicle 1",
        [4] = "WSS1",
    };

    public Func<Exception>? Fail { get; init; }
    public bool Hang { get; init; }
    public bool CanWrite { get; init; }

    private async Task<T> Answer<T>(Func<T> make, CancellationToken ct, string? device = null)
    {
        if (Hang) await Task.Delay(Timeout.Infinite, ct);
        if (Fail is not null) throw Fail();
        SqlMakinoReader.FindDevice(Devices, device);
        return make();
    }

    public Task<CellOverview> GetOverviewAsync(CancellationToken ct) => Answer(() => new CellOverview(Now,
    [
        new DeviceState("MC1", "machine", true, false, "memory", "machining", null,
            [new PalletSpot("table", 7, "processing"), new PalletSpot("next front", 9, "reserved")], 1, 0, Now.AddMinutes(-12), [])
        {
            Status = "busy", RecoveryRequest = false, ToolStatus = "lacking tools", CncRun = "run", OperatorCall = "released",
            RunJob = "machining", Program = new NcProgram("O1234", "O0100"),
        },
        new DeviceState("Vehicle 1", "vehicle", true, false, null, "load/unload", null,
            [new PalletSpot("buffer 1", 7, "")], 0, 0, Now.AddHours(-1), ["vehicle mode", "buffer status"])
        {
            Status = "busy", RecoveryRequest = false, TransportFrom = "WSS1", TransportTo = "MC1",
        },
    ]) { HistoryDays = 90 }, ct);

    public Task<AlarmList> GetAlarmsAsync(int hours, string? device, bool openOnly, bool includeWarnings, CancellationToken ct) =>
        Answer(() => new AlarmList(Now, false,
        [
            new Alarm("PMC", "MC1", 1012, "alarm", "Coolant level low", Now.AddMinutes(-30), null, 30),
        ]), ct, device);

    public Task<AlarmSummary> GetAlarmSummaryAsync(int days, string? device, CancellationToken ct) =>
        Answer(() => new AlarmSummary(Now, false,
        [
            new AlarmSummaryRow("MC1", "PMC", 1012, "Coolant level low", 4, 95.5, Now.AddDays(-6), Now.AddMinutes(-30)),
        ]), ct, device);

    public Task<UtilizationList> GetUtilizationAsync(int days, string? device, CancellationToken ct) =>
        Answer(() => new UtilizationList(Now, false,
        [
            new UtilizationDay("MC1", Now.Date, 0, 2, 6, 0.5, 0.25, 68.6, null),
        ]), ct, device);

    public Task<ActivityList> GetRecentActivityAsync(int hours, string? device, CancellationToken ct) =>
        Answer(() => new ActivityList(Now, false,
        [
            new ActivityItem("machining", "MC1", Now.AddHours(-2), Now.AddHours(-1), 7, "O1234", "normal"),
        ]), ct, device);

    public Task<OrderList> GetOrdersAsync(bool activeOnly, CancellationToken ct) =>
        Answer(() => new OrderList(Now, false,
        [
            new OrderInfo("ORDER-001", "CONTOSO-BRACKET", "in progress", 10, false, Now.AddDays(1), null),
        ]), ct);

    public Task<PalletList> GetPalletsAsync(CancellationToken ct) =>
        Answer(() => new PalletList(Now, false,
        [
            new PalletInfo(7, "P7", "waiting for machining", "not completed", "MC1 table", ["FIXTURE-A"]),
        ]), ct);

    public Task<LoginRightsInfo> GetLoginRightsAsync(CancellationToken ct) =>
        Answer(() => new LoginRightsInfo(CanWrite, Devices.Count, CanWrite ? "can write" : "read only"), ct);
}
