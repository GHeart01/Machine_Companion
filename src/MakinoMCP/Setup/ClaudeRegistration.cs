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

    /// <summary>The <c>claude</c> on this PC's PATH (claude.exe, or claude.cmd from npm), or null.</summary>
    public static async Task<string?> FindAsync(ICommandRunner runner, CancellationToken ct)
    {
        try
        {
            var r = await runner.RunAsync("where.exe", ["claude"], Timeout, ct);
            return r.TimedOut || r.ExitCode != 0 ? null : PickClaude(r.Output);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    /// <summary>The first line of where.exe's answer that Windows can run directly.</summary>
    public static string? PickClaude(string whereOutput) =>
        Lines(whereOutput)
            .Select(l => l.Trim())
            .FirstOrDefault(l => Path.IsPathFullyQualified(l)
                && Path.GetExtension(l).ToLowerInvariant() is ".exe" or ".cmd" or ".bat");

    // ------------------------------------------------------------------ reading the state

    public async Task<ClaudeStatus> GetStatusAsync(CancellationToken ct)
    {
        var get = await RunAsync(GetArgs, ct);
        if (get.Problem is { } problem) return new ClaudeStatus(RegistrationState.Unknown, Problem: problem);

        var status = ParseGet(get.Result!.AllText);
        if (status.State != RegistrationState.Unknown) return status;

        // An answer get doesn't recognise (a newer Claude Code?): list shows every server by name.
        var list = await RunAsync(ListArgs, ct);
        if (list.Problem is null && ParseList(list.Result!.AllText) is { State: not RegistrationState.Unknown } listed) return listed;
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
        if (lines.Any(l => l.StartsWith(ServerName + ": ", StringComparison.Ordinal))) return new ClaudeStatus(RegistrationState.On);
        if (lines.Any(l => l.StartsWith("No MCP servers configured", StringComparison.Ordinal)
                || l.StartsWith("Checking MCP server health", StringComparison.Ordinal)))
            return new ClaudeStatus(RegistrationState.Off);
        return new ClaudeStatus(RegistrationState.Unknown);
    }

    // ------------------------------------------------------------------ changing it

    /// <summary>Adds the user-scope entry for <paramref name="launcher"/>; an entry for another path is replaced,
    /// one for this path is left as it is.</summary>
    public async Task<StepResult> TurnOnAsync(string launcher, CancellationToken ct)
    {
        var status = await GetStatusAsync(ct);
        if (status.State == RegistrationState.Unknown) return new StepResult(false, $"Couldn't check Claude Code: {status.Problem}");
        if (status.Matches(launcher)) return StepResult.Done;
        if (status.State == RegistrationState.On)
        {
            var removed = await RemoveAsync(ct);
            if (!removed.Ok) return removed;
        }

        var add = await RunAsync(AddArgs(launcher), ct);
        if (add.Problem is { } problem) return new StepResult(false, $"Claude Code couldn't add MakinoMCP: {problem}");
        return add.Result!.ExitCode == 0
            ? StepResult.Done
            : new StepResult(false, $"Claude Code couldn't add MakinoMCP: {FirstLine(add.Result.AllText) ?? $"it stopped with code {add.Result.ExitCode}."}");
    }

    public Task<StepResult> TurnOffAsync(CancellationToken ct) => RemoveAsync(ct);

    private async Task<StepResult> RemoveAsync(CancellationToken ct)
    {
        var remove = await RunAsync(RemoveArgs, ct);
        if (remove.Problem is { } problem) return new StepResult(false, $"Claude Code couldn't remove MakinoMCP: {problem}");
        var r = remove.Result!;
        // Already gone counts as removed.
        if (r.ExitCode == 0 || Lines(r.AllText).Any(l => MissingLine().IsMatch(l))) return StepResult.Done;
        return new StepResult(false, $"Claude Code couldn't remove MakinoMCP: {FirstLine(r.AllText) ?? $"it stopped with code {r.ExitCode}."}");
    }

    // ------------------------------------------------------------------ helpers

    private async Task<(CommandResult? Result, string? Problem)> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        try
        {
            var r = await runner.RunAsync(claudePath, args, Timeout, ct);
            return r.TimedOut ? (null, TimeoutText) : (r, null);
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
