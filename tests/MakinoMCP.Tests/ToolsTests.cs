using Xunit;
using Xunit.Abstractions;
using MakinoMCP.Core.Cell;
using MakinoMCP.Core.Settings;

namespace MakinoMCP.Tests;

// Tool data additions from the 2026-11 manual review (TL-3..TL-6, TC-1..TC-4): SQL and words without a database, plus
// one live read. Sample values only.
public class ToolsTests(ITestOutputHelper output)
{
    private static readonly Dictionary<string, string[]> Schema = new()
    {
        ["Devices"] = ["DeviceID", "DeviceName", "DeviceType", "DeviceNumber"],
        ["SystemParameters"] = ["SystemID", "ParameterName", "ParameterValue"],
        ["IndividualToolData"] = ["ITNID", "ITN", "FTNID", "PTN", "ITNComment", "Status", "ProhibitTool", "AlarmTool", "AbsentTool"],
        ["IndividualToolCutterData"] = ["ITNID", "CutterNo", "CutterStatus", "Length", "Diameter", "LengthWear", "DiameterWear",
            "ActualToolLife", "TLAlarm", "TLWarning", "BTSAlarm"],
        ["FunctionalToolData"] = ["FTNID", "FTN", "FTNComment", "Type", "DiameterClass", "TLAlarmProhibit"],
        ["FunctionalToolCutterData"] = ["FTNID", "CutterNo", "Kind", "ToolLife", "ToolLifeWarning", "ToolLifeType",
            "MinimumLength", "MaximumLength", "MinimumDiameter", "MaximumDiameter", "OperatorCall", "FirstUseTool"],
        ["ToolLocation"] = ["ITNID", "CurrentDeviceID", "CurrentPot", "AllocatedDeviceID", "AllocatedPot", "FirstTool"],
        ["MainProgramTools"] = ["NCProgramFileID", "ProgramToolNumber", "FTN"],
        ["MachineJobs"] = ["JobID", "NCProgramFileID"],
        ["JobFeasibleDevices"] = ["JobID", "FeasibleDeviceID"],
        ["FixtureRun"] = ["CurJobID", "MachineDeviceID", "CurJobStatus"],
        ["Jobs"] = ["JobID", "ProcessID"],
        ["Processes"] = ["ProcessID", "PartID"],
        ["Orders"] = ["PartID", "Status"],
        ["PresetResults"] = ["PresetDateTime", "FTN", "ITN", "DeviceID", "M6Flag", "StatusOld", "StatusNew"],
        ["ToolInputOutputResults"] = ["InOutDateTime", "Action", "FTN", "ITN", "FromDeviceID", "FromPot", "ToDeviceID", "ToPot",
            "ToolOutReason"],
    };

    private static SchemaInfo SchemaWithout(params string[] tableDotColumn) =>
        new(Schema
            .SelectMany(t => t.Value.Select(c => (Table: t.Key, Column: c)))
            .Where(tc => !tableDotColumn.Contains(tc.Table + "." + tc.Column) && !tableDotColumn.Contains(tc.Table)));

    // --- codes ---

    [Fact]
    public void Size_class_is_the_low_four_bits_plus_its_flags()
    {
        Assert.Equal("small", Codes.ToolSizeClass(1));
        Assert.Equal("middle", Codes.ToolSizeClass(2)); // the software's order: 2 middle, 3 large
        Assert.Equal("large", Codes.ToolSizeClass(3));
        Assert.Equal("extra large 2", Codes.ToolSizeClass(6));
        Assert.Equal("empty", Codes.ToolSizeClass(8));
        Assert.Equal("large, Borematic", Codes.ToolSizeClass(0x13));
        Assert.Equal("APC prohibited, irregular shape", Codes.ToolSizeClass(0x60)); // no size, flags only
        Assert.Equal("code 7", Codes.ToolSizeClass(7));
        Assert.Equal("small, code 128", Codes.ToolSizeClass(0x81));
        Assert.Equal("", Codes.ToolSizeClass(0));
        Assert.Equal("", Codes.ToolSizeClass(null));
    }

    [Fact]
    public void Tool_out_reason_modes_and_change_sources_have_words()
    {
        Assert.Equal("never asked", Codes.ToolOutReasonMode(0));
        Assert.StartsWith("asked only for tools not in alarm", Codes.ToolOutReasonMode(1));
        Assert.Equal("always required", Codes.ToolOutReasonMode(2));
        Assert.StartsWith("required for tools not in alarm", Codes.ToolOutReasonMode(3));
        Assert.Equal("code 9", Codes.ToolOutReasonMode(9));

        Assert.Equal("machine upload", Codes.PresetResultSource(1));
        Assert.StartsWith("cell (presetter", Codes.PresetResultSource(0));
        Assert.Equal("", Codes.PresetResultSource(null));
    }

