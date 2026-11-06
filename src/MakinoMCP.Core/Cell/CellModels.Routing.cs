using System.Text.Json.Serialization;

namespace MakinoMCP.Core.Cell;

// get_part_routing additions: tool study per program and the process diagnosis (blockers).

/// <summary>The program's tool study (MainPrograms): Mode off, every run or one shot; Studied when the cell last
/// measured the program's tool use, on which machine and pallet. Note "not studied" when it never was and the program's
/// tools carry no cutting values; with Mode off, cutting values may have been typed in.</summary>
public sealed record ProgramStudy(string Mode)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? Studied { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StudiedOn { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? StudiedPallet { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; init; }
}

/// <summary>Why the part's processes may not be running, as the console's Process Diagnosis would show it. Reasons:
/// what blocks it, most basic first (no order, no fixture, no pallet, no machine, every place or machine held), or one
/// line saying nothing was found. Orders: the part's active orders with their status (OtherOrders counts the finished
/// ones). Devices: the state of each machine and station the part's jobs are assigned to. NotChecked: what the console
/// also checks but the cell keeps only in memory, so "nothing found" doesn't rule those out.</summary>
public sealed record RoutingBlockers(IReadOnlyList<string> Reasons, IReadOnlyList<string> NotChecked)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Orders { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? OtherOrders { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? Devices { get; init; }
}
