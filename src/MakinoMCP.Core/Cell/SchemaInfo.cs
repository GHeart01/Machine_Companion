using System.Globalization;
using Microsoft.Data.SqlClient;

namespace MakinoMCP.Core.Cell;

/// <summary>Which of the tables and columns MakinoMCP reads exist on this cell and are readable by the login. Older
/// MAS-A5 versions lack some columns (PowerStatus came in V3.0) and some tables; a section that only some cells have
/// (the vehicle's status, SystemParameters, …) checks <see cref="HasTable"/> and says it is not available. A table a
/// call can't do without is required: a missing one throws <see cref="CellSchemaException"/>. A table reached through
/// a synonym (a remote tool crib's tool tables) counts when a SELECT through it works.</summary>
public sealed class SchemaInfo
{
    /// <summary>Every table (or vendor view) the reader may touch; only these names are ever probed.</summary>
    public static readonly IReadOnlyList<string> Tables =
    [
        "Devices", "MachineStatus", "WorkSetStatus", "PMCAlarms", "CNCAlarms", "DeviceStatusDaySummary",
        "DeviceResultDaySummary", "MachineResults", "WorkSetResults", "VehicleResults", "Orders", "Parts", "Pallets",
        "PalletLocation", "PalletFixtures", "Fixtures",
        // 0.2: device dashboard and parameters.
        "VehicleStatus", "ViewVehicleStatus", "SystemParameters", "DeviceParameters",
        // 0.2: tools (the first five are synonyms on a cell with a remote tool crib).
        "FunctionalToolData", "FunctionalToolCutterData", "IndividualToolData", "IndividualToolCutterData", "ToolLocation",
        "ToolAlarmHistory", "PresetResults", "ToolInputOutputResults", "MainProgramTools",
        // 0.2: routing, production and attention.
        "Processes", "Operations", "Jobs", "MachineJobs", "WorkSetJobs", "JobFeasibleDevices", "FixtureProcesses",
        "NCProgramFiles", "InformationFiles", "Quantities", "FixtureQuantity", "FixtureRun", "OrderCompletedQtyResults",
        "WorkSetCommand", "ManualTransport",
        // The vendor's views of the order quantities, for a login that can't read the tables behind them.
        "ViewOrders", "ViewDayOrderCompletedQty",
    ];

    /// <summary>How long one synonym probe's command may run.</summary>
    public const int ProbeTimeoutSeconds = 5;

    /// <summary>How long all synonym probes together may take: a linked server that is down must cost seconds, not
    /// the whole tool budget.</summary>
    public static readonly TimeSpan ProbeCap = TimeSpan.FromSeconds(10);

    private readonly HashSet<string> _columns = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _tables = new(StringComparer.OrdinalIgnoreCase);

    public SchemaInfo(IEnumerable<(string Table, string Column)> columns, IEnumerable<string>? unreadableSynonyms = null)
    {
        foreach (var (table, column) in columns)
        {
            _tables.Add(table);
            _columns.Add(table + "." + column);
        }
        UnreadableSynonyms = unreadableSynonyms?.ToList() ?? [];
    }

    /// <summary>Synonyms among <see cref="Tables"/> whose probe failed or ran out of time: missing for now, worth
    /// another try later (see <see cref="SchemaCache"/>).</summary>
    public IReadOnlyList<string> UnreadableSynonyms { get; }

    public bool Has(string table, string column) => _columns.Contains(table + "." + column);

    /// <summary>True when the table exists and this login may SELECT from it.</summary>
    public bool HasTable(string table) => _tables.Contains(table);

    /// <summary>Throws <see cref="CellSchemaException"/> naming every listed table that is missing or not readable.</summary>
    public void Require(params string[] tables)
    {
        var missing = tables.Where(t => !HasTable(t)).ToList();
        if (missing.Count > 0) throw new CellSchemaException(missing);
    }

