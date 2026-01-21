using System.IO;
using System.Text;
using Markdig;
using MarkdownViewer.Resources;

namespace MarkdownViewer.Services
{
    /// <summary>
    /// Generates HTML content from Markdown source.
    /// </summary>
    public class HtmlGenerator
    {
        private readonly MarkdownPipeline _pipeline;

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

            // Convert path for file:// URL
            var baseUrl = new Uri(baseDir + Path.DirectorySeparatorChar).AbsoluteUri;

            // Generate nonce for CSP
            var nonce = Convert.ToBase64String(Guid.NewGuid().ToByteArray());

            var html = new StringBuilder();
            html.AppendLine("<!DOCTYPE html>");
            html.AppendLine("<html><head>");
            html.AppendLine("<meta charset='utf-8'/>");
            html.AppendLine($"<meta http-equiv='Content-Security-Policy' content=\"default-src 'none'; style-src 'unsafe-inline' https://cdn.jsdelivr.net; img-src file: data: blob:; script-src 'nonce-{nonce}' 'unsafe-eval' https://cdn.jsdelivr.net; font-src https://cdn.jsdelivr.net;\"/>");
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
            html.AppendLine("<script src='https://cdn.jsdelivr.net/npm/mermaid/dist/mermaid.min.js'></script>");
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


    }
}
