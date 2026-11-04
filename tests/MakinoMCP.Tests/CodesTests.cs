using Xunit;
using MakinoMCP.Core.Cell;

namespace MakinoMCP.Tests;

public class CodesTests
{
    [Fact]
    public void PalletStatus_30_is_waiting_for_machining()
    {
        Assert.Equal("waiting for machining", Codes.PalletStatus(30));
    }

    [Fact]
    public void Unknown_value_is_code_N()
    {
        Assert.Equal("code 999", Codes.PalletStatus(999));
    }

    [Fact]
    public void Null_is_empty()
    {
        Assert.Equal("", Codes.PalletStatus(null));
        Assert.Equal("", Codes.MachiningFinish(null));
        Assert.Equal("", Codes.VehicleCommand(null));
    }

    [Fact]
    public void MachiningFinish_combines_flags()
    {
        Assert.Equal("broken tool (short), tool life", Codes.MachiningFinish(0x100000 | 0x10000));
    }

    [Fact]
    public void MachiningFinish_zero_is_normal()
    {
        Assert.Equal("normal", Codes.MachiningFinish(0));
    }

    [Fact]
    public void MachiningFinish_unknown_bits_shown()
    {
        Assert.Equal("code 16", Codes.MachiningFinish(0x10));
    }

    [Fact]
    public void VehicleCommand_zero_is_none()
    {
        Assert.Equal("none", Codes.VehicleCommand(0));
    }

    [Fact]
    public void VehicleCommand_known_and_unknown_bits()
    {
        Assert.Equal("load/unload, position", Codes.VehicleCommand(1 | 8));
        Assert.Equal("load/unload, code 64", Codes.VehicleCommand(1 | 64));
    }

    [Fact]
    public void CncMode_10_is_remote()
    {
        Assert.Equal("remote", Codes.CncMode(10));
    }

    [Fact]
    public void PmcAlarmLevel_maps_warning_and_alarm()
    {
        Assert.Equal("warning", Codes.PmcAlarmLevel(true));
        Assert.Equal("alarm", Codes.PmcAlarmLevel(false));
    }

    [Fact]
    public void Each_method_returns_its_mapped_words()
    {
        Assert.Equal("machine", Codes.DeviceType(1));
        Assert.Equal("MAG3 pallet changer", Codes.DeviceType(6));
        Assert.Equal("MDI", Codes.CncMode(0));
        Assert.Equal("DNC", Codes.CncMode(101));
        Assert.Equal("machining", Codes.MachineAction(1));
        Assert.Equal("pallet index", Codes.MachineSubAction(1));
        Assert.Equal("waiting for material", Codes.OrderStatus(8));
        Assert.Equal("clamp", Codes.WorkSetOperation(2));
        Assert.Equal("unclamp scrap cancelled", Codes.WorkSetOperation(5));
        Assert.Equal("check fixtures", Codes.WorkSetOperation(30));
        Assert.Equal("code 28", Codes.WorkSetOperation(28));
        Assert.Equal("maintenance", Codes.WssMode(16));
    }

    // 0.2 tables: one known value and one unknown value each.
    public static TheoryData<string, int, string> NewTables => new()
    {
        { "ToolStatus", 2, "life warning" },
        { "ToolLifeType", 3, "tenths of a second" },
        { "ToolKind", 9, "face mill" },
        { "DevicePositionStatus", 3, "processing" },
        { "DeviceDashStatus", 4, "error" },
        { "MachineToolStatus", 2, "lacking tools" },
        { "CncRun", 2, "hold" },
        { "OperatorCall", 1, "M code" },
        { "RunJob", 2, "pallet change" },
        { "PalletDisplayStatus", 4, "waiting for machining" },
        { "PalletWorkStatus", 3, "abnormal" },
        { "VehicleMode", 48, "manual interrupt" },
        { "JobType", 2, "machining" },
        { "ProcessStatus", -1, "varied" },
        { "FixtureJobStatus", 4, "abnormal finish" },
        { "FixtureWorkStatus", 6, "seating error" },
        { "PreMcSuspendReason", 2, "CSG error" },
        { "ReclampStatus", 1, "timeout alarm" },
        { "WorkSetCommandOperation", 21, "check fixtures" },
        { "ManualTransportStatus", 3, "waiting to finish" },
        { "InfoFileType", 2, "tool setting" },
    };

