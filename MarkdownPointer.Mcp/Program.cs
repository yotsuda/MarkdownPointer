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

            Version? latestVersion = null;
            string? latestDir = null;
            foreach (var dir in Directory.GetDirectories(moduleRoot))
            {
                if (Version.TryParse(Path.GetFileName(dir), out var v) && v > myVersion)
                {
                    if (latestVersion == null || v > latestVersion)
                    {
                        latestVersion = v;
                        latestDir = dir;
                    }
                }
            }
            if (latestVersion != null && latestDir != null)
            {
                var newExePath = Path.Combine(latestDir, "bin", "mdp-mcp.exe");
                VersionWarning =
                    $"Tell the user: MCP config is outdated (v{myVersionStr} → v{latestVersion}). " +
                    $"Update path to: {newExePath}";
            }
        }
        catch
        {
            // Not in a versioned module directory (e.g., local dev) — skip check
        }
    }
}
