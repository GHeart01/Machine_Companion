using Xunit;
using MakinoMCP.Core.Cell;

namespace MakinoMCP.Tests;

// get_part_routing's matching, SQL and tool availability, without a database. Sample values only.
public class RoutingReaderTests
{
    private static readonly Dictionary<string, string[]> Schema = new()
    {
        ["Devices"] = ["DeviceID", "DeviceName", "DeviceType", "DeviceNumber"],
        ["Parts"] = ["PartID", "PartName", "Revision", "Comment"],
        ["Processes"] = ["ProcessID", "PartID", "ProcessNumber", "ProcessName", "Status", "MaxFixtures"],
        ["Operations"] = ["OperationID", "ProcessID", "OperationSequence", "ClampQuantity", "OperationName"],
        ["Jobs"] = ["JobID", "ProcessID", "JobNumber", "JobType"],
        ["MachineJobs"] = ["JobID", "NCProgramFileID", "MachiningTime", "OperatorCall"],
        ["WorkSetJobs"] = ["JobID", "OperationType", "InformationFileID", "WorkSetTime"],
        ["JobFeasibleDevices"] = ["JobID", "FeasibleDeviceID"],
        ["NCProgramFiles"] = ["NCProgramFileID", "Name"],
        ["InformationFiles"] = ["InformationFileID", "Name"],
        ["FixtureProcesses"] = ["FixtureID", "ProcessID", "FixturePriority", "Enabled"],
        ["Fixtures"] = ["FixtureID", "FixtureName"],
        ["PalletFixtures"] = ["PalletFixtureID", "PalletID", "FixtureNumber", "FixtureID"],
        ["Pallets"] = ["PalletID", "PalletNumber"],
        ["MainProgramTools"] = ["NCProgramFileID", "ProgramToolNumber", "ToolCount", "CutterNo", "CuttingTime",
            "CuttingDistance", "CuttingCount", "ToolLifeType", "FTN"],
        ["FunctionalToolData"] = ["FTNID", "FTN", "FTNComment"],
    };

    private static SchemaInfo SchemaWithout(params string[] tableDotColumn) =>
        new(Schema
            .SelectMany(t => t.Value.Select(c => (Table: t.Key, Column: c)))
            .Where(tc => !tableDotColumn.Contains(tc.Table + "." + tc.Column) && !tableDotColumn.Contains(tc.Table)));

    private static SqlMakinoReader.PartMatch P(int id, string name, string revision = "", int processes = 1) =>
        new(id, name, revision, processes);

    // --- matching: exact first, then contains; several → candidates ---

    [Fact]
    public void Exact_name_wins_over_names_that_contain_it_and_the_others_are_named()
    {
        var found = new[] { P(1, "CONTOSO-100-OP1"), P(2, "contoso-100"), P(3, "CONTOSO-100-OP2") };

        var (chosen, candidates, others) = SqlMakinoReader.PickPart(found, "CONTOSO-100");

        Assert.Equal(2, chosen?.Id);
        Assert.Empty(candidates);
        Assert.Equal([1, 3], others.Select(o => o.Id));
    }

    [Fact]
    public void One_part_containing_the_text_is_picked()
    {
        var (chosen, candidates, others) = SqlMakinoReader.PickPart([P(7, "FABRIKAM-BRACKET-77")], "bracket");

        Assert.Equal(7, chosen?.Id);
        Assert.Empty(candidates);
        Assert.Empty(others);
    }

    [Fact]
    public void Parts_split_per_operation_are_candidates_never_merged_or_picked()
    {
        var found = new[] { P(1, "CONTOSO-200-OP1"), P(2, "CONTOSO-200-OP2") };

        var (chosen, candidates, _) = SqlMakinoReader.PickPart(found, "CONTOSO-200");

        Assert.Null(chosen);
        Assert.Equal([1, 2], candidates.Select(c => c.Id));
    }

    [Fact]
    public void Same_name_in_several_revisions_gives_candidates()
    {
        var found = new[] { P(1, "CONTOSO-300", "A"), P(2, "CONTOSO-300", "B"), P(3, "CONTOSO-300-OP2") };

        var (chosen, candidates, _) = SqlMakinoReader.PickPart(found, " contoso-300 ");

        Assert.Null(chosen);
        Assert.Equal([1, 2], candidates.Select(c => c.Id)); // the exact ones, not the split part
    }

    [Fact]
    public void Nothing_found_gives_no_part_and_no_candidates()
    {
        var (chosen, candidates, others) = SqlMakinoReader.PickPart([], "CONTOSO-404");

        Assert.Null(chosen);
        Assert.Empty(candidates);
        Assert.Empty(others);
    }

    [Fact]
    public void Like_pattern_escapes_wildcards_so_the_text_is_matched_literally()
    {
        Assert.Equal("%CONTOSO\\_10\\%\\[A]%", SqlMakinoReader.LikePattern("CONTOSO_10%[A]"));
        Assert.Equal("%A\\\\B%", SqlMakinoReader.LikePattern("A\\B"));
    }

    [Fact]
    public void Part_search_is_parameterized_bounded_and_puts_exact_names_first()
    {
        var sql = SqlMakinoReader.PartSearchSql(SchemaWithout(), withRevision: false);

        Assert.Contains("TOP (@take)", sql);
        Assert.Contains("LIKE UPPER(@pattern) ESCAPE '\\'", sql);
        Assert.Contains("UPPER(p.PartName) = UPPER(@part)", sql);
        Assert.DoesNotContain("@revision", sql);
        Assert.Contains("@revision", SqlMakinoReader.PartSearchSql(SchemaWithout(), withRevision: true));
        Assert.Contains("CAST(NULL AS varchar(4000)) AS Revision", SqlMakinoReader.PartSearchSql(SchemaWithout("Parts.Revision"), false));
    }

