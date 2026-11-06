using System.Text.Json;
using Xunit;
using Xunit.Abstractions;
using MakinoMCP.Core.Cell;
using MakinoMCP.Core.Folders;
using MakinoMCP.Core.Settings;
using MakinoMCP.Tools;

namespace MakinoMCP.Tests;

// History, utilization, alarms and folders: shift filter, result-based utilization, activity details, alarm help
// files and the ADE folder notes.
public class HistoryTests(ITestOutputHelper output)
{
    private static readonly Dictionary<string, string[]> Schema = new()
    {
        ["PMCAlarms"] = ["AlarmTime", "ResetTime", "DeviceID", "AlarmNumber", "IsWarning", "AlarmMessage"],
        ["CNCAlarms"] = ["AlarmTime", "ResetTime", "DeviceID", "AlarmNumber", "AlarmMessage"],
        ["DeviceStatusDaySummary"] = ["DeviceID", "Date", "Down", "Idle", "Busy", "Manual", "Error"],
        ["DeviceResultDaySummary"] = ["DeviceID", "Date", "PotentialProductionTime", "SpindleRotationTime", "Availability",
            "Performance", "Quality", "OEE"],
        ["MachineResults"] = ["StartDateTime", "FinishDateTime", "PrepStartDateTime", "PrepFinishDateTime", "DeviceID",
            "PalletID", "SpindleTime", "MachiningTime", "PlannedMachiningTime", "AbnormalFinish"],
        ["WorkSetResults"] = ["StartDateTime", "FinishDateTime", "DeviceID", "PalletID", "OperationType", "OperationStatus",
            "ClampQuantityOpe1", "ClampQuantityOpe2", "ClampQuantityOpe3", "ClampQuantityOpe4", "UnclampNormalQtyOpe1",
            "UnclampScrapQtyOpe1", "UnclampOutProcQtyOpe1", "ReMachine"],
        ["VehicleResults"] = ["StartDateTime", "FinishDateTime", "DeviceID", "PalletID"],
        ["Pallets"] = ["PalletID", "PalletNumber"],
    };

    private static SchemaInfo SchemaWithout(params string[] tableDotColumn) =>
        new(Schema
            .SelectMany(t => t.Value.Select(c => (Table: t.Key, Column: c)))
            .Where(tc => !tableDotColumn.Contains(tc.Table + "." + tc.Column) && !tableDotColumn.Contains(tc.Table)));

    // ------------------------------------------------------------------ shift filter

    [Fact]
    public void Shift_parses_times_weekdays_and_overnight_pieces()
    {
        Assert.Null(ShiftFilter.Parse(null, " ", ""));

        var day = ShiftFilter.Parse("06:00", "14:30", "mon-fri")!;
        Assert.Equal((6 * 3600, 14 * 3600 + 1800, 0, 0), day.Pieces);
        Assert.Equal(0b0011111, day.Weekdays);
        Assert.Equal("06:00-14:30, mon-fri", day.Text);

        var night = ShiftFilter.Parse("22:00", "06:00", null)!;
        Assert.Equal((0, 6 * 3600, 22 * 3600, 24 * 3600), night.Pieces);
        Assert.Equal(ShiftFilter.AllDays, night.Weekdays);
        Assert.Equal("22:00-06:00", night.Text);

        var weekend = ShiftFilter.Parse(null, null, "Saturday, sun")!;
        Assert.True(weekend.WholeDay);
        Assert.Equal((0, 24 * 3600, 0, 0), weekend.Pieces);
        Assert.Equal("whole day, sat,sun", weekend.Text);

        Assert.Equal(0b1100011, ShiftFilter.Parse(null, null, "sat-tue")!.Weekdays); // a range may wrap the week
        Assert.Equal((22 * 3600, 24 * 3600, 0, 0), ShiftFilter.Parse("22:00", "24:00", null)!.Pieces);
    }

    [Theory]
    [InlineData("25:00", "06:00", null)]
    [InlineData("6", "14:00", null)]
    [InlineData("06:00", null, null)]
    [InlineData(null, "14:00", null)]
    [InlineData("24:00", "06:00", null)]
    [InlineData(null, null, "funday")]
    [InlineData(null, null, "mon-")]
    public void Bad_shift_says_what_is_accepted(string? start, string? end, string? weekdays)
    {
        var e = Assert.Throws<ArgumentException>(() => ShiftFilter.Parse(start, end, weekdays));
        Assert.StartsWith(ShiftFilter.BadInputStart, e.Message);
        Assert.Contains("HH:mm", e.Message);
    }