    private static Func<int?, string> Table(string name) =>
        (Func<int?, string>)typeof(Codes).GetMethod(name, [typeof(int?)])!.CreateDelegate(typeof(Func<int?, string>));

    [Theory]
    [MemberData(nameof(NewTables))]
    public void New_tables_map_known_and_unknown_values(string table, int known, string expected)
    {
        var words = Table(table);
        Assert.Equal(expected, words(known));
        Assert.Equal("code 999", words(999));
        Assert.Equal("", words(null));
    }

    [Fact]
    public void Tool_and_device_tables_spot_checks()
    {
        Assert.Equal("ready", Codes.ToolStatus(1));
        Assert.Equal("alarm", Codes.ToolStatus(4));
        Assert.Equal("offset error", Codes.ToolStatus(3));
        Assert.Equal("undefined", Codes.ToolLifeType(-1));
        Assert.Equal("seconds", Codes.ToolLifeType(0));
        Assert.Equal("count", Codes.ToolLifeType(2));
        Assert.Equal("code 13", Codes.ToolKind(13)); // a gap in the enum
        Assert.Equal("drill speed-up cycle", Codes.ToolKind(29));
        Assert.Equal("empty", Codes.DevicePositionStatus(0));
        Assert.Equal("unload before", Codes.DevicePositionStatus(6));
        Assert.Equal("down", Codes.DeviceDashStatus(0));
        Assert.Equal("offset error", Codes.MachineToolStatus(5));
        Assert.Equal("run", Codes.CncRun(3));
        Assert.Equal("released", Codes.OperatorCall(0));
        Assert.Equal("manual transport", Codes.RunJob(4));
        Assert.Equal("idle", Codes.PalletDisplayStatus(0));
        Assert.Equal("pallet active", Codes.PalletDisplayStatus(14));
        Assert.Equal("none", Codes.PalletWorkStatus(0));
        Assert.Equal("cancel", Codes.PalletWorkStatus(4));
        Assert.Equal("system", Codes.VehicleMode(0));
        Assert.Equal("code 3", Codes.VehicleMode(3)); // a lookup, not flags
    }

    [Fact]
    public void ToolAlarms_lists_each_flag_and_leftover_bits()
    {
        Assert.Empty(Codes.ToolAlarms(0));
        Assert.Empty(Codes.ToolAlarms(null));
        Assert.Equal(["tool life alarm", "broken tool (short)"], Codes.ToolAlarms(2 | 8));
        Assert.Equal(["STS not OK", "code 2048"], Codes.ToolAlarms(0x400 | 0x800));
    }

    [Fact]
    public void ToolAlarm_columns_map_one_bit_each()
    {
        var columns = Codes.ToolAlarmColumns;
        Assert.Contains(columns, c => c.Column == "TLAlarm" && c.Flag == 2);
        Assert.Contains(columns, c => c.Column == "BT2AlarmLong" && c.Flag == 0x10);
        Assert.Equal(columns.Count, columns.Select(c => c.Flag).Distinct().Count());
        Assert.All(columns, c => Assert.Single(Codes.ToolAlarms(c.Flag)));
    }

    [Fact]
    public void Unknown_values_are_code_N_in_every_mapped_method()
    {
        Assert.Equal("code 999", Codes.DeviceType(999));
        Assert.Equal("code 999", Codes.CncMode(999));
        Assert.Equal("code 999", Codes.MachineAction(999));
        Assert.Equal("code 999", Codes.MachineSubAction(999));
        Assert.Equal("code 999", Codes.OrderStatus(999));
        Assert.Equal("code 999", Codes.WorkSetOperation(999));
        Assert.Equal("code 999", Codes.WssMode(999));
    }
}
