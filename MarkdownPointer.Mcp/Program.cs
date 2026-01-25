using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MarkdownPointer.Mcp.Services;
using MarkdownPointer.Mcp.Tools;

namespace MarkdownPointer.Mcp;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.Logging.AddConsole(consoleLogOptions =>
        {
            // Configure all logs to go to stderr (required for MCP stdio transport)
            consoleLogOptions.LogToStandardErrorThreshold = LogLevel.Trace;
        });

        builder.Services
            .AddSingleton<NamedPipeClient>();

        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<MarkdownPointerTools>();

        var host = builder.Build();

        await host.RunAsync();
    }
}
