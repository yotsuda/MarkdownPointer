using System.IO;
using Microsoft.Web.WebView2.Wpf;

namespace MarkdownPointer.Services
{
    /// <summary>
    /// Shared export logic for preprocessing Mermaid/SVG and invoking Pandoc.
    /// Used by both the UI export button and the pipe server export command.
    /// </summary>
    public static class ExportService
    {
        /// <summary>
        /// Preprocesses Mermaid diagrams and SVG images to PNG, then exports via Pandoc.
        /// Returns (success, error, tempDir). Caller must clean up tempDir if non-null.
        /// </summary>
        public static async Task<(bool Success, string? Error, string? TempDir)> ExportAsync(
            string sourcePath,
            string outputPath,
            string? templatePath,
            WebView2? webView,
            MermaidExportService mermaidExportService)
        {
            string? tempDir = null;
            var markdownPath = sourcePath;
            var ext = Path.GetExtension(outputPath).ToLowerInvariant();

            try
            {
                if ((ext == ".docx" || ext == ".pptx") && webView?.CoreWebView2 != null)
                {
                    var mdContent = await File.ReadAllTextAsync(sourcePath);
                    bool modified = false;

                    // Mermaid diagrams → PNG
                    if (mdContent.Contains("```mermaid"))
                    {
                        tempDir = Path.Combine(Path.GetTempPath(), $"mdp_export_{Guid.NewGuid():N}");
                        Directory.CreateDirectory(tempDir);

                        var pngs = await mermaidExportService.CaptureAllMermaidPngsAsync(webView, tempDir);
                        if (pngs.Count > 0)
                        {
                            mdContent = MermaidExportService.ReplaceMermaidBlocksWithImages(mdContent, pngs);
                            modified = true;
                        }
                    }

                    // SVG images → PNG
                    if (MermaidExportService.ContainsSvgImages(mdContent))
                    {
                        if (tempDir == null)
                        {
                            tempDir = Path.Combine(Path.GetTempPath(), $"mdp_export_{Guid.NewGuid():N}");
                            Directory.CreateDirectory(tempDir);
                        }

                        var svgPngs = await mermaidExportService.CaptureAllInlineSvgPngsAsync(webView, tempDir);
                        if (svgPngs.Count > 0)
                        {
                            mdContent = MermaidExportService.ReplaceSvgImagesWithPngs(mdContent, svgPngs);
                            modified = true;
                        }
                    }

                    if (modified)
                    {
                        markdownPath = Path.Combine(tempDir!, "export.md");
                        await File.WriteAllTextAsync(markdownPath, mdContent);
                    }
                }

                // Pass original file's directory as resource-path so Pandoc can resolve relative images
                var resourceDir = markdownPath != sourcePath ? Path.GetDirectoryName(sourcePath) : null;

                (bool success, string? error) result;
                if (ext == ".pptx")
                    result = await SlideService.ExportPptxAsync(markdownPath, outputPath, templatePath, resourceDir);
                else
                    result = await PandocService.ConvertAsync(markdownPath, outputPath, templatePath, resourceDir);

                return (result.success, result.error, tempDir);
            }
            catch (Exception ex)
            {
                return (false, ex.Message, tempDir);
            }
        }

        /// <summary>
        /// Cleans up the temporary directory created during export.
        /// </summary>
        public static void CleanupTempDir(string? tempDir)
        {
            if (tempDir != null)
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

    }
}
