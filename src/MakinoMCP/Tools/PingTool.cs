using System.ComponentModel;
using ModelContextProtocol.Server;

namespace MakinoMCP.Tools;

[McpServerToolType]
public static class PingTool
{
    [McpServerTool(Name = "ping"), Description("Returns pong. Used to check the server is alive.")]
    public static string Ping() => "pong";
}
