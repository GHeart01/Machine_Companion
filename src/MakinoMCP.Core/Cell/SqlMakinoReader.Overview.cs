using System.Text;
using Microsoft.Data.SqlClient;

namespace MakinoMCP.Core.Cell;

public sealed partial class SqlMakinoReader
{
    private const int MachineType = 1;
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
        ("WorkSetStatus", "WSSMode", "station mode"),
        ("WorkSetStatus", "DeviceSkip", "skip"),
        ("WorkSetStatus", "TablePalletID", "table pallet"),
        ("WorkSetStatus", "FrontPalletID", "front pallet"),
        ("WorkSetStatus", "PowerStatus", "power status"),
    ];

    private static readonly string[] MachineSpots = ["Table", "Front", "Back"];
    private static readonly string[] StationSpots = ["Table", "Front"];

    private sealed record StatusRow(
        bool? Connected, bool? Emergency, int? Mode, bool? Skip, int? Action, int? SubAction, List<PalletSpot> Pallets);

    public async Task<CellOverview> GetOverviewAsync(CancellationToken ct)
    {
        await using var s = await OpenAsync(ct);
        var schema = s.Schema;
        var machineTable = schema.HasTable("MachineStatus");
        var stationTable = schema.HasTable("WorkSetStatus");

        var sql = new StringBuilder();
        sql.AppendLine(machineTable ? StatusSql(schema, "MachineStatus", MachineSpots) : "SELECT TOP 0 1 AS DeviceID;");
        sql.AppendLine(stationTable ? StatusSql(schema, "WorkSetStatus", StationSpots) : "SELECT TOP 0 1 AS DeviceID;");
        sql.AppendLine(OpenAlarmSql(schema));
        sql.AppendLine(LastActivitySql(schema));

        using var cmd = Command(s.Connection, sql.ToString());
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

        var devices = new List<DeviceState>();
        foreach (var d in s.Devices)
        {
            var (alarms, warnings) = open.GetValueOrDefault(d.Id);
            DateTime? lastActivity = last.TryGetValue(d.Id, out var when) ? when : null;
            var state = new DeviceState(d.Name, Codes.DeviceType(d.Type), null, null, null, null, null, [], alarms, warnings,
                lastActivity, []);

            if (d.Type == MachineType)
            {
                var missing = Missing(schema, "MachineStatus", machineTable);
                state = machines.TryGetValue(d.Id, out var m)
                    ? state with
                    {
                        Connected = m.Connected, Emergency = m.Emergency, Mode = Words(m.Mode, Codes.CncMode),
                        Action = Words(m.Action, Codes.MachineAction), SubAction = Words(m.SubAction, Codes.MachineSubAction),
                        Pallets = m.Pallets, NotAvailable = missing,
                    }
                    : state with { NotAvailable = missing };
            }
            else if (d.Type == StationType)
            {
                var missing = Missing(schema, "WorkSetStatus", stationTable);
                state = stations.TryGetValue(d.Id, out var w)
                    ? state with
                    {
                        Connected = w.Connected, Emergency = w.Emergency, Mode = StationMode(w),
                        Pallets = w.Pallets, NotAvailable = missing,
                    }
                    : state with { NotAvailable = missing };
            }
            devices.Add(state);
        }
        return new CellOverview(s.Now, devices);
    }

    private static string? Words(int? code, Func<int?, string> words) => code is null ? null : words(code);

    private static string? StationMode(StatusRow w)
    {
        var mode = Words(w.Mode, Codes.WssMode);
        if (w.Skip != true) return mode;
        return mode is null ? "skipped" : $"{mode}, skipped";
    }

    private static IReadOnlyList<string> Missing(SchemaInfo schema, string table, bool tableExists) =>
        !tableExists
            ? ["status"]
            : OptionalStatus.Where(o => o.Table == table && !schema.Has(table, o.Column)).Select(o => o.Words).ToList();

    // One status table, with each pallet spot's pallet number looked up in Pallets.
    private static string StatusSql(SchemaInfo schema, string table, string[] spots)
    {
        var machine = table == "MachineStatus";
        // Columns a table doesn't have by design (no sub-action on a station) are absent, the same as on old versions.
        var cols = new List<string>
        {
            "s.DeviceID", "s.Connected", "s.Emergency",
            Opt(schema, table, "s", machine ? "CNCMode" : "WSSMode", "ModeCode"),
            Opt(schema, table, "s", "MachineAction"),
            Opt(schema, table, "s", "MachineSubAction"),
            Opt(schema, table, "s", "DeviceSkip"),
        };
        foreach (var spot in spots)
        {
            var id = spot + "PalletID";
            cols.Add(Opt(schema, table, "s", id));
            cols.Add(schema.Has(table, id)
                ? $"(SELECT TOP 1 p.PalletNumber FROM Pallets p WHERE p.PalletID = s.{id}) AS {spot}PalletNumber"
                : $"CAST(NULL AS int) AS {spot}PalletNumber");
            cols.Add(Opt(schema, table, "s", spot + "Status"));
        }
        return $"SELECT {string.Join(", ", cols)} FROM {table} s;";
    }

    private static async Task<Dictionary<int, StatusRow>> ReadStatusAsync(SqlDataReader r, string[] spots, CancellationToken ct)
    {
        var rows = new Dictionary<int, StatusRow>();
        while (await r.ReadAsync(ct))
        {
            var pallets = new List<PalletSpot>();
            foreach (var spot in spots)
            {
                if (Int(r, spot + "PalletID") is not > 0) continue;
                var status = Int(r, spot + "Status");
                pallets.Add(new PalletSpot(spot.ToLowerInvariant(), Int(r, spot + "PalletNumber"), status is null ? "" : $"code {status}"));
            }
            rows[Int(r, "DeviceID") ?? 0] = new StatusRow(
                Bool(r, "Connected"), Bool(r, "Emergency"), Int(r, "ModeCode"), Bool(r, "DeviceSkip"),
                Int(r, "MachineAction"), Int(r, "MachineSubAction"), pallets);
        }
        return rows;
    }

    // Open (not yet reset) alarms and warnings per device.
    private static string OpenAlarmSql(SchemaInfo schema)
    {
        var parts = new List<string>();
        if (schema.HasTable("PMCAlarms"))
            parts.Add("SELECT DeviceID, CASE WHEN IsWarning = 1 THEN 0 ELSE 1 END AS A, CASE WHEN IsWarning = 1 THEN 1 ELSE 0 END AS W FROM PMCAlarms WHERE ResetTime IS NULL");
        if (schema.HasTable("CNCAlarms"))
            parts.Add("SELECT DeviceID, 1 AS A, 0 AS W FROM CNCAlarms WHERE ResetTime IS NULL");
        return parts.Count == 0
            ? "SELECT TOP 0 1 AS DeviceID, 0 AS Alarms, 0 AS Warnings;"
            : $"SELECT DeviceID, SUM(A) AS Alarms, SUM(W) AS Warnings FROM ({string.Join(" UNION ALL ", parts)}) a GROUP BY DeviceID;";
    }

    // Last activity per device: the latest finish over the three result tables.
    private static string LastActivitySql(SchemaInfo schema)
    {
        var parts = new[] { "MachineResults", "WorkSetResults", "VehicleResults" }
            .Where(schema.HasTable)
            .Select(t => $"SELECT DeviceID, MAX(FinishDateTime) AS F FROM {t} GROUP BY DeviceID")
            .ToList();
        return parts.Count == 0
            ? "SELECT TOP 0 1 AS DeviceID, CAST(NULL AS datetime) AS LastFinish;"
            : $"SELECT DeviceID, MAX(F) AS LastFinish FROM ({string.Join(" UNION ALL ", parts)}) x GROUP BY DeviceID;";
    }
}
