using Xunit;
using MakinoMCP.Core.Settings;

namespace MakinoMCP.Tests;

public class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mmcp-" + Guid.NewGuid().ToString("N"));

    public SettingsStoreTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    private static string ExamplePath()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "MakinoMCP.settings.example.json"))) d = d.Parent;
        return Path.Combine(d!.FullName, "MakinoMCP.settings.example.json");
    }

    private static McpSettings Sample(params SharedFolder[] folders) =>
        new(new MakinoLogin("SRV", "Makino", false, "me", "s3cret-pw"), folders, new Limits(10, 20));

    [Fact]
    public void Load_missing_file_reports_setup_problem()
    {
        var r = SettingsStore.Load(Path.Combine(_dir, "none.json"));
        Assert.Null(r.Settings);
        Assert.Contains("run MakinoMCP.exe", r.Problem);
    }

    [Fact]
    public void Load_malformed_json_reports_problem_not_throw()
    {
        var p = Path.Combine(_dir, "bad.json");
        File.WriteAllText(p, "{ not json");
        var r = SettingsStore.Load(p);
        Assert.Null(r.Settings);
        Assert.False(string.IsNullOrWhiteSpace(r.Problem));
    }

    [Fact]
    public void Load_reads_example_file()
    {
        var r = SettingsStore.Load(ExamplePath());
        Assert.True(r.Problem is null, r.Problem);
        Assert.Equal("MAKINO-CELL-PC", r.Settings!.Makino.Server);
        Assert.Equal(2, r.Settings.Folders.Count);
        Assert.Equal(500, r.Settings.Limits.MaxRows);
        Assert.Equal(256, r.Settings.Limits.MaxFileKB);
    }

    [Fact]
    public void Load_allows_comments_and_defaults_missing_limits()
    {
        var p = Path.Combine(_dir, "c.json");
        File.WriteAllText(p, "{ // hi\n \"makino\": {\"server\":\"s\",\"database\":\"d\",\"useWindowsLogin\":true,\"user\":\"\",\"password\":\"\"}, \"folders\": [] }");
        var r = SettingsStore.Load(p);
        Assert.True(r.Problem is null, r.Problem);
        Assert.Equal(new Limits(), r.Settings!.Limits);
    }

    [Fact]
    public void Save_then_Load_round_trips()
    {
        var p = Path.Combine(_dir, "s.json");
        var s = Sample(new SharedFolder("a", @"\x\y", "d"));
        SettingsStore.Save(p, s);
        Assert.False(File.Exists(p + ".tmp"));
        var r = SettingsStore.Load(p);
        Assert.True(r.Problem is null, r.Problem);
        Assert.Equal(s.Makino, r.Settings!.Makino);
        Assert.Equal(s.Limits, r.Settings.Limits);
        Assert.Equal(s.Folders, r.Settings.Folders);
    }

    [Fact]
    public void Save_overwrites_existing_file()
    {
        var p = Path.Combine(_dir, "s.json");
        SettingsStore.Save(p, Sample());
        SettingsStore.Save(p, Sample() with { Limits = new Limits(1, 2) });
        Assert.Equal(1, SettingsStore.Load(p).Settings!.Limits.MaxRows);
    }

    [Fact]
    public void Failed_save_leaves_no_temp_file_with_the_password()
    {
        // File.Replace fails: the settings file is held open by someone else.
        var locked = Path.Combine(_dir, "locked.json");
        SettingsStore.Save(locked, Sample());
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.NotNull(Record.Exception(() => SettingsStore.Save(locked, Sample())));
        }
        Assert.False(File.Exists(locked + ".tmp"));

        // File.Move fails: a folder has the settings file's name.
        var folder = Path.Combine(_dir, "folder.json");
        Directory.CreateDirectory(folder);
        Assert.NotNull(Record.Exception(() => SettingsStore.Save(folder, Sample())));
        Assert.False(File.Exists(folder + ".tmp"));
    }

    [Fact]
    public void DefaultPath_prefers_settings_argument()
    {
        Assert.Equal(@"C:\x\a.json", SettingsStore.DefaultPath(["serve", "--settings", @"C:\x\a.json"]));
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "MakinoMCP.settings.json"), SettingsStore.DefaultPath([]));
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "MakinoMCP.settings.json"), SettingsStore.DefaultPath(["--settings"]));
    }

    [Fact]
    public void IsReady_false_when_login_empty()
    {
        Assert.True(Sample().IsReady);
        var empty = new MakinoLogin("", "", false, "", "");
        Assert.False((Sample() with { Makino = empty }).IsReady);
        Assert.False((Sample() with { Makino = Sample().Makino with { User = "" } }).IsReady);
        Assert.True((Sample() with { Makino = Sample().Makino with { User = "", UseWindowsLogin = true } }).IsReady);
    }

    [Fact]
    public void Folder_names_must_be_unique()
    {
        var p = Path.Combine(_dir, "d.json");
        SettingsStore.Save(p, Sample(new SharedFolder("dup", "a", ""), new SharedFolder("DUP", "b", "")));
        var r = SettingsStore.Load(p);
        Assert.Null(r.Settings);
        Assert.Contains("dup", r.Problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Password_never_in_ToString_or_problem()
    {
        var s = Sample();
        Assert.DoesNotContain("s3cret-pw", s.ToString());
        Assert.DoesNotContain("s3cret-pw", s.Makino.ToString());
        var p = Path.Combine(_dir, "pw.json");
        File.WriteAllText(p, "{\"makino\":{\"password\":\"s3cret-pw\"}, \"folders\": \"oops\"}");
        Assert.DoesNotContain("s3cret-pw", SettingsStore.Load(p).Problem ?? "");
    }

    [Fact]
    public void Load_MissingUpdates_DefaultsToAuto()
    {
        var p = Path.Combine(_dir, "u.json");
        File.WriteAllText(p, "{ \"makino\": {\"server\":\"s\",\"database\":\"d\",\"useWindowsLogin\":true,\"user\":\"\",\"password\":\"\"}, \"folders\": [] }");
        var r = SettingsStore.Load(p);
        Assert.True(r.Problem is null, r.Problem);
        Assert.Equal("auto", r.Settings!.Updates);
        Assert.False(r.Settings.UpdatesOff);
    }

    [Fact]
    public void SaveLoad_RoundTripsUpdatesOff()
    {
        var p = Path.Combine(_dir, "off.json");
        SettingsStore.Save(p, Sample() with { Updates = "off" });
        Assert.Contains("\"updates\": \"off\"", File.ReadAllText(p));
        var r = SettingsStore.Load(p);
        Assert.Equal("off", r.Settings!.Updates);
        Assert.True(r.Settings.UpdatesOff);
        Assert.True((Sample() with { Updates = "OFF" }).UpdatesOff);
    }

    [Fact]
    public void Load_UnknownUpdatesValue_KeepsValueAndIsOn()
    {
        var p = Path.Combine(_dir, "weekly.json");
        File.WriteAllText(p, "{ \"makino\": {\"server\":\"s\",\"database\":\"d\",\"useWindowsLogin\":true,\"user\":\"\",\"password\":\"\"}, \"folders\": [], \"updates\": \"weekly\" }");
        var r = SettingsStore.Load(p);
        Assert.Equal("weekly", r.Settings!.Updates);
        Assert.False(r.Settings.UpdatesOff);
    }

    [Fact]
    public void Password_never_in_ToString_with_updates()
    {
        var s = Sample() with { Updates = "off" };
        Assert.DoesNotContain("s3cret-pw", s.ToString());
        Assert.Contains("Updates = off", s.ToString());
    }

    [Fact]
    public void ConnectionString_sets_app_name_and_retry()
    {
        var cs = Sample().Makino.ConnectionString();
        Assert.Contains("MakinoMCP", cs);
        Assert.Contains("Connect Retry Count=3", cs);
        Assert.Contains("User ID=me", cs);
        var win = Sample().Makino with { UseWindowsLogin = true };
        Assert.Contains("Integrated Security=True", win.ConnectionString());
    }
}