    [Fact]
    public void Shift_covers_its_hours_on_its_weekdays()
    {
        var night = ShiftFilter.Parse("22:00", "06:00", "mon-fri")!;
        var monday = new DateTime(2026, 11, 2); // a Monday
        Assert.True(night.Covers(monday.AddHours(23)));
        Assert.True(night.Covers(monday.AddHours(5)));
        Assert.False(night.Covers(monday.AddHours(12)));
        Assert.False(night.Covers(monday.AddDays(5).AddHours(23))); // Saturday
    }

    [Fact]
    public void Window_seconds_follow_the_shift_and_stop_at_now()
    {
        var monday = new DateTime(2026, 11, 2);
        var whole = new ShiftFilter(0, 0, ShiftFilter.AllDays);
        Assert.Equal(86400, SqlMakinoReader.WindowSeconds(monday, whole, monday.AddDays(3)));
        Assert.Equal(3 * 3600, SqlMakinoReader.WindowSeconds(monday, whole, monday.AddHours(3))); // today so far

        var night = ShiftFilter.Parse("22:00", "06:00", "mon-fri")!;
        Assert.Equal(8 * 3600, SqlMakinoReader.WindowSeconds(monday, night, monday.AddDays(1)));
        Assert.Equal(0, SqlMakinoReader.WindowSeconds(monday.AddDays(5), night, monday.AddDays(9))); // Saturday
        Assert.Equal(6 * 3600 + 1800, SqlMakinoReader.WindowSeconds(monday, night, monday.AddHours(22.5)));
    }

    // ------------------------------------------------------------------ SQL shape

    [Fact]
    public void Alarm_summary_with_a_shift_filters_both_sources_by_time_of_day_and_weekday()
    {
        var plain = SqlMakinoReader.AlarmSummarySql(SchemaWithout());
        Assert.DoesNotContain("@weekdays", plain);

        var sql = SqlMakinoReader.AlarmSummarySql(SchemaWithout(), ShiftFilter.Parse("06:00", "14:00", "mon-fri"));
        Assert.Equal(2, sql.Split(SqlMakinoReader.InShiftSql("AlarmTime")).Length - 1);
        Assert.Contains("@from2", sql);
        Assert.Contains("CAST('19000101' AS datetime)", sql);
    }

    [Fact]
    public void Utilization_reads_oee_parts_and_spindle_through_opt()
    {
        var sql = SqlMakinoReader.UtilizationSql(SchemaWithout());
        foreach (var column in new[] { "Availability", "Performance", "Quality", "SpindleRotationTime", "PotentialProductionTime" })
            Assert.Contains($"o.{column} AS {column}", sql);
        Assert.Contains("o.OEE AS Oee", sql);
        Assert.DoesNotContain("@weekdays", sql);

        var older = SqlMakinoReader.UtilizationSql(SchemaWithout("DeviceResultDaySummary.SpindleRotationTime", "DeviceResultDaySummary.Quality"));
        Assert.Contains("CAST(NULL AS int) AS SpindleRotationTime", older);
        Assert.Contains("CAST(NULL AS int) AS Quality", older);
        Assert.DoesNotContain("x.SpindleRotationTime", older);

        var none = SqlMakinoReader.UtilizationSql(SchemaWithout("DeviceResultDaySummary"));
        Assert.DoesNotContain("DeviceResultDaySummary", none);
        Assert.DoesNotContain("OUTER APPLY", none);

        Assert.Contains("@weekdays", SqlMakinoReader.UtilizationSql(SchemaWithout(), ShiftFilter.Parse(null, null, "mon")));
    }

