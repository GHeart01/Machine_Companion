using MakinoMCP.Launcher;
using Xunit;

namespace MakinoMCP.Tests;

public class ShareInstallerTests : IDisposable
{
    private const string Build = "0.8.0-20261107-1500";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mmcp-si-" + Guid.NewGuid().ToString("N"));
    private string Share => Path.Combine(_root, "share");
    private string Local => Path.Combine(_root, "local", "Programs", "MakinoMCP");

    public ShareInstallerTests()
    {
        Directory.CreateDirectory(Path.Combine(Share, "versions", Build));
        File.WriteAllText(Path.Combine(Share, "versions", Build, "MakinoMCP.exe"), "app");
        File.WriteAllText(Path.Combine(Share, "versions", Build, "build.txt"), "build=" + Build);
        File.WriteAllText(Path.Combine(Share, "MakinoMCP.exe"), "launcher");
        File.WriteAllText(Path.Combine(Share, "MakinoMCP.exe.config"), "config");
        File.WriteAllText(Path.Combine(Share, "current.txt"), Build);
        File.WriteAllText(Path.Combine(Share, "MakinoMCP.settings.json"), "{ \"makino\": { \"server\": \"CELL\" } }");
        File.WriteAllText(Path.Combine(Share, "MakinoMCP.settings.example.json"), "{ }");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private string L(params string[] parts) => Path.Combine([Local, .. parts]);

    [Fact]
    public void Install_copies_build_launcher_settings_and_writes_current_last()
    {
        var r = ShareInstaller.Install(Share, Local, null);
        Assert.True(r.Installed, r.Reason);
        Assert.Equal("app", File.ReadAllText(L("versions", Build, "MakinoMCP.exe")));
        Assert.Equal("launcher", File.ReadAllText(L("MakinoMCP.exe")));
        Assert.Equal("config", File.ReadAllText(L("MakinoMCP.exe.config")));
        Assert.Equal(Build, File.ReadAllText(L("current.txt")).Trim());
        Assert.Contains("CELL", File.ReadAllText(L("MakinoMCP.settings.json")));
        Assert.True(File.Exists(L("MakinoMCP.settings.example.json")));
        Assert.True(ShareInstaller.IsInstalled(Local));
    }

    [Fact]
    public void Install_writes_the_default_marker_when_the_share_has_none()
    {
        Assert.True(ShareInstaller.Install(Share, Local, null).Installed);
        Assert.Equal("GHeart01/Machine_Companion", UpdateSource.ReadRepository(Local));
    }

    [Fact]
    public void Install_copies_the_shares_marker()
    {
        File.WriteAllText(Path.Combine(Share, UpdateSource.MarkerFileName), "{ \"source\": \"github\", \"repository\": \"someone/fork\" }");
        Assert.True(ShareInstaller.Install(Share, Local, null).Installed);
        Assert.Equal("someone/fork", UpdateSource.ReadRepository(Local));
    }

    [Fact]
    public void Install_keeps_local_settings_and_an_existing_install()
    {
        Directory.CreateDirectory(L("versions", "0.9.0-20261201-0900"));
        File.WriteAllText(L("versions", "0.9.0-20261201-0900", "MakinoMCP.exe"), "newer app");
        File.WriteAllText(L("MakinoMCP.exe"), "newer launcher");
        File.WriteAllText(L("current.txt"), "0.9.0-20261201-0900");
        File.WriteAllText(L("MakinoMCP.settings.json"), "{ \"mine\": true }");

        var r = ShareInstaller.Install(Share, Local, null);
        Assert.True(r.Installed, r.Reason);
        Assert.Equal("0.9.0-20261201-0900", File.ReadAllText(L("current.txt")).Trim());
        Assert.Equal("newer launcher", File.ReadAllText(L("MakinoMCP.exe")));
        Assert.Contains("mine", File.ReadAllText(L("MakinoMCP.settings.json")));
        Assert.False(Directory.Exists(L("versions", Build)));
    }

    [Fact]
    public void Install_without_share_settings_leaves_none()
    {
        File.Delete(Path.Combine(Share, "MakinoMCP.settings.json"));
        Assert.True(ShareInstaller.Install(Share, Local, null).Installed);
        Assert.False(File.Exists(L("MakinoMCP.settings.json")));
    }

    [Theory]
    [InlineData(@"..\..\escape")]
    [InlineData("0.8.0-missing")]
    [InlineData(null)]
    public void Install_refuses_a_share_without_a_valid_build(string? current)
    {
        if (current == null) File.Delete(Path.Combine(Share, "current.txt"));
        else File.WriteAllText(Path.Combine(Share, "current.txt"), current);
        var r = ShareInstaller.Install(Share, Local, null);
        Assert.False(r.Installed);
        Assert.False(ShareInstaller.IsInstalled(Local));
        Assert.False(File.Exists(L("current.txt")));
    }

    [Fact]
    public void IsInstalled_needs_launcher_and_current_build()
    {
        Assert.False(ShareInstaller.IsInstalled(Local));
        Directory.CreateDirectory(Local);
        File.WriteAllText(L("MakinoMCP.exe"), "launcher");
        File.WriteAllText(L("current.txt"), Build);
        Assert.False(ShareInstaller.IsInstalled(Local)); // the build itself is missing
        Directory.CreateDirectory(L("versions", Build));
        File.WriteAllText(L("versions", Build, "MakinoMCP.exe"), "app");
        Assert.True(ShareInstaller.IsInstalled(Local));
    }

    [Fact]
    public void LocalInstallDir_is_under_programs()
    {
        var dir = ShareInstaller.LocalInstallDir();
        Assert.EndsWith(Path.Combine("Programs", "MakinoMCP"), dir);
    }
}
