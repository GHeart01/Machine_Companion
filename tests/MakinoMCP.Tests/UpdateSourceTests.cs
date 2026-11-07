using MakinoMCP.Launcher;
using Xunit;

namespace MakinoMCP.Tests;

public class UpdateSourceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mmcp-us-" + Guid.NewGuid().ToString("N"));

    public UpdateSourceTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    private void Marker(string json) => File.WriteAllText(Path.Combine(_dir, UpdateSource.MarkerFileName), json);
    private string Settings(string json)
    {
        var p = Path.Combine(_dir, "MakinoMCP.settings.json");
        File.WriteAllText(p, json);
        return p;
    }

    private const string GoodMarker = "{ \"source\": \"github\", \"repository\": \"GHeart01/Machine_Companion\" }";

    [Fact]
    public void ReadRepository_valid_marker()
    {
        Marker(GoodMarker);
        Assert.Equal("GHeart01/Machine_Companion", UpdateSource.ReadRepository(_dir));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{ not json")]
    [InlineData("{ \"source\": \"share\", \"repository\": \"GHeart01/Machine_Companion\" }")]
    [InlineData("{ \"source\": \"github\", \"repository\": \"a/b/c\" }")]
    [InlineData("{ \"source\": \"github\", \"repository\": \"../x\" }")]
    [InlineData("{ \"source\": \"github\" }")]
    public void ReadRepository_rejects_missing_or_bad_marker(string? json)
    {
        if (json != null) Marker(json);
        Assert.Null(UpdateSource.ReadRepository(_dir));
    }

    [Fact]
    public void IsNetworkPath_unc_mapped_and_local()
    {
        Assert.True(UpdateSource.IsNetworkPath(@"\\server\share\MakinoMCP", _ => false));
        Assert.True(UpdateSource.IsNetworkPath(@"Z:\Tools\MakinoMCP", root => root.StartsWith("Z", StringComparison.OrdinalIgnoreCase)));
        Assert.False(UpdateSource.IsNetworkPath(@"C:\Tools\MakinoMCP", root => root.StartsWith("Z", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void IsInsideLocalShare_equal_child_and_sibling_prefix()
    {
        var shares = new[] { @"C:\Tools\" };
        Assert.True(UpdateSource.IsInsideLocalShare(@"C:\Tools", shares));
        Assert.True(UpdateSource.IsInsideLocalShare(@"c:\tools\MakinoMCP", shares));
        Assert.False(UpdateSource.IsInsideLocalShare(@"C:\Tools2\MakinoMCP", shares));
        Assert.False(UpdateSource.IsInsideLocalShare(@"C:\Tools", null));
    }

    [Fact]
    public void UpdatesOff_reads_only_the_updates_field()
    {
        Assert.True(UpdateSource.UpdatesOff(Settings("{ \"makino\": { \"password\": \"s3cret\" }, \"updates\": \"off\" }")));
        Assert.True(UpdateSource.UpdatesOff(Settings("{ \"updates\": \"OFF\" }")));
        Assert.False(UpdateSource.UpdatesOff(Settings("{ \"updates\": \"auto\" }")));
        Assert.False(UpdateSource.UpdatesOff(Settings("{ \"makino\": { \"password\": \"s3cret\" } }")));
        Assert.False(UpdateSource.UpdatesOff(Settings("{ not json")));
        Assert.False(UpdateSource.UpdatesOff(Path.Combine(_dir, "missing.json")));
        // Comments are allowed in the settings file (SettingsStore skips them).
        Assert.True(UpdateSource.UpdatesOff(Settings("{ // mine\n \"updates\": \"off\" }")));
    }

    [Fact]
    public void Decide_truth_table()
    {
        var settings = Path.Combine(_dir, "MakinoMCP.settings.json");
        Func<string, bool> local = _ => false;

        var none = UpdateSource.Decide(_dir, settings, local, [], null);
        Assert.False(none.GitHubInstall);
        Assert.False(none.UpdatesOn);
        Assert.False(string.IsNullOrEmpty(none.Reason));

        Marker(GoodMarker);
        var on = UpdateSource.Decide(_dir, settings, local, [], null);
        Assert.True(on.GitHubInstall);
        Assert.True(on.UpdatesOn);
        Assert.Equal("GHeart01/Machine_Companion", on.Repository);

        Assert.False(UpdateSource.Decide(_dir, settings, _ => true, [], null).GitHubInstall);
        Assert.False(UpdateSource.Decide(_dir, settings, local, [_dir], null).GitHubInstall);
        Assert.True(UpdateSource.Decide(_dir, settings, local, null, null).GitHubInstall);

        var env = UpdateSource.Decide(_dir, settings, local, [], "1");
        Assert.True(env.GitHubInstall);
        Assert.False(env.UpdatesOn);

        Settings("{ \"updates\": \"off\" }");
        var off = UpdateSource.Decide(_dir, settings, local, [], null);
        Assert.True(off.GitHubInstall);
        Assert.False(off.UpdatesOn);
    }

    [Theory]
    [InlineData("0.6.0-20261107-1232", "0.6.0")]
    [InlineData("12.3.45-20261107-1232", "12.3.45")]
    [InlineData("0.6.0", null)]
    [InlineData("dev", null)]
    [InlineData(null, null)]
    public void InstalledVersion_from_build(string? build, string? expected) =>
        Assert.Equal(expected, UpdateSource.InstalledVersion(build)?.ToString(3));
}
