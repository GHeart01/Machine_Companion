namespace MakinoMCP.Core.Cell;

// Production: Quantities (per order and process: remaining, good, scrap), FixtureQuantity + FixtureRun (parts clamped
// on fixtures for an order now) and OrderCompletedQtyResults (good and scrap per order and production day). The rules
// are the cell's own order screen's (its ViewOrders gives the same numbers): completed = good parts of the part's
// highest-numbered process, scrap = scrap of every process, remaining = remaining of the first process, in process =
// clamped now + parts waiting between processes. A login that can't read the tables gets the vendor's views.
public sealed partial class SqlMakinoReader
{
    private const string ProcessOf = "JOIN Processes pr ON pr.ProcessID = q.ProcessID";
    private const string LastProcess = "pr.ProcessNumber = (SELECT MAX(p2.ProcessNumber) FROM Processes p2 WHERE p2.PartID = o.PartID)";
    private const string FirstProcess = "pr.ProcessNumber = (SELECT MIN(p2.ProcessNumber) FROM Processes p2 WHERE p2.PartID = o.PartID)";
    private const string LaterProcess = "pr.ProcessNumber > (SELECT MIN(p2.ProcessNumber) FROM Processes p2 WHERE p2.PartID = o.PartID)";

    /// <summary>An order's quantity columns and the words for those this cell can't give (selected as NULL).</summary>
    internal sealed record OrderQuantities(IReadOnlyList<string> Columns, IReadOnlyList<string> Missing);

    private const string NoCompleted = "completed quantity";
    private const string NoScrap = "scrap quantity";
    private const string NoRemaining = "remaining quantity";
    private const string NoInProcess = "parts in process";
    private const string NoClamped = "parts clamped on fixtures (in process includes them)";

    /// <summary>The select-list entries for an order's (alias o) quantities: CompletedQty, ScrapQty, RemainingQty and,
    /// with <paramref name="inProcess"/>, ClampedQty and InProcessQty, with the words for any this cell lacks. From
    /// Quantities when readable, else from the vendor's ViewOrders (by order name; no clamped count); null when
    /// neither is readable.</summary>
    internal static OrderQuantities? OrderQuantityColumns(SchemaInfo schema, bool inProcess)
    {
        const string q = "Quantities";
        var missing = new List<string>();
        if (QuantitiesFromTable(schema))
        {
            string Sum(string column, string where, string name, string words)
            {
                if (schema.Has(q, column)) return $"(SELECT SUM(q.{column}) FROM Quantities q {ProcessOf} WHERE q.OrderID = o.OrderID AND {where}) AS {name}";
                missing.Add(words);
                return $"CAST(NULL AS int) AS {name}";
            }
            var cols = new List<string> { Sum("NormalQuantity", LastProcess, "CompletedQty", NoCompleted) };
            if (schema.Has(q, "ScrapQuantity")) cols.Add("(SELECT SUM(q.ScrapQuantity) FROM Quantities q WHERE q.OrderID = o.OrderID) AS ScrapQty");
            else
            {
                cols.Add("CAST(NULL AS int) AS ScrapQty");
                missing.Add(NoScrap);
            }
            cols.Add(Sum("RemainingQuantity", FirstProcess, "RemainingQty", NoRemaining));
            if (!inProcess) return new OrderQuantities(cols, missing);

            var clamped = schema.Has("FixtureQuantity", "ClampedQuantity") && schema.Has("FixtureQuantity", "PalletFixtureID")
                && schema.Has("FixtureRun", "PalletFixtureID") && schema.Has("FixtureRun", "CurOrderID");
            if (clamped && schema.Has(q, "RemainingQuantity"))
            {
                const string onFixtures =
                    "(SELECT SUM(fq.ClampedQuantity) FROM FixtureQuantity fq JOIN FixtureRun fr ON fr.PalletFixtureID = fq.PalletFixtureID WHERE fr.CurOrderID = o.OrderID)";
                cols.Add($"{onFixtures} AS ClampedQty");
                cols.Add($"ISNULL({onFixtures}, 0) + ISNULL((SELECT SUM(q.RemainingQuantity) FROM Quantities q {ProcessOf} " +
                         $"WHERE q.OrderID = o.OrderID AND {LaterProcess}), 0) AS InProcessQty");
            }
            else
            {
                cols.Add("CAST(NULL AS int) AS ClampedQty");
                cols.Add("CAST(NULL AS int) AS InProcessQty");
                missing.Add(NoInProcess);
            }
            return new OrderQuantities(cols, missing);
        }

        if (schema.HasTable("ViewOrders") && schema.Has("ViewOrders", "OrderName"))
        {
            var partMatch = ViewPartMatch(schema, "ViewOrders", "v", "pa");
            if (partMatch is null) missing.Add(ViewNameOnly);
            string View(string column, string name, string words)
            {
                if (schema.Has("ViewOrders", column))
                    return $"(SELECT TOP 1 v.{column} FROM ViewOrders v WHERE v.OrderName = o.OrderName{partMatch}) AS {name}";
                missing.Add(words);
                return $"CAST(NULL AS int) AS {name}";
            }
            var cols = new List<string>
            {
                View("NormalQty", "CompletedQty", NoCompleted), View("ScrapQty", "ScrapQty", NoScrap), View("RemainQty", "RemainingQty", NoRemaining),
            };
            if (inProcess)
            {
                cols.Add("CAST(NULL AS int) AS ClampedQty");
                cols.Add(View("InProcQty", "InProcessQty", NoInProcess));
                if (!missing.Contains(NoInProcess)) missing.Add(NoClamped);
            }
            return new OrderQuantities(cols, missing);
        }
        return null;
    }

