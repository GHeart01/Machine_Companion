using System.Globalization;
using System.Text;

namespace MakinoMCP.Core.Cell;

// Routing: Parts → Processes (the route's steps) → Operations (parts per clamp) and Jobs (work setting, machining, …)
// with their MachineJobs / WorkSetJobs detail, the devices each job may run on (JobFeasibleDevices) and the fixtures
// that can hold each process (FixtureProcesses → PalletFixtures, with FixtureRun for what each place holds now). With
// tools: the tool types each machining program needs (MainProgramTools) against the tools on each feasible machine.
// Blockers: the console's Process Diagnosis, from the orders, fixture places, device states and tool sets.
public sealed partial class SqlMakinoReader
{
    public const string NoPartGiven = "Give a part name (or part of one) to look up.";

    public const string SeveralPartsNote =
        "Several parts match, so no routing is shown: ask again with one candidate's exact name (and revision when names repeat).";

    // MainProgramTools is the tool set the cell itself registered for each program (its own scan follows the
    // subprograms; shared subprograms unless the cell is set not to scan them), the same rows the console's View Tool
    // Set shows. The cell counts a tool usable by its stored status (ready or life warning); this check uses the status
    // as get_tools derives it, so a tool with an alarm bit isn't usable here: deliberately stricter.
    public const string ToolCheckNote =
        "approximate: the tool set the cell registered for the program (its subprograms included); usable = tools of " +
        "that type on the machine in status ready or life warning as get_tools shows it (an alarm bit makes a tool " +
        "unusable here, while the cell counts the stored status, so this check can be stricter)";

    internal const string NothingBlocks = "nothing found that blocks the part's processes (see notChecked)";

    // Orders.Status codes that are finished or undefined (not listed as the part's active orders).
    private const string RoutingFinishedOrders = "0, 5, 6";

    // How many of the part's active orders the blockers list by name.
    private const int OrdersListed = 20;

    // How many held places a blocker names before counting the rest.
    private const int PlacesNamed = 3;

    private const int LackingTools = 10; // FixtureDiagnosisStatus: machine lacks the tools for the program

    /// <summary>A part the search found: name, revision ("" when blank) and how many processes it has.</summary>
    internal sealed record PartMatch(int Id, string Name, string Revision, int ProcessCount);

    // --- the cell's settings routing depends on ---

    private static readonly string[] RoutingParameterNames =
    [
        "ToolManagementType", "ToolResourceManagementType", "PTNDownloadType", "DoNotShowlackingToolIfNoOrder",
        "OptionalBlockSkip", "MachiningTimeExceptStopping", "ProcessOpeCallOneshotType", "UnclampMultiplier",
        "ProbePartCountMacroVariable", "OffsetFileType", "OffsetFileTypePriority", "PalletFixtureOffsetType", "M198ONumber",
        "OrderMachiningSuspend", "TransportCancel", "DutyManagement", "OffDutyOpeCallPalletTransport",
    ];

    /// <summary>The settings routing reads (SystemParameters, the first system's; an absent row is the vendor's
    /// default). Known: false when this login can't read them, then every value is the default and nothing that
    /// depends on them is claimed.</summary>
    internal sealed record RoutingSettings
    {
        public bool Known { get; init; }
        public int? ToolManagement { get; init; }
        public int? ToolResource { get; init; }
        public int? PtnDownload { get; init; }
        public bool LackingOnlyWithOrder { get; init; }
        public bool? BlockSkipSent { get; init; }
        public bool NetOfStops { get; init; }
        public int OneShotType { get; init; }
        public bool UnclampMultiplier { get; init; }
        public int ProbePartCountVariable { get; init; }
        public int OffsetTypes { get; init; } = 1 | 4; // fixture | pallet fixture
        public IReadOnlyList<int> OffsetPriority { get; init; } = [8, 2, 1, 4]; // process, fixture process, fixture, pallet fixture
        public bool PalletFixtureExternal { get; init; }
        public int M198ONumber { get; init; } = 7996;
        public bool OrderMachiningSuspend { get; init; }

        /// <summary>PTN download before machining: the cell adds up every PTN of one tool type.</summary>
        public bool SumsPerType => PtnDownload is 1 or 2;

        /// <summary>Whether the cell holds a machining job for missing tools: tool-resource management that doesn't
        /// only inform. Null when the cell doesn't say (never assumed either way).</summary>
        public bool? ToolsHoldWork => !Known || ToolManagement is null ? null
            : ToolManagement == 2 ? ToolResource != 2
            : false;
    }

    /// <summary>A yes/no setting: 1 (or "True") is on; anything else, or no row, is off.</summary>
    private static bool On(IReadOnlyDictionary<string, string>? values, string name) =>
        values?.GetValueOrDefault(name)?.Trim() is { } v && (ToInt(v) == 1 || v.Equals("true", StringComparison.OrdinalIgnoreCase));

    internal static RoutingSettings RoutingSettingsFrom(IReadOnlyDictionary<string, string>? p)
    {
        if (p is null) return new RoutingSettings();
        int? Get(string name) => ToInt(p.GetValueOrDefault(name));
        var priority = (p.GetValueOrDefault("OffsetFileTypePriority") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ToInt).OfType<int>().ToList();
        return new RoutingSettings
        {
            Known = true,
            ToolManagement = Get("ToolManagementType"),
            ToolResource = Get("ToolResourceManagementType") ?? 0,
            PtnDownload = Get("PTNDownloadType") ?? 0,
            LackingOnlyWithOrder = Get("DoNotShowlackingToolIfNoOrder") == 1,
            BlockSkipSent = On(p, "OptionalBlockSkip"),
            NetOfStops = On(p, "MachiningTimeExceptStopping"),
            OneShotType = Get("ProcessOpeCallOneshotType") ?? 0,
            UnclampMultiplier = On(p, "UnclampMultiplier"),
            ProbePartCountVariable = Get("ProbePartCountMacroVariable") ?? 0,
            OffsetTypes = Get("OffsetFileType") ?? (1 | 4),
            OffsetPriority = priority.Count > 0 ? priority : [8, 2, 1, 4],
            PalletFixtureExternal = Get("PalletFixtureOffsetType") == 1,
            M198ONumber = Get("M198ONumber") is > 0 and var m198 ? m198 : 7996,
            OrderMachiningSuspend = Get("OrderMachiningSuspend") == 1,
        };
    }

    /// <summary>How far the tool check goes on this cell: <see cref="ToolCheckNote"/> plus the cell's own lacking rule
    /// (whether it checks tools at all, only warns, also checks remaining life, adds up PTNs of a type, or shows
    /// lacking tools only for parts with an order).</summary>
    internal static string ToolCheckWords(RoutingSettings s)
    {
        var words = new StringBuilder(ToolCheckNote);
        if (!s.Known)
        {
            words.Append("; this login can't read the cell's tool settings, so whether the cell holds work for missing tools is unknown");
            return words.ToString();
        }
        if (s.ToolManagement is 0 or 1)
            words.Append($"; this cell schedules without checking tools (tool management: {Codes.ToolManagementType(s.ToolManagement)}), so missingOn doesn't hold work");
        else if (s.ToolManagement is null)
            words.Append("; the cell doesn't say how it manages tools, so whether it holds work for missing tools is unknown");
        else if (s.ToolResource == 2)
            words.Append("; this cell doesn't hold jobs for missing tools, it only warns");
        if (s.ToolResource is 1 or 3 or 4)
            words.Append($"; this cell also checks remaining life ({Codes.ToolResourceManagementType(s.ToolResource)}), this check doesn't, so a machine can still lack tools with enough of them there");
        else
            words.Append("; remaining life not checked");
        if (s.SumsPerType)
            words.Append("; the cell adds up the needed tools of every program tool number of one tool type (neededForType)");
        if (s.LackingOnlyWithOrder)
            words.Append("; missingOn ignores orders, while this cell shows lacking tools only for parts with an active order");
        return words.ToString();
    }

    public async Task<PartRouting> GetPartRoutingAsync(string part, string? revision, bool includeTools, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(part)) throw new ArgumentException(NoPartGiven);
        var wanted = part.Trim();
        var rev = string.IsNullOrWhiteSpace(revision) ? null : revision.Trim();

        await using var s = await OpenAsync(ct);
        var schema = s.Schema;
        schema.Require("Parts", "Processes", "Operations", "Jobs", "MachineJobs", "WorkSetJobs", "JobFeasibleDevices", "NCProgramFiles");
        var withRevision = rev is not null && schema.Has("Parts", "Revision");
        var notAvailable = new List<string>();
        if (rev is not null && !withRevision) notAvailable.Add("revision filter (this cell keeps no part revisions)");

        var found = new List<PartMatch>();
        using (var cmd = Command(s.Connection, PartSearchSql(schema, withRevision)))
        {
            AddTake(cmd);
            cmd.Parameters.Add("@part", System.Data.SqlDbType.VarChar, 400).Value = wanted;
            cmd.Parameters.Add("@pattern", System.Data.SqlDbType.VarChar, 1000).Value = LikePattern(wanted);
            if (withRevision) cmd.Parameters.Add("@revision", System.Data.SqlDbType.VarChar, 400).Value = rev;
            using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                found.Add(new PartMatch(Int(r, "PartID") ?? 0, Str(r, "PartName"), Str(r, "Revision"), Int(r, "ProcessCount") ?? 0));
        }
        var (shown, capped) = Cap(found);
        var (chosen, candidates, others) = PickPart(shown, wanted);
        if (chosen is null)
        {
            var asked = rev is null ? $"'{wanted}'" : $"'{wanted}' revision '{rev}'";
            return new PartRouting(s.Now, wanted, capped)
            {
                Candidates = candidates.Select(c => new PartCandidate(c.Name, c.ProcessCount) { Revision = NonEmpty(c.Revision) }).ToList(),
                Note = candidates.Count > 0 ? SeveralPartsNote : $"No part name is or contains {asked}.",
                NotAvailable = notAvailable.Count > 0 ? notAvailable : null,
            };
        }

