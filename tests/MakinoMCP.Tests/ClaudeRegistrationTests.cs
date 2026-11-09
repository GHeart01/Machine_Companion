using MakinoMCP.Setup;
using Xunit;

namespace MakinoMCP.Tests;

/// <summary>Records every call and answers from a script: never runs the real claude CLI.</summary>
internal sealed class FakeRunner : ICommandRunner
{
    private readonly Queue<CommandResult> _answers = new();
    public List<(string Program, IReadOnlyList<string> Args, CancellationToken Ct)> Calls { get; } = [];

    public FakeRunner Then(int exitCode, string output = "", string error = "", bool timedOut = false)
    {
        _answers.Enqueue(new CommandResult(exitCode, output, error, timedOut));
        return this;
    }

    public Task<CommandResult> RunAsync(string program, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        Calls.Add((program, args.ToList(), ct));
        if (_answers.Count == 0) throw new InvalidOperationException($"Unexpected call: {program} {string.Join(' ', args)}");
        return Task.FromResult(_answers.Dequeue());
    }
}

public class ClaudeRegistrationTests
{
    private const string Claude = @"C:\Users\someone\AppData\Local\Programs\claude\claude.exe";
    private const string ShareLauncher = @"\\server\Tools\MakinoMCP\MakinoMCP.exe";
    private static readonly ClaudeStatus Off = new(RegistrationState.Off);
    private const string SpacedLauncher = @"C:\Program Files\X Y\MakinoMCP.exe";

    // Shapes captured from claude mcp get / list (Claude Code 2.x). Paths are sample paths.
    private const string GetUserConnected = """
        makino:
          Scope: User config (available in all your projects)
          Status: ✔ Connected
          Type: stdio
          Command: \\server\Tools\MakinoMCP\MakinoMCP.exe
          Args: serve
          Environment:

        To remove this server, run: claude mcp remove makino -s user
        """;

    private const string GetUserFailed = """
        makino:
          Scope: User config (available in all your projects)
          Status: ✘ Failed to connect
          Issue: CONNECTION_CLOSED: Connection closed
          Type: stdio
          Command: C:\Program Files\X Y\MakinoMCP.exe
          Args: serve
          Environment:

        To remove this server, run: claude mcp remove makino -s user
        """;

    private const string GetLocal = """
        makino:
          Scope: Local config (private to you in this project)
          Status: ✘ Failed to connect
          Issue: CONNECTION_CLOSED: Connection closed
          Type: stdio
          Command: C:\l.exe
          Args: serve
          Environment:

        To remove this server, run: claude mcp remove makino -s local
        """;

    private const string GetMissingOthers =
        "No MCP server named \"makino\". Configured servers: claude.ai Claude Docs, plugin:design:figma, other-server " +
        "(and 14 more — run `claude mcp list` to see all)\n";

    private const string GetMissingNone = "No MCP server named \"makino\". Run `claude mcp add` to add one.\n";

    private const string ListWithMakino = """
        Checking MCP server health…

        other-server: npx -y other-server - ✔ Connected
        makino: \\server\Tools\MakinoMCP\MakinoMCP.exe serve - ✔ Connected
        plugin:design:figma: https://mcp.example.com/mcp (HTTP) - ! Needs authentication
        """;

    private const string ListWithoutMakino = """
        Checking MCP server health…

        other-server: npx -y other-server - ✔ Connected
        makino-two: C:\x.exe serve - ✘ Failed to connect
        """;

    private const string ListEmpty = "No MCP servers configured. Use `claude mcp add` to add a server.\n";

