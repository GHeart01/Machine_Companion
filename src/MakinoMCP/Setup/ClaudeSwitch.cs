namespace MakinoMCP.Setup;

/// <summary>What the setup window knows when it shows the switch. <paramref name="Claude"/> is null when Claude Code
/// isn't installed; <paramref name="CoworkFolder"/> says Claude desktop (Cowork) is installed for this user.</summary>
public sealed record SwitchFacts(ClaudeStatus? Claude, bool CoworkFolder, bool CoworkEntry, bool SettingsSaved, string Launcher);

/// <summary>How the switch looks: whether it can be used, its state, the status line under it and an optional note.</summary>
public sealed record SwitchView(bool Enabled, bool Checked, StatusLine Status, string? Note);

/// <summary>The "Use MakinoMCP in Claude on this PC" switch: what it shows, and what turning it on or off does.
/// On adds MakinoMCP to Claude Code (user scope) and to every Cowork config; Off removes both and stops the servers
/// running for this user. Claude Code decides the state; without it, the switch works on Cowork alone.</summary>
public sealed class ClaudeSwitch(
    ClaudeRegistration? claude, IReadOnlyList<string> coworkFiles, Func<StopResult> stopServers, Func<DateTime> now)
{
    public const string OffText = "Off — Claude won't start MakinoMCP on this PC.";
    public const string RestartText = "Restart Claude to see the MakinoMCP tools.";
    public const string NotInstalledNote = "Claude Code isn't installed on this PC — use the install lines below.";

    public static SwitchView Describe(SwitchFacts f)
    {
        if (f.Claude is null)
        {
            if (!f.CoworkFolder) return new SwitchView(false, false, new StatusLine(StatusKind.Info, OffText), NotInstalledNote);
            var note = NotInstalledNote + " The switch sets up Cowork (Claude desktop) only.";
            return f.CoworkEntry
                ? new SwitchView(true, true, new StatusLine(StatusKind.Success, "On — Cowork will start MakinoMCP."), note)
                : Off(f, note);
        }

        var c = f.Claude;
        switch (c.State)
        {
            case RegistrationState.Unknown:
                return new SwitchView(false, false, new StatusLine(StatusKind.Error, $"Couldn't check Claude Code: {c.Problem}"), null);
            case RegistrationState.On when c.Matches(f.Launcher) || c.Command is null:
                return new SwitchView(true, true, new StatusLine(StatusKind.Success, "On — Claude Code will start MakinoMCP."), null);
            case RegistrationState.On:
                return new SwitchView(true, true, new StatusLine(StatusKind.Warning,
                    $"On, but Claude Code starts MakinoMCP from {c.Command}. Turn this off and on again to use this copy."), null);
            default:
                return Off(f, null, c.OtherScope is null ? "" : " Claude Code has a MakinoMCP entry for one project only.");
        }
    }

    private static SwitchView Off(SwitchFacts f, string? note, string extra = "") => f.SettingsSaved
        ? new SwitchView(true, false, new StatusLine(StatusKind.Info, OffText + extra), note)
        : new SwitchView(false, false, new StatusLine(StatusKind.Info, "Off. Save first, then turn this on." + extra), note);

    // ------------------------------------------------------------------ turning on and off

    public async Task<StatusLine> TurnOnAsync(string launcher, CancellationToken ct)
    {
        if (claude is not null)
        {
            var added = await claude.TurnOnAsync(launcher, ct);
            if (!added.Ok) return new StatusLine(StatusKind.Error, added.Message);
        }
        var problems = await Task.Run(() => Problems(coworkFiles.Select(f => CoworkConfig.AddToFile(f, launcher, now()))), ct);

        var who = claude is not null ? "Claude Code" : "Cowork";
        var text = $"On — {who} will start MakinoMCP. {RestartText}";
        return problems.Count == 0
            ? new StatusLine(StatusKind.Success, text)
            : new StatusLine(StatusKind.Warning, $"{text} {string.Join(" ", problems)}");
    }

    public async Task<StatusLine> TurnOffAsync(CancellationToken ct)
    {
        if (claude is not null)
        {
            var removed = await claude.TurnOffAsync(ct);
            if (!removed.Ok) return new StatusLine(StatusKind.Error, removed.Message);
        }
        var (problems, stop) = await Task.Run(() =>
        {
            var p = Problems(coworkFiles.Select(f => CoworkConfig.RemoveFromFile(f, now())));
            return (p, stopServers());
        }, ct);

        var text = OffText;
        if (stop.Stopped > 0) text += $" Stopped {Processes(stop.Stopped)}.";
        if (stop.Failed > 0) problems.Add($"Couldn't stop {Processes(stop.Failed)}: close Claude to stop {(stop.Failed == 1 ? "it" : "them")}.");
        if (stop.Problem is { } p) problems.Add(p);
        return problems.Count == 0
            ? new StatusLine(StatusKind.Success, text)
            : new StatusLine(StatusKind.Warning, $"{text} {string.Join(" ", problems)}");
    }

    private static string Processes(int n) => n == 1 ? "1 running MakinoMCP process" : $"{n} running MakinoMCP processes";

    private static List<string> Problems(IEnumerable<FileOutcome> outcomes) =>
        outcomes.Where(o => o.Change is FileChange.Invalid or FileChange.Failed)
            .Select(o => o.Problem!)
            .ToList();
}
