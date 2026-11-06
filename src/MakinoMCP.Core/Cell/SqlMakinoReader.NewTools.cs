using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;

namespace MakinoMCP.Core.Cell;

// The newer tools:
// - get_macro_values: macro variable values the machines uploaded (CommonValues: written when a machine program asks
//   the cell to read its macro variables with M480), each matched to the machining result running at that time.
// - get_work_set: the work-set station monitor as text (WorkSetStatus, WorkSetCommand and what its rows point at).
// - find_serial: where a part serial number was recorded (PartSerialNumberTracking), with the result it belongs to.
public sealed partial class SqlMakinoReader
{
    // ------------------------------------------------------------------ get_macro_values

    /// <summary>Uploaded macro values in the window (cap + 1, newest first), each with the machining result that was
    /// running on that machine at that time (one per value, the latest started); then whether the table has any row at
    /// all. Filters: @device, @variable and @pallet (each NULL for none).</summary>
    internal static string MacroValuesSql(SchemaInfo schema)
    {
        const string t = "CommonValues";
        var context = schema.Has("MachineResults", "DeviceID") && schema.Has("MachineResults", "StartDateTime")
                      && schema.Has("MachineResults", "FinishDateTime");
        string M(string column, string type) =>
            context ? Opt(schema, "MachineResults", "m", column, "Result" + column, type) : $"CAST(NULL AS {type}) AS Result{column}";
        var cols = string.Join(", ",
            "c.ExecDateTime", "c.DeviceID",
            Opt(schema, t, "c", "Number"), Opt(schema, t, "c", "Value", type: "float"),
            Opt(schema, t, "c", "PalletNumber"), Opt(schema, t, "c", "MainONumber"),
            Opt(schema, t, "c", "ExecONUmber", "ExecONumber"), Opt(schema, t, "c", "SequenceNumber"),
            M("PartName", Text), M("Revision", Text), M("ProcessNumber", "int"), M("NCProgramFileName", Text),
            M("FixtureNumber", "int"));
        // A running result's finish is NULL, or the vendor's empty default (1900), which is before its start.
        var apply = context
            ? """
              OUTER APPLY (SELECT TOP 1 * FROM MachineResults x
                           WHERE x.DeviceID = c.DeviceID AND x.StartDateTime <= c.ExecDateTime
                             AND (x.FinishDateTime IS NULL OR x.FinishDateTime < x.StartDateTime OR x.FinishDateTime >= c.ExecDateTime)
                           ORDER BY x.StartDateTime DESC) m
              """
            : "";
        var variable = schema.Has(t, "Number") ? "AND (@variable IS NULL OR c.Number = @variable)" : "";
        var pallet = schema.Has(t, "PalletNumber") ? "AND (@pallet IS NULL OR c.PalletNumber = @pallet)" : "";
        var then = schema.Has(t, "Number") ? ", c.Number" : "";
        return $"""
            SELECT TOP (@take) {cols}
            FROM CommonValues c
            {apply}
            WHERE c.ExecDateTime >= DATEADD(hour, -@hours, @now) AND (@device IS NULL OR c.DeviceID = @device)
              {variable} {pallet}
            ORDER BY c.ExecDateTime DESC, c.DeviceID{then};
            SELECT CASE WHEN EXISTS (SELECT 1 FROM CommonValues) THEN 1 ELSE 0 END AS AnyRows;
            """;
    }

    internal const string NoMacroUploads =
        "The cell holds no uploaded macro values at all: macro upload (a machine program asking the cell to read its " +
        "macro variables with M480) isn't used on this cell.";

    public async Task<MacroValueList> GetMacroValuesAsync(int days, string? device, int? variable, int? pallet, CancellationToken ct)
    {
        await using var s = await OpenAsync(ct);
        var deviceId = FindDevice(s.Names(), device);
        var schema = s.Schema;
        schema.Require("CommonValues");

        var notAvailable = new List<string>();
        if (!schema.Has("MachineResults", "StartDateTime") || !schema.Has("MachineResults", "FinishDateTime"))
            notAvailable.Add("the part and program each value was read during (MachineResults)");
        if (!schema.Has("CommonValues", "PalletNumber"))
            notAvailable.Add(pallet is null ? "pallet and programs (this cell's version doesn't record them)" : "the pallet filter (this cell's version records no pallet), so it wasn't applied");
        if (variable is not null && !schema.Has("CommonValues", "Number"))
            notAvailable.Add("the variable filter (no variable numbers), so it wasn't applied");

        using var cmd = Command(s.Connection, MacroValuesSql(schema));
        AddWindow(cmd, s.Now, deviceId, hours: Days(days) * 24);
        cmd.Parameters.Add("@variable", System.Data.SqlDbType.Int).Value = (object?)variable ?? DBNull.Value;
        cmd.Parameters.Add("@pallet", System.Data.SqlDbType.Int).Value = (object?)pallet ?? DBNull.Value;

        var rows = new List<MacroValue>();
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var programs = Program(Int(r, "MainONumber"), Int(r, "ExecONumber"));
            rows.Add(new MacroValue(Date(r, "ExecDateTime") ?? default, s.NameOf(Int(r, "DeviceID") ?? 0), Int(r, "Number") ?? 0, Double(r, "Value"))
            {
                Pallet = Positive(Int(r, "PalletNumber")), MainProgram = programs?.Main, ExecutingProgram = programs?.Executing,
                Sequence = Positive(Int(r, "SequenceNumber")),
                Part = NonEmpty(Str(r, "ResultPartName")), Revision = NonEmpty(Str(r, "ResultRevision")),
                Process = Positive(Int(r, "ResultProcessNumber")), Program = NonEmpty(Str(r, "ResultNCProgramFileName")),
                Fixture = Positive(Int(r, "ResultFixtureNumber")),
            });
        }
        await r.NextResultAsync(ct);
        var any = await r.ReadAsync(ct) && Int(r, "AnyRows") == 1;

