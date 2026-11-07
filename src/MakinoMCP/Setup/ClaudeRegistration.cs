using System.Globalization;
using System.Text.RegularExpressions;

namespace MakinoMCP.Setup;

public enum RegistrationState { On, Off, Unknown }

/// <summary>Whether Claude Code has a user-scope <c>makino</c> server. <paramref name="Command"/> and
/// <paramref name="Args"/> are known when <c>claude mcp get</c> showed them; <paramref name="OtherScope"/> names a
/// project-only entry (Local or Project) seen instead; <paramref name="Problem"/> says why the state is Unknown.</summary>
public sealed record ClaudeStatus(
    RegistrationState State, string? Command = null, string? Args = null, string? OtherScope = null, string? Problem = null)
{
    /// <summary>True when the entry starts this launcher in serve mode.</summary>
    public bool Matches(string launcher) =>
        State == RegistrationState.On
        && string.Equals(Command, launcher, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Args?.Trim(), "serve", StringComparison.OrdinalIgnoreCase);
}

/// <summary>The result of one change: whether it worked, and the words to show when it didn't.</summary>
public sealed record StepResult(bool Ok, string Message)
{
    public static readonly StepResult Done = new(true, "");
}

/// <summary>Adds MakinoMCP to Claude Code for this Windows user, removes it, and reads whether it is there, through
/// the <c>claude</c> command line (as the install snippet does). Every call has a 30 second limit.</summary>
public sealed partial class ClaudeRegistration(ICommandRunner runner, string claudePath)
{
    public const string ServerName = "makino";
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly string TimeoutText =
        $"Claude Code didn't answer within {Timeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} seconds.";

    public static readonly IReadOnlyList<string> GetArgs = ["mcp", "get", ServerName];
    public static readonly IReadOnlyList<string> ListArgs = ["mcp", "list"];
    public static readonly IReadOnlyList<string> RemoveArgs = ["mcp", "remove", ServerName, "--scope", "user"];

    /// <summary>The same command as <see cref="InstallSnippets"/>, as separate arguments.</summary>
    public static IReadOnlyList<string> AddArgs(string launcher) =>
        ["mcp", "add", "--scope", "user", ServerName, "--", launcher, "serve"];

    // ------------------------------------------------------------------ finding claude

    /// <summary>The <c>claude</c> this Windows user would run from a terminal: each PATH folder in order, and in
    /// each folder the PATHEXT extensions in order (so claude.exe before an npm claude.cmd beside it). Only full
    /// paths are searched (never the current or the app's folder), and only .exe, .cmd and .bat count.</summary>
    public static string? Find(string? path, string? pathExt)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var extensions = (string.IsNullOrWhiteSpace(pathExt) ? ".COM;.EXE;.BAT;.CMD" : pathExt)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(e => e.ToLowerInvariant() is ".exe" or ".cmd" or ".bat")
            .ToList();

        foreach (var entry in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var folder = entry.Trim('"');
            if (folder.Length == 0 || !Path.IsPathFullyQualified(folder)) continue;
            foreach (var ext in extensions)
            {
                try
                {
                    var candidate = Path.Combine(folder, "claude" + ext.ToLowerInvariant());
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { /* a PATH entry with characters a path can't have */ }
            }
        }
        return null;
    }

    /// <summary><see cref="Find"/> with this process's PATH and PATHEXT.</summary>
    public static string? FindForThisUser() =>
        Find(Environment.GetEnvironmentVariable("PATH"), Environment.GetEnvironmentVariable("PATHEXT"));

    // ------------------------------------------------------------------ reading the state

    public async Task<ClaudeStatus> GetStatusAsync(CancellationToken ct)
    {
        var get = await RunAsync(GetArgs, ct);
        if (get.Problem is { } problem) return new ClaudeStatus(RegistrationState.Unknown, Problem: problem);

        var status = ParseGet(get.Result!.AllText);
        if (status.State != RegistrationState.Unknown) return status;

        // An answer get doesn't recognise (a newer Claude Code?): list shows every server by name.
        var list = await RunAsync(ListArgs, ct);
        if (list.Problem is null && ParseList(list.Result!.AllText) is { } listed
            && (listed.State != RegistrationState.Unknown || listed.Problem is not null)) return listed;
        return new ClaudeStatus(RegistrationState.Unknown,
            Problem: $"Claude Code gave an unexpected answer: {FirstLine(get.Result!.AllText) ?? "nothing"}");
    }

    /// <summary>Reads <c>claude mcp get makino</c>. The block names the scope; the Status line (connected or
    /// not) doesn't matter, the entry is there either way.</summary>
    public static ClaudeStatus ParseGet(string output)
    {
        var lines = Lines(output);
        if (lines.Any(l => MissingLine().IsMatch(l))) return new ClaudeStatus(RegistrationState.Off);

        var header = lines.FindIndex(l => l.TrimEnd() == ServerName + ":");
        if (header < 0) return new ClaudeStatus(RegistrationState.Unknown);

        string? scope = null, command = null, args = null;
        foreach (var line in lines.Skip(header + 1))
        {
            if (line.Length > 0 && !char.IsWhiteSpace(line[0])) break; // the end of the indented block
            if (Field().Match(line) is not { Success: true } m) continue;
            var value = m.Groups["value"].Value.Trim();
            switch (m.Groups["name"].Value)
            {
                case "Scope": scope = value.Split(' ', 2)[0]; break;
                case "Command": command = value; break;
                case "Args": args = value; break;
            }
        }

        return scope switch
        {
            "User" => new ClaudeStatus(RegistrationState.On, command, args),
            "Local" or "Project" => new ClaudeStatus(RegistrationState.Off, OtherScope: scope),
            _ => new ClaudeStatus(RegistrationState.Unknown),
        };
    }

