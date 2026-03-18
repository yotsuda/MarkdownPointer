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

    [JsonPropertyName("Paths")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? Paths { get; set; }

    [JsonPropertyName("Line")]
    public int? Line { get; set; }

    [JsonPropertyName("SlideView")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? SlideView { get; set; }

    [JsonPropertyName("SlideAction")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SlideAction { get; set; }

    [JsonPropertyName("SlideIndex")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SlideIndex { get; set; }
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

// Slide control response from MCP tool
public class SlideControlResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("currentIndex")]
    public int CurrentIndex { get; set; }

    [JsonPropertyName("totalSlides")]
    public int TotalSlides { get; set; }

    [JsonPropertyName("currentContent")]
    public string CurrentContent { get; set; } = "";

    [JsonPropertyName("nextContent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NextContent { get; set; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; set; }
}

// Source generator context - no reflection needed
[JsonSerializable(typeof(PipeCommand))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(ExportResponse))]
[JsonSerializable(typeof(SlideControlResponse))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class PipeJsonContext : JsonSerializerContext
{
}
