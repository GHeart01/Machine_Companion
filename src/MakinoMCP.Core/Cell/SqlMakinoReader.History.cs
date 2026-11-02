using System.Globalization;

namespace MakinoMCP.Core.Cell;

public sealed partial class SqlMakinoReader
{
    // MachineResults.FinishStatus holds the names of the cell software's EndOfProgramFinishStatus flags ("Normal",
    // "BrokenToolShort, ToolLife"); these are their bit values, which Codes.MachiningFinish puts into words.
    private static readonly Dictionary<string, int> FinishFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Normal"] = 0,
        ["M940"] = 0x1,
        ["BrokenToolShort"] = 0x10000,
        ["BrokenToolLong"] = 0x20000,
        ["AdaptiveControl"] = 0x40000,
        ["SpindleLoad"] = 0x80000,
        ["ToolLife"] = 0x100000,
        ["FixtureAlarm"] = 0x200000,
        ["SpareToolSelect"] = 0x400000,
        ["ToolPrepCheck"] = 0x800000,
        ["AbortSwitch"] = 0x1000000,
        ["SystemModeOff"] = 0x2000000,
        ["NotSeated"] = 0x4000000,
        ["WorkVacuum"] = 0x8000000,
    };

    public async Task<UtilizationList> GetUtilizationAsync(int days, string? device, CancellationToken ct)
    {
        await using var s = await OpenAsync(ct);
        var deviceId = FindDevice(s.Names(), device);
        s.Schema.Require("DeviceStatusDaySummary");

        using var cmd = Command(s.Connection, UtilizationSql(s.Schema));
        AddWindow(cmd, s.Now, deviceId, hours: Days(days) * 24);
        cmd.Parameters.Add("@days", System.Data.SqlDbType.Int).Value = Days(days);

        var rows = new List<UtilizationDay>();
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            double down = Double(r, "Down"), idle = Double(r, "Idle"), busy = Double(r, "Busy"),
                manual = Double(r, "Manual"), error = Double(r, "Error");
            var total = down + idle + busy + manual + error;
            rows.Add(new UtilizationDay(
                s.NameOf(Int(r, "DeviceID") ?? 0),
                Date(r, "Date")!.Value,
                ToHours(down), ToHours(idle), ToHours(busy), ToHours(manual), ToHours(error),
                total > 0 ? Math.Round(busy * 100 / total, 1) : 0,
                Int(r, "Oee")));
        }
        var (list, capped) = Cap(rows);
        return new UtilizationList(s.Now, capped, list);

        static double ToHours(double seconds) => Math.Round(seconds / 3600, 2);
    }

    /// <summary>Daily state seconds; Oee only from DeviceResultDaySummary, the one optional table. Days counts today
    /// as one: days = 1 is today so far.</summary>
    internal static string UtilizationSql(SchemaInfo schema)
    {
        const string t = "DeviceStatusDaySummary";
        var oee = schema.Has("DeviceResultDaySummary", "OEE")
            ? "(SELECT TOP 1 o.OEE FROM DeviceResultDaySummary o WHERE o.DeviceID = d.DeviceID AND o.Date = d.Date) AS Oee"
            : "CAST(NULL AS int) AS Oee";
        var cols = string.Join(", ",
            "d.DeviceID", "d.Date",
            Opt(schema, t, "d", "Down"), Opt(schema, t, "d", "Idle"), Opt(schema, t, "d", "Busy"),
            Opt(schema, t, "d", "Manual"), Opt(schema, t, "d", "Error"), oee);
        return $"""
            SELECT TOP (@take) {cols}
            FROM {t} d
            WHERE d.DeviceID IS NOT NULL AND d.Date IS NOT NULL
              AND d.Date >= DATEADD(day, 1 - @days, CAST(CAST(@now AS date) AS datetime))
              AND (@device IS NULL OR d.DeviceID = @device)
            ORDER BY d.Date DESC, d.DeviceID;
            """;
    }

    /// <summary>The newest cap + 1 rows of each result table, as three result sets (machining, work-set, transport).
    /// Each table's newest cap + 1 rows hold every row of the merged newest cap + 1, so merging them is exact.</summary>
    internal static string ActivitySql(SchemaInfo schema)
    {
        const string window = "x.StartDateTime >= DATEADD(hour, -@hours, @now) AND (@device IS NULL OR x.DeviceID = @device)";
        var pallet = PalletNumberSql(schema, "x.PalletID", "PalletNumber");

        string Select(string table, params (string Column, string Type)[] columns) =>
            $"SELECT TOP (@take) x.DeviceID, x.StartDateTime, x.FinishDateTime, {pallet}, "
            + string.Join(", ", columns.Select(c => Opt(schema, table, "x", c.Column, type: c.Type)))
            + $" FROM {table} x WHERE {window} ORDER BY x.StartDateTime DESC;";

        return string.Join("\n",
            Select("MachineResults", ("OrderName", Text), ("PartName", Text), ("NCProgramFileName", Text),
                ("FinishStatus", Text), ("AlarmNumber", Text), ("AbnormalFinish", "bit")),
            Select("WorkSetResults", ("FixtureNumber", "int"), ("UnclampOrderName", Text), ("ClampOrderName", Text),
                ("OperationStatus", "int"), ("ErrorCode", Text)),
            Select("VehicleResults", ("LoadDevice", Text), ("UnloadDevice", Text), ("CommandType", "int"),
                ("FinishStatus", "int")));
    }

    public async Task<ActivityList> GetRecentActivityAsync(int hours, string? device, CancellationToken ct)
    {
        await using var s = await OpenAsync(ct);
        var deviceId = FindDevice(s.Names(), device);

        s.Schema.Require("MachineResults", "WorkSetResults", "VehicleResults", "Pallets");

        using var cmd = Command(s.Connection, ActivitySql(s.Schema));
        AddWindow(cmd, s.Now, deviceId, hours: Hours(hours));

        var items = new List<ActivityItem>();
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var finish = Date(r, "FinishDateTime");
            items.Add(Item(r, "machining", s,
                Join(Labelled("order", Str(r, "OrderName")), Labelled("part", Str(r, "PartName")), Labelled("program", Str(r, "NCProgramFileName"))),
                finish is null ? null : MachiningResult(Str(r, "FinishStatus"), Str(r, "AlarmNumber"), Bool(r, "AbnormalFinish") == true)));
        }
        await r.NextResultAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var finish = Date(r, "FinishDateTime");
            items.Add(Item(r, "work-set", s,
                Join(Labelled("unclamp", Str(r, "UnclampOrderName")), Labelled("clamp", Str(r, "ClampOrderName")), Labelled("fixture", Str(r, "FixtureNumber"))),
                finish is null ? null : WorkSetResult(Int(r, "OperationStatus"), Str(r, "ErrorCode"))));
        }
        await r.NextResultAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var finish = Date(r, "FinishDateTime");
            var command = Codes.VehicleCommand(Int(r, "CommandType"));
            var route = $"{Or(Str(r, "LoadDevice"), "?")} → {Or(Str(r, "UnloadDevice"), "?")}";
            items.Add(Item(r, "transport", s,
                command.Length > 0 ? $"{route} ({command})" : route,
                finish is null ? null : CodeText(Int(r, "FinishStatus"))));
        }

        var newest = items.OrderByDescending(i => i.Start).ToList();
        var (list, capped) = Cap(newest);
        return new ActivityList(s.Now, capped, list);

        static ActivityItem Item(Microsoft.Data.SqlClient.SqlDataReader r, string kind, Session s, string detail, string? result) =>
            new(kind, s.NameOf(Int(r, "DeviceID") ?? 0), Date(r, "StartDateTime")!.Value, Date(r, "FinishDateTime"),
                Int(r, "PalletNumber"), detail, result);
    }

    /// <summary>A machining result in words: the finish flags (enum names or a number), plus the alarm number when the
    /// cycle ended abnormally.</summary>
    public static string MachiningResult(string? finishStatus, string? alarmNumber, bool abnormal)
    {
        var raw = (finishStatus ?? "").Trim();
        string words;
        if (raw.Length == 0) words = "";
        else if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)) words = Codes.MachiningFinish(number);
        else
        {
            var bits = 0;
            var unknown = new List<string>();
            foreach (var name in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (FinishFlags.TryGetValue(name, out var bit)) bits |= bit;
                else unknown.Add(name);
            }
            words = Join(unknown.Count == 0 || bits != 0 ? Codes.MachiningFinish(bits) : "", string.Join(", ", unknown));
        }

        var alarm = (alarmNumber ?? "").Trim();
        return abnormal && alarm.Length > 0 && alarm != "0" ? Join(words, $"alarm {alarm}") : words;
    }

    private static string WorkSetResult(int? operationStatus, string errorCode) =>
        errorCode.Length > 0 && errorCode != "0"
            ? Join(Codes.WorkSetOperation(operationStatus), $"error {errorCode}")
            : Codes.WorkSetOperation(operationStatus);

    private static string Labelled(string label, string value) => value.Length > 0 ? $"{label} {value}" : "";

    private static string Or(string value, string fallback) => value.Length > 0 ? value : fallback;

    private static string Join(params string[] parts) => string.Join(", ", parts.Where(p => p.Length > 0));
}
