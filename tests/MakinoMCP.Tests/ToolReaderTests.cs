using Xunit;
using MakinoMCP.Core.Cell;

namespace MakinoMCP.Tests;

// The tool tools' SQL and words, without a database. Sample values only.
public class ToolReaderTests
{
    private static readonly string[] AlarmBits =
        ["TLAlarm", "TLWarning", "BTSAlarm", "BT2AlarmLong", "ACAlarm", "SLAlarm", "SLLowAlarm", "ToolIDAlarm", "NotInspected", "STSNotOK"];

    private static readonly Dictionary<string, string[]> Schema = new()
    {
        ["Devices"] = ["DeviceID", "DeviceName", "DeviceType", "DeviceNumber"],
        ["SystemParameters"] = ["SystemID", "ParameterName", "ParameterValue"],
        ["IndividualToolData"] = ["ITNID", "ITN", "FTNID", "ITNComment", "Status", "ProhibitTool", "AlarmTool", "AbsentTool",
            "LastUsedDate", "LastUsedProgram"],
        ["IndividualToolCutterData"] = ["ITNID", "CutterNo", "CutterStatus", "Length", "Diameter", "ActualToolLife", .. AlarmBits],
        ["FunctionalToolData"] = ["FTNID", "FTN", "FTNComment", "TotalCutter"],
        ["FunctionalToolCutterData"] = ["FTNID", "CutterNo", "Kind", "ToolLife", "ToolLifeWarning", "ToolLifeType"],
        ["ToolLocation"] = ["ITNID", "CurrentDeviceID", "CurrentPot", "AllocatedDeviceID", "AllocatedPot", "CurrentDeviceName"],
        ["PresetResults"] = ["PresetDateTime", "FTN", "ITN", "TotalCutter", "CutterNo", "DeviceID", "Username", "M6Flag",
            "ToolLifeType", "ToolLifeSecond", "ToolLifeDistance", "ToolLifeCount", "ToolLifeWarningSecond",
            "ToolLifeWarningDistance", "ToolLifeWarningCount",
            "ActualToolLifeSecondOld", "ActualToolLifeSecondNew", "ActualToolLifeDistanceOld", "ActualToolLifeDistanceNew",
            "ActualToolLifeCountOld", "ActualToolLifeCountNew", "StatusOld", "StatusNew", "LengthOld", "LengthNew",
            "DiameterOld", "DiameterNew", "HWearOld", "HWearNew", "LengthWearOld", "LengthWearNew",
            "AlarmToolOld", "AlarmToolNew", "TLAlarmOld", "TLAlarmNew", "BTSAlarmOld", "BTSAlarmNew"],
        ["ToolAlarmHistory"] = ["AlarmTime", "DeviceID", "POT", "ITN", "FTN", "Length", "Diameter", "ToolLife",
            "ActualToolLife", "AlarmTool", "BTSAlarm", "ACAlarm", "SLAlarm", "TLAlarm", "BT2Alarm", "TLWarning",
            "ToolLifeType", "ToolLifeCountType"],
        ["ToolInputOutputResults"] = ["InOutDateTime", "Action", "FTN", "ITN", "TotalCutter", "CutterNo", "FTNComment",
            "ITNComment", "Status", "RatioUsed", "FromDeviceID", "FromPot", "ToDeviceID", "ToPot", "Username", "ToolOutReason"],
    };

    private static SchemaInfo SchemaWithout(params string[] tableDotColumn) =>
        new(Schema
            .SelectMany(t => t.Value.Select(c => (Table: t.Key, Column: c)))
            .Where(tc => !tableDotColumn.Contains(tc.Table + "." + tc.Column) && !tableDotColumn.Contains(tc.Table)));

    // --- life: units, direction, not life-managed ---

    [Fact]
    public void Life_reads_the_same_whether_the_cell_counts_up_or_down()
    {
        // 100 minutes of life, warning at 40 minutes used, 20 minutes used.
        var up = SqlMakinoReader.Life(lifeType: 0, limit: 6000, warning: 2400, actual: 1200, remain: false);
        var down = SqlMakinoReader.Life(lifeType: 0, limit: 6000, warning: 3600, actual: 4800, remain: true);

        foreach (var life in new[] { up, down })
        {
            Assert.NotNull(life);
            Assert.Equal("min", life.Unit);
            Assert.Equal(100.0, life.Limit);
            Assert.Equal(40.0, life.WarnAtUsed);
            Assert.Equal(20.0, life.Used);
            Assert.Equal(80.0, life.Remaining);
            Assert.Equal(20.0, life.UsedPercent);
            Assert.Null(life.Note);
        }
    }

