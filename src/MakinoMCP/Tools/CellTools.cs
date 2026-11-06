using System.ComponentModel;
using ModelContextProtocol.Server;

namespace MakinoMCP.Tools;

/// <summary>The cell database tools: thin, the reader does the SQL and the words, ToolContext the rest.</summary>
[McpServerToolType]
public static partial class CellTools
{
    private const string DeviceParam =
        "Only this device, by its name as get_cell_overview shows it (e.g. a machine or vehicle). Leave empty for every device.";

    [McpServerTool(Name = "get_cell_overview", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("What is every device in the cell doing right now? Start here. For each device (machines, vehicles, " +
                 "work-set stations, …): connected, emergency stop and mode (both left out while disconnected, when the " +
                 "cell's values are stale), status (the cell's own down/idle/busy/manual/error: a machine is busy only " +
                 "with a running job, in system mode, the CNC in memory, remote or DNC and no operator call, so a machine " +
                 "stopped at an operator call or closed reads idle; manual also covers a manual transport on its table or " +
                 "front and a CNC switched to MDI or edit mid-job; a work-set station is busy while it shows an " +
                 "instruction (waiting on or being worked by an operator) and manual when out of system mode, the vehicle " +
                 "likewise; error comes from an open PMC alarm, emergency stop or " +
                 "recovery, never from a CNC alarm, so check the open alarm count too), " +
                 "recoveryRequest (true: the device waits for an operator to recover it), open (only shown when false: " +
                 "the device is closed and gets no automatic work although it reads idle; devices come up closed after " +
                 "a software start, Stop System or Work Data Reset), transportCancelled (only on cells that use transport " +
                 "cancel: no pallets are sent to it; not the same as the console's load disable, which MakinoMCP can't " +
                 "see), current action, since and plannedFinish of the " +
                 "machining, station instruction or transport going on now, the " +
                 "pallets on it with each position's state (\"next …\" spots are pallets reserved to come there next; a " +
                 "position with palletNumber null holds no pallet but still has a state, possibly a position status " +
                 "error), how many alarms and warnings are open, and when it last finished something (lastActivity). Machines " +
                 "also show systemMode (system or standalone), toolStatus (ready, lacking tools, tool alarm, …), cncRun, " +
                 "operatorCall, runJob, program " +
                 "(main and executing O-number, the running job's program name, and mainIs when the main O-number is " +
                 "the cell's own dataserver or work offset program), waitingFor (an extra tool pallet it needs), " +
                 "ncMemory (free KB and program slots as the cell last recorded them; it matters only when " +
                 "ncProgramDownload is \"only when needed\"), and nextCmmIn/nextGageIn (parts until the next per-machine " +
                 "inspection); work-set stations show the instruction while " +
                 "busy and any disabledOperations; the vehicle shows its transport command (in action), transportFrom/transportTo and the " +
                 "pallets on its buffers (through the vendor's vehicle view, the normal path for the stock reporting " +
                 "login, its mode is only system or not). powerStatus and powerOffResult appear during a scheduled power-off. Cell level: " +
                 "softwareVersion, duty (on or off duty; dutySchedule when it follows the weekly schedule), powerOff when one is " +
                 "scheduled, recoveryPolicy: what the cell does after each kind of abnormal finish (raise a " +
                 "recovery request, unless the machine is in auto-unloading mode; finish normally; finish abnormally; " +
                 "or always wait), per machine when the cell sets it per device (so an abnormal finish with tool-monitor " +
                 "alarms that scrapped the part and moved on is often the designed behaviour, day or night), " +
                 "schedulingRules (the order the cell sends waiting pallets in: transportOrder, first term decides most, " +
                 "destinationOrder, notUsed: default terms this cell left out, such as order due date), " +
                 "ncProgramDownload and cellFiles (the ADE drop and error folders, monitoring output, tool data export " +
                 "folders: paths only, add one as a shared folder to read it). Retention: historyDays is how many days the cell keeps PMC alarms and result history " +
                 "(the vendor's default of 90 with historyDaysAssumed when the cell doesn't say; CNC alarms are " +
                 "limited to about the last 200 regardless); presetHistoryMonths and toolMoveHistoryMonths for tool data " +
                 "changes and tool moves (the purge's fallback of 12 and 3 flagged *Assumed when the cell doesn't say); " +
                 "day summaries go back about 12 months and daily order counts about 3. The cell purges as rows are added, " +
                 "per device for alarms and results, so these are minimums: an idle device keeps older rows. lastActivity " +
                 "looks back 30 days: null means nothing finished in that time, not " +
                 "that it never did. Status values other than since carry no timestamp, so compare lastActivity with serverTime to " +
                 "notice a device that is connected but has done nothing for hours. notAvailable names fields this " +
                 "cell's software version (or this login) doesn't provide.")]
    public static Task<string> GetCellOverview(ToolContext context, CancellationToken ct) =>
        context.Database("get_cell_overview", (r, t) => r.GetOverviewAsync(t), ct);

    [McpServerTool(Name = "get_alarms", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Which alarms are open now, or happened recently? PMC and CNC alarms merged, newest first, each with " +
                 "device, number, level (alarm or warning), message, raised and reset time, and minutes open (to reset, " +
                 "or to serverTime when still open; reset is null while open). Defaults: the last 24 hours, every device, " +
                 "warnings included. Use openOnly=true for \"what is wrong right now\" (every alarm still open, however " +
                 "old). History is limited: CNC alarms keep only about the last 200, PMC about three months. What blocks " +
                 "work: a machine gets no pallets while it has an open PMC alarm that isn't a warning (its warnings and " +
                 "CNC alarms don't stop transport); a work-set station is blocked by any open PMC or CNC alarm. " +
                 "helpFiles: for each device with a PMC alarm in the list, the alarm help files the cell uses (paths on " +
                 "the cell PC). A machine's .ini file has a section per alarm number ([10001]: the number as 5 digits, 6 " +
                 "on a Pro5 control) with the title, cause and recovery; alarms 990000 and up use the second file when " +
                 "there is one. If the cell's Message folder is a shared folder, look the number up there with " +
                 "search_files or read_file. The vehicle's and stations' .mdb files are databases, not readable as text.")]
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
                 "CNC alarm history keeps only about the last 200 alarms, so older CNC counts may be incomplete. " +
                 "shiftStart/shiftEnd/weekdays count only alarms raised in that shift (total minutes still run to each " +
                 "alarm's reset).")]
    public static Task<string> GetAlarmSummary(
        ToolContext context,
        [Description("How many days back to look (today counts as one). Default 7.")] int days = 7,
        [Description(DeviceParam)] string? device = null,
        [Description(ShiftStartParam)] string? shiftStart = null,
        [Description(ShiftEndParam)] string? shiftEnd = null,
        [Description(WeekdaysParam)] string? weekdays = null,
        CancellationToken ct = default) =>
        context.Database("get_alarm_summary", (r, t) =>
            r.GetAlarmSummaryAsync(days, device, Core.Cell.ShiftFilter.Parse(shiftStart, shiftEnd, weekdays), t), ct);

    private const string ShiftStartParam =
        "Only this shift: its start as HH:mm on the cell's clock, with shiftEnd (e.g. 06:00). Leave empty for whole days.";

    private const string ShiftEndParam =
        "The shift's end as HH:mm, with shiftStart (e.g. 14:30; 22:00 to 06:00 is a night shift, counted on each date " +
        "as the hours before 06:00 and after 22:00).";

    private const string WeekdaysParam = "Only these weekdays, e.g. mon-fri or sat,sun. Leave empty for every day.";

    [McpServerTool(Name = "get_utilization", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("How has each device's time been spent? One row per device and day, newest first: hours down, idle, " +
                 "busy, manual and in error and busyPercent (the share of time in the dashboard's busy status), OEE " +
                 "with its availability, performance and quality (percent, where the cell keeps them), and for " +
                 "machines spindleHours and spindlePercent (spindle running time, as a share of the day; of the day so " +
                 "far for today). resultPercent, results and avgMinutes are the console's Utilization report, rebuilt " +
                 "from the result records: the share of the day covered by machining (machineResultTime says how a " +
                 "machine's time is counted: from prep start to finish, alarms and operator calls included, or the net " +
                 "machining time), station visits (from the instruction to Finish, so the operator's wait is included; " +
                 "a visit counts once however many fixtures it had; avgMinutes is time per visit, not labour) or " +
                 "transports, how many results touch the day and their average minutes in it. It usually reads higher " +
                 "than busyPercent. The day summaries go back about 12 months, so older questions can be answered here; " +
                 "the result figures only as far as the cell keeps results (historyDays, see notAvailable). With " +
                 "shiftStart/shiftEnd the result figures cover only the shift's hours; the status hours, spindle and " +
                 "OEE stay whole-day figures (the cell keeps no time of day for them), and weekdays filter every row. " +
                 "Default: the last 7 days (today counts as one, and is only part of a day), every device; long ranges " +
                 "hit the row cap, so pick a device.")]
    public static Task<string> GetUtilization(
        ToolContext context,
        [Description("How many days back (today counts as one; up to about 12 months are kept). Default 7.")] int days = 7,
        [Description(DeviceParam)] string? device = null,
        [Description(ShiftStartParam)] string? shiftStart = null,
        [Description(ShiftEndParam)] string? shiftEnd = null,
        [Description(WeekdaysParam)] string? weekdays = null,
        CancellationToken ct = default) =>
        context.Database("get_utilization", (r, t) =>
            r.GetUtilizationAsync(days, device, Core.Cell.ShiftFilter.Parse(shiftStart, shiftEnd, weekdays), t), ct);

    [McpServerTool(Name = "get_recent_activity", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("What has the cell actually done lately, and were there gaps? Machining, work-set station and " +
                 "vehicle transport results, newest first: kind, device, start and finish, pallet, detail (program or " +
                 "operation) and result (a machining result says \"abnormal finish\" when the cycle ended abnormally; " +
                 "tool life or spare tool selected alone is information, not a fault). Machining items add, when known, " +
                 "prepMinutes (preparation before the cycle), spindleMinutes (spindle running), netMinutes (the cycle " +
                 "without its stops, where the cell measures it) and plannedMinutes (the job's planned time). Work-set " +
                 "items say what the station asked for (clamp, unclamp and clamp, …) and, in the result, what was done " +
                 "and the part counts (unclamped good, unclamped scrap, to external process, clamped; re-machine when " +
                 "parts go back for machining); note says when the cell's probe count can lower a fixture's parts. What " +
                 "the times cover: a work-set item runs from the instruction being issued (pallet at the station) to " +
                 "Finish, so it includes the wait for the operator, and is written only at Finish (one in progress isn't " +
                 "listed); a transport runs from the pickup command to set-down; a machining item from the cycle start " +
                 "to its end. Machining run by hand at the machine isn't logged: a gap here plus manual hours in " +
                 "get_utilization means manual work. Default: started in the last 8 hours, every device. A long gap " +
                 "between items means nothing was finished in that time.")]
    public static Task<string> GetRecentActivity(
        ToolContext context,
        [Description("How many hours back, from serverTime. Default 8.")] int hours = 8,
        [Description(DeviceParam)] string? device = null,
        CancellationToken ct = default) =>
        context.Database("get_recent_activity", (r, t) => r.GetRecentActivityAsync(hours, device, t), ct);

    [McpServerTool(Name = "get_orders", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Is there work queued in the cell, and what has finished? Orders, latest due first: name, part, " +
                 "status (the stored order status; \"suspended\" is an order suspended as a whole by an operator), " +
                 "dashboardStatus (only when it differs: running or waiting for material, from the cell's dashboard or " +
                 "worked out as dashboardStatusFrom says), quantity (required), completedQty (good parts out of the last " +
                 "process), scrapQty, remainingQty (not started yet), priority, comment, start (only when still to come " +
                 "or the order is inactive: an order activates at midnight of its start date), waitingFor (what a " +
                 "waiting order needs: its start date, a quantity, material input, an operator's release, or a fixture to " +
                 "take its material), quickData (an order of a quick-data part), machiningSuspended (the order's " +
                 "machining suspend: its waiting fixtures aren't machined; only where the cell's setting makes it count), " +
                 "due and completed time. The due date is a scheduling input, not a deadline the cell enforces: pallets " +
                 "go by operator priority, transport type and pallet priority first, then due date, and order priority " +
                 "only breaks due-date ties. completeIncludesScrap: true means the cell calls an order complete once good " +
                 "plus scrap parts reach its quantity. It lists the whole queue, so fields " +
                 "at their default are left out: machiningSuspended only when true, due and completed only when set, a quantity of " +
                 "0 (notAvailable says when the quantities can't be read). Default: active orders only (not " +
                 "complete, cancelled or undefined); activeOnly=false includes finished ones too. With finished orders " +
                 "or the archive, autoDelete says when the cell deletes orders by itself, so a missing order may simply " +
                 "have been deleted. includeArchive=true adds archive: finished and deleted orders the cell recorded in " +
                 "the last archiveDays (name, part, quantities, completed, deleted, recorded; archiveCapped when the row " +
                 "limit cut it). get_production adds parts in process and the counts per day.")]
    public static Task<string> GetOrders(
        ToolContext context,
        [Description("true: only orders still to do. false: every order. Default true.")] bool activeOnly = true,
        [Description("true: also list finished and deleted orders from the cell's order archive. Default false.")] bool includeArchive = false,
        [Description("How many days of the archive, from today back (today counts as one). Default 30.")] int archiveDays = 30,
        CancellationToken ct = default) =>
        context.Database("get_orders", (r, t) => r.GetOrdersAsync(activeOnly, includeArchive, archiveDays, t), ct);

    [McpServerTool(Name = "get_pallets", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Where is each pallet and what state is it in? Every pallet: number, name, status (idle, machining, " +
                 "waiting for machining, machining suspended, work-set hold, damaged, …; idle also covers a pallet whose " +
                 "work is finished, and the transport states mean a manual or special transport is set up, not that the " +
                 "vehicle is carrying it now). The pallet's status is its highest-priority fixture state, so one fixture " +
                 "can be suspended under \"waiting for machining\". workStatus is what waits on the pallet to be " +
                 "unloaded, not the pallet's health: not completed, normal result (good parts), scrap or abnormal work " +
                 "set, cancelled (order or process: the parts still go to a station to be unloaded, which is not a " +
                 "fault); none when there is no work on it. location: a device and position (table, front, back, buffer), " +
                 "a stocker shelf, or out of system for a pallet taken out. type (only when not standard: material " +
                 "pallets have no dashboard status), priority and operatorPriority (only when not 0; the cell also raises " +
                 "operator priority itself for manual and synchronous transports, so it doesn't prove an operator " +
                 "expedited the pallet), notFeasibleFor (machines and stations the pallet may not go to; left out when " +
                 "it may go to all). fixtures: one short line per fixture: number and its name if it has one (nameless " +
                 "fixtures in a row with the same states share a line, \"fixtures 1-4: …\"), then empty or its states in the console's order " +
                 "(damaged, machining suspended, work-set hold, pre-machining suspended, operator call, off-duty process, " +
                 "cleaning or deburring skipped), MakinoMCP's own additions (reclamp timeout, lacking tools, clamp-wait " +
                 "hold disabled: a fixture exempt from the cell's wait-for-clamp hold; where the cell uses that hold, a " +
                 "pallet whose remaining fixtures wait for unclamping with no new material may wait at home on purpose), " +
                 "and scrap (with how the cycle ended) or cancelled. Give a pallet number, or includeFixtures=true, for " +
                 "the fixtures as objects instead (fixtureDetails: label (its name; for a nameless fixture of one " +
                 "process, part | revision | process N; else its comment), processes, empty, order, part, process, job, " +
                 "jobStatus, states, operatorCall, workStatus). Fixture work statuses seating error and unclamp NG " +
                 "aren't rolled up into the pallet's workStatus, which then reads not completed. Values whose meaning " +
                 "is not known are shown as \"code N\"; notAvailable names what this cell or login can't show.")]
    public static Task<string> GetPallets(
        ToolContext context,
        [Description("Only this pallet number, with its fixtures in full. Leave empty for every pallet.")] int? pallet = null,
        [Description("true: every pallet's fixtures as objects instead of short lines (much longer). Default false.")] bool includeFixtures = false,
        CancellationToken ct = default) =>
        context.Database("get_pallets", (r, t) => r.GetPalletsAsync(pallet, includeFixtures, t), ct);

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
                 "type (the FTN's free-text type), sizeClass (small/middle/large/…, plus Borematic, APC prohibited, " +
                 "irregular shape), ptn (the T number it answers to, when not 0 and not its FTN), " +
                 "location (machine and pot, or the tool crib / presetter), status (derived as the System Console " +
                 "shows it: an alarm bit set means alarm even if the stored status still says ready; the console's " +
                 "colours: red alarm, yellow life warning, light grey offset error, white ready, orange unknown), flags " +
                 "(alarm tool; prohibited: the machine won't select it for its T number; absent: reported missing from " +
                 "its pot, both from the machine's data or a tool-ID read; spare (no T number), on cells that give T " +
                 "numbers at tool-in; not used since tool-in, only on cells that inspect first-used tools), typeFlags " +
                 "(settings on the tool type: operator call tool, first-use check, tool life alarm prohibit), movingTo " +
                 "(during a tool in/out), and per cutter: status, alarms (BT2 alarm: the tool measured " +
                 "too long, e.g. pulled out of its holder, not a break), life, measured length/" +
                 "diameter (in lengthUnit, mm or inch as the cell keeps them; diameterIsRadius: true means the cell " +
                 "stores radii in its diameter values) and, in offset error, offsetError: which of length + wear or " +
                 "diameter + wear is outside the tool type's limits now, or that both are within them (the stored " +
                 "status may predate a limit change). life: unit (min, count, m or inch), limit, " +
                 "warnAtUsed, used, remaining " +
                 "and usedPercent, already corrected for whether this cell counts life up or down (lifeDirectionAssumed: " +
                 "true means the cell doesn't say and up was assumed); note \"not life-managed\" means the tool has no " +
                 "life limit, so no percentage. A tool-life alarm doesn't stop the machine by itself: the tool may finish " +
                 "its program, per the machine's tool-life handling. What this is and isn't: location is where the cell's " +
                 "tool data puts the tool (the physical tool can differ until a tool change or a tool-ID read); a " +
                 "machine tool's pot is its magazine home pot, and the database doesn't record which tool is in the " +
                 "spindle (lastUsed and lastUsedProgram are the nearest hint); machine tool data is as of the last " +
                 "upload from the machine (with the machine's UpdateAllTool setting off, only the spindle tool is re-read " +
                 "when a program finishes). toolManagementOff: the cell doesn't manage tools, and the console then shows " +
                 "offset error and unknown as ready. Filters: problems (default: status life warning, offset error or alarm, " +
                 "an alarm/absent/prohibited flag, or 90 % or more of its life used), near_end (80 % used or past its " +
                 "warning), alarm, absent, all, and unused (needs a machine as device: the tools on it nothing there " +
                 "needs, the console's unused-tools view, strongest reason first, with unusedReasons: no program uses " +
                 "this tool type; only programs not feasible here (no machining job feasible on the machine, or " +
                 "processing there now, runs them); no active order needs it (inactiveOrderStatuses lists the order " +
                 "statuses that don't count); spare (no T number). Its cutters are left out; ask for one tool by number " +
                 "for them). Every filter is capped at the row limit (capped: true when more tools matched). Giving a " +
                 "tool number without a filter shows that tool whatever its state. " +
                 "notAvailable says when this cell lacks what a filter needs, so it couldn't be applied, or which " +
                 "unused reasons it can't check.")]
    public static Task<string> GetTools(
        ToolContext context,
        [Description(DeviceParam)] string? device = null,
        [Description("problems (default), near_end, alarm, absent, all, or unused (needs a machine as device).")] string? filter = null,
        [Description(ToolParam)] int? tool = null,
        CancellationToken ct = default) =>
        context.Database("get_tools", (r, t) => r.GetToolsAsync(device, filter, tool, t), ct);

    [McpServerTool(Name = "get_tool_changes", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("What changed in the tool data? Every change the cell recorded (presetter measurements, tool data " +
                 "imports, manual edits, alarms set or cleared after machining), newest first: time, device (where the " +
                 "tool was), itn, ftn, user, by (machine upload: the machine's own values read back after a tool " +
                 "change or machining finish, or accepted from it when comparing tool data; cell: presetter, tool-ID " +
                 "read, screen edit or import), and only the values that changed (lengths in lengthUnit; diameters are " +
                 "radii when diameterIsRadius), e.g. " +
                 "\"length 101.234 → 101.250 mm\", " +
                 "\"status ready → alarm\", \"tool life alarm on\", \"life used 75.0 → 0.0 min (reset)\". An alarm " +
                 "cleared with no life reset in the same row means the operator kept the remaining life. By default the " +
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
                 "(tool life alarm, BTS alarm: broken tool, BT2 alarm: tool measured too long (pulled out), SL alarm: " +
                 "spindle load, …), the tool's life then, and its length/diameter (in lengthUnit; radii when " +
                 "diameterIsRadius). source " +
                 "says where they come from: the cell's tool alarm history, or the tool data changes in which an alarm " +
                 "bit turned on, which is the normal source on most cells (the history table only exists from V3.30, " +
                 "and the vendor's reporting login can't read it) and the one the cell's own Tool Alarm History report " +
                 "reads; it has no pot (see notAvailable). Default: the last 7 days, every device and tool.")]
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
                 "usedPercent at the time, user and the reason given for taking it out. An exchange shows as an out " +
                 "then an in on the same machine and pot, the reason on the out row; probablyExchangedWith on an in " +
                 "names the tool taken out of that pot just before (a guess from the order of the moves; both can be " +
                 "logged only when the machine-side operation finishes). reasonAsked: when the console asks for a " +
                 "tool-out reason (never; only for tools not in alarm; always; or required for tools not in alarm and " +
                 "optional for alarmed ones). No reason is asked for a tool moved out of a tool crib location, and " +
                 "tool-outs made outside the console's screen may never prompt, so an empty reason isn't a missing " +
                 "one. The cell logs only moves into " +
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
                 "containing the text next to an exact match. Routings change under running production and the cell keeps " +
                 "no versions: updated (the part, its processes and operations) and programUpdated (any edit of the " +
                 "program file's data) say when they last changed. The routing: processes in order (number, name, status, " +
                 "maxFixtures: the most fixtures the cell gives this process at once, cell-wide, inUse: how many hold it " +
                 "now, and fixtures: the pallet places that can hold it, as \"pallet N fixture M: free\", \"…: this " +
                 "process\" or \"…: in use: PART process N\", with suspended, work-set hold, damaged, pre-machining " +
                 "suspended and the offset file the cell would send), their operations (clampQuantity: material pieces " +
                 "loaded per fixture; unclampPerClamp: parts out per piece) and jobs in order: type (work setting, " +
                 "machining, …), workSet (load/unload) and infoFile (work instructions), program (NC program), oNumber " +
                 "(the O-number the cell starts), programType, minutes (planned time; for work setting an " +
                 "operator-entered plan the station's expected finish uses, never measured), operatorCall (permanent: " +
                 "every cycle; one-shot: once per fixture, or the next pallet only where the cell is set so; a call can " +
                 "also come from the fixture's own setting), timeStudy (the planned minutes will be replaced by the next " +
                 "normal cycle's time), blockSkips (optional block skips set on the job), toolStudy (the program's tool " +
                 "study mode and when, where and on which pallet it was last studied), feasibleDevices ([] with a note " +
                 "when the job is assigned to none: the cell won't schedule it). " +
                 "With includeTools (default), each machining job lists the tool types (ftn, name) its main program " +
                 "needs, how many (needed; neededForType when the cell adds up several PTNs of one type), the life one run " +
                 "uses (cutting), how many usable tools of that type (status ready or life warning) sit on each feasible " +
                 "machine (usable; usableButFlagged: how many of those are prohibited or absent), missingOn: the " +
                 "machines with too few, elsewhere: usable tools of the type on other devices, and toolSet: per " +
                 "machine the console's tool set status (ready, lacking, life warning, alarm, offset error: any tool of " +
                 "a needed type there in that state; unknown). That check is approximate (toolCheck says how this " +
                 "cell's own lacking rule differs). blockers: why the part's processes may not be running, by the " +
                 "console's Process Diagnosis: reasons (no active order, a process not assigned to a fixture or with no " +
                 "pallet carrying one, no free fixture place, at its fixture limit, a job assigned to no machine, or no " +
                 "assigned machine or station that can take it: not ready, closed, transport cancelled, lacking tools, " +
                 "off-duty holds), orders (the part's active orders with status; otherOrders: the ones not listed), " +
                 "devices (each assigned machine's or station's own state) and notChecked (what the console keeps in " +
                 "memory). For one pallet's fixtures in detail use get_pallet_diagnosis. notAvailable names what this " +
                 "cell or login can't show.")]
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
                 "process; also those sent to an external process after the last process when " +
                 "completedIncludesLastOutProcess), scrapQty (any process), remainingQty (material put in for the first " +
                 "process, not started), inProcessQty (started, not finished: clampedQty on fixtures now plus parts " +
                 "waiting for a later process, including material put in there directly; the database can't tell those " +
                 "two apart; parts on suspended or held fixtures are already left out of active work), outProcessQty " +
                 "(parts away at an external process, in neither inProcessQty nor completedQty), status, " +
                 "completedInWindow and scrapInWindow; daily: per production day (date, completed, scrap; days with none " +
                 "left out) the totals over the listed orders, or each order's own days when an order or part is named " +
                 "(dailyCapped: the oldest days didn't fit the row limit). On cells that use CMM inspection, completed " +
                 "counts can lag. probePartCount: true means the machines' probe reports how many parts are really " +
                 "clamped and the cell lowers the clamped quantity to it, so fewer parts than a fixture's clamp quantity " +
                 "is by design. Orders " +
                 "that made the most in the window come first, then latest due. Default: the last 7 days (today counts " +
                 "as one) and only the active orders something happened to in them (output, scrap, or parts on a " +
                 "fixture that isn't suspended or on hold); otherActiveOrders counts the rest, includeIdle=true lists " +
                 "them too. Naming an order or a part (exact name, else every name containing the text) shows its " +
                 "orders whatever their status or activity. completeIncludesScrap: true means an order counts as " +
                 "complete once good plus scrap parts reach its quantity. autoDelete says when the cell deletes orders " +
                 "by itself. includeCycleTime=true adds machiningMinutes, workSetMinutes and machiningMinutesPerPart " +
                 "per order, an approximation (cycleTimeNote says how it is counted). The cell keeps daily counts for about three months.")]
    public static Task<string> GetProduction(
        ToolContext context,
        [Description("How many days of daily counts, from today back (today counts as one). Default 7.")] int days = 7,
        [Description("Only this order: its name, or part of it. Leave empty for every active order.")] string? order = null,
        [Description("Only the orders of this part: its name, or part of it. Leave empty for every part.")] string? part = null,
        [Description("true: also list the active orders nothing happened to in the window. Default false.")] bool includeIdle = false,
        [Description("true: add each order's machining and work-set minutes from the cell's results (approximate). Default false.")] bool includeCycleTime = false,
        CancellationToken ct = default) =>
        context.Database("get_production", (r, t) => r.GetProductionAsync(days, order, part, includeIdle, includeCycleTime, t), ct);

    [McpServerTool(Name = "get_pallet_diagnosis", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Why isn't this pallet's work moving? The console's Pallet Diagnosis for one pallet: for each fixture, " +
                 "what it holds (order, process), its current job (type, number, status) and the verdict, with each " +
                 "feasible machine's or station's own verdict. Per device the cell's checks run in the console's order " +
                 "and the first that fails is the verdict: not ready (disconnected, emergency stop, not in system mode, " +
                 "an open PMC alarm; any open alarm for a station), machining suspended (the fixture or, where the cell " +
                 "uses it, its order), pre-machining suspended, off-duty process while on duty, closed, transport " +
                 "cancelled, damaged, operator-call job held while off duty; at a station also work-set hold and " +
                 "skipped. Across devices, \"can run\" on any device wins. A running job reads \"processing now\"; " +
                 "cleaning, turning, deburring and measuring jobs aren't checked. notChecked names what the console also " +
                 "checks from its memory (system ready, the tool set on tool-resource cells, vehicle reach, CSG and " +
                 "measuring data), so \"can run\" doesn't rule those out. One pallet per call; get_pallets or " +
                 "get_attention give the numbers.")]
    public static Task<string> GetPalletDiagnosis(
        ToolContext context,
        [Description("The pallet number, as get_pallets shows it.")] int pallet,
        CancellationToken ct = default) =>
        context.Database("get_pallet_diagnosis", (r, t) => r.GetPalletDiagnosisAsync(pallet, t), ct);

    [McpServerTool(Name = "get_attention", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("What is waiting on a person right now? One list, most severe first: devices with a recovery " +
                 "request (detail: open alarms and what the device was doing; \"lacking-tool recovery request\" when the " +
                 "machine holds a pallet for missing tools), error, emergency stop, disconnected, a machine's operator " +
                 "call, a machine waiting for the operator to confirm 5-axis coupling or for a security login, a feed " +
                 "hold mid-cycle (the dashboard still reads busy), a station's help request or operation timeout; a " +
                 "possible position status error (a table with no pallet that still reads busy); instructions set at a " +
                 "work-set station (clamp, unclamp, …; detail: progress, an inspection confirmation it waits for, " +
                 "re-machining, a whole-pallet other/input/output operation merged with its manual transport; one that has " +
                 "stood long usually ends with the operator's Finish on the station, or on the console for a virtual " +
                 "station); manual " +
                 "transports not finished (kind, destination, and why: not picked up yet, waiting for the destination to " +
                 "be opened or closed as the cell requires, or who completes it); a machine waiting for an extra tool " +
                 "pallet that is outside the cell; " +
                 "fixtures damaged, pre-machining suspended (with the reason) or past their reclamp time; tools in " +
                 "alarm or with an offset error per machine; fixtures lacking tools (lackingToolScope: which of their " +
                 "jobs the cell checks; when the cell doesn't work this out, notAvailable says so, so no such items " +
                 "is then not an all-clear; missingTools: for those not parked, per program, the tool types short on " +
                 "the machines that may run it, by status, with the tool crib's count); pallets held because one fixture lacks tools (cells " +
                 "that machine a pallet only when every fixture has its tools); waiting work blocked: fixtures whose " +
                 "waiting job no feasible device can take now (closed, disconnected, emergency stop, not in system " +
                 "mode, an open PMC alarm, any open alarm at a station, skipped, transport cancelled), one item per job " +
                 "type and device set with fixtures, some pallets, and the devices with their reasons (get_pallet_diagnosis " +
                 "for one pallet); stations " +
                 "or the vehicle over their maintenance cycle count; closed or skipped devices, low in the list since " +
                 "closing is often deliberate (one \"all devices closed\" item when every device is). How recovery works " +
                 "on the cell: an open alarm without a recovery request is fixed at the device first; a recovery request " +
                 "means the local alarm was reset and the console's recovery comes next (check where the pallet " +
                 "physically is: Retry needs it where it was before the command, Normal Finish where it would be after). " +
                 "Don't suggest Cancel, and Work Data Reset only as a last resort. The recovery history isn't in the " +
                 "database (only in the cell's RecoveryMaintenaceOperation.log, when its log folder is shared); a fixture " +
                 "back at waiting may just have been pallet-recovered and will re-run its whole job. Fixtures held back on " +
                 "purpose are counted in parked: only suspended (loading allowed, no machining) or held at the work-set " +
                 "station (not taken to a station), and those the cell's rules hold: their order's machining suspended, " +
                 "an off-duty process while on duty, an operator-call job while off duty (an operator-call tool in the " +
                 "program isn't checked); how many, how many lack tools, how many hold back a job waiting now (blocking; " +
                 "the rest have the flag set with no work waiting), per place (where their pallets are) and condition, " +
                 "with withParts for held fixtures that still hold parts; includeParked=true lists them " +
                 "last as items. Damaged fixtures get no loading or machining and their parts are unloaded at a station. " +
                 "A fixture's detail is its status reason: one text shared by suspend, hold, damaged and " +
                 "off duty, typed by an operator or written by the cell, possibly empty or left from an earlier " +
                 "condition (a machine recovery can suspend fixtures too). Each item: kind, what, device or " +
                 "pallet/fixture, order and process, detail, since (only when the cell records it), fixtures and pallets " +
                 "for grouped items. counts: how many " +
                 "of each condition the cell has, parked ones included, even past the row cap. " +
                 "machineToolStatus: each machine's tool status, as context only, one value by precedence (\"lacking " +
                 "tools\" there usually means a later job waits for a tool, and it hides tool alarms: see get_tools " +
                 "filter=alarm; the fixtures concerned are the items). notes: what this cell's settings mean for the " +
                 "list (e.g. a probe that lowers part counts). notAvailable names sections " +
                 "this cell or login can't show.")]
    public static Task<string> GetAttention(
        ToolContext context,
        [Description("true: list the parked fixtures (only suspended or on hold) one by one instead of only counting them. Default false.")]
        bool includeParked = false,
        CancellationToken ct = default) =>
        context.Database("get_attention", (r, t) => r.GetAttentionAsync(includeParked, t), ct);
}
