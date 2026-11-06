using System.Text.Json.Serialization;

namespace MakinoMCP.Core.Cell;

// What the reader returns. Codes are already turned into words; unknown codes read "code N".
// Every list carries the cell's ServerTime and Capped (more rows existed than the row limit let through).

/// <summary>HistoryDays: how many days the cell keeps alarm and result history (SystemParameters.PreserveResults);
/// the vendor's default of 90 with HistoryDaysAssumed when the cell has no such setting; null when this login can't
/// read the settings.</summary>
public sealed record CellOverview(DateTime ServerTime, IReadOnlyList<DeviceState> Devices)
{
    public int? HistoryDays { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? HistoryDaysAssumed { get; init; }

    /// <summary>On duty or off duty (the cell's duty management; "off duty (duty management not used)" when the cell
    /// doesn't use it, since it then behaves as off duty); null when it can't be read.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Duty { get; init; }

    /// <summary>True when duty follows the cell's weekly schedule rather than being switched by hand.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? DutySchedule { get; init; }

    /// <summary>The cell's scheduled auto power-off, when one is set; null when there is none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CellPowerOff? PowerOff { get; init; }

    /// <summary>What the cell does when a machining cycle ends abnormally, per kind of abnormal finish (the same for
    /// every machine; per machine instead when the cell sets it per device). Only the kinds the cell has a setting for.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? RecoveryPolicy { get; init; }

    /// <summary>Cell-level sections this cell or login can't show (duty, power-off, recovery policy).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? NotAvailable { get; init; }
}

/// <summary>A scheduled auto power-off: its state, and the start and release times as the cell stores them.</summary>
public sealed record CellPowerOff(string Status)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Start { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Release { get; init; }
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

    // 0.4: open/closed, timing, power and per-device settings; left out at their default.

    /// <summary>False when the device is closed: it gets no automatic work (status still reads idle). Left out when
    /// open.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Open { get; init; }

    /// <summary>Work-set stations: the instruction it shows the operator while busy.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Instruction { get; init; }

    /// <summary>When the current machining, work-set instruction or transport started.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? Since { get; init; }

    /// <summary>When it is planned to finish (start plus the planned time); left out when the cell has no plan.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? PlannedFinish { get; init; }

    /// <summary>The device's power state when not plainly on, and why a scheduled power-off is still pending.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PowerStatus { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PowerOffResult { get; init; }

    /// <summary>Work-set stations: operations switched off for this station.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? DisabledOperations { get; init; }

    /// <summary>Machines, when the cell sets the recovery policy per device: as CellOverview.RecoveryPolicy.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? RecoveryPolicy { get; init; }
}

/// <summary>O-numbers as "O1234"; Executing is null when the cell doesn't say. Name: the NC program of the job
/// running now. MainIs: what the main O-number is when it is one of the cell's own programs (the dataserver program
/// that runs the job's program, or the work offset program).</summary>
public sealed record NcProgram(string? Main, string? Executing)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MainIs { get; init; }
}

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

public sealed record OrderList(DateTime ServerTime, bool Capped, IReadOnlyList<OrderInfo> Orders)
{
    /// <summary>True when the cell counts an order complete once good plus scrap parts reach its quantity (its
    /// OrderIncludesScrap setting, on by default), false when only good parts count; null when the settings can't be
    /// read.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? CompleteIncludesScrap { get; init; }

    /// <summary>What this cell (or login) can't show: the quantities when neither the cell's quantity table nor its
    /// order view is readable.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? NotAvailable { get; init; }

    /// <summary>Where the orders' DashboardStatus comes from, when any order shows one.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DashboardStatusFrom { get; init; }

    /// <summary>The cell's automatic order deletion, in words (with finished orders listed, or the archive); null when
    /// the settings can't be read or finished orders weren't asked for.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AutoDelete { get; init; }

    /// <summary>Finished and deleted orders from the cell's archive, newest first (only with includeArchive).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ArchivedOrder>? Archive { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ArchiveDays { get; init; }

    /// <summary>True when more archived orders existed than the row limit let through.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ArchiveCapped { get; init; }
}

