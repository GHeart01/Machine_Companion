using System.Diagnostics;
using System.Management;

namespace MakinoMCP.Setup;

/// <summary>One MakinoMCP.exe process: its id, its parent's, its command line (null when Windows won't say) and its
/// logon session.</summary>
public sealed record ProcessRow(int Pid, int ParentPid, string? CommandLine, int SessionId);

/// <summary>How many MakinoMCP processes were stopped, and how many couldn't be. <paramref name="Problem"/> says why
/// the running processes couldn't even be listed.</summary>
public sealed record StopResult(int Stopped, int Failed, string? Problem = null);

/// <summary>Finds and stops the MakinoMCP servers that Claude started for this user (the launcher and the copy it
/// runs, both with <c>serve</c> as their first argument), so turning the switch off takes effect now. Never this setup
/// window or the launcher that opened it.</summary>
public static class RunningServers
{
    private const string ProcessName = "MakinoMCP";

    /// <summary>The processes to stop: in serve mode, in this logon session, and not this process or its parent.</summary>
    public static IReadOnlyList<int> ToStop(IEnumerable<ProcessRow> rows, int currentPid, int currentParentPid, int sessionId) =>
        rows.Where(r => r.SessionId == sessionId
                        && r.Pid != currentPid
                        && r.Pid != currentParentPid
                        && IsServe(r.CommandLine))
            .Select(r => r.Pid)
            .Order()
            .ToList();

    /// <summary>True when the first argument is <c>serve</c>, as Program and the launcher decide it. A path that
    /// merely contains "serve" (\\server\…) doesn't count.</summary>
    public static bool IsServe(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return false;
        var args = CommandLine.Split(commandLine);
        return args.Count >= 2 && args[1].Equals("serve", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Stops this user's MakinoMCP servers. A process that has already ended is not counted.</summary>
    public static StopResult StopForThisUser()
    {
        using var self = Process.GetCurrentProcess();
        List<ProcessRow> rows;
        int parent;
        try
        {
            rows = Query();
            parent = ParentOf(self.Id);
        }
        catch (Exception e) // WMI can fail in many ways (service off, COM, type initialisation): say so, never crash
        {
            return new StopResult(0, 0, $"Couldn't list the running MakinoMCP processes: {e.Message}");
        }
        var stopped = 0;
        var failed = 0;
        foreach (var pid in ToStop(rows, self.Id, parent, self.SessionId))
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                // The id may have been reused since the query: only ever stop a MakinoMCP.
                if (!p.ProcessName.Equals(ProcessName, StringComparison.OrdinalIgnoreCase)) continue;
                p.Kill();
                p.WaitForExit(TimeSpan.FromSeconds(5));
                stopped++;
            }
            catch (ArgumentException) { /* already gone */ }
            catch (InvalidOperationException) { /* already gone */ }
            catch (System.ComponentModel.Win32Exception) { failed++; }
        }
        return new StopResult(stopped, failed);
    }

    private static List<ProcessRow> Query()
    {
        using var search = new ManagementObjectSearcher(
            $"SELECT ProcessId, ParentProcessId, CommandLine, SessionId FROM Win32_Process WHERE Name = '{ProcessName}.exe'");
        using var results = search.Get();
        var rows = new List<ProcessRow>();
        foreach (var o in results)
        {
            using (o)
            {
                rows.Add(new ProcessRow(
                    Convert.ToInt32(o["ProcessId"], System.Globalization.CultureInfo.InvariantCulture),
                    Convert.ToInt32(o["ParentProcessId"], System.Globalization.CultureInfo.InvariantCulture),
                    o["CommandLine"] as string,
                    Convert.ToInt32(o["SessionId"], System.Globalization.CultureInfo.InvariantCulture)));
            }
        }
        return rows;
    }

    private static int ParentOf(int pid)
    {
        using var search = new ManagementObjectSearcher($"SELECT ParentProcessId FROM Win32_Process WHERE ProcessId = {pid}");
        using var results = search.Get();
        foreach (var o in results)
        {
            using (o) return Convert.ToInt32(o["ParentProcessId"], System.Globalization.CultureInfo.InvariantCulture);
        }
        return -1;
    }
}
