namespace MakinoMCP.Core.Cell;

// Pallet diagnosis: why a pallet's work isn't moving, by the console's own Pallet Diagnosis rules. For each fixture
// (PalletFixtures + FixtureRun), its current job (Jobs, MachineJobs) is checked against every device that may run it
// (JobFeasibleDevices), using the device states (MachineStatus, WorkSetStatus), open alarms and the cell's settings.
// Per device the first failing check is its verdict; across devices any "can run" wins, else the highest verdict
// number, as the console does. What the console checks from memory only is listed as not checked.
public sealed partial class SqlMakinoReader
{
    // FixtureDiagnosisStatus values (words in Codes.FixtureDiagnosis).
    internal const int DiagUnknown = -1;
    internal const int DiagNormal = 1;
    internal const int DiagDamaged = 2;
    internal const int DiagWssClose = 3;
    internal const int DiagWssHold = 4;
    internal const int DiagWssDeviceError = 5;
    internal const int DiagWssNoFeasible = 6;
    internal const int DiagMcClose = 7;
    internal const int DiagMcSuspend = 8;
    internal const int DiagMcDeviceError = 9;
    internal const int DiagMcNoFeasible = 11;
    internal const int DiagNotActive = 12;
    internal const int DiagMcOffDutyProcess = 13;
    internal const int DiagWssTransProhibited = 22;
    internal const int DiagMcTransProhibited = 23;
    internal const int DiagOffDutyOperatorCall = 32;
    internal const int DiagPreMcSuspend = 43;
    internal const int DiagPreMcSuspendByError = 44;

    private const int WorkSettingJob = 1;      // ProcessJobType
    private const int MachiningJob = 2;        // ProcessJobType
    private const int JobProcessing = 2;       // FixtureJobStatus
    private const int CsgError = 2;            // PreMcSuspendReasonType
    private const int ToolResource = 2;        // ToolManagementType

    internal const string RunningNow = "processing now (the job is running)";

    /// <summary>One fixture of the pallet with what its current job needs checked.</summary>
    internal sealed record DiagFixture(int? Fixture)
    {
        public int? JobId { get; init; }
        public int? JobType { get; init; }
        public int? JobNumber { get; init; }
        public int? JobStatus { get; init; }
        public int? ProcessId { get; init; }
        public int? Process { get; init; }
        public string? Order { get; init; }
        public bool OrderMachiningSuspended { get; init; }
        public bool Suspend { get; init; }
        public bool Hold { get; init; }
        public bool Damaged { get; init; }
        public bool PreMcSuspend { get; init; }
        public int? PreMcReason { get; init; }
        public bool OffDutyProcess { get; init; }
        public int? OperatorCallStatus { get; init; }
        public int? LastOneShotProcessId { get; init; }
        public int? JobOperatorCall { get; init; }
        public int? ExtraToolPallet { get; init; }
    }

    /// <summary>A device's state as the diagnosis sees it. PmcAlarms: open non-warning PMC alarms; AllAlarms: every
    /// open PMC and CNC alarm and warning.</summary>
    internal sealed record DiagDevice(string Name, int Type)
    {
        public bool? Connected { get; init; }
        public bool? Emergency { get; init; }
        public bool? SystemMode { get; init; }
        public bool? Open { get; init; }
        public bool? Skip { get; init; }
        public bool? TransProhibit { get; init; }
        public int PmcAlarms { get; init; }
        public int AllAlarms { get; init; }
    }

    /// <summary>The cell's settings the checks follow. DutyModeOff: the stored duty mode (null when unreadable).</summary>
    internal sealed record DiagSettings
    {
        public bool TransportCancel { get; init; }
        public bool OrderMachiningSuspend { get; init; }
        public int DutyManagement { get; init; } = 1;
        public bool OffDutyOperatorCallTransport { get; init; }
        public int OneShotType { get; init; }
        public bool? DutyModeOff { get; init; }
    }

    internal static DiagSettings DiagSettingsFrom(IReadOnlyDictionary<string, string>? system, IReadOnlyDictionary<string, string>? runtime)
    {
        int? Get(string name) => ToInt(system?.GetValueOrDefault(name));
        return new DiagSettings
        {
            TransportCancel = Get("TransportCancel") == 1,
            OrderMachiningSuspend = Get("OrderMachiningSuspend") == 1,
            DutyManagement = Get("DutyManagement") ?? 1,
            OffDutyOperatorCallTransport = Get("OffDutyOpeCallPalletTransport") == 1,
            OneShotType = Get("ProcessOpeCallOneshotType") ?? 0,
            DutyModeOff = runtime is null ? null : runtime.GetValueOrDefault("DutyMode")?.Trim() == "1",
        };
    }

