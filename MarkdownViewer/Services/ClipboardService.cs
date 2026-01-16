using System;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Wpf;

namespace MarkdownViewer.Services
{
    /// <summary>
    /// Handles clipboard operations for copying diagrams and math as images.
    /// </summary>
    public class ClipboardService
    {
        private readonly Action<string> _setStatusText;

        public ClipboardService(Action<string> setStatusText)
        {
            _setStatusText = setStatusText;
        }

        /// <summary>
        /// Copies a Mermaid diagram as SVG at the specified position.
        /// </summary>
        public async Task CopyMermaidSvgAsync(WebView2 webView, Point position)
        {
            var script = $@"
                (function() {{
                    var x = {position.X};
                    var y = {position.Y};
                    var element = document.elementFromPoint(x, y);
                    var mermaidDiv = element ? element.closest('.mermaid') : null;
                    if (!mermaidDiv) return '';
                    var svg = mermaidDiv.querySelector('svg');
                    if (!svg) return '';
                    var serializer = new XMLSerializer();
                    return serializer.serializeToString(svg);
                }})()";
            
            var result = await webView.CoreWebView2.ExecuteScriptAsync(script);
            
            // Remove surrounding quotes and unescape
            if (result.StartsWith("\"") && result.EndsWith("\""))
            {
                result = result.Substring(1, result.Length - 2);
                result = System.Text.RegularExpressions.Regex.Unescape(result);
            }
            
            if (!string.IsNullOrEmpty(result) && result.Contains("<svg"))
            {
                Clipboard.SetText(result);
                _setStatusText("✓ SVG copied");
            }
            else
            {
                _setStatusText("No diagram found");
            }
        }

        /// <summary>
        /// Copies a Mermaid diagram as PNG at the specified position.
        /// </summary>
        public async Task CopyMermaidPngAsync(WebView2 webView, Point position)
        {
            var tcs = new TaskCompletionSource<string>();
            
            void handler(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
            {
                var msg = e.TryGetWebMessageAsString();
                if (msg != null && msg.StartsWith("PNG:"))
                {
                    webView.CoreWebView2.WebMessageReceived -= handler;
                    tcs.TrySetResult(msg.Substring(4));
                }
            }
            
            webView.CoreWebView2.WebMessageReceived += handler;
            
            var script = $@"
                (async function() {{
                    var x = {position.X};
                    var y = {position.Y};
                    var element = document.elementFromPoint(x, y);
                    var mermaidDiv = element ? element.closest('.mermaid') : null;
                    if (!mermaidDiv) {{ window.chrome.webview.postMessage('PNG:'); return; }}
                    
                    var svg = mermaidDiv.querySelector('svg');
                    if (!svg) {{ window.chrome.webview.postMessage('PNG:'); return; }}
                    
                    var bbox = svg.getBoundingClientRect();
                    var width = Math.ceil(bbox.width);
                    var height = Math.ceil(bbox.height);
                    
                    var clonedSvg = svg.cloneNode(true);
                    clonedSvg.setAttribute('width', width);
                    clonedSvg.setAttribute('height', height);
                    
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
                        window.chrome.webview.postMessage('PNG:' + canvas.toDataURL('image/png'));
                    }};
                    img.onerror = function() {{
                        window.chrome.webview.postMessage('PNG:error');
                    }};
                    img.src = dataUrl;
                }})()";
            
            await webView.CoreWebView2.ExecuteScriptAsync(script);
            
            var timeoutTask = Task.Delay(5000);
            var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);
            
            if (completedTask == timeoutTask)
            {
                webView.CoreWebView2.WebMessageReceived -= handler;
                _setStatusText("✗ PNG copy timeout");
                return;
            }
            
            var result = await tcs.Task;
            await SetImageFromDataUrlAsync(result, "PNG");
        }

        /// <summary>
        /// Copies a KaTeX math element as PNG at the specified position.
        /// </summary>
        public async Task CopyMathPngAsync(WebView2 webView, Point position)
        {
            var tcs = new TaskCompletionSource<string>();
            
            void handler(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
            {
                var msg = e.TryGetWebMessageAsString();
                if (msg != null && msg.StartsWith("MATHPNG:"))
                {
                    webView.CoreWebView2.WebMessageReceived -= handler;
                    tcs.TrySetResult(msg.Substring(8));
                }
            }
            
            webView.CoreWebView2.WebMessageReceived += handler;
            
            var script = $@"
                (async function() {{
                    var x = {position.X};
                    var y = {position.Y};
                    var element = document.elementFromPoint(x, y);
                    var mathEl = element ? element.closest('.katex') : null;
                    if (!mathEl) {{
                        var mathContainer = element ? element.closest('.math') : null;
                        if (mathContainer) {{
                            mathEl = mathContainer.querySelector('.katex');
                        }}
                    }}
                    if (!mathEl) {{ window.chrome.webview.postMessage('MATHPNG:'); return; }}
                    
                    try {{
                        var rect = mathEl.getBoundingClientRect();
                        var canvas = await html2canvas(mathEl, {{
                            backgroundColor: '#ffffff',
                            scale: 3,
                            logging: false,
                            y: -10,
                            height: rect.height + 20,
                            windowHeight: rect.height + 20
                        }});
                        
                        // Auto-trim white borders
                        var ctx = canvas.getContext('2d');
                        var imageData = ctx.getImageData(0, 0, canvas.width, canvas.height);
                        var data = imageData.data;
                        var w = canvas.width, h = canvas.height;
                        
                        var top = h, left = w, right = 0, bottom = 0;
                        for (var y = 0; y < h; y++) {{
                            for (var x = 0; x < w; x++) {{
                                var idx = (y * w + x) * 4;
                                if (data[idx] < 250 || data[idx+1] < 250 || data[idx+2] < 250) {{
                                    if (x < left) left = x;
                                    if (x > right) right = x;
                                    if (y < top) top = y;
                                    if (y > bottom) bottom = y;
                                }}
                            }}
                        }}
                        
                        var padding = 15;
                        left = Math.max(0, left - padding);
                        top = Math.max(0, top - padding);
                        right = Math.min(w - 1, right + padding);
                        bottom = Math.min(h - 1, bottom + padding);
                        
                        var trimmedWidth = right - left + 1;
                        var trimmedHeight = bottom - top + 1;
                        
                        var trimmedCanvas = document.createElement('canvas');
                        trimmedCanvas.width = trimmedWidth;
                        trimmedCanvas.height = trimmedHeight;
                        var trimmedCtx = trimmedCanvas.getContext('2d');
                        trimmedCtx.drawImage(canvas, left, top, trimmedWidth, trimmedHeight, 0, 0, trimmedWidth, trimmedHeight);
                        
                        window.chrome.webview.postMessage('MATHPNG:' + trimmedCanvas.toDataURL('image/png'));
                    }} catch(e) {{
                        window.chrome.webview.postMessage('MATHPNG:error');
                    }}
                }})()";
            
            await webView.CoreWebView2.ExecuteScriptAsync(script);
            
            var timeoutTask = Task.Delay(5000);
            var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);
            
            if (completedTask == timeoutTask)
            {
                webView.CoreWebView2.WebMessageReceived -= handler;
                _setStatusText("✗ PNG copy timeout");
                return;
            }
            
            var result = await tcs.Task;
            await SetImageFromDataUrlAsync(result, "Math PNG");
        }

        /// <summary>
        /// Copies an element (Mermaid or Math) as PNG based on element type.
        /// </summary>
        public async Task CopyElementAsPngAsync(WebView2 webView, Point position, string elementType)
        {
            if (elementType == "mermaid")
            {
                await CopyMermaidPngAsync(webView, position);
            }
            else if (elementType == "math")
            {
                await CopyMathPngAsync(webView, position);
            }
        }

        private Task SetImageFromDataUrlAsync(string dataUrl, string imageType)
        {
            if (!string.IsNullOrEmpty(dataUrl) && dataUrl.StartsWith("data:image/png;base64,"))
            {
                try
                {
                    var base64 = dataUrl.Substring("data:image/png;base64,".Length);
                    var bytes = Convert.FromBase64String(base64);
                    using var stream = new System.IO.MemoryStream(bytes);
                    var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    
                    Clipboard.SetImage(bitmap);
                    _setStatusText($"✓ {imageType} copied");
                }
                catch
                {
                    _setStatusText($"✗ {imageType} copy failed");
                }
            }
            else
            {
                _setStatusText($"No {imageType.ToLower()} found");
            }
            
            return Task.CompletedTask;
        }
    }
}