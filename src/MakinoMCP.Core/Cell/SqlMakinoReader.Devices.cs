using System.Globalization;
using Microsoft.Data.SqlClient;

namespace MakinoMCP.Core.Cell;

// Devices: the cell-level settings get_cell_overview reports (software version, retention, scheduling rules, NC
// download, file locations) and the per-device extras (transport cancel, extra tool pallet wait, NC memory, inspection
// countdown, empty positions that still carry a state).
//
// Logins: what MakinoMCP sees is decided by the SQL login, not by anyone's console group. The cell's stock reporting
// login (install script CREATE_USERS.SQL) has SELECT on 56 tables, among them Devices, MachineStatus, WorkSetStatus,
// PMCAlarms, CNCAlarms, the result and day-summary tables, Orders, Quantities, Pallets, PalletFixtures, PalletLocation,
// FixtureRun, FixtureQuantity, Jobs, MachineJobs, WorkSetJobs, JobFeasibleDevices, NCProgramFiles, WorkSetCommand,
// ManualTransport, the tool tables, PresetResults, ToolInputOutputResults, MainProgramTools, SystemConfig,
// SystemParameters, DeviceParameters, UserSysData, CommonValues, OrderResults, OrderCompletedQtyResults and
// PartSerialNumberTracking. It has none on VehicleStatus, ToolAlarmHistory, AutomationErrors, MachineOnumbers,
// FixtureRunFinishStatus, ComprehensiveDeleteEvents, PalletStatus, Stockers, CleaningResults, WorkSetCommandQuantity,
// the auxiliary device status tables or the console's user tables. The install scripts grant no dashboard view either,
// but on real cells the views (ViewVehicleStatus, ViewOrders, …) are readable by that login, so the view fallbacks
// are the normal path there. MakinoMCP never reads the console's user tables (Users, UserCards, Groups, GroupMembers,
// GroupPermissions, Permissions and the CompSec user tables): console passwords are kept there in plain text.
public sealed partial class SqlMakinoReader
{
    /// <summary>The SystemParameters rows get_cell_overview reads beyond duty and recovery.</summary>
    private static readonly string[] OverviewSettings =
    [
        "TransportCancel", "PresetResultKeepMonth", "ToolInOutResultKeepMonth", "OrderManagement", "ExtraToolPalletTransport",
        "NCProgramAutoDownloadType", "InspectionType", "ADEFolder", "ADEErrorFolder", "MonitoringOutputFilename",
        "FTNOutFolder", "EnableFTNExport", "ITNOutFolder", "EnableITNExport",
    ];

    /// <summary>The UserSysData rows behind the scheduling rules.</summary>
    private static readonly string[] SchedulingNames =
    [
        "TransPriorityRule", "TransDevPriorityRule", "TransPriorityProcessSequenceAscend", "TransPriorityMcJobSequenceAscend",
    ];

    // The tool data purge's own fallbacks when the cell has no setting (months).
    internal const int DefaultPresetKeepMonths = 12;
    internal const int DefaultToolMoveKeepMonths = 3;

    // InspectionType bits: per machine CMM / gauge cycle (counted in MachineStatus); per process ones (counted in
    // JobInspectCycle, which the stock login can't read).
    private const int MachineCmmBit = 4;
    private const int MachineGageBit = 8;
    private const int ProcessInspectionBits = 1 | 2 | 0x20;

    internal const string ProcessInspectionNotRead =
        "per-process inspection countdowns (kept in JobInspectCycle, which MakinoMCP doesn't read)";

    private const int ProcessSequenceTerm = 6; // TransportOrderItems: process sequence
    private const int OrderPriorityTerm = 4;   // TransportOrderItems: order priority

    /// <summary>The software version (SystemConfig.DBVersion, CHAR padded: trimmed); null when unreadable or empty.</summary>
    private static async Task<string?> SoftwareVersionAsync(Session s, CancellationToken ct)
    {
        if (!s.Schema.Has("SystemConfig", "DBVersion")) return null;
        using var cmd = Command(s.Connection, "SELECT TOP 1 DBVersion FROM SystemConfig;");
        using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? NonEmpty(Str(r, "DBVersion")) : null;
    }

