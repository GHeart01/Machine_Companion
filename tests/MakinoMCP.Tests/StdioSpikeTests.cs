using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace MakinoMCP.Tests;

public class StdioSpikeTests
{
    private static readonly string[] ToolNames =
    [
        "get_alarm_summary", "get_alarms", "get_attention", "get_cell_overview", "get_orders", "get_pallets",
        "get_part_routing", "get_production", "get_recent_activity", "get_tool_alarms", "get_tool_changes", "get_tool_movements", "get_tools", "get_utilization", "list_files",
        "list_shared_folders", "read_file", "search_files",
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
            await CleanUp(root);
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
            await CleanUp(root);
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

    [Fact]
    public async Task A_new_build_is_served_from_the_previous_local_copy_while_it_copies()
    {
        var root = Path.Combine(Path.GetTempPath(), "makinomcp-spike-" + Guid.NewGuid().ToString("N"));
        try
        {
            var share = Path.Combine(root, "share");
            var localAppData = Path.Combine(root, "localappdata");

            var (launcherOut, mainOut) = BuildOutputs();
            CopyDir(launcherOut, share);
            File.WriteAllText(Path.Combine(share, "current.txt"), "0.0.0-new");
            // The new build on the share can't run at all, so a working session proves the previous local copy served it.
            var newBuild = Directory.CreateDirectory(Path.Combine(share, "versions", "0.0.0-new")).FullName;
            File.WriteAllText(Path.Combine(newBuild, "MakinoMCP.exe"), "not a program");
            CopyDir(mainOut, Path.Combine(localAppData, "MakinoMCP", "app", "0.0.0-old"));

            await using (var client = await McpClient.CreateAsync(NewTransport(share, localAppData)))
            {
                var tools = await client.ListToolsAsync();
                Assert.Equal(ToolNames.Order(), tools.Select(t => t.Name).Order());
            }

            var copied = Path.Combine(localAppData, "MakinoMCP", "app", "0.0.0-new", "MakinoMCP.exe");
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline && !File.Exists(copied)) await Task.Delay(200);
            Assert.True(File.Exists(copied), "the new build was never copied in the background");
        }
        finally
        {
            await CleanUp(root);
        }
    }

    [Fact]
    public async Task Prefetch_does_not_hold_the_clients_stdout_open()
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
            var version = Path.Combine(share, "versions", "0.0.0-test");
            CopyDir(mainOut, version);
            var pad = Directory.CreateDirectory(Path.Combine(version, "pad")).FullName;
            for (var i = 0; i < 12000; i++) File.WriteAllText(Path.Combine(pad, $"f{i}.txt"), "x");

            var psi = new System.Diagnostics.ProcessStartInfo(Path.Combine(share, "MakinoMCP.exe"), "serve")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.Environment["LOCALAPPDATA"] = localAppData;
            using var launcher = System.Diagnostics.Process.Start(psi)!;
            var stderr = launcher.StandardError.ReadToEndAsync();
            await Task.Delay(1500);
            launcher.StandardInput.Close();
            Assert.True(launcher.WaitForExit(20000), "launcher did not exit after stdin closed");