    /// <summary>Reads <c>claude mcp list</c>: one "name: command - status" line per server, any scope.</summary>
    public static ClaudeStatus ParseList(string output)
    {
        var lines = Lines(output);
        // list doesn't show the scope: a makino line may be a project-only entry, so the state stays unknown.
        if (lines.Any(l => l.StartsWith(ServerName + ": ", StringComparison.Ordinal)))
            return new ClaudeStatus(RegistrationState.Unknown,
                Problem: "Claude Code lists a MakinoMCP entry but didn't say whether it is for all your projects.");
        if (lines.Any(l => l.StartsWith("No MCP servers configured", StringComparison.Ordinal)
                || l.StartsWith("Checking MCP server health", StringComparison.Ordinal)))
            return new ClaudeStatus(RegistrationState.Off);
        return new ClaudeStatus(RegistrationState.Unknown);
    }

    // ------------------------------------------------------------------ changing it

    /// <summary>Adds the user-scope entry for <paramref name="launcher"/>; an entry for another path is replaced,
    /// one for this path is left as it is. <paramref name="current"/> is the state already read (read again only when
    /// it is missing or Unknown). Only that read can be cancelled: a change, once started, runs to its end (or the
    /// 30 second limit), so Claude Code is never left half-written.</summary>
    public async Task<StepResult> TurnOnAsync(string launcher, ClaudeStatus? current, CancellationToken ct)
    {
        var status = current is { State: not RegistrationState.Unknown } ? current : await GetStatusAsync(ct);
        if (status.State == RegistrationState.Unknown) return new StepResult(false, $"Couldn't check Claude Code: {status.Problem}");
        if (status.Matches(launcher)) return StepResult.Done;
        if (status.State == RegistrationState.On)
        {
            var removed = await RemoveAsync();
            if (!removed.Ok) return removed;
        }

        var add = await RunAsync(AddArgs(launcher), CancellationToken.None);
        string? failure = add.Problem
            ?? (add.Result!.ExitCode == 0 ? null : FirstLine(add.Result.AllText) ?? $"it stopped with code {add.Result.ExitCode}.");
        if (failure is null) return StepResult.Done;
        var message = $"Claude Code couldn't add MakinoMCP: {failure}";
        // The old entry was removed to make room: put it back, so Claude keeps working the way it did.
        if (status.State == RegistrationState.On && !string.IsNullOrEmpty(status.Command))
        {
            var back = await RunAsync(AddArgs(status.Command), CancellationToken.None);
            message += back.Problem is null && back.Result!.ExitCode == 0
                ? $" It still starts MakinoMCP from {status.Command}."
                : $" Its earlier entry ({status.Command}) couldn't be put back either: turn the switch on again.";
        }
        return new StepResult(false, message);
    }

    /// <summary>Removes the user-scope entry; nothing to do when <paramref name="current"/> already says Off. Never
    /// cancelled half way (<paramref name="ct"/> is not passed on).</summary>
    public Task<StepResult> TurnOffAsync(ClaudeStatus? current, CancellationToken ct) =>
        current?.State == RegistrationState.Off ? Task.FromResult(StepResult.Done) : RemoveAsync();

    private async Task<StepResult> RemoveAsync()
    {
        var remove = await RunAsync(RemoveArgs, CancellationToken.None);
        if (remove.Problem is { } problem) return new StepResult(false, $"Claude Code couldn't remove MakinoMCP: {problem}");
        var r = remove.Result!;
        // Already gone counts as removed.
        if (r.ExitCode == 0 || Lines(r.AllText).Any(l => MissingLine().IsMatch(l))) return StepResult.Done;
        return new StepResult(false, $"Claude Code couldn't remove MakinoMCP: {FirstLine(r.AllText) ?? $"it stopped with code {r.ExitCode}."}");
    }

    // ------------------------------------------------------------------ helpers

    private async Task<(CommandResult? Result, string? Problem)> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        // An npm install runs through cmd.exe, which would change some names even inside quotes: refuse them here.
        if (CommandLine.RunsThroughCmd(claudePath) && (CommandLine.ChangedByCmd(claudePath) || args.Any(CommandLine.ChangedByCmd)))
            return (null, new CmdArgumentException().Message);
        try
        {
            var r = await runner.RunAsync(claudePath, args, Timeout, ct);
            return r.TimedOut ? (null, TimeoutText) : (r, null);
        }
        catch (CmdArgumentException e)
        {
            return (null, e.Message);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException
            or UnauthorizedAccessException)
        {
            return (null, $"Couldn't run Claude Code: {e.Message}");
        }
    }

    private static List<string> Lines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();

    /// <summary>The first line with words in it, at most 200 characters: enough to say what went wrong, never a
    /// stack trace.</summary>
    private static string? FirstLine(string text)
    {
        var line = Lines(text).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        return line is { Length: > 200 } ? line[..200] + "…" : line;
    }

    [GeneratedRegex("""^No MCP server named "makino"(\.| |$)""")]
    private static partial Regex MissingLine();

    [GeneratedRegex(@"^\s+(?<name>Scope|Command|Args):\s?(?<value>.*)$")]
    private static partial Regex Field();
}
