using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace MakinoMCP.Launcher
{
    // Starts the real MakinoMCP.exe (a local copy if present; otherwise straight from the share while a background copy is made) and pumps
    // stdin/stdout/stderr between the MCP client and it as raw bytes. Stdout carries only MCP messages.
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                string launcherDir = AppDomain.CurrentDomain.BaseDirectory;
                // Background copy started by a first run; must stay silent (no stdout/stderr).
                if (args.Length == 2 && args[0] == "--prefetch") return Prefetch(launcherDir, args[1]);
                string exe = ResolveExe(launcherDir);
                if (exe == null)
                {
                    Console.Error.WriteLine("MakinoMCP: no installed version found.");
                    return 2;
                }
                return Run(exe, args, Path.Combine(launcherDir, "MakinoMCP.settings.json")).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("MakinoMCP launcher failed: " + ex.Message);
                return 1;
            }
        }

        private static string LocalApp()
        {
            return Path.Combine(
                Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MakinoMCP", "app");
        }

        private static string ResolveExe(string launcherDir)
        {
            string currentTxt = Path.Combine(launcherDir, "current.txt");
            if (!File.Exists(currentTxt))
            {
                // Dev layout: MakinoMCP.exe in a sibling app\ folder.
                string dev = Path.Combine(launcherDir, "app", "MakinoMCP.exe");
                if (File.Exists(dev)) return dev;
                // No current.txt and no dev exe: the share may be unreachable, so use what is installed locally.
                return NewestLocal(LocalApp());
            }

            string local = LocalApp();
            try
            {
                string version = File.ReadAllText(currentTxt).Trim();
                if (!IsValidVersion(version)) throw new InvalidDataException("current.txt does not hold a valid version");
                string target = Path.Combine(local, version);
                if (Directory.Exists(target)) return Path.Combine(target, "MakinoMCP.exe");

                // First start on this PC: the copy is far slower than a client's connect timeout, so serve this
                // session straight from the share and copy in a detached background process for the next start.
                string shareExe = Path.Combine(launcherDir, "versions", version, "MakinoMCP.exe");
                if (!File.Exists(shareExe)) throw new FileNotFoundException("version " + version + " is not on the share");
                StartPrefetch(version);
                return shareExe;
            }
            catch (Exception ex)
            {
                // Share unreachable (or copy failed): fall back to the newest local version.
                Console.Error.WriteLine("MakinoMCP: could not update from the share (" + ex.Message + "); using newest local version.");
                return NewestLocal(local);
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public string lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess, hThread;
            public int dwProcessId, dwThreadId;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcessW(string applicationName, StringBuilder commandLine, IntPtr processAttributes,
            IntPtr threadAttributes, bool inheritHandles, int creationFlags, IntPtr environment, string currentDirectory,
            ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        private const int CreateNoWindow = 0x08000000;

        // Starts "MakinoMCP.exe --prefetch <version>" detached. bInheritHandles is FALSE and no std handles are
        // passed, so the child cannot hold the MCP client's stdio pipes open. Best effort: the session works without it.
        private static void StartPrefetch(string version)
        {
            try
            {
                string self = Process.GetCurrentProcess().MainModule.FileName;
                var cmd = new StringBuilder(Quote(self) + " --prefetch " + Quote(version));
                var si = new STARTUPINFO();
                si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
                PROCESS_INFORMATION pi;
                if (!CreateProcessW(null, cmd, IntPtr.Zero, IntPtr.Zero, false, CreateNoWindow, IntPtr.Zero,
                        Path.GetTempPath(), ref si, out pi))
                {
                    Console.Error.WriteLine("MakinoMCP: could not start the background copy (Win32 error " + Marshal.GetLastWin32Error() + ").");
                    return;
                }
                CloseHandle(pi.hThread);
                CloseHandle(pi.hProcess);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("MakinoMCP: could not start the background copy (" + ex.Message + ").");
            }
        }

        // A version names a folder: refuse anything that could leave the app\ or versions\ folders.
        private static bool IsValidVersion(string v)
        {
            return !string.IsNullOrWhiteSpace(v)
                && v == v.Trim()
                && v != "." && !v.Contains("..")
                && v.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
                && !Path.IsPathRooted(v);
        }

        // Copies versions\<version> from the share to a unique temp folder, then renames it into place.
        private static int Prefetch(string launcherDir, string version)
        {
            string tmp = null;
            try
            {
                if (!IsValidVersion(version)) return 1;
                string local = LocalApp();
                Directory.CreateDirectory(local);
                CleanStale(local);
                string target = Path.Combine(local, version);
                if (Directory.Exists(target)) return 0;

                tmp = target + ".tmp-" + Guid.NewGuid().ToString("N");
                CopyDir(Path.Combine(launcherDir, "versions", version), tmp);
                try
                {
                    Directory.Move(tmp, target);
                }
                catch (IOException)
                {
                    if (!Directory.Exists(target)) throw;
                    // Another prefetch won the race.
                }
                TryDelete(tmp);
                return 0;
            }
            catch
            {
                if (tmp != null) TryDelete(tmp);
                return 1;
            }
        }

        private static void CleanStale(string local)
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

        private static void TryDelete(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }

        private static string NewestLocal(string local)
        {
            if (!Directory.Exists(local)) return null;
            return Directory.GetDirectories(local)
                .Where(d => Path.GetFileName(d).IndexOf(".tmp", StringComparison.OrdinalIgnoreCase) < 0)
                .Where(d => File.Exists(Path.Combine(d, "MakinoMCP.exe")))
                .OrderByDescending(d => Directory.GetCreationTimeUtc(d))
                .Select(d => Path.Combine(d, "MakinoMCP.exe"))
                .FirstOrDefault();
        }

        private static void CopyDir(string source, string dest)
        {
            Directory.CreateDirectory(dest);
            foreach (string f in Directory.GetFiles(source))
                File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), true);
            foreach (string d in Directory.GetDirectories(source))
                CopyDir(d, Path.Combine(dest, Path.GetFileName(d)));
        }

        private static async Task<int> Run(string exe, string[] args, string settingsPath)
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(exe),
                Arguments = string.Join(" ", args.Concat(new[] { "--settings", settingsPath }).Select(Quote)),
            };

            using (var child = Process.Start(psi))
            {
                Task inPump = Pump(Console.OpenStandardInput(), child.StandardInput.BaseStream, closeDest: true);
                Task outPump = Pump(child.StandardOutput.BaseStream, Console.OpenStandardOutput(), closeDest: false);
                Task errPump = Pump(child.StandardError.BaseStream, Console.OpenStandardError(), closeDest: false);

                child.WaitForExit();
                await Task.WhenAll(outPump, errPump);
                return child.ExitCode;
            }
        }

        // Raw byte copy, flushing every read so the client sees each message immediately.
        private static async Task Pump(Stream from, Stream to, bool closeDest)
        {
            var buffer = new byte[8192];
            try
            {
                int n;
                while ((n = await from.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                {
                    await to.WriteAsync(buffer, 0, n).ConfigureAwait(false);
                    await to.FlushAsync().ConfigureAwait(false);
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            finally
            {
                if (closeDest)
                {
                    try { to.Close(); } catch (IOException) { }
                }
            }
        }

        private const char BackslashChar = (char)92;

        private static string Quote(string arg)
        {
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return arg;
            var sb = new StringBuilder("\"");
            int backslashes = 0;
            foreach (char c in arg)
            {
                if (c == BackslashChar) { backslashes++; continue; }
                if (c == '"') { sb.Append(BackslashChar, backslashes * 2 + 1).Append('"'); }
                else { sb.Append(BackslashChar, backslashes).Append(c); }
                backslashes = 0;
            }
            sb.Append(BackslashChar, backslashes * 2).Append('"');
            return sb.ToString();
        }
    }
}
