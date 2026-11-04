using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MakinoMCP.Setup;

/// <summary>The config file is not JSON MakinoMCP can safely edit, so it is left as it is.</summary>
public sealed class InvalidCoworkConfigException(string message, Exception? inner = null) : Exception(message, inner);

public enum FileChange { Changed, Unchanged, Invalid, Failed }

/// <summary>What happened to one config file. <paramref name="Problem"/> is plain words for Invalid and Failed.</summary>
public sealed record FileOutcome(string Path, FileChange Change, string? BackupPath = null, string? Problem = null);

/// <summary>The <c>makino</c> entry in Claude desktop's (Cowork's) <c>claude_desktop_config.json</c>. Everything else in
/// the file is kept; a file that isn't a JSON object is never touched; a change is backed up first and written
/// through a temp file, so Claude never reads half a file.</summary>
public static class CoworkConfig
{
    public const string FileName = "claude_desktop_config.json";
    private const string Servers = "mcpServers";
    private const string Name = ClaudeRegistration.ServerName;

    private static readonly JsonSerializerOptions Write = new()
    {
        WriteIndented = true,
        // Paths and accents stay readable (no \u escapes); the file is read by Claude, not a browser.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ------------------------------------------------------------------ the JSON

    /// <summary>The file's text with <c>mcpServers.makino</c> starting <paramref name="launcher"/> in serve mode, or
    /// null when it already does. An empty file counts as <c>{}</c>.</summary>
    public static string? Add(string json, string launcher)
    {
        var root = ParseRoot(json);
        var servers = ServersOf(root, create: true)!;
        var entry = new JsonObject { ["command"] = launcher, ["args"] = new JsonArray("serve") };
        if (JsonNode.DeepEquals(servers[Name], entry)) return null;
        servers[Name] = entry;
        return root.ToJsonString(Write);
    }

    /// <summary>The file's text without <c>mcpServers.makino</c>, or null when it has none.</summary>
    public static string? Remove(string json)
    {
        var root = ParseRoot(json);
        var servers = ServersOf(root, create: false);
        if (servers is null || !servers.Remove(Name)) return null;
        return root.ToJsonString(Write);
    }

    /// <summary>True when the text is valid and has a <c>makino</c> server.</summary>
    public static bool HasEntry(string json)
    {
        try
        {
            return ServersOf(ParseRoot(json), create: false)?.ContainsKey(Name) == true;
        }
        catch (InvalidCoworkConfigException)
        {
            return false;
        }
    }

    private static JsonObject ParseRoot(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        JsonNode? node;
        try
        {
            // JsonNode accepts a name twice and only fails later, when the object is used: refuse it here.
            using (var doc = JsonDocument.Parse(json))
            {
                if (HasDuplicateName(doc.RootElement)) throw new InvalidCoworkConfigException("it lists a name twice");
            }
            node = JsonNode.Parse(json);
        }
        catch (JsonException e)
        {
            throw new InvalidCoworkConfigException("it isn't valid JSON", e);
        }
        return node as JsonObject ?? throw new InvalidCoworkConfigException("it isn't a JSON object");
    }

    /// <summary>True when any object, at any depth, has the same name twice (names are case-sensitive in JSON).</summary>
    private static bool HasDuplicateName(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var p in e.EnumerateObject())
                    if (!seen.Add(p.Name) || HasDuplicateName(p.Value)) return true;
                return false;
            case JsonValueKind.Array:
                return e.EnumerateArray().Any(HasDuplicateName);
            default:
                return false;
        }
    }

    private static JsonObject? ServersOf(JsonObject root, bool create)
    {
        if (!root.TryGetPropertyValue(Servers, out var node) || node is null)
        {
            if (!create) return null;
            var made = new JsonObject();
            root[Servers] = made;
            return made;
        }
        return node as JsonObject ?? throw new InvalidCoworkConfigException($"its {Servers} isn't a JSON object");
    }

    // ------------------------------------------------------------------ the files

    /// <summary>The Claude desktop config folders on this PC: the classic install's <c>%APPDATA%\Claude</c> and each
    /// Microsoft Store install's <c>%LOCALAPPDATA%\Packages\Claude_*\LocalCache\Roaming\Claude</c>. Only folders that
    /// exist: a file is created only where Claude desktop is installed.</summary>
    public static IReadOnlyList<string> Folders(string? appData, string? localAppData)
    {
        var found = new List<string>();
        if (!string.IsNullOrEmpty(appData) && Directory.Exists(Path.Combine(appData, "Claude")))
            found.Add(Path.Combine(appData, "Claude"));

        var packages = string.IsNullOrEmpty(localAppData) ? null : Path.Combine(localAppData, "Packages");
        if (packages is not null && Directory.Exists(packages))
        {
            try
            {
                foreach (var package in Directory.EnumerateDirectories(packages, "Claude_*").Order(StringComparer.OrdinalIgnoreCase))
                {
                    var folder = Path.Combine(package, "LocalCache", "Roaming", "Claude");
                    if (Directory.Exists(folder)) found.Add(folder);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* no Store installs readable */ }
        }
        return found;
    }

    /// <summary>The config files for this Windows user (existing or not) in the folders that exist.</summary>
    public static IReadOnlyList<string> FilesForThisUser() =>
        Folders(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
            .Select(f => Path.Combine(f, FileName))
            .ToList();

    /// <summary>True when any of the files has a <c>makino</c> entry.</summary>
    public static bool AnyHasEntry(IEnumerable<string> files) => files.Any(f =>
    {
        try { return File.Exists(f) && HasEntry(File.ReadAllText(f)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    });

    public static FileOutcome AddToFile(string path, string launcher, DateTime now) =>
        Change(path, now, json => Add(json, launcher), createIfMissing: true);

    public static FileOutcome RemoveFromFile(string path, DateTime now) =>
        Change(path, now, Remove, createIfMissing: false);

    private static FileOutcome Change(string path, DateTime now, Func<string, string?> edit, bool createIfMissing)
    {
        try
        {
            var exists = File.Exists(path);
            if (!exists && !createIfMissing) return new FileOutcome(path, FileChange.Unchanged);

            string? after;
            try
            {
                after = edit(exists ? File.ReadAllText(path) : "");
            }
            catch (InvalidCoworkConfigException e)
            {
                return new FileOutcome(path, FileChange.Invalid, Problem: $"{path} wasn't changed: {e.Message}.");
            }
            if (after is null) return new FileOutcome(path, FileChange.Unchanged);

            string? backup = null;
            if (exists)
            {
                backup = FreeBackupName(path, now);
                File.Copy(path, backup, overwrite: false);
            }
            WriteAtomically(path, after);
            return new FileOutcome(path, FileChange.Changed, backup);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new FileOutcome(path, FileChange.Failed, Problem: $"Couldn't change {path}: {e.Message}");
        }
    }

    private static string FreeBackupName(string path, DateTime now)
    {
        var stem = $"{path}.bak-{now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}";
        if (!File.Exists(stem)) return stem;
        for (var i = 2; ; i++)
            if (!File.Exists($"{stem}-{i}")) return $"{stem}-{i}";
    }

    private static void WriteAtomically(string path, string text)
    {
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temp, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
