using System.Text.Json.Nodes;
using MakinoMCP.Setup;
using Xunit;

namespace MakinoMCP.Tests;

public class CoworkConfigTests
{
    private const string Launcher = @"\\server\Tools\Make It\MakinoMCP.exe";

    private const string WithOthers = """
        {
          "globalShortcut": "Ctrl+Space",
          "mcpServers": {
            "files": { "command": "npx", "args": ["-y", "@modelcontextprotocol/server-filesystem", "C:\\Data"], "env": { "A": "1" } },
            "weather": { "command": "C:\\Tools\\weather.exe" }
          },
          "preferences": { "menuBarEnabled": false, "list": [1, 2.5, null, true, "ü"] }
        }
        """;

    private static JsonObject Makino(string json) => JsonNode.Parse(json)!["mcpServers"]!["makino"]!.AsObject();

    private static JsonNode WithoutMakino(string json)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        root["mcpServers"]?.AsObject().Remove("makino");
        return root;
    }

    // ------------------------------------------------------------------ pure JSON

    [Theory]
    [InlineData("")]
    [InlineData("  \r\n")]
    [InlineData("{}")]
    public void Add_to_an_empty_file(string before)
    {
        var after = CoworkConfig.Add(before, Launcher)!;
        var makino = Makino(after);
        Assert.Equal(Launcher, makino["command"]!.GetValue<string>());
        Assert.Equal("serve", makino["args"]![0]!.GetValue<string>());
        Assert.Single(makino["args"]!.AsArray());
    }

    [Fact]
    public void Add_keeps_everything_else()
    {
        var after = CoworkConfig.Add(WithOthers, Launcher)!;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(WithOthers), WithoutMakino(after)));
        Assert.Equal(Launcher, Makino(after)["command"]!.GetValue<string>());
    }

    [Fact]
    public void Add_writes_paths_and_accents_readably()
    {
        var after = CoworkConfig.Add(WithOthers, @"\\server\Fräse+Co\MakinoMCP.exe")!;
        Assert.Contains(@"Fräse+Co", after);
        Assert.Contains("\"ü\"", after);
    }

    [Fact]
    public void Add_replaces_an_existing_makino_entry()
    {
        var old = CoworkConfig.Add(WithOthers, @"C:\Old\MakinoMCP.exe")!;
        var after = CoworkConfig.Add(old, Launcher)!;
        Assert.Equal(Launcher, Makino(after)["command"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(WithOthers), WithoutMakino(after)));
    }

    [Fact]
    public void Add_returns_null_when_the_entry_is_already_right()
    {
        var once = CoworkConfig.Add(WithOthers, Launcher)!;
        Assert.Null(CoworkConfig.Add(once, Launcher));
    }

    [Fact]
    public void Remove_leaves_the_other_servers()
    {
        var with = CoworkConfig.Add(WithOthers, Launcher)!;
        var after = CoworkConfig.Remove(with)!;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(WithOthers), JsonNode.Parse(after)));
    }

    [Fact]
    public void Remove_returns_null_when_there_is_nothing_to_remove()
    {
        Assert.Null(CoworkConfig.Remove(WithOthers));
        Assert.Null(CoworkConfig.Remove(""));
        Assert.Null(CoworkConfig.Remove("""{ "preferences": {} }"""));
    }

    [Fact]
    public void HasEntry()
    {
        Assert.False(CoworkConfig.HasEntry(WithOthers));
        Assert.True(CoworkConfig.HasEntry(CoworkConfig.Add(WithOthers, Launcher)!));
        Assert.False(CoworkConfig.HasEntry(""));
        Assert.False(CoworkConfig.HasEntry("{ not json"));
    }

    [Theory]
    [InlineData("{ \"mcpServers\": { ")]
    [InlineData("{ \"a\": 1, }")]
    [InlineData("// comment\n{}")]
    [InlineData("[1, 2]")]
    [InlineData("{ \"mcpServers\": [] }")]
    [InlineData("{ \"mcpServers\": { \"makino\": 1 } } trailing")]
    public void Invalid_json_is_refused(string json)
    {
        Assert.Throws<InvalidCoworkConfigException>(() => CoworkConfig.Add(json, Launcher));
        Assert.Throws<InvalidCoworkConfigException>(() => CoworkConfig.Remove(json));
    }

    [Theory]
    [InlineData("""{ "a": 1, "a": 2 }""")]
    [InlineData("""{ "mcpServers": { "x": { "command": "a" }, "x": { "command": "b" } } }""")]
    [InlineData("""{ "mcpServers": { "makino": { "command": "a", "command": "b" } } }""")]
    [InlineData("""{ "p": [ { "deep": { "k": 1, "k": 2 } } ] }""")]
    public void A_name_listed_twice_is_refused_in_plain_words(string json)
    {
        var e = Assert.Throws<InvalidCoworkConfigException>(() => CoworkConfig.Add(json, Launcher));
        Assert.Equal("it lists a name twice", e.Message);
        Assert.Throws<InvalidCoworkConfigException>(() => CoworkConfig.Remove(json));
        Assert.False(CoworkConfig.HasEntry(json));
    }

    [Fact]
    public void Names_differing_only_in_case_are_different_names()
    {
        Assert.NotNull(CoworkConfig.Add("""{ "a": 1, "A": 2 }""", Launcher));
    }

    // ------------------------------------------------------------------ files

    [Fact]
    public void A_file_with_a_name_listed_twice_is_left_untouched()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, CoworkConfig.FileName);
        const string twice = """{ "mcpServers": { "makino": { "command": "x" }, "makino": { "command": "y" } } }""";
        File.WriteAllText(path, twice);

        var add = CoworkConfig.AddToFile(path, Launcher, Now);
        var remove = CoworkConfig.RemoveFromFile(path, Now);

        Assert.Equal(FileChange.Invalid, add.Change);
        Assert.Equal($"{path} wasn't changed: it lists a name twice.", add.Problem);
        Assert.Equal(FileChange.Invalid, remove.Change);
        Assert.Equal(twice, File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(dir.Path));
        Assert.False(CoworkConfig.AnyHasEntry([path]));
    }

    private static readonly DateTime Now = new(2026, 11, 3, 14, 5, 9);

    [Fact]
    public void AddToFile_backs_up_then_writes()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, CoworkConfig.FileName);
        File.WriteAllText(path, WithOthers);

        var outcome = CoworkConfig.AddToFile(path, Launcher, Now);

        Assert.Equal(FileChange.Changed, outcome.Change);
        var backup = path + ".bak-20261103140509";
        Assert.Equal(backup, outcome.BackupPath);
        Assert.Equal(WithOthers, File.ReadAllText(backup));
        Assert.Equal(Launcher, Makino(File.ReadAllText(path))["command"]!.GetValue<string>());
        // Only the config and its backup: the temp file is gone.
        Assert.Equal(2, Directory.GetFiles(dir.Path).Length);
    }

    [Fact]
    public void A_second_backup_in_the_same_second_gets_its_own_name()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, CoworkConfig.FileName);
        File.WriteAllText(path, WithOthers);

        CoworkConfig.AddToFile(path, Launcher, Now);
        var second = CoworkConfig.RemoveFromFile(path, Now);

        Assert.Equal(FileChange.Changed, second.Change);
        Assert.Equal(path + ".bak-20261103140509-2", second.BackupPath);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(WithOthers), JsonNode.Parse(File.ReadAllText(path))));
    }

    [Fact]
    public void AddToFile_creates_a_missing_file_without_a_backup()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, CoworkConfig.FileName);

        var outcome = CoworkConfig.AddToFile(path, Launcher, Now);

        Assert.Equal(FileChange.Changed, outcome.Change);
        Assert.Null(outcome.BackupPath);
        Assert.Single(Directory.GetFiles(dir.Path));
        Assert.True(CoworkConfig.HasEntry(File.ReadAllText(path)));
    }

    [Fact]
    public void Invalid_file_is_left_untouched()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, CoworkConfig.FileName);
        const string broken = "{ \"mcpServers\": { \"x\": ";
        File.WriteAllText(path, broken);

        var add = CoworkConfig.AddToFile(path, Launcher, Now);
        var remove = CoworkConfig.RemoveFromFile(path, Now);

        Assert.Equal(FileChange.Invalid, add.Change);
        Assert.Equal(FileChange.Invalid, remove.Change);
        Assert.Equal(broken, File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(dir.Path)); // no backup, no temp
    }

    [Fact]
    public void Unchanged_file_is_not_rewritten_or_backed_up()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, CoworkConfig.FileName);
        File.WriteAllText(path, WithOthers);

        Assert.Equal(FileChange.Unchanged, CoworkConfig.RemoveFromFile(path, Now).Change);
        Assert.Equal(FileChange.Unchanged, CoworkConfig.RemoveFromFile(Path.Combine(dir.Path, "missing", CoworkConfig.FileName), Now).Change);
        Assert.Single(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public void Folders_finds_the_classic_and_store_installs_that_exist()
    {
        using var dir = new TempDir();
        var appData = Path.Combine(dir.Path, "Roaming");
        var local = Path.Combine(dir.Path, "Local");
        Directory.CreateDirectory(Path.Combine(appData, "Claude"));
        Directory.CreateDirectory(Path.Combine(local, "Packages", "Claude_abc123", "LocalCache", "Roaming", "Claude"));
        Directory.CreateDirectory(Path.Combine(local, "Packages", "Claude_noconfig", "LocalCache"));
        Directory.CreateDirectory(Path.Combine(local, "Packages", "Other_abc", "LocalCache", "Roaming", "Claude"));

        var folders = CoworkConfig.Folders(appData, local);

        Assert.Equal(
            [Path.Combine(appData, "Claude"), Path.Combine(local, "Packages", "Claude_abc123", "LocalCache", "Roaming", "Claude")],
            folders);
    }

    [Fact]
    public void Folders_finds_the_data_folder_a_store_install_moved_out_of_its_package()
    {
        using var dir = new TempDir();
        var local = Path.Combine(dir.Path, "Local");
        Directory.CreateDirectory(Path.Combine(local, "Packages", "Claude_abc123", "LocalCache", "Roaming", "Claude"));
        Directory.CreateDirectory(Path.Combine(local, "Claude-Data", "claude_abc123"));
        Directory.CreateDirectory(Path.Combine(local, "Claude-Data", "Other_abc"));

        var folders = CoworkConfig.Folders(Path.Combine(dir.Path, "Roaming"), local);

        Assert.Equal(
            [Path.Combine(local, "Packages", "Claude_abc123", "LocalCache", "Roaming", "Claude"), Path.Combine(local, "Claude-Data", "claude_abc123")],
            folders);
    }

    [Fact]
    public void Folders_empty_when_nothing_is_installed()
    {
        using var dir = new TempDir();
        Assert.Empty(CoworkConfig.Folders(Path.Combine(dir.Path, "a"), Path.Combine(dir.Path, "b")));
        Assert.Empty(CoworkConfig.Folders(null, null));
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "makinomcp-cowork-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }
}
