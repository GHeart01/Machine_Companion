namespace MakinoMCP.Core.Cell;

// What the reader returns. Codes are already turned into words; unknown codes read "code N".
// Every list carries the cell's ServerTime and Capped (more rows existed than the row limit let through).

public sealed record CellOverview(DateTime ServerTime, IReadOnlyList<DeviceState> Devices);

/// <summary>One device. Fields the device type or this cell's software version doesn't have are null;
/// NotAvailable names the ones missing because of the version.</summary>
public sealed record DeviceState(
    string Name,
    string Type,
    bool? Connected,
    bool? Emergency,
    string? Mode,
    string? Action,
    string? SubAction,
    IReadOnlyList<PalletSpot> Pallets,
    int OpenAlarms,
    int OpenWarnings,
    DateTime? LastActivity,
    IReadOnlyList<string> NotAvailable);

/// <summary>A pallet on a device: Position is table, front or back; Status is the spot's raw status as "code N".</summary>
public sealed record PalletSpot(string Position, int? PalletNumber, string Status);

public sealed record AlarmList(DateTime ServerTime, bool Capped, IReadOnlyList<Alarm> Alarms);

/// <summary>Source is PMC or CNC; Level is alarm or warning; Minutes runs to Reset, or to ServerTime when still open.</summary>
public sealed record Alarm(
    string Source,
    string Device,
    int Number,
    string Level,
    string Message,
    DateTime Raised,
    DateTime? Reset,
    double? Minutes);

public sealed record AlarmSummary(DateTime ServerTime, bool Capped, IReadOnlyList<AlarmSummaryRow> Rows);

public sealed record AlarmSummaryRow(
    string Device,
    string Source,
    int Number,
    string Message,
    int Count,
    double TotalMinutes,
    DateTime FirstSeen,
    DateTime LastSeen);

public sealed record UtilizationList(DateTime ServerTime, bool Capped, IReadOnlyList<UtilizationDay> Days);

/// <summary>Hours per state for one device and day; Oee (percent) only where the cell keeps DeviceResultDaySummary.</summary>
public sealed record UtilizationDay(
    string Device,
    DateTime Date,
    double DownH,
    double IdleH,
    double BusyH,
    double ManualH,
    double ErrorH,
    double BusyPercent,
    int? Oee);

public sealed record ActivityList(DateTime ServerTime, bool Capped, IReadOnlyList<ActivityItem> Items);

/// <summary>Kind is machining, work-set or transport.</summary>
public sealed record ActivityItem(
    string Kind,
    string Device,
    DateTime Start,
    DateTime? Finish,
    int? PalletNumber,
    string Detail,
    string? Result);

public sealed record OrderList(DateTime ServerTime, bool Capped, IReadOnlyList<OrderInfo> Orders);

public sealed record OrderInfo(
    string Name,
    string Part,
    string Status,
    int Quantity,
    bool Suspended,
    DateTime? Due,
    DateTime? Completed);

public sealed record PalletList(DateTime ServerTime, bool Capped, IReadOnlyList<PalletInfo> Pallets);

public sealed record PalletInfo(int Number, string Name, string Status, string Location, IReadOnlyList<string> Fixtures);

/// <summary>CanWrite is true when the login could change the cell database (MakinoMCP never does).</summary>
public sealed record LoginRightsInfo(bool CanWrite, int DeviceCount, string Summary);
