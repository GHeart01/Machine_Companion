using Xunit;
using MakinoMCP.Core.Cell;

namespace MakinoMCP.Tests;

// get_production and get_orders' quantities, without a database.
public class ProductionReaderTests
{
    private static readonly Dictionary<string, string[]> Schema = new()
    {
        ["Devices"] = ["DeviceID", "DeviceName", "DeviceType", "DeviceNumber"],
        ["Orders"] = ["OrderID", "PartID", "OrderName", "Quantity", "Status", "DueDate", "CompleteDate", "MachiningSuspended", "RegistrationDate"],
        ["Parts"] = ["PartID", "PartName", "Revision"],
        ["Processes"] = ["ProcessID", "PartID", "ProcessNumber"],
        ["Quantities"] = ["OrderID", "ProcessID", "RemainingQuantity", "NormalQuantity", "ScrapQuantity", "MaterialGroupNo"],
        ["FixtureQuantity"] = ["PalletFixtureID", "OperationID", "ClampedQuantity"],
        ["FixtureRun"] = ["PalletFixtureID", "CurOrderID", "CurProcessID"],
        ["PalletFixtures"] = ["PalletFixtureID", "Suspend", "WorkSetHold"],
        ["OrderCompletedQtyResults"] = ["ProductionDate", "OrderID", "NormalQtyDay", "ScrapQtyDay"],
        ["ViewOrders"] = ["OrderName", "PartName", "PartRevision", "NormalQty", "ScrapQty", "RemainQty", "InProcQty"],
        ["ViewDayOrderCompletedQty"] = ["ProductionDate", "OrderName", "PartName", "PartRevision", "NormalQtyDay", "ScrapQtyDay"],
    };

    private static SchemaInfo SchemaWithout(params string[] tableDotColumn) =>
        new(Schema
            .SelectMany(t => t.Value.Select(c => (Table: t.Key, Column: c)))
            .Where(tc => !tableDotColumn.Contains(tc.Table + "." + tc.Column) && !tableDotColumn.Contains(tc.Table)));

    [Fact]
    public void Completed_is_the_last_process_good_count_scrap_the_sum_remaining_the_first_process()
    {
        var cols = string.Join("\n", SqlMakinoReader.OrderQuantityColumns(SchemaWithout(), inProcess: false)!.Columns);

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
        var cols = string.Join("\n", SqlMakinoReader.OrderQuantityColumns(SchemaWithout(), inProcess: true)!.Columns);

        Assert.Contains("SUM(fq.ClampedQuantity) FROM FixtureQuantity fq JOIN FixtureRun fr ON fr.PalletFixtureID = fq.PalletFixtureID WHERE fr.CurOrderID = o.OrderID", cols);
        Assert.Contains("pr.ProcessNumber > (SELECT MIN(p2.ProcessNumber)", cols);
        Assert.Contains("AS ClampedQty", cols);
        Assert.Contains("AS InProcessQty", cols);

        var noFixtures = string.Join("\n", SqlMakinoReader.OrderQuantityColumns(SchemaWithout("FixtureRun"), inProcess: true)!.Columns);
        Assert.Contains("CAST(NULL AS int) AS InProcessQty", noFixtures);
        Assert.Contains("CAST(NULL AS int) AS ClampedQty", noFixtures);
    }

    [Fact]
    public void Without_quantities_the_vendor_order_view_gives_them_and_without_both_there_are_none()
    {
        var view = string.Join("\n", SqlMakinoReader.OrderQuantityColumns(SchemaWithout("Quantities"), inProcess: true)!.Columns);
        Assert.Contains("(SELECT TOP 1 v.NormalQty FROM ViewOrders v WHERE v.OrderName = o.OrderName " +
                        "AND v.PartName = pa.PartName AND ISNULL(v.PartRevision, '') = ISNULL(pa.Revision, '')) AS CompletedQty", view);
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
        var active = SqlMakinoReader.ProductionSql(SchemaWithout(), byOrder: false, byPart: false, includeIdle: true);
        Assert.Contains("TOP (@take)", active);
        Assert.Contains("o.Status NOT IN (0, 5, 6)", active);
        Assert.Contains("d.ProductionDate >= @from", active);
        Assert.Contains("FROM OrderCompletedQtyResults d", active);
        Assert.Contains("ORDER BY d.ProductionDate DESC", active);

        var named = SqlMakinoReader.ProductionSql(SchemaWithout(), byOrder: true, byPart: false, includeIdle: false);
        Assert.DoesNotContain("o.Status NOT IN", named); // a named order shows whatever its status
        Assert.Contains("UPPER(x.OrderName) = UPPER(@order)", named);
        Assert.Contains("UPPER(o.OrderName) LIKE UPPER(@orderLike) ESCAPE '\\'", named);

        var part = SqlMakinoReader.ProductionSql(SchemaWithout(), byOrder: false, byPart: true, includeIdle: false);
        Assert.Contains("UPPER(pa.PartName) LIKE UPPER(@partLike) ESCAPE '\\'", part);
    }

