#nullable disable
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace MakinoMCP.Launcher
{
    // The one routine that copies versions\<v> from the share to the PC, used by setup (with progress) and the
    // detached prefetch (without). Per file it calls CopyFileEx: as fast as File.Copy (measured on the real share,
    // faster than a managed stream loop) and it reports bytes while a big file is copying.
    // Also compiled into the tests, so keep it to plain System APIs.
    internal static class VersionCopier
    {
        // Copies source to a unique temp folder beside local\<version>, then renames it into place. Throws on failure
        // after removing the temp folder. progress, when given, gets (bytes copied, total bytes) and ends with done == total.
        internal static void CopyToLocal(string source, string local, string version, Action<long, long> progress)
        {
            string tmp = null;
            try
            {
                Directory.CreateDirectory(local);
                CleanStale(local);
                string target = Path.Combine(local, version);
                if (Directory.Exists(target)) return;

                long total = progress == null ? 0 : DirSize(source);
                long done = 0;
                Action<long> onBytes = null;
                if (progress != null) onBytes = n => { done += n; progress(done, total); };
                tmp = target + ".tmp-" + Guid.NewGuid().ToString("N");
                CopyDir(source, tmp, onBytes);
                if (progress != null) progress(done, total);
                try
                {
                    Directory.Move(tmp, target);
                }
                catch (IOException)
                {
                    if (!Directory.Exists(target)) throw;
                    // Another copy won the race.
                }
                TryDelete(tmp);
            }
            catch
            {
                if (tmp != null) TryDelete(tmp);
                throw;
            }
        }

        private static long DirSize(string dir)
        {
            long n = 0;
            foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories)) n += new FileInfo(f).Length;
            return n;
        }

        internal static void CleanStale(string local)
        {
            try
            {
                foreach (string d in Directory.GetDirectories(local, "*.tmp*"))
                {
                    if (Directory.GetLastWriteTimeUtc(d) < DateTime.UtcNow.AddHours(-1)) TryDelete(d);
                }
            }
            catch { }
        }

        // Deletes the other builds copied to this PC, keeping `keep` and every folder inUse says a running MakinoMCP
        // uses (Windows 11 lets a running exe be deleted, so a failed delete is no guard). Temp folders are left to
        // CleanStale. Never throws: a folder that can't be deleted now is tried again on the next start.
        internal static void RemoveOtherVersions(string local, string keep, Func<string, bool> inUse)
        {
            try
            {
                if (!Directory.Exists(local)) return;
                foreach (string d in Directory.GetDirectories(local))
                {
                    string name = Path.GetFileName(d);
                    if (string.Equals(name, keep, StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.IndexOf(".tmp", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (inUse(d)) continue;
                    // Renamed first, so an interrupted delete never leaves a build folder without its exe; CleanStale
                    // removes a leftover .tmp folder later.
                    string doomed = d + ".tmp-del-" + Guid.NewGuid().ToString("N");
                    try { Directory.Move(d, doomed); } catch { continue; }
                    TryDelete(doomed);
                }
            }
            catch { }
        }

        private static void TryDelete(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }

        private static void CopyDir(string source, string dest, Action<long> onBytes)
        {
            Directory.CreateDirectory(dest);
            foreach (string f in Directory.GetFiles(source))
                CopyFile(f, Path.Combine(dest, Path.GetFileName(f)), onBytes);
            foreach (string d in Directory.GetDirectories(source))
                CopyDir(d, Path.Combine(dest, Path.GetFileName(d)), onBytes);
        }

        private delegate uint CopyProgressRoutine(long total, long transferred, long streamSize, long streamTransferred,
            uint streamNumber, uint reason, IntPtr source, IntPtr dest, IntPtr data);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CopyFileExW(string existing, string target, CopyProgressRoutine progress, IntPtr data,
            ref int cancel, uint flags);

        private static void CopyFile(string from, string to, Action<long> onBytes)
        {
            long reported = 0;
            // Kept in a local so the delegate cannot be collected while the native call runs.
            CopyProgressRoutine callback = null;
            if (onBytes != null)
            {
                callback = (total, transferred, streamSize, streamTransferred, streamNumber, reason, src, dst, data) =>
                {
                    long delta = transferred - reported;
                    reported = transferred;
                    if (delta > 0) onBytes(delta);
                    return 0; // PROGRESS_CONTINUE
                };
            }
            int cancel = 0;
            if (!CopyFileExW(from, to, callback, IntPtr.Zero, ref cancel, 0))
            {
                var inner = new Win32Exception(Marshal.GetLastWin32Error());
                throw new IOException(inner.Message, inner);
            }
            GC.KeepAlive(callback);
        }
    }
}