    /// <summary>The console's effective operator call for the fixture's current machining job: the fixture's own
    /// setting (2 permanent, 3 one-shot), the job's permanent call, or its one-shot call (every pallet when the cell
    /// says so, else until this process has had one).</summary>
    internal static bool EffectiveOperatorCall(DiagFixture f, DiagSettings s) =>
        f.OperatorCallStatus is 2 or 3 || f.JobOperatorCall == 1
        || (f.JobOperatorCall == 2 && (s.OneShotType == 1 || f.LastOneShotProcessId != f.ProcessId));

    /// <summary>One machine's verdict for a fixture's waiting machining job, in the console's order.</summary>
    internal static int MachineVerdict(DiagFixture f, DiagDevice m, DiagSettings s)
    {
        if (m.Connected == false || m.Emergency == true || m.SystemMode == false || m.PmcAlarms > 0) return DiagMcDeviceError;
        if (f.Suspend || (s.OrderMachiningSuspend && f.OrderMachiningSuspended)) return DiagMcSuspend;
        if (f.PreMcSuspend) return f.PreMcReason == CsgError ? DiagPreMcSuspendByError : DiagPreMcSuspend;
        if (f.OffDutyProcess && s.DutyModeOff == false) return DiagMcOffDutyProcess;
        if (m.Open == false) return DiagMcClose;
        if (s.TransportCancel && m.TransProhibit == true) return DiagMcTransProhibited;
        if (f.Damaged) return DiagDamaged;
        if (s.DutyManagement != 0 && !s.OffDutyOperatorCallTransport && s.DutyModeOff == true && EffectiveOperatorCall(f, s))
            return DiagOffDutyOperatorCall;
        return DiagNormal;
    }

    /// <summary>One work-set station's verdict for a fixture's waiting work-set job, in the console's order.</summary>
    internal static int StationVerdict(DiagFixture f, DiagDevice w, DiagSettings s)
    {
        if (w.Connected == false || w.Emergency == true || w.SystemMode == false || w.AllAlarms > 0) return DiagWssDeviceError;
        if (f.Hold) return DiagWssHold;
        if (w.Skip == true) return DiagUnknown;
        if (w.Open == false) return DiagWssClose;
        if (s.TransportCancel && w.TransProhibit == true) return DiagWssTransProhibited;
        return DiagNormal;
    }

    /// <summary>The verdicts of every feasible device combined as the console does: "can run" if any device gives it,
    /// else the highest value; <paramref name="noFeasible"/> when there is no device.</summary>
    internal static int Combine(IReadOnlyCollection<int> verdicts, int noFeasible) =>
        verdicts.Count == 0 ? noFeasible : verdicts.Contains(DiagNormal) ? DiagNormal : verdicts.Max();

    /// <summary>A fixture's diagnosis against the devices that may run its current job.</summary>
    internal static FixtureDiagnosis DiagnoseFixture(DiagFixture f, IReadOnlyList<DiagDevice> feasible, DiagSettings s)
    {
        var shown = new FixtureDiagnosis(f.Fixture, "")
        {
            Order = f.Order, Process = f.Process,
            Job = f.JobType is null ? null : Join(Codes.JobType(f.JobType), f.JobNumber is { } n ? $"job {n}" : ""),
            JobStatus = f.JobStatus is null ? null : Codes.FixtureJobStatus(f.JobStatus),
        };
        if (f.JobId is not > 0) return shown with { Verdict = Codes.FixtureDiagnosis(f.Damaged ? DiagDamaged : DiagNotActive) };
        if (f.JobStatus == JobProcessing) return shown with { Verdict = RunningNow };

        Func<DiagDevice, int>? verdictOf = f.JobType switch
        {
            MachiningJob => m => MachineVerdict(f, m, s),
            WorkSettingJob => w => StationVerdict(f, w, s),
            _ => null,
        };
        if (verdictOf is null)
            return shown with { Verdict = $"{Codes.JobType(f.JobType)} job: not checked by this tool" };

        var deviceType = f.JobType == MachiningJob ? MachineType : StationType;
        var devices = feasible.Where(d => d.Type == deviceType).ToList();
        var verdicts = devices.Select(d => (Device: d, Code: verdictOf(d))).ToList();
        var combined = Combine(verdicts.Select(v => v.Code).ToList(), f.JobType == MachiningJob ? DiagMcNoFeasible : DiagWssNoFeasible);
        return shown with
        {
            Verdict = Codes.FixtureDiagnosis(combined),
            Devices = verdicts.Select(v => new DeviceVerdict(v.Device.Name, DeviceVerdictWords(v.Device, v.Code))).ToList(),
        };
    }

