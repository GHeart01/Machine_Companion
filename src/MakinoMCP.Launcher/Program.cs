using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MakinoMCP.Launcher
{
    // Starts the real MakinoMCP.exe (copied to a local folder so the share can be updated) and pumps
    // stdin/stdout/stderr between the MCP client and it as raw bytes. Stdout carries only MCP messages.
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                string launcherDir = AppDomain.CurrentDomain.BaseDirectory;
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
                if (!Directory.Exists(target))
                {
                    string source = Path.Combine(launcherDir, "versions", version);
                    string tmp = target + ".tmp";
                    if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
                    CopyDir(source, tmp);
                    Directory.Move(tmp, target);
                }
                return Path.Combine(target, "MakinoMCP.exe");
            }
            catch (Exception ex)
            {
                // Share unreachable (or copy failed): fall back to the newest local version.
                Console.Error.WriteLine("MakinoMCP: could not update from the share (" + ex.Message + "); using newest local version.");
                return NewestLocal(local);
            }
        }

        private static string NewestLocal(string local)
        {
            if (!Directory.Exists(local)) return null;
            return Directory.GetDirectories(local)
                .Where(d => !d.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
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
