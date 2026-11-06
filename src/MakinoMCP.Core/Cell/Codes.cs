// Code meanings for values in the cell database. The values are facts about the cell database, read from the
// installed MAS-A5 software and written fresh here. To ship without them, remove this file's tables.

namespace MakinoMCP.Core.Cell;

public static class Codes
{
    private static readonly Dictionary<int, string> DeviceTypes = new()
    {
        [1] = "machine",
        [2] = "vehicle",
        [3] = "work-set station",
        [4] = "pallet stocker",
        [5] = "tool crib",
        [6] = "MAG3 pallet changer",
        [7] = "tool presetter",
        [8] = "vehicle controller",
        [9] = "tool ID",
        [10] = "ID controller",
        [11] = "cleaning station",
        [12] = "turning center",
        [13] = "deburring machine",
        [14] = "pallet replace station",
        [15] = "pass station",
        [16] = "out pallet stocker",
        [17] = "measuring machine",
        [18] = "oil dump",
    };

    private static readonly Dictionary<int, string> CncModes = new()
    {
        [0] = "MDI",
        [1] = "memory",
        [3] = "edit",
        [4] = "handle",
        [5] = "jog",
        [9] = "reference return",
        [10] = "remote (tape)",
        [100] = "rapid",
        [101] = "DNC",
        [200] = "manual",
    };

    private static readonly Dictionary<int, string> MachineActions = new()
    {
        [0] = "none",
        [1] = "machining",
        [2] = "preparation",
    };

    private static readonly Dictionary<int, string> MachineSubActions = new()
    {
        [0] = "none",
        [1] = "pallet index",
        [2] = "pallet change",
        [3] = "pallet load",
        [4] = "pallet unload",
        [5] = "unload/load preparation",
        [6] = "manual couple",
        [7] = "manual uncouple",
        [8] = "security login",
        [9] = "before unload",
        [10] = "after unload",
        [11] = "before load",
        [12] = "after load",
        [13] = "before unload/load",
        [14] = "pallet load (back)",
        [15] = "pallet unload (front)",
        [16] = "table pallet preparation",
        [17] = "tool relocation",
        [18] = "front load / back unload",
        [19] = "front unload / back load",
    };

    private static readonly Dictionary<int, string> OrderStatuses = new()
    {
        [0] = "undefined",
        [1] = "inactive",
        [2] = "active",
        [3] = "running",
        [4] = "suspended",
        [5] = "complete",
        [6] = "cancelled",
        [7] = "no quantity",
        [8] = "waiting for material",
    };

    private static readonly Dictionary<int, string> VehicleCommandBits = new()
    {
        [1] = "load/unload",
        [2] = "load",
        [4] = "unload",
        [8] = "position",
        [16] = "oil dump",
        [32] = "read ID",
    };

    // WorkSetResults.OperationStatus: what the operator did at the work-set station.
    private static readonly Dictionary<int, string> WorkSetOperations = new()
    {
        [0] = "nothing",
        [1] = "unclamp",
        [2] = "clamp",
        [3] = "clamp suspended",
        [4] = "unclamp cancelled",
        [5] = "unclamp scrap cancelled",
        [6] = "other",
        [7] = "other cancelled",
        [8] = "input",
        [9] = "input cancelled",
        [10] = "output",
        [11] = "output cancelled",
        [12] = "stay on table",
        [13] = "stay on table cancelled",
        [14] = "inspection",
        [15] = "inspection cancelled",
        [16] = "exchange fixture",
        [17] = "exchange fixture cancelled",
        [18] = "stay at front",
        [19] = "stay at front cancelled",
        [20] = "material input",
        [21] = "material input cancelled",
        [22] = "material output",
        [23] = "material output cancelled",
        [24] = "exchange",
        [25] = "exchange cancelled",
        [26] = "remove scrap",
        [27] = "fixture change",
        [29] = "clamp NG",
        [30] = "check fixtures",
    };

    // WorkSetStatus.WSSMode (PDevMode). The console shows every value but system and maintenance as local.
    private static readonly Dictionary<int, string> WssModes = new()
    {
        [0] = "system",
        [1] = "local (from-to)",
        [2] = "local (jog high)",
        [4] = "local (jog low)",
        [16] = "maintenance",
        [32] = "local",
        [48] = "local (manual)",
        [64] = "local (teaching)",
        [128] = "local (reference)",
    };

