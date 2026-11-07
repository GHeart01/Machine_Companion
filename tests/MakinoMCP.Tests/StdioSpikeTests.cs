using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace MakinoMCP.Tests;

// The real launcher in temp install folders, driven through an MCP client. A copy on this PC runs its build where it
// is; a copy on the network (reached here through \\127.0.0.1\C$) installs itself on the PC and hands over to that copy.
public class StdioSpikeTests
{
    private static readonly string[] ToolNames =
    [
        "explain_ade_file", "find_serial", "get_alarm_summary", "get_alarms", "get_attention", "get_cell_overview", "get_macro_values", "get_orders", "get_pallet_diagnosis", "get_pallets",
        "get_part_routing", "get_production", "get_recent_activity", "get_tool_alarms", "get_tool_changes", "get_tool_movements", "get_tools", "get_utilization", "get_work_set", "list_files",
        "list_shared_folders", "read_file", "search_files",
    ];

    private const string Marker = "{ \"source\": \"github\", \"repository\": \"GHeart01/Machine_Companion\" }";
    private const string UpdatesOff = "{ \"updates\": \"off\" }";

    private sealed class Folders : IAsyncDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "makinomcp-spike-" + Guid.NewGuid().ToString("N"));
        public string LocalAppData => Path.Combine(Root, "localappdata");
        public string PcInstall => Path.Combine(LocalAppData, "Programs", "MakinoMCP");
        public Folders() => Directory.CreateDirectory(LocalAppData);

        /// <summary>A launcher folder at dir: launcher, current.txt and the build, optionally marker and settings.</summary>
        public string Layout(string dir, string? current = "0.0.0-test", bool marker = false, string? settings = UpdatesOff, string? buildName = null)
        {
            var (launcherOut, mainOut) = BuildOutputs();
            CopyDir(launcherOut, dir);
            if (current != null) File.WriteAllText(Path.Combine(dir, "current.txt"), current);
            CopyDir(mainOut, Path.Combine(dir, "versions", buildName ?? current ?? "0.0.0-test"));
            if (marker) File.WriteAllText(Path.Combine(dir, "MakinoMCP.update.json"), Marker);
            // Tests never ask GitHub: updates are off unless a test says otherwise.
            if (settings != null) File.WriteAllText(Path.Combine(dir, "MakinoMCP.settings.json"), settings);
            return dir;
        }

        public StdioClientTransport Transport(string launcherDir) => new(new StdioClientTransportOptions
        {
            Name = "makino-spike",
            Command = Path.Combine(launcherDir, "MakinoMCP.exe"),
            Arguments = ["serve"],
            EnvironmentVariables = new Dictionary<string, string?> { ["LOCALAPPDATA"] = LocalAppData },
        });

        public List<string> Running() => ProcessesUnder(Root).Select(p => p.MainModule!.FileName).ToList();

        public async ValueTask DisposeAsync() => await CleanUp(Root);
    }

    // \\127.0.0.1\C$\... for a local path: a real network path to the same folder (needs the admin share).
    private static string? Unc(string path)
    {
        var unc = @"\\127.0.0.1\" + path[0] + "$" + path.Substring(2);
        try { return Directory.Exists(unc) ? unc : null; } catch { return null; }
    }

    private static bool RunsFrom(List<string> running, string dir) =>
        running.Any(f => f.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                         && f.EndsWith("MakinoMCP.exe", StringComparison.OrdinalIgnoreCase)
                         && f.Contains(Path.DirectorySeparatorChar + "versions" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public async Task Launcher_serves_tools_over_stdio()
    {
        await using var f = new Folders();
        var install = f.Layout(Path.Combine(f.Root, "install"));
        await using (var client = await McpClient.CreateAsync(f.Transport(install)))
        {
            var tools = await client.ListToolsAsync();
            Assert.Equal(ToolNames.Order(), tools.Select(t => t.Name).Order());
            Assert.Contains("get_cell_overview", client.ServerInstructions);
            // The ToolContext and CancellationToken parameters come from the server, not from Claude.
            var alarms = tools.Single(t => t.Name == "get_alarms").JsonSchema.GetRawText();
            Assert.Contains("\"openOnly\"", alarms);
            Assert.DoesNotContain("context", alarms, StringComparison.OrdinalIgnoreCase);

            // Only "updates": "off" in the settings file: every tool still answers, with what to do.
            var result = await client.CallToolAsync("get_cell_overview");
            var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
            Assert.Contains("\"error\"", text.Text);

            // The build runs where it is, never from a copy.
            Assert.True(RunsFrom(f.Running(), install));
        }
        Assert.False(Directory.Exists(Path.Combine(f.LocalAppData, "MakinoMCP", "app")));
        Assert.False(Directory.Exists(f.PcInstall));
    }

    [Fact]
    public async Task Release_zip_install_runs_in_place()
    {
        await using var f = new Folders();
        var install = f.Layout(Path.Combine(f.Root, "install"), marker: true);
        await using (var client = await McpClient.CreateAsync(f.Transport(install)))
        {
            Assert.Contains("get_cell_overview", (await client.ListToolsAsync()).Select(t => t.Name));
            Assert.True(RunsFrom(f.Running(), install));
        }
        Assert.False(File.Exists(Path.Combine(f.LocalAppData, "MakinoMCP", "update-state.json")));
    }

    // Updates on but checked an hour ago: no update process, no request to GitHub.
    [Fact]
    public async Task Release_zip_install_waits_six_hours_between_checks()
    {
        await using var f = new Folders();
        var install = f.Layout(Path.Combine(f.Root, "install"), marker: true, settings: null);
        var state = Path.Combine(f.LocalAppData, "MakinoMCP", "update-state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(state)!);
        var stateText = "{\"lastAttemptUtc\":\"" + DateTime.UtcNow.AddHours(-1).ToString("yyyy-MM-ddTHH:mm:ssZ") + "\",\"result\":\"up-to-date\"}";
        File.WriteAllText(state, stateText);

        await using (var client = await McpClient.CreateAsync(f.Transport(install)))
            Assert.NotEmpty(await client.ListToolsAsync());
        await Task.Delay(1500);
        Assert.Equal(stateText, File.ReadAllText(state));
    }

    // The developer layout: the app in app\ beside the launcher, no current.txt.
    [Fact]
    public async Task Dev_layout_runs_app_folder()
    {
        await using var f = new Folders();
        var dir = Path.Combine(f.Root, "dev");
        var (launcherOut, mainOut) = BuildOutputs();
        CopyDir(launcherOut, dir);
        CopyDir(mainOut, Path.Combine(dir, "app"));
        await using var client = await McpClient.CreateAsync(f.Transport(dir));
        Assert.NotEmpty(await client.ListToolsAsync());
        Assert.Contains(f.Running(), p => p.EndsWith(Path.Combine("dev", "app", "MakinoMCP.exe"), StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("0.0.0-gone")]
    [InlineData(@"..\..\escape")]
    public async Task Missing_or_invalid_build_runs_newest(string current)
    {
        await using var f = new Folders();
        var install = f.Layout(Path.Combine(f.Root, "install"), current: current, buildName: "0.0.0-there");
        await using var client = await McpClient.CreateAsync(f.Transport(install));
        Assert.NotEmpty(await client.ListToolsAsync());
        Assert.Contains(f.Running(), p => p.EndsWith(Path.Combine("versions", "0.0.0-there", "MakinoMCP.exe"), StringComparison.OrdinalIgnoreCase));
    }

    // Copies older versions made in %LOCALAPPDATA%\MakinoMCP\app are removed once nothing runs from them.
    [Fact]
    public async Task Old_cache_is_tidied()
    {
        await using var f = new Folders();
        var install = f.Layout(Path.Combine(f.Root, "install"));
        var app = Path.Combine(f.LocalAppData, "MakinoMCP", "app");
        Directory.CreateDirectory(Path.Combine(app, "0.6.0-old"));
        File.WriteAllText(Path.Combine(app, "0.6.0-old", "MakinoMCP.exe"), "old copy");

        await using (var client = await McpClient.CreateAsync(f.Transport(install)))
            Assert.NotEmpty(await client.ListToolsAsync());
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline && Directory.Exists(app)) await Task.Delay(200);
        Assert.False(Directory.Exists(app));
    }

    // A PC that already has its own copy: the share's launcher hands the session to it.
    [SkippableFact]
    public async Task Share_hands_over_to_the_pcs_copy()
    {
        await using var f = new Folders();
        var share = Unc(f.Layout(Path.Combine(f.Root, "share"), settings: null));
        Skip.If(share is null, @"\\127.0.0.1\C$ isn't reachable here");
        f.Layout(f.PcInstall, current: "0.0.0-pc", marker: true);

        await using (var client = await McpClient.CreateAsync(f.Transport(share!)))
        {
            Assert.NotEmpty(await client.ListToolsAsync());
            Assert.True(RunsFrom(f.Running(), f.PcInstall), "the session should run the PC's copy");
        }
    }

    // The first start from a share on a PC: served from the share, while the PC's copy installs in the background.
    [SkippableFact]
    public async Task Share_serves_first_and_installs_on_the_pc()
    {
        await using var f = new Folders();
        var share = Unc(f.Layout(Path.Combine(f.Root, "share"), settings: UpdatesOff));
        Skip.If(share is null, @"\\127.0.0.1\C$ isn't reachable here");

        await using (var client = await McpClient.CreateAsync(f.Transport(share!)))
            Assert.NotEmpty(await client.ListToolsAsync());

        var installed = Path.Combine(f.PcInstall, "versions", "0.0.0-test", "MakinoMCP.exe");
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline && !File.Exists(Path.Combine(f.PcInstall, "current.txt"))) await Task.Delay(200);
        Assert.True(File.Exists(installed), "the PC's copy was never installed");
        Assert.True(File.Exists(Path.Combine(f.PcInstall, "MakinoMCP.update.json")));
        Assert.Contains("off", File.ReadAllText(Path.Combine(f.PcInstall, "MakinoMCP.settings.json")));

        // The next start runs the PC's copy.
        await using (var client = await McpClient.CreateAsync(f.Transport(share!)))
        {
            Assert.NotEmpty(await client.ListToolsAsync());
            Assert.True(RunsFrom(f.Running(), f.PcInstall));
        }
    }

    // Opening MakinoMCP.exe on the share installs it on the PC and opens the PC copy's setup window.
    [SkippableFact]
    public async Task Share_setup_installs_and_opens_the_pcs_setup()
    {
        await using var f = new Folders();
        var share = Unc(f.Layout(Path.Combine(f.Root, "share"), settings: UpdatesOff));
        Skip.If(share is null, @"\\127.0.0.1\C$ isn't reachable here");

        var psi = new System.Diagnostics.ProcessStartInfo(Path.Combine(share!, "MakinoMCP.exe")) { UseShellExecute = false };
        psi.Environment["LOCALAPPDATA"] = f.LocalAppData;
        psi.Environment["MAKINOMCP_NO_WINDOW"] = "1"; // no progress window in tests
        using (var p = System.Diagnostics.Process.Start(psi)!)
        {
            Assert.True(p.WaitForExit(120_000));
            Assert.Equal(0, p.ExitCode);
        }
        Assert.True(File.Exists(Path.Combine(f.PcInstall, "versions", "0.0.0-test", "MakinoMCP.exe")));
        // The PC's launcher started its setup window (closed again by the clean-up).
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline && !RunsFrom(f.Running(), f.PcInstall)) await Task.Delay(200);
        Assert.True(RunsFrom(f.Running(), f.PcInstall), "the PC copy's setup window should be open");
    }

    // A detached process may still be running and holds files in the layout: wait for it (kill after a timeout),
    // then delete, retrying while Windows releases the executables. Setup windows never exit on their own.
    private static async Task CleanUp(string root)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline && ProcessesUnder(root).Any()) await Task.Delay(200);
        foreach (var p in ProcessesUnder(root))
        {
            try { p.Kill(true); p.WaitForExit(5000); } catch { }
        }
        for (var i = 0; i < 40; i++)
        {
            try { Directory.Delete(root, true); return; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { await Task.Delay(250); }
        }
    }

    // Processes started from root, by its local path or through \\127.0.0.1\C$.
    private static List<System.Diagnostics.Process> ProcessesUnder(string root)
    {
        var unc = @"\\127.0.0.1\" + root[0] + "$" + root.Substring(2);
        var found = new List<System.Diagnostics.Process>();
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("MakinoMCP"))
        {
            try
            {
                var file = p.MainModule?.FileName;
                if (file is not null && (file.StartsWith(root, StringComparison.OrdinalIgnoreCase) || file.StartsWith(unc, StringComparison.OrdinalIgnoreCase)))
                    found.Add(p);
            }
            catch { }
        }
        return found;
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
