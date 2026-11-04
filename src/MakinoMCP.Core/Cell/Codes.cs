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

    // The cell's in-memory pallet states (PalletStatus). The Pallets table stores dashboard codes instead: see
    // PalletDisplayStatuses.
    private static readonly Dictionary<int, string> PalletStatuses = new()
    {
        [0] = "undefined",
        [1] = "no fixture",
        [2] = "fixture loaded",
        [3] = "damaged",
        [4] = "material pallet",
        [5] = "extra tool pallet",
        [10] = "input",
        [11] = "output",
        [12] = "manual transport",
        [13] = "extra tool pallet transport",
        [14] = "pallet change transport",
        [15] = "manual transport reclamp",
        [16] = "manual transport reclamp warning",
        [17] = "exchange",
        [18] = "fixture change",
        [19] = "warm-up transport",
        [20] = "processing",
        [30] = "waiting for machining",
        [31] = "waiting for work setting",
        [32] = "waiting for cleaning",
        [33] = "waiting for turning",
        [34] = "waiting for deburring",
        [35] = "waiting for measuring",
        [40] = "work setting finished",
        [41] = "machining finished",
        [42] = "cleaning finished",
        [43] = "turning finished",
        [44] = "deburring finished",
        [45] = "measuring finished",
        [50] = "machining suspended",
        [51] = "work setting suspended",
        [52] = "cleaning suspended",
        [53] = "turning suspended",
        [54] = "deburring suspended",
        [55] = "machining suspended (reclamp)",
        [56] = "cleaning suspended (reclamp)",
        [57] = "turning suspended (reclamp)",
        [58] = "deburring suspended (reclamp)",
        [59] = "pre-machining suspended",
        [60] = "machining off-duty process",
        [61] = "turning off-duty process",
        [62] = "deburring off-duty process",
        [70] = "measuring suspended",
        [80] = "measuring suspended (reclamp)",
        [110] = "check fixtures",
    };

    private static readonly Dictionary<int, string> CncModes = new()
    {
        [0] = "MDI",
        [1] = "memory",
        [3] = "edit",
        [4] = "handle",
        [5] = "jog",
        [9] = "reference return",
        [10] = "remote",
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

    private static readonly Dictionary<int, string> WssModes = new()
    {
        [0] = "system",
        [16] = "maintenance",
        [32] = "local",
    };

    private static readonly Dictionary<int, string> MachiningFinishBits = new()
    {
        [0x1] = "M940",
        [0x10000] = "broken tool (short)",
        [0x20000] = "broken tool (long)",
        [0x40000] = "adaptive control",
        [0x80000] = "spindle load",
        [0x100000] = "tool life",
        [0x200000] = "fixture alarm",
        [0x400000] = "spare tool select",
        [0x800000] = "tool prep check",
        [0x1000000] = "abort switch",
        [0x2000000] = "system mode off",
        [0x4000000] = "not seated",
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
        [0x8] = "broken tool (short)",
        [0x10] = "broken tool (long)",
        [0x20] = "adaptive control",
        [0x40] = "spindle load",
        [0x80] = "spindle load low",
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
        [5] = "load after",
        [6] = "unload before",
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

    // Pallets.PalletStatus as the cell's dashboard procedure rewrites it (not the in-memory PalletStatus list above).
    private static readonly Dictionary<int, string> PalletDisplayStatuses = new()
    {
        [0] = "idle",
        [1] = "machining",
        [2] = "work setting",
        [3] = "in another device",
        [4] = "waiting for machining",
        [5] = "waiting for work setting",
        [6] = "waiting for work setting",
        [7] = "waiting for an auxiliary device",
        [8] = "machining suspended",
        [9] = "work setting suspended",
        [10] = "work setting suspended",
        [11] = "auxiliary device suspended",
        [12] = "transport",
        [13] = "damaged",
        [14] = "pallet active",
    };

    // Pallets.WorkStatus as the same procedure rewrites it; 0 is the raw "undefined" it leaves in place when there is
    // no work to report (idle, transport, damaged).
    private static readonly Dictionary<int, string> PalletWorkStatuses = new()
    {
        [0] = "none",
        [1] = "not completed",
        [2] = "normal",
        [3] = "abnormal",
        [4] = "cancel",
    };

    // VehicleStatus.VHCMode (VehicleMode): a list of values, not flags.
    private static readonly Dictionary<int, string> VehicleModes = new()
    {
        [0] = "system",
        [1] = "from-to",
        [2] = "jog 1",
        [4] = "jog 2",
        [16] = "maintenance",
        [32] = "not system",
        [48] = "manual interrupt",
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

    // InformationFiles.InfoType (InformationFileType).
    private static readonly Dictionary<int, string> InfoFileTypes = new()
    {
        [0] = "undefined",
        [1] = "work setting",
        [2] = "tool setting",
    };

    public static string DeviceType(int? value) => Lookup(value, DeviceTypes);

    public static string PalletStatus(int? value) => Lookup(value, PalletStatuses);

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