    private static string DeviceVerdictWords(DiagDevice d, int code)
    {
        var words = Codes.FixtureDiagnosis(code);
        var alarms = code == DiagMcDeviceError ? d.PmcAlarms : code == DiagWssDeviceError ? d.AllAlarms : 0;
        return alarms > 0 ? $"{words}: {alarms} open alarm{(alarms == 1 ? "" : "s")}" : words;
    }

    /// <summary>What the console also checks but this tool can't: the cell keeps it in memory, or (vehicle reach) this
    /// tool doesn't follow it. Only the ones that apply to the pallet's jobs.</summary>
    internal static List<string> DiagNotChecked(IReadOnlyList<DiagFixture> fixtures, IReadOnlyDictionary<string, string>? system)
    {
        var jobs = fixtures.Where(f => f.JobId is > 0 && f.JobStatus != JobProcessing).ToList();
        var list = new List<string>
        {
            "whether each device is system ready beyond its connection, emergency stop and system mode (kept in memory)",
            "which vehicle can reach each device (dedicated vehicles)",
        };
        if (jobs.Any(f => f.JobType == MachiningJob))
        {
            if (ToInt(system?.GetValueOrDefault("ToolManagementType")) == ToolResource)
                list.Add("the tool set for the job's program on each machine (kept in memory; see get_part_routing)");
            list.Add("CSG files and measuring data the job waits for");
            list.Add("operator-call tools in the program while off duty");
            if (ToInt(system?.GetValueOrDefault("ExtraToolPalletTransport")) is > 0 && jobs.Any(f => f.ExtraToolPallet is > 0))
                list.Add("the extra tool pallet the job needs");
        }
        if (jobs.Any(f => f.JobType == WorkSettingJob)) list.Add("material groups and material pallets at the work-set station");
        return list;
    }

    // Optional columns a check depends on, and the words notAvailable uses when one is missing.
    private static readonly (string Table, string Column, string Words)[] DiagColumns =
    [
        ("PalletFixtures", "Suspend", "fixture machining suspend"), ("PalletFixtures", "WorkSetHold", "fixture work-set hold"),
        ("PalletFixtures", "Damaged", "fixture damaged flag"), ("PalletFixtures", "PreMcSuspend", "pre-machining suspend"),
        ("PalletFixtures", "OffDutyProcess", "off-duty process flag"), ("PalletFixtures", "OperatorCallStatus", "fixture operator-call setting"),
        ("Orders", "MachiningSuspended", "order machining suspend"), ("MachineJobs", "OperatorCall", "the job's operator call"),
        ("MachineStatus", "SystemMode", "machine system mode"), ("MachineStatus", "DeviceOpen", "machine open or closed"),
        ("MachineStatus", "DeviceTransProhibit", "transport cancel at machines"),
        ("WorkSetStatus", "SystemMode", "station system mode"), ("WorkSetStatus", "DeviceOpen", "station open or closed"),
        ("WorkSetStatus", "DeviceTransProhibit", "transport cancel at stations"),
    ];

