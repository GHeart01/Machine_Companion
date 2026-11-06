using Xunit;
using MakinoMCP.Core.Cell;

namespace MakinoMCP.Tests;

// get_pallet_diagnosis: the console's Pallet Diagnosis rules, without a database.
public class DiagnosisTests
{
    private static readonly SqlMakinoReader.DiagSettings OnDuty = new() { DutyModeOff = false };
    private static readonly SqlMakinoReader.DiagSettings OffDuty = new() { DutyModeOff = true };

    private static SqlMakinoReader.DiagDevice Machine(string name = "MC1") =>
        new(name, 1) { Connected = true, Emergency = false, SystemMode = true, Open = true };

    private static SqlMakinoReader.DiagDevice Station(string name = "WSS1") =>
        new(name, 3) { Connected = true, Emergency = false, SystemMode = true, Open = true, Skip = false };

    private static SqlMakinoReader.DiagFixture Waiting(int jobType = 2) =>
        new(1) { JobId = 10, JobType = jobType, JobNumber = 2, JobStatus = 1, ProcessId = 5, Process = 1, Order = "ORDER-001" };

    [Fact]
    public void A_ready_machine_lets_the_fixture_run()
    {
        var d = SqlMakinoReader.DiagnoseFixture(Waiting(), [Machine()], OnDuty);
        Assert.Equal(Codes.FixtureDiagnosis(1), d.Verdict);
        Assert.Equal("machining, job 2", d.Job);
        Assert.Equal("waiting for processing", d.JobStatus);
        Assert.Equal(Codes.FixtureDiagnosis(1), Assert.Single(d.Devices!).Verdict);
    }

    [Fact]
    public void Machine_checks_run_in_the_consoles_order()
    {
        var f = Waiting();
        // Not ready comes first, whatever else is wrong.
        Assert.Equal(9, SqlMakinoReader.MachineVerdict(f with { Suspend = true }, Machine() with { PmcAlarms = 1, Open = false }, OnDuty));
        Assert.Equal(9, SqlMakinoReader.MachineVerdict(f, Machine() with { SystemMode = false }, OnDuty));
        Assert.Equal(8, SqlMakinoReader.MachineVerdict(f with { Suspend = true }, Machine() with { Open = false }, OnDuty));
        Assert.Equal(43, SqlMakinoReader.MachineVerdict(f with { PreMcSuspend = true, PreMcReason = 1 }, Machine(), OnDuty));
        Assert.Equal(44, SqlMakinoReader.MachineVerdict(f with { PreMcSuspend = true, PreMcReason = 2 }, Machine(), OnDuty));
        Assert.Equal(13, SqlMakinoReader.MachineVerdict(f with { OffDutyProcess = true }, Machine(), OnDuty));
        Assert.Equal(1, SqlMakinoReader.MachineVerdict(f with { OffDutyProcess = true }, Machine(), OffDuty));
        Assert.Equal(7, SqlMakinoReader.MachineVerdict(f with { Damaged = true }, Machine() with { Open = false }, OnDuty));
        Assert.Equal(2, SqlMakinoReader.MachineVerdict(f with { Damaged = true }, Machine(), OnDuty));
        // A warning alone doesn't block a machine.
        Assert.Equal(1, SqlMakinoReader.MachineVerdict(f, Machine() with { AllAlarms = 2 }, OnDuty));
    }

    [Fact]
    public void Order_suspend_and_transport_cancel_count_only_with_the_cells_setting()
    {
        var f = Waiting() with { OrderMachiningSuspended = true };
        Assert.Equal(1, SqlMakinoReader.MachineVerdict(f, Machine(), OnDuty));
        Assert.Equal(8, SqlMakinoReader.MachineVerdict(f, Machine(), OnDuty with { OrderMachiningSuspend = true }));

        var prohibited = Machine() with { TransProhibit = true };
        Assert.Equal(1, SqlMakinoReader.MachineVerdict(Waiting(), prohibited, OnDuty));
        Assert.Equal(23, SqlMakinoReader.MachineVerdict(Waiting(), prohibited, OnDuty with { TransportCancel = true }));
    }

