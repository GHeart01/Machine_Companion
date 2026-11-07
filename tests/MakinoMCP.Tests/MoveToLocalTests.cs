using MakinoMCP.Setup;
using Xunit;

namespace MakinoMCP.Tests;

// Setup opened by the share's installer moves Claude to the copy on this PC, but only when Claude starts MakinoMCP
// from that share; any other entry (or none) is left to the user.
public class MoveToLocalTests
{
    private const string Share = @"\\server\Tools\MakinoMCP\MakinoMCP.exe";
    private const string Local = @"C:\Users\me\AppData\Local\Programs\MakinoMCP\MakinoMCP.exe";

    private static SwitchFacts Facts(ClaudeStatus? claude) => new(claude, false, false, true, Local);

    [Fact]
    public void Moves_when_claude_starts_the_share_launcher() =>
        Assert.True(ClaudeSwitch.ShouldMoveFrom(Facts(new ClaudeStatus(RegistrationState.On, Share, "serve")), Share));

    [Fact]
    public void Matches_the_path_in_any_case() =>
        Assert.True(ClaudeSwitch.ShouldMoveFrom(Facts(new ClaudeStatus(RegistrationState.On, Share.ToUpperInvariant(), "serve")), Share));

    [Fact]
    public void Leaves_other_entries_alone()
    {
        Assert.False(ClaudeSwitch.ShouldMoveFrom(Facts(new ClaudeStatus(RegistrationState.On, Local, "serve")), Share));
        Assert.False(ClaudeSwitch.ShouldMoveFrom(Facts(new ClaudeStatus(RegistrationState.On, @"D:\Other\MakinoMCP.exe", "serve")), Share));
        Assert.False(ClaudeSwitch.ShouldMoveFrom(Facts(new ClaudeStatus(RegistrationState.Off)), Share));
        Assert.False(ClaudeSwitch.ShouldMoveFrom(Facts(new ClaudeStatus(RegistrationState.Unknown, Problem: "x")), Share));
        Assert.False(ClaudeSwitch.ShouldMoveFrom(Facts(null), Share));
        Assert.False(ClaudeSwitch.ShouldMoveFrom(Facts(new ClaudeStatus(RegistrationState.On, Share, "serve")), null));
    }
}
