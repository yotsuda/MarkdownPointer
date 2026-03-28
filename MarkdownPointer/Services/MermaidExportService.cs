using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;

namespace MarkdownPointer.Services
{
    public class MermaidExportService
    {
        private static readonly Regex MermaidBlockPattern = new(
            @"```mermaid\s*\r?\n[\s\S]*?\r?\n```",
            RegexOptions.Compiled);

        private static readonly Regex SvgImagePattern = new(
            @"!\[(?<alt>[^\]]*)\]\((?:<(?<src>[^>]+\.svg)>|(?<src>[^\s\)""]+\.svg))(?:\s+""[^""]*"")?\)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // docx text area is ~15cm ≈ 570px at 96dpi
        private const int PageWidthPx = 570;

        /// <summary>
        /// Captures all Mermaid diagrams from the WebView2 as PNG files.
        /// Returns (filePath, svgWidth) for each diagram.
        /// </summary>
        public async Task<List<(string Path, int Width)>> CaptureAllMermaidPngsAsync(WebView2 webView, string tempDir)
        {
            var result = new List<(string, int)>();
            if (webView?.CoreWebView2 == null) return result;

            var countResult = await webView.CoreWebView2.ExecuteScriptAsync(
                "document.querySelectorAll('.mermaid').length");
            if (!int.TryParse(countResult, out var count) || count == 0)
                return result;

            for (int i = 0; i < count; i++)
            {
                var pngPath = Path.Combine(tempDir, $"mermaid_{i}.png");
                var (captured, width) = await CaptureMermaidByIndexAsync(webView, i, pngPath);
                result.Add(captured ? (pngPath, width) : ("", 0));
                // Yield to UI thread so spinner can animate
                await Dispatcher.Yield(DispatcherPriority.Background);
            }

            return result;
        }

        private async Task<(bool Captured, int Width)> CaptureMermaidByIndexAsync(WebView2 webView, int index, string outputPath)
        {
            var tcs = new TaskCompletionSource<string>();
            var prefix = $"MERMAID_EXPORT:{index}:";

            void handler(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
            {
                var msg = e.TryGetWebMessageAsString();
                if (msg != null && msg.StartsWith(prefix))
                {
                    tcs.TrySetResult(msg.Substring(prefix.Length));
                }
            }

            webView.CoreWebView2.WebMessageReceived += handler;
            try
            {
                await webView.CoreWebView2.ExecuteScriptAsync(BuildCaptureScript(index));

                var timeoutTask = Task.Delay(10000);
                var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);
                if (completedTask == timeoutTask) return (false, 0);

                var message = await tcs.Task;
                // Message format: "svgWidth|data:image/png;base64,..."
                var pipeIdx = message.IndexOf('|');
                if (pipeIdx < 0) return (false, 0);

                var widthStr = message.Substring(0, pipeIdx);
                var dataUrl = message.Substring(pipeIdx + 1);

                if (!int.TryParse(widthStr, out var svgWidth)) svgWidth = 0;
                if (string.IsNullOrEmpty(dataUrl) || !dataUrl.StartsWith("data:image/png;base64,"))
                    return (false, 0);

                var base64 = dataUrl.Substring("data:image/png;base64,".Length);
                await Task.Run(async () =>
                {
                    var bytes = Convert.FromBase64String(base64);
                    await File.WriteAllBytesAsync(outputPath, bytes);
                });
                return (true, svgWidth);
            }
            finally
            {
                webView.CoreWebView2.WebMessageReceived -= handler;
            }
        }

        private static string BuildCaptureScript(int index)
        {
            return $@"
                (async function() {{
                    var divs = document.querySelectorAll('.mermaid');
                    var elem = divs[{index}];
                    if (!elem) {{ window.chrome.webview.postMessage('MERMAID_EXPORT:{index}:0|'); return; }}

                    var svg = elem.querySelector('svg');
                    if (!svg) {{ window.chrome.webview.postMessage('MERMAID_EXPORT:{index}:0|'); return; }}

                    var vb = svg.getAttribute('viewBox');
                    var width, height;
                    if (vb) {{
                        var parts = vb.split(/[\s,]+/);
                        width = Math.ceil(parseFloat(parts[2]));
                        height = Math.ceil(parseFloat(parts[3]));
                    }} else {{
                        var bbox = svg.getBoundingClientRect();
                        width = Math.ceil(bbox.width);
                        height = Math.ceil(bbox.height);
                    }}

                    var clonedSvg = svg.cloneNode(true);
                    clonedSvg.style.width = '';
                    clonedSvg.style.height = '';
                    clonedSvg.style.minWidth = '';
                    clonedSvg.style.maxWidth = '';
                    clonedSvg.setAttribute('width', width);
                    clonedSvg.setAttribute('height', height);
                    clonedSvg.setAttribute('xmlns', 'http://www.w3.org/2000/svg');

                    var css = '';
                    for (var si = 0; si < document.styleSheets.length; si++) {{
                        try {{ var rules = document.styleSheets[si].cssRules; for (var ri = 0; ri < rules.length; ri++) css += rules[ri].cssText + ' '; }} catch(e) {{}}
                    }}
                    if (css) {{
                        var styleEl = document.createElementNS('http://www.w3.org/2000/svg', 'style');
                        styleEl.textContent = css;
                        clonedSvg.insertBefore(styleEl, clonedSvg.firstChild);
                    }}

                    var serializer = new XMLSerializer();
                    var svgStr = serializer.serializeToString(clonedSvg);
                    var svgBase64 = btoa(unescape(encodeURIComponent(svgStr)));
                    var dataUrl = 'data:image/svg+xml;base64,' + svgBase64;

                    var canvas = document.createElement('canvas');
                    canvas.width = width * 2;
                    canvas.height = height * 2;
                    var ctx = canvas.getContext('2d');
                    ctx.scale(2, 2);
                    ctx.fillStyle = 'white';
                    ctx.fillRect(0, 0, width, height);

                    var img = new Image();
                    img.onload = function() {{
                        ctx.drawImage(img, 0, 0);
                        window.chrome.webview.postMessage('MERMAID_EXPORT:{index}:' + width + '|' + canvas.toDataURL('image/png'));
                    }};
                    img.onerror = function() {{
                        window.chrome.webview.postMessage('MERMAID_EXPORT:{index}:0|error');
                    }};
                    img.src = dataUrl;
                }})()";
        }

        /// <summary>
        /// Replaces mermaid code blocks with image references.
        /// Adds {width=100%} only when the SVG width exceeds the docx page width.
        /// </summary>
        public static string ReplaceMermaidBlocksWithImages(string markdown, IReadOnlyList<(string Path, int Width)> pngs)
        {
            int i = 0;
            return MermaidBlockPattern.Replace(markdown, match =>
            {
                if (i < pngs.Count && !string.IsNullOrEmpty(pngs[i].Path))
                {
                    var path = pngs[i].Path.Replace("\\", "/");
                    string widthAttr;
                    if (pngs[i].Width > PageWidthPx)
                    {
                        widthAttr = "{width=100%}";
                    }
                    else
                    {
                        // Convert SVG px to cm (96 DPI) so 2x PNG displays at correct size
                        var cm = pngs[i].Width / 96.0 * 2.54;
                        widthAttr = $"{{width={cm.ToString("F1", CultureInfo.InvariantCulture)}cm}}";
                    }
                    i++;
                    return $"![]({path}){widthAttr}";
                }
                i++;
                return match.Value;
            });
        }

        #region SVG Image Export

        public static bool ContainsSvgImages(string markdown)
        {
            return SvgImagePattern.IsMatch(markdown);
        }

        /// <summary>
        /// Captures all inline SVG images from the WebView2 as PNG files.
        /// These are SVG files referenced in markdown that were inlined by HtmlGenerator.
        /// </summary>
        public async Task<List<(string OriginalSrc, string Path, int Width)>> CaptureAllInlineSvgPngsAsync(WebView2 webView, string tempDir)
        {
            var result = new List<(string, string, int)>();
            if (webView?.CoreWebView2 == null) return result;

            var countResult = await webView.CoreWebView2.ExecuteScriptAsync(
                "document.querySelectorAll('svg.inlined-svg').length");
            if (!int.TryParse(countResult, out var count) || count == 0)
                return result;

            for (int i = 0; i < count; i++)
            {
                var pngPath = Path.Combine(tempDir, $"svg_{i}.png");
                var (captured, originalSrc, width) = await CaptureSvgByIndexAsync(webView, i, pngPath);
                if (captured)
                    result.Add((originalSrc, pngPath, width));
                await Dispatcher.Yield(DispatcherPriority.Background);
            }

            return result;
        }

        private async Task<(bool Captured, string OriginalSrc, int Width)> CaptureSvgByIndexAsync(WebView2 webView, int index, string outputPath)
        {
            var tcs = new TaskCompletionSource<string>();
            var prefix = $"SVG_EXPORT:{index}:";

            void handler(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
            {
                var msg = e.TryGetWebMessageAsString();
                if (msg != null && msg.StartsWith(prefix))
                {
                    tcs.TrySetResult(msg.Substring(prefix.Length));
                }
            }

            webView.CoreWebView2.WebMessageReceived += handler;
            try
            {
                await webView.CoreWebView2.ExecuteScriptAsync(BuildSvgCaptureScript(index));

                var timeoutTask = Task.Delay(10000);
                var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);
                if (completedTask == timeoutTask) return (false, "", 0);

                var message = await tcs.Task;
                // Message format: "originalSrc|svgWidth|data:image/png;base64,..."
                var firstPipe = message.IndexOf('|');
                if (firstPipe < 0) return (false, "", 0);
                var secondPipe = message.IndexOf('|', firstPipe + 1);
                if (secondPipe < 0) return (false, "", 0);

                var originalSrc = message.Substring(0, firstPipe);
                var widthStr = message.Substring(firstPipe + 1, secondPipe - firstPipe - 1);
                var dataUrl = message.Substring(secondPipe + 1);

                if (!int.TryParse(widthStr, out var svgWidth) || svgWidth <= 0) return (false, originalSrc, 0);
                if (string.IsNullOrEmpty(dataUrl) || !dataUrl.StartsWith("data:image/png;base64,"))
                    return (false, originalSrc, 0);

                var base64 = dataUrl.Substring("data:image/png;base64,".Length);
                await Task.Run(async () =>
                {
                    var bytes = Convert.FromBase64String(base64);
                    await File.WriteAllBytesAsync(outputPath, bytes);
                });
                return (true, originalSrc, svgWidth);
            }
            finally
            {
                webView.CoreWebView2.WebMessageReceived -= handler;
            }
        }

        private static string BuildSvgCaptureScript(int index)
        {
            return $@"
                (async function() {{
                    var svgs = document.querySelectorAll('svg.inlined-svg');
                    var svg = svgs[{index}];
                    if (!svg) {{ window.chrome.webview.postMessage('SVG_EXPORT:{index}:|0|'); return; }}

                    var originalSrc = svg.getAttribute('data-original-src') || '';

                    var width = parseFloat(svg.getAttribute('width')) || 0;
                    var height = parseFloat(svg.getAttribute('height')) || 0;
                    if (!width || !height) {{
                        var vb = svg.getAttribute('viewBox');
                        if (vb) {{
                            var parts = vb.split(/[\s,]+/);
                            width = Math.ceil(parseFloat(parts[2]));
                            height = Math.ceil(parseFloat(parts[3]));
                        }}
                    }}
                    if (!width || !height) {{
                        width = svg.getBoundingClientRect().width;
                        height = svg.getBoundingClientRect().height;
                    }}
                    width = Math.ceil(width);
                    height = Math.ceil(height);

                    if (width <= 0 || height <= 0) {{
                        window.chrome.webview.postMessage('SVG_EXPORT:{index}:' + originalSrc + '|0|');
                        return;
                    }}

                    var clonedSvg = svg.cloneNode(true);
                    clonedSvg.removeAttribute('class');
                    clonedSvg.removeAttribute('data-original-src');
                    clonedSvg.removeAttribute('aria-label');
                    clonedSvg.style.width = '';
                    clonedSvg.style.height = '';
                    clonedSvg.style.maxWidth = '';
                    clonedSvg.setAttribute('width', width);
                    clonedSvg.setAttribute('height', height);
                    clonedSvg.setAttribute('xmlns', 'http://www.w3.org/2000/svg');

                    var serializer = new XMLSerializer();
                    var svgStr = serializer.serializeToString(clonedSvg);
                    var svgBase64 = btoa(unescape(encodeURIComponent(svgStr)));
                    var dataUrl = 'data:image/svg+xml;base64,' + svgBase64;

                    var canvas = document.createElement('canvas');
                    canvas.width = width * 2;
                    canvas.height = height * 2;
                    var ctx = canvas.getContext('2d');
                    ctx.scale(2, 2);
                    ctx.fillStyle = 'white';
                    ctx.fillRect(0, 0, width, height);

                    var img = new Image();
                    img.onload = function() {{
                        ctx.drawImage(img, 0, 0);
                        window.chrome.webview.postMessage('SVG_EXPORT:{index}:' + originalSrc + '|' + width + '|' + canvas.toDataURL('image/png'));
                    }};
                    img.onerror = function() {{
                        window.chrome.webview.postMessage('SVG_EXPORT:{index}:' + originalSrc + '|0|error');
                    }};
                    img.src = dataUrl;
                }})()";
        }

        /// <summary>
        /// Replaces SVG image references in markdown with PNG image references.
        /// </summary>
        public static string ReplaceSvgImagesWithPngs(string markdown, IReadOnlyList<(string OriginalSrc, string Path, int Width)> pngs)
        {
            if (pngs.Count == 0) return markdown;

            var lookup = new Dictionary<string, (string Path, int Width)>(StringComparer.OrdinalIgnoreCase);
            foreach (var (src, path, width) in pngs)
            {
                if (!string.IsNullOrEmpty(src) && !string.IsNullOrEmpty(path))
                    lookup[src] = (path, width);
            }

            return SvgImagePattern.Replace(markdown, match =>
            {
                var alt = match.Groups["alt"].Value;
                var svgSrc = match.Groups["src"].Value;

                if (lookup.TryGetValue(svgSrc, out var png))
                {
                    var pngPath = png.Path.Replace("\\", "/");
                    string widthAttr;
                    if (png.Width > PageWidthPx)
                    {
                        widthAttr = "{width=100%}";
                    }
                    else
                    {
                        var cm = png.Width / 96.0 * 2.54;
                        widthAttr = $"{{width={cm.ToString("F1", CultureInfo.InvariantCulture)}cm}}";
                    }
                    return $"![{alt}]({pngPath}){widthAttr}";
                }

                return match.Value;
            });
        }

        #endregion
    }
}