    [Fact]
    public void Remaining_life_is_never_reported_as_used()
    {
        // Counting down with 90 % of the life left: 10 % used, not 90 %.
        var life = SqlMakinoReader.Life(lifeType: 2, limit: 1000, warning: 100, actual: 900, remain: true)!;
        Assert.Equal(100.0, life.Used);
        Assert.Equal(900.0, life.Remaining);
        Assert.Equal(10.0, life.UsedPercent);
        Assert.Equal("count", life.Unit);
    }

    [Fact]
    public void Life_zero_is_not_life_managed_and_has_no_percentage()
    {
        foreach (var remain in new[] { false, true })
        {
            var life = SqlMakinoReader.Life(lifeType: 3, limit: 0, warning: 0, actual: 120, remain)!;
            Assert.Equal("not life-managed", life.Note);
            Assert.Null(life.UsedPercent);
            Assert.Null(life.Used);
            Assert.Null(life.Remaining);
            Assert.Null(life.Limit);
        }
        Assert.Null(SqlMakinoReader.Life(lifeType: 0, limit: null, warning: null, actual: null, remain: false));
    }

    [Fact]
    public void Life_units_follow_the_life_type()
    {
        // Types 0 and 3 are both stored in seconds (3 only counts in tenths on the control): minutes, 1 decimal.
        Assert.Equal(1.5, SqlMakinoReader.Life(3, 90, 0, 0, false)!.Limit);
        Assert.Equal(1.5, SqlMakinoReader.Life(0, 90, 0, 0, false)!.Limit);
        Assert.Equal("min", SqlMakinoReader.Life(3, 90, 0, 0, false)!.Unit);
                Assert.Equal(500.0, SqlMakinoReader.Life(1, 500, 0, 50, false)!.Limit);
        Assert.Equal("code 7", SqlMakinoReader.Life(7, 500, 0, 50, false)!.Unit);
        Assert.Null(SqlMakinoReader.Life(2, 50, 0, 0, false)!.WarnAtUsed); // no warning set
    }

    [Fact]
    public void Worn_past_its_life_shows_over_100_percent_and_nothing_remaining()
    {
        var life = SqlMakinoReader.Life(2, 100, 80, 110, remain: false)!;
        Assert.Equal(110.0, life.UsedPercent);
        Assert.Equal(0.0, life.Remaining);
    }

    [Fact]
    public void Tool_settings_come_from_system_parameters_else_are_assumed()
    {
        // ToolLifeCountType: 0 counts up, 1 down. ToolOffsetUnit (DeviceUnit): 0-4 mm, 10-14 inch.
        Assert.Equal(new SqlMakinoReader.ToolSettings(false, false, "mm", false), SqlMakinoReader.ToolSettingsFrom("0", "3"));
        Assert.Equal(new SqlMakinoReader.ToolSettings(true, false, "inch", false), SqlMakinoReader.ToolSettingsFrom(" 1 ", "12"));
        Assert.Equal(new SqlMakinoReader.ToolSettings(false, true, "mm", true), SqlMakinoReader.ToolSettingsFrom(null, null));
        Assert.Equal(new SqlMakinoReader.ToolSettings(false, true, "mm", true), SqlMakinoReader.ToolSettingsFrom("2", "21"));

        var sql = SqlMakinoReader.ToolSettingsSql(SchemaWithout());
        Assert.NotNull(sql);
        Assert.Contains("ParameterName IN ('ToolLifeCountType', 'ToolOffsetUnit')", sql);
        Assert.Contains("ORDER BY SystemID", sql);
        Assert.Null(SqlMakinoReader.ToolSettingsSql(SchemaWithout("SystemParameters")));
    }

    [Fact]
    public void Lengths_are_hundred_thousandths_of_the_cells_unit_and_zero_is_left_out()
    {
        Assert.Equal(101.234, SqlMakinoReader.Length(10123400, "mm"));
        Assert.Equal(-0.001, SqlMakinoReader.Length(-80, "mm")); // 3 decimals in mm
        Assert.Equal(0.375, SqlMakinoReader.Length(37500, "inch"));
        Assert.Equal(4.9976, SqlMakinoReader.Length(499760, "inch")); // 4 decimals in inch
        Assert.Null(SqlMakinoReader.Length(0, "mm"));
        Assert.Null(SqlMakinoReader.Length(null, "inch"));
    }

