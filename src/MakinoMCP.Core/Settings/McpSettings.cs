using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;

namespace MakinoMCP.Core.Settings;

/// <summary>Everything MakinoMCP needs from MakinoMCP.settings.json.</summary>
public sealed record McpSettings(MakinoLogin Makino, IReadOnlyList<SharedFolder> Folders, Limits Limits)
{
    /// <summary>True when the Makino login is filled in enough to try a connection.</summary>
    [JsonIgnore]
    public bool IsReady =>
        !string.IsNullOrWhiteSpace(Makino.Server)
        && !string.IsNullOrWhiteSpace(Makino.Database)
        && (Makino.UseWindowsLogin || !string.IsNullOrWhiteSpace(Makino.User));

    // The generated ToString would print the nested login; keep the password out of any log line.
    public override string ToString() =>
        $"McpSettings {{ Makino = {Makino}, Folders = {Folders.Count}, Limits = {Limits} }}";
}

public sealed record MakinoLogin(
    string Server,
    string Database,
    bool UseWindowsLogin,
    string User,
    string Password,
    bool TrustServerCertificate = true,
    int ConnectTimeoutSeconds = 10)
{
    public string ConnectionString()
    {
        var b = new SqlConnectionStringBuilder
        {
            DataSource = Server,
            InitialCatalog = Database,
            TrustServerCertificate = TrustServerCertificate,
            ConnectTimeout = ConnectTimeoutSeconds,
            ApplicationName = "MakinoMCP",
            ConnectRetryCount = 3,
        };
        if (UseWindowsLogin)
        {
            b.IntegratedSecurity = true;
        }
        else
        {
            b.UserID = User;
            b.Password = Password;
        }
        return b.ConnectionString;
    }

    // Never print the password (the generated ToString would).
    public override string ToString() =>
        $"MakinoLogin {{ Server = {Server}, Database = {Database}, UseWindowsLogin = {UseWindowsLogin}, User = {User}, Password = *** }}";
}

public sealed record SharedFolder(string Name, string Path, string Description);

public sealed record Limits(int MaxRows = 500, int MaxFileKB = 256);
