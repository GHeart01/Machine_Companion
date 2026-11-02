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
        if (!s.Schema.HasTable("DeviceStatusDaySummary")) return new UtilizationList(s.Now, false, []);

        var oee = s.Schema.Has("DeviceResultDaySummary", "OEE")
            ? "(SELECT TOP 1 o.OEE FROM DeviceResultDaySummary o WHERE o.DeviceID = d.DeviceID AND o.Date = d.Date)"
            : "CAST(NULL AS int)";
        // Days counts today as one: days = 1 is today so far.
        using var cmd = Command(s.Connection, $"""
            SELECT TOP (@take) d.DeviceID, d.Date, d.Down, d.Idle, d.Busy, d.Manual, d.Error, {oee} AS Oee
            FROM DeviceStatusDaySummary d
            WHERE d.DeviceID IS NOT NULL AND d.Date IS NOT NULL
              AND d.Date >= DATEADD(day, 1 - @days, CAST(CAST(@now AS date) AS datetime))
              AND (@device IS NULL OR d.DeviceID = @device)
            ORDER BY d.Date DESC, d.DeviceID;
            """);
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

    public async Task<ActivityList> GetRecentActivityAsync(int hours, string? device, CancellationToken ct)
    {
        await using var s = await OpenAsync(ct);
        var deviceId = FindDevice(s.Names(), device);

        // Each table's newest cap + 1 rows hold every row of the merged newest cap + 1, so the merge below is exact.
        const string window = "x.StartDateTime >= DATEADD(hour, -@hours, @now) AND (@device IS NULL OR x.DeviceID = @device)";
        const string pallet = "(SELECT TOP 1 p.PalletNumber FROM Pallets p WHERE p.PalletID = x.PalletID) AS PalletNumber";
        var sql = new List<string>
        {
            s.Schema.HasTable("MachineResults")
                ? $"SELECT TOP (@take) x.DeviceID, x.StartDateTime, x.FinishDateTime, {pallet}, x.OrderName, x.PartName, x.NCProgramFileName, x.FinishStatus, x.AlarmNumber, x.AbnormalFinish FROM MachineResults x WHERE {window} ORDER BY x.StartDateTime DESC;"
                : "SELECT TOP 0 1 AS DeviceID;",
            s.Schema.HasTable("WorkSetResults")
                ? $"SELECT TOP (@take) x.DeviceID, x.StartDateTime, x.FinishDateTime, {pallet}, x.FixtureNumber, x.UnclampOrderName, x.ClampOrderName, x.OperationStatus, x.ErrorCode FROM WorkSetResults x WHERE {window} ORDER BY x.StartDateTime DESC;"
                : "SELECT TOP 0 1 AS DeviceID;",
            s.Schema.HasTable("VehicleResults")
                ? $"SELECT TOP (@take) x.DeviceID, x.StartDateTime, x.FinishDateTime, {pallet}, x.LoadDevice, x.UnloadDevice, x.CommandType, x.FinishStatus FROM VehicleResults x WHERE {window} ORDER BY x.StartDateTime DESC;"
                : "SELECT TOP 0 1 AS DeviceID;",
        };
        using var cmd = Command(s.Connection, string.Join("\n", sql));
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
                finish is null ? null : $"code {Int(r, "FinishStatus") ?? 0}"));
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
            ? Join(Codes.WorkSetFinish(operationStatus), $"error {errorCode}")
            : Codes.WorkSetFinish(operationStatus);

    private static string Labelled(string label, string value) => value.Length > 0 ? $"{label} {value}" : "";

    private static string Or(string value, string fallback) => value.Length > 0 ? value : fallback;

    private static string Join(params string[] parts) => string.Join(", ", parts.Where(p => p.Length > 0));
}
