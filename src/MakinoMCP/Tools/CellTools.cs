using System.ComponentModel;
using ModelContextProtocol.Server;

namespace MakinoMCP.Tools;

/// <summary>The cell database tools: thin, the reader does the SQL and the words, ToolContext the rest.</summary>
[McpServerToolType]
public static class CellTools
{
    private const string DeviceParam =
        "Only this device, by its name as get_cell_overview shows it (e.g. a machine or vehicle). Leave empty for every device.";

    [McpServerTool(Name = "get_cell_overview", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("What is every device in the cell doing right now? Start here. For each device (machines, vehicles, " +
                 "work-set stations, …): connected, emergency stop, status (the cell's own down/idle/busy/manual/error), " +
                 "recoveryRequest (true: the device waits for an operator to recover it), mode, current action, the " +
                 "pallets on it with each position's state (\"next …\" spots are pallets reserved to come there next), " +
                 "how many alarms and warnings are open, and when it last finished something (lastActivity). Machines " +
                 "also show toolStatus (ready, lacking tools, tool alarm, …), cncRun, operatorCall, runJob and program " +
                 "(main and executing O-number); the vehicle shows its transport command (in action), transportFrom/transportTo and the " +
                 "pallets on its buffers. historyDays is how many days the cell keeps alarm and result history (null " +
                 "when it doesn't say). lastActivity looks back 30 days: null means nothing finished in that time, not " +
                 "that it never did. Status values carry no timestamp, so compare lastActivity with serverTime to " +
                 "notice a device that is connected but has done nothing for hours. notAvailable names fields this " +
                 "cell's software version (or this login) doesn't provide.")]
    public static Task<string> GetCellOverview(ToolContext context, CancellationToken ct) =>
        context.Database("get_cell_overview", (r, t) => r.GetOverviewAsync(t), ct);

    [McpServerTool(Name = "get_alarms", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Which alarms are open now, or happened recently? PMC and CNC alarms merged, newest first, each with " +
                 "device, number, level (alarm or warning), message, raised and reset time, and minutes open (to reset, " +
                 "or to serverTime when still open; reset is null while open). Defaults: the last 24 hours, every device, " +
                 "warnings included. Use openOnly=true for \"what is wrong right now\" (every alarm still open, however " +
                 "old). History is limited: CNC alarms keep only about the last 200, PMC about three months.")]
    public static Task<string> GetAlarms(
        ToolContext context,
        [Description("How many hours back to look, from serverTime. Default 24.")] int hours = 24,
        [Description(DeviceParam)] string? device = null,
        [Description("true: only alarms still open (ignores hours). Default false.")] bool openOnly = false,
        [Description("false: leave out warnings, alarms only. Default true.")] bool includeWarnings = true,
        CancellationToken ct = default) =>
        context.Database("get_alarms", (r, t) => r.GetAlarmsAsync(hours, device, openOnly, includeWarnings, t), ct);

    [McpServerTool(Name = "get_alarm_summary", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Which alarms keep coming back? Alarms of the last days grouped by device, source (PMC/CNC) and alarm " +
                 "number, most frequent first: count, total minutes open, first and last seen. Default: the last 7 " +
                 "days (today counts as one, and is only part of a day), every device. Use it for recurring problems; use get_alarms for the individual occurrences. " +
                 "CNC alarm history keeps only about the last 200 alarms, so older CNC counts may be incomplete.")]
    public static Task<string> GetAlarmSummary(
        ToolContext context,
        [Description("How many days back to look (today counts as one). Default 7.")] int days = 7,
        [Description(DeviceParam)] string? device = null,
        CancellationToken ct = default) =>
        context.Database("get_alarm_summary", (r, t) => r.GetAlarmSummaryAsync(days, device, t), ct);

    [McpServerTool(Name = "get_utilization", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("How has each device's time been spent? One row per device and day, newest first: hours down, idle, " +
                 "busy, manual and in error, busy percent, and OEE percent where the cell keeps it. Default: the last 7 " +
                 "days (today counts as one, and is only part of a day), every device.")]
    public static Task<string> GetUtilization(
        ToolContext context,
        [Description("How many days back (today counts as one). Default 7.")] int days = 7,
        [Description(DeviceParam)] string? device = null,
        CancellationToken ct = default) =>
        context.Database("get_utilization", (r, t) => r.GetUtilizationAsync(days, device, t), ct);

    [McpServerTool(Name = "get_recent_activity", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("What has the cell actually done lately, and were there gaps? Machining, work-set station and " +
                 "vehicle transport results, newest first: kind, device, start and finish, pallet, detail (program or " +
                 "operation) and result. Default: started in the last 8 hours, every device. A long gap between items " +
                 "means nothing was finished in that time.")]
    public static Task<string> GetRecentActivity(
        ToolContext context,
        [Description("How many hours back, from serverTime. Default 8.")] int hours = 8,
        [Description(DeviceParam)] string? device = null,
        CancellationToken ct = default) =>
        context.Database("get_recent_activity", (r, t) => r.GetRecentActivityAsync(hours, device, t), ct);

    [McpServerTool(Name = "get_orders", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Is there work queued in the cell, and what has finished? Orders, latest due first: name, part, " +
                 "status, quantity, suspended, due and completed time. Default: active orders only (not complete, " +
                 "cancelled or undefined); activeOnly=false includes finished ones too.")]
    public static Task<string> GetOrders(
        ToolContext context,
        [Description("true: only orders still to do. false: every order. Default true.")] bool activeOnly = true,
        CancellationToken ct = default) =>
        context.Database("get_orders", (r, t) => r.GetOrdersAsync(activeOnly, t), ct);

    [McpServerTool(Name = "get_pallets", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Where is each pallet and what state is it in? Every pallet: number, name, status (idle, machining, " +
                 "waiting for machining, machining suspended, transport, damaged, …), workStatus (not completed, normal, " +
                 "abnormal, cancel; none when there is no work on it), location (a device, stocker shelf or station) and " +
                 "the fixtures on it. Values whose meaning is not known are shown as \"code N\".")]
    public static Task<string> GetPallets(ToolContext context, CancellationToken ct) =>
        context.Database("get_pallets", (r, t) => r.GetPalletsAsync(t), ct);

    private const string ToolParam = "Only this tool: an ITN (one physical tool) or FTN (a tool type) number. Leave empty for every tool.";

    private const string DaysParam = "How many days back to look, from serverTime. Default 7.";

    [McpServerTool(Name = "get_tools", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Which tools need attention, and where is a tool? summary: every device's tool count by status " +
                 "(ready, life warning, offset error, alarm, unknown), always over all tools. tools: the tools the filter " +
                 "picks, most severe first: itn (the physical tool), ftn (its tool type), name (the FTN comment), kind, " +
                 "location (machine and pot, or the tool crib / presetter), status (derived as the System Console " +
                 "shows it: an alarm bit set means alarm even if the stored status still says ready), flags (alarm tool, prohibited, " +
                 "absent), movingTo (during a tool in/out), and per cutter: status, alarms, life and measured length/" +
                 "diameter (in lengthUnit, mm or inch as the cell keeps them). life: unit (min, count, m or inch), limit, " +
                 "warnAtUsed, used, remaining " +
                 "and usedPercent, already corrected for whether this cell counts life up or down (lifeDirectionAssumed: " +
                 "true means the cell doesn't say and up was assumed); note \"not life-managed\" means the tool has no " +
                 "life limit, so no percentage. Filters: problems (default: status life warning, offset error or alarm, " +
                 "an alarm/absent/prohibited flag, or 90 % or more of its life used), near_end (80 % used or past its " +
                 "warning), alarm, absent, all. Giving a tool number without a filter shows that tool whatever its state. " +
                 "notAvailable says when this cell lacks what a filter needs, so it couldn't be applied.")]
    public static Task<string> GetTools(
        ToolContext context,
        [Description(DeviceParam)] string? device = null,
        [Description("problems (default), near_end, alarm, absent or all.")] string? filter = null,
        [Description(ToolParam)] int? tool = null,
        CancellationToken ct = default) =>
        context.Database("get_tools", (r, t) => r.GetToolsAsync(device, filter, tool, t), ct);

    [McpServerTool(Name = "get_tool_changes", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("What changed in the tool data? Every change the cell recorded (presetter measurements, tool data " +
                 "imports, manual edits, alarms set or cleared after machining), newest first: time, device (where the " +
                 "tool was), itn, ftn, user, and only the values that changed (lengths in lengthUnit), e.g. " +
                 "\"length 101.234 → 101.250 mm\", " +
                 "\"status ready → alarm\", \"tool life alarm on\", \"life used 75.0 → 0.0 min (reset)\". By default the " +
                 "routine life-count updates after each use are left out (lifeUpdatesLeftOut); includeLifeUpdates=true " +
                 "shows them too. Default: the last 7 days, every device and tool.")]
    public static Task<string> GetToolChanges(
        ToolContext context,
        [Description(DaysParam)] int days = 7,
        [Description(DeviceParam)] string? device = null,
        [Description(ToolParam)] int? tool = null,
        [Description("true: also the routine life-count updates after each use (many rows). Default false.")] bool includeLifeUpdates = false,
        CancellationToken ct = default) =>
        context.Database("get_tool_changes", (r, t) => r.GetToolChangesAsync(days, device, tool, includeLifeUpdates, t), ct);

    [McpServerTool(Name = "get_tool_alarms", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Which tools alarmed lately (worn out, broken, …)? Newest first: time, device, pot, itn, ftn, alarms " +
                 "(tool life alarm, broken tool, spindle load, …), the tool's life then, and its length/diameter (in " +
                 "lengthUnit). source " +
                 "says where they come from: the cell's tool alarm history, or, when this login can't read it, the tool " +
                 "data changes in which an alarm bit turned on (no pot: see notAvailable). Default: the last 7 days, " +
                 "every device and tool.")]
    public static Task<string> GetToolAlarms(
        ToolContext context,
        [Description(DaysParam)] int days = 7,
        [Description(DeviceParam)] string? device = null,
        [Description(ToolParam)] int? tool = null,
        CancellationToken ct = default) =>
        context.Database("get_tool_alarms", (r, t) => r.GetToolAlarmsAsync(days, device, tool, t), ct);

    [McpServerTool(Name = "get_tool_movements", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Which tools were put into or taken out of the machines? Newest first: time, action (in: into a " +
                 "machine, out: out of one), itn, ftn, name, from and to (device and pot), the tool's status and " +
                 "usedPercent at the time, user and the reason given for taking it out. The cell logs only moves into " +
                 "or out of a machine, not tool crib ↔ presetter. Default: the last 7 days, every device (either end) " +
                 "and tool.")]
    public static Task<string> GetToolMovements(
        ToolContext context,
        [Description(DaysParam)] int days = 7,
        [Description(DeviceParam)] string? device = null,
        [Description(ToolParam)] int? tool = null,
        CancellationToken ct = default) =>
        context.Database("get_tool_movements", (r, t) => r.GetToolMovementsAsync(days, device, tool, t), ct);
}
