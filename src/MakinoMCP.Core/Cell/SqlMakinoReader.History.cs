using System.Globalization;
using Microsoft.Data.SqlClient;

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

    /// <summary>SystemParameters: MachiningTimeExceptStopping (1: MachineResults.MachiningTime is the cycle without its
    /// stops, and the console's machine utilization counts that) and ProbePartCountMacroVariable (above 0: a probe's
    /// count lowers the fixture's part count at machining).</summary>
    private const string NetMachiningParameter = "MachiningTimeExceptStopping";

    private const string ProbeCountParameter = "ProbePartCountMacroVariable";

    internal const string ProbeCountNote =
        "This cell lowers a fixture's part count at machining to what its probe counted (ProbePartCountMacroVariable), " +
        "so fewer parts unclamped than were clamped can be by design, not a loss.";

    internal const string NetMachiningTime = "net machining time (cycle start to finish without its stops, spread over the pallet's time in the machine)";

    internal const string PrepToFinish = "prep start to finish (the whole time the pallet is in the machine for the job)";

    private const int SecondsPerDay = 24 * 60 * 60;

    public async Task<UtilizationList> GetUtilizationAsync(int days, string? device, ShiftFilter? shift, CancellationToken ct)
    {
        await using var s = await OpenAsync(ct);
        var deviceId = FindDevice(s.Names(), device);
        s.Schema.Require("DeviceStatusDaySummary");
        var settings = await SystemParametersAsync(s, ct, NetMachiningParameter);
        var net = settings is not null && settings.TryGetValue(NetMachiningParameter, out var v) && v.Trim() == "1";

        var rows = new List<UtilizationDay>();
        var types = s.Devices.ToDictionary(d => d.Id, d => d.Type);
        var ids = new List<int>();
        using (var cmd = Command(s.Connection, UtilizationSql(s.Schema, shift)))
        {
            AddWindow(cmd, s.Now, deviceId, hours: Days(days) * 24);
            cmd.Parameters.Add("@days", System.Data.SqlDbType.Int).Value = Days(days);
            if (shift is not null) AddShift(cmd, shift);

            using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                double down = Double(r, "Down"), idle = Double(r, "Idle"), busy = Double(r, "Busy"),
                    manual = Double(r, "Manual"), error = Double(r, "Error");
                var total = down + idle + busy + manual + error;
                var id = Int(r, "DeviceID") ?? 0;
                var spindle = Int(r, "SpindleRotationTime");
                var potential = Int(r, "PotentialProductionTime") is int p and > 0 ? p : SecondsPerDay;
                var machine = types.GetValueOrDefault(id) == MachineType;
                ids.Add(id);
                rows.Add(new UtilizationDay(
                    s.NameOf(id),
                    Date(r, "Date")!.Value,
                    ToHours(down), ToHours(idle), ToHours(busy), ToHours(manual), ToHours(error),
                    total > 0 ? Math.Round(busy * 100 / total, 1) : 0,
                    Int(r, "Oee"))
                {
                    Availability = Int(r, "Availability"),
                    Performance = Int(r, "Performance"),
                    Quality = Int(r, "Quality"),
                    SpindleHours = machine && spindle is not null ? ToHours(spindle.Value) : null,
                    SpindlePercent = machine && spindle is not null ? Math.Round(Math.Min(100, spindle.Value * 100.0 / potential), 1) : null,
                });
            }
        }
        var (list, capped) = Cap(rows);

        // The console's result-based utilization, per device and day, added to the rows above.
        var notAvailable = new List<string>();
        var effective = shift ?? new ShiftFilter(0, 0, ShiftFilter.AllDays);
        var results = new Dictionary<(int, DateTime), (double Seconds, int Count)>();
        var oldest = new Dictionary<int, DateTime?>();
        var tables = ResultTables(s.Schema);
        foreach (var table in new[] { "MachineResults", "WorkSetResults", "VehicleResults" }.Where(t => !tables.Contains(t)))
        {
            notAvailable.Add($"result figures from {table} (missing, or this login can't read it)");
        }
        if (tables.Count > 0 && list.Count > 0)
        {
            using var cmd = Command(s.Connection, ResultUtilizationSql(s.Schema, net));
            AddWindow(cmd, s.Now, deviceId, hours: Days(days) * 24);
            cmd.Parameters.Add("@days", System.Data.SqlDbType.Int).Value = Days(days);
            AddShift(cmd, effective);
            using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            oldest[MachineType] = tables.Contains("MachineResults") ? Date(r, "MachineFrom") : null;
            oldest[StationType] = tables.Contains("WorkSetResults") ? Date(r, "StationFrom") : null;
            oldest[VehicleType] = tables.Contains("VehicleResults") ? Date(r, "VehicleFrom") : null;
            await r.NextResultAsync(ct);
            while (await r.ReadAsync(ct))
            {
                results[(Int(r, "DeviceID") ?? 0, Date(r, "Day")!.Value.Date)] = (Double(r, "Seconds"), Int(r, "Results") ?? 0);
            }
        }

        var before = new SortedDictionary<int, DateTime>();
        var annotated = new List<UtilizationDay>(list.Count);
        for (var i = 0; i < list.Count; i++)
        {
            var row = list[i];
            var type = types.GetValueOrDefault(ids[i]);
            if (!oldest.TryGetValue(type, out var from) || from is null)
            {
                annotated.Add(row);
                continue;
            }
            if (row.Date.Date < from.Value.Date)
            {
                before[type] = from.Value.Date;
                annotated.Add(row);
                continue;
            }
            var window = WindowSeconds(row.Date.Date, effective, s.Now);
            var (seconds, count) = results.GetValueOrDefault((ids[i], row.Date.Date));
            annotated.Add(row with
            {
                ResultPercent = window > 0 ? Math.Round(Math.Min(100, seconds * 100 / window), 1) : 0,
                Results = count,
                AvgMinutes = count > 0 ? Math.Round(seconds / count / 60, 1) : null,
            });
        }
        foreach (var (type, from) in before)
        {
            notAvailable.Add($"result figures for {Codes.DeviceType(type)}s before {from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} " +
                             "(the oldest result this cell still keeps)");
        }
        if (shift is not null && !shift.WholeDay)
        {
            notAvailable.Add("shift hours for the status hours, busyPercent, spindle and OEE figures: the cell keeps them per " +
                             "whole day, so only the result figures follow the shift's hours");
        }

        return new UtilizationList(s.Now, capped, annotated)
        {
            Shift = shift?.Text,
            MachineResultTime = oldest.GetValueOrDefault(MachineType) is not null ? (net ? NetMachiningTime : PrepToFinish) : null,
            NotAvailable = notAvailable.Count > 0 ? notAvailable : null,
        };

        static double ToHours(double seconds) => Math.Round(seconds / 3600, 2);
    }

    /// <summary>The result tables this login can read with the columns the result figures need.</summary>
    private static HashSet<string> ResultTables(SchemaInfo schema) =>
        new[] { "MachineResults", "WorkSetResults", "VehicleResults" }
            .Where(t => schema.HasTable(t) && schema.Has(t, "DeviceID") && schema.Has(t, "StartDateTime") && schema.Has(t, "FinishDateTime"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many seconds of <paramref name="day"/> the shift counts, up to <paramref name="now"/>.</summary>
    internal static double WindowSeconds(DateTime day, ShiftFilter shift, DateTime now)
    {
        var weekday = ((int)day.DayOfWeek + 6) % 7;
        if ((shift.Weekdays & (1 << weekday)) == 0) return 0;
        var (f1, t1, f2, t2) = shift.Pieces;
        return Piece(f1, t1) + Piece(f2, t2);

        double Piece(int from, int to)
        {
            var start = day.AddSeconds(from);
            var end = day.AddSeconds(to);
            if (end > now) end = now;
            return end > start ? (end - start).TotalSeconds : 0;
        }
    }

    /// <summary>Daily state seconds, with OEE, its parts and the spindle time from DeviceResultDaySummary (optional).
    /// Days counts today as one: days = 1 is today so far. A shift's weekdays filter the days.</summary>
    internal static string UtilizationSql(SchemaInfo schema, ShiftFilter? shift = null)
    {
        const string t = "DeviceStatusDaySummary", rt = "DeviceResultDaySummary";
        string[] summary = ["OEE", "Availability", "Performance", "Quality", "SpindleRotationTime", "PotentialProductionTime"];
        var present = schema.Has(rt, "DeviceID") && schema.Has(rt, "Date")
            ? summary.Where(c => schema.Has(rt, c)).ToList()
            : [];
        string R(string column, string? name = null) =>
            present.Contains(column) ? $"o.{column} AS {name ?? column}" : $"CAST(NULL AS int) AS {name ?? column}";
        var apply = present.Count > 0
            ? $"OUTER APPLY (SELECT TOP 1 {string.Join(", ", present.Select(c => "x." + c))} FROM {rt} x " +
              "WHERE x.DeviceID = d.DeviceID AND x.Date = d.Date) o"
            : "";
        var cols = string.Join(", ",
            "d.DeviceID", "d.Date",
            Opt(schema, t, "d", "Down"), Opt(schema, t, "d", "Idle"), Opt(schema, t, "d", "Busy"),
            Opt(schema, t, "d", "Manual"), Opt(schema, t, "d", "Error"),
            R("OEE", "Oee"), R("Availability"), R("Performance"), R("Quality"), R("SpindleRotationTime"), R("PotentialProductionTime"));
        var weekdays = shift is null ? "" : $"\n  AND {OnWeekdaysSql("d.Date")}";
        return $"""
            SELECT TOP (@take) {cols}
            FROM {t} d
            {apply}
            WHERE d.DeviceID IS NOT NULL AND d.Date IS NOT NULL
              AND d.Date >= DATEADD(day, 1 - @days, CAST(CAST(@now AS date) AS datetime))
              AND (@device IS NULL OR d.DeviceID = @device){weekdays}
            ORDER BY d.Date DESC, d.DeviceID;
            """;
    }

    /// <summary>The console's result-based utilization, rebuilt as one SELECT (the vendor's report procedures are
    /// never run): first the oldest result each table keeps, then per device and day the seconds its results overlap
    /// the day's counted window (the whole day or the shift, never past @now) and how many results touch it.
    /// Machines count from prep start to finish (with <paramref name="netMachining"/>, the net machining time spread
    /// over that span, as the console does); a station visit is counted once, although the cell writes one row per
    /// fixture; transports run from pickup to set-down.</summary>
    internal static string ResultUtilizationSql(SchemaInfo schema, bool netMachining)
    {
        var tables = ResultTables(schema);
        var parts = new List<string>();
        const string filter = "x.FinishDateTime > @from AND x.StartDateTime < @now AND (@device IS NULL OR x.DeviceID = @device)";
        if (tables.Contains("MachineResults"))
        {
            // A prep start that is unset (the column's '' default reads 1900), after the cycle start or more than a day
            // before it is not trusted: the cycle start is used instead.
            var start = schema.Has("MachineResults", "PrepStartDateTime")
                ? "CASE WHEN x.PrepStartDateTime IS NULL OR x.PrepStartDateTime > x.StartDateTime " +
                  "OR x.PrepStartDateTime < DATEADD(day, -1, x.StartDateTime) THEN x.StartDateTime ELSE x.PrepStartDateTime END"
                : "x.StartDateTime";
            var weight = netMachining && schema.Has("MachineResults", "MachiningTime")
                ? $"CASE WHEN DATEDIFF(second, {start}, x.FinishDateTime) <= 0 THEN 1.0 " +
                  $"WHEN x.MachiningTime >= DATEDIFF(second, {start}, x.FinishDateTime) THEN 1.0 " +
                  $"WHEN x.MachiningTime <= 0 THEN 0.0 " +
                  $"ELSE CAST(x.MachiningTime AS float) / DATEDIFF(second, {start}, x.FinishDateTime) END"
                : "1.0";
            parts.Add($"SELECT x.DeviceID, {start} AS S, x.FinishDateTime AS E, {weight} AS W FROM MachineResults x WHERE {filter}");
        }
        if (tables.Contains("WorkSetResults"))
        {
            var visit = schema.Has("WorkSetResults", "PalletID") ? "x.DeviceID, x.StartDateTime, x.PalletID" : "x.DeviceID, x.StartDateTime";
            parts.Add($"SELECT x.DeviceID, x.StartDateTime AS S, MAX(x.FinishDateTime) AS E, 1.0 AS W FROM WorkSetResults x WHERE {filter} GROUP BY {visit}");
        }
        if (tables.Contains("VehicleResults"))
        {
            parts.Add($"SELECT x.DeviceID, x.StartDateTime AS S, x.FinishDateTime AS E, 1.0 AS W FROM VehicleResults x WHERE {filter}");
        }
        if (parts.Count == 0) throw new CellSchemaException(["MachineResults", "WorkSetResults", "VehicleResults"]);

        string From(string table, string name) =>
            tables.Contains(table) ? $"(SELECT MIN(StartDateTime) FROM {table}) AS {name}" : $"CAST(NULL AS datetime) AS {name}";
        const string digits = "(VALUES (0), (1), (2), (3), (4), (5), (6), (7), (8), (9))";
        return $"""
            DECLARE @from datetime = DATEADD(day, 1 - @days, CAST(CAST(@now AS date) AS datetime));
            SELECT {From("MachineResults", "MachineFrom")}, {From("WorkSetResults", "StationFrom")}, {From("VehicleResults", "VehicleFrom")};
            WITH n AS (
                SELECT a.k + 10 * b.k + 100 * c.k AS k FROM {digits} a(k) CROSS JOIN {digits} b(k) CROSS JOIN {digits} c(k)),
            w AS (
                SELECT d.Day, p.S, CASE WHEN p.E > @now THEN @now ELSE p.E END AS E
                FROM (SELECT DATEADD(day, -n.k, CAST(CAST(@now AS date) AS datetime)) AS Day FROM n WHERE n.k < @days) d
                CROSS APPLY (VALUES (DATEADD(second, @from1, d.Day), DATEADD(second, @to1, d.Day)),
                                    (DATEADD(second, @from2, d.Day), DATEADD(second, @to2, d.Day))) p(S, E)
                WHERE p.S < p.E AND p.S < @now AND {OnWeekdaysSql("d.Day")}),
            r AS (
                SELECT ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS Id, u.DeviceID, u.S, u.E, u.W
                FROM ({string.Join("\n        UNION ALL\n        ", parts)}) u
                WHERE u.S IS NOT NULL AND u.E IS NOT NULL AND u.E > u.S)
            SELECT r.DeviceID, w.Day,
                   SUM(CAST(DATEDIFF(second, CASE WHEN r.S > w.S THEN r.S ELSE w.S END,
                                             CASE WHEN r.E < w.E THEN r.E ELSE w.E END) AS float) * r.W) AS Seconds,
                   COUNT(DISTINCT r.Id) AS Results
            FROM r JOIN w ON r.S < w.E AND r.E > w.S
            GROUP BY r.DeviceID, w.Day;
            """;
    }

    /// <summary>True (SQL) when the date of <paramref name="column"/> is one of the shift's weekdays (@weekdays, bit 0
    /// Monday; 1900-01-01 was a Monday, so this doesn't depend on the server's DATEFIRST).</summary>
    internal static string OnWeekdaysSql(string column) =>
        $"(@weekdays & POWER(2, DATEDIFF(day, CAST('19000101' AS datetime), {column}) % 7)) <> 0";

    /// <summary>True (SQL) when <paramref name="column"/> falls in the shift: one of its weekdays, and a time of day in
    /// [@from1, @to1) or [@from2, @to2) (seconds after midnight).</summary>
    internal static string InShiftSql(string column)
    {
        var second = $"DATEDIFF(second, CAST(CAST({column} AS date) AS datetime), {column})";
        return $"({OnWeekdaysSql(column)} AND (({second} >= @from1 AND {second} < @to1) OR ({second} >= @from2 AND {second} < @to2)))";
    }

    /// <summary>The shift's parameters: @weekdays and the two pieces of a day.</summary>
    private static void AddShift(SqlCommand cmd, ShiftFilter shift)
    {
        var (f1, t1, f2, t2) = shift.Pieces;
        cmd.Parameters.Add("@weekdays", System.Data.SqlDbType.Int).Value = shift.Weekdays;
        cmd.Parameters.Add("@from1", System.Data.SqlDbType.Int).Value = f1;
        cmd.Parameters.Add("@to1", System.Data.SqlDbType.Int).Value = t1;
        cmd.Parameters.Add("@from2", System.Data.SqlDbType.Int).Value = f2;
        cmd.Parameters.Add("@to2", System.Data.SqlDbType.Int).Value = t2;
    }

    /// <summary>The newest cap + 1 rows of each result table, as three result sets (machining, work-set, transport).
    /// Each table's newest cap + 1 rows hold every row of the merged newest cap + 1, so merging them is exact.</summary>
    internal static string ActivitySql(SchemaInfo schema)
    {
        const string window = "x.StartDateTime >= DATEADD(hour, -@hours, @now) AND (@device IS NULL OR x.DeviceID = @device)";
        var pallet = PalletNumberSql(schema, "x.PalletID", "PalletNumber");

        string Select(string table, IEnumerable<string> columns) =>
            $"SELECT TOP (@take) x.DeviceID, x.StartDateTime, x.FinishDateTime, {pallet}, "
            + string.Join(", ", columns)
            + $" FROM {table} x WHERE {window} ORDER BY x.StartDateTime DESC;";

        IEnumerable<string> Cols(string table, params (string Column, string Type)[] columns) =>
            columns.Select(c => Opt(schema, table, "x", c.Column, type: c.Type));

        const string ws = "WorkSetResults";
        return string.Join("\n",
            Select("MachineResults", Cols("MachineResults", ("OrderName", Text), ("PartName", Text), ("NCProgramFileName", Text),
                ("FinishStatus", Text), ("AlarmNumber", Text), ("AbnormalFinish", "bit"),
                ("PrepStartDateTime", "datetime"), ("PrepFinishDateTime", "datetime"), ("SpindleTime", "int"),
                ("MachiningTime", "int"), ("PlannedMachiningTime", "int"))),
            Select(ws, Cols(ws, ("FixtureNumber", "int"), ("UnclampOrderName", Text), ("ClampOrderName", Text),
                    ("OperationType", "int"), ("OperationStatus", "int"), ("ErrorCode", Text), ("ReMachine", "bit"))
                .Append(OptSum(schema, ws, "ClampQuantityOpe", "Clamped"))
                .Append(OptSum(schema, ws, "UnclampNormalQtyOpe", "UnclampedGood"))
                .Append(OptSum(schema, ws, "UnclampScrapQtyOpe", "UnclampedScrap"))
                .Append(OptSum(schema, ws, "UnclampOutProcQtyOpe", "ToExternal"))),
            Select("VehicleResults", Cols("VehicleResults", ("LoadDevice", Text), ("UnloadDevice", Text), ("CommandType", "int"),
                ("FinishStatus", "int"))));
    }

    /// <summary>The sum of the per-operation columns <paramref name="prefix"/>1..4 this cell has, or NULL when it has
    /// none (cells before V3.4).</summary>
    private static string OptSum(SchemaInfo schema, string table, string prefix, string name)
    {
        var present = Enumerable.Range(1, 4).Select(i => prefix + i).Where(c => schema.Has(table, c)).ToList();
        return present.Count > 0
            ? $"({string.Join(" + ", present.Select(c => $"ISNULL(x.{c}, 0)"))}) AS {name}"
            : $"CAST(NULL AS int) AS {name}";
    }

    public async Task<ActivityList> GetRecentActivityAsync(int hours, string? device, CancellationToken ct)
    {
        await using var s = await OpenAsync(ct);
        var deviceId = FindDevice(s.Names(), device);

        s.Schema.Require("MachineResults", "WorkSetResults", "VehicleResults", "Pallets");
        var settings = await SystemParametersAsync(s, ct, NetMachiningParameter, ProbeCountParameter);
        var net = settings is not null && settings.TryGetValue(NetMachiningParameter, out var n) && n.Trim() == "1";
        var probeCount = settings is not null && settings.TryGetValue(ProbeCountParameter, out var p)
            && int.TryParse(p.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var macro) && macro > 0;

        using var cmd = Command(s.Connection, ActivitySql(s.Schema));
        AddWindow(cmd, s.Now, deviceId, hours: Hours(hours));

        var items = new List<ActivityItem>();
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var finish = Date(r, "FinishDateTime");
            items.Add(Item(r, "machining", s,
                Join(Labelled("order", Str(r, "OrderName")), Labelled("part", Str(r, "PartName")), Labelled("program", Str(r, "NCProgramFileName"))),
                finish is null ? null : MachiningResult(Str(r, "FinishStatus"), Str(r, "AlarmNumber"), Bool(r, "AbnormalFinish") == true))
                with
                {
                    PrepMinutes = PrepMinutes(Date(r, "PrepStartDateTime"), Date(r, "PrepFinishDateTime")),
                    SpindleMinutes = ResultMinutes(Int(r, "SpindleTime")),
                    NetMinutes = net ? ResultMinutes(Int(r, "MachiningTime")) : null,
                    PlannedMinutes = ResultMinutes(Int(r, "PlannedMachiningTime")),
                });
        }
        await r.NextResultAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var finish = Date(r, "FinishDateTime");
            var operation = Int(r, "OperationType");
            items.Add(Item(r, "work-set", s,
                Join(operation is null ? "" : Codes.WorkSetCommandOperation(operation),
                    Labelled("unclamp", Str(r, "UnclampOrderName")), Labelled("clamp", Str(r, "ClampOrderName")), Labelled("fixture", Str(r, "FixtureNumber"))),
                finish is null ? null : Join(WorkSetResult(Int(r, "OperationStatus"), Str(r, "ErrorCode")),
                    WorkSetCounts(operation, Int(r, "Clamped"), Int(r, "UnclampedGood"), Int(r, "UnclampedScrap"),
                        Int(r, "ToExternal"), Bool(r, "ReMachine") == true))));
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
        return new ActivityList(s.Now, capped, list) { Note = probeCount ? ProbeCountNote : null };

        static ActivityItem Item(SqlDataReader r, string kind, Session s, string detail, string? result) =>
            new(kind, s.NameOf(Int(r, "DeviceID") ?? 0), Date(r, "StartDateTime")!.Value, Date(r, "FinishDateTime"),
                Int(r, "PalletNumber"), detail, result);
    }

    /// <summary>A result's stored seconds as minutes with one decimal; 0, negative or none is null.</summary>
    internal static double? ResultMinutes(int? seconds) => seconds is > 0 ? Math.Round(seconds.Value / 60.0, 1) : null;

    /// <summary>The preparation before a machining cycle in minutes; null when either time is unset (the columns'
    /// '' default reads as 1900) or the span isn't positive.</summary>
    internal static double? PrepMinutes(DateTime? start, DateTime? finish)
    {
        if (start is not { Year: >= 2000 } from || finish is not { Year: >= 2000 } to || to <= from) return null;
        return Math.Round((to - from).TotalMinutes, 1);
    }

    /// <summary>A work-set result's part counts (summed over its operations), zeros left out: unclamped good (for an
    /// "other" operation the cell stores the fixture's parts there, so "parts N"), unclamped scrap, sent to an
    /// external process, clamped, and "re-machine" when the parts go back for machining.</summary>
    public static string WorkSetCounts(int? operationType, int? clamped, int? good, int? scrap, int? toExternal, bool reMachine)
    {
        const int other = 6; // WorkSetStationOperationType.Other
        return Join(
            good is > 0 ? (operationType == other ? $"parts {good}" : $"unclamped good {good}") : "",
            scrap is > 0 ? $"unclamped scrap {scrap}" : "",
            toExternal is > 0 ? $"to external process {toExternal}" : "",
            clamped is > 0 ? $"clamped {clamped}" : "",
            reMachine ? "re-machine" : "");
    }

    /// <summary>A machining result in words: the finish flags (enum names or a number), then, when the cycle ended
    /// abnormally, "abnormal finish" and its alarm number if it has one. An abnormal cycle never reads "normal".</summary>
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

        if (!abnormal) return words;
        var alarm = (alarmNumber ?? "").Trim();
        return Join(words == Codes.MachiningFinish(0) ? "" : words, "abnormal finish",
            alarm.Length > 0 && alarm != "0" ? $"alarm {alarm}" : "");
    }

    private static string WorkSetResult(int? operationStatus, string errorCode) =>
        errorCode.Length > 0 && errorCode != "0"
            ? Join(Codes.WorkSetOperation(operationStatus), $"error {errorCode}")
            : Codes.WorkSetOperation(operationStatus);

    private static string Labelled(string label, string value) => value.Length > 0 ? $"{label} {value}" : "";

    private static string Or(string value, string fallback) => value.Length > 0 ? value : fallback;

    private static string Join(params string[] parts) => string.Join(", ", parts.Where(p => p.Length > 0));
}
