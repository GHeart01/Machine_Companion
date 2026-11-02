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

    private static string Lookup(int? value, IReadOnlyDictionary<int, string> map)
    {
        if (value is null) return "";
        return map.TryGetValue(value.Value, out var words) ? words : $"code {value.Value}";
    }

    // Splits a flag value into its known bits, rendered in ascending bit order, then any leftover bits as one code.
    private static string Flags(int? value, IReadOnlyDictionary<int, string> bits, string zeroWords)
    {
        if (value is null) return "";
        if (value.Value == 0) return zeroWords;

        var words = new List<string>();
        foreach (var (bit, name) in bits.OrderBy(pair => pair.Key))
        {
            if ((value.Value & bit) != 0) words.Add(name);
        }

        var knownMask = bits.Keys.Aggregate(0, (mask, bit) => mask | bit);
        var leftover = value.Value & ~knownMask;
        if (leftover != 0) words.Add($"code {leftover}");

        return string.Join(", ", words);
    }
}
