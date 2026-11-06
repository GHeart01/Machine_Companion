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

    /// <summary>Tools: per-device counts by status, plus the tools <paramref name="filter"/> picks (problems, all,
    /// near_end, alarm, absent; null = problems, or all when <paramref name="tool"/> is given). <paramref name="tool"/>
    /// matches an ITN or FTN number. An unknown filter throws ArgumentException listing the valid ones.</summary>
    Task<ToolList> GetToolsAsync(string? device, string? filter, int? tool, CancellationToken ct);

    /// <summary>Tool data changes of the last <paramref name="days"/>, newest first; without
    /// <paramref name="includeLifeUpdates"/> the routine life-count updates after each use are left out.</summary>
    Task<ToolChangeList> GetToolChangesAsync(int days, string? device, int? tool, bool includeLifeUpdates, CancellationToken ct);

    /// <summary>Tool alarms of the last <paramref name="days"/>, newest first.</summary>
    Task<ToolAlarmList> GetToolAlarmsAsync(int days, string? device, int? tool, CancellationToken ct);

    /// <summary>Tools put into or taken out of a machine in the last <paramref name="days"/>, newest first.</summary>
    Task<ToolMovementList> GetToolMovementsAsync(int days, string? device, int? tool, CancellationToken ct);

    /// <summary>A part's routing: the part named exactly <paramref name="part"/> (case-insensitive; also the
    /// <paramref name="revision"/> when given), else the one part whose name contains it; several → candidates and no
    /// routing. An empty part name throws ArgumentException.</summary>
    Task<PartRouting> GetPartRoutingAsync(string part, string? revision, bool includeTools, CancellationToken ct);

    /// <summary>Quantities per order (active orders, or the ones named by <paramref name="order"/> / <paramref name="part"/>
    /// whatever their status), with the completed and scrap counts per day of the last <paramref name="days"/>. Without
    /// <paramref name="includeIdle"/>, active orders with no output, scrap or work on an active fixture in the window are
    /// only counted (OtherActiveOrders).</summary>
    Task<ProductionList> GetProductionAsync(int days, string? order, string? part, bool includeIdle, CancellationToken ct);

    /// <summary>Everything that waits on a person right now, most severe first. Without <paramref name="includeParked"/>,
    /// fixtures that are only suspended or on hold are counted per place (Parked), not listed.</summary>
    Task<AttentionList> GetAttentionAsync(bool includeParked, CancellationToken ct);

    /// <summary>Why the pallet numbered <paramref name="pallet"/> isn't moving, by the console's Pallet Diagnosis rules.
    /// An unknown pallet number throws ArgumentException.</summary>
    Task<PalletDiagnosis> GetPalletDiagnosisAsync(int pallet, CancellationToken ct);

    /// <summary>Macro variable values the machines uploaded in the last <paramref name="days"/>, newest first, each
    /// with the machining result running then. <paramref name="variable"/> and <paramref name="pallet"/> filter (null
    /// for none).</summary>
    Task<MacroValueList> GetMacroValuesAsync(int days, string? device, int? variable, int? pallet, CancellationToken ct);

    /// <summary>The work-set stations as their monitor shows them: state, and each fixture's instruction.</summary>
    Task<WorkSetView> GetWorkSetAsync(string? device, CancellationToken ct);

    /// <summary>Serial number records of the last <paramref name="days"/> containing <paramref name="serial"/>
    /// (case-insensitive), newest first, with the result each belongs to.</summary>
    Task<SerialSearch> FindSerialAsync(string serial, int days, string? device, CancellationToken ct);

    Task<LoginRightsInfo> GetLoginRightsAsync(CancellationToken ct);
}
