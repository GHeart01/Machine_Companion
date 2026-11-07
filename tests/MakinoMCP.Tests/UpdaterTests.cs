using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using MakinoMCP.Launcher;
using Xunit;

namespace MakinoMCP.Tests;

public class UpdaterTests : IDisposable
{
    private const string Repo = "GHeart01/Machine_Companion";
    private const string OldBuild = "0.6.0-20261101-1000";
    private const string NewBuild = "0.7.0-20261107-1200";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "mmcp-up-" + Guid.NewGuid().ToString("N"));
    private readonly string _install;
    private readonly string _state;
    private DateTime _now = new(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);

    public UpdaterTests()
    {
        _install = Path.Combine(_root, "MakinoMCP");
        _state = Path.Combine(_root, "state");
        Directory.CreateDirectory(Path.Combine(_install, "versions", OldBuild));
        File.WriteAllText(Path.Combine(_install, "versions", OldBuild, "MakinoMCP.exe"), "old app");
        File.WriteAllText(Path.Combine(_install, "MakinoMCP.exe"), "old launcher");
        File.WriteAllText(Path.Combine(_install, "MakinoMCP.exe.config"), "config");
        File.WriteAllText(Path.Combine(_install, "current.txt"), OldBuild);
        File.WriteAllText(Path.Combine(_install, "MakinoMCP.settings.json"), "{ \"makino\": { \"password\": \"keep-me-secret\" } }");
        File.WriteAllText(Path.Combine(_install, UpdateSource.MarkerFileName),
            "{ \"source\": \"github\", \"repository\": \"" + Repo + "\" }");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed class FakeSource(string json, string zipPath, string shaText) : IReleaseSource
    {
        public int Downloads;
        public int Checks;
        public Exception? Throw;
        public string LatestJson(string repository)
        {
            Checks++;
            Assert.Equal(Repo, repository);
            if (Throw != null) throw Throw;
            return json;
        }
        public void Download(string url, string path, long maxBytes)
        {
            Downloads++;
            File.Copy(zipPath, path);
        }
        public string DownloadText(string url, int maxBytes) => shaText;
    }

    private string Zip()
    {
        var path = Path.Combine(_root, "MakinoMCP-0.7.0-win-x64.zip");
        if (File.Exists(path)) return path;
        using var z = ZipFile.Open(path, ZipArchiveMode.Create);
        void Add(string name, string text)
        {
            using var w = new StreamWriter(z.CreateEntry(name).Open());
            w.Write(text);
        }
        Add("current.txt", NewBuild);
        Add($"versions/{NewBuild}/MakinoMCP.exe", "new app");
        Add("MakinoMCP.exe", "new launcher");
        Add("MakinoMCP.exe.config", "config");
        return path;
    }

    private static string Sha(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    private static string Json(string tag, string? zipUrl = null)
    {
        var v = tag.TrimStart('v');
        zipUrl ??= $"https://github.com/{Repo}/releases/download/{tag}/MakinoMCP-{v}-win-x64.zip";
        return $"{{\"tag_name\":\"{tag}\",\"draft\":false,\"prerelease\":false,\"assets\":[" +
               $"{{\"name\":\"MakinoMCP-{v}-win-x64.zip\",\"browser_download_url\":\"{zipUrl}\"}}," +
               $"{{\"name\":\"MakinoMCP-{v}-win-x64.zip.sha256\",\"browser_download_url\":\"https://github.com/{Repo}/releases/download/{tag}/MakinoMCP-{v}-win-x64.zip.sha256\"}}]}}";
    }

    private FakeSource Source(string tag = "v0.7.0", string? sha = null, string? zipUrl = null)
    {
        var zip = Zip();
        return new FakeSource(Json(tag, zipUrl), zip, (sha ?? Sha(zip)) + "  MakinoMCP-" + tag.TrimStart('v') + "-win-x64.zip\n");
    }

    private Updater Updater(IReleaseSource source) =>
        new(_install, _state, source, () => _now, _ => false, _ => false);

    private string Current() => File.ReadAllText(Path.Combine(_install, "current.txt")).Trim();

    [Fact]
    public void Due_NoState_True() => Assert.True(Updater(Source()).Due());

    [Fact]
    public void Due_RecentAttempt_False()
    {
        var u = Updater(Source(tag: "v0.6.0"));
        Assert.Equal("up-to-date", u.Run().Result);
        _now = _now.AddHours(1);
        Assert.False(u.Due());
        _now = _now.AddHours(6);
        Assert.True(u.Due());
    }

    [Fact]
    public void Run_NewerRelease_Updates()
    {
        var src = Source();
        var state = Updater(src).Run();
        Assert.Equal("updated", state.Result);
        Assert.Equal("0.7.0", state.Version);
        Assert.Equal(NewBuild, Current());
        Assert.Equal(1, src.Downloads);
        Assert.Empty(Directory.GetFiles(Path.Combine(_install, "versions"), ".download-*"));
        Assert.Contains("\"updated\"", File.ReadAllText(Path.Combine(_state, "update-state.json")));
        Assert.Single(Directory.GetFiles(Path.Combine(_state, "logs"), "update-*.log"));
    }

    [Fact]
    public void Run_SameVersion_UpToDate()
    {
        var src = Source(tag: "v0.6.0");
        Assert.Equal("up-to-date", Updater(src).Run().Result);
        Assert.Equal(0, src.Downloads);
        Assert.Equal(OldBuild, Current());
    }

    [Fact]
    public void Run_OlderRelease_NoDowngrade()
    {
        var src = Source(tag: "v0.5.0");
        Assert.Equal("up-to-date", Updater(src).Run().Result);
        Assert.Equal(0, src.Downloads);
    }

    [Fact]
    public void Run_HashMismatch_Failed()
    {
        var state = Updater(Source(sha: new string('0', 64))).Run();
        Assert.Equal("failed", state.Result);
        Assert.Contains("SHA-256", state.Message);
        Assert.Equal(OldBuild, Current());
        Assert.Empty(Directory.GetFiles(Path.Combine(_install, "versions"), ".download-*"));
    }

    [Fact]
    public void Run_DisallowedDownloadUrl_Failed()
    {
        var src = Source(zipUrl: "https://example.com/MakinoMCP-0.7.0-win-x64.zip");
        Assert.Equal("failed", Updater(src).Run().Result);
        Assert.Equal(0, src.Downloads);
        Assert.Equal(OldBuild, Current());
    }

    [Fact]
    public void Check_ApiFailure_RecordsFailedAndThrottles()
    {
        var src = Source();
        src.Throw = new HttpRequestException("proxy said no");
        var u = Updater(src);
        var state = u.Run();
        Assert.Equal("failed", state.Result);
        Assert.Contains("proxy said no", state.Message);
        Assert.False(u.Due());
        Assert.Equal(OldBuild, Current());
    }

    [Fact]
    public void Run_SecondInstance_ReturnsWithoutWork()
    {
        var src = Source();
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            using var m = new Mutex(false, MakinoMCP.Launcher.Updater.MutexName(_install));
            m.WaitOne();
            held.Set();
            release.Wait();
            m.ReleaseMutex();
        });
        holder.Start();
        held.Wait();
        try
        {
            Assert.Equal("busy", Updater(src).Run().Result);
            Assert.Equal(0, src.Checks);
        }
        finally
        {
            release.Set();
            holder.Join();
        }
    }

    [Fact]
    public void Run_StateAndLogHaveNoSettingsContent()
    {
        Updater(Source()).Run();
        Assert.DoesNotContain("keep-me-secret", File.ReadAllText(Path.Combine(_state, "update-state.json")));
        foreach (var log in Directory.GetFiles(Path.Combine(_state, "logs")))
            Assert.DoesNotContain("keep-me-secret", File.ReadAllText(log));
    }

    [Fact]
    public void State_round_trips_for_the_setup_window()
    {
        Updater(Source()).Run();
        var s = MakinoMCP.Launcher.Updater.ReadState(Path.Combine(_state, "update-state.json"));
        Assert.NotNull(s);
        Assert.Equal("updated", s!.Result);
        Assert.Equal(_now, s.LastAttemptUtc);
    }
}
