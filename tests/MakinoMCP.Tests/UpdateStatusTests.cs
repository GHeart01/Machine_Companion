using MakinoMCP.Setup;
using Xunit;

namespace MakinoMCP.Tests;

public class UpdateStatusTests
{
    private static readonly DateTime Now = new(2026, 11, 7, 18, 0, 0, DateTimeKind.Utc);

    private static string State(string result, DateTime at, string? version = null, string? message = null) =>
        "{\"lastAttemptUtc\":\"" + at.ToString("yyyy-MM-ddTHH:mm:ssZ") + "\",\"result\":\"" + result + "\"" +
        (version == null ? "" : ",\"version\":\"" + version + "\"") +
        (message == null ? "" : ",\"message\":\"" + message + "\"") + "}";

    [Fact]
    public void Up_to_date_says_when() =>
        Assert.Equal("Checked 2 hours ago: up to date.", UpdateStatus.Describe(State("up-to-date", Now.AddHours(-2)), Now));

    [Fact]
    public void Updated_says_when_claude_uses_it() =>
        Assert.Equal("Updated to 0.7.0 5 minutes ago. Claude uses it from its next start.",
            UpdateStatus.Describe(State("updated", Now.AddMinutes(-5), "0.7.0"), Now));

    [Fact]
    public void Failed_gives_the_reason_once() =>
        Assert.Equal("The last check failed 3 days ago: GitHub answered 403 Forbidden.",
            UpdateStatus.Describe(State("failed", Now.AddDays(-3), message: "GitHub answered 403 Forbidden."), Now));

    [Fact]
    public void Time_words()
    {
        Assert.Equal("Checked just now: up to date.", UpdateStatus.Describe(State("up-to-date", Now.AddSeconds(-20)), Now));
        Assert.Equal("Checked 1 minute ago: up to date.", UpdateStatus.Describe(State("up-to-date", Now.AddMinutes(-1)), Now));
        Assert.Equal("Checked 1 hour ago: up to date.", UpdateStatus.Describe(State("up-to-date", Now.AddMinutes(-61)), Now));
        Assert.Equal("Checked 1 day ago: up to date.", UpdateStatus.Describe(State("up-to-date", Now.AddHours(-30)), Now));
    }

    [Fact]
    public void Checking_recent_and_stale()
    {
        Assert.Equal("Checking for a new version…", UpdateStatus.Describe(State("checking", Now.AddMinutes(-2)), Now));
        Assert.Equal("The last check didn't finish (2 hours ago).", UpdateStatus.Describe(State("checking", Now.AddHours(-2)), Now));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("{\"result\":\"up-to-date\"}")]
    [InlineData("{\"lastAttemptUtc\":\"2026-11-07T17:00:00Z\",\"result\":\"something-new\"}")]
    public void Missing_or_unknown_state_is_null(string? json) => Assert.Null(UpdateStatus.Describe(json, Now));

    [Fact]
    public void Reads_the_launchers_escaped_json() =>
        // DataContractJsonSerializer escapes slashes.
        Assert.Equal("The last check failed 1 hour ago: no valid file at C:/x.",
            UpdateStatus.Describe("{\"lastAttemptUtc\":\"2026-11-07T17:00:00Z\",\"result\":\"failed\",\"message\":\"no valid file at C:\\/x\"}", Now));
}
