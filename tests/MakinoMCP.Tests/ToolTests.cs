using System.Reflection;
using System.Text.Json;
using MakinoMCP.Core.Folders;
using MakinoMCP.Core.Settings;
using MakinoMCP.Logging;
using MakinoMCP.Tools;
using ModelContextProtocol.Server;
using Xunit;

namespace MakinoMCP.Tests;

public sealed class ToolTests : IDisposable
{
    private const string Password = "Pa55-w0rd-xyzzy";

    private static readonly string[] SpecNames =
    [
        "get_cell_overview", "get_alarms", "get_alarm_summary", "get_utilization", "get_recent_activity", "get_orders",
        "get_pallets", "list_shared_folders", "list_files", "read_file", "search_files",
    ];

    private readonly string _base = Path.Combine(Path.GetTempPath(), "makinomcp-tools-" + Guid.NewGuid().ToString("N"));
    private readonly string _shared;
    private readonly string _logs;

    public ToolTests()
    {
        _shared = Path.Combine(_base, "shared");
        _logs = Path.Combine(_base, "logs");
        Directory.CreateDirectory(_shared);
        File.WriteAllText(Path.Combine(_shared, "cell.log"), "10:00 start\n10:05 ALARM 1012 coolant level low\n10:06 reset\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, true); }
        catch (Exception) { /* temp folder; leaving it behind is harmless */ }
    }

    private static MakinoLogin Login(string user = "your-login") =>
        new("MAKINO-CELL-PC", "Makino", false, user, Password);

    private McpSettings Settings(string user = "your-login", bool withFolder = true) =>
        new(Login(user), withFolder ? [new SharedFolder("logs", _shared, "cell logs")] : [], new Limits());

    private ToolContext Ready(FakeMakinoReader reader, FileLog? log = null)
    {
        var s = Settings();
        return new ToolContext(new SettingsLoad(s, null), reader, new SharedFolders(s.Folders, s.Limits, []), log);
    }

    private static readonly (string Name, Func<ToolContext, Task<string>> Call)[] AllTools =
    [
        ("get_cell_overview", c => CellTools.GetCellOverview(c, CancellationToken.None)),
        ("get_alarms", c => CellTools.GetAlarms(c, ct: CancellationToken.None)),
        ("get_alarm_summary", c => CellTools.GetAlarmSummary(c, ct: CancellationToken.None)),
        ("get_utilization", c => CellTools.GetUtilization(c, ct: CancellationToken.None)),
        ("get_recent_activity", c => CellTools.GetRecentActivity(c, ct: CancellationToken.None)),
        ("get_orders", c => CellTools.GetOrders(c, ct: CancellationToken.None)),
        ("get_pallets", c => CellTools.GetPallets(c, CancellationToken.None)),
        ("list_shared_folders", c => FolderTools.ListSharedFolders(c, CancellationToken.None)),
        ("list_files", c => FolderTools.ListFiles(c, "logs", ct: CancellationToken.None)),
        ("read_file", c => FolderTools.ReadFile(c, "logs", "cell.log", ct: CancellationToken.None)),
        ("search_files", c => FolderTools.SearchFiles(c, "logs", "ALARM", ct: CancellationToken.None)),
    ];

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private static string ErrorOf(string json)
    {
        var root = Parse(json);
        Assert.True(root.TryGetProperty("error", out var error), $"expected an error, got {json}");
        return error.GetString()!;
    }

    private static void AssertOk(string json) =>
        Assert.False(Parse(json).TryGetProperty("error", out _), $"expected a result, got {json}");

    [Fact]
    public async Task Every_tool_returns_setup_message_when_not_ready()
    {
        var missing = new ToolContext(
            new SettingsLoad(null, @"Settings file not found at C:\Apps\MakinoMCP.settings.json."), null, null);
        var emptyLogin = new ToolContext(new SettingsLoad(Settings(user: "", withFolder: false), null), null, null);

        Assert.Equal(SpecNames.Length, AllTools.Length);
        foreach (var (_, call) in AllTools)
        {
            var error = ErrorOf(await call(missing));
            Assert.StartsWith(ToolContext.NotSetUp, error);
            Assert.Contains("run MakinoMCP.exe", error);
            Assert.Contains("Settings file not found", error); // the specific problem is appended

            Assert.StartsWith(ToolContext.NotSetUp, ErrorOf(await call(emptyLogin)));
        }
    }

    [Fact]
    public async Task Db_failure_returns_error_json_without_password()
    {
        var context = Ready(new FakeMakinoReader
        {
            Fail = () => new InvalidOperationException($"Login failed for 'your-login' with Password={Password}; server down"),
        });

        foreach (var (_, call) in AllTools.Take(7))
        {
            var json = await call(context);
            var error = ErrorOf(json);
            Assert.StartsWith("Could not read the Makino cell database", error);
            Assert.DoesNotContain(Password, json);
        }
    }

    [Fact]
    public async Task Folder_tools_work_when_db_down()
    {
        var context = Ready(new FakeMakinoReader { Fail = () => new TimeoutException("cell PC unreachable") });

        Assert.StartsWith("Could not read", ErrorOf(await CellTools.GetCellOverview(context, CancellationToken.None)));

        var folders = Parse(await FolderTools.ListSharedFolders(context, CancellationToken.None));
        var folder = Assert.Single(folders.GetProperty("folders").EnumerateArray());
        Assert.Equal("logs", folder.GetProperty("name").GetString());
        Assert.True(folder.GetProperty("reachable").GetBoolean());

        var files = Parse(await FolderTools.ListFiles(context, "logs", ct: CancellationToken.None));
        Assert.Contains(files.GetProperty("entries").EnumerateArray(), e => e.GetProperty("path").GetString() == "cell.log");

        var read = Parse(await FolderTools.ReadFile(context, "logs", "cell.log", ct: CancellationToken.None));
        Assert.Contains("coolant level low", read.GetProperty("text").GetString());

        var hits = Parse(await FolderTools.SearchFiles(context, "logs", "ALARM", ct: CancellationToken.None));
        var hit = Assert.Single(hits.GetProperty("hits").EnumerateArray());
        Assert.Equal(2, hit.GetProperty("line").GetInt32());
    }

    [Fact]
    public async Task Folder_tools_work_when_login_is_empty()
    {
        var settingsDir = Path.Combine(_base, "app");
        var settingsPath = Path.Combine(settingsDir, SettingsStore.FileName);
        SettingsStore.Save(settingsPath, Settings(user: ""));

        var context = ToolContext.Load(settingsPath, null);

        Assert.Null(context.Reader);
        Assert.NotNull(context.Folders);
        Assert.StartsWith(ToolContext.NotSetUp, ErrorOf(await CellTools.GetAlarms(context, ct: CancellationToken.None)));
        AssertOk(await FolderTools.ReadFile(context, "logs", "cell.log", ct: CancellationToken.None));
    }

    [Fact]
    public async Task Folder_tools_never_read_the_settings_file()
    {
        // The settings file sits inside the shared folder: the program's own folder is shared by mistake.
        var settingsPath = Path.Combine(_shared, SettingsStore.FileName);
        SettingsStore.Save(settingsPath, Settings());

        var context = ToolContext.Load(settingsPath, null);

        var json = await FolderTools.ReadFile(context, "logs", SettingsStore.FileName, ct: CancellationToken.None);
        ErrorOf(json);
        Assert.DoesNotContain(Password, json);
        Assert.DoesNotContain(Password, await FolderTools.SearchFiles(context, "logs", "Pa55", ct: CancellationToken.None));
    }

    [Fact]
    public async Task Overview_json_has_serverTime_and_words()
    {
        var json = await CellTools.GetCellOverview(Ready(new FakeMakinoReader()), CancellationToken.None);
        var root = Parse(json);

        Assert.Equal(FakeMakinoReader.Now, root.GetProperty("serverTime").GetDateTime());
        var mc1 = root.GetProperty("devices")[0];
        Assert.Equal("MC1", mc1.GetProperty("name").GetString());
        Assert.Equal("machine", mc1.GetProperty("type").GetString());
        Assert.Equal("processing", mc1.GetProperty("pallets")[0].GetProperty("status").GetString());
        Assert.Contains("\"lastActivity\"", json); // camelCase
    }

    [Fact]
    public async Task Overview_json_has_dashboard_state_cart_and_history_days()
    {
        var root = Parse(await CellTools.GetCellOverview(Ready(new FakeMakinoReader()), CancellationToken.None));

        Assert.Equal(90, root.GetProperty("historyDays").GetInt32());
        var mc1 = root.GetProperty("devices")[0];
        Assert.Equal("busy", mc1.GetProperty("status").GetString());
        Assert.False(mc1.GetProperty("recoveryRequest").GetBoolean());
        Assert.Equal("lacking tools", mc1.GetProperty("toolStatus").GetString());
        Assert.Equal("run", mc1.GetProperty("cncRun").GetString());
        Assert.Equal("released", mc1.GetProperty("operatorCall").GetString());
        Assert.Equal("machining", mc1.GetProperty("runJob").GetString());
        Assert.Equal("O1234", mc1.GetProperty("program").GetProperty("main").GetString());
        Assert.Equal("O0100", mc1.GetProperty("program").GetProperty("executing").GetString());
        Assert.Equal("next front", mc1.GetProperty("pallets")[1].GetProperty("position").GetString());

        var cart = root.GetProperty("devices")[1];
        Assert.Equal("load/unload", cart.GetProperty("action").GetString());
        Assert.Equal("WSS1", cart.GetProperty("transportFrom").GetString());
        Assert.Equal("MC1", cart.GetProperty("transportTo").GetString());
        Assert.Equal("buffer 1", cart.GetProperty("pallets")[0].GetProperty("position").GetString());
        Assert.False(cart.TryGetProperty("toolStatus", out _)); // new fields that don't apply are left out
        Assert.False(cart.TryGetProperty("program", out _));
        // The fields of the 0.1 shape are all still there.
        foreach (var field in new[] { "name", "type", "connected", "emergency", "mode", "action", "subAction", "pallets",
                     "openAlarms", "openWarnings", "lastActivity", "notAvailable" })
            Assert.True(cart.TryGetProperty(field, out _), field);
    }

    [Fact]
    public async Task Pallets_json_has_work_status()
    {
        var pallet = Parse(await CellTools.GetPallets(Ready(new FakeMakinoReader()), CancellationToken.None))
            .GetProperty("pallets")[0];
        Assert.Equal("waiting for machining", pallet.GetProperty("status").GetString());
        Assert.Equal("not completed", pallet.GetProperty("workStatus").GetString());
    }

    [Fact]
    public async Task Unknown_device_lists_names()
    {
        var context = Ready(new FakeMakinoReader());

        var error = ErrorOf(await CellTools.GetAlarms(context, device: "MC9", ct: CancellationToken.None));

        Assert.DoesNotContain("Could not read the Makino cell database", error); // passed through as-is
        Assert.Contains("MC9", error);
        Assert.Contains("MC1", error);
        Assert.Contains("Vehicle 1", error);
    }

    [Fact]
    public void Tool_names_match_spec()
    {
        var names = typeof(ToolContext).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>())
            .OfType<McpServerToolAttribute>()
            .Select(a => a.Name)
            .Order()
            .ToArray();

        Assert.Equal(SpecNames.Order(), names);
    }