    // --- TL-3: why a cutter is in offset error ---

    private static Func<string, int?> Cutter(params (string Column, int? Value)[] values) =>
        column => values.FirstOrDefault(v => v.Column == column).Value;

    [Fact]
    public void Offset_error_names_the_value_outside_its_limits_with_its_wear()
    {
        // 101.25 + 0.01 wear against 95..100 (inch: 4 decimals).
        var above = Cutter(("Length", 10125000), ("LengthWear", 1000), ("MinimumLength", 9500000), ("MaximumLength", 10000000),
            ("MinimumDiameter", 0), ("MaximumDiameter", 0));
        Assert.Equal("length 101.2600 above max 100.0000 inch", SqlMakinoReader.OffsetErrorText(above, "inch", false));

        var below = Cutter(("Length", 9000000), ("MinimumLength", 9500000), ("MaximumLength", 10000000),
            ("Diameter", 1200000), ("MinimumDiameter", 1000000), ("MaximumDiameter", 1100000));
        Assert.Equal("length 90.000 below min 95.000 mm; diameter 12.000 above max 11.000 mm",
            SqlMakinoReader.OffsetErrorText(below, "mm", false));

        var radius = Cutter(("Diameter", 600000), ("MinimumDiameter", 500000), ("MaximumDiameter", 550000));
        Assert.Equal("radius 6.000 above max 5.500 mm", SqlMakinoReader.OffsetErrorText(radius, "mm", true));
    }

    [Fact]
    public void Offset_error_says_not_measured_only_a_minimum_or_within_limits_now()
    {
        var notMeasured = Cutter(("Length", 0), ("MinimumLength", 9500000), ("MaximumLength", 10000000));
        Assert.Equal("length not measured (0), min 95.000 mm", SqlMakinoReader.OffsetErrorText(notMeasured, "mm", false));

        // A minimum without a maximum: the cell compares with a maximum of 0, so every length is too long.
        var onlyMin = Cutter(("Length", 10000000), ("MinimumLength", 9500000), ("MaximumLength", 0));
        Assert.Contains("only a minimum set", SqlMakinoReader.OffsetErrorText(onlyMin, "mm", false));

        // The stored status says offset error, but the limits (edited since?) no longer do.
        var within = Cutter(("Length", 9800000), ("MinimumLength", 9500000), ("MaximumLength", 10000000),
            ("MinimumDiameter", 0), ("MaximumDiameter", 0));
        Assert.StartsWith("within its limits now", SqlMakinoReader.OffsetErrorText(within, "mm", false));

        // No limit columns on this cell: nothing to say.
        Assert.Null(SqlMakinoReader.OffsetErrorText(Cutter(("Length", 9800000)), "mm", false));
    }

    // --- TL-4: the unused filter ---

