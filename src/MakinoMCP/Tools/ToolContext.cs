using System.Diagnostics;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using MakinoMCP.Core.Cell;
using MakinoMCP.Core.Folders;
using MakinoMCP.Core.Settings;
using MakinoMCP.Logging;

namespace MakinoMCP.Tools;

/// <summary>
/// What every tool shares (a DI singleton): the settings read once at startup, the cell reader and the shared folders
/// when they are set up, and the one way a tool call runs (time budget, JSON, errors as <c>{ "error": "..." }</c>, one
/// log line). Nothing escapes a tool as an exception.
/// </summary>
public sealed class ToolContext
{
    public const string NotSetUp =
        "MakinoMCP isn't set up yet: run MakinoMCP.exe (no arguments) on this PC to enter the database login.";

    public const string ServerInstructions =
        "MakinoMCP shows what a Makino MAS-A5 cell is doing, read only: it never changes the cell, its database or any " +
        "file. Start with get_cell_overview for the state of every device, or get_attention for what needs a person. " +
        "Then dig in: get_tools and get_tool_changes / get_tool_alarms / get_tool_movements for tools, " +
        "get_part_routing for how a part is made and whether its tools are there, get_production for parts made and " +
        "order progress, get_pallet_diagnosis for why a pallet's work isn't moving. Times are the cell database server's clock; every database result carries serverTime, the " +
        "server's current time, so measure \"open now\" and \"2 hours ago\" against it, not your own clock. History is " +
        "limited: the overview's historyDays is how many days the cell keeps PMC alarms and results (the vendor's " +
        "default of 90 when the cell doesn't say, flagged historyDaysAssumed); presetHistoryMonths and " +
        "toolMoveHistoryMonths cover tool data changes and tool moves (about 12 and 3 months when the cell doesn't " +
        "say); day summaries go back about 12 months and daily order counts about 3; CNC alarms are limited to about the " +
        "last 200 regardless. The cell purges as rows are added, so these are minimums. Tool lengths are in the cell's own unit " +
        "(lengthUnit), not always mm, and diameters are radii when diameterIsRadius is true. A device's mode and " +
        "emergency stop are left out while it is disconnected. Values whose meaning is not known are shown as \"code N\"; don't guess what " +
        "they mean. Results are capped at a row limit; capped: true means more rows existed, so narrow the " +
        "time range or pick a device. What MakinoMCP sees is decided by its SQL login, not by anyone's console " +
        "group; the cell's own read-only reporting login can't read a few tables (the vehicle's status table, the tool " +
        "alarm history), and the vendor's views stand in for them, so name a gap from notAvailable rather than guess. " +
        "Kept only in the cell server's memory or on the console, so not visible here: a machine's maintenance mode, " +
        "load disable and auto-unload mode, an NC download in progress, why a device was closed, the spindle and next " +
        "tool, overrides, single block, the memory-protect key, ATC/APC ready, the NC block running, the magazine size, " +
        "the vehicle's command phase and wait reasons, the full transport queue order, a recovery's error number and " +
        "offered actions, and whether an alarm was e-mailed. Visible: CNC mode, cncRun (hold matches the Feed Hold lamp " +
        "when no operator call is active), the executing O-number, open/closed, transport cancelled and system or " +
        "standalone; transport cancel is not load disable. Recovery history (what was recovered, how, when, Work Data " +
        "Resets) is only in the cell's RecoveryMaintenaceOperation.log, readable when the cell's log folder is a shared " +
        "folder. Shared folders hold the cell PC's log files and similar text files.";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        // Results go to Claude, not into HTML: keep accents, quotes and dashes readable.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public ToolContext(SettingsLoad settings, IMakinoReader? reader, SharedFolders? folders, FileLog? log = null)
    {
        Settings = settings;
        Reader = reader;
        Folders = folders;
        Log = log;
        SetupProblem = settings.Settings?.IsReady == true
            ? null
            : settings.Problem is { Length: > 0 } problem ? $"{NotSetUp} {problem}" : NotSetUp;
    }

    public SettingsLoad Settings { get; }

    /// <summary>Null until the database login is filled in.</summary>
    public IMakinoReader? Reader { get; }

    /// <summary>Null when there are no settings, or no login and no folders: nothing to read.</summary>
    public SharedFolders? Folders { get; }

    /// <summary>Why the database tools can't run yet (the not-set-up message), or null when they can.</summary>
    public string? SetupProblem { get; }

    public FileLog? Log { get; }

    public TimeSpan DatabaseBudget { get; init; } = TimeSpan.FromSeconds(45);