    [Fact]
    public void Distance_life_is_in_metres_or_inches()
    {
        Assert.Equal("m", SqlMakinoReader.Life(1, 500, 0, 50, false, "mm")!.Unit);
        Assert.Equal("inch", SqlMakinoReader.Life(1, 500, 0, 50, false, "inch")!.Unit);
    }

    [Fact]
    public void Places_name_the_device_and_pot()
    {
        Assert.Equal("MC1 pot 20", SqlMakinoReader.Place("MC1", 1, 20));
        Assert.Equal("MC1", SqlMakinoReader.Place("MC1", 1, 0));
        Assert.Equal("CRIB1 (tool crib)", SqlMakinoReader.Place("CRIB1", 5, 0));
        Assert.Equal("CRIB1 (tool crib) pot 12", SqlMakinoReader.Place("CRIB1", 5, 12));
        Assert.Equal("PRESET1 (tool presetter)", SqlMakinoReader.Place("PRESET1", 7, null));
    }

    // --- get_tools ---

    [Fact]
    public void Tool_filter_defaults_to_problems_or_all_for_one_tool()
    {
        Assert.Equal("problems", SqlMakinoReader.ToolFilter(null, null));
        Assert.Equal("all", SqlMakinoReader.ToolFilter(null, 1234));
        Assert.Equal("near_end", SqlMakinoReader.ToolFilter(" Near_End ", null));
        Assert.Equal("alarm", SqlMakinoReader.ToolFilter("alarm", 1234));
        var e = Assert.Throws<ArgumentException>(() => SqlMakinoReader.ToolFilter("broken", null));
        Assert.StartsWith("No filter named 'broken'", e.Message);
        foreach (var name in new[] { "problems", "all", "near_end", "alarm", "absent" }) Assert.Contains(name, e.Message);
    }

    [Fact]
    public void Tools_sql_is_capped_filtered_and_reads_cutters_of_the_same_tools()
    {
        var sql = SqlMakinoReader.ToolsSql(SchemaWithout(), "problems");
        Assert.Equal(2, sql.Split("SELECT TOP (@take) i.ITNID").Length - 1); // tools, and the cutters of those tools
        Assert.Contains("(@device IS NULL OR l.CurrentDeviceID = @device)", sql);
        Assert.Contains("(@tool IS NULL OR i.ITN = @tool OR f.FTN = @tool)", sql);
        Assert.Contains("i.Status IN (2, 3, 4)", sql);
        Assert.Contains("i.AbsentTool = 1", sql);
        Assert.Contains("i.ProhibitTool = 1", sql);
        Assert.Contains("c.BTSAlarm = 1", sql);
        Assert.Contains("* 100 >= 90 *", sql);
        Assert.Contains("@remain = 1", sql); // used life depends on the counting direction
        Assert.Contains("fc.ToolLife > 0", sql); // not life-managed tools never count as worn

        var nearEnd = SqlMakinoReader.ToolsSql(SchemaWithout(), "near_end");
        Assert.Contains("* 100 >= 80 *", nearEnd);
        Assert.Contains("c.ActualToolLife <= fc.ToolLifeWarning", nearEnd); // counting down: warning on remaining
        Assert.Contains("c.ActualToolLife >= fc.ToolLifeWarning", nearEnd);
        Assert.DoesNotContain("* 100 >= 90 *", nearEnd);

        var alarm = SqlMakinoReader.ToolsSql(SchemaWithout(), "alarm");
        Assert.Contains("i.Status = 4", alarm);
        Assert.DoesNotContain("c.TLWarning = 1", alarm); // a life warning is not an alarm

        Assert.Contains("i.AbsentTool = 1", SqlMakinoReader.ToolsSql(SchemaWithout(), "absent"));
        Assert.DoesNotContain("i.Status IN", SqlMakinoReader.ToolsSql(SchemaWithout(), "all"));
    }

    [Fact]
    public void Tools_sql_selects_missing_columns_as_null_and_drops_their_conditions()
    {
        var sql = SqlMakinoReader.ToolsSql(SchemaWithout("IndividualToolData.AbsentTool", "IndividualToolCutterData.ActualToolLife",
            "IndividualToolCutterData.STSNotOK", "ToolLocation.AllocatedDeviceID"), "problems");
        Assert.DoesNotContain("AbsentTool = 1", sql);
        Assert.Contains("CAST(NULL AS bit) AS AbsentTool", sql);
        Assert.DoesNotContain("c.ActualToolLife", sql);
        Assert.Contains("CAST(NULL AS int) AS ActualToolLife", sql);
        Assert.DoesNotContain("STSNotOK = 1", sql);
        Assert.Contains("CAST(NULL AS int) AS AllocatedDeviceID", sql);
    }

