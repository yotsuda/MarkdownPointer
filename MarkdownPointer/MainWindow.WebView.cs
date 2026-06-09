using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MarkdownPointer.Models;

namespace MarkdownPointer
{
    // WebView event handlers and initialization partial class
    public partial class MainWindow
    {
        #region WebView Initialization and Events

        private async Task InitializeTabWebViewAsync(TabItemData tab)
        {
            // Handle drops on WebView2
            tab.WebView.AllowDrop = true;
            tab.WebView.PreviewDragOver += WebView_PreviewDragOver;
            tab.WebView.PreviewDrop += WebView_PreviewDrop;

            // Wrap the WebView2 control in the platform-agnostic host. EnsureReadyAsync
            // initializes the engine, applies settings, and wires the core events
            // (MessageReceived / NavigationCompleted / NewWindowRequested).
            tab.Host = new Services.WebViewHosting.WebView2WebViewHost(tab.WebView);
            await tab.Host.EnsureReadyAsync();

            // Check if tab still exists
            if (!_tabs.Contains(tab)) return;

            // Custom context menu for Mermaid diagrams and math
            // (WebView2-specific; stays on the concrete control)
            tab.WebView.CoreWebView2.ContextMenuRequested += async (s, e) =>
                await HandleContextMenuRequestedAsync(tab, e);

            // Prevent dropped files from opening in new window; open supported files instead
            tab.Host.NewWindowRequested += HandleNewWindowRequested;

            // Handle messages from JavaScript (link clicks, hovers, pointing)
            tab.Host.MessageReceived += message => HandleWebMessageReceived(tab, message);

            // Apply pointing mode and drag mode state after navigation
            tab.Host.NavigationCompleted += () =>
            {
                if (tab.IsInitialized)
                {
                    // Switch from placeholder to tab control on first render
                    if (PlaceholderPanel.Visibility == Visibility.Visible)
                    {
                        PlaceholderPanel.Visibility = Visibility.Collapsed;
                        FileTabControl.Visibility = Visibility.Visible;
                        SlideViewToggle.IsEnabled = true;
                        SlideThemeDropdown.IsEnabled = true;
                    }

                    tab.Host.ExecuteScriptAsync($"setPointingMode({(_isPointingMode ? "true" : "false")})");

                    // Also update text selection style
                    var userSelect = _isPointingMode ? "none" : "";
                    tab.Host.ExecuteScriptAsync($"document.body.style.userSelect = '{userSelect}'");

                    // Restore saved scroll position (slide position is restored in HandleRenderComplete)
                    if (!tab.IsSlideView && tab.SavedScrollPosition > 0)
                    {
                        tab.Host.ExecuteScriptAsync($"window.scrollTo(0, {tab.SavedScrollPosition})");
                    }

                    // Restore CSS zoom
                    if (Math.Abs(tab.CssZoomFactor - 1.0) > 0.001)
                    {
                        ApplyCssZoom(tab, tab.CssZoomFactor);
                    }
                }
            };

            // Setup zoom
            SetupZoomForTab(tab);

            tab.IsInitialized = true;

            // Setup file watcher
            SetupFileWatcher(tab);

            // Render Markdown (or use cached HTML if available)
            tab.LastFileWriteTime = File.GetLastWriteTime(tab.FilePath);
            if (tab.RenderedHtml != null)
            {
                // Use cached HTML for fast display (e.g., when detaching tab to new window)
                NavigateToHtml(tab, tab.RenderedHtml);
            }
            else
            {
                RenderMarkdown(tab);
            }
        }

        private void WebView_PreviewDragOver(object sender, DragEventArgs e)
        {
            // Accept tab drops from other MarkdownPointer windows
            if (e.Data.GetDataPresent(DataFormats.Text))
            {
                var text = e.Data.GetData(DataFormats.Text) as string;
                if (!string.IsNullOrEmpty(text) && text.StartsWith("MDVIEWER:"))
                {
                    e.Effects = DragDropEffects.Move;
                    e.Handled = true;
                    return;
                }
            }

            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                e.Effects = files.Any(f => IsSupportedFile(f))
                    ? DragDropEffects.Copy
                    : DragDropEffects.None;
                e.Handled = true;
            }
        }

