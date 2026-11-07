#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;

namespace MakinoMCP.Launcher
{
    // Installs a downloaded release zip into a release-zip install, the way publish.ps1 updates a share: the build in
    // its own versions\<build> folder, the launcher swapped only when it changed (a running one is moved aside, which
    // Windows allows), current.txt written last so a launcher starting meanwhile sees a complete build either way.
    // MakinoMCP.settings.json and the marker are never touched. Never throws: the result says why not.
    // Also compiled into the tests, so keep it to plain System APIs.
    internal static class UpdateApplier
    {
        private const long MaxUnpackedBytes = 1024L * 1024 * 1024;
        private static readonly string[] LauncherFiles = { "MakinoMCP.exe", "MakinoMCP.exe.config" };
        private static readonly string[] TopFiles = { "MakinoMCP.settings.example.json", "README.md", "LICENSE" };
        private const string OldLaunchers = "old-launchers";
        // What a published build folder holds (publish.ps1). A signed install takes nothing else: a DLL beside the signed
        // exe would be loaded by it. Add here when a build ships a new file.
        private static readonly string[] SignedBuildFiles = { "MakinoMCP.exe", "build.txt", "version.txt", "LICENSE" };
        // .config elements that make the net48 launcher load other assemblies.
        private static readonly string[] ConfigLoadsCode = { "appDomainManager", "probing", "privatePath", "codeBase" };

