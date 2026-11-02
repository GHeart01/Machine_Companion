using System.ComponentModel;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using MakinoMCP.Core.Settings;

namespace MakinoMCP.Core.Cell;

/// <summary>Turns a failed read into one short line Claude can act on. Never contains the password or the
/// connection string, whatever the exception.</summary>
public static partial class ErrorText
{
    private const int MaxDetail = 300;

    public static string ForClaude(Exception e, MakinoLogin login)
    {
        var inner = Unwrap(e);
        // Scrub before cutting, so a cut can't leave part of the password behind; scrub the whole line again after.
        var detail = Scrub(inner.Message.ReplaceLineEndings(" ").Trim(), login);
        if (detail.Length > MaxDetail) detail = detail[..MaxDetail] + "…";

        var text = $"Could not read the Makino cell database: {Hint(inner, login)}. ({inner.GetType().Name}: {detail})";
        return Scrub(text, login);
    }

    private static Exception Unwrap(Exception e)
    {
        while (e is AggregateException { InnerExceptions.Count: 1 } a) e = a.InnerExceptions[0];
        return e;
    }

    private static string Hint(Exception e, MakinoLogin login) => e switch
    {
        SqlException sql => sql.Number switch
        {
            18456 or 18452 or 18486 or 18487 or 18488 => login.UseWindowsLogin
                ? "login refused: this Windows account has no access to the cell database"
                : "login refused: check the user name and password in the settings",
            4060 => $"database '{login.Database}' not found or not allowed: check the database name",
            -2 => "the query timed out: the cell PC may be busy, try a shorter time range",
            53 or 2 or 40 or 11001 or 10060 or 10061 or -1 or 258 =>
                $"could not reach the SQL Server: check the server name ('{login.Server}') and that the cell PC is on",
            229 or 230 or 297 or 300 => "this login is not allowed to read that table",
            208 => "a table MakinoMCP reads is missing on this cell's software version",
            207 => "a column MakinoMCP reads is missing on this cell's software version",
            _ => $"SQL Server error {sql.Number}",
        },
        TimeoutException => "timed out: the cell PC may be busy or unreachable",
        Win32Exception { NativeErrorCode: 258 } => "timed out: the cell PC may be busy or unreachable",
        OperationCanceledException => "the request was cancelled",
        InvalidOperationException when e.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase) =>
            "timed out waiting for a connection: check the server name and that the cell PC is on",
        ArgumentException when e.Message.Contains("connection string", StringComparison.OrdinalIgnoreCase)
            || e.Message.Contains("initialization string", StringComparison.OrdinalIgnoreCase) =>
            "the login settings are not valid: open the setup window to fix them",
        _ => "unexpected error",
    };

    private static string Scrub(string text, MakinoLogin login)
    {
        // The connection string first: it contains the password, so replacing the password first would hide it.
        string connection;
        try { connection = login.ConnectionString(); }
        catch (ArgumentException) { connection = ""; }
        if (connection.Length > 0)
            text = text.Replace(connection, "(connection string)", StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(login.Password))
            text = text.Replace(login.Password, "***", StringComparison.Ordinal);

        return SecretFragment().Replace(text, "($1 hidden)");
    }

    // "Password=x", "Pwd = x", "password: x" up to the next separator.
    [GeneratedRegex(@"\b(password|pwd)\s*[=:]\s*[^;,\s]*", RegexOptions.IgnoreCase)]
    private static partial Regex SecretFragment();
}
