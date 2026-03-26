using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace MarkdownPointer.Services
{
    public static class PandocService
    {
        public const string PandocNotFound = "PandocNotFound";

        public static async Task<(bool Success, string? Error)> ConvertAsync(
            string markdownPath, string outputPath, string? templatePath = null, string? resourceDir = null)
        {
            var ext = Path.GetExtension(outputPath).ToLowerInvariant();
            var format = ext switch
            {
                ".docx" => "docx",
                ".pptx" => "pptx",
                _ => "docx"
            };

            var refDoc = !string.IsNullOrEmpty(templatePath) && File.Exists(templatePath)
                ? $"--reference-doc=\"{templatePath}\" "
                : "";

            var resPath = !string.IsNullOrEmpty(resourceDir)
                ? $"--resource-path=\"{resourceDir}\" "
                : "";

            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "pandoc",
                        Arguments = $"-f markdown -t {format} {refDoc}{resPath}-o \"{outputPath}\" \"{markdownPath}\"",
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
                await tcs.Task;

                if (process.ExitCode != 0)
                    return (false, string.IsNullOrWhiteSpace(stderr) ? "Pandoc exited with error" : stderr.Trim());

                return (true, null);
            }
            catch (Win32Exception)
            {
                return (false, PandocNotFound);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        /// <summary>
        /// Legacy wrapper for backward compatibility (MCP server uses this).
        /// </summary>
        public static Task<(bool Success, string? Error)> ConvertToDocxAsync(
            string markdownPath, string outputPath)
            => ConvertAsync(markdownPath, outputPath);
    }
}