    /// <summary>The pallet (cap-free: one row), then its fixtures with their current job, then the feasible devices of
    /// those jobs.</summary>
    internal static string DiagnosisSql(SchemaInfo schema)
    {
        const string pf = "PalletFixtures";
        string Location(string column) => schema.Has("PalletLocation", column)
            ? $"(SELECT TOP 1 l.{column} FROM PalletLocation l WHERE l.PalletID = p.PalletID) AS {column}"
            : $"CAST(NULL AS int) AS {column}";
        var pallet = string.Join(", ",
            "p.PalletID", Opt(schema, "Pallets", "p", "PalletStatus"), Opt(schema, "Pallets", "p", "WorkStatus"),
            Opt(schema, "Pallets", "p", "PalletType"),
            Location("CurDeviceType"), Location("CurDeviceNumber"), Location("CurDevicePosition"));
        var fixtures = string.Join(", ",
            Opt(schema, pf, "pf", "FixtureNumber"),
            Opt(schema, pf, "pf", "Suspend", type: "bit"), Opt(schema, pf, "pf", "WorkSetHold", type: "bit"),
            Opt(schema, pf, "pf", "Damaged", type: "bit"), Opt(schema, pf, "pf", "PreMcSuspend", type: "bit"),
            Opt(schema, pf, "pf", "PreMcSuspendReason"), Opt(schema, pf, "pf", "OffDutyProcess", type: "bit"),
            Opt(schema, pf, "pf", "OperatorCallStatus"), Opt(schema, pf, "pf", "LastOneShotOpeCallProcessID"),
            Opt(schema, "FixtureRun", "fr", "CurJobID"), Opt(schema, "FixtureRun", "fr", "CurJobStatus"),
            Opt(schema, "FixtureRun", "fr", "CurProcessID"),
            Opt(schema, "Orders", "o", "OrderName", type: Text), Opt(schema, "Orders", "o", "MachiningSuspended", type: "bit"),
            Opt(schema, "Processes", "pr", "ProcessNumber"),
            Opt(schema, "Jobs", "j", "JobType"), Opt(schema, "Jobs", "j", "JobNumber"),
            Opt(schema, "MachineJobs", "mj", "OperatorCall", "JobOperatorCall"), Opt(schema, "MachineJobs", "mj", "ExtraToolPallet"));
        var order = schema.Has(pf, "FixtureNumber") ? "pf.FixtureNumber" : "pf.PalletFixtureID";
        return $"""
            SELECT TOP 1 {pallet} FROM Pallets p WHERE p.PalletNumber = @pallet;
            SELECT {fixtures}
            FROM PalletFixtures pf
            LEFT JOIN FixtureRun fr ON fr.PalletFixtureID = pf.PalletFixtureID
            LEFT JOIN Orders o ON o.OrderID = fr.CurOrderID
            LEFT JOIN Processes pr ON pr.ProcessID = fr.CurProcessID
            LEFT JOIN Jobs j ON j.JobID = fr.CurJobID
            LEFT JOIN MachineJobs mj ON mj.JobID = fr.CurJobID
            WHERE pf.PalletID = (SELECT TOP 1 p.PalletID FROM Pallets p WHERE p.PalletNumber = @pallet)
            ORDER BY {order};
            SELECT DISTINCT f.JobID, f.FeasibleDeviceID
            FROM JobFeasibleDevices f
            WHERE f.JobID IN (SELECT fr.CurJobID FROM PalletFixtures pf JOIN FixtureRun fr ON fr.PalletFixtureID = pf.PalletFixtureID
                              WHERE pf.PalletID = (SELECT TOP 1 p.PalletID FROM Pallets p WHERE p.PalletNumber = @pallet));
            """;
    }

    /// <summary>Open alarms per device: non-warning PMC alarms, and every open PMC and CNC alarm or warning.</summary>
    internal static string DiagnosisAlarmSql(SchemaInfo schema)
    {
        var warning = schema.Has("PMCAlarms", "IsWarning") ? "IsWarning = 1" : "1 = 0";
        return $"""
            SELECT DeviceID, SUM(Pmc) AS PmcAlarms, COUNT(*) AS AllAlarms FROM (
                SELECT DeviceID, CASE WHEN {warning} THEN 0 ELSE 1 END AS Pmc FROM PMCAlarms WHERE ResetTime IS NULL
                UNION ALL
                SELECT DeviceID, 0 FROM CNCAlarms WHERE ResetTime IS NULL) a
            GROUP BY DeviceID;
            """;
    }