    [Fact]
    public void Unused_filter_reads_program_tool_lists_feasibility_orders_and_spares_without_exec()
    {
        Assert.Equal("unused", SqlMakinoReader.ToolFilter("unused", null));
        var sql = SqlMakinoReader.ToolsSql(SchemaWithout(), "unused");

        Assert.Contains("NOT EXISTS (SELECT 1 FROM MainProgramTools m WHERE m.FTN = f.FTN)", sql);
        Assert.Contains("fd.FeasibleDeviceID = @device", sql);
        Assert.Contains("fr.MachineDeviceID = @device AND fr.CurJobStatus = 2", sql); // processing there now counts
        Assert.Contains("o.Status NOT IN (0, 5, 6)", sql); // the finished order statuses don't make it needed
        Assert.Contains("(@spare = 1 AND i.PTN = 0)", sql);
        Assert.Contains("AS UnusedNoProgram", sql);
        Assert.Contains("SELECT TOP (@take)", sql); // capped like every filter
        Assert.DoesNotContain("EXEC", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SubprogramLinks", sql); // MainProgramTools already holds the subprograms' tools
        Assert.Empty(SqlMakinoReader.UnusedNotAvailable(SchemaWithout(), ptnDownloadType: 0));

        // Other filters carry the reason columns only as NULLs.
        var problems = SqlMakinoReader.ToolsSql(SchemaWithout(), "problems");
        Assert.Contains("CAST(NULL AS int) AS UnusedNoProgram", problems);
        Assert.DoesNotContain("MainProgramTools", problems);
    }

    [Fact]
    public void Unused_reasons_a_cell_cant_check_are_named_never_silently_dropped()
    {
        var noFeasible = SchemaWithout("JobFeasibleDevices");
        var sql = SqlMakinoReader.ToolsSql(noFeasible, "unused");
        Assert.DoesNotContain("JobFeasibleDevices", sql);
        Assert.Contains("MainProgramTools", sql);
        Assert.Contains(SqlMakinoReader.UnusedNotAvailable(noFeasible, 0), n => n.Contains("not feasible here"));

        Assert.Contains(SqlMakinoReader.UnusedNotAvailable(SchemaWithout("FixtureRun"), 0), n => n.Contains("processing"));
        Assert.Contains(SqlMakinoReader.UnusedNotAvailable(SchemaWithout("Orders"), 0), n => n.Contains("active order"));
        Assert.Contains(SqlMakinoReader.UnusedNotAvailable(SchemaWithout(), null), n => n.Contains("PTN download setting"));

        // Neither the program tool lists nor the PTN: the filter can't be applied at all.
        var none = SchemaWithout("MainProgramTools", "IndividualToolData.PTN");
        Assert.Null(SqlMakinoReader.ToolFilterSql(none, "unused"));
        Assert.Contains("MainProgramTools", SqlMakinoReader.FilterNotAvailable("unused"));
    }

    // --- TL-5 / TL-6: identity fields and settings are read through Opt ---

    [Fact]
    public void Tools_sql_reads_identity_fields_limits_and_type_settings_or_nulls()
    {
        var sql = SqlMakinoReader.ToolsSql(SchemaWithout(), "all");
        foreach (var column in new[]
                 {
                     "i.PTN AS PTN", "f.Type AS Type", "f.DiameterClass AS DiameterClass", "f.TLAlarmProhibit AS TLAlarmProhibit",
                     "l.FirstTool AS FirstTool", "cu.LengthWear AS LengthWear", "cf.MaximumLength AS MaximumLength",
                     "cf.OperatorCall AS OperatorCall", "cf.FirstUseTool AS FirstUseTool",
                 })
            Assert.Contains(column, sql);

        var old = SqlMakinoReader.ToolsSql(SchemaWithout("IndividualToolData.PTN", "FunctionalToolCutterData.MaximumLength",
            "ToolLocation.FirstTool"), "all");
        Assert.Contains("CAST(NULL AS int) AS PTN", old);
        Assert.Contains("CAST(NULL AS int) AS MaximumLength", old);
        Assert.Contains("CAST(NULL AS bit) AS FirstTool", old);
    }

    // --- TC-1: who changed the tool data ---

    [Fact]
    public void Tool_changes_read_who_wrote_each_row()
    {
        Assert.Contains("p.M6Flag AS M6Flag", SqlMakinoReader.ToolChangesSql(SchemaWithout(), includeLifeUpdates: true));
        Assert.Contains("CAST(NULL AS bit) AS M6Flag",
            SqlMakinoReader.ToolChangesSql(SchemaWithout("PresetResults.M6Flag"), includeLifeUpdates: true));
    }

    // --- TC-3: exchange pairs ---

    private static SqlMakinoReader.MoveEnds In(int itn, int device, int pot) => new("in", itn, 9, 0, device, pot);

    private static SqlMakinoReader.MoveEnds Out(int itn, int device, int pot) => new("out", itn, device, pot, 9, 0);

    [Fact]
    public void An_in_pairs_with_the_out_from_the_same_pot_just_before_it()
    {
        // Oldest to newest: 1001 out of MC1 pot 20, 1002 in; 1003 out of pot 5 then 1004 into pot 6 (another pot);
        // 1002 out of pot 20 again, 1005 in. Listed newest first.
        List<SqlMakinoReader.MoveEnds> oldestFirst =
        [
            Out(1001, 1, 20), In(1002, 1, 20), Out(1003, 1, 5), In(1004, 1, 6), Out(1002, 1, 20), In(1005, 1, 20),
        ];
        var newestFirst = Enumerable.Reverse(oldestFirst).ToList();
        var pairs = SqlMakinoReader.ExchangePairs(newestFirst);

        Assert.Equal([1002, null, null, null, 1001, null], pairs.ToArray());
    }

    [Fact]
    public void An_in_after_another_in_or_into_pot_0_never_pairs()
    {
        // Oldest to newest: 1001 out of pot 20, 1002 in, 1003 in (no out between), and a pot-0 pair.
        List<SqlMakinoReader.MoveEnds> oldestFirst =
            [Out(1001, 1, 20), In(1002, 1, 20), In(1003, 1, 20), Out(1004, 1, 0), In(1005, 1, 0)];
        var pairs = SqlMakinoReader.ExchangePairs(Enumerable.Reverse(oldestFirst).ToList());
        Assert.Equal([null, null, null, 1001, null], pairs.ToArray());
    }

    // --- live (skipped unless MAKINOMCP_LIVE_SETTINGS is set; shape only, never a real name or count) ---

    [SkippableFact]
    public async Task Live_tool_additions_are_read_and_shaped()
    {
        var path = Environment.GetEnvironmentVariable("MAKINOMCP_LIVE_SETTINGS");
        Skip.If(string.IsNullOrWhiteSpace(path), "MAKINOMCP_LIVE_SETTINGS is not set");
        var load = SettingsStore.Load(path!);
        Assert.True(load.Settings is not null, load.Problem);
        var reader = new SqlMakinoReader(load.Settings!.Makino, new Limits(500, load.Settings.Limits.MaxFileKB));
        var ct = CancellationToken.None;

        var overview = await reader.GetOverviewAsync(ct);
        var machine = overview.Devices.First(d => d.Type == "machine").Name;
        await Assert.ThrowsAsync<ArgumentException>(() => reader.GetToolsAsync(null, "unused", null, ct));

        var unused = await reader.GetToolsAsync(machine, "unused", null, ct);
        output.WriteLine($"unused on the first machine: {unused.Tools.Count} tools (capped {unused.Capped}); " +
                         $"inactive order statuses {string.Join("/", unused.InactiveOrderStatuses ?? [])}; " +
                         $"notAvailable {string.Join(" | ", unused.NotAvailable ?? [])}");
        foreach (var g in unused.Tools.SelectMany(t => t.UnusedReasons ?? []).GroupBy(x => x))
            output.WriteLine($"  reason '{g.Key}': {g.Count()}");
        Assert.Equal("unused", unused.Filter);
        Assert.All(unused.Tools, t => Assert.NotEmpty(t.UnusedReasons ?? []));
        Assert.All(unused.Tools, t => Assert.Empty(t.Cutters));
        Assert.NotNull(unused.InactiveOrderStatuses);

        var all = await new SqlMakinoReader(load.Settings.Makino, new Limits(100000, 256)).GetToolsAsync(null, "all", null, ct);
        output.WriteLine($"all tools {all.Tools.Count}; toolManagementOff {all.ToolManagementOff}; " +
                         $"with ptn {all.Tools.Count(t => t.Ptn is not null)}, type {all.Tools.Count(t => t.Type is not null)}, " +
                         $"sizeClass {all.Tools.Count(t => t.SizeClass is not null)}, " +
                         $"spare {all.Tools.Count(t => t.Flags?.Contains("spare (no T number)") ?? false)}, " +
                         $"not used since tool-in {all.Tools.Count(t => t.Flags?.Contains("not used since tool-in") ?? false)}");
        foreach (var g in all.Tools.SelectMany(t => t.TypeFlags ?? []).GroupBy(x => x))
            output.WriteLine($"  typeFlag '{g.Key}': {g.Count()}");
        foreach (var g in all.Tools.Select(t => t.SizeClass).OfType<string>().GroupBy(x => x))
            output.WriteLine($"  sizeClass '{g.Key}': {g.Count()}");
        var offset = all.Tools.SelectMany(t => t.Cutters).Where(c => c.Status == "offset error").ToList();
        output.WriteLine($"cutters in offset error {offset.Count}; explained {offset.Count(c => c.OffsetError is not null)}");
        foreach (var g in offset.Select(c => c.OffsetError?.Split(' ')[0] ?? "none").GroupBy(x => x))
            output.WriteLine($"  offset text starting '{g.Key}': {g.Count()}");
        Assert.All(all.Tools.SelectMany(t => t.Cutters).Where(c => c.Status != "offset error"), c => Assert.Null(c.OffsetError));
        Assert.All(all.Tools, t => Assert.True(t.Ptn is null || (t.Ptn > 0 && t.Ptn != t.Ftn)));
        Assert.All(all.Tools, t => Assert.Null(t.UnusedReasons));

        var changes = await reader.GetToolChangesAsync(3, null, null, false, ct);
        foreach (var g in changes.Changes.GroupBy(c => c.By ?? "(none)"))
            output.WriteLine($"changes by '{g.Key}': {g.Count()}");
        Assert.All(changes.Changes, c => Assert.True(c.By is null || c.By == "machine upload" || c.By.StartsWith("cell", StringComparison.Ordinal)));

        var moves = await reader.GetToolMovementsAsync(30, null, null, ct);
        output.WriteLine($"movements {moves.Movements.Count}; reasonAsked '{moves.ReasonAsked}'; " +
                         $"exchanges {moves.Movements.Count(m => m.ProbablyExchangedWith is not null)}; " +
                         $"notAvailable {string.Join(" | ", moves.NotAvailable ?? [])}");
        Assert.All(moves.Movements.Where(m => m.ProbablyExchangedWith is not null), m => Assert.Equal("in", m.Action));
        Assert.True(moves.ReasonAsked is not null || moves.NotAvailable is not null);

        var alarms = await reader.GetToolAlarmsAsync(7, null, null, ct);
        output.WriteLine($"tool alarm source: {alarms.Source}");
    }
}
