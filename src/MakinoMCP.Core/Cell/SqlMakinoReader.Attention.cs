using Microsoft.Data.SqlClient;

namespace MakinoMCP.Core.Cell;

// Attention: what waits on a person. Devices (MachineStatus, WorkSetStatus, the vehicle: recovery request, error,
// emergency stop, disconnected, a machine's operator call), work-set instructions waiting at a station
// (WorkSetCommand), manual transports not finished (ManualTransport), fixtures that are damaged, suspended, on hold or
// lacking tools (PalletFixtures with the current order from FixtureRun), tools in alarm on each machine, and stations or
// the vehicle past their maintenance cycle count (DeviceParameters.WarningCycleCount). A section whose table this cell
// or login lacks is named in NotAvailable instead.
public sealed partial class SqlMakinoReader
{
    // Severity ranks: lower comes first.
    private const int RankDevice = 0;
    private const int RankWorkSet = 1;
    private const int RankTransport = 2;
    private const int RankFixtureBlocked = 3;
    private const int RankTools = 4;
    private const int RankLackingTools = 5;
    private const int RankMaintenance = 6;
    private const int RankParked = 7;

    // Code values compared against (meanings and words are in Codes).
    private const int DashDown = 0;               // DeviceDashStatus: down
    private const int DashError = 4;              // DeviceDashStatus: error
    private const int ReclampTimeoutAlarm = 1;    // ReclampStatus
    private const int ReclampTimeoutWarning = 2;  // ReclampStatus
    private const int ToolAlarm = 4;              // ToolStatus (derived): alarm
    private const int ToolOffsetError = 3;        // ToolStatus: offset error
    private const int NoWorkSetOperation = 0;     // WorkSetStationOperationType: nothing
    private const string ClosedTransports = "4, 5"; // ManualTransportStatus: finished, cancelled

    internal sealed record AttentionDevice(string Name, int Type)
    {
        public bool? Connected { get; init; }
        public bool? Emergency { get; init; }
        public bool? Recovery { get; init; }
        public int? Status { get; init; }
        public int? OperatorCall { get; init; }
        public int? ToolStatus { get; init; }
        public int OpenAlarms { get; init; }
        public int? Cycles { get; init; }
        public int? CycleLimit { get; init; }
    }

    internal sealed record AttentionFixture(int? Pallet, int? Fixture)
    {
        public bool Suspend { get; init; }
        public bool Hold { get; init; }
        public bool Damaged { get; init; }
        public bool PreMcSuspend { get; init; }
        public int? PreMcReason { get; init; }
        public bool LackingTool { get; init; }
        public int? Reclamp { get; init; }
        public string Reason { get; init; } = "";
        public string? Order { get; init; }
        public int? Process { get; init; }
    }

    internal sealed record AttentionCommand(string Device, int? Pallet, int? Fixture, int? Operation, int? Status, string? Order, int? Quantity);

    internal sealed record AttentionTransport(int? Pallet, string To, int? Status, DateTime? Since);

    internal sealed record AttentionTools(string Device, int Alarm, int OffsetError);

    /// <summary>What the cell holds for get_attention. FixtureCounts: the cell's count per fixture condition (all
    /// fixtures, past the row cap); null → counted from <see cref="Fixtures"/>.</summary>
    internal sealed record AttentionRows(
        IReadOnlyList<AttentionDevice> Devices, IReadOnlyList<AttentionFixture> Fixtures, IReadOnlyList<AttentionCommand> Commands,
        IReadOnlyList<AttentionTransport> Transports, IReadOnlyList<AttentionTools> Tools)
    {
        public IReadOnlyDictionary<string, int>? FixtureCounts { get; init; }

        /// <summary>The parked fixtures grouped by place and condition (see <see cref="AttentionParkedSql"/>).</summary>
        public IReadOnlyList<ParkedGroup>? Parked { get; init; }
    }

    /// <summary>Parked: suspended or on hold, and nothing else wrong with it (lacking tools aside).</summary>
    private static bool IsParked(AttentionFixture f) =>
        (f.Suspend || f.Hold) && !f.Damaged && !f.PreMcSuspend && f.Reclamp is null or 0;

