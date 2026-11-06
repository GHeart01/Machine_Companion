// Code meanings for the tool data (get_tools, get_tool_changes, get_tool_movements). Like Codes.cs: facts about the
// cell database, read from the installed MAS-A5 software and written fresh here.

namespace MakinoMCP.Core.Cell;

public static partial class Codes
{
    // SystemParameters.ToolOutReason (ToolOutReasonType): when the console asks for a reason as a tool is taken out
    // of a machine. Never asked for a tool moved out of a tool crib location.
    private static readonly Dictionary<int, string> ToolOutReasonModes = new()
    {
        [0] = "never asked",
        [1] = "asked only for tools not in alarm (an alarmed tool can't be given a reason)",
        [2] = "always required",
        [3] = "required for tools not in alarm, optional for alarmed tools",
    };

    // FunctionalToolData.DiameterClass (ToolDiameterClass): the low four bits are the size class, the bits above are
    // flags. The software's mapping is used (2 middle, 3 large); an older manual swaps the two.
    private const int ToolSizeMask = 0x0F;

    private static readonly Dictionary<int, string> ToolSizeClasses = new()
    {
        [1] = "small",
        [2] = "middle",
        [3] = "large",
        [4] = "extra large",
        [5] = "fixed",
        [6] = "extra large 2",
        [8] = "empty",
    };

    private static readonly Dictionary<int, string> ToolSizeFlagBits = new()
    {
        [0x10] = "Borematic",
        [0x20] = "APC prohibited",
        [0x40] = "irregular shape",
    };

    // PresetResults.M6Flag: who wrote the tool data change. 1 is the machine's own tool data read back after a tool
    // change or a machining finish (or an operator accepting the machine's values when comparing tool data); 0 is
    // the cell itself (a presetter measurement, a tool-ID read, a screen edit or a tool data import).
    private static readonly Dictionary<int, string> PresetResultSources = new()
    {
        [0] = "cell (presetter, tool-ID read, screen edit or import)",
        [1] = "machine upload",
    };

    public static string ToolOutReasonMode(int? value) => Lookup(value, ToolOutReasonModes);

    /// <summary>A DiameterClass as words: the size class (left out when 0) then its flags, e.g. "large, Borematic";
    /// "" for 0 or NULL. Unknown sizes and bits read "code N".</summary>
    public static string ToolSizeClass(int? value)
    {
        if (value is null or 0) return "";
        var words = new List<string>();
        var size = value.Value & ToolSizeMask;
        if (size != 0) words.Add(Lookup(size, ToolSizeClasses));
        words.AddRange(FlagList(value.Value & ~ToolSizeMask, ToolSizeFlagBits));
        return string.Join(", ", words);
    }

    public static string PresetResultSource(int? value) => Lookup(value, PresetResultSources);
}
