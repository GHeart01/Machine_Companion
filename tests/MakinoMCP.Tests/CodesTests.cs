using Xunit;
using MakinoMCP.Core.Cell;

namespace MakinoMCP.Tests;

public class CodesTests
{
    [Fact]
    public void Unknown_value_is_code_N()
    {
        Assert.Equal("code 999", Codes.PalletDisplayStatus(999));
    }

    [Fact]
    public void Null_is_empty()
    {
        Assert.Equal("", Codes.PalletDisplayStatus(null));
        Assert.Equal("", Codes.MachiningFinish(null));
        Assert.Equal("", Codes.VehicleCommand(null));
    }

    [Fact]
    public void MachiningFinish_combines_flags_in_the_consoles_alarm_names()
    {
        Assert.Equal("BTS alarm (broken tool), TL alarm (tool life)", Codes.MachiningFinish(0x100000 | 0x10000));
        // BT2 is a tool measured too long (pulled out), not a broken one.
        Assert.Equal("BT2 alarm (tool too long)", Codes.MachiningFinish(0x20000));
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
    public void CncMode_10_is_remote_tape()
    {
        Assert.Equal("remote (tape)", Codes.CncMode(10));
    }

    [Fact]
    public void Vehicle_and_station_modes_use_the_consoles_words()
    {
        Assert.Equal("jog high", Codes.VehicleMode(2));
        Assert.Equal("jog low", Codes.VehicleMode(4));
        Assert.Equal("local", Codes.VehicleMode(32));
        Assert.Equal("manual", Codes.VehicleMode(48));
        // The console shows every station mode but system and maintenance as local.
        Assert.Equal("local", Codes.WssMode(32));
        Assert.Equal("local (jog high)", Codes.WssMode(2));
        Assert.Equal("local (teaching)", Codes.WssMode(64));
    }

    [Fact]
    public void Pallet_display_statuses_tell_the_pairs_apart()
    {
        Assert.Equal("waiting for work setting (no result on it)", Codes.PalletDisplayStatus(5));
        Assert.Equal("waiting for work setting (with a result to unload)", Codes.PalletDisplayStatus(6));
        Assert.Equal("work-set hold", Codes.PalletDisplayStatus(9));
        Assert.Equal("work-set hold (with a result on it)", Codes.PalletDisplayStatus(10));
        Assert.Equal("pallet input/output, manual transport or exchange", Codes.PalletDisplayStatus(12));
    }

    [Fact]
    public void Lacking_tool_scope_words()
    {
        Assert.Equal("its next machining job", Codes.LackingToolScope(0));
        Assert.Equal("all its remaining jobs", Codes.LackingToolScope(2));
        Assert.Equal("code 3", Codes.LackingToolScope(3));
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
        { "ToolLifeType", 3, "seconds (the control counts tenths)" },
        { "ToolKind", 9, "face mill" },
        { "DevicePositionStatus", 3, "processing" },
        { "DeviceDashStatus", 4, "error" },
        { "MachineToolStatus", 2, "lacking tools" },
        { "CncRun", 2, "hold" },
        { "OperatorCall", 1, "M code" },
        { "RunJob", 2, "pallet change" },
        { "PalletDisplayStatus", 4, "waiting for machining" },
        { "PalletWorkStatus", 3, "scrap or abnormal work set" },
        { "VehicleMode", 48, "manual" },
        { "JobType", 2, "machining" },
        { "ProcessStatus", -1, "varied" },
        { "FixtureJobStatus", 4, "abnormal finish" },
        { "FixtureWorkStatus", 6, "seating error" },
        { "PreMcSuspendReason", 2, "CSG error" },
        { "ReclampStatus", 1, "timeout alarm" },
        { "WorkSetCommandOperation", 21, "check fixtures" },
        { "ManualTransportStatus", 3, "waiting to finish" },
        { "InfoFileType", 2, "tool setting" },
        { "LackingToolScope", 1, "its machining jobs before the next work setting" },
        { "WssInstruction", 6, "unclamp and clamp" },
        { "DevicePowerStatus", 6, "powered off" },
        { "DevicePowerOffResult", 2, "a pallet still has work" },
        { "CellPowerOffStatus", 2, "running (time reached, finishing the remaining work)" },
        { "AbnormalFinishAction", 3, "always wait for the operator" },
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
        Assert.Equal("before-unload program running", Codes.DevicePositionStatus(6));
        Assert.Equal("down", Codes.DeviceDashStatus(0));
        Assert.Equal("offset error", Codes.MachineToolStatus(5));
        Assert.Equal("run", Codes.CncRun(3));
        Assert.Equal("released", Codes.OperatorCall(0));
        Assert.Equal("manual transport", Codes.RunJob(4));
        Assert.Equal("idle", Codes.PalletDisplayStatus(0));
        Assert.Equal("special transport (extra tool pallet, APC pallet change, reclamp or fixture change)", Codes.PalletDisplayStatus(14));
        Assert.Equal("none", Codes.PalletWorkStatus(0));
        Assert.Equal("cancelled (order or process)", Codes.PalletWorkStatus(4));
        Assert.Equal("system", Codes.VehicleMode(0));
        Assert.Equal("code 3", Codes.VehicleMode(3)); // a lookup, not flags
    }

    [Fact]
    public void ToolAlarms_lists_each_flag_and_leftover_bits()
    {
        Assert.Empty(Codes.ToolAlarms(0));
        Assert.Empty(Codes.ToolAlarms(null));
        Assert.Equal(["tool life alarm", "BTS alarm (broken tool)"], Codes.ToolAlarms(2 | 8));
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
