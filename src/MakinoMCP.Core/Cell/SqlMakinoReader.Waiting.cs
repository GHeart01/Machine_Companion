namespace MakinoMCP.Core.Cell;

// Attention, waiting work: the fixtures whose current job waits (FixtureRun.CurJobStatus = 1), checked against the
// devices that may run it (JobFeasibleDevices) and the cell's holds, by the console's own diagnosis rules (see
// SqlMakinoReader.Diagnosis.cs). Grouped, so a cell with many waiting fixtures still gives a short list: work blocked
// because no feasible device can take it, fixtures held by an order's machining suspend or the off-duty rules (counted
// with the parked ones), pallets held because one fixture lacks tools, and the tools those fixtures miss.
public sealed partial class SqlMakinoReader
{
    private const int FixtureWaiting = 1;      // FixtureJobStatus: waiting for processing
    private const int TurningJob = 4;          // ProcessJobType
    private const int DeburringJob = 5;        // ProcessJobType
    private const int ToolCribType = 5;        // DeviceType: tool crib
    private const int VirtualControl = 11;     // ControlType: virtual (a station worked from the console)
    private const int MaxPalletsListed = 10;
    private const int MaxToolsListed = 10;
    private const int MaxToolGroups = 20;
    private const int WaitingRowLimit = 5000;

    // The conditions holds are counted under (counts and parked groups).
    internal const string OrderSuspendHold = "order machining suspended";
    internal const string OffDutyProcessHold = "off-duty process: waits until the cell is off duty";
    internal const string OffDutyCallHold = "operator-call job held until the cell is on duty";

    /// <summary>A fixture whose current job waits, with what decides whether something can run it.</summary>
    internal sealed record AttentionWaiting(int? Pallet, int? Fixture)
    {
        public string Place { get; init; } = "unknown";
        public int? JobId { get; init; }
        public int? JobType { get; init; }
        public int? ProcessId { get; init; }
        public string? Order { get; init; }
        public bool OrderMachiningSuspended { get; init; }
        public bool Suspend { get; init; }
        public bool Hold { get; init; }
        public bool Damaged { get; init; }
        public bool PreMcSuspend { get; init; }
        public bool LackingTool { get; init; }
        public bool OffDutyProcess { get; init; }
        public int? OperatorCallStatus { get; init; }
        public int? LastOneShotProcessId { get; init; }
        public int? JobOperatorCall { get; init; }
        public int? ProgramId { get; init; }
        public string? Program { get; init; }
    }

    /// <summary>The cell's settings get_attention follows. DutyOn: null when the duty mode can't be read.</summary>
    internal sealed record AttentionSettings
    {
        public bool TransportCancel { get; init; }
        public bool OrderMachiningSuspend { get; init; }
        public int DutyManagement { get; init; } = 1;
        public bool? DutyOn { get; init; }
        public int OffDutyProcessManagement { get; init; }
        public bool OffDutyOperatorCallTransport { get; init; }
        public int OneShotType { get; init; }
        public bool AllFixtureTsReady { get; init; }
        public bool ToolsHoldWork { get; init; }
        public bool InspectionConfirmation { get; init; }
        public int? ManualTransAvailType { get; init; }
        public bool ExtraToolPalletTransport { get; init; }
        public int ProbePartCount { get; init; }
    }

    /// <summary>The SystemParameters rows get_attention reads.</summary>
    private static readonly string[] AttentionSettingNames =
    [
        "FixtureToolLackInfo", "CheckLackToolsAllRemainingJobs", "LackingToolsMachineRecovery", "ToolResourceManagementType",
        "TransportCancel", "OrderMachiningSuspend", "DutyManagement", "OffDutyProcessManagement", "OffDutyOpeCallPalletTransport",
        "ProcessOpeCallOneshotType", "AllFixtureTsReady", "ToolManagementType", "InspectionMode", "PartTrackingType",
        "ManualTransAvailType", "ExtraToolPalletTransport", "ProbePartCountMacroVariable",
    ];