    /// <summary>True when transport to the device is cancelled: only on a cell that uses transport cancel, and not for
    /// a closed device (closed says more). Null otherwise.</summary>
    internal static bool? TransportCancelledFlag(bool cellUsesIt, bool? open, bool? prohibit) =>
        cellUsesIt && open != false && prohibit == true ? true : null;

    /// <summary>Months kept, from the setting; the purge's fallback, flagged assumed, when the cell has no row; null
    /// when the settings can't be read.</summary>
    internal static (int? Months, bool Assumed) KeepMonths(IReadOnlyDictionary<string, string>? system, string name, int fallback)
    {
        if (system is null) return (null, false);
        return ToInt(system.GetValueOrDefault(name)) is > 0 and var months ? (months, false) : (fallback, true);
    }

    /// <summary>Parts until the next inspection when the cell counts them per machine; null when it doesn't (the bit is
    /// off or the cycle is 0).</summary>
    internal static int? Countdown(bool counted, int? cycle, int? count) =>
        counted && cycle is > 0 ? Math.Max(0, cycle.Value - (count ?? 0)) : null;

    /// <summary>A machine's NC memory: FreePage is bytes (the console converts it so), shown in KB; the used shares only
    /// when the machine's size settings are set (0 is the default: not set). Null when the cell records nothing.</summary>
    internal static NcMemory? NcMemoryFrom(long? freeBytes, int? freeFiles, IReadOnlyDictionary<string, string>? device)
    {
        if (freeBytes is not > 0 && freeFiles is not > 0) return null;
        static int? Used(long? free, int? max) =>
            free is { } f && max is > 0 ? (int)Math.Clamp(Math.Round(100.0 * (max.Value - f) / max.Value), 0, 100) : null;
        return new NcMemory
        {
            FreeKB = freeBytes is > 0 ? freeBytes / 1024 : null,
            FreeFiles = freeFiles,
            MemoryUsedPercent = Used(freeBytes, ToInt(device?.GetValueOrDefault("MaxNCMemorySize"))),
            FilesUsedPercent = Used(freeFiles, ToInt(device?.GetValueOrDefault("MaxNCProgramFiles"))),
        };
    }

