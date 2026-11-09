using MakinoMCP.Setup;
using Xunit;

namespace MakinoMCP.Tests;

public class ClaudeSwitchTests
{
    private const string Launcher = @"\\server\Tools\MakinoMCP\MakinoMCP.exe";

    private static SwitchFacts Facts(ClaudeStatus? claude, bool coworkFolder = false, bool coworkEntry = false, bool saved = true) =>
        new(claude, coworkFolder, coworkEntry, saved, Launcher);

    private static ClaudeStatus On(string command = Launcher) => new(RegistrationState.On, command, "serve");

    [Fact]
    public void On_when_claude_code_has_this_launcher()
    {
        var v = ClaudeSwitch.Describe(Facts(On()));
        Assert.True(v.Enabled);
        Assert.True(v.Checked);
        Assert.Equal(new StatusLine(StatusKind.Success, "On — Claude Code will start MakinoMCP."), v.Status);
        Assert.Null(v.Note);
    }

    [Fact]
    public void On_but_another_copy_warns()
    {
        var v = ClaudeSwitch.Describe(Facts(On(@"C:\Old\MakinoMCP.exe")));
        Assert.True(v.Checked);
        Assert.Equal(StatusKind.Warning, v.Status.Kind);
        Assert.Contains(@"C:\Old\MakinoMCP.exe", v.Status.Text);
    }

    [Fact]
    public void Off()
    {
        var v = ClaudeSwitch.Describe(Facts(new ClaudeStatus(RegistrationState.Off)));
        Assert.True(v.Enabled);
        Assert.False(v.Checked);
        Assert.Equal(new StatusLine(StatusKind.Info, "Off — Claude won't start MakinoMCP on this PC."), v.Status);
    }

    [Fact]
    public void Off_mentions_a_project_only_entry()
    {
        var v = ClaudeSwitch.Describe(Facts(new ClaudeStatus(RegistrationState.Off, OtherScope: "Local")));
        Assert.False(v.Checked);
        Assert.Contains("one project", v.Status.Text);
    }

    [Fact]
    public void Off_and_not_saved_cannot_be_turned_on_yet()
    {
        var v = ClaudeSwitch.Describe(Facts(new ClaudeStatus(RegistrationState.Off), saved: false));
        Assert.False(v.Enabled);
        Assert.False(v.Checked);
        Assert.Contains("Save first", v.Status.Text);
    }

    [Fact]
    public void On_and_not_saved_can_still_be_turned_off()
    {
        var v = ClaudeSwitch.Describe(Facts(On(), saved: false));
        Assert.True(v.Enabled);
        Assert.True(v.Checked);
    }

    [Fact]
    public void Unknown_disables_the_switch_and_says_why()
    {
        var v = ClaudeSwitch.Describe(Facts(new ClaudeStatus(RegistrationState.Unknown, Problem: "Claude Code didn't answer within 30 seconds.")));
        Assert.False(v.Enabled);
        Assert.Equal(new StatusLine(StatusKind.Error, "Couldn't check Claude Code: Claude Code didn't answer within 30 seconds."), v.Status);
    }

    [Fact]
    public void No_claude_code_and_no_cowork_is_disabled_with_a_note()
    {
        var v = ClaudeSwitch.Describe(Facts(null));
        Assert.False(v.Enabled);
        Assert.False(v.Checked);
        Assert.Equal("Claude Code isn't installed on this PC — use the install lines below.", v.Note);
    }

    [Fact]
    public void No_claude_code_but_cowork_switches_cowork_only()
    {
        var off = ClaudeSwitch.Describe(Facts(null, coworkFolder: true));
        Assert.True(off.Enabled);
        Assert.False(off.Checked);
        Assert.Equal("Claude Code isn't installed on this PC — use the install lines below. " +
                     "The switch sets up Cowork (Claude desktop) only.", off.Note);

        var on = ClaudeSwitch.Describe(Facts(null, coworkFolder: true, coworkEntry: true));
        Assert.True(on.Checked);
        Assert.Equal(new StatusLine(StatusKind.Success, "On — Cowork will start MakinoMCP."), on.Status);
    }