    /// <summary>The items, most severe first (then newest first where the cell says since when), capped; the counts
    /// and each machine's tool status as context.</summary>
    internal static AttentionList BuildAttention(
        DateTime now, int cap, AttentionRows rows, IReadOnlyList<string> notAvailable, bool includeParked)
    {
        var items = new List<(int Rank, AttentionItem Item)>();
        var counts = new Dictionary<string, int>();
        void Count(string key, int n = 1)
        {
            if (n > 0) counts[key] = counts.GetValueOrDefault(key) + n;
        }

        var toolStatus = new Dictionary<string, string>();
        foreach (var d in rows.Devices)
        {
            var what = new List<string>();
            if (d.Recovery == true) what.Add("recovery request");
            if (d.Emergency == true) what.Add("emergency stop");
            if (d.Connected == false) what.Add("disconnected");
            else if (d.Status == DashDown) what.Add(Codes.DeviceDashStatus(d.Status));
            if (d.Status == DashError) what.Add(Codes.DeviceDashStatus(d.Status));
            foreach (var w in what) Count(w);
            if (d.Type == MachineType && d.OperatorCall is > 0)
            {
                what.Add($"operator call ({Codes.OperatorCall(d.OperatorCall)})");
                Count("operator call");
            }
            if (what.Count > 0)
            {
                items.Add((RankDevice, new AttentionItem("device", string.Join(", ", what))
                {
                    Device = d.Name,
                    Detail = d.OpenAlarms > 0 ? $"{d.OpenAlarms} open alarm{(d.OpenAlarms == 1 ? "" : "s")}" : null,
                }));
            }
            if (d.Type == MachineType && d.ToolStatus is not null) toolStatus.TryAdd(d.Name, Codes.MachineToolStatus(d.ToolStatus));
            if (d.CycleLimit is > 0 && d.Cycles >= d.CycleLimit)
            {
                const string over = "cycle count over its maintenance limit";
                items.Add((RankMaintenance, new AttentionItem("maintenance", over)
                {
                    Device = d.Name, Detail = $"{d.Cycles} cycles, limit {d.CycleLimit}",
                }));
                Count(over);
            }
        }

        foreach (var c in rows.Commands)
        {
            var detail = new List<string>();
            // WorkSetCommand.OperationStatus has no confirmed meaning yet (it isn't WorkSetResults' list): raw code.
            if (c.Status is > 0) detail.Add($"status {CodeText(c.Status)}");
            if (c.Quantity is > 0) detail.Add($"quantity {c.Quantity}");
            items.Add((RankWorkSet, new AttentionItem("work set", Codes.WorkSetCommandOperation(c.Operation))
            {
                Device = c.Device, Pallet = c.Pallet, Fixture = c.Fixture, Order = c.Order,
                Detail = detail.Count > 0 ? string.Join(", ", detail) : null,
            }));
            Count("work-set instructions waiting");
        }

        foreach (var t in rows.Transports)
        {
            items.Add((RankTransport, new AttentionItem("manual transport", Codes.ManualTransportStatus(t.Status))
            {
                Pallet = t.Pallet, Detail = t.To.Length > 0 ? $"to {t.To}" : null, Since = t.Since,
            }));
            Count("manual transports open");
        }

        foreach (var t in rows.Tools)
        {
            var what = new List<string>();
            if (t.Alarm > 0) what.Add($"{t.Alarm} tool{(t.Alarm == 1 ? "" : "s")} in alarm");
            if (t.OffsetError > 0)
                what.Add(t.Alarm > 0 ? $"{t.OffsetError} with an offset error" : $"{t.OffsetError} tool{(t.OffsetError == 1 ? "" : "s")} with an offset error");
            if (what.Count == 0) continue;
            items.Add((RankTools, new AttentionItem("tools", string.Join(", ", what))
            {
                Device = t.Device,
                Detail = toolStatus.TryGetValue(t.Device, out var status) ? $"machine tool status: {status}" : null,
            }));
            Count("tools in alarm on machines", t.Alarm);
            Count("tools with an offset error on machines", t.OffsetError);
        }

        foreach (var f in rows.Fixtures)
        {
            var conditions = new List<(string Words, string Key, int Rank)>();
            if (f.Damaged) conditions.Add(("damaged", "damaged", RankFixtureBlocked));
            if (f.PreMcSuspend)
            {
                var words = f.PreMcReason is > 0 ? $"pre-machining suspended ({Codes.PreMcSuspendReason(f.PreMcReason)})" : "pre-machining suspended";
                conditions.Add((words, "pre-machining suspended", RankFixtureBlocked));
            }
            if (f.Reclamp is > 0)
            {
                var words = $"reclamp {Codes.ReclampStatus(f.Reclamp)}";
                conditions.Add((words, words, f.Reclamp == ReclampTimeoutAlarm ? RankFixtureBlocked : RankMaintenance));
            }
            // A suspended or held fixture won't run, so its missing tools wait with it.
            if (f.LackingTool) conditions.Add(("lacking tools", "lacking tools", f.Suspend || f.Hold ? RankParked : RankLackingTools));
            if (f.Hold) conditions.Add(("work-set hold", "work-set hold", RankParked));
            if (f.Suspend) conditions.Add(("machining suspended", "machining suspended", RankParked));
            if (conditions.Count == 0) continue;
            if (rows.FixtureCounts is null) foreach (var c in conditions) Count(c.Key);
            if (!includeParked && IsParked(f)) continue; // counted in Parked instead
            items.Add((conditions.Min(c => c.Rank), new AttentionItem("fixture", string.Join(", ", conditions.Select(c => c.Words)))
            {
                Pallet = f.Pallet, Fixture = f.Fixture, Order = f.Order, Process = f.Process, Detail = NonEmpty(OneLine(f.Reason)),
            }));
        }
        if (rows.FixtureCounts is not null)
        {
            foreach (var (key, n) in rows.FixtureCounts) Count(key, n);
        }

        var sorted = items
            .OrderBy(i => i.Rank)
            .ThenByDescending(i => i.Item.Since.HasValue)
            .ThenByDescending(i => i.Item.Since)
            .Select(i => i.Item)
            .ToList();
        var capped = sorted.Count > cap;
        var parked = rows.Parked is { Count: > 0 } groups
            ? new ParkedFixtures(groups.Sum(g => g.Fixtures), groups)
            {
                LackingTools = groups.Sum(g => g.LackingTools ?? 0) is > 0 and var lacking ? lacking : null,
            }
            : null;
        return new AttentionList(now, capped, counts, capped ? sorted.GetRange(0, cap) : sorted, notAvailable)
        {
            MachineToolStatus = toolStatus.Count > 0 ? toolStatus : null,
            Parked = parked,
        };
    }

