using MakinoMCP.Core.Settings;
using MakinoMCP.Logging;
using MakinoMCP.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MakinoMCP;

internal static class Program
{
    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("serve", StringComparison.OrdinalIgnoreCase))
        {
            return await Serve(args);
        }

        // No args or "setup": the setup window (placeholder until the real one lands).
        System.Windows.Forms.Application.EnableVisualStyles();
        System.Windows.Forms.Application.Run(new System.Windows.Forms.Form { Text = "MakinoMCP setup" });
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