    [Fact]
    public void Says_when_claude_code_and_cowork_disagree()
    {
        var onWithout = ClaudeSwitch.Describe(Facts(On(), coworkFolder: true, coworkEntry: false));
        Assert.True(onWithout.Checked);
        Assert.Equal(new StatusLine(StatusKind.Success,
            "On — Claude Code will start MakinoMCP. Cowork doesn't have it: turn this off and on again to add it."), onWithout.Status);

        var offWith = ClaudeSwitch.Describe(Facts(new ClaudeStatus(RegistrationState.Off), coworkFolder: true, coworkEntry: true));
        Assert.False(offWith.Checked);
        Assert.Equal(new StatusLine(StatusKind.Info,
            "Off — Claude won't start MakinoMCP on this PC. Cowork still has it: turn this on and off again to remove it."), offWith.Status);

        // In step: nothing added.
        Assert.Equal("On — Claude Code will start MakinoMCP.",
            ClaudeSwitch.Describe(Facts(On(), coworkFolder: true, coworkEntry: true)).Status.Text);
        Assert.Equal(ClaudeSwitch.OffText,
            ClaudeSwitch.Describe(Facts(new ClaudeStatus(RegistrationState.Off), coworkFolder: true)).Status.Text);
    }

    // ------------------------------------------------------------------ after a change

    private static readonly StatusLine TurnedOn = new(StatusKind.Success, "On — Claude Code will start MakinoMCP. Restart Claude to see the MakinoMCP tools.");
    private static readonly StatusLine TurnedOff = new(StatusKind.Success, "Off — Claude won't start MakinoMCP on this PC.");
    private static readonly ClaudeStatus Unreadable = new(RegistrationState.Unknown, Problem: "Claude Code didn't answer within 30 seconds.");

    [Fact]
    public void After_a_change_the_outcome_is_shown_with_the_fresh_state()
    {
        var v = ClaudeSwitch.DescribeAfterChange(Facts(On()), TurnedOn, turnedOn: true, wasChecked: false);
        Assert.True(v.Checked);
        Assert.True(v.Enabled);
        Assert.Equal(TurnedOn, v.Status);
    }

    [Fact]
    public void After_a_change_that_cannot_be_checked_again_the_box_follows_the_outcome()
    {
        var on = ClaudeSwitch.DescribeAfterChange(Facts(Unreadable), TurnedOn, turnedOn: true, wasChecked: false);
        Assert.True(on.Checked);
        Assert.True(on.Enabled);
        Assert.Equal(new StatusLine(StatusKind.Warning,
            "On — Claude Code will start MakinoMCP. Restart Claude to see the MakinoMCP tools. " +
            "Couldn't check again: Claude Code didn't answer within 30 seconds."), on.Status);

        var off = ClaudeSwitch.DescribeAfterChange(Facts(Unreadable), TurnedOff, turnedOn: false, wasChecked: true);
        Assert.False(off.Checked);
    }

    [Fact]
    public void A_failed_change_that_cannot_be_checked_keeps_the_old_state()
    {
        var failed = new StatusLine(StatusKind.Error, "Claude Code couldn't add MakinoMCP: Error: permission denied");
        var v = ClaudeSwitch.DescribeAfterChange(Facts(Unreadable), failed, turnedOn: true, wasChecked: false);
        Assert.False(v.Checked);
        Assert.Equal(new StatusLine(StatusKind.Error,
            "Claude Code couldn't add MakinoMCP: Error: permission denied. Couldn't check again: Claude Code didn't answer within 30 seconds."),
            v.Status);
    }

    // ------------------------------------------------------------------ turning on and off

    private static readonly DateTime Now = new(2026, 11, 3, 14, 5, 9);
    private const string Claude = @"C:\Tools\claude.exe";
    private static readonly ClaudeStatus OffStatus = new(RegistrationState.Off);

