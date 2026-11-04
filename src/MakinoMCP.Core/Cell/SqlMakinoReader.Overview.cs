using System.Text;
using Microsoft.Data.SqlClient;

namespace MakinoMCP.Core.Cell;

public sealed partial class SqlMakinoReader
{
    private const int MachineType = 1;
    private const int VehicleType = 2;
    private const int StationType = 3;

    /// <summary>Status columns not every MAS-A5 version has, with the words DeviceState.NotAvailable uses for them.
    /// PowerStatus (V3.0) is probed so an older cell is reported, though DeviceState has no field for it yet.</summary>
    private static readonly (string Table, string Column, string Words)[] OptionalStatus =
    [
        ("MachineStatus", "CNCMode", "CNC mode"),
        ("MachineStatus", "MachineAction", "machine action"),
        ("MachineStatus", "MachineSubAction", "machine sub-action"),
        ("MachineStatus", "TablePalletID", "table pallet"),
        ("MachineStatus", "FrontPalletID", "front pallet"),
        ("MachineStatus", "BackPalletID", "back pallet"),
        ("MachineStatus", "PowerStatus", "power status"),
        ("MachineStatus", "Status", "status"),
        ("MachineStatus", "Recovery", "recovery request"),
        ("MachineStatus", "ToolStatus", "tool status"),
        ("MachineStatus", "CNCRun", "CNC run state"),
        ("MachineStatus", "OperatorCall", "operator call"),
        ("MachineStatus", "RunJob", "run job"),
        ("MachineStatus", "MainONumber", "program"),
        ("MachineStatus", "ExecONumber", "executing program"),
        ("MachineStatus", "TableReservedPalletID", "next table pallet"),
        ("MachineStatus", "FrontReservedPalletID", "next front pallet"),
        ("MachineStatus", "BackReservedPalletID", "next back pallet"),
        ("WorkSetStatus", "WSSMode", "station mode"),
        ("WorkSetStatus", "DeviceSkip", "skip"),
        ("WorkSetStatus", "TablePalletID", "table pallet"),
        ("WorkSetStatus", "FrontPalletID", "front pallet"),
        ("WorkSetStatus", "PowerStatus", "power status"),
        ("WorkSetStatus", "Status", "status"),
        ("WorkSetStatus", "Recovery", "recovery request"),
        ("WorkSetStatus", "TableReservedPalletID", "next table pallet"),
        ("WorkSetStatus", "FrontReservedPalletID", "next front pallet"),
        ("VehicleStatus", "VHCMode", "vehicle mode"),
        ("VehicleStatus", "Status", "status"),
        ("VehicleStatus", "Recovery", "recovery request"),
        ("VehicleStatus", "CommandType", "command"),
        ("VehicleStatus", "FromDeviceName", "transport from"),
        ("VehicleStatus", "ToDeviceName", "transport to"),
        ("VehicleStatus", "Buffer1PalletID", "buffer pallets"),
        ("VehicleStatus", "Buffer1Status", "buffer status"),
        ("ViewVehicleStatus", "Status", "status"),
        ("ViewVehicleStatus", "Recovery", "recovery request"),
        ("ViewVehicleStatus", "Command", "command"),
        ("ViewVehicleStatus", "FromDevice", "transport from"),
        ("ViewVehicleStatus", "ToDevice", "transport to"),
        ("ViewVehicleStatus", "PalletNumber", "buffer pallets"),
    ];

    private static readonly string[] MachineSpots = ["Table", "Front", "Back"];
    private static readonly string[] StationSpots = ["Table", "Front"];

    private sealed record StatusRow(
        bool? Connected, bool? Emergency, int? Mode, bool? Skip, int? Action, int? SubAction, List<PalletSpot> Pallets,
        int? Status, bool? Recovery, int? ToolStatus, int? CncRun, int? OperatorCall, int? RunJob, NcProgram? Program);

    private sealed record VehicleRow(
        bool? Connected, bool? Emergency, int? Mode, int? Status, bool? Recovery, int? Command, string From, string To,
        List<PalletSpot> Pallets);

