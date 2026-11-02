using System.Globalization;
using MakinoMCP.Core.Cell;
using MakinoMCP.Core.Settings;

namespace MakinoMCP.Setup;

public enum StatusKind { Info, Success, Warning, Error }

/// <summary>One line for the setup window's status area: always words, the kind only picks the icon.</summary>
public sealed record StatusLine(StatusKind Kind, string Text);

/// <summary>The setup window's rules, kept out of the form so they can be tested.</summary>
public static class SetupChecks
{
    /// <summary>Null when the login is filled in enough to save (the same rule as <see cref="McpSettings.IsReady"/>),
    /// else one sentence naming every missing field.</summary>
    public static string? ValidateLogin(string server, string database, bool useWindowsLogin, string user)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(server)) missing.Add("the server");
        if (string.IsNullOrWhiteSpace(database)) missing.Add("the database");
        if (!useWindowsLogin && string.IsNullOrWhiteSpace(user)) missing.Add("the user name");
        return missing.Count == 0 ? null : $"Enter {JoinAnd(missing)}.";
    }

    /// <summary>Null when every folder has a name and no two share one (what <see cref="SettingsStore.Load"/> requires).</summary>
    public static string? ValidateFolders(IReadOnlyList<SharedFolder> folders)
    {
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in folders)
        {
            if (string.IsNullOrWhiteSpace(f.Name)) return "Every shared folder needs a name.";
            var name = f.Name.Trim();
            if (!seen.TryAdd(name, name)) return $"Two shared folders are both called '{seen[name]}': give each its own name.";
        }
        return null;
    }

    /// <summary>A short name for a newly picked folder: its leaf ("D-drive" for a drive root), with -2, -3… added
    /// when that name is taken (ignoring case).</summary>
    public static string UniqueName(string folderPath, IEnumerable<string> existing)
    {
        // The last part of the path: a share root (\\srv\share) gives the share, a drive root (D:\) "D-drive".
        var leaf = folderPath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault() ?? "";
        if (leaf.Length == 2 && leaf[1] == ':') leaf = $"{char.ToUpperInvariant(leaf[0])}-drive";
        if (leaf.Length == 0) leaf = "folder";

        var taken = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(leaf)) return leaf;
        for (var i = 2; ; i++)
            if (!taken.Contains($"{leaf}-{i}")) return $"{leaf}-{i}";
    }

    /// <summary>Connects with the login and says what it can see and do. Gives up after <paramref name="cap"/> even if
    /// the driver doesn't; the text never contains the password.</summary>
    public static async Task<StatusLine> TestConnectionAsync(IMakinoReader reader, MakinoLogin login, TimeSpan cap, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(cap);
        try
        {
            var rights = await reader.GetLoginRightsAsync(limit.Token).WaitAsync(limit.Token);
            var found = $"Connected: found {rights.DeviceCount} {(rights.DeviceCount == 1 ? "device" : "devices")}.";
            return rights.CanWrite
                ? new(StatusKind.Warning, $"{found} This login can also change data — MakinoMCP only ever reads.")
                : new(StatusKind.Success, $"{found} This login is read only.");
        }
        catch (Exception) when (limit.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            var seconds = cap.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture);
            return new(StatusKind.Error,
                $"No answer from {login.Server} within {seconds} seconds: check the server name and that the cell PC is on.");
        }
        catch (Exception e)
        {
            return new(StatusKind.Error, ErrorText.ForClaude(e, login));
        }
    }

    /// <summary>The names of the folders this PC can't see right now (missing, offline, or no answer in time).</summary>
    public static IReadOnlyList<string> Unreachable(IReadOnlyList<SharedFolder> folders, TimeSpan timeout)
    {
        var probes = folders.Select(f => Task.Run(() => Directory.Exists(f.Path))).ToArray();
        try { Task.WaitAll(probes, timeout); }
        catch (AggregateException) { /* a failed probe counts as unreachable below */ }
        return folders
            .Where((_, i) => !probes[i].IsCompletedSuccessfully || !probes[i].Result)
            .Select(f => f.Name)
            .ToList();
    }

    /// <summary>A write failure in plain words. IO messages name the file at most, never the login.</summary>
    public static string SaveError(string path, Exception e) => $"Couldn't save to {path}: {e.Message}";

    /// <summary>The WCAG contrast ratio of two colours (1 to 21).</summary>
    public static double ContrastRatio(System.Drawing.Color a, System.Drawing.Color b)
    {
        static double Channel(int v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        static double Luminance(System.Drawing.Color c) =>
            0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);

        var (x, y) = (Luminance(a), Luminance(b));
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    /// <summary>Of the system colours offered, the one that reads best on <paramref name="background"/> (the first on
    /// a tie). Lets the form stay on system colours where a theme pairs two of them badly.</summary>
    public static System.Drawing.Color MostContrasting(System.Drawing.Color background, params System.Drawing.Color[] candidates)
    {
        var best = candidates[0];
        foreach (var c in candidates.Skip(1))
            if (ContrastRatio(c, background) > ContrastRatio(best, background)) best = c;
        return best;
    }

    private static string JoinAnd(IReadOnlyList<string> items) => items.Count switch
    {
        1 => items[0],
        _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
    };
}