    [Fact]
    public void By_default_only_orders_with_output_scrap_or_work_on_an_active_fixture_and_the_rest_counted()
    {
        var sql = SqlMakinoReader.ProductionSql(SchemaWithout(), byOrder: false, byPart: false, includeIdle: false);
        Assert.Contains("ISNULL((SELECT SUM(d.NormalQtyDay)", sql);
        Assert.Contains("ISNULL((SELECT SUM(d.ScrapQtyDay)", sql);
        Assert.Contains("JOIN PalletFixtures pf ON pf.PalletFixtureID = fq.PalletFixtureID", sql);
        Assert.Contains("ISNULL(pf.Suspend, 0) = 0 AND ISNULL(pf.WorkSetHold, 0) = 0", sql);
        Assert.Contains("AS OtherActiveOrders", sql);
        Assert.Contains("AND NOT (", sql);

        var all = SqlMakinoReader.ProductionSql(SchemaWithout(), false, false, includeIdle: true);
        Assert.Contains("CAST(NULL AS int) AS OtherActiveOrders", all);
        Assert.DoesNotContain("AND NOT (", all);

        // Named orders are listed whatever their activity.
        Assert.Contains("CAST(NULL AS int) AS OtherActiveOrders", SqlMakinoReader.ProductionSql(SchemaWithout(), true, false, false));
    }

    [Fact]
    public void Missing_quantity_columns_are_named_not_silently_null()
    {
        Assert.Empty(SqlMakinoReader.OrderQuantityColumns(SchemaWithout(), inProcess: true)!.Missing);

        var old = SqlMakinoReader.OrderQuantityColumns(SchemaWithout("Quantities.NormalQuantity", "Quantities.ScrapQuantity"), inProcess: false)!;
        Assert.Equal(["completed quantity", "scrap quantity"], old.Missing);

        var noFixtures = SqlMakinoReader.OrderQuantityColumns(SchemaWithout("FixtureRun"), inProcess: true)!;
        Assert.Equal(["parts in process"], noFixtures.Missing);

        var view = SqlMakinoReader.OrderQuantityColumns(SchemaWithout("Quantities"), inProcess: true)!;
        Assert.Equal(["parts clamped on fixtures (in process includes them)"], view.Missing);
    }

    [Fact]
    public void The_order_views_match_orders_by_name_part_and_revision_else_say_they_match_by_name_only()
    {
        // Two parts may carry the same order name; a view matched by name alone would mix them.
        var nameOnly = SqlMakinoReader.OrderQuantityColumns(SchemaWithout("Quantities", "ViewOrders.PartRevision"), inProcess: false)!;
        Assert.Contains("(SELECT TOP 1 v.NormalQty FROM ViewOrders v WHERE v.OrderName = o.OrderName) AS CompletedQty", nameOnly.Columns);
        Assert.Contains(SqlMakinoReader.ViewNameOnly, nameOnly.Missing);

        // Daily rows outlive their order: only days from the order's registration on.
        var daily = SqlMakinoReader.ProductionSql(SchemaWithout("OrderCompletedQtyResults"), byOrder: true, byPart: false, includeIdle: false);
        Assert.Contains("LEFT JOIN Parts pd ON pd.PartID = od.PartID", daily);
        Assert.Contains("AND d.PartName = pd.PartName AND ISNULL(d.PartRevision, '') = ISNULL(pd.Revision, '')", daily);
        Assert.Contains("AND d.ProductionDate >= DATEADD(day, -1, CAST(od.RegistrationDate AS date))", daily);
        Assert.Contains("WHERE d.OrderName = o.OrderName AND d.PartName = pa.PartName", daily); // the window sums

        var old = SqlMakinoReader.ProductionSql(SchemaWithout("OrderCompletedQtyResults", "ViewDayOrderCompletedQty.PartName", "Orders.RegistrationDate"), true, false, false);
        Assert.DoesNotContain("d.PartName", old);
        Assert.DoesNotContain("RegistrationDate", old);
    }

