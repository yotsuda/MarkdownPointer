using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MarkdownPointer.Mcp.Services;

namespace MarkdownPointer.Mcp.Tools;

[McpServerToolType]
public class MarkdownPointerTools(NamedPipeClient pipeClient)
{
    private readonly NamedPipeClient _pipeClient = pipeClient;

    private static readonly JsonSerializerOptions Utf8JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static string WithVersionWarning(string response)
    {
        if (Program.VersionWarning == null) return response;
        var node = JsonNode.Parse(response)!.AsObject();
        node["success"] = false;
        node["error"] = Program.VersionWarning;
        return node.ToJsonString();
    }

    [McpServerTool(Name = "show_markdown"), Description("Open one or more Markdown/SVG files in MarkdownPointer. Supports Mermaid diagrams, KaTeX math, and SVG with embedded fonts. Auto-refreshes on file changes. Returns current tab status and any render errors. When slideView is true, opens in reveal.js slide mode and returns slide state.")]
    public async Task<string> ShowMarkdown(
        [Description("Path(s) to the Markdown file(s) to open")] string[] paths,
        [Description("Optional line number to scroll to in the last opened file")] int? line = null,
        [Description("Open in slide view mode (reveal.js). Enables slide_control navigation. Keep each slide short (5-7 bullet points max) to avoid overflow.")] bool? slideView = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var fullPaths = new List<string>();
            var notFound = new List<string>();

            foreach (var p in paths)
            {
                var fullPath = Path.GetFullPath(p);
                if (File.Exists(fullPath))
                    fullPaths.Add(fullPath);
                else
                    notFound.Add(fullPath);
            }

            if (fullPaths.Count == 0)
            {
                return WithVersionWarning(JsonSerializer.Serialize(
                    new ErrorResponse { Success = false, Error = $"File(s) not found: {string.Join(", ", notFound)}" },
                    PipeJsonContext.Default.ErrorResponse));
            }

            var message = new PipeCommand { Command = "open", Paths = fullPaths.ToArray(), Line = line, SlideView = slideView };
            var result = await _pipeClient.SendCommandAsync(message, cancellationToken);

            if (result == null)
            {
                return WithVersionWarning(JsonSerializer.Serialize(
                    new ErrorResponse
                    {
                        Success = false,
                        Error = "Failed to communicate with MarkdownPointer",
                        ViewerRunning = _pipeClient.IsViewerRunning()
                    },
                    PipeJsonContext.Default.ErrorResponse));
            }

            return WithVersionWarning(result.RootElement.GetRawText());
        }
        catch (Exception ex)
        {
            return WithVersionWarning(JsonSerializer.Serialize(
                new ErrorResponse { Success = false, Error = $"{ex.GetType().Name}: {ex.Message}" },
                PipeJsonContext.Default.ErrorResponse));
        }
    }

    [McpServerTool(Name = "get_status"), Description("Get current MarkdownPointer status: open windows, tabs, selected file, and slide state if in slide view. Use this to check what the user is viewing before taking action.")]
    public async Task<string> GetStatus(CancellationToken cancellationToken = default)
    {
        try
        {
            var message = new PipeCommand { Command = "status" };
            var result = await _pipeClient.SendCommandAsync(message, cancellationToken);

            if (result == null)
            {
                return WithVersionWarning(JsonSerializer.Serialize(
                    new ErrorResponse
                    {
                        Success = false,
                        Error = "Failed to communicate with MarkdownPointer",
                        ViewerRunning = _pipeClient.IsViewerRunning()
                    },
                    PipeJsonContext.Default.ErrorResponse));
            }

            return WithVersionWarning(result.RootElement.GetRawText());
        }
        catch (Exception ex)
        {
            return WithVersionWarning(JsonSerializer.Serialize(
                new ErrorResponse { Success = false, Error = $"{ex.GetType().Name}: {ex.Message}" },
                PipeJsonContext.Default.ErrorResponse));
        }
    }

    [McpServerTool(Name = "slide_control"), Description("Control slide navigation in MarkdownPointer. Requires a file to be open in slide view (use show_markdown with slideView=true first). Returns current slide content and next slide content for presentation narration.")]
    public async Task<string> SlideControl(
        [Description("Slide action: 'next', 'prev', 'first', 'last', or 'goto'")] string action,
        [Description("Slide index for 'goto' action (0-based)")] int? index = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var message = new PipeCommand
            {
                Command = "slideControl",
                SlideAction = action,
                SlideIndex = index
            };
            var result = await _pipeClient.SendCommandAsync(message, cancellationToken);

            if (result == null)
            {
                return WithVersionWarning(JsonSerializer.Serialize(
                    new SlideControlResponse { Success = false, Error = "Failed to communicate with MarkdownPointer" },
                    Utf8JsonOptions));
            }

            // Extract slide state from pipe response
            var root = result.RootElement;
            var success = root.GetProperty("success").GetBoolean();

            if (!success)
            {
                var error = root.TryGetProperty("error", out var errProp) ? errProp.GetString() : "Unknown error";
                return WithVersionWarning(JsonSerializer.Serialize(
                    new SlideControlResponse { Success = false, Error = error },
                    Utf8JsonOptions));
            }

            if (root.TryGetProperty("slideState", out var stateProp) && stateProp.ValueKind != JsonValueKind.Null)
            {
                var response = new SlideControlResponse
                {
                    Success = true,
                    CurrentIndex = stateProp.TryGetProperty("currentIndex", out var ci) ? ci.GetInt32() : 0,
                    TotalSlides = stateProp.TryGetProperty("totalSlides", out var ts) ? ts.GetInt32() : 0,
                    CurrentContent = stateProp.TryGetProperty("currentContent", out var cc) ? cc.GetString() ?? "" : "",
                    NextContent = stateProp.TryGetProperty("nextContent", out var nc) && nc.ValueKind != JsonValueKind.Null ? nc.GetString() : null,
                    Overflowed = stateProp.TryGetProperty("overflowed", out var ov) && ov.GetBoolean()
                };
                return WithVersionWarning(JsonSerializer.Serialize(response, Utf8JsonOptions));
            }

            return WithVersionWarning(JsonSerializer.Serialize(
                new SlideControlResponse { Success = false, Error = "No slide state returned" },
                Utf8JsonOptions));
        }
        catch (Exception ex)
        {
            return WithVersionWarning(JsonSerializer.Serialize(
                new SlideControlResponse { Success = false, Error = $"{ex.GetType().Name}: {ex.Message}" },
                Utf8JsonOptions));
        }
    }

    [McpServerTool(Name = "export_document"), Description("Export a Markdown file to .docx (Word) or .pptx (PowerPoint). Output format is determined by the file extension of the output path. Defaults to .docx. Mermaid diagrams and SVG images are rendered as PNG for full fidelity. Requires Pandoc to be installed.")]
    public async Task<string> ExportDocument(
        [Description("Path to the Markdown file")] string path,
        [Description("Output file path (.docx or .pptx). Defaults to same directory with .docx extension")] string? output = null,
        [Description("Path to a .docx/.pptx template (reference-doc) for styling the output")] string? template = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var ext = output != null ? Path.GetExtension(Path.GetFullPath(output)).ToLowerInvariant() : ".docx";
            var format = ext == ".pptx" ? "pptx" : "docx";
            var fullPath = Path.GetFullPath(path);

            if (!File.Exists(fullPath))
            {
                return WithVersionWarning(JsonSerializer.Serialize(
                    new ExportResponse { Success = false, Error = $"File not found: {fullPath}" },
                    PipeJsonContext.Default.ExportResponse));
            }

            var outputPath = output != null ? Path.GetFullPath(output) : Path.ChangeExtension(fullPath, ext);

            // Try routing through MarkdownPointer app for Mermaid/SVG conversion
            try
            {
                var templatePath = template != null ? Path.GetFullPath(template) : null;
                var message = new PipeCommand
                {
                    Command = "export",
                    Path = fullPath,
                    OutputPath = outputPath,
                    TemplatePath = templatePath
                };
                var result = await _pipeClient.SendCommandAsync(message, cancellationToken);

                if (result != null)
                {
                    var root = result.RootElement;
                    var success = root.GetProperty("success").GetBoolean();
                    if (success)
                    {
                        var exportOutput = root.TryGetProperty("exportOutput", out var outProp) ? outProp.GetString() : outputPath;
                        return WithVersionWarning(JsonSerializer.Serialize(
                            new ExportResponse { Success = true, Output = exportOutput },
                            PipeJsonContext.Default.ExportResponse));
                    }
                    var error = root.TryGetProperty("error", out var errProp) ? errProp.GetString() : "Export failed";
                    return WithVersionWarning(JsonSerializer.Serialize(
                        new ExportResponse { Success = false, Error = error },
                        PipeJsonContext.Default.ExportResponse));
                }
            }
            catch
            {
                // Fall through to direct Pandoc call
            }

            // Fallback: direct Pandoc (no Mermaid/SVG conversion)
            var templateFullPath = template != null ? Path.GetFullPath(template) : null;
            var refDoc = templateFullPath != null && File.Exists(templateFullPath)
                ? $"--reference-doc=\"{templateFullPath}\" "
                : "";
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "pandoc",
                    Arguments = $"-f markdown -t {format} {refDoc}-o \"{outputPath}\" \"{fullPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                },
                EnableRaisingEvents = true
            };

            var tcs = new TaskCompletionSource<int>();
            process.Exited += (_, _) => tcs.TrySetResult(process.ExitCode);

            process.Start();
            var stderr = await process.StandardError.ReadToEndAsync();

            using var reg = cancellationToken.Register(() =>
            {
                try { process.Kill(); } catch { }
                tcs.TrySetCanceled();
            });

            await tcs.Task;

            if (process.ExitCode != 0)
            {
                var pandocError = string.IsNullOrWhiteSpace(stderr) ? "Pandoc exited with error" : stderr.Trim();
                return WithVersionWarning(JsonSerializer.Serialize(
                    new ExportResponse { Success = false, Error = pandocError },
                    PipeJsonContext.Default.ExportResponse));
            }

            return WithVersionWarning(JsonSerializer.Serialize(
                new ExportResponse { Success = true, Output = outputPath },
                PipeJsonContext.Default.ExportResponse));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return WithVersionWarning(JsonSerializer.Serialize(
                new ExportResponse { Success = false, Error = $"{ex.GetType().Name}: {ex.Message}" },
                PipeJsonContext.Default.ExportResponse));
        }
    }
}
