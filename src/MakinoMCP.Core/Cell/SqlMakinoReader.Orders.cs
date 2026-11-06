namespace MakinoMCP.Core.Cell;

// Orders (Orders, Parts, quantities as get_production counts them, the archive in OrderResults) and pallets (Pallets,
// PalletLocation, PalletFeasibleDevice, and per fixture PalletFixtures + FixtureRun with the current job from Jobs,
// labelled from Fixtures / FixtureProcesses).
public sealed partial class SqlMakinoReader
{
    // PalletLocation.CurDeviceType 0: the pallet was taken out of the system (an offline pallet).
    private const int OutOfSystem = 0;
    internal const string OutOfSystemWords = "out of system (offline pallet)";

    // Orders.Status codes left out of an "active only" list: undefined, complete and cancelled.
    private const string FinishedOrderStatuses = "0, 5, 6";

    // Orders.Status (OrderStatus) values the order notes depend on.
    private const int OrderInactive = 1;
    private const int OrderActive = 2;
    private const int OrderRunning = 3;
    private const int OrderSuspended = 4;
    private const int OrderNoQuantity = 7;
    private const int OrderWaitMaterial = 8;

    /// <summary>How close an order's last update must be to its registration to count as "never changed since".</summary>
    private static readonly TimeSpan UntouchedSinceCreated = TimeSpan.FromMinutes(1);

    private const int DefaultArchiveDays = 30;

    internal const string DashboardFromCell =
        "the cell's dashboard (running: fewer parts ready than still to finish; waiting for material: none left to start and none being worked on)";

    internal const string DashboardDerived =
        "worked out as the console's order screen does (running: parts in process; waiting for material: none left to start), since this cell has no dashboard order status";

    private static readonly string[] OrderParameterNames =
    [
        "OrderMachiningSuspend", "OrderIncludesScrap", "NewOrderSuspended", "AutoOrderDelete", "AutoOrderDeleteForCancel",
        "AutoDeleteWaitMaterial", "ComprehensiveDelete",
    ];

    public async Task<OrderList> GetOrdersAsync(bool activeOnly, bool includeArchive, int archiveDays, CancellationToken ct)
    {
        await using var s = await OpenAsync(ct);
        s.Schema.Require("Orders", "Parts");
        var settings = await SystemParametersAsync(s, ct, OrderParameterNames);
        var machiningSuspendCounts = MachiningSuspendCounts(settings);
        var newOrdersSuspended = ToInt(settings?.GetValueOrDefault("NewOrderSuspended")) == 1;
        var hasStatus2 = s.Schema.Has("Orders", "Status2");

        var rows = new List<OrderInfo>();
        using (var cmd = Command(s.Connection, OrdersSql(s.Schema, activeOnly)))
        {
            AddTake(cmd);
            using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var revision = Str(r, "Revision");
                var order = new OrderInfo(
                    Str(r, "OrderName"),
                    revision.Length > 0 ? $"{Str(r, "PartName")} rev {revision}" : Str(r, "PartName"),
                    Codes.OrderStatus(Int(r, "Status")),
                    Int(r, "Quantity") ?? 0,
                    machiningSuspendCounts && Int(r, "MachiningSuspended") is > 0 ? true : null,
                    Date(r, "DueDate"),
                    Date(r, "CompleteDate"))
                {
                    CompletedQty = Int(r, "CompletedQty"), ScrapQty = Int(r, "ScrapQty"), RemainingQty = Int(r, "RemainingQty"),
                    InProcessQty = Int(r, "InProcessQty"),
                    Priority = Int(r, "Priority") is { } p and not 0 ? p : null,
                    Comment = NonEmpty(OneLine(Str(r, "Comment"))),
                    QuickData = Int(r, "QuickEntry") is > 0 ? true : null,
                };
                rows.Add(OrderNotes(order, new OrderFacts(Int(r, "Status"), hasStatus2 ? Int(r, "Status2") : null, Date(r, "StartDate"),
                    Date(r, "RegistrationDate"), Date(r, "UpdateDate")), s.Now, newOrdersSuspended));
            }
        }
        var (orders, capped) = Cap(rows);
        var notAvailable = OrderQuantityColumns(s.Schema, inProcess: false) switch
        {
            null => [QuantitiesNotAvailable],
            { Missing.Count: > 0 } quantities => quantities.Missing.ToList(),
            _ => new List<string>(),
        };
        if (!s.Schema.Has("Orders", "MachiningSuspended")) notAvailable.Add("order machining suspend");