    /// <summary>Whether the quantities come from the cell's own table (by order id), not the vendor's view.</summary>
    private static bool QuantitiesFromTable(SchemaInfo schema) =>
        schema.HasTable("Quantities") && schema.HasTable("Processes")
        && schema.Has("Quantities", "OrderID") && schema.Has("Quantities", "ProcessID")
        && schema.Has("Processes", "ProcessID") && schema.Has("Processes", "PartID") && schema.Has("Processes", "ProcessNumber");

    /// <summary>Parts sent to an external process, from the quantity table (the vendor's view doesn't have them); null
    /// when the cell can't give them.</summary>
    private static string? OutProcessColumn(SchemaInfo schema) =>
        QuantitiesFromTable(schema) && schema.Has("Quantities", "OutProcessQuantity")
            ? "(SELECT SUM(q.OutProcessQuantity) FROM Quantities q WHERE q.OrderID = o.OrderID) AS OutProcessQty"
            : null;

    internal const string NoOutProcess = "parts at an external process";

    /// <summary>The order's machining and work-set station minutes in the cell's results (alias o, part alias pa),
    /// matched by order name, part and revision from the order's registration on (a day early); null for a side this
    /// cell's results can't be matched for.</summary>
    private static (string? Machining, string? WorkSet) CycleColumns(SchemaInfo schema)
    {
        var since = schema.Has("Orders", "RegistrationDate") ? " AND x.StartDateTime >= DATEADD(day, -1, CAST(o.RegistrationDate AS date))" : "";
        string? Sum(string table, string order, string part, string revision)
        {
            if (!schema.HasTable(table) || !new[] { "StartDateTime", "FinishDateTime", order, part, revision }.All(c => schema.Has(table, c))
                || !schema.Has("Parts", "PartName") || !schema.Has("Parts", "Revision")) return null;
            return $"(SELECT SUM(DATEDIFF(minute, x.StartDateTime, x.FinishDateTime)) FROM {table} x " +
                   $"WHERE x.{order} = o.OrderName AND x.{part} = pa.PartName AND ISNULL(x.{revision}, '') = ISNULL(pa.Revision, '') " +
                   $"AND x.FinishDateTime >= x.StartDateTime{since})";
        }
        return (Sum("MachineResults", "OrderName", "PartName", "Revision"),
                Sum("WorkSetResults", "ClampOrderName", "ClampPartName", "ClampRevision"));
    }

