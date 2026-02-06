using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace MarkdownPointer.Mcp.Services;

public class NamedPipeClient
{
    private const string PipeName = "MarkdownPointer_Pipe";
    private const int ConnectionTimeoutMs = 5000;
    private const int BufferSize = 65536; // 64KB to match server
    
    private readonly string? _viewerExePath;
    
    public NamedPipeClient()
    {
        _viewerExePath = FindViewerExe();
    }
    
    private static string? FindViewerExe()
    {
        var exePath = Path.Combine(AppContext.BaseDirectory, "mdp.exe");
        return File.Exists(exePath) ? exePath : null;
    }

    public async Task<JsonDocument?> SendCommandAsync(PipeCommand message, CancellationToken cancellationToken = default)
    {
        // Start viewer first if not running
        if (!IsViewerRunning())
        {
            await StartViewerAsync();
        }
        
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
                $"Failed to communicate with MarkdownPointer after 3 attempts: {lastException.Message}", 
                lastException);
        }
        
        return null;
    }
    
    public bool IsViewerRunning()
    {
        return Process.GetProcessesByName("mdp").Length > 0;
    }
    
    public async Task StartViewerAsync()
    {
        if (IsViewerRunning())
        {
            return;
        }
        
        if (_viewerExePath == null)
        {
            throw new FileNotFoundException("mdp.exe not found in the same directory as mdp-mcp.exe");
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
        
        throw new TimeoutException("MarkdownPointer failed to start within 5 seconds");
    }
}