    /// <summary>Rows of <see cref="AttentionParkedSql"/> (pallet place type and number, suspended, held, fixtures,
    /// lacking tools) as groups per place and condition, biggest first. A place with a device row is that device's
    /// name; any other (the stocker shelves) its device type's words, so all shelves are one place.</summary>
    internal static List<ParkedGroup> MergeParked(
        IEnumerable<(int? Type, int? Number, bool Suspended, bool Held, int N, int Lacking)> rows,
        IReadOnlyList<(int Type, int Number, string Name)> devices)
    {
        var merged = new List<(string Place, string Condition, int N, int Lacking)>();
        foreach (var r in rows)
        {
            var place = r.Type is null or 0
                ? "unknown"
                : devices.FirstOrDefault(d => d.Type == r.Type && d.Number == r.Number).Name ?? Codes.DeviceType(r.Type);
            var condition = string.Join(", ", new[] { r.Suspended ? "machining suspended" : null, r.Held ? "work-set hold" : null }.OfType<string>());
            var i = merged.FindIndex(m => m.Place == place && m.Condition == condition);
            if (i < 0) merged.Add((place, condition, r.N, r.Lacking));
            else merged[i] = (place, condition, merged[i].N + r.N, merged[i].Lacking + r.Lacking);
        }
        return merged
            .OrderByDescending(m => m.N)
            .Select(m => new ParkedGroup(m.Place, m.Condition, m.N) { LackingTools = m.Lacking > 0 ? m.Lacking : null })
            .ToList();
    }

    // Columns whose absence leaves part of a section empty, and the words notAvailable uses for them.
    private static readonly (string Table, string Column, string Words)[] AttentionColumns =
    [
        ("MachineStatus", "Connected", "machine connection"), ("MachineStatus", "Emergency", "machine emergency stop"),
        ("MachineStatus", "Status", "machine error state"), ("MachineStatus", "Recovery", "machine recovery requests"),
        ("MachineStatus", "OperatorCall", "machine operator calls"), ("MachineStatus", "ToolStatus", "machine tool status"),
        ("WorkSetStatus", "Connected", "work-set station connection"), ("WorkSetStatus", "Emergency", "work-set station emergency stop"),
        ("WorkSetStatus", "Status", "work-set station error state"), ("WorkSetStatus", "Recovery", "work-set station recovery requests"),
        ("WorkSetStatus", "TotalCycleCount", "work-set station cycle counts"),
    ];

    private static readonly (string Column, string Words)[] VehicleColumns =
    [
        ("Connected", "vehicle connection"), ("Emergency", "vehicle emergency stop"), ("Status", "vehicle error state"),
        ("Recovery", "vehicle recovery request"), ("TotalCycleCount", "vehicle cycle count"),
    ];

