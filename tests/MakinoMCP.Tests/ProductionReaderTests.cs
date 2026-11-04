using Xunit;
using MakinoMCP.Core.Cell;

namespace MakinoMCP.Tests;

// get_production and get_orders' quantities, without a database.
public class ProductionReaderTests
{
    private static readonly Dictionary<string, string[]> Schema = new()
    {
        ["Devices"] = ["DeviceID", "DeviceName", "DeviceType", "DeviceNumber"],
        ["Orders"] = ["OrderID", "PartID", "OrderName", "Quantity", "Status", "DueDate", "CompleteDate", "MachiningSuspended"],
        ["Parts"] = ["PartID", "PartName", "Revision"],
        ["Processes"] = ["ProcessID", "PartID", "ProcessNumber"],
        ["Quantities"] = ["OrderID", "ProcessID", "RemainingQuantity", "NormalQuantity", "ScrapQuantity", "MaterialGroupNo"],
        ["FixtureQuantity"] = ["PalletFixtureID", "OperationID", "ClampedQuantity"],
        ["FixtureRun"] = ["PalletFixtureID", "CurOrderID", "CurProcessID"],
        ["OrderCompletedQtyResults"] = ["ProductionDate", "OrderID", "NormalQtyDay", "ScrapQtyDay"],
        ["ViewOrders"] = ["OrderName", "NormalQty", "ScrapQty", "RemainQty", "InProcQty"],
        ["ViewDayOrderCompletedQty"] = ["ProductionDate", "OrderName", "NormalQtyDay", "ScrapQtyDay"],
    };

    private static SchemaInfo SchemaWithout(params string[] tableDotColumn) =>
        new(Schema
            .SelectMany(t => t.Value.Select(c => (Table: t.Key, Column: c)))
            .Where(tc => !tableDotColumn.Contains(tc.Table + "." + tc.Column) && !tableDotColumn.Contains(tc.Table)));

    [Fact]
    public void Completed_is_the_last_process_good_count_scrap_the_sum_remaining_the_first_process()
    {
        var cols = string.Join("\n", SqlMakinoReader.OrderQuantityColumns(SchemaWithout(), inProcess: false)!);

        // Completed: good parts of the part's highest-numbered process (summed over material groups).
        Assert.Contains("SUM(q.NormalQuantity)", cols);
        Assert.Contains("pr.ProcessNumber = (SELECT MAX(p2.ProcessNumber) FROM Processes p2 WHERE p2.PartID = o.PartID)", cols);
        Assert.Contains("AS CompletedQty", cols);
        // Scrap: every process of the order.
        Assert.Contains("(SELECT SUM(q.ScrapQuantity) FROM Quantities q WHERE q.OrderID = o.OrderID) AS ScrapQty", cols);
        // Remaining (not started): the first process.
        Assert.Contains("pr.ProcessNumber = (SELECT MIN(p2.ProcessNumber) FROM Processes p2 WHERE p2.PartID = o.PartID)", cols);
        Assert.Contains("AS RemainingQty", cols);
        Assert.DoesNotContain("InProcessQty", cols);
    }

    [Fact]
    public void In_process_is_parts_clamped_for_the_order_plus_those_waiting_between_processes()
    {
        var cols = string.Join("\n", SqlMakinoReader.OrderQuantityColumns(SchemaWithout(), inProcess: true)!);

        Assert.Contains("SUM(fq.ClampedQuantity) FROM FixtureQuantity fq JOIN FixtureRun fr ON fr.PalletFixtureID = fq.PalletFixtureID WHERE fr.CurOrderID = o.OrderID", cols);
        Assert.Contains("pr.ProcessNumber > (SELECT MIN(p2.ProcessNumber)", cols);
        Assert.Contains("AS ClampedQty", cols);
        Assert.Contains("AS InProcessQty", cols);

        var noFixtures = string.Join("\n", SqlMakinoReader.OrderQuantityColumns(SchemaWithout("FixtureRun"), inProcess: true)!);
        Assert.Contains("CAST(NULL AS int) AS InProcessQty", noFixtures);
        Assert.Contains("CAST(NULL AS int) AS ClampedQty", noFixtures);
    }

    [Fact]
    public void Without_quantities_the_vendor_order_view_gives_them_and_without_both_there_are_none()
    {
        var view = string.Join("\n", SqlMakinoReader.OrderQuantityColumns(SchemaWithout("Quantities"), inProcess: true)!);
        Assert.Contains("(SELECT TOP 1 v.NormalQty FROM ViewOrders v WHERE v.OrderName = o.OrderName) AS CompletedQty", view);
        Assert.Contains("v.InProcQty", view);
        Assert.Contains("CAST(NULL AS int) AS ClampedQty", view);

        Assert.Null(SqlMakinoReader.OrderQuantityColumns(SchemaWithout("Quantities", "ViewOrders"), inProcess: false));
    }

    [Fact]
    public void Orders_carry_the_same_quantities()
    {
        var sql = SqlMakinoReader.OrdersSql(SchemaWithout(), activeOnly: true);
        Assert.Contains("AS CompletedQty", sql);
        Assert.Contains("AS ScrapQty", sql);
        Assert.Contains("AS RemainingQty", sql);
        Assert.DoesNotContain("InProcessQty", sql);

        var none = SqlMakinoReader.OrdersSql(SchemaWithout("Quantities", "ViewOrders"), activeOnly: true);
        Assert.Contains("CAST(NULL AS int) AS CompletedQty", none);
    }

    [Fact]
    public void Production_is_bounded_active_by_default_and_named_orders_match_exactly_first()
    {
        var active = SqlMakinoReader.ProductionSql(SchemaWithout(), byOrder: false, byPart: false);
        Assert.Contains("TOP (@take)", active);
        Assert.Contains("o.Status NOT IN (0, 5, 6)", active);
        Assert.Contains("d.ProductionDate >= @from", active);
        Assert.Contains("FROM OrderCompletedQtyResults d", active);
        Assert.Contains("ORDER BY d.ProductionDate DESC", active);

        var named = SqlMakinoReader.ProductionSql(SchemaWithout(), byOrder: true, byPart: false);
        Assert.DoesNotContain("o.Status NOT IN", named); // a named order shows whatever its status
        Assert.Contains("UPPER(x.OrderName) = UPPER(@order)", named);
        Assert.Contains("UPPER(o.OrderName) LIKE UPPER(@orderLike) ESCAPE '\\'", named);

        var part = SqlMakinoReader.ProductionSql(SchemaWithout(), byOrder: false, byPart: true);
        Assert.Contains("UPPER(pa.PartName) LIKE UPPER(@partLike) ESCAPE '\\'", part);
    }

    [Fact]
    public void Daily_quantities_fall_back_to_the_vendor_view_and_else_are_left_out()
    {
        var view = SqlMakinoReader.ProductionSql(SchemaWithout("OrderCompletedQtyResults"), false, false);
        Assert.Contains("FROM ViewDayOrderCompletedQty d JOIN Orders od ON od.OrderName = d.OrderName", view);

        var none = SqlMakinoReader.ProductionSql(SchemaWithout("OrderCompletedQtyResults", "ViewDayOrderCompletedQty"), false, false);
        Assert.Contains("CAST(NULL AS int) AS WindowCompleted", none);
        Assert.Contains("WHERE 1 = 0", none); // the daily result set stays, empty
    }
}
