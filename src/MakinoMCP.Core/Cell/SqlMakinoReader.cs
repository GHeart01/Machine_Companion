using System.Globalization;
using Microsoft.Data.SqlClient;
using MakinoMCP.Core.Settings;

namespace MakinoMCP.Core.Cell;

/// <summary>Reads the Makino cell database: SELECT only, READ UNCOMMITTED, one connection per call. Queries per area
/// live in the SqlMakinoReader.*.cs partial files; this file holds the plumbing they share.</summary>
public sealed partial class SqlMakinoReader : IMakinoReader
{
    public const string Isolation = "SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;\n";
    public const int CommandTimeoutSeconds = 30;
    private const int DefaultCap = 500;
    private const int MaxHours = 24 * 400;

    private readonly MakinoLogin _login;
    private readonly int _cap;
    private readonly SchemaCache _schema = new();
    private readonly Action<string>? _log;

    /// <param name="log">Where to note problems that don't fail a call (a synonym table that can't be read); the
    /// lines never hold the password or the connection string.</param>
    public SqlMakinoReader(MakinoLogin login, Limits limits, Action<string>? log = null)
    {
        _login = login;
        _cap = limits.MaxRows < 1 ? DefaultCap : limits.MaxRows;
        _log = log;
    }

    /// <summary>A device row: Type and Number are how PalletLocation names a place.</summary>
    private sealed record DeviceRow(int Id, string Name, int Type, int Number);

    /// <summary>What every call starts with: an open connection, the schema, the cell's clock and its devices.</summary>
    private sealed class Session(SqlConnection connection, SchemaInfo schema, DateTime now, IReadOnlyList<DeviceRow> devices)
        : IAsyncDisposable
    {
        public SqlConnection Connection { get; } = connection;
        public SchemaInfo Schema { get; } = schema;
        public DateTime Now { get; } = now;
        public IReadOnlyList<DeviceRow> Devices { get; } = devices;

        public Dictionary<int, string> Names() => Devices.ToDictionary(d => d.Id, d => d.Name);

        public string NameOf(int deviceId) =>
            Devices.FirstOrDefault(d => d.Id == deviceId)?.Name ?? $"device {deviceId}";

        public ValueTask DisposeAsync() => Connection.DisposeAsync();
    }