    private static readonly (string Column, string Words)[] FixtureColumns =
    [
        ("Suspend", "suspended fixtures"), ("WorkSetHold", "fixtures on hold"), ("Damaged", "damaged fixtures"),
        ("PreMcSuspend", "pre-machining suspended fixtures"), ("LackingTool", "fixtures lacking tools"),
        ("ReclampStatus", "reclamp timeouts"),
    ];

    /// <summary>What get_attention can't show because a table it reads lacks a column (a missing table is reported
    /// by the section itself).</summary>
    internal static List<string> AttentionColumnsMissing(SchemaInfo schema)
    {
        var missing = AttentionColumns.Where(c => schema.HasTable(c.Table) && !schema.Has(c.Table, c.Column)).Select(c => c.Words).ToList();
        var vehicle = schema.HasTable("VehicleStatus") ? "VehicleStatus" : schema.HasTable("ViewVehicleStatus") ? "ViewVehicleStatus" : null;
        if (vehicle is not null) missing.AddRange(VehicleColumns.Where(c => !schema.Has(vehicle, c.Column)).Select(c => c.Words));
        if (schema.HasTable("PalletFixtures"))
        {
            var fixtures = FixtureColumns.Where(c => !schema.Has("PalletFixtures", c.Column)).Select(c => c.Words).ToList();
            if (fixtures.Count == FixtureColumns.Length) missing.Add("fixtures (suspended, damaged, lacking tools, …)");
            else missing.AddRange(fixtures);
        }
        if (schema.HasTable(Itd) && DerivedStatusSql(schema) == "0") missing.Add("tools in alarm");
        return missing;
    }

