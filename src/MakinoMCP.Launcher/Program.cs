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
    // Starts the install's MakinoMCP.exe (versions\<current.txt>, where it is) and pumps stdin/stdout/stderr between the
    // MCP client and it as raw bytes. Stdout carries only MCP messages. A copy on a share installs itself on each PC
    // that opens it (ShareInstaller) and hands over to that copy; every install updates itself from GitHub (Updater).
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                string launcherDir = AppDomain.CurrentDomain.BaseDirectory;
                // Background install from a share onto this PC; silent (no stdout/stderr).
                if (args.Length == 1 && args[0] == "--install") return InstallInBackground(launcherDir);
                // Middle steps of the detached starts: start the work and exit at once, so it has no living parent and
                // survives the MCP client ending the session's process tree.
                if (args.Length == 1 && args[0] == "--install-detach") { StartDetached("--install", null); return 0; }
                // Background update from GitHub; silent, outcome in update-state.json.
                if (args.Length == 1 && args[0] == "--update") return RunUpdate(launcherDir);
                if (args.Length == 1 && args[0] == "--update-detach") { StartDetached("--update", null); return 0; }

                // A copy on the network, or in a folder this PC shares, is an installer: Claude never runs from it.
                if (UpdateSource.IsInstallerFolder(launcherDir.TrimEnd('\\'), UpdateSource.IsNetworkDrive, UpdateSource.LocalSharePaths()))
                    return RunFromShare(launcherDir, args);

                // Copies of older versions that PCs running from a share kept; gone once nothing runs from them.
                Task.Run(() => OldCache.Tidy(LocalApp(), d => RunningFolders().Contains(d.TrimEnd('\\'))));

                UpdateDecision update = UpdateSource.DecideForThisPc(launcherDir.TrimEnd('\\'));
                // A release zip that doesn't update itself says why, in the update log (never on stdout).
                if (update.Repository != null && !update.GitHubInstall)
                    Updater.LogLine(LocalRoot(), launcherDir.TrimEnd('\\'), DateTime.UtcNow, "not updating from GitHub: " + update.Reason);
                if (update.ShareCheckSkipped)
                    Updater.LogLine(LocalRoot(), launcherDir.TrimEnd('\\'), DateTime.UtcNow,
                        "couldn't read this PC's list of shared folders; assumed this folder isn't shared");
                if (update.UpdatesOn) StartUpdateIfDue(launcherDir);
                return RunInPlace(launcherDir, args);
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

        // Runs this install's build with its own settings file.
        private static int RunInPlace(string launcherDir, string[] args)
        {
            string exe = ResolveInPlace(launcherDir);
            if (exe == null)
            {
                Console.Error.WriteLine("MakinoMCP: no installed version found in " + launcherDir + ".");
                return 2;
            }
            return Run(exe, args, Path.Combine(launcherDir, "MakinoMCP.settings.json")).GetAwaiter().GetResult();
        }

        // A copy on a share: setup installs MakinoMCP on this PC (with a progress window) and opens the PC copy's setup
        // window, which moves Claude to it; serve hands over to the PC's copy, or, the first time, serves from the share
        // while the PC's copy installs in the background.
        private static int RunFromShare(string launcherDir, string[] args)
        {
            string local = ShareInstaller.LocalInstallDir();
            string localLauncher = Path.Combine(local, "MakinoMCP.exe");
            // %LOCALAPPDATA% redirected to the network: nowhere to install, so run from the share as it is.
            if (UpdateSource.IsInstallerFolder(local, UpdateSource.IsNetworkDrive, UpdateSource.LocalSharePaths()))
                return RunInPlace(launcherDir, args);

            if (IsSetupMode(args))
            {
                if (!ShareInstaller.IsInstalled(local))
                {
                    InstallResult r = InstallWithWindow(launcherDir, local);
                    if (!r.Installed)
                    {
                        string text = "Couldn't install MakinoMCP on this PC: " + r.Reason + ". Opening it from the share instead.";
                        if (ShowWindows()) MessageBox.Show(text, "MakinoMCP", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        else Console.Error.WriteLine("MakinoMCP: " + text);
                        return RunInPlace(launcherDir, args);
                    }
                }
                string self = Path.Combine(launcherDir, "MakinoMCP.exe");
                Process.Start(new ProcessStartInfo(localLauncher, "setup --installed-from " + Quote(self))
                {
                    UseShellExecute = false,
                    WorkingDirectory = local,
                });
                return 0;
            }

            if (ShareInstaller.IsInstalled(local)) return Run(localLauncher, args, null).GetAwaiter().GetResult();
            StartDetached("--install-detach", null);
            return RunInPlace(launcherDir, args);
        }

        private static bool ShowWindows() =>
            Environment.UserInteractive && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MAKINOMCP_NO_WINDOW"));

        // The install with a small progress window when a desktop is there; the copy runs on a worker thread and the
        // window closes itself when it ends.
        private static InstallResult InstallWithWindow(string launcherDir, string local)
        {
            if (!ShowWindows()) return ShareInstaller.Install(launcherDir, local, null);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            InstallResult result = null;
            using (var form = new CopyProgressForm())
            {
                form.Shown += (s, e) => Task.Run(() =>
                {
                    result = ShareInstaller.Install(launcherDir, local, form.Report);
                    form.Finish();
                });
                Application.Run(form);
            }
            return result ?? new InstallResult { Installed = false, Reason = "the copy didn't run" };
        }

        // The detached install: one at a time per user.
        private static int InstallInBackground(string launcherDir)
        {
            try
            {
                using (var only = new System.Threading.Mutex(false, @"Local\MakinoMCP-install"))
                {
                    bool mine;
                    try { mine = only.WaitOne(0); }
                    catch (System.Threading.AbandonedMutexException) { mine = true; }
                    if (!mine) return 0;
                    try { return ShareInstaller.Install(launcherDir, ShareInstaller.LocalInstallDir(), null).Installed ? 0 : 1; }
                    finally { only.ReleaseMutex(); }
                }
            }
            catch
            {
                return 1;
            }
        }

        // %LOCALAPPDATA%\MakinoMCP: the update state file and logs.
        private static string LocalRoot() => Path.GetDirectoryName(LocalApp());

        private static Updater NewUpdater(string launcherDir, IReleaseSource source)
        {
            return new Updater(launcherDir.TrimEnd('\\'), LocalRoot(), source, () => DateTime.UtcNow,
                Signatures.IsSignPathSigned, d =>
                {
                    var running = RunningFolders();
                    return running.Contains(d.TrimEnd('\\'));
                });
        }

        // Starts the background update when the last check is 6 hours old. Never waits and never fails the start.
        private static void StartUpdateIfDue(string launcherDir)
        {
            try
            {
                if (NewUpdater(launcherDir, null).Due()) StartDetached("--update-detach", null);
            }
            catch { }
        }

        private static int RunUpdate(string launcherDir)
        {
            try
            {
                // Checked again here: the settings or the environment may have changed since the start asked.
                if (!UpdateSource.DecideForThisPc(launcherDir.TrimEnd('\\')).UpdatesOn) return 0;
                using (var source = new GitHubReleaseSource())
                {
                    var updater = NewUpdater(launcherDir, source);
                    if (updater.Due()) updater.Run();
                }
                return 0;
            }
            catch
            {
                return 1;
            }
        }

        // versions\<current.txt>\MakinoMCP.exe, where it is; the newest build when that one is missing; the developer
        // layout (app\MakinoMCP.exe beside the launcher, no current.txt) for tests.
        private static string ResolveInPlace(string launcherDir)
        {
            string versions = Path.Combine(launcherDir, "versions");
            string current = UpdateApplier.ReadCurrent(launcherDir);
            if (current == null)
            {
                string dev = Path.Combine(launcherDir, "app", "MakinoMCP.exe");
                if (File.Exists(dev)) return dev;
            }
            if (current != null && IsValidVersion(current))
            {
                string exe = Path.Combine(versions, current, "MakinoMCP.exe");
                if (File.Exists(exe)) return exe;
            }
            Console.Error.WriteLine("MakinoMCP: the build in current.txt is missing; using the newest build in " + versions + ".");
            if (!Directory.Exists(versions)) return null;
            return Directory.GetDirectories(versions)
                .Where(d => !Path.GetFileName(d).StartsWith(".") && Path.GetFileName(d).IndexOf(".tmp", StringComparison.OrdinalIgnoreCase) < 0)
                .Where(d => File.Exists(Path.Combine(d, "MakinoMCP.exe")))
                .OrderByDescending(d => Directory.GetCreationTimeUtc(d))
                .Select(d => Path.Combine(d, "MakinoMCP.exe"))
                .FirstOrDefault();
        }

        private static string LocalApp()
        {
            return Path.Combine(
                Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MakinoMCP", "app");
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

        // Starts "MakinoMCP.exe <verb> [<version>]" detached: out of the client's job when the job allows it,
        // bInheritHandles FALSE and no std handles, so it can't hold the MCP client's stdio pipes open.
        private static void StartDetached(string verb, string version)
        {
            try
            {
                string self = Process.GetCurrentProcess().MainModule.FileName;
                var si = new STARTUPINFO();
                si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
                PROCESS_INFORMATION pi;
                string commandLine = Quote(self) + " " + verb + (version == null ? "" : " " + Quote(version));
                // A job that forbids breakaway refuses the flag (access denied): then start inside the job.
                if (!CreateProcessW(null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero,
                        false, CreateNoWindow | CreateBreakawayFromJob, IntPtr.Zero, Path.GetTempPath(), ref si, out pi)
                    && !CreateProcessW(null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero,
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
                // Another launcher (a PC's own copy, started by the share's) adds its own settings path.
                Arguments = string.Join(" ", (settingsPath == null ? args : args.Concat(new[] { "--settings", settingsPath })).Select(Quote)),
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
