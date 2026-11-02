using System.Text.Json;

namespace MakinoMCP.Core.Settings;

public sealed record SettingsLoad(McpSettings? Settings, string? Problem);

public static class SettingsStore
{
    public const string FileName = "MakinoMCP.settings.json";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true,
    };

    /// <summary>Reads and validates the settings file. Never throws and never puts the password in Problem.</summary>
    public static SettingsLoad Load(string path)
    {
        if (!File.Exists(path))
            return new(null, $"Settings file not found at {path}. Open the setup window (run MakinoMCP.exe with no arguments) to create it.");

        McpSettings? raw;
        try
        {
            raw = JsonSerializer.Deserialize<McpSettings>(File.ReadAllText(path), Options);
        }
        catch (JsonException ex)
        {
            // Position only: the exception text could quote file content.
            return new(null, $"Settings file {path} is not valid (line {ex.LineNumber + 1}, path {ex.Path}). Fix it or run MakinoMCP.exe to recreate it.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(null, $"Settings file {path} could not be read ({ex.GetType().Name}).");
        }

        if (raw is null)
            return new(null, $"Settings file {path} is empty. Run MakinoMCP.exe to set it up.");

        var settings = new McpSettings(
            raw.Makino ?? new MakinoLogin("", "", false, "", ""),
            raw.Folders ?? [],
            raw.Limits ?? new Limits());
        settings = settings with
        {
            Makino = settings.Makino with
            {
                Server = settings.Makino.Server ?? "",
                Database = settings.Makino.Database ?? "",
                User = settings.Makino.User ?? "",
                Password = settings.Makino.Password ?? "",
            },
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in settings.Folders)
        {
            if (f is null || string.IsNullOrWhiteSpace(f.Name))
                return new(null, "Every shared folder needs a name.");
            if (!seen.Add(f.Name))
                return new(null, $"Folder names must be unique: '{f.Name}' appears more than once.");
        }

        return new(settings, null);
    }

    /// <summary>Writes to a .tmp file, then swaps it in so a reader never sees half a file.</summary>
    public static void Save(string path, McpSettings s)
    {
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s, Options));
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
    }

    /// <summary>The path after <c>--settings</c>, else MakinoMCP.settings.json beside the exe.</summary>
    public static string DefaultPath(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], "--settings", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(args[i + 1]))
                return args[i + 1];
        return System.IO.Path.Combine(AppContext.BaseDirectory, FileName);
    }
}