    [Fact]
    public void By_default_the_daily_counts_are_totals_per_day_and_per_order_only_for_a_named_order_or_part()
    {
        var totals = SqlMakinoReader.ProductionSql(SchemaWithout(), byOrder: false, byPart: false, includeIdle: false);
        Assert.Contains("CAST(NULL AS int) AS OrderID, d.ProductionDate, SUM(d.NormalQtyDay) AS NormalQtyDay", totals);
        Assert.Contains("GROUP BY d.ProductionDate", totals);
        Assert.Contains("HAVING SUM(d.NormalQtyDay) <> 0 OR SUM(d.ScrapQtyDay) <> 0", totals);

        var named = SqlMakinoReader.ProductionSql(SchemaWithout(), byOrder: true, byPart: false, includeIdle: false);
        Assert.Contains("SELECT TOP (@take) d.OrderID, d.ProductionDate, d.NormalQtyDay, d.ScrapQtyDay", named);
        Assert.DoesNotContain("GROUP BY d.ProductionDate", named);

        var view = SqlMakinoReader.ProductionSql(SchemaWithout("OrderCompletedQtyResults"), false, false, false);
        Assert.Contains("FROM ViewDayOrderCompletedQty d JOIN Orders od ON od.OrderName = d.OrderName", view);
        Assert.Contains("GROUP BY d.ProductionDate", view);
    }

    [Fact]
    public void Machining_suspend_counts_only_with_the_cells_setting_and_complete_may_include_scrap()
    {
        Assert.False(SqlMakinoReader.MachiningSuspendCounts(new Dictionary<string, string>())); // default: off
        Assert.True(SqlMakinoReader.MachiningSuspendCounts(new Dictionary<string, string> { ["OrderMachiningSuspend"] = "1" }));
        Assert.True(SqlMakinoReader.MachiningSuspendCounts(null)); // can't rule it out

        Assert.True(SqlMakinoReader.CompleteIncludesScrap(new Dictionary<string, string>())); // default: on
        Assert.False(SqlMakinoReader.CompleteIncludesScrap(new Dictionary<string, string> { ["OrderIncludesScrap"] = "0" }));
        Assert.Null(SqlMakinoReader.CompleteIncludesScrap(null));
    }

    [Fact]
    public void A_named_order_or_part_that_matches_nothing_says_so()
    {
        Assert.Equal("No order whose order name is or contains 'ORDER-404'.", SqlMakinoReader.ProductionNote(" ORDER-404 ", null, 0));
        Assert.Equal("No order whose order name is or contains 'A' and whose part name is or contains 'B'.",
            SqlMakinoReader.ProductionNote("A", "B", 0));
        Assert.Null(SqlMakinoReader.ProductionNote("ORDER-001", null, 1));
        Assert.Null(SqlMakinoReader.ProductionNote(null, null, 0)); // active orders, none: nothing to explain
    }

    [Fact]
    public void Daily_quantities_fall_back_to_the_vendor_view_and_else_are_left_out()
    {
        var view = SqlMakinoReader.ProductionSql(SchemaWithout("OrderCompletedQtyResults"), false, false, true);
        Assert.Contains("FROM ViewDayOrderCompletedQty d JOIN Orders od ON od.OrderName = d.OrderName", view);

        var none = SqlMakinoReader.ProductionSql(SchemaWithout("OrderCompletedQtyResults", "ViewDayOrderCompletedQty"), false, false, true);
        Assert.Contains("CAST(NULL AS int) AS WindowCompleted", none);
        Assert.Contains("WHERE 1 = 0", none); // the daily result set stays, empty
    }
}
