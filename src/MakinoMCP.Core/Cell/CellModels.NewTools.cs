using System.Text.Json.Serialization;

namespace MakinoMCP.Core.Cell;

// Results of the newer tools: get_macro_values, get_work_set, find_serial.

/// <summary>Macro variable values the cell uploaded from its machines (CommonValues), newest first. Note: why the
/// list is empty (the cell never uploads any, or nothing matched); NotAvailable: what this cell can't show.</summary>
public sealed record MacroValueList(DateTime ServerTime, bool Capped, int Days, IReadOnlyList<MacroValue> Values)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? NotAvailable { get; init; }
}

/// <summary>One uploaded value: when, on which machine, the variable number and its value, with the pallet and the
/// programs the machine reported, and the machining result running on that machine at that time (part, revision,
/// process, program, fixture), matched by time as the vendor's report does.</summary>
public sealed record MacroValue(DateTime Time, string Machine, int Variable, double Value)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Pallet { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MainProgram { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExecutingProgram { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Sequence { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Part { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Revision { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Process { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Program { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Fixture { get; init; }
}

/// <summary>The work-set stations as their monitor shows them: each station's state and, per fixture, what the
/// operator is asked to do.</summary>
public sealed record WorkSetView(DateTime ServerTime, bool Capped, IReadOnlyList<StationWork> Stations)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? NotAvailable { get; init; }
}

/// <summary>One station: status, mode, open (only when closed), the instruction shown while busy with since and
/// plannedFinish, the pallets on it, and its fixtures' instructions (empty when there is nothing to do).</summary>
public sealed record StationWork(string Station, IReadOnlyList<PalletSpot> Pallets, IReadOnlyList<FixtureWork> Fixtures)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Connected { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Status { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Mode { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Open { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Instruction { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? Since { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? PlannedFinish { get; init; }
}

/// <summary>One fixture's instruction at a station, in the monitor's words: Instruction (with quantities), LeavesAs
/// (what the fixture becomes once the operator finishes), the unclamp side (the part coming off: what was done, and
/// how its machining finished) and the clamp side (the part going on).</summary>
public sealed record FixtureWork(int? Pallet, int? Fixture, string Instruction)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LeavesAs { get; init; }

    /// <summary>The raw instruction type and progress when the monitor has no words of its own for the pair.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Detail { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReMachining { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? WaitingForInspectionConfirmation { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? OffDutyProcess { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FixtureName { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FaceComment { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StatusReason { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WorkSide? Unclamp { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WorkSide? Clamp { get; init; }
}

/// <summary>The order, part and process on one side of a work-set instruction, with the comments the monitor shows,
/// the information file (work instructions) and, for the unclamp side, the last program run, the machine and how the
/// machining finished.</summary>
public sealed record WorkSide
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Order { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OrderComment { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Part { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Revision { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PartComment { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Process { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProcessName { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProcessComment { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Job { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? JobComment { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InfoFile { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Quantity { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Quantities { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LastProgram { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Machine { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MachiningFinish { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WorkStatus { get; init; }
}

/// <summary>Where serial numbers containing the searched text were recorded, newest first. Note: why the list is
/// empty (the cell never records serials, or nothing matched).</summary>
public sealed record SerialSearch(DateTime ServerTime, bool Capped, string Serial, int Days, IReadOnlyList<SerialMatch> Matches)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? NotAvailable { get; init; }
}

/// <summary>One record: when the job finished, where, which kind of job, the fixture and operation sequence, the
/// quantity, the serials recorded (only the matching ones are listed), and the result it belongs to (pallet, order,
/// part, revision, process, program), matched by device, finish time and fixture as the vendor's reports do.</summary>
public sealed record SerialMatch(DateTime Time, string Device, string Kind, IReadOnlyList<SerialCode> Serials)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Fixture { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? OperationSequence { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Quantity { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Pallet { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Order { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Part { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Revision { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Process { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Program { get; init; }
}

/// <summary>One serial slot (1-10) with the text recorded and its status, shown as "code N" (meanings unknown) and
/// left out when 0.</summary>
public sealed record SerialCode(int Slot, string Serial)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Status { get; init; }
}
