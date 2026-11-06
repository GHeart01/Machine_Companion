using System.Text.Json.Serialization;

namespace MakinoMCP.Core.Cell;

// get_pallets' fixture objects and get_orders' archive. Fields at their default are left out of the JSON.

/// <summary>One fixture of a pallet as the console's pallet tooltip shows it. Label: the fixture's name; for a nameless
/// fixture that can hold one process, "part | revision | process N" (--- for no revision); else its comment.
/// Processes: how many processes the fixture can hold, when more than one. Empty: no work on it. For a fixture with
/// work: order, part, process, job (type and number) and jobStatus. States: the non-default states in the console's
/// order (damaged; machining suspended, work-set hold, pre-machining suspended; operator call; off-duty process;
/// cleaning or deburring skipped), then MakinoMCP's additions (reclamp timeout, lacking tools, clamp-wait hold
/// disabled). OperatorCall: why the fixture stops for the operator (its own setting, or a remaining machining job's).
/// WorkStatus: only scrap (with how the cycle ended) or cancelled, the results still waiting to be unloaded.</summary>
public sealed record FixtureDetail(int? Fixture, string Label)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Processes { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Empty { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Order { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Part { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Process { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Job { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? JobStatus { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? States { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OperatorCall { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WorkStatus { get; init; }
}

/// <summary>A finished or deleted order from the cell's order archive (OrderResults): one row per order, a completed
/// order that was later deleted shown once with Deleted. Recorded: when the archive row was written (completion or
/// deletion). Quantities as the order had them then; 0 left out.</summary>
public sealed record ArchivedOrder(string Name, string Part)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Quantity { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CompletedQty { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ScrapQty { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? Completed { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Deleted { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? Recorded { get; init; }
}
