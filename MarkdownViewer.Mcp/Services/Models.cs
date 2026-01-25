using System.Text.Json;
using System.Text.Json.Serialization;

namespace MarkdownViewer.Mcp.Services;

// Command sent to MarkdownViewer via Named Pipe
public class PipeCommand
{
    [JsonPropertyName("Command")]
    public string Command { get; set; } = "";
    
    [JsonPropertyName("Path")]
    public string? Path { get; set; }
    
    [JsonPropertyName("Line")]
    public int? Line { get; set; }
}

// Error response from MCP tool
public class ErrorResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }
    
    [JsonPropertyName("error")]
    public string? Error { get; set; }
    
    [JsonPropertyName("viewerRunning")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ViewerRunning { get; set; }
}

// Source generator context - no reflection needed
[JsonSerializable(typeof(PipeCommand))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class PipeJsonContext : JsonSerializerContext
{
}
