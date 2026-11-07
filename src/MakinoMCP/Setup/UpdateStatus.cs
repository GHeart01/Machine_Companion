using System.Globalization;
using System.Text.Json;

namespace MakinoMCP.Setup;

/// <summary>The setup window's one-line summary of the last GitHub update check, from the state file the launcher's
/// update process writes (%LOCALAPPDATA%\MakinoMCP\update-state.json).</summary>
public static class UpdateStatus
{
    public static string StatePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MakinoMCP", "update-state.json");

    /// <summary>The line to show, or null when there is no usable state yet.</summary>
    public static string? Describe(string? stateJson, DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(stateJson)) return null;
        string? result, version, message, at;
        try
        {
            using var doc = JsonDocument.Parse(stateJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            result = Str(root, "result");
            version = Str(root, "version");
            message = Str(root, "message");
            at = Str(root, "lastAttemptUtc");
        }
        catch (JsonException)
        {
            return null;
        }
        if (at is null || !DateTime.TryParse(at, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var when))
            return null;

        var ago = Ago(utcNow - when);
        return result switch
        {
            "up-to-date" => $"Checked {ago}: up to date.",
            "updated" => $"Updated to {version} {ago}. Claude uses it from its next start.",
            "failed" => $"The last check failed {ago}: {Sentence(message ?? "no reason given")}",
            "checking" when utcNow - when < TimeSpan.FromHours(1) => "Checking for a new version…",
            "checking" => $"The last check didn't finish ({ago}).",
            _ => null,
        };
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Sentence(string s)
    {
        s = s.Trim();
        return s.EndsWith('.') ? s : s + ".";
    }

    private static string Ago(TimeSpan t)
    {
        if (t < TimeSpan.FromMinutes(1)) return "just now";
        if (t < TimeSpan.FromHours(1)) return Plural((int)t.TotalMinutes, "minute") + " ago";
        if (t < TimeSpan.FromDays(1)) return Plural((int)t.TotalHours, "hour") + " ago";
        return Plural((int)t.TotalDays, "day") + " ago";
    }

    private static string Plural(int n, string unit) => n == 1 ? $"1 {unit}" : $"{n} {unit}s";
}