    [Fact]
    public void An_operator_call_job_is_held_while_off_duty_only_when_every_gate_is_open()
    {
        var permanent = Waiting() with { JobOperatorCall = 1 };
        Assert.Equal(32, SqlMakinoReader.MachineVerdict(permanent, Machine(), OffDuty));
        Assert.Equal(1, SqlMakinoReader.MachineVerdict(permanent, Machine(), OnDuty));
        Assert.Equal(1, SqlMakinoReader.MachineVerdict(permanent, Machine(), OffDuty with { DutyManagement = 0 }));
        Assert.Equal(1, SqlMakinoReader.MachineVerdict(permanent, Machine(), OffDuty with { OffDutyOperatorCallTransport = true }));

        // The fixture's own setting, and the one-shot rule.
        Assert.True(SqlMakinoReader.EffectiveOperatorCall(Waiting() with { OperatorCallStatus = 3 }, OffDuty));
        Assert.True(SqlMakinoReader.EffectiveOperatorCall(Waiting() with { JobOperatorCall = 2, LastOneShotProcessId = 4 }, OffDuty));
        Assert.False(SqlMakinoReader.EffectiveOperatorCall(Waiting() with { JobOperatorCall = 2, LastOneShotProcessId = 5 }, OffDuty));
        Assert.True(SqlMakinoReader.EffectiveOperatorCall(Waiting() with { JobOperatorCall = 2, LastOneShotProcessId = 5 }, OffDuty with { OneShotType = 1 }));
        Assert.False(SqlMakinoReader.EffectiveOperatorCall(Waiting(), OffDuty));
    }

    [Fact]
    public void Any_machine_that_can_run_wins_else_the_highest_verdict()
    {
        var two = SqlMakinoReader.DiagnoseFixture(Waiting(), [Machine("MC1") with { Open = false }, Machine("MC2")], OnDuty);
        Assert.Equal(Codes.FixtureDiagnosis(1), two.Verdict);
        Assert.Equal([Codes.FixtureDiagnosis(7), Codes.FixtureDiagnosis(1)], two.Devices!.Select(v => v.Verdict));

        // Closed (7) and not ready (9): 9 is the higher value, so it is the fixture's verdict.
        var blocked = SqlMakinoReader.DiagnoseFixture(Waiting(),
            [Machine("MC1") with { Open = false }, Machine("MC2") with { PmcAlarms = 1 }], OnDuty);
        Assert.Equal(Codes.FixtureDiagnosis(9), blocked.Verdict);
        Assert.Equal(Codes.FixtureDiagnosis(9) + ": 1 open alarm", blocked.Devices![1].Verdict);

        Assert.Equal(1, SqlMakinoReader.Combine([7, 1, 9], 11));
        Assert.Equal(11, SqlMakinoReader.Combine([], 11));
    }

    [Fact]
    public void Work_set_jobs_follow_the_station_chain()
    {
        var f = Waiting(jobType: 1);
        Assert.Equal(5, SqlMakinoReader.StationVerdict(f with { Hold = true }, Station() with { AllAlarms = 1 }, OnDuty));
        Assert.Equal(4, SqlMakinoReader.StationVerdict(f with { Hold = true }, Station(), OnDuty));
        Assert.Equal(-1, SqlMakinoReader.StationVerdict(f, Station() with { Skip = true }, OnDuty));
        Assert.Equal(3, SqlMakinoReader.StationVerdict(f, Station() with { Open = false }, OnDuty));
        Assert.Equal(22, SqlMakinoReader.StationVerdict(f, Station() with { TransProhibit = true }, OnDuty with { TransportCancel = true }));
        Assert.Equal(1, SqlMakinoReader.StationVerdict(f, Station(), OnDuty));

        // A machining job ignores feasible stations, so with no machine there is no feasible device.
        Assert.Equal(Codes.FixtureDiagnosis(11), SqlMakinoReader.DiagnoseFixture(Waiting(), [Station()], OnDuty).Verdict);
        Assert.Equal(Codes.FixtureDiagnosis(6), SqlMakinoReader.DiagnoseFixture(f, [], OnDuty).Verdict);
    }

