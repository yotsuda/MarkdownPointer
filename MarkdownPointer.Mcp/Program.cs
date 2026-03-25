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
            .AddSingleton<NamedPipeClient>()
            .AddSingleton<SlideService>();

        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<MarkdownPointerTools>()
            .WithTools<SlideTools>();

        var host = builder.Build();

        await host.RunAsync();
    }

    private static void CheckForNewerVersion()
    {
        VersionWarning = DetectNewerVersion(AppContext.BaseDirectory);
    }

    /// <summary>
    /// Checks if a newer module version exists in a sibling directory.
    /// Expected layout: Modules/MarkdownPointer/&lt;version&gt;/bin/mdp-mcp.exe
    /// </summary>
    internal static string? DetectNewerVersion(string baseDirectory)
    {
        try
        {
            var binDir = baseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            var versionDir = Path.GetDirectoryName(binDir);
            var moduleRoot = versionDir != null ? Path.GetDirectoryName(versionDir) : null;
            if (moduleRoot == null || versionDir == null) return null;

            var myVersionStr = Path.GetFileName(versionDir);
            if (!Version.TryParse(myVersionStr, out var myVersion)) return null;

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
                return $"Tell the user: MCP config is outdated (v{myVersionStr} → v{latestVersion}). " +
                       $"Update path to: {newExePath}";
            }
            return null;
        }
        catch
        {
            // Not in a versioned module directory (e.g., local dev) — skip check
            return null;
        }
    }
}
