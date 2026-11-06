using Microsoft.Data.SqlClient;

namespace MakinoMCP.Core.Cell;

// Attention: what waits on a person. Devices (MachineStatus, WorkSetStatus, the vehicle: recovery request, error,
// emergency stop, disconnected, a machine's operator call), instructions set at a work-set station
// (WorkSetCommand), manual transports not finished (ManualTransport), fixtures that are damaged, suspended, on hold or
// lacking tools (PalletFixtures with the current order from FixtureRun), tools in alarm on each machine, and stations or
// the vehicle past their maintenance cycle count (DeviceParameters.WarningCycleCount). A section whose table this cell
// or login lacks is named in NotAvailable instead.
public sealed partial class SqlMakinoReader
{
    // Severity ranks: lower comes first.
    private const int RankDevice = 0;
    private const int RankWorkSet = 1;
    private const int RankTransport = 2;
    private const int RankFixtureBlocked = 3;
    private const int RankTools = 4;
    private const int RankLackingTools = 5;
    private const int RankClosed = 6;      // closing is often deliberate
    private const int RankMaintenance = 7;
    private const int RankParked = 8;

    // Code values compared against (meanings and words are in Codes).
    private const int DashDown = 0;               // DeviceDashStatus: down
    private const int DashError = 4;              // DeviceDashStatus: error
    private const int ReclampTimeoutAlarm = 1;    // ReclampStatus
    private const int ReclampTimeoutWarning = 2;  // ReclampStatus
    private const int ToolAlarm = 4;              // ToolStatus (derived): alarm
    private const int ToolOffsetError = 3;        // ToolStatus: offset error
    private const int NoWorkSetOperation = 0;     // WorkSetStationOperationType: nothing
    private const int ClampOperation = 1;         // WorkSetStationOperationType: clamp
    private const int UnclampCancelled = 4;       // WorkSetStationOperationStatus: unclamp cancelled
    private const string ClosedTransports = "4, 5"; // ManualTransportStatus: finished, cancelled

    internal sealed record AttentionDevice(string Name, int Type)
    {
        public bool? Connected { get; init; }
        public bool? Emergency { get; init; }
        public bool? Recovery { get; init; }
        public int? Status { get; init; }
        public int? OperatorCall { get; init; }
        public int? ToolStatus { get; init; }
        public int OpenAlarms { get; init; }
        public int? Cycles { get; init; }
        public int? CycleLimit { get; init; }
        public bool? Open { get; init; }
        public bool? Skip { get; init; }
        public int? TablePallet { get; init; }
        public int? Action { get; init; }
        public int? SubAction { get; init; }
        public int? Command { get; init; }
        public string From { get; init; } = "";
        public string To { get; init; } = "";
        public int Id { get; init; }
        public bool? SystemMode { get; init; }
        public bool? TransProhibit { get; init; }
        public int PmcAlarms { get; init; }
        public int AllAlarms { get; init; }
        public int? CncRun { get; init; }
        public int? HelpRequest { get; init; }
        public int? OpeTimeout { get; init; }
        public int? ExtraToolPallet { get; init; }
        public bool ExtraToolPalletOutside { get; init; }
        public bool ExtraToolPalletTransport { get; init; }
        public int? TableStatus { get; init; }
        public bool TableReserved { get; init; }
        public bool? ApcDirect { get; init; }
        public bool VirtualStation { get; init; }
        public int? ManualTransportSendCommand { get; init; }
    }

    internal sealed record AttentionFixture(int? Pallet, int? Fixture)
    {
        public bool Suspend { get; init; }
        public bool Hold { get; init; }
        public bool Damaged { get; init; }
        public bool PreMcSuspend { get; init; }
        public int? PreMcReason { get; init; }
        public bool LackingTool { get; init; }
        public int? Reclamp { get; init; }
        public string Reason { get; init; } = "";
        public string? Order { get; init; }
        public int? Process { get; init; }
    }

    internal sealed record AttentionCommand(string Device, int? Pallet, int? Fixture, int? Operation, int? Status, string? Order, int? Quantity)
    {
        public bool ReMachine { get; init; }
        public int? ReMachineJob { get; init; }
        public string? ReMachineProgram { get; init; }
        public bool InspectionCheck { get; init; }
        public bool InspectionConfirmed { get; init; }
        public int? OperatorCallStatus { get; init; }
    }

    /// <summary>DestDevice: the destination device's name when it is one device (null for "any work-set station").</summary>
    internal sealed record AttentionTransport(int? Pallet, string To, int? Status, DateTime? Since)
    {
        public int? Type { get; init; }
        public int? DestType { get; init; }
        public string? DestDevice { get; init; }
    }

    internal sealed record AttentionTools(string Device, int Alarm, int OffsetError);

    /// <summary>What the cell holds for get_attention. FixtureCounts: the cell's count per fixture condition (all
    /// fixtures, past the row cap); null → counted from <see cref="Fixtures"/>.</summary>
    internal sealed record AttentionRows(
        IReadOnlyList<AttentionDevice> Devices, IReadOnlyList<AttentionFixture> Fixtures, IReadOnlyList<AttentionCommand> Commands,
        IReadOnlyList<AttentionTransport> Transports, IReadOnlyList<AttentionTools> Tools)
    {
        public IReadOnlyDictionary<string, int>? FixtureCounts { get; init; }

        /// <summary>The parked fixtures grouped by place and condition (see <see cref="AttentionParkedSql"/>).</summary>
        public IReadOnlyList<ParkedGroup>? Parked { get; init; }

        /// <summary>The cell's number of work-set instructions and open manual transports (past the row cap); null →
        /// counted from <see cref="Commands"/> / <see cref="Transports"/>.</summary>
        public int? CommandTotal { get; init; }

        public int? TransportTotal { get; init; }

        /// <summary>True when the cell raises a machine recovery for missing tools (LackingToolsMachineRecovery set,
        /// or tool resource management that only informs).</summary>
        public bool LackingToolRecovery { get; init; }

        /// <summary>The waiting fixtures sorted into blocked work, holds and pallet holds; null when not read.</summary>
        public WaitingAnalysis? Waiting { get; init; }

        public AttentionSettings Settings { get; init; } = new();

        public IReadOnlyList<MissingToolGroup>? MissingTools { get; init; }
    }

    /// <summary>The words for the all-closed roll-up; the database keeps no record of why, so the cause is a guess.</summary>
    internal const string AllClosed = "all devices closed";

    internal const string AllClosedDetail =
        "as after a software start, Stop System or Work Data Reset: check that the pallets are where the console shows, " +
        "then open the devices";

    /// <summary>A recovery item's detail: the open alarms and what the device was doing, so the operator knows where
    /// the pallet should be before choosing Retry or Normal Finish at the console.</summary>
    internal static string? RecoveryContext(AttentionDevice d, IReadOnlyList<AttentionCommand> commands)
    {
        var parts = new List<string>();
        if (d.OpenAlarms > 0) parts.Add($"{d.OpenAlarms} open alarm{(d.OpenAlarms == 1 ? "" : "s")}");
        if (d.Recovery != true) return parts.Count > 0 ? string.Join(", ", parts) : null;
        if (d.Type == MachineType)
        {
            var action = d.Action is > 0 ? Codes.MachineAction(d.Action) : "";
            if (d.SubAction is > 0) action = Join(action, Codes.MachineSubAction(d.SubAction));
            if (action.Length > 0) parts.Add($"was in {action}");
            if (d.TablePallet is > 0) parts.Add($"table pallet {d.TablePallet}");
        }
        else if (d.Type == VehicleType)
        {
            if (d.From.Length > 0 || d.To.Length > 0) parts.Add($"transport {Or(d.From, "?")} → {Or(d.To, "?")}");
            if (d.Command is > 0) parts.Add(Codes.VehicleCommand(d.Command));
        }
        else if (d.Type == StationType)
        {
            var pending = commands.Where(c => c.Device == d.Name).Select(c => Codes.WorkSetCommandOperation(c.Operation)).Distinct().ToList();
            if (pending.Count > 0) parts.Add($"instruction {string.Join(", ", pending)}");
        }
        return parts.Count > 0 ? string.Join(", ", parts) : null;
    }

    /// <summary>Parked: suspended or on hold, and nothing else wrong with it (lacking tools aside).</summary>
    private static bool IsParked(AttentionFixture f) =>
        (f.Suspend || f.Hold) && !f.Damaged && !f.PreMcSuspend && f.Reclamp is null or 0;

    // Code values compared against in the device checks (meanings and words are in Codes).
    private const int PreparationAction = 2;      // MachineAction: preparation
    private const int CncHold = 2;                // CNCRunStatus: hold
    private const int OtherOperation = 6;         // WorkSetStationOperationType: other
    private const int InputOperation = 7;         // WorkSetStationOperationType: input
    private const int OutputOperation = 8;        // WorkSetStationOperationType: output
    private const int TransportWaiting = 1;       // ManualTransportStatus: waiting for transport
    private const int TransportToFinish = 3;      // ManualTransportStatus: waiting to finish
    private const int ExtraToolPalletTransportType = 6; // ManualTransportType
    private const int PalletChangeTransportType = 7;    // ManualTransportType
    private const int ReclampTransportType = 10;        // ManualTransportType
    private const int WarmUpTransportType = 14;         // ManualTransportType
    private const int ManualTransAvailWss = 1;          // ManualTransAvailType: work-set station must be closed