    [Fact]
    public void Tool_summary_counts_every_tool_per_device_and_status()
    {
        var sql = SqlMakinoReader.ToolSummarySql(SchemaWithout());
        Assert.Contains("GROUP BY l.CurrentDeviceID, i.Status", sql);
        Assert.Contains("(@device IS NULL OR l.CurrentDeviceID = @device)", sql);
        Assert.DoesNotContain("@tool", sql);
    }

    [Fact]
    public void Usable_tools_are_status_ready_or_life_warning_per_device_and_ftn()
    {
        var sql = SqlMakinoReader.UsableToolsSql(3);
        Assert.Contains("i.Status IN (1, 2)", sql);
        Assert.Contains("f.FTN IN (@ftn0, @ftn1, @ftn2)", sql);
        Assert.Contains("GROUP BY l.CurrentDeviceID, f.FTN", sql);
        Assert.Throws<ArgumentOutOfRangeException>(() => SqlMakinoReader.UsableToolsSql(0));
    }

    // --- get_tool_changes ---

    [Fact]
    public void Tool_changes_sql_is_windowed_capped_and_leaves_out_routine_life_updates()
    {
        var sql = SqlMakinoReader.ToolChangesSql(SchemaWithout(), includeLifeUpdates: false);
        Assert.Contains("SELECT TOP (@take)", sql);
        Assert.Contains("p.PresetDateTime >= DATEADD(day, -@days, @now)", sql);
        Assert.Contains("ORDER BY p.PresetDateTime DESC", sql);
        Assert.Contains("(@device IS NULL OR p.DeviceID = @device)", sql);
        Assert.Contains("(@tool IS NULL OR p.ITN = @tool OR p.FTN = @tool)", sql);
        Assert.Contains("p.LengthOld <> p.LengthNew", sql);
        Assert.Contains("p.M6Flag = 0", sql);

        var all = SqlMakinoReader.ToolChangesSql(SchemaWithout(), includeLifeUpdates: true);
        Assert.DoesNotContain("p.LengthOld <> p.LengthNew", all);
        Assert.Contains("p.PresetDateTime >= DATEADD(day, -@days, @now)", all);

        // A pair one half of which is missing is neither compared nor selected.
        var old = SqlMakinoReader.ToolChangesSql(SchemaWithout("PresetResults.HWearNew", "PresetResults.M6Flag"), false);
        Assert.DoesNotContain("HWear", old);
        Assert.DoesNotContain("M6Flag", old);
    }

    [Fact]
    public void Tool_change_words_list_only_what_changed()
    {
        var row = new Dictionary<string, int?>
        {
            ["StatusOld"] = 1, ["StatusNew"] = 4,
            ["LengthOld"] = 10123400, ["LengthNew"] = 10125000,
            ["DiameterOld"] = 1250000, ["DiameterNew"] = 1250000,
            ["TLAlarmOld"] = 0, ["TLAlarmNew"] = 1,
            ["BTSAlarmOld"] = 1, ["BTSAlarmNew"] = 0,
            ["AlarmToolOld"] = 0, ["AlarmToolNew"] = 0,
            ["ActualToolLifeSecondOld"] = 4500, ["ActualToolLifeSecondNew"] = 4500,
        };
        var words = SqlMakinoReader.ChangeWords(SchemaWithout(), c => row.GetValueOrDefault(c), lifeType: 3, remain: false, "mm");

        Assert.Equal(["status ready → alarm", "length 101.234 → 101.250 mm", "tool life alarm on", "broken tool (short) off"], words);
    }