        var (list, capped) = Cap(rows);
        return new MacroValueList(s.Now, capped, Days(days), list)
        {
            Note = list.Count > 0 ? null
                : any ? $"No uploaded values in the last {Days(days)} days for these filters. The cell keeps them as long as its result history (historyDays in get_cell_overview)."
                : NoMacroUploads,
            NotAvailable = notAvailable.Count > 0 ? notAvailable : null,
        };
    }

    private static int? Positive(int? value) => value is > 0 ? value : null;

    // ------------------------------------------------------------------ find_serial

    private const int SerialSlots = 10;

    /// <summary>The LIKE pattern for "contains <paramref name="text"/>", with the wildcard characters escaped (ESCAPE
    /// '\').</summary>
    internal static string ContainsPattern(string text)
    {
        var b = new StringBuilder("%");
        foreach (var ch in text)
        {
            if (ch is '\\' or '%' or '_' or '[') b.Append('\\');
            b.Append(ch);
        }
        return b.Append('%').ToString();
    }

    /// <summary>Serial records whose Code1..Code10 contain @pattern in the window (cap + 1, newest first), each with
    /// the machining or work-set result it belongs to (same device, finish time and fixture, as the vendor's reports
    /// join them); then whether the table has any row at all.</summary>
    internal static string SerialSql(SchemaInfo schema)
    {
        const string t = "PartSerialNumberTracking";
        var slots = Enumerable.Range(1, SerialSlots).Where(n => schema.Has(t, $"Code{n}")).ToList();
        var cols = new List<string>
        {
            "t.FinishDateTime", "t.DeviceID", Opt(schema, t, "t", "DeviceJobType"), Opt(schema, t, "t", "FixtureNumber"),
            Opt(schema, t, "t", "OperationSequence"), Opt(schema, t, "t", "Quantity"),
        };
        for (var n = 1; n <= SerialSlots; n++)
        {
            cols.Add(Opt(schema, t, "t", $"Code{n}", type: Text));
            cols.Add(Opt(schema, t, "t", $"Status{n}"));
        }

        var fixtureKey = schema.Has(t, "FixtureNumber");
        string Apply(string table, string alias, string kindTest, params (string Column, string Name, string Type)[] columns)
        {
            var can = fixtureKey && schema.Has(table, "DeviceID") && schema.Has(table, "FinishDateTime") && schema.Has(table, "FixtureNumber");
            foreach (var (column, name, type) in columns)
                cols.Add(can ? Opt(schema, table, alias, column, name, type) : $"CAST(NULL AS {type}) AS {name}");
            cols.Add(can ? PalletNumberSql(schema, $"{alias}.PalletID", alias.ToUpperInvariant() + "Pallet") : $"CAST(NULL AS int) AS {alias.ToUpperInvariant()}Pallet");
            return can
                ? $"OUTER APPLY (SELECT TOP 1 * FROM {table} x WHERE {kindTest} x.DeviceID = t.DeviceID AND x.FinishDateTime = t.FinishDateTime AND x.FixtureNumber = t.FixtureNumber) {alias}"
                : "";
        }

        var kind = schema.Has(t, "DeviceJobType");
        var machining = Apply("MachineResults", "m", kind ? "t.DeviceJobType = 1 AND" : "",
            ("OrderName", "MOrder", Text), ("PartName", "MPart", Text), ("Revision", "MRevision", Text),
            ("ProcessNumber", "MProcess", "int"), ("NCProgramFileName", "MProgram", Text));
        var workSet = Apply("WorkSetResults", "w", kind ? "t.DeviceJobType IN (2, 3) AND" : "",
            ("ClampOrderName", "WClampOrder", Text), ("ClampPartName", "WClampPart", Text), ("ClampRevision", "WClampRevision", Text),
            ("ClampProcessNumber", "WClampProcess", "int"), ("UnclampOrderName", "WUnclampOrder", Text),
            ("UnclampPartName", "WUnclampPart", Text), ("UnclampRevision", "WUnclampRevision", Text),
            ("UnclampProcessNumber", "WUnclampProcess", "int"));

        var match = slots.Count == 0 ? "1 = 0" : string.Join(" OR ", slots.Select(n => $"t.Code{n} LIKE @pattern ESCAPE '\\'"));
        return $"""
            SELECT TOP (@take) {string.Join(", ", cols)}
            FROM PartSerialNumberTracking t
            {machining}
            {workSet}
            WHERE t.FinishDateTime >= DATEADD(hour, -@hours, @now) AND (@device IS NULL OR t.DeviceID = @device)
              AND ({match})
            ORDER BY t.FinishDateTime DESC;
            SELECT CASE WHEN EXISTS (SELECT 1 FROM PartSerialNumberTracking) THEN 1 ELSE 0 END AS AnyRows;
            """;
    }

    internal const string NoSerialTracking =
        "The cell holds no part serial numbers at all: serial number tracking isn't used on this cell.";

    internal const string NoSerialGiven = "Give the serial number, or part of it, to look for.";

    public async Task<SerialSearch> FindSerialAsync(string serial, int days, string? device, CancellationToken ct)
    {
        var wanted = (serial ?? "").Trim();
        await using var s = await OpenAsync(ct);
        var deviceId = FindDevice(s.Names(), device);
        if (wanted.Length == 0) return new SerialSearch(s.Now, false, wanted, Days(days), []) { Note = NoSerialGiven };
        var schema = s.Schema;
        schema.Require("PartSerialNumberTracking");

        var notAvailable = new List<string>();
        if (!schema.Has("PartSerialNumberTracking", "FixtureNumber"))
            notAvailable.Add("the result each record belongs to (this cell records no fixture number)");
        if (!schema.HasTable("MachineResults")) notAvailable.Add("machining results (order, part, program)");
        if (!schema.HasTable("WorkSetResults")) notAvailable.Add("work-set results (order, part)");

        using var cmd = Command(s.Connection, SerialSql(schema));
        AddWindow(cmd, s.Now, deviceId, hours: Days(days) * 24);
        cmd.Parameters.Add("@pattern", System.Data.SqlDbType.VarChar, 300).Value = ContainsPattern(wanted);

        var rows = new List<SerialMatch>();
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var codes = new List<SerialCode>();
            for (var n = 1; n <= SerialSlots; n++)
            {
                var code = Str(r, $"Code{n}");
                if (!code.Contains(wanted, StringComparison.OrdinalIgnoreCase)) continue;
                codes.Add(new SerialCode(n, code) { Status = Int(r, $"Status{n}") is { } st and not 0 ? CodeText(st) : null });
            }
            var type = Int(r, "DeviceJobType");
            var match = new SerialMatch(Date(r, "FinishDateTime") ?? default, s.NameOf(Int(r, "DeviceID") ?? 0),
                Codes.SerialJobType(type), codes)
            {
                Fixture = Positive(Int(r, "FixtureNumber")), OperationSequence = Positive(Int(r, "OperationSequence")),
                Quantity = Int(r, "Quantity"),
            };
            match = type switch
            {
                1 => match with
                {
                    Pallet = Int(r, "MPallet"), Order = NonEmpty(Str(r, "MOrder")), Part = NonEmpty(Str(r, "MPart")),
                    Revision = NonEmpty(Str(r, "MRevision")), Process = Positive(Int(r, "MProcess")), Program = NonEmpty(Str(r, "MProgram")),
                },
                2 => match with
                {
                    Pallet = Int(r, "WPallet"), Order = NonEmpty(Str(r, "WClampOrder")), Part = NonEmpty(Str(r, "WClampPart")),
                    Revision = NonEmpty(Str(r, "WClampRevision")), Process = Positive(Int(r, "WClampProcess")),
                },
                3 => match with
                {
                    Pallet = Int(r, "WPallet"), Order = NonEmpty(Str(r, "WUnclampOrder")), Part = NonEmpty(Str(r, "WUnclampPart")),
                    Revision = NonEmpty(Str(r, "WUnclampRevision")), Process = Positive(Int(r, "WUnclampProcess")),
                },
                _ => match,
            };
            rows.Add(match);
        }
        await r.NextResultAsync(ct);
        var any = await r.ReadAsync(ct) && Int(r, "AnyRows") == 1;

        var (list, capped) = Cap(rows);
        return new SerialSearch(s.Now, capped, wanted, Days(days), list)
        {
            Note = list.Count > 0 ? null
                : any ? $"No serial containing '{wanted}' was recorded in the last {Days(days)} days. The cell keeps serials as long as its result history (historyDays in get_cell_overview)."
                : NoSerialTracking,
            NotAvailable = notAvailable.Count > 0 ? notAvailable : null,
        };
    }

    // ------------------------------------------------------------------ get_work_set

    // WorkSetCommand.OperationType (WorkSetStationOperationType).
    private const int WsTypeNothing = 0, WsTypeClamp = 1, WsTypeUnclampClamp = 2, WsTypeUnclampClampScrap = 3, WsTypeUnclamp = 4,
        WsTypeUnclampScrap = 5, WsTypeOther = 6, WsTypeInspection = 10, WsTypeExchangeFixture = 11, WsTypeFixtureChange = 20;

    // WorkSetCommand.OperationStatus (WorkSetStationOperationStatus).
    private const int WsStatusUnclamp = 1, WsStatusClamp = 2, WsStatusClampSuspend = 3, WsStatusUnclampCancel = 4,
        WsStatusUnclampScrapCancel = 5, WsStatusOtherCancel = 7;

    // The monitor's instruction (WorkSetStationMonitorInstruction, words in Codes.WssInstruction).
    internal const int WsShowNothing = 0, WsShowClamp = 1, WsShowClampCancel = 2, WsShowUnclamp = 3, WsShowUnclampScrap = 4,
        WsShowUnclampCancel = 5, WsShowUnclampClamp = 6, WsShowUnclampScrapClamp = 7, WsShowUnclampClampCancel = 8,
        WsShowUnclampScrapClampCancel = 9, WsShowReMachining = 10, WsShowOther = 11, WsShowOtherCancel = 12, WsShowInspection = 13,
        WsShowExchangeFixture = 15, WsShowReCleaning = 20, WsShowReDeburring = 21, WsShowReUnclamp = 22, WsShowReMeasuring = 28,
        WsShowFixtureChange = 29;

    // What the fixture leaves as (WorkSetStationFinishStatus, words in Codes.WorkSetLeavesAs).
    internal const int WsLeavesMachining = 1, WsLeavesMachiningSuspend = 2, WsLeavesHoldClamp = 3, WsLeavesHoldUnclamp = 4,
        WsLeavesHoldOther = 5, WsLeavesCleaning = 7, WsLeavesDeburring = 8, WsLeavesMeasuring = 9;

    /// <summary>One fixture's row of WorkSetCommand as the instruction rules need it. Key: its PalletFixtureID (0 when
    /// the row has none); Scrap: the unclamp scraps parts (scrap quantity, or a scrap instruction type when the
    /// quantities can't be read).</summary>
    internal sealed record WsCommand(int Device, int Key, int Type, int Status)
    {
        public int ClampQuantity { get; init; }
        public int MoveQuantity { get; init; }
        public int MoveKey { get; init; }
        public bool ReMachine { get; init; }
        public int ReCleaning { get; init; }
        public int ReDeburring { get; init; }
        public int ReMeasuring { get; init; }
        public int ReUnclamp { get; init; }
        public bool ClampCancel { get; init; }
        public bool Scrap { get; init; }
    }

    /// <summary>The monitor's instruction and what the fixture leaves as: Show null means the monitor has no words of
    /// its own for the pair (shown raw), WsShowNothing that it shows nothing (the row is left out). LeavesAs null: no
    /// words; WaitWorkSetting: a re-unclamp, which leaves the fixture waiting for its work setting.</summary>
    internal readonly record struct WsInstruction(int? Show, int? LeavesAs = null, bool WaitWorkSetting = false);

    /// <summary>Whether the clamp side's move partner was unclamp-cancelled, re-machined or isn't an unclamp at all:
    /// then the monitor shows no clamp for this fixture.</summary>
    internal static bool MoveCancelled(WsCommand c, IReadOnlyList<WsCommand> station)
    {
        if (c.MoveKey <= 0) return false;
        var partner = station.FirstOrDefault(x => x.Key == c.MoveKey);
        if (partner is null) return true;
        return partner.Type is WsTypeUnclampClamp or WsTypeUnclampClampScrap or WsTypeUnclamp or WsTypeUnclampScrap
            ? partner.ReMachine || partner.Status == WsStatusUnclampCancel
            : true;
    }

    /// <summary>The work-set station monitor's instruction for one fixture, by the console's rules for the (type,
    /// status) pair: clamps without parts or with a cancelled move show nothing; re-machining, re-cleaning,
    /// re-deburring, re-measuring and re-unclamp come before the plain unclamp; a cancel leaves the fixture on
    /// work-set hold.</summary>
    internal static WsInstruction WorkSetInstruction(WsCommand c, bool moveCancelled)
    {
        var suspend = c.Status == WsStatusClampSuspend;
        var machining = suspend ? WsLeavesMachiningSuspend : WsLeavesMachining;
        WsInstruction? Again(bool unclamp)
        {
            if (c.ReMachine) return new(WsShowReMachining, machining);
            if (c.ReCleaning != 0) return new(WsShowReCleaning, WsLeavesCleaning);
            if (c.ReDeburring != 0) return new(WsShowReDeburring, WsLeavesDeburring);
            if (c.ReMeasuring != 0) return new(WsShowReMeasuring, WsLeavesMeasuring);
            if (unclamp && c.ReUnclamp != 0) return new(WsShowReUnclamp, WaitWorkSetting: true);
            return null;
        }

        switch (c.Type)
        {
            case WsTypeNothing:
                return new(WsShowNothing);
            case WsTypeClamp:
                if (moveCancelled || c.ClampQuantity + c.MoveQuantity == 0) return new(WsShowNothing);
                return c.Status switch
                {
                    WsStatusClamp => new(WsShowClamp, WsLeavesMachining),
                    WsStatusClampSuspend => new(WsShowClamp, WsLeavesMachiningSuspend),
                    WsStatusUnclamp => new(WsShowClampCancel, WsLeavesHoldClamp),
                    _ => new(null),
                };
            case WsTypeUnclamp or WsTypeUnclampScrap:
                if (Again(unclamp: true) is { } again) return again;
                return c.Status switch
                {
                    WsStatusUnclamp => new(c.Scrap ? WsShowUnclampScrap : WsShowUnclamp),
                    WsStatusUnclampCancel or WsStatusUnclampScrapCancel => new(WsShowUnclampCancel, WsLeavesHoldUnclamp),
                    _ => new(null),
                };
            case WsTypeUnclampClamp or WsTypeUnclampClampScrap:
                if (Again(unclamp: false) is { } again2) return again2;
                switch (c.Status)
                {
                    case WsStatusClamp or WsStatusClampSuspend:
                        if (moveCancelled || c.ClampQuantity + c.MoveQuantity == 0) return new(WsShowUnclamp);
                        return new(c.Scrap ? WsShowUnclampScrapClamp : WsShowUnclampClamp, machining);
                    case WsStatusUnclampCancel or WsStatusUnclampScrapCancel:
                        return new(WsShowUnclampCancel, WsLeavesHoldUnclamp);
                    case WsStatusUnclamp:
                        return c.ClampCancel
                            ? new(c.Scrap ? WsShowUnclampScrapClamp : WsShowUnclampClamp, WsLeavesMachining)
                            : new(c.Scrap ? WsShowUnclampScrapClampCancel : WsShowUnclampClampCancel, WsLeavesHoldClamp);
                    default:
                        return new(null);
                }
            case WsTypeExchangeFixture:
                return new(WsShowExchangeFixture);
            case WsTypeFixtureChange:
                return new(WsShowFixtureChange);
            case WsTypeInspection:
                return new(WsShowInspection);
            case WsTypeOther:
                if (Again(unclamp: false) is { } again3) return again3;
                return c.Status == WsStatusOtherCancel ? new(WsShowOtherCancel, WsLeavesHoldOther) : new(WsShowOther);
            default:
                return new(null);
        }
    }

    /// <summary>One WorkSetCommand row with everything it points at.</summary>
    private sealed record WsRow(WsCommand Command)
    {
        public int? Pallet { get; init; }
        public int? Fixture { get; init; }
        public string? FixtureName { get; init; }
        public string? FaceComment { get; init; }
        public string? StatusReason { get; init; }
        public bool OffDuty { get; init; }
        public bool InspectionConfirmed { get; init; }
        public string? ReMachineProgram { get; init; }
        public WorkSide Unclamp { get; init; } = new();
        public WorkSide Clamp { get; init; } = new();
        public int? MachineId { get; init; }
        public int? MachiningFinish { get; init; }
        public int? WorkStatus { get; init; }
        public bool Machined { get; init; }
    }

    /// <summary>Builds LEFT JOINs over optional tables: a join is added only when its columns exist, and a column of a
    /// table that wasn't joined reads as NULL.</summary>
    private sealed class OptionalJoins(SchemaInfo schema)
    {
        private readonly HashSet<string> _joined = new(StringComparer.Ordinal);
        private readonly List<string> _clauses = [];

        public bool Has(string alias) => _joined.Contains(alias);

        public void Left(string table, string alias, string on, bool when)
        {
            if (!when) return;
            _clauses.Add($"LEFT JOIN {table} {alias} ON {on}");
            _joined.Add(alias);
        }

        public string Col(string table, string alias, string column, string name, string type = "int") =>
            Has(alias) ? Opt(schema, table, alias, column, name, type) : $"CAST(NULL AS {type}) AS {name}";

        public override string ToString() => string.Join("\n", _clauses);
    }

    /// <summary>The program of a machining job: its NC program's name, else its O-number.</summary>
    private static string JobProgramSql(SchemaInfo schema, string jobId, string name)
    {
        if (!schema.Has("MachineJobs", "JobID") || !schema.Has("MachineJobs", "NCProgramFileID")
            || !schema.Has("NCProgramFiles", "NCProgramFileID") || !schema.Has("NCProgramFiles", "Name"))
            return $"CAST(NULL AS {Text}) AS {name}";
        var onumber = schema.Has("MachineJobs", "ONumber")
            ? "CASE WHEN mj.ONumber > 0 THEN 'O' + CAST(mj.ONumber AS varchar(12)) END"
            : "NULL";
        return $"""
            (SELECT TOP 1 COALESCE(NULLIF(LTRIM(RTRIM(n.Name)), ''), {onumber}) FROM MachineJobs mj
             LEFT JOIN NCProgramFiles n ON n.NCProgramFileID = mj.NCProgramFileID WHERE mj.JobID = {jobId}) AS {name}
            """;
    }

    /// <summary>Every station instruction that isn't "nothing" (cap + 1), with its fixture, the order, part, process
    /// and job on each side, the information files, the programs and how the unclamped part's machining finished.</summary>
    internal static string WorkSetCommandsDetailSql(SchemaInfo schema)
    {
        const string t = "WorkSetCommand";
        var j = new OptionalJoins(schema);
        var pfKey = schema.Has(t, "PalletFixtureID");
        j.Left("PalletFixtures", "pf", "pf.PalletFixtureID = c.PalletFixtureID", pfKey && schema.Has("PalletFixtures", "PalletFixtureID"));
        j.Left("Pallets", "p", "p.PalletID = pf.PalletID", j.Has("pf") && schema.Has("PalletFixtures", "PalletID") && schema.Has("Pallets", "PalletID"));
        j.Left("Fixtures", "fx", "fx.FixtureID = pf.FixtureID", j.Has("pf") && schema.Has("PalletFixtures", "FixtureID") && schema.Has("Fixtures", "FixtureID"));
        j.Left("FixtureRun", "fr", "fr.PalletFixtureID = c.PalletFixtureID", pfKey && schema.Has("FixtureRun", "PalletFixtureID"));

        var cols = new List<string>
        {
            "c.DeviceID", "c.OperationType",
            pfKey ? "c.PalletFixtureID" : "CAST(NULL AS int) AS PalletFixtureID",
            Opt(schema, t, "c", "FixtureNumber"), Opt(schema, t, "c", "OperationStatus"),
            Opt(schema, t, "c", "ClampQuantity"), Opt(schema, t, "c", "MoveQuantity"), Opt(schema, t, "c", "MovePalletFixtureID"),
            Opt(schema, t, "c", "ReMachine", type: "bit"), Opt(schema, t, "c", "ReCleaning"), Opt(schema, t, "c", "ReDeburring"),
            Opt(schema, t, "c", "ReMeasuring"), Opt(schema, t, "c", "ReUnclamp"), Opt(schema, t, "c", "OffDutyProcess"),
            Opt(schema, t, "c", "InspectionConfirmation", type: "bit"),
            schema.Has(t, "ReMachineJobID") ? JobProgramSql(schema, "c.ReMachineJobID", "ReMachineProgram") : $"CAST(NULL AS {Text}) AS ReMachineProgram",
            j.Col("Pallets", "p", "PalletNumber", "PalletNumber"), j.Col("PalletFixtures", "pf", "FixtureNumber", "PfFixtureNumber"),
            j.Col("PalletFixtures", "pf", "Comment", "FaceComment", Text), j.Col("PalletFixtures", "pf", "StatusReason", "StatusReason", Text),
            j.Col("Fixtures", "fx", "FixtureName", "FixtureName", Text),
            j.Col("FixtureRun", "fr", "ClampCancel", "ClampCancel", "bit"), j.Col("FixtureRun", "fr", "MachineDeviceID", "MachineDeviceID"),
            j.Col("FixtureRun", "fr", "MachiningFinishStatus", "MachiningFinishStatus"), j.Col("FixtureRun", "fr", "WorkStatus", "RunWorkStatus"),
        };

        // The last machining job: the instruction's own, else the fixture run's.
        var lastJob = (schema.Has(t, "LastMachiningJobID"), j.Has("fr") && schema.Has("FixtureRun", "LastMachiningJobID")) switch
        {
            (true, true) => "COALESCE(NULLIF(c.LastMachiningJobID, 0), fr.LastMachiningJobID)",
            (true, false) => "c.LastMachiningJobID",
            (false, true) => "fr.LastMachiningJobID",
            _ => null,
        };
        cols.Add(lastJob is null ? "CAST(NULL AS int) AS LastMachiningJobID" : $"{lastJob} AS LastMachiningJobID");
        cols.Add(lastJob is null ? $"CAST(NULL AS {Text}) AS LastProgram" : JobProgramSql(schema, lastJob, "LastProgram"));

        foreach (var (side, orderId, jobId) in new[] { ("u", "UnclampOrderID", "UnclampJobID"), ("k", "ClampOrderID", "ClampJobID") })
        {
            j.Left("Orders", side + "o", $"{side}o.OrderID = c.{orderId}", schema.Has(t, orderId) && schema.Has("Orders", "OrderID"));
            j.Left("Jobs", side + "j", $"{side}j.JobID = c.{jobId}", schema.Has(t, jobId) && schema.Has("Jobs", "JobID"));
            j.Left("Processes", side + "pr", $"{side}pr.ProcessID = {side}j.ProcessID",
                j.Has(side + "j") && schema.Has("Jobs", "ProcessID") && schema.Has("Processes", "ProcessID"));
            j.Left("Parts", side + "pt", $"{side}pt.PartID = {side}pr.PartID",
                j.Has(side + "pr") && schema.Has("Processes", "PartID") && schema.Has("Parts", "PartID"));
            j.Left("WorkSetJobs", side + "w", $"{side}w.JobID = c.{jobId}", schema.Has(t, jobId) && schema.Has("WorkSetJobs", "JobID"));
            j.Left("InformationFiles", side + "i", $"{side}i.InformationFileID = {side}w.InformationFileID",
                j.Has(side + "w") && schema.Has("WorkSetJobs", "InformationFileID") && schema.Has("InformationFiles", "InformationFileID"));
            cols.AddRange(
            [
                j.Col("Orders", side + "o", "OrderName", side + "Order", Text), j.Col("Orders", side + "o", "Comment", side + "OrderComment", Text),
                j.Col("Parts", side + "pt", "PartName", side + "Part", Text), j.Col("Parts", side + "pt", "Revision", side + "Revision", Text),
                j.Col("Parts", side + "pt", "Comment", side + "PartComment", Text),
                j.Col("Processes", side + "pr", "ProcessNumber", side + "Process"), j.Col("Processes", side + "pr", "ProcessName", side + "ProcessName", Text),
                j.Col("Processes", side + "pr", "Comment", side + "ProcessComment", Text),
                j.Col("Jobs", side + "j", "JobNumber", side + "Job"), j.Col("Jobs", side + "j", "Comment", side + "JobComment", Text),
                j.Col("InformationFiles", side + "i", "Name", side + "InfoFile", Text),
            ]);
        }

        var order = new List<string> { "c.DeviceID" };
        if (j.Has("p") && schema.Has("Pallets", "PalletNumber")) order.Add("p.PalletNumber");
        if (schema.Has(t, "FixtureNumber")) order.Add("c.FixtureNumber");
        return $"""
            SELECT TOP (@take) {string.Join(", ", cols)}
            FROM WorkSetCommand c
            {j}
            WHERE c.OperationType <> {WsTypeNothing} AND (@device IS NULL OR c.DeviceID = @device)
            ORDER BY {string.Join(", ", order)};
            """;
    }

    /// <summary>The unclamp quantities per fixture and operation (normal, scrap, out of process) of the fixtures that
    /// have an instruction. Null when the table or its columns can't be read.</summary>
    internal static string? WorkSetQuantitiesSql(SchemaInfo schema)
    {
        const string t = "WorkSetCommandQuantity";
        if (!schema.Has(t, "PalletFixtureID") || !schema.Has(t, "UnclampNormalQuantity") || !schema.Has(t, "UnclampScrapQuantity")
            || !schema.Has("WorkSetCommand", "PalletFixtureID"))
            return null;
        var sequence = schema.Has(t, "OperationSequence") ? "q.OperationSequence" : "0";
        return $"""
            SELECT q.PalletFixtureID, {sequence} AS OperationSequence, q.UnclampNormalQuantity, q.UnclampScrapQuantity,
                   {Opt(schema, t, "q", "OutProcessQuantity")}
            FROM WorkSetCommandQuantity q
            WHERE q.PalletFixtureID IN (SELECT c.PalletFixtureID FROM WorkSetCommand c WHERE c.OperationType <> {WsTypeNothing})
            ORDER BY q.PalletFixtureID, {sequence};
            """;
    }

    /// <summary>Unclamp quantities in words, one entry per operation when there are several: "normal 2, scrap 1".</summary>
    internal static string? UnclampQuantityWords(IReadOnlyList<(int Normal, int Scrap, int OutProcess)> perOperation)
    {
        if (perOperation.Count == 0) return null;
        string Each(Func<(int Normal, int Scrap, int OutProcess), int> pick) => string.Join("/", perOperation.Select(q => pick(q).ToString(CultureInfo.InvariantCulture)));
        var parts = new List<string>();
        if (perOperation.Any(q => q.Normal > 0)) parts.Add($"normal {Each(q => q.Normal)}");
        if (perOperation.Any(q => q.Scrap > 0)) parts.Add($"scrap {Each(q => q.Scrap)}");
        if (perOperation.Any(q => q.OutProcess > 0)) parts.Add($"out of process {Each(q => q.OutProcess)}");
        return parts.Count > 0 ? string.Join(", ", parts) : null;
    }

    private const string NoMachining = "no machining";

    private static readonly (string Table, string Words)[] WorkSetOptionalTables =
    [
        ("PalletFixtures", "the pallet and fixture of each instruction (PalletFixtures)"),
        ("FixtureRun", "how the unclamped part's machining finished, and clamp cancels (FixtureRun)"),
        ("Orders", "the orders (Orders)"), ("Jobs", "the jobs, processes and parts (Jobs)"),
        ("Processes", "the processes and parts (Processes)"), ("Parts", "the parts (Parts)"),
        ("WorkSetJobs", "the information files (WorkSetJobs)"), ("MachineJobs", "the programs (MachineJobs)"),
    ];

    public async Task<WorkSetView> GetWorkSetAsync(string? device, CancellationToken ct)
    {
        await using var s = await OpenAsync(ct);
        var deviceId = FindDevice(s.Names(), device);
        var schema = s.Schema;
        schema.Require("WorkSetStatus", "WorkSetCommand");

        var notAvailable = WorkSetOptionalTables.Where(x => !schema.HasTable(x.Table)).Select(x => x.Words).ToList();
        var quantitiesSql = WorkSetQuantitiesSql(schema);
        if (quantitiesSql is null)
            notAvailable.Add("unclamp quantities per operation (normal, scrap, out of process): this login can't read WorkSetCommandQuantity, so scrap comes from the instruction type");
        var system = await SystemParametersAsync(s, ct, "InspectionMode");
        if (system is null) notAvailable.Add("whether the cell requires inspection confirmation (SystemParameters)");
        var inspectionRequired = ToInt(system?.GetValueOrDefault("InspectionMode")) == 1;

        // The stations.
        Dictionary<int, StatusRow> stations;
        using (var cmd = Command(s.Connection, StatusSql(schema, "WorkSetStatus", StationSpots)))
        using (var r = await cmd.ExecuteReaderAsync(ct))
            stations = await ReadStatusAsync(r, StationSpots, ct);

        // Unclamp quantities per fixture, in operation order.
        var quantities = new Dictionary<int, List<(int Normal, int Scrap, int OutProcess)>>();
        if (quantitiesSql is not null)
        {
            using var cmd = Command(s.Connection, quantitiesSql);
            using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var key = Int(r, "PalletFixtureID") ?? 0;
                if (!quantities.TryGetValue(key, out var list)) quantities[key] = list = [];
                list.Add((Int(r, "UnclampNormalQuantity") ?? 0, Int(r, "UnclampScrapQuantity") ?? 0, Int(r, "OutProcessQuantity") ?? 0));
            }
        }

        // The instructions.
        var rows = new List<WsRow>();
        using (var cmd = Command(s.Connection, WorkSetCommandsDetailSql(schema)))
        {
            AddTake(cmd);
            cmd.Parameters.Add("@device", System.Data.SqlDbType.Int).Value = (object?)deviceId ?? DBNull.Value;
            using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) rows.Add(ReadWorkSetRow(r, quantities, quantitiesSql is not null));
        }
        var (shown, capped) = Cap(rows);

        var result = new List<StationWork>();
        foreach (var d in s.Devices.Where(d => d.Type == StationType && (deviceId is null || d.Id == deviceId)))
        {
            var own = shown.Where(x => x.Command.Device == d.Id).ToList();
            var commands = own.Select(x => x.Command).ToList();
            var fixtures = new List<FixtureWork>();
            foreach (var row in own)
            {
                var c = row.Command;
                var instruction = WorkSetInstruction(c, MoveCancelled(c, commands));
                if (instruction.Show == WsShowNothing) continue;
                fixtures.Add(FixtureWorkFrom(row, instruction, s, stations.GetValueOrDefault(d.Id), inspectionRequired, quantities));
            }

            var station = new StationWork(d.Name, stations.GetValueOrDefault(d.Id)?.Pallets ?? [], fixtures);
            if (stations.TryGetValue(d.Id, out var w))
            {
                var instructing = w.Connected != false && w.Command is > 0 && (w.Status ?? DashBusy) == DashBusy;
                var (since, planned) = Timing(instructing, w.Start, w.Finish);
                station = station with
                {
                    Connected = w.Connected, Status = Words(w.Status, Codes.DeviceDashStatus), Mode = StationMode(w),
                    Open = OpenFlag(w.Open), Instruction = instructing ? Codes.WssInstruction(w.Command) : null,
                    Since = since, PlannedFinish = planned,
                };
            }
            result.Add(station);
        }

        return new WorkSetView(s.Now, capped, result) { NotAvailable = notAvailable.Count > 0 ? notAvailable : null };
    }

    private static WsRow ReadWorkSetRow(SqlDataReader r, Dictionary<int, List<(int Normal, int Scrap, int OutProcess)>> quantities, bool haveQuantities)
    {
        var key = Int(r, "PalletFixtureID") ?? 0;
        var type = Int(r, "OperationType") ?? 0;
        var scrap = haveQuantities && key > 0
            ? quantities.GetValueOrDefault(key)?.Any(q => q.Scrap > 0) == true
            : type is WsTypeUnclampClampScrap or WsTypeUnclampScrap;
        var command = new WsCommand(Int(r, "DeviceID") ?? 0, key, type, Int(r, "OperationStatus") ?? 0)
        {
            ClampQuantity = Int(r, "ClampQuantity") ?? 0, MoveQuantity = Int(r, "MoveQuantity") ?? 0,
            MoveKey = Int(r, "MovePalletFixtureID") ?? 0, ReMachine = Bool(r, "ReMachine") == true,
            ReCleaning = Int(r, "ReCleaning") ?? 0, ReDeburring = Int(r, "ReDeburring") ?? 0,
            ReMeasuring = Int(r, "ReMeasuring") ?? 0, ReUnclamp = Int(r, "ReUnclamp") ?? 0,
            ClampCancel = Bool(r, "ClampCancel") == true, Scrap = scrap,
        };
        WorkSide Side(string p) => new()
        {
            Order = NonEmpty(Str(r, p + "Order")), OrderComment = NonEmpty(Str(r, p + "OrderComment")),
            Part = NonEmpty(Str(r, p + "Part")), Revision = NonEmpty(Str(r, p + "Revision")), PartComment = NonEmpty(Str(r, p + "PartComment")),
            Process = Positive(Int(r, p + "Process")), ProcessName = NonEmpty(Str(r, p + "ProcessName")),
            ProcessComment = NonEmpty(Str(r, p + "ProcessComment")), Job = Positive(Int(r, p + "Job")),
            JobComment = NonEmpty(Str(r, p + "JobComment")), InfoFile = NonEmpty(Str(r, p + "InfoFile")),
        };
        return new WsRow(command)
        {
            Pallet = Positive(Int(r, "PalletNumber")), Fixture = Positive(Int(r, "FixtureNumber")) ?? Positive(Int(r, "PfFixtureNumber")),
            FixtureName = NonEmpty(Str(r, "FixtureName")), FaceComment = NonEmpty(Str(r, "FaceComment")),
            StatusReason = NonEmpty(Str(r, "StatusReason")), OffDuty = Int(r, "OffDutyProcess") is > 0,
            InspectionConfirmed = Bool(r, "InspectionConfirmation") == true, ReMachineProgram = NonEmpty(Str(r, "ReMachineProgram")),
            Unclamp = Side("u") with { LastProgram = NonEmpty(Str(r, "LastProgram")) }, Clamp = Side("k"),
            MachineId = Positive(Int(r, "MachineDeviceID")), MachiningFinish = Int(r, "MachiningFinishStatus"),
            WorkStatus = Int(r, "RunWorkStatus"), Machined = Int(r, "LastMachiningJobID") is > 0,
        };
    }

    private static FixtureWork FixtureWorkFrom(WsRow row, WsInstruction instruction, Session s, StatusRow? station,
        bool inspectionRequired, Dictionary<int, List<(int Normal, int Scrap, int OutProcess)>> quantities)
    {
        var c = row.Command;
        var unclampSide = c.Type is WsTypeUnclamp or WsTypeUnclampScrap or WsTypeUnclampClamp or WsTypeUnclampClampScrap;
        var clampSide = c.Type is WsTypeClamp or WsTypeUnclampClamp or WsTypeUnclampClampScrap
                        && instruction.Show is not (WsShowUnclamp or WsShowUnclampScrap or WsShowUnclampCancel);
        var unclampWords = UnclampQuantityWords(quantities.GetValueOrDefault(c.Key) ?? []);
        var clampQuantity = c.ClampQuantity + c.MoveQuantity;

        string words;
        string? detail = null;
        if (instruction.Show is { } show)
        {
            words = Codes.WssInstruction(show);
            var quantity = show switch
            {
                WsShowClamp => $"clamp {clampQuantity}",
                WsShowUnclamp or WsShowUnclampScrap => unclampWords,
                WsShowUnclampClamp or WsShowUnclampScrapClamp => Join(unclampWords ?? "", $"clamp {clampQuantity}"),
                _ => null,
            };
            if (quantity is { Length: > 0 }) words = $"{words} ({quantity})";
        }
        else
        {
            words = Codes.WorkSetCommandOperation(c.Type);
            detail = $"progress: {Codes.WorkSetOperation(c.Status)}";
        }

        // Rows without a fixture (other, pallet input/output) are about the pallet on the station's table.
        var pallet = row.Pallet ?? (c.Key == 0 ? station?.Pallets.FirstOrDefault(p => p.Position == "table")?.PalletNumber : null);

        var unclamp = unclampSide
            ? row.Unclamp with
            {
                Quantities = unclampWords,
                Machine = row.MachineId is { } m ? s.NameOf(m) : null,
                MachiningFinish = row.MachineId is not null ? Codes.MachiningFinish(row.MachiningFinish)
                    : !row.Machined && row.Unclamp.Part is not null ? NoMachining : null,
                WorkStatus = row.WorkStatus is > 0 ? Codes.FixtureWorkStatus(row.WorkStatus) : null,
            }
            : null;
        var clamp = clampSide ? row.Clamp with { Quantity = clampQuantity } : null;

        return new FixtureWork(pallet, row.Fixture, words)
        {
            LeavesAs = instruction.WaitWorkSetting ? Codes.WorkSetLeavesAsWaitingForWorkSetting
                : instruction.LeavesAs is { } leaves ? NonEmpty(Codes.WorkSetLeavesAs(leaves)) : null,
            Detail = detail,
            ReMachining = instruction.Show == WsShowReMachining ? row.ReMachineProgram ?? "" : null,
            WaitingForInspectionConfirmation = inspectionRequired && c.Scrap && unclampSide && !row.InspectionConfirmed ? true : null,
            OffDutyProcess = row.OffDuty ? true : null,
            FixtureName = row.FixtureName, FaceComment = row.FaceComment, StatusReason = row.StatusReason,
            Unclamp = unclamp is null || IsEmpty(unclamp) ? null : unclamp,
            Clamp = clamp is null || IsEmpty(clamp with { Quantity = null }) ? null : clamp,
        };

        static bool IsEmpty(WorkSide w) => w == new WorkSide();
    }
}
