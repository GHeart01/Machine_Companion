using MakinoMCP.Core.Cell;

namespace MakinoMCP.Tests;

/// <summary>A cell with sample data, for tool tests. <see cref="Fail"/> makes every call throw;
/// <see cref="Hang"/> makes every call wait until cancelled.</summary>
internal sealed class FakeMakinoReader : IMakinoReader
{
    public static readonly DateTime Now = new(2026, 11, 2, 10, 30, 0);

    private static readonly Dictionary<int, string> Devices = new()
    {
        [1] = "MC1",
        [2] = "MC2",
        [3] = "Vehicle 1",
        [4] = "WSS1",
    };

    public Func<Exception>? Fail { get; init; }
    public bool Hang { get; init; }
    public bool CanWrite { get; init; }

    private async Task<T> Answer<T>(Func<T> make, CancellationToken ct, string? device = null)
    {
        if (Hang) await Task.Delay(Timeout.Infinite, ct);
        if (Fail is not null) throw Fail();
        SqlMakinoReader.FindDevice(Devices, device);
        return make();
    }

    public Task<CellOverview> GetOverviewAsync(CancellationToken ct) => Answer(() => new CellOverview(Now,
    [
        new DeviceState("MC1", "machine", true, false, "memory", "machining", null,
            [new PalletSpot("table", 7, "processing"), new PalletSpot("next front", 9, "reserved")], 1, 0, Now.AddMinutes(-12), [])
        {
            Status = "busy", RecoveryRequest = false, ToolStatus = "lacking tools", CncRun = "run", OperatorCall = "released",
            RunJob = "machining", Program = new NcProgram("O1234", "O0100"),
        },
        new DeviceState("Vehicle 1", "vehicle", true, false, null, "load/unload", null,
            [new PalletSpot("buffer 1", 7, "")], 0, 0, Now.AddHours(-1), ["vehicle mode", "buffer status"])
        {
            Status = "busy", RecoveryRequest = false, TransportFrom = "WSS1", TransportTo = "MC1",
        },
    ]) { HistoryDays = 90 }, ct);

    public Task<AlarmList> GetAlarmsAsync(int hours, string? device, bool openOnly, bool includeWarnings, CancellationToken ct) =>
        Answer(() => new AlarmList(Now, false,
        [
            new Alarm("PMC", "MC1", 1012, "alarm", "Coolant level low", Now.AddMinutes(-30), null, 30),
        ]), ct, device);

    public Task<AlarmSummary> GetAlarmSummaryAsync(int days, string? device, CancellationToken ct) =>
        Answer(() => new AlarmSummary(Now, false,
        [
            new AlarmSummaryRow("MC1", "PMC", 1012, "Coolant level low", 4, 95.5, Now.AddDays(-6), Now.AddMinutes(-30)),
        ]), ct, device);

    public Task<UtilizationList> GetUtilizationAsync(int days, string? device, CancellationToken ct) =>
        Answer(() => new UtilizationList(Now, false,
        [
            new UtilizationDay("MC1", Now.Date, 0, 2, 6, 0.5, 0.25, 68.6, null),
        ]), ct, device);

    public Task<ActivityList> GetRecentActivityAsync(int hours, string? device, CancellationToken ct) =>
        Answer(() => new ActivityList(Now, false,
        [
            new ActivityItem("machining", "MC1", Now.AddHours(-2), Now.AddHours(-1), 7, "O1234", "normal"),
        ]), ct, device);

    public Task<OrderList> GetOrdersAsync(bool activeOnly, bool includeArchive, int archiveDays, CancellationToken ct) =>
        Answer(() => new OrderList(Now, false,
        [
            new OrderInfo("ORDER-001", "CONTOSO-BRACKET", "in progress", 10, null, Now.AddDays(1), null)
            {
                CompletedQty = 40, ScrapQty = 2, RemainingQty = 57,
            },
            new OrderInfo("ORDER-002", "FABRIKAM-PLATE", "in progress", 5, true, null, null)
            {
                CompletedQty = 0, ScrapQty = 0, RemainingQty = 5,
            },
        ]), ct);

    public Task<PalletList> GetPalletsAsync(int? pallet, bool includeFixtures, CancellationToken ct) =>
        Answer(() => new PalletList(Now, false,
        [
            new PalletInfo(7, "P7", "waiting for machining", "not completed (no result yet)", "MC1, table",
                pallet is null && !includeFixtures ? ["fixture 1: machining suspended"] : null)
            {
                FixtureDetails = pallet is null && !includeFixtures ? null :
                [
                    new FixtureDetail(1, "FIXTURE-A")
                    {
                        Order = "ORDER-001", Part = "CONTOSO-BRACKET", Process = 1, Job = "machining, job 2",
                        JobStatus = "waiting for processing", States = ["machining suspended"],
                    },
                ],
            },
        ]), ct);

    public Task<ToolList> GetToolsAsync(string? device, string? filter, int? tool, CancellationToken ct) =>
        Answer(() => new ToolList(Now, false, SqlMakinoReader.ToolFilter(filter, tool), "mm",
        [
            new DeviceToolCount("MC1", 5, new Dictionary<string, int> { ["ready"] = 3, ["life warning"] = 1, ["alarm"] = 1 }),
        ],
        [
            new ToolInfo(1001, 101, "CONTOSO 10MM END MILL", "MC1 pot 20", "life warning",
                [new CutterInfo(1, "life warning") { Life = SqlMakinoReader.Life(3, 4500, 3600, 4140, false), Length = 101.25 }])
            {
                Kind = "end mill",
            },
            new ToolInfo(1002, 102, "CONTOSO PROBE", "MC1 pot 1", "alarm",
                [new CutterInfo(1, "alarm") { Alarms = ["BTS alarm (broken tool)"], Life = SqlMakinoReader.Life(0, 0, 0, 0, false) }])
            {
                Flags = ["alarm tool"],
            },
        ]), ct, device);