    /// <summary>The '|' list of codes the cell stores for a sort rule; an empty list when none is stored.</summary>
    internal static List<int?> RuleCodes(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(t => ToInt(t)).ToList();

    /// <summary>The transport scheduling rules (UserSysData), with the vendor's default order when the cell stores
    /// none; null when the runtime settings can't be read.</summary>
    internal static SchedulingRules? SchedulingRulesFrom(IReadOnlyDictionary<string, string>? runtime, IReadOnlyDictionary<string, string>? system)
    {
        if (runtime is null) return null;
        var stored = RuleCodes(runtime.GetValueOrDefault("TransPriorityRule"));
        var order = stored.Count > 0 ? stored : Codes.DefaultTransportOrder.Select(c => (int?)c).ToList();
        var devices = RuleCodes(runtime.GetValueOrDefault("TransDevPriorityRule"));
        var deviceOrder = devices.Count > 0 ? devices : Codes.DefaultTransportDeviceOrder.Select(c => (int?)c).ToList();
        // Order priority sorts by process priority when the cell doesn't manage orders.
        var noOrders = ToInt(system?.GetValueOrDefault("OrderManagement")) == 0;
        string Term(int? code) => code is null ? "code ?" : code == OrderPriorityTerm && noOrders
            ? "process priority (order management is off)" : Codes.TransportOrderTerm(code);
        var notUsed = Codes.DefaultTransportOrder.Where(c => !order.Contains(c)).Select(c => Codes.TransportOrderTerm(c)).ToList();
        string Direction(string name, string what) =>
            (ToInt(runtime.GetValueOrDefault(name)) ?? 0) == 1 ? $"lowest {what} first" : $"highest {what} first";
        return new SchedulingRules(order.Select(Term).ToList(), deviceOrder.Select(c => c is null ? "code ?" : Codes.TransportDeviceTerm(c)).ToList())
        {
            NotUsed = notUsed.Count > 0 ? notUsed : null,
            ProcessSequence = order.Contains(ProcessSequenceTerm)
                ? $"{Direction("TransPriorityProcessSequenceAscend", "process")}, then {Direction("TransPriorityMcJobSequenceAscend", "machining job")}"
                : null,
            Default = stored.Count == 0 ? true : null,
        };
    }

    /// <summary>The cell's file locations from SystemParameters; an export only when it is turned on (its folder, as
    /// the cell does, falls back from the ITN folder to the FTN folder). Null when nothing is set or the settings can't
    /// be read.</summary>
    internal static CellFiles? CellFilesFrom(IReadOnlyDictionary<string, string>? system)
    {
        if (system is null) return null;
        string? Get(string name) => NonEmpty(system.GetValueOrDefault(name)?.Trim() ?? "");
        var ftnFolder = Get("FTNOutFolder");
        var ftn = ToInt(Get("EnableFTNExport")) is > 0 and var f ? new ToolExport(Codes.FtnExportType(f)) { Folder = ftnFolder } : null;
        var itn = ToInt(Get("EnableITNExport")) is > 0 and var i ? new ToolExport(Codes.ItnExportType(i)) { Folder = Get("ITNOutFolder") ?? ftnFolder } : null;
        var files = new CellFiles
        {
            AdeFolder = Get("ADEFolder"), AdeErrorFolder = Get("ADEErrorFolder"),
            MonitoringOutputFile = Get("MonitoringOutputFilename"), FtnExport = ftn, ItnExport = itn,
        };
        return files == new CellFiles() ? null : files;
    }

    // DevicePositionStatus values compared against.
    internal const int PositionEmpty = 0;
    internal const int PositionReserved = 1;
    internal const int PositionFinished = 4;

    /// <summary>Whether an empty position (no pallet) is shown: when its state isn't empty, unless it is only reserved
    /// for a pallet that is on its way (a reserved pallet is set).</summary>
    internal static bool EmptyPositionShown(int? status, bool reservedPallet) =>
        status is { } s && s != PositionEmpty && !(s == PositionReserved && reservedPallet);

    /// <summary>Where the machine's extra tool pallet is (PalletLocation device type; 0 out of the system, NULL no row).</summary>
    private static string ExtraToolPalletPlaceSql(SchemaInfo schema, string table) =>
        schema.Has(table, "NeedExtraToolPalletID") && schema.Has("PalletLocation", "PalletID") && schema.Has("PalletLocation", "CurDeviceType")
            ? "(SELECT TOP 1 l.CurDeviceType FROM PalletLocation l WHERE l.PalletID = s.NeedExtraToolPalletID) AS ExtraToolPalletPlace"
            : "CAST(NULL AS int) AS ExtraToolPalletPlace";

    /// <summary>1 when the machine's extra tool pallet has a manual transport not finished or cancelled.</summary>
    private static string ExtraToolPalletTransportSql(SchemaInfo schema, string table) =>
        schema.Has(table, "NeedExtraToolPalletID") && schema.Has("ManualTransport", "PalletID") && schema.Has("ManualTransport", "Status")
            ? $"CASE WHEN EXISTS (SELECT 1 FROM ManualTransport t WHERE t.PalletID = s.NeedExtraToolPalletID AND t.Status NOT IN ({ClosedTransports})) THEN 1 ELSE 0 END AS ExtraToolPalletTransport"
            : "CAST(NULL AS int) AS ExtraToolPalletTransport";

    private static long? Long(SqlDataReader r, string column)
    {
        var i = r.GetOrdinal(column);
        if (r.IsDBNull(i)) return null;
        try { return Convert.ToInt64(r.GetValue(i), CultureInfo.InvariantCulture); }
        catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException) { return null; }
    }
}
