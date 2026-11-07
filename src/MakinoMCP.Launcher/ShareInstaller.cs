#nullable disable
using System;
using System.IO;

namespace MakinoMCP.Launcher
{
    // A copy of MakinoMCP on a share installs itself on each PC that opens it: into %LOCALAPPDATA%\Programs\MakinoMCP
    // (per Windows user, no admin rights, always writable), where it then runs and keeps itself up to date from GitHub.
    // What is already on the PC wins: a local install is never downgraded and its settings are never overwritten.
    // Also compiled into the tests, so keep it to plain System APIs.
    internal static class ShareInstaller
    {
        internal const string DefaultMarker = "{ \"source\": \"github\", \"repository\": \"GHeart01/Machine_Companion\" }";
        private static readonly string[] CopyIfMissing =
            { "MakinoMCP.exe", "MakinoMCP.exe.config", "MakinoMCP.settings.json", "MakinoMCP.settings.example.json", "README.md", "LICENSE" };

        internal static string LocalInstallDir() => Path.Combine(
            Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "MakinoMCP");

        /// <summary>A launcher and the build its current.txt names are there.</summary>
        internal static bool IsInstalled(string localDir)
        {
            if (!File.Exists(Path.Combine(localDir, "MakinoMCP.exe"))) return false;
            string current = UpdateApplier.ReadCurrent(localDir);
            return IsBuildName(current) && File.Exists(Path.Combine(localDir, "versions", current, "MakinoMCP.exe"));
        }

        /// <summary>Copies the share's current build, launcher and settings to localDir. Never throws; progress gets
        /// (bytes copied, total) while the build copies.</summary>
        internal static InstallResult Install(string shareDir, string localDir, Action<long, long> progress)
        {
            try
            {
                if (IsInstalled(localDir)) return new InstallResult { Installed = true, Reason = "already installed" };

                string build = UpdateApplier.ReadCurrent(shareDir);
                if (!IsBuildName(build)) return Fail("the share's current.txt doesn't name a build");
                string source = Path.Combine(shareDir, "versions", build);
                if (!File.Exists(Path.Combine(source, "MakinoMCP.exe"))) return Fail("the share has no build " + build);
                if (!File.Exists(Path.Combine(shareDir, "MakinoMCP.exe"))) return Fail("the share has no MakinoMCP.exe");

                Directory.CreateDirectory(localDir);
                VersionCopier.CopyToLocal(source, Path.Combine(localDir, "versions"), build, progress);
                foreach (string name in CopyIfMissing)
                {
                    string from = Path.Combine(shareDir, name), to = Path.Combine(localDir, name);
                    if (File.Exists(from) && !File.Exists(to)) File.Copy(from, to);
                }
                string marker = Path.Combine(localDir, UpdateSource.MarkerFileName);
                if (!File.Exists(marker))
                {
                    string shareMarker = Path.Combine(shareDir, UpdateSource.MarkerFileName);
                    if (File.Exists(shareMarker)) File.Copy(shareMarker, marker);
                    else File.WriteAllText(marker, DefaultMarker);
                }
                // current.txt last: until it is there, the install doesn't count as installed.
                string current = Path.Combine(localDir, "current.txt");
                if (!File.Exists(current)) File.WriteAllText(current, build);
                return new InstallResult { Installed = IsInstalled(localDir), Build = build,
                    Reason = IsInstalled(localDir) ? null : "the copy on this PC is incomplete" };
            }
            catch (Exception ex)
            {
                return Fail(ex.Message);
            }
        }

        private static InstallResult Fail(string reason) => new InstallResult { Installed = false, Reason = reason };

        private static bool IsBuildName(string v) =>
            !string.IsNullOrWhiteSpace(v) && v == v.Trim() && v != "." && !v.Contains("..")
            && v.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !Path.IsPathRooted(v);
    }

    internal sealed class InstallResult
    {
        public bool Installed { get; set; }
        public string Build { get; set; }
        public string Reason { get; set; }
    }
}
