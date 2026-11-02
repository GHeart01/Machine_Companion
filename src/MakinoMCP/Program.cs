using MakinoMCP.Core.Settings;
using MakinoMCP.Logging;
using MakinoMCP.Setup;
using MakinoMCP.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MakinoMCP;

internal static class Program
{
    // Synchronous so [STAThread] really applies: the setup window's folder picker and clipboard need an STA thread.
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("serve", StringComparison.OrdinalIgnoreCase))
        {
            return Serve(args).GetAwaiter().GetResult();
        }

        // No args or "setup": the setup window, saving to --settings when given, else beside the exe.
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
#pragma warning disable WFO5001 // Dark mode is still marked experimental; system colours keep it safe either way.
        Application.SetColorMode(SystemColorMode.System);
#pragma warning restore WFO5001
        Application.Run(new SetupForm(SettingsStore.DefaultPath(args)));
        return 0;
    }

    private static async Task<int> Serve(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        // stdout carries only MCP messages: every log line goes to stderr (and the tools' own log to a file).
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

        // Settings are read once, here; a change takes effect when Claude restarts the server.
        var log = new FileLog(FileLog.DefaultFolder());
        builder.Services.AddSingleton(log);
        builder.Services.AddSingleton(ToolContext.Load(SettingsStore.DefaultPath(args), log));

        builder.Services
            .AddMcpServer(o => o.ServerInstructions = ToolContext.ServerInstructions)
            .WithStdioServerTransport()
            .WithToolsFromAssembly();
        await builder.Build().RunAsync();
        return 0;
    }
}