    internal static AttentionSettings AttentionSettingsFrom(
        IReadOnlyDictionary<string, string>? system, IReadOnlyDictionary<string, string>? runtime)
    {
        int? Get(string name) => ToInt(system?.GetValueOrDefault(name));
        var duty = DutyFrom(system, runtime);
        return new AttentionSettings
        {
            TransportCancel = Get("TransportCancel") == 1,
            OrderMachiningSuspend = Get("OrderMachiningSuspend") == 1,
            DutyManagement = Get("DutyManagement") ?? 1,
            DutyOn = duty is null ? null : duty == "on duty",
            OffDutyProcessManagement = Get("OffDutyProcessManagement") ?? 0,
            OffDutyOperatorCallTransport = Get("OffDutyOpeCallPalletTransport") == 1,
            OneShotType = Get("ProcessOpeCallOneshotType") ?? 0,
            AllFixtureTsReady = Get("AllFixtureTsReady") == 1,
            // Tools hold work back only under tool resource management that doesn't merely inform.
            ToolsHoldWork = Get("ToolManagementType") == ToolResource && Get("ToolResourceManagementType") != 2,
            // The station asks for an inspection confirmation (also under the MJDT and JDA part tracking types).
            InspectionConfirmation = Get("InspectionMode") == 1 || Get("PartTrackingType") is 2 or 3,
            ManualTransAvailType = system is null ? null : Get("ManualTransAvailType"),
            ExtraToolPalletTransport = Get("ExtraToolPalletTransport") is > 0,
            ProbePartCount = Get("ProbePartCountMacroVariable") ?? 0,
        };
    }

    /// <summary>Why a device can't take automatic work now, by the console's diagnosis checks; null when it can.
    /// Machines: disconnected, emergency stop, not in system mode, an open PMC alarm (not a warning), closed, transport
    /// cancelled (only where the cell uses it). Stations: the same, with any open alarm or warning, and skipped.</summary>
    internal static string? DeviceBlock(AttentionDevice d, AttentionSettings s)
    {
        if (d.Connected == false) return "disconnected";
        if (d.Emergency == true) return "emergency stop";
        if (d.SystemMode == false) return "not in system mode";
        if (d.Type == MachineType && d.PmcAlarms > 0) return "open PMC alarm";
        if (d.Type == StationType && d.AllAlarms > 0) return "open alarm";
        if (d.Type == StationType && d.Skip == true) return "skipped";
        if (d.Open == false) return "closed";
        if (s.TransportCancel && d.TransProhibit == true) return "transport cancelled";
        return null;
    }

    /// <summary>Waiting work no feasible device can take: one group per job type and set of devices with their
    /// reasons.</summary>
    internal sealed record BlockedGroup(string What, string Devices, IReadOnlyList<int?> Pallets, int Fixtures);

    /// <summary>A fixture held by the cell's rules rather than its own flag (counted like the parked ones).</summary>
    internal sealed record AttentionHold(int? Pallet, int? Fixture, string Place, string Condition, string Words)
    {
        public bool LackingTool { get; init; }
        public string? Order { get; init; }
    }

    /// <summary>A pallet whose fixtures wait because one of them lacks tools (AllFixtureTsReady).</summary>
    internal sealed record PalletHold(int Pallet, IReadOnlyList<int?> Lacking, IReadOnlyList<int?> HeldBack);

    internal sealed record WaitingAnalysis(
        IReadOnlyList<BlockedGroup> Blocked, IReadOnlyList<AttentionHold> Holds, IReadOnlyList<PalletHold> PalletHolds);