    public Task<ToolChangeList> GetToolChangesAsync(int days, string? device, int? tool, bool includeLifeUpdates, CancellationToken ct) =>
        Answer(() => new ToolChangeList(Now, false, "mm",
        [
            new ToolChange(Now.AddHours(-3), "MC1", 1001, 101, ["length 101.234 → 101.250 mm"]),
        ]) { LifeUpdatesLeftOut = !includeLifeUpdates, LifeWarningTransitions = includeLifeUpdates ? null : 4 }, ct, device);

    public Task<ToolAlarmList> GetToolAlarmsAsync(int days, string? device, int? tool, CancellationToken ct) =>
        Answer(() => new ToolAlarmList(Now, false, "tool alarm history", "mm",
        [
            new ToolAlarmEvent(Now.AddHours(-1), "MC1", 1002, 102, ["tool life alarm"]) { Pot = 1 },
        ], []), ct, device);

    public Task<ToolMovementList> GetToolMovementsAsync(int days, string? device, int? tool, CancellationToken ct) =>
        Answer(() => new ToolMovementList(Now, false,
        [
            new ToolMovement(Now.AddHours(-2), "out", 1003, 103, "MC1 pot 7", "CRIB1 (tool crib)", "ready")
            {
                Name = "CONTOSO 6MM DRILL", UsedPercent = 40,
            },
        ]), ct, device);

    public Task<PartRouting> GetPartRoutingAsync(string part, string? revision, bool includeTools, CancellationToken ct) =>
        Answer(() =>
        {
            if (string.IsNullOrWhiteSpace(part)) throw new ArgumentException(SqlMakinoReader.NoPartGiven);
            if (part == "CONTOSO")
            {
                return new PartRouting(Now, part, false)
                {
                    Candidates = [new PartCandidate("CONTOSO-BRACKET-OP1", 1), new PartCandidate("CONTOSO-BRACKET-OP2", 1)],
                    Note = SqlMakinoReader.SeveralPartsNote,
                };
            }
            var tool = new ProgramTool(501, 1)
            {
                Name = "CONTOSO 12MM END MILL", Cutting = "12.5 min",
                Usable = new Dictionary<string, int> { ["MC1"] = 2, ["MC2"] = 0 }, MissingOn = ["MC2"],
            };
            return new PartRouting(Now, part, false)
            {
                Part = new PartRoute("CONTOSO-BRACKET", "ready",
                [
                    new ProcessRoute(1, "ready", [new OperationRoute(1, 1)],
                    [
                        new JobRoute(1, "work setting") { WorkSet = "load", FeasibleDevices = ["WSS1"] },
                        new JobRoute(2, "machining")
                        {
                            Program = "CONTOSO-BRACKET-OP1", Minutes = 42.5, FeasibleDevices = ["MC1", "MC2"], Tools = [tool],
                        },
                    ])
                    {
                        Fixtures = ["pallet 7 fixture 1"],
                    },
                ]),
                ToolCheck = SqlMakinoReader.ToolCheckNote,
            };
        }, ct);

    public Task<ProductionList> GetProductionAsync(int days, string? order, string? part, bool includeIdle, bool includeCycleTime, CancellationToken ct) =>
        Answer(() => new ProductionList(Now, false, days,
        [
            new OrderProduction("ORDER-001", "CONTOSO-BRACKET", "active", 100)
            {
                CompletedQty = 40, ScrapQty = 2, RemainingQty = 57, InProcessQty = 1, ClampedQty = 1, CompletedInWindow = 6,
                ScrapInWindow = 0,
                Daily = order is null && part is null ? null : [new DailyQty(new DateOnly(2026, 11, 1), 6, 0)],
            },
        ])
        {
            OtherActiveOrders = includeIdle ? null : 3,
            Daily = order is null && part is null ? [new DailyQty(new DateOnly(2026, 11, 1), 6, 0)] : null,
        }, ct);

    public Task<AttentionList> GetAttentionAsync(bool includeParked, CancellationToken ct) =>
        Answer(() => new AttentionList(Now, false, new Dictionary<string, int> { ["recovery request"] = 1, ["machining suspended"] = 1 },
        [
            new AttentionItem("device", "recovery request") { Device = "WSS1" },
            .. includeParked
                ? [new AttentionItem("fixture", "machining suspended") { Pallet = 7, Fixture = 1, Detail = "waiting for material" }]
                : Array.Empty<AttentionItem>(),
        ], [])
        {
            MachineToolStatus = new Dictionary<string, string> { ["MC1"] = "lacking tools" },
            Parked = new ParkedFixtures(1, [new ParkedGroup("pallet stocker", "machining suspended", 1)]),
        }, ct);

    public Task<PalletDiagnosis> GetPalletDiagnosisAsync(int pallet, CancellationToken ct) =>
        Answer(() => pallet != 7 ? throw new ArgumentException($"No pallet numbered {pallet}.") : new PalletDiagnosis(Now, 7,
            "waiting for machining", "not completed (no result yet)", "pallet stocker 3",
            [
                new FixtureDiagnosis(1, "machine closed")
                {
                    Order = "ORDER-001", Process = 1, Job = "machining, job 2", JobStatus = "waiting for processing",
                    Devices = [new DeviceVerdict("MC1", "machine closed")],
                },
            ], ["which vehicle can reach each device (dedicated vehicles)"]), ct);

    public Task<LoginRightsInfo> GetLoginRightsAsync(CancellationToken ct) =>
        Answer(() => new LoginRightsInfo(CanWrite, Devices.Count, CanWrite ? "can write" : "read only"), ct);
}
