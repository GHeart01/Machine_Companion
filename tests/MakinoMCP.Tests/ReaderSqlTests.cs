using Xunit;
using MakinoMCP.Core.Cell;
using MakinoMCP.Core.Settings;

namespace MakinoMCP.Tests;

// The SQL the reader builds for a cell's schema, without a database.
public class ReaderSqlTests
{
    private static readonly Dictionary<string, string[]> FullSchema = new()
    {
        ["Devices"] = ["DeviceID", "DeviceName", "DeviceType", "DeviceNumber"],
        ["PMCAlarms"] = ["AlarmTime", "ResetTime", "DeviceID", "AlarmNumber", "IsWarning", "AlarmMessage"],
        ["CNCAlarms"] = ["AlarmTime", "ResetTime", "DeviceID", "AlarmNumber", "AlarmMessage"],
        ["DeviceStatusDaySummary"] = ["DeviceID", "Date", "Down", "Idle", "Busy", "Manual", "Error"],
        ["DeviceResultDaySummary"] = ["DeviceID", "Date", "OEE"],
        ["MachineResults"] = ["StartDateTime", "FinishDateTime", "DeviceID", "PalletID", "OrderName", "PartName", "NCProgramFileName", "FinishStatus", "AlarmNumber", "AbnormalFinish"],
        ["WorkSetResults"] = ["StartDateTime", "FinishDateTime", "DeviceID", "PalletID", "FixtureNumber", "UnclampOrderName", "ClampOrderName", "OperationStatus", "ErrorCode"],
        ["VehicleResults"] = ["StartDateTime", "FinishDateTime", "DeviceID", "PalletID", "LoadDevice", "UnloadDevice", "CommandType", "FinishStatus"],
        ["Orders"] = ["OrderID", "PartID", "OrderName", "Quantity", "Status", "DueDate", "CompleteDate", "MachiningSuspended"],
        ["Parts"] = ["PartID", "PartName", "Revision"],
        ["Pallets"] = ["PalletID", "PalletNumber", "PalletName", "PalletStatus"],
        ["PalletLocation"] = ["PalletID", "CurDeviceType", "CurDeviceNumber", "CurDevicePosition"],
        ["PalletFixtures"] = ["PalletID", "FixtureNumber", "FixtureID", "Suspend", "Damaged"],
        ["Fixtures"] = ["FixtureID", "FixtureName"],
    };

    private static SchemaInfo SchemaWithout(params string[] tableDotColumn) =>
        new(FullSchema
            .SelectMany(t => t.Value.Select(c => (Table: t.Key, Column: c)))
            .Where(tc => !tableDotColumn.Contains(tc.Table + "." + tc.Column) && !tableDotColumn.Contains(tc.Table)));

    [Fact]
    public void Missing_column_is_selected_as_null()
    {
        var full = SqlMakinoReader.ActivitySql(SchemaWithout());
        Assert.Contains("x.NCProgramFileName AS NCProgramFileName", full);

        var sql = SqlMakinoReader.ActivitySql(SchemaWithout("MachineResults.NCProgramFileName", "VehicleResults.CommandType"));
        Assert.DoesNotContain("x.NCProgramFileName", sql);
        Assert.Contains("CAST(NULL AS varchar(4000)) AS NCProgramFileName", sql);
        Assert.DoesNotContain("x.CommandType", sql);
        Assert.Contains("CAST(NULL AS int) AS CommandType", sql);

        var orders = SqlMakinoReader.OrdersSql(SchemaWithout("Orders.MachiningSuspended", "Parts.Revision"), activeOnly: false);
        Assert.Contains("CAST(NULL AS int) AS MachiningSuspended", orders);
        Assert.DoesNotContain("pa.Revision", orders);

        var pallets = SqlMakinoReader.PalletsSql(SchemaWithout("Pallets.PalletName", "Fixtures.FixtureName", "PalletFixtures.Damaged"));
        Assert.DoesNotContain("p.PalletName", pallets);
        Assert.DoesNotContain("f.FixtureName", pallets);
        Assert.DoesNotContain("pf.Damaged", pallets);

        var alarms = SqlMakinoReader.AlarmUnion(SchemaWithout("PMCAlarms.IsWarning", "CNCAlarms.AlarmMessage"), "1 = 1", includeWarnings: false);
        Assert.DoesNotContain("IsWarning = 0", alarms);
        Assert.Contains("CAST(NULL AS nvarchar(4000)) AS AlarmMessage", alarms);

        var util = SqlMakinoReader.UtilizationSql(SchemaWithout("DeviceStatusDaySummary.Manual"));
        Assert.Contains("CAST(NULL AS int) AS Manual", util);
    }

    [Fact]
    public void Missing_core_table_throws_missing_table_error()
    {
        var schema = SchemaWithout("CNCAlarms");
        var e = Assert.Throws<CellSchemaException>(() => schema.Require("PMCAlarms", "CNCAlarms"));
        Assert.Contains("CNCAlarms", e.Message);

        var login = new MakinoLogin("CELL-PC", "Makino", false, "reader", "pw-xyz");
        Assert.Contains("a table MakinoMCP reads is missing", ErrorText.ForClaude(e, login));

        SchemaWithout().Require("PMCAlarms", "CNCAlarms");
    }

    [Fact]
    public void Optional_result_summary_table_gives_null_oee()
    {
        Assert.Contains("o.OEE", SqlMakinoReader.UtilizationSql(SchemaWithout()));
        var sql = SqlMakinoReader.UtilizationSql(SchemaWithout("DeviceResultDaySummary"));
        Assert.DoesNotContain("DeviceResultDaySummary", sql);
        Assert.Contains("CAST(NULL AS int) AS Oee", sql);
    }

    [Fact]
    public void Last_activity_looks_back_30_days()
    {
        var sql = SqlMakinoReader.LastActivitySql();
        Assert.Equal(3, sql.Split("FinishDateTime >= DATEADD(day, -30, @now)").Length - 1);
    }

    [Fact]
    public void Raw_codes_and_numbers_read_safely()
    {
        Assert.Equal("", SqlMakinoReader.CodeText(null));
        Assert.Equal("code 0", SqlMakinoReader.CodeText(0));
        Assert.Equal(123, SqlMakinoReader.ToInt("123"));
        Assert.Null(SqlMakinoReader.ToInt("E12"));
        Assert.Null(SqlMakinoReader.ToInt(""));
        Assert.Equal(5, SqlMakinoReader.ToInt((short)5));
        Assert.Equal(1, SqlMakinoReader.ToInt(true));
    }
}