    /// <summary>Sorts the waiting fixtures into blocked work, holds and pallet holds. Fixtures with their own item
    /// (damaged, pre-machining suspended) or already parked by a flag (suspended, work-set hold) are left to that.</summary>
    internal static WaitingAnalysis AnalyzeWaiting(
        IReadOnlyList<AttentionWaiting> waiting, IReadOnlyDictionary<int, IReadOnlyList<int>> feasible,
        IReadOnlyDictionary<int, AttentionDevice> devices, AttentionSettings s)
    {
        var holds = new List<AttentionHold>();
        var blocked = new Dictionary<(string What, string Devices), List<int?>>();
        foreach (var w in waiting)
        {
            if (w.JobId is not > 0 || w.Damaged || w.PreMcSuspend) continue;
            var machining = w.JobType == MachiningJob;
            if (machining && !w.Suspend && !w.Hold) // suspended or held: already parked by its own flag
            {
                string? hold = null, words = null;
                if (s.OrderMachiningSuspend && w.OrderMachiningSuspended)
                    (hold, words) = (OrderSuspendHold, w.Order is null ? OrderSuspendHold : $"{OrderSuspendHold}: {w.Order}");
                else if (s.OffDutyProcessManagement != 0 && s.DutyOn == true && w.OffDutyProcess)
                    (hold, words) = (OffDutyProcessHold, OffDutyProcessHold);
                else if (s.DutyManagement != 0 && !s.OffDutyOperatorCallTransport && s.DutyOn == false && EffectiveOperatorCall(
                             new DiagFixture(w.Fixture)
                             {
                                 ProcessId = w.ProcessId, OperatorCallStatus = w.OperatorCallStatus,
                                 LastOneShotProcessId = w.LastOneShotProcessId, JobOperatorCall = w.JobOperatorCall,
                             },
                             new DiagSettings { OneShotType = s.OneShotType }))
                    (hold, words) = (OffDutyCallHold, OffDutyCallHold);
                if (hold is not null)
                {
                    holds.Add(new AttentionHold(w.Pallet, w.Fixture, w.Place, hold, words!) { LackingTool = w.LackingTool, Order = w.Order });
                    continue;
                }
            }
            if (w.Suspend || w.Hold) continue; // parked: counted there
            if (w.JobType is not (MachiningJob or WorkSettingJob)) continue;

            var type = machining ? MachineType : StationType;
            var candidates = (feasible.GetValueOrDefault(w.JobId.Value) ?? [])
                .Select(id => devices.GetValueOrDefault(id)).OfType<AttentionDevice>().Where(d => d.Type == type).ToList();
            var reasons = candidates.Select(d => (d.Name, Reason: DeviceBlock(d, s))).ToList();
            if (reasons.Any(r => r.Reason is null)) continue;
            var what = machining ? "machining" : "work setting";
            var on = reasons.Count == 0
                ? (machining ? "no feasible machine" : "no feasible work-set station")
                : string.Join(", ", reasons.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Select(r => $"{r.Name} ({r.Reason})"));
            if (!blocked.TryGetValue((what, on), out var pallets)) blocked[(what, on)] = pallets = [];
            pallets.Add(w.Pallet);
        }

        var palletHolds = new List<PalletHold>();
        if (s.AllFixtureTsReady && s.ToolsHoldWork)
        {
            var held = holds.Select(h => (h.Pallet, h.Fixture)).ToHashSet();
            foreach (var pallet in waiting.Where(w => w.Pallet is not null && w.JobType == MachiningJob).GroupBy(w => w.Pallet!.Value))
            {
                // Suspended, pre-machining suspended and off-duty-held fixtures don't count either way.
                var eligible = pallet.Where(w => !w.Suspend && !w.PreMcSuspend && !w.Damaged && !held.Contains((w.Pallet, w.Fixture))).ToList();
                var lacking = eligible.Where(w => w.LackingTool).Select(w => w.Fixture).ToList();
                var back = eligible.Where(w => !w.LackingTool).Select(w => w.Fixture).ToList();
                if (lacking.Count > 0 && back.Count > 0) palletHolds.Add(new PalletHold(pallet.Key, lacking, back));
            }
        }

        return new WaitingAnalysis(
            blocked.Select(b => new BlockedGroup(b.Key.What, b.Key.Devices, b.Value.Distinct().ToList(), b.Value.Count))
                .OrderByDescending(b => b.Fixtures).ToList(),
            holds, palletHolds);
    }