    [Fact]
    public void A_fixture_without_a_job_running_or_with_another_job_type_gets_no_device_checks()
    {
        Assert.Equal(Codes.FixtureDiagnosis(12), SqlMakinoReader.DiagnoseFixture(new(1), [Machine()], OnDuty).Verdict);
        Assert.Equal(Codes.FixtureDiagnosis(2), SqlMakinoReader.DiagnoseFixture(new(1) { Damaged = true }, [Machine()], OnDuty).Verdict);

        var running = SqlMakinoReader.DiagnoseFixture(Waiting() with { JobStatus = 2 }, [Machine() with { PmcAlarms = 1 }], OnDuty);
        Assert.Equal(SqlMakinoReader.RunningNow, running.Verdict);
        Assert.Null(running.Devices);

        Assert.Equal("cleaning job: not checked by this tool", SqlMakinoReader.DiagnoseFixture(Waiting(jobType: 3), [], OnDuty).Verdict);
    }

    [Fact]
    public void Settings_follow_the_vendors_defaults()
    {
        var none = SqlMakinoReader.DiagSettingsFrom(new Dictionary<string, string>(), new Dictionary<string, string>());
        Assert.False(none.TransportCancel);
        Assert.False(none.OrderMachiningSuspend);
        Assert.Equal(1, none.DutyManagement); // no row: manual
        Assert.False(none.DutyModeOff);
        Assert.Null(SqlMakinoReader.DiagSettingsFrom(null, null).DutyModeOff);

        var set = SqlMakinoReader.DiagSettingsFrom(
            new Dictionary<string, string> { ["TransportCancel"] = "1", ["OrderMachiningSuspend"] = "1", ["DutyManagement"] = "0" },
            new Dictionary<string, string> { ["DutyMode"] = "1" });
        Assert.True(set.TransportCancel && set.OrderMachiningSuspend && set.DutyModeOff == true);
        Assert.Equal(0, set.DutyManagement);
    }

    [Fact]
    public void Not_checked_names_only_what_applies_to_the_pallets_jobs()
    {
        var resource = new Dictionary<string, string> { ["ToolManagementType"] = "2" };
        var machining = SqlMakinoReader.DiagNotChecked([Waiting()], resource);
        Assert.Contains(machining, n => n.StartsWith("the tool set", StringComparison.Ordinal));
        Assert.DoesNotContain(machining, n => n.StartsWith("material groups", StringComparison.Ordinal));
        Assert.DoesNotContain(SqlMakinoReader.DiagNotChecked([Waiting()], new Dictionary<string, string>()),
            n => n.StartsWith("the tool set", StringComparison.Ordinal));
        Assert.Contains(SqlMakinoReader.DiagNotChecked([Waiting(jobType: 1)], null), n => n.StartsWith("material groups", StringComparison.Ordinal));
    }

    [Fact]
    public void The_sql_reads_one_pallet_its_fixtures_and_their_feasible_devices()
    {
        var schema = new SchemaInfo(new Dictionary<string, string[]>
        {
            ["Pallets"] = ["PalletID", "PalletNumber", "PalletStatus", "WorkStatus"],
            ["PalletFixtures"] = ["PalletFixtureID", "PalletID", "FixtureNumber", "Suspend"],
            ["FixtureRun"] = ["PalletFixtureID", "CurJobID", "CurJobStatus"],
            ["Jobs"] = ["JobID", "JobType"],
            ["JobFeasibleDevices"] = ["JobID", "FeasibleDeviceID"],
        }.SelectMany(t => t.Value.Select(c => (t.Key, c))));
        var sql = SqlMakinoReader.DiagnosisSql(schema);
        Assert.Contains("FROM Pallets p WHERE p.PalletNumber = @pallet", sql);
        Assert.Contains("pf.Suspend AS Suspend", sql);
        Assert.Contains("CAST(NULL AS bit) AS OffDutyProcess", sql); // an older cell: the check is named in notAvailable
        Assert.Contains("FROM JobFeasibleDevices f", sql);
        Assert.Contains("SUM(Pmc) AS PmcAlarms", SqlMakinoReader.DiagnosisAlarmSql(schema));
    }
}
