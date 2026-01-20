using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;
using MarkdownViewer.Models;

namespace MarkdownViewer
{
    // Tab management partial class
    public partial class MainWindow
    {
        #region Public API for Pipe Commands

        public List<TabItemData> GetTabs()
        {
            return _tabs.ToList();
        }

        public int GetSelectedTabIndex()
        {
            return FileTabControl.SelectedIndex;
        }

        public void ScrollToLine(int tabIndex, int line)
        {
            if (tabIndex >= 0 && tabIndex < _tabs.Count)
            {
                var tab = _tabs[tabIndex];
                FileTabControl.SelectedItem = tab;
                ScrollToLine(tab, line);
            }
        }

        public void ScrollToLine(TabItemData tab, int line)
        {
            if (tab.IsInitialized && tab.WebView.CoreWebView2 != null)
            {
                // Use setTimeout to ensure page is fully rendered
                tab.WebView.CoreWebView2.ExecuteScriptAsync($"setTimeout(function() {{ scrollToLine({line}); }}, 100)");
            }
        }

        public TabItemData? FindTabByFilePath(string filePath)
        {
            var normalizedPath = Path.GetFullPath(filePath);
            return _tabs.FirstOrDefault(t =>
                !string.IsNullOrEmpty(t.FilePath) &&
                string.Equals(Path.GetFullPath(t.FilePath), normalizedPath, StringComparison.OrdinalIgnoreCase));
        }

        public void SelectTab(TabItemData tab)
        {
            FileTabControl.SelectedItem = tab;
        }

        public void RefreshTab(TabItemData tab)
        {
            if (tab.IsInitialized)
            {
                RenderMarkdown(tab);
                StatusText.Text = $"✓ {DateTime.Now:HH:mm:ss}";
            }
        }

        #endregion

        #region Tab Lifecycle

