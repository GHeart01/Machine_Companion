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
                 "pallets on its buffers. historyDays is how many days the cell keeps PMC alarms and result history " +
                 "(null when it doesn't say; CNC alarms are limited to about the last 200 regardless). lastActivity " +
                 "looks back 30 days: null means nothing finished in that time, not " +
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
                 "status, quantity (required), completedQty (good parts out of the last process), scrapQty, " +
                 "remainingQty (not started yet), suspended, due and completed time. It lists the whole queue, so fields " +
                 "at their default are left out: suspended only when true, due and completed only when set, a quantity of " +
                 "0 (notAvailable says when the quantities can't be read). Default: active orders only (not " +
                 "complete, cancelled or undefined); activeOnly=false includes finished ones too. get_production adds " +
                 "parts in process and the counts per day.")]
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

    private const string ToolParam =
        "Only this tool number. It is matched against both ITNs (physical tools) and FTNs (tool types), so one number can " +
        "match a tool and a tool type at once; each row carries both itn and ftn. Leave empty for every tool.";

    private const string DaysParam = "How many days back to look, from serverTime. Default 7.";

    private const string ChangeDaysParam = "How many days back to look, from serverTime. Default 3.";

    [McpServerTool(Name = "get_tools", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Which tools need attention, and where is a tool? summary: each device's tool count by status " +
                 "(ready, life warning, offset error, alarm, unknown) over all its tools: the device filter applies, the " +
                 "tool filter and tool number don't. tools: the tools the filter " +
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
                 "routine updates are left out (lifeUpdatesLeftOut): the life-count ticks after each use, and rows in " +
                 "which a tool only stepped to life warning (routine wear), counted in lifeWarningTransitions; " +
                 "includeLifeUpdates=true lists both. notAvailable names what this cell's log can't tell apart. " +
                 "Default: the last 3 days (a busy cell logs dozens of changes a day), every device and tool.")]
    public static Task<string> GetToolChanges(
        ToolContext context,
        [Description(ChangeDaysParam)] int days = 3,
        [Description(DeviceParam)] string? device = null,
        [Description(ToolParam)] int? tool = null,
        [Description("true: also the routine updates: life-count ticks after each use and steps to life warning (many rows). Default false.")] bool includeLifeUpdates = false,
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

    [McpServerTool(Name = "get_part_routing", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("How is a part made in the cell, and are its tools there? Finds the part by its exact name " +
                 "(case-insensitive), else the one part whose name contains the text; when several match, it lists " +
                 "them as candidates (name, revision, process count) and shows no routing, so ask again with the exact " +
                 "name. Parts split per operation (…-OP1, …-OP2) are separate parts. otherMatches names other parts " +
                 "containing the text next to an exact match. The routing: processes in order (number, name, status, " +
                 "maxFixtures, the fixtures that can hold it as \"pallet N fixture M\"), their operations (parts per " +
                 "clamp) and jobs in order: type (work setting, machining, …), workSet (load/unload) and infoFile " +
                 "(work instructions), program (NC program), minutes (planned time), operatorCall, feasibleDevices. " +
                 "With includeTools (default), each machining job lists the tool types (ftn, name) its main program " +
                 "needs, how many (needed), the life one run uses (cutting), how many usable tools of that type " +
                 "(status ready or life warning) sit on each feasible machine (usable) and missingOn: the machines " +
                 "with too few. That check is approximate (toolCheck): main program only, subprograms not followed, " +
                 "remaining life not checked. notAvailable names what this cell or login can't show.")]
    public static Task<string> GetPartRouting(
        ToolContext context,
        [Description("The part name, or part of it.")] string part,
        [Description("Only the part with this revision (for names that repeat across revisions). Leave empty for any.")] string? revision = null,
        [Description("false: leave out the tools each program needs. Default true.")] bool includeTools = true,
        CancellationToken ct = default) =>
        context.Database("get_part_routing", (r, t) => r.GetPartRoutingAsync(part, revision, includeTools, t), ct);

    [McpServerTool(Name = "get_production", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("How many parts has the cell made, and how far along is each order? Per order, counted the way the " +
                 "cell's own order screen counts: quantity (required), completedQty (good parts out of the last " +
                 "process), scrapQty (any process), remainingQty (not started), inProcessQty (started, not finished: " +
                 "clampedQty on fixtures now plus parts waiting between processes), status, completedInWindow and " +
                 "scrapInWindow; daily: per production day (date, completed, scrap; days with none left out) the totals " +
                 "over the listed orders, or each order's own days when an order or part is named (dailyCapped: the " +
                 "oldest days didn't fit the row limit). Orders " +
                 "that made the most in the window come first, then latest due. Default: the last 7 days (today counts " +
                 "as one) and only the active orders something happened to in them (output, scrap, or parts on a " +
                 "fixture that isn't suspended or on hold); otherActiveOrders counts the rest, includeIdle=true lists " +
                 "them too. Naming an order or a part (exact name, else every name containing the text) shows its " +
                 "orders whatever their status or activity. The cell keeps daily counts for about three months.")]
    public static Task<string> GetProduction(
        ToolContext context,
        [Description("How many days of daily counts, from today back (today counts as one). Default 7.")] int days = 7,
        [Description("Only this order: its name, or part of it. Leave empty for every active order.")] string? order = null,
        [Description("Only the orders of this part: its name, or part of it. Leave empty for every part.")] string? part = null,
        [Description("true: also list the active orders nothing happened to in the window. Default false.")] bool includeIdle = false,
        CancellationToken ct = default) =>
        context.Database("get_production", (r, t) => r.GetProductionAsync(days, order, part, includeIdle, t), ct);

    [McpServerTool(Name = "get_attention", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("What is waiting on a person right now? One list, most severe first: devices with a recovery " +
                 "request, error, emergency stop, disconnected or a machine's operator call; instructions set at a " +
                 "work-set station (clamp, unclamp, …); manual transports not finished; " +
                 "fixtures damaged, pre-machining suspended (with the reason) or past their reclamp time; tools in " +
                 "alarm or with an offset error per machine; fixtures lacking tools for their next machining; stations " +
                 "or the vehicle over their maintenance cycle count. Fixtures that are only suspended or held at the " +
                 "work-set station (operators park fixtures this way) are counted in parked: how many, how many of " +
                 "them lack tools, per place (where their pallets are) and condition; includeParked=true lists them " +
                 "last as items with the reason the operator typed (detail). Each item: kind, what, device or " +
                 "pallet/fixture, order and process, detail, since (only when the cell records it). counts: how many " +
                 "of each condition the cell has, parked ones included, even past the row cap. " +
                 "machineToolStatus: each machine's tool status, as context only (\"lacking tools\" there usually means " +
                 "a later job waits for a tool; the fixtures concerned are the items). notAvailable names sections " +
                 "this cell or login can't show.")]
    public static Task<string> GetAttention(
        ToolContext context,
        [Description("true: list the parked fixtures (only suspended or on hold) one by one instead of only counting them. Default false.")]
        bool includeParked = false,
        CancellationToken ct = default) =>
        context.Database("get_attention", (r, t) => r.GetAttentionAsync(includeParked, t), ct);
}
