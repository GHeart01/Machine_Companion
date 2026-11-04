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
        ["Pallets"] = ["PalletID", "PalletNumber", "PalletName", "PalletStatus", "WorkStatus", "PalletType"],
        ["PalletLocation"] = ["PalletID", "CurDeviceType", "CurDeviceNumber", "CurDevicePosition"],
        ["PalletFixtures"] = ["PalletID", "FixtureNumber", "FixtureID", "Suspend", "Damaged"],
        ["Fixtures"] = ["FixtureID", "FixtureName"],
        ["MachineStatus"] = ["DeviceID", "Connected", "Emergency", "CNCMode", "MachineAction", "MachineSubAction",
            "TablePalletID", "TableStatus", "FrontPalletID", "FrontStatus", "BackPalletID", "BackStatus", "PowerStatus",
            "Status", "Recovery", "ToolStatus", "CNCRun", "OperatorCall", "RunJob", "MainONumber", "ExecONumber",
            "TableReservedPalletID", "FrontReservedPalletID", "BackReservedPalletID"],
        ["WorkSetStatus"] = ["DeviceID", "Connected", "Emergency", "WSSMode", "DeviceSkip", "TablePalletID", "TableStatus",
            "FrontPalletID", "FrontStatus", "PowerStatus", "Status", "Recovery", "TableReservedPalletID", "FrontReservedPalletID"],
        ["VehicleStatus"] = ["DeviceID", "Connected", "Emergency", "VHCMode", "Status", "Recovery", "CommandType",
            "FromDeviceName", "ToDeviceName", "Buffer1PalletID", "Buffer1Status", "Buffer2PalletID", "Buffer2Status"],
        ["ViewVehicleStatus"] = ["DeviceNumber", "DeviceName", "Connected", "Status", "Emergency", "Recovery",
            "PalletNumber", "PalletNumber2", "Command", "FromDevice", "ToDevice"],
        ["SystemParameters"] = ["SystemID", "ParameterName", "ParameterValue"],
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
    public void Alarm_summary_counts_calendar_days_like_utilization()
    {
        const string window = "DATEADD(day, 1 - @days, CAST(CAST(@now AS date) AS datetime))";
        var sql = SqlMakinoReader.AlarmSummarySql(SchemaWithout());
        // Both halves of the PMC/CNC union use the same window, starting at midnight (days - 1) days ago.
        Assert.Equal(2, sql.Split("AlarmTime >= " + window).Length - 1);
        Assert.DoesNotContain("DATEADD(hour", sql);
        Assert.Contains("d.Date >= " + window, SqlMakinoReader.UtilizationSql(SchemaWithout()));
    }

    [Fact]
    public void Last_activity_looks_back_30_days()
    {
        var sql = SqlMakinoReader.LastActivitySql();
        Assert.Equal(3, sql.Split("FinishDateTime >= DATEADD(day, -30, @now)").Length - 1);
    }

    [Fact]
    public void Status_sql_reads_dashboard_columns_and_reserved_pallets()
    {
        var machines = SqlMakinoReader.StatusSql(SchemaWithout(), "MachineStatus", ["Table", "Front", "Back"]);
        foreach (var column in new[] { "Status", "Recovery", "ToolStatus", "CNCRun", "OperatorCall", "RunJob", "MainONumber", "ExecONumber" })
            Assert.Contains($"s.{column} AS {column}", machines);
        Assert.Contains("p.PalletID = s.TableReservedPalletID) AS TableReservedPalletNumber", machines);
        Assert.Contains("p.PalletID = s.BackReservedPalletID) AS BackReservedPalletNumber", machines);

        // A station has no tool status or program by design; an older machine lacks the dashboard columns.
        var stations = SqlMakinoReader.StatusSql(SchemaWithout(), "WorkSetStatus", ["Table", "Front"]);
        Assert.Contains("s.Recovery AS Recovery", stations);
        Assert.Contains("CAST(NULL AS int) AS ToolStatus", stations);
        Assert.Contains("p.PalletID = s.FrontReservedPalletID) AS FrontReservedPalletNumber", stations);

        var old = SchemaWithout("MachineStatus.Recovery", "MachineStatus.Status", "MachineStatus.TableReservedPalletID");
        var oldSql = SqlMakinoReader.StatusSql(old, "MachineStatus", ["Table", "Front", "Back"]);
        Assert.Contains("CAST(NULL AS bit) AS Recovery", oldSql);
        Assert.Contains("CAST(NULL AS int) AS Status", oldSql);
        Assert.Contains("CAST(NULL AS int) AS TableReservedPalletNumber", oldSql);
        Assert.DoesNotContain("s.TableReservedPalletID", oldSql);

        var missing = SqlMakinoReader.Missing(old, "MachineStatus");
        Assert.Contains("recovery request", missing);
        Assert.Contains("status", missing);
        Assert.Contains("next table pallet", missing);
        Assert.Empty(SqlMakinoReader.Missing(SchemaWithout(), "MachineStatus"));
        Assert.Empty(SqlMakinoReader.Missing(SchemaWithout(), "WorkSetStatus"));
    }

    [Fact]
    public void Vehicle_reads_its_table_else_the_vendor_view_else_nothing()
    {
        var table = SqlMakinoReader.VehicleSql(SchemaWithout());
        Assert.NotNull(table);
        Assert.Contains("FROM VehicleStatus v", table);
        Assert.Contains("v.VHCMode AS VHCMode", table);
        Assert.Contains("p.PalletID = v.Buffer1PalletID) AS Buffer1PalletNumber", table);

        var view = SqlMakinoReader.VehicleSql(SchemaWithout("VehicleStatus"));
        Assert.NotNull(view);
        Assert.Contains("FROM ViewVehicleStatus v", view);
        Assert.Contains("v.Command AS CommandType", view);
        Assert.Contains("v.FromDevice AS FromDeviceName", view);
        Assert.Contains("v.PalletNumber2 AS Buffer2PalletNumber", view);
        Assert.Contains("CAST(NULL AS int) AS VHCMode", view);
        Assert.Contains("CAST(NULL AS int) AS DeviceID", view);

        Assert.Null(SqlMakinoReader.VehicleSql(SchemaWithout("VehicleStatus", "ViewVehicleStatus")));

        Assert.Empty(SqlMakinoReader.VehicleMissing(SchemaWithout()));
        Assert.Equal(["vehicle mode", "buffer status"], SqlMakinoReader.VehicleMissing(SchemaWithout("VehicleStatus")));
        Assert.Equal(["vehicle status"], SqlMakinoReader.VehicleMissing(SchemaWithout("VehicleStatus", "ViewVehicleStatus")));
    }

    [Fact]
    public void History_days_come_from_system_parameters_when_present()
    {
        var sql = SqlMakinoReader.HistoryDaysSql(SchemaWithout());
        Assert.NotNull(sql);
        Assert.Contains("FROM SystemParameters", sql);
        Assert.Contains("ParameterName = 'PreserveResults'", sql);
        Assert.Null(SqlMakinoReader.HistoryDaysSql(SchemaWithout("SystemParameters")));
        Assert.Null(SqlMakinoReader.HistoryDaysSql(SchemaWithout("SystemParameters.ParameterValue")));

        Assert.Equal(90, SqlMakinoReader.HistoryDays("90"));
        Assert.Null(SqlMakinoReader.HistoryDays("0"));
        Assert.Null(SqlMakinoReader.HistoryDays("abc"));
        Assert.Null(SqlMakinoReader.HistoryDays(null));
    }

    [Fact]
    public void Pallets_read_work_status_and_type()
    {
        var sql = SqlMakinoReader.PalletsSql(SchemaWithout());
        Assert.Contains("p.WorkStatus AS WorkStatus", sql);
        Assert.Contains("p.PalletType AS PalletType", sql);
        var old = SqlMakinoReader.PalletsSql(SchemaWithout("Pallets.WorkStatus", "Pallets.PalletType"));
        Assert.Contains("CAST(NULL AS int) AS WorkStatus", old);
        Assert.Contains("CAST(NULL AS int) AS PalletType", old);
    }

    [Fact]
    public void Pallet_status_words_apply_to_dashboard_pallet_types_only()
    {
        // The cell rewrites status and work status into dashboard codes for pallet types 0, 1, 2, 8 and 16.
        Assert.Equal("waiting for machining", SqlMakinoReader.PalletStatusWords(0, 4));
        Assert.Equal("machining suspended", SqlMakinoReader.PalletStatusWords(16, 8));
        Assert.Equal("waiting for machining", SqlMakinoReader.PalletStatusWords(null, 4)); // older cell, no type column
        Assert.Equal("code 4", SqlMakinoReader.PalletStatusWords(4, 4));
        Assert.Equal("abnormal", SqlMakinoReader.PalletWorkStatusWords(1, 3));
        Assert.Equal("none", SqlMakinoReader.PalletWorkStatusWords(0, 0));
        Assert.Equal("code 3", SqlMakinoReader.PalletWorkStatusWords(5, 3));
        Assert.Equal("", SqlMakinoReader.PalletWorkStatusWords(0, null));
    }

    [Fact]
    public void Program_numbers_read_as_O_numbers()
    {
        Assert.Null(SqlMakinoReader.Program(null, null));
        Assert.Null(SqlMakinoReader.Program(0, 0));
        Assert.Equal(new NcProgram("O1234", "O0100"), SqlMakinoReader.Program(1234, 100));
        Assert.Equal(new NcProgram("O1234", null), SqlMakinoReader.Program(1234, 0));
    }

    [Fact]
    public async Task Synonym_tables_count_as_present_with_their_probed_columns()
    {
        var probed = new List<string>();
        Task<IReadOnlyList<string>> ColumnsOf(string table, CancellationToken ct)
        {
            probed.Add(table);
            if (table == "ToolLocation") throw new InvalidOperationException("linked server unreachable");
            return Task.FromResult<IReadOnlyList<string>>(["ITNID", "Status"]);
        }

        var schema = await SchemaInfo.BuildAsync(
            [("Devices", "DeviceID")],
            ["IndividualToolData", "ToolLocation", "NotOneOfOurs", "individualtooldata"],
            ColumnsOf,
            CancellationToken.None);

        Assert.True(schema.HasTable("Devices"));
        Assert.True(schema.HasTable("IndividualToolData"));
        Assert.True(schema.Has("IndividualToolData", "Status"));
        Assert.False(schema.HasTable("ToolLocation")); // unreadable through the synonym: missing, not empty
        Assert.Throws<CellSchemaException>(() => schema.Require("ToolLocation"));
        Assert.False(schema.HasTable("NotOneOfOurs"));
        Assert.DoesNotContain("NotOneOfOurs", probed); // only names from the constant list are ever queried
        Assert.Equal(2, probed.Count); // each listed synonym probed once
    }

    [Fact]
    public void Probe_list_holds_the_new_tables()
    {
        foreach (var table in new[] { "VehicleStatus", "ViewVehicleStatus", "SystemParameters", "IndividualToolData",
                     "IndividualToolCutterData", "FunctionalToolData", "FunctionalToolCutterData", "ToolLocation" })
            Assert.Contains(table, SchemaInfo.Tables);
        Assert.All(SchemaInfo.Tables, t => Assert.Matches("^[A-Za-z]+$", t));
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
