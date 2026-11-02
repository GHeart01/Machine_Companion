using System.Globalization;

namespace MakinoMCP.Logging;

/// <summary>
/// The server's own log: one file per day in %LOCALAPPDATA%\MakinoMCP\logs (per PC, so users of a shared install
/// don't fight over one file), kept 14 days. One line per tool call and every error; never the password, never tool
/// results, never stdout (stdout carries only MCP messages). A failure to log is swallowed: logging never breaks a tool.
/// </summary>
public sealed class FileLog
{
    private const int KeepDays = 14;
    private const string Prefix = "makinomcp-";
    private const string DateFormat = "yyyyMMdd";

    private readonly string _folder;
    private readonly Lock _gate = new();

    public FileLog(string folder)
    {
        _folder = folder;
        Prune();
    }

    /// <summary>%LOCALAPPDATA%\MakinoMCP\logs, reading LOCALAPPDATA the way the launcher does.</summary>
    public static string DefaultFolder() => Path.Combine(LocalAppDataFolder(), "logs");

    /// <summary>%LOCALAPPDATA%\MakinoMCP (the variable first, as the launcher reads it, then the known folder).</summary>
    public static string LocalAppDataFolder() => Path.Combine(
        Environment.GetEnvironmentVariable("LOCALAPPDATA") is { Length: > 0 } env
            ? env
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MakinoMCP");

    /// <summary>One line for a finished tool call: its name, how long it took, ok or the error shown to Claude.</summary>
    public void ToolCall(string tool, TimeSpan duration, string? error) =>
        Write(error is null
            ? $"{tool} {(long)duration.TotalMilliseconds} ms ok"
            : $"{tool} {(long)duration.TotalMilliseconds} ms error: {error}");

    public void Write(string message)
    {
        try
        {
            var now = DateTime.Now;
            var line = $"{now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} {message.ReplaceLineEndings(" ")}{Environment.NewLine}";
            lock (_gate)
            {
                Directory.CreateDirectory(_folder);
                File.AppendAllText(Path.Combine(_folder, Prefix + now.ToString(DateFormat, CultureInfo.InvariantCulture) + ".log"), line);
            }
        }
        catch (Exception)
        {
            // Never let logging break a tool call.
        }
    }

    // Deletes day files older than KeepDays, by the date in their name.
    private void Prune()
    {
        try
        {
            if (!Directory.Exists(_folder)) return;
            var oldest = DateTime.Today.AddDays(-KeepDays);
            foreach (var file in Directory.EnumerateFiles(_folder, Prefix + "*.log"))
            {
                var stem = Path.GetFileNameWithoutExtension(file)[Prefix.Length..];
                if (DateTime.TryParseExact(stem, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                    && day < oldest)
                {
                    try { File.Delete(file); }
                    catch (Exception) { /* in use or gone: try again next start */ }
                }
            }
        }
        catch (Exception)
        {
            // An unreadable log folder is not worth failing over.
        }
    }
}
