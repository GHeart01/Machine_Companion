#nullable disable
using System;
using System.IO;

namespace MakinoMCP.Launcher
{
    // Until 0.7, PCs running MakinoMCP from a share kept copies of its builds in %LOCALAPPDATA%\MakinoMCP\app. From 0.8
    // every PC runs an install of its own, so those copies are removed once nothing runs from them, and then the folder.
    // Also compiled into the tests, so keep it to plain System APIs.
    internal static class OldCache
    {
        /// <summary>Deletes the build folders in appDir that inUse says no MakinoMCP runs from, then appDir when empty.
        /// Never throws: whatever can't go now is tried again on a later start.</summary>
        internal static void Tidy(string appDir, Func<string, bool> inUse, string launcherDir)
        {
            try
            {
                if (!Directory.Exists(appDir)) return;
                string self = launcherDir == null ? null : Path.GetFullPath(launcherDir).TrimEnd('\\');
                foreach (string d in Directory.GetDirectories(appDir))
                {
                    if (inUse(d)) continue;
                    // An install that happens to live in there (this launcher's own folder) is not an old copy.
                    string full = Path.GetFullPath(d).TrimEnd('\\');
                    if (self != null && (string.Equals(self, full, StringComparison.OrdinalIgnoreCase)
                                         || self.StartsWith(full + "\\", StringComparison.OrdinalIgnoreCase))) continue;
                    // Renamed first, so an interrupted delete never leaves a build folder without its exe.
                    string doomed = d + ".tmp-del-" + Guid.NewGuid().ToString("N");
                    try { Directory.Move(d, doomed); } catch { continue; }
                    try { Directory.Delete(doomed, true); } catch { }
                }
                if (Directory.GetFileSystemEntries(appDir).Length == 0) Directory.Delete(appDir);
            }
            catch { }
        }
    }
}
