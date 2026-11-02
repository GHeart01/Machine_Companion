namespace MakinoMCP.Core.Cell;

/// <summary>Read-only view of the Makino cell database. Each call opens its own connection; failures throw
/// (an unknown device name throws ArgumentException listing the valid names).</summary>
public interface IMakinoReader
{
    Task<CellOverview> GetOverviewAsync(CancellationToken ct);

    /// <summary>Alarms raised in the last <paramref name="hours"/>, newest first. With openOnly, every alarm still open,
    /// however old.</summary>
    Task<AlarmList> GetAlarmsAsync(int hours, string? device, bool openOnly, bool includeWarnings, CancellationToken ct);

    /// <summary>Alarms of the last <paramref name="days"/> grouped by device, source and number, most frequent first.</summary>
    Task<AlarmSummary> GetAlarmSummaryAsync(int days, string? device, CancellationToken ct);

    /// <summary>Daily state hours for the last <paramref name="days"/> (today counts as one), newest first.</summary>
    Task<UtilizationList> GetUtilizationAsync(int days, string? device, CancellationToken ct);

    /// <summary>Machining, work-set and transport results started in the last <paramref name="hours"/>, newest first.</summary>
    Task<ActivityList> GetRecentActivityAsync(int hours, string? device, CancellationToken ct);

    /// <summary>Orders, latest due first; activeOnly leaves out complete, cancelled and undefined ones.</summary>
    Task<OrderList> GetOrdersAsync(bool activeOnly, CancellationToken ct);

    Task<PalletList> GetPalletsAsync(CancellationToken ct);

    Task<LoginRightsInfo> GetLoginRightsAsync(CancellationToken ct);
}
