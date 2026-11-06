// Code meanings for the device and attention values (get_cell_overview, get_attention). As in Codes.cs: facts about
// the cell database, read from the installed MAS-A5 software and written fresh here.

namespace MakinoMCP.Core.Cell;

public static partial class Codes
{
    // UserSysData TransPriorityRule: the terms the cell sorts waiting transports by, in the order stored ('|' list).
    private static readonly Dictionary<int, string> TransportOrderTerms = new()
    {
        [1] = "operator priority",
        [2] = "pallet priority",
        [3] = "order due date",
        [4] = "order priority",
        [5] = "destination device",
        [6] = "process sequence",
        [7] = "unclamp jobs first",
        [8] = "order balance (orders with the most still to make first)",
        [9] = "wait time (longest waiting first)",
    };

    // UserSysData TransDevPriorityRule: which kind of destination comes first when the transport rule sorts by it.
    private static readonly Dictionary<int, string> TransportDeviceTerms = new()
    {
        [1] = "machine",
        [2] = "work-set station",
        [3] = "cleaning station",
        [4] = "pallet stocker",
        [5] = "turning center",
        [6] = "deburring machine",
        [7] = "pallet replace station",
        [8] = "pass station",
        [9] = "measuring machine",
    };

    // ManualTransport.TransportType: what kind of manual transport an operator (or the cell) set up.
    private static readonly Dictionary<int, string> ManualTransportTypes = new()
    {
        [0] = "undefined",
        [1] = "standard",
        [2] = "pallet input",
        [3] = "pallet output",
        [4] = "to a cleaning station",
        [5] = "to a deburring machine",
        [6] = "extra tool pallet",
        [7] = "pallet change",
        [8] = "to the pallet replace station",
        [9] = "to the pass station",
        [10] = "reclamp",
        [11] = "pallet exchange",
        [12] = "to a measuring machine",
        [13] = "fixture change",
        [14] = "warm-up",
        [15] = "check fixtures",
    };

    // A pallet position on a device (ManualTransport.DestDevicePosition, PalletLocation.CurDevicePosition).
    private static readonly Dictionary<int, string> DevicePositions = new()
    {
        [1] = "table",
        [2] = "front",
        [3] = "back",
        [4] = "buffer 1",
        [5] = "buffer 2",
    };

    // SystemParameters EnableFTNExport / EnableITNExport: the file format the cell writes tool data out in.
    private static readonly Dictionary<int, string> FtnExportTypes = new()
    {
        [0] = "off",
        [1] = "MM standard",
        [2] = "MT standard",
        [3] = "Zoller",
        [4] = "without cutter data",
        [5] = "FTN export utility",
    };

    private static readonly Dictionary<int, string> ItnExportTypes = new()
    {
        [0] = "off",
        [1] = "MM standard",
        [2] = "MT standard",
        [3] = "without cutter data",
    };

    // SystemParameters NCProgramAutoDownloadType: when the cell sends a program to the machine's NC memory.
    private static readonly Dictionary<int, string> NcDownloadTypes = new()
    {
        [0] = "always (before each run)",
        [1] = "only when needed (the program stays in NC memory while it fits)",
    };

    // MachineStatus.MachineSubAction values during preparation (MachineAction 2) at which the machine waits for a
    // person, in the console's sense.
    private static readonly Dictionary<int, string> MachineOperatorWaits = new()
    {
        [6] = "waiting for the operator to confirm the 5-axis table is coupled",
        [7] = "waiting for the operator to confirm the 5-axis table is uncoupled",
        [8] = "waiting for a security login",
    };

    // WorkSetStatus.HelpRequest and OpeTimeout (one special station type writes them; others leave 0).
    private static readonly Dictionary<int, string> WssHelpRequests = new()
    {
        [0] = "none",
        [1] = "help requested",
    };

    private static readonly Dictionary<int, string> WssOperationTimeouts = new()
    {
        [0] = "none",
        [1] = "operation timed out",
    };

    public static string TransportOrderTerm(int? value) => Lookup(value, TransportOrderTerms);

    public static string TransportDeviceTerm(int? value) => Lookup(value, TransportDeviceTerms);

    /// <summary>The terms of the vendor's default transport order (used when the cell stores none), in order.</summary>
    public static IReadOnlyList<int> DefaultTransportOrder { get; } = [1, 2, 3, 4, 5, 6, 7, 8, 9];

    /// <summary>The vendor's default destination order (used when the cell stores none), in order.</summary>
    public static IReadOnlyList<int> DefaultTransportDeviceOrder { get; } = [9, 6, 3, 5, 1, 2, 4];

    public static string ManualTransportType(int? value) => Lookup(value, ManualTransportTypes);

    /// <summary>A position's words; "" for 0 (none) or NULL.</summary>
    public static string DevicePosition(int? value) => value is null or 0 ? "" : Lookup(value, DevicePositions);

    public static string FtnExportType(int? value) => Lookup(value, FtnExportTypes);

    public static string ItnExportType(int? value) => Lookup(value, ItnExportTypes);

    public static string NcDownloadType(int? value) => Lookup(value, NcDownloadTypes);

    /// <summary>What the machine waits for at this sub-action, or null when the sub-action isn't a wait on a person.</summary>
    public static string? MachineOperatorWait(int? subAction) =>
        subAction is { } s && MachineOperatorWaits.TryGetValue(s, out var words) ? words : null;

    /// <summary>A machine's MachineStatus.SystemMode: system (the cell runs it) or standalone.</summary>
    public static string MachineSystemMode(bool systemMode) => systemMode ? "system" : "standalone";

    /// <summary>The vehicle's mode as far as the vendor view tells it (only whether it is in system mode).</summary>
    public static string VehicleSystemMode(bool systemMode) =>
        systemMode ? "system" : "not system (local, from-to, jog, maintenance or manual)";

    public static string WssHelpRequest(int? value) => Lookup(value, WssHelpRequests);

    public static string WssOperationTimeout(int? value) => Lookup(value, WssOperationTimeouts);
}