    public async Task<CellOverview> GetOverviewAsync(CancellationToken ct)
    {
        await using var s = await OpenAsync(ct);
        var schema = s.Schema;
        schema.Require("MachineStatus", "WorkSetStatus", "Pallets", "PMCAlarms", "CNCAlarms",
            "MachineResults", "WorkSetResults", "VehicleResults");

        // The vehicle's status and the history length are sections some cells (or logins) don't have.
        var vehicleSql = VehicleSql(schema);
        var historySql = HistoryDaysSql(schema);
        var sql = new StringBuilder();
        sql.AppendLine(StatusSql(schema, "MachineStatus", MachineSpots));
        sql.AppendLine(StatusSql(schema, "WorkSetStatus", StationSpots));
        sql.AppendLine(OpenAlarmSql(schema));
        sql.AppendLine(LastActivitySql());
        if (vehicleSql is not null) sql.AppendLine(vehicleSql);
        if (historySql is not null) sql.AppendLine(historySql);

        using var cmd = Command(s.Connection, sql.ToString());
        cmd.Parameters.Add("@now", System.Data.SqlDbType.DateTime).Value = s.Now;
        using var r = await cmd.ExecuteReaderAsync(ct);
        var machines = await ReadStatusAsync(r, MachineSpots, ct);
        await r.NextResultAsync(ct);
        var stations = await ReadStatusAsync(r, StationSpots, ct);
        await r.NextResultAsync(ct);
        var open = new Dictionary<int, (int Alarms, int Warnings)>();
        while (await r.ReadAsync(ct))
            open[Int(r, "DeviceID") ?? 0] = (Int(r, "Alarms") ?? 0, Int(r, "Warnings") ?? 0);
        await r.NextResultAsync(ct);
        var last = new Dictionary<int, DateTime>();
        while (await r.ReadAsync(ct))
        {
            if (Date(r, "LastFinish") is { } when) last[Int(r, "DeviceID") ?? 0] = when;
        }
        var vehicles = new Dictionary<int, VehicleRow>();
        if (vehicleSql is not null)
        {
            await r.NextResultAsync(ct);
            vehicles = await ReadVehiclesAsync(r, s.Devices, ct);
        }
        int? historyDays = null;
        if (historySql is not null)
        {
            await r.NextResultAsync(ct);
            if (await r.ReadAsync(ct)) historyDays = HistoryDays(Str(r, "ParameterValue"));
        }

        var devices = new List<DeviceState>();
        foreach (var d in s.Devices)
        {
            var (alarms, warnings) = open.GetValueOrDefault(d.Id);
            DateTime? lastActivity = last.TryGetValue(d.Id, out var when) ? when : null;
            var state = new DeviceState(d.Name, Codes.DeviceType(d.Type), null, null, null, null, null, [], alarms, warnings,
                lastActivity, []);

            if (d.Type == MachineType)
            {
                var missing = Missing(schema, "MachineStatus");
                state = machines.TryGetValue(d.Id, out var m)
                    ? state with
                    {
                        Connected = m.Connected, Emergency = m.Emergency, Mode = Words(m.Mode, Codes.CncMode),
                        Action = Words(m.Action, Codes.MachineAction), SubAction = Words(m.SubAction, Codes.MachineSubAction),
                        Pallets = m.Pallets, NotAvailable = missing,
                        Status = Words(m.Status, Codes.DeviceDashStatus), RecoveryRequest = m.Recovery,
                        ToolStatus = Words(m.ToolStatus, Codes.MachineToolStatus), CncRun = Words(m.CncRun, Codes.CncRun),
                        OperatorCall = Words(m.OperatorCall, Codes.OperatorCall), RunJob = Words(m.RunJob, Codes.RunJob),
                        Program = m.Program,
                    }
                    : state with { NotAvailable = missing };
            }
            else if (d.Type == StationType)
            {
                var missing = Missing(schema, "WorkSetStatus");
                state = stations.TryGetValue(d.Id, out var w)
                    ? state with
                    {
                        Connected = w.Connected, Emergency = w.Emergency, Mode = StationMode(w),
                        Pallets = w.Pallets, NotAvailable = missing,
                        Status = Words(w.Status, Codes.DeviceDashStatus), RecoveryRequest = w.Recovery,
                    }
                    : state with { NotAvailable = missing };
            }
            else if (d.Type == VehicleType)
            {
                var missing = VehicleMissing(schema);
                state = vehicles.TryGetValue(d.Id, out var v)
                    ? state with
                    {
                        Connected = v.Connected, Emergency = v.Emergency, Mode = Words(v.Mode, Codes.VehicleMode),
                        Action = Words(v.Command, Codes.VehicleCommand), Pallets = v.Pallets, NotAvailable = missing,
                        Status = Words(v.Status, Codes.DeviceDashStatus), RecoveryRequest = v.Recovery,
                        TransportFrom = v.From.Length > 0 ? v.From : null, TransportTo = v.To.Length > 0 ? v.To : null,
                    }
                    : state with { NotAvailable = missing };
            }
            devices.Add(state);
        }
        return new CellOverview(s.Now, devices) { HistoryDays = historyDays };
    }