    // --- the routing SQL ---

    [Fact]
    public void Routing_reads_one_part_and_degrades_for_optional_tables()
    {
        var full = SqlMakinoReader.RoutingSql(SchemaWithout());
        Assert.Contains("WHERE pr.PartID = @partId", full);
        Assert.Contains("LEFT JOIN MachineJobs mj ON mj.JobID = j.JobID", full);
        Assert.Contains("LEFT JOIN WorkSetJobs wj ON wj.JobID = j.JobID", full);
        Assert.Contains("i.Name AS InfoFileName", full);
        Assert.Contains("FROM FixtureProcesses fp", full);
        Assert.Contains("FROM JobFeasibleDevices", full);

        var old = SqlMakinoReader.RoutingSql(SchemaWithout("InformationFiles", "FixtureProcesses", "Processes.MaxFixtures"));
        Assert.Contains("CAST(NULL AS varchar(4000)) AS InfoFileName", old);
        Assert.Contains("CAST(NULL AS int) AS MaxFixtures", old);
        Assert.DoesNotContain("FROM FixtureProcesses", old);
        Assert.Contains("WHERE 1 = 0", old); // the fixture result set stays, empty
    }

    [Fact]
    public void Program_tools_come_from_main_program_tools_of_this_parts_programs_first_cutter_only()
    {
        var sql = SqlMakinoReader.ProgramToolsSql(SchemaWithout());
        Assert.NotNull(sql);
        Assert.Contains("TOP (@take)", sql);
        Assert.Contains("FROM MainProgramTools m", sql);
        Assert.Contains("m.CutterNo = 1", sql);
        Assert.Contains("pr.PartID = @partId", sql);
        Assert.Contains("FTNComment", sql);

        Assert.Null(SqlMakinoReader.ProgramToolsSql(SchemaWithout("MainProgramTools")));
        var noNames = SqlMakinoReader.ProgramToolsSql(SchemaWithout("FunctionalToolData"));
        Assert.Contains("CAST(NULL AS varchar(4000)) AS FTNComment", noNames);
    }

    // --- tool use and availability ---

    [Fact]
    public void Cutting_use_is_in_the_unit_of_its_life_type()
    {
        Assert.Equal("12.5 min", SqlMakinoReader.CuttingUse(0, 750, 0, 0, "mm"));
        Assert.Equal("40 count", SqlMakinoReader.CuttingUse(2, 0, 0, 40, "mm"));
        Assert.Equal("300 m", SqlMakinoReader.CuttingUse(1, 0, 300, 0, "mm"));
        Assert.Equal("300 inch", SqlMakinoReader.CuttingUse(1, 0, 300, 0, "inch"));
        Assert.Null(SqlMakinoReader.CuttingUse(-1, 0, 0, 0, "mm")); // not life-checked
        Assert.Null(SqlMakinoReader.CuttingUse(0, 0, 0, 0, "mm"));
    }

    [Fact]
    public void A_tool_is_missing_on_each_feasible_machine_with_fewer_usable_tools_than_needed()
    {
        var machines = new[] { (Id: 1, Name: "MC1"), (Id: 2, Name: "MC2"), (Id: 3, Name: "MC3") };
        var counts = new Dictionary<(int DeviceId, int Ftn), int> { [(1, 501)] = 2, [(2, 501)] = 1, [(9, 501)] = 4 };

        var (usable, missingOn) = SqlMakinoReader.Availability(501, needed: 2, machines, counts);

        Assert.Equal(new Dictionary<string, int> { ["MC1"] = 2, ["MC2"] = 1, ["MC3"] = 0 }, usable);
        Assert.Equal(["MC2", "MC3"], missingOn);
    }

    [Fact]
    public void Availability_unknown_is_not_reported_as_missing()
    {
        var (usable, missingOn) = SqlMakinoReader.Availability(501, 1, [(1, "MC1")], counts: null);
        Assert.Null(usable);
        Assert.Null(missingOn);

        var (none, noMachines) = SqlMakinoReader.Availability(501, 1, [], new Dictionary<(int, int), int>());
        Assert.Null(none);
        Assert.Null(noMachines);

        // The cell counts a tool missing only when fewer usable ones than ToolCount sit there; ToolCount 0 never is.
        var (counted, neverMissing) = SqlMakinoReader.Availability(501, 0, [(1, "MC1")], new Dictionary<(int, int), int>());
        Assert.Equal(0, counted!["MC1"]);
        Assert.Null(neverMissing);
    }

    [Fact]
    public void Fixture_places_name_the_pallet_and_fixture_number()
    {
        Assert.Equal("pallet 12 fixture 2", SqlMakinoReader.FixturePlace(12, 2, "", 40, enabled: true));
        Assert.Equal("pallet 12 fixture 2 (CONTOSO VISE, disabled)", SqlMakinoReader.FixturePlace(12, 2, "CONTOSO VISE", 40, enabled: false));
        Assert.Equal("fixture id 40 (not on a pallet)", SqlMakinoReader.FixturePlace(null, null, "", 40, enabled: true));
    }

    [Fact]
    public void New_codes_for_routing_have_words()
    {
        Assert.Equal("unload", Codes.WorkSetJobOperation(1));
        Assert.Equal("code 9", Codes.WorkSetJobOperation(9));
        Assert.Equal("one-shot", Codes.JobOperatorCall(2));
        Assert.Equal("code 7", Codes.JobOperatorCall(7));
    }
}