            var eof = launcher.StandardOutput.ReadToEndAsync();
            var finished = await Task.WhenAny(eof, Task.Delay(5000));
            var prefetchStillCopying = Directory.GetDirectories(Path.Combine(localAppData, "MakinoMCP", "app"), "*.tmp*").Length > 0;
            Assert.True(finished == eof, "stdout stayed open after the launcher exited (the prefetch inherited the pipe)");
            Assert.True(prefetchStillCopying, "the copy finished too early for this test to prove anything; add more files");
        }
        finally
        {
            await CleanUp(root);
        }
    }

    [Fact]
    public async Task The_copy_survives_the_client_killing_the_sessions_process_tree()
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
            var version = Path.Combine(share, "versions", "0.0.0-test");
            CopyDir(mainOut, version);
            var pad = Directory.CreateDirectory(Path.Combine(version, "pad")).FullName;
            for (var i = 0; i < 12000; i++) File.WriteAllText(Path.Combine(pad, $"f{i}.txt"), "x");

            var psi = new System.Diagnostics.ProcessStartInfo(Path.Combine(share, "MakinoMCP.exe"), "serve")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.Environment["LOCALAPPDATA"] = localAppData;
            using var launcher = System.Diagnostics.Process.Start(psi)!;
            _ = launcher.StandardOutput.ReadToEndAsync();
            _ = launcher.StandardError.ReadToEndAsync();
            var app = Path.Combine(localAppData, "MakinoMCP", "app");
            var started = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < started && !(Directory.Exists(app) && Directory.GetDirectories(app, "*.tmp*").Length > 0))
                await Task.Delay(100);
            Assert.True(Directory.GetDirectories(app, "*.tmp*").Length > 0, "the background copy never started");

            // What an MCP client does when a session closes: end the whole tree, as taskkill /T /F does.
            launcher.Kill(entireProcessTree: true);
            launcher.WaitForExit(10000);

            var installed = Path.Combine(app, "0.0.0-test", "MakinoMCP.exe");
            var deadline = DateTime.UtcNow.AddSeconds(90);
            while (DateTime.UtcNow < deadline && !File.Exists(installed)) await Task.Delay(200);
            Assert.True(File.Exists(installed), "the copy died with the session's process tree");
        }
        finally
        {
            await CleanUp(root);
        }
    }

    [Fact]
    public async Task Setup_copies_to_this_pc_before_starting_the_local_copy()
    {
        var root = Path.Combine(Path.GetTempPath(), "makinomcp-spike-" + Guid.NewGuid().ToString("N"));
        try
        {
            var share = Path.Combine(root, "share");
            var localAppData = Path.Combine(root, "localappdata");
            Directory.CreateDirectory(localAppData);

            var (launcherOut, _) = BuildOutputs();
            CopyDir(launcherOut, share);
            File.WriteAllText(Path.Combine(share, "current.txt"), "0.0.0-test");
            // Stand-in for the main exe: cmd.exe prints its prompt, which names the folder it was started in.
            var version = Directory.CreateDirectory(Path.Combine(share, "versions", "0.0.0-test")).FullName;
            File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), Path.Combine(version, "MakinoMCP.exe"));

            var psi = new System.Diagnostics.ProcessStartInfo(Path.Combine(share, "MakinoMCP.exe"), "setup")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.Environment["LOCALAPPDATA"] = localAppData;
            psi.Environment["MAKINOMCP_NO_WINDOW"] = "1"; // tests never show the progress window
            using var launcher = System.Diagnostics.Process.Start(psi)!;
            var stdout = launcher.StandardOutput.ReadToEndAsync();
            var stderr = launcher.StandardError.ReadToEndAsync();
            launcher.StandardInput.Close();
            Assert.True(launcher.WaitForExit(30000), "launcher did not exit");

            var installed = Path.Combine(localAppData, "MakinoMCP", "app", "0.0.0-test");
            Assert.True(File.Exists(Path.Combine(installed, "MakinoMCP.exe")), "setup did not copy the version to this PC");
            Assert.Empty(Directory.GetDirectories(Path.Combine(localAppData, "MakinoMCP", "app"), "*.tmp*"));
            // The child ran from the local copy, and the copy was complete before it started (no background copy to wait for).
            Assert.Contains(installed, await stdout, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("share", (await stderr).Replace(root, ""), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await CleanUp(root);
        }
    }

    [Fact]
    public async Task Setup_falls_back_to_the_share_when_the_copy_fails()
    {
        var root = Path.Combine(Path.GetTempPath(), "makinomcp-spike-" + Guid.NewGuid().ToString("N"));
        try
        {
            var share = Path.Combine(root, "share");
            var localAppData = Path.Combine(root, "localappdata");
            Directory.CreateDirectory(localAppData);

            var (launcherOut, _) = BuildOutputs();
            CopyDir(launcherOut, share);
            File.WriteAllText(Path.Combine(share, "current.txt"), "0.0.0-test");
            var version = Directory.CreateDirectory(Path.Combine(share, "versions", "0.0.0-test")).FullName;
            File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), Path.Combine(version, "MakinoMCP.exe"));
            var locked = Path.Combine(version, "zz-locked.bin"); // copied after the exe, so the copy fails part-way
            File.WriteAllText(locked, "x");

            var psi = new System.Diagnostics.ProcessStartInfo(Path.Combine(share, "MakinoMCP.exe"), "setup")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.Environment["LOCALAPPDATA"] = localAppData;
            psi.Environment["MAKINOMCP_NO_WINDOW"] = "1";
            string output, error;
            using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                using var launcher = System.Diagnostics.Process.Start(psi)!;
                var stdout = launcher.StandardOutput.ReadToEndAsync();
                var stderr = launcher.StandardError.ReadToEndAsync();
                launcher.StandardInput.Close();
                Assert.True(launcher.WaitForExit(30000), "launcher did not exit");
                output = await stdout;
                error = await stderr;
            }

            Assert.Contains("Couldn't copy MakinoMCP to this PC", error);
            Assert.Contains("Opening it from the share instead", error);
            Assert.Contains(version, output, StringComparison.OrdinalIgnoreCase); // the share copy was started
            // The background prefetch the share run starts fails too and cleans up; wait for it.
            var app = Path.Combine(localAppData, "MakinoMCP", "app");
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline && Directory.Exists(app) && Directory.GetDirectories(app, "*.tmp*").Length > 0)
                await Task.Delay(200);
            Assert.False(Directory.Exists(app) && Directory.GetDirectories(app, "*.tmp*").Length > 0, "a .tmp folder was left behind");
            Assert.False(Directory.Exists(Path.Combine(app, "0.0.0-test")));
        }
        finally
        {
            await CleanUp(root);
        }
    }

    [Fact]
    public async Task Invalid_version_in_current_txt_falls_back_to_newest_local()
    {
        var root = Path.Combine(Path.GetTempPath(), "makinomcp-spike-" + Guid.NewGuid().ToString("N"));
        try
        {
            var share = Path.Combine(root, "share");
            var localAppData = Path.Combine(root, "localappdata");

            var (launcherOut, mainOut) = BuildOutputs();
            CopyDir(launcherOut, share);
            File.WriteAllText(Path.Combine(share, "current.txt"), @"..\..\escape");
            CopyDir(mainOut, Path.Combine(root, "escape")); // what a traversal would reach
            CopyDir(mainOut, Path.Combine(localAppData, "MakinoMCP", "app", "0.0.0-local"));

            await using (var client = await McpClient.CreateAsync(NewTransport(share, localAppData)))
            {
                var tools = await client.ListToolsAsync();
                Assert.Equal(ToolNames.Order(), tools.Select(t => t.Name).Order());
            }

            Assert.False(Directory.Exists(Path.Combine(localAppData, "escape")) || Directory.Exists(Path.Combine(localAppData, "escape.tmp")));
            Assert.Equal(["0.0.0-local"], Directory.GetDirectories(Path.Combine(localAppData, "MakinoMCP", "app")).Select(Path.GetFileName));
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

    // A detached prefetch may still be copying and holds files in the layout: wait for it (kill after a timeout),
    // then delete, retrying while Windows releases the executables.
    private static async Task CleanUp(string root)
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
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

    private static List<System.Diagnostics.Process> ProcessesUnder(string root)
    {
        var found = new List<System.Diagnostics.Process>();
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("MakinoMCP"))
        {
            try
            {
                if (p.MainModule?.FileName.StartsWith(root, StringComparison.OrdinalIgnoreCase) == true) found.Add(p);
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