    // MachineResults.FinishStatus (EndOfProgramFinishStatus), named as the console names them. BT2 is a tool measured
    // too long (pulled out of its holder), not a break. Tool life and spare tool selected are informational unless the
    // result is also abnormal.
    private static readonly Dictionary<int, string> MachiningFinishBits = new()
    {
        [0x1] = "M940 (the program ended itself)",
        [0x10000] = "BTS alarm (broken tool)",
        [0x20000] = "BT2 alarm (tool too long)",
        [0x40000] = "AC alarm (adaptive control)",
        [0x80000] = "SL alarm (spindle load)",
        [0x100000] = "TL alarm (tool life)",
        [0x200000] = "fixture alarm",
        [0x400000] = "STS (spare tool selected)",
        [0x800000] = "tool prep check failed",
        [0x1000000] = "system abort (aborted at the machine)",
        [0x2000000] = "system mode off",
        [0x4000000] = "pallet not seated",
        [0x8000000] = "work vacuum",
    };

    // IndividualToolData.Status and IndividualToolCutterData.CutterStatus (ToolStatus).
    private static readonly Dictionary<int, string> ToolStatuses = new()
    {
        [0] = "unknown",
        [1] = "ready",
        [2] = "life warning",
        [3] = "offset error",
        [4] = "alarm",
    };

    // FunctionalToolCutterData.ToolLifeType and the life columns that follow it (ToolLife). Both time types are stored
    // in seconds: 3 only means the control counts in tenths (the cell converts on the way in and out).
    private static readonly Dictionary<int, string> ToolLifeTypes = new()
    {
        [-1] = "undefined",
        [0] = "seconds",
        [1] = "distance",
        [2] = "count",
        [3] = "seconds (the control counts tenths)",
    };

    // FunctionalToolCutterData.Kind (ToolKind).
    private static readonly Dictionary<int, string> ToolKinds = new()
    {
        [0] = "undefined",
        [1] = "drill",
        [2] = "ball end mill",
        [3] = "end mill",
        [4] = "boring bar",
        [5] = "spring tool 1",
        [6] = "spring tool 2",
        [7] = "tap",
        [8] = "reamer",
        [9] = "face mill",
        [10] = "probe",
        [11] = "grinding tool",
        [12] = "grinding dresser",
        [14] = "limited tool",
        [15] = "air turbine",
        [16] = "NT attachment",
        [17] = "angle head",
        [18] = "INCS Nakanishi",
        [19] = "air turbine (fixed)",
        [21] = "turning tool",
        [22] = "chamfer tool",
        [23] = "radius end mill",
        [24] = "reference tool",
        [25] = "calibration tool",
        [26] = "setup tool",
        [27] = "special tool 1",
        [28] = "special tool 2",
        [29] = "drill speed-up cycle",
    };

    // Cutter alarm flags (ToolAlarmStatus); each bit stands for one alarm bit column of a cutter row.
    private static readonly Dictionary<int, string> ToolAlarmBits = new()
    {
        [0x1] = "alarm tool",
        [0x2] = "tool life alarm",
        [0x4] = "tool life warning",
        [0x8] = "BTS alarm (broken tool)",
        [0x10] = "BT2 alarm (tool too long)",
        [0x20] = "AC alarm (adaptive control)",
        [0x40] = "SL alarm (spindle load)",
        [0x80] = "SL low alarm (spindle load too low)",
        [0x100] = "tool ID alarm",
        [0x200] = "not inspected",
        [0x400] = "STS not OK",
    };

    /// <summary>The bit column behind each <see cref="ToolAlarms"/> flag: AlarmTool sits on IndividualToolData, the
    /// rest on IndividualToolCutterData (and as Old/New pairs on PresetResults).</summary>
    public static readonly IReadOnlyList<(string Column, int Flag)> ToolAlarmColumns =
    [
        ("AlarmTool", 0x1),
        ("TLAlarm", 0x2),
        ("TLWarning", 0x4),
        ("BTSAlarm", 0x8),
        ("BT2AlarmLong", 0x10),
        ("ACAlarm", 0x20),
        ("SLAlarm", 0x40),
        ("SLLowAlarm", 0x80),
        ("ToolIDAlarm", 0x100),
        ("NotInspected", 0x200),
        ("STSNotOK", 0x400),
    ];

