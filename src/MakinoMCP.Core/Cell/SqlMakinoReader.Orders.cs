namespace MakinoMCP.Core.Cell;

public sealed partial class SqlMakinoReader
{
    // PalletLocation.CurDeviceType 0: the pallet was taken out of the system (an offline pallet).
    private const int OutOfSystem = 0;
    internal const string OutOfSystemWords = "out of system (offline pallet)";

    // Orders.Status codes left out of an "active only" list: undefined, complete and cancelled.
    private const string FinishedOrderStatuses = "0, 5, 6";

    public async Task<OrderList> GetOrdersAsync(bool activeOnly, CancellationToken ct)
    {
        await using var s = await OpenAsync(ct);
        s.Schema.Require("Orders", "Parts");
        var settings = await SystemParametersAsync(s, ct, OrderParameterNames);
        var machiningSuspendCounts = MachiningSuspendCounts(settings);
        using var cmd = Command(s.Connection, OrdersSql(s.Schema, activeOnly));
        AddTake(cmd);

        var rows = new List<OrderInfo>();
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var revision = Str(r, "Revision");
            rows.Add(new OrderInfo(
                Str(r, "OrderName"),
                revision.Length > 0 ? $"{Str(r, "PartName")} rev {revision}" : Str(r, "PartName"),
                Codes.OrderStatus(Int(r, "Status")),
                Int(r, "Quantity") ?? 0,
                machiningSuspendCounts && Int(r, "MachiningSuspended") is > 0 ? true : null,
                Date(r, "DueDate"),
                Date(r, "CompleteDate"))
            {
                CompletedQty = Int(r, "CompletedQty"), ScrapQty = Int(r, "ScrapQty"), RemainingQty = Int(r, "RemainingQty"),
            });
        }
        var (orders, capped) = Cap(rows);
        var notAvailable = OrderQuantityColumns(s.Schema, inProcess: false) switch
        {
            null => [QuantitiesNotAvailable],
            { Missing.Count: > 0 } quantities => quantities.Missing.ToList(),
            _ => new List<string>(),
        };
        if (!s.Schema.Has("Orders", "MachiningSuspended")) notAvailable.Add("order machining suspend");
        return new OrderList(s.Now, capped, orders)
        {
            CompleteIncludesScrap = CompleteIncludesScrap(settings),
            NotAvailable = notAvailable.Count > 0 ? notAvailable : null,
        };
    }

    private static readonly string[] OrderParameterNames = ["OrderMachiningSuspend", "OrderIncludesScrap"];

    /// <summary>Whether an order's MachiningSuspended flag does anything: only with the cell's OrderMachiningSuspend
    /// setting at 1 (0 or no row, the default, and the console hides the column and ignores the flag). Settings this
    /// login can't read: shown, since it can't be ruled out.</summary>
    internal static bool MachiningSuspendCounts(IReadOnlyDictionary<string, string>? settings) =>
        settings is null || ToInt(settings.GetValueOrDefault("OrderMachiningSuspend")) == 1;

    /// <summary>The cell's OrderIncludesScrap setting: complete counts scrap too (1, and the default when there is no
    /// row) or good parts only (0); null when the settings can't be read.</summary>
    internal static bool? CompleteIncludesScrap(IReadOnlyDictionary<string, string>? settings) =>
        settings is null ? null : ToInt(settings.GetValueOrDefault("OrderIncludesScrap")) != 0;

    /// <summary>Orders with their part and quantities (the same as get_production's), latest due first. Without a
    /// Status column activeOnly can't filter.</summary>
    internal static string OrdersSql(SchemaInfo schema, bool activeOnly)
    {
        List<string> cols =
        [
            Opt(schema, "Orders", "o", "OrderName", type: Text),
            Opt(schema, "Parts", "pa", "PartName", type: Text),
            Opt(schema, "Parts", "pa", "Revision", type: Text),
            Opt(schema, "Orders", "o", "Status"),
            Opt(schema, "Orders", "o", "Quantity"),
            Opt(schema, "Orders", "o", "MachiningSuspended"),
            Opt(schema, "Orders", "o", "DueDate", type: "datetime"),
            Opt(schema, "Orders", "o", "CompleteDate", type: "datetime"),
            .. OrderQuantityColumns(schema, inProcess: false)?.Columns ?? NoQuantityColumns(inProcess: false),
        ];
        var where = activeOnly && schema.Has("Orders", "Status") ? $"WHERE o.Status NOT IN ({FinishedOrderStatuses})" : "";
        var order = schema.Has("Orders", "DueDate") ? "o.DueDate DESC, o.OrderID DESC" : "o.OrderID DESC";
        return $"""
            SELECT TOP (@take) {string.Join(", ", cols)}
            FROM Orders o LEFT JOIN Parts pa ON pa.PartID = o.PartID
            {where}
            ORDER BY {order};
            """;
    }

    public async Task<PalletList> GetPalletsAsync(CancellationToken ct)
    {
        await using var s = await OpenAsync(ct);
        s.Schema.Require("Pallets", "PalletLocation", "PalletFixtures", "Fixtures");
        using var cmd = Command(s.Connection, PalletsSql(s.Schema));
        AddTake(cmd);

        var pallets = new List<(int Id, PalletInfo Info)>();
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var type = Int(r, "PalletType");
            pallets.Add((Int(r, "PalletID") ?? 0, new PalletInfo(
                Int(r, "PalletNumber") ?? 0,
                Str(r, "PalletName"),
                PalletStatusWords(type, Int(r, "PalletStatus")),
                PalletWorkStatusWords(type, Int(r, "WorkStatus")),
                Location(s, Int(r, "CurDeviceType"), Int(r, "CurDeviceNumber"), Int(r, "CurDevicePosition")),
                [])));
        }
        await r.NextResultAsync(ct);
        var fixtures = new Dictionary<int, List<string>>();
        while (await r.ReadAsync(ct))
        {
            // Fixture names are often blank on a cell; the number on the pallet always identifies it.
            var flags = Join(Bool(r, "Suspend") == true ? "suspended" : "", Bool(r, "Damaged") == true ? "damaged" : "");
            var text = Join(Labelled("fixture", Str(r, "FixtureNumber")), Str(r, "FixtureName"));
            var id = Int(r, "PalletID") ?? 0;
            if (!fixtures.TryGetValue(id, out var names)) fixtures[id] = names = [];
            names.Add(flags.Length > 0 ? $"{text} ({flags})" : text);
        }

        var rows = pallets.Select(p => p.Info with { Fixtures = fixtures.GetValueOrDefault(p.Id) ?? [] }).ToList();
        var (list, capped) = Cap(rows);
        return new PalletList(s.Now, capped, list);
    }

    /// <summary>Pallets with their location, then the fixtures on those same pallets.</summary>
    internal static string PalletsSql(SchemaInfo schema)
    {
        string Location(string column) => schema.Has("PalletLocation", column)
            ? $"(SELECT TOP 1 l.{column} FROM PalletLocation l WHERE l.PalletID = p.PalletID) AS {column}"
            : $"CAST(NULL AS int) AS {column}";

        var order = schema.Has("Pallets", "PalletNumber") ? "p.PalletNumber, p.PalletID" : "p.PalletID";
        var pallets = string.Join(", ",
            "p.PalletID",
            Opt(schema, "Pallets", "p", "PalletNumber"),
            Opt(schema, "Pallets", "p", "PalletName", type: Text),
            Opt(schema, "Pallets", "p", "PalletStatus"),
            Opt(schema, "Pallets", "p", "WorkStatus"),
            Opt(schema, "Pallets", "p", "PalletType"),
            Location("CurDeviceType"), Location("CurDeviceNumber"), Location("CurDevicePosition"));
        var fixtures = string.Join(", ",
            "pf.PalletID",
            Opt(schema, "PalletFixtures", "pf", "FixtureNumber"),
            Opt(schema, "Fixtures", "f", "FixtureName", type: Text),
            Opt(schema, "PalletFixtures", "pf", "Suspend", type: "bit"),
            Opt(schema, "PalletFixtures", "pf", "Damaged", type: "bit"));
        return $"""
            SELECT TOP (@take) {pallets}
            FROM Pallets p
            ORDER BY {order};
            SELECT {fixtures}
            FROM PalletFixtures pf LEFT JOIN Fixtures f ON f.FixtureID = pf.FixtureID
            WHERE pf.PalletID IN (SELECT TOP (@take) p.PalletID FROM Pallets p ORDER BY {order})
            ORDER BY pf.PalletID, FixtureNumber;
            """;
    }

    // The pallet types whose status and work status the cell rewrites into dashboard codes (its UpdatePalletStatus
    // procedure). For other types it always stores PalletStatus 0 and passes the raw work status through, so neither
    // value is a dashboard code there and both show "code N". No PalletType column (an older cell): treat as a
    // dashboard pallet.
    private static readonly int[] DashboardPalletTypes = [0, 1, 2, 8, 16];

    internal static string PalletStatusWords(int? palletType, int? status) =>
        IsDashboardPallet(palletType) ? Codes.PalletDisplayStatus(status) : CodeText(status);

    internal static string PalletWorkStatusWords(int? palletType, int? workStatus) =>
        IsDashboardPallet(palletType) ? Codes.PalletWorkStatus(workStatus) : CodeText(workStatus);

    private static bool IsDashboardPallet(int? palletType) => palletType is null || DashboardPalletTypes.Contains(palletType.Value);

    /// <summary>Where a pallet is, in words: the device's name when the cell has a device row for that place, else its
    /// type and number (stockers usually have no row); then the position. Type 0 is a pallet taken out of the system
    /// (pallet output); no location at all is "unknown".</summary>
    private static string Location(Session s, int? type, int? number, int? position)
    {
        if (type is null) return "unknown";
        if (type == OutOfSystem) return OutOfSystemWords;
        var device = s.Devices.FirstOrDefault(d => d.Type == type && d.Number == number)?.Name
            ?? $"{Codes.DeviceType(type)} {number}";
        return position is > 0 ? $"{device}, position {position}" : device;
    }
}
