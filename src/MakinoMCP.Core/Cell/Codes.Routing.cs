// Code meanings get_part_routing uses: NC program and machining job settings, tool study and tool sets, the cell's
// tool management and offset file settings. Facts about the cell database, read from the installed MAS-A5 software
// and written fresh here.

namespace MakinoMCP.Core.Cell;

public static partial class Codes
{
    // MachineJobs.OptionalBlockSkip (OptionalBlockSkip, flags): the optional block skips set on a machining job.
    private static readonly Dictionary<int, string> BlockSkipBits = new()
    {
        [0x1] = "/2",
        [0x2] = "/3",
        [0x4] = "/4",
        [0x8] = "/5",
        [0x10] = "/6",
        [0x20] = "/7",
        [0x40] = "/8",
        [0x80] = "/9",
    };

    // ONumbers.ProgramType (NCProgramType, flags): what each O-number of an NC program file is.
    private static readonly Dictionary<int, string> NcProgramTypeBits = new()
    {
        [0x1] = "main",
        [0x2] = "private subprogram",
        [0x4] = "shared subprogram",
        [0x8] = "dataserver",
        [0x10] = "M198 by PC",
        [0x20] = "Pro5 data center",
        [0x40] = "dataserver main",
        [0x80] = "resident program",
    };

    // MainPrograms.studyMode (ToolStudyMode): whether the cell measures the program's tool use.
    private static readonly Dictionary<int, string> ToolStudyModes = new()
    {
        [0] = "off",
        [1] = "every run",
        [2] = "one shot",
    };

    // The console's tool set status of a program on a machine (ToolSetStatus, flags).
    private static readonly Dictionary<int, string> ToolSetStatusBits = new()
    {
        [0x1] = "ready",
        [0x2] = "lacking",
        [0x4] = "life warning",
        [0x8] = "alarm",
        [0x10] = "offset error",
        [0x20] = "mismatch",
    };

    // SystemParameters.ToolManagementType (ToolManagementType): how far the cell manages tools.
    private static readonly Dictionary<int, string> ToolManagementTypes = new()
    {
        [0] = "none",
        [1] = "tool data",
        [2] = "tool resource",
    };

    // SystemParameters.ToolResourceManagementType (ToolResourceManagementType): what a tool-resource cell checks.
    private static readonly Dictionary<int, string> ToolResourceManagementTypes = new()
    {
        [0] = "standard",
        [1] = "tool life check",
        [2] = "inform only",
        [3] = "tool life check 2",
        [4] = "tool life check 3",
    };

    // SystemParameters.PTNDownloadType (PTNDownloadType): when program tool numbers are given to the tools.
    private static readonly Dictionary<int, string> PtnDownloadTypes = new()
    {
        [0] = "at tool in/out",
        [1] = "before machining 1",
        [2] = "before machining 2",
        [3] = "at tool in/out (PTN duplication)",
    };

    // SystemParameters.OffsetFileType and OffsetFileTypePriority (OffsetFileType, flags): the levels an offset file
    // can be assigned at.
    private static readonly Dictionary<int, string> OffsetFileTypeBits = new()
    {
        [0x1] = "fixture",
        [0x2] = "fixture process",
        [0x4] = "pallet fixture",
        [0x8] = "process",
    };

    public static IReadOnlyList<string> BlockSkips(int? flags) => FlagList(flags, BlockSkipBits);

    public static IReadOnlyList<string> NcProgramTypes(int? flags) => FlagList(flags, NcProgramTypeBits);

    public static string ToolStudyMode(int? value) => Lookup(value, ToolStudyModes);

    /// <summary>A tool set status as words; 0 is "unknown" (the console can't tell).</summary>
    public static string ToolSetStatus(int? flags) => Flags(flags, ToolSetStatusBits, "unknown");

    public static string ToolManagementType(int? value) => Lookup(value, ToolManagementTypes);

    public static string ToolResourceManagementType(int? value) => Lookup(value, ToolResourceManagementTypes);

    public static string PtnDownloadType(int? value) => Lookup(value, PtnDownloadTypes);

    public static string OffsetFileTypes(int? flags) => Flags(flags, OffsetFileTypeBits, "none");
}