        internal static ApplyResult Apply(string installDir, string zipPath, Version expected, Func<string, bool> isSigned,
            Func<string, bool> inUse, Action<string> log)
        {
            string versions = Path.Combine(installDir, "versions");
            string tmpBuild = null, tmpLauncher = null;
            try
            {
                Directory.CreateDirectory(versions);
                CleanLeftovers(versions, zipPath, log);
                if (!CanWrite(versions)) return Fail("the install folder can't be written by this user", log);

                using (var zip = ZipFile.OpenRead(zipPath))
                {
                    string problem = CheckShape(zip, expected, out string build);
                    if (problem != null) return Fail(problem, log);

                    string previous = ReadCurrent(installDir);
                    string target = Path.Combine(versions, build);

                    // 1. Unpack the build and the launcher files into temp folders.
                    tmpBuild = target + ".tmp-" + Guid.NewGuid().ToString("N");
                    tmpLauncher = Path.Combine(versions, ".launcher.tmp-" + Guid.NewGuid().ToString("N"));
                    string prefix = "versions/" + build + "/";
                    foreach (var e in zip.Entries)
                    {
                        string name = Name(e);
                        if (name.StartsWith(prefix, StringComparison.Ordinal) && name.Length > prefix.Length && !name.EndsWith("/"))
                            Extract(e, tmpBuild, name.Substring(prefix.Length).Replace('/', '\\'));
                        else if (LauncherFiles.Contains(name) || TopFiles.Contains(name))
                            Extract(e, tmpLauncher, name);
                    }

                    // 2. A signed install only takes a signed update (app and launcher alike), and nothing beside the
                    //    signed exes that could load other code: a build holds only its known files, and the launcher's
                    //    .config names no assembly to load.
                    string installedApp = previous == null ? null : Path.Combine(versions, previous, "MakinoMCP.exe");
                    if (installedApp != null && File.Exists(installedApp) && isSigned(installedApp))
                    {
                        if (!isSigned(Path.Combine(tmpBuild, "MakinoMCP.exe")))
                            return Fail("the installed build is signed but the update is not signed by " + Signatures.Signer, log);
                        string extra = Directory.GetFiles(tmpBuild, "*", SearchOption.AllDirectories)
                            .Select(f => f.Substring(tmpBuild.Length + 1))
                            .FirstOrDefault(f => !SignedBuildFiles.Contains(f, StringComparer.OrdinalIgnoreCase));
                        if (extra != null)
                            return Fail("the update's build holds " + extra + ", which a signed install doesn't take", log);
                    }
                    string installedLauncher = Path.Combine(installDir, "MakinoMCP.exe");
                    if (File.Exists(installedLauncher) && isSigned(installedLauncher))
                    {
                        if (!isSigned(Path.Combine(tmpLauncher, "MakinoMCP.exe")))
                            return Fail("the installed launcher is signed but the new one is not signed by " + Signatures.Signer, log);
                        string config = File.ReadAllText(Path.Combine(tmpLauncher, "MakinoMCP.exe.config"));
                        string loads = ConfigLoadsCode.FirstOrDefault(k => config.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
                        if (loads != null)
                            return Fail("the new launcher's .config names code to load (" + loads + "), which a signed install doesn't take", log);
                    }

                    // 3. The build into place.
                    if (Directory.Exists(target) && !File.Exists(Path.Combine(target, "MakinoMCP.exe"))) Directory.Delete(target, true);
                    if (!Directory.Exists(target)) { string from = tmpBuild; Retry(() => Directory.Move(from, target)); }
                    else TryDelete(tmpBuild);
                    tmpBuild = null;

                    // 4. The launcher files, only those that changed; all or nothing.
                    problem = SwapLauncher(installDir, versions, tmpLauncher, log);
                    if (problem != null) return Fail(problem, log);

                    // 5. Example settings, README, LICENSE.
                    foreach (string f in TopFiles)
                    {
                        string from = Path.Combine(tmpLauncher, f);
                        if (File.Exists(from)) File.Copy(from, Path.Combine(installDir, f), true);
                    }

                    // 6. current.txt last.
                    WriteCurrent(installDir, build);
                    log("installed build " + build + (previous == null ? "" : " (was " + previous + ")"));

                    // 7. Tidy.
                    Tidy(versions, build, previous, inUse, log);
                    return new ApplyResult { Applied = true, Build = build };
                }
            }
            catch (Exception ex)
            {
                return Fail("could not install the update: " + ex.Message, log);
            }
            finally
            {
                if (tmpBuild != null) TryDelete(tmpBuild);
                if (tmpLauncher != null) TryDelete(tmpLauncher);
            }
        }

        // A virus scanner briefly opens files that were just written; a rename or replace in that moment fails. Tries
        // again for about 3 seconds, then lets the last error through.
        internal static void Retry(Action action)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    action();
                    return;
                }
                catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < 15)
                {
                    System.Threading.Thread.Sleep(200);
                }
            }
        }

        private static ApplyResult Fail(string reason, Action<string> log)
        {
            log("not installed: " + reason);
            return new ApplyResult { Applied = false, Reason = reason };
        }

        private static string Name(ZipArchiveEntry e) => e.FullName.Replace('\\', '/');

        // The zip must hold current.txt naming a build of the expected version, the build's exe and the launcher files,
        // and nothing that could land outside the install folder.
        private static string CheckShape(ZipArchive zip, Version expected, out string build)
        {
            build = null;
            long total = 0;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in zip.Entries)
            {
                string n = Name(e);
                // An empty segment ("a//b") would make the rest a rooted path once joined, so it is refused like "..".
                string[] segments = (n.EndsWith("/") ? n.Substring(0, n.Length - 1) : n).Split('/');
                if (n.Length == 0 || n.StartsWith("/") || n.Contains(":") || segments.Any(p => p.Length == 0 || p == ".." || p == "."))
                    return "the zip holds an unsafe path (" + e.FullName + ")";
                total += e.Length;
                names.Add(n);
            }
            if (total > MaxUnpackedBytes) return "the zip unpacks to more than 1 GB";

            var current = zip.GetEntry("current.txt");
            if (current == null) return "the zip has no current.txt";
            if (current.Length > 256) return "the zip's current.txt is too long";
            using (var r = new StreamReader(current.Open())) build = r.ReadToEnd().Trim();
            if (build.Length == 0 || build.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || build.Contains(".."))
                return "the zip's current.txt doesn't name a build";
            var v = UpdateSource.InstalledVersion(build);
            if (v == null || v != expected) return "the zip holds build " + build + ", not version " + expected.ToString(3);

            foreach (string required in new[] { "versions/" + build + "/MakinoMCP.exe", "MakinoMCP.exe", "MakinoMCP.exe.config" })
                if (!names.Contains(required)) return "the zip has no " + required;
            return null;
        }

        // Writes the entry to root\relative, and refuses any path that would land outside root.
        private static void Extract(ZipArchiveEntry e, string root, string relative)
        {
            string rootFull = Path.GetFullPath(root).TrimEnd('\\') + "\\";
            string path = Path.GetFullPath(Path.Combine(rootFull, relative));
            if (!path.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("the zip entry " + e.FullName + " would land outside the install folder");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var from = e.Open())
            using (var to = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                from.CopyTo(to);
        }

        // Moves every changed launcher file aside, then copies the new ones in; on any failure puts the old ones back.
        private static string SwapLauncher(string installDir, string versions, string tmpLauncher, Action<string> log)
        {
            var changed = LauncherFiles.Where(f => !SameFile(Path.Combine(installDir, f), Path.Combine(tmpLauncher, f))).ToList();
            if (changed.Count == 0) return null;

            string stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            var moved = new List<KeyValuePair<string, string>>();
            try
            {
                string aside = Path.Combine(versions, OldLaunchers);
                Directory.CreateDirectory(aside);
                foreach (string f in changed)
                {
                    string live = Path.Combine(installDir, f);
                    if (!File.Exists(live)) continue;
                    string to = Path.Combine(aside, f + "." + stamp);
                    Retry(() => File.Move(live, to));
                    moved.Add(new KeyValuePair<string, string>(live, to));
                }
                foreach (string f in changed) Retry(() => File.Copy(Path.Combine(tmpLauncher, f), Path.Combine(installDir, f), true));
                log("replaced " + string.Join(", ", changed));
                return null;
            }
            catch (Exception ex)
            {
                foreach (var m in moved)
                {
                    try
                    {
                        if (File.Exists(m.Key)) File.Delete(m.Key);
                        File.Move(m.Value, m.Key);
                    }
                    catch (Exception restore)
                    {
                        log("could not put " + m.Key + " back: " + restore.Message);
                    }
                }
                return "the launcher could not be replaced (" + ex.Message + ")";
            }
        }

        private static bool SameFile(string a, string b)
        {
            if (!File.Exists(a) || !File.Exists(b)) return false;
            if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
            using (var sha = SHA256.Create())
            {
                byte[] ha, hb;
                using (var s = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) ha = sha.ComputeHash(s);
                using (var s = File.OpenRead(b)) hb = sha.ComputeHash(s);
                return ha.SequenceEqual(hb);
            }
        }

        internal static string ReadCurrent(string installDir)
        {
            try
            {
                string p = Path.Combine(installDir, "current.txt");
                return File.Exists(p) ? File.ReadAllText(p).Trim() : null;
            }
            catch
            {
                return null;
            }
        }

        private static void WriteCurrent(string installDir, string build)
        {
            string current = Path.Combine(installDir, "current.txt");
            string tmp = current + ".tmp";
            File.WriteAllText(tmp, build);
            Retry(() =>
            {
                if (File.Exists(current)) File.Replace(tmp, current, null);
                else File.Move(tmp, current);
            });
        }

        private static bool CanWrite(string versions)
        {
            string probe = Path.Combine(versions, ".write-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
                return true;
            }
            catch
            {
                return false;
            }
        }

        // Temp folders and downloads an interrupted update left behind (older than an hour, so a running one is safe).
        private static void CleanLeftovers(string versions, string keepZip, Action<string> log)
        {
            DateTime cutoff = DateTime.UtcNow.AddHours(-1);
            try
            {
                foreach (string d in Directory.GetDirectories(versions, "*.tmp-*"))
                    if (Directory.GetLastWriteTimeUtc(d) < cutoff) { TryDelete(d); log("removed leftover " + Path.GetFileName(d)); }
                foreach (string f in Directory.GetFiles(versions, ".download-*.zip"))
                {
                    if (string.Equals(Path.GetFullPath(f), Path.GetFullPath(keepZip), StringComparison.OrdinalIgnoreCase)) continue;
                    if (File.GetLastWriteTimeUtc(f) < cutoff) { try { File.Delete(f); log("removed leftover " + Path.GetFileName(f)); } catch { } }
                }
            }
            catch { }
        }

        // Keeps the newest three builds, the new one and the previous one; skips builds in use. Deletes set-aside
        // launchers nobody holds open.
        private static void Tidy(string versions, string build, string previous, Func<string, bool> inUse, Action<string> log)
        {
            try
            {
                var builds = Directory.GetDirectories(versions)
                    .Where(d =>
                    {
                        string n = Path.GetFileName(d);
                        return n != OldLaunchers && !n.StartsWith(".") && n.IndexOf(".tmp", StringComparison.OrdinalIgnoreCase) < 0;
                    })
                    .OrderByDescending(Directory.GetCreationTimeUtc)
                    .ToList();
                var keep = new HashSet<string>(builds.Take(3).Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase) { build };
                if (previous != null) keep.Add(previous);
                foreach (string d in builds)
                {
                    if (keep.Contains(Path.GetFileName(d)) || inUse(d)) continue;
                    string doomed = d + ".tmp-del-" + Guid.NewGuid().ToString("N");
                    try { Directory.Move(d, doomed); } catch { continue; }
                    TryDelete(doomed);
                    log("removed old build " + Path.GetFileName(d));
                }

                string aside = Path.Combine(versions, OldLaunchers);
                if (Directory.Exists(aside))
                {
                    foreach (string f in Directory.GetFiles(aside))
                    {
                        try
                        {
                            using (new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.None)) { }
                            File.Delete(f);
                        }
                        catch { } // still running: a later update deletes it
                    }
                }
            }
            catch (Exception ex)
            {
                log("tidying failed: " + ex.Message);
            }
        }

        private static void TryDelete(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }
    }

    internal sealed class ApplyResult
    {
        public bool Applied { get; set; }
        public string Build { get; set; }
        public string Reason { get; set; }
    }
}