        public TabItemData? LoadMarkdownFile(string filePath, int? line = null, string? title = null, bool isTemp = false, string? renderedHtml = null)
        {
            if (!File.Exists(filePath))
            {
                MessageBox.Show($"File not found: {filePath}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return null;
            }

            // For temp files, always create a new tab (or reuse existing temp tab with same title)
            if (!isTemp)
            {
                // Check if file is already open in this window
                foreach (var tab in _tabs)
                {
                    if (string.Equals(tab.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                    {
                        FileTabControl.SelectedItem = tab;
                        if (line.HasValue)
                        {
                            ScrollToLine(tab, line.Value);
                        }
                        // Return existing tab (errors are cached from last render)
                        tab.RenderCompletion = null;
                        return tab;
                    }
                }

                // Close the file if it's open in another window
                foreach (Window window in Application.Current.Windows)
                {
                    if (window is MainWindow mw && mw != this)
                    {
                        var tabToClose = mw._tabs.FirstOrDefault(t =>
                            string.Equals(t.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
                        if (tabToClose != null)
                        {
                            mw.CloseTab(tabToClose);
                            break;
                        }
                    }
                }
            }
            else
            {
                // For temp files, reuse existing tab with same title if exists
                var displayTitle = title ?? Path.GetFileName(filePath);
                foreach (var tab in _tabs)
                {
                    if (tab.IsTemp && tab.Title == displayTitle)
                    {
                        // Update existing temp tab
                        tab.FilePath = filePath;
                        tab.LastFileWriteTime = File.GetLastWriteTime(filePath);
                        FileTabControl.SelectedItem = tab;
                        SetupFileWatcher(tab);  // Re-setup watcher for new file
                        // Re-render to collect errors
                        tab.RenderCompletion = new TaskCompletionSource<List<string>>();
                        RefreshTab(tab);
                        if (line.HasValue)
                        {
                            ScrollToLine(tab, line.Value);
                        }
                        return tab;
                    }
                }
            }

            // Create new tab
            var newTab = new TabItemData
            {
                FilePath = filePath,
                Title = title ?? Path.GetFileName(filePath),
                WebView = new WebView2(),
                IsTemp = isTemp,
                RenderCompletion = new TaskCompletionSource<List<string>>(),
                RenderedHtml = renderedHtml  // Use cached HTML if available
            };

            // Initialize WebView2 (fire-and-forget, exceptions handled internally)
            _ = InitializeTabWebViewAsync(newTab).ContinueWith(t =>
            {
                if (t.IsFaulted && t.Exception != null)
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        MessageBox.Show($"Failed to initialize WebView2:\n{t.Exception.InnerException?.Message ?? t.Exception.Message}",
                            "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        CloseTab(newTab);
                    });
                }
            }, TaskScheduler.Default);

            _tabs.Add(newTab);
            FileTabControl.SelectedItem = newTab;

            // Hide placeholder, show tab control
            PlaceholderPanel.Visibility = Visibility.Collapsed;
            FileTabControl.Visibility = Visibility.Visible;

            UpdateWindowTitle();
            return newTab;
        }

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

        #region Zoom

        private void SetupZoomForTab(TabItemData tab)
        {
            tab.WebView.ZoomFactorChanged += (s, e) =>
            {
                if (FileTabControl.SelectedItem == tab)
                {
                    var currentZoom = tab.WebView.ZoomFactor;
                    if (Math.Abs(currentZoom - _lastZoomFactor) > 0.001)
                    {
                        _lastZoomFactor = currentZoom;
                        AdjustWindowSizeForZoom(currentZoom);
                    }
                }
            };

            // Animation timer (shared across window)
            if (_zoomAnimationTimer == null)
            {
                _zoomAnimationTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(16)
                };

                _zoomAnimationTimer.Tick += (s, e) =>
                {
                    if (FileTabControl.SelectedItem is TabItemData currentTab)
                    {
                        var currentZoom = currentTab.WebView.ZoomFactor;
                        var diff = _targetZoomFactor - currentZoom;

                        if (Math.Abs(diff) < 0.005)
                        {
                            currentTab.WebView.ZoomFactor = _targetZoomFactor;
                            _zoomAnimationTimer.Stop();
                            AdjustWindowSizeForZoom(_targetZoomFactor);
                            return;
                        }

                        var step = diff * 0.1;
                        var newZoom = currentZoom + step;
                        currentTab.WebView.ZoomFactor = newZoom;

                        // Call directly in drag move mode as ZoomFactorChanged does not fire
                        if (_isDragMoveMode)
                        {
                            AdjustWindowSizeForZoom(newZoom);
                        }
                    }
                };
            }

            // Mouse wheel handling
            tab.WebView.PreviewMouseWheel += (s, e) =>
            {
                if (Keyboard.Modifiers == ModifierKeys.Control)
                {
                    e.Handled = true;
                    ApplyZoomDelta(e.Delta);
                }
            };
        }

        private void AdjustWindowSizeForZoom(double zoomFactor)
        {
            // Window size is now fixed - zoom only affects content
        }

        private void ApplyZoomDelta(int delta)
        {
            if (delta > 0)
            {
                _targetZoomFactor = Math.Min(MaxZoom, _targetZoomFactor + ZoomStep);
            }
            else
            {
                _targetZoomFactor = Math.Max(MinZoom, _targetZoomFactor - ZoomStep);
            }

            // Apply zoom immediately without animation
            if (FileTabControl.SelectedItem is TabItemData tab)
            {
                tab.WebView.ZoomFactor = _targetZoomFactor;
                _lastZoomFactor = _targetZoomFactor;
            }
        }

        #endregion

        #region File Watcher

        private void SetupFileWatcher(TabItemData tab)
        {
            tab.Watcher?.Dispose();

            var directory = Path.GetDirectoryName(tab.FilePath);
            var fileName = Path.GetFileName(tab.FilePath);

            // Skip watching if directory cannot be obtained
            if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(fileName))
            {
                return;
            }

            tab.Watcher = new FileSystemWatcher(directory)
            {
                Filter = fileName,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
            };

            // Debounce timer
            tab.DebounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(200)
            };

            tab.DebounceTimer.Tick += (s, e) =>
            {
                tab.DebounceTimer.Stop();
                // Check if tab still exists
                if (!_tabs.Contains(tab)) return;

                // Skip if file timestamp hasn't changed
                var currentWriteTime = File.GetLastWriteTime(tab.FilePath);
                if (currentWriteTime == tab.LastFileWriteTime) return;
                tab.LastFileWriteTime = currentWriteTime;

                RenderMarkdown(tab);
                StatusText.Text = $"✓ {currentWriteTime:HH:mm:ss}";
            };

            tab.Watcher.Changed += (s, e) =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    // Check if tab still exists
                    if (!_tabs.Contains(tab)) return;

                    tab.DebounceTimer?.Stop();
                    tab.DebounceTimer?.Start();
                    if (FileTabControl.SelectedItem == tab)
                    {
                        StatusText.Text = "⟳";
                    }
                });
            };

            tab.Watcher.Deleted += (s, e) =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    // Check if tab still exists
                    if (!_tabs.Contains(tab)) return;

                    CloseTab(tab);
                });
            };

            tab.Watcher.Renamed += (s, e) =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    // Check if tab still exists
                    if (!_tabs.Contains(tab)) return;

                    tab.FilePath = e.FullPath;
                    tab.Title = e.Name ?? Path.GetFileName(e.FullPath);

                    if (tab.Watcher != null && e.Name != null)
                    {
                        tab.Watcher.Filter = e.Name;
                    }

                    UpdateWindowTitle();
                    if (FileTabControl.SelectedItem == tab)
                    {
                        StatusText.Text = $"✓ {DateTime.Now:HH:mm:ss}";
                    }
                });
            };

            tab.Watcher.EnableRaisingEvents = true;
        }

        #endregion

        #region Tab Operations

        private void CloseTab(TabItemData tab)
        {
            tab.Dispose();
            _tabs.Remove(tab);

            if (_tabs.Count == 0)
            {
                // Check if there are other MarkdownViewer windows
                var otherWindows = Application.Current.Windows
                    .OfType<MainWindow>()
                    .Where(w => w != this)
                    .ToList();

                if (otherWindows.Count > 0)
                {
                    // Other windows exist - close this window
                    Close();
                }
                else
                {
                    // This is the last window - show placeholder instead of closing
                    FileTabControl.Visibility = Visibility.Collapsed;
                    PlaceholderPanel.Visibility = Visibility.Visible;
                    Title = "Markdown Viewer";
                    LinkStatusText.Text = "";
                    WatchStatusText.Text = "";
                }
            }
            else
            {
                UpdateWindowTitle();
            }
        }

        private void UpdateWindowTitle()
        {
            if (FileTabControl.SelectedItem is TabItemData tab)
            {
                Title = $"Markdown Viewer - {tab.Title}";
                LinkStatusText.Text = "";
                WatchStatusText.Text = "👁 Watching";
            }
        }

        private void ShowStatusMessage(string message, double seconds = 3.0)
        {
            StatusText.Text = message;
            _statusMessageTimer?.Stop();
            _statusMessageTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            _statusMessageTimer.Tick += (s, args) => { StatusText.Text = ""; _statusMessageTimer.Stop(); };
            _statusMessageTimer.Start();
        }

        private async void RenderMarkdown(TabItemData tab)
        {
            try
            {
                // Read file asynchronously with retry for locked files
                string? markdown = null;
                for (int i = 0; i < 3; i++)
                {
                    try
                    {
                        markdown = await File.ReadAllTextAsync(tab.FilePath, Encoding.UTF8);
                        break;
                    }
                    catch (IOException) when (i < 2)
                    {
                        await Task.Delay(100);
                    }
                }

                if (markdown == null)
                {
                    markdown = await File.ReadAllTextAsync(tab.FilePath, Encoding.UTF8);
                }

                // Check if tab still exists after async operation
                if (!_tabs.Contains(tab)) return;

                var baseDir = Path.GetDirectoryName(tab.FilePath);
                var html = _htmlGenerator.ConvertToHtml(markdown, baseDir!);
                tab.RenderedHtml = html;  // Cache for fast window detach
                tab.WebView.NavigateToString(html);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Markdown rendering error: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion
    }
}