    internal const string CycleTimeNote =
        "Approximate, like the console's Part Cycle Time report: the minutes from start to finish of every machining and " +
        "work-set result of the order (matched by order name, part and revision, from its registration on) within the " +
        "history the cell keeps. Work-set minutes run from the station's start to its finish, so time the station " +
        "waited for the operator is in them, and a work-set operation on several fixtures is counted once per fixture; " +
        "machiningMinutesPerPart divides by the parts made so far (completed + scrap) although parts still in process " +
        "have used time too, so it reads high while an order runs.";

    private static List<string> NoQuantityColumns(bool inProcess) =>
    [
        "CAST(NULL AS int) AS CompletedQty", "CAST(NULL AS int) AS ScrapQty", "CAST(NULL AS int) AS RemainingQty",
        .. inProcess ? (string[])["CAST(NULL AS int) AS ClampedQty", "CAST(NULL AS int) AS InProcessQty"] : [],
    ];

    private const string QuantitiesNotAvailable = "completed, scrap and remaining quantities";

    /// <summary>notAvailable's words when a vendor view can only be matched to orders by name.</summary>
    internal const string ViewNameOnly =
        "matching the cell's order views to orders by part (matched by order name only: orders of different parts that share a name are mixed)";

    /// <summary>The extra condition matching a vendor view's row (alias <paramref name="v"/>) to its order's part (alias
    /// <paramref name="part"/>): two parts may carry the same order name. Null when either side lacks the part name
    /// or revision, so only the name can match.</summary>
    private static string? ViewPartMatch(SchemaInfo schema, string view, string v, string part) =>
        schema.Has(view, "PartName") && schema.Has(view, "PartRevision") && schema.Has("Parts", "PartName") && schema.Has("Parts", "Revision")
            ? $" AND {v}.PartName = {part}.PartName AND ISNULL({v}.PartRevision, '') = ISNULL({part}.Revision, '')"
            : null;

    /// <summary>Daily rows outlive their order, so a newer order of the same name and part would pick up a deleted one's
    /// days: only days from the order's registration on (a day early, for a production day that starts before
    /// midnight).</summary>
    private static string RegisteredSince(SchemaInfo schema, string d, string order) =>
        schema.Has("Orders", "RegistrationDate")
            ? $" AND {d}.ProductionDate >= DATEADD(day, -1, CAST({order}.RegistrationDate AS date))"
            : "";

    // Where the daily counts come from: the table (by order id), else the vendor's view (by order name), else nowhere.
    private enum DailySource { None, Table, View }

    private static DailySource Daily(SchemaInfo schema)
    {
        string[] counts = ["ProductionDate", "NormalQtyDay", "ScrapQtyDay"];
        if (schema.HasTable("OrderCompletedQtyResults") && counts.Append("OrderID").All(c => schema.Has("OrderCompletedQtyResults", c)))
            return DailySource.Table;
        if (schema.HasTable("ViewDayOrderCompletedQty") && counts.Append("OrderName").All(c => schema.Has("ViewDayOrderCompletedQty", c)))
            return DailySource.View;
        return DailySource.None;
    }

    /// <summary>A name filter: exactly @name when such a row exists, else every name containing it (@nameLike).</summary>
    private static string NameMatch(string table, string column, string alias, string param) =>
        $"((EXISTS (SELECT 1 FROM {table} x WHERE UPPER(x.{column}) = UPPER(@{param})) AND UPPER({alias}.{column}) = UPPER(@{param})) " +
        $"OR (NOT EXISTS (SELECT 1 FROM {table} x WHERE UPPER(x.{column}) = UPPER(@{param})) " +
        $"AND UPPER({alias}.{column}) LIKE UPPER(@{param}Like) ESCAPE '\\'))";