    /// <summary>The fixtures whose current job waits (cap + 1), with the job, its program and the fixture's flags; then
    /// the devices each of those jobs may run on. Null without the tables it needs.</summary>
    internal static string? WaitingSql(SchemaInfo schema)
    {
        const string pf = "PalletFixtures";
        if (!schema.Has(pf, "PalletFixtureID") || !schema.Has("FixtureRun", "PalletFixtureID") || !schema.Has("FixtureRun", "CurJobID")
            || !schema.Has("FixtureRun", "CurJobStatus") || !schema.Has("Jobs", "JobID") || !schema.Has("JobFeasibleDevices", "JobID")
            || !schema.Has("JobFeasibleDevices", "FeasibleDeviceID"))
            return null;
        string Place(string column) =>
            schema.Has(pf, "PalletID") && schema.Has("PalletLocation", "PalletID") && schema.Has("PalletLocation", column)
                ? $"(SELECT TOP 1 l.{column} FROM PalletLocation l WHERE l.PalletID = pf.PalletID) AS {column}"
                : $"CAST(NULL AS int) AS {column}";
        var withOrders = schema.Has("FixtureRun", "CurOrderID") && schema.Has("Orders", "OrderID");
        var withProgram = schema.Has("MachineJobs", "JobID") && schema.Has("MachineJobs", "NCProgramFileID");
        var programName = withProgram && schema.Has("NCProgramFiles", "NCProgramFileID") && schema.Has("NCProgramFiles", "Name")
            ? "(SELECT TOP 1 n.Name FROM NCProgramFiles n WHERE n.NCProgramFileID = mj.NCProgramFileID) AS ProgramName"
            : $"CAST(NULL AS {Text}) AS ProgramName";
        var cols = string.Join(", ",
            schema.Has(pf, "PalletID") ? PalletNumberSql(schema, "pf.PalletID", "PalletNumber") : "CAST(NULL AS int) AS PalletNumber",
            Opt(schema, pf, "pf", "FixtureNumber"), Place("CurDeviceType"), Place("CurDeviceNumber"),
            "fr.CurJobID", Opt(schema, "FixtureRun", "fr", "CurProcessID"), Opt(schema, "Jobs", "j", "JobType"),
            Opt(schema, pf, "pf", "Suspend", type: "bit"), Opt(schema, pf, "pf", "WorkSetHold", type: "bit"),
            Opt(schema, pf, "pf", "Damaged", type: "bit"), Opt(schema, pf, "pf", "PreMcSuspend", type: "bit"),
            Opt(schema, pf, "pf", "LackingTool", type: "bit"), Opt(schema, pf, "pf", "OffDutyProcess", type: "bit"),
            Opt(schema, pf, "pf", "OperatorCallStatus"), Opt(schema, pf, "pf", "LastOneShotOpeCallProcessID"),
            withOrders ? Opt(schema, "Orders", "o", "OrderName", type: Text) : $"CAST(NULL AS {Text}) AS OrderName",
            withOrders ? Opt(schema, "Orders", "o", "MachiningSuspended", type: "bit") : "CAST(NULL AS bit) AS MachiningSuspended",
            withProgram ? Opt(schema, "MachineJobs", "mj", "OperatorCall", "JobOperatorCall") : "CAST(NULL AS int) AS JobOperatorCall",
            withProgram ? "mj.NCProgramFileID" : "CAST(NULL AS int) AS NCProgramFileID", programName);
        var orders = withOrders ? " LEFT JOIN Orders o ON o.OrderID = fr.CurOrderID" : "";
        var programs = withProgram ? " LEFT JOIN MachineJobs mj ON mj.JobID = fr.CurJobID" : "";
        var order = schema.Has(pf, "FixtureNumber") ? "PalletNumber, pf.FixtureNumber" : "PalletNumber";
        return $"""
            SELECT TOP (@take) {cols}
            FROM PalletFixtures pf
            JOIN FixtureRun fr ON fr.PalletFixtureID = pf.PalletFixtureID
            LEFT JOIN Jobs j ON j.JobID = fr.CurJobID{orders}{programs}
            WHERE fr.CurJobStatus = {FixtureWaiting} AND fr.CurJobID IS NOT NULL
            ORDER BY {order};
            SELECT DISTINCT f.JobID, f.FeasibleDeviceID
            FROM JobFeasibleDevices f
            WHERE f.JobID IN (SELECT fr.CurJobID FROM FixtureRun fr WHERE fr.CurJobStatus = {FixtureWaiting});
            """;
    }

