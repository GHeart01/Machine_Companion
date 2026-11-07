using MakinoMCP.Launcher;
using Xunit;

namespace MakinoMCP.Tests;

public class OldCacheTests : IDisposable
{
    private readonly string _app = Path.Combine(Path.GetTempPath(), "mmcp-oc-" + Guid.NewGuid().ToString("N"), "app");

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_app)!, true); } catch { }
    }

    private string Build(string name)
    {
        var d = Path.Combine(_app, name);
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "MakinoMCP.exe"), "x");
        return d;
    }

    [Fact]
    public void Tidy_removes_unused_builds_and_the_folder()
    {
        Build("0.2.0");
        Build("0.6.0-20261106-1740");
        OldCache.Tidy(_app, _ => false, null);
        Assert.False(Directory.Exists(_app));
    }

    [Fact]
    public void Tidy_skips_running()
    {
        var running = Build("0.6.0-20261106-1740");
        Build("0.2.0");
        OldCache.Tidy(_app, d => string.Equals(d, running, StringComparison.OrdinalIgnoreCase), null);
        Assert.True(Directory.Exists(running));
        Assert.Equal([running], Directory.GetDirectories(_app));
    }

    [Fact]
    public void Tidy_missing_folder_is_fine() => OldCache.Tidy(_app, _ => false, null);

    [Fact]
    public void Tidy_leaves_a_folder_that_holds_this_launcher()
    {
        // Someone installed MakinoMCP into %LOCALAPPDATA%\MakinoMCP\app\mine: that is an install, not an old copy.
        var install = Build("mine");
        Directory.CreateDirectory(Path.Combine(install, "versions", "0.8.0-20261107-1500"));
        Build("0.2.0");
        OldCache.Tidy(_app, _ => false, install);
        Assert.True(Directory.Exists(Path.Combine(install, "versions", "0.8.0-20261107-1500")));
        Assert.False(Directory.Exists(Path.Combine(_app, "0.2.0")));
    }
}