    private async Task<Session> OpenAsync(CancellationToken ct)
    {
        var connection = new SqlConnection(_login.ConnectionString());
        try
        {
            await connection.OpenAsync(ct);
            var schema = await SchemaAsync(connection, ct);
            schema.Require("Devices");

            using var cmd = Command(connection, """
                SELECT GETDATE();
                SELECT DeviceID, DeviceName, DeviceType, DeviceNumber FROM Devices ORDER BY DeviceType, DeviceNumber, DeviceID;
                """);
            using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            var now = r.GetDateTime(0);
            await r.NextResultAsync(ct);
            var devices = new List<DeviceRow>();
            while (await r.ReadAsync(ct))
            {
                var id = Int(r, "DeviceID") ?? 0;
                var name = Str(r, "DeviceName");
                devices.Add(new DeviceRow(id, name.Length > 0 ? name : $"device {id}", Int(r, "DeviceType") ?? 0, Int(r, "DeviceNumber") ?? 0));
            }
            return new Session(connection, schema, now, devices);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    // Loaded once per reader (see SchemaCache: a failed load is not kept; unreadable synonyms are retried later).
    private Task<SchemaInfo> SchemaAsync(SqlConnection connection, CancellationToken ct) =>
        _schema.GetAsync(token => SchemaInfo.LoadAsync(connection, OpenProbeAsync,
            (table, e) => _log?.Invoke(ProbeFailedLine(table, e, _login)), token), ct);

    // A connection of its own for one synonym probe.
    private async Task<SqlConnection> OpenProbeAsync(CancellationToken ct)
    {
        var connection = new SqlConnection(_login.ConnectionString());
        try
        {
            await connection.OpenAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>The log line for a synonym that couldn't be read: table, exception type and message, scrubbed.</summary>
    internal static string ProbeFailedLine(string table, Exception e, MakinoLogin login) =>
        $"schema: {table} (a synonym) not readable, counted as missing until a retry in " +
        $"{SchemaCache.DefaultRetryAfter.TotalMinutes:0} minutes: {ErrorText.ForClaude(e, login)}";

    private static SqlCommand Command(SqlConnection connection, string sql) =>
        new(Isolation + sql, connection) { CommandTimeout = CommandTimeoutSeconds };

    /// <summary>The rows to fetch: one more than the cap, so a full page shows that more existed.</summary>
    private int Take => _cap + 1;

    private (IReadOnlyList<T> Rows, bool Capped) Cap<T>(List<T> rows) =>
        rows.Count > _cap ? (rows.GetRange(0, _cap), true) : (rows, false);

    private static int Hours(int hours) => Math.Clamp(hours, 1, MaxHours);

    private static int Days(int days) => Math.Clamp(days, 1, MaxHours / 24);

    /// <summary>The device id for a name (case-insensitive), null for no filter; an unknown name throws with the valid names.</summary>
    public static int? FindDevice(IReadOnlyDictionary<int, string> devices, string? device)
    {
        if (string.IsNullOrWhiteSpace(device)) return null;
        var wanted = device.Trim();
        foreach (var (id, name) in devices)
        {
            if (string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase)) return id;
        }
        var names = devices.Values.Order(StringComparer.OrdinalIgnoreCase);
        throw new ArgumentException($"No device named '{wanted}'. Devices on this cell: {string.Join(", ", names)}.");
    }

    /// <summary>The select-list entry for a column an older cell may lack: the column when this cell has it, else a
    /// NULL of <paramref name="type"/> (so UNIONs keep their column types), named <paramref name="name"/> (default: the
    /// column's own name). Every column a query shows goes through here; only join keys and the time columns a query
    /// filters on are selected unchecked.</summary>
    private static string Opt(SchemaInfo schema, string table, string alias, string column, string? name = null, string type = "int") =>
        schema.Has(table, column) ? $"{alias}.{column} AS {name ?? column}" : $"CAST(NULL AS {type}) AS {name ?? column}";

    private const string Text = "varchar(4000)";

    /// <summary>The pallet number for a PalletID expression, looked up in Pallets.</summary>
    private static string PalletNumberSql(SchemaInfo schema, string palletId, string name) =>
        schema.Has("Pallets", "PalletNumber")
            ? $"(SELECT TOP 1 p.PalletNumber FROM Pallets p WHERE p.PalletID = {palletId}) AS {name}"
            : $"CAST(NULL AS int) AS {name}";

    /// <summary>The SystemParameters rows named <paramref name="names"/> (ParameterName, ParameterValue), the first
    /// system's first; null when the table or those columns can't be read. The names are constants from this code,
    /// never user input.</summary>
    internal static string? SystemParametersSql(SchemaInfo schema, IEnumerable<string> names)
    {
        if (!schema.Has("SystemParameters", "ParameterName") || !schema.Has("SystemParameters", "ParameterValue")) return null;
        var order = schema.Has("SystemParameters", "SystemID") ? "SystemID, ParameterName" : "ParameterName";
        var list = string.Join(", ", names.Select(n => $"'{n}'"));
        return $"SELECT ParameterName, ParameterValue FROM SystemParameters WHERE ParameterName IN ({list}) ORDER BY {order};";
    }

    /// <summary>The cell's settings by name (case-insensitive; a name the cell has no row for is absent, which the
    /// vendor reads as its default). Null when this login can't read SystemParameters: then nothing is known.</summary>
    private static async Task<IReadOnlyDictionary<string, string>?> SystemParametersAsync(
        Session s, CancellationToken ct, params string[] names)
    {
        if (SystemParametersSql(s.Schema, names) is not { } sql) return null;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = Command(s.Connection, sql);
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) values.TryAdd(Str(r, "ParameterName"), Str(r, "ParameterValue")); // first system wins
        return values;
    }

    /// <summary>Per-device settings (DeviceParameters) by device id and name; null when the table or its columns can't
    /// be read. A device without a row for a name runs on the vendor's default.</summary>
    private static async Task<IReadOnlyDictionary<int, Dictionary<string, string>>?> DeviceParametersAsync(
        Session s, CancellationToken ct, params string[] names)
    {
        const string t = "DeviceParameters";
        if (!s.Schema.Has(t, "DeviceID") || !s.Schema.Has(t, "ParameterName") || !s.Schema.Has(t, "ParameterValue")) return null;
        var list = string.Join(", ", names.Select(n => $"'{n}'"));
        using var cmd = Command(s.Connection,
            $"SELECT DeviceID, ParameterName, ParameterValue FROM DeviceParameters WHERE ParameterName IN ({list});");
        using var r = await cmd.ExecuteReaderAsync(ct);
        var values = new Dictionary<int, Dictionary<string, string>>();
        while (await r.ReadAsync(ct))
        {
            var id = Int(r, "DeviceID") ?? 0;
            if (!values.TryGetValue(id, out var own)) values[id] = own = new(StringComparer.OrdinalIgnoreCase);
            own.TryAdd(Str(r, "ParameterName"), Str(r, "ParameterValue"));
        }
        return values;
    }

    /// <summary>The cell's runtime settings (UserSysData: duty mode, power-off state, …) by name, the first system's;
    /// null when the table can't be read.</summary>
    private static async Task<IReadOnlyDictionary<string, string>?> UserSysDataAsync(
        Session s, CancellationToken ct, params string[] names)
    {
        const string t = "UserSysData";
        if (!s.Schema.Has(t, "DataName") || !s.Schema.Has(t, "DataValue")) return null;
        var list = string.Join(", ", names.Select(n => $"'{n}'"));
        var order = s.Schema.Has(t, "SystemID") ? "SystemID, DataName" : "DataName";
        using var cmd = Command(s.Connection, $"SELECT DataName, DataValue FROM UserSysData WHERE DataName IN ({list}) ORDER BY {order};");
        using var r = await cmd.ExecuteReaderAsync(ct);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (await r.ReadAsync(ct)) values.TryAdd(Str(r, "DataName"), Str(r, "DataValue"));
        return values;
    }

    /// <summary>A raw code nobody has mapped: "code N", or "" when NULL.</summary>
    internal static string CodeText(int? value) => value is null ? "" : $"code {value}";

    /// <summary>Any integer width or bit as int; text only when it is a whole number (else null). Never throws.</summary>
    internal static int? ToInt(object? value) => value switch
    {
        null or DBNull => null,
        string text => int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null,
        bool b => b ? 1 : 0,
        IConvertible c => TryConvert(c),
        _ => null,
    };

    private static int? TryConvert(IConvertible value)
    {
        try { return value.ToInt32(CultureInfo.InvariantCulture); }
        catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException) { return null; }
    }

    public async Task<LoginRightsInfo> GetLoginRightsAsync(CancellationToken ct)
    {
        await using var s = await OpenAsync(ct);
        // Effective rights: role membership, database-wide grants, or a grant on any single table.
        using var cmd = Command(s.Connection, """
            SELECT SUSER_SNAME() AS LoginName,
                   ISNULL(IS_SRVROLEMEMBER('sysadmin'), 0) AS SysAdmin,
                   ISNULL(IS_MEMBER('db_owner'), 0) AS DbOwner,
                   ISNULL(IS_MEMBER('db_datawriter'), 0) AS DataWriter,
                   ISNULL(IS_MEMBER('db_ddladmin'), 0) AS DdlAdmin,
                   CASE WHEN HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'INSERT') = 1
                          OR HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'UPDATE') = 1
                          OR HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'DELETE') = 1 THEN 1 ELSE 0 END AS DbGrant,
                   CASE WHEN EXISTS (
                       SELECT 1 FROM sys.tables t
                       CROSS APPLY (SELECT QUOTENAME(SCHEMA_NAME(t.schema_id)) + '.' + QUOTENAME(t.name) AS FullName) n
                       WHERE HAS_PERMS_BY_NAME(n.FullName, 'OBJECT', 'INSERT') = 1
                          OR HAS_PERMS_BY_NAME(n.FullName, 'OBJECT', 'UPDATE') = 1
                          OR HAS_PERMS_BY_NAME(n.FullName, 'OBJECT', 'DELETE') = 1) THEN 1 ELSE 0 END AS TableGrant;
            """);
        using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);

        var reasons = new List<string>();
        if (Int(r, "SysAdmin") == 1) reasons.Add("server administrator");
        if (Int(r, "DbOwner") == 1) reasons.Add("database owner");
        if (Int(r, "DataWriter") == 1) reasons.Add("db_datawriter role");
        if (Int(r, "DdlAdmin") == 1) reasons.Add("db_ddladmin role");
        if (Int(r, "DbGrant") == 1) reasons.Add("database-wide write grant");
        if (Int(r, "TableGrant") == 1 && reasons.Count == 0) reasons.Add("write grant on some tables");

        var login = Str(r, "LoginName");
        var canWrite = reasons.Count > 0;
        var summary = canWrite
            ? $"Login '{login}' can change the cell database ({string.Join(", ", reasons)}). MakinoMCP only reads, but a read-only login (db_datareader) would be safer. It sees {s.Devices.Count} devices."
            : $"Login '{login}' can read but not change the cell database. It sees {s.Devices.Count} devices.";
        return new LoginRightsInfo(canWrite, s.Devices.Count, summary);
    }

    // Column readers by name: NULL comes back as null (or "" for text); numbers of any integer width read as int.
    private static int? Int(SqlDataReader r, string column) => ToInt(r.GetValue(r.GetOrdinal(column)));

    private static bool? Bool(SqlDataReader r, string column)
    {
        var i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? null : Convert.ToBoolean(r.GetValue(i), CultureInfo.InvariantCulture);
    }

    private static DateTime? Date(SqlDataReader r, string column)
    {
        var i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? null : r.GetDateTime(i);
    }

    private static double Double(SqlDataReader r, string column)
    {
        var i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? 0 : Convert.ToDouble(r.GetValue(i), CultureInfo.InvariantCulture);
    }

    private static string Str(SqlDataReader r, string column)
    {
        var i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture)!.Trim();
    }
}
