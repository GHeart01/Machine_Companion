using System.Text.Json.Serialization;

namespace MakinoMCP.Core.Cell;

// 0.6: devices and attention (get_cell_overview, get_attention).

/// <summary>How the cell orders waiting transports (UserSysData). TransportOrder: the sort terms, first decides most
/// (the cell always sorts a hidden "beforehand transport" key first). DestinationOrder: which kind of destination
/// comes first when "destination device" is a term. NotUsed: terms of the vendor's default order this cell has taken
/// out (a cell without "order due date" doesn't schedule by due date). Default: true when the cell stores no rule and
/// runs on the vendor's order.</summary>
public sealed record SchedulingRules(IReadOnlyList<string> TransportOrder, IReadOnlyList<string> DestinationOrder)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? NotUsed { get; init; }

    /// <summary>Within "process sequence": highest or lowest process first, then highest or lowest machining job.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProcessSequence { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Default { get; init; }
}

/// <summary>Where the cell's own files are, as SystemParameters names them (path strings only; MakinoMCP doesn't open
/// them from here; add a folder as a shared folder to read it). Each is left out when the cell has none set.</summary>
public sealed record CellFiles
{
    /// <summary>The automatic data entry (ADE) drop folder, and where rejected files go.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AdeFolder { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AdeErrorFolder { get; init; }

    /// <summary>The file the cell writes its monitoring output to.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MonitoringOutputFile { get; init; }

    /// <summary>Tool type (FTN) and individual tool (ITN) data export: format and folder, when turned on.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ToolExport? FtnExport { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ToolExport? ItnExport { get; init; }
}

public sealed record ToolExport(string Format)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Folder { get; init; }
}

/// <summary>A machine's NC memory as the cell last recorded it: free memory (KB) and free program slots, and how full
/// it is against the machine's configured size (only when the cell has the size set). Matters only when the cell
/// downloads programs "only when needed".</summary>
public sealed record NcMemory
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? FreeKB { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? FreeFiles { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MemoryUsedPercent { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? FilesUsedPercent { get; init; }
}

/// <summary>The tools a lacking-tool fixture's machining program is short of, per program: how many fixtures wait for
/// it, the machines that may run it, and per tool type the count needed, the usable count on each of those machines and
/// in the tool crib. By status only (ready or life warning): the cell's own check also weighs remaining life.
/// Capped: the program needs more short tool types than are listed.</summary>
public sealed record MissingToolGroup(string Program, int Fixtures, IReadOnlyList<string> Machines, IReadOnlyList<MissingTool> Tools)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Capped { get; init; }
}

public sealed record MissingTool(int Ftn, int Needed, IReadOnlyDictionary<string, int> Usable)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? InCrib { get; init; }
}
