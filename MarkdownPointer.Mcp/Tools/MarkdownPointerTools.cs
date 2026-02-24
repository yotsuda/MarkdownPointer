using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using MarkdownPointer.Mcp.Services;

namespace MarkdownPointer.Mcp.Tools;

[McpServerToolType]
public class MarkdownPointerTools(NamedPipeClient pipeClient)
{
    private readonly NamedPipeClient _pipeClient = pipeClient;

    private static string WithVersionWarning(string response) =>
        Program.VersionWarning != null ? response + Program.VersionWarning : response;

    [McpServerTool(Name = "show_markdown"), Description("Open one or more Markdown/SVG files in MarkdownPointer. Supports Mermaid diagrams, KaTeX math, and SVG with embedded fonts. Auto-refreshes on file changes. Returns current tab status and any render errors.")]
    public async Task<string> ShowMarkdown(
        [Description("Path(s) to the Markdown file(s) to open")] string[] paths,
        [Description("Optional line number to scroll to in the last opened file")] int? line = null,
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

            var message = new PipeCommand { Command = "open", Paths = fullPaths.ToArray(), Line = line };
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

    [McpServerTool(Name = "export_docx"), Description("Convert a Markdown file to .docx using Pandoc. Requires Pandoc to be installed.")]
    public async Task<string> ExportDocx(
        [Description("Path to the Markdown file")] string path,
        [Description("Output .docx file path. Defaults to same directory with .docx extension")] string? output = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);

            if (!File.Exists(fullPath))
            {
                return WithVersionWarning(JsonSerializer.Serialize(
                    new ExportResponse { Success = false, Error = $"File not found: {fullPath}" },
                    PipeJsonContext.Default.ExportResponse));
            }

            var outputPath = output != null ? Path.GetFullPath(output) : Path.ChangeExtension(fullPath, ".docx");

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "pandoc",
                    Arguments = $"-f markdown -t docx -o \"{outputPath}\" \"{fullPath}\"",
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
                var error = string.IsNullOrWhiteSpace(stderr) ? "Pandoc exited with error" : stderr.Trim();
                return WithVersionWarning(JsonSerializer.Serialize(
                    new ExportResponse { Success = false, Error = error },
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
