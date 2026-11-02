namespace MakinoMCP.Core.Cell;

public sealed partial class SqlMakinoReader
{
    // Orders.Status codes left out of an "active only" list: undefined, complete and cancelled.
    private const string FinishedOrderStatuses = "0, 5, 6";

    public async Task<OrderList> GetOrdersAsync(bool activeOnly, CancellationToken ct)
    {
        await using var s = await OpenAsync(ct);
        using var cmd = Command(s.Connection, $"""
            SELECT TOP (@take) o.OrderName, pa.PartName, pa.Revision, o.Status, o.Quantity, o.MachiningSuspended, o.DueDate, o.CompleteDate
            FROM Orders o LEFT JOIN Parts pa ON pa.PartID = o.PartID
            {(activeOnly ? $"WHERE o.Status NOT IN ({FinishedOrderStatuses})" : "")}
            ORDER BY o.DueDate DESC, o.OrderName;
            """);
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

    public async Task<PalletList> GetPalletsAsync(CancellationToken ct)
    {
        await using var s = await OpenAsync(ct);
        using var cmd = Command(s.Connection, """
            SELECT TOP (@take) p.PalletID, p.PalletNumber, p.PalletName, p.PalletStatus,
                   (SELECT TOP 1 l.CurDeviceType FROM PalletLocation l WHERE l.PalletID = p.PalletID) AS CurDeviceType,
                   (SELECT TOP 1 l.CurDeviceNumber FROM PalletLocation l WHERE l.PalletID = p.PalletID) AS CurDeviceNumber,
                   (SELECT TOP 1 l.CurDevicePosition FROM PalletLocation l WHERE l.PalletID = p.PalletID) AS CurDevicePosition
            FROM Pallets p
            ORDER BY p.PalletNumber, p.PalletID;
            SELECT pf.PalletID, pf.FixtureNumber, f.FixtureName, pf.Suspend, pf.Damaged
            FROM PalletFixtures pf LEFT JOIN Fixtures f ON f.FixtureID = pf.FixtureID
            WHERE pf.PalletID IN (SELECT TOP (@take) PalletID FROM Pallets ORDER BY PalletNumber, PalletID)
            ORDER BY pf.PalletID, pf.FixtureNumber;
            """);
        AddTake(cmd);

        var pallets = new List<(int Id, PalletInfo Info)>();
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            pallets.Add((Int(r, "PalletID") ?? 0, new PalletInfo(
                Int(r, "PalletNumber") ?? 0,
                Str(r, "PalletName"),
                Codes.PalletStatus(Int(r, "PalletStatus")),
                Location(s, Int(r, "CurDeviceType"), Int(r, "CurDeviceNumber"), Int(r, "CurDevicePosition")),
                [])));
        }
        await r.NextResultAsync(ct);
        var fixtures = new Dictionary<int, List<string>>();
        while (await r.ReadAsync(ct))
        {
            // Fixture names are often blank on a cell; the number on the pallet always identifies it.
            var flags = Join(Bool(r, "Suspend") == true ? "suspended" : "", Bool(r, "Damaged") == true ? "damaged" : "");
            var text = Join($"fixture {Str(r, "FixtureNumber")}", Str(r, "FixtureName"));
            var id = Int(r, "PalletID") ?? 0;
            if (!fixtures.TryGetValue(id, out var names)) fixtures[id] = names = [];
            names.Add(flags.Length > 0 ? $"{text} ({flags})" : text);
        }

        var rows = pallets.Select(p => p.Info with { Fixtures = fixtures.GetValueOrDefault(p.Id) ?? [] }).ToList();
        var (list, capped) = Cap(rows);
        return new PalletList(s.Now, capped, list);
    }

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
