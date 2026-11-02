using Microsoft.Data.SqlClient;

namespace MakinoMCP.Core.Cell;

/// <summary>Which of the tables and columns MakinoMCP reads exist on this cell and are readable by the login. Older
/// MAS-A5 versions lack some columns (PowerStatus came in V3.0); DeviceResultDaySummary is the one optional table.
/// Every other table is required: a call that needs a missing one throws <see cref="CellSchemaException"/>.</summary>
public sealed class SchemaInfo
{
    /// <summary>Every table the reader touches.</summary>
    public static readonly IReadOnlyList<string> Tables =
    [
        "Devices", "MachineStatus", "WorkSetStatus", "PMCAlarms", "CNCAlarms", "DeviceStatusDaySummary",
        "DeviceResultDaySummary", "MachineResults", "WorkSetResults", "VehicleResults", "Orders", "Parts", "Pallets",
        "PalletLocation", "PalletFixtures", "Fixtures",
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
        using var cmd = connection.CreateCommand();
        cmd.CommandTimeout = SqlMakinoReader.CommandTimeoutSeconds;
        // OBJECT_ID resolves an unqualified name the same way the reader's queries do (the login's default schema,
        // then dbo). The names are the constants above, so listing them inline is safe.
        cmd.CommandText = SqlMakinoReader.Isolation + $"""
            SELECT v.name, c.name
            FROM (VALUES {string.Join(", ", Tables.Select(n => $"('{n}')"))}) v(name)
            JOIN sys.columns c ON c.object_id = OBJECT_ID(v.name)
            WHERE HAS_PERMS_BY_NAME(v.name, 'OBJECT', 'SELECT') = 1;
            """;

        var list = new List<(string, string)>();
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add((r.GetString(0), r.GetString(1)));
        return new SchemaInfo(list);
    }
}

/// <summary>A table the reader needs is missing on this cell or not readable by the login.</summary>
public sealed class CellSchemaException(IReadOnlyList<string> tables)
    : Exception($"Table{(tables.Count == 1 ? "" : "s")} {string.Join(", ", tables)} missing on this cell or not readable by this login.")
{
    public IReadOnlyList<string> Tables { get; } = tables;
}
