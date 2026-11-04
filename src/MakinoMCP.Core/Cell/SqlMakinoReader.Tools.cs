using System.Globalization;
using Microsoft.Data.SqlClient;

namespace MakinoMCP.Core.Cell;

// Tools: FunctionalToolData (FTN, the tool type) and its cutters, IndividualToolData (ITN, the physical tool) and its
// cutters, ToolLocation (where each ITN is), and the three tool logs: PresetResults (every change to a tool's data,
// old and new values), ToolAlarmHistory and ToolInputOutputResults (tools put into or taken out of a machine).
public sealed partial class SqlMakinoReader
{
    public static readonly IReadOnlyList<string> ToolFilterNames = ["problems", "all", "near_end", "alarm", "absent"];

    private const string Itd = "IndividualToolData";
    private const string Itc = "IndividualToolCutterData";
    private const string Ftd = "FunctionalToolData";
    private const string Ftc = "FunctionalToolCutterData";
    private const string Loc = "ToolLocation";
    private const string Preset = "PresetResults";
    private const string AlarmHistory = "ToolAlarmHistory";
    private const string InOut = "ToolInputOutputResults";


    // ToolAlarmHistory's alarm bit columns: the cutter's columns, except that the long broken-tool bit is BT2Alarm.
    private static readonly (string Column, int Flag)[] HistoryAlarmColumns =
    [
        ("AlarmTool", 0x1), ("TLAlarm", 0x2), ("TLWarning", 0x4), ("BTSAlarm", 0x8), ("BT2Alarm", 0x10), ("ACAlarm", 0x20),
        ("SLAlarm", 0x40),
    ];

    private enum PairKind { Status, Mm, Alarm }

    // The PresetResults Old/New pairs get_tool_changes compares (the life pairs are handled apart: one per life type).
    private static readonly (string Label, string Column, PairKind Kind)[] ChangePairs =
    [
        ("status", "Status", PairKind.Status),
        ("length", "Length", PairKind.Mm), ("diameter", "Diameter", PairKind.Mm),
        ("H geometry", "HGeometry", PairKind.Mm), ("D geometry", "DGeometry", PairKind.Mm),
        ("H wear", "HWear", PairKind.Mm), ("D wear", "DWear", PairKind.Mm),
        ("length wear", "LengthWear", PairKind.Mm), ("diameter wear", "DiameterWear", PairKind.Mm),
        ("R geometry", "RGeometry", PairKind.Mm), ("R wear", "RWear", PairKind.Mm),
        ("corner R", "CornerR", PairKind.Mm), ("corner R wear", "CornerRWear", PairKind.Mm),
        .. Codes.ToolAlarmColumns.Select(c => (Codes.ToolAlarms(c.Flag)[0], c.Column, PairKind.Alarm)),
    ];

    private static readonly string[] LifeKinds = ["Second", "Distance", "Count"];

    // --- shared words and numbers ---

    /// <summary>The filter get_tools applies: the named one, or by default problems (all when one tool is asked for).
    /// An unknown name throws ArgumentException listing the valid ones.</summary>
    public static string ToolFilter(string? filter, int? tool)
    {
        if (string.IsNullOrWhiteSpace(filter)) return tool is null ? "problems" : "all";
        var wanted = filter.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
        if (ToolFilterNames.Contains(wanted)) return wanted;
        throw new ArgumentException($"No filter named '{filter.Trim()}'. Use one of: {string.Join(", ", ToolFilterNames)}.");
    }

    /// <summary>One cutter's life in words and numbers. Time types (0 seconds, 3 counted in tenths on the control) are
    /// stored in seconds and shown in minutes; distance (m on a mm cell, inch on an inch cell) and count as stored.
    /// <paramref name="remain"/>: the cell counts life down (ActualToolLife is what remains) instead of up (what is
    /// used); the warning compares with ActualToolLife in the same direction. Limit 0 is a tool without life
    /// management: no percentage. Null when the cell doesn't give the limit.</summary>
    internal static LifeInfo? Life(int? lifeType, int? limit, int? warning, int? actual, bool remain, string lengthUnit = "mm")
    {
        if (limit is null) return null;
        if (limit <= 0) return new LifeInfo { Note = "not life-managed" };

        var (unit, perMinute) = lifeType switch
        {
            0 or 3 => ("min", true),
            1 => (lengthUnit == "inch" ? "inch" : "m", false),
            2 => ("count", false),
            _ => (CodeText(lifeType) is { Length: > 0 } code ? code : null, false),
        };
        double? Scale(double? value) => value is null ? null : perMinute ? Math.Round(value.Value / 60, 1) : value;

        double full = limit.Value;
        double? warnAt = warning is > 0 ? (remain ? full - warning.Value : warning.Value) : null;
        var life = new LifeInfo { Unit = unit, Limit = Scale(full), WarnAtUsed = Scale(warnAt) };
        if (actual is not { } a) return life;

        var used = Math.Max(0, remain ? full - a : a);
        var remaining = Math.Max(0, remain ? a : full - a);
        return life with { Used = Scale(used), Remaining = Scale(remaining), UsedPercent = Math.Round(used * 100 / full, 1) };
    }

    /// <summary>How this cell keeps tool data: Remain, life counts down (ActualToolLife is what remains) instead of up;
    /// LengthUnit, the tool offset unit lengths are stored in (1/100000 of it).</summary>
    internal sealed record ToolSettings(bool Remain, bool LifeAssumed, string LengthUnit, bool UnitAssumed);