        private void WebView_PreviewDrop(object sender, DragEventArgs e)
        {
            // Handle tab drop from another MarkdownPointer window
            if (e.Data.GetDataPresent(DataFormats.Text))
            {
                var text = e.Data.GetData(DataFormats.Text) as string;
                if (!string.IsNullOrEmpty(text) && text.StartsWith("MDVIEWER:"))
                {
                    var filePath = text.Substring(9);
                    if (IsSupportedFile(filePath))
                    {
                        LoadMarkdownFile(filePath);
                        e.Effects = DragDropEffects.Move;
                        e.Handled = true;
                        return;
                    }
                }
            }

            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                foreach (var file in files)
                {
                    if (IsSupportedFile(file))
                    {
                        LoadMarkdownFile(file);
                    }
                }
                e.Handled = true;
            }
        }

        private void HandleNewWindowRequested(string requestedUri)
        {
            // The host already suppressed the new window; open supported local files in a tab.
            if (requestedUri.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                var uri = new Uri(requestedUri);
                var path = uri.LocalPath;
                if (IsSupportedFile(path))
                {
                    LoadMarkdownFile(path);
                }
            }
        }

        private async Task HandleContextMenuRequestedAsync(TabItemData tab, Microsoft.Web.WebView2.Core.CoreWebView2ContextMenuRequestedEventArgs e)
        {
            // Save context menu position
            _contextMenuPosition = new Point(e.Location.X, e.Location.Y);

            var deferral = e.GetDeferral();
            try
            {
                // Check what's at the click position using JavaScript
                var checkScript = $@"
                    (function() {{
                        var x = {e.Location.X};
                        var y = {e.Location.Y};
                        var element = document.elementFromPoint(x, y);
                        if (!element) return 'none';
                        if (element.closest('.mermaid')) return 'mermaid';
                        if (element.closest('.katex') || element.closest('.math')) return 'math';
                        var pre = element.closest('pre');
                        if (pre && pre.querySelector('code')) return 'code';
                        return 'none';
                    }})()";

                var result = await tab.WebView.CoreWebView2.ExecuteScriptAsync(checkScript);
                var elementType = result.Trim('"');

                // Suppress WebView2's context menu and show WPF ContextMenu instead
                e.MenuItems.Clear();
                e.Handled = true;

                if (elementType == "code")
                {
                    await CopyCodeBlockToClipboard(tab, e.Location.X, e.Location.Y);
                }
                else if (elementType == "mermaid" || elementType == "math")
                {
                    ShowDiagramContextMenu(tab, elementType);
                }
            }
            finally
            {
                deferral.Complete();
            }
        }

        private async Task CopyCodeBlockToClipboard(TabItemData tab, int x, int y)
        {
            var script = $@"
                (function() {{
                    var element = document.elementFromPoint({x}, {y});
                    if (!element) return '';
                    var pre = element.closest('pre');
                    if (pre && pre.querySelector('code')) {{
                        return pre.querySelector('code').textContent;
                    }}
                    return '';
                }})()";
            var result = await tab.WebView.CoreWebView2.ExecuteScriptAsync(script);
            // ExecuteScriptAsync returns JSON-encoded string, so deserialize it
            var codeText = JsonSerializer.Deserialize<string>(result) ?? "";
            if (!string.IsNullOrEmpty(codeText))
            {
                Clipboard.SetText(codeText);
                ShowStatusMessage("✓ Code copied to clipboard");
            }
        }

        private void ShowDiagramContextMenu(TabItemData tab, string elementType)
        {
            var contextMenu = new System.Windows.Controls.ContextMenu();

            if (elementType == "mermaid")
            {
                var savePngItem = new System.Windows.Controls.MenuItem { Header = "Save as Image..." };
                savePngItem.Click += async (s, args) => await _clipboardService.SaveMermaidPngAsync(tab.WebView, _contextMenuPosition);
                contextMenu.Items.Add(savePngItem);

                contextMenu.Items.Add(new System.Windows.Controls.Separator());
            }

            var copyPngItem = new System.Windows.Controls.MenuItem { Header = "Copy as Image" };
            copyPngItem.Click += async (s, args) => await _clipboardService.CopyElementAsPngAsync(tab.WebView, _contextMenuPosition, elementType);
            contextMenu.Items.Add(copyPngItem);

            if (elementType == "mermaid")
            {
                var copySvgItem = new System.Windows.Controls.MenuItem { Header = "Copy as SVG" };
                copySvgItem.Click += async (s, args) => await _clipboardService.CopyMermaidSvgAsync(tab.WebView, _contextMenuPosition);
                contextMenu.Items.Add(copySvgItem);
            }

            contextMenu.IsOpen = true;
        }

        private void HandleWebMessageReceived(TabItemData tab, string message)
        {
            // Redirect to the correct owner window if tab was moved
            if (tab.OwnerWindow is MainWindow owner && owner != this)
            {
                owner.HandleWebMessageReceived(tab, message);
                return;
            }

            if (string.IsNullOrEmpty(message))
            {
                return;
            }

            // Handle link hover
            if (message.StartsWith("hover:", StringComparison.Ordinal))
            {
                var url = message.Substring(6);
                // Remove file:// prefix for local files to show clean path
                if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var fileUri = new Uri(url);
                        url = Uri.UnescapeDataString(fileUri.LocalPath);
                    }
                    catch
                    {
                        // Keep original URL if parsing fails
                    }
                }
                LinkStatusText.Text = url;
                UpdateFilePathVisibility();
                return;
            }

            // Handle zoom from JavaScript
            if (message.StartsWith("zoom:", StringComparison.Ordinal))
            {
                var direction = message.Substring(5);
                ApplyZoomDelta(direction == "in" ? 120 : -120);
                return;
            }

            if (message == "leave:")
            {
                LinkStatusText.Text = "";
                UpdateFilePathVisibility();
                return;
            }

            // Handle link click
            if (message.StartsWith("click:", StringComparison.Ordinal))
            {
                HandleLinkClick(message.Substring(6));
                return;
            }

            // Handle pointing mode hover
            if (message.StartsWith("pointhover:", StringComparison.Ordinal))
            {
                var line = message.Substring(11);
                LineNumberText.Text = $"L{line}";
                return;
            }

            if (message == "pointleave:")
            {
                LineNumberText.Text = "";
                return;
            }

            // Handle pointing mode click
            if (message.StartsWith("point:", StringComparison.Ordinal))
            {
                HandlePointingModeClick(tab, message.Substring(6));
                return;
            }

            // Handle render completion notification
            if (message.StartsWith("render-complete:", StringComparison.Ordinal))
            {
                HandleRenderComplete(tab, message);
                return;
            }
        }

        private void HandleLinkClick(string uri)
        {
            // Open remote URLs (http/https) in browser
            if (uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
                return;
            }

            // Local file
            if (uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var fileUri = new Uri(uri);
                    var path = Uri.UnescapeDataString(fileUri.LocalPath);
                    if (!File.Exists(path))
                    {
                        ShowStatusMessage($"✗ File not found: {path}");
                        return;
                    }

                    if (IsSupportedFile(path))
                        LoadMarkdownFile(path);
                    else
                        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    ShowStatusMessage($"✗ Failed to open file: {ex.Message}");
                }
            }
        }

        private void HandlePointingModeClick(TabItemData tab, string data)
        {
            var parts = data.Split('|', 2);
            if (parts.Length >= 2)
            {
                var line = parts[0];
                var elementContent = parts[1];
                var reference = $"[{tab.FilePath}:{line}] {elementContent}";
                Clipboard.SetText(reference);
                ShowStatusMessage("✓ Ref copied - Paste into prompt to point AI here", 3.0);
            }
        }

        private void HandleRenderComplete(TabItemData tab, string message)
        {
            const string prefix = "render-complete:";
            var json = message.Substring(prefix.Length);
            try
            {
                var errors = System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
                tab.LastRenderErrors = errors;
                tab.RenderCompletion?.TrySetResult(errors);
            }
            catch (System.Text.Json.JsonException)
            {
                // Ignore malformed JSON - use empty error list
                tab.LastRenderErrors = new List<string>();
                tab.RenderCompletion?.TrySetResult(new List<string>());
            }

            // Restore position using saved source line (theme switch, view switch)
            if (tab.SavedSourceLine > 0)
            {
                var line = tab.SavedSourceLine;
                tab.SavedSourceLine = 0;
                if (tab.IsSlideView)
                {
                    // Find slide containing the source line and navigate by h/v indices
                    tab.Host.ExecuteScriptAsync(
                        $"(function(){{var slides=Reveal.getSlides();var best=null;" +
                        $"for(var i=0;i<slides.length;i++){{var dl=slides[i].querySelector('[data-line]');" +
                        $"var l=dl?parseInt(dl.getAttribute('data-line')):parseInt(slides[i].getAttribute('data-line')||'0');" +
                        $"if(l>0&&l<={line})best=slides[i]}}" +
                        $"if(best){{var idx=Reveal.getIndices(best);Reveal.slide(idx.h,idx.v)}}}})()");
                }
                else
                {
                    tab.Host.ExecuteScriptAsync($"scrollToLine({line})");
                }
            }

            // Stop spinner and show completion message
            ShowStatusMessage(tab.IsSlideView
                ? "🎞 Slide view"
                : "📄 Document view");

            // Update error indicator if this is the selected tab
            if (FileTabControl.SelectedItem == tab)
            {
                UpdateErrorIndicator(tab);
            }
        }

        /// <summary>
        /// Updates the error indicator in the status bar based on the tab's render errors.
        /// </summary>
        private void UpdateErrorIndicator(TabItemData? tab)
        {
            if (tab == null || tab.LastRenderErrors.Count == 0)
            {
                ErrorIndicator.Visibility = Visibility.Collapsed;
                return;
            }

            var errorCount = tab.LastRenderErrors.Count;
            ErrorIndicatorText.Text = $"⚠ {errorCount} error{(errorCount > 1 ? "s" : "")}";
            ErrorToolTipText.Text = string.Join(Environment.NewLine, tab.LastRenderErrors);
            ErrorIndicator.Visibility = Visibility.Visible;
        }

        #endregion
    }
}