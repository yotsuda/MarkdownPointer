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
        // 15 retries × 500ms timeout = ~7.5s max wait (enough for viewer cold start).
        const int maxRetries = 15;
        for (int retry = 0; retry < maxRetries; retry++)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
                await client.ConnectAsync(500, cancellationToken);

                await client.WriteAsync(bytes, cancellationToken);
                await client.FlushAsync(cancellationToken);

                // Read full response (may arrive in multiple chunks)
                using var ms = new MemoryStream();
                var buffer = new byte[BufferSize];
                int bytesRead;
                do
                {
                    bytesRead = await client.ReadAsync(buffer, cancellationToken);
                    if (bytesRead > 0)
                        ms.Write(buffer, 0, bytesRead);
                } while (bytesRead == buffer.Length);

                if (ms.Length > 0)
                {
                    var responseJson = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
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
                Debug.WriteLine($"Pipe connect attempt {retry + 1}/{maxRetries} failed: {ex.Message}");
            }
        }

        throw new InvalidOperationException(
            $"Failed to communicate with MarkdownPointer after {maxRetries} attempts: {lastException?.Message}",
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