    /// <summary>Typed text on one line, runs of spaces collapsed.</summary>
    internal static string OneLine(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // --- SQL ---

    // PalletFixtures' condition columns and when each counts.
    private static readonly (string Column, string Condition, string Key)[] FixtureFlags =
    [
        ("Damaged", "pf.Damaged = 1", "damaged"),
        ("PreMcSuspend", "pf.PreMcSuspend = 1", "pre-machining suspended"),
        ("ReclampStatus", $"pf.ReclampStatus = {ReclampTimeoutAlarm}", $"reclamp {Codes.ReclampStatus(ReclampTimeoutAlarm)}"),
        ("ReclampStatus", $"pf.ReclampStatus = {ReclampTimeoutWarning}", $"reclamp {Codes.ReclampStatus(ReclampTimeoutWarning)}"),
        ("LackingTool", "pf.LackingTool = 1", "lacking tools"),
        ("WorkSetHold", "pf.WorkSetHold = 1", "work-set hold"),
        ("Suspend", "pf.Suspend = 1", "machining suspended"),
    ];

    /// <summary>The fixtures with any condition (cap + 1; blocked ones first, then those lacking tools that aren't
    /// suspended or held, then the rest),
    /// with the order and process FixtureRun says they hold now. "WHERE 1 = 0" when the cell has none of the
    /// condition columns.</summary>
    internal static string AttentionFixturesSql(SchemaInfo schema, bool includeParked)
    {
        const string t = "PalletFixtures";
        bool Has(string column) => schema.Has(t, column);
        var conditions = new List<string>();
        if (Has("Suspend")) conditions.Add("pf.Suspend = 1");
        if (Has("WorkSetHold")) conditions.Add("pf.WorkSetHold = 1");
        if (Has("Damaged")) conditions.Add("pf.Damaged = 1");
        if (Has("PreMcSuspend")) conditions.Add("pf.PreMcSuspend = 1");
        if (Has("LackingTool")) conditions.Add("pf.LackingTool = 1");
        if (Has("ReclampStatus")) conditions.Add($"pf.ReclampStatus IN ({ReclampTimeoutAlarm}, {ReclampTimeoutWarning})");

        var blocked = new List<string>();
        if (Has("Damaged")) blocked.Add("pf.Damaged = 1");
        if (Has("PreMcSuspend")) blocked.Add("pf.PreMcSuspend = 1");
        if (Has("ReclampStatus")) blocked.Add($"pf.ReclampStatus = {ReclampTimeoutAlarm}");
        var whens = new List<string>();
        if (blocked.Count > 0) whens.Add($"WHEN {string.Join(" OR ", blocked)} THEN 0");
        if (Has("LackingTool"))
        {
            var parked = (Has("Suspend") ? " AND ISNULL(pf.Suspend, 0) = 0" : "") + (Has("WorkSetHold") ? " AND ISNULL(pf.WorkSetHold, 0) = 0" : "");
            whens.Add($"WHEN pf.LackingTool = 1{parked} THEN 1");
        }
        // Parked fixtures last (3), so the cap drops them before anything still waiting (a reclamp warning is 2).
        if (ParkedCondition(schema) is { } parkedLast) whens.Add($"WHEN {parkedLast} THEN 3");
        var severity = whens.Count > 0 ? $"CASE {string.Join(" ", whens)} ELSE 2 END" : "0";

        var run = schema.HasTable("FixtureRun") && schema.Has("FixtureRun", "PalletFixtureID") && Has("PalletFixtureID");
        var withOrder = run && schema.Has("FixtureRun", "CurOrderID") && schema.HasTable("Orders");
        var withProcess = run && schema.Has("FixtureRun", "CurProcessID") && schema.HasTable("Processes");
        var cols = string.Join(", ",
            Has("PalletID") ? PalletNumberSql(schema, "pf.PalletID", "PalletNumber") : "CAST(NULL AS int) AS PalletNumber",
            Opt(schema, t, "pf", "FixtureNumber"),
            Opt(schema, t, "pf", "Suspend", type: "bit"),
            Opt(schema, t, "pf", "WorkSetHold", type: "bit"),
            Opt(schema, t, "pf", "Damaged", type: "bit"),
            Opt(schema, t, "pf", "PreMcSuspend", type: "bit"),
            Opt(schema, t, "pf", "PreMcSuspendReason"),
            Opt(schema, t, "pf", "LackingTool", type: "bit"),
            Opt(schema, t, "pf", "ReclampStatus"),
            Opt(schema, t, "pf", "StatusReason", type: Text),
            withOrder ? Opt(schema, "Orders", "o", "OrderName", type: Text) : $"CAST(NULL AS {Text}) AS OrderName",
            withProcess ? Opt(schema, "Processes", "pr", "ProcessNumber") : "CAST(NULL AS int) AS ProcessNumber");
        var joins = (run ? " LEFT JOIN FixtureRun fr ON fr.PalletFixtureID = pf.PalletFixtureID" : "")
                    + (withOrder ? " LEFT JOIN Orders o ON o.OrderID = fr.CurOrderID" : "")
                    + (withProcess ? " LEFT JOIN Processes pr ON pr.ProcessID = fr.CurProcessID" : "");
        var where = conditions.Count > 0 ? string.Join(" OR ", conditions) : "1 = 0";
        if (!includeParked && ParkedCondition(schema) is { } parkedSql) where = $"({where}) AND NOT ({parkedSql})";
        return $"""
            SELECT TOP (@take) {cols}
            FROM PalletFixtures pf{joins}
            WHERE {where}
            ORDER BY {severity}, PalletNumber, FixtureNumber;
            """;
    }

    /// <summary>A parked fixture in SQL: suspended or on hold, not damaged, not pre-machining suspended, no reclamp
    /// timeout. Null when the cell has neither the suspend nor the hold column.</summary>
    private static string? ParkedCondition(SchemaInfo schema)
    {
        const string t = "PalletFixtures";
        var held = new List<string>();
        if (schema.Has(t, "Suspend")) held.Add("pf.Suspend = 1");
        if (schema.Has(t, "WorkSetHold")) held.Add("pf.WorkSetHold = 1");
        if (held.Count == 0) return null;
        var nothingElse = new List<string>();
        if (schema.Has(t, "Damaged")) nothingElse.Add("ISNULL(pf.Damaged, 0) = 0");
        if (schema.Has(t, "PreMcSuspend")) nothingElse.Add("ISNULL(pf.PreMcSuspend, 0) = 0");
        if (schema.Has(t, "ReclampStatus")) nothingElse.Add("ISNULL(pf.ReclampStatus, 0) = 0");
        return $"({string.Join(" OR ", held)})" + string.Concat(nothingElse.Select(c => " AND " + c));
    }

    /// <summary>The parked fixtures counted per pallet place (PalletLocation type and number) and condition, with how
    /// many are lacking tools. Grouped rows, at most places × 3. Null when the cell can't tell a parked fixture.</summary>
    internal static string? AttentionParkedSql(SchemaInfo schema)
    {
        const string t = "PalletFixtures";
        if (ParkedCondition(schema) is not { } parked) return null;
        string Place(string column) =>
            schema.Has(t, "PalletID") && schema.Has("PalletLocation", "PalletID") && schema.Has("PalletLocation", column)
                ? $"(SELECT TOP 1 l.{column} FROM PalletLocation l WHERE l.PalletID = pf.PalletID) AS {column}"
                : $"CAST(NULL AS int) AS {column}";
        string Bit(string column, string condition) => schema.Has(t, column) ? $"CASE WHEN {condition} THEN 1 ELSE 0 END" : "0";
        return $"""
            SELECT x.CurDeviceType, x.CurDeviceNumber, x.Suspended, x.Held, COUNT(*) AS N, SUM(x.Lacking) AS Lacking FROM (
                SELECT {Place("CurDeviceType")}, {Place("CurDeviceNumber")},
                       {Bit("Suspend", "pf.Suspend = 1")} AS Suspended, {Bit("WorkSetHold", "pf.WorkSetHold = 1")} AS Held,
                       {Bit("LackingTool", "pf.LackingTool = 1")} AS Lacking
                FROM PalletFixtures pf
                WHERE {parked}) x
            GROUP BY x.CurDeviceType, x.CurDeviceNumber, x.Suspended, x.Held;
            """;
    }

    /// <summary>Every fixture condition counted over all fixtures (one row); null when the cell has none of them.</summary>
    internal static string? AttentionFixtureCountsSql(SchemaInfo schema)
    {
        var sums = FixtureFlags.Where(f => schema.Has("PalletFixtures", f.Column))
            .Select(f => $"SUM(CASE WHEN {f.Condition} THEN 1 ELSE 0 END) AS C{Array.IndexOf(FixtureFlags, f)}")
            .ToList();
        return sums.Count == 0 ? null : $"SELECT {string.Join(", ", sums)} FROM PalletFixtures pf;";
    }

    /// <summary>Instructions a work-set station is waiting for the operator to carry out (cap + 1). Null without the
    /// OperationType column.</summary>
    internal static string? WorkSetCommandsSql(SchemaInfo schema)
    {
        const string t = "WorkSetCommand";
        if (!schema.Has(t, "OperationType") || !schema.Has(t, "DeviceID")) return null;
        var pallet = schema.Has(t, "PalletFixtureID") && schema.Has("PalletFixtures", "PalletFixtureID") && schema.Has("PalletFixtures", "PalletID")
                     && schema.Has("Pallets", "PalletNumber")
            ? "(SELECT TOP 1 p.PalletNumber FROM PalletFixtures pf JOIN Pallets p ON p.PalletID = pf.PalletID WHERE pf.PalletFixtureID = c.PalletFixtureID) AS PalletNumber"
            : "CAST(NULL AS int) AS PalletNumber";
        var orderId = (schema.Has(t, "ClampOrderID"), schema.Has(t, "UnclampOrderID")) switch
        {
            (true, true) => "COALESCE(NULLIF(c.ClampOrderID, 0), c.UnclampOrderID)",
            (true, false) => "c.ClampOrderID",
            (false, true) => "c.UnclampOrderID",
            _ => null,
        };
        var order = orderId is not null && schema.Has("Orders", "OrderName")
            ? $"(SELECT TOP 1 o.OrderName FROM Orders o WHERE o.OrderID = {orderId}) AS OrderName"
            : $"CAST(NULL AS {Text}) AS OrderName";
        var cols = string.Join(", ",
            "c.DeviceID", pallet,
            Opt(schema, t, "c", "FixtureNumber"),
            Opt(schema, t, "c", "OperationType"),
            Opt(schema, t, "c", "OperationStatus"),
            Opt(schema, t, "c", "ClampQuantity"),
            order);
        var then = schema.Has(t, "FixtureNumber") ? ", c.FixtureNumber" : "";
        return $"""
            SELECT TOP (@take) {cols}
            FROM WorkSetCommand c
            WHERE c.OperationType <> {NoWorkSetOperation}
            ORDER BY c.DeviceID{then};
            """;
    }

    /// <summary>Manual transports not finished or cancelled, newest first (cap + 1). Null without a Status column.</summary>
    internal static string? ManualTransportsSql(SchemaInfo schema)
    {
        const string t = "ManualTransport";
        if (!schema.Has(t, "Status")) return null;
        var cols = string.Join(", ",
            schema.Has(t, "PalletID") ? PalletNumberSql(schema, "t.PalletID", "PalletNumber") : "CAST(NULL AS int) AS PalletNumber",
            Opt(schema, t, "t", "DestDeviceType"),
            Opt(schema, t, "t", "DestDeviceNumber"),
            Opt(schema, t, "t", "DestDevicePosition"),
            Opt(schema, t, "t", "Status"),
            Opt(schema, t, "t", "EntryDate", type: "datetime"));
        var order = schema.Has(t, "EntryDate") ? "t.EntryDate DESC" : "PalletNumber";
        return $"""
            SELECT TOP (@take) {cols}
            FROM ManualTransport t
            WHERE t.Status NOT IN ({ClosedTransports})
            ORDER BY {order};
            """;
    }

    /// <summary>Each device's maintenance cycle limit (DeviceParameters.WarningCycleCount; 0 means none set); null when
    /// the cell has no DeviceParameters.</summary>
    internal static string? CycleLimitSql(SchemaInfo schema)
    {
        const string t = "DeviceParameters";
        if (!schema.Has(t, "DeviceID") || !schema.Has(t, "ParameterName") || !schema.Has(t, "ParameterValue")) return null;
        return "SELECT DeviceID, ParameterValue FROM DeviceParameters WHERE ParameterName = 'WarningCycleCount';";
    }

    // --- reading ---

    public async Task<AttentionList> GetAttentionAsync(bool includeParked, CancellationToken ct)
    {
        await using var s = await OpenAsync(ct);
        var schema = s.Schema;
        var notAvailable = AttentionColumnsMissing(schema);

        var devices = s.Devices.ToDictionary(d => d.Id, d => new AttentionDevice(d.Name, d.Type));

        // Device states; the extra columns (TotalCycleCount) come through the overview's own SQL.
        async Task ReadStatus(string sql, Func<SqlDataReader, int?> idOf)
        {
            using var cmd = Command(s.Connection, sql);
            using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                if (idOf(r) is not { } id || !devices.TryGetValue(id, out var d)) continue;
                devices[id] = d with
                {
                    Connected = Bool(r, "Connected"), Emergency = Bool(r, "Emergency"), Recovery = Bool(r, "Recovery"),
                    Status = Int(r, "Status"), Cycles = Int(r, "TotalCycleCount"),
                    OperatorCall = Has(r, "OperatorCall") ? Int(r, "OperatorCall") : null,
                    ToolStatus = Has(r, "ToolStatus") ? Int(r, "ToolStatus") : null,
                };
            }
        }
        if (schema.HasTable("MachineStatus")) await ReadStatus(StatusSql(schema, "MachineStatus", MachineSpots), r => Int(r, "DeviceID"));
        else notAvailable.Add("machine status");
        if (schema.HasTable("WorkSetStatus")) await ReadStatus(StatusSql(schema, "WorkSetStatus", StationSpots), r => Int(r, "DeviceID"));
        else notAvailable.Add("work-set station status");
        if (VehicleSql(schema) is { } vehicleSql) await ReadStatus(vehicleSql, r => VehicleIdOf(r, s.Devices));
        else notAvailable.Add("vehicle status");

        if (schema.HasTable("PMCAlarms") && schema.HasTable("CNCAlarms"))
        {
            using var cmd = Command(s.Connection, OpenAlarmSql(schema));
            using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var id = Int(r, "DeviceID") ?? 0;
                if (devices.TryGetValue(id, out var d)) devices[id] = d with { OpenAlarms = Int(r, "Alarms") ?? 0 };
            }
        }

