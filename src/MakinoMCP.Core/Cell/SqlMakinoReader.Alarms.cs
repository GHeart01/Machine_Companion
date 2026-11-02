using Microsoft.Data.SqlClient;

namespace MakinoMCP.Core.Cell;

public sealed partial class SqlMakinoReader
{
    public async Task<AlarmList> GetAlarmsAsync(int hours, string? device, bool openOnly, bool includeWarnings, CancellationToken ct)
    {
        await using var s = await OpenAsync(ct);
        var deviceId = FindDevice(s.Names(), device);

        s.Schema.Require("PMCAlarms", "CNCAlarms");

        // openOnly ignores hours on purpose: it lists every alarm still open (capped), however long ago it was raised.
        // Otherwise the window applies.
        var where = openOnly ? "ResetTime IS NULL" : "AlarmTime >= DATEADD(hour, -@hours, @now)";
        var union = AlarmUnion(s.Schema, where, includeWarnings);

        using var cmd = Command(s.Connection, $"""
            SELECT TOP (@take) Source, DeviceID, AlarmNumber, IsWarning, AlarmMessage, AlarmTime, ResetTime
            FROM ({union}) a
            ORDER BY AlarmTime DESC, Source, AlarmNumber;
            """);
        AddWindow(cmd, s.Now, deviceId, hours: Hours(hours));

        var rows = new List<Alarm>();
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var source = Str(r, "Source");
            var raised = Date(r, "AlarmTime")!.Value;
            var reset = Date(r, "ResetTime");
            var minutes = Math.Round(Math.Max(0, ((reset ?? s.Now) - raised).TotalMinutes), 1);
            rows.Add(new Alarm(
                source,
                s.NameOf(Int(r, "DeviceID") ?? 0),
                Int(r, "AlarmNumber") ?? 0,
                source == "PMC" ? Codes.PmcAlarmLevel(Bool(r, "IsWarning") == true) : "alarm",
                Str(r, "AlarmMessage"),
                raised,
                reset,
                minutes));
        }
        var (alarms, capped) = Cap(rows);
        return new AlarmList(s.Now, capped, alarms);
    }

    public async Task<AlarmSummary> GetAlarmSummaryAsync(int days, string? device, CancellationToken ct)
    {
        await using var s = await OpenAsync(ct);
        var deviceId = FindDevice(s.Names(), device);

        s.Schema.Require("PMCAlarms", "CNCAlarms");
        var union = AlarmUnion(s.Schema, "AlarmTime >= DATEADD(hour, -@hours, @now)", includeWarnings: true);

        // Grouped by number, not message: a message with values in it would otherwise split one alarm into many rows.
        using var cmd = Command(s.Connection, $"""
            SELECT TOP (@take) Source, DeviceID, AlarmNumber, MAX(AlarmMessage) AS AlarmMessage, COUNT(*) AS Times,
                   SUM(CAST(CASE WHEN ISNULL(ResetTime, @now) > AlarmTime
                                 THEN DATEDIFF(second, AlarmTime, ISNULL(ResetTime, @now)) ELSE 0 END AS bigint)) / 60.0 AS TotalMinutes,
                   MIN(AlarmTime) AS FirstSeen, MAX(AlarmTime) AS LastSeen
            FROM ({union}) a
            GROUP BY Source, DeviceID, AlarmNumber
            ORDER BY COUNT(*) DESC, MAX(AlarmTime) DESC;
            """);
        AddWindow(cmd, s.Now, deviceId, hours: Days(days) * 24);

        var rows = new List<AlarmSummaryRow>();
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            rows.Add(new AlarmSummaryRow(
                s.NameOf(Int(r, "DeviceID") ?? 0),
                Str(r, "Source"),
                Int(r, "AlarmNumber") ?? 0,
                Str(r, "AlarmMessage"),
                Int(r, "Times") ?? 0,
                Math.Round(Double(r, "TotalMinutes"), 1),
                Date(r, "FirstSeen")!.Value,
                Date(r, "LastSeen")!.Value));
        }
        var (summary, capped) = Cap(rows);
        return new AlarmSummary(s.Now, capped, summary);
    }

    /// <summary>PMC and CNC alarms as one row shape; CNC alarms have no warning level. Without IsWarning every PMC entry
    /// counts as an alarm, so includeWarnings has nothing to filter.</summary>
    internal static string AlarmUnion(SchemaInfo schema, string where, bool includeWarnings)
    {
        const string device = "(@device IS NULL OR DeviceID = @device)";
        var hasWarning = schema.Has("PMCAlarms", "IsWarning");
        var warnings = includeWarnings || !hasWarning ? "" : " AND IsWarning = 0";
        var pmc = string.Join(", ",
            "'PMC' AS Source", "DeviceID",
            Opt(schema, "PMCAlarms", "a", "AlarmNumber"),
            hasWarning ? "a.IsWarning AS IsWarning" : "CAST(0 AS bit) AS IsWarning",
            Opt(schema, "PMCAlarms", "a", "AlarmMessage", type: "nvarchar(4000)"),
            "AlarmTime", "ResetTime");
        var cnc = string.Join(", ",
            "'CNC'", "DeviceID",
            Opt(schema, "CNCAlarms", "a", "AlarmNumber"),
            "CAST(0 AS bit)",
            Opt(schema, "CNCAlarms", "a", "AlarmMessage", type: "nvarchar(4000)"),
            "AlarmTime", "ResetTime");
        return $"""
            SELECT {pmc} FROM PMCAlarms a WHERE AlarmTime IS NOT NULL AND {where} AND {device}{warnings}
            UNION ALL
            SELECT {cnc} FROM CNCAlarms a WHERE AlarmTime IS NOT NULL AND {where} AND {device}
            """;
    }

    /// <summary>The parameters every windowed query takes: the cap, the cell's clock, the device filter and the window.</summary>
    private void AddWindow(SqlCommand cmd, DateTime now, int? deviceId, int hours)
    {
        AddTake(cmd);
        cmd.Parameters.Add("@now", System.Data.SqlDbType.DateTime).Value = now;
        cmd.Parameters.Add("@device", System.Data.SqlDbType.Int).Value = (object?)deviceId ?? DBNull.Value;
        cmd.Parameters.Add("@hours", System.Data.SqlDbType.Int).Value = hours;
    }

    private void AddTake(SqlCommand cmd) => cmd.Parameters.Add("@take", System.Data.SqlDbType.Int).Value = Take;
}