    private static string? Words(int? code, Func<int?, string> words) => code is null ? null : words(code);

    private static string? StationMode(StatusRow w)
    {
        var mode = Words(w.Mode, Codes.WssMode);
        if (w.Skip != true) return mode;
        return mode is null ? "skipped" : $"{mode}, skipped";
    }

    internal static IReadOnlyList<string> Missing(SchemaInfo schema, string table) =>
        OptionalStatus.Where(o => o.Table == table && !schema.Has(table, o.Column)).Select(o => o.Words).ToList();

    /// <summary>What a vehicle can't show: the status table's missing columns; through the vendor view (when the login
    /// can't read the table) also the mode and buffer status the view leaves out; neither readable, the whole status.</summary>
    internal static IReadOnlyList<string> VehicleMissing(SchemaInfo schema)
    {
        if (schema.HasTable("VehicleStatus")) return Missing(schema, "VehicleStatus");
        if (schema.HasTable("ViewVehicleStatus")) return ["vehicle mode", "buffer status", .. Missing(schema, "ViewVehicleStatus")];
        return ["vehicle status"];
    }

    /// <summary>One status table, with each pallet spot's pallet number (and the one reserved to come next) looked up
    /// in Pallets.</summary>
    internal static string StatusSql(SchemaInfo schema, string table, string[] spots)
    {
        var machine = table == "MachineStatus";
        // Columns a table doesn't have by design (no sub-action on a station) are absent, the same as on old versions.
        var cols = new List<string>
        {
            "s.DeviceID",
            Opt(schema, table, "s", "Connected", type: "bit"),
            Opt(schema, table, "s", "Emergency", type: "bit"),
            Opt(schema, table, "s", machine ? "CNCMode" : "WSSMode", "ModeCode"),
            Opt(schema, table, "s", "MachineAction"),
            Opt(schema, table, "s", "MachineSubAction"),
            Opt(schema, table, "s", "DeviceSkip"),
            Opt(schema, table, "s", "Status"),
            Opt(schema, table, "s", "Recovery", type: "bit"),
            Opt(schema, table, "s", "ToolStatus"),
            Opt(schema, table, "s", "CNCRun"),
            Opt(schema, table, "s", "OperatorCall"),
            Opt(schema, table, "s", "RunJob"),
            Opt(schema, table, "s", "MainONumber"),
            Opt(schema, table, "s", "ExecONumber"),
        };
        foreach (var spot in spots)
        {
            var id = spot + "PalletID";
            cols.Add(Opt(schema, table, "s", id));
            cols.Add(PalletNumberOf(schema, table, id, spot + "PalletNumber"));
            cols.Add(Opt(schema, table, "s", spot + "Status"));
            var reserved = spot + "ReservedPalletID";
            cols.Add(Opt(schema, table, "s", reserved));
            cols.Add(PalletNumberOf(schema, table, reserved, spot + "ReservedPalletNumber"));
        }
        return $"SELECT {string.Join(", ", cols)} FROM {table} s;";
    }

    private static string PalletNumberOf(SchemaInfo schema, string table, string idColumn, string name) =>
        schema.Has(table, idColumn) ? PalletNumberSql(schema, $"s.{idColumn}", name) : $"CAST(NULL AS int) AS {name}";