/// <summary>Quantity is the order's required quantity; Completed the time it completed. CompletedQty: good parts out
/// of the last process; ScrapQty: scrapped in any process; RemainingQty: not started yet (first process).
/// MachiningSuspended: the order's machining suspend (its waiting fixtures aren't machined), true only while the
/// cell's OrderMachiningSuspend setting makes it count; an order suspended as a whole has status "suspended" instead.
/// get_orders lists the whole queue, so the JSON leaves out fields at their default: MachiningSuspended unless true,
/// Due and Completed when null, a quantity of 0 (the properties themselves keep 0, so code reading them can tell 0
/// from not available).</summary>
public sealed record OrderInfo(
    string Name,
    string Part,
    string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int Quantity,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? MachiningSuspended,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTime? Due,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTime? Completed)
{
    [JsonIgnore]
    public int? CompletedQty { get; init; }

    [JsonIgnore]
    public int? ScrapQty { get; init; }

    [JsonIgnore]
    public int? RemainingQty { get; init; }

    // What the JSON shows: the quantities, 0 left out.
    [JsonPropertyName("completedQty"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CompletedQtyShown => NonZero(CompletedQty);

    [JsonPropertyName("scrapQty"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ScrapQtyShown => NonZero(ScrapQty);

    [JsonPropertyName("remainingQty"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RemainingQtyShown => NonZero(RemainingQty);

    /// <summary>Parts started and not finished (clamped plus waiting for a later process); read to tell whether the
    /// order's material has been taken, not shown.</summary>
    [JsonIgnore]
    public int? InProcessQty { get; init; }

    /// <summary>The cell's dashboard word for an active order (running, waiting for material), only when it differs
    /// from Status; see OrderList.DashboardStatusFrom.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DashboardStatus { get; init; }

    /// <summary>The start date, only when it is still to come or the order is inactive.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? Start { get; init; }

    /// <summary>The order's priority, only when not 0.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Priority { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Comment { get; init; }

    /// <summary>What the order waits for, when it isn't running: its start date, a quantity, material, an operator's
    /// release, or a fixture to take its material.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WaitingFor { get; init; }

    /// <summary>True for an order of a quick-data part (never deleted automatically).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? QuickData { get; init; }

    private static int? NonZero(int? n) => n is 0 ? null : n;
}

/// <summary>NotAvailable: what this cell or login can't show (fixture work, feasible devices, …).</summary>
public sealed record PalletList(DateTime ServerTime, bool Capped, IReadOnlyList<PalletInfo> Pallets)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? NotAvailable { get; init; }
}

/// <summary>Status and WorkStatus are the cell's dashboard words (idle, waiting for machining, …; not completed,
/// normal, abnormal, cancel). For pallet types the cell's dashboard doesn't track, it stores status 0 and a raw work
/// status, so both show "code N". Fixtures: one short line per fixture (number, own name, non-default states);
/// FixtureDetails instead when one pallet or the details were asked for.</summary>
public sealed record PalletInfo(
    int Number, string Name, string Status, string WorkStatus, string Location,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Fixtures)
{
    /// <summary>The pallet type, only when not standard.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; init; }

    /// <summary>The pallet's transport priority, only when not 0.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Priority { get; init; }

    /// <summary>The raised priority of a queued pallet, only when not 0 (the cell also sets it itself for manual and
    /// synchronous transports; it clears when the pallet leaves the queue).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? OperatorPriority { get; init; }

    /// <summary>Machines and work-set stations the pallet may not go to (its feasible-device list lacks them); null
    /// when it may go to every one.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? NotFeasibleFor { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<FixtureDetail>? FixtureDetails { get; init; }
}

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

    /// <summary>True when the cell stores radii in its "diameter" values (DiameterTypePC = 0): diameter, D geometry,
    /// D wear and diameter wear are then radii, shown as stored.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? DiameterIsRadius { get; init; }

    /// <summary>True when the cell doesn't say whether it stores diameters or radii, and diameter was assumed.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? DiameterTypeAssumed { get; init; }

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
    /// <summary>True when the routine updates were left out (the default): the life-count ticks after each use and the
    /// tools that only stepped to life warning.</summary>
    public bool LifeUpdatesLeftOut { get; init; }

    /// <summary>How many rows in the window were only a tool stepping to life warning (routine wear), left out of
    /// Changes; null when they are listed (includeLifeUpdates) or the cell keeps no status pair.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LifeWarningTransitions { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LifeDirectionAssumed { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LengthUnitAssumed { get; init; }

    /// <summary>True when the cell stores radii in its "diameter" values (DiameterTypePC = 0): diameter, D geometry,
    /// D wear and diameter wear are then radii, shown as stored.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? DiameterIsRadius { get; init; }

    /// <summary>True when the cell doesn't say whether it stores diameters or radii, and diameter was assumed.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? DiameterTypeAssumed { get; init; }

    /// <summary>What this cell's tool data log can't tell apart (columns it lacks), and how to see the rows anyway.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? NotAvailable { get; init; }
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

    /// <summary>True when the cell stores radii in its "diameter" values (DiameterTypePC = 0): diameter, D geometry,
    /// D wear and diameter wear are then radii, shown as stored.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? DiameterIsRadius { get; init; }

    /// <summary>True when the cell doesn't say whether it stores diameters or radii, and diameter was assumed.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? DiameterTypeAssumed { get; init; }
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

// 0.2: routing, production and attention. Optional fields are left out of the JSON when null.

/// <summary>A part's routing, or the candidates when the name matched several parts (then Part is null; ask again
/// with the exact name, and the revision when names repeat). OtherMatches names other parts whose names contain the
/// query (an exact name wins over them; parts split per operation are separate parts). ToolCheck says how far the
/// tool check goes; NotAvailable names what this cell or login can't show.</summary>
public sealed record PartRouting(DateTime ServerTime, string Query, bool Capped)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PartRoute? Part { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<PartCandidate>? Candidates { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? OtherMatches { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolCheck { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? NotAvailable { get; init; }
}

public sealed record PartCandidate(string Name, int Processes)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Revision { get; init; }
}

/// <summary>Status is the most severe of its processes' statuses (ready, suspended, cancelled).</summary>
public sealed record PartRoute(string Name, string Status, IReadOnlyList<ProcessRoute> Processes)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Revision { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Comment { get; init; }
}

/// <summary>One process (route step), in order. MaxFixtures: how many fixtures may work on it at once (left out when
/// unlimited). Fixtures: where it can be clamped, as "pallet 12 fixture 1", in the cell's priority order.</summary>
public sealed record ProcessRoute(int Number, string Status, IReadOnlyList<OperationRoute> Operations, IReadOnlyList<JobRoute> Jobs)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxFixtures { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Fixtures { get; init; }
}

/// <summary>ClampQuantity: parts clamped per fixture for this operation.</summary>
public sealed record OperationRoute(int Sequence, int ClampQuantity)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }
}

/// <summary>One job of a process, in order: work setting (WorkSet load or unload, InfoFile the work instructions),
/// machining (Program, OperatorCall when the job stops for the operator, Tools), … Minutes: the planned machining or
/// work-set time. FeasibleDevices: where it may run.</summary>
public sealed record JobRoute(int Number, string Type)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Program { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WorkSet { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InfoFile { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Minutes { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OperatorCall { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? FeasibleDevices { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ProgramTool>? Tools { get; init; }
}

/// <summary>A tool type (FTN) the machining program needs, Needed of them (the program's ToolCount; 0 is never
/// counted missing, as on the cell). Ptn only when the program calls it by
/// another number. Cutting: how much life one run uses ("12.5 min", "40 count"). Usable: per feasible machine, the
/// tools of this type there in status ready or life warning; MissingOn: the machines with fewer than Needed.</summary>
public sealed record ProgramTool(int Ftn, int Needed)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Ptn { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Cutting { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, int>? Usable { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? MissingOn { get; init; }
}

/// <summary>Days: the window of the daily counts (today counts as one). Daily: good and scrap per production day
/// summed over the listed orders (when no order or part is named; then each order has its own Daily instead).
/// Capped: more orders than the row limit; DailyCapped: more day rows than the row limit (the oldest left out).</summary>
public sealed record ProductionList(DateTime ServerTime, bool Capped, int Days, IReadOnlyList<OrderProduction> Orders)
{
    /// <summary>As <see cref="OrderList.CompleteIncludesScrap"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? CompleteIncludesScrap { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<DailyQty>? Daily { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? DailyCapped { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? NotAvailable { get; init; }

    /// <summary>Why the list is empty when an order or part was named.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; init; }

    /// <summary>Active orders left out because nothing happened to them in the window (no output, no scrap, no parts on
    /// a fixture that isn't suspended or on hold); null when every active order is listed.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? OtherActiveOrders { get; init; }

    /// <summary>True when completedQty also counts parts sent to an external process after the last process (the
    /// cell's NormalFinIncludeLastOutProc); null when the settings can't be read.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? CompletedIncludesLastOutProcess { get; init; }

    /// <summary>True when the machines' probe reports how many parts are really clamped (the cell's
    /// ProbePartCountMacroVariable): the clamped quantity is then lowered at machining, so fewer parts than a
    /// fixture's clamp quantity is by design.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ProbePartCount { get; init; }

    /// <summary>As <see cref="OrderList.AutoDelete"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AutoDelete { get; init; }

    /// <summary>Only with includeCycleTime: how the minutes are counted.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CycleTimeNote { get; init; }
}

/// <summary>One order's counts, as the cell's own order screen counts them. Quantity: required. CompletedQty: good
/// parts out of the last process; ScrapQty: scrapped in any process; RemainingQty: not started (first process);
/// InProcessQty: started, not finished (ClampedQty clamped on fixtures now, plus parts waiting between processes).
/// CompletedInWindow/ScrapInWindow: in the window. Daily (only when an order or part is named): per production day in
/// the window, days without any left out.</summary>
public sealed record OrderProduction(string Name, string Part, string Status, int Quantity)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CompletedQty { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ScrapQty { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RemainingQty { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? InProcessQty { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ClampedQty { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CompletedInWindow { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ScrapInWindow { get; init; }

    /// <summary>Parts sent to an external process (any process), only when not 0. They are in neither InProcessQty nor
    /// CompletedQty (unless the cell counts the last process's as completed).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? OutProcessQty { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<DailyQty>? Daily { get; init; }

    /// <summary>Only with includeCycleTime: machining and work-set station minutes in the cell's results for this order
    /// (matched by order name, part and revision), and machining minutes per part made (completed + scrap).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MachiningMinutes { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? WorkSetMinutes { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MachiningMinutesPerPart { get; init; }
}

public sealed record DailyQty(DateOnly Date, int Completed, int Scrap);

/// <summary>What waits on a person, most severe first. Counts: how many of each condition the cell has (devices,
/// fixtures, tools, …), even past the row cap. MachineToolStatus: each machine's tool status for its upcoming work, as
/// context only (a busy machine often reads "lacking tools" while a later job waits for a tool; the fixtures lacking
/// tools are items). NotAvailable: sections this cell or login can't show.</summary>
public sealed record AttentionList(
    DateTime ServerTime, bool Capped, IReadOnlyDictionary<string, int> Counts, IReadOnlyList<AttentionItem> Items,
    IReadOnlyList<string> NotAvailable)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? MachineToolStatus { get; init; }

    /// <summary>Fixtures that are only suspended or on hold (no damage, pre-machining suspend or reclamp timeout),
    /// counted per place; null when there are none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ParkedFixtures? Parked { get; init; }

    /// <summary>Which of a fixture's jobs the cell checks when it marks the fixture lacking tools (the cell's
    /// setting); null when the cell doesn't mark fixtures lacking tools or its settings can't be read.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LackingToolScope { get; init; }
}

/// <summary>Fixtures parked by operators (machining suspended or held at the work-set station): how many, how many of
/// them are also lacking tools, and per place (where their pallets are: a device, or "pallet stocker") and condition.
/// get_attention with includeParked lists them one by one with the reason typed.</summary>
public sealed record ParkedFixtures(int Fixtures, IReadOnlyList<ParkedGroup> Groups)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LackingTools { get; init; }
}

public sealed record ParkedGroup(string Place, string Condition, int Fixtures)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LackingTools { get; init; }
}

/// <summary>Kind: device, work set, manual transport, fixture, tools or maintenance. What: its conditions, joined.
/// Since only where the cell records when it started.</summary>
public sealed record AttentionItem(string Kind, string What)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Device { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Pallet { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Fixture { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Order { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Process { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Detail { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? Since { get; init; }
}

// 0.5: get_pallet_diagnosis.

/// <summary>Why a pallet's work isn't moving, as the console's Pallet Diagnosis would say: per fixture its current job
/// and the verdict, with each feasible device's own verdict. NotChecked: what the console also checks but the cell keeps
/// only in memory (or this tool doesn't follow), so a "can run" verdict doesn't rule those out. NotAvailable: checks this
/// cell or login can't make, named rather than passed.</summary>
public sealed record PalletDiagnosis(
    DateTime ServerTime, int Pallet, string Status, string WorkStatus, string Location,
    IReadOnlyList<FixtureDiagnosis> Fixtures, IReadOnlyList<string> NotChecked)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? NotAvailable { get; init; }
}

/// <summary>One fixture: what it holds now (order, process), its current job (type, number, status) and the verdict.
/// Devices: each feasible device's verdict; left out when the job is running or there is no job.</summary>
public sealed record FixtureDiagnosis(int? Fixture, string Verdict)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Order { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Process { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Job { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? JobStatus { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<DeviceVerdict>? Devices { get; init; }
}

public sealed record DeviceVerdict(string Device, string Verdict);
