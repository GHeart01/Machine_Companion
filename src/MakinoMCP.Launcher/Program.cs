using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MakinoMCP.Launcher
{
    // Starts the real MakinoMCP.exe (a local copy if present; otherwise straight from the share while a background copy is made) and pumps
    // stdin/stdout/stderr between the MCP client and it as raw bytes. Stdout carries only MCP messages.
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                string launcherDir = AppDomain.CurrentDomain.BaseDirectory;
                // Background copy started when this PC lacks the current build; must stay silent (no stdout/stderr).
                if (args.Length == 2 && args[0] == "--prefetch") return Prefetch(launcherDir, args[1]);
                // Middle step of StartPrefetch: starts the copy and exits at once, so the copy has no living parent
                // and survives the MCP client ending the session's process tree.
                if (args.Length == 2 && args[0] == "--prefetch-detach") { StartDetached("--prefetch", args[1]); return 0; }
                // Setup (no arguments, or "setup") prepares this PC first: the copy is finished before the setup window
                // opens, while a small window shows progress. Serving never waits for a copy (see ResolveExe).
                if (IsSetupMode(args)) PrepareThisPc(launcherDir);
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

        internal static bool IsSetupMode(string[] args)
        {
            return args.Length == 0 || args[0].Equals("setup", StringComparison.OrdinalIgnoreCase);
        }

        // Copies the current version to this PC if it is not there yet, with a progress window when a desktop is
        // available. Never throws: on failure it says so and the caller runs the share copy.
        private static void PrepareThisPc(string launcherDir)
        {
            try
            {
                string currentTxt = Path.Combine(launcherDir, "current.txt");
                if (!File.Exists(currentTxt)) return;
                string version = File.ReadAllText(currentTxt).Trim();
                if (!IsValidVersion(version)) return;
                if (Directory.Exists(Path.Combine(LocalApp(), version))) return;
                if (!Directory.Exists(Path.Combine(launcherDir, "versions", version))) return;

                Exception error = null;
                bool showWindow = Environment.UserInteractive
                    && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MAKINOMCP_NO_WINDOW"));
                if (showWindow) error = CopyWithWindow(launcherDir, version);
                else
                {
                    try { CopyVersionToLocal(launcherDir, version, null); } catch (Exception ex) { error = ex; }
                }

                if (error != null)
                {
                    string text = "Couldn't copy MakinoMCP to this PC: " + error.Message + ". Opening it from the share instead.";
                    if (showWindow) MessageBox.Show(text, "MakinoMCP", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    else Console.Error.WriteLine("MakinoMCP: " + text);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("MakinoMCP: could not prepare this PC (" + ex.Message + ").");
            }
        }

        // A small progress window; the copy runs on a worker thread and the window closes itself when it ends.
        private static Exception CopyWithWindow(string launcherDir, string version)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Exception error = null;
            using (var form = new CopyProgressForm())
            {
                form.Shown += (s, e) => Task.Run(() =>
                {
                    try { CopyVersionToLocal(launcherDir, version, form.Report); }
                    catch (Exception ex) { error = ex; }
                    form.Finish();
                });
                Application.Run(form);
            }
            return error;
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
                if (Directory.Exists(target) && !File.Exists(Path.Combine(target, "MakinoMCP.exe")))
                {
                    // A copy cut short by an interrupted delete: drop it, so this start copies the build again.
                    try { Directory.Delete(target, true); } catch { }
                }
                if (File.Exists(Path.Combine(target, "MakinoMCP.exe")))
                {
                    // Every publish is a new build folder, so older copies pile up: remove those no MakinoMCP runs from.
                    // In the background (the launcher lives for the whole session), silent like everything before Run.
                    // Matched by build name under an app\ folder, so a short (8.3) LOCALAPPDATA path can't hide one.
                    Task.Run(() =>
                    {
                        var running = RunningFolders()
                            .Where(f => string.Equals(Path.GetFileName(Path.GetDirectoryName(f)), "app", StringComparison.OrdinalIgnoreCase))
                            .Select(Path.GetFileName)
                            .ToList();
                        VersionCopier.RemoveOtherVersions(local, version,
                            d => running.Contains(Path.GetFileName(d), StringComparer.OrdinalIgnoreCase));
                    });
                    return Path.Combine(target, "MakinoMCP.exe");
                }

                // This build isn't on the PC yet. Copying it, or even running it from the share, is slower than a
                // client's connect timeout, so copy it in a detached background process for the next start and serve
                // this session from the PC's newest copy; a PC that has none runs this build straight from the share.
                string shareExe = Path.Combine(launcherDir, "versions", version, "MakinoMCP.exe");
                if (!File.Exists(shareExe)) throw new FileNotFoundException("version " + version + " is not on the share");
                StartPrefetch(version);
                return NewestLocal(local) ?? shareExe;
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

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int access, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageNameW(IntPtr process, int flags, StringBuilder name, ref int size);

        private const int ProcessQueryLimitedInformation = 0x1000;

        // The folders every running MakinoMCP.exe on this PC was started from (any user, any bitness: this asks Windows
        // for the image path instead of reading the process's modules). A process that can't be asked is skipped.
        private static System.Collections.Generic.HashSet<string> RunningFolders()
        {
            var folders = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Process p in Process.GetProcessesByName("MakinoMCP"))
            {
                try
                {
                    IntPtr h = OpenProcess(ProcessQueryLimitedInformation, false, p.Id);
                    if (h == IntPtr.Zero) continue;
                    try
                    {
                        var name = new StringBuilder(1024);
                        int size = name.Capacity;
                        if (QueryFullProcessImageNameW(h, 0, name, ref size))
                            folders.Add(Path.GetDirectoryName(name.ToString()).TrimEnd('\\'));
                    }
                    finally { CloseHandle(h); }
                }
                catch { }
                finally { p.Dispose(); }
            }
            return folders;
        }

        private const int CreateNoWindow = 0x08000000;

        private const int CreateBreakawayFromJob = 0x01000000;

        // Copies <version> to this PC in the background: "--prefetch-detach" starts "--prefetch" and exits, so the copy
        // outlives an MCP client that ends the session's whole process tree when the session closes (measured: a copy
        // started as a direct child died with a 5-second session, leaving a half-copied build). Best effort: the
        // session works without it.
        private static void StartPrefetch(string version)
        {
            StartDetached("--prefetch-detach", version);
        }

        // Starts "MakinoMCP.exe <verb> <version>" detached: out of the client's job when the job allows it,
        // bInheritHandles FALSE and no std handles, so it can't hold the MCP client's stdio pipes open.
        private static void StartDetached(string verb, string version)
        {
            try
            {
                string self = Process.GetCurrentProcess().MainModule.FileName;
                var si = new STARTUPINFO();
                si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
                PROCESS_INFORMATION pi;
                // A job that forbids breakaway refuses the flag (access denied): then start inside the job.
                if (!CreateProcessW(null, new StringBuilder(Quote(self) + " " + verb + " " + Quote(version)), IntPtr.Zero, IntPtr.Zero,
                        false, CreateNoWindow | CreateBreakawayFromJob, IntPtr.Zero, Path.GetTempPath(), ref si, out pi)
                    && !CreateProcessW(null, new StringBuilder(Quote(self) + " " + verb + " " + Quote(version)), IntPtr.Zero, IntPtr.Zero,
                        false, CreateNoWindow, IntPtr.Zero, Path.GetTempPath(), ref si, out pi))
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

        private static int Prefetch(string launcherDir, string version)
        {
            try
            {
                if (!IsValidVersion(version)) return 1;
                // One copy per build at a time: sessions started in quick succession each ask for one.
                using (var only = new System.Threading.Mutex(false, @"Local\MakinoMCP-prefetch-" + version))
                {
                    bool mine;
                    try { mine = only.WaitOne(0); }
                    catch (System.Threading.AbandonedMutexException) { mine = true; }
                    if (!mine) return 0;
                    try { CopyVersionToLocal(launcherDir, version, null); }
                    finally { only.ReleaseMutex(); }
                }
                return 0;
            }
            catch
            {
                return 1;
            }
        }

        // Copies versions\<version> from the share into place; throws on failure. progress gets (bytes copied, total).
        private static void CopyVersionToLocal(string launcherDir, string version, Action<long, long> progress)
        {
            VersionCopier.CopyToLocal(Path.Combine(launcherDir, "versions", version), LocalApp(), version, progress);
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