    internal const string ProbePartCountNote =
        "the cell lowers a fixture's part count from its probe at machining (ProbePartCountMacroVariable), so fewer parts " +
        "to unclamp than were clamped is by design";

    internal const string AllFixtureToolsNote =
        "the cell machines a pallet only when every fixture on it has its tools (AllFixtureTsReady); pallet holds here " +
        "are by each fixture's lacking-tools flag, while the cell's own per-machine tool-set check is kept in memory";

    /// <summary>The items, most severe first (then newest first where the cell says since when), capped; the counts
    /// and each machine's tool status as context.</summary>
    internal static AttentionList BuildAttention(
        DateTime now, int cap, AttentionRows rows, IReadOnlyList<string> notAvailable, bool includeParked)
    {
        var items = new List<(int Rank, AttentionItem Item)>();
        var counts = new Dictionary<string, int>();
        void Count(string key, int n = 1)
        {
            if (n > 0) counts[key] = counts.GetValueOrDefault(key) + n;
        }
        void Total(string key, int n)
        {
            if (n > 0) counts[key] = n;
            else counts.Remove(key);
        }
        var settings = rows.Settings;
        var byName = new Dictionary<string, AttentionDevice>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in rows.Devices) byName.TryAdd(d.Name, d);

        var toolStatus = new Dictionary<string, string>();
        // Pallets whose fixtures lack tools: a machine recovery with such a pallet on the table is a lacking-tool recovery.
        var lackingPallets = rows.Fixtures.Where(f => f.LackingTool && f.Pallet is not null).Select(f => f.Pallet!.Value).ToHashSet();
        // Devices that can be opened and closed; when every one is closed, one roll-up item replaces theirs.
        var openable = rows.Devices.Where(d => d.Open is not null).ToList();
        var allClosed = openable.Count > 1 && openable.All(d => d.Open == false);
        // Where pallets are on their way to: a reserved position with no pallet is fine while one heads there.
        var heading = rows.Devices.Where(d => d.Type == VehicleType && d.To.Length > 0).Select(d => d.To)
            .Concat(rows.Transports.Select(t => t.DestDevice).OfType<string>())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var d in rows.Devices)
        {
            var what = new List<string>();
            var lackingRecovery = d.Recovery == true && d.Type == MachineType && rows.LackingToolRecovery
                                  && d.TablePallet is { } table && lackingPallets.Contains(table);
            if (lackingRecovery) what.Add("lacking-tool recovery request");
            else if (d.Recovery == true) what.Add("recovery request");
            // The emergency flag is stale or a placeholder while the device is disconnected.
            if (d.Emergency == true && d.Connected != false) what.Add("emergency stop");
            if (d.Connected == false) what.Add("disconnected");
            else if (d.Status == DashDown) what.Add(Codes.DeviceDashStatus(d.Status));
            if (d.Status == DashError) what.Add(Codes.DeviceDashStatus(d.Status));
            // A lacking-tool recovery counts as a recovery request; its own words stay on the item.
            foreach (var w in what) Count(w == "lacking-tool recovery request" ? "recovery request" : w);
            if (d.Type == MachineType && d.OperatorCall is > 0)
            {
                what.Add($"operator call ({Codes.OperatorCall(d.OperatorCall)})");
                Count("operator call");
            }
            // A machine waiting on a person mid-preparation, or held mid-cycle; a recovery request says more.
            if (d.Type == MachineType && d.Recovery != true && d.Connected != false)
            {
                if (d.Action == PreparationAction && Codes.MachineOperatorWait(d.SubAction) is { } wait)
                {
                    what.Add(wait);
                    Count("machine waiting on the operator");
                }
                else if (d.Action == MachiningAction && d.CncRun == CncHold && d.OperatorCall is not > 0)
                {
                    what.Add("feed hold (the dashboard still reads busy)");
                    Count("feed hold");
                }
            }
            if (d.Type == StationType && d.Connected != false)
            {
                if (d.HelpRequest is > 0) what.Add(Codes.WssHelpRequest(d.HelpRequest));
                if (d.OpeTimeout is > 0) what.Add(Codes.WssOperationTimeout(d.OpeTimeout));
            }
            if (what.Count > 0)
            {
                var detail = lackingRecovery
                    ? Join(RecoveryContext(d, rows.Commands) ?? "",
                        "the machine holds the pallet until the missing tools are loaded, then Retry at the console")
                    : RecoveryContext(d, rows.Commands);
                items.Add((RankDevice, new AttentionItem("device", string.Join(", ", what)) { Device = d.Name, Detail = detail }));
            }
            // The machine waits for an extra tool pallet that is outside the cell or still on a manual transport.
            if (d.Type == MachineType && settings.ExtraToolPalletTransport && d.ExtraToolPallet is > 0
                && (d.ExtraToolPalletOutside || d.ExtraToolPalletTransport))
            {
                items.Add((RankTransport, new AttentionItem("device", $"waiting for extra tool pallet {d.ExtraToolPallet}")
                {
                    Device = d.Name, Pallet = d.ExtraToolPallet,
                    Detail = d.ExtraToolPalletOutside
                        ? "the pallet is outside the cell: bring it in with a manual transport"
                        : "its manual transport isn't finished",
                }));
                Count("waiting for an extra tool pallet");
            }
            if (PositionStatusError(d, heading))
            {
                items.Add((RankFixtureBlocked, new AttentionItem("device", "position status error (possibly)")
                {
                    Device = d.Name,
                    Detail = $"the table holds no pallet but reads \"{Codes.DevicePositionStatus(d.TableStatus)}\"; " +
                             "check it in the console's Device Diagnosis",
                }));
                Count("position status error (possibly)");
            }
            // Closed or skipped devices get no automatic work; the dashboard status still reads idle.
            if (d.Open == false)
            {
                Count("closed");
                if (!allClosed && d.Connected != false)
                {
                    items.Add((RankClosed, new AttentionItem("device", "closed")
                    {
                        Device = d.Name, Detail = "gets no automatic work until it is opened at the console",
                    }));
                }
            }
            if (d.Skip == true)
            {
                Count("skipped");
                items.Add((RankClosed, new AttentionItem("device", "skipped") { Device = d.Name, Detail = "the cell sends it no work" }));
            }
            if (d.Type == MachineType && d.ToolStatus is not null) toolStatus.TryAdd(d.Name, Codes.MachineToolStatus(d.ToolStatus));
            if (d.CycleLimit is > 0 && d.Cycles >= d.CycleLimit)
            {
                const string over = "cycle count over its maintenance limit";
                items.Add((RankMaintenance, new AttentionItem("maintenance", over)
                {
                    Device = d.Name, Detail = $"{d.Cycles} cycles, limit {d.CycleLimit}",
                }));
                Count(over);
            }
        }

        // A station's other/input/output instruction and the manual transport of the same pallet are one item.
        var wholePallet = rows.Commands.Where(c => c.Operation is OtherOperation or InputOperation or OutputOperation && c.Pallet is not null)
            .Select(c => c.Pallet!.Value).ToHashSet();
        var mergedTransports = new Dictionary<int, string>();
        foreach (var t in rows.Transports)
        {
            var detail = TransportDetail(t, byName, settings);
            if (t.Pallet is { } p && wholePallet.Contains(p))
            {
                mergedTransports.TryAdd(p, Join($"manual transport {Codes.ManualTransportStatus(t.Status)}", detail ?? ""));
                Count("manual transports open");
                continue;
            }
            items.Add((RankTransport, new AttentionItem("manual transport", Codes.ManualTransportStatus(t.Status))
            {
                Pallet = t.Pallet, Detail = detail, Since = t.Since,
            }));
            Count("manual transports open");
        }
        if (rows.TransportTotal is { } transportTotal) Total("manual transports open", transportTotal);

        foreach (var c in rows.Commands)
        {
            var detail = new List<string>();
            // WorkSetCommand.OperationStatus is the same list as WorkSetResults.OperationStatus (the console reads both
            // as WorkSetStationOperationStatus): how far the operator has got with the instruction.
            if (c.Status is > 0) detail.Add($"status {Codes.WorkSetOperation(c.Status)}");
            if (c.Quantity is > 0) detail.Add($"quantity {c.Quantity}");
            detail.AddRange(CommandNotes(c, settings));
            if (c.Pallet is { } p && mergedTransports.TryGetValue(p, out var transport)) detail.Add(transport);
            if (byName.TryGetValue(c.Device, out var station) && station.VirtualStation)
                detail.Add("finished on the console (a virtual station)");
            items.Add((RankWorkSet, new AttentionItem("work set", Codes.WorkSetCommandOperation(c.Operation))
            {
                Device = c.Device, Pallet = c.Pallet, Fixture = c.Fixture, Order = c.Order,
                Detail = detail.Count > 0 ? string.Join(", ", detail) : null,
            }));
            Count("work-set instructions");
        }
        if (rows.CommandTotal is { } commandTotal) Total("work-set instructions", commandTotal);

        foreach (var t in rows.Tools)
        {
            var what = new List<string>();
            if (t.Alarm > 0) what.Add($"{t.Alarm} tool{(t.Alarm == 1 ? "" : "s")} in alarm");
            if (t.OffsetError > 0)
                what.Add(t.Alarm > 0 ? $"{t.OffsetError} with an offset error" : $"{t.OffsetError} tool{(t.OffsetError == 1 ? "" : "s")} with an offset error");
            if (what.Count == 0) continue;
            items.Add((RankTools, new AttentionItem("tools", string.Join(", ", what))
            {
                Device = t.Device,
                Detail = toolStatus.TryGetValue(t.Device, out var status) ? $"machine tool status: {status}" : null,
            }));
            Count("tools in alarm on machines", t.Alarm);
            Count("tools with an offset error on machines", t.OffsetError);
        }

        // Fixtures the cell's rules hold (order machining suspend, off duty): parked-like, counted with the parked ones.
        var holds = rows.Waiting?.Holds ?? [];
        var holdOf = new Dictionary<(int?, int?), AttentionHold>();
        foreach (var h in holds)
        {
            holdOf.TryAdd((h.Pallet, h.Fixture), h);
            Count(h.Condition);
        }
        var holdItems = new HashSet<(int?, int?)>();
        foreach (var f in rows.Fixtures)
        {
            var conditions = new List<(string Words, string Key, int Rank)>();
            var hold = holdOf.GetValueOrDefault((f.Pallet, f.Fixture));
            if (f.Damaged) conditions.Add(("damaged", "damaged", RankFixtureBlocked));
            if (f.PreMcSuspend)
            {
                var words = f.PreMcReason is > 0 ? $"pre-machining suspended ({Codes.PreMcSuspendReason(f.PreMcReason)})" : "pre-machining suspended";
                conditions.Add((words, "pre-machining suspended", RankFixtureBlocked));
            }
            if (f.Reclamp is > 0)
            {
                var words = $"reclamp {Codes.ReclampStatus(f.Reclamp)}";
                conditions.Add((words, words, f.Reclamp == ReclampTimeoutAlarm ? RankFixtureBlocked : RankMaintenance));
            }
            // A suspended or held fixture won't run, so its missing tools wait with it.
            if (f.LackingTool)
                conditions.Add(("lacking tools", "lacking tools", f.Suspend || f.Hold || hold is not null ? RankParked : RankLackingTools));
            if (f.Hold) conditions.Add(("work-set hold", "work-set hold", RankParked));
            if (f.Suspend) conditions.Add(("machining suspended", "machining suspended", RankParked));
            if (conditions.Count == 0) continue;
            if (rows.FixtureCounts is null) foreach (var c in conditions) Count(c.Key);
            var parkedLike = IsParked(f) || (hold is not null && conditions.All(c => c.Rank == RankParked));
            if (hold is not null)
            {
                conditions.Add((hold.Words, hold.Condition, RankParked));
                holdItems.Add((f.Pallet, f.Fixture));
            }
            if (!includeParked && parkedLike) continue; // counted in Parked instead
            items.Add((conditions.Min(c => c.Rank), new AttentionItem("fixture", string.Join(", ", conditions.Select(c => c.Words)))
            {
                Pallet = f.Pallet, Fixture = f.Fixture, Order = f.Order, Process = f.Process, Detail = NonEmpty(OneLine(f.Reason)),
            }));
        }
        if (includeParked)
        {
            foreach (var h in holds.Where(h => !holdItems.Contains((h.Pallet, h.Fixture))))
                items.Add((RankParked, new AttentionItem("fixture", h.Words) { Pallet = h.Pallet, Fixture = h.Fixture, Order = h.Order }));
        }
        if (rows.FixtureCounts is not null)
        {
            foreach (var (key, n) in rows.FixtureCounts) Count(key, n);
        }

        // Waiting work no feasible device can take, grouped; and pallets held because one fixture lacks tools.
        foreach (var b in rows.Waiting?.Blocked ?? [])
        {
            items.Add((RankLackingTools, new AttentionItem("waiting work", $"{b.What} blocked: no feasible device can take it")
            {
                Detail = b.Devices, Fixtures = b.Fixtures, Pallets = FewPallets(b.Pallets),
            }));
            Count("waiting work blocked", b.Fixtures);
        }
        foreach (var h in rows.Waiting?.PalletHolds ?? [])
        {
            items.Add((RankLackingTools, new AttentionItem("pallet", "pallet held: a fixture lacks tools")
            {
                Pallet = h.Pallet,
                Detail = $"fixture {string.Join(", ", h.Lacking)} lacks tools and holds back fixture {string.Join(", ", h.HeldBack)} " +
                         "(the cell machines the pallet only when every fixture has its tools)",
            }));
            Count("pallets held for missing tools");
        }
        if (allClosed) items.Add((RankClosed, new AttentionItem("cell", AllClosed) { Detail = AllClosedDetail }));

        var sorted = items
            .OrderBy(i => i.Rank)
            .ThenByDescending(i => i.Item.Since.HasValue)
            .ThenByDescending(i => i.Item.Since)
            .Select(i => i.Item)
            .ToList();
        var capped = sorted.Count > cap;
        var groups = WithHolds(rows.Parked ?? [], holds);
        var parked = groups.Count > 0
            ? new ParkedFixtures(groups.Sum(g => g.Fixtures), groups)
            {
                LackingTools = groups.Sum(g => g.LackingTools ?? 0) is > 0 and var lacking ? lacking : null,
                Blocking = groups.Sum(g => g.Blocking ?? 0) is > 0 and var blocking ? blocking : null,
            }
            : null;
        var notes = new List<string>();
        if (settings.ProbePartCount > 0) notes.Add(ProbePartCountNote);
        if (settings.AllFixtureTsReady && settings.ToolsHoldWork) notes.Add(AllFixtureToolsNote);
        return new AttentionList(now, capped, counts, capped ? sorted.GetRange(0, cap) : sorted, notAvailable)
        {
            MachineToolStatus = toolStatus.Count > 0 ? toolStatus : null,
            Parked = parked,
            MissingTools = rows.MissingTools,
            Notes = notes.Count > 0 ? notes : null,
        };
    }

    /// <summary>A table position that reads busy with no pallet on it, by the console's Device Diagnosis rule for
    /// direct-table machines and work-set stations: its state is neither empty nor finished, and it isn't reserved for
    /// a pallet on its way (none reserved and no transport heading to the device). The console also compares the
    /// vehicle's final destination, kept in memory, so this is only "possibly".</summary>
    internal static bool PositionStatusError(AttentionDevice d, IReadOnlySet<string> heading) =>
        d.Connected != false && (d.Type == StationType || (d.Type == MachineType && d.ApcDirect == true))
        && d.TablePallet is not > 0 && d.TableStatus is { } s && s != PositionEmpty && s != PositionFinished
        && (s != PositionReserved || (!d.TableReserved && !heading.Contains(d.Name)));

    /// <summary>What a station instruction waits for beyond its status: a whole-pallet operation, an inspection
    /// confirmation, re-machining.</summary>
    internal static List<string> CommandNotes(AttentionCommand c, AttentionSettings s)
    {
        var notes = new List<string>();
        switch (c.Operation)
        {
            case OtherOperation:
                notes.Add("pallet brought by manual transport or fixture change: no clamping expected; the operator presses FINISH when done");
                break;
            case InputOperation:
                notes.Add("the whole pallet is loaded into the cell by hand");
                break;
            case OutputOperation:
                notes.Add("the whole pallet is taken out of the cell by hand");
                break;
        }
        // Unclamp operations with an inspection mark (or a scrap unclamp) wait for the operator to confirm the inspection.
        if (s.InspectionConfirmation && c.Operation is 2 or 3 or 4 or 5 && (c.InspectionCheck || c.Operation is 3 or 5) && !c.InspectionConfirmed)
            notes.Add("waiting for inspection confirmation at the station");
        if (c.ReMachine)
        {
            var from = c.ReMachineJob is > 0 ? $"re-machining from job {c.ReMachineJob}" : "re-machining";
            notes.Add(c.ReMachineProgram is { } program ? $"{from} ({program})" : from);
            if (c.OperatorCallStatus == 3) notes.Add("the machine will wait for an operator to start it");
        }
        return notes;
    }

    /// <summary>A manual transport's detail: its kind, destination, and what it waits for (the destination's open or
    /// closed state the cell requires, or who finishes it).</summary>
    internal static string? TransportDetail(AttentionTransport t, IReadOnlyDictionary<string, AttentionDevice> devices, AttentionSettings s)
    {
        var parts = new List<string>();
        if (t.Type is > 1) parts.Add(Codes.ManualTransportType(t.Type)); // undefined and standard say nothing
        if (t.To.Length > 0) parts.Add($"to {t.To}");
        var dest = t.DestDevice is { } name ? devices.GetValueOrDefault(name) : null;
        if (t.Status == TransportWaiting)
        {
            parts.Add("not picked up yet (can still be cancelled)");
            if (dest is not null && t.Type != WarmUpTransportType && WaitsFor(t, dest, s) is { } waits) parts.Add(waits);
        }
        else if (t.Status == TransportToFinish)
        {
            if (t.DestType == StationType)
                parts.Add(dest?.ManualTransportSendCommand == 0 || dest?.VirtualStation == true
                    ? "Complete it on the console's Manual Transport screen"
                    : "the operator presses FINISH at the station");
            else if (t.DestType == MachineType) parts.Add("Complete it on the console's Manual Transport screen");
        }
        return parts.Count > 0 ? string.Join(", ", parts) : null;
    }

    /// <summary>Whether a waiting manual transport waits for its destination to be opened or closed: a machine takes a
    /// manual transport only closed (an extra tool pallet either way, a pallet change only open); a station follows
    /// the cell's ManualTransAvailType (closed when its work-set station bit is set, else open; a reclamp always open).</summary>
    private static string? WaitsFor(AttentionTransport t, AttentionDevice dest, AttentionSettings s)
    {
        bool? mustBeOpen = null;
        var why = "";
        if (dest.Type == MachineType)
        {
            if (t.Type == ExtraToolPalletTransportType) return null;
            (mustBeOpen, why) = t.Type == PalletChangeTransportType
                ? (true, "a pallet change needs the machine open")
                : (false, "a manual transport goes only to a closed machine");
        }
        else if (dest.Type == StationType)
        {
            if (t.Type == ReclampTransportType) (mustBeOpen, why) = (true, "a reclamp needs the station open");
            else if (s.ManualTransAvailType is { } avail)
                (mustBeOpen, why) = (avail & ManualTransAvailWss) != 0
                    ? (false, "the cell sends manual transports only to a closed station")
                    : (true, "the cell sends manual transports only to an open station");
        }
        if (mustBeOpen is null || dest.Open is null) return null;
        if (mustBeOpen == true && dest.Open == false) return $"waits for {dest.Name} to be opened ({why})";
        if (mustBeOpen == false && dest.Open == true) return $"waits for {dest.Name} to be closed ({why})";
        return null;
    }

    /// <summary>The parked groups with the fixtures the cell's rules hold added, per place and condition.</summary>
    internal static List<ParkedGroup> WithHolds(IReadOnlyList<ParkedGroup> parked, IReadOnlyList<AttentionHold> holds)
    {
        var groups = parked.ToList();
        foreach (var g in holds.GroupBy(h => (h.Place, h.Condition)))
        {
            var lacking = g.Count(h => h.LackingTool);
            // A hold is on a job that waits now, so each one is blocking.
            groups.Add(new ParkedGroup(g.Key.Place, g.Key.Condition, g.Count()) { LackingTools = lacking > 0 ? lacking : null, Blocking = g.Count() });
        }
        return groups.OrderByDescending(g => g.Fixtures).ToList();
    }

    /// <summary>Rows of <see cref="AttentionParkedSql"/> (pallet place type and number, suspended, held, fixtures,
    /// lacking tools) as groups per place and condition, biggest first. A place with a device row is that device's
    /// name; any other (the stocker shelves) its device type's words, so all shelves are one place.</summary>
    internal static List<ParkedGroup> MergeParked(
        IEnumerable<(int? Type, int? Number, bool Suspended, bool Held, int N, int Lacking)> rows,
        IReadOnlyList<(int Type, int Number, string Name)> devices) =>
        MergeParked(rows.Select(r => (r.Type, r.Number, r.Suspended, r.Held, r.N, r.Lacking, 0, 0)), devices);

    /// <summary>The same, with how many of each group hold back a waiting job (Blocking) and, for work-set holds, still
    /// hold parts (WithParts).</summary>
    internal static List<ParkedGroup> MergeParked(
        IEnumerable<(int? Type, int? Number, bool Suspended, bool Held, int N, int Lacking, int Blocking, int WithParts)> rows,
        IReadOnlyList<(int Type, int Number, string Name)> devices)
    {
        var merged = new List<(string Place, string Condition, int N, int Lacking, int Blocking, int WithParts)>();
        foreach (var r in rows)
        {
            var place = PlaceName(r.Type, r.Number, devices);
            var condition = string.Join(", ", new[] { r.Suspended ? "machining suspended" : null, r.Held ? "work-set hold" : null }.OfType<string>());
            var i = merged.FindIndex(m => m.Place == place && m.Condition == condition);
            if (i < 0) merged.Add((place, condition, r.N, r.Lacking, r.Blocking, r.WithParts));
            else
            {
                var m = merged[i];
                merged[i] = (place, condition, m.N + r.N, m.Lacking + r.Lacking, m.Blocking + r.Blocking, m.WithParts + r.WithParts);
            }
        }
        static int? Some(int n) => n > 0 ? n : null;
        return merged
            .OrderByDescending(m => m.N)
            .Select(m => new ParkedGroup(m.Place, m.Condition, m.N)
            {
                LackingTools = Some(m.Lacking), Blocking = Some(m.Blocking), WithParts = Some(m.WithParts),
            })
            .ToList();
    }

    // Columns whose absence leaves part of a section empty, and the words notAvailable uses for them.
    private static readonly (string Table, string Column, string Words)[] AttentionColumns =
    [
        ("MachineStatus", "Connected", "machine connection"), ("MachineStatus", "Emergency", "machine emergency stop"),
        ("MachineStatus", "Status", "machine error state"), ("MachineStatus", "Recovery", "machine recovery requests"),
        ("MachineStatus", "OperatorCall", "machine operator calls"), ("MachineStatus", "ToolStatus", "machine tool status"),
        ("WorkSetStatus", "Connected", "work-set station connection"), ("WorkSetStatus", "Emergency", "work-set station emergency stop"),
        ("WorkSetStatus", "Status", "work-set station error state"), ("WorkSetStatus", "Recovery", "work-set station recovery requests"),
        ("WorkSetStatus", "TotalCycleCount", "work-set station cycle counts"),
    ];

    private static readonly (string Column, string Words)[] VehicleColumns =
    [
        ("Connected", "vehicle connection"), ("Emergency", "vehicle emergency stop"), ("Status", "vehicle error state"),
        ("Recovery", "vehicle recovery request"), ("TotalCycleCount", "vehicle cycle count"),
    ];

    private static readonly (string Column, string Words)[] FixtureColumns =
    [
        ("Suspend", "suspended fixtures"), ("WorkSetHold", "fixtures on hold"), ("Damaged", "damaged fixtures"),
        ("PreMcSuspend", "pre-machining suspended fixtures"), ("LackingTool", "fixtures lacking tools"),
        ("ReclampStatus", "reclamp timeouts"),
    ];

    /// <summary>What get_attention can't show because a table it reads lacks a column (a missing table is reported
    /// by the section itself).</summary>
    internal static List<string> AttentionColumnsMissing(SchemaInfo schema)
    {
        var missing = AttentionColumns.Where(c => schema.HasTable(c.Table) && !schema.Has(c.Table, c.Column)).Select(c => c.Words).ToList();
        var vehicle = schema.HasTable("VehicleStatus") ? "VehicleStatus" : schema.HasTable("ViewVehicleStatus") ? "ViewVehicleStatus" : null;
        if (vehicle is not null) missing.AddRange(VehicleColumns.Where(c => !schema.Has(vehicle, c.Column)).Select(c => c.Words));
        if (schema.HasTable("PalletFixtures"))
        {
            var fixtures = FixtureColumns.Where(c => !schema.Has("PalletFixtures", c.Column)).Select(c => c.Words).ToList();
            if (fixtures.Count == FixtureColumns.Length) missing.Add("fixtures (suspended, damaged, lacking tools, …)");
            else missing.AddRange(fixtures);
        }
        if (schema.HasTable(Itd) && DerivedStatusSql(schema) == "0") missing.Add("tools in alarm");
        return missing;
    }

    /// <summary>notAvailable's words when the cell's settings leave PalletFixtures.LackingTool meaningless.</summary>
    internal const string LackingToolsNotComputed =
        "fixtures lacking tools (the cell doesn't work them out: its FixtureToolLackInfo setting is off)";

    internal const string LackingToolsSettingUnknown =
        "whether the cell works out fixtures lacking tools (its settings can't be read, so none may only mean it doesn't)";

    /// <summary>Whether the cell marks fixtures lacking tools (SystemParameters FixtureToolLackInfo 1 or 2; 0 or no
    /// row is the vendor's default, off, and then the flag is cleared, so "none" would be a false all-clear) and,
    /// when it does, which jobs it checks (CheckLackToolsAllRemainingJobs, default 0). Null settings: this login
    /// can't read them, so neither is known.</summary>
    internal static (bool? Computed, string? Scope) LackingToolSetting(IReadOnlyDictionary<string, string>? settings)
    {
        if (settings is null) return (null, null);
        if (ToInt(settings.GetValueOrDefault("FixtureToolLackInfo")) is not (1 or 2)) return (false, null);
        var scope = ToInt(settings.GetValueOrDefault("CheckLackToolsAllRemainingJobs")) ?? 0;
        return (true, Codes.LackingToolScope(scope));
    }

    /// <summary>Whether a machine raises a recovery for missing tools: LackingToolsMachineRecovery 1-4 (alarm or
    /// warning, always or only on duty; 0 or no row, the default, none), or tool resource management set to inform
    /// only (2).</summary>
    internal static bool LackingToolRecoveryOn(IReadOnlyDictionary<string, string>? settings) =>
        settings is not null && (ToInt(settings.GetValueOrDefault("LackingToolsMachineRecovery")) is >= 1 and <= 4
                                 || ToInt(settings.GetValueOrDefault("ToolResourceManagementType")) == 2);

    /// <summary>Typed text on one line, runs of spaces collapsed.</summary>
    internal static string OneLine(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // --- SQL ---

    // PalletFixtures' condition columns and when each counts.
    private static readonly (string Column, string Condition, string Key)[] FixtureFlags =
    [
        ("Damaged", "pf.Damaged = 1", "damaged"),
        ("PreMcSuspend", "pf.PreMcSuspend = 1", "pre-machining suspended"),
        ("ReclampStatus", $"pf.ReclampStatus = {ReclampTimeoutAlarm}", $"reclamp {Codes.ReclampStatus(ReclampTimeoutAlarm)}"),
        ("ReclampStatus", $"pf.ReclampStatus = {ReclampTimeoutWarning}", $"reclamp {Codes.ReclampStatus(ReclampTimeoutWarning)}"),
        ("LackingTool", "pf.LackingTool = 1", "lacking tools"),
        ("WorkSetHold", "pf.WorkSetHold = 1", "work-set hold"),
        ("Suspend", "pf.Suspend = 1", "machining suspended"),
    ];

    /// <summary>The fixtures with any condition (cap + 1; blocked ones first, then those lacking tools that aren't
    /// suspended or held, then the rest),
    /// with the order and process FixtureRun says they hold now. "WHERE 1 = 0" when the cell has none of the
    /// condition columns.</summary>
    internal static string AttentionFixturesSql(SchemaInfo schema, bool includeParked)
    {
        const string t = "PalletFixtures";
        bool Has(string column) => schema.Has(t, column);
        var conditions = new List<string>();
        if (Has("Suspend")) conditions.Add("pf.Suspend = 1");
        if (Has("WorkSetHold")) conditions.Add("pf.WorkSetHold = 1");
        if (Has("Damaged")) conditions.Add("pf.Damaged = 1");
        if (Has("PreMcSuspend")) conditions.Add("pf.PreMcSuspend = 1");
        if (Has("LackingTool")) conditions.Add("pf.LackingTool = 1");
        if (Has("ReclampStatus")) conditions.Add($"pf.ReclampStatus IN ({ReclampTimeoutAlarm}, {ReclampTimeoutWarning})");

        var blocked = new List<string>();
        if (Has("Damaged")) blocked.Add("pf.Damaged = 1");
        if (Has("PreMcSuspend")) blocked.Add("pf.PreMcSuspend = 1");
        if (Has("ReclampStatus")) blocked.Add($"pf.ReclampStatus = {ReclampTimeoutAlarm}");
        var whens = new List<string>();
        if (blocked.Count > 0) whens.Add($"WHEN {string.Join(" OR ", blocked)} THEN 0");
        if (Has("LackingTool"))
        {
            var parked = (Has("Suspend") ? " AND ISNULL(pf.Suspend, 0) = 0" : "") + (Has("WorkSetHold") ? " AND ISNULL(pf.WorkSetHold, 0) = 0" : "");
            whens.Add($"WHEN pf.LackingTool = 1{parked} THEN 1");
        }
        // Parked fixtures last (3), so the cap drops them before anything still waiting (a reclamp warning is 2).
        if (ParkedCondition(schema) is { } parkedLast) whens.Add($"WHEN {parkedLast} THEN 3");
        // Without any condition column there are no rows to sort; "(SELECT 0)", since a bare 0 in ORDER BY is a column
        // position and an error.
        var severity = whens.Count > 0 ? $"CASE {string.Join(" ", whens)} ELSE 2 END" : "(SELECT 0)";

        var run = schema.HasTable("FixtureRun") && schema.Has("FixtureRun", "PalletFixtureID") && Has("PalletFixtureID");
        var withOrder = run && schema.Has("FixtureRun", "CurOrderID") && schema.HasTable("Orders");
        var withProcess = run && schema.Has("FixtureRun", "CurProcessID") && schema.HasTable("Processes");
        var cols = string.Join(", ",
            Has("PalletID") ? PalletNumberSql(schema, "pf.PalletID", "PalletNumber") : "CAST(NULL AS int) AS PalletNumber",
            Opt(schema, t, "pf", "FixtureNumber"),
            Opt(schema, t, "pf", "Suspend", type: "bit"),
            Opt(schema, t, "pf", "WorkSetHold", type: "bit"),
            Opt(schema, t, "pf", "Damaged", type: "bit"),
            Opt(schema, t, "pf", "PreMcSuspend", type: "bit"),
            Opt(schema, t, "pf", "PreMcSuspendReason"),
            Opt(schema, t, "pf", "LackingTool", type: "bit"),
            Opt(schema, t, "pf", "ReclampStatus"),
            Opt(schema, t, "pf", "StatusReason", type: Text),
            withOrder ? Opt(schema, "Orders", "o", "OrderName", type: Text) : $"CAST(NULL AS {Text}) AS OrderName",
            withProcess ? Opt(schema, "Processes", "pr", "ProcessNumber") : "CAST(NULL AS int) AS ProcessNumber");
        var joins = (run ? " LEFT JOIN FixtureRun fr ON fr.PalletFixtureID = pf.PalletFixtureID" : "")
                    + (withOrder ? " LEFT JOIN Orders o ON o.OrderID = fr.CurOrderID" : "")
                    + (withProcess ? " LEFT JOIN Processes pr ON pr.ProcessID = fr.CurProcessID" : "");
        var where = conditions.Count > 0 ? string.Join(" OR ", conditions) : "1 = 0";
        if (!includeParked && ParkedCondition(schema) is { } parkedSql) where = $"({where}) AND NOT ({parkedSql})";
        return $"""
            SELECT TOP (@take) {cols}
            FROM PalletFixtures pf{joins}
            WHERE {where}
            ORDER BY {severity}, PalletNumber, FixtureNumber;
            """;
    }

    /// <summary>A parked fixture in SQL: suspended or on hold, not damaged, not pre-machining suspended, no reclamp
    /// timeout. Null when the cell has neither the suspend nor the hold column.</summary>
    private static string? ParkedCondition(SchemaInfo schema)
    {
        const string t = "PalletFixtures";
        var held = new List<string>();
        if (schema.Has(t, "Suspend")) held.Add("pf.Suspend = 1");
        if (schema.Has(t, "WorkSetHold")) held.Add("pf.WorkSetHold = 1");
        if (held.Count == 0) return null;
        var nothingElse = new List<string>();
        if (schema.Has(t, "Damaged")) nothingElse.Add("ISNULL(pf.Damaged, 0) = 0");
        if (schema.Has(t, "PreMcSuspend")) nothingElse.Add("ISNULL(pf.PreMcSuspend, 0) = 0");
        if (schema.Has(t, "ReclampStatus")) nothingElse.Add("ISNULL(pf.ReclampStatus, 0) = 0");
        return $"({string.Join(" OR ", held)})" + string.Concat(nothingElse.Select(c => " AND " + c));
    }

    /// <summary>The parked fixtures counted per pallet place (PalletLocation type and number) and condition, with how
    /// many are lacking tools. Grouped rows, at most places × 3. Null when the cell can't tell a parked fixture.</summary>
    internal static string? AttentionParkedSql(SchemaInfo schema)
    {
        const string t = "PalletFixtures";
        if (ParkedCondition(schema) is not { } parked) return null;
        string Place(string column) =>
            schema.Has(t, "PalletID") && schema.Has("PalletLocation", "PalletID") && schema.Has("PalletLocation", column)
                ? $"(SELECT TOP 1 l.{column} FROM PalletLocation l WHERE l.PalletID = pf.PalletID) AS {column}"
                : $"CAST(NULL AS int) AS {column}";
        string Bit(string column, string condition) => schema.Has(t, column) ? $"CASE WHEN {condition} THEN 1 ELSE 0 END" : "0";
        // Blocking: the flag holds back the job waiting now (suspend: machining, turning and deburring jobs; work-set
        // hold: work setting). WithParts: a held fixture that still holds parts.
        var job = schema.Has(t, "PalletFixtureID") && schema.Has("FixtureRun", "PalletFixtureID") && schema.Has("FixtureRun", "CurJobID")
                  && schema.Has("FixtureRun", "CurJobStatus") && schema.Has("Jobs", "JobID") && schema.Has("Jobs", "JobType");
        var suspendBlocks = schema.Has(t, "Suspend") ? $"(pf.Suspend = 1 AND j.JobType IN ({MachiningJob}, {TurningJob}, {DeburringJob}))" : "1 = 0";
        var holdBlocks = schema.Has(t, "WorkSetHold") ? $"(pf.WorkSetHold = 1 AND j.JobType = {WorkSettingJob})" : "1 = 0";
        var blocking = job
            ? $"CASE WHEN EXISTS (SELECT 1 FROM FixtureRun fr JOIN Jobs j ON j.JobID = fr.CurJobID WHERE fr.PalletFixtureID = pf.PalletFixtureID AND fr.CurJobStatus = {FixtureWaiting} AND ({suspendBlocks} OR {holdBlocks})) THEN 1 ELSE 0 END"
            : "0";
        var withParts = schema.Has(t, "WorkSetHold") && schema.Has(t, "PalletFixtureID") && schema.Has("FixtureQuantity", "PalletFixtureID")
                        && schema.Has("FixtureQuantity", "ClampedQuantity")
            ? "CASE WHEN pf.WorkSetHold = 1 AND (SELECT SUM(q.ClampedQuantity) FROM FixtureQuantity q WHERE q.PalletFixtureID = pf.PalletFixtureID) > 0 THEN 1 ELSE 0 END"
            : "0";
        return $"""
            SELECT x.CurDeviceType, x.CurDeviceNumber, x.Suspended, x.Held, COUNT(*) AS N, SUM(x.Lacking) AS Lacking,
                   SUM(x.Blocking) AS Blocking, SUM(x.WithParts) AS WithParts FROM (
                SELECT {Place("CurDeviceType")}, {Place("CurDeviceNumber")},
                       {Bit("Suspend", "pf.Suspend = 1")} AS Suspended, {Bit("WorkSetHold", "pf.WorkSetHold = 1")} AS Held,
                       {Bit("LackingTool", "pf.LackingTool = 1")} AS Lacking, {blocking} AS Blocking, {withParts} AS WithParts
                FROM PalletFixtures pf
                WHERE {parked}) x
            GROUP BY x.CurDeviceType, x.CurDeviceNumber, x.Suspended, x.Held;
            """;
    }

    /// <summary>Every fixture condition counted over all fixtures (one row); null when the cell has none of them.</summary>
    internal static string? AttentionFixtureCountsSql(SchemaInfo schema)
    {
        var sums = FixtureFlags.Where(f => schema.Has("PalletFixtures", f.Column))
            .Select(f => $"SUM(CASE WHEN {f.Condition} THEN 1 ELSE 0 END) AS C{Array.IndexOf(FixtureFlags, f)}")
            .ToList();
        return sums.Count == 0 ? null : $"SELECT {string.Join(", ", sums)} FROM PalletFixtures pf;";
    }

    // The *Check columns of FixtureRun: each holds the machines (device ids) that marked the fixture for that check.
    private static readonly string[] InspectionChecks =
    [
        "FirstToolCheck", "InsToolLifeCheck", "InsTotalToolLifeCheck", "McCMMCheck", "McGageCheck", "ProcCMMCheck",
        "ProcCMMFullCheck", "ProcGageCheck",
    ];

    /// <summary>Instructions set at a work-set station (cap + 1). A clamp the console shows nothing for is left out:
    /// nothing to clamp (clamp plus move quantity 0), or its move partner cancelled (the partner's unclamp was
    /// cancelled or is re-machined, or the partner isn't an unclamp). Other, input and output rows have no fixture:
    /// their pallet is the one on the station's table. Null without the OperationType column.</summary>
    internal static string? WorkSetCommandsSql(SchemaInfo schema)
    {
        const string t = "WorkSetCommand";
        if (!schema.Has(t, "OperationType") || !schema.Has(t, "DeviceID")) return null;
        bool Has(string column) => schema.Has(t, column);
        var fixturePallet = Has("PalletFixtureID") && schema.Has("PalletFixtures", "PalletFixtureID") && schema.Has("PalletFixtures", "PalletID")
                            && schema.Has("Pallets", "PalletNumber")
            ? "(SELECT TOP 1 p.PalletNumber FROM PalletFixtures pf JOIN Pallets p ON p.PalletID = pf.PalletID WHERE pf.PalletFixtureID = c.PalletFixtureID)"
            : null;
        var tablePallet = schema.Has("WorkSetStatus", "DeviceID") && schema.Has("WorkSetStatus", "TablePalletID") && schema.Has("Pallets", "PalletNumber")
            ? "(SELECT TOP 1 p.PalletNumber FROM WorkSetStatus w JOIN Pallets p ON p.PalletID = w.TablePalletID WHERE w.DeviceID = c.DeviceID)"
            : null;
        var pallet = (fixturePallet, tablePallet) switch
        {
            (not null, not null) => $"COALESCE({fixturePallet}, {tablePallet}) AS PalletNumber",
            (not null, null) => $"{fixturePallet} AS PalletNumber",
            (null, not null) => $"{tablePallet} AS PalletNumber",
            _ => "CAST(NULL AS int) AS PalletNumber",
        };
        var orderId = (Has("ClampOrderID"), Has("UnclampOrderID")) switch
        {
            (true, true) => "COALESCE(NULLIF(c.ClampOrderID, 0), c.UnclampOrderID)",
            (true, false) => "c.ClampOrderID",
            (false, true) => "c.UnclampOrderID",
            _ => null,
        };
        var order = orderId is not null && schema.Has("Orders", "OrderName")
            ? $"(SELECT TOP 1 o.OrderName FROM Orders o WHERE o.OrderID = {orderId}) AS OrderName"
            : $"CAST(NULL AS {Text}) AS OrderName";
        // Re-machining: the job it restarts from, by number and program.
        var reJob = Has("ReMachineJobID") && schema.Has("Jobs", "JobID") && schema.Has("Jobs", "JobNumber")
            ? "(SELECT TOP 1 j.JobNumber FROM Jobs j WHERE j.JobID = c.ReMachineJobID) AS ReMachineJobNumber"
            : "CAST(NULL AS int) AS ReMachineJobNumber";
        var reProgram = Has("ReMachineJobID") && schema.Has("MachineJobs", "JobID") && schema.Has("MachineJobs", "NCProgramFileID")
                        && schema.Has("NCProgramFiles", "NCProgramFileID") && schema.Has("NCProgramFiles", "Name")
            ? "(SELECT TOP 1 n.Name FROM MachineJobs mj JOIN NCProgramFiles n ON n.NCProgramFileID = mj.NCProgramFileID WHERE mj.JobID = c.ReMachineJobID) AS ReMachineProgram"
            : $"CAST(NULL AS {Text}) AS ReMachineProgram";
        // Inspection marks: a *Check column naming a machine (any non-zero digit), or the fixture gauge flag.
        var marks = InspectionChecks.Where(col => schema.Has("FixtureRun", col)).Select(col => $"fr.{col} LIKE '%[1-9]%'").ToList();
        if (schema.Has("FixtureRun", "FixtureGageCheck")) marks.Add("fr.FixtureGageCheck = 1");
        var check = marks.Count > 0 && Has("PalletFixtureID") && schema.Has("FixtureRun", "PalletFixtureID")
            ? $"CASE WHEN EXISTS (SELECT 1 FROM FixtureRun fr WHERE fr.PalletFixtureID = c.PalletFixtureID AND ({string.Join(" OR ", marks)})) THEN 1 ELSE 0 END AS InspectionCheck"
            : "CAST(0 AS int) AS InspectionCheck";
        var callStatus = Has("PalletFixtureID") && schema.Has("PalletFixtures", "OperatorCallStatus")
            ? "(SELECT TOP 1 pf.OperatorCallStatus FROM PalletFixtures pf WHERE pf.PalletFixtureID = c.PalletFixtureID) AS OperatorCallStatus"
            : "CAST(NULL AS int) AS OperatorCallStatus";
        var cols = string.Join(", ",
            "c.DeviceID", pallet, "COUNT(*) OVER () AS Total",
            Opt(schema, t, "c", "FixtureNumber"),
            Opt(schema, t, "c", "OperationType"),
            Opt(schema, t, "c", "OperationStatus"),
            Opt(schema, t, "c", "ClampQuantity"),
            Opt(schema, t, "c", "ReMachine", type: "bit"),
            Opt(schema, t, "c", "InspectionConfirmation", type: "bit"),
            order, reJob, reProgram, check, callStatus);

        // The clamps the console shows nothing for.
        var hidden = "";
        if (Has("ClampQuantity"))
        {
            var quantity = Has("MoveQuantity") ? "c.ClampQuantity + c.MoveQuantity" : "c.ClampQuantity";
            var partner = Has("MovePalletFixtureID") && Has("PalletFixtureID")
                ? " OR (c.MovePalletFixtureID > 0 AND EXISTS (SELECT 1 FROM WorkSetCommand m WHERE m.DeviceID = c.DeviceID " +
                  "AND m.PalletFixtureID = c.MovePalletFixtureID AND (m.OperationType NOT IN (2, 3, 4, 5)" +
                  (Has("ReMachine") ? " OR m.ReMachine = 1" : "") +
                  (Has("OperationStatus") ? $" OR m.OperationStatus = {UnclampCancelled}" : "") + ")))"
                : "";
            hidden = $" AND NOT (c.OperationType = {ClampOperation} AND ({quantity} = 0{partner}))";
        }
        var then = Has("FixtureNumber") ? ", c.FixtureNumber" : "";
        return $"""
            SELECT TOP (@take) {cols}
            FROM WorkSetCommand c
            WHERE c.OperationType <> {NoWorkSetOperation}{hidden}
            ORDER BY c.DeviceID{then};
            """;
    }

    /// <summary>Manual transports not finished or cancelled, newest first (cap + 1). Null without a Status column.</summary>
    internal static string? ManualTransportsSql(SchemaInfo schema)
    {
        const string t = "ManualTransport";
        if (!schema.Has(t, "Status")) return null;
        var cols = string.Join(", ",
            schema.Has(t, "PalletID") ? PalletNumberSql(schema, "t.PalletID", "PalletNumber") : "CAST(NULL AS int) AS PalletNumber",
            "COUNT(*) OVER () AS Total",
            Opt(schema, t, "t", "DestDeviceType"),
            Opt(schema, t, "t", "DestDeviceNumber"),
            Opt(schema, t, "t", "DestDevicePosition"),
            Opt(schema, t, "t", "Status"),
            Opt(schema, t, "t", "TransportType"),
            Opt(schema, t, "t", "EntryDate", type: "datetime"));
        var order = schema.Has(t, "EntryDate") ? "t.EntryDate DESC" : "PalletNumber";
        return $"""
            SELECT TOP (@take) {cols}
            FROM ManualTransport t
            WHERE t.Status NOT IN ({ClosedTransports})
            ORDER BY {order};
            """;
    }

    /// <summary>Each device's maintenance cycle limit (DeviceParameters.WarningCycleCount; 0 means none set); null when
    /// the cell has no DeviceParameters.</summary>
    internal static string? CycleLimitSql(SchemaInfo schema)
    {
        const string t = "DeviceParameters";
        if (!schema.Has(t, "DeviceID") || !schema.Has(t, "ParameterName") || !schema.Has(t, "ParameterValue")) return null;
        return "SELECT DeviceID, ParameterValue FROM DeviceParameters WHERE ParameterName = 'WarningCycleCount';";
    }

    /// <summary>Open alarms per device: Alarms (non-warning PMC plus CNC, as the overview counts them), PmcAlarms
    /// (non-warning PMC only: what keeps a machine from automatic work) and AllAlarms (every open alarm and warning:
    /// what keeps a station from it).</summary>
    internal static string AttentionAlarmSql(SchemaInfo schema)
    {
        var warning = schema.Has("PMCAlarms", "IsWarning") ? "IsWarning = 1" : "1 = 0";
        return $"""
            SELECT DeviceID, SUM(A) AS Alarms, SUM(P) AS PmcAlarms, COUNT(*) AS AllAlarms FROM (
                SELECT DeviceID, CASE WHEN {warning} THEN 0 ELSE 1 END AS A, CASE WHEN {warning} THEN 0 ELSE 1 END AS P
                FROM PMCAlarms WHERE ResetTime IS NULL
                UNION ALL
                SELECT DeviceID, 1, 0 FROM CNCAlarms WHERE ResetTime IS NULL) a
            GROUP BY DeviceID;
            """;
    }

    // --- reading ---

    public async Task<AttentionList> GetAttentionAsync(bool includeParked, CancellationToken ct)
    {
        await using var s = await OpenAsync(ct);
        var schema = s.Schema;
        var notAvailable = AttentionColumnsMissing(schema);

        // With the cell's lacking-tool setting off, LackingTool is always 0: the fixture queries act as if the column
        // weren't there, and notAvailable says why.
        var toolSettings = await SystemParametersAsync(s, ct, AttentionSettingNames);
        var runtime = await UserSysDataAsync(s, ct, "DutyMode");
        var settings = AttentionSettingsFrom(toolSettings, runtime);
        if (toolSettings is null) notAvailable.Add("the cell's settings (transport cancel, duty, order machining suspend, …)");
        else if (runtime is null) notAvailable.Add("off-duty holds (the cell's duty mode can't be read)");
        var perDevice = await DeviceParametersAsync(s, ct, "APCArmType", "ControlType", "ManualTransportSendCommand");
        var (lackComputed, lackScope) = LackingToolSetting(toolSettings);
        var fixtureSchema = schema;
        if (schema.Has("PalletFixtures", "LackingTool"))
        {
            if (lackComputed == false)
            {
                fixtureSchema = schema.Without("PalletFixtures", "LackingTool");
                notAvailable.Add(LackingToolsNotComputed);
            }
            else if (lackComputed is null) notAvailable.Add(LackingToolsSettingUnknown);
        }

        var devices = s.Devices.ToDictionary(d => d.Id, d =>
        {
            var own = perDevice?.GetValueOrDefault(d.Id);
            // APC arm type None (0) or Direct (3): the console's direct-table position check applies.
            var apc = ToInt(own?.GetValueOrDefault("APCArmType"));
            return new AttentionDevice(d.Name, d.Type)
            {
                Id = d.Id, ApcDirect = apc is null ? null : apc is 0 or 3,
                VirtualStation = d.Type == StationType && IsVirtualControl(own?.GetValueOrDefault("ControlType")),
                ManualTransportSendCommand = ToInt(own?.GetValueOrDefault("ManualTransportSendCommand")),
            };
        });

        // Device states; the extra columns (TotalCycleCount) come through the overview's own SQL.
        var locations = schema.Has("PalletLocation", "PalletID") && schema.Has("PalletLocation", "CurDeviceType");
        async Task ReadStatus(string sql, Func<SqlDataReader, int?> idOf)
        {
            using var cmd = Command(s.Connection, sql);
            using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                if (idOf(r) is not { } id || !devices.TryGetValue(id, out var d)) continue;
                devices[id] = d with
                {
                    Connected = Bool(r, "Connected"), Emergency = Bool(r, "Emergency"), Recovery = Bool(r, "Recovery"),
                    Status = Int(r, "Status"), Cycles = Int(r, "TotalCycleCount"),
                    OperatorCall = Has(r, "OperatorCall") ? Int(r, "OperatorCall") : null,
                    ToolStatus = Has(r, "ToolStatus") ? Int(r, "ToolStatus") : null,
                    Open = Has(r, "DeviceOpen") ? Bool(r, "DeviceOpen") : null,
                    Skip = Has(r, "DeviceSkip") ? Bool(r, "DeviceSkip") : null,
                    TablePallet = Has(r, "TablePalletNumber") ? Int(r, "TablePalletNumber") : null,
                    Action = Has(r, "MachineAction") ? Int(r, "MachineAction") : null,
                    SubAction = Has(r, "MachineSubAction") ? Int(r, "MachineSubAction") : null,
                    Command = Has(r, "CommandType") ? Int(r, "CommandType") : null,
                    From = Has(r, "FromDeviceName") ? Str(r, "FromDeviceName") : "",
                    To = Has(r, "ToDeviceName") ? Str(r, "ToDeviceName") : "",
                    SystemMode = Has(r, "SystemMode") ? Bool(r, "SystemMode") : null,
                    TransProhibit = Has(r, "DeviceTransProhibit") ? Bool(r, "DeviceTransProhibit") : null,
                    CncRun = Has(r, "CNCRun") ? Int(r, "CNCRun") : null,
                    HelpRequest = Has(r, "HelpRequest") ? Int(r, "HelpRequest") : null,
                    OpeTimeout = Has(r, "OpeTimeout") ? Int(r, "OpeTimeout") : null,
                    ExtraToolPallet = Has(r, "NeedExtraToolPalletID") && Int(r, "NeedExtraToolPalletID") is > 0 ? Int(r, "ExtraToolPalletNumber") : null,
                    // No PalletLocation row, or "out of system": the pallet is outside the cell.
                    ExtraToolPalletOutside = locations && Has(r, "ExtraToolPalletPlace") && Int(r, "NeedExtraToolPalletID") is > 0
                                             && Int(r, "ExtraToolPalletPlace") is null or OutOfSystem,
                    ExtraToolPalletTransport = Has(r, "ExtraToolPalletTransport") && Int(r, "ExtraToolPalletTransport") == 1,
                    TableStatus = Has(r, "TableStatus") ? Int(r, "TableStatus") : null,
                    TableReserved = Has(r, "TableReservedPalletID") && Int(r, "TableReservedPalletID") is > 0,
                };
            }
        }
        if (schema.HasTable("MachineStatus")) await ReadStatus(StatusSql(schema, "MachineStatus", MachineSpots), r => Int(r, "DeviceID"));
        else notAvailable.Add("machine status");
        if (schema.HasTable("WorkSetStatus")) await ReadStatus(StatusSql(schema, "WorkSetStatus", StationSpots), r => Int(r, "DeviceID"));
        else notAvailable.Add("work-set station status");
        if (VehicleSql(schema) is { } vehicleSql) await ReadStatus(vehicleSql, r => VehicleIdOf(r, s.Devices));
        else notAvailable.Add("vehicle status");

        if (schema.HasTable("PMCAlarms") && schema.HasTable("CNCAlarms"))
        {
            using var cmd = Command(s.Connection, AttentionAlarmSql(schema));
            using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var id = Int(r, "DeviceID") ?? 0;
                if (devices.TryGetValue(id, out var d))
                {
                    devices[id] = d with
                    {
                        OpenAlarms = Int(r, "Alarms") ?? 0, PmcAlarms = Int(r, "PmcAlarms") ?? 0, AllAlarms = Int(r, "AllAlarms") ?? 0,
                    };
                }
            }
        }

        if (CycleLimitSql(schema) is { } limitSql)
        {
            using var cmd = Command(s.Connection, limitSql);
            using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var id = Int(r, "DeviceID") ?? 0;
                if (devices.TryGetValue(id, out var d)) devices[id] = d with { CycleLimit = ToInt(Str(r, "ParameterValue")) };
            }
        }
        else notAvailable.Add("maintenance cycle limits");

        // Tools in alarm or with an offset error, per machine, by the status the console shows.
        var tools = new List<AttentionTools>();
        if (schema.HasTable(Itd) && schema.HasTable(Itc) && schema.HasTable(Ftd) && schema.HasTable(Loc))
        {
            using var cmd = Command(s.Connection, ToolSummarySql(schema));
            cmd.Parameters.Add("@device", System.Data.SqlDbType.Int).Value = DBNull.Value;
            using var r = await cmd.ExecuteReaderAsync(ct);
            var perMachine = new Dictionary<int, (int Alarm, int Offset)>();
            while (await r.ReadAsync(ct))
            {
                var id = Int(r, "DeviceID") ?? 0;
                if (!devices.TryGetValue(id, out var d) || d.Type != MachineType) continue;
                var (alarm, offset) = perMachine.GetValueOrDefault(id);
                var n = Int(r, "N") ?? 0;
                perMachine[id] = Int(r, "Status") switch
                {
                    ToolAlarm => (alarm + n, offset),
                    ToolOffsetError => (alarm, offset + n),
                    _ => (alarm, offset),
                };
            }
            tools.AddRange(s.Devices.Where(d => perMachine.ContainsKey(d.Id))
                .Select(d => new AttentionTools(d.Name, perMachine[d.Id].Alarm, perMachine[d.Id].Offset)));
        }
        else notAvailable.Add("tools in alarm");

        var fixtures = new List<AttentionFixture>();
        IReadOnlyDictionary<string, int>? fixtureCounts = null;
        List<ParkedGroup>? parked = null;
        if (schema.HasTable("PalletFixtures"))
        {
            if (!schema.HasTable("FixtureRun")) notAvailable.Add("each fixture's current order and process");
            using (var cmd = Command(s.Connection, AttentionFixturesSql(fixtureSchema, includeParked)))
            {
                AddTake(cmd);
                using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    fixtures.Add(new AttentionFixture(Int(r, "PalletNumber"), Int(r, "FixtureNumber"))
                    {
                        Suspend = Bool(r, "Suspend") == true, Hold = Bool(r, "WorkSetHold") == true, Damaged = Bool(r, "Damaged") == true,
                        PreMcSuspend = Bool(r, "PreMcSuspend") == true, PreMcReason = Int(r, "PreMcSuspendReason"),
                        LackingTool = Bool(r, "LackingTool") == true, Reclamp = Int(r, "ReclampStatus"), Reason = Str(r, "StatusReason"),
                        Order = NonEmpty(Str(r, "OrderName")), Process = Int(r, "ProcessNumber"),
                    });
                }
            }
            if (AttentionFixtureCountsSql(fixtureSchema) is { } countSql)
            {
                using var cmd = Command(s.Connection, countSql);
                using var r = await cmd.ExecuteReaderAsync(ct);
                var found = new Dictionary<string, int>();
                if (await r.ReadAsync(ct))
                {
                    for (var i = 0; i < FixtureFlags.Length; i++)
                    {
                        if (fixtureSchema.Has("PalletFixtures", FixtureFlags[i].Column) && (Int(r, $"C{i}") ?? 0) is > 0 and var n)
                            found[FixtureFlags[i].Key] = n;
                    }
                }
                fixtureCounts = found;
            }
            if (AttentionParkedSql(fixtureSchema) is { } parkedSql)
            {
                using var cmd = Command(s.Connection, parkedSql);
                using var r = await cmd.ExecuteReaderAsync(ct);
                var groups = new List<(int?, int?, bool, bool, int, int, int, int)>();
                while (await r.ReadAsync(ct))
                {
                    groups.Add((Int(r, "CurDeviceType"), Int(r, "CurDeviceNumber"), Int(r, "Suspended") == 1, Int(r, "Held") == 1,
                        Int(r, "N") ?? 0, Int(r, "Lacking") ?? 0, Int(r, "Blocking") ?? 0, Int(r, "WithParts") ?? 0));
                }
                parked = MergeParked(groups, s.Devices.Select(d => (d.Type, d.Number, d.Name)).ToList());
            }
        }
        else notAvailable.Add("fixtures (suspended, damaged, lacking tools, …)");

        var commands = new List<AttentionCommand>();
        int? commandTotal = null, transportTotal = null;
        if (schema.HasTable("WorkSetCommand") && WorkSetCommandsSql(schema) is { } commandSql)
        {
            using var cmd = Command(s.Connection, commandSql);
            AddTake(cmd);
            using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                commandTotal = Int(r, "Total");
                commands.Add(new AttentionCommand(DeviceName(s, Int(r, "DeviceID")), Int(r, "PalletNumber"), Int(r, "FixtureNumber"),
                    Int(r, "OperationType"), Int(r, "OperationStatus"), NonEmpty(Str(r, "OrderName")), Int(r, "ClampQuantity"))
                {
                    ReMachine = Bool(r, "ReMachine") == true, ReMachineJob = Int(r, "ReMachineJobNumber"),
                    ReMachineProgram = NonEmpty(Str(r, "ReMachineProgram")), InspectionCheck = Int(r, "InspectionCheck") == 1,
                    InspectionConfirmed = Bool(r, "InspectionConfirmation") == true, OperatorCallStatus = Int(r, "OperatorCallStatus"),
                });
            }
        }
        else notAvailable.Add("work-set instructions");

        var transports = new List<AttentionTransport>();
        if (schema.HasTable("ManualTransport") && ManualTransportsSql(schema) is { } transportSql)
        {
            using var cmd = Command(s.Connection, transportSql);
            AddTake(cmd);
            using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                transportTotal = Int(r, "Total");
                var (type, number) = (Int(r, "DestDeviceType"), Int(r, "DestDeviceNumber"));
                var device = s.Devices.FirstOrDefault(d => d.Type == type && d.Number == number && number is > 0)?.Name;
                // A destination of type 0 or none is unset, not a place; a station numbered 0 is any station.
                var to = type is null or OutOfSystem ? ""
                    : type == StationType && number is 0 ? "any work-set station"
                    : Join(device ?? $"{Codes.DeviceType(type)} {number}", Codes.DevicePosition(Int(r, "DestDevicePosition")));
                transports.Add(new AttentionTransport(Int(r, "PalletNumber"), to, Int(r, "Status"), Date(r, "EntryDate"))
                {
                    Type = Int(r, "TransportType"), DestType = type, DestDevice = device,
                });
            }
        }
        else notAvailable.Add("manual transports");
        if (settings.ManualTransAvailType is null && toolSettings is not null
            && transports.Any(t => t.DestType == StationType && t.Status == TransportWaiting))
            notAvailable.Add("whether a manual transport waits for a work-set station to be open or closed (the cell has no ManualTransAvailType setting)");

        // The fixtures whose job waits: blocked work, holds, pallet holds and the tools they miss.
        WaitingAnalysis? waiting = null;
        List<MissingToolGroup>? missing = null;
        // The waiting fixtures feed grouped items and aren't listed, so they are bounded by the cell's size (a few
        // hundred fixtures), not by the row limit, which would change the groups.
        if (await ReadWaitingAsync(s, fixtureSchema, WaitingRowLimit + 1, ct) is { } read)
        {
            waiting = AnalyzeWaiting(read.Rows, read.Feasible, devices, settings);
            if (read.Capped) notAvailable.Add($"waiting work past the first {WaitingRowLimit} waiting fixtures");
            // Only for the fixtures that are items: parked and held ones wait for their flag first.
            var held = waiting.Holds.Select(h => (h.Pallet, h.Fixture)).ToHashSet();
            var open = read.Rows.Where(w => !w.Suspend && !w.Hold && !held.Contains((w.Pallet, w.Fixture))).ToList();
            missing = await MissingToolsAsync(s, open, read.Feasible, ct);
        }
        else notAvailable.Add("waiting work blocked (needs FixtureRun, Jobs and JobFeasibleDevices)");

        var rows = new AttentionRows(s.Devices.Select(d => devices[d.Id]).ToList(), fixtures, commands, transports, tools)
        {
            FixtureCounts = fixtureCounts, Parked = parked, CommandTotal = commandTotal, TransportTotal = transportTotal,
            LackingToolRecovery = LackingToolRecoveryOn(toolSettings), Waiting = waiting, Settings = settings, MissingTools = missing,
        };
        return BuildAttention(s.Now, _cap, rows, notAvailable, includeParked) with
        {
            LackingToolScope = fixtureSchema.Has("PalletFixtures", "LackingTool") ? lackScope : null,
        };
    }

    private static bool Has(SqlDataReader r, string column)
    {
        for (var i = 0; i < r.FieldCount; i++)
        {
            if (string.Equals(r.GetName(i), column, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