    public async Task<PalletDiagnosis> GetPalletDiagnosisAsync(int pallet, CancellationToken ct)
    {
        await using var s = await OpenAsync(ct);
        var schema = s.Schema;
        schema.Require("Pallets", "PalletFixtures", "FixtureRun", "Jobs", "JobFeasibleDevices", "MachineStatus", "WorkSetStatus",
            "PMCAlarms", "CNCAlarms");

        var system = await SystemParametersAsync(s, ct,
            "TransportCancel", "OrderMachiningSuspend", "DutyManagement", "OffDutyOpeCallPalletTransport",
            "ProcessOpeCallOneshotType", "ToolManagementType", "ExtraToolPalletTransport");
        var runtime = await UserSysDataAsync(s, ct, "DutyMode");
        var settings = DiagSettingsFrom(system, runtime);
        var notAvailable = DiagColumns.Where(c => schema.HasTable(c.Table) && !schema.Has(c.Table, c.Column)).Select(c => c.Words).ToList();
        if (system is null) notAvailable.Add("the cell's settings (transport cancel, order machining suspend, duty management)");
        if (runtime is null) notAvailable.Add("the duty mode (off-duty checks)");

        // Device states and open alarms.
        var devices = new Dictionary<int, DiagDevice>();
        foreach (var d in s.Devices.Where(d => d.Type is MachineType or StationType)) devices[d.Id] = new DiagDevice(d.Name, d.Type);
        foreach (var table in new[] { "MachineStatus", "WorkSetStatus" })
        {
            using var cmd = Command(s.Connection, StatusSql(schema, table, table == "MachineStatus" ? MachineSpots : StationSpots));
            using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                if (!devices.TryGetValue(Int(r, "DeviceID") ?? 0, out var d)) continue;
                devices[Int(r, "DeviceID")!.Value] = d with
                {
                    Connected = Bool(r, "Connected"), Emergency = Bool(r, "Emergency"), SystemMode = Bool(r, "SystemMode"),
                    Open = Bool(r, "DeviceOpen"), Skip = Bool(r, "DeviceSkip"), TransProhibit = Bool(r, "DeviceTransProhibit"),
                };
            }
        }
        using (var cmd = Command(s.Connection, DiagnosisAlarmSql(schema)))
        {
            using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var id = Int(r, "DeviceID") ?? 0;
                if (devices.TryGetValue(id, out var d))
                    devices[id] = d with { PmcAlarms = Int(r, "PmcAlarms") ?? 0, AllAlarms = Int(r, "AllAlarms") ?? 0 };
            }
        }

        // The pallet, its fixtures and their jobs' feasible devices.
        using var main = Command(s.Connection, DiagnosisSql(schema));
        main.Parameters.Add("@pallet", System.Data.SqlDbType.Int).Value = pallet;
        using var q = await main.ExecuteReaderAsync(ct);
        if (!await q.ReadAsync(ct)) throw new ArgumentException($"No pallet numbered {pallet}. get_pallets lists the pallets.");
        var type = Int(q, "PalletType");
        var status = PalletStatusWords(type, Int(q, "PalletStatus"));
        var workStatus = PalletWorkStatusWords(type, Int(q, "WorkStatus"));
        var location = Location(s, Int(q, "CurDeviceType"), Int(q, "CurDeviceNumber"), Int(q, "CurDevicePosition"));

        await q.NextResultAsync(ct);
        var fixtures = new List<DiagFixture>();
        while (await q.ReadAsync(ct))
        {
            fixtures.Add(new DiagFixture(Int(q, "FixtureNumber"))
            {
                JobId = Int(q, "CurJobID"), JobType = Int(q, "JobType"), JobNumber = Int(q, "JobNumber"),
                JobStatus = Int(q, "CurJobStatus"), ProcessId = Int(q, "CurProcessID"), Process = Int(q, "ProcessNumber"),
                Order = NonEmpty(Str(q, "OrderName")), OrderMachiningSuspended = Bool(q, "MachiningSuspended") == true,
                Suspend = Bool(q, "Suspend") == true, Hold = Bool(q, "WorkSetHold") == true, Damaged = Bool(q, "Damaged") == true,
                PreMcSuspend = Bool(q, "PreMcSuspend") == true, PreMcReason = Int(q, "PreMcSuspendReason"),
                OffDutyProcess = Bool(q, "OffDutyProcess") == true, OperatorCallStatus = Int(q, "OperatorCallStatus"),
                LastOneShotProcessId = Int(q, "LastOneShotOpeCallProcessID"), JobOperatorCall = Int(q, "JobOperatorCall"),
                ExtraToolPallet = Int(q, "ExtraToolPallet"),
            });
        }
        await q.NextResultAsync(ct);
        var feasible = new Dictionary<int, List<DiagDevice>>();
        while (await q.ReadAsync(ct))
        {
            var job = Int(q, "JobID") ?? 0;
            if (!devices.TryGetValue(Int(q, "FeasibleDeviceID") ?? 0, out var d)) continue;
            if (!feasible.TryGetValue(job, out var list)) feasible[job] = list = [];
            list.Add(d);
        }

        var diagnosed = fixtures
            .Select(f => DiagnoseFixture(f, f.JobId is { } j ? feasible.GetValueOrDefault(j) ?? [] : [], settings))
            .ToList();
        return new PalletDiagnosis(s.Now, pallet, status, workStatus, location, diagnosed, DiagNotChecked(fixtures, system))
        {
            NotAvailable = notAvailable.Count > 0 ? notAvailable : null,
        };
    }
}