    [Fact]
    public void Result_utilization_counts_station_visits_once_and_spreads_net_machining_time()
    {
        var sql = SqlMakinoReader.ResultUtilizationSql(SchemaWithout(), netMachining: true);
        Assert.Contains("GROUP BY x.DeviceID, x.StartDateTime, x.PalletID", sql);
        Assert.Contains("CAST(x.MachiningTime AS float) / DATEDIFF(second", sql);
        Assert.Contains("x.PrepStartDateTime", sql);
        Assert.Contains("COUNT(DISTINCT r.Id)", sql);
        Assert.DoesNotContain("EXEC", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MIN(StartDateTime) FROM VehicleResults", sql);

        var gross = SqlMakinoReader.ResultUtilizationSql(SchemaWithout("MachineResults.PrepStartDateTime"), netMachining: false);
        Assert.DoesNotContain("MachiningTime", gross);
        Assert.DoesNotContain("PrepStartDateTime", gross);

        var noVehicle = SqlMakinoReader.ResultUtilizationSql(SchemaWithout("VehicleResults"), netMachining: false);
        Assert.Contains("CAST(NULL AS datetime) AS VehicleFrom", noVehicle);
        Assert.DoesNotContain("FROM VehicleResults", noVehicle);
    }

    [Fact]
    public void Activity_reads_times_and_part_counts_through_opt()
    {
        var sql = SqlMakinoReader.ActivitySql(SchemaWithout());
        Assert.Contains("x.PrepStartDateTime AS PrepStartDateTime", sql);
        Assert.Contains("x.PlannedMachiningTime AS PlannedMachiningTime", sql);
        Assert.Contains("(ISNULL(x.ClampQuantityOpe1, 0) + ISNULL(x.ClampQuantityOpe2, 0) + ISNULL(x.ClampQuantityOpe3, 0) + ISNULL(x.ClampQuantityOpe4, 0)) AS Clamped", sql);
        Assert.Contains("(ISNULL(x.UnclampNormalQtyOpe1, 0)) AS UnclampedGood", sql);
        Assert.Contains("x.OperationType AS OperationType", sql);

        var old = SqlMakinoReader.ActivitySql(SchemaWithout("WorkSetResults.ClampQuantityOpe1", "WorkSetResults.ClampQuantityOpe2",
            "WorkSetResults.ClampQuantityOpe3", "WorkSetResults.ClampQuantityOpe4", "MachineResults.MachiningTime", "WorkSetResults.ReMachine"));
        Assert.Contains("CAST(NULL AS int) AS Clamped", old);
        Assert.Contains("CAST(NULL AS int) AS MachiningTime", old);
        Assert.Contains("CAST(NULL AS bit) AS ReMachine", old);
    }

    // ------------------------------------------------------------------ words

    [Theory]
    [InlineData(2, 4, 4, 0, 0, false, "unclamped good 4, clamped 4")]
    [InlineData(3, 0, 2, 1, 0, false, "unclamped good 2, unclamped scrap 1")]
    [InlineData(4, 0, 0, 0, 3, false, "to external process 3")]
    [InlineData(2, 0, 4, 0, 0, true, "unclamped good 4, re-machine")]
    [InlineData(6, 0, 5, 0, 0, false, "parts 5")]
    [InlineData(1, 0, 0, 0, 0, false, "")]
    [InlineData(null, null, null, null, null, false, "")]
    public void Work_set_counts_leave_out_zeros(int? type, int? clamped, int? good, int? scrap, int? external, bool reMachine, string expected) =>
        Assert.Equal(expected, SqlMakinoReader.WorkSetCounts(type, clamped, good, scrap, external, reMachine));

    [Fact]
    public void Machining_minutes_skip_unset_and_zero_values()
    {
        var start = new DateTime(2026, 11, 2, 8, 0, 0);
        Assert.Equal(1.5, SqlMakinoReader.PrepMinutes(start, start.AddSeconds(90)));
        Assert.Null(SqlMakinoReader.PrepMinutes(new DateTime(1900, 1, 1), start));
        Assert.Null(SqlMakinoReader.PrepMinutes(start, start));
        Assert.Null(SqlMakinoReader.PrepMinutes(null, start));
        Assert.Equal(4.2, SqlMakinoReader.ResultMinutes(252));
        Assert.Null(SqlMakinoReader.ResultMinutes(0));
        Assert.Null(SqlMakinoReader.ResultMinutes(null));
    }

    [Fact]
    public void Alarm_help_lists_each_devices_files_in_order()
    {
        var help = SqlMakinoReader.AlarmHelp(
        [
            ("MC1", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["PMC2AlarmMessageFilename"] = @"C:\Makino\Message\English\second.ini",
                ["PMCAlarmMessageFilename"] = @"C:\Makino\Message\English\first.ini",
            }),
            ("MC2", new Dictionary<string, string> { ["PMCAlarmMessageFilename"] = "", ["PMC2AlarmMessageFilename"] = "" }),
            ("WSS1", new Dictionary<string, string> { ["AlarmDBFilename"] = @"C:\Makino\Message\English\station.mdb" }),
            ("VHC1", null),
        ])!;

        Assert.Equal(["MC1", "WSS1"], help.Select(h => h.Device));
        Assert.Equal([@"C:\Makino\Message\English\first.ini", @"C:\Makino\Message\English\second.ini"], help[0].Files);
        Assert.Null(SqlMakinoReader.AlarmHelp([("MC2", new Dictionary<string, string> { ["PMCAlarmMessageFilename"] = " " })]));
    }