    private static async Task<Dictionary<int, StatusRow>> ReadStatusAsync(SqlDataReader r, string[] spots, CancellationToken ct)
    {
        var rows = new Dictionary<int, StatusRow>();
        while (await r.ReadAsync(ct))
        {
            var pallets = new List<PalletSpot>();
            foreach (var spot in spots)
            {
                if (Int(r, spot + "PalletID") is not > 0) continue;
                pallets.Add(new PalletSpot(spot.ToLowerInvariant(), Int(r, spot + "PalletNumber"),
                    Codes.DevicePositionStatus(Int(r, spot + "Status"))));
            }
            foreach (var spot in spots)
            {
                if (Int(r, spot + "ReservedPalletID") is not > 0) continue;
                pallets.Add(new PalletSpot("next " + spot.ToLowerInvariant(), Int(r, spot + "ReservedPalletNumber"), "reserved"));
            }
            rows[Int(r, "DeviceID") ?? 0] = new StatusRow(
                Bool(r, "Connected"), Bool(r, "Emergency"), Int(r, "ModeCode"), Bool(r, "DeviceSkip"),
                Int(r, "MachineAction"), Int(r, "MachineSubAction"), pallets,
                Int(r, "Status"), Bool(r, "Recovery"), Int(r, "ToolStatus"), Int(r, "CNCRun"), Int(r, "OperatorCall"),
                Int(r, "RunJob"), Program(Int(r, "MainONumber"), Int(r, "ExecONumber")));
        }
        return rows;
    }

    /// <summary>A program pair as O-numbers; 0 or NULL means none, and neither gives null.</summary>
    internal static NcProgram? Program(int? main, int? executing)
    {
        static string? O(int? n) => n is > 0 ? $"O{n.Value:D4}" : null;
        var (m, e) = (O(main), O(executing));
        return m is null && e is null ? null : new NcProgram(m, e);
    }

    /// <summary>The vehicle's state: from VehicleStatus when the login can read it, else from the vendor's
    /// ViewVehicleStatus (no mode or buffer status; rows carry the device's number and name, not its id); null when
    /// neither is readable. Both give the same column names.</summary>
    internal static string? VehicleSql(SchemaInfo schema)
    {
        if (schema.HasTable("VehicleStatus"))
        {
            const string t = "VehicleStatus";
            string Buffer(int n) => schema.Has(t, $"Buffer{n}PalletID")
                ? PalletNumberSql(schema, $"v.Buffer{n}PalletID", $"Buffer{n}PalletNumber")
                : $"CAST(NULL AS int) AS Buffer{n}PalletNumber";
            var cols = string.Join(", ",
                "v.DeviceID", $"CAST(NULL AS {Text}) AS DeviceName", "CAST(NULL AS int) AS DeviceNumber",
                Opt(schema, t, "v", "Connected", type: "bit"),
                Opt(schema, t, "v", "Emergency", type: "bit"),
                Opt(schema, t, "v", "VHCMode"),
                Opt(schema, t, "v", "Status"),
                Opt(schema, t, "v", "Recovery", type: "bit"),
                Opt(schema, t, "v", "CommandType"),
                Opt(schema, t, "v", "FromDeviceName", type: Text),
                Opt(schema, t, "v", "ToDeviceName", type: Text),
                Buffer(1), Opt(schema, t, "v", "Buffer1Status"),
                Buffer(2), Opt(schema, t, "v", "Buffer2Status"));
            return $"SELECT {cols} FROM VehicleStatus v;";
        }
        if (schema.HasTable("ViewVehicleStatus"))
        {
            const string t = "ViewVehicleStatus";
            var cols = string.Join(", ",
                "CAST(NULL AS int) AS DeviceID",
                Opt(schema, t, "v", "DeviceName", type: Text),
                Opt(schema, t, "v", "DeviceNumber"),
                Opt(schema, t, "v", "Connected", type: "bit"),
                Opt(schema, t, "v", "Emergency", type: "bit"),
                "CAST(NULL AS int) AS VHCMode",
                Opt(schema, t, "v", "Status"),
                Opt(schema, t, "v", "Recovery", type: "bit"),
                Opt(schema, t, "v", "Command", "CommandType"),
                Opt(schema, t, "v", "FromDevice", "FromDeviceName", Text),
                Opt(schema, t, "v", "ToDevice", "ToDeviceName", Text),
                Opt(schema, t, "v", "PalletNumber", "Buffer1PalletNumber"), "CAST(NULL AS int) AS Buffer1Status",
                Opt(schema, t, "v", "PalletNumber2", "Buffer2PalletNumber"), "CAST(NULL AS int) AS Buffer2Status");
            return $"SELECT {cols} FROM ViewVehicleStatus v;";
        }
        return null;
    }