        if (CycleLimitSql(schema) is { } limitSql)
        {
            using var cmd = Command(s.Connection, limitSql);
            using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var id = Int(r, "DeviceID") ?? 0;
                if (devices.TryGetValue(id, out var d)) devices[id] = d with { CycleLimit = ToInt(Str(r, "ParameterValue")) };
            }
        }
        else notAvailable.Add("maintenance cycle limits");

        // Tools in alarm or with an offset error, per machine, by the status the console shows.
        var tools = new List<AttentionTools>();
        if (schema.HasTable(Itd) && schema.HasTable(Itc) && schema.HasTable(Ftd) && schema.HasTable(Loc))
        {
            using var cmd = Command(s.Connection, ToolSummarySql(schema));
            cmd.Parameters.Add("@device", System.Data.SqlDbType.Int).Value = DBNull.Value;
            using var r = await cmd.ExecuteReaderAsync(ct);
            var perMachine = new Dictionary<int, (int Alarm, int Offset)>();
            while (await r.ReadAsync(ct))
            {
                var id = Int(r, "DeviceID") ?? 0;
                if (!devices.TryGetValue(id, out var d) || d.Type != MachineType) continue;
                var (alarm, offset) = perMachine.GetValueOrDefault(id);
                var n = Int(r, "N") ?? 0;
                perMachine[id] = Int(r, "Status") switch
                {
                    ToolAlarm => (alarm + n, offset),
                    ToolOffsetError => (alarm, offset + n),
                    _ => (alarm, offset),
                };
            }
            tools.AddRange(s.Devices.Where(d => perMachine.ContainsKey(d.Id))
                .Select(d => new AttentionTools(d.Name, perMachine[d.Id].Alarm, perMachine[d.Id].Offset)));
        }
        else notAvailable.Add("tools in alarm");

        var fixtures = new List<AttentionFixture>();
        IReadOnlyDictionary<string, int>? fixtureCounts = null;
        List<ParkedGroup>? parked = null;
        if (schema.HasTable("PalletFixtures"))
        {
            if (!schema.HasTable("FixtureRun")) notAvailable.Add("each fixture's current order and process");
            using (var cmd = Command(s.Connection, AttentionFixturesSql(schema, includeParked)))
            {
                AddTake(cmd);
                using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    fixtures.Add(new AttentionFixture(Int(r, "PalletNumber"), Int(r, "FixtureNumber"))
                    {
                        Suspend = Bool(r, "Suspend") == true, Hold = Bool(r, "WorkSetHold") == true, Damaged = Bool(r, "Damaged") == true,
                        PreMcSuspend = Bool(r, "PreMcSuspend") == true, PreMcReason = Int(r, "PreMcSuspendReason"),
                        LackingTool = Bool(r, "LackingTool") == true, Reclamp = Int(r, "ReclampStatus"), Reason = Str(r, "StatusReason"),
                        Order = NonEmpty(Str(r, "OrderName")), Process = Int(r, "ProcessNumber"),
                    });
                }
            }
            if (AttentionFixtureCountsSql(schema) is { } countSql)
            {
                using var cmd = Command(s.Connection, countSql);
                using var r = await cmd.ExecuteReaderAsync(ct);
                var found = new Dictionary<string, int>();
                if (await r.ReadAsync(ct))
                {
                    for (var i = 0; i < FixtureFlags.Length; i++)
                    {
                        if (schema.Has("PalletFixtures", FixtureFlags[i].Column) && (Int(r, $"C{i}") ?? 0) is > 0 and var n)
                            found[FixtureFlags[i].Key] = n;
                    }
                }
                fixtureCounts = found;
            }
            if (AttentionParkedSql(schema) is { } parkedSql)
            {
                using var cmd = Command(s.Connection, parkedSql);
                using var r = await cmd.ExecuteReaderAsync(ct);
                var groups = new List<(int?, int?, bool, bool, int, int)>();
                while (await r.ReadAsync(ct))
                {
                    groups.Add((Int(r, "CurDeviceType"), Int(r, "CurDeviceNumber"), Int(r, "Suspended") == 1, Int(r, "Held") == 1,
                        Int(r, "N") ?? 0, Int(r, "Lacking") ?? 0));
                }
                parked = MergeParked(groups, s.Devices.Select(d => (d.Type, d.Number, d.Name)).ToList());
            }
        }
        else notAvailable.Add("fixtures (suspended, damaged, lacking tools, …)");

        var commands = new List<AttentionCommand>();
        if (schema.HasTable("WorkSetCommand") && WorkSetCommandsSql(schema) is { } commandSql)
        {
            using var cmd = Command(s.Connection, commandSql);
            AddTake(cmd);
            using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                commands.Add(new AttentionCommand(DeviceName(s, Int(r, "DeviceID")), Int(r, "PalletNumber"), Int(r, "FixtureNumber"),
                    Int(r, "OperationType"), Int(r, "OperationStatus"), NonEmpty(Str(r, "OrderName")), Int(r, "ClampQuantity")));
            }
        }
        else notAvailable.Add("work-set instructions waiting");

        var transports = new List<AttentionTransport>();
        if (schema.HasTable("ManualTransport") && ManualTransportsSql(schema) is { } transportSql)
        {
            using var cmd = Command(s.Connection, transportSql);
            AddTake(cmd);
            using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var to = Location(s, Int(r, "DestDeviceType"), Int(r, "DestDeviceNumber"), Int(r, "DestDevicePosition"));
                transports.Add(new AttentionTransport(Int(r, "PalletNumber"), to == "unknown" ? "" : to, Int(r, "Status"), Date(r, "EntryDate")));
            }
        }
        else notAvailable.Add("manual transports");

        var rows = new AttentionRows(s.Devices.Select(d => devices[d.Id]).ToList(), fixtures, commands, transports, tools)
        {
            FixtureCounts = fixtureCounts, Parked = parked,
        };
        return BuildAttention(s.Now, _cap, rows, notAvailable, includeParked);
    }

    private static bool Has(SqlDataReader r, string column)
    {
        for (var i = 0; i < r.FieldCount; i++)
        {
            if (string.Equals(r.GetName(i), column, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
