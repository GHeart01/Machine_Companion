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
    ];

    private readonly HashSet<string> _columns = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _tables = new(StringComparer.OrdinalIgnoreCase);

    public SchemaInfo(IEnumerable<(string Table, string Column)> columns)
    {
        foreach (var (table, column) in columns)
        {
            _tables.Add(table);
            _columns.Add(table + "." + column);
        }
    }

    public bool Has(string table, string column) => _columns.Contains(table + "." + column);

    /// <summary>True when the table exists and this login may SELECT from it.</summary>
    public bool HasTable(string table) => _tables.Contains(table);

    /// <summary>Throws <see cref="CellSchemaException"/> naming every listed table that is missing or not readable.</summary>
    public void Require(params string[] tables)
    {
        var missing = tables.Where(t => !HasTable(t)).ToList();
        if (missing.Count > 0) throw new CellSchemaException(missing);
    }

    public static async Task<SchemaInfo> LoadAsync(SqlConnection connection, CancellationToken ct)
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
        return await BuildAsync(columns, synonyms, (name, token) => SynonymColumnsAsync(connection, name, token), ct);
    }

    /// <summary>The schema from the direct tables' columns plus the synonyms among <see cref="Tables"/>, whose columns
    /// come from <paramref name="columnsOf"/>. A synonym whose probe throws (no rights through it, the linked server
    /// down) is missing; names not in <see cref="Tables"/> are ignored and never probed.</summary>
    internal static async Task<SchemaInfo> BuildAsync(
        IEnumerable<(string Table, string Column)> columns, IEnumerable<string> synonyms,
        Func<string, CancellationToken, Task<IReadOnlyList<string>>> columnsOf, CancellationToken ct)
    {
        var all = columns.ToList();
        var listed = synonyms
            .Select(s => Tables.FirstOrDefault(t => string.Equals(t, s, StringComparison.OrdinalIgnoreCase)))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var table in listed)
        {
            IReadOnlyList<string> found;
            try { found = await columnsOf(table, ct); }
            catch (Exception) when (!ct.IsCancellationRequested) { continue; }
            all.AddRange(found.Select(c => (table, c)));
        }
        return new SchemaInfo(all);
    }

    // The columns a SELECT through a synonym returns; the name is one of the constants in Tables.
    private static async Task<IReadOnlyList<string>> SynonymColumnsAsync(SqlConnection connection, string table, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandTimeout = SqlMakinoReader.CommandTimeoutSeconds;
        cmd.CommandText = SqlMakinoReader.Isolation + $"SELECT TOP (0) * FROM {table};";
        using var r = await cmd.ExecuteReaderAsync(ct);
        return Enumerable.Range(0, r.FieldCount).Select(r.GetName).ToList();
    }
}

/// <summary>A table the reader needs is missing on this cell or not readable by the login.</summary>
public sealed class CellSchemaException(IReadOnlyList<string> tables)
    : Exception($"Table{(tables.Count == 1 ? "" : "s")} {string.Join(", ", tables)} missing on this cell or not readable by this login.")
{
    public IReadOnlyList<string> Tables { get; } = tables;
}