    /// <summary>The settings from SystemParameters.ToolLifeCountType (enum ToolLifeCount: 0 accumulate, 1 remain) and
    /// ToolOffsetUnit (enum DeviceUnit: 0-4 mm, 10-14 inch). Missing or anything else: counts up, mm, both assumed.</summary>
    internal static ToolSettings ToolSettingsFrom(string? lifeCountType, string? toolOffsetUnit)
    {
        var (remain, lifeAssumed) = ToInt(lifeCountType) switch { 0 => (false, false), 1 => (true, false), _ => (false, true) };
        var (unit, unitAssumed) = ToInt(toolOffsetUnit) switch
        {
            >= 0 and <= 4 => ("mm", false),
            >= 10 and <= 14 => ("inch", false),
            _ => ("mm", true),
        };
        return new ToolSettings(remain, lifeAssumed, unit, unitAssumed);
    }

    internal static string? ToolSettingsSql(SchemaInfo schema)
    {
        if (!schema.Has("SystemParameters", "ParameterName") || !schema.Has("SystemParameters", "ParameterValue")) return null;
        var order = schema.Has("SystemParameters", "SystemID") ? "SystemID" : "ParameterName";
        return "SELECT ParameterName, ParameterValue FROM SystemParameters " +
               $"WHERE ParameterName IN ('ToolLifeCountType', 'ToolOffsetUnit') ORDER BY {order};";
    }