        // The cell's settings first: one open reader per connection.
        var parameters = await SystemParametersAsync(s, ct, RoutingParameterNames);
        var runtime = await UserSysDataAsync(s, ct, "DutyMode");
        var settings = RoutingSettingsFrom(parameters);
        if (parameters is null) notAvailable.Add("the cell's settings (tool management, offset files, block skips, …)");

        // The search's cap only matters for candidates; a picked part's routing is capped by its own fixture and tool rows.
        var read = await ReadRouteAsync(s, chosen, settings, notAvailable, ct);
        var route = read.Route;
        capped = read.FixturesCapped;
        Dictionary<(int Process, int Job), IReadOnlyList<string>>? lacking = null;
        if (includeTools)
        {
            var tools = await AddToolsAsync(s, chosen.Id, route, read.Programs, settings, notAvailable, ct);
            (route, capped, lacking) = (tools.Route, capped || tools.Capped, tools.Lacking);
        }

        var blockers = await BlockersAsync(s, chosen.Id, read, lacking, includeTools, settings,
            DiagSettingsFrom(parameters, runtime), notAvailable, ct);
        return new PartRouting(s.Now, wanted, capped)
        {
            Part = route,
            OtherMatches = others.Count > 0 ? others.Select(o => PartLabel(o.Name, o.Revision)).ToList() : null,
            ToolCheck = includeTools ? ToolCheckWords(settings) : null,
            Blockers = blockers,
            NotAvailable = notAvailable.Count > 0 ? notAvailable : null,
        };
    }

    private static string PartLabel(string name, string revision) => revision.Length > 0 ? $"{name} rev {revision}" : name;

    /// <summary>The part named exactly <paramref name="wanted"/> (case-insensitive, spaces around ignored) wins; several
    /// with that name (revisions) are candidates. With no exact name, one part containing it is picked, several are
    /// candidates. Others: the rest of what was found, next to an exact pick.</summary>
    internal static (PartMatch? Chosen, IReadOnlyList<PartMatch> Candidates, IReadOnlyList<PartMatch> Others) PickPart(
        IReadOnlyList<PartMatch> found, string wanted)
    {
        var name = wanted.Trim();
        var exact = found.Where(f => string.Equals(f.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1) return (exact[0], [], found.Where(f => f.Id != exact[0].Id).ToList());
        if (exact.Count > 1) return (null, exact, []);
        if (found.Count == 1) return (found[0], [], []);
        return (null, found.ToList(), []);
    }

    /// <summary>A LIKE pattern (ESCAPE '\') matching names that contain <paramref name="text"/> literally.</summary>
    internal static string LikePattern(string text)
    {
        var escaped = text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");
        return $"%{escaped}%";
    }

    /// <summary>Parts whose name contains @pattern (and, with <paramref name="withRevision"/>, whose revision is
    /// @revision), exact names (@part) first, cap + 1.</summary>
    internal static string PartSearchSql(SchemaInfo schema, bool withRevision)
    {
        var cols = string.Join(", ",
            "p.PartID", "p.PartName",
            Opt(schema, "Parts", "p", "Revision", type: Text),
            "(SELECT COUNT(*) FROM Processes pr WHERE pr.PartID = p.PartID) AS ProcessCount");
        var revision = withRevision ? " AND UPPER(ISNULL(p.Revision, '')) = UPPER(@revision)" : "";
        var then = schema.Has("Parts", "Revision") ? "p.PartName, p.Revision, p.PartID" : "p.PartName, p.PartID";
        return $"""
            SELECT TOP (@take) {cols}
            FROM Parts p
            WHERE UPPER(p.PartName) LIKE UPPER(@pattern) ESCAPE '\'{revision}
            ORDER BY CASE WHEN UPPER(p.PartName) = UPPER(@part) THEN 0 ELSE 1 END, {then};
            """;
    }

    // The fixture result set's columns, so the empty set (a cell without FixtureProcesses) has the same names.
    private static readonly (string Name, string Type)[] RoutingFixtureColumns =
    [
        ("ProcessID", "int"), ("FixtureID", "int"), ("FixturePriority", "int"), ("Enabled", "bit"), ("FixtureName", Text),
        ("PalletNumber", "int"), ("FixtureNumber", "int"), ("Suspend", "bit"), ("WorkSetHold", "bit"), ("Damaged", "bit"),
        ("PreMcSuspend", "bit"), ("UseProcessID", "int"), ("UsePartName", Text), ("UseProcessNumber", "int"),
        ("UseOrderMachiningSuspended", "bit"), ("FixtureProcessOffset", Text), ("FixtureOffset", Text),
        ("PalletFixtureOffset", Text),
    ];

    /// <summary>Whether the fixture places can show what each holds now (FixtureRun by pallet fixture).</summary>
    internal static bool PlacesShowUse(SchemaInfo schema) =>
        schema.HasTable("FixtureProcesses") && schema.Has("PalletFixtures", "FixtureID") && schema.Has("PalletFixtures", "PalletFixtureID")
        && schema.Has("FixtureRun", "PalletFixtureID") && schema.Has("FixtureRun", "CurProcessID");

    /// <summary>Seven result sets for one part (@partId): the part, its processes, their operations, their jobs (with
    /// the machining and work-set detail and the program's tool study), the jobs' feasible devices, the fixtures that
    /// can hold each process (every pallet place with what it holds now, so cap + 1 rows; an empty set when the cell
    /// has no FixtureProcesses), and the O-number types of the part's programs.</summary>
    internal static string RoutingSql(SchemaInfo schema)
    {
        var sql = new StringBuilder();
        sql.AppendLine($"SELECT {Opt(schema, "Parts", "p", "Comment", type: Text)}, " +
                       $"{Opt(schema, "Parts", "p", "UpdateDate", "PartUpdated", "datetime")} FROM Parts p WHERE p.PartID = @partId;");

        var processOrder = schema.Has("Processes", "ProcessNumber") ? "pr.ProcessNumber, pr.ProcessID" : "pr.ProcessID";
        var inUse = schema.Has("FixtureRun", "CurProcessID")
            ? "(SELECT COUNT(*) FROM FixtureRun fr WHERE fr.CurProcessID = pr.ProcessID) AS InUse"
            : "CAST(NULL AS int) AS InUse";
        sql.AppendLine($"""
            SELECT pr.ProcessID, {string.Join(", ",
                Opt(schema, "Processes", "pr", "ProcessNumber"),
                Opt(schema, "Processes", "pr", "ProcessName", type: Text),
                Opt(schema, "Processes", "pr", "Status"),
                Opt(schema, "Processes", "pr", "MaxFixtures"),
                Opt(schema, "Processes", "pr", "OffDutyProcess", type: "bit"),
                inUse)}
            FROM Processes pr WHERE pr.PartID = @partId ORDER BY {processOrder};
            """);

        var opOrder = schema.Has("Operations", "OperationSequence") ? "o.ProcessID, o.OperationSequence" : "o.ProcessID, o.OperationID";
        sql.AppendLine($"""
            SELECT o.ProcessID, {string.Join(", ",
                Opt(schema, "Operations", "o", "OperationSequence"),
                Opt(schema, "Operations", "o", "ClampQuantity"),
                Opt(schema, "Operations", "o", "UnclampMultiplier"),
                Opt(schema, "Operations", "o", "OperationName", type: Text))}
            FROM Operations o JOIN Processes pr ON pr.ProcessID = o.ProcessID
            WHERE pr.PartID = @partId ORDER BY {opOrder};
            """);

        var info = schema.HasTable("InformationFiles") && schema.Has("WorkSetJobs", "InformationFileID");
        var byProgram = schema.Has("MachineJobs", "NCProgramFileID");
        var study = schema.HasTable("MainPrograms") && schema.Has("MainPrograms", "NCProgramFileID") && byProgram;
        var jobCols = string.Join(", ",
            "j.ProcessID", "j.JobID",
            Opt(schema, "Jobs", "j", "JobNumber"),
            Opt(schema, "Jobs", "j", "JobType"),
            Opt(schema, "MachineJobs", "mj", "JobID", "MachineJobID"),
            Opt(schema, "MachineJobs", "mj", "NCProgramFileID"),
            Opt(schema, "NCProgramFiles", "n", "Name", "ProgramName", Text),
            Opt(schema, "NCProgramFiles", "n", "UpdateDate", "ProgramUpdated", "datetime"),
            Opt(schema, "MachineJobs", "mj", "MachiningTime"),
            Opt(schema, "MachineJobs", "mj", "OperatorCall", "JobOperatorCall"),
            Opt(schema, "MachineJobs", "mj", "TimeStudy", type: "bit"),
            Opt(schema, "MachineJobs", "mj", "OptionalBlockSkip"),
            Opt(schema, "MachineJobs", "mj", "ONumber", "JobONumber"),
            study ? Opt(schema, "MainPrograms", "mp", "NCProgramFileID", "StudyProgramID") : "CAST(NULL AS int) AS StudyProgramID",
            study ? Opt(schema, "MainPrograms", "mp", "studyMode", "StudyMode") : "CAST(NULL AS int) AS StudyMode",
            study ? Opt(schema, "MainPrograms", "mp", "StudiedDate", type: "datetime") : "CAST(NULL AS datetime) AS StudiedDate",
            study ? Opt(schema, "MainPrograms", "mp", "DeviceID", "StudiedDeviceID") : "CAST(NULL AS int) AS StudiedDeviceID",
            study && schema.Has("MainPrograms", "PalletID")
                ? PalletNumberSql(schema, "mp.PalletID", "StudiedPallet") : "CAST(NULL AS int) AS StudiedPallet",
            Opt(schema, "WorkSetJobs", "wj", "JobID", "WorkSetJobID"),
            Opt(schema, "WorkSetJobs", "wj", "OperationType", "WorkSetOperation"),
            info ? Opt(schema, "InformationFiles", "i", "Name", "InfoFileName", Text) : $"CAST(NULL AS {Text}) AS InfoFileName",
            Opt(schema, "WorkSetJobs", "wj", "WorkSetTime"));
        var programJoin = byProgram
            ? " LEFT JOIN NCProgramFiles n ON n.NCProgramFileID = mj.NCProgramFileID"
            : " LEFT JOIN NCProgramFiles n ON 1 = 0";
        var studyJoin = study ? " LEFT JOIN MainPrograms mp ON mp.NCProgramFileID = mj.NCProgramFileID" : "";
        var infoJoin = info ? " LEFT JOIN InformationFiles i ON i.InformationFileID = wj.InformationFileID" : "";
        var jobOrder = schema.Has("Jobs", "JobNumber") ? "j.ProcessID, j.JobNumber, j.JobID" : "j.ProcessID, j.JobID";
        sql.AppendLine($"""
            SELECT {jobCols}
            FROM Jobs j JOIN Processes pr ON pr.ProcessID = j.ProcessID
            LEFT JOIN MachineJobs mj ON mj.JobID = j.JobID{programJoin}{studyJoin}
            LEFT JOIN WorkSetJobs wj ON wj.JobID = j.JobID{infoJoin}
            WHERE pr.PartID = @partId ORDER BY {jobOrder};
            """);

        sql.AppendLine("""
            SELECT f.JobID, f.FeasibleDeviceID
            FROM JobFeasibleDevices f JOIN Jobs j ON j.JobID = f.JobID JOIN Processes pr ON pr.ProcessID = j.ProcessID
            WHERE pr.PartID = @partId;
            """);

        sql.AppendLine(FixturesSql(schema));
        sql.AppendLine(ProgramTypesSql(schema));
        return sql.ToString();
    }

    /// <summary>The pallet places that can hold each of the part's processes, cap + 1, with their holds, what each
    /// holds now and the offset files assigned at each level.</summary>
    private static string FixturesSql(SchemaInfo schema)
    {
        if (!schema.HasTable("FixtureProcesses"))
            return $"SELECT {string.Join(", ", RoutingFixtureColumns.Select(c => $"CAST(NULL AS {c.Type}) AS {c.Name}"))} WHERE 1 = 0;";

        var named = schema.HasTable("Fixtures");
        var onPallets = schema.HasTable("PalletFixtures") && schema.Has("PalletFixtures", "FixtureID");
        var run = PlacesShowUse(schema);
        var offsets = schema.Has("OffsetFiles", "OffsetFileID") && schema.Has("OffsetFiles", "Name");
        string OffsetName(bool joined, string table, string alias, string name) =>
            joined && offsets && schema.Has(table, "OffsetFileID")
                ? $"(SELECT TOP 1 ofs.Name FROM OffsetFiles ofs WHERE ofs.OffsetFileID = {alias}.OffsetFileID) AS {name}"
                : $"CAST(NULL AS {Text}) AS {name}";
        string Pf(string column, string type = "int") =>
            onPallets ? Opt(schema, "PalletFixtures", "pf", column, type: type) : $"CAST(NULL AS {type}) AS {column}";

        var cols = string.Join(", ",
            "fp.ProcessID", "fp.FixtureID",
            Opt(schema, "FixtureProcesses", "fp", "FixturePriority"),
            Opt(schema, "FixtureProcesses", "fp", "Enabled", type: "bit"),
            named ? Opt(schema, "Fixtures", "fx", "FixtureName", type: Text) : $"CAST(NULL AS {Text}) AS FixtureName",
            onPallets && schema.Has("PalletFixtures", "PalletID")
                ? PalletNumberSql(schema, "pf.PalletID", "PalletNumber") : "CAST(NULL AS int) AS PalletNumber",
            Pf("FixtureNumber"), Pf("Suspend", "bit"), Pf("WorkSetHold", "bit"), Pf("Damaged", "bit"), Pf("PreMcSuspend", "bit"),
            run ? "fr.CurProcessID AS UseProcessID" : "CAST(NULL AS int) AS UseProcessID",
            run ? Opt(schema, "Parts", "upp", "PartName", "UsePartName", Text) : $"CAST(NULL AS {Text}) AS UsePartName",
            run ? Opt(schema, "Processes", "up", "ProcessNumber", "UseProcessNumber") : "CAST(NULL AS int) AS UseProcessNumber",
            run && schema.Has("FixtureRun", "CurOrderID")
                ? Opt(schema, "Orders", "uo", "MachiningSuspended", "UseOrderMachiningSuspended", "bit")
                : "CAST(NULL AS bit) AS UseOrderMachiningSuspended",
            OffsetName(true, "FixtureProcesses", "fp", "FixtureProcessOffset"),
            OffsetName(named, "Fixtures", "fx", "FixtureOffset"),
            OffsetName(onPallets, "PalletFixtures", "pf", "PalletFixtureOffset"));
        var joins = new StringBuilder();
        if (named) joins.Append(" LEFT JOIN Fixtures fx ON fx.FixtureID = fp.FixtureID");
        if (onPallets) joins.Append(" LEFT JOIN PalletFixtures pf ON pf.FixtureID = fp.FixtureID");
        if (run)
        {
            joins.Append(" LEFT JOIN FixtureRun fr ON fr.PalletFixtureID = pf.PalletFixtureID");
            joins.Append(" LEFT JOIN Processes up ON up.ProcessID = fr.CurProcessID LEFT JOIN Parts upp ON upp.PartID = up.PartID");
            if (schema.Has("FixtureRun", "CurOrderID") && schema.HasTable("Orders"))
                joins.Append(" LEFT JOIN Orders uo ON uo.OrderID = fr.CurOrderID");
        }
        return $"""
            SELECT TOP (@take) {cols}
            FROM FixtureProcesses fp JOIN Processes pr ON pr.ProcessID = fp.ProcessID{joins}
            WHERE pr.PartID = @partId ORDER BY fp.ProcessID, FixturePriority, PalletNumber, FixtureNumber, fp.FixtureID;
            """;
    }

    /// <summary>Per program of the part's machining jobs, each O-number type it holds with its first O-number (by file
    /// index): a few rows per program, never one per subprogram.</summary>
    private static string ProgramTypesSql(SchemaInfo schema)
    {
        const string empty = "SELECT CAST(NULL AS int) AS NCProgramFileID, CAST(NULL AS int) AS ProgramType, " +
                             "CAST(NULL AS int) AS FileIndex, CAST(NULL AS int) AS ONumber WHERE 1 = 0;";
        if (!schema.Has("ONumbers", "NCProgramFileID") || !schema.Has("ONumbers", "ONumber") || !schema.Has("ONumbers", "ProgramType")
            || !schema.Has("MachineJobs", "NCProgramFileID"))
            return empty;
        var index = schema.Has("ONumbers", "FileIndex");
        var first = index ? "o2.FileIndex, o2.ONumber" : "o2.ONumber";
        return $"""
            SELECT o.NCProgramFileID, o.ProgramType, {(index ? "MIN(o.FileIndex)" : "0")} AS FileIndex,
                (SELECT TOP 1 o2.ONumber FROM ONumbers o2
                 WHERE o2.NCProgramFileID = o.NCProgramFileID AND o2.ProgramType = o.ProgramType ORDER BY {first}) AS ONumber
            FROM ONumbers o
            WHERE o.NCProgramFileID IN (
                SELECT mj.NCProgramFileID FROM MachineJobs mj JOIN Jobs j ON j.JobID = mj.JobID
                JOIN Processes pr ON pr.ProcessID = j.ProcessID WHERE pr.PartID = @partId)
            GROUP BY o.NCProgramFileID, o.ProgramType;
            """;
    }

    /// <summary>The O-number the cell starts for a program file: as the cell picks the file's first O-number, its main
    /// program (type 1) first, else the file-index-0 dataserver main (64), else a dataserver (8), which the CNC runs
    /// through the cell's M198 program (<paramref name="m198"/>); null when the file has no O-numbers.</summary>
    internal static int? MainONumber(IReadOnlyList<(int Type, int FileIndex, int ONumber)> types, int m198)
    {
        if (types.Count == 0) return null;
        if (types.FirstOrDefault(t => t.Type == 1) is { Type: 1 } main) return main.ONumber;
        var start = types.OrderBy(t => t.FileIndex).ThenBy(t => t.Type == 64 ? 0 : t.Type == 8 ? 1 : 2).First();
        return start.Type == 8 ? m198 : start.ONumber;
    }

    /// <summary>The offset file the cell would send for a fixture place, walking the cell's priority: the first level
    /// that is enabled and has a file wins. The process level selects nothing in this software version; the pallet
    /// fixture level counts only when its offsets are files, and with external offsets the search stops there
    /// (External: the offsets come from outside, no file).</summary>
    internal static (string? File, bool External) OffsetFileFor(IReadOnlyList<int> priority, int enabled, bool palletFixtureExternal,
        string? fixtureProcess, string? fixture, string? palletFixture)
    {
        foreach (var level in priority)
        {
            var file = level switch
            {
                2 when (enabled & 2) != 0 => fixtureProcess,
                1 when (enabled & 1) != 0 => fixture,
                4 when (enabled & 4) != 0 && !palletFixtureExternal => palletFixture,
                _ => null,
            };
            if (!string.IsNullOrWhiteSpace(file)) return (file, false);
            if ((level & 4) != 0 && palletFixtureExternal) return (null, true);
        }
        return (null, false);
    }

    /// <summary>A fixture place as shown: where it is, then what it holds now ("this process", "in use: PART process
    /// N", "free"; nothing when that isn't known), its holds and the offset file the cell would send.</summary>
    internal static string FixturePlaceState(string place, string? use, IReadOnlyList<string> holds, string? offset)
    {
        var words = new List<string>();
        if (use is not null) words.Add(use);
        words.AddRange(holds);
        var text = words.Count > 0 ? $"{place}: {string.Join(", ", words)}" : place;
        return offset is null ? text : $"{text}; {offset}";
    }

    /// <summary>What a place holds, in words: null when unknown (the cell's FixtureRun can't be read or the fixture is
    /// on no pallet), "free" for no current process.</summary>
    internal static string? PlaceUse(bool known, int? useProcessId, int processId, string usePart, int? useProcess) =>
        !known ? null
        : useProcessId is null or 0 ? "free"
        : useProcessId == processId ? "this process"
        : $"in use: {(usePart.Length > 0 ? usePart : "another part")}{(useProcess is { } n ? $" process {n}" : "")}";

    // --- reading the route ---

    private sealed record JobRow(int ProcessId, int JobId, int? JobType, JobRoute Job, int? ProgramId, int? OperatorCall);

    /// <summary>One process as the blockers see it.</summary>
    internal sealed record BlockerProcess(int Id, int Number, int? Status)
    {
        public int? MaxFixtures { get; init; }
        public int? InUse { get; init; }
        public bool OffDuty { get; init; }
        /// <summary>FixtureProcesses rows for the process (pallet place or not); null when the cell has none.</summary>
        public int? Assigned { get; init; }
        public IReadOnlyList<BlockerPlace> Places { get; init; } = [];
        public IReadOnlyList<BlockerJob> Jobs { get; init; } = [];
    }

    /// <summary>A pallet place that can hold the process. UseProcessId: what it holds now (0 free, null unknown).</summary>
    internal sealed record BlockerPlace(string Place, bool Enabled, IReadOnlyList<string> Holds, int? UseProcessId);

    /// <summary>A job with the devices it may run on.</summary>
    internal sealed record BlockerJob(int Number, int? Type, int? OperatorCall, IReadOnlyList<int> Devices);

    internal sealed record BlockerOrder(string Name, int? Status, bool MachiningSuspended);

    /// <summary>What reading the route gives: the route, per process (in the same order) the program id of each of its
    /// jobs (null for jobs that aren't machining), the processes as the blockers see them, and whether the fixtures
    /// list was cut at the row limit.</summary>
    private sealed record RouteRead(PartRoute Route, List<List<int?>> Programs, List<BlockerProcess> Processes, bool FixturesCapped,
        bool PlacesKnown);

    private async Task<RouteRead> ReadRouteAsync(Session s, PartMatch part, RoutingSettings settings, List<string> notAvailable,
        CancellationToken ct)
    {
        var schema = s.Schema;
        if (!schema.HasTable("FixtureProcesses")) notAvailable.Add("fixtures for each process");
        else if (!PlacesShowUse(schema)) notAvailable.Add("what each fixture place holds now");
        if (!schema.HasTable("InformationFiles")) notAvailable.Add("work instruction file names");
        if (!schema.HasTable("ONumbers")) notAvailable.Add("program O-numbers and types");
        if (!schema.HasTable("MainPrograms")) notAvailable.Add("tool study per program");

        using var cmd = Command(s.Connection, RoutingSql(schema));
        AddTake(cmd);
        cmd.Parameters.Add("@partId", System.Data.SqlDbType.Int).Value = part.Id;
        using var r = await cmd.ExecuteReaderAsync(ct);

        string comment = "";
        DateTime? updated = null;
        if (await r.ReadAsync(ct)) (comment, updated) = (Str(r, "Comment"), Date(r, "PartUpdated"));

        await r.NextResultAsync(ct);
        var processes = new List<(int Id, int Number, string Name, int? Status, int? MaxFixtures, bool OffDuty, int? InUse)>();
        while (await r.ReadAsync(ct))
        {
            processes.Add((Int(r, "ProcessID") ?? 0, Int(r, "ProcessNumber") ?? 0, Str(r, "ProcessName"), Int(r, "Status"),
                Int(r, "MaxFixtures"), Bool(r, "OffDutyProcess") == true, Int(r, "InUse")));
        }

        await r.NextResultAsync(ct);
        var operations = new List<(int ProcessId, OperationRoute Operation)>();
        while (await r.ReadAsync(ct))
        {
            var multiplier = Int(r, "UnclampMultiplier");
            operations.Add((Int(r, "ProcessID") ?? 0,
                new OperationRoute(Int(r, "OperationSequence") ?? 0, Int(r, "ClampQuantity") ?? 0)
                {
                    Name = NonEmpty(Str(r, "OperationName")),
                    UnclampPerClamp = settings.UnclampMultiplier && multiplier is > 1 ? multiplier : null,
                }));
        }

        await r.NextResultAsync(ct);
        var jobs = new List<JobRow>();
        while (await r.ReadAsync(ct)) jobs.Add(JobFrom(r, s, settings));

        await r.NextResultAsync(ct);
        var feasible = new Dictionary<int, List<int>>();
        while (await r.ReadAsync(ct))
        {
            var jobId = Int(r, "JobID") ?? 0;
            if (!feasible.TryGetValue(jobId, out var list)) feasible[jobId] = list = [];
            if (Int(r, "FeasibleDeviceID") is { } device) list.Add(device);
        }

        await r.NextResultAsync(ct);
        var placesKnown = PlacesShowUse(schema);
        var fixtures = new Dictionary<int, List<string>>();
        var places = new Dictionary<int, List<BlockerPlace>>();
        var assigned = new Dictionary<int, int>();
        var fixtureRows = 0;
        while (await r.ReadAsync(ct))
        {
            if (++fixtureRows > _cap) break; // the row past the cap only shows that more existed
            var processId = Int(r, "ProcessID") ?? 0;
            assigned[processId] = assigned.GetValueOrDefault(processId) + 1;
            var pallet = Int(r, "PalletNumber");
            var enabled = Bool(r, "Enabled") != false;
            var place = FixturePlace(pallet, Int(r, "FixtureNumber"), Str(r, "FixtureName"), Int(r, "FixtureID") ?? 0, enabled);
            var useId = Int(r, "UseProcessID");
            var holds = new List<string>();
            if (Bool(r, "Suspend") == true) holds.Add("suspended");
            if (Bool(r, "WorkSetHold") == true) holds.Add("work-set hold");
            if (Bool(r, "Damaged") == true) holds.Add("damaged");
            if (Bool(r, "PreMcSuspend") == true) holds.Add("pre-machining suspended");
            if (settings.OrderMachiningSuspend && useId is > 0 && Bool(r, "UseOrderMachiningSuspended") == true)
                holds.Add("order machining suspended");
            string? offset = null;
            if (pallet is not null && settings.Known)
            {
                var (file, external) = OffsetFileFor(settings.OffsetPriority, settings.OffsetTypes, settings.PalletFixtureExternal,
                    NonEmpty(Str(r, "FixtureProcessOffset")), NonEmpty(Str(r, "FixtureOffset")), NonEmpty(Str(r, "PalletFixtureOffset")));
                offset = file is not null ? $"offset file {file}" : external ? "external offsets" : null;
            }
            var use = PlaceUse(placesKnown && pallet is not null, useId, processId, Str(r, "UsePartName"), Int(r, "UseProcessNumber"));
            if (!fixtures.TryGetValue(processId, out var list)) fixtures[processId] = list = [];
            list.Add(FixturePlaceState(place, use, holds, offset));
            if (pallet is null) continue;
            if (!places.TryGetValue(processId, out var shown)) places[processId] = shown = [];
            shown.Add(new BlockerPlace(place, enabled, holds, placesKnown ? useId ?? 0 : null));
        }

        await r.NextResultAsync(ct);
        var types = new Dictionary<int, List<(int Type, int FileIndex, int ONumber)>>();
        while (await r.ReadAsync(ct))
        {
            var program = Int(r, "NCProgramFileID") ?? 0;
            if (!types.TryGetValue(program, out var list)) types[program] = list = [];
            list.Add((Int(r, "ProgramType") ?? 0, Int(r, "FileIndex") ?? 0, Int(r, "ONumber") ?? 0));
        }

        // Devices in the cell's own order (machines, then the rest as Devices lists them).
        int Rank(int id)
        {
            for (var i = 0; i < s.Devices.Count; i++) if (s.Devices[i].Id == id) return i;
            return int.MaxValue;
        }
        JobRoute Shown(JobRow j)
        {
            var job = j.Job;
            var ids = feasible.GetValueOrDefault(j.JobId);
            if (ids is { Count: > 0 })
                job = job with { FeasibleDevices = ids.Distinct().OrderBy(Rank).ThenBy(id => id).Select(s.NameOf).ToList() };
            else if (j.JobType is MachiningJob or WorkSettingJob)
                job = job with
                {
                    FeasibleDevices = [],
                    Note = Join(job.Note ?? "", j.JobType == MachiningJob
                        ? "not assigned to any machine: the cell won't schedule it"
                        : "not assigned to any work-set station: the cell won't schedule it"),
                };
            if (j.ProgramId is { } program && types.TryGetValue(program, out var held))
            {
                var flags = held.Aggregate(0, (all, t) => all | t.Type);
                job = job with
                {
                    ONumber = MainONumber(held, settings.M198ONumber),
                    ProgramType = Codes.NcProgramTypes(flags) is { Count: > 0 } words ? words : null,
                };
            }
            return job;
        }

        var routes = processes.Select(p =>
        {
            var ops = operations.Where(o => o.ProcessId == p.Id).Select(o => o.Operation).ToList();
            return new ProcessRoute(
                p.Number,
                Codes.ProcessStatus(p.Status) is { Length: > 0 } status ? status : "unknown",
                ops,
                jobs.Where(j => j.ProcessId == p.Id).Select(Shown).ToList())
            {
                Name = NonEmpty(p.Name),
                MaxFixtures = p.MaxFixtures is > 0 ? p.MaxFixtures : null,
                InUse = p.InUse is > 0 ? p.InUse : null,
                Note = ops.Count > 1 ? $"{ops.Count} operations: the process's machining program machines all of them in one clamping" : null,
                Fixtures = fixtures.TryGetValue(p.Id, out var shown) && shown.Count > 0 ? shown : null,
            };
        }).ToList();

        // The part's status: its most severe process status (the cell's own part list shows the highest).
        var worst = processes.Select(p => p.Status).Where(x => x is not null).DefaultIfEmpty(null).Max();
        var notes = new List<string>();
        if (settings.ProbePartCountVariable > 0)
        {
            notes.Add($"this cell's probe can lower the parts clamped per fixture at machining (probe part count in macro " +
                      $"variable #{settings.ProbePartCountVariable}), so fewer parts than clampQuantity can be by design");
        }
        var route = new PartRoute(part.Name, worst is null ? "unknown" : Codes.ProcessStatus(worst), routes)
        {
            Revision = NonEmpty(part.Revision), Comment = NonEmpty(comment), Updated = updated,
            Notes = notes.Count > 0 ? notes : null,
        };
        var programs = processes.Select(p => jobs.Where(j => j.ProcessId == p.Id).Select(j => j.ProgramId).ToList()).ToList();
        var blockerProcesses = processes.Select(p => new BlockerProcess(p.Id, p.Number, p.Status)
        {
            MaxFixtures = p.MaxFixtures is > 0 ? p.MaxFixtures : null,
            InUse = p.InUse,
            OffDuty = p.OffDuty,
            Assigned = schema.HasTable("FixtureProcesses") ? assigned.GetValueOrDefault(p.Id) : null,
            Places = places.GetValueOrDefault(p.Id) ?? [],
            Jobs = jobs.Where(j => j.ProcessId == p.Id)
                .Select(j => new BlockerJob(j.Job.Number, j.JobType, j.OperatorCall, feasible.GetValueOrDefault(j.JobId)?.Distinct().ToList() ?? []))
                .ToList(),
        }).ToList();
        return new RouteRead(route, programs, blockerProcesses, fixtureRows > _cap, placesKnown);
    }

    /// <summary>One job row of the routing: type, then the machining detail (program, its O-number, planned minutes,
    /// operator call in the cell's terms, time study, block skips, tool study) or the work-set detail.</summary>
    private static JobRow JobFrom(Microsoft.Data.SqlClient.SqlDataReader r, Session s, RoutingSettings settings)
    {
        var type = Int(r, "JobType");
        var job = new JobRoute(Int(r, "JobNumber") ?? 0, Codes.JobType(type) is { Length: > 0 } words ? words : "unknown");
        var machining = Int(r, "MachineJobID") is not null;
        int? program = null;
        var call = machining ? Int(r, "JobOperatorCall") : null;
        if (machining)
        {
            program = Int(r, "NCProgramFileID");
            var notes = new List<string>();
            var timeStudy = Bool(r, "TimeStudy") == true;
            if (timeStudy)
            {
                notes.Add("time study: the planned minutes will be replaced by the next normal cycle's time" +
                          (settings.Known ? settings.NetOfStops ? " (without feed holds and operator calls)" : " (feed holds and operator calls included)" : ""));
            }
            var skips = Codes.BlockSkips(Int(r, "OptionalBlockSkip"));
            if (skips.Count > 0 && settings.Known && settings.BlockSkipSent == false)
                notes.Add("block skips set on the job aren't sent: this cell's block-skip setting is off");
            ProgramStudy? study = null;
            if (Int(r, "StudyProgramID") is not null)
            {
                var deviceId = Int(r, "StudiedDeviceID");
                study = new ProgramStudy(Codes.ToolStudyMode(Int(r, "StudyMode")) is { Length: > 0 } mode ? mode : "unknown")
                {
                    Studied = Date(r, "StudiedDate"),
                    StudiedOn = deviceId is > 0 ? s.NameOf(deviceId.Value) : null,
                    StudiedPallet = Int(r, "StudiedPallet"),
                };
            }
            job = job with
            {
                Program = NonEmpty(Str(r, "ProgramName")),
                ONumber = program is null && Int(r, "JobONumber") is > 0 and var resident ? resident : null,
                ProgramUpdated = program is null ? null : Date(r, "ProgramUpdated"),
                Minutes = Minutes(Int(r, "MachiningTime")),
                OperatorCall = call is > 0 ? OperatorCallWords(call, settings) : null,
                TimeStudy = timeStudy ? true : null,
                BlockSkips = skips.Count > 0 ? skips : null,
                ToolStudy = study,
                Note = notes.Count > 0 ? string.Join("; ", notes) : null,
            };
            if (program is null && job.ONumber is not null)
                job = job with { Note = Join(job.Note ?? "", "a program resident in the CNC (no program file)") };
        }
        if (Int(r, "WorkSetJobID") is not null)
        {
            job = job with
            {
                WorkSet = Int(r, "WorkSetOperation") is { } op ? Codes.WorkSetJobOperation(op) : null,
                InfoFile = NonEmpty(Str(r, "InfoFileName")),
                Minutes = job.Minutes ?? Minutes(Int(r, "WorkSetTime")),
            };
        }
        return new JobRow(Int(r, "ProcessID") ?? 0, Int(r, "JobID") ?? 0, type, job, program, call);
    }

    /// <summary>A machining job's operator call as the cell applies it: permanent stops every cycle; one-shot stops once
    /// per fixture (again only after the fixture has run another process), or, when the cell is set so, only for the
    /// next pallet, after which the cell clears the job's call.</summary>
    internal static string OperatorCallWords(int? call, RoutingSettings settings) => call switch
    {
        1 => "permanent (every cycle)",
        2 when settings.Known && settings.OneShotType == 1 => "one-shot (the next pallet only: then the cell clears it)",
        2 when settings.Known => "one-shot (once per fixture, again after the fixture has run another process)",
        _ => Codes.JobOperatorCall(call),
    };

    /// <summary>Planned seconds as minutes with one decimal; 0 or none is null.</summary>
    private static double? Minutes(int? seconds) => seconds is > 0 ? Math.Round(seconds.Value / 60.0, 1) : null;

    /// <summary>Where a fixture is: "pallet 12 fixture 1" (with its name and "disabled" when it has them), or
    /// "fixture id N (not on a pallet)".</summary>
    internal static string FixturePlace(int? pallet, int? fixture, string name, int fixtureId, bool enabled)
    {
        var place = pallet is null ? $"fixture id {fixtureId}" : fixture is null ? $"pallet {pallet}" : $"pallet {pallet} fixture {fixture}";
        var notes = new List<string>();
        if (pallet is null) notes.Add("not on a pallet");
        if (name.Length > 0) notes.Add(name);
        if (!enabled) notes.Add("disabled");
        return notes.Count > 0 ? $"{place} ({string.Join(", ", notes)})" : place;
    }

    // --- tools a machining program needs ---

    /// <summary>The tool types (first cutter rows) of every main program this part's machining jobs use (@partId),
    /// with the FTN's comment and whether the tool type is still registered, cap + 1. Null when the cell has no
    /// MainProgramTools (or it lacks the program/FTN columns).</summary>
    internal static string? ProgramToolsSql(SchemaInfo schema)
    {
        const string t = "MainProgramTools";
        if (!schema.HasTable(t) || !schema.Has(t, "NCProgramFileID") || !schema.Has(t, "FTN")) return null;
        var ftnTable = schema.Has("FunctionalToolData", "FTN");
        var name = ftnTable && schema.Has("FunctionalToolData", "FTNComment")
            ? "(SELECT TOP 1 f.FTNComment FROM FunctionalToolData f WHERE f.FTN = m.FTN ORDER BY f.FTNID) AS FTNComment"
            : $"CAST(NULL AS {Text}) AS FTNComment";
        var known = ftnTable
            ? "CASE WHEN EXISTS (SELECT 1 FROM FunctionalToolData f WHERE f.FTN = m.FTN) THEN 1 ELSE 0 END AS FtnKnown"
            : "CAST(NULL AS int) AS FtnKnown";
        var cols = string.Join(", ",
            "m.NCProgramFileID", "m.FTN",
            Opt(schema, t, "m", "ProgramToolNumber"),
            Opt(schema, t, "m", "ToolCount"),
            Opt(schema, t, "m", "CuttingTime"),
            Opt(schema, t, "m", "CuttingDistance"),
            Opt(schema, t, "m", "CuttingCount"),
            Opt(schema, t, "m", "ToolLifeType"),
            name, known);
        var cutter = schema.Has(t, "CutterNo") ? "m.CutterNo = 1 AND " : "";
        var order = schema.Has(t, "ProgramToolNumber") ? "m.NCProgramFileID, m.ProgramToolNumber, m.FTN" : "m.NCProgramFileID, m.FTN";
        return $"""
            SELECT TOP (@take) {cols}
            FROM MainProgramTools m
            WHERE {cutter}m.NCProgramFileID IN (
                SELECT mj.NCProgramFileID FROM MachineJobs mj JOIN Jobs j ON j.JobID = mj.JobID
                JOIN Processes pr ON pr.ProcessID = j.ProcessID WHERE pr.PartID = @partId)
            ORDER BY {order};
            """;
    }

    /// <summary>How much life one run of the program takes from the tool, in its life type's unit; null when the
    /// program's tool isn't life-checked (type -1) or uses nothing.</summary>
    internal static string? CuttingUse(int? lifeType, int? time, int? distance, int? count, string lengthUnit) => lifeType switch
    {
        0 or 3 when time is > 0 => $"{Math.Round(time.Value / 60.0, 1).ToString("0.#", CultureInfo.InvariantCulture)} min",
        1 when distance is > 0 => $"{distance} {(lengthUnit == "inch" ? "inch" : "m")}",
        2 when count is > 0 => $"{count} count",
        _ => null,
    };

    /// <summary>Per feasible machine, how many usable tools of <paramref name="ftn"/> sit there, and the machines with
    /// fewer than <paramref name="needed"/>. Both null when the counts aren't known or no machine is feasible (never
    /// "missing" on a guess).</summary>
    internal static (Dictionary<string, int>? Usable, List<string>? MissingOn) Availability(
        int ftn, int needed, IReadOnlyList<(int Id, string Name)> machines,
        IReadOnlyDictionary<(int DeviceId, int Ftn), int>? counts)
    {
        if (counts is null || machines.Count == 0) return (null, null);
        var usable = new Dictionary<string, int>();
        foreach (var (id, name) in machines) usable.TryAdd(name, counts.GetValueOrDefault((id, ftn)));
        var missing = usable.Where(u => u.Value < needed).Select(u => u.Key).ToList();
        return (usable, missing.Count > 0 ? missing : null);
    }

    /// <summary>What the tool tables say about one tool type on one device: usable tools (status ready or life warning
    /// as get_tools derives it), how many of those are flagged prohibited or absent, and the stored statuses the
    /// console's tool set status counts (null when this cell keeps no stored status).</summary>
    internal sealed record ToolState(int Usable, int Flagged, int? Unknown, int? Warning, int? OffsetError, int? Alarm);

    /// <summary>Per device and FTN (@ftn0 … @ftn{n-1}), the <see cref="ToolState"/> counts. Null when this cell lacks a
    /// column the usable count needs.</summary>
    internal static string? ToolStatesSql(SchemaInfo schema, int ftnCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ftnCount, 1);
        if (UsableToolsSql(schema, ftnCount) is null) return null; // the same columns as the plain usable count
        var stored = schema.Has(Itd, "Status");
        var flags = new List<string>();
        if (schema.Has(Itd, "ProhibitTool")) flags.Add("i.ProhibitTool = 1");
        if (schema.Has(Itd, "AbsentTool")) flags.Add("i.AbsentTool = 1");
        var flagged = flags.Count > 0 ? $"CASE WHEN {string.Join(" OR ", flags)} THEN 1 ELSE 0 END" : "0";
        string Stored(int status, string name) =>
            stored ? $"SUM(CASE WHEN x.Stored = {status} THEN 1 ELSE 0 END) AS {name}" : $"CAST(NULL AS int) AS {name}";
        var names = string.Join(", ", Enumerable.Range(0, ftnCount).Select(n => $"@ftn{n}"));
        return $"""
            SELECT x.DeviceID, x.FTN,
                SUM(CASE WHEN x.Status IN (1, 2) THEN 1 ELSE 0 END) AS Usable,
                SUM(CASE WHEN x.Status IN (1, 2) AND x.Flagged = 1 THEN 1 ELSE 0 END) AS Flagged,
                {Stored(0, "StoredUnknown")}, {Stored(2, "StoredWarning")}, {Stored(3, "StoredOffsetError")}, {Stored(4, "StoredAlarm")}
            FROM (
                SELECT l.CurrentDeviceID AS DeviceID, f.FTN, {DerivedStatusSql(schema)} AS Status,
                    {(stored ? "i.Status" : "CAST(NULL AS int)")} AS Stored, {flagged} AS Flagged
                FROM {ToolFromSql(schema)}
                WHERE f.FTN IN ({names}) AND l.CurrentDeviceID IS NOT NULL) x
            GROUP BY x.DeviceID, x.FTN;
            """;
    }

    /// <summary>Usable tools (ITN, FTN, device) of the FTNs @ftn0 … @ftn{n-1}, up to 50 per FTN, for "elsewhere".
    /// Null when the cell lacks the ITN column.</summary>
    internal static string? UsableToolListSql(SchemaInfo schema, int ftnCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ftnCount, 1);
        if (UsableToolsSql(schema, ftnCount) is null || !schema.Has(Itd, "ITN")) return null;
        var names = string.Join(", ", Enumerable.Range(0, ftnCount).Select(n => $"@ftn{n}"));
        return $"""
            SELECT y.ITN, y.FTN, y.DeviceID FROM (
                SELECT i.ITN, f.FTN, l.CurrentDeviceID AS DeviceID, ROW_NUMBER() OVER (PARTITION BY f.FTN ORDER BY i.ITN, i.ITNID) AS Row
                FROM {ToolFromSql(schema)}
                WHERE f.FTN IN ({names}) AND l.CurrentDeviceID IS NOT NULL AND {DerivedStatusSql(schema)} IN (1, 2)) y
            WHERE y.Row <= 50
            ORDER BY y.FTN, y.ITN;
            """;
    }

    /// <summary>The console's tool set status of a program on one machine: unknown when any tool of a needed type
    /// there has the stored status unknown; else lacking (from missingOn), life warning, offset error and alarm when
    /// any tool of a needed type there has that stored status (spares or not); else ready. Null when the stored
    /// statuses aren't known.</summary>
    internal static string? ToolSetWords(IEnumerable<ToolState?> states, bool lacking)
    {
        var list = states.ToList();
        if (list.Any(t => t is not null && t.Unknown is null)) return null;
        var present = list.OfType<ToolState>().ToList();
        if (present.Any(t => t.Unknown > 0)) return Codes.ToolSetStatus(0);
        var flags = 0;
        if (lacking) flags |= 0x2;
        if (present.Any(t => t.Warning > 0)) flags |= 0x4;
        if (present.Any(t => t.Alarm > 0)) flags |= 0x8;
        if (present.Any(t => t.OffsetError > 0)) flags |= 0x10;
        return Codes.ToolSetStatus(flags == 0 ? 0x1 : flags);
    }

    /// <summary>For a tool missing on <paramref name="missingOn"/>: up to three usable tools of the type that aren't
    /// on those machines ("ITN 1234 at CRIB1 (tool crib)") and how many more there are.</summary>
    internal static (List<string>? Shown, int? More) Elsewhere(
        IReadOnlyList<(int Itn, string Place, int DeviceId)> usable, IReadOnlySet<int> missingOn, int total)
    {
        var away = usable.Where(u => !missingOn.Contains(u.DeviceId)).ToList();
        if (total <= 0 && away.Count == 0) return (null, null);
        var shown = away.Take(3).Select(u => $"ITN {u.Itn} at {u.Place}").ToList();
        var more = Math.Max(total, away.Count) - shown.Count;
        return (shown.Count > 0 ? shown : null, more > 0 ? more : null);
    }

    private sealed record ToolsRead(PartRoute Route, bool Capped, Dictionary<(int Process, int Job), IReadOnlyList<string>> Lacking);

    /// <summary>The route with each machining job's tools and tool set per machine; Capped when the part's programs
    /// list more tool rows than the row limit (the rest are left out). Lacking: per job (process index, job index) the
    /// machines it lacks tools on.</summary>
    private async Task<ToolsRead> AddToolsAsync(
        Session s, int partId, PartRoute route, List<List<int?>> programs, RoutingSettings settings, List<string> notAvailable,
        CancellationToken ct)
    {
        var lackingByJob = new Dictionary<(int, int), IReadOnlyList<string>>();
        if (ProgramToolsSql(s.Schema) is not { } sql)
        {
            notAvailable.Add("tools each program needs");
            return new ToolsRead(route, false, lackingByJob);
        }
        var toolSettings = await ToolSettingsAsync(s, ct);

        var rows = new List<(int Program, int Ftn, int? Ptn, int Needed, string? Cutting, string Name, int? LifeType, bool? Known)>();
        using (var cmd = Command(s.Connection, sql))
        {
            AddTake(cmd);
            cmd.Parameters.Add("@partId", System.Data.SqlDbType.Int).Value = partId;
            using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                rows.Add((Int(r, "NCProgramFileID") ?? 0, Int(r, "FTN") ?? 0, Int(r, "ProgramToolNumber"),
                    Int(r, "ToolCount") ?? 0, // the cell's own rule: missing when usable < ToolCount
                    CuttingUse(Int(r, "ToolLifeType"), Int(r, "CuttingTime"), Int(r, "CuttingDistance"), Int(r, "CuttingCount"),
                        toolSettings.LengthUnit),
                    Str(r, "FTNComment"), Int(r, "ToolLifeType"), Int(r, "FtnKnown") is { } k ? k == 1 : null));
            }
        }
        var toolsCapped = rows.Count > _cap;
        if (toolsCapped) rows.RemoveRange(_cap, rows.Count - _cap);

        // Tool counts need the tool tables; on a cell where they aren't readable (a remote tool crib that is down)
        // they are "not available", never zero.
        Dictionary<(int DeviceId, int Ftn), ToolState>? states = null;
        if (rows.Count > 0)
        {
            if (s.Schema.HasTable(Itd) && s.Schema.HasTable(Itc) && s.Schema.HasTable(Ftd) && s.Schema.HasTable(Loc))
                states = await ToolStatesAsync(s, rows.Select(t => t.Ftn).ToList(), ct);
            if (states is null) notAvailable.Add("usable tools per machine");
        }
        var counts = states?.ToDictionary(p => p.Key, p => p.Value.Usable);

        // With PTN download before machining the cell compares the sum over every PTN of a type with its ready tools.
        var perType = rows.GroupBy(t => (t.Program, t.Ftn)).ToDictionary(g => g.Key, g => g.Sum(t => t.Needed));

        var machines = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in s.Devices.Where(d => d.Type == MachineType)) machines.TryAdd(d.Name, d.Id);
        var lackingFtns = new HashSet<int>();
        var processes = route.Processes.Select((p, pi) => p with
        {
            Jobs = p.Jobs.Select((job, ji) =>
            {
                if (programs[pi][ji] is not { } programId) return job;
                var feasible = (job.FeasibleDevices ?? [])
                    .Where(machines.ContainsKey).Select(n => (machines[n], n)).ToList();
                var tools = rows.Where(t => t.Program == programId).Select(t =>
                {
                    var sum = perType[(t.Program, t.Ftn)];
                    var check = settings.SumsPerType ? sum : t.Needed;
                    var (usable, missing) = Availability(t.Ftn, check, feasible, counts);
                    if (missing is not null) lackingFtns.Add(t.Ftn);
                    var flagged = states is null ? null : feasible
                        .Select(m => (m.n, Count: states.GetValueOrDefault((m.Item1, t.Ftn))?.Flagged ?? 0))
                        .Where(m => m.Count > 0).ToDictionary(m => m.n, m => m.Count);
                    return new ProgramTool(t.Ftn, t.Needed)
                    {
                        Ptn = t.Ptn is { } ptn && ptn != t.Ftn ? ptn : null,
                        Name = NonEmpty(t.Name), Cutting = t.Cutting, Usable = usable, MissingOn = missing,
                        NeededForType = settings.SumsPerType && sum != t.Needed ? sum : null,
                        UsableButFlagged = flagged is { Count: > 0 } ? flagged : null,
                        Note = t.Known == false ? "tool type not registered on this cell (its FTN was deleted)" : null,
                    };
                }).ToList();
                if (tools.Count == 0) return job;

                var missingOn = tools.SelectMany(t => t.MissingOn ?? []).Distinct().ToList();
                if (missingOn.Count > 0) lackingByJob[(pi, ji)] = missingOn;
                Dictionary<string, string>? toolSet = null;
                if (states is not null && feasible.Count > 0)
                {
                    var ftns = tools.Select(t => t.Ftn).Distinct().ToList();
                    var words = feasible.Select(m => (m.n, Words: ToolSetWords(ftns.Select(f => states.GetValueOrDefault((m.Item1, f))),
                        missingOn.Contains(m.n)))).ToList();
                    if (words.All(w => w.Words is not null)) toolSet = words.ToDictionary(w => w.n, w => w.Words!);
                }
                var study = job.ToolStudy;
                if (study is { Studied: null } && tools.All(t => t.Cutting is null)) study = study with { Note = "not studied" };
                return job with { Tools = tools, ToolSet = toolSet, ToolStudy = study };
            }).ToList(),
        }).ToList();
        route = route with { Processes = processes };

        // Elsewhere: usable tools of each lacking type that aren't on the machines it is missing on.
        if (lackingFtns.Count > 0 && states is not null && await UsableToolListAsync(s, lackingFtns.ToList(), ct) is { } list)
        {
            route = route with
            {
                Processes = route.Processes.Select(p => p with
                {
                    Jobs = p.Jobs.Select(j => j.Tools is null ? j : j with
                    {
                        Tools = j.Tools.Select(t =>
                        {
                            if (t.MissingOn is null) return t;
                            var missingIds = t.MissingOn.Where(machines.ContainsKey).Select(n => machines[n]).ToHashSet();
                            var total = states.Where(st => st.Key.Ftn == t.Ftn && !missingIds.Contains(st.Key.DeviceId)).Sum(st => st.Value.Usable);
                            var (shown, more) = Elsewhere(list.GetValueOrDefault(t.Ftn) ?? [], missingIds, total);
                            return t with { Elsewhere = shown, ElsewhereMore = more };
                        }).ToList(),
                    }).ToList(),
                }).ToList(),
            };
        }
        return new ToolsRead(route, toolsCapped, lackingByJob);
    }

    private static async Task<Dictionary<(int DeviceId, int Ftn), ToolState>?> ToolStatesAsync(
        Session s, IReadOnlyCollection<int> ftns, CancellationToken ct)
    {
        var states = new Dictionary<(int, int), ToolState>();
        foreach (var chunk in ftns.Distinct().Chunk(500))
        {
            if (ToolStatesSql(s.Schema, chunk.Length) is not { } sql) return null;
            using var cmd = Command(s.Connection, sql);
            for (var n = 0; n < chunk.Length; n++) cmd.Parameters.Add($"@ftn{n}", System.Data.SqlDbType.Int).Value = chunk[n];
            using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                states[(Int(r, "DeviceID") ?? 0, Int(r, "FTN") ?? 0)] = new ToolState(Int(r, "Usable") ?? 0, Int(r, "Flagged") ?? 0,
                    Int(r, "StoredUnknown"), Int(r, "StoredWarning"), Int(r, "StoredOffsetError"), Int(r, "StoredAlarm"));
            }
        }
        return states;
    }

    private static async Task<Dictionary<int, List<(int Itn, string Place, int DeviceId)>>?> UsableToolListAsync(
        Session s, IReadOnlyCollection<int> ftns, CancellationToken ct)
    {
        var tools = new Dictionary<int, List<(int, string, int)>>();
        foreach (var chunk in ftns.Distinct().Chunk(500))
        {
            if (UsableToolListSql(s.Schema, chunk.Length) is not { } sql) return null;
            using var cmd = Command(s.Connection, sql);
            for (var n = 0; n < chunk.Length; n++) cmd.Parameters.Add($"@ftn{n}", System.Data.SqlDbType.Int).Value = chunk[n];
            using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var ftn = Int(r, "FTN") ?? 0;
                var deviceId = Int(r, "DeviceID") ?? 0;
                var device = s.Devices.FirstOrDefault(d => d.Id == deviceId);
                if (!tools.TryGetValue(ftn, out var list)) tools[ftn] = list = [];
                list.Add((Int(r, "ITN") ?? 0, Place(device?.Name ?? $"device {deviceId}", device?.Type, null), deviceId));
            }
        }
        return tools;
    }

    // --- blockers: the console's Process Diagnosis ---

    /// <summary>The part's active orders (cap + 1) and how many finished ones it has. Null when Orders can't be read by
    /// part.</summary>
    internal static string? PartOrdersSql(SchemaInfo schema)
    {
        if (!schema.Has("Orders", "PartID") || !schema.Has("Orders", "OrderName")) return null;
        var status = schema.Has("Orders", "Status");
        var active = status ? $"ISNULL(o.Status, 0) NOT IN ({RoutingFinishedOrders})" : "1 = 1";
        var due = schema.Has("Orders", "DueDate") ? "o.DueDate, " : "";
        return $"""
            SELECT TOP (@take) o.OrderName, {Opt(schema, "Orders", "o", "Status")}, {Opt(schema, "Orders", "o", "MachiningSuspended", type: "bit")}
            FROM Orders o WHERE o.PartID = @partId AND {active} ORDER BY {due}o.OrderName;
            SELECT COUNT(*) AS Finished FROM Orders o WHERE o.PartID = @partId AND NOT ({active});
            """;
    }

    private async Task<RoutingBlockers> BlockersAsync(Session s, int partId, RouteRead read,
        Dictionary<(int Process, int Job), IReadOnlyList<string>>? lacking, bool includeTools, RoutingSettings settings,
        DiagSettings diag, List<string> notAvailable, CancellationToken ct)
    {
        List<BlockerOrder>? orders = null;
        var finished = 0;
        if (PartOrdersSql(s.Schema) is { } ordersSql)
        {
            orders = [];
            using var cmd = Command(s.Connection, ordersSql);
            AddTake(cmd);
            cmd.Parameters.Add("@partId", System.Data.SqlDbType.Int).Value = partId;
            using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                orders.Add(new BlockerOrder(Str(r, "OrderName"), Int(r, "Status"), Bool(r, "MachiningSuspended") == true));
            if (await r.NextResultAsync(ct) && await r.ReadAsync(ct)) finished = Int(r, "Finished") ?? 0;
        }
        else notAvailable.Add("orders of the part");

        var devices = await DeviceStatesAsync(s, ct);
        if (devices is null) notAvailable.Add("device states for the blockers");

        // The lacking machines per job, by the process and job numbers the blockers use.
        Dictionary<(int Process, int Job), IReadOnlyList<int>>? lackingIds = null;
        if (includeTools && lacking is not null)
        {
            var ids = s.Devices.Where(d => d.Type == MachineType).GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);
            lackingIds = lacking.ToDictionary(
                l => (read.Processes[l.Key.Process].Number, read.Processes[l.Key.Process].Jobs[l.Key.Job].Number),
                l => (IReadOnlyList<int>)l.Value.Where(ids.ContainsKey).Select(n => ids[n]).ToList());
        }
        var (reasons, states) = Blockers(read.Processes, orders, devices, lackingIds, settings, diag, read.FixturesCapped);
        var shownOrders = orders?.Take(OrdersListed).Select(o => OrderWords(o, settings)).ToList();
        var notListed = finished + Math.Max(0, (orders?.Count ?? 0) - OrdersListed);
        return new RoutingBlockers(reasons, BlockersNotChecked(read.Processes, includeTools))
        {
            Orders = shownOrders is { Count: > 0 } ? shownOrders : null,
            OtherOrders = notListed > 0 ? notListed : null,
            Devices = states is { Count: > 0 } ? states : null,
        };
    }

    /// <summary>Machine and station states (connection, emergency stop, system mode, open, skip, transport cancel) with
    /// their open alarms, as the pallet diagnosis reads them; null when neither status table can be read.</summary>
    private static async Task<Dictionary<int, DiagDevice>?> DeviceStatesAsync(Session s, CancellationToken ct)
    {
        var schema = s.Schema;
        var tables = new[] { "MachineStatus", "WorkSetStatus" }.Where(schema.HasTable).ToList();
        if (tables.Count == 0) return null;
        var devices = new Dictionary<int, DiagDevice>();
        foreach (var table in tables)
        {
            var type = table == "MachineStatus" ? MachineType : StationType;
            foreach (var d in s.Devices.Where(d => d.Type == type)) devices[d.Id] = new DiagDevice(d.Name, d.Type);
            using var cmd = Command(s.Connection, StatusSql(schema, table, table == "MachineStatus" ? MachineSpots : StationSpots));
            using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var id = Int(r, "DeviceID") ?? 0;
                if (!devices.TryGetValue(id, out var d)) continue;
                devices[id] = d with
                {
                    Connected = Bool(r, "Connected"), Emergency = Bool(r, "Emergency"), SystemMode = Bool(r, "SystemMode"),
                    Open = Bool(r, "DeviceOpen"), Skip = Bool(r, "DeviceSkip"), TransProhibit = Bool(r, "DeviceTransProhibit"),
                };
            }
        }
        if (schema.HasTable("PMCAlarms") && schema.HasTable("CNCAlarms"))
        {
            using var cmd = Command(s.Connection, DiagnosisAlarmSql(schema));
            using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var id = Int(r, "DeviceID") ?? 0;
                if (devices.TryGetValue(id, out var d))
                    devices[id] = d with { PmcAlarms = Int(r, "PmcAlarms") ?? 0, AllAlarms = Int(r, "AllAlarms") ?? 0 };
            }
        }
        return devices;
    }

    private static string OrderWords(BlockerOrder o, RoutingSettings settings)
    {
        var words = Codes.OrderStatus(o.Status) is { Length: > 0 } status ? $"{o.Name}: {status}" : o.Name;
        return MachiningSuspendShown(o, settings) ? $"{words}, machining suspended" : words;
    }

    // An order's machining suspend counts only with the cell's OrderMachiningSuspend on; settings this login can't read
    // can't rule it out.
    private static bool MachiningSuspendShown(BlockerOrder o, RoutingSettings settings) =>
        o.MachiningSuspended && (!settings.Known || settings.OrderMachiningSuspend);

    /// <summary>The blockers of the part's processes in the console's terms, most basic first, and each assigned
    /// device's own state. <paramref name="orders"/>: the active orders (null: unknown); <paramref name="devices"/>:
    /// device states (null: unknown, then no device verdicts); <paramref name="lacking"/>: per (process, job) number
    /// the machines lacking the program's tools (null: not checked).</summary>
    internal static (List<string> Reasons, Dictionary<string, string>? Devices) Blockers(
        IReadOnlyList<BlockerProcess> processes, IReadOnlyList<BlockerOrder>? orders, IReadOnlyDictionary<int, DiagDevice>? devices,
        IReadOnlyDictionary<(int Process, int Job), IReadOnlyList<int>>? lacking, RoutingSettings settings, DiagSettings diag,
        bool placesCapped)
    {
        var reasons = new List<string>();
        if (processes.Count == 0) reasons.Add("the part has no processes");

        if (orders is not null)
        {
            if (orders.Count == 0) reasons.Add("no active order for this part: the cell machines nothing without one");
            else if (!orders.Any(o => o.Status is 2 or 3 && !MachiningSuspendShown(o, settings)))
                reasons.Add("no active order can run: " + string.Join("; ", orders.Take(PlacesNamed).Select(o => OrderWords(o, settings)))
                            + (orders.Count > PlacesNamed ? $"; {orders.Count - PlacesNamed} more" : ""));
        }

        var states = devices is null ? null : new Dictionary<string, string>();
        var blank = new DiagFixture(null);
        foreach (var p in processes)
        {
            var name = $"process {p.Number}";
            if (p.Status is 2 or 3) reasons.Add($"{name} is {Codes.ProcessStatus(p.Status)}");

            // Fixtures: assigned at all, on a pallet, and a place that can take it now.
            if (p.Assigned == 0) reasons.Add($"{name}: not assigned to any fixture");
            else if (p.Assigned > 0 && p.Places.Count == 0) reasons.Add($"{name}: no pallet carries a fixture assigned to it");
            else if (p.Places.Count > 0 && p.Places.All(pl => pl.UseProcessId is not null))
            {
                var own = p.Places.Where(pl => pl.UseProcessId == p.Id).ToList();
                var heldOwn = own.Where(pl => pl.Holds.Count > 0).ToList();
                if (heldOwn.Count > 0)
                    reasons.Add($"{name}: on {Named(heldOwn.Select(pl => $"{pl.Place} ({string.Join(", ", pl.Holds)})").ToList())}");
                var canTake = p.Places.Any(pl => pl.Enabled && pl.Holds.Count == 0 && (pl.UseProcessId == 0 || pl.UseProcessId == p.Id));
                if (own.Count == 0 && !canTake)
                {
                    var other = p.Places.Count(pl => pl.UseProcessId is > 0 && pl.UseProcessId != p.Id);
                    var held = p.Places.Count(pl => pl.Holds.Count > 0);
                    var disabled = p.Places.Count(pl => !pl.Enabled);
                    var parts = new List<string>();
                    if (other > 0) parts.Add($"{other} in use by other processes");
                    if (held > 0) parts.Add($"{held} held (suspended, work-set hold, damaged or pre-machining suspended)");
                    if (disabled > 0) parts.Add($"{disabled} disabled for it");
                    reasons.Add($"{name}: no fixture place can take it now{(placesCapped ? " (of the places listed)" : "")}: {string.Join(", ", parts)}");
                }
            }
            if (p.MaxFixtures is { } max && p.InUse >= max)
                reasons.Add($"{name}: at its limit of {max} fixture{(max == 1 ? "" : "s")} at once ({p.InUse} in use)");

            // Jobs: assigned devices, each judged by the console's device checks.
            foreach (var job in p.Jobs.Where(j => j.Type is MachiningJob or WorkSettingJob))
            {
                var label = $"{name} job {job.Number} ({Codes.JobType(job.Type)})";
                if (job.Devices.Count == 0)
                {
                    reasons.Add($"{label}: not assigned to any {(job.Type == MachiningJob ? "machine" : "work-set station")}");
                    continue;
                }
                if (devices is null) continue;
                var fixture = new DiagFixture(null) { ProcessId = p.Id, OffDutyProcess = p.OffDuty, JobOperatorCall = job.OperatorCall };
                var lackingOn = lacking?.GetValueOrDefault((p.Number, job.Number)) ?? [];
                var verdicts = new List<(DiagDevice Device, int Code)>();
                foreach (var id in job.Devices)
                {
                    if (!devices.TryGetValue(id, out var d)) continue;
                    var own = job.Type == MachiningJob ? MachineVerdict(blank, d, diag) : StationVerdict(blank, d, diag);
                    states![d.Name] = DeviceVerdictWords(d, own);
                    var code = job.Type == MachiningJob
                        ? settings.ToolsHoldWork != false && lackingOn.Contains(id) ? LackingTools : MachineVerdict(fixture, d, diag)
                        : StationVerdict(fixture, d, diag);
                    verdicts.Add((d, code));
                }
                if (verdicts.Count == 0) continue;
                var combined = Combine(verdicts.Select(v => v.Code).ToList(), job.Type == MachiningJob ? DiagMcNoFeasible : DiagWssNoFeasible);
                if (combined == DiagNormal) continue;
                var device = job.Type == MachiningJob ? "machine" : "station";
                reasons.Add($"{label}: no assigned {device} can take it: " +
                            string.Join("; ", verdicts.Select(v => $"{v.Device.Name} {DeviceVerdictWords(v.Device, v.Code)}")));
            }
        }
        if (reasons.Count == 0) reasons.Add(NothingBlocks);
        return (reasons, states);
    }

    private static string Named(IReadOnlyList<string> places) =>
        string.Join("; ", places.Take(PlacesNamed)) + (places.Count > PlacesNamed ? $"; {places.Count - PlacesNamed} more" : "");

    /// <summary>What the console also checks for a process but the cell keeps only in memory (or this tool doesn't
    /// follow); only the ones that apply to the part's jobs.</summary>
    internal static List<string> BlockersNotChecked(IReadOnlyList<BlockerProcess> processes, bool includeTools)
    {
        var jobs = processes.SelectMany(p => p.Jobs).ToList();
        var list = new List<string>
        {
            "whether each device is system ready beyond its connection, emergency stop and system mode (kept in memory)",
            "which vehicle can reach each device (dedicated vehicles)",
            "transport order between parts competing for the same fixtures",
        };
        if (jobs.Any(j => j.Type == MachiningJob))
        {
            list.Add(includeTools
                ? "the tool set the cell keeps in memory for each program and machine (toolSet is worked out from the tool tables; a FOCAS-connected machine always reads ready on the console)"
                : "tools (includeTools=false)");
            list.Add("CSG files, measuring data and extra tool pallets a job waits for");
            list.Add("one-shot operator calls already made on each fixture (the process's own fixtures: get_pallet_diagnosis)");
        }
        if (jobs.Any(j => j.Type == WorkSettingJob)) list.Add("material groups and material pallets at the work-set station");
        return list;
    }
}
