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
/// normal, abnormal, cancel). For pallet types the cell's dashboard doesn't track, it stores status 0 and a raw work
/// status, so both show "code N".</summary>
public sealed record PalletInfo(
    int Number, string Name, string Status, string WorkStatus, string Location, IReadOnlyList<string> Fixtures);

/// <summary>CanWrite is true when the login could change the cell database (MakinoMCP never does).</summary>
public sealed record LoginRightsInfo(bool CanWrite, int DeviceCount, string Summary);

// 0.2: tools. Optional fields are left out of the JSON when null, so a healthy tool stays short.

/// <summary>Tools on the cell. Summary counts every tool per device by status (whatever the filter); Tools holds the
/// ones the filter picked, most severe first. LengthUnit (mm or inch, the cell's tool offset unit) is the unit of
/// every length and diameter. LifeDirectionAssumed is true when the cell doesn't say whether its tool life counts up
/// (used) or down (remaining) and "counts up" was assumed; LengthUnitAssumed likewise for the unit (mm assumed).</summary>
public sealed record ToolList(
    DateTime ServerTime, bool Capped, string Filter, string LengthUnit, IReadOnlyList<DeviceToolCount> Summary,
    IReadOnlyList<ToolInfo> Tools)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LifeDirectionAssumed { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LengthUnitAssumed { get; init; }

    /// <summary>What this cell lacks for the chosen filter, which then couldn't be applied (Tools is empty).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? NotAvailable { get; init; }
}

/// <summary>How many tools sit on a device, and how many of them are in each status (statuses with none left out).</summary>
public sealed record DeviceToolCount(string Device, int Tools, IReadOnlyDictionary<string, int> ByStatus);

/// <summary>One physical tool (ITN) of a tool type (FTN). Name is the FTN's comment, Location the device and pot
/// ("MC1 pot 20", "CRIB1 (tool crib)"), MovingTo the place reserved for it during a tool in/out. Status (and each
/// cutter's) is derived as the System Console shows it: an alarm bit (AlarmTool or a cutter's) → alarm; else a stored
/// status other than ready; else a life warning bit → life warning; else ready. Flags: alarm tool, prohibited, absent.
/// LastUsed is the CNC's raw value (format not confirmed); LastUsedProgram the O-number of the program that last used
/// the tool.</summary>
public sealed record ToolInfo(int Itn, int? Ftn, string Name, string Location, string Status, IReadOnlyList<CutterInfo> Cutters)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ItnComment { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Kind { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Flags { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MovingTo { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LastUsed { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LastUsedProgram { get; init; }
}

/// <summary>One cutting edge of a tool: its status, the alarm bits set on it, its life and its measured length and
/// diameter (in the list's LengthUnit; left out when 0, not measured).</summary>
public sealed record CutterInfo(int CutterNo, string Status)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Alarms { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LifeInfo? Life { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Length { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Diameter { get; init; }
}

/// <summary>Tool life in Unit: "min" (time), "count", "m" or "inch" (distance, by the cell's unit), or "code N". Limit is
/// the full life, WarnAtUsed how much use raises the life warning; Used + Remaining = Limit (Remaining stops at 0).
/// A tool whose life is 0 isn't life-managed: only Note ("not life-managed") is set, never a percentage.</summary>
public sealed record LifeInfo
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Unit { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Limit { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? WarnAtUsed { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Used { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Remaining { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? UsedPercent { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; init; }
}

/// <summary>LengthUnit is the unit of the lengths in Changed (mm or inch).</summary>
public sealed record ToolChangeList(DateTime ServerTime, bool Capped, string LengthUnit, IReadOnlyList<ToolChange> Changes)
{
    /// <summary>True when the routine life-count updates after each use were left out (the default).</summary>
    public bool LifeUpdatesLeftOut { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LifeDirectionAssumed { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LengthUnitAssumed { get; init; }
}

/// <summary>One change to a tool's data (presetter, tool data import, manual edit, the machine after a use): only the
/// values that changed, as "length 101.234 → 101.250 mm"; a routine life tick is only shown when nothing else changed. Cutter only for tools with more than one.</summary>
public sealed record ToolChange(DateTime Time, string Device, int Itn, int? Ftn, IReadOnlyList<string> Changed)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Cutter { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? User { get; init; }
}

/// <summary>Tool alarms, newest first. Source says where they come from: the cell's tool alarm history, or (when this
/// login can't read it) the tool data changes in which an alarm bit turned on, which is what the cell's own Tool
/// Alarm History report reads. NotAvailable names fields the source lacks. LengthUnit: mm or inch.</summary>
public sealed record ToolAlarmList(
    DateTime ServerTime, bool Capped, string Source, string LengthUnit, IReadOnlyList<ToolAlarmEvent> Alarms,
    IReadOnlyList<string> NotAvailable)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LifeDirectionAssumed { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LengthUnitAssumed { get; init; }
}

public sealed record ToolAlarmEvent(DateTime Time, string Device, int Itn, int? Ftn, IReadOnlyList<string> Alarms)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Pot { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Cutter { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LifeInfo? Life { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Length { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Diameter { get; init; }
}

public sealed record ToolMovementList(DateTime ServerTime, bool Capped, IReadOnlyList<ToolMovement> Movements);

/// <summary>A tool put into ("in") or taken out of ("out") a machine; the cell logs no other moves. From and To are
/// device and pot; Status is the tool's status at the time, UsedPercent its life used then.</summary>
public sealed record ToolMovement(DateTime Time, string Action, int Itn, int? Ftn, string From, string To, string Status)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ItnComment { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Cutter { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? UsedPercent { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? User { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; init; }
}
