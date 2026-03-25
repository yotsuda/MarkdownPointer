using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace MarkdownPointer.Mcp.Services;

public class NamedPipeClient
{
    private const string PipeName = "MarkdownPointer_Pipe";

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
        // Start viewer if not running, or detect version mismatch
        if (!IsViewerRunning())
        {
            StartViewer();
        }
        else
        {
            ValidateViewerPath();
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

                // Write length-prefixed message
                var lengthBytes = BitConverter.GetBytes(bytes.Length);
                await client.WriteAsync(lengthBytes, cancellationToken);
                await client.WriteAsync(bytes, cancellationToken);
                await client.FlushAsync(cancellationToken);

                // Read length-prefixed response
                var respLengthBuffer = new byte[4];
                await ReadExactAsync(client, respLengthBuffer, cancellationToken);
                var respLength = BitConverter.ToInt32(respLengthBuffer, 0);
                var responseBytes = new byte[respLength];
                await ReadExactAsync(client, responseBytes, respLength, cancellationToken);

                return JsonDocument.Parse(responseBytes);
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

    private void ValidateViewerPath()
    {
        if (_viewerExePath == null) return;

        foreach (var proc in Process.GetProcessesByName("mdp"))
        {
            try
            {
                if (proc.MainModule?.FileName is string exePath)
                    ThrowIfPathMismatch(_viewerExePath, exePath);
            }
            catch (InvalidOperationException) { throw; }
            catch { /* Access denied for protected processes - skip */ }
        }
    }

    internal static void ThrowIfPathMismatch(string expectedPath, string runningPath)
    {
        var expectedDir = Path.GetDirectoryName(expectedPath);
        var runningDir = Path.GetDirectoryName(runningPath);
        if (!string.Equals(runningDir, expectedDir, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Version mismatch: running mdp.exe is at '{runningPath}', " +
                $"but this MCP server expects '{expectedPath}'. " +
                $"Kill the running mdp.exe or update the MCP client configuration.");
        }
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

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
        => await ReadExactAsync(stream, buffer, buffer.Length, ct);

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken ct)
    {
        int offset = 0;
        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), ct);
            if (read == 0) throw new EndOfStreamException("Pipe closed before all data was received");
            offset += read;
        }
    }
}
