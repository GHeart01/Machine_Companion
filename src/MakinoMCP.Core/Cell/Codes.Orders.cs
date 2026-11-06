// Code meanings for the pallet, order and production values (get_pallets, get_orders, get_production). Like
// Codes.cs: facts about the cell database, read from the installed MAS-A5 software and written fresh here.

namespace MakinoMCP.Core.Cell;

public static partial class Codes
{
    // PalletLocation.CurDevicePosition (DevicePosition) at a machine, work-set station or vehicle. Stockers number
    // their shelves instead, so this list doesn't apply there.
    private static readonly Dictionary<int, string> DevicePositions = new()
    {
        [0] = "undefined",
        [1] = "table",
        [2] = "front",
        [3] = "back",
        [4] = "buffer 1",
        [5] = "buffer 2",
    };

    // Pallets.PalletType (PalletType): flags, 0 is a standard pallet and -1 undefined.
    private static readonly Dictionary<int, string> PalletTypeBits = new()
    {
        [0x1] = "rotary work head (5-axis table)",
        [0x2] = "through-pallet auto coupler (TPAC)",
        [0x4] = "material",
        [0x8] = "hydraulic",
        [0x10] = "separate",
    };

    // PalletFixtures.OperatorCallStatus (FixtureOperatorCallStatus): 0 and 1 leave it to the process's jobs; 2 and 3
    // make every machining job of the fixture call the operator.
    private static readonly Dictionary<int, string> FixtureOperatorCalls = new()
    {
        [0] = "per process (permanent)",
        [1] = "per process (one-shot)",
        [2] = "permanent (fixture setting)",
        [3] = "one-shot (fixture setting)",
    };

    // FixtureRun.CleaningFinishStatus / DeburringFinishStatus (CommandCompleteStatus): how a station's or device's
    // command ended.
    private static readonly Dictionary<int, string> CommandFinishes = new()
    {
        [0] = "undefined",
        [1] = "normal",
        [2] = "alarm",
        [3] = "cancel",
        [4] = "interrupt",
        [5] = "seating error",
        [6] = "break off",
        [7] = "unclamp cancel",
        [8] = "retry",
        [9] = "fixture damage",
        [10] = "stocker empty",
        [11] = "system abort",
        [12] = "tool error",
        [13] = "skipped",
        [14] = "IMC class 2 traverse",
        [15] = "remove scrap",
        [16] = "measuring result NG",
        [17] = "bucket damage",
        [18] = "clamp NG",
        [19] = "unclamp NG",
    };

    public static string DevicePosition(int? value) => Lookup(value, DevicePositions);

    /// <summary>A pallet type in words: "standard" for 0, "undefined" for -1, else its flags.</summary>
    public static string PalletType(int? value) => value switch
    {
        -1 => "undefined",
        _ => Flags(value, PalletTypeBits, "standard"),
    };

    public static string FixtureOperatorCall(int? value) => Lookup(value, FixtureOperatorCalls);

    public static string CommandFinish(int? value) => Lookup(value, CommandFinishes);
}