    /// <summary>A pallet place's name: a device's name, else the device type's words (all stocker shelves are one
    /// place), "out of system" or "unknown".</summary>
    internal static string PlaceName(int? type, int? number, IReadOnlyList<(int Type, int Number, string Name)> devices) =>
        type is null ? "unknown"
        : type == OutOfSystem ? OutOfSystemWords
        : devices.FirstOrDefault(d => d.Type == type && d.Number == number).Name ?? Codes.DeviceType(type);

    private static async Task<(List<AttentionWaiting> Rows, Dictionary<int, IReadOnlyList<int>> Feasible, bool Capped)?> ReadWaitingAsync(
        Session s, SchemaInfo schema, int take, CancellationToken ct)
    {
        if (WaitingSql(schema) is not { } sql) return null;
        var places = s.Devices.Select(d => (d.Type, d.Number, d.Name)).ToList();
        using var cmd = Command(s.Connection, sql);
        cmd.Parameters.Add("@take", System.Data.SqlDbType.Int).Value = take;
        using var r = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<AttentionWaiting>();
        while (await r.ReadAsync(ct))
        {
            rows.Add(new AttentionWaiting(Int(r, "PalletNumber"), Int(r, "FixtureNumber"))
            {
                Place = PlaceName(Int(r, "CurDeviceType"), Int(r, "CurDeviceNumber"), places),
                JobId = Int(r, "CurJobID"), JobType = Int(r, "JobType"), ProcessId = Int(r, "CurProcessID"),
                Order = NonEmpty(Str(r, "OrderName")), OrderMachiningSuspended = Bool(r, "MachiningSuspended") == true,
                Suspend = Bool(r, "Suspend") == true, Hold = Bool(r, "WorkSetHold") == true, Damaged = Bool(r, "Damaged") == true,
                PreMcSuspend = Bool(r, "PreMcSuspend") == true, LackingTool = Bool(r, "LackingTool") == true,
                OffDutyProcess = Bool(r, "OffDutyProcess") == true, OperatorCallStatus = Int(r, "OperatorCallStatus"),
                LastOneShotProcessId = Int(r, "LastOneShotOpeCallProcessID"), JobOperatorCall = Int(r, "JobOperatorCall"),
                ProgramId = Int(r, "NCProgramFileID"), Program = NonEmpty(Str(r, "ProgramName")),
            });
        }
        await r.NextResultAsync(ct);
        var feasible = new Dictionary<int, List<int>>();
        while (await r.ReadAsync(ct))
        {
            var job = Int(r, "JobID") ?? 0;
            if (!feasible.TryGetValue(job, out var list)) feasible[job] = list = [];
            list.Add(Int(r, "FeasibleDeviceID") ?? 0);
        }
        var capped = rows.Count >= take;
        if (capped) rows.RemoveAt(rows.Count - 1);
        return (rows, feasible.ToDictionary(f => f.Key, f => (IReadOnlyList<int>)f.Value), capped);
    }