    [Fact]
    public void Tool_change_words_show_life_in_its_unit_and_mark_a_reset()
    {
        var used = new Dictionary<string, int?> { ["ActualToolLifeSecondOld"] = 4500, ["ActualToolLifeSecondNew"] = 0 };
        Assert.Equal(["life used 75.0 → 0.0 min (reset)"],
            SqlMakinoReader.ChangeWords(SchemaWithout(), c => used.GetValueOrDefault(c), lifeType: 0, remain: false, "mm"));

        var tick = new Dictionary<string, int?> { ["ActualToolLifeSecondOld"] = 600, ["ActualToolLifeSecondNew"] = 630 };
        Assert.Equal(["life used 10.0 → 10.5 min"],
            SqlMakinoReader.ChangeWords(SchemaWithout(), c => tick.GetValueOrDefault(c), lifeType: 3, remain: false, "mm"));

        // A routine tick next to another change is noise; a reset next to one is not.
        var withStatus = new Dictionary<string, int?>
        {
            ["StatusOld"] = 1, ["StatusNew"] = 2, ["ActualToolLifeSecondOld"] = 600, ["ActualToolLifeSecondNew"] = 630,
        };
        Assert.Equal(["status ready → life warning"],
            SqlMakinoReader.ChangeWords(SchemaWithout(), c => withStatus.GetValueOrDefault(c), lifeType: 3, remain: false, "mm"));
        var inch = new Dictionary<string, int?>
        {
            ["LengthOld"] = 512300, ["LengthNew"] = 513300, ["ActualToolLifeSecondOld"] = 6000, ["ActualToolLifeSecondNew"] = 0,
        };
        Assert.Equal(["length 5.1230 → 5.1330 inch", "life used 100.0 → 0.0 min (reset)"],
            SqlMakinoReader.ChangeWords(SchemaWithout(), c => inch.GetValueOrDefault(c), lifeType: 3, remain: false, "inch"));

        // Counting down, the life rising again is the reset.
        var down = new Dictionary<string, int?> { ["ActualToolLifeCountOld"] = 3, ["ActualToolLifeCountNew"] = 500 };
        Assert.Equal(["life remaining 3 → 500 count (reset)"],
            SqlMakinoReader.ChangeWords(SchemaWithout(), c => down.GetValueOrDefault(c), lifeType: 2, remain: true, "mm"));
    }

    // --- get_tool_alarms ---

    [Fact]
    public void Tool_alarms_read_the_history_table_else_the_alarm_bits_that_turned_on()
    {
        var (table, tableSource, tableMissing) = SqlMakinoReader.ToolAlarmsSql(SchemaWithout());
        Assert.Contains("FROM ToolAlarmHistory h", table);
        Assert.Contains("h.AlarmTime >= DATEADD(day, -@days, @now)", table);
        Assert.Contains("ORDER BY h.AlarmTime DESC", table);
        Assert.Contains("SELECT TOP (@take)", table);
        Assert.Equal("tool alarm history", tableSource);
        Assert.Empty(tableMissing);

        var (preset, presetSource, presetMissing) = SqlMakinoReader.ToolAlarmsSql(SchemaWithout("ToolAlarmHistory"));
        Assert.Contains("FROM PresetResults p", preset);
        Assert.Contains("(p.TLAlarmOld = 0 AND p.TLAlarmNew = 1)", preset);
        Assert.Contains("p.PresetDateTime >= DATEADD(day, -@days, @now)", preset);
        Assert.Contains("SELECT TOP (@take)", preset);
        Assert.StartsWith("tool data changes", presetSource);
        Assert.Contains("pot", presetMissing);

        var e = Assert.Throws<CellSchemaException>(() => SqlMakinoReader.ToolAlarmsSql(SchemaWithout("ToolAlarmHistory", "PresetResults")));
        Assert.Contains("ToolAlarmHistory", e.Tables);
    }

    [Fact]
    public void Tool_alarm_history_bits_become_words()
    {
        var row = new Dictionary<string, int?> { ["TLAlarm"] = 1, ["BT2Alarm"] = 1, ["ACAlarm"] = 0 };
        Assert.Equal(["tool life alarm", "broken tool (long)"],
            SqlMakinoReader.HistoryAlarmWords(SchemaWithout(), c => row.GetValueOrDefault(c)));
    }

    // --- get_tool_movements ---

    [Fact]
    public void Tool_movements_sql_is_windowed_capped_and_matches_either_end()
    {
        var sql = SqlMakinoReader.ToolMovementsSql(SchemaWithout());
        Assert.Contains("SELECT TOP (@take)", sql);
        Assert.Contains("t.InOutDateTime >= DATEADD(day, -@days, @now)", sql);
        Assert.Contains("(@device IS NULL OR t.FromDeviceID = @device OR t.ToDeviceID = @device)", sql);
        Assert.Contains("(@tool IS NULL OR t.ITN = @tool OR t.FTN = @tool)", sql);
        Assert.Contains("ORDER BY t.InOutDateTime DESC", sql);
        Assert.Contains("CAST(NULL AS varchar(4000)) AS ToolOutReason",
            SqlMakinoReader.ToolMovementsSql(SchemaWithout("ToolInputOutputResults.ToolOutReason")));
    }

    [Fact]
    public void Probe_list_holds_the_tool_tables()
    {
        foreach (var table in new[] { "ToolAlarmHistory", "PresetResults", "ToolInputOutputResults", "MainProgramTools" })
            Assert.Contains(table, SchemaInfo.Tables);
    }
}
