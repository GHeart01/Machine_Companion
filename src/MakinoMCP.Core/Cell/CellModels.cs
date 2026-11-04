using System.Text.Json.Serialization;

namespace MakinoMCP.Core.Cell;

// What the reader returns. Codes are already turned into words; unknown codes read "code N".
// Every list carries the cell's ServerTime and Capped (more rows existed than the row limit let through).

/// <summary>HistoryDays: how many days the cell keeps alarm and result history (SystemParameters.PreserveResults),
/// null when the cell doesn't say.</summary>
public sealed record CellOverview(DateTime ServerTime, IReadOnlyList<DeviceState> Devices)
{
    public int? HistoryDays { get; init; }
}

/// <summary>One device. Fields the device type or this cell's software version doesn't have are null;
/// NotAvailable names the ones missing because of the version (or because the login can't read them).
/// Mode is the CNC mode on a machine, the station mode on a work-set station and the vehicle mode on a vehicle;
/// Action on a vehicle is its current transport command. The fields added in 0.2 (Status … TransportTo) are left out
/// of the JSON when null.</summary>
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
    IReadOnlyList<string> NotAvailable)
{
    /// <summary>The cell's own dashboard state: down, idle, busy, manual or error.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Status { get; init; }

    /// <summary>The console's "recovery request": the device waits for an operator to recover it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? RecoveryRequest { get; init; }

    /// <summary>Machines: whether the tools for the current work are there (ready, lacking tools, tool alarm, …).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolStatus { get; init; }

    /// <summary>Machines: the CNC's run state (reset, stop, hold, run).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CncRun { get; init; }

    /// <summary>Machines: an operator call from the NC program (M code) or the PMC; "released" when none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OperatorCall { get; init; }

    /// <summary>Machines: what kind of job runs (machining, pallet change, other preparation, manual transport).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RunJob { get; init; }

    /// <summary>Machines: the main program and the one executing now (a subprogram while it runs one).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public NcProgram? Program { get; init; }

    /// <summary>Vehicles: where the current transport comes from and goes to (device or stocker names).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TransportFrom { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TransportTo { get; init; }
}

/// <summary>O-numbers as "O1234"; Executing is null when the cell doesn't say.</summary>
public sealed record NcProgram(string? Main, string? Executing);

/// <summary>A pallet on a device. Position is table, front or back with Status the position's state (processing,
/// finished, …); "next table" (front, back) is a pallet reserved to come there next, Status "reserved"; on a
/// vehicle, "buffer 1" and "buffer 2" are its two pallet buffers, Status "code N" (or "" when the cell doesn't say).</summary>
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

/// <summary>Status and WorkStatus are the cell's dashboard words (idle, waiting for machining, …; not completed,
/// normal, abnormal, cancel); pallet types the dashboard doesn't cover show "code N".</summary>
public sealed record PalletInfo(
    int Number, string Name, string Status, string WorkStatus, string Location, IReadOnlyList<string> Fixtures);

/// <summary>CanWrite is true when the login could change the cell database (MakinoMCP never does).</summary>
public sealed record LoginRightsInfo(bool CanWrite, int DeviceCount, string Summary);
