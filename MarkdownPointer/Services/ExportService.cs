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

                        // Save current Mermaid theme and re-render with default for export
                        var prevThemeJson = await webView.CoreWebView2.ExecuteScriptAsync(
                            "window._mermaidExportPrevTheme = mermaid.mermaidAPI.getConfig().theme || 'default'");
                        var prevTheme = prevThemeJson?.Trim('"') ?? "default";

                        if (prevTheme != "default")
                        {
                            // Capture screenshot and overlay it to freeze the visual
                            using var screenshotStream = new MemoryStream();
                            await webView.CoreWebView2.CapturePreviewAsync(
                                Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png,
                                screenshotStream);
                            var base64 = Convert.ToBase64String(screenshotStream.ToArray());
                            await webView.CoreWebView2.ExecuteScriptAsync(
                                "var _ov = document.createElement('div'); _ov.id='_export_overlay'; " +
                                "_ov.style.cssText='position:fixed;top:0;left:0;width:100%;height:100%;" +
                                "z-index:999999;background:url(data:image/png;base64," + base64 + ") no-repeat top left;" +
                                "background-size:100% 100%'; document.body.appendChild(_ov)");

                            await webView.CoreWebView2.ExecuteScriptAsync(
                                "mermaid.initialize({ startOnLoad: false, theme: 'default' }); " +
                                "(async()=>{ " +
                                "var sections = document.querySelectorAll('section'); " +
                                "var saved = []; sections.forEach(function(s){ " +
                                "saved.push({d:s.style.display,v:s.style.visibility}); " +
                                "s.style.display='block'; s.style.visibility='visible'; }); " +
                                "var elems = document.querySelectorAll('.mermaid'); " +
                                "for(var e of elems){ var svg = e.querySelector('svg'); if(!svg) continue; " +
                                "var src = e.getAttribute('data-mermaid-source') || ''; " +
                                "if(!src) continue; e.removeAttribute('data-processed'); e.textContent = src; " +
                                "try { await mermaid.run({ nodes: [e] }); } catch(x){} } " +
                                "sections.forEach(function(s,i){ s.style.display=saved[i].d; s.style.visibility=saved[i].v; }); " +
                                "})()");
                            await Task.Delay(500);
                        }

                        var pngs = await mermaidExportService.CaptureAllMermaidPngsAsync(webView, tempDir);

                        // Restore original Mermaid theme and show WebView
                        if (prevTheme != "default")
                        {
                            await webView.CoreWebView2.ExecuteScriptAsync(
                                $"mermaid.initialize({{ startOnLoad: false, theme: '{prevTheme}' }}); " +
                                "(async()=>{ " +
                                "var sections = document.querySelectorAll('section'); " +
                                "var saved = []; sections.forEach(function(s){ " +
                                "saved.push({d:s.style.display,v:s.style.visibility}); " +
                                "s.style.display='block'; s.style.visibility='visible'; }); " +
                                "var elems = document.querySelectorAll('.mermaid'); " +
                                "for(var e of elems){ var src = e.getAttribute('data-mermaid-source') || ''; " +
                                "if(!src) continue; e.removeAttribute('data-processed'); e.textContent = src; " +
                                "try { await mermaid.run({ nodes: [e] }); } catch(x){} } " +
                                "sections.forEach(function(s,i){ s.style.display=saved[i].d; s.style.visibility=saved[i].v; }); " +
                                "})()");
                            await Task.Delay(500);
                            await webView.CoreWebView2.ExecuteScriptAsync(
                                "var ov = document.getElementById('_export_overlay'); if(ov) ov.remove()");
                        }

                        if (pngs.Count > 0)
                        {
                            mdContent = MermaidExportService.ReplaceMermaidBlocksWithImages(mdContent, pngs);
                            modified = true;
                        }
                    }

                    // YouTube iframes → thumbnail images
                    var ytReplaced = HtmlGenerator.YouTubeIframePattern.Replace(mdContent, match =>
                    {
                        var videoId = match.Groups[1].Value;
                        return $"[![YouTube video](https://img.youtube.com/vi/{videoId}/maxresdefault.jpg)](https://www.youtube.com/watch?v={videoId})";
                    });
                    if (ytReplaced != mdContent)
                    {
                        mdContent = ytReplaced;
                        modified = true;
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
                        if (tempDir == null)
                        {
                            tempDir = Path.Combine(Path.GetTempPath(), $"mdp_export_{Guid.NewGuid():N}");
                            Directory.CreateDirectory(tempDir);
                        }
                        markdownPath = Path.Combine(tempDir, "export.md");
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
