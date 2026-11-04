using System.Diagnostics;
using System.Text;

namespace MakinoMCP.Setup;

/// <summary>What a finished command printed. <paramref name="TimedOut"/> means it was stopped before it ended.</summary>
public sealed record CommandResult(int ExitCode, string Output, string Error, bool TimedOut = false)
{
    /// <summary>Stdout and stderr together, for parsers that don't care which stream a line came on.</summary>
    public string AllText => Error.Length == 0 ? Output : Output.Length == 0 ? Error : Output + "\n" + Error;
}

/// <summary>An argument that cmd.exe would change (%, !, a quote or a line break), so it can't be passed to a .cmd.</summary>
public sealed class CmdArgumentException() : Exception(
    "This folder's name can't be passed to the npm install of Claude Code; use the install line below or Claude Code's own installer.");

/// <summary>Runs a program and captures what it prints. Injectable so tests never run the real Claude CLI.</summary>
public interface ICommandRunner
{
    Task<CommandResult> RunAsync(string program, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct);
}

/// <summary>Runs commands with no console window, stdin closed and both outputs captured as UTF-8. Gives up after
/// the timeout and stops the command and anything it started.</summary>
public sealed class ProcessCommandRunner(string workingDirectory) : ICommandRunner
{
    public async Task<CommandResult> RunAsync(string program, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var (file, arguments) = CommandLine.For(program, args);
        var psi = new ProcessStartInfo(file, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory,
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"{Path.GetFileName(program)} didn't start.");
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var error = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token);
            // Something it started may still hold the pipes open: the same limit covers reading them to the end.
            await Task.WhenAll(output, error).WaitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { /* already gone */ }
            ct.ThrowIfCancellationRequested();
            return new CommandResult(-1, "", "", TimedOut: true);
        }
        return new CommandResult(process.ExitCode, await output, await error);
    }
}

/// <summary>Windows command-line quoting, the rules programs use to split their arguments (CommandLineToArgvW).</summary>
public static class CommandLine
{
    /// <summary>True for a .cmd or .bat (an npm install of Claude Code), which Windows runs through cmd.exe.</summary>
    public static bool RunsThroughCmd(string program)
    {
        var ext = Path.GetExtension(program);
        return ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || ext.Equals(".bat", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when cmd.exe would change the text even inside quotes: % and ! expand variables, a quote ends
    /// the quoting, a line break ends the command. (&amp; ^ | &lt; &gt; and parentheses are safe inside quotes.)</summary>
    public static bool ChangedByCmd(string arg) => arg.IndexOfAny(['%', '!', '"', '\n', '\r']) >= 0;

    /// <summary>The file to start and its argument string. A .cmd or .bat goes through cmd.exe with AutoRun off
    /// (/d), every argument quoted (so &amp; and ^ stay literal) and the whole line quoted once more for /s. Throws
    /// <see cref="CmdArgumentException"/> for text cmd.exe would change anyway.</summary>
    public static (string FileName, string Arguments) For(string program, IReadOnlyList<string> args)
    {
        if (!RunsThroughCmd(program)) return (program, Join(args));

        if (ChangedByCmd(program) || args.Any(ChangedByCmd)) throw new CmdArgumentException();
        var cmd = Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } comSpec
            ? comSpec
            : Path.Combine(Environment.SystemDirectory, "cmd.exe");
        return (cmd, $"/d /s /c \"\"{program}\" {string.Join(" ", args.Select(a => Quote(a, always: true)))}\"");
    }

    public static string Join(IEnumerable<string> args) => string.Join(" ", args.Select(a => Quote(a)));

    /// <summary>One argument, quoted when it needs it (or <paramref name="always"/>). Backslashes are doubled only where
    /// they precede a quote (inside the argument or the closing one), so paths stay as they are.</summary>
    public static string Quote(string arg, bool always = false)
    {
        if (!always && arg.Length > 0 && arg.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0) return arg;

        var b = new StringBuilder(arg.Length + 2).Append('"');
        var slashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                slashes++;
                continue;
            }
            if (c == '"')
            {
                b.Append('\\', slashes * 2 + 1).Append('"');
            }
            else
            {
                b.Append('\\', slashes).Append(c);
            }
            slashes = 0;
        }
        return b.Append('\\', slashes * 2).Append('"').ToString();
    }

    /// <summary>Splits a command line the way a program's startup code does: spaces separate arguments outside
    /// quotes; 2n backslashes before a quote give n and toggle quoting, 2n+1 give n and a literal quote.</summary>
    public static IReadOnlyList<string> Split(string commandLine)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var any = false;
        for (var i = 0; i < commandLine.Length; i++)
        {
            var c = commandLine[i];
            if (c == '\\')
            {
                var start = i;
                while (i < commandLine.Length && commandLine[i] == '\\') i++;
                var count = i - start;
                if (i < commandLine.Length && commandLine[i] == '"')
                {
                    current.Append('\\', count / 2);
                    if (count % 2 == 1) current.Append('"');
                    else inQuotes = !inQuotes;
                }
                else
                {
                    current.Append('\\', count);
                    i--;
                }
                any = true;
            }
            else if (c == '"')
            {
                // "" inside quotes is a literal quote (as the C runtime reads it).
                if (inQuotes && i + 1 < commandLine.Length && commandLine[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else inQuotes = !inQuotes;
                any = true;
            }
            else if (!inQuotes && (c == ' ' || c == '\t'))
            {
                if (any) args.Add(current.ToString());
                current.Clear();
                any = false;
            }
            else
            {
                current.Append(c);
                any = true;
            }
        }
        if (any) args.Add(current.ToString());
        return args;
    }
}