    /// <summary>Reads which listed tables exist. Synonyms are probed all at once, each on its own connection from
    /// <paramref name="openProbe"/> (so a hung probe never holds <paramref name="connection"/>), under
    /// <see cref="ProbeCap"/>; <paramref name="probeFailed"/> hears about each one that failed.</summary>
    public static async Task<SchemaInfo> LoadAsync(
        SqlConnection connection, Func<CancellationToken, Task<SqlConnection>> openProbe,
        Action<string, Exception>? probeFailed, CancellationToken ct)
    {
        var columns = new List<(string, string)>();
        var synonyms = new List<string>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandTimeout = SqlMakinoReader.CommandTimeoutSeconds;
            // OBJECT_ID resolves an unqualified name the same way the reader's queries do (the login's default schema,
            // then dbo). The names are the constants above, so listing them inline is safe. A synonym has no rows in
            // sys.columns, so synonyms are listed separately and probed below.
            var names = $"(VALUES {string.Join(", ", Tables.Select(n => $"('{n}')"))}) v(name)";
            cmd.CommandText = SqlMakinoReader.Isolation + $"""
                SELECT v.name, c.name
                FROM {names}
                JOIN sys.columns c ON c.object_id = OBJECT_ID(v.name)
                WHERE HAS_PERMS_BY_NAME(v.name, 'OBJECT', 'SELECT') = 1;
                SELECT v.name
                FROM {names}
                JOIN sys.synonyms s ON s.object_id = OBJECT_ID(v.name);
                """;

            using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) columns.Add((r.GetString(0), r.GetString(1)));
            await r.NextResultAsync(ct);
            while (await r.ReadAsync(ct)) synonyms.Add(r.GetString(0));
        }
        return await BuildAsync(columns, synonyms, (name, token) => SynonymColumnsAsync(openProbe, name, token),
            ProbeCap, probeFailed, ct);
    }

    /// <summary>The schema from the direct tables' columns plus the synonyms among <see cref="Tables"/>, whose columns
    /// come from <paramref name="columnsOf"/>, all probed at once. A synonym whose probe throws (no rights through it,
    /// the linked server down) or hasn't answered within <paramref name="cap"/> is missing and listed in
    /// <see cref="UnreadableSynonyms"/>; names not in <see cref="Tables"/> are ignored and never probed.</summary>
    internal static async Task<SchemaInfo> BuildAsync(
        IEnumerable<(string Table, string Column)> columns, IEnumerable<string> synonyms,
        Func<string, CancellationToken, Task<IReadOnlyList<string>>> columnsOf, TimeSpan cap,
        Action<string, Exception>? probeFailed, CancellationToken ct)
    {
        var listed = synonyms
            .Select(s => Tables.FirstOrDefault(t => string.Equals(t, s, StringComparison.OrdinalIgnoreCase)))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(cap);
        var results = await Task.WhenAll(listed.Select(table => ProbeAsync(table, columnsOf, cap, probeFailed, deadline, ct)));

        var all = columns.ToList();
        foreach (var (table, found) in results)
        {
            if (found is not null) all.AddRange(found.Select(c => (table, c)));
        }
        return new SchemaInfo(all, results.Where(x => x.Columns is not { Count: > 0 }).Select(x => x.Table));
    }

    private static async Task<(string Table, IReadOnlyList<string>? Columns)> ProbeAsync(
        string table, Func<string, CancellationToken, Task<IReadOnlyList<string>>> columnsOf, TimeSpan cap,
        Action<string, Exception>? probeFailed, CancellationTokenSource deadline, CancellationToken ct)
    {
        try
        {
            // Task.Run: a probe that throws before its first await fails alone. WaitAsync: stop waiting at the cap
            // even if the probe ignores its token.
            var found = await Task.Run(() => columnsOf(table, deadline.Token), CancellationToken.None).WaitAsync(deadline.Token);
            return (table, found);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            var reason = e is OperationCanceledException && deadline.IsCancellationRequested
                ? new TimeoutException(
                    $"no answer within {cap.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} seconds", e)
                : e;
            probeFailed?.Invoke(table, reason);
            return (table, null);
        }
    }

    // The columns a SELECT through a synonym returns, on a connection of its own; the name is one of the constants in
    // Tables.
    private static async Task<IReadOnlyList<string>> SynonymColumnsAsync(
        Func<CancellationToken, Task<SqlConnection>> openProbe, string table, CancellationToken ct)
    {
        await using var connection = await openProbe(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandTimeout = ProbeTimeoutSeconds;
        cmd.CommandText = SqlMakinoReader.Isolation + $"SELECT TOP (0) * FROM {table};";
        using var r = await cmd.ExecuteReaderAsync(ct);
        return Enumerable.Range(0, r.FieldCount).Select(r.GetName).ToList();
    }
}

/// <summary>The reader's schema, loaded once. A first load that throws is not kept, so the next call tries again. A
/// schema with unreadable synonyms is kept (those tables count as missing) and reloaded no sooner than
/// <c>retryAfter</c> later; if that reload fails, the schema already known stands for another interval.</summary>
public sealed class SchemaCache(Func<DateTime> utcNow, TimeSpan retryAfter)
{
    public static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private SchemaInfo? _schema;
    private DateTime _loadedAt;

    public SchemaCache() : this(() => DateTime.UtcNow, DefaultRetryAfter) { }

    public async Task<SchemaInfo> GetAsync(Func<CancellationToken, Task<SchemaInfo>> load, CancellationToken ct)
    {
        if (_schema is { } fresh && !Due(fresh)) return fresh;
        await _gate.WaitAsync(ct);
        try
        {
            if (_schema is { } known)
            {
                if (!Due(known)) return known;
                try { _schema = await load(ct); }
                catch (Exception) when (!ct.IsCancellationRequested) { /* keep what is known; try again later */ }
                _loadedAt = utcNow();
                return _schema;
            }
            var loaded = await load(ct);
            (_schema, _loadedAt) = (loaded, utcNow());
            return loaded;
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool Due(SchemaInfo schema) => schema.UnreadableSynonyms.Count > 0 && utcNow() - _loadedAt >= retryAfter;
}

/// <summary>A table the reader needs is missing on this cell or not readable by the login.</summary>
public sealed class CellSchemaException(IReadOnlyList<string> tables)
    : Exception($"Table{(tables.Count == 1 ? "" : "s")} {string.Join(", ", tables)} missing on this cell or not readable by this login.")
{
    public IReadOnlyList<string> Tables { get; } = tables;
}
