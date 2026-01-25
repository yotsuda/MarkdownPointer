using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace MarkdownViewer.Mcp.Services;

public class NamedPipeClient
{
    private const string PipeName = "MarkdownViewer_Pipe";
    private const int ConnectionTimeoutMs = 10000;
    private const int BufferSize = 65536; // 64KB to match server
    
    private readonly string? _viewerExePath;
    
    public NamedPipeClient()
    {
        _viewerExePath = FindViewerExe();
    }
    
    private static string? FindViewerExe()
    {
        // 0. Environment variable override
        var envPath = Environment.GetEnvironmentVariable("MARKDOWNVIEWER_PATH");
        if (!string.IsNullOrEmpty(envPath) && File.Exists(envPath))
        {
            return envPath;
        }
        
        // Search order:
        // 1. Bundled with this MCP server (for NuGet distribution)
        var baseDir = AppContext.BaseDirectory;
        var bundledPath = Path.Combine(baseDir, "viewer", "MarkdownViewer.exe");
        if (File.Exists(bundledPath))
        {
            return bundledPath;
        }
        
        // 2. Development environment: look for sibling WPF project
        // From: MarkdownViewer.Mcp/bin/{config}/{tfm}/{rid}/
        // To:   MarkdownViewer/bin/{config}/{tfm}-windows/{rid}/
        var devPath = FindDevEnvironmentViewer(baseDir);
        if (devPath != null)
        {
            return devPath;
        }
        
        // 3. PowerShell module installation
        var psModulePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "PowerShell", "7", "Modules", "MarkdownViewer", "bin", "MarkdownViewer.exe");
        if (File.Exists(psModulePath))
        {
            return psModulePath;
        }
        
        // 4. User module path
        var userModulePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "PowerShell", "Modules", "MarkdownViewer", "bin", "MarkdownViewer.exe");
        if (File.Exists(userModulePath))
        {
            return userModulePath;
        }
        
        return null;
    }
    
    private static string? FindDevEnvironmentViewer(string baseDir)
    {
        // Try to find MarkdownViewer.exe in development structure
        // Example baseDir: .../MarkdownViewer.Mcp/bin/Debug/net8.0/win-x64/
        try
        {
            var dir = new DirectoryInfo(baseDir);
            
            // Navigate up to solution root (looking for .sln file or MarkdownViewer folder)
            var current = dir;
            while (current != null && current.Parent != null)
            {
                current = current.Parent;
                
                // Check if we found the solution directory
                var viewerProjectDir = Path.Combine(current.FullName, "MarkdownViewer");
                if (Directory.Exists(viewerProjectDir))
                {
                    // Look for exe in various build configurations
                    var configs = new[] { "Debug", "Release" };
                    var tfms = new[] { "net8.0-windows", "net9.0-windows", "net10.0-windows" };
                    var rids = new[] { "win-x64", "win-x86", "win-arm64", "" };
                    
                    foreach (var config in configs)
                    {
                        foreach (var tfm in tfms)
                        {
                            foreach (var rid in rids)
                            {
                                var exePath = string.IsNullOrEmpty(rid)
                                    ? Path.Combine(viewerProjectDir, "bin", config, tfm, "MarkdownViewer.exe")
                                    : Path.Combine(viewerProjectDir, "bin", config, tfm, rid, "MarkdownViewer.exe");
                                
                                if (File.Exists(exePath))
                                {
                                    return exePath;
                                }
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // Ignore errors in dev environment detection
        }
        
        return null;
    }
    
    public async Task<JsonDocument?> SendCommandAsync(PipeCommand message, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(message, PipeJsonContext.Default.PipeCommand);
        var bytes = Encoding.UTF8.GetBytes(json);
        Exception? lastException = null;
        
        for (int retry = 0; retry < 3; retry++)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
                
                await client.ConnectAsync(ConnectionTimeoutMs, cancellationToken);
                
                await client.WriteAsync(bytes, cancellationToken);
                await client.FlushAsync(cancellationToken);
                
                // Read response
                var buffer = new byte[BufferSize];
                var bytesRead = await client.ReadAsync(buffer, cancellationToken);
                
                if (bytesRead > 0)
                {
                    var responseJson = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    return JsonDocument.Parse(responseJson);
                }
                
                return null;
            }
            catch (TimeoutException ex)
            {
                lastException = ex;
                Console.Error.WriteLine($"[WARN] Pipe connection timeout (retry {retry + 1}/3)");
                
                // Try to start the viewer if not running
                if (retry == 0 && !IsViewerRunning())
                {
                    try
                    {
                        await StartViewerAsync();
                    }
                    catch (Exception startEx)
                    {
                        Console.Error.WriteLine($"[ERROR] Failed to start viewer: {startEx.Message}");
                        throw; // Re-throw to indicate viewer cannot be started
                    }
                }
                
                if (retry < 2)
                {
                    await Task.Delay(500, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                throw; // Don't retry on cancellation
            }
            catch (Exception ex)
            {
                lastException = ex;
                Console.Error.WriteLine($"[ERROR] Named Pipe communication failed: {ex.Message}");
                
                if (retry < 2)
                {
                    await Task.Delay(500, cancellationToken);
                }
            }
        }
        
        // All retries exhausted
        if (lastException != null)
        {
            throw new InvalidOperationException(
                $"Failed to communicate with MarkdownViewer after 3 attempts: {lastException.Message}", 
                lastException);
        }
        
        return null;
    }
    
    public bool IsViewerRunning()
    {
        return Process.GetProcessesByName("MarkdownViewer").Length > 0;
    }
    
    public async Task StartViewerAsync()
    {
        if (IsViewerRunning())
        {
            return;
        }
        
        if (_viewerExePath == null)
        {
            throw new FileNotFoundException(
                "MarkdownViewer.exe not found. " +
                "Please install MarkdownViewer module: Install-Module MarkdownViewer");
        }
        
        var startInfo = new ProcessStartInfo
        {
            FileName = _viewerExePath,
            UseShellExecute = false,
            CreateNoWindow = false
        };
        
        Process.Start(startInfo);
        
        // Wait for the viewer to initialize the Named Pipe
        for (int i = 0; i < 50; i++) // Max 5 seconds
        {
            await Task.Delay(100);
            if (IsViewerRunning())
            {
                // Additional wait for pipe initialization
                await Task.Delay(500);
                return;
            }
        }
        
        throw new TimeoutException("MarkdownViewer failed to start within 5 seconds");
    }
}