    // MachineStatus/WorkSetStatus Table/Front/BackStatus (DevicePositionStatus).
    private static readonly Dictionary<int, string> DevicePositionStatuses = new()
    {
        [0] = "empty",
        [1] = "reserved",
        [2] = "waiting for processing",
        [3] = "processing",
        [4] = "finished",
        [5] = "after-load program running",
        [6] = "before-unload program running",
    };

    // The dashboard Status of MachineStatus, WorkSetStatus and VehicleStatus: the same buckets as the day summaries.
    private static readonly Dictionary<int, string> DeviceDashStatuses = new()
    {
        [0] = "down",
        [1] = "idle",
        [2] = "busy",
        [3] = "manual",
        [4] = "error",
    };

    // MachineStatus.ToolStatus: the machine's tool set for its current work, as the cell server writes it.
    private static readonly Dictionary<int, string> MachineToolStatuses = new()
    {
        [0] = "unknown",
        [1] = "ready",
        [2] = "lacking tools",
        [3] = "tool alarm",
        [4] = "tool life warning",
        [5] = "offset error",
    };

    // MachineStatus.CNCRun (CNCRunStatus).
    private static readonly Dictionary<int, string> CncRuns = new()
    {
        [0] = "reset",
        [1] = "stop",
        [2] = "hold",
        [3] = "run",
    };

    // MachineStatus.OperatorCall (OperatorCallStatus).
    private static readonly Dictionary<int, string> OperatorCalls = new()
    {
        [0] = "released",
        [1] = "M code",
        [2] = "PMC",
    };

    // MachineStatus.RunJob: what the machine's current job is.
    private static readonly Dictionary<int, string> RunJobs = new()
    {
        [0] = "none",
        [1] = "machining",
        [2] = "pallet change",
        [3] = "other preparation",
        [4] = "manual transport",
    };

    // Pallets.PalletStatus as the cell's dashboard procedure (UpdatePalletStatus) rewrites the in-memory pallet state.
    // Every in-memory state it doesn't map is stored as 0 "idle": no fixtures, no work, material pallet, extra tool
    // pallet, every "... finished" state, reclamp waits, warm-up transport and check fixtures. So a pallet whose work
    // is done reads idle with work status "normal result". 12 and 14 mean the pallet is in a special transport state,
    // not that the vehicle is carrying it right now.
    private static readonly Dictionary<int, string> PalletDisplayStatuses = new()
    {
        [0] = "idle",
        [1] = "machining",
        [2] = "work setting",
        [3] = "in another device",
        [4] = "waiting for machining",
        [5] = "waiting for work setting (no result on it)",
        [6] = "waiting for work setting (with a result to unload)",
        [7] = "waiting for an auxiliary device",
        [8] = "machining suspended",
        [9] = "work-set hold",
        [10] = "work-set hold (with a result on it)",
        [11] = "auxiliary device suspended",
        [12] = "pallet input/output, manual transport or exchange",
        [13] = "damaged",
        [14] = "special transport (extra tool pallet, APC pallet change, reclamp or fixture change)",
    };

    // Pallets.WorkStatus as the same procedure rewrites it; 0 is the raw "undefined" it leaves in place when there is
    // no work to report (idle, transport, damaged). It is the state of the work on the pallet, not the pallet's health.
    private static readonly Dictionary<int, string> PalletWorkStatuses = new()
    {
        [0] = "none",
        [1] = "not completed (no result yet)",
        [2] = "normal result",
        [3] = "scrap or abnormal work set",
        [4] = "cancelled (order or process)",
    };

    // VehicleStatus.VHCMode (VehicleMode): a list of values, not flags.
    private static readonly Dictionary<int, string> VehicleModes = new()
    {
        [0] = "system",
        [1] = "from-to",
        [2] = "jog high",
        [4] = "jog low",
        [16] = "maintenance",
        [32] = "local",
        [48] = "manual",
    };

    // Jobs.JobType (ProcessJobType).
    private static readonly Dictionary<int, string> JobTypes = new()
    {
        [0] = "undefined",
        [1] = "work setting",
        [2] = "machining",
        [3] = "cleaning",
        [4] = "turning",
        [5] = "deburring",
        [6] = "measuring",
    };

    // Processes.Status (ProcessStatus).
    private static readonly Dictionary<int, string> ProcessStatuses = new()
    {
        [-1] = "varied",
        [0] = "undefined",
        [1] = "ready",
        [2] = "suspended",
        [3] = "cancelled",
    };

    // FixtureRun.CurJobStatus (FixtureJobStatus).
    private static readonly Dictionary<int, string> FixtureJobStatuses = new()
    {
        [0] = "nothing",
        [1] = "waiting for processing",
        [2] = "processing",
        [3] = "normal finish",
        [4] = "abnormal finish",
    };

    // FixtureRun.WorkStatus (WorkStatus).
    private static readonly Dictionary<int, string> FixtureWorkStatuses = new()
    {
        [0] = "undefined",
        [1] = "normal",
        [2] = "scrap",
        [3] = "process cancelled",
        [4] = "order cancelled",
        [5] = "abnormal work set",
        [6] = "seating error",
        [7] = "unclamp NG",
    };

    // PalletFixtures.PreMcSuspendReason (PreMcSuspendReasonType).
    private static readonly Dictionary<int, string> PreMcSuspendReasons = new()
    {
        [0] = "none",
        [1] = "operator suspended",
        [2] = "CSG error",
    };

    // PalletFixtures.ReclampStatus (ReclampStatus).
    private static readonly Dictionary<int, string> ReclampStatuses = new()
    {
        [0] = "normal",
        [1] = "timeout alarm",
        [2] = "timeout warning",
    };

    // WorkSetCommand.OperationType (WorkSetStationOperationType): what the station asks the operator to do. A different
    // list from WorkSetResults.OperationStatus above.
    private static readonly Dictionary<int, string> WorkSetCommandOperations = new()
    {
        [0] = "nothing",
        [1] = "clamp",
        [2] = "unclamp and clamp",
        [3] = "unclamp and clamp (scrap)",
        [4] = "unclamp",
        [5] = "unclamp (scrap)",
        [6] = "other",
        [7] = "input",
        [8] = "output",
        [9] = "stay on table",
        [10] = "inspection",
        [11] = "exchange fixture",
        [12] = "stay at front",
        [13] = "hand change",
        [14] = "other, unclamp and clamp",
        [15] = "material input",
        [16] = "material output",
        [17] = "reclamp",
        [18] = "exchange",
        [19] = "material work set",
        [20] = "fixture change",
        [21] = "check fixtures",
    };

    // ManualTransport.Status (ManualTransportStatus).
    private static readonly Dictionary<int, string> ManualTransportStatuses = new()
    {
        [0] = "undefined",
        [1] = "waiting for transport",
        [2] = "transporting",
        [3] = "waiting to finish",
        [4] = "finished",
        [5] = "cancelled",
    };

    // WorkSetJobs.OperationType (WorkSetOperation): what a work-set job of a routing does.
    private static readonly Dictionary<int, string> WorkSetJobOperations = new()
    {
        [0] = "load",
        [1] = "unload",
        [2] = "other",
    };

    // MachineJobs.OperatorCall (OperatorCallType): whether a machining job stops for the operator.
    private static readonly Dictionary<int, string> JobOperatorCalls = new()
    {
        [0] = "none",
        [1] = "permanent",
        [2] = "one-shot",
    };

    // SystemParameters.CheckLackToolsAllRemainingJobs (CheckLackToolAllRemaining): which of a fixture's jobs the cell
    // checks when it marks the fixture lacking tools.
    private static readonly Dictionary<int, string> LackingToolScopes = new()
    {
        [0] = "its next machining job",
        [1] = "its machining jobs before the next work setting",
        [2] = "all its remaining jobs",
    };

    // WorkSetStatus.Command (WorkSetStationMonitorInstruction): the instruction the station shows the operator.
    private static readonly Dictionary<int, string> WssInstructions = new()
    {
        [0] = "nothing",
        [1] = "clamp",
        [2] = "clamp cancelled",
        [3] = "unclamp",
        [4] = "unclamp (scrap)",
        [5] = "unclamp cancelled",
        [6] = "unclamp and clamp",
        [7] = "unclamp (scrap) and clamp",
        [8] = "unclamp and clamp cancelled",
        [9] = "unclamp (scrap) and clamp cancelled",
        [10] = "re-machining",
        [11] = "other",
        [12] = "other cancelled",
        [13] = "inspection",
        [14] = "inspection cancelled",
        [15] = "exchange fixture",
        [16] = "exchange fixture cancelled",
        [17] = "manual transport",
        [18] = "pallet input",
        [19] = "pallet output",
        [20] = "re-cleaning",
        [21] = "re-deburring",
        [22] = "re-unclamp",
        [23] = "material input",
        [24] = "material output",
        [25] = "material work set",
        [26] = "reclamp",
        [27] = "pallet exchange",
        [28] = "re-measuring",
        [29] = "fixture change",
        [30] = "check fixtures",
    };

    // Machine/WorkSet/VehicleStatus.PowerStatus (DevicePowerStatus).
    private static readonly Dictionary<int, string> DevicePowerStatuses = new()
    {
        [0] = "on",
        [1] = "power-off reserved",
        [2] = "ready to power off",
        [3] = "waiting for power-off acknowledgement",
        [4] = "power-off cancelled",
        [5] = "power-off rejected",
        [6] = "powered off",
    };

    // Machine/WorkSet/VehicleStatus.PowerOffResult (DevicePowerOffResult): why a scheduled power-off is still pending.
    private static readonly Dictionary<int, string> DevicePowerOffResults = new()
    {
        [0] = "none",
        [1] = "reserved",
        [2] = "a pallet still has work",
        [3] = "waiting until empty",
        [4] = "ready",
        [5] = "waiting for the machine to be ready",
        [6] = "waiting for the machine to power off",
        [7] = "waiting for the work-set station to be ready",
        [8] = "waiting for the work-set station to power off",
        [9] = "waiting for the vehicle to be ready",
        [10] = "dummy power-off",
        [11] = "powered off",
        [12] = "rejected",
        [13] = "cancelled (device closed)",
        [14] = "cancelled (mode)",
        [15] = "cancelled (pallet)",
        [16] = "cancelled (node)",
        [17] = "waiting for the cleaning station to be ready",
        [18] = "waiting for the cleaning station to power off",
        [19] = "waiting for the turning center to be ready",
        [20] = "waiting for the turning center to power off",
        [21] = "waiting for the deburring machine to be ready",
        [22] = "waiting for the deburring machine to power off",
        [23] = "waiting for the pallet replace station to be ready",
        [24] = "waiting for the pallet replace station to power off",
        [25] = "waiting for the pass station to be ready",
        [26] = "waiting for the pass station to power off",
        [27] = "waiting for a warm-up transport",
    };

    // UserSysData PowerOffStatus: the cell's scheduled auto power-off.
    private static readonly Dictionary<int, string> CellPowerOffStatuses = new()
    {
        [0] = "none",
        [1] = "scheduled, waiting",
        [2] = "running (time reached, finishing the remaining work)",
        [3] = "finished",
    };

    // SystemParameters *Recovery and DeviceParameters Device*Recovery (AbnormalFinishAction): what the cell does when
    // a machining cycle ends with that kind of abnormal finish.
    private static readonly Dictionary<int, string> AbnormalFinishActions = new()
    {
        [0] = "raise a recovery request",
        [1] = "finish normally",
        [2] = "finish abnormally (scrap)",
        [3] = "always wait for the operator",
    };

    // DeviceParameters DisableOperation on a work-set station (WorkSetStationDisableOperation, flags).
    private static readonly Dictionary<int, string> WssDisabledOperationBits = new()
    {
        [1] = "pallet input/output",
        [2] = "automatic work setting",
        [4] = "reclamp",
    };

    // InformationFiles.InfoType (InformationFileType).
    private static readonly Dictionary<int, string> InfoFileTypes = new()
    {
        [0] = "undefined",
        [1] = "work setting",
        [2] = "tool setting",
    };

    public static string DeviceType(int? value) => Lookup(value, DeviceTypes);

    public static string CncMode(int? value) => Lookup(value, CncModes);

    public static string PmcAlarmLevel(bool isWarning) => isWarning ? "warning" : "alarm";

    public static string MachineAction(int? value) => Lookup(value, MachineActions);

    public static string MachineSubAction(int? value) => Lookup(value, MachineSubActions);

    public static string OrderStatus(int? value) => Lookup(value, OrderStatuses);

    public static string VehicleCommand(int? value) => Flags(value, VehicleCommandBits, "none");

    public static string WorkSetOperation(int? value) => Lookup(value, WorkSetOperations);

    public static string WssMode(int? value) => Lookup(value, WssModes);

