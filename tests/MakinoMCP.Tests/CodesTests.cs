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
        Assert.Equal("machining", Codes.WorkSetFinish(1));
        Assert.Equal("maintenance", Codes.WssMode(16));
    }

    [Fact]
    public void Unknown_values_are_code_N_in_every_mapped_method()
    {
        Assert.Equal("code 999", Codes.DeviceType(999));
        Assert.Equal("code 999", Codes.CncMode(999));
        Assert.Equal("code 999", Codes.MachineAction(999));
        Assert.Equal("code 999", Codes.MachineSubAction(999));
        Assert.Equal("code 999", Codes.OrderStatus(999));
        Assert.Equal("code 999", Codes.WorkSetFinish(999));
        Assert.Equal("code 999", Codes.WssMode(999));
    }
}