    private sealed class Cowork : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "makinomcp-switch-" + Guid.NewGuid().ToString("N"));
        public string File => Path.Combine(Dir, CoworkConfig.FileName);
        public Cowork(string? content = null)
        {
            Directory.CreateDirectory(Dir);
            if (content != null) System.IO.File.WriteAllText(File, content);
        }
        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task TurnOn_adds_to_claude_code_and_cowork_then_asks_for_a_restart()
    {
        using var cowork = new Cowork("{}");
        var runner = new FakeRunner().Then(0, "Added stdio MCP server makino\n");
        var stops = 0;
        var sw = new ClaudeSwitch(new ClaudeRegistration(runner, Claude), [cowork.File], () => { stops++; return new StopResult(0, 0); }, () => Now);

        var line = await sw.TurnOnAsync(Launcher, OffStatus, CancellationToken.None);

        Assert.Equal(new StatusLine(StatusKind.Success, "On — Claude Code will start MakinoMCP. Restart Claude to see the MakinoMCP tools."), line);
        Assert.True(CoworkConfig.HasEntry(System.IO.File.ReadAllText(cowork.File)));
        Assert.Equal(0, stops);
    }

    [Fact]
    public async Task TurnOn_says_when_a_cowork_file_was_left_alone()
    {
        using var cowork = new Cowork("{ broken");
        var runner = new FakeRunner().Then(0, "Added\n");
        var sw = new ClaudeSwitch(new ClaudeRegistration(runner, Claude), [cowork.File], () => new StopResult(0, 0), () => Now);

        var line = await sw.TurnOnAsync(Launcher, OffStatus, CancellationToken.None);

        Assert.Equal(StatusKind.Warning, line.Kind);
        Assert.Contains(cowork.File, line.Text);
        Assert.Contains("isn't valid JSON", line.Text);
        Assert.Contains("Restart Claude to see the MakinoMCP tools.", line.Text);
        Assert.Equal("{ broken", System.IO.File.ReadAllText(cowork.File));
    }

    [Fact]
    public async Task TurnOn_cowork_only_without_claude_code()
    {
        using var cowork = new Cowork();
        var sw = new ClaudeSwitch(null, [cowork.File], () => new StopResult(0, 0), () => Now);

        var line = await sw.TurnOnAsync(Launcher, OffStatus, CancellationToken.None);

        Assert.Equal(new StatusLine(StatusKind.Success, "On — Cowork will start MakinoMCP. Restart Claude to see the MakinoMCP tools."), line);
        Assert.True(CoworkConfig.HasEntry(System.IO.File.ReadAllText(cowork.File)));
    }

    [Fact]
    public async Task TurnOff_removes_everywhere_then_stops_the_servers()
    {
        using var cowork = new Cowork(CoworkConfig.Add("""{ "mcpServers": { "other": { "command": "x" } } }""", Launcher));
        var runner = new FakeRunner().Then(0, "Removed MCP server makino from user config\n");
        var stops = 0;
        var sw = new ClaudeSwitch(new ClaudeRegistration(runner, Claude), [cowork.File], () => { stops++; return new StopResult(2, 0); }, () => Now);

        var line = await sw.TurnOffAsync(null, CancellationToken.None);

        Assert.Equal(new StatusLine(StatusKind.Success, "Off — Claude won't start MakinoMCP on this PC. Stopped 2 running MakinoMCP processes."), line);
        Assert.Equal(1, stops);
        var after = System.IO.File.ReadAllText(cowork.File);
        Assert.False(CoworkConfig.HasEntry(after));
        Assert.Contains("other", after);
    }

    [Fact]
    public async Task TurnOff_stops_nothing_when_claude_code_refuses()
    {
        using var cowork = new Cowork("{}");
        var runner = new FakeRunner().Then(1, "", "Error: permission denied\n");
        var stops = 0;
        var sw = new ClaudeSwitch(new ClaudeRegistration(runner, Claude), [cowork.File], () => { stops++; return new StopResult(0, 0); }, () => Now);

        var line = await sw.TurnOffAsync(null, CancellationToken.None);

        Assert.Equal(new StatusLine(StatusKind.Error, "Claude Code couldn't remove MakinoMCP: Error: permission denied"), line);
        Assert.Equal(0, stops);
    }

    [Fact]
    public async Task TurnOff_reports_processes_it_could_not_stop()
    {
        var sw = new ClaudeSwitch(null, [], () => new StopResult(0, 1), () => Now);
        var line = await sw.TurnOffAsync(null, CancellationToken.None);
        Assert.Equal(StatusKind.Warning, line.Kind);
        Assert.Contains("Couldn't stop 1 running MakinoMCP process", line.Text);
    }

    [Fact]
    public async Task A_cancelled_token_never_stops_a_change_half_way()
    {
        using var cowork = new Cowork("{}");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var runner = new FakeRunner().Then(0, "Added\n").Then(0, "Removed\n");
        var stops = 0;
        var sw = new ClaudeSwitch(new ClaudeRegistration(runner, Claude), [cowork.File], () => { stops++; return new StopResult(1, 0); }, () => Now);

        var on = await sw.TurnOnAsync(Launcher, OffStatus, cancelled.Token);
        Assert.Equal(StatusKind.Success, on.Kind);
        Assert.True(CoworkConfig.HasEntry(System.IO.File.ReadAllText(cowork.File)));

        var off = await sw.TurnOffAsync(null, cancelled.Token);
        Assert.Equal(StatusKind.Success, off.Kind);
        Assert.False(CoworkConfig.HasEntry(System.IO.File.ReadAllText(cowork.File)));
        Assert.Equal(1, stops);
    }

    [Fact]
    public void Every_status_has_words()
    {
        foreach (var facts in new[]
                 {
                     Facts(On()), Facts(On("x")), Facts(new ClaudeStatus(RegistrationState.Off)),
                     Facts(new ClaudeStatus(RegistrationState.Unknown, Problem: "p")), Facts(null), Facts(null, true),
                 })
        {
            Assert.False(string.IsNullOrWhiteSpace(ClaudeSwitch.Describe(facts).Status.Text));
        }
    }
}
