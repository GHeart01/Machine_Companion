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

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetHandleInformation(IntPtr hObject, int dwMask, int dwFlags);

        // Starts "MakinoMCP.exe --prefetch <version>" detached. Best effort: the session works without it.
        private static void StartPrefetch(string version)
        {
            try
            {
                // CreateProcess inherits every inheritable handle, including the client's stdio pipes, which would
                // then stay open after the session ends. Make our own std handles non-inheritable first.
                foreach (int std in new[] { -10, -11, -12 })
                {
                    IntPtr h = GetStdHandle(std);
                    if (h != IntPtr.Zero && h != new IntPtr(-1)) SetHandleInformation(h, 1, 0);
                }

                var psi = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    // Redirected so the child gets fresh pipes instead of our (now non-inheritable) std handles;
                    // closed straight away because the prefetch never reads or writes them.
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = Path.GetTempPath(),
                    Arguments = "--prefetch " + Quote(version),
                };
                using (var child = Process.Start(psi))
                {
                    child.StandardInput.Close();
                    child.StandardOutput.Close();
                    child.StandardError.Close();
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("MakinoMCP: could not start the background copy (" + ex.Message + ").");
            }
        }

        // Copies versions\<version> from the share to a unique temp folder, then renames it into place.
        private static int Prefetch(string launcherDir, string version)
        {
            string tmp = null;
            try
            {
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
