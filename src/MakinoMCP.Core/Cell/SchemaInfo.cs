using Microsoft.Data.SqlClient;

namespace MakinoMCP.Core.Cell;

/// <summary>Which of the tables and columns MakinoMCP reads exist on this cell. Older MAS-A5 versions lack some
/// columns (PowerStatus came in V3.0) and whole optional tables (DeviceResultDaySummary).</summary>
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

    public bool HasTable(string table) => _tables.Contains(table);

    public static async Task<SchemaInfo> LoadAsync(SqlConnection connection, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandTimeout = SqlMakinoReader.CommandTimeoutSeconds;
        // The names are the constants above, so listing them inline is safe (and works on old SQL Server versions).
        cmd.CommandText = SqlMakinoReader.Isolation + $"""
            SELECT t.name, c.name
            FROM sys.tables t JOIN sys.columns c ON c.object_id = t.object_id
            WHERE SCHEMA_NAME(t.schema_id) = 'dbo' AND t.name IN ({string.Join(", ", Tables.Select(n => $"'{n}'"))});
            """;

        var list = new List<(string, string)>();
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add((r.GetString(0), r.GetString(1)));
        return new SchemaInfo(list);
    }
}
