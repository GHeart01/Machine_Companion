using Xunit;
using MakinoMCP.Core.Cell;

namespace MakinoMCP.Tests;

// get_cell_overview's settings and timing, without a database.
public class OverviewSettingsTests
{
    private static readonly DateTime Start = new(2026, 11, 2, 9, 0, 0);

    private static Dictionary<string, string> D(params (string Key, string Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Timing_shows_only_work_in_progress_and_a_real_planned_finish()
    {
        Assert.Equal((Start, Start.AddMinutes(10)), SqlMakinoReader.Timing(true, Start, Start.AddMinutes(10)));
        // The cell stores the start as the finish when it has no planned time.
        Assert.Equal((Start, (DateTime?)null), SqlMakinoReader.Timing(true, Start, Start));
        Assert.Equal(((DateTime?)null, (DateTime?)null), SqlMakinoReader.Timing(false, Start, Start.AddMinutes(10)));
        Assert.Equal(((DateTime?)null, (DateTime?)null), SqlMakinoReader.Timing(true, null, null));
    }

    [Fact]
    public void Duty_follows_duty_management_then_the_duty_mode()
    {
        // Not used: the cell behaves as off duty whatever is stored.
        Assert.Equal("off duty (duty management not used)",
            SqlMakinoReader.DutyFrom(D(("DutyManagement", "0")), D(("DutyMode", "0"))));
        Assert.Equal("on duty", SqlMakinoReader.DutyFrom(D(("DutyManagement", "2")), D(("DutyMode", "0"))));
        Assert.Equal("off duty", SqlMakinoReader.DutyFrom(D(("DutyManagement", "1")), D(("DutyMode", "1"))));
        // No DutyManagement row is the default, manual; no DutyMode row is on duty.
        Assert.Equal("on duty", SqlMakinoReader.DutyFrom(D(), D()));
        Assert.Null(SqlMakinoReader.DutyFrom(null, D()));
        Assert.Null(SqlMakinoReader.DutyFrom(D(("DutyManagement", "1")), null));

        Assert.True(SqlMakinoReader.DutyScheduleFrom(D(("DutyManageMode", "1"))));
        Assert.False(SqlMakinoReader.DutyScheduleFrom(D()));
    }

    [Fact]
    public void Power_off_is_shown_only_when_set()
    {
        Assert.Null(SqlMakinoReader.PowerOffFrom(D()));
        Assert.Null(SqlMakinoReader.PowerOffFrom(D(("PowerOffStatus", "0"))));
        var set = SqlMakinoReader.PowerOffFrom(D(("PowerOffStatus", "1"), ("PowerOffStartDateTime", "2026/11/06 18:00")));
        Assert.Equal("scheduled, waiting", set!.Status);
        Assert.Equal("2026/11/06 18:00", set.Start);
        Assert.Null(set.Release);
    }

    [Fact]
    public void Recovery_policy_lists_only_the_kinds_the_cell_sets()
    {
        var policy = SqlMakinoReader.RecoveryPolicyFrom(D(("SystemAbortRecovery", "0"), ("ToolMonitorRecovery", "2")), "");
        Assert.Equal(new Dictionary<string, string>
        {
            ["system abort"] = "raise a recovery request", ["tool monitor alarm"] = "finish abnormally (scrap)",
        }, policy);
        Assert.Equal("always wait for the operator",
            SqlMakinoReader.RecoveryPolicyFrom(D(("DeviceWorkVacuumRecovery", "3")), "Device")!["work vacuum"]);
        Assert.Null(SqlMakinoReader.RecoveryPolicyFrom(D(), ""));
    }

    [Fact]
    public void The_main_program_is_named_when_it_is_one_of_the_cells_own()
    {
        Assert.StartsWith("dataserver program", SqlMakinoReader.ProgramRole(7996, null, null));
        Assert.Equal("work offset program", SqlMakinoReader.ProgramRole(7998, D(), D()));
        Assert.StartsWith("dataserver program", SqlMakinoReader.ProgramRole(7000, D(("DataserverONumber", "7000")), null));
        Assert.Equal("work offset program", SqlMakinoReader.ProgramRole(7100, null, D(("WorkOffsetONumber", "7100"))));
        Assert.Null(SqlMakinoReader.ProgramRole(1000, null, null));
        Assert.Null(SqlMakinoReader.ProgramRole(0, null, null));
    }

    [Fact]
    public void Disabled_operations_read_numbers_or_enum_names()
    {
        Assert.Null(SqlMakinoReader.DisabledOperationsFrom("0"));
        Assert.Null(SqlMakinoReader.DisabledOperationsFrom(null));
        Assert.Equal(["pallet input/output", "reclamp"], SqlMakinoReader.DisabledOperationsFrom("5"));
        Assert.Equal(["automatic work setting"], SqlMakinoReader.DisabledOperationsFrom("AutoDriveWorkSetJob"));
        Assert.Equal(["pallet input/output", "code 8"], SqlMakinoReader.DisabledOperationsFrom("9"));
        Assert.Equal(["code Banana"], SqlMakinoReader.DisabledOperationsFrom("Banana"));
    }
}