    private static async Task<ToolSettings> ToolSettingsAsync(Session s, CancellationToken ct)
    {
        if (ToolSettingsSql(s.Schema) is not { } sql) return ToolSettingsFrom(null, null);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = Command(s.Connection, sql);
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) values.TryAdd(Str(r, "ParameterName"), Str(r, "ParameterValue")); // first system wins
        return ToolSettingsFrom(values.GetValueOrDefault("ToolLifeCountType"), values.GetValueOrDefault("ToolOffsetUnit"));
    }

    private static bool? Assumed(bool assumed) => assumed ? true : null;

    /// <summary>A length, diameter or offset (stored in 1/100000 of the cell's unit) with 3 decimals in mm, 4 in inch;
    /// 0 (not measured) is null.</summary>
    internal static double? Length(int? raw, string unit) =>
        raw is null or 0 ? null : Math.Round(raw.Value / 100000.0, unit == "inch" ? 4 : 3);

    private static string LengthText(int? raw, string unit) =>
        ((raw ?? 0) / 100000.0).ToString(unit == "inch" ? "0.0000" : "0.000", CultureInfo.InvariantCulture);

    /// <summary>A tool's place: "MC1 pot 20" on a machine, "CRIB1 (tool crib)" elsewhere; pot 0 is left out.</summary>
    internal static string Place(string name, int? type, int? pot)
    {
        var place = type is null or MachineType ? name : $"{name} ({Codes.DeviceType(type)})";
        return pot is > 0 ? $"{place} pot {pot}" : place;
    }

    private static string PlaceOf(Session s, int? deviceId, string fallbackName, int? pot)
    {
        if (deviceId is null or 0) return fallbackName.Length > 0 ? Place(fallbackName, null, pot) : "unknown";
        var d = s.Devices.FirstOrDefault(x => x.Id == deviceId);
        if (d is not null) return Place(d.Name, d.Type, pot);
        return Place(fallbackName.Length > 0 ? fallbackName : $"device {deviceId}", null, pot);
    }

    private static string DeviceName(Session s, int? deviceId) => deviceId is null or 0 ? "unknown" : s.NameOf(deviceId.Value);

    private static string? NonEmpty(string text) => text.Length > 0 ? text : null;

    private static string Any(IEnumerable<string?> conditions)
    {
        var list = conditions.OfType<string>().ToList();
        return list.Count == 0 ? "1 = 0" : string.Join(" OR ", list);
    }

    // --- status as the System Console shows it ---

    // The cutter alarm bits that make a cutter (and its tool) an alarm: every one but the life warning.
    private static IEnumerable<string> CutterAlarmBits(SchemaInfo schema) =>
        Codes.ToolAlarmColumns.Select(c => c.Column).Where(c => c is not ("AlarmTool" or "TLWarning") && schema.Has(Itc, c));

    /// <summary>A status derived the way the System Console shows it, not just the stored one: an alarm bit set →
    /// alarm (4); else a stored status other than ready is kept (offset error, alarm, life warning, unknown); else the
    /// life warning bit → life warning (2); else the stored status (ready), or 0 unknown when there is none.</summary>
    internal static int DerivedStatus(bool alarm, int? stored, bool warning) =>
        alarm ? 4 : stored is { } kept && kept != 1 ? kept : warning ? 2 : stored ?? 0;

    /// <summary><see cref="DerivedStatus"/> for a tool (alias i) in SQL: AlarmTool or any cutter alarm bit (cb, the
    /// OUTER APPLY in <see cref="ToolFromSql"/>) first. "0" (unknown) when this cell has none of the columns.</summary>
    internal static string DerivedStatusSql(SchemaInfo schema)
    {
        var status = schema.Has(Itd, "Status");
        var alarm = new List<string>();
        if (schema.Has(Itd, "AlarmTool")) alarm.Add("i.AlarmTool = 1");
        if (CutterAlarmBits(schema).Any()) alarm.Add("cb.AnyAlarm = 1");
        var whens = new List<string>();
        if (alarm.Count > 0) whens.Add($"WHEN {string.Join(" OR ", alarm)} THEN 4");
        if (status) whens.Add("WHEN i.Status <> 1 THEN i.Status");
        if (schema.Has(Itc, "TLWarning")) whens.Add("WHEN cb.AnyWarning = 1 THEN 2");
        return whens.Count == 0 ? "0" : $"CASE {string.Join(" ", whens)} ELSE {(status ? "ISNULL(i.Status, 0)" : "0")} END";
    }

    /// <summary>The tools with their tool type, place and (cb) whether any cutter has an alarm or a life warning bit.</summary>
    private static string ToolFromSql(SchemaInfo schema)
    {
        var alarm = Any(CutterAlarmBits(schema).Select(b => $"ca.{b} = 1"));
        var warning = schema.Has(Itc, "TLWarning") ? "ca.TLWarning = 1" : "1 = 0";
        return "IndividualToolData i LEFT JOIN FunctionalToolData f ON f.FTNID = i.FTNID LEFT JOIN ToolLocation l ON l.ITNID = i.ITNID"
               + $" OUTER APPLY (SELECT MAX(CASE WHEN {alarm} THEN 1 ELSE 0 END) AS AnyAlarm,"
               + $" MAX(CASE WHEN {warning} THEN 1 ELSE 0 END) AS AnyWarning"
               + " FROM IndividualToolCutterData ca WHERE ca.ITNID = i.ITNID) cb";
    }

    // --- get_tools ---

    public async Task<ToolList> GetToolsAsync(string? device, string? filter, int? tool, CancellationToken ct)
    {
        var chosen = ToolFilter(filter, tool);
        await using var s = await OpenAsync(ct);
        var deviceId = FindDevice(s.Names(), device);
        s.Schema.Require(Itd, Itc, Ftd, Ftc, Loc);
        var settings = await ToolSettingsAsync(s, ct);

        using var cmd = Command(s.Connection, ToolSummarySql(s.Schema) + "\n" + ToolsSql(s.Schema, chosen));
        AddTake(cmd);
        cmd.Parameters.Add("@device", System.Data.SqlDbType.Int).Value = (object?)deviceId ?? DBNull.Value;
        cmd.Parameters.Add("@tool", System.Data.SqlDbType.Int).Value = (object?)tool ?? DBNull.Value;
        cmd.Parameters.Add("@remain", System.Data.SqlDbType.Int).Value = settings.Remain ? 1 : 0;
        using var r = await cmd.ExecuteReaderAsync(ct);

        var counts = new List<(int? Device, int? Status, int N)>();
        while (await r.ReadAsync(ct)) counts.Add((Int(r, "DeviceID"), Int(r, "Status"), Int(r, "N") ?? 0));

        await r.NextResultAsync(ct);
        var tools = new List<(int Id, ToolInfo Tool)>();
        while (await r.ReadAsync(ct))
        {
            var flags = new List<string>();
            if (Bool(r, "AlarmTool") == true) flags.Add("alarm tool");
            if (Bool(r, "ProhibitTool") == true) flags.Add("prohibited");
            if (Bool(r, "AbsentTool") == true) flags.Add("absent");
            var allocated = Int(r, "AllocatedDeviceID");
            tools.Add((Int(r, "ITNID") ?? 0, new ToolInfo(
                Int(r, "ITN") ?? 0, Int(r, "FTN"), Str(r, "FTNComment"),
                PlaceOf(s, Int(r, "CurrentDeviceID"), Str(r, "CurrentDeviceName"), Int(r, "CurrentPot")),
                Codes.ToolStatus(Int(r, "Status")), [])
            {
                ItnComment = NonEmpty(Str(r, "ITNComment")),
                Flags = flags.Count > 0 ? flags : null,
                MovingTo = allocated is > 0 ? PlaceOf(s, allocated, "", Int(r, "AllocatedPot")) : null,
                LastUsed = Int(r, "LastUsedDate") is > 0 and var used ? used : null,
                LastUsedProgram = Int(r, "LastUsedProgram") is > 0 and var program ? $"O{program:D4}" : null,
            }));
        }

        await r.NextResultAsync(ct);
        var cutters = new Dictionary<int, List<(CutterInfo Cutter, int? Kind)>>();
        while (await r.ReadAsync(ct))
        {
            var flags = 0;
            foreach (var (column, flag) in Codes.ToolAlarmColumns)
            {
                if (column != "AlarmTool" && Bool(r, column) == true) flags |= flag;
            }
            var alarms = Codes.ToolAlarms(flags);
            var status = DerivedStatus((flags & ~0x4) != 0, Int(r, "CutterStatus"), (flags & 0x4) != 0); // 0x4: life warning
            var cutter = new CutterInfo(Int(r, "CutterNo") ?? 0, Codes.ToolStatus(status))
            {
                Alarms = alarms.Count > 0 ? alarms : null,
                Life = Life(Int(r, "ToolLifeType"), Int(r, "ToolLife"), Int(r, "ToolLifeWarning"), Int(r, "ActualToolLife"),
                    settings.Remain, settings.LengthUnit),
                Length = Length(Int(r, "Length"), settings.LengthUnit),
                Diameter = Length(Int(r, "Diameter"), settings.LengthUnit),
            };
            var id = Int(r, "ITNID") ?? 0;
            if (!cutters.TryGetValue(id, out var list)) cutters[id] = list = [];
            list.Add((cutter, Int(r, "Kind")));
        }

        var picked = tools.Select(t =>
        {
            var own = cutters.GetValueOrDefault(t.Id) ?? [];
            var kind = own.Count > 0 && own[0].Kind is > 0 ? Codes.ToolKind(own[0].Kind) : null;
            return t.Tool with { Cutters = own.Select(c => c.Cutter).ToList(), Kind = kind };
        }).ToList();
        var (shown, capped) = Cap(picked);
        return new ToolList(s.Now, capped, chosen, settings.LengthUnit, Summary(s, counts), shown)
        {
            LifeDirectionAssumed = Assumed(settings.LifeAssumed), LengthUnitAssumed = Assumed(settings.UnitAssumed),
            NotAvailable = ToolFilterSql(s.Schema, chosen) is null ? [FilterNotAvailable(chosen)] : null,
        };
    }

    private static List<DeviceToolCount> Summary(Session s, List<(int? Device, int? Status, int N)> counts)
    {
        int Rank(int? id)
        {
            for (var i = 0; i < s.Devices.Count; i++) if (s.Devices[i].Id == id) return i;
            return int.MaxValue;
        }
        return counts
            .GroupBy(c => c.Device)
            .OrderBy(g => Rank(g.Key)).ThenBy(g => g.Key)
            .Select(g => new DeviceToolCount(
                g.Key is null ? "no location" : DeviceName(s, g.Key),
                g.Sum(c => c.N),
                g.OrderBy(c => c.Status ?? -1)
                    .GroupBy(c => c.Status is null ? "unknown" : Codes.ToolStatus(c.Status))
                    .ToDictionary(x => x.Key, x => x.Sum(c => c.N))))
            .ToList();
    }

    /// <summary>Every tool counted per device (its current place) and derived status; the device filter applies, the
    /// tool filter doesn't. At most devices × statuses rows.</summary>
    internal static string ToolSummarySql(SchemaInfo schema) => $"""
        SELECT DeviceID, Status, COUNT(*) AS N FROM (
            SELECT l.CurrentDeviceID AS DeviceID, {DerivedStatusSql(schema)} AS Status
            FROM {ToolFromSql(schema)}
            WHERE (@device IS NULL OR l.CurrentDeviceID = @device)) x
        GROUP BY DeviceID, Status;
        """;

    /// <summary>Two result sets: the tools <paramref name="filter"/> picks (cap + 1, most severe first, then by place),
    /// and the cutters of exactly those tools. Expects @take, @device, @tool and @remain (1: the cell counts life down).</summary>
    internal static string ToolsSql(SchemaInfo schema, string filter)
    {
        var where = "(@device IS NULL OR l.CurrentDeviceID = @device) AND (@tool IS NULL OR i.ITN = @tool OR f.FTN = @tool)"
                    + $" AND ({ToolFilterSql(schema, filter) ?? "1 = 0"})";
        var status = DerivedStatusSql(schema);
        var from = ToolFromSql(schema);
        var pot = schema.Has(Loc, "CurrentPot") ? "l.CurrentPot" : "0";
        var order = $"CASE {status} WHEN 4 THEN 0 WHEN 3 THEN 1 WHEN 2 THEN 2 ELSE 3 END, l.CurrentDeviceID, {pot}, i.ITN, i.ITNID";

        var toolColumns = string.Join(", ",
            "i.ITNID", "i.ITN", "f.FTN",
            Opt(schema, Ftd, "f", "FTNComment", type: Text),
            Opt(schema, Itd, "i", "ITNComment", type: Text),
            $"{status} AS Status",
            Opt(schema, Itd, "i", "AlarmTool", type: "bit"),
            Opt(schema, Itd, "i", "ProhibitTool", type: "bit"),
            Opt(schema, Itd, "i", "AbsentTool", type: "bit"),
            Opt(schema, Itd, "i", "LastUsedDate"),
            Opt(schema, Itd, "i", "LastUsedProgram"),
            "l.CurrentDeviceID",
            Opt(schema, Loc, "l", "CurrentPot"),
            Opt(schema, Loc, "l", "CurrentDeviceName", type: Text),
            Opt(schema, Loc, "l", "AllocatedDeviceID"),
            Opt(schema, Loc, "l", "AllocatedPot"));
        var cutterColumns = string.Join(", ",
            [
                "cu.ITNID", "cu.CutterNo",
                Opt(schema, Itc, "cu", "CutterStatus"),
                .. Codes.ToolAlarmColumns.Where(c => c.Column != "AlarmTool").Select(c => Opt(schema, Itc, "cu", c.Column, type: "bit")),
                Opt(schema, Itc, "cu", "ActualToolLife"),
                Opt(schema, Itc, "cu", "Length"),
                Opt(schema, Itc, "cu", "Diameter"),
                Opt(schema, Ftc, "cf", "ToolLifeType"),
                Opt(schema, Ftc, "cf", "ToolLife"),
                Opt(schema, Ftc, "cf", "ToolLifeWarning"),
                Opt(schema, Ftc, "cf", "Kind"),
            ]);
        return $"""
            SELECT TOP (@take) {toolColumns}
            FROM {from}
            WHERE {where}
            ORDER BY {order};
            SELECT {cutterColumns}
            FROM IndividualToolCutterData cu
            JOIN IndividualToolData it ON it.ITNID = cu.ITNID
            LEFT JOIN FunctionalToolCutterData cf ON cf.FTNID = it.FTNID AND cf.CutterNo = cu.CutterNo
            WHERE cu.ITNID IN (SELECT TOP (@take) i.ITNID FROM {from} WHERE {where} ORDER BY {order})
            ORDER BY cu.ITNID, cu.CutterNo;
            """;
    }

    /// <summary>What a filter needs that this cell lacks, for NotAvailable when <see cref="ToolFilterSql"/> is null.</summary>
    internal static string FilterNotAvailable(string filter) => filter switch
    {
        "absent" => "absent flag",
        "alarm" => "tool status and alarm columns",
        "near_end" => "tool status and life columns",
        _ => "tool status, flag and alarm columns",
    } + $" (the {filter} filter couldn't be applied)";

    /// <summary>The WHERE condition for a get_tools filter, over the tool (alias i, its derived status) and, through
    /// EXISTS, its cutters (c, with their FTN cutter data fc). Conditions on columns this cell lacks are left out; null
    /// when none is left (the filter can't be applied on this cell).</summary>
    internal static string? ToolFilterSql(SchemaInfo schema, string filter)
    {
        var derived = DerivedStatusSql(schema);
        var status = derived != "0";
        var cutterStatus = schema.Has(Itc, "CutterStatus");
        var bits = Codes.ToolAlarmColumns.Select(c => c.Column).Where(c => c != "AlarmTool" && schema.Has(Itc, c)).ToList();
        var life = schema.Has(Itc, "ActualToolLife") && schema.Has(Ftc, "ToolLife");
        const string used = "CAST(CASE WHEN @remain = 1 THEN fc.ToolLife - c.ActualToolLife ELSE c.ActualToolLife END AS bigint)";

        string? ToolBit(string column) => schema.Has(Itd, column) ? $"i.{column} = 1" : null;
        string? Worn(int percent) => life ? $"(fc.ToolLife > 0 AND {used} * 100 >= {percent} * CAST(fc.ToolLife AS bigint))" : null;
        var pastWarning = life && schema.Has(Ftc, "ToolLifeWarning")
            ? "(fc.ToolLife > 0 AND fc.ToolLifeWarning > 0 AND ((@remain = 0 AND c.ActualToolLife >= fc.ToolLifeWarning)"
              + " OR (@remain = 1 AND c.ActualToolLife <= fc.ToolLifeWarning)))"
            : null;
        string? Cutter(IEnumerable<string?> conditions)
        {
            var any = Any(conditions);
            return any == "1 = 0" ? null
                : "EXISTS (SELECT 1 FROM IndividualToolCutterData c LEFT JOIN FunctionalToolCutterData fc "
                  + $"ON fc.FTNID = i.FTNID AND fc.CutterNo = c.CutterNo WHERE c.ITNID = i.ITNID AND ({any}))";
        }

        var condition = filter switch
        {
            "all" => "1 = 1",
            "absent" => Any([ToolBit("AbsentTool")]),
            "alarm" => Any([status ? $"{derived} = 4" : null]),
            "near_end" => Any([
                status ? $"{derived} = 2" : null,
                Cutter([cutterStatus ? "c.CutterStatus = 2" : null, bits.Contains("TLWarning") ? "c.TLWarning = 1" : null,
                    Worn(80), pastWarning]),
            ]),
            "problems" => Any([
                status ? $"{derived} IN (2, 3, 4)" : null, ToolBit("AlarmTool"), ToolBit("AbsentTool"), ToolBit("ProhibitTool"),
                Cutter([cutterStatus ? "c.CutterStatus IN (2, 3, 4)" : null, .. bits.Select(b => $"c.{b} = 1"), Worn(90)]),
            ]),
            _ => throw new ArgumentOutOfRangeException(nameof(filter), filter, "not a tool filter"),
        };
        return condition == "1 = 0" ? null : condition;
    }

    /// <summary>For get_part_routing: how many usable tools (stored status ready or life warning, the rule the cell's
    /// own lacking-tool check uses) of each of the FTNs <c>@ftn0 … @ftn{n-1}</c> sit on each device. Null when this cell
    /// lacks a column the count needs (it can't be made, rather than made wrong).</summary>
    internal static string? UsableToolsSql(SchemaInfo schema, int ftnCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ftnCount, 1);
        (string Table, string Column)[] needed =
        [
            (Itd, "ITNID"), (Itd, "FTNID"), (Itd, "Status"), (Ftd, "FTNID"), (Ftd, "FTN"), (Loc, "ITNID"), (Loc, "CurrentDeviceID"),
        ];
        if (needed.Any(n => !schema.Has(n.Table, n.Column))) return null;
        var names = string.Join(", ", Enumerable.Range(0, ftnCount).Select(n => $"@ftn{n}"));
        return $"""
            SELECT l.CurrentDeviceID AS DeviceID, f.FTN, COUNT(*) AS Usable
            FROM IndividualToolData i
            JOIN FunctionalToolData f ON f.FTNID = i.FTNID
            JOIN ToolLocation l ON l.ITNID = i.ITNID
            WHERE i.Status IN (1, 2) AND f.FTN IN ({names})
            GROUP BY l.CurrentDeviceID, f.FTN;
            """;
    }

    /// <summary>Usable tools (see <see cref="UsableToolsSql"/>) per (device id, FTN) for the given FTNs, on the
    /// session's connection; FTNs with none anywhere are absent from the result. Null when the count can't be made on
    /// this cell (a column missing).</summary>
    private static async Task<Dictionary<(int DeviceId, int Ftn), int>?> UsableToolCountsAsync(
        Session s, IReadOnlyCollection<int> ftns, CancellationToken ct)
    {
        s.Schema.Require(Itd, Ftd, Loc);
        var counts = new Dictionary<(int, int), int>();
        foreach (var chunk in ftns.Distinct().Chunk(500))
        {
            if (UsableToolsSql(s.Schema, chunk.Length) is not { } sql) return null;
            using var cmd = Command(s.Connection, sql);
            for (var n = 0; n < chunk.Length; n++) cmd.Parameters.Add($"@ftn{n}", System.Data.SqlDbType.Int).Value = chunk[n];
            using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                counts[(Int(r, "DeviceID") ?? 0, Int(r, "FTN") ?? 0)] = Int(r, "Usable") ?? 0;
        }
        return counts;
    }

    /// <summary>The same, on a connection of its own (for tests and callers outside a session).</summary>
    internal async Task<Dictionary<(int DeviceId, int Ftn), int>?> GetUsableToolCountsAsync(
        IReadOnlyCollection<int> ftns, CancellationToken ct)
    {
        await using var s = await OpenAsync(ct);
        return await UsableToolCountsAsync(s, ftns, ct);
    }

    // --- get_tool_changes ---

    public async Task<ToolChangeList> GetToolChangesAsync(int days, string? device, int? tool, bool includeLifeUpdates, CancellationToken ct)
    {
        await using var s = await OpenAsync(ct);
        var deviceId = FindDevice(s.Names(), device);
        s.Schema.Require(Preset);
        var settings = await ToolSettingsAsync(s, ct);

        using var cmd = Command(s.Connection, ToolChangesSql(s.Schema, includeLifeUpdates));
        AddToolWindow(cmd, s.Now, days, deviceId, tool, settings.Remain);
        var rows = new List<ToolChange>();
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var words = ChangeWords(s.Schema, c => Int(r, c), Int(r, "ToolLifeType"), settings.Remain, settings.LengthUnit);
            rows.Add(new ToolChange(Date(r, "PresetDateTime")!.Value, DeviceName(s, Int(r, "DeviceID")), Int(r, "ITN") ?? 0,
                Int(r, "FTN"), words)
            {
                Cutter = Int(r, "TotalCutter") is > 1 ? Int(r, "CutterNo") : null,
                User = NonEmpty(Str(r, "Username")),
            });
        }
        var (list, capped) = Cap(rows);
        return new ToolChangeList(s.Now, capped, settings.LengthUnit, list)
        {
            LifeUpdatesLeftOut = !includeLifeUpdates, LifeDirectionAssumed = Assumed(settings.LifeAssumed),
            LengthUnitAssumed = Assumed(settings.UnitAssumed),
        };
    }

    private static bool HasPair(SchemaInfo schema, string column) =>
        schema.Has(Preset, column + "Old") && schema.Has(Preset, column + "New");

    private static string LifeKind(int? lifeType) => lifeType switch { 1 => "Distance", 2 => "Count", _ => "Second" };

    /// <summary>Tool data changes, newest first, cap + 1. Without <paramref name="includeLifeUpdates"/> only rows where
    /// something besides the life count changed, the life was reset (moved against its counting direction), or the
    /// life was changed outside a tool change (M6Flag 0). Expects @take, @now, @days, @device, @tool and @remain.</summary>
    internal static string ToolChangesSql(SchemaInfo schema, bool includeLifeUpdates)
    {
        var pairs = ChangePairs.Where(p => HasPair(schema, p.Column)).Select(p => p.Column).ToList();
        var lives = LifeKinds.Select(k => $"ActualToolLife{k}").Where(c => HasPair(schema, c)).ToList();
        var columns = string.Join(", ",
            [
                "p.PresetDateTime", "p.DeviceID", "p.ITN", "p.FTN",
                Opt(schema, Preset, "p", "TotalCutter"), Opt(schema, Preset, "p", "CutterNo"),
                Opt(schema, Preset, "p", "Username", type: Text), Opt(schema, Preset, "p", "ToolLifeType"),
                .. pairs.Concat(lives).SelectMany(c => new[] { $"p.{c}Old", $"p.{c}New" }),
            ]);
        var where = "p.PresetDateTime >= DATEADD(day, -@days, @now) AND (@device IS NULL OR p.DeviceID = @device)"
                    + " AND (@tool IS NULL OR p.ITN = @tool OR p.FTN = @tool)";
        if (!includeLifeUpdates)
        {
            var lifeMoved = lives.Select(c => $"p.{c}Old <> p.{c}New").ToList();
            var keep = Any([
                .. pairs.Select(c => $"p.{c}Old <> p.{c}New"),
                .. lives.Select(c => $"(@remain = 0 AND p.{c}New < p.{c}Old) OR (@remain = 1 AND p.{c}New > p.{c}Old)"),
                schema.Has(Preset, "M6Flag") && lifeMoved.Count > 0 ? $"(p.M6Flag = 0 AND ({string.Join(" OR ", lifeMoved)}))" : null,
            ]);
            where += $" AND ({keep})";
        }
        return $"""
            SELECT TOP (@take) {columns}
            FROM PresetResults p
            WHERE {where}
            ORDER BY p.PresetDateTime DESC;
            """;
    }

    /// <summary>What changed in one PresetResults row, in words: status, lengths and offsets (in
    /// <paramref name="lengthUnit"/>), alarm bits (on/off) and each life count in its unit, marked "(reset)" when it
    /// moved against its counting direction; a routine tick (with the counting direction) is left out when something
    /// else changed too. A missing old or new value reads "unknown"; an unknown life type shows raw numbers with
    /// "code N", as <see cref="Life"/> does. Never empty: a row with nothing shown changed reads "tool data updated".
    /// Only pairs this cell has are read, through <paramref name="value"/> (column name → value).</summary>
    internal static List<string> ChangeWords(SchemaInfo schema, Func<string, int?> value, int? lifeType, bool remain, string lengthUnit)
    {
        static string Shown(int? v, Func<int?, string> text) => v is null ? "unknown" : text(v);

        var words = new List<string>();
        foreach (var (label, column, kind) in ChangePairs)
        {
            if (!HasPair(schema, column)) continue;
            int? old = value(column + "Old"), now = value(column + "New");
            if (old == now) continue;
            words.Add(kind switch
            {
                PairKind.Status => $"{label} {Shown(old, v => Codes.ToolStatus(v))} → {Shown(now, v => Codes.ToolStatus(v))}",
                PairKind.Mm =>
                    $"{label} {Shown(old, v => LengthText(v, lengthUnit))} → {Shown(now, v => LengthText(v, lengthUnit))} {lengthUnit}",
                _ => $"{label} {(now == 1 ? "on" : "off")}",
            });
        }

        var others = words.Count;
        var unknownType = lifeType is not null and not (>= 0 and <= 3);
        foreach (var kind in LifeKinds)
        {
            var column = $"ActualToolLife{kind}";
            if (!HasPair(schema, column)) continue;
            int? old = value(column + "Old"), now = value(column + "New");
            if (old == now) continue;
            var reset = remain ? now > old : now < old;
            if (!reset && others > 0) continue;

            var minutes = kind == "Second" && !unknownType;
            var unit = unknownType ? CodeText(lifeType)
                : kind switch { "Second" => "min", "Count" => "count", _ => lengthUnit == "inch" ? "inch" : "m" };
            string Text(int? v) => Shown(v, x => minutes
                ? (x!.Value / 60.0).ToString("0.0", CultureInfo.InvariantCulture)
                : x!.Value.ToString(CultureInfo.InvariantCulture));
            words.Add($"life {(remain ? "remaining" : "used")} {Text(old)} → {Text(now)} {unit}{(reset ? " (reset)" : "")}");
        }
        if (words.Count == 0) words.Add("tool data updated");
        return words;
    }

    private void AddToolWindow(SqlCommand cmd, DateTime now, int days, int? deviceId, int? tool, bool remain)
    {
        AddTake(cmd);
        cmd.Parameters.Add("@now", System.Data.SqlDbType.DateTime).Value = now;
        cmd.Parameters.Add("@days", System.Data.SqlDbType.Int).Value = Days(days);
        cmd.Parameters.Add("@device", System.Data.SqlDbType.Int).Value = (object?)deviceId ?? DBNull.Value;
        cmd.Parameters.Add("@tool", System.Data.SqlDbType.Int).Value = (object?)tool ?? DBNull.Value;
        cmd.Parameters.Add("@remain", System.Data.SqlDbType.Int).Value = remain ? 1 : 0;
    }

    // --- get_tool_alarms ---

    public async Task<ToolAlarmList> GetToolAlarmsAsync(int days, string? device, int? tool, CancellationToken ct)
    {
        await using var s = await OpenAsync(ct);
        var deviceId = FindDevice(s.Names(), device);
        var (sql, source, notAvailable) = ToolAlarmsSql(s.Schema);
        var history = s.Schema.HasTable(AlarmHistory);
        var settings = await ToolSettingsAsync(s, ct);
        var (remain, unit) = (settings.Remain, settings.LengthUnit);

        using var cmd = Command(s.Connection, sql);
        AddToolWindow(cmd, s.Now, days, deviceId, tool, remain);
        var rows = new List<ToolAlarmEvent>();
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            IReadOnlyList<string> alarms;
            LifeInfo? life;
            int? cutter = null;
            if (history)
            {
                alarms = HistoryAlarmWords(s.Schema, c => Int(r, c));
                var rowRemain = Int(r, "ToolLifeCountType") is { } countType ? countType == 1 : remain;
                life = Life(Int(r, "ToolLifeType"), Int(r, "ToolLife"), null, Int(r, "ActualToolLife"), rowRemain, unit);
            }
            else
            {
                alarms = RaisedAlarmWords(s.Schema, c => Int(r, c));
                var kind = LifeKind(Int(r, "ToolLifeType"));
                life = Life(Int(r, "ToolLifeType"), Int(r, $"ToolLife{kind}"), Int(r, $"ToolLifeWarning{kind}"),
                    Int(r, $"ActualToolLife{kind}New"), remain, unit);
                cutter = Int(r, "TotalCutter") is > 1 ? Int(r, "CutterNo") : null;
            }
            rows.Add(new ToolAlarmEvent(Date(r, "AlarmTime")!.Value, DeviceName(s, Int(r, "DeviceID")), Int(r, "ITN") ?? 0,
                Int(r, "FTN"), alarms)
            {
                Pot = Int(r, "POT") is > 0 and var pot ? pot : null,
                Cutter = cutter,
                Life = life,
                Length = Length(Int(r, "Length"), unit),
                Diameter = Length(Int(r, "Diameter"), unit),
            });
        }
        var (list, capped) = Cap(rows);
        // The history rows carry their own life direction; only the fallback depends on the cell-wide one.
        return new ToolAlarmList(s.Now, capped, source, unit, list, notAvailable)
        {
            LifeDirectionAssumed = Assumed(settings.LifeAssumed && !history), LengthUnitAssumed = Assumed(settings.UnitAssumed),
        };
    }

    /// <summary>Where tool alarms come from on this cell: ToolAlarmHistory when the login can read it, else the
    /// PresetResults rows in which an alarm bit turned on (what the cell's own Tool Alarm History report reads; no pot
    /// there). Neither readable: CellSchemaException. Newest first, cap + 1; expects @take, @now, @days, @device, @tool.</summary>
    internal static (string Sql, string Source, IReadOnlyList<string> NotAvailable) ToolAlarmsSql(SchemaInfo schema)
    {
        if (schema.HasTable(AlarmHistory))
        {
            const string t = AlarmHistory;
            var columns = string.Join(", ",
                [
                    "h.AlarmTime", "h.DeviceID", "h.ITN", "h.FTN",
                    Opt(schema, t, "h", "POT"), Opt(schema, t, "h", "Length"), Opt(schema, t, "h", "Diameter"),
                    Opt(schema, t, "h", "ToolLife"), Opt(schema, t, "h", "ActualToolLife"),
                    Opt(schema, t, "h", "ToolLifeType"), Opt(schema, t, "h", "ToolLifeCountType"),
                    .. HistoryAlarmColumns.Select(c => Opt(schema, t, "h", c.Column, type: "bit")),
                ]);
            var missing = new[] { ("POT", "pot"), ("ToolLife", "life") }.Where(c => !schema.Has(t, c.Item1)).Select(c => c.Item2).ToList();
            return ($"""
                SELECT TOP (@take) {columns}
                FROM ToolAlarmHistory h
                WHERE h.AlarmTime >= DATEADD(day, -@days, @now) AND (@device IS NULL OR h.DeviceID = @device)
                  AND (@tool IS NULL OR h.ITN = @tool OR h.FTN = @tool)
                ORDER BY h.AlarmTime DESC;
                """, "tool alarm history", missing);
        }
        if (schema.HasTable(Preset))
        {
            var bits = Codes.ToolAlarmColumns.Select(c => c.Column).Where(c => HasPair(schema, c)).ToList();
            var columns = string.Join(", ",
                [
                    "p.PresetDateTime AS AlarmTime", "p.DeviceID", "p.ITN", "p.FTN", "CAST(NULL AS int) AS POT",
                    Opt(schema, Preset, "p", "TotalCutter"), Opt(schema, Preset, "p", "CutterNo"),
                    Opt(schema, Preset, "p", "LengthNew", "Length"), Opt(schema, Preset, "p", "DiameterNew", "Diameter"),
                    Opt(schema, Preset, "p", "ToolLifeType"),
                    .. LifeKinds.SelectMany(k => new[]
                    {
                        Opt(schema, Preset, "p", $"ToolLife{k}"), Opt(schema, Preset, "p", $"ToolLifeWarning{k}"),
                        Opt(schema, Preset, "p", $"ActualToolLife{k}New"),
                    }),
                    .. bits.SelectMany(c => new[] { $"p.{c}Old", $"p.{c}New" }),
                ]);
            var raised = Any(bits.Select(c => $"(p.{c}Old = 0 AND p.{c}New = 1)"));
            return ($"""
                SELECT TOP (@take) {columns}
                FROM PresetResults p
                WHERE p.PresetDateTime >= DATEADD(day, -@days, @now) AND (@device IS NULL OR p.DeviceID = @device)
                  AND (@tool IS NULL OR p.ITN = @tool OR p.FTN = @tool) AND ({raised})
                ORDER BY p.PresetDateTime DESC;
                """, "tool data changes in which an alarm bit turned on (the tool alarm history is missing on this cell or not readable by this login)",
                ["pot"]);
        }
        throw new CellSchemaException([AlarmHistory, Preset]);
    }

    /// <summary>The alarm words of one ToolAlarmHistory row.</summary>
    internal static IReadOnlyList<string> HistoryAlarmWords(SchemaInfo schema, Func<string, int?> value)
    {
        var flags = 0;
        foreach (var (column, flag) in HistoryAlarmColumns)
        {
            if (schema.Has(AlarmHistory, column) && value(column) == 1) flags |= flag;
        }
        return Codes.ToolAlarms(flags);
    }

    // The alarm bits that went from 0 to 1 in one PresetResults row.
    private static IReadOnlyList<string> RaisedAlarmWords(SchemaInfo schema, Func<string, int?> value)
    {
        var flags = 0;
        foreach (var (column, flag) in Codes.ToolAlarmColumns)
        {
            if (HasPair(schema, column) && value(column + "Old") == 0 && value(column + "New") == 1) flags |= flag;
        }
        return Codes.ToolAlarms(flags);
    }

    // --- get_tool_movements ---

    public async Task<ToolMovementList> GetToolMovementsAsync(int days, string? device, int? tool, CancellationToken ct)
    {
        await using var s = await OpenAsync(ct);
        var deviceId = FindDevice(s.Names(), device);
        s.Schema.Require(InOut);

        using var cmd = Command(s.Connection, ToolMovementsSql(s.Schema));
        AddToolWindow(cmd, s.Now, days, deviceId, tool, remain: false);
        var rows = new List<ToolMovement>();
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            rows.Add(new ToolMovement(
                Date(r, "InOutDateTime")!.Value,
                Str(r, "Action").ToLowerInvariant(),
                Int(r, "ITN") ?? 0, Int(r, "FTN"),
                PlaceOf(s, Int(r, "FromDeviceID"), Str(r, "FromDeviceName"), Int(r, "FromPot")),
                PlaceOf(s, Int(r, "ToDeviceID"), Str(r, "ToDeviceName"), Int(r, "ToPot")),
                Codes.ToolStatus(Int(r, "Status")))
            {
                Name = NonEmpty(Str(r, "FTNComment")),
                ItnComment = NonEmpty(Str(r, "ITNComment")),
                Cutter = Int(r, "TotalCutter") is > 1 ? Int(r, "CutterNo") : null,
                UsedPercent = Int(r, "RatioUsed"),
                User = NonEmpty(Str(r, "Username")),
                Reason = NonEmpty(Str(r, "ToolOutReason")),
            });
        }
        var (list, capped) = Cap(rows);
        return new ToolMovementList(s.Now, capped, list);
    }

    /// <summary>Tools put into or taken out of a machine, newest first, cap + 1; a device matches either end.
    /// Expects @take, @now, @days, @device and @tool.</summary>
    internal static string ToolMovementsSql(SchemaInfo schema)
    {
        const string t = InOut;
        var columns = string.Join(", ",
            "t.InOutDateTime", "t.ITN", "t.FTN", "t.FromDeviceID", "t.ToDeviceID",
            Opt(schema, t, "t", "Action", type: Text),
            Opt(schema, t, "t", "TotalCutter"), Opt(schema, t, "t", "CutterNo"),
            Opt(schema, t, "t", "FTNComment", type: Text), Opt(schema, t, "t", "ITNComment", type: Text),
            Opt(schema, t, "t", "Status"), Opt(schema, t, "t", "RatioUsed"),
            Opt(schema, t, "t", "FromPot"), Opt(schema, t, "t", "ToPot"),
            Opt(schema, t, "t", "FromDeviceName", type: Text), Opt(schema, t, "t", "ToDeviceName", type: Text),
            Opt(schema, t, "t", "Username", type: Text), Opt(schema, t, "t", "ToolOutReason", type: Text));
        return $"""
            SELECT TOP (@take) {columns}
            FROM ToolInputOutputResults t
            WHERE t.InOutDateTime >= DATEADD(day, -@days, @now)
              AND (@device IS NULL OR t.FromDeviceID = @device OR t.ToDeviceID = @device)
              AND (@tool IS NULL OR t.ITN = @tool OR t.FTN = @tool)
            ORDER BY t.InOutDateTime DESC;
            """;
    }
}
