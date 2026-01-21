using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MarkdownViewer.Models;

namespace MarkdownViewer
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

            await tab.WebView.EnsureCoreWebView2Async(null);

            // Check if tab still exists
            if (!_tabs.Contains(tab)) return;

            // Configure WebView2 settings for security
            tab.WebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            tab.WebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            tab.WebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            tab.WebView.CoreWebView2.Settings.IsWebMessageEnabled = true;
            tab.WebView.CoreWebView2.Settings.AreHostObjectsAllowed = false;

            // Custom context menu for Mermaid diagrams and math
            tab.WebView.CoreWebView2.ContextMenuRequested += async (s, e) =>
                await HandleContextMenuRequestedAsync(tab, e);

            // Prevent dropped files from opening in new window
            tab.WebView.CoreWebView2.NewWindowRequested += CoreWebView2_NewWindowRequested;

            // Handle messages from JavaScript (link clicks and hovers)
            tab.WebView.CoreWebView2.WebMessageReceived += (s, e) =>
                HandleWebMessageReceived(tab, e);

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
                tab.WebView.NavigateToString(tab.RenderedHtml);
            }
            else
            {
                RenderMarkdown(tab);
            }
            StatusText.Text = $"✓ {tab.LastFileWriteTime:HH:mm:ss}";
        }

        private void WebView_PreviewDragOver(object sender, DragEventArgs e)
        {
            // Accept tab drops from other MarkdownViewer windows
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
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
            }
        }

        private void WebView_PreviewDrop(object sender, DragEventArgs e)
        {
            // Handle tab drop from another MarkdownViewer window
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

        private void CoreWebView2_NewWindowRequested(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2NewWindowRequestedEventArgs e)
        {
            e.Handled = true;
            if (e.Uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                var uri = new Uri(e.Uri);
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
                        return 'none';
                    }})()";

                var result = await tab.WebView.CoreWebView2.ExecuteScriptAsync(checkScript);
                var elementType = result.Trim('"');

                var menuItems = e.MenuItems;
                menuItems.Clear();

                if (elementType == "mermaid" || elementType == "math")
                {
                    var copyPngItem = tab.WebView.CoreWebView2.Environment.CreateContextMenuItem(
                        "Copy as Image", null,
                        Microsoft.Web.WebView2.Core.CoreWebView2ContextMenuItemKind.Command);
                    copyPngItem.CustomItemSelected += async (sender, args) =>
                    {
                        await _clipboardService.CopyElementAsPngAsync(tab.WebView, _contextMenuPosition, elementType);
                    };
                    menuItems.Add(copyPngItem);

                    if (elementType == "mermaid")
                    {
                        var copySvgItem = tab.WebView.CoreWebView2.Environment.CreateContextMenuItem(
                            "Copy as SVG", null,
                            Microsoft.Web.WebView2.Core.CoreWebView2ContextMenuItemKind.Command);
                        copySvgItem.CustomItemSelected += async (sender, args) =>
                        {
                            await _clipboardService.CopyMermaidSvgAsync(tab.WebView, _contextMenuPosition);
                        };
                        menuItems.Add(copySvgItem);
                    }
                }
            }
            finally
            {
                deferral.Complete();
            }
        }

        private void HandleWebMessageReceived(TabItemData tab, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
        {
            // Redirect to the correct owner window if tab was moved
            if (tab.OwnerWindow is MainWindow owner && owner != this)
            {
                owner.HandleWebMessageReceived(tab, e);
                return;
            }

            var message = e.TryGetWebMessageAsString();

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
                return;
            }

            // Handle link click
            if (message.StartsWith("click:", StringComparison.Ordinal))
            {
                HandleLinkClick(message.Substring(6));
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
                    if (IsSupportedFile(path))
                    {
                        // Open Markdown files in new tab
                        if (File.Exists(path))
                        {
                            LoadMarkdownFile(path);
                        }
                        else
                        {
                            MessageBox.Show($"File not found: {path}", "Error",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                        }
                    }
                    else
                    {
                        // Open other files with default app
                        if (File.Exists(path))
                        {
                            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                        }
                        else
                        {
                            MessageBox.Show($"File not found: {path}", "Error",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                        }
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to open file: {ex.Message}", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void HandlePointingModeClick(TabItemData tab, string data)
        {
            var parts = data.Split('|');
            if (parts.Length >= 2)
            {
                var line = parts[0];
                var elementContent = parts.Length > 1 ? parts[1] : "";
                var reference = $"[{tab.FilePath}:{line}] {elementContent}";
                Clipboard.SetText(reference);
                ShowStatusMessage("✓ Copied. Paste into prompt to point AI here.", 3.0);
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
            ErrorToolTipText.Text = string.Join(Environment.NewLine + Environment.NewLine, tab.LastRenderErrors);
            ErrorIndicator.Visibility = Visibility.Visible;
        }

        #endregion
    }
}