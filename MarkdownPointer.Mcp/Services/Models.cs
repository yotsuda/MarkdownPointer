using System.Text.Json;
using System.Text.Json.Serialization;

namespace MarkdownPointer.Mcp.Services;

// Command sent to MarkdownPointer via Named Pipe
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

// Export response from MCP tool
public class ExportResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("output")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Output { get; set; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; set; }
}

// Source generator context - no reflection needed
[JsonSerializable(typeof(PipeCommand))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(ExportResponse))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class PipeJsonContext : JsonSerializerContext
{
}
