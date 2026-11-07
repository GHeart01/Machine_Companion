#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace MakinoMCP.Launcher
{
    // Whether this install updates itself from GitHub. Only a release-zip install qualifies: the zip carries the marker
    // file, and the folder must be on this PC's own disk and not inside a folder this PC shares, so a share (or a
    // shared local folder) never changes what other people run. Its owner keeps it up to date (publish.ps1).
    // Also compiled into the tests and the app (setup window), so keep it to plain System APIs.
    internal static class UpdateSource
    {
        internal const string MarkerFileName = "MakinoMCP.update.json";
        internal const string SettingsFileName = "MakinoMCP.settings.json";
        internal const string NoUpdateVariable = "MAKINOMCP_NO_UPDATE";

        private static readonly Regex RepositoryPattern = new Regex(@"^[A-Za-z0-9-]+/[A-Za-z0-9._-]+$");
        private static readonly Regex BuildPattern = new Regex(@"^(\d+)\.(\d+)\.(\d+)-");

        [DataContract]
        private sealed class Marker
        {
            [DataMember(Name = "source")] public string Source { get; set; }
            [DataMember(Name = "repository")] public string Repository { get; set; }
        }

        /// <summary>The GitHub repository the marker names, or null when there is no valid marker.</summary>
        internal static string ReadRepository(string installDir)
        {
            var m = ReadJson<Marker>(Path.Combine(installDir, MarkerFileName));
            if (m == null || !string.Equals(m.Source, "github", StringComparison.OrdinalIgnoreCase)) return null;
            var repo = m.Repository?.Trim();
            if (repo == null || !RepositoryPattern.IsMatch(repo) || repo.Split('/').Any(p => p == "." || p == "..")) return null;
            return repo;
        }

        /// <summary>A UNC path, or a drive whose type is network (isNetworkDrive gets the root, e.g. "Z:\").</summary>
        internal static bool IsNetworkPath(string dir, Func<string, bool> isNetworkDrive)
        {
            if (string.IsNullOrEmpty(dir)) return true;
            if (dir.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return true;
            if (dir.StartsWith(@"\\?\", StringComparison.Ordinal)) dir = dir.Substring(4);
            if (dir.StartsWith(@"\\", StringComparison.Ordinal) || dir.StartsWith("//", StringComparison.Ordinal)) return true;
            if (dir.Length >= 2 && dir[1] == ':') return isNetworkDrive(char.ToUpperInvariant(dir[0]) + @":\");
            return true; // a relative or odd path: don't guess it's local
        }

        internal static bool IsNetworkDrive(string root)
        {
            try { return new DriveInfo(root).DriveType == DriveType.Network; }
            catch { return true; }
        }

        /// <summary>True when dir is a folder this PC shares, or inside one. A null list (unreadable) counts as no.</summary>
        internal static bool IsInsideLocalShare(string dir, IEnumerable<string> sharePaths)
        {
            if (sharePaths == null) return false;
            string d = Normalize(dir);
            foreach (string share in sharePaths)
            {
                if (string.IsNullOrWhiteSpace(share)) continue;
                string s = Normalize(share);
                if (string.Equals(d, s, StringComparison.OrdinalIgnoreCase)) return true;
                if (d.StartsWith(s + @"\", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static string Normalize(string p)
        {
            try { p = Path.GetFullPath(p); } catch { }
            return p.TrimEnd('\\', '/');
        }

        /// <summary>The folders this PC shares (the Server service's list in the registry), or null when it can't be
        /// read. Each value is a list of lines; the folder is on the line "Path=...".</summary>
        internal static IEnumerable<string> LocalSharePaths()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\LanmanServer\Shares"))
                {
                    if (key == null) return new string[0];
                    var paths = new List<string>();
                    foreach (string name in key.GetValueNames())
                    {
                        if (!(key.GetValue(name) is string[] lines)) continue;
                        foreach (string line in lines)
                            if (line != null && line.StartsWith("Path=", StringComparison.OrdinalIgnoreCase))
                                paths.Add(line.Substring(5));
                    }
                    return paths;
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>True only when the settings file says "updates": "off". Reads that one field and nothing else; a
        /// missing or unreadable file, or no such field, counts as on.</summary>
        internal static bool UpdatesOff(string settingsPath)
        {
            try
            {
                if (!File.Exists(settingsPath) || new FileInfo(settingsPath).Length > 1024 * 1024) return false;
                // The app reads property names in any case ("updates", "Updates"), so this does too. Only a real key
                // counts: a quote that isn't escaped, so the words inside some other string value don't.
                var m = UpdatesKey.Match(StripComments(File.ReadAllText(settingsPath)));
                return m.Success && string.Equals(m.Groups[1].Value.Trim(), "off", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static readonly Regex UpdatesKey = new Regex(@"(?<!\\)""updates""\s*:\s*""([^""\\]*)""", RegexOptions.IgnoreCase);

        /// <summary>"0.6.0" from the build name "0.6.0-20261107-1232"; null for anything else.</summary>
        internal static Version InstalledVersion(string build)
        {
            if (build == null) return null;
            var m = BuildPattern.Match(build);
            if (!m.Success) return null;
            try { return new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)); }
            catch { return null; }
        }

        internal static UpdateDecision Decide(string installDir, string settingsPath, Func<string, bool> isNetworkDrive,
            IEnumerable<string> sharePaths, string noUpdateEnv)
        {
            var d = new UpdateDecision();
            d.Repository = ReadRepository(installDir);
            if (d.Repository == null) { d.Reason = "not a release-zip install (no valid " + MarkerFileName + ")"; return d; }
            if (IsNetworkPath(installDir, isNetworkDrive)) { d.Reason = "the install folder is on a network drive"; return d; }
            if (IsInsideLocalShare(installDir, sharePaths)) { d.Reason = "the install folder is shared on the network"; return d; }
            d.GitHubInstall = true;
            d.ShareCheckSkipped = sharePaths == null;
            if (!string.IsNullOrEmpty(noUpdateEnv)) { d.Reason = NoUpdateVariable + " is set"; return d; }
            if (UpdatesOff(settingsPath)) { d.Reason = "updates are turned off in " + SettingsFileName; return d; }
            d.UpdatesOn = true;
            return d;
        }

        /// <summary>The decision for the launcher in installDir, with this PC's drives, shares and environment.</summary>
        internal static UpdateDecision DecideForThisPc(string installDir) =>
            Decide(installDir, Path.Combine(installDir, SettingsFileName), IsNetworkDrive, LocalSharePaths(),
                Environment.GetEnvironmentVariable(NoUpdateVariable));

        // Reads a small JSON file into T, or null. Never throws, never reports the content (the settings file holds the
        // password). Comments are allowed, as in SettingsStore.
        private static T ReadJson<T>(string path) where T : class
        {
            try
            {
                if (!File.Exists(path)) return null;
                var info = new FileInfo(path);
                if (info.Length > 1024 * 1024) return null;
                string text = StripComments(File.ReadAllText(path));
                using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(text)))
                    return new DataContractJsonSerializer(typeof(T)).ReadObject(ms) as T;
            }
            catch
            {
                return null;
            }
        }

        // Removes // and /* */ comments outside strings.
        internal static string StripComments(string s)
        {
            var b = new StringBuilder(s.Length);
            bool inString = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (inString)
                {
                    b.Append(c);
                    if (c == '\\' && i + 1 < s.Length) b.Append(s[++i]);
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') { inString = true; b.Append(c); continue; }
                if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
                {
                    while (i < s.Length && s[i] != '\n') i++;
                    b.Append('\n');
                    continue;
                }
                if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
                {
                    int end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = end < 0 ? s.Length : end + 1;
                    b.Append(' ');
                    continue;
                }
                b.Append(c);
            }
            return b.ToString();
        }
    }

    internal sealed class UpdateDecision
    {
        /// <summary>A release-zip install on this PC's own disk, not shared: it runs in place and may update from GitHub.</summary>
        public bool GitHubInstall { get; set; }
        /// <summary>GitHubInstall, and updates aren't turned off.</summary>
        public bool UpdatesOn { get; set; }
        public string Repository { get; set; }
        /// <summary>This PC's list of shared folders couldn't be read, so "not shared" was assumed (logged).</summary>
        public bool ShareCheckSkipped { get; set; }
        /// <summary>Why not, in a few words; null when UpdatesOn.</summary>
        public string Reason { get; set; }
    }
}