    /// <summary>Three result sets: the orders (cap + 1; active ones, or those named by @order / @part whatever their
    /// status; most produced in the window first, then latest due) with their quantities and window sums; the daily
    /// counts since @from (cap + 1, newest first, days with nothing left out): one total per day across those orders
    /// (OrderID NULL), or per order when an order or part is named; and the count of active orders left out.</summary>
    internal static string ProductionSql(SchemaInfo schema, bool byOrder, bool byPart, bool includeIdle, bool includeCycleTime = false)
    {
        var conditions = new List<string>();
        if (byOrder) conditions.Add(NameMatch("Orders", "OrderName", "o", "order"));
        if (byPart) conditions.Add(NameMatch("Parts", "PartName", "pa", "part"));
        if (!byOrder && !byPart && schema.Has("Orders", "Status")) conditions.Add($"o.Status NOT IN ({FinishedOrderStatuses})");
        var activeWhere = conditions.Count > 0 ? string.Join(" AND ", conditions) : "1 = 1";

        var source = Daily(schema);
        const string dayView = "ViewDayOrderCompletedQty";
        var viewMatch = ViewPartMatch(schema, dayView, "d", "pa") + RegisteredSince(schema, "d", "o");
        var dailyMatch = ViewPartMatch(schema, dayView, "d", "pd") + RegisteredSince(schema, "d", "od");
        var dailyJoin = $"FROM {dayView} d JOIN Orders od ON od.OrderName = d.OrderName LEFT JOIN Parts pd ON pd.PartID = od.PartID";
        string? Window(string column) => source switch
        {
            DailySource.Table => $"(SELECT SUM(d.{column}) FROM OrderCompletedQtyResults d WHERE d.OrderID = o.OrderID AND d.ProductionDate >= @from)",
            DailySource.View => $"(SELECT SUM(d.{column}) FROM {dayView} d WHERE d.OrderName = o.OrderName{viewMatch} AND d.ProductionDate >= @from)",
            _ => null,
        };
        var windowCompleted = Window("NormalQtyDay");
        var windowScrap = Window("ScrapQtyDay");

        // Unless asked for every order (or one is named), only those with something happening: output or scrap in
        // the window, or parts on a fixture that isn't parked (suspended or on hold). The rest are counted.
        var activity = includeIdle || byOrder || byPart ? null : ActivitySql(schema, windowCompleted, windowScrap);
        var where = activity is null ? activeWhere : $"{activeWhere} AND ({activity})";
        var others = activity is null
            ? "SELECT CAST(NULL AS int) AS OtherActiveOrders;"
            : $"SELECT COUNT(*) AS OtherActiveOrders FROM Orders o LEFT JOIN Parts pa ON pa.PartID = o.PartID WHERE {activeWhere} AND NOT ({activity});";
        var due = schema.Has("Orders", "DueDate") ? "o.DueDate DESC, " : "";
        var order = (windowCompleted is null ? "" : $"{windowCompleted} DESC, ") + due + "o.OrderID DESC";

        List<string> cols =
        [
            "o.OrderID",
            Opt(schema, "Orders", "o", "OrderName", type: Text),
            Opt(schema, "Parts", "pa", "PartName", type: Text),
            Opt(schema, "Parts", "pa", "Revision", type: Text),
            Opt(schema, "Orders", "o", "Status"),
            Opt(schema, "Orders", "o", "Quantity"),
            .. OrderQuantityColumns(schema, inProcess: true)?.Columns ?? NoQuantityColumns(inProcess: true),
            windowCompleted is null ? "CAST(NULL AS int) AS WindowCompleted" : $"{windowCompleted} AS WindowCompleted",
            windowScrap is null ? "CAST(NULL AS int) AS WindowScrap" : $"{windowScrap} AS WindowScrap",
            OutProcessColumn(schema) ?? "CAST(NULL AS int) AS OutProcessQty",
        ];
        if (includeCycleTime)
        {
            var (machining, workSet) = CycleColumns(schema);
            cols.Add(machining is null ? "CAST(NULL AS int) AS MachiningMinutes" : $"{machining} AS MachiningMinutes");
            cols.Add(workSet is null ? "CAST(NULL AS int) AS WorkSetMinutes" : $"{workSet} AS WorkSetMinutes");
        }
        const string from = "FROM Orders o LEFT JOIN Parts pa ON pa.PartID = o.PartID";
        var picked = $"SELECT TOP (@take) o.OrderID {from} WHERE {where} ORDER BY {order}";
        var perOrder = byOrder || byPart;
        var daily = (source, perOrder) switch
        {
            (DailySource.Table, false) => $"""
                SELECT TOP (@take) CAST(NULL AS int) AS OrderID, d.ProductionDate, SUM(d.NormalQtyDay) AS NormalQtyDay, SUM(d.ScrapQtyDay) AS ScrapQtyDay
                FROM OrderCompletedQtyResults d
                WHERE d.ProductionDate >= @from AND d.OrderID IN ({picked})
                GROUP BY d.ProductionDate
                HAVING SUM(d.NormalQtyDay) <> 0 OR SUM(d.ScrapQtyDay) <> 0
                ORDER BY d.ProductionDate DESC;
                """,
            (DailySource.View, false) => $"""
                SELECT TOP (@take) CAST(NULL AS int) AS OrderID, d.ProductionDate, SUM(d.NormalQtyDay) AS NormalQtyDay, SUM(d.ScrapQtyDay) AS ScrapQtyDay
                {dailyJoin}
                WHERE d.ProductionDate >= @from{dailyMatch} AND od.OrderID IN ({picked})
                GROUP BY d.ProductionDate
                HAVING SUM(d.NormalQtyDay) <> 0 OR SUM(d.ScrapQtyDay) <> 0
                ORDER BY d.ProductionDate DESC;
                """,
            (DailySource.Table, true) => $"""
                SELECT TOP (@take) d.OrderID, d.ProductionDate, d.NormalQtyDay, d.ScrapQtyDay
                FROM OrderCompletedQtyResults d
                WHERE d.ProductionDate >= @from AND (d.NormalQtyDay <> 0 OR d.ScrapQtyDay <> 0) AND d.OrderID IN ({picked})
                ORDER BY d.ProductionDate DESC, d.OrderID;
                """,
            (DailySource.View, true) => $"""
                SELECT TOP (@take) od.OrderID, d.ProductionDate, d.NormalQtyDay, d.ScrapQtyDay
                {dailyJoin}
                WHERE d.ProductionDate >= @from{dailyMatch} AND (d.NormalQtyDay <> 0 OR d.ScrapQtyDay <> 0) AND od.OrderID IN ({picked})
                ORDER BY d.ProductionDate DESC, od.OrderID;
                """,
            _ => "SELECT CAST(NULL AS int) AS OrderID, CAST(NULL AS date) AS ProductionDate, CAST(NULL AS int) AS NormalQtyDay, " +
                 "CAST(NULL AS int) AS ScrapQtyDay WHERE 1 = 0;",
        };
        return $"""
            SELECT TOP (@take) {string.Join(", ", cols)}
            {from}
            WHERE {where}
            ORDER BY {order};
            {daily}
            {others}
            """;
    }

