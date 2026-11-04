using MakinoMCP.Setup;
using Xunit;

namespace MakinoMCP.Tests;

public class RunningServersTests
{
    private const int Session = 3;
    private const string Me = "S-1-5-21-1000-2000-3000-1001";
    private const string SomeoneElse = "S-1-5-21-1000-2000-3000-1002";
    private const int Self = 500;          // this setup window
    private const int SelfLauncher = 400;  // the launcher that opened it

    private const string ShareLauncher = @"""\\server\Tools\MakinoMCP\MakinoMCP.exe""";
    private const string LocalCopy = @"""C:\Users\someone\AppData\Local\MakinoMCP\app\0.1.2\MakinoMCP.exe""";
    private const string Settings = @"""\\server\Tools\MakinoMCP\MakinoMCP.settings.json""";

    private static IReadOnlyList<ProcessRow> Rows() =>
    [
        // A Claude session's server: the launcher and the child it started.
        new(100, 90, $"{ShareLauncher} serve", Session, Me),
        new(101, 100, $"{LocalCopy} serve --settings {Settings}", Session, Me),
        // Another serve started with different spelling.
        new(110, 91, $"{ShareLauncher} SERVE", Session, Me),
        // This setup window and its launcher: setup mode, and their paths contain "server".
        new(SelfLauncher, 80, ShareLauncher, Session, Me),
        new(Self, SelfLauncher, $"{LocalCopy} --settings {Settings}", Session, Me),
        // Another setup window, an explicit "setup", a background copy.
        new(120, 81, $"{ShareLauncher} setup", Session, Me),
        new(121, 120, $"{LocalCopy} setup --settings {Settings}", Session, Me),
        new(130, 100, $"{ShareLauncher} --prefetch 0.1.2", Session, Me),
        // "serve" appears only inside a path or as a later argument.
        new(140, 1, @"""C:\serve\MakinoMCP.exe"" --settings C:\serve\x.json", Session, Me),
        new(141, 1, @"C:\Tools\MakinoMCP.exe --settings serve", Session, Me),
        // Another user's session, and a process whose command line can't be read.
        new(150, 1, $"{ShareLauncher} serve", 7, Me),
        new(160, 1, null, Session, Me),
        // Same session, but another account (run as), or an owner Windows won't name.
        new(170, 1, $"{ShareLauncher} serve", Session, SomeoneElse),
        new(171, 1, $"{ShareLauncher} serve", Session, null),
    ];

    [Fact]
    public void Only_processes_this_user_owns()
    {
        IReadOnlyList<ProcessRow> rows =
        [
            new(100, 1, $"{ShareLauncher} serve", Session, Me),
            new(101, 1, $"{ShareLauncher} serve", Session, SomeoneElse),
            new(102, 1, $"{ShareLauncher} serve", Session, "s-1-5-21-1000-2000-3000-1001"), // SIDs ignore case
        ];
        Assert.Equal([100, 102], RunningServers.ToStop(rows, Self, SelfLauncher, Session, Me));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(900, true)]
    [InlineData(-900, true)]
    [InlineData(1500, false)]
    [InlineData(-60_000, false)]
    public void The_same_process_only_if_it_started_when_the_query_said(int offsetMs, bool same)
    {
        var created = new DateTime(2026, 11, 4, 8, 0, 0, DateTimeKind.Local);
        Assert.Equal(same, RunningServers.SameStart(created, created.AddMilliseconds(offsetMs)));
        Assert.False(RunningServers.SameStart(null, created));
    }

    [Fact]
    public void Picks_the_launcher_and_its_child_in_serve_mode()
    {
        var pick = RunningServers.ToStop(Rows(), Self, SelfLauncher, Session, Me);
        Assert.Equal([100, 101, 110], pick);
    }

    [Fact]
    public void Never_the_current_process_or_its_parent_even_if_they_serve()
    {
        IReadOnlyList<ProcessRow> rows =
        [
            new(Self, SelfLauncher, $"{LocalCopy} serve", Session, Me),
            new(SelfLauncher, 1, $"{ShareLauncher} serve", Session, Me),
            new(100, 1, $"{ShareLauncher} serve", Session, Me),
        ];
        Assert.Equal([100], RunningServers.ToStop(rows, Self, SelfLauncher, Session, Me));
    }

    [Fact]
    public void Nothing_to_stop()
    {
        Assert.Empty(RunningServers.ToStop([], Self, SelfLauncher, Session, Me));
        Assert.Empty(RunningServers.ToStop([new(Self, SelfLauncher, LocalCopy, Session, Me)], Self, SelfLauncher, Session, Me));
    }

    [Theory]
    [InlineData(@"""C:\A B\MakinoMCP.exe"" serve", true)]
    [InlineData(@"C:\AB\MakinoMCP.exe serve --settings x", true)]
    [InlineData(@"MakinoMCP.exe ""serve""", true)]
    [InlineData(@"MakinoMCP.exe", false)]
    [InlineData(@"MakinoMCP.exe served", false)]
    [InlineData(@"""\\server\serve\MakinoMCP.exe""", false)]
    [InlineData(@"MakinoMCP.exe setup serve", false)]
    [InlineData("", false)]
    public void IsServe_needs_serve_as_the_first_argument(string commandLine, bool expected)
    {
        Assert.Equal(expected, RunningServers.IsServe(commandLine));
    }
}
