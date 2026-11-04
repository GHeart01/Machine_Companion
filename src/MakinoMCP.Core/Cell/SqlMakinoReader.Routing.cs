using System.Globalization;
using System.Text;

namespace MakinoMCP.Core.Cell;

// Routing: Parts → Processes (the route's steps) → Operations (parts per clamp) and Jobs (work setting, machining, …)
// with their MachineJobs / WorkSetJobs detail, the devices each job may run on (JobFeasibleDevices) and the fixtures
// that can hold each process (FixtureProcesses → PalletFixtures). With tools: the tool types each machining program
// needs (MainProgramTools) against the usable tools on each feasible machine.
public sealed partial class SqlMakinoReader
{
    public const string NoPartGiven = "Give a part name (or part of one) to look up.";

    public const string SeveralPartsNote =
        "Several parts match, so no routing is shown: ask again with one candidate's exact name (and revision when names repeat).";

    public const string ToolCheckNote =
        "approximate: main program only, subprograms not followed; usable = tools of that type on the machine in status " +
        "ready or life warning, remaining life not checked";

    /// <summary>A part the search found: name, revision ("" when blank) and how many processes it has.</summary>
    internal sealed record PartMatch(int Id, string Name, string Revision, int ProcessCount);

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

        var (route, programs) = await ReadRouteAsync(s, chosen, notAvailable, ct);
        if (includeTools)
        {
            var (withTools, toolsCapped) = await AddToolsAsync(s, chosen.Id, route, programs, notAvailable, ct);
            (route, capped) = (withTools, capped || toolsCapped);
        }
        return new PartRouting(s.Now, wanted, capped)
        {
            Part = route,
            OtherMatches = others.Count > 0 ? others.Select(o => PartLabel(o.Name, o.Revision)).ToList() : null,
            ToolCheck = includeTools ? ToolCheckNote : null,
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

    /// <summary>Six result sets for one part (@partId): the part, its processes, their operations, their jobs (with the
    /// machining and work-set detail), the jobs' feasible devices, and the fixtures that can hold each process (an
    /// empty set when the cell has no FixtureProcesses).</summary>
    internal static string RoutingSql(SchemaInfo schema)
    {
        var sql = new StringBuilder();
        sql.AppendLine($"SELECT {Opt(schema, "Parts", "p", "Comment", type: Text)} FROM Parts p WHERE p.PartID = @partId;");

        var processOrder = schema.Has("Processes", "ProcessNumber") ? "pr.ProcessNumber, pr.ProcessID" : "pr.ProcessID";
        sql.AppendLine($"""
            SELECT pr.ProcessID, {string.Join(", ",
                Opt(schema, "Processes", "pr", "ProcessNumber"),
                Opt(schema, "Processes", "pr", "ProcessName", type: Text),
                Opt(schema, "Processes", "pr", "Status"),
                Opt(schema, "Processes", "pr", "MaxFixtures"))}
            FROM Processes pr WHERE pr.PartID = @partId ORDER BY {processOrder};
            """);

        var opOrder = schema.Has("Operations", "OperationSequence") ? "o.ProcessID, o.OperationSequence" : "o.ProcessID, o.OperationID";
        sql.AppendLine($"""
            SELECT o.ProcessID, {string.Join(", ",
                Opt(schema, "Operations", "o", "OperationSequence"),
                Opt(schema, "Operations", "o", "ClampQuantity"),
                Opt(schema, "Operations", "o", "OperationName", type: Text))}
            FROM Operations o JOIN Processes pr ON pr.ProcessID = o.ProcessID
            WHERE pr.PartID = @partId ORDER BY {opOrder};
            """);

        var info = schema.HasTable("InformationFiles") && schema.Has("WorkSetJobs", "InformationFileID");
        var jobCols = string.Join(", ",
            "j.ProcessID", "j.JobID",
            Opt(schema, "Jobs", "j", "JobNumber"),
            Opt(schema, "Jobs", "j", "JobType"),
            Opt(schema, "MachineJobs", "mj", "JobID", "MachineJobID"),
            Opt(schema, "MachineJobs", "mj", "NCProgramFileID"),
            Opt(schema, "NCProgramFiles", "n", "Name", "ProgramName", Text),
            Opt(schema, "MachineJobs", "mj", "MachiningTime"),
            Opt(schema, "MachineJobs", "mj", "OperatorCall", "JobOperatorCall"),
            Opt(schema, "WorkSetJobs", "wj", "JobID", "WorkSetJobID"),
            Opt(schema, "WorkSetJobs", "wj", "OperationType", "WorkSetOperation"),
            info ? Opt(schema, "InformationFiles", "i", "Name", "InfoFileName", Text) : $"CAST(NULL AS {Text}) AS InfoFileName",
            Opt(schema, "WorkSetJobs", "wj", "WorkSetTime"));
        var programJoin = schema.Has("MachineJobs", "NCProgramFileID")
            ? " LEFT JOIN NCProgramFiles n ON n.NCProgramFileID = mj.NCProgramFileID"
            : " LEFT JOIN NCProgramFiles n ON 1 = 0";
        var infoJoin = info ? " LEFT JOIN InformationFiles i ON i.InformationFileID = wj.InformationFileID" : "";
        var jobOrder = schema.Has("Jobs", "JobNumber") ? "j.ProcessID, j.JobNumber, j.JobID" : "j.ProcessID, j.JobID";
        sql.AppendLine($"""
            SELECT {jobCols}
            FROM Jobs j JOIN Processes pr ON pr.ProcessID = j.ProcessID
            LEFT JOIN MachineJobs mj ON mj.JobID = j.JobID{programJoin}
            LEFT JOIN WorkSetJobs wj ON wj.JobID = j.JobID{infoJoin}
            WHERE pr.PartID = @partId ORDER BY {jobOrder};
            """);

        sql.AppendLine("""
            SELECT f.JobID, f.FeasibleDeviceID
            FROM JobFeasibleDevices f JOIN Jobs j ON j.JobID = f.JobID JOIN Processes pr ON pr.ProcessID = j.ProcessID
            WHERE pr.PartID = @partId;
            """);

        if (schema.HasTable("FixtureProcesses"))
        {
            var named = schema.HasTable("Fixtures");
            var onPallets = schema.HasTable("PalletFixtures") && schema.Has("PalletFixtures", "FixtureID");
            var fixtureCols = string.Join(", ",
                "fp.ProcessID", "fp.FixtureID",
                Opt(schema, "FixtureProcesses", "fp", "FixturePriority"),
                Opt(schema, "FixtureProcesses", "fp", "Enabled", type: "bit"),
                named ? Opt(schema, "Fixtures", "fx", "FixtureName", type: Text) : $"CAST(NULL AS {Text}) AS FixtureName",
                onPallets && schema.Has("PalletFixtures", "PalletID")
                    ? PalletNumberSql(schema, "pf.PalletID", "PalletNumber") : "CAST(NULL AS int) AS PalletNumber",
                onPallets ? Opt(schema, "PalletFixtures", "pf", "FixtureNumber") : "CAST(NULL AS int) AS FixtureNumber");
            var joins = (named ? " LEFT JOIN Fixtures fx ON fx.FixtureID = fp.FixtureID" : "")
                        + (onPallets ? " LEFT JOIN PalletFixtures pf ON pf.FixtureID = fp.FixtureID" : "");
            sql.AppendLine($"""
                SELECT {fixtureCols}
                FROM FixtureProcesses fp JOIN Processes pr ON pr.ProcessID = fp.ProcessID{joins}
                WHERE pr.PartID = @partId ORDER BY fp.ProcessID, FixturePriority, PalletNumber, FixtureNumber, fp.FixtureID;
                """);
        }
        else
        {
            sql.AppendLine($"SELECT CAST(NULL AS int) AS ProcessID, CAST(NULL AS int) AS FixtureID, " +
                           "CAST(NULL AS int) AS FixturePriority, CAST(NULL AS bit) AS Enabled, " +
                           $"CAST(NULL AS {Text}) AS FixtureName, CAST(NULL AS int) AS PalletNumber, " +
                           "CAST(NULL AS int) AS FixtureNumber WHERE 1 = 0;");
        }
        return sql.ToString();
    }

    private sealed record JobRow(int ProcessId, int JobId, JobRoute Job, int? ProgramId);

    /// <summary>The part's route, and per process (in the same order) the program id of each of its jobs (null for jobs
    /// that aren't machining).</summary>
    private static async Task<(PartRoute Route, List<List<int?>> Programs)> ReadRouteAsync(
        Session s, PartMatch part, List<string> notAvailable, CancellationToken ct)
    {
        if (!s.Schema.HasTable("FixtureProcesses")) notAvailable.Add("fixtures for each process");
        if (!s.Schema.HasTable("InformationFiles")) notAvailable.Add("work instruction file names");

        using var cmd = Command(s.Connection, RoutingSql(s.Schema));
        cmd.Parameters.Add("@partId", System.Data.SqlDbType.Int).Value = part.Id;
        using var r = await cmd.ExecuteReaderAsync(ct);

        var comment = await r.ReadAsync(ct) ? Str(r, "Comment") : "";

        await r.NextResultAsync(ct);
        var processes = new List<(int Id, int Number, string Name, int? Status, int? MaxFixtures)>();
        while (await r.ReadAsync(ct))
            processes.Add((Int(r, "ProcessID") ?? 0, Int(r, "ProcessNumber") ?? 0, Str(r, "ProcessName"), Int(r, "Status"), Int(r, "MaxFixtures")));

        await r.NextResultAsync(ct);
        var operations = new List<(int ProcessId, OperationRoute Operation)>();
        while (await r.ReadAsync(ct))
        {
            operations.Add((Int(r, "ProcessID") ?? 0,
                new OperationRoute(Int(r, "OperationSequence") ?? 0, Int(r, "ClampQuantity") ?? 0) { Name = NonEmpty(Str(r, "OperationName")) }));
        }

        await r.NextResultAsync(ct);
        var jobs = new List<JobRow>();
        while (await r.ReadAsync(ct))
        {
            var job = new JobRoute(Int(r, "JobNumber") ?? 0, Codes.JobType(Int(r, "JobType")) is { Length: > 0 } type ? type : "unknown");
            if (Int(r, "MachineJobID") is not null)
            {
                job = job with
                {
                    Program = NonEmpty(Str(r, "ProgramName")),
                    Minutes = Minutes(Int(r, "MachiningTime")),
                    OperatorCall = Int(r, "JobOperatorCall") is > 0 and var call ? Codes.JobOperatorCall(call) : null,
                };
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
            var program = Int(r, "MachineJobID") is not null ? Int(r, "NCProgramFileID") : null;
            jobs.Add(new JobRow(Int(r, "ProcessID") ?? 0, Int(r, "JobID") ?? 0, job, program));
        }

        await r.NextResultAsync(ct);
        var feasible = new Dictionary<int, List<int>>();
        while (await r.ReadAsync(ct))
        {
            var jobId = Int(r, "JobID") ?? 0;
            if (!feasible.TryGetValue(jobId, out var list)) feasible[jobId] = list = [];
            if (Int(r, "FeasibleDeviceID") is { } device) list.Add(device);
        }

        await r.NextResultAsync(ct);
        var fixtures = new Dictionary<int, List<string>>();
        while (await r.ReadAsync(ct))
        {
            var processId = Int(r, "ProcessID") ?? 0;
            if (!fixtures.TryGetValue(processId, out var list)) fixtures[processId] = list = [];
            list.Add(FixturePlace(Int(r, "PalletNumber"), Int(r, "FixtureNumber"), Str(r, "FixtureName"), Int(r, "FixtureID") ?? 0,
                Bool(r, "Enabled") != false));
        }

        // Devices in the cell's own order (machines, then the rest as Devices lists them).
        int Rank(int id)
        {
            for (var i = 0; i < s.Devices.Count; i++) if (s.Devices[i].Id == id) return i;
            return int.MaxValue;
        }
        var routes = processes.Select(p => new ProcessRoute(
            p.Number,
            Codes.ProcessStatus(p.Status) is { Length: > 0 } status ? status : "unknown",
            operations.Where(o => o.ProcessId == p.Id).Select(o => o.Operation).ToList(),
            jobs.Where(j => j.ProcessId == p.Id).Select(j => j.Job with
            {
                FeasibleDevices = feasible.TryGetValue(j.JobId, out var ids) && ids.Count > 0
                    ? ids.Distinct().OrderBy(Rank).ThenBy(id => id).Select(s.NameOf).ToList()
                    : null,
            }).ToList())
        {
            Name = NonEmpty(p.Name),
            MaxFixtures = p.MaxFixtures is > 0 ? p.MaxFixtures : null,
            Fixtures = fixtures.TryGetValue(p.Id, out var places) && places.Count > 0 ? places : null,
        }).ToList();

        // The part's status: its most severe process status (the cell's own part list shows the highest).
        var worst = processes.Select(p => p.Status).Where(x => x is not null).DefaultIfEmpty(null).Max();
        var route = new PartRoute(part.Name, worst is null ? "unknown" : Codes.ProcessStatus(worst), routes)
        {
            Revision = NonEmpty(part.Revision), Comment = NonEmpty(comment),
        };
        var programs = processes.Select(p => jobs.Where(j => j.ProcessId == p.Id).Select(j => j.ProgramId).ToList()).ToList();
        return (route, programs);
    }

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
    /// with the FTN's comment, cap + 1. Null when the cell has no MainProgramTools (or it lacks the program/FTN
    /// columns).</summary>
    internal static string? ProgramToolsSql(SchemaInfo schema)
    {
        const string t = "MainProgramTools";
        if (!schema.HasTable(t) || !schema.Has(t, "NCProgramFileID") || !schema.Has(t, "FTN")) return null;
        var name = schema.Has("FunctionalToolData", "FTN") && schema.Has("FunctionalToolData", "FTNComment")
            ? "(SELECT TOP 1 f.FTNComment FROM FunctionalToolData f WHERE f.FTN = m.FTN ORDER BY f.FTNID) AS FTNComment"
            : $"CAST(NULL AS {Text}) AS FTNComment";
        var cols = string.Join(", ",
            "m.NCProgramFileID", "m.FTN",
            Opt(schema, t, "m", "ProgramToolNumber"),
            Opt(schema, t, "m", "ToolCount"),
            Opt(schema, t, "m", "CuttingTime"),
            Opt(schema, t, "m", "CuttingDistance"),
            Opt(schema, t, "m", "CuttingCount"),
            Opt(schema, t, "m", "ToolLifeType"),
            name);
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

    /// <summary>The route with each machining job's tools; Capped when the part's programs list more tool rows than
    /// the row limit (the rest are left out).</summary>
    private async Task<(PartRoute Route, bool Capped)> AddToolsAsync(
        Session s, int partId, PartRoute route, List<List<int?>> programs, List<string> notAvailable, CancellationToken ct)
    {
        if (ProgramToolsSql(s.Schema) is not { } sql)
        {
            notAvailable.Add("tools each program needs");
            return (route, false);
        }
        var settings = await ToolSettingsAsync(s, ct);

        var rows = new List<(int Program, int Ftn, int? Ptn, int Needed, string? Cutting, string Name)>();
        using (var cmd = Command(s.Connection, sql))
        {
            AddTake(cmd);
            cmd.Parameters.Add("@partId", System.Data.SqlDbType.Int).Value = partId;
            using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                rows.Add((Int(r, "NCProgramFileID") ?? 0, Int(r, "FTN") ?? 0, Int(r, "ProgramToolNumber"),
                    Math.Max(1, Int(r, "ToolCount") ?? 1),
                    CuttingUse(Int(r, "ToolLifeType"), Int(r, "CuttingTime"), Int(r, "CuttingDistance"), Int(r, "CuttingCount"),
                        settings.LengthUnit),
                    Str(r, "FTNComment")));
            }
        }
        var toolsCapped = rows.Count > _cap;
        if (toolsCapped) rows.RemoveRange(_cap, rows.Count - _cap);

        // Usable counts need the tool tables; on a cell where they aren't readable (a remote tool crib that is down)
        // the counts are "not available", never zero.
        Dictionary<(int DeviceId, int Ftn), int>? counts = null;
        if (rows.Count > 0)
        {
            if (s.Schema.HasTable(Itd) && s.Schema.HasTable(Ftd) && s.Schema.HasTable(Loc))
                counts = await UsableToolCountsAsync(s, rows.Select(t => t.Ftn).ToList(), ct);
            if (counts is null) notAvailable.Add("usable tools per machine");
        }

        var machines = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in s.Devices.Where(d => d.Type == MachineType)) machines.TryAdd(d.Name, d.Id);
        var processes = route.Processes.Select((p, pi) => p with
        {
            Jobs = p.Jobs.Select((job, ji) =>
            {
                if (programs[pi][ji] is not { } programId) return job;
                var feasible = (job.FeasibleDevices ?? [])
                    .Where(machines.ContainsKey).Select(n => (machines[n], n)).ToList();
                var tools = rows.Where(t => t.Program == programId).Select(t =>
                {
                    var (usable, missing) = Availability(t.Ftn, t.Needed, feasible, counts);
                    return new ProgramTool(t.Ftn, t.Needed)
                    {
                        Ptn = t.Ptn is { } ptn && ptn != t.Ftn ? ptn : null,
                        Name = NonEmpty(t.Name), Cutting = t.Cutting, Usable = usable, MissingOn = missing,
                    };
                }).ToList();
                return tools.Count > 0 ? job with { Tools = tools } : job;
            }).ToList(),
        }).ToList();
        return (route with { Processes = processes }, toolsCapped);
    }
}
