using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MarkdownPointer.Mcp.Services;
using MarkdownPointer.Mcp.Tools;

namespace MarkdownPointer.Mcp;

public class Program
{
    public static string? VersionWarning { get; private set; }

    public static async Task Main(string[] args)
    {
        CheckForNewerVersion();

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

    private static void CheckForNewerVersion()
    {
        try
        {
            // Expected layout: Modules/MarkdownPointer/<version>/bin/mdp-mcp.exe
            var binDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            var versionDir = Path.GetDirectoryName(binDir);
            var moduleRoot = versionDir != null ? Path.GetDirectoryName(versionDir) : null;
            if (moduleRoot == null || versionDir == null) return;

            var myVersionStr = Path.GetFileName(versionDir);
            if (!Version.TryParse(myVersionStr, out var myVersion)) return;

            foreach (var dir in Directory.GetDirectories(moduleRoot))
            {
                if (Version.TryParse(Path.GetFileName(dir), out var v) && v > myVersion)
                {
                    var newExePath = Path.Combine(dir, "bin", "mdp-mcp.exe");
                    VersionWarning =
                        $"\n\n⚠ MarkdownPointer v{v} is installed but MCP config still references v{myVersionStr}. " +
                        $"Please update your MCP server config to: {newExePath}";
                    return;
                }
            }
        }
        catch
        {
            // Not in a versioned module directory (e.g., local dev) — skip check
        }
    }
}