    public TimeSpan FolderBudget { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Reads the settings file once and builds what it allows: the reader when the login is filled in,
    /// the folders when there are settings with a login or folders.</summary>
    public static ToolContext Load(string settingsPath, FileLog? log)
    {
        var load = SettingsStore.Load(settingsPath);
        var s = load.Settings;
        var reader = s is { IsReady: true } ? new SqlMakinoReader(s.Makino, s.Limits, log is null ? null : log.Write) : null;
        var folders = s is not null && (s.IsReady || s.Folders.Count > 0)
            ? new SharedFolders(s.Folders, s.Limits, ProtectedPaths(settingsPath))
            : null;
        var context = new ToolContext(load, reader, folders, log);
        log?.Write($"serve started; settings {settingsPath}; database {(reader is null ? "not set up" : "set up")}; " +
                   $"{s?.Folders.Count ?? 0} shared folder(s)" + (load.Problem is null ? "" : $"; problem: {load.Problem}"));
        return context;
    }

    /// <summary>What the folder tools must never show: the settings file (it holds the password), this program's
    /// folder, the launcher's folder (where the settings file lives) and MakinoMCP's own local folder.</summary>
    public static IReadOnlyList<string> ProtectedPaths(string settingsPath)
    {
        string full;
        try { full = Path.GetFullPath(settingsPath); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { full = settingsPath; }
        var paths = new List<string> { full, AppContext.BaseDirectory, FileLog.LocalAppDataFolder() };
        if (Path.GetDirectoryName(full) is { Length: > 0 } folder) paths.Add(folder);
        return paths;
    }

    /// <summary>Runs a database read with the database budget; not set up, failures and timeouts become error JSON.</summary>
    internal Task<string> Database<T>(string tool, Func<IMakinoReader, CancellationToken, Task<T>> read, CancellationToken ct)
    {
        if (Reader is not { } reader) return Task.FromResult(Refuse(tool, SetupProblem ?? NotSetUp));
        var budget = DatabaseBudget;
        return Run(tool, budget, token => read(reader, token),
            () => $"the cell database didn't answer within {Seconds(budget)} seconds",
            e => IsBadArgument(e) ? e.Message : ErrorText.ForClaude(e, Login),
            ct);
    }

    /// <summary>Runs a (synchronous) folder call on a worker with the folder budget; stops waiting at the deadline.</summary>
    internal Task<string> Folder<T>(string tool, string? folder, Func<SharedFolders, T> call, CancellationToken ct)
    {
        if (Folders is not { } folders) return Task.FromResult(Refuse(tool, SetupProblem ?? NotSetUp));
        var budget = FolderBudget;
        return Run(tool, budget, _ => Task.Run(() => call(folders), CancellationToken.None),
            () => folder is null
                ? $"the shared folders didn't answer within {Seconds(budget)} seconds — one may be offline"
                : $"the folder '{folder.Trim()}' didn't answer within {Seconds(budget)} seconds — it may be offline",
            // SharedFolders turns every expected problem into a FolderAccessException; anything else is a surprise.
            e => e is FolderAccessException ? e.Message
                : e is OperationCanceledException ? "the request was cancelled"
                : $"Could not read the shared folder{(folder is null ? "s" : $" '{folder.Trim()}'")}: unexpected error ({e.GetType().Name}: {Short(e.Message)})",
            ct);
    }

    private async Task<string> Run<T>(
        string tool, TimeSpan budget, Func<CancellationToken, Task<T>> work, Func<string> timedOut,
        Func<Exception, string> failed, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        string? error = null;
        string json;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(budget);
        try
        {
            // WaitAsync: stop waiting at the deadline even if the work ignores its token.
            var result = await work(deadline.Token).WaitAsync(deadline.Token);
            json = JsonSerializer.Serialize(result, Json);
        }
        catch (Exception e)
        {
            // Past the deadline every failure (often the cancellation itself) is the timeout.
            error = deadline.IsCancellationRequested && !ct.IsCancellationRequested ? timedOut() : failed(e);
            json = ErrorJson(error);
        }
        Log?.ToolCall(tool, watch.Elapsed, error);
        return json;
    }

    private string Refuse(string tool, string message)
    {
        Log?.ToolCall(tool, TimeSpan.Zero, message);
        return ErrorJson(message);
    }

    private MakinoLogin Login => Settings.Settings?.Makino ?? new MakinoLogin("", "", false, "", "");

    // The reader's "No device named 'X'. Devices on this cell: …", "No filter named 'X'. Use one of: …" and "Give a part
    // name …" (any other
    // ArgumentException, such as a bad connection string, goes through ErrorText, which scrubs it).
    private static bool IsBadArgument(Exception e) =>
        e.GetType() == typeof(ArgumentException)
        && (e.Message.StartsWith("No device named ", StringComparison.Ordinal)
            || e.Message.StartsWith("No filter named ", StringComparison.Ordinal)
            || e.Message == SqlMakinoReader.NoPartGiven);

    private static string Short(string message)
    {
        var line = message.ReplaceLineEndings(" ").Trim();
        return line.Length > 300 ? line[..300] + "…" : line;
    }

    private static string Seconds(TimeSpan t) => t.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture);

    private static string ErrorJson(string message) => JsonSerializer.Serialize(new { error = message }, Json);
}