    private static async Task<Dictionary<int, VehicleRow>> ReadVehiclesAsync(
        SqlDataReader r, IReadOnlyList<DeviceRow> devices, CancellationToken ct)
    {
        var vehicles = devices.Where(d => d.Type == VehicleType).ToList();
        var rows = new Dictionary<int, VehicleRow>();
        while (await r.ReadAsync(ct))
        {
            // The view names the vehicle by number (and name) instead of id.
            var id = Int(r, "DeviceID");
            if (id is null)
            {
                var number = Int(r, "DeviceNumber");
                var name = Str(r, "DeviceName");
                id = (vehicles.FirstOrDefault(d => number is not null && d.Number == number)
                      ?? vehicles.FirstOrDefault(d => name.Length > 0 && string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)))?.Id;
                if (id is null) continue;
            }
            var pallets = new List<PalletSpot>();
            for (var n = 1; n <= 2; n++)
            {
                var pallet = Int(r, $"Buffer{n}PalletNumber");
                if (pallet is not > 0) continue;
                pallets.Add(new PalletSpot($"buffer {n}", pallet, CodeText(Int(r, $"Buffer{n}Status"))));
            }
            rows[id.Value] = new VehicleRow(
                Bool(r, "Connected"), Bool(r, "Emergency"), Int(r, "VHCMode"), Int(r, "Status"), Bool(r, "Recovery"),
                Int(r, "CommandType"), Str(r, "FromDeviceName"), Str(r, "ToDeviceName"), pallets);
        }
        return rows;
    }

    /// <summary>How long the cell keeps its history (days); null when SystemParameters or its columns are missing.</summary>
    internal static string? HistoryDaysSql(SchemaInfo schema) => SystemParameterSql(schema, "PreserveResults");

    /// <summary>One SystemParameters value (column ParameterValue); null when the table or its columns are missing.
    /// <paramref name="name"/> is always a constant from this code, never user input.</summary>
    private static string? SystemParameterSql(SchemaInfo schema, string name)
    {
        if (!schema.Has("SystemParameters", "ParameterName") || !schema.Has("SystemParameters", "ParameterValue")) return null;
        var order = schema.Has("SystemParameters", "SystemID") ? " ORDER BY SystemID" : "";
        return $"SELECT TOP 1 ParameterValue FROM SystemParameters WHERE ParameterName = '{name}'{order};";
    }

    /// <summary>The PreserveResults value as days; anything but a positive whole number is null.</summary>
    internal static int? HistoryDays(string? value) => ToInt(value) is int days && days > 0 ? days : null;

    // Open (not yet reset) alarms and warnings per device; without IsWarning every PMC entry counts as an alarm.
    private static string OpenAlarmSql(SchemaInfo schema)
    {
        var warning = schema.Has("PMCAlarms", "IsWarning") ? "IsWarning = 1" : "1 = 0";
        return $"""
            SELECT DeviceID, SUM(A) AS Alarms, SUM(W) AS Warnings FROM (
                SELECT DeviceID, CASE WHEN {warning} THEN 0 ELSE 1 END AS A, CASE WHEN {warning} THEN 1 ELSE 0 END AS W
                FROM PMCAlarms WHERE ResetTime IS NULL
                UNION ALL
                SELECT DeviceID, 1, 0 FROM CNCAlarms WHERE ResetTime IS NULL) a
            GROUP BY DeviceID;
            """;
    }

    /// <summary>Last activity per device: the latest finish over the three result tables, looking back 30 days only
    /// (so the overview doesn't scan years of results); no finish in 30 days reads as null.</summary>
    internal static string LastActivitySql()
    {
        var parts = new[] { "MachineResults", "WorkSetResults", "VehicleResults" }
            .Select(t => $"SELECT DeviceID, MAX(FinishDateTime) AS F FROM {t} WHERE FinishDateTime >= DATEADD(day, -30, @now) GROUP BY DeviceID");
        return $"SELECT DeviceID, MAX(F) AS LastFinish FROM ({string.Join(" UNION ALL ", parts)}) x GROUP BY DeviceID;";
    }
}
