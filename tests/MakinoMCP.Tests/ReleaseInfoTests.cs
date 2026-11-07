using MakinoMCP.Launcher;
using Xunit;

namespace MakinoMCP.Tests;

public class ReleaseInfoTests
{
    private const string Repo = "GHeart01/Machine_Companion";

    // Trimmed from a real releases/latest answer.
    private static string Json(string tag = "v0.7.0", bool prerelease = false, bool draft = false, bool withSha = true)
    {
        var v = tag.TrimStart('v');
        var sha = withSha
            ? $",{{\"name\":\"MakinoMCP-{v}-win-x64.zip.sha256\",\"size\":95,\"browser_download_url\":\"https://github.com/{Repo}/releases/download/{tag}/MakinoMCP-{v}-win-x64.zip.sha256\"}}"
            : "";
        return $"{{\"url\":\"https://api.github.com/repos/{Repo}/releases/1\",\"tag_name\":\"{tag}\",\"name\":\"{tag}\"," +
               $"\"draft\":{(draft ? "true" : "false")},\"prerelease\":{(prerelease ? "true" : "false")}," +
               "\"published_at\":\"2026-11-07T18:00:00Z\",\"author\":{\"login\":\"GHeart01\",\"id\":1}," +
               $"\"assets\":[{{\"name\":\"MakinoMCP-{v}-win-x64.zip\",\"size\":73400320,\"browser_download_url\":\"https://github.com/{Repo}/releases/download/{tag}/MakinoMCP-{v}-win-x64.zip\"}}{sha}]," +
               "\"body\":\"notes\"}";
    }

    [Fact]
    public void Parse_latest_release()
    {
        var r = ReleaseInfo.Parse(Json());
        Assert.NotNull(r);
        Assert.Equal("v0.7.0", r!.Tag);
        Assert.Equal(new Version(0, 7, 0), r.Version);
        Assert.Equal($"https://github.com/{Repo}/releases/download/v0.7.0/MakinoMCP-0.7.0-win-x64.zip", r.ZipUrl);
        Assert.EndsWith(".zip.sha256", r.ShaUrl);
        Assert.Equal("MakinoMCP-0.7.0-win-x64.zip", r.ZipName);
    }

    [Fact]
    public void Parse_rejects_prerelease_draft_odd_tag_and_missing_asset()
    {
        Assert.Null(ReleaseInfo.Parse(Json(tag: "v0.7.0-rc1")));
        Assert.Null(ReleaseInfo.Parse(Json(tag: "0.7.0")));
        Assert.Null(ReleaseInfo.Parse(Json(prerelease: true)));
        Assert.Null(ReleaseInfo.Parse(Json(draft: true)));
        Assert.Null(ReleaseInfo.Parse(Json(withSha: false)));
        Assert.Null(ReleaseInfo.Parse("<html>proxy login</html>"));
        Assert.Null(ReleaseInfo.Parse(""));
    }

    [Fact]
    public void IsAllowedDownload_only_this_repository_over_https()
    {
        Assert.True(ReleaseInfo.IsAllowedDownload($"https://github.com/{Repo}/releases/download/v0.7.0/x.zip", Repo));
        Assert.False(ReleaseInfo.IsAllowedDownload("https://github.com/someone/else/releases/download/v0.7.0/x.zip", Repo));
        Assert.False(ReleaseInfo.IsAllowedDownload($"http://github.com/{Repo}/releases/download/v0.7.0/x.zip", Repo));
        Assert.False(ReleaseInfo.IsAllowedDownload($"https://github.com.evil.example/{Repo}/releases/download/v0.7.0/x.zip", Repo));
        Assert.False(ReleaseInfo.IsAllowedDownload($"https://github.com/{Repo}/releases/download/../../../x.zip", Repo));
        Assert.False(ReleaseInfo.IsAllowedDownload(null, Repo));
    }

    [Fact]
    public void ParseSha256_line()
    {
        var hash = new string('a', 32) + new string('F', 32);
        Assert.Equal(hash.ToLowerInvariant(), ReleaseInfo.ParseSha256($"{hash}  MakinoMCP-0.7.0-win-x64.zip\n", "MakinoMCP-0.7.0-win-x64.zip"));
        Assert.Null(ReleaseInfo.ParseSha256($"{hash}  other.zip\n", "MakinoMCP-0.7.0-win-x64.zip"));
        Assert.Null(ReleaseInfo.ParseSha256("xyz  MakinoMCP-0.7.0-win-x64.zip", "MakinoMCP-0.7.0-win-x64.zip"));
        Assert.Null(ReleaseInfo.ParseSha256(null, "MakinoMCP-0.7.0-win-x64.zip"));
    }
}
