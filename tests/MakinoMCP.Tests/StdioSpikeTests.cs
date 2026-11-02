using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace MakinoMCP.Tests;

public class StdioSpikeTests
{
    private static readonly string[] ToolNames =
    [
        "get_alarm_summary", "get_alarms", "get_cell_overview", "get_orders", "get_pallets", "get_recent_activity",
        "get_utilization", "list_files", "list_shared_folders", "read_file", "search_files",
    ];

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
            Assert.Equal(ToolNames.Order(), tools.Select(t => t.Name).Order());
            Assert.Contains("get_cell_overview", client.ServerInstructions);
            // The ToolContext and CancellationToken parameters come from the server, not from Claude.
            var alarms = tools.Single(t => t.Name == "get_alarms").JsonSchema.GetRawText();
            Assert.Contains("\"openOnly\"", alarms);
            Assert.DoesNotContain("context", alarms, StringComparison.OrdinalIgnoreCase);

            // No settings file on the share: every tool still answers, with what to do.
            var result = await client.CallToolAsync("get_cell_overview");
            var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
            Assert.Contains("run MakinoMCP.exe", text.Text);
            Assert.Contains("\"error\"", text.Text);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Launcher_runs_newest_local_version_when_share_is_unreachable()
    {
        var root = Path.Combine(Path.GetTempPath(), "makinomcp-spike-" + Guid.NewGuid().ToString("N"));
        try
        {
            var share = Path.Combine(root, "share");
            var localAppData = Path.Combine(root, "localappdata");

            var (launcherOut, mainOut) = BuildOutputs();
            CopyDir(launcherOut, share); // no current.txt, no app\ folder: the share's content is unreachable
            CopyDir(mainOut, Path.Combine(localAppData, "MakinoMCP", "app", "0.0.0-local"));

            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "makino-spike",
                Command = Path.Combine(share, "MakinoMCP.exe"),
                Arguments = ["serve"],
                EnvironmentVariables = new Dictionary<string, string?> { ["LOCALAPPDATA"] = localAppData },
            });

            await using var client = await McpClient.CreateAsync(transport);
            var tools = await client.ListToolsAsync();
            Assert.Equal(ToolNames.Order(), tools.Select(t => t.Name).Order());
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task First_start_serves_from_the_share_and_copies_in_background()
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

            await using (var client = await McpClient.CreateAsync(NewTransport(share, localAppData)))
            {
                var tools = await client.ListToolsAsync();
                Assert.Equal(ToolNames.Order(), tools.Select(t => t.Name).Order());
            }

            var app = Path.Combine(localAppData, "MakinoMCP", "app");
            var installed = Path.Combine(app, "0.0.0-test", "MakinoMCP.exe");
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline && !File.Exists(installed)) await Task.Delay(200);
            Assert.True(File.Exists(installed), "the background copy never produced " + installed);
            Assert.Empty(Directory.GetDirectories(app, "*.tmp*"));
        }
        finally
        {
            await CleanUp(root);
        }
    }

    [Fact]
    public async Task Second_start_uses_the_local_copy()
    {
        var root = Path.Combine(Path.GetTempPath(), "makinomcp-spike-" + Guid.NewGuid().ToString("N"));
        try
        {
            var share = Path.Combine(root, "share");
            var localAppData = Path.Combine(root, "localappdata");

            var (launcherOut, mainOut) = BuildOutputs();
            CopyDir(launcherOut, share);
            File.WriteAllText(Path.Combine(share, "current.txt"), "0.0.0-test");
            // The share names the version but holds no files for it: only the local copy can serve this session,
            // and with the copy present no background copy is started.
            CopyDir(mainOut, Path.Combine(localAppData, "MakinoMCP", "app", "0.0.0-test"));

            await using (var client = await McpClient.CreateAsync(NewTransport(share, localAppData)))
            {
                var tools = await client.ListToolsAsync();
                Assert.Equal(ToolNames.Order(), tools.Select(t => t.Name).Order());
            }

            Assert.Empty(Directory.GetDirectories(Path.Combine(localAppData, "MakinoMCP", "app"), "*.tmp*"));
        }
        finally
        {
            await CleanUp(root);
        }
    }

    private static StdioClientTransport NewTransport(string share, string localAppData) => new(new StdioClientTransportOptions
    {
        Name = "makino-spike",
        Command = Path.Combine(share, "MakinoMCP.exe"),
        Arguments = ["serve"],
        EnvironmentVariables = new Dictionary<string, string?> { ["LOCALAPPDATA"] = localAppData },
    });

    private static async Task CleanUp(string root)
    {
        for (var i = 0; i < 20; i++)
        {
            try { Directory.Delete(root, true); return; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { await Task.Delay(250); }
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