    /// <summary>"Something happened to the order" (alias o): output or scrap since @from, or parts clamped on a
    /// fixture now that isn't suspended or on hold. Null when the cell can tell none of these.</summary>
    private static string? ActivitySql(SchemaInfo schema, string? windowCompleted, string? windowScrap)
    {
        var parts = new List<string>();
        if (windowCompleted is not null) parts.Add($"ISNULL({windowCompleted}, 0) > 0");
        if (windowScrap is not null) parts.Add($"ISNULL({windowScrap}, 0) > 0");
        if (schema.Has("FixtureQuantity", "ClampedQuantity") && schema.Has("FixtureQuantity", "PalletFixtureID")
            && schema.Has("FixtureRun", "PalletFixtureID") && schema.Has("FixtureRun", "CurOrderID"))
        {
            var notParked = new List<string>();
            if (schema.Has("PalletFixtures", "Suspend")) notParked.Add("ISNULL(pf.Suspend, 0) = 0");
            if (schema.Has("PalletFixtures", "WorkSetHold")) notParked.Add("ISNULL(pf.WorkSetHold, 0) = 0");
            var join = notParked.Count > 0 && schema.Has("PalletFixtures", "PalletFixtureID")
                ? " JOIN PalletFixtures pf ON pf.PalletFixtureID = fq.PalletFixtureID" : "";
            var filter = join.Length > 0 ? " AND " + string.Join(" AND ", notParked) : "";
            parts.Add("ISNULL((SELECT SUM(fq.ClampedQuantity) FROM FixtureQuantity fq JOIN FixtureRun fr ON fr.PalletFixtureID = fq.PalletFixtureID" +
                      $"{join} WHERE fr.CurOrderID = o.OrderID{filter}), 0) > 0");
        }
        return parts.Count > 0 ? string.Join(" OR ", parts) : null;
    }

