// Code meanings for the newer tools (get_work_set, find_serial). Like Codes.cs: facts about the cell database, read
// from the installed MAS-A5 software and written fresh here.

namespace MakinoMCP.Core.Cell;

public static partial class Codes
{
    // PartSerialNumberTracking.DeviceJobType: which kind of job recorded the serial numbers.
    private static readonly Dictionary<int, string> SerialJobTypes = new()
    {
        [0] = "none",
        [1] = "machining",
        [2] = "work-set clamp",
        [3] = "work-set unclamp",
    };

    // WorkSetStationFinishStatus: what the work-set station monitor says a fixture leaves the station as, once the
    // operator finishes the instruction.
    private static readonly Dictionary<int, string> WorkSetLeavesAsWords = new()
    {
        [0] = "",
        [1] = "machining",
        [2] = "machining suspended",
        [3] = "work-set hold (clamp)",
        [4] = "work-set hold (unclamp)",
        [5] = "work-set hold",
        [6] = "no work",
        [7] = "cleaning",
        [8] = "deburring",
        [9] = "measuring",
    };

    /// <summary>What a re-unclamp leaves the fixture as: waiting for its work setting again.</summary>
    public const string WorkSetLeavesAsWaitingForWorkSetting = "waiting for work setting";

    public static string SerialJobType(int? value) => Lookup(value, SerialJobTypes);

    public static string WorkSetLeavesAs(int? value) => Lookup(value, WorkSetLeavesAsWords);
}
