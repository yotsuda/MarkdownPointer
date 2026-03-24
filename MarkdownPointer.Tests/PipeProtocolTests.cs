using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace MarkdownPointer.Tests;

/// <summary>
/// Tests for the length-prefixed Named Pipe protocol used between
/// PS module / mdp.exe / mdp-mcp.exe.
/// </summary>
public class PipeProtocolTests : IDisposable
{
    private readonly string _pipeName = $"MdpTest_{Guid.NewGuid():N}";
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(10));

    public void Dispose() => _cts.Dispose();

    [Fact]
    public async Task LengthPrefixed_RoundTrip_ReturnsCorrectData()
    {
        var request = new { command = "status" };
        var response = new { success = true, message = "ok" };

        var serverTask = RunEchoServer(response);

        // Client: write length-prefixed request, read length-prefixed response
        using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut);
        await client.ConnectAsync(_cts.Token);

        var requestBytes = JsonSerializer.SerializeToUtf8Bytes(request);
        await WriteLengthPrefixed(client, requestBytes);

        var responseBytes = await ReadLengthPrefixed(client);
        var result = JsonSerializer.Deserialize<JsonElement>(responseBytes);

        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Equal("ok", result.GetProperty("message").GetString());

        await serverTask;
    }

    [Fact]
    public async Task LengthPrefixed_LargePayload_HandledCorrectly()
    {
        // 100KB payload — well under the 10MB sanity limit
        var largeContent = new string('x', 100_000);
        var response = new { success = true, data = largeContent };

        var serverTask = RunEchoServer(response);

        using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut);
        await client.ConnectAsync(_cts.Token);

        var requestBytes = JsonSerializer.SerializeToUtf8Bytes(new { command = "test" });
        await WriteLengthPrefixed(client, requestBytes);

        var responseBytes = await ReadLengthPrefixed(client);
        var result = JsonSerializer.Deserialize<JsonElement>(responseBytes);

        Assert.Equal(largeContent, result.GetProperty("data").GetString());

        await serverTask;
    }

    [Fact]
    public async Task OldProtocol_NoLengthPrefix_ServerRejectsGracefully()
    {
        // Simulate old client sending JSON without length prefix.
        // Server reads first 4 bytes as length → huge number → sanity check rejects.
        var serverReceivedMessage = false;

        var serverTask = Task.Run(async () =>
        {
            using var server = new NamedPipeServerStream(
                _pipeName, PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            await server.WaitForConnectionAsync(_cts.Token);

            var lengthBuffer = new byte[4];
            await ReadExact(server, lengthBuffer, _cts.Token);
            var length = BitConverter.ToInt32(lengthBuffer, 0);

            // Sanity check (same as PipeServer.HandleConnectionAsync)
            if (length <= 0 || length > 10 * 1024 * 1024)
            {
                // Invalid length — reject
                return;
            }

            serverReceivedMessage = true;
        });

        // Old client: sends JSON without length prefix
        using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut);
        await client.ConnectAsync(_cts.Token);

        var json = """{"command":"open","paths":["test.md"]}""";
        var bytes = Encoding.UTF8.GetBytes(json);
        try
        {
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
        }
        catch (IOException)
        {
            // Server may close pipe before client finishes writing — expected
        }

        await serverTask;

        // Server should NOT have processed the message
        Assert.False(serverReceivedMessage);
    }

    [Fact]
    public async Task OldProtocol_JsonStartBytes_ParsedAsHugeLength()
    {
        // Verify that `{"co` interpreted as int32 produces an unreasonable value
        var jsonStart = Encoding.UTF8.GetBytes("{\"co");
        var length = BitConverter.ToInt32(jsonStart, 0);

        // {"co = 0x6F 0x63 0x22 0x7B in memory (little-endian) = 1,869,816,443
        Assert.True(length > 10 * 1024 * 1024, $"Expected huge length, got {length}");
    }

    // --- Helpers ---

    private async Task RunEchoServer(object response)
    {
        await Task.Run(async () =>
        {
            using var server = new NamedPipeServerStream(
                _pipeName, PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            await server.WaitForConnectionAsync(_cts.Token);

            // Read length-prefixed request
            var requestBytes = await ReadLengthPrefixedServer(server);
            Assert.True(requestBytes.Length > 0);

            // Write length-prefixed response
            var responseBytes = JsonSerializer.SerializeToUtf8Bytes(response);
            await WriteLengthPrefixed(server, responseBytes);
        });
    }

    private static async Task WriteLengthPrefixed(Stream stream, byte[] data)
    {
        var lengthBytes = BitConverter.GetBytes(data.Length);
        await stream.WriteAsync(lengthBytes);
        await stream.WriteAsync(data);
        await stream.FlushAsync();
    }

    private static async Task<byte[]> ReadLengthPrefixed(Stream stream)
    {
        var ct = CancellationToken.None;
        var lengthBuffer = new byte[4];
        await ReadExact(stream, lengthBuffer, ct);
        var length = BitConverter.ToInt32(lengthBuffer, 0);
        var data = new byte[length];
        await ReadExact(stream, data, ct);
        return data;
    }

    private static async Task<byte[]> ReadLengthPrefixedServer(Stream stream)
    {
        var ct = CancellationToken.None;
        var lengthBuffer = new byte[4];
        await ReadExact(stream, lengthBuffer, ct);
        var length = BitConverter.ToInt32(lengthBuffer, 0);
        if (length <= 0 || length > 10 * 1024 * 1024)
            throw new InvalidOperationException($"Invalid message length: {length}");
        var data = new byte[length];
        await ReadExact(stream, data, ct);
        return data;
    }

    private static async Task ReadExact(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct);
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
    }
}