    // A property: static fields of a partial class across files initialize in no set order.
    private static string[] ProductionParameterNames =>
        [.. OrderParameterNames, "NormalFinIncludeLastOutProc", "ProbePartCountMacroVariable"];

    public async Task<ProductionList> GetProductionAsync(
        int days, string? order, string? part, bool includeIdle, bool includeCycleTime, CancellationToken ct)
    {
        var window = Days(days);
        var byOrder = !string.IsNullOrWhiteSpace(order);
        var byPart = !string.IsNullOrWhiteSpace(part);
        await using var s = await OpenAsync(ct);
        var schema = s.Schema;
        schema.Require("Orders", "Parts");
        // The quantities are this tool's purpose: without the table or the vendor's view it is an error.
        var quantities = OrderQuantityColumns(schema, inProcess: true) ?? throw new CellSchemaException(["Quantities", "ViewOrders"]);

        var notAvailable = new List<string>(quantities.Missing);
        if (Daily(schema) == DailySource.None) notAvailable.Add("daily quantities");
        else if (Daily(schema) == DailySource.View && ViewPartMatch(schema, "ViewDayOrderCompletedQty", "d", "pa") is null
                 && !notAvailable.Contains(ViewNameOnly)) notAvailable.Add(ViewNameOnly);
        if (OutProcessColumn(schema) is null) notAvailable.Add(NoOutProcess);
        if (includeCycleTime)
        {
            var (machining, workSet) = CycleColumns(schema);
            if (machining is null) notAvailable.Add("machining minutes per order (the machining results can't be matched to orders)");
            if (workSet is null) notAvailable.Add("work-set minutes per order (the station results can't be matched to orders)");
        }
        var settings = await SystemParametersAsync(s, ct, ProductionParameterNames);

        using var cmd = Command(s.Connection, ProductionSql(schema, byOrder, byPart, includeIdle, includeCycleTime));
        AddTake(cmd);
        cmd.Parameters.Add("@from", System.Data.SqlDbType.DateTime).Value = s.Now.Date.AddDays(1 - window);
        if (byOrder)
        {
            cmd.Parameters.Add("@order", System.Data.SqlDbType.VarChar, 400).Value = order!.Trim();
            cmd.Parameters.Add("@orderLike", System.Data.SqlDbType.VarChar, 1000).Value = LikePattern(order.Trim());
        }
        if (byPart)
        {
            cmd.Parameters.Add("@part", System.Data.SqlDbType.VarChar, 400).Value = part!.Trim();
            cmd.Parameters.Add("@partLike", System.Data.SqlDbType.VarChar, 1000).Value = LikePattern(part.Trim());
        }

        var orders = new List<(int Id, OrderProduction Order)>();
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            orders.Add((Int(r, "OrderID") ?? 0, new OrderProduction(
                Str(r, "OrderName"), PartLabel(Str(r, "PartName"), Str(r, "Revision")), Codes.OrderStatus(Int(r, "Status")),
                Int(r, "Quantity") ?? 0)
            {
                CompletedQty = Int(r, "CompletedQty"), ScrapQty = Int(r, "ScrapQty"), RemainingQty = Int(r, "RemainingQty"),
                InProcessQty = Int(r, "InProcessQty"), ClampedQty = Int(r, "ClampedQty"),
                CompletedInWindow = Int(r, "WindowCompleted"), ScrapInWindow = Int(r, "WindowScrap"),
                OutProcessQty = Int(r, "OutProcessQty") is { } outQty and not 0 ? outQty : null,
            }));
            if (includeCycleTime)
            {
                var o = orders[^1].Order;
                var machining = Int(r, "MachiningMinutes");
                var made = (o.CompletedQty ?? 0) + (o.ScrapQty ?? 0);
                orders[^1] = (orders[^1].Id, o with
                {
                    MachiningMinutes = machining,
                    WorkSetMinutes = Int(r, "WorkSetMinutes"),
                    MachiningMinutesPerPart = machining is > 0 && made > 0 ? Math.Round((double)machining.Value / made, 1) : null,
                });
            }
        }

