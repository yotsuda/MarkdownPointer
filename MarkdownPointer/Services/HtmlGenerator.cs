using System.IO;
using System.Text.RegularExpressions;
using System.Text;
using Markdig;
using MarkdownPointer.Resources;

namespace MarkdownPointer.Services
{
    /// <summary>
    /// Generates HTML content from Markdown source.
    /// </summary>
    public class HtmlGenerator
    {
        private readonly MarkdownPipeline _pipeline;

        // Pre-compiled regex patterns for image inlining
        private static readonly Regex LocalImagePattern = new(
            @"(<img\s+[^>]*?src\s*=\s*[""'])([^""']+?)([""'][^>]*?/?>)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly Regex SvgImagePattern = new(
            @"<img\s+([^>]*?)src\s*=\s*[""']([^""']+\.svg)(?:\?[^""']*)?[""']([^>]*?)\/?>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly Regex XmlDeclarationPattern = new(
            @"<\?xml[^?]*\?>\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex AltAttributePattern = new(
            @"alt\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex SvgOpenTagPattern = new(
            @"(<svg\s)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public HtmlGenerator(MarkdownPipeline pipeline)
        {
            _pipeline = pipeline;
        }

        /// <summary>
        /// Converts Markdown content to HTML with line tracking, KaTeX, and Mermaid support.
        /// </summary>
        /// <param name="markdown">Markdown source text</param>
        /// <param name="baseDir">Base directory for resolving relative paths</param>
        /// <returns>Complete HTML document</returns>
        public string ConvertToHtml(string markdown, string baseDir)
        {
            // Parse markdown to AST
            var document = Markdown.Parse(markdown, _pipeline);

            // Render with line tracking
            using var writer = new StringWriter();
            var renderer = new LineTrackingHtmlRenderer(writer);
            _pipeline.Setup(renderer);
            renderer.ReplaceExtensionRenderers();
            renderer.Render(document);
            var htmlContent = writer.ToString();

            // Inline SVG images to enable embedded fonts
            htmlContent = InlineSvgImages(htmlContent, baseDir);

            // Inline local images as base64 data URIs
            // (NavigateToString loads from about:blank, which blocks file:// access)
            htmlContent = InlineLocalImages(htmlContent, baseDir);

            // Convert path for file:// URL
            var baseUrl = new Uri(baseDir + Path.DirectorySeparatorChar).AbsoluteUri;

            // Generate nonce for CSP
            var nonce = Convert.ToBase64String(Guid.NewGuid().ToByteArray());

            var html = new StringBuilder();
            html.AppendLine("<!DOCTYPE html>");
            html.AppendLine("<html><head>");
            html.AppendLine("<meta charset='utf-8'/>");
            html.AppendLine($"<meta http-equiv='Content-Security-Policy' content=\"default-src 'none'; style-src 'unsafe-inline' https://cdn.jsdelivr.net; img-src file: data: blob: https: http:; script-src 'nonce-{nonce}' 'unsafe-eval' https://cdn.jsdelivr.net; font-src https://cdn.jsdelivr.net data:;\"/>");
            html.AppendLine($"<base href='{baseUrl}'/>");

            // CSS
            html.AppendLine("<style>");
            html.AppendLine(CssResources.MainStyles);
            html.AppendLine("</style>");

            // External libraries
            html.AppendLine("<link rel='stylesheet' href='https://cdn.jsdelivr.net/npm/katex@0.16.9/dist/katex.min.css'/>");
            html.AppendLine("<script defer src='https://cdn.jsdelivr.net/npm/katex@0.16.9/dist/katex.min.js'></script>");
            html.AppendLine("<script defer src='https://cdn.jsdelivr.net/npm/katex@0.16.9/dist/contrib/auto-render.min.js'></script>");
            html.AppendLine("<script src='https://cdn.jsdelivr.net/npm/html2canvas@1.4.1/dist/html2canvas.min.js'></script>");

            // Core event handlers
            html.AppendLine($"<script nonce='{nonce}'>");
            html.AppendLine(JsResources.CoreEventHandlers);
            html.AppendLine("</script>");

            // Scroll and pointing mode
            html.AppendLine($"<script nonce='{nonce}'>");
            html.AppendLine(JsResources.ScrollAndPointingMode);
            html.AppendLine(JsResources.PointingHelpers);
            html.AppendLine(JsResources.GetElementContent);
            html.AppendLine(JsResources.PointingEventHandlers);
            html.AppendLine("</script>");

            // Mermaid
            html.AppendLine("<script src='https://cdn.jsdelivr.net/npm/mermaid@11.12.3/dist/mermaid.min.js'></script>");
            html.AppendLine($"<script nonce='{nonce}'>mermaid.initialize({{ startOnLoad: false, theme: 'default' }});</script>");

            html.AppendLine("</head><body>");
            html.AppendLine(htmlContent);

            // DOMContentLoaded: KaTeX + Mermaid rendering
            html.AppendLine($"<script nonce='{nonce}'>");
            html.AppendLine(GetDomContentLoadedScript());
            html.AppendLine("</script>");

            html.AppendLine("</body></html>");

            return html.ToString();
        }

        /// <summary>
        /// Gets the DOMContentLoaded script for KaTeX and Mermaid rendering.
        /// </summary>
        private static string GetDomContentLoadedScript()
        {
            return JsResources.DomContentLoadedHandler + JsResources.MermaidNodeProcessing;
        }

        /// <summary>
        /// Inlines local images as base64 data URIs.
        /// NavigateToString() loads from about:blank, which blocks file:// access.
        /// </summary>
        private static string InlineLocalImages(string html, string baseDir)
        {
            return LocalImagePattern.Replace(html, match =>
            {
                var prefix = match.Groups[1].Value;
                var src = match.Groups[2].Value;
                var suffix = match.Groups[3].Value;

                // Skip absolute URLs and data URIs
                if (src.Contains("://") || src.StartsWith("data:"))
                    return match.Value;

                try
                {
                    var fullPath = Path.IsPathRooted(src)
                        ? src
                        : Path.GetFullPath(Path.Combine(baseDir, src));

                    if (!File.Exists(fullPath))
                        return match.Value;

                    var ext = Path.GetExtension(fullPath).ToLowerInvariant();
                    var mime = ext switch
                    {
                        ".png" => "image/png",
                        ".jpg" or ".jpeg" => "image/jpeg",
                        ".gif" => "image/gif",
                        ".webp" => "image/webp",
                        ".bmp" => "image/bmp",
                        ".ico" => "image/x-icon",
                        _ => null
                    };
                    if (mime is null)
                        return match.Value;

                    var bytes = File.ReadAllBytes(fullPath);
                    var base64 = Convert.ToBase64String(bytes);
                    return prefix + $"data:{mime};base64,{base64}" + suffix;
                }
                catch
                {
                    return match.Value;
                }
            });
        }

        /// <summary>
        /// Replaces SVG image tags with inline SVG content to enable embedded fonts.
        /// </summary>
        private static string InlineSvgImages(string html, string baseDir)
        {
            return SvgImagePattern.Replace(html, match =>
            {
                var beforeSrc = match.Groups[1].Value;
                var svgPath = match.Groups[2].Value;
                var afterSrc = match.Groups[3].Value;

                try
                {
                    // Resolve relative path
                    string fullPath;
                    if (Path.IsPathRooted(svgPath))
                    {
                        fullPath = svgPath;
                    }
                    else
                    {
                        fullPath = Path.GetFullPath(Path.Combine(baseDir, svgPath));
                    }

                    if (!File.Exists(fullPath))
                    {
                        return match.Value; // Keep original if file not found
                    }

                    var svgContent = File.ReadAllText(fullPath);

                    // Remove XML declaration if present
                    svgContent = XmlDeclarationPattern.Replace(svgContent, "");

                    // Extract alt attribute for aria-label
                    var altMatch = AltAttributePattern.Match(beforeSrc + afterSrc);
                    if (altMatch.Success)
                    {
                        var altText = altMatch.Groups[1].Value;
                        svgContent = SvgOpenTagPattern.Replace(svgContent, $"$1aria-label=\"{altText}\" ");
                    }

                    // Add class for styling
                    svgContent = SvgOpenTagPattern.Replace(svgContent, "$1class=\"inlined-svg\" ");

                    // Add data-original-src for reference
                    svgContent = SvgOpenTagPattern.Replace(svgContent, $"$1data-original-src=\"{svgPath}\" ");

                    // Ensure responsive sizing
                    if (!svgContent.Contains("style="))
                    {
                        svgContent = SvgOpenTagPattern.Replace(svgContent, "$1style=\"max-width:100%;height:auto\" ");
                    }

                    return svgContent;
                }
                catch
                {
                    return match.Value; // Keep original on error
                }
            });
        }


    }
}
