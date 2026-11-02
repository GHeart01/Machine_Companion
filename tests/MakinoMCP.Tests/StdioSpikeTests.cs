using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace MakinoMCP.Tests;

public class StdioSpikeTests
{
    [Fact]
    public async Task Launcher_serves_tools_over_stdio()
    {
        var root = Path.Combine(Path.GetTempPath(), "makinomcp-spike-" + Guid.NewGuid().ToString("N"));
        try
        {
            var share = Path.Combine(root, "share");
            var localAppData = Path.Combine(root, "localappdata");
            Directory.CreateDirectory(localAppData);

            var (launcherOut, mainOut) = BuildOutputs();
            CopyDir(launcherOut, share);
            File.WriteAllText(Path.Combine(share, "current.txt"), "0.0.0-test");
            CopyDir(mainOut, Path.Combine(share, "versions", "0.0.0-test"));

            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "makino-spike",
                Command = Path.Combine(share, "MakinoMCP.exe"),
                Arguments = ["serve"],
                EnvironmentVariables = new Dictionary<string, string?> { ["LOCALAPPDATA"] = localAppData },
            });

            await using var client = await McpClient.CreateAsync(transport);
            var tools = await client.ListToolsAsync();
            Assert.Contains(tools, t => t.Name == "ping");

            var result = await client.CallToolAsync("ping");
            var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
            Assert.Equal("pong", text.Text);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    private static (string launcher, string main) BuildOutputs()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MakinoMCP.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var config = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
        return (Path.Combine(dir!.FullName, "src", "MakinoMCP.Launcher", "bin", config, "net48"),
                Path.Combine(dir.FullName, "src", "MakinoMCP", "bin", config, "net10.0-windows"));
    }

    private static void CopyDir(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var f in Directory.GetFiles(source)) File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), true);
        foreach (var d in Directory.GetDirectories(source)) CopyDir(d, Path.Combine(dest, Path.GetFileName(d)));
    }
}