    public static string MachiningFinish(int? value) => Flags(value, MachiningFinishBits, "normal");

    public static string ToolStatus(int? value) => Lookup(value, ToolStatuses);

    public static string ToolLifeType(int? value) => Lookup(value, ToolLifeTypes);

    public static string ToolKind(int? value) => Lookup(value, ToolKinds);

    /// <summary>One entry per alarm flag set (see <see cref="ToolAlarmColumns"/>), leftover bits as one "code N";
    /// empty for none or NULL.</summary>
    public static IReadOnlyList<string> ToolAlarms(int? flags) => FlagList(flags, ToolAlarmBits);

    public static string DevicePositionStatus(int? value) => Lookup(value, DevicePositionStatuses);

    public static string DeviceDashStatus(int? value) => Lookup(value, DeviceDashStatuses);

    public static string MachineToolStatus(int? value) => Lookup(value, MachineToolStatuses);

    public static string CncRun(int? value) => Lookup(value, CncRuns);

    public static string OperatorCall(int? value) => Lookup(value, OperatorCalls);

    public static string RunJob(int? value) => Lookup(value, RunJobs);

    public static string PalletDisplayStatus(int? value) => Lookup(value, PalletDisplayStatuses);

    public static string PalletWorkStatus(int? value) => Lookup(value, PalletWorkStatuses);

    public static string VehicleMode(int? value) => Lookup(value, VehicleModes);

    public static string JobType(int? value) => Lookup(value, JobTypes);

    public static string ProcessStatus(int? value) => Lookup(value, ProcessStatuses);

    public static string FixtureJobStatus(int? value) => Lookup(value, FixtureJobStatuses);

    public static string FixtureWorkStatus(int? value) => Lookup(value, FixtureWorkStatuses);

    public static string PreMcSuspendReason(int? value) => Lookup(value, PreMcSuspendReasons);

    public static string ReclampStatus(int? value) => Lookup(value, ReclampStatuses);

    public static string WorkSetCommandOperation(int? value) => Lookup(value, WorkSetCommandOperations);

    public static string ManualTransportStatus(int? value) => Lookup(value, ManualTransportStatuses);

    public static string InfoFileType(int? value) => Lookup(value, InfoFileTypes);

    public static string LackingToolScope(int? value) => Lookup(value, LackingToolScopes);

    public static string WssInstruction(int? value) => Lookup(value, WssInstructions);

    public static string DevicePowerStatus(int? value) => Lookup(value, DevicePowerStatuses);

    public static string DevicePowerOffResult(int? value) => Lookup(value, DevicePowerOffResults);

    public static string CellPowerOffStatus(int? value) => Lookup(value, CellPowerOffStatuses);

    public static string AbnormalFinishAction(int? value) => Lookup(value, AbnormalFinishActions);

    /// <summary>One entry per disabled operation, leftover bits as one "code N"; empty for none or NULL.</summary>
    public static IReadOnlyList<string> WssDisabledOperations(int? flags) => FlagList(flags, WssDisabledOperationBits);

    public static string WorkSetJobOperation(int? value) => Lookup(value, WorkSetJobOperations);

    public static string JobOperatorCall(int? value) => Lookup(value, JobOperatorCalls);

    private static string Lookup(int? value, IReadOnlyDictionary<int, string> map)
    {
        if (value is null) return "";
        return map.TryGetValue(value.Value, out var words) ? words : $"code {value.Value}";
    }

    // A flag value as one string; zero has its own words.
    private static string Flags(int? value, IReadOnlyDictionary<int, string> bits, string zeroWords)
    {
        if (value is null) return "";
        if (value.Value == 0) return zeroWords;
        return string.Join(", ", FlagList(value, bits));
    }

    // Splits a flag value into its known bits, in ascending bit order, then any leftover bits as one code.
    private static List<string> FlagList(int? value, IReadOnlyDictionary<int, string> bits)
    {
        var words = new List<string>();
        if (value is null or 0) return words;
        foreach (var (bit, name) in bits.OrderBy(pair => pair.Key))
        {
            if ((value.Value & bit) != 0) words.Add(name);
        }

        var knownMask = bits.Keys.Aggregate(0, (mask, bit) => mask | bit);
        var leftover = value.Value & ~knownMask;
        if (leftover != 0) words.Add($"code {leftover}");
        return words;
    }
}