    /// <summary>The tool types each lacking-tool fixture's program is short of on the machines that may run it, per
    /// program (at most <see cref="MaxToolGroups"/> programs, <see cref="MaxToolsListed"/> tools each); null when there
    /// is nothing to show or the cell can't count tools.</summary>
    private static async Task<List<MissingToolGroup>?> MissingToolsAsync(
        Session s, IReadOnlyList<AttentionWaiting> waiting, IReadOnlyDictionary<int, IReadOnlyList<int>> feasible,
        CancellationToken ct)
    {
        const string t = "MainProgramTools";
        var lacking = waiting.Where(w => w.LackingTool && w.JobType == MachiningJob && w.ProgramId is > 0 && w.JobId is > 0).ToList();
        if (lacking.Count == 0 || !s.Schema.Has(t, "NCProgramFileID") || !s.Schema.Has(t, "FTN") || !s.Schema.Has(t, "ToolCount")
            || !(s.Schema.HasTable(Itd) && s.Schema.HasTable(Itc) && s.Schema.HasTable(Ftd) && s.Schema.HasTable(Loc)))
            return null;
        var programs = lacking.GroupBy(w => w.ProgramId!.Value).Take(MaxToolGroups).ToList();
        var ids = programs.Select(p => p.Key).ToList();
        var name = s.Schema.Has(Ftd, "FTN") && s.Schema.Has(Ftd, "FTNComment")
            ? "(SELECT TOP 1 f.FTNComment FROM FunctionalToolData f WHERE f.FTN = m.FTN ORDER BY f.FTNID) AS FTNComment"
            : $"CAST(NULL AS {Text}) AS FTNComment";
        var cutter = s.Schema.Has(t, "CutterNo") ? "m.CutterNo = 1 AND " : "";
        var needs = new List<(int Program, int Ftn, int Needed, string Name)>();
        using (var cmd = Command(s.Connection,
                   $"SELECT m.NCProgramFileID, m.FTN, m.ToolCount, {name} FROM MainProgramTools m " +
                   $"WHERE {cutter}m.NCProgramFileID IN ({string.Join(", ", ids.Select((_, i) => $"@p{i}"))});"))
        {
            for (var i = 0; i < ids.Count; i++) cmd.Parameters.Add($"@p{i}", System.Data.SqlDbType.Int).Value = ids[i];
            using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                needs.Add((Int(r, "NCProgramFileID") ?? 0, Int(r, "FTN") ?? 0, Int(r, "ToolCount") ?? 0, Str(r, "FTNComment")));
        }
        if (needs.Count == 0) return null;
        var counts = await UsableToolCountsAsync(s, needs.Select(n => n.Ftn).ToList(), ct);
        if (counts is null) return null;
        var cribs = s.Devices.Where(d => d.Type == ToolCribType).Select(d => d.Id).ToList();

        var groups = new List<MissingToolGroup>();
        foreach (var program in programs)
        {
            var machines = program.SelectMany(w => feasible.GetValueOrDefault(w.JobId!.Value) ?? [])
                .Distinct().Select(id => s.Devices.FirstOrDefault(d => d.Id == id)).OfType<DeviceRow>()
                .Where(d => d.Type == MachineType).ToList();
            if (machines.Count == 0) continue;
            var shortTools = needs.Where(n => n.Program == program.Key)
                .Select(n => (n, Usable: machines.ToDictionary(m => m.Name, m => counts.GetValueOrDefault((m.Id, n.Ftn)))))
                .Where(x => x.Usable.Values.Any(u => u < x.n.Needed))
                .ToList();
            if (shortTools.Count == 0) continue;
            groups.Add(new MissingToolGroup(
                program.First().Program ?? $"program id {program.Key}", program.Count(), machines.Select(m => m.Name).ToList(),
                shortTools.Take(MaxToolsListed).Select(x => new MissingTool(x.n.Ftn, x.n.Needed, x.Usable)
                {
                    Name = NonEmpty(x.n.Name),
                    InCrib = cribs.Count > 0 ? cribs.Sum(c => counts.GetValueOrDefault((c, x.n.Ftn))) : null,
                }).ToList())
            {
                Capped = shortTools.Count > MaxToolsListed ? true : null,
            });
        }
        return groups.Count > 0 ? groups : null;
    }

    /// <summary>A few pallet numbers for a grouped item.</summary>
    private static List<int> FewPallets(IEnumerable<int?> pallets) =>
        pallets.OfType<int>().Distinct().Order().Take(MaxPalletsListed).ToList();

    private static bool IsVirtualControl(string? value) =>
        value is not null && (ToInt(value) == VirtualControl || value.Trim().Equals("Virtual", StringComparison.OrdinalIgnoreCase));
}
