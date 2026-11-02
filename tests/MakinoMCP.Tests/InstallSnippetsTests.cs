using System.Text.Json;
using MakinoMCP.Setup;
using Xunit;

namespace MakinoMCP.Tests;

public class InstallSnippetsTests
{
    private static string? SrvCommonOnF(string drive) => drive == "F:" ? @"\\srv\Common" : null;

    [Fact]
    public void FromMapped_F_drive_to_unc()
    {
        Assert.Equal(@"\\srv\Common\A B\x.exe", UncPath.FromMapped(@"F:\A B\x.exe", SrvCommonOnF));
    }

    [Fact]
    public void FromMapped_lower_case_drive_letter_is_found()
    {
        Assert.Equal(@"\\srv\Common\x.exe", UncPath.FromMapped(@"f:\x.exe", SrvCommonOnF));
    }

    [Fact]
    public void FromMapped_remote_with_trailing_backslash_does_not_double_it()
    {
        Assert.Equal(@"\\srv\Common\x.exe", UncPath.FromMapped(@"F:\x.exe", _ => @"\\srv\Common\"));
    }

    [Fact]
    public void FromMapped_unc_unchanged()
    {
        var called = false;
        var result = UncPath.FromMapped(@"\\srv\Common\x.exe", d => { called = true; return @"\\other\share"; });
        Assert.Equal(@"\\srv\Common\x.exe", result);
        Assert.False(called);
    }

    [Fact]
    public void FromMapped_local_drive_unchanged()
    {
        Assert.Equal(@"C:\Tools\x.exe", UncPath.FromMapped(@"C:\Tools\x.exe", SrvCommonOnF));
    }

    [Fact]
    public void Snippets_escape_backslashes_in_json()
    {
        var path = @"\\srv\Common\Make It\MakinoMCP.exe";
        var (_, cowork) = InstallSnippets.For(path);

        Assert.Equal(
            @"""makino"": { ""command"": ""\\\\srv\\Common\\Make It\\MakinoMCP.exe"", ""args"": [""serve""] }",
            cowork);
        // It is a valid JSON member that reads back as the same path.
        using var doc = JsonDocument.Parse("{" + cowork + "}");
        var makino = doc.RootElement.GetProperty("makino");
        Assert.Equal(path, makino.GetProperty("command").GetString());
        Assert.Equal("serve", makino.GetProperty("args")[0].GetString());
    }

    [Fact]
    public void Snippets_quote_path_with_spaces()
    {
        var (claudeCode, _) = InstallSnippets.For(@"\\srv\Common\Make It\MakinoMCP.exe");
        Assert.Equal(@"claude mcp add makino -- ""\\srv\Common\Make It\MakinoMCP.exe"" serve", claudeCode);
    }

    [Fact]
    public void Snippets_keep_non_ascii_readable()
    {
        var (_, cowork) = InstallSnippets.For(@"\\srv\Fräse+Co\MakinoMCP.exe");
        Assert.Contains(@"Fräse+Co", cowork);
    }

    [Fact]
    public void Launcher_sits_beside_the_settings_file()
    {
        Assert.Equal(@"F:\Apps\MakinoMCP\MakinoMCP.exe",
            InstallSnippets.LauncherPath(@"F:\Apps\MakinoMCP\MakinoMCP.settings.json"));
    }
}