    [Fact]
    public async Task Slow_database_returns_plain_timeout_error()
    {
        var context = new ToolContext(new SettingsLoad(Settings(), null), new FakeMakinoReader { Hang = true }, null)
        {
            DatabaseBudget = TimeSpan.FromMilliseconds(200),
        };

        var started = DateTime.UtcNow;
        var error = ErrorOf(await CellTools.GetCellOverview(context, CancellationToken.None));

        Assert.StartsWith("the cell database didn't answer within ", error);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
        var defaults = new ToolContext(new SettingsLoad(null, null), null, null);
        Assert.Equal(TimeSpan.FromSeconds(45), defaults.DatabaseBudget);
        Assert.Equal(TimeSpan.FromSeconds(20), defaults.FolderBudget);
    }

    [Fact]
    public void Server_instructions_say_read_only_and_where_to_start()
    {
        Assert.Contains("read only", ToolContext.ServerInstructions);
        Assert.Contains("get_cell_overview", ToolContext.ServerInstructions);
        Assert.Contains("serverTime", ToolContext.ServerInstructions);
        Assert.Contains("200", ToolContext.ServerInstructions);
    }

    [Fact]
    public async Task Log_has_one_line_per_call_and_never_the_password_or_results()
    {
        var log = new FileLog(_logs);
        var ok = Ready(new FakeMakinoReader(), log);
        var failing = Ready(new FakeMakinoReader { Fail = () => new InvalidOperationException($"Password={Password}") }, log);

        await CellTools.GetOrders(ok, ct: CancellationToken.None);
        await CellTools.GetPallets(failing, CancellationToken.None);

        var file = Assert.Single(Directory.GetFiles(_logs, "makinomcp-*.log"));
        Assert.Equal($"makinomcp-{DateTime.Now:yyyyMMdd}.log", Path.GetFileName(file));
        var lines = File.ReadAllLines(file);
        Assert.Contains(lines, l => l.Contains("get_orders") && l.Contains(" ok"));
        Assert.Contains(lines, l => l.Contains("get_pallets") && l.Contains("error"));
        var all = string.Join("\n", lines);
        Assert.DoesNotContain(Password, all);
        Assert.DoesNotContain("CONTOSO-BRACKET", all); // never tool results
    }

    [Fact]
    public void Log_keeps_14_days()
    {
        Directory.CreateDirectory(_logs);
        var old = Path.Combine(_logs, $"makinomcp-{DateTime.Now.AddDays(-15):yyyyMMdd}.log");
        var recent = Path.Combine(_logs, $"makinomcp-{DateTime.Now.AddDays(-13):yyyyMMdd}.log");
        var other = Path.Combine(_logs, "notes.txt");
        File.WriteAllText(old, "x");
        File.WriteAllText(recent, "x");
        File.WriteAllText(other, "x");

        _ = new FileLog(_logs);

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(other));
    }
}