    // ------------------------------------------------------------------ finding claude

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "makinomcp-find-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public string Sub(string name)
        {
            var d = System.IO.Path.Combine(Path, name);
            Directory.CreateDirectory(d);
            return d;
        }
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }

    private const string PathExt = ".COM;.EXE;.BAT;.CMD;.VBS;.JS";

    [Fact]
    public void Find_searches_path_in_order_including_non_ascii_folders()
    {
        using var t = new TempDir();
        var empty = t.Sub("empty");
        var npm = t.Sub("Jos\u00e9 R\u00f8d\\npm");
        var later = t.Sub("later");
        File.WriteAllText(System.IO.Path.Combine(npm, "claude.cmd"), "");
        File.WriteAllText(System.IO.Path.Combine(later, "claude.exe"), "");

        var found = ClaudeRegistration.Find($"{empty};\"{npm}\";{later}", PathExt);

        Assert.Equal(System.IO.Path.Combine(npm, "claude.cmd"), found);
    }

    [Fact]
    public void Find_prefers_exe_over_cmd_in_one_folder_by_pathext_order()
    {
        using var t = new TempDir();
        var dir = t.Sub("both");
        File.WriteAllText(System.IO.Path.Combine(dir, "claude.cmd"), "");
        File.WriteAllText(System.IO.Path.Combine(dir, "claude.exe"), "");
        File.WriteAllText(System.IO.Path.Combine(dir, "claude"), ""); // the npm shell script: not runnable here

        Assert.Equal(System.IO.Path.Combine(dir, "claude.exe"), ClaudeRegistration.Find(dir, PathExt));
        Assert.Equal(System.IO.Path.Combine(dir, "claude.cmd"), ClaudeRegistration.Find(dir, ".CMD;.EXE"));
    }

    [Fact]
    public void Find_ignores_relative_entries_and_other_extensions()
    {
        using var t = new TempDir();
        var dir = t.Sub("scripts");
        File.WriteAllText(System.IO.Path.Combine(dir, "claude.vbs"), "");
        File.WriteAllText(System.IO.Path.Combine(dir, "claude.com"), "");

        Assert.Null(ClaudeRegistration.Find($".;bin;{dir}", PathExt));
        Assert.Null(ClaudeRegistration.Find(null, PathExt));
        Assert.Null(ClaudeRegistration.Find("", null));
    }

    [Fact]
    public void Find_uses_default_extensions_when_pathext_is_missing()
    {
        using var t = new TempDir();
        var dir = t.Sub("d");
        File.WriteAllText(System.IO.Path.Combine(dir, "claude.cmd"), "");
        Assert.Equal(System.IO.Path.Combine(dir, "claude.cmd"), ClaudeRegistration.Find(dir, null));
    }

    [Fact]
    public void FindInClaudeDesktop_takes_the_highest_version_in_either_layout()
    {
        using var t = new TempDir();
        var store = t.Sub(@"Claude-Data\claude_abc");
        var classic = t.Sub(@"Roaming\Claude");
        File.WriteAllText(System.IO.Path.Combine(t.Sub(@"Claude-Data\claude_abc\claude-code\2.1.309\9078b9"), "claude.exe"), "");
        File.WriteAllText(System.IO.Path.Combine(t.Sub(@"Claude-Data\claude_abc\claude-code\2.1.317\a33aa5"), "claude.exe"), "");
        File.WriteAllText(System.IO.Path.Combine(t.Sub(@"Roaming\Claude\claude-code\2.1.40"), "claude.exe"), ""); // 40 < 309
        t.Sub(@"Claude-Data\claude_abc\claude-code\2.2.0\empty");                                                    // no exe
        File.WriteAllText(System.IO.Path.Combine(t.Sub(@"Claude-Data\claude_abc\claude-code\latest"), "claude.exe"), "");

        Assert.Equal(System.IO.Path.Combine(store, @"claude-code\2.1.317\a33aa5\claude.exe"),
            ClaudeRegistration.FindInClaudeDesktop([classic, store]));
        Assert.Equal(System.IO.Path.Combine(classic, @"claude-code\2.1.40\claude.exe"),
            ClaudeRegistration.FindInClaudeDesktop([classic]));
    }

    [Fact]
    public void FindInClaudeDesktop_null_without_a_bundled_copy()
    {
        using var t = new TempDir();
        Assert.Null(ClaudeRegistration.FindInClaudeDesktop([t.Sub("Claude"), System.IO.Path.Combine(t.Path, "missing")]));
        Assert.Null(ClaudeRegistration.FindInClaudeDesktop([]));
    }

    // ------------------------------------------------------------------ status parsing

    [Fact]
    public void ParseGet_user_scope_connected_is_on_with_its_command()
    {
        var s = ClaudeRegistration.ParseGet(GetUserConnected);
        Assert.Equal(RegistrationState.On, s.State);
        Assert.Equal(ShareLauncher, s.Command);
        Assert.Equal("serve", s.Args);
        Assert.True(s.Matches(ShareLauncher));
        Assert.True(s.Matches(ShareLauncher.ToUpperInvariant()));
        Assert.False(s.Matches(SpacedLauncher));
    }

    [Fact]
    public void ParseGet_user_scope_failed_to_connect_is_still_on()
    {
        var s = ClaudeRegistration.ParseGet(GetUserFailed);
        Assert.Equal(RegistrationState.On, s.State);
        Assert.Equal(SpacedLauncher, s.Command);
    }

    [Fact]
    public void ParseGet_crlf_output_parses_the_same()
    {
        var s = ClaudeRegistration.ParseGet(GetUserConnected.Replace("\n", "\r\n", StringComparison.Ordinal));
        Assert.Equal(RegistrationState.On, s.State);
        Assert.Equal(ShareLauncher, s.Command);
    }

    [Fact]
    public void ParseGet_other_scope_is_off_and_says_so()
    {
        var s = ClaudeRegistration.ParseGet(GetLocal);
        Assert.Equal(RegistrationState.Off, s.State);
        Assert.Equal("Local", s.OtherScope);
    }

    [Fact]
    public void ParseGet_missing_with_or_without_other_servers_is_off()
    {
        Assert.Equal(RegistrationState.Off, ClaudeRegistration.ParseGet(GetMissingOthers).State);
        Assert.Equal(RegistrationState.Off, ClaudeRegistration.ParseGet(GetMissingNone).State);
        Assert.Null(ClaudeRegistration.ParseGet(GetMissingOthers).OtherScope);
    }

    [Fact]
    public void ParseGet_another_server_whose_name_starts_makino_is_not_ours()
    {
        var other = GetUserConnected.Replace("makino:", "makino-two:", StringComparison.Ordinal);
        Assert.Equal(RegistrationState.Unknown, ClaudeRegistration.ParseGet(other).State);
    }

    [Fact]
    public void ParseGet_unrecognised_output_is_unknown_not_off()
    {
        Assert.Equal(RegistrationState.Unknown, ClaudeRegistration.ParseGet("").State);
        Assert.Equal(RegistrationState.Unknown, ClaudeRegistration.ParseGet("Error: something broke\n").State);
    }

    [Fact]
    public void ParseList_finds_makino_among_other_servers()
    {
        // list can't say which scope a makino line is in: that is Unknown, not On.
        var listed = ClaudeRegistration.ParseList(ListWithMakino);
        Assert.Equal(RegistrationState.Unknown, listed.State);
        Assert.NotNull(listed.Problem);
        Assert.Equal(RegistrationState.Off, ClaudeRegistration.ParseList(ListWithoutMakino).State);
        Assert.Equal(RegistrationState.Off, ClaudeRegistration.ParseList(ListEmpty).State);
        Assert.Equal(RegistrationState.Unknown, ClaudeRegistration.ParseList("Error: config is broken\n").State);
    }

    // ------------------------------------------------------------------ status, add, remove through the runner

    [Fact]
    public async Task Status_reads_get_and_does_not_trust_the_exit_code_alone()
    {
        // get exits 1 for a missing server: that is a clear Off, not a failure.
        var r = new FakeRunner().Then(1, "", GetMissingNone);
        var s = await new ClaudeRegistration(r, Claude).GetStatusAsync(CancellationToken.None);
        Assert.Equal(RegistrationState.Off, s.State);
        Assert.Equal(Claude, r.Calls[0].Program);
        Assert.Equal(["mcp", "get", "makino"], r.Calls[0].Args);
    }

    [Fact]
    public async Task Status_falls_back_to_list_when_get_is_unrecognised()
    {
        var r = new FakeRunner().Then(1, "", "Error: unexpected\n").Then(0, ListWithoutMakino);
        var s = await new ClaudeRegistration(r, Claude).GetStatusAsync(CancellationToken.None);
        Assert.Equal(RegistrationState.Off, s.State);
        Assert.Equal(["mcp", "list"], r.Calls[1].Args);
    }

    [Fact]
    public async Task Status_from_list_with_a_makino_line_is_unknown_and_says_why()
    {
        var r = new FakeRunner().Then(1, "", "Error: unexpected\n").Then(0, ListWithMakino);
        var s = await new ClaudeRegistration(r, Claude).GetStatusAsync(CancellationToken.None);
        Assert.Equal(RegistrationState.Unknown, s.State);
        Assert.Equal("Claude Code lists a MakinoMCP entry but didn't say whether it is for all your projects.", s.Problem);
    }

    [Fact]
    public async Task Status_timeout_is_unknown_in_plain_words()
    {
        var r = new FakeRunner().Then(-1, "", "", timedOut: true);
        var s = await new ClaudeRegistration(r, Claude).GetStatusAsync(CancellationToken.None);
        Assert.Equal(RegistrationState.Unknown, s.State);
        Assert.Equal("Claude Code didn't answer within 30 seconds.", s.Problem);
    }

    [Fact]
    public void Add_and_remove_arguments()
    {
        Assert.Equal(["mcp", "add", "--scope", "user", "makino", "--", SpacedLauncher, "serve"],
            ClaudeRegistration.AddArgs(SpacedLauncher));
        Assert.Equal(["mcp", "add", "--scope", "user", "makino", "--", ShareLauncher, "serve"],
            ClaudeRegistration.AddArgs(ShareLauncher));
        Assert.Equal(["mcp", "remove", "makino", "--scope", "user"], ClaudeRegistration.RemoveArgs);
    }

    [Fact]
    public async Task TurnOn_adds_when_missing()
    {
        // The state already read is passed in: no second claude mcp get.
        var r = new FakeRunner().Then(0, $"Added stdio MCP server makino with command: {SpacedLauncher} serve to user config\n");
        var result = await new ClaudeRegistration(r, Claude).TurnOnAsync(SpacedLauncher, Off, CancellationToken.None);
        Assert.True(result.Ok);
        Assert.Single(r.Calls);
        Assert.Equal(ClaudeRegistration.AddArgs(SpacedLauncher), r.Calls[0].Args);
    }

    [Fact]
    public async Task TurnOn_reads_the_state_when_it_is_not_known()
    {
        foreach (var known in new ClaudeStatus?[] { null, new(RegistrationState.Unknown, Problem: "timed out") })
        {
            var r = new FakeRunner().Then(1, "", GetMissingNone).Then(0, "Added\n");
            var result = await new ClaudeRegistration(r, Claude).TurnOnAsync(SpacedLauncher, known, CancellationToken.None);
            Assert.True(result.Ok);
            Assert.Equal(ClaudeRegistration.GetArgs, r.Calls[0].Args);
            Assert.Equal(ClaudeRegistration.AddArgs(SpacedLauncher), r.Calls[1].Args);
        }
    }

    [Fact]
    public async Task Changes_are_never_cancelled_half_way()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var r = new FakeRunner().Then(0, "Removed\n").Then(0, "Added\n").Then(0, "Removed\n");
        var reg = new ClaudeRegistration(r, Claude);

        Assert.True((await reg.TurnOnAsync(ShareLauncher, ClaudeRegistration.ParseGet(GetUserFailed), cancelled.Token)).Ok);
        Assert.True((await reg.TurnOffAsync(null, cancelled.Token)).Ok);

        Assert.Equal(3, r.Calls.Count);
        Assert.All(r.Calls, c => Assert.False(c.Ct.CanBeCanceled));
    }

    [Fact]
    public async Task TurnOn_does_nothing_when_the_same_entry_exists()
    {
        var r = new FakeRunner();
        var result = await new ClaudeRegistration(r, Claude).TurnOnAsync(ShareLauncher, ClaudeRegistration.ParseGet(GetUserConnected), CancellationToken.None);
        Assert.True(result.Ok);
        Assert.Empty(r.Calls);
    }

    [Fact]
    public async Task TurnOn_replaces_an_entry_with_a_different_command()
    {
        var r = new FakeRunner()
            .Then(0, "Removed MCP server makino from user config\n")
            .Then(0, "Added stdio MCP server makino\n");
        var result = await new ClaudeRegistration(r, Claude).TurnOnAsync(ShareLauncher, ClaudeRegistration.ParseGet(GetUserFailed), CancellationToken.None);
        Assert.True(result.Ok);
        Assert.Equal(ClaudeRegistration.RemoveArgs, r.Calls[0].Args);
        Assert.Equal(ClaudeRegistration.AddArgs(ShareLauncher), r.Calls[1].Args);
    }

    [Fact]
    public async Task TurnOn_puts_the_old_entry_back_when_the_new_one_cant_be_added()
    {
        var r = new FakeRunner()
            .Then(0, "Removed MCP server makino from user config\n")
            .Then(1, "", "Error: could not write the config file\n")
            .Then(0, "Added stdio MCP server makino\n");
        var old = ClaudeRegistration.ParseGet(GetUserConnected); // the share's launcher
        var result = await new ClaudeRegistration(r, Claude).TurnOnAsync(SpacedLauncher, old, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains("still starts MakinoMCP from", result.Message);
        Assert.Equal(ClaudeRegistration.AddArgs(ShareLauncher), r.Calls[2].Args);
    }

    [Fact]
    public async Task TurnOn_failure_is_one_plain_line()
    {
        var r = new FakeRunner()
            .Then(1, "", "Error: could not write the config file\n    at Object.writeFile (node:fs:1:1)\n");
        var result = await new ClaudeRegistration(r, Claude).TurnOnAsync(ShareLauncher, Off, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal("Claude Code couldn't add MakinoMCP: Error: could not write the config file", result.Message);
    }

    [Fact]
    public async Task TurnOff_removes_and_accepts_already_gone()
    {
        var removed = new FakeRunner().Then(0, "Removed MCP server makino from user config\n");
        Assert.True((await new ClaudeRegistration(removed, Claude).TurnOffAsync(ClaudeRegistration.ParseGet(GetUserConnected), CancellationToken.None)).Ok);
        Assert.Equal(ClaudeRegistration.RemoveArgs, removed.Calls[0].Args);

        var gone = new FakeRunner().Then(1, "", "No MCP server named \"makino\" in user scope\n");
        Assert.True((await new ClaudeRegistration(gone, Claude).TurnOffAsync(null, CancellationToken.None)).Ok);

        var broken = new FakeRunner().Then(1, "", "Error: permission denied\n");
        var result = await new ClaudeRegistration(broken, Claude).TurnOffAsync(null, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal("Claude Code couldn't remove MakinoMCP: Error: permission denied", result.Message);
    }

    [Fact]
    public async Task TurnOff_skips_claude_code_when_it_is_known_to_be_off()
    {
        var r = new FakeRunner();
        Assert.True((await new ClaudeRegistration(r, Claude).TurnOffAsync(Off, CancellationToken.None)).Ok);
        Assert.Empty(r.Calls);
    }

    [Theory]
    [InlineData(@"\\server\Make%PATH%\MakinoMCP.exe")]
    [InlineData(@"C:\Wow!\MakinoMCP.exe")]
    public async Task A_name_cmd_would_change_is_refused_for_the_npm_install(string launcher)
    {
        var r = new FakeRunner();
        var result = await new ClaudeRegistration(r, @"C:\Users\someone\AppData\Roaming\npm\claude.cmd")
            .TurnOnAsync(launcher, Off, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal("Claude Code couldn't add MakinoMCP: This folder's name can't be passed to the npm install of Claude Code; " +
                     "use the install line below or Claude Code's own installer.", result.Message);
        Assert.Empty(r.Calls);
    }

    [Fact]
    public async Task The_same_names_are_fine_for_claude_exe()
    {
        var r = new FakeRunner().Then(0, "Added\n");
        var result = await new ClaudeRegistration(r, Claude).TurnOnAsync(@"C:\Wow!\R&D 100%\MakinoMCP.exe", Off, CancellationToken.None);
        Assert.True(result.Ok);
    }

    [Fact]
    public async Task A_runner_that_cannot_start_claude_gives_plain_words()
    {
        var r = new ThrowingRunner();
        var s = await new ClaudeRegistration(r, Claude).GetStatusAsync(CancellationToken.None);
        Assert.Equal(RegistrationState.Unknown, s.State);
        Assert.Equal("Couldn't run Claude Code: The system cannot find the file specified.", s.Problem);
    }

    private sealed class ThrowingRunner : ICommandRunner
    {
        public Task<CommandResult> RunAsync(string program, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct) =>
            throw new System.ComponentModel.Win32Exception(2, "The system cannot find the file specified.");
    }
}

public class CommandLineTests
{
    [Fact]
    public void Quote_leaves_plain_words_alone_and_quotes_spaces()
    {
        Assert.Equal("serve", CommandLine.Quote("serve"));
        Assert.Equal("\"\"", CommandLine.Quote(""));
        Assert.Equal(@"""C:\Program Files\X Y\MakinoMCP.exe""", CommandLine.Quote(@"C:\Program Files\X Y\MakinoMCP.exe"));
        Assert.Equal(@"\\server\Tools\MakinoMCP\MakinoMCP.exe", CommandLine.Quote(@"\\server\Tools\MakinoMCP\MakinoMCP.exe"));
        Assert.Equal(@"""\\server\Make It\MakinoMCP.exe""", CommandLine.Quote(@"\\server\Make It\MakinoMCP.exe"));
    }

    [Fact]
    public void Quote_doubles_backslashes_only_before_a_quote()
    {
        Assert.Equal(@"""C:\A B\\""", CommandLine.Quote(@"C:\A B\"));
        Assert.Equal(@"""say \""hi\""""", CommandLine.Quote(@"say ""hi"""));
    }

    [Theory]
    [InlineData(@"C:\Program Files\X Y\MakinoMCP.exe")]
    [InlineData(@"\\server\Tools\MakinoMCP\MakinoMCP.exe")]
    [InlineData(@"\\server\Make It\MakinoMCP.exe")]
    [InlineData(@"C:\A B\")]
    [InlineData(@"say ""hi""")]
    public void Split_reads_back_what_Join_wrote(string path)
    {
        var args = ClaudeRegistration.AddArgs(path);
        Assert.Equal(["claude", .. args], CommandLine.Split("claude " + CommandLine.Join(args)));
    }

    [Fact]
    public void For_an_exe_passes_the_arguments_directly()
    {
        var (file, arguments) = CommandLine.For(@"C:\Tools\claude.exe", ClaudeRegistration.AddArgs(@"C:\Program Files\X Y\MakinoMCP.exe"));
        Assert.Equal(@"C:\Tools\claude.exe", file);
        Assert.Equal(@"mcp add --scope user makino -- ""C:\Program Files\X Y\MakinoMCP.exe"" serve", arguments);
    }

    [Fact]
    public void For_a_cmd_goes_through_cmd_exe_with_the_whole_line_quoted()
    {
        var (file, arguments) = CommandLine.For(@"C:\Users\some one\npm\claude.cmd",
            ClaudeRegistration.AddArgs(@"\\server\Make It\MakinoMCP.exe"));
        Assert.Equal("cmd.exe", Path.GetFileName(file), ignoreCase: true);
        Assert.Equal(@"/d /s /c """"C:\Users\some one\npm\claude.cmd"" ""mcp"" ""add"" ""--scope"" ""user"" ""makino"" ""--"" ""\\server\Make It\MakinoMCP.exe"" ""serve""""",
            arguments);
    }

    [Theory]
    [InlineData(@"\\server\R&D\MakinoMCP.exe")]
    [InlineData(@"C:\A^B\MakinoMCP.exe")]
    [InlineData(@"C:\Tools(x86)\a|b<c>d\MakinoMCP.exe")]
    public void On_the_cmd_route_special_characters_are_always_quoted(string path)
    {
        var (_, arguments) = CommandLine.For(@"C:\npm\claude.cmd", ClaudeRegistration.AddArgs(path));
        Assert.Contains($"\"{path}\"", arguments);
        Assert.False(CommandLine.ChangedByCmd(path));
    }

    [Theory]
    [InlineData(@"C:\100%\MakinoMCP.exe")]
    [InlineData(@"C:\%USERNAME%\MakinoMCP.exe")]
    [InlineData(@"C:\Wow!\MakinoMCP.exe")]
    [InlineData("C:\\a\"b\\MakinoMCP.exe")]
    [InlineData("C:\\a\nb\\MakinoMCP.exe")]
    [InlineData("C:\\a\rb\\MakinoMCP.exe")]
    public void On_the_cmd_route_what_cmd_would_change_is_refused(string path)
    {
        Assert.True(CommandLine.ChangedByCmd(path));
        Assert.Throws<CmdArgumentException>(() => CommandLine.For(@"C:\npm\claude.cmd", ClaudeRegistration.AddArgs(path)));
        // claude.exe gets it unchanged.
        Assert.Equal(["claude", .. ClaudeRegistration.AddArgs(path)],
            CommandLine.Split("claude " + CommandLine.For(@"C:\x\claude.exe", ClaudeRegistration.AddArgs(path)).Arguments));
    }
}
