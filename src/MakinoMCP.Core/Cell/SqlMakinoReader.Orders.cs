namespace MakinoMCP.Core.Cell;

public sealed partial class SqlMakinoReader
{
    // Orders.Status codes left out of an "active only" list: undefined, complete and cancelled.
    private const string FinishedOrderStatuses = "0, 5, 6";

    public async Task<OrderList> GetOrdersAsync(bool activeOnly, CancellationToken ct)
    {
        await using var s = await OpenAsync(ct);
        s.Schema.Require("Orders", "Parts");
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
                Int(r, "MachiningSuspended") is > 0,
                Date(r, "DueDate"),
                Date(r, "CompleteDate")));
        }
        var (orders, capped) = Cap(rows);
        return new OrderList(s.Now, capped, orders);
    }

    /// <summary>Orders with their part, latest due first. Without a Status column activeOnly can't filter.</summary>
    internal static string OrdersSql(SchemaInfo schema, bool activeOnly)
    {
        var cols = string.Join(", ",
            Opt(schema, "Orders", "o", "OrderName", type: Text),
            Opt(schema, "Parts", "pa", "PartName", type: Text),
            Opt(schema, "Parts", "pa", "Revision", type: Text),
            Opt(schema, "Orders", "o", "Status"),
            Opt(schema, "Orders", "o", "Quantity"),
            Opt(schema, "Orders", "o", "MachiningSuspended"),
            Opt(schema, "Orders", "o", "DueDate", type: "datetime"),
            Opt(schema, "Orders", "o", "CompleteDate", type: "datetime"));
        var where = activeOnly && schema.Has("Orders", "Status") ? $"WHERE o.Status NOT IN ({FinishedOrderStatuses})" : "";
        var order = schema.Has("Orders", "DueDate") ? "o.DueDate DESC, o.OrderID DESC" : "o.OrderID DESC";
        return $"""
            SELECT TOP (@take) {cols}
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
    // procedure); other types keep raw values. No PalletType column (an older cell): treat as a dashboard pallet.
    private static readonly int[] DashboardPalletTypes = [0, 1, 2, 8, 16];

    internal static string PalletStatusWords(int? palletType, int? status) =>
        IsDashboardPallet(palletType) ? Codes.PalletDisplayStatus(status) : CodeText(status);

    internal static string PalletWorkStatusWords(int? palletType, int? workStatus) =>
        IsDashboardPallet(palletType) ? Codes.PalletWorkStatus(workStatus) : CodeText(workStatus);

    private static bool IsDashboardPallet(int? palletType) => palletType is null || DashboardPalletTypes.Contains(palletType.Value);

    /// <summary>Where a pallet is, in words: the device's name when the cell has a device row for that place, else its
    /// type and number (stockers usually have no row); then the position.</summary>
    private static string Location(Session s, int? type, int? number, int? position)
    {
        if (type is null or 0) return "unknown";
        var device = s.Devices.FirstOrDefault(d => d.Type == type && d.Number == number)?.Name
            ?? $"{Codes.DeviceType(type)} {number}";
        return position is > 0 ? $"{device}, position {position}" : device;
    }
}