        await r.NextResultAsync(ct);
        var daily = new Dictionary<int, List<DailyQty>>();
        var totals = new List<DailyQty>();
        var dailyRows = 0;
        while (await r.ReadAsync(ct))
        {
            if (++dailyRows > _cap) break; // the cap + 1st row only says more existed
            if (Date(r, "ProductionDate") is not { } day) continue;
            var row = new DailyQty(DateOnly.FromDateTime(day), Int(r, "NormalQtyDay") ?? 0, Int(r, "ScrapQtyDay") ?? 0);
            if (Int(r, "OrderID") is not { } id)
            {
                totals.Add(row);
                continue;
            }
            if (!daily.TryGetValue(id, out var list)) daily[id] = list = [];
            list.Add(row);
        }

        await r.NextResultAsync(ct);
        int? others = await r.ReadAsync(ct) ? Int(r, "OtherActiveOrders") : null;

        var rows = orders.Select(o => o.Order with { Daily = daily.TryGetValue(o.Id, out var d) ? d : null }).ToList();
        var (shown, capped) = Cap(rows);
        return new ProductionList(s.Now, capped, window, shown)
        {
            CompleteIncludesScrap = CompleteIncludesScrap(settings),
            Daily = byOrder || byPart || totals.Count == 0 ? null : totals,
            DailyCapped = dailyRows > _cap ? true : null,
            NotAvailable = notAvailable.Count > 0 ? notAvailable : null,
            Note = ProductionNote(order, part, shown.Count),
            OtherActiveOrders = others,
            CompletedIncludesLastOutProcess = settings is null ? null : ToInt(settings.GetValueOrDefault("NormalFinIncludeLastOutProc")) == 1,
            ProbePartCount = ToInt(settings?.GetValueOrDefault("ProbePartCountMacroVariable")) is > 0 ? true : null,
            AutoDelete = AutoDeleteWords(settings),
            CycleTimeNote = includeCycleTime ? CycleTimeNote : null,
        };
    }

    /// <summary>When a named order or part found no orders: what was looked for; else null.</summary>
    internal static string? ProductionNote(string? order, string? part, int found)
    {
        if (found > 0) return null;
        var asked = new List<string>();
        if (!string.IsNullOrWhiteSpace(order)) asked.Add($"order name is or contains '{order.Trim()}'");
        if (!string.IsNullOrWhiteSpace(part)) asked.Add($"part name is or contains '{part.Trim()}'");
        return asked.Count == 0 ? null : $"No order whose {string.Join(" and whose ", asked)}.";
    }
}
