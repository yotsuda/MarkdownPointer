using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;
using MarkdownViewer.Mcp.Services;

namespace MarkdownViewer.Mcp.Tools;

[McpServerToolType]
public class MarkdownViewerTools(NamedPipeClient pipeClient)
{
    private readonly NamedPipeClient _pipeClient = pipeClient;

    [McpServerTool, Description("Open a Markdown file in MarkdownViewer. Supports Mermaid diagrams and KaTeX math rendering.")]
    public async Task<string> ShowMarkdown(
        [Description("Path to the Markdown file to open")] string path,
        [Description("Optional line number to scroll to")] int? line = null,
        CancellationToken cancellationToken = default)
    {
        // Resolve to absolute path
        var fullPath = Path.GetFullPath(path);
        
        if (!File.Exists(fullPath))
        {
            return $"Error: File not found: {fullPath}";
        }
        
        var message = new Dictionary<string, object>
        {
            ["Command"] = "open",
            ["Path"] = fullPath
        };
        
        if (line.HasValue)
        {
            message["Line"] = line.Value;
        }
        
        var result = await _pipeClient.SendCommandAsync(message, cancellationToken);
        
        if (result != null)
        {
            if (result.RootElement.TryGetProperty("Errors", out var errors))
            {
                return $"Opened with warnings: {errors}";
            }
            return $"Opened: {fullPath}";
        }
        
        return $"Opened: {fullPath}";
    }

    [McpServerTool, Description("Display Markdown content directly in MarkdownViewer without saving to a file.")]
    public async Task<string> ShowMarkdownContent(
        [Description("Markdown content to display")] string content,
        [Description("Title for the preview tab")] string title = "Preview",
        [Description("Optional line number to scroll to")] int? line = null,
        CancellationToken cancellationToken = default)
    {
        // Create temp file
        var tempDir = Path.Combine(Path.GetTempPath(), "MarkdownViewer");
        Directory.CreateDirectory(tempDir);
        
        var safeTitle = string.Join("_", title.Split(Path.GetInvalidFileNameChars()));
        var tempFile = Path.Combine(tempDir, $"{safeTitle}.md");
        
        await File.WriteAllTextAsync(tempFile, content, cancellationToken);
        
        var message = new Dictionary<string, object>
        {
            ["Command"] = "openTemp",
            ["Path"] = tempFile,
            ["Title"] = title
        };
        
        if (line.HasValue)
        {
            message["Line"] = line.Value;
        }
        
        var result = await _pipeClient.SendCommandAsync(message, cancellationToken);
        
        if (result != null)
        {
            if (result.RootElement.TryGetProperty("Errors", out var errors))
            {
                return $"Opened preview with warnings: {errors}";
            }
        }
        
        return $"Opened preview: {title}";
    }

    [McpServerTool, Description("Get a list of all open tabs in MarkdownViewer.")]
    public async Task<string> GetTabs(CancellationToken cancellationToken = default)
    {
        var message = new Dictionary<string, object>
        {
            ["Command"] = "getTabs"
        };
        
        var result = await _pipeClient.SendCommandAsync(message, cancellationToken);
        
        if (result == null)
        {
            return "MarkdownViewer is not running or no tabs are open.";
        }
        
        if (result.RootElement.TryGetProperty("Tabs", out var tabs))
        {
            return tabs.GetRawText();
        }
        
        return "No tabs information available.";
    }
}
