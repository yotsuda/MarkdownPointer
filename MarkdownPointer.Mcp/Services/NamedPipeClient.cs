using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace MarkdownPointer.Mcp.Services;

public class NamedPipeClient
{
    private const string PipeName = "MarkdownPointer_Pipe";
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
        // Start viewer if not running
        if (!IsViewerRunning())
        {
            StartViewer();
        }

        var json = JsonSerializer.Serialize(message, PipeJsonContext.Default.PipeCommand);
        var bytes = Encoding.UTF8.GetBytes(json);
        Exception? lastException = null;

        // Retry loop: connect and send command directly (no separate probe step).
        // ConnectAsync waits for the pipe to become available, so this handles
        // both the "viewer just started" and "viewer already running" cases.
        for (int retry = 0; retry < 50; retry++)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
                await client.ConnectAsync(200, cancellationToken);

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
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastException = ex;
            }
        }

        throw new InvalidOperationException(
            $"Failed to communicate with MarkdownPointer after multiple attempts: {lastException?.Message}",
            lastException);
    }

    public bool IsViewerRunning()
    {
        return Process.GetProcessesByName("mdp").Length > 0;
    }

    private void StartViewer()
    {
        if (_viewerExePath == null)
        {
            throw new FileNotFoundException("mdp.exe not found in the same directory as mdp-mcp.exe");
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = _viewerExePath,
            UseShellExecute = false,
            CreateNoWindow = false
        });
    }
}
