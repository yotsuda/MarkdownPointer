using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using MarkdownViewer.Mcp.Services;

namespace MarkdownViewer.Mcp.Tools;

[McpServerToolType]
public class MarkdownViewerTools(NamedPipeClient pipeClient)
{
    private readonly NamedPipeClient _pipeClient = pipeClient;

    [McpServerTool(Name = "show_markdown"), Description("Open a Markdown file in MarkdownViewer. Supports Mermaid diagrams and KaTeX math rendering. Returns current tab status and any render errors.")]
    public async Task<string> ShowMarkdown(
        [Description("Path to the Markdown file to open")] string path,
        [Description("Optional line number to scroll to")] int? line = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            
            if (!File.Exists(fullPath))
            {
                return JsonSerializer.Serialize(
                    new ErrorResponse { Success = false, Error = $"File not found: {fullPath}" },
                    PipeJsonContext.Default.ErrorResponse);
            }
            
            var message = new PipeCommand { Command = "open", Path = fullPath, Line = line };
            var result = await _pipeClient.SendCommandAsync(message, cancellationToken);
            
            if (result == null)
            {
                return JsonSerializer.Serialize(
                    new ErrorResponse 
                    { 
                        Success = false, 
                        Error = "Failed to communicate with MarkdownViewer",
                        ViewerRunning = _pipeClient.IsViewerRunning()
                    },
                    PipeJsonContext.Default.ErrorResponse);
            }
            
            return result.RootElement.GetRawText();
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(
                new ErrorResponse { Success = false, Error = $"{ex.GetType().Name}: {ex.Message}" },
                PipeJsonContext.Default.ErrorResponse);
        }
    }
}