    // ------------------------------------------------------------------ tools

    [Fact]
    public async Task Bad_shift_comes_back_as_a_plain_error()
    {
        var s = new McpSettings(new MakinoLogin("MAKINO-CELL-PC", "Makino", false, "your-login", "pw"), [], new Limits());
        var context = new ToolContext(new SettingsLoad(s, null), new FakeMakinoReader(), null);

        foreach (var json in new[]
        {
            await CellTools.GetUtilization(context, shiftStart: "25:00", shiftEnd: "06:00", ct: CancellationToken.None),
            await CellTools.GetAlarmSummary(context, weekdays: "funday", ct: CancellationToken.None),
        })
        {
            var error = JsonDocument.Parse(json).RootElement.GetProperty("error").GetString()!;
            Assert.StartsWith(ShiftFilter.BadInputStart, error);
            Assert.DoesNotContain("Could not read", error);
        }

        var ok = JsonDocument.Parse(await CellTools.GetUtilization(context, shiftStart: "06:00", shiftEnd: "14:00",
            weekdays: "mon-fri", ct: CancellationToken.None)).RootElement;
        Assert.False(ok.TryGetProperty("error", out _));
    }

    // ------------------------------------------------------------------ ADE folders

    [Theory]
    [InlineData(@"\\MAKINO-CELL-PC\ADE\ADE_Error", AdeHints.Error)]
    [InlineData(@"D:\share\ade_error\[2026_11_02_08_00_00]ORDER-001.xml", AdeHints.Error)]
    [InlineData(@"\\MAKINO-CELL-PC\ADE\ADE_Success", AdeHints.Success)]
    [InlineData(@"\\MAKINO-CELL-PC\ADE\ADE_Success\ORDER-001.success", AdeHints.Success)]
    [InlineData(@"\\MAKINO-CELL-PC\ADE\ADE_Success\FTN\1001.xml", AdeHints.Export)]
    [InlineData(@"\\MAKINO-CELL-PC\ADE\ADE_Success\itn", AdeHints.Export)]
    [InlineData(@"\\MAKINO-CELL-PC\ADE", null)]
    [InlineData(@"C:\Makino\Log\Exception.log", null)]
    public void Ade_folders_get_their_note(string path, string? expected) =>
        Assert.Equal(expected, AdeHints.For(path));

    [Fact]
    public void Listing_and_reading_inside_ade_error_carry_the_note()
    {
        var root = Path.Combine(Path.GetTempPath(), "makinomcp-ade-" + Guid.NewGuid().ToString("N"));
        try
        {
            var error = Path.Combine(root, "ADE_Error");
            Directory.CreateDirectory(error);
            File.WriteAllText(Path.Combine(error, "[2026_11_02 08_00_00]ORDER-001.xml"), "<Orders><Order Name=\"ORDER-001\"/></Orders>");
            File.WriteAllText(Path.Combine(root, "waiting.xml"), "<Orders/>");
            var folders = new SharedFolders([new SharedFolder("ade", root, "ADE drop folder")], new Limits(), []);

            Assert.Null(folders.ListFiles("ade", null, null, null).Note);
            Assert.Equal(AdeHints.Error, folders.ListFiles("ade", "ADE_Error", null, null).Note);
            Assert.Contains(AdeHints.Error, folders.ReadFile("ade", @"ADE_Error\[2026_11_02 08_00_00]ORDER-001.xml", null, null, null).Note);
            Assert.Null(folders.ReadFile("ade", "waiting.xml", null, null, null).Note);
        }
        finally
        {
            try { Directory.Delete(root, true); }
            catch (Exception) { /* temp folder */ }
        }
    }

    // ------------------------------------------------------------------ live

