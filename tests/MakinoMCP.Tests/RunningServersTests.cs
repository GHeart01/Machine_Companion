using MakinoMCP.Setup;
using Xunit;

namespace MakinoMCP.Tests;

public class RunningServersTests
{
    private const int Session = 3;
    private const int Self = 500;          // this setup window
    private const int SelfLauncher = 400;  // the launcher that opened it

    private const string ShareLauncher = @"""\\server\Tools\MakinoMCP\MakinoMCP.exe""";
    private const string LocalCopy = @"""C:\Users\someone\AppData\Local\MakinoMCP\app\0.1.2\MakinoMCP.exe""";
    private const string Settings = @"""\\server\Tools\MakinoMCP\MakinoMCP.settings.json""";

    private static IReadOnlyList<ProcessRow> Rows() =>
    [
        // A Claude session's server: the launcher and the child it started.
        new(100, 90, $"{ShareLauncher} serve", Session),
        new(101, 100, $"{LocalCopy} serve --settings {Settings}", Session),
        // Another serve started with different spelling.
        new(110, 91, $"{ShareLauncher} SERVE", Session),
        // This setup window and its launcher: setup mode, and their paths contain "server".
        new(SelfLauncher, 80, ShareLauncher, Session),
        new(Self, SelfLauncher, $"{LocalCopy} --settings {Settings}", Session),
        // Another setup window, an explicit "setup", a background copy.
        new(120, 81, $"{ShareLauncher} setup", Session),
        new(121, 120, $"{LocalCopy} setup --settings {Settings}", Session),
        new(130, 100, $"{ShareLauncher} --prefetch 0.1.2", Session),
        // "serve" appears only inside a path or as a later argument.
        new(140, 1, @"""C:\serve\MakinoMCP.exe"" --settings C:\serve\x.json", Session),
        new(141, 1, @"C:\Tools\MakinoMCP.exe --settings serve", Session),
        // Another user's session, and a process whose command line can't be read.
        new(150, 1, $"{ShareLauncher} serve", 7),
        new(160, 1, null, Session),
    ];

    [Fact]
    public void Picks_the_launcher_and_its_child_in_serve_mode()
    {
        var pick = RunningServers.ToStop(Rows(), Self, SelfLauncher, Session);
        Assert.Equal([100, 101, 110], pick);
    }

    [Fact]
    public void Never_the_current_process_or_its_parent_even_if_they_serve()
    {
        IReadOnlyList<ProcessRow> rows =
        [
            new(Self, SelfLauncher, $"{LocalCopy} serve", Session),
            new(SelfLauncher, 1, $"{ShareLauncher} serve", Session),
            new(100, 1, $"{ShareLauncher} serve", Session),
        ];
        Assert.Equal([100], RunningServers.ToStop(rows, Self, SelfLauncher, Session));
    }

    [Fact]
    public void Nothing_to_stop()
    {
        Assert.Empty(RunningServers.ToStop([], Self, SelfLauncher, Session));
        Assert.Empty(RunningServers.ToStop([new(Self, SelfLauncher, LocalCopy, Session)], Self, SelfLauncher, Session));
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
