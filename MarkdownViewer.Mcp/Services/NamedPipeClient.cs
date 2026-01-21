using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace MarkdownViewer.Mcp.Services;

public class NamedPipeClient
{
    private const string PipeName = "MarkdownViewer_Pipe";
    private const int ConnectionTimeoutMs = 10000;
    private const int BufferSize = 4096;
    
    private readonly string? _viewerExePath;
    
    public NamedPipeClient()
    {
        _viewerExePath = FindViewerExe();
    }
    
    private static string? FindViewerExe()
    {
        // Search order:
        // 1. Bundled with this MCP server (for NuGet distribution)
        var baseDir = AppContext.BaseDirectory;
        var bundledPath = Path.Combine(baseDir, "viewer", "MarkdownViewer.exe");
        if (File.Exists(bundledPath))
        {
            return bundledPath;
        }
        
        // 2. PowerShell module installation
        var psModulePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "PowerShell", "7", "Modules", "MarkdownViewer", "bin", "MarkdownViewer.exe");
        if (File.Exists(psModulePath))
        {
            return psModulePath;
        }
        
        // 3. User module path
        var userModulePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "PowerShell", "Modules", "MarkdownViewer", "bin", "MarkdownViewer.exe");
        if (File.Exists(userModulePath))
        {
            return userModulePath;
        }
        
        return null;
    }
    
    public async Task<JsonDocument?> SendCommandAsync(object message, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(message, JsonContext.Default.Options);
        var bytes = Encoding.UTF8.GetBytes(json);
        
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
            catch (TimeoutException) when (retry < 2)
            {
                // Try to start the viewer if not running
                if (retry == 0 && !IsViewerRunning())
                {
                    await StartViewerAsync();
                }
                await Task.Delay(500, cancellationToken);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ERROR] Named Pipe communication failed: {ex.Message}");
                if (retry == 2) throw;
                await Task.Delay(500, cancellationToken);
            }
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

// JSON Source Generator for AOT/trimming compatibility
[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, object>))]
internal partial class JsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