    /// <summary>Reads the real cell (skipped unless MAKINOMCP_LIVE_SETTINGS is set) and prints what the history
    /// tools now report; asserts only shape, never a device, count or value of the cell.</summary>
    [SkippableFact]
    public async Task Live_history_utilization_and_alarm_details()
    {
        var path = Environment.GetEnvironmentVariable("MAKINOMCP_LIVE_SETTINGS");
        Skip.If(string.IsNullOrWhiteSpace(path), "MAKINOMCP_LIVE_SETTINGS is not set");
        var settings = SettingsStore.Load(path!).Settings!;
        var reader = new SqlMakinoReader(settings.Makino, new Limits(500, settings.Limits.MaxFileKB));
        var ct = CancellationToken.None;

        var util = await reader.GetUtilizationAsync(7, null, null, ct);
        var size = JsonSerializer.Serialize(util, new JsonSerializerOptions(JsonSerializerDefaults.Web)).Length;
        output.WriteLine($"utilization: {util.Days.Count} rows, {size / 1024.0:0.0} KB, machineResultTime {util.MachineResultTime}, " +
                         $"notAvailable [{string.Join("; ", util.NotAvailable ?? [])}]");
        foreach (var d in util.Days.Take(12))
        {
            output.WriteLine($"  {d.Date:MM-dd} busy {d.BusyPercent}% oee {d.Oee} a/p/q {d.Availability}/{d.Performance}/{d.Quality} " +
                             $"spindle {d.SpindleHours}h {d.SpindlePercent}% result {d.ResultPercent}% n {d.Results} avg {d.AvgMinutes}");
        }
        Assert.All(util.Days, d => Assert.True(d.ResultPercent is null or >= 0 and <= 100));
        Assert.All(util.Days, d => Assert.True(d.SpindlePercent is null or >= 0 and <= 100));
        Assert.All(util.Days, d => Assert.True(d.Results is null or >= 0));
        Assert.All(util.Days, d => Assert.True(d.AvgMinutes is null || d.Results > 0));
        Assert.Contains(util.Days, d => d.ResultPercent is not null);

        var year = await reader.GetUtilizationAsync(365, null, null, ct);
        output.WriteLine($"utilization 365 days: {year.Days.Count} rows (capped {year.Capped}), oldest {year.Days.LastOrDefault()?.Date:yyyy-MM-dd}, " +
                         $"notAvailable [{string.Join("; ", year.NotAvailable ?? [])}]");

        var night = ShiftFilter.Parse("22:00", "06:00", "mon-fri")!;
        var shifted = await reader.GetUtilizationAsync(14, null, night, ct);
        output.WriteLine($"night shift: {shifted.Shift}, {shifted.Days.Count} rows");
        Assert.Equal(night.Text, shifted.Shift);
        Assert.All(shifted.Days, d => Assert.True(d.Date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)));
        Assert.All(shifted.Days, d => Assert.True(d.ResultPercent is null or >= 0 and <= 100));

        var summary = await reader.GetAlarmSummaryAsync(14, null, night, ct);
        output.WriteLine($"alarm summary in the night shift: {summary.Rows.Count} rows");
        Assert.Equal(night.Text, summary.Shift);
        Assert.All(summary.Rows, a => Assert.True(night.Covers(a.FirstSeen) && night.Covers(a.LastSeen)));

        var alarms = await reader.GetAlarmsAsync(24 * 30, null, false, true, ct);
        output.WriteLine($"alarms 30 days: {alarms.Alarms.Count}; help files: " +
                         string.Join("; ", (alarms.HelpFiles ?? []).Select(h => $"{h.Files.Count} file(s), {string.Join(", ", h.Files.Select(Path.GetExtension))}")));
        Assert.All(alarms.HelpFiles ?? [], h => Assert.NotEmpty(h.Files));
        Assert.All(alarms.HelpFiles ?? [], h => Assert.Contains(alarms.Alarms, a => a.Device == h.Device && a.Source == "PMC"));

        var activity = await reader.GetRecentActivityAsync(24, null, ct);
        output.WriteLine($"activity 24 h: {activity.Items.Count} items, note {activity.Note ?? "-"}");
        foreach (var kind in new[] { "machining", "work-set", "transport" })
        {
            foreach (var i in activity.Items.Where(i => i.Kind == kind).Take(3))
            {
                output.WriteLine($"  {kind}: {i.Detail.Split(',')[0]} | {i.Result} | prep {i.PrepMinutes} spindle {i.SpindleMinutes} " +
                                 $"net {i.NetMinutes} planned {i.PlannedMinutes}");
            }
        }
        // Present only when the stored seconds are above 0 (a few seconds round to 0.0 minutes).
        Assert.All(activity.Items, i => Assert.True(i.PrepMinutes is null or > 0 && i.SpindleMinutes is null or >= 0 && i.NetMinutes is null or >= 0));
        Assert.All(activity.Items.Where(i => i.Kind != "machining"),
            i => Assert.True(i.PrepMinutes is null && i.SpindleMinutes is null && i.NetMinutes is null && i.PlannedMinutes is null));
    }
}
