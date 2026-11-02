namespace MakinoMCP.Setup;

/// <summary>The two ways to tell Claude about MakinoMCP. Both point at the launcher on the share.</summary>
public static class InstallSnippets
{
    public const string LauncherFileName = "MakinoMCP.exe";

    /// <summary>The Claude Code command and the Cowork <c>mcpServers</c> entry for the launcher at
    /// <paramref name="launcherPath"/>.</summary>
    public static (string ClaudeCode, string CoworkJson) For(string launcherPath)
    {
        var claudeCode = $"claude mcp add makino -- \"{launcherPath}\" serve";
        var cowork = $"\"makino\": {{ \"command\": \"{JsonEscape(launcherPath)}\", \"args\": [\"serve\"] }}";
        return (claudeCode, cowork);
    }

    /// <summary>The launcher sits beside the settings file (the launcher passes that file with --settings).</summary>
    public static string LauncherPath(string settingsPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settingsPath)) ?? "", LauncherFileName);

    // Only what JSON requires, so the path stays readable (no \u escapes for spaces, '+' or accents).
    private static string JsonEscape(string s)
    {
        var b = new System.Text.StringBuilder(s.Length + 8);
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\': b.Append(@"\\"); break;
                case '"': b.Append("\\\""); break;
                case < ' ': b.Append($"\\u{(int)c:x4}"); break;
                default: b.Append(c); break;
            }
        }
        return b.ToString();
    }
}