        IReadOnlyList<ArchivedOrder>? archive = null;
        var archiveCapped = false;
        var window = Days(archiveDays < 1 ? DefaultArchiveDays : archiveDays);
        if (includeArchive)
        {
            if (ArchiveSql(s.Schema) is { } sql)
            {
                var found = new List<ArchivedOrder>();
                using var cmd = Command(s.Connection, sql);
                AddTake(cmd);
                cmd.Parameters.Add("@from", System.Data.SqlDbType.DateTime).Value = s.Now.Date.AddDays(1 - window);
                using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    found.Add(new ArchivedOrder(Str(r, "OrderName"), PartLabel(Str(r, "PartName"), Str(r, "Revision")))
                    {
                        Quantity = Int(r, "RequiredQuantity") is { } q and not 0 ? q : null,
                        CompletedQty = Int(r, "NormalQuantity") is { } n and not 0 ? n : null,
                        ScrapQty = Int(r, "ScrapQuantity") is { } sc and not 0 ? sc : null,
                        Completed = Date(r, "CompleteDate"),
                        Deleted = Int(r, "IsDelete") is > 0 ? true : null,
                        Recorded = Date(r, "ResultDate"),
                    });
                }
                (archive, archiveCapped) = Cap(found);
            }
            else notAvailable.Add(ArchiveNotAvailable);
        }

        return new OrderList(s.Now, capped, orders)
        {
            CompleteIncludesScrap = CompleteIncludesScrap(settings),
            NotAvailable = notAvailable.Count > 0 ? notAvailable : null,
            DashboardStatusFrom = orders.Any(o => o.DashboardStatus is not null) ? hasStatus2 ? DashboardFromCell : DashboardDerived : null,
            AutoDelete = activeOnly && !includeArchive ? null : AutoDeleteWords(settings),
            Archive = archive,
            ArchiveDays = includeArchive ? window : null,
            ArchiveCapped = archiveCapped ? true : null,
        };
    }

    internal const string ArchiveNotAvailable = "the order archive (finished and deleted orders)";

    /// <summary>The order columns the notes are worked out from.</summary>
    internal sealed record OrderFacts(int? Status, int? Status2, DateTime? Start, DateTime? Registered, DateTime? Updated);

    /// <summary>An order with its dashboard word, start date and what it waits for. DashboardStatus: Status2 when the
    /// cell keeps it (shown only when it differs from the stored status), else the console's own rule for an active
    /// order (parts in process: running; nothing left to start: waiting for material). Start: only when still to come
    /// (dates compared, since an order activates at midnight of its start date) or the order is inactive.</summary>
    internal static OrderInfo OrderNotes(OrderInfo order, OrderFacts f, DateTime now, bool newOrdersSuspended)
    {
        int? dashboard = f.Status2;
        if (f.Status2 is null && f.Status == OrderActive)
        {
            if (order.InProcessQty is > 0) dashboard = OrderRunning;
            else if (order.RemainingQty is <= 0) dashboard = OrderWaitMaterial;
        }
        var shownDashboard = dashboard is { } d && d != f.Status && d != 0 ? Codes.OrderStatus(d) : null;
        var start = f.Start is { } st && st.Year > 1900 && (st.Date > now.Date || f.Status == OrderInactive) ? st : (DateTime?)null;

        string? waiting = f.Status switch
        {
            OrderInactive when start is { } at && at.Date > now.Date => $"starts {at:yyyy-MM-dd}",
            OrderNoQuantity => "no required quantity",
            OrderSuspended when newOrdersSuspended && f.Registered is { } reg && f.Updated is { } upd
                                && (upd - reg).Duration() <= UntouchedSinceCreated =>
                "suspended by an operator, possibly since it was created (this cell creates new orders suspended) and never released",
            OrderSuspended => "suspended by an operator (release it from the console's order menu)",
            OrderActive when dashboard == OrderWaitMaterial => "waiting for material input",
            OrderActive when dashboard is (null or OrderActive) && order.RemainingQty is > 0 && order.InProcessQty is 0 =>
                "has material, but no fixture has taken it yet (see get_part_routing or get_pallet_diagnosis)",
            _ => null,
        };
        return order with { DashboardStatus = shownDashboard, Start = start, WaitingFor = waiting };
    }

    /// <summary>Whether an order's MachiningSuspended flag does anything: only with the cell's OrderMachiningSuspend
    /// setting at 1 (0 or no row, the default, and the console hides the column and ignores the flag). Settings this
    /// login can't read: shown, since it can't be ruled out.</summary>
    internal static bool MachiningSuspendCounts(IReadOnlyDictionary<string, string>? settings) =>
        settings is null || ToInt(settings.GetValueOrDefault("OrderMachiningSuspend")) == 1;

    /// <summary>The cell's OrderIncludesScrap setting: complete counts scrap too (1, and the default when there is no
    /// row) or good parts only (0); null when the settings can't be read.</summary>
    internal static bool? CompleteIncludesScrap(IReadOnlyDictionary<string, string>? settings) =>
        settings is null ? null : ToInt(settings.GetValueOrDefault("OrderIncludesScrap")) != 0;

    /// <summary>The cell's automatic order deletion in words (AutoOrderDelete, AutoOrderDeleteForCancel,
    /// AutoDeleteWaitMaterial in hours, 0 = off; ComprehensiveDelete). The cell deletes only orders with nothing left in
    /// work and never those of quick-data parts. Null when the settings can't be read.</summary>
    internal static string? AutoDeleteWords(IReadOnlyDictionary<string, string>? settings)
    {
        if (settings is null) return null;
        int Get(string name) => ToInt(settings.GetValueOrDefault(name)) ?? 0;
        var complete = Get("AutoOrderDelete");
        var waitMaterial = Get("AutoDeleteWaitMaterial");
        var rules = new List<string>();
        if (complete > 0)
        {
            rules.Add($"complete orders {complete} h after they complete");
            if (Get("AutoOrderDeleteForCancel") == 1) rules.Add($"cancelled orders {complete} h after their due date");
        }
        if (waitMaterial > 0)
            rules.Add($"active orders that never got material and made nothing {waitMaterial} h after their start date (cancelled, then deleted)");
        var text = rules.Count == 0
            ? "The cell deletes no orders automatically."
            : $"The cell deletes {string.Join("; ", rules)}, once nothing of the order is left in work and never for quick-data parts.";
        if (Get("ComprehensiveDelete") == 1) text += " Deleting an order also deletes its part data and the programs no other part uses.";
        return text + " An order missing from the list may have been deleted: includeArchive lists finished and deleted ones, " +
               "and the daily counts in get_production outlive the order.";
    }

    /// <summary>Orders with their part and quantities (the same as get_production's, in process included), latest due
    /// first. Without a Status column activeOnly can't filter.</summary>
    internal static string OrdersSql(SchemaInfo schema, bool activeOnly)
    {
        List<string> cols =
        [
            Opt(schema, "Orders", "o", "OrderName", type: Text),
            Opt(schema, "Parts", "pa", "PartName", type: Text),
            Opt(schema, "Parts", "pa", "Revision", type: Text),
            Opt(schema, "Parts", "pa", "QuickEntry"),
            Opt(schema, "Orders", "o", "Status"),
            Opt(schema, "Orders", "o", "Status2"),
            Opt(schema, "Orders", "o", "Quantity"),
            Opt(schema, "Orders", "o", "Priority"),
            Opt(schema, "Orders", "o", "Comment", type: Text),
            Opt(schema, "Orders", "o", "MachiningSuspended"),
            Opt(schema, "Orders", "o", "StartDate", type: "datetime"),
            Opt(schema, "Orders", "o", "DueDate", type: "datetime"),
            Opt(schema, "Orders", "o", "CompleteDate", type: "datetime"),
            Opt(schema, "Orders", "o", "RegistrationDate", type: "datetime"),
            Opt(schema, "Orders", "o", "UpdateDate", type: "datetime"),
            .. OrderQuantityColumns(schema, inProcess: true)?.Columns ?? NoQuantityColumns(inProcess: true),
        ];
        var where = activeOnly && schema.Has("Orders", "Status") ? $"WHERE o.Status NOT IN ({FinishedOrderStatuses})" : "";
        var order = schema.Has("Orders", "DueDate") ? "o.DueDate DESC, o.OrderID DESC" : "o.OrderID DESC";
        return $"""
            SELECT TOP (@take) {string.Join(", ", cols)}
            FROM Orders o LEFT JOIN Parts pa ON pa.PartID = o.PartID
            {where}
            ORDER BY {order};
            """;
    }

    /// <summary>The order archive since @from (cap + 1, newest first): one row per order, a completed order that was
    /// later deleted (two archive rows, same registration) merged with Deleted set. Null when the table or the columns
    /// it is grouped and windowed by can't be read.</summary>
    internal static string? ArchiveSql(SchemaInfo schema)
    {
        const string t = "OrderResults";
        if (!schema.HasTable(t) || !schema.Has(t, "OrderName") || !schema.Has(t, "ResultDate")) return null;
        string[] keys = ["OrderName", "PartName", "Revision", "RegistrationDate"];
        var groupBy = string.Join(", ", keys.Where(k => schema.Has(t, k)).Select(k => $"r.{k}"));
        string Key(string column, string type) => schema.Has(t, column) ? $"r.{column}" : $"CAST(NULL AS {type}) AS {column}";
        string Max(string column, string type = "int") =>
            schema.Has(t, column) ? $"MAX({(type == "bit" ? $"CAST(r.{column} AS int)" : $"r.{column}")}) AS {column}" : $"CAST(NULL AS int) AS {column}";
        var cols = string.Join(", ",
            Key("OrderName", Text), Key("PartName", Text), Key("Revision", Text),
            Max("RequiredQuantity"), Max("NormalQuantity"), Max("ScrapQuantity"),
            schema.Has(t, "CompleteDate") ? "MAX(r.CompleteDate) AS CompleteDate" : "CAST(NULL AS datetime) AS CompleteDate",
            Max("IsDelete", "bit"), "MAX(r.ResultDate) AS ResultDate");
        return $"""
            SELECT TOP (@take) {cols}
            FROM OrderResults r
            WHERE r.ResultDate >= @from
            GROUP BY {groupBy}
            ORDER BY MAX(r.ResultDate) DESC;
            """;
    }

    // --- pallets ---

    private static readonly string[] PalletParameterNames =
    [
        "ProcessOpeCallOneshotType", "OffDutyProcessManagement", "ClampWaitingSuspendType", "FixtureToolLackInfo",
        "CheckLackToolsAllRemainingJobs",
    ];

    /// <summary>The cell's settings the fixture states follow. LackingComputed: as <see cref="LackingToolSetting"/>
    /// (null: unknown).</summary>
    internal sealed record FixtureSettings(bool OneShotFirstPallet, bool OffDutyProcesses, bool ClampWaitHold, bool? LackingComputed)
    {
        internal static FixtureSettings From(IReadOnlyDictionary<string, string>? settings)
        {
            int? Get(string name) => ToInt(settings?.GetValueOrDefault(name));
            return new FixtureSettings(
                Get("ProcessOpeCallOneshotType") == 1,
                settings is null || Get("OffDutyProcessManagement") is > 0,
                Get("ClampWaitingSuspendType") == 1,
                LackingToolSetting(settings).Computed);
        }
    }

    /// <summary>One fixture as read: its flags, its work (FixtureRun and the current job) and what the jobs ahead hold.
    /// JobCallAhead: a machining job from the current one on calls the operator by its own setting;
    /// MachiningAhead: there is a machining job from the current one on; CleaningBefore / DeburringBefore: such a job
    /// came before the current one in its process.</summary>
    internal sealed record FixtureRow(int? Fixture)
    {
        public string Name { get; init; } = "";
        public string Comment { get; init; } = "";
        public int? ProcessCount { get; init; }
        public string FixturePart { get; init; } = "";
        public string FixtureRevision { get; init; } = "";
        public int? FixtureProcess { get; init; }
        public bool Suspend { get; init; }
        public bool Hold { get; init; }
        public bool Damaged { get; init; }
        public bool PreMcSuspend { get; init; }
        public int? PreMcReason { get; init; }
        public bool OffDutyProcess { get; init; }
        public int? Reclamp { get; init; }
        public bool LackingTool { get; init; }
        public int? OperatorCallStatus { get; init; }
        public bool ClampWaitDisabled { get; init; }
        public int? JobId { get; init; }
        public int? JobStatus { get; init; }
        public int? JobType { get; init; }
        public int? JobNumber { get; init; }
        public int? WorkStatus { get; init; }
        public int? MachiningFinish { get; init; }
        public int? CleaningFinish { get; init; }
        public int? DeburringFinish { get; init; }
        public string Order { get; init; } = "";
        public string Part { get; init; } = "";
        public string Revision { get; init; } = "";
        public int? Process { get; init; }
        public bool JobCallAhead { get; init; }
        public bool MachiningAhead { get; init; }
        public bool CleaningBefore { get; init; }
        public bool DeburringBefore { get; init; }
    }

    private const int WorkScrap = 2;           // WorkStatus
    private const int WorkProcessCancel = 3;   // WorkStatus
    private const int WorkOrderCancel = 4;     // WorkStatus
    private const int FinishSkip = 13;         // CommandCompleteStatus
    private static readonly int[] CleaningFaults = [2, 5, 11];       // alarm, seating error, system abort
    private static readonly int[] DeburringFaults = [2, 5, 11, 12];  // the same and tool error

    /// <summary>The console's label for a fixture: its name; for a nameless fixture that holds exactly one process,
    /// "part | revision | process N" ("---" for no revision); else the pallet fixture's comment.</summary>
    internal static string FixtureLabel(FixtureRow f)
    {
        if (f.Name.Length > 0) return f.Name;
        if (f.ProcessCount == 1 && f.FixturePart.Length > 0)
            return $"{f.FixturePart} | {(f.FixtureRevision.Length > 0 ? f.FixtureRevision : "---")} | process {f.FixtureProcess}";
        return OneLine(f.Comment);
    }

    /// <summary>A fixture's state, as the console's pallet tooltip lists it, plus MakinoMCP's additions.</summary>
    internal static FixtureDetail FixtureOf(FixtureRow f, FixtureSettings s)
    {
        var hasWork = f.JobId is > 0;
        var states = new List<string>();
        if (f.Damaged) states.Add("damaged");
        if (f.Suspend) states.Add("machining suspended");
        if (f.Hold) states.Add("work-set hold");
        if (f.PreMcSuspend) states.Add(f.PreMcReason is > 0 ? $"pre-machining suspended ({Codes.PreMcSuspendReason(f.PreMcReason)})" : "pre-machining suspended");

        string? call = null;
        if (hasWork && f.JobStatus is not (null or 0))
        {
            if (f.OperatorCallStatus is 2 or 3 && f.MachiningAhead) call = Codes.FixtureOperatorCall(f.OperatorCallStatus);
            else if (f.JobCallAhead) call = "a remaining machining job calls the operator";
        }
        if (call is not null) states.Add("operator call");
        if (s.OffDutyProcesses && f.OffDutyProcess) states.Add("off-duty process");
        if (hasWork && f.CleaningBefore && f.CleaningFinish == FinishSkip) states.Add("cleaning skipped");
        if (hasWork && f.DeburringBefore && f.DeburringFinish == FinishSkip) states.Add("deburring skipped");
        // MakinoMCP's additions: the console's tooltip doesn't show these.
        if (f.Reclamp is > 0) states.Add($"reclamp {Codes.ReclampStatus(f.Reclamp)}");
        if (s.LackingComputed != false && f.LackingTool) states.Add("lacking tools");
        if (s.ClampWaitHold && f.ClampWaitDisabled) states.Add("clamp-wait hold disabled");

        string? work = null;
        if (hasWork)
        {
            work = f.WorkStatus switch
            {
                WorkScrap => "scrap" + ScrapDetail(f),
                WorkOrderCancel => "order cancelled",
                WorkProcessCancel => "process cancelled",
                _ => null,
            };
        }

        return new FixtureDetail(f.Fixture, NonEmpty(FixtureLabel(f)))
        {
            Processes = f.ProcessCount is > 1 ? f.ProcessCount : null,
            Empty = hasWork ? null : true,
            Order = hasWork ? NonEmpty(f.Order) : null,
            Part = hasWork && f.Part.Length > 0 ? PartLabel(f.Part, f.Revision) : null,
            Process = hasWork ? f.Process : null,
            Job = hasWork && f.JobType is not null ? Join(Codes.JobType(f.JobType), f.JobNumber is { } n ? $"job {n}" : "") : null,
            JobStatus = hasWork && f.JobStatus is not null ? Codes.FixtureJobStatus(f.JobStatus) : null,
            States = states.Count > 0 ? states : null,
            OperatorCall = call,
            WorkStatus = work,
        };
    }

    private static string ScrapDetail(FixtureRow f)
    {
        var how = new List<string>();
        if (f.MachiningFinish is { } m and not 0) how.Add($"machining: {Codes.MachiningFinish(m)}");
        if (f.CleaningBefore && f.CleaningFinish is { } c && CleaningFaults.Contains(c)) how.Add($"cleaning: {Codes.CommandFinish(c)}");
        if (f.DeburringBefore && f.DeburringFinish is { } d && DeburringFaults.Contains(d)) how.Add($"deburring: {Codes.CommandFinish(d)}");
        return how.Count > 0 ? $" ({string.Join("; ", how)})" : "";
    }

    /// <summary>A pallet's fixtures as short lines: number, the fixture's own name when it has one (the derived label
    /// is in the details only, to keep the whole list short), then empty / its states and work status. Nameless
    /// fixtures in a row with the same notes share one line ("fixtures 1-4: machining suspended").</summary>
    internal static List<string> FixtureLines(IReadOnlyList<(FixtureDetail Detail, string Name)> fixtures)
    {
        static string Notes(FixtureDetail d)
        {
            var notes = new List<string>();
            if (d.Empty == true) notes.Add("empty");
            notes.AddRange(d.States ?? []);
            if (d.WorkStatus is { } w) notes.Add(w);
            return string.Join(", ", notes);
        }

        var lines = new List<string>();
        for (var i = 0; i < fixtures.Count;)
        {
            var (first, name) = fixtures[i];
            var notes = Notes(first);
            var last = i;
            if (name.Length == 0 && first.Fixture is not null)
            {
                while (last + 1 < fixtures.Count && fixtures[last + 1].Name.Length == 0
                       && fixtures[last + 1].Detail.Fixture == fixtures[last].Detail.Fixture + 1 && Notes(fixtures[last + 1].Detail) == notes)
                    last++;
            }
            var head = last > i
                ? $"fixtures {first.Fixture}-{fixtures[last].Detail.Fixture}"
                : string.Join(' ', new[] { first.Fixture is { } n ? $"fixture {n}" : "fixture", name }.Where(t => t.Length > 0));
            lines.Add(notes.Length > 0 ? $"{head}: {notes}" : head);
            i = last + 1;
        }
        return lines;
    }

    public async Task<PalletList> GetPalletsAsync(int? pallet, bool includeFixtures, CancellationToken ct)
    {
        await using var s = await OpenAsync(ct);
        var schema = s.Schema;
        schema.Require("Pallets", "PalletLocation", "PalletFixtures", "Fixtures");
        var settingValues = await SystemParametersAsync(s, ct, PalletParameterNames);
        var settings = FixtureSettings.From(settingValues);
        var notAvailable = new List<string>();
        if (settingValues is null) notAvailable.Add("the cell's settings (the one-shot operator-call rule, off-duty processes, clamp-wait hold)");
        // With the cell's lacking-tool setting off, LackingTool is always 0: read as if the column weren't there.
        var fixtureSchema = schema;
        if (schema.Has("PalletFixtures", "LackingTool"))
        {
            if (settings.LackingComputed == false) fixtureSchema = schema.Without("PalletFixtures", "LackingTool");
            else if (settings.LackingComputed is null) notAvailable.Add(LackingToolsSettingUnknown);
        }
        var parts = PalletParts.Of(fixtureSchema);
        if (!parts.Run) notAvailable.Add("each fixture's work (order, process, job)");
        if (!parts.Labels) notAvailable.Add("fixture labels from the processes a nameless fixture holds");
        if (!parts.Feasible) notAvailable.Add("which machines and stations each pallet may go to");

        using var cmd = Command(s.Connection, PalletsSql(fixtureSchema, pallet is not null, settings.OneShotFirstPallet));
        AddTake(cmd);
        if (pallet is not null) cmd.Parameters.Add("@pallet", System.Data.SqlDbType.Int).Value = pallet.Value;

        var pallets = new List<(int Id, PalletInfo Info)>();
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var type = Int(r, "PalletType");
            pallets.Add((Int(r, "PalletID") ?? 0, new PalletInfo(
                Int(r, "PalletNumber") ?? 0,
                Str(r, "PalletName"),
                PalletStatusWords(type, Int(r, "PalletStatus")),
                PalletWorkStatusWords(type, Int(r, "WorkStatus")),
                Location(s, Int(r, "CurDeviceType"), Int(r, "CurDeviceNumber"), Int(r, "CurDevicePosition")),
                [])
            {
                Type = type is null or 0 ? null : Codes.PalletType(type),
                Priority = Int(r, "Priority") is { } p and not 0 ? p : null,
                OperatorPriority = Int(r, "OperatorPriority") is { } op and not 0 ? op : null,
            }));
        }
        if (pallet is not null && pallets.Count == 0)
            throw new ArgumentException($"No pallet numbered {pallet}. get_pallets without a pallet lists them all.");

        await r.NextResultAsync(ct);
        var rows = new List<(int PalletId, int? FixtureId, FixtureRow Row)>();
        while (await r.ReadAsync(ct))
        {
            rows.Add((Int(r, "PalletID") ?? 0, Int(r, "FixtureID"), new FixtureRow(Int(r, "FixtureNumber"))
            {
                Name = Str(r, "FixtureName"), Comment = Str(r, "Comment"),
                Suspend = Bool(r, "Suspend") == true, Hold = Bool(r, "WorkSetHold") == true, Damaged = Bool(r, "Damaged") == true,
                PreMcSuspend = Bool(r, "PreMcSuspend") == true, PreMcReason = Int(r, "PreMcSuspendReason"),
                OffDutyProcess = Bool(r, "OffDutyProcess") == true, Reclamp = Int(r, "ReclampStatus"),
                LackingTool = Bool(r, "LackingTool") == true, OperatorCallStatus = Int(r, "OperatorCallStatus"),
                ClampWaitDisabled = Bool(r, "ClampWaitingSuspendDisable") == true,
                JobId = Int(r, "CurJobID"), JobStatus = Int(r, "CurJobStatus"), JobType = Int(r, "JobType"), JobNumber = Int(r, "JobNumber"),
                WorkStatus = Int(r, "FixtureWorkStatus"), MachiningFinish = Int(r, "MachiningFinishStatus"),
                CleaningFinish = Int(r, "CleaningFinishStatus"), DeburringFinish = Int(r, "DeburringFinishStatus"),
                Order = Str(r, "OrderName"), Part = Str(r, "PartName"), Revision = Str(r, "Revision"), Process = Int(r, "ProcessNumber"),
                JobCallAhead = Int(r, "JobCallAhead") == 1, MachiningAhead = Int(r, "MachiningAhead") == 1,
                CleaningBefore = Int(r, "CleaningBefore") == 1, DeburringBefore = Int(r, "DeburringBefore") == 1,
            }));
        }

        var labels = new Dictionary<int, (int Count, string Part, string Revision, int? Process)>();
        if (parts.Labels)
        {
            await r.NextResultAsync(ct);
            while (await r.ReadAsync(ct))
                labels[Int(r, "FixtureID") ?? 0] = (Int(r, "Processes") ?? 0, Str(r, "PartName"), Str(r, "Revision"), Int(r, "ProcessNumber"));
        }
        var feasible = new Dictionary<int, HashSet<int>>();
        if (parts.Feasible)
        {
            await r.NextResultAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var id = Int(r, "PalletID") ?? 0;
                if (!feasible.TryGetValue(id, out var set)) feasible[id] = set = [];
                set.Add(Int(r, "FeasibleDeviceID") ?? 0);
            }
        }

        var detailed = pallet is not null || includeFixtures;
        var restricted = s.Devices.Where(d => d.Type is MachineType or StationType).ToList();
        var fixtures = rows
            .Select(x =>
            {
                var row = x.FixtureId is { } fid && labels.TryGetValue(fid, out var l)
                    ? x.Row with { ProcessCount = l.Count, FixturePart = l.Part, FixtureRevision = l.Revision, FixtureProcess = l.Process }
                    : x.Row;
                return (x.PalletId, Detail: FixtureOf(row, settings), x.Row.Name);
            })
            .GroupBy(x => x.PalletId)
            .ToDictionary(g => g.Key, g => g.Select(x => (x.Detail, x.Name)).ToList());

        var list = pallets.Select(p =>
        {
            var own = fixtures.GetValueOrDefault(p.Id) ?? [];
            var notFeasible = parts.Feasible
                ? restricted.Where(d => !(feasible.GetValueOrDefault(p.Id)?.Contains(d.Id) ?? false)).Select(d => d.Name).ToList()
                : [];
            return p.Info with
            {
                Fixtures = detailed ? null : FixtureLines(own),
                FixtureDetails = detailed ? own.Select(f => f.Detail).ToList() : null,
                NotFeasibleFor = notFeasible.Count > 0 ? notFeasible : null,
            };
        }).ToList();
        var (shown, capped) = Cap(list);
        return new PalletList(s.Now, capped, shown) { NotAvailable = notAvailable.Count > 0 ? notAvailable : null };
    }

    /// <summary>Which parts of the pallet query this cell supports: each fixture's work (FixtureRun), labels from the
    /// processes a fixture holds (FixtureProcesses), the pallets' feasible devices (PalletFeasibleDevice).</summary>
    internal sealed record PalletParts(bool Run, bool Labels, bool Feasible)
    {
        internal static PalletParts Of(SchemaInfo schema) => new(
            schema.HasTable("FixtureRun") && schema.Has("FixtureRun", "PalletFixtureID") && schema.Has("PalletFixtures", "PalletFixtureID"),
            schema.HasTable("FixtureProcesses") && schema.Has("FixtureProcesses", "FixtureID") && schema.Has("FixtureProcesses", "ProcessID")
                && schema.Has("PalletFixtures", "FixtureID"),
            schema.HasTable("PalletFeasibleDevice") && schema.Has("PalletFeasibleDevice", "PalletID")
                && schema.Has("PalletFeasibleDevice", "FeasibleDeviceID"));
    }

    /// <summary>Pallets with their location (cap + 1; only @pallet when <paramref name="onePallet"/>), then the fixtures
    /// on those pallets with their work and current job, then (when readable) the processes each of those fixtures
    /// can hold and the pallets' feasible devices.</summary>
    internal static string PalletsSql(SchemaInfo schema, bool onePallet = false, bool oneShotFirstPallet = false)
    {
        const string pf = "PalletFixtures";
        string Location(string column) => schema.Has("PalletLocation", column)
            ? $"(SELECT TOP 1 l.{column} FROM PalletLocation l WHERE l.PalletID = p.PalletID) AS {column}"
            : $"CAST(NULL AS int) AS {column}";

        var order = schema.Has("Pallets", "PalletNumber") ? "p.PalletNumber, p.PalletID" : "p.PalletID";
        var filter = onePallet && schema.Has("Pallets", "PalletNumber") ? "WHERE p.PalletNumber = @pallet " : "";
        var picked = $"SELECT TOP (@take) p.PalletID FROM Pallets p {filter}ORDER BY {order}";
        var pallets = string.Join(", ",
            "p.PalletID",
            Opt(schema, "Pallets", "p", "PalletNumber"),
            Opt(schema, "Pallets", "p", "PalletName", type: Text),
            Opt(schema, "Pallets", "p", "PalletStatus"),
            Opt(schema, "Pallets", "p", "WorkStatus"),
            Opt(schema, "Pallets", "p", "PalletType"),
            Opt(schema, "Pallets", "p", "Priority"),
            Opt(schema, "Pallets", "p", "OperatorPriority"),
            Location("CurDeviceType"), Location("CurDeviceNumber"), Location("CurDevicePosition"));

        var parts = PalletParts.Of(schema);
        var run = parts.Run;
        var withOrder = run && schema.Has("FixtureRun", "CurOrderID") && schema.HasTable("Orders") && schema.Has("Orders", "OrderID");
        var withProcess = run && schema.Has("FixtureRun", "CurProcessID") && schema.HasTable("Processes") && schema.Has("Processes", "ProcessID");
        var withPart = withProcess && schema.Has("Processes", "PartID") && schema.HasTable("Parts") && schema.Has("Parts", "PartID");
        var withJob = run && schema.Has("FixtureRun", "CurJobID") && schema.HasTable("Jobs") && schema.Has("Jobs", "JobID");
        string Col(bool joined, string table, string alias, string column, string? name = null, string type = "int") =>
            joined ? Opt(schema, table, alias, column, name, type) : $"CAST(NULL AS {type}) AS {name ?? column}";

        // What the jobs of the fixture's process hold around its current job (needs the jobs' process and number).
        var jobsAround = withJob && withProcess && schema.Has("Jobs", "ProcessID") && schema.Has("Jobs", "JobNumber") && schema.Has("Jobs", "JobType");
        string Exists(string condition) =>
            $"CASE WHEN EXISTS (SELECT 1 FROM Jobs j2{condition}) THEN 1 ELSE 0 END";
        const string sameProcess = " WHERE j2.ProcessID = fr.CurProcessID";
        var machiningAhead = jobsAround ? Exists($"{sameProcess} AND j2.JobType = {MachiningJob} AND j2.JobNumber >= j.JobNumber") : "0";
        var callAhead = "0";
        if (jobsAround && schema.HasTable("MachineJobs") && schema.Has("MachineJobs", "JobID") && schema.Has("MachineJobs", "OperatorCall"))
        {
            // A one-shot call counts for the first pallet only, or until this process has had one (each pallet).
            var oneShot = oneShotFirstPallet ? "1 = 1"
                : schema.Has(pf, "LastOneShotOpeCallProcessID") ? "ISNULL(pf.LastOneShotOpeCallProcessID, 0) <> fr.CurProcessID" : "1 = 1";
            callAhead = $"CASE WHEN EXISTS (SELECT 1 FROM Jobs j2 JOIN MachineJobs mj2 ON mj2.JobID = j2.JobID{sameProcess} " +
                        $"AND j2.JobNumber >= j.JobNumber AND (mj2.OperatorCall = 1 OR (mj2.OperatorCall = 2 AND {oneShot}))) THEN 1 ELSE 0 END";
        }
        var cleaningBefore = jobsAround ? Exists($"{sameProcess} AND j2.JobType = 3 AND j2.JobNumber < j.JobNumber") : "0";
        var deburringBefore = jobsAround ? Exists($"{sameProcess} AND j2.JobType = 5 AND j2.JobNumber < j.JobNumber") : "0";

        var fixtures = string.Join(", ",
            "pf.PalletID",
            schema.Has(pf, "FixtureID") ? "pf.FixtureID" : "CAST(NULL AS int) AS FixtureID",
            Opt(schema, pf, "pf", "FixtureNumber"),
            Opt(schema, "Fixtures", "f", "FixtureName", type: Text),
            Opt(schema, pf, "pf", "Comment", type: Text),
            Opt(schema, pf, "pf", "Suspend", type: "bit"),
            Opt(schema, pf, "pf", "WorkSetHold", type: "bit"),
            Opt(schema, pf, "pf", "Damaged", type: "bit"),
            Opt(schema, pf, "pf", "PreMcSuspend", type: "bit"),
            Opt(schema, pf, "pf", "PreMcSuspendReason"),
            Opt(schema, pf, "pf", "OffDutyProcess", type: "bit"),
            Opt(schema, pf, "pf", "ReclampStatus"),
            Opt(schema, pf, "pf", "LackingTool", type: "bit"),
            Opt(schema, pf, "pf", "OperatorCallStatus"),
            Opt(schema, pf, "pf", "ClampWaitingSuspendDisable", type: "bit"),
            Col(run, "FixtureRun", "fr", "CurJobID"),
            Col(run, "FixtureRun", "fr", "CurJobStatus"),
            Col(run, "FixtureRun", "fr", "WorkStatus", "FixtureWorkStatus"),
            Col(run, "FixtureRun", "fr", "MachiningFinishStatus"),
            Col(run, "FixtureRun", "fr", "CleaningFinishStatus"),
            Col(run, "FixtureRun", "fr", "DeburringFinishStatus"),
            Col(withOrder, "Orders", "o", "OrderName", type: Text),
            Col(withPart, "Parts", "pa", "PartName", type: Text),
            Col(withPart, "Parts", "pa", "Revision", type: Text),
            Col(withProcess, "Processes", "pr", "ProcessNumber"),
            Col(withJob, "Jobs", "j", "JobType"),
            Col(withJob, "Jobs", "j", "JobNumber"),
            $"{callAhead} AS JobCallAhead", $"{machiningAhead} AS MachiningAhead",
            $"{cleaningBefore} AS CleaningBefore", $"{deburringBefore} AS DeburringBefore");
        var joins = string.Concat(
            run ? " LEFT JOIN FixtureRun fr ON fr.PalletFixtureID = pf.PalletFixtureID" : "",
            withOrder ? " LEFT JOIN Orders o ON o.OrderID = fr.CurOrderID" : "",
            withProcess ? " LEFT JOIN Processes pr ON pr.ProcessID = fr.CurProcessID" : "",
            withPart ? " LEFT JOIN Parts pa ON pa.PartID = pr.PartID" : "",
            withJob ? " LEFT JOIN Jobs j ON j.JobID = fr.CurJobID" : "");

        var labels = "";
        if (parts.Labels)
        {
            var named = schema.HasTable("Processes") && schema.Has("Processes", "ProcessID") && schema.Has("Processes", "PartID")
                        && schema.HasTable("Parts") && schema.Has("Parts", "PartID");
            labels = $"""

                SELECT fp.FixtureID, COUNT(*) AS Processes,
                       {(named && schema.Has("Parts", "PartName") ? "MIN(pa.PartName)" : $"CAST(NULL AS {Text})")} AS PartName,
                       {(named && schema.Has("Parts", "Revision") ? "MIN(pa.Revision)" : $"CAST(NULL AS {Text})")} AS Revision,
                       {(named && schema.Has("Processes", "ProcessNumber") ? "MIN(pr.ProcessNumber)" : "CAST(NULL AS int)")} AS ProcessNumber
                FROM FixtureProcesses fp{(named ? " LEFT JOIN Processes pr ON pr.ProcessID = fp.ProcessID LEFT JOIN Parts pa ON pa.PartID = pr.PartID" : "")}
                WHERE fp.FixtureID IN (SELECT pf.FixtureID FROM PalletFixtures pf WHERE pf.PalletID IN ({picked}))
                GROUP BY fp.FixtureID;
                """;
        }
        var feasible = parts.Feasible
            ? $"""

                SELECT d.PalletID, d.FeasibleDeviceID FROM PalletFeasibleDevice d WHERE d.PalletID IN ({picked});
                """
            : "";
        var fixtureOrder = schema.Has(pf, "FixtureNumber") ? "pf.PalletID, pf.FixtureNumber" : "pf.PalletID";
        return $"""
            SELECT TOP (@take) {pallets}
            FROM Pallets p
            {filter}ORDER BY {order};
            SELECT {fixtures}
            FROM PalletFixtures pf LEFT JOIN Fixtures f ON f.FixtureID = pf.FixtureID{joins}
            WHERE pf.PalletID IN ({picked})
            ORDER BY {fixtureOrder};{labels}{feasible}
            """;
    }

    // The pallet types whose status and work status the cell rewrites into dashboard codes (its UpdatePalletStatus
    // procedure). For other types it always stores PalletStatus 0 and passes the raw work status through, so neither
    // value is a dashboard code there and both show "code N". No PalletType column (an older cell): treat as a
    // dashboard pallet.
    private static readonly int[] DashboardPalletTypes = [0, 1, 2, 8, 16];

    internal static string PalletStatusWords(int? palletType, int? status) =>
        IsDashboardPallet(palletType) ? Codes.PalletDisplayStatus(status) : CodeText(status);

    internal static string PalletWorkStatusWords(int? palletType, int? workStatus) =>
        IsDashboardPallet(palletType) ? Codes.PalletWorkStatus(workStatus) : CodeText(workStatus);

    private static bool IsDashboardPallet(int? palletType) => palletType is null || DashboardPalletTypes.Contains(palletType.Value);

    /// <summary>Where a pallet is, in words: the device's name when the cell has a device row for that place, else its
    /// type and number (stockers usually have no row); then the position: table, front, back or buffer at a machine,
    /// station or vehicle, the raw shelf number elsewhere. Type 0 is a pallet taken out of the system (pallet output);
    /// no location at all is "unknown".</summary>
    private static string Location(Session s, int? type, int? number, int? position)
    {
        if (type is null) return "unknown";
        if (type == OutOfSystem) return OutOfSystemWords;
        var device = s.Devices.FirstOrDefault(d => d.Type == type && d.Number == number)?.Name
            ?? $"{Codes.DeviceType(type)} {number}";
        return PlaceWords(device, type, position);
    }

    /// <summary>A device and a position on it, in words (see <see cref="Location"/>).</summary>
    internal static string PlaceWords(string device, int? type, int? position)
    {
        if (position is not > 0) return device;
        return type is MachineType or VehicleType or StationType
            ? $"{device}, {Codes.DevicePosition(position)}"
            : $"{device}, position {position}";
    }
}
