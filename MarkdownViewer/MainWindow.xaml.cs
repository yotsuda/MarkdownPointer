using System.Collections.ObjectModel;
using System.Linq;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Markdig;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;

namespace MarkdownViewer
{
    // Tab data model
    public class TabItemData : INotifyPropertyChanged
    {
        private string _title = "";
        public string Title
        {
            get => _title;
            set { _title = value; OnPropertyChanged(nameof(Title)); }
        }

        public string FilePath { get; set; } = "";
        public WebView2 WebView { get; set; } = null!;
        public FileSystemWatcher? Watcher { get; set; }
        public DispatcherTimer? DebounceTimer { get; set; }
        public bool IsInitialized { get; set; }
        public int? PendingScrollLine { get; set; }
        public DateTime LastFileWriteTime { get; set; }
        public bool IsTemp { get; set; }
        public TaskCompletionSource<List<string>>? RenderCompletion { get; set; }
        public List<string> LastRenderErrors { get; set; } = new();
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) => 
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public partial class MainWindow : Window
    {
        // P/Invoke for getting cursor position
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        // Get cursor position in WPF DIP coordinates
        // Convert physical pixels to WPF DIP coordinates
        private Point PhysicalToDip(Point physical)
        {
            var source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget != null)
            {
                var transform = source.CompositionTarget.TransformFromDevice;
                return new Point(physical.X * transform.M11, physical.Y * transform.M22);
            }
            return physical;
        }

        // Get cursor position in WPF DIP coordinates
        private Point GetCursorPosDip()
        {
            if (GetCursorPos(out POINT pt))
            {
                return PhysicalToDip(new Point(pt.X, pt.Y));
            }
            return new Point(0, 0);
        }

        // Find another MainWindow at the given screen position (excluding this window)
        private MainWindow? FindWindowAtPosition(Point screenPos)
        {
            foreach (Window window in Application.Current.Windows)
            {
                if (window is MainWindow mw && mw != this)
                {
                    var rect = new Rect(mw.Left, mw.Top, mw.Width, mw.Height);
                    if (rect.Contains(screenPos))
                    {
                        return mw;
                    }
                }
            }
            return null;
        }

        // Constants
        private const double ZoomStep = 0.01;
        private const double MinZoom = 0.25;
        private const double MaxZoom = 5.0;
        private const double BaseContentWidth = 1060.0;
        private const double ScrollbarWidth = 20.0;
        private const double MinWindowWidth = 400.0;

        private static readonly string[] SupportedExtensions = { ".md", ".markdown", ".txt" };

        private readonly MarkdownPipeline _pipeline;
        private readonly ObservableCollection<TabItemData> _tabs = new();
        private DispatcherTimer? _zoomAnimationTimer;
        private double _lastZoomFactor = 1.0;
        private double _targetZoomFactor = 1.0;
        private bool _isDragMoveMode = false;
        private bool _isPointingMode = false;
        private DispatcherTimer? _statusMessageTimer;
        
        // Context menu position for diagram copy
        private Point _contextMenuPosition;

        // Document scroll state
        private bool _isDocumentScrolling = false;
        private Point _scrollStartPoint;

        // Tab drag state
        private Point _tabDragStartPoint;
        private Point _dragStartCursorPos;
        private Point _dragStartWindowPos;
        private bool _isTabDragging = false;
        private TabItemData? _draggedTab = null;
        private Window? _dragPreviewWindow = null;

        private static bool IsSupportedFile(string filePath)
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            return Array.Exists(SupportedExtensions, e => e == ext);
        }

        public MainWindow()
        {
            InitializeComponent();

            // Configure Markdig pipeline
            // Note: UseDiagrams() is excluded because it causes NullReferenceException in Setup()
            _pipeline = new MarkdownPipelineBuilder()
                .UseAbbreviations()
                .UseAutoIdentifiers()
                .UseCitations()
                .UseCustomContainers()
                .UseDefinitionLists()
                .UseEmphasisExtras()
                .UseFigures()
                .UseFooters()
                .UseFootnotes()
                .UseGridTables()
                .UseMathematics()
                .UseMediaLinks()
                .UsePipeTables()
                .UseListExtras()
                .UseTaskLists()
                .UseAutoLinks()
                .UseGenericAttributes()
                .Build();

            FileTabControl.ItemsSource = _tabs;
        }

        #region Public API for Pipe Commands
        
        public System.Collections.Generic.List<TabItemData> GetTabs()
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
        
        private void ScrollToLine(TabItemData tab, int line)
        {
            if (tab.IsInitialized && tab.WebView.CoreWebView2 != null)
            {
                // Use setTimeout to ensure page is fully rendered
                tab.WebView.CoreWebView2.ExecuteScriptAsync($"setTimeout(function() {{ scrollToLine({line}); }}, 100)");
            }
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

        #region Tab Management

        public TabItemData? LoadMarkdownFile(string filePath, int? line = null, string? title = null, bool isTemp = false)
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
                RenderCompletion = new TaskCompletionSource<List<string>>()
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
            // Handle drops on WebView2 at window level
            tab.WebView.AllowDrop = true;
            tab.WebView.PreviewDragOver += (s, e) =>
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
            };
            tab.WebView.PreviewDrop += (s, e) =>
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
            };

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
                            await CopyElementAsPng(tab, elementType);
                        };
                        menuItems.Add(copyPngItem);
                        
                        if (elementType == "mermaid")
                        {
                            var copySvgItem = tab.WebView.CoreWebView2.Environment.CreateContextMenuItem(
                                "Copy as SVG", null,
                                Microsoft.Web.WebView2.Core.CoreWebView2ContextMenuItemKind.Command);
                            copySvgItem.CustomItemSelected += async (sender, args) =>
                            {
                                await CopyMermaidDiagramAtCursor(tab);
                            };
                            menuItems.Add(copySvgItem);
                        }
                    }
                }
                finally
                {
                    deferral.Complete();
                }
            };
            // Prevent dropped files from opening in new window
            tab.WebView.CoreWebView2.NewWindowRequested += (s, e) =>
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
            };

            // Handle messages from JavaScript (link clicks and hovers)
            tab.WebView.CoreWebView2.WebMessageReceived += (s, e) =>
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
                    var uri = message.Substring(6);

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

                // Handle pointing mode click
                if (message.StartsWith("point:", StringComparison.Ordinal))
                {
                    var data = message.Substring(6);
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

                // Handle render completion notification
                if (message.StartsWith("render-complete:", StringComparison.Ordinal))
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
                    return;
                }
            };

            // Setup zoom
            SetupZoomForTab(tab);

            tab.IsInitialized = true;

            // Setup file watcher
            SetupFileWatcher(tab);

            // Render Markdown
            tab.LastFileWriteTime = File.GetLastWriteTime(tab.FilePath);
            RenderMarkdown(tab);
            StatusText.Text = $"✓ {tab.LastFileWriteTime:HH:mm:ss}";
        }

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

        private void CloseTab(TabItemData tab)
        {
            tab.Watcher?.Dispose();
            tab.DebounceTimer?.Stop();
            tab.WebView.Dispose();

            _tabs.Remove(tab);

            if (_tabs.Count == 0)
            {
                // No tabs left - close this window
                Close();
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
                var html = ConvertMarkdownToHtml(markdown, baseDir!);
                tab.WebView.NavigateToString(html);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Markdown rendering error: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #region Window Size Adjustment

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

        #region HTML Conversion

        private string ConvertMarkdownToHtml(string markdown, string baseDir)
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
            // Content Security Policy to prevent XSS attacks
            html.AppendLine($"<meta http-equiv='Content-Security-Policy' content=\"default-src 'none'; style-src 'unsafe-inline' https://cdn.jsdelivr.net; img-src file: data: blob:; script-src 'nonce-{nonce}' 'unsafe-eval' https://cdn.jsdelivr.net; font-src https://cdn.jsdelivr.net;\"/>");
            html.AppendLine($"<base href='{baseUrl}'/>");
            html.AppendLine("<style>");
            html.AppendLine(@"
                body {
                    font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Helvetica, Arial, sans-serif;
                    line-height: 1.6;
                    padding: 40px;
                    max-width: 980px;
                    margin: 0 auto;
                    background-color: #ffffff;
                    color: #24292e;
                }
                h1, h2, h3, h4, h5, h6 {
                    margin-top: 24px;
                    margin-bottom: 16px;
                    font-weight: 600;
                    line-height: 1.25;
                }
                h1 { font-size: 2em; border-bottom: 1px solid #eaecef; padding-bottom: 0.3em; }
                h2 { font-size: 1.5em; border-bottom: 1px solid #eaecef; padding-bottom: 0.3em; }
                h3 { font-size: 1.25em; }
                code {
                    background-color: rgba(27,31,35,0.05);
                    padding: 0.2em 0.4em;
                    margin: 0;
                    border-radius: 3px;
                    font-family: 'Consolas', 'Monaco', 'Courier New', monospace;
                    font-size: 85%;
                }
                pre {
                    background-color: #f6f8fa;
                    padding: 16px;
                    border-radius: 6px;
                    overflow: auto;
                    line-height: 1.45;
                }
                pre code {
                    background-color: transparent;
                    padding: 0;
                    font-size: 100%;
                }
                blockquote {
                    padding: 0 1em;
                    color: #6a737d;
                    border-left: 0.25em solid #dfe2e5;
                    margin: 0 0 16px 0;
                }
                ul, ol {
                    padding-left: 2em;
                    margin-bottom: 16px;
                }
                li { margin-bottom: 4px; }
                table {
                    border-collapse: collapse;
                    width: 100%;
                    margin-bottom: 16px;
                }
                table th, table td {
                    padding: 6px 13px;
                    border: 1px solid #dfe2e5;
                }
                table th {
                    font-weight: 600;
                    background-color: #f6f8fa;
                }
                table tr:nth-child(2n) {
                    background-color: #f6f8fa;
                }
                table th p, table td p {
                    margin: 0;
                    display: inline;
                }
                a {
                    color: #0366d6;
                    text-decoration: none;
                }
                a:hover {
                    text-decoration: underline;
                }
                img {
                    max-width: 100%;
                    box-sizing: border-box;
                }
                hr {
                    height: 0.25em;
                    padding: 0;
                    margin: 24px 0;
                    background-color: #e1e4e8;
                    border: 0;
                }
                p {
                    margin-bottom: 16px;
                }
                .pointing-highlight {
                    outline: 2px solid #0078d4 !important;
                    outline-offset: 2px;
                    background-color: rgba(0, 120, 212, 0.1) !important;
                    cursor: pointer !important;
                }
                .pointing-flash {
                    animation: flash-effect 0.5s ease-out;
                }
                @keyframes flash-effect {
                    0% { box-shadow: inset 0 0 0 100px rgba(0, 120, 212, 0.4); }
                    100% { box-shadow: inset 0 0 0 100px transparent; }
                }
            ");
            html.AppendLine("</style>");
            // KaTeX for math rendering
            html.AppendLine("<link rel='stylesheet' href='https://cdn.jsdelivr.net/npm/katex@0.16.9/dist/katex.min.css'/>");
            html.AppendLine("<script defer src='https://cdn.jsdelivr.net/npm/katex@0.16.9/dist/katex.min.js'></script>");
            html.AppendLine("<script defer src='https://cdn.jsdelivr.net/npm/katex@0.16.9/dist/contrib/auto-render.min.js'></script>");
            // html2canvas for copying elements as PNG
            html.AppendLine("<script src='https://cdn.jsdelivr.net/npm/html2canvas@1.4.1/dist/html2canvas.min.js'></script>");
            html.AppendLine($"<script nonce='{nonce}'>");
            html.AppendLine(@"
                // Handle link clicks
                document.addEventListener('click', function(e) {
                    var target = e.target;
                    while (target && target.tagName !== 'A') {
                        target = target.parentElement;
                    }
                    if (target && target.href) {
                        var href = target.getAttribute('href');
                        // Handle anchor links (e.g. #footnote-1) within the page
                        if (href && href.startsWith('#')) {
                            e.preventDefault();
                            var targetId = href.substring(1);
                            var targetEl = document.getElementById(targetId);
                            if (targetEl) {
                                targetEl.scrollIntoView({ behavior: 'smooth' });
                            }
                        } else {
                            e.preventDefault();
                            window.chrome.webview.postMessage('click:' + target.href);
                        }
                    }
                });

                // Handle link hover
                document.addEventListener('mouseover', function(e) {
                    var target = e.target;
                    while (target && target.tagName !== 'A') {
                        target = target.parentElement;
                    }
                    if (target && target.href) {
                        window.chrome.webview.postMessage('hover:' + target.href);
                    }
                });

                // Handle Ctrl+wheel for zoom
                document.addEventListener('wheel', function(e) {
                    if (e.ctrlKey) {
                        e.preventDefault();
                        window.chrome.webview.postMessage('zoom:' + (e.deltaY < 0 ? 'in' : 'out'));
                    }
                }, { passive: false });

                // Handle mouse leave from link
                document.addEventListener('mouseout', function(e) {
                    var target = e.target;
                    while (target && target.tagName !== 'A') {
                        target = target.parentElement;
                    }
                    if (target && target.tagName === 'A') {
                        window.chrome.webview.postMessage('leave:');
                    }
                });
            ");
            html.AppendLine("</script>");
            html.AppendLine($"<script nonce='{nonce}'>");
            html.AppendLine(@"
                // Scroll to line function (called from C#)
                function scrollToLine(line) {
                    var elements = document.querySelectorAll('[data-line]');
                    var closest = null;
                    var closestLine = -1;
                    
                    for (var i = 0; i < elements.length; i++) {
                        var elemLine = parseInt(elements[i].getAttribute('data-line'));
                        if (elemLine <= line && elemLine > closestLine) {
                            closest = elements[i];
                            closestLine = elemLine;
                        }
                    }
                    
                    if (closest) {
                        closest.scrollIntoView({ behavior: 'smooth', block: 'start' });
                    }
                }

                // Pointing mode
                var pointingModeEnabled = false;
                var currentHighlight = null;

                function setPointingMode(enabled) {
                    pointingModeEnabled = enabled;
                    if (!enabled && currentHighlight) {
                        currentHighlight.classList.remove('pointing-highlight');
                        currentHighlight = null;
                    }
                    document.body.style.cursor = enabled ? 'crosshair' : '';
                    document.body.style.userSelect = enabled ? 'none' : '';
                }

                function getPointableElement(element) {
                    while (element && element !== document.body) {
                        var tagName = element.tagName ? element.tagName.toLowerCase() : '';
                        
                        // Table cells
                        if (tagName === 'td' || tagName === 'th') return element;
                        
                        // Mermaid nodes (g.node, g.cluster, g.edgeLabel)
                        if (element.hasAttribute && element.hasAttribute('data-mermaid-node')) {
                            return element;
                        }
                        
                        // Elements with data-line
                        if (element.hasAttribute && element.hasAttribute('data-line')) return element;
                        
                        // Mermaid container
                        if (element.classList && element.classList.contains('mermaid')) {
                            var parent = element;
                            while (parent && parent !== document.body) {
                                if (parent.hasAttribute && parent.hasAttribute('data-line')) return parent;
                                parent = parent.parentElement || parent.parentNode;
                            }
                            return element;
                        }
                        // KaTeX - try to find parent with data-line, otherwise return the katex element
                        if (element.classList && (element.classList.contains('katex') || element.classList.contains('math'))) {
                            // First check if this element itself has data-line
                            if (element.hasAttribute && element.hasAttribute('data-line')) return element;
                            // Then search parents
                            var parent = element.parentElement;
                            while (parent && parent !== document.body) {
                                if (parent.hasAttribute && parent.hasAttribute('data-line')) return parent;
                                parent = parent.parentElement;
                            }
                            // No parent with data-line found, return the katex element itself
                            return element;
                        }
                        element = element.parentElement || element.parentNode;
                    }
                    return null;
                }

                function getElementLine(element) {
                    // First check for exact source line (set on Mermaid nodes)
                    if (element && element.hasAttribute && element.hasAttribute('data-source-line')) {
                        return element.getAttribute('data-source-line');
                    }
                    while (element && element !== document.body) {
                        if (element.hasAttribute && element.hasAttribute('data-line')) {
                            return element.getAttribute('data-line');
                        }
                        // Use parentNode for SVG elements, parentElement for HTML
                        element = element.parentElement || element.parentNode;
                    }
                    return '?';
                }

                function getTableRowMarkdown(tr) {
                    var cells = tr.querySelectorAll('td, th');
                    var parts = [];
                    cells.forEach(function(cell) { parts.push(cell.textContent.trim()); });
                    return '| ' + parts.join(' | ') + ' |';
                }

                function getElementContent(element) {
                    var tagName = element.tagName.toLowerCase();
                    
                    if (tagName === 'td' || tagName === 'th') {
                        var tr = element.parentElement;
                        var table = tr ? tr.closest('table') : null;
                        var cellIndex = Array.from(tr.children).indexOf(element);
                        var rowIndex = table ? Array.from(table.querySelectorAll('tr')).indexOf(tr) : 0;
                        var cellContent = element.textContent.trim();
                        return 'table[row ' + rowIndex + ', col ' + cellIndex + '] cell: ' + cellContent + ' | row: ' + getTableRowMarkdown(tr);
                    }
                    if (tagName === 'tr') {
                        var table = element.closest('table');
                        var rowIndex = table ? Array.from(table.querySelectorAll('tr')).indexOf(element) : 0;
                        return 'table[row ' + rowIndex + '] ' + getTableRowMarkdown(element);
                    }
                    if (tagName === 'table') {
                        var headerRow = element.querySelector('tr');
                        return headerRow ? 'table: ' + getTableRowMarkdown(headerRow) : '(table)';
                    }
                    if (tagName === 'ul' || tagName === 'ol') {
                        var prefix = tagName === 'ol' ? '1.' : '-';
                        var items = [];
                        var directLis = element.querySelectorAll(':scope > li');
                        directLis.forEach(function(li, idx) {
                            var text = '';
                            for (var i = 0; i < li.childNodes.length; i++) {
                                var node = li.childNodes[i];
                                if (node.nodeType === 3) text += node.textContent;
                                else if (node.tagName && node.tagName.toLowerCase() === 'p') text += node.textContent;
                            }
                            text = text.trim();
                            if (text.length > 20) text = text.substring(0, 20) + '...';
                            var hasNested = li.querySelector('ul, ol');
                            var itemPrefix = tagName === 'ol' ? (idx + 1) + '.' : '-';
                            items.push(itemPrefix + ' ' + text + (hasNested ? ' [+]' : ''));
                        });
                        return items.join(', ');
                    }
                    // Mermaid node (inside SVG)
                    if (element.hasAttribute && element.hasAttribute('data-mermaid-node')) {
                        var nodeText = element.textContent.trim().replace(/\s+/g, ' ');
                        if (nodeText.length > 40) nodeText = nodeText.substring(0, 40) + '...';
                        var nodeType = 'node';
                        var className = element.getAttribute('class') || '';
                        if (element.classList && element.classList.contains('cluster')) nodeType = 'subgraph';
                        else if (element.classList && element.classList.contains('edgeLabel')) nodeType = 'edge';
                        else if (element.hasAttribute && element.hasAttribute('data-class-member')) {
                            // Check if member or method by parent class
                            var parent = element.parentElement;
                            if (parent && parent.classList && parent.classList.contains('methods-group')) {
                                nodeType = 'method';
                            } else {
                                nodeType = 'member';
                            }
                        }
                        else if (className.indexOf('messageText') !== -1) nodeType = 'message';
                        else if (element.hasAttribute && element.hasAttribute('data-hit-area-for')) {
                            nodeType = 'arrow';
                            var hitFor = element.getAttribute('data-hit-area-for');
                            var linkMatch = hitFor.match(/^L[-_]([^-_]+)[-_]([^-_]+)[-_]/);
                            if (linkMatch) {
                                nodeText = linkMatch[1] + ' -> ' + linkMatch[2];
                            }
                        }
                        else if (element.hasAttribute && element.hasAttribute('data-seq-arrow-text')) {
                            nodeType = 'arrow';
                            nodeText = element.getAttribute('data-seq-arrow-text');
                        }
                        else if (element.hasAttribute && element.hasAttribute('data-state-transition')) {
                            nodeType = 'transition';
                            var trans = element.getAttribute('data-state-transition');
                            nodeText = trans.replace('->', ' -> ');
                        }
                        else if (element.hasAttribute && element.hasAttribute('data-er-attr')) {
                            nodeType = 'attribute';
                            nodeText = element.getAttribute('data-er-attr');
                        }
                        else if (element.hasAttribute && element.hasAttribute('data-er-entity')) {
                            nodeType = 'entity';
                            nodeText = element.getAttribute('data-er-entity');
                        }
                        else if (element.hasAttribute && element.hasAttribute('data-er-relation')) {
                            nodeType = 'relationship';
                            nodeText = element.getAttribute('data-er-relation');
                        }
                        else if (element.hasAttribute && element.hasAttribute('data-gantt-task')) {
                            nodeType = 'task';
                            nodeText = element.getAttribute('data-gantt-task');
                        }
                        else if (element.hasAttribute && element.hasAttribute('data-gantt-task-name')) {
                            nodeType = 'task';
                            nodeText = element.getAttribute('data-gantt-task-name');
                        }
                        else if (element.hasAttribute && element.hasAttribute('data-gantt-section')) {
                            nodeType = 'section';
                            nodeText = element.getAttribute('data-gantt-section');
                        }
                        else if (className.indexOf('flowchart-link') !== -1) {
                            nodeType = 'arrow';
                            var elemId = element.id || '';
                            var linkMatch = elemId.match(/^L[-_]([^-_]+)[-_]([^-_]+)[-_]/);
                            if (linkMatch) {
                                nodeText = linkMatch[1] + ' -> ' + linkMatch[2];
                            }
                        }
                        else if (className.indexOf('messageLine') !== -1) {
                            nodeType = 'arrow';
                            // Get message text from previous sibling
                            var prev = element.previousElementSibling;
                            if (prev && prev.classList && prev.classList.contains('messageText')) {
                                nodeText = prev.textContent.trim();
                            }
                        }
                        else if (className.indexOf('transition') !== -1) {
                            nodeType = 'transition';
                            // Transition text would need to be fetched from mapping
                        }
                        return 'mermaid ' + nodeType + ': ' + nodeText;
                    }
                    // Mermaid container
                    if (element.classList && element.classList.contains('mermaid')) {
                        var src = element.getAttribute('data-mermaid-source') || '';
                        if (src) {
                            var firstLine = src.split('\n')[0].trim();
                            var nodeCount = (src.match(/\[.*?\]|\(.*?\)|{.*?}/g) || []).length;
                            var info = firstLine.length > 30 ? firstLine.substring(0, 30) + '...' : firstLine;
                            return '```mermaid ' + info + (nodeCount > 0 ? ' (' + nodeCount + ' nodes)' : '') + '```';
                        }
                        return '```mermaid (diagram)```';
                    }
                    if (element.classList.contains('katex') || element.classList.contains('math') || element.querySelector('.katex')) {
                        // Try to get original source from data attribute or textContent
                        var mathSrc = element.getAttribute('data-math') || element.textContent.trim();
                        mathSrc = mathSrc.replace(/\s+/g, ' ');
                        if (mathSrc.length > 60) mathSrc = mathSrc.substring(0, 60) + '...';
                        return '$$ ' + mathSrc + ' $$';
                    }
                    if (tagName === 'pre') {
                        var code = element.querySelector('code');
                        var lang = '';
                        if (code && code.className) {
                            var match = code.className.match(/language-(\w+)/);
                            if (match) lang = match[1];
                        }
                        var codeText = element.textContent.trim();
                        var lines = codeText.split('\n');
                        var preview = lines.slice(0, 2).join(' ').substring(0, 50);
                        if (lines.length > 2 || codeText.length > 50) preview += '...';
                        return '```' + lang + ' ' + preview + ' ```';
                    }
                    if (/^h[1-6]$/.test(tagName)) {
                        var level = tagName.charAt(1);
                        return '#'.repeat(parseInt(level)) + ' ' + element.textContent.trim();
                    }
                    if (tagName === 'li') {
                        // Get direct text content, handling both raw text and <p> wrapped text
                        var text = '';
                        var hasNested = false;
                        for (var i = 0; i < element.childNodes.length; i++) {
                            var node = element.childNodes[i];
                            if (node.nodeType === 3) { // TEXT_NODE
                                text += node.textContent;
                            } else if (node.tagName) {
                                var childTag = node.tagName.toLowerCase();
                                if (childTag === 'ul' || childTag === 'ol') {
                                    hasNested = true;
                                } else if (childTag === 'p') {
                                    text += node.textContent;
                                }
                            }
                        }
                        text = text.trim();
                        if (text.length > 60) text = text.substring(0, 60) + '...';
                        var parent = element.parentElement;
                        var prefix = (parent && parent.tagName.toLowerCase() === 'ol')
                            ? (Array.from(parent.children).indexOf(element) + 1) + '. '
                            : '- ';
                        return prefix + text + (hasNested ? ' (has nested items)' : '');
                    }
                    if (tagName === 'blockquote') {
                        var text = element.textContent.trim();
                        if (text.length > 60) text = text.substring(0, 60) + '...';
                        return '> ' + text;
                    }
                    if (tagName === 'hr') return '---';
                    var text = element.textContent.trim();
                    if (text.length > 80) text = text.substring(0, 80) + '...';
                    return text;
                }

                document.addEventListener('mouseover', function(e) {
                    if (!pointingModeEnabled) return;
                    var pointable = getPointableElement(e.target);
                    if (pointable && pointable !== currentHighlight) {
                        if (currentHighlight) currentHighlight.classList.remove('pointing-highlight');
                        pointable.classList.add('pointing-highlight');
                        currentHighlight = pointable;
                    }
                });

                document.addEventListener('mouseout', function(e) {
                    if (!pointingModeEnabled) return;
                    if (!currentHighlight) return;
                    var related = e.relatedTarget;
                    var relatedPointable = related ? getPointableElement(related) : null;
                    if (relatedPointable !== currentHighlight) {
                        currentHighlight.classList.remove('pointing-highlight');
                        currentHighlight = null;
                    }
                });

                document.addEventListener('click', function(e) {
                    if (!pointingModeEnabled) return;
                    var pointable = getPointableElement(e.target);
                    if (pointable) {
                        e.preventDefault();
                        e.stopPropagation();
                        
                        // Flash effect (SVG uses drop-shadow filter, HTML uses CSS class)
                        // For hit areas, flash the original element instead
                        var flashTarget = pointable;
                        if (pointable.hasAttribute && pointable.hasAttribute('data-hit-area-for')) {
                            var origId = pointable.getAttribute('data-hit-area-for');
                            var origElem = pointable.ownerSVGElement.getElementById(origId);
                            if (origElem) flashTarget = origElem;
                        } else if (pointable.hasAttribute && pointable.hasAttribute('data-seq-arrow-text')) {
                            // For sequence arrow hit areas, flash the previous sibling (the line element)
                            var prevElem = pointable.previousElementSibling;
                            if (prevElem) flashTarget = prevElem;
                        } else if (pointable.hasAttribute && pointable.hasAttribute('data-state-transition')) {
                            // For state transition hit areas, flash the previous sibling (the path element)
                            var prevElem = pointable.previousElementSibling;
                            if (prevElem) flashTarget = prevElem;
                        } else if (pointable.hasAttribute && pointable.hasAttribute('data-er-relation')) {
                            // For ER relation hit areas, flash the previous sibling (the path element)
                            var prevElem = pointable.previousElementSibling;
                            if (prevElem) flashTarget = prevElem;
                        }
                        var isSvg = flashTarget instanceof SVGElement;
                        if (isSvg) {
                            flashTarget.style.transition = 'none';
                            flashTarget.style.filter = 'drop-shadow(0 0 8px rgba(0, 120, 212, 1)) drop-shadow(0 0 4px rgba(0, 120, 212, 0.8))';
                            setTimeout(function() {
                                flashTarget.style.transition = 'filter 0.7s ease-out';
                                flashTarget.style.filter = '';
                            }, 10);
                            setTimeout(function() { flashTarget.style.transition = ''; }, 720);
                        } else {
                            flashTarget.classList.remove('pointing-flash');
                            void flashTarget.offsetWidth;
                            flashTarget.classList.add('pointing-flash');
                            setTimeout(function() { flashTarget.classList.remove('pointing-flash'); }, 500);
                        }
                        
                        var line = getElementLine(pointable);
                        var content = getElementContent(pointable);
                        window.chrome.webview.postMessage('point:' + line + '|' + content);
                    }
                }, true);
            ");
            html.AppendLine("</script>");
            // Mermaid.js for diagram rendering
            html.AppendLine("<script src='https://cdn.jsdelivr.net/npm/mermaid/dist/mermaid.min.js'></script>");
            html.AppendLine($"<script nonce='{nonce}'>mermaid.initialize({{ startOnLoad: false, theme: 'default' }});</script>");
            html.AppendLine("</head><body>");
            html.AppendLine(htmlContent);
            // KaTeX auto-render initialization
            html.AppendLine($"<script nonce='{nonce}'>");
            html.AppendLine(@"
                document.addEventListener('DOMContentLoaded', async function() {
                    var renderErrors = [];
                    
                    // KaTeX rendering with error collection
                    if (typeof renderMathInElement !== 'undefined') {
                        renderMathInElement(document.body, {
                            delimiters: [
                                {left: '\\[', right: '\\]', display: true},
                                {left: '\\(', right: '\\)', display: false},
                                {left: '$$', right: '$$', display: true},
                                {left: '$', right: '$', display: false}
                            ],
                            throwOnError: false,
                            errorCallback: function(msg, err) {
                                renderErrors.push('[KaTeX] ' + msg);
                            }
                        });
                        
                        // Also check for KaTeX error elements and extract line info
                        document.querySelectorAll('.katex-error').forEach(function(errElem) {
                            var line = '?';
                            var parent = errElem.parentElement;
                            while (parent && parent !== document.body) {
                                if (parent.hasAttribute('data-line')) {
                                    line = parent.getAttribute('data-line');
                                    break;
                                }
                                parent = parent.parentElement;
                            }
                            var formula = errElem.getAttribute('title') || errElem.textContent;
                            if (formula && !renderErrors.some(e => e.includes(formula.substring(0, 20)))) {
                                renderErrors.push('[KaTeX Line ' + line + '] ' + formula);
                            }
                        });
                        
                        // Copy data-line from parent to KaTeX elements
                        document.querySelectorAll('.katex').forEach(function(katex) {
                            var parent = katex.parentElement;
                            while (parent && parent !== document.body) {
                                if (parent.hasAttribute('data-line')) {
                                    katex.setAttribute('data-line', parent.getAttribute('data-line'));
                                    break;
                                }
                                parent = parent.parentElement;
                            }
                        });
                    }
                    
                    // Mermaid manual rendering with error collection
                    if (typeof mermaid !== 'undefined') {
                        var mermaidElements = document.querySelectorAll('.mermaid');
                        for (var elem of mermaidElements) {
                            try {
                                await mermaid.run({ nodes: [elem] });
                            } catch (e) {
                                var line = elem.getAttribute('data-line') || '?';
                                var msg = e.message || String(e);
                                renderErrors.push('[Mermaid Line ' + line + '] ' + msg);
                            }
                        }
                        
                        
                        // Make mermaid nodes clickable and set source line numbers
                        document.querySelectorAll('.mermaid').forEach(function(container) {
                            var source = container.getAttribute('data-mermaid-source') || '';
                            var baseLine = parseInt(container.getAttribute('data-line') || '0', 10);
                            var sourceLines = source.split('\n');
                            var svg = container.querySelector('svg');
                            if (!svg) return;
                            
                            // Parse source to build line number mappings
                            var nodeLineMap = {};
                            var arrowLineMap = {};
                            var messageLineNums = [];
                            var edgeLabelLineMap = {};
                            
                            for (var i = 0; i < sourceLines.length; i++) {
                                var line = sourceLines[i];
                                var lineNum = baseLine + i + 1;
                                
                                // Flowchart node: A[text] or A{text} or A(text)
                                var nodeMatch = line.match(/^\s*([A-Za-z0-9_]+)\s*[\[\{\(]/);
                                if (nodeMatch && !nodeLineMap[nodeMatch[1]]) {
                                    nodeLineMap[nodeMatch[1]] = lineNum;
                                }
                                
                                // Flowchart arrow: A --> B or A -->|label| B
                                var arrowMatch = line.match(/^\s*([A-Za-z0-9_]+)[^\-]*--[->](\|[^|]*\|)?\s*([A-Za-z0-9_]+)/);
                                if (arrowMatch) {
                                    var key = arrowMatch[1] + '-' + arrowMatch[3];
                                    arrowLineMap[key] = lineNum;
                                    if (arrowMatch[2]) {
                                        var label = arrowMatch[2].replace(/\|/g, '');
                                        edgeLabelLineMap[label] = lineNum;
                                    }
                                }
                                
                                // Sequence diagram message
                                if (/->>|-->|->/.test(line) && line.indexOf(':') !== -1) {
                                    messageLineNums.push(lineNum);
                                }
                                
                                // Sequence participant/actor
                                var actorMatch = line.match(/^\s*(participant|actor)\s+([A-Za-z0-9_]+)/);
                                if (actorMatch && !nodeLineMap[actorMatch[2]]) {
                                    nodeLineMap[actorMatch[2]] = lineNum;
                                }
                                
                                // Sequence implicit actor
                                var seqMatch = line.match(/^\s*([A-Za-z0-9_]+)\s*-/);
                                if (seqMatch && !nodeLineMap[seqMatch[1]]) {
                                    nodeLineMap[seqMatch[1]] = lineNum;
                                }
                                
                                // Class diagram member/method: ClassName : +memberName or ClassName: +methodName()
                                var classMemberMatch = line.match(/^\s*([A-Za-z0-9_]+)\s*:\s*(.+)$/);
                                if (classMemberMatch) {
                                    var memberText = classMemberMatch[2].trim();
                                    nodeLineMap[memberText] = lineNum;
                                }
                                
                                // State diagram transition: State1 --> State2 or [*] --> State
                                var stateTransMatch = line.match(/^\s*(\[\*\]|[A-Za-z0-9_]+)\s*-->\s*(\[\*\]|[A-Za-z0-9_]+)/);
                                if (stateTransMatch) {
                                    var stateKey = stateTransMatch[1] + '->' + stateTransMatch[2];
                                    arrowLineMap[stateKey] = lineNum;
                                }
                                
                                // ER diagram relationship: ENTITY1 ||--o{ ENTITY2 : label
                                var erRelMatch = line.match(/^\s*([A-Za-z0-9_-]+)\s*(\||\}|o).*(\||\{|o)\s*([A-Za-z0-9_-]+)\s*:\s*(\w+)/);
                                if (erRelMatch) {
                                    var erKey = erRelMatch[1] + '-' + erRelMatch[4];
                                    arrowLineMap[erKey] = lineNum;
                                    // Map the label
                                    nodeLineMap[erRelMatch[5]] = lineNum;
                                    // Map entity names from relationship (for entities without explicit definition)
                                    if (!nodeLineMap['errel:' + erRelMatch[1]]) nodeLineMap['errel:' + erRelMatch[1]] = lineNum;
                                    if (!nodeLineMap['errel:' + erRelMatch[4]]) nodeLineMap['errel:' + erRelMatch[4]] = lineNum;
                                }
                                
                                // ER diagram entity definition: ENTITY {
                                var erEntityMatch = line.match(/^\s*([A-Za-z0-9_-]+)\s*\{/);
                                if (erEntityMatch && !nodeLineMap['entity:' + erEntityMatch[1]]) {
                                    nodeLineMap['entity:' + erEntityMatch[1]] = lineNum;
                                }
                                
                                // ER diagram attribute: type attributeName
                                var erAttrMatch = line.match(/^\s+(\w+)\s+(\w+)\s*$/);
                                if (erAttrMatch) {
                                    nodeLineMap[erAttrMatch[2]] = lineNum;  // Map by attribute name
                                    nodeLineMap[erAttrMatch[1] + ' ' + erAttrMatch[2]] = lineNum;  // Map by 'type name'
                                }
                                
                                // Gantt task: TaskName :taskId, ...
                                var ganttTaskMatch = line.match(/^\s*(.+?)\s*:([a-zA-Z0-9]+),/);
                                if (ganttTaskMatch) {
                                    nodeLineMap['gantt:' + ganttTaskMatch[2]] = lineNum;
                                    nodeLineMap['gantt-name:' + ganttTaskMatch[1].trim()] = lineNum;
                                }
                                
                                // Gantt section: section SectionName
                                var ganttSectionMatch = line.match(/^\s*section\s+(.+)$/);
                                if (ganttSectionMatch) {
                                    nodeLineMap['gantt-section:' + ganttSectionMatch[1].trim()] = lineNum;
                                }
                            }
                            
                            // Mark parent g of bottom actors
                            svg.querySelectorAll('rect.actor-bottom').forEach(function(rect) {
                                var parent = rect.parentElement;
                                if (parent && parent.tagName.toLowerCase() === 'g') {
                                    parent.style.cursor = 'pointer';
                                    parent.setAttribute('data-mermaid-node', 'true');
                                    var textEl = parent.querySelector('text.actor');
                                    if (textEl) {
                                        var actorName = textEl.textContent.trim();
                                        if (nodeLineMap[actorName]) {
                                            parent.setAttribute('data-source-line', String(nodeLineMap[actorName]));
                                        }
                                    }
                                }
                            });
                            
                            // Sequence messages
                            var msgIdx = 0;
                            svg.querySelectorAll('text.messageText').forEach(function(msg) {
                                msg.style.cursor = 'pointer';
                                msg.setAttribute('data-mermaid-node', 'true');
                                if (msgIdx < messageLineNums.length) {
                                    msg.setAttribute('data-source-line', String(messageLineNums[msgIdx]));
                                }
                                msgIdx++;
                            });
                            var lineIdx = 0;
                            svg.querySelectorAll('line.messageLine0, line.messageLine1').forEach(function(ln) {
                                var sourceLine = null;
                                if (lineIdx < messageLineNums.length) {
                                    sourceLine = String(messageLineNums[lineIdx]);
                                }
                                
                                // Get message text from previous sibling for hit area
                                var msgText = '';
                                var prev = ln.previousElementSibling;
                                if (prev && prev.classList && prev.classList.contains('messageText')) {
                                    msgText = prev.textContent.trim();
                                }
                                
                                // Create transparent rect for hit area
                                var bbox = ln.getBBox();
                                var minSize = 16;
                                var rectW = Math.max(bbox.width, minSize);
                                var rectH = Math.max(bbox.height, minSize);
                                var rectX = bbox.x - (rectW - bbox.width) / 2;
                                var rectY = bbox.y - (rectH - bbox.height) / 2;
                                var hitRect = document.createElementNS('http://www.w3.org/2000/svg', 'rect');
                                hitRect.setAttribute('x', rectX);
                                hitRect.setAttribute('y', rectY);
                                hitRect.setAttribute('width', rectW);
                                hitRect.setAttribute('height', rectH);
                                hitRect.setAttribute('fill', 'transparent');
                                hitRect.style.cursor = 'pointer';
                                hitRect.setAttribute('data-mermaid-node', 'true');
                                hitRect.setAttribute('data-seq-arrow-text', msgText);
                                if (sourceLine) {
                                    hitRect.setAttribute('data-source-line', sourceLine);
                                }
                                ln.parentNode.insertBefore(hitRect, ln.nextSibling);
                                
                                // Original line also needs attributes
                                ln.style.cursor = 'pointer';
                                ln.setAttribute('data-mermaid-node', 'true');
                                if (sourceLine) {
                                    ln.setAttribute('data-source-line', sourceLine);
                                }
                                lineIdx++;
                            });
                            
                            
                            // Flowchart arrows - add transparent rect for full bounding box hit area
                            svg.querySelectorAll('path.flowchart-link').forEach(function(path) {
                                var pathId = path.id || '';
                                var linkMatch = pathId.match(/^L[-_]([^-_]+)[-_]([^-_]+)[-_]/);
                                var sourceLine = null;
                                if (linkMatch) {
                                    var key = linkMatch[1] + '-' + linkMatch[2];
                                    if (arrowLineMap[key]) {
                                        sourceLine = String(arrowLineMap[key]);
                                    }
                                }
                                
                                // Create transparent rect covering bounding box with minimum size
                                var bbox = path.getBBox();
                                var minSize = 16;
                                var rectW = Math.max(bbox.width, minSize);
                                var rectH = Math.max(bbox.height, minSize);
                                var rectX = bbox.x - (rectW - bbox.width) / 2;
                                var rectY = bbox.y - (rectH - bbox.height) / 2;
                                var hitRect = document.createElementNS('http://www.w3.org/2000/svg', 'rect');
                                hitRect.setAttribute('x', rectX);
                                hitRect.setAttribute('y', rectY);
                                hitRect.setAttribute('width', rectW);
                                hitRect.setAttribute('height', rectH);
                                hitRect.setAttribute('fill', 'transparent');
                                hitRect.style.cursor = 'pointer';
                                hitRect.setAttribute('data-mermaid-node', 'true');
                                hitRect.setAttribute('data-hit-area-for', pathId);
                                if (sourceLine) {
                                    hitRect.setAttribute('data-source-line', sourceLine);
                                }
                                path.parentNode.insertBefore(hitRect, path.nextSibling);
                                
                                // Original path also needs attributes for flash effect
                                path.setAttribute('data-mermaid-node', 'true');
                                if (sourceLine) {
                                    path.setAttribute('data-source-line', sourceLine);
                                }
                            });
                            
                            // State diagram transitions - add transparent rect for hit area
                            var stateTransKeys = Object.keys(arrowLineMap).filter(function(k) { return k.indexOf('->') !== -1; });
                            var transIdx = 0;
                            svg.querySelectorAll('path.transition').forEach(function(path) {
                                var sourceLine = null;
                                var transKey = stateTransKeys[transIdx] || '';
                                if (transKey && arrowLineMap[transKey]) {
                                    sourceLine = String(arrowLineMap[transKey]);
                                }
                                
                                // Create transparent rect covering bounding box
                                var bbox = path.getBBox();
                                var minSize = 16;
                                var rectW = Math.max(bbox.width, minSize);
                                var rectH = Math.max(bbox.height, minSize);
                                var rectX = bbox.x - (rectW - bbox.width) / 2;
                                var rectY = bbox.y - (rectH - bbox.height) / 2;
                                var hitRect = document.createElementNS('http://www.w3.org/2000/svg', 'rect');
                                hitRect.setAttribute('x', rectX);
                                hitRect.setAttribute('y', rectY);
                                hitRect.setAttribute('width', rectW);
                                hitRect.setAttribute('height', rectH);
                                hitRect.setAttribute('fill', 'transparent');
                                hitRect.style.cursor = 'pointer';
                                hitRect.setAttribute('data-mermaid-node', 'true');
                                hitRect.setAttribute('data-state-transition', transKey);
                                if (sourceLine) {
                                    hitRect.setAttribute('data-source-line', sourceLine);
                                }
                                path.parentNode.insertBefore(hitRect, path.nextSibling);
                                
                                // Original path also needs attributes
                                path.setAttribute('data-mermaid-node', 'true');
                                if (sourceLine) {
                                    path.setAttribute('data-source-line', sourceLine);
                                }
                                transIdx++;
                            });
                            
                            // Other nodes
                            svg.querySelectorAll('g.node, g.cluster, g.edgeLabel, g[id^=""root-""], text.actor, g.note, g.activation').forEach(function(node) {
                                node.style.cursor = 'pointer';
                                node.setAttribute('data-mermaid-node', 'true');
                                
                                var nodeId = node.id || '';
                                var nodeText = node.textContent.trim().replace(/\s+/g, ' ');
                                
                                // Flowchart node
                                var flowMatch = nodeId.match(/^flowchart-([^-]+)-/);
                                if (flowMatch && nodeLineMap[flowMatch[1]]) {
                                    node.setAttribute('data-source-line', String(nodeLineMap[flowMatch[1]]));
                                    return;
                                }
                                
                                // Sequence actor container
                                if (nodeId.indexOf('root-') === 0) {
                                    var actorText = node.querySelector('text.actor');
                                    if (actorText) {
                                        var actorName = actorText.textContent.trim();
                                        if (nodeLineMap[actorName]) {
                                            node.setAttribute('data-source-line', String(nodeLineMap[actorName]));
                                        }
                                    }
                                    return;
                                }
                                
                                // ER diagram entity
                                var erEntityMatch = nodeId.match(/^entity-([A-Za-z0-9_-]+)-/);
                                if (erEntityMatch) {
                                    var entityName = erEntityMatch[1];
                                    if (nodeLineMap['entity:' + entityName]) {
                                        node.setAttribute('data-source-line', String(nodeLineMap['entity:' + entityName]));
                                    } else if (nodeLineMap['errel:' + entityName]) {
                                        // Use relationship line for entities without explicit definition
                                        node.setAttribute('data-source-line', String(nodeLineMap['errel:' + entityName]));
                                    }
                                    return;
                                }
                                
                                // Edge label
                                if (node.classList && node.classList.contains('edgeLabel')) {
                                    if (edgeLabelLineMap[nodeText]) {
                                        node.setAttribute('data-source-line', String(edgeLabelLineMap[nodeText]));
                                    } else if (nodeLineMap[nodeText]) {
                                        // Also check nodeLineMap for ER diagram labels
                                        node.setAttribute('data-source-line', String(nodeLineMap[nodeText]));
                                    }
                                    return;
                                }
                                
                                // Sequence actor text
                                if (node.tagName && node.tagName.toLowerCase() === 'text') {
                                    var className = node.getAttribute('class') || '';
                                    if (className.indexOf('actor') !== -1 && nodeLineMap[nodeText]) {
                                        node.setAttribute('data-source-line', String(nodeLineMap[nodeText]));
                                    }
                                }
                            });
                            
                            // Class diagram members and methods
                            svg.querySelectorAll('g.members-group g.label, g.methods-group g.label').forEach(function(label) {
                                label.style.cursor = 'pointer';
                                label.setAttribute('data-mermaid-node', 'true');
                                label.setAttribute('data-class-member', 'true');
                                var memberText = label.textContent.trim();
                                if (nodeLineMap[memberText]) {
                                    label.setAttribute('data-source-line', String(nodeLineMap[memberText]));
                                }
                            });
                            
                            // Class diagram relations (inheritance arrows)
                            svg.querySelectorAll('path.relation').forEach(function(path) {
                                var pathId = path.id || '';
                                
                                // Create transparent rect for hit area
                                var bbox = path.getBBox();
                                var minSize = 16;
                                var rectW = Math.max(bbox.width, minSize);
                                var rectH = Math.max(bbox.height, minSize);
                                var rectX = bbox.x - (rectW - bbox.width) / 2;
                                var rectY = bbox.y - (rectH - bbox.height) / 2;
                                var hitRect = document.createElementNS('http://www.w3.org/2000/svg', 'rect');
                                hitRect.setAttribute('x', rectX);
                                hitRect.setAttribute('y', rectY);
                                hitRect.setAttribute('width', rectW);
                                hitRect.setAttribute('height', rectH);
                                hitRect.setAttribute('fill', 'transparent');
                                hitRect.style.cursor = 'pointer';
                                hitRect.setAttribute('data-mermaid-node', 'true');
                                hitRect.setAttribute('data-class-relation', pathId);
                                path.parentNode.insertBefore(hitRect, path.nextSibling);
                                
                                path.style.cursor = 'pointer';
                                path.setAttribute('data-mermaid-node', 'true');
                            });
                            
                            // ER diagram attributes
                            svg.querySelectorAll('g.label.attribute-name, g.label.attribute-type').forEach(function(label) {
                                var attrText = label.textContent.trim();
                                var isType = label.classList.contains('attribute-type');
                                label.style.cursor = 'pointer';
                                label.setAttribute('data-mermaid-node', 'true');
                                label.setAttribute('data-er-attr', attrText);
                                
                                // For attribute-type, get line from next sibling (attribute-name)
                                if (isType) {
                                    var nextSib = label.nextElementSibling;
                                    if (nextSib && nextSib.classList.contains('attribute-name')) {
                                        var nameText = nextSib.textContent.trim();
                                        if (nodeLineMap[nameText]) {
                                            label.setAttribute('data-source-line', String(nodeLineMap[nameText]));
                                        }
                                    }
                                } else if (nodeLineMap[attrText]) {
                                    label.setAttribute('data-source-line', String(nodeLineMap[attrText]));
                                }
                            });
                            
                            // ER diagram entity names
                            svg.querySelectorAll('g.label.name').forEach(function(label) {
                                var entityName = label.textContent.trim();
                                label.style.cursor = 'pointer';
                                label.setAttribute('data-mermaid-node', 'true');
                                label.setAttribute('data-er-entity', entityName);
                                if (nodeLineMap['entity:' + entityName]) {
                                    label.setAttribute('data-source-line', String(nodeLineMap['entity:' + entityName]));
                                }
                            });
                            
                            // ER diagram relationship lines
                            svg.querySelectorAll('path.relationshipLine').forEach(function(path) {
                                var pathId = path.id || '';
                                // Extract entity names from id like 'id_entity-CUSTOMER-0_entity-ORDER-1_0'
                                var relMatch = pathId.match(/entity-([A-Za-z0-9_-]+)-\d+_entity-([A-Za-z0-9_-]+)-/);
                                var sourceLine = null;
                                var relText = '';
                                if (relMatch) {
                                    var key = relMatch[1] + '-' + relMatch[2];
                                    relText = relMatch[1] + ' - ' + relMatch[2];
                                    if (arrowLineMap[key]) {
                                        sourceLine = String(arrowLineMap[key]);
                                    }
                                }
                                
                                // Create transparent rect for hit area
                                var bbox = path.getBBox();
                                var minSize = 16;
                                var rectW = Math.max(bbox.width, minSize);
                                var rectH = Math.max(bbox.height, minSize);
                                var rectX = bbox.x - (rectW - bbox.width) / 2;
                                var rectY = bbox.y - (rectH - bbox.height) / 2;
                                var hitRect = document.createElementNS('http://www.w3.org/2000/svg', 'rect');
                                hitRect.setAttribute('x', rectX);
                                hitRect.setAttribute('y', rectY);
                                hitRect.setAttribute('width', rectW);
                                hitRect.setAttribute('height', rectH);
                                hitRect.setAttribute('fill', 'transparent');
                                hitRect.style.cursor = 'pointer';
                                hitRect.setAttribute('data-mermaid-node', 'true');
                                hitRect.setAttribute('data-er-relation', relText);
                                if (sourceLine) {
                                    hitRect.setAttribute('data-source-line', sourceLine);
                                }
                                path.parentNode.insertBefore(hitRect, path.nextSibling);
                                
                                path.style.cursor = 'pointer';
                                path.setAttribute('data-mermaid-node', 'true');
                                if (sourceLine) {
                                    path.setAttribute('data-source-line', sourceLine);
                                }
                            });
                            
                            // Gantt chart tasks
                            svg.querySelectorAll('rect.task').forEach(function(task) {
                                var taskId = task.id || '';
                                task.style.cursor = 'pointer';
                                task.setAttribute('data-mermaid-node', 'true');
                                task.setAttribute('data-gantt-task', taskId);
                                if (nodeLineMap['gantt:' + taskId]) {
                                    task.setAttribute('data-source-line', String(nodeLineMap['gantt:' + taskId]));
                                }
                            });
                            
                            // Gantt chart task text
                            svg.querySelectorAll('text.taskText').forEach(function(text) {
                                var textId = text.id || '';
                                var taskId = textId.replace('-text', '');
                                var taskName = text.textContent.trim();
                                text.style.cursor = 'pointer';
                                text.setAttribute('data-mermaid-node', 'true');
                                text.setAttribute('data-gantt-task-name', taskName);
                                if (nodeLineMap['gantt:' + taskId]) {
                                    text.setAttribute('data-source-line', String(nodeLineMap['gantt:' + taskId]));
                                } else if (nodeLineMap['gantt-name:' + taskName]) {
                                    text.setAttribute('data-source-line', String(nodeLineMap['gantt-name:' + taskName]));
                                }
                            });
                            
                            // Gantt chart section titles
                            svg.querySelectorAll('text.sectionTitle').forEach(function(text) {
                                var sectionName = text.textContent.trim();
                                text.style.cursor = 'pointer';
                                text.setAttribute('data-mermaid-node', 'true');
                                text.setAttribute('data-gantt-section', sectionName);
                                if (nodeLineMap['gantt-section:' + sectionName]) {
                                    text.setAttribute('data-source-line', String(nodeLineMap['gantt-section:' + sectionName]));
                                }
                            });
                        });
                    }
                    
                    // Notify render completion
                    window.chrome.webview.postMessage('render-complete:' + JSON.stringify(renderErrors));
                });
            ");
            html.AppendLine("</script>");
            html.AppendLine("</body></html>");

            return html.ToString();
        }

        #endregion

        #region File Operations

        private void OpenFileDialog()
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Markdown files (*.md;*.markdown)|*.md;*.markdown|All files (*.*)|*.*",
                Title = "Open Markdown File"
            };

            if (dialog.ShowDialog() == true)
            {
                LoadMarkdownFile(dialog.FileName);
            }
        }

        #endregion

        #region Event Handlers

        private void TabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (FileTabControl.SelectedItem is TabItemData tab)
            {
                UpdateWindowTitle();
                _targetZoomFactor = tab.WebView.ZoomFactor;
                _lastZoomFactor = tab.WebView.ZoomFactor;
            }
        }

        private void CloseTab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is TabItemData tab)
            {
                CloseTab(tab);
            }
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            // Handle tab drop from another MarkdownViewer window
            if (e.Data.GetDataPresent(DataFormats.Text))
            {
                var text = e.Data.GetData(DataFormats.Text) as string;
                if (!string.IsNullOrEmpty(text) && text.StartsWith("MDVIEWER:"))
                {
                    var filePath = text.Substring(9); // Remove "MDVIEWER:" prefix
                    if (IsSupportedFile(filePath))
                    {
                        LoadMarkdownFile(filePath);
                        e.Effects = DragDropEffects.Move;
                        e.Handled = true;
                        return;
                    }
                }
            }

            // Handle file drop from Explorer
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
            }
        }

        private void Window_DragOver(object sender, DragEventArgs e)
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

            // Accept file drops from Explorer
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F5)
            {
                if (FileTabControl.SelectedItem is TabItemData tab)
                {
                    RenderMarkdown(tab);
                    StatusText.Text = $"✓ {DateTime.Now:HH:mm:ss}";
                }
                e.Handled = true;
            }
            else if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control)
            {
                OpenFileDialog();
                e.Handled = true;
            }
            else if (e.Key == Key.W && Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (FileTabControl.SelectedItem is TabItemData tab)
                {
                    CloseTab(tab);
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Tab && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                // Ctrl+Shift+Tab: Previous tab
                if (_tabs.Count > 1)
                {
                    var currentIndex = FileTabControl.SelectedIndex;
                    FileTabControl.SelectedIndex = (currentIndex - 1 + _tabs.Count) % _tabs.Count;
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.Control)
            {
                // Ctrl+Tab: Next tab
                if (_tabs.Count > 1)
                {
                    var currentIndex = FileTabControl.SelectedIndex;
                    FileTabControl.SelectedIndex = (currentIndex + 1) % _tabs.Count;
                }
                e.Handled = true;
            }
        }

        private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                ApplyZoomDelta(e.Delta);
            }
        }

        private void TopmostToggle_Click(object sender, RoutedEventArgs e)
        {
            Topmost = TopmostToggle.IsChecked == true;
        }

        private void DragMoveToggle_Click(object sender, RoutedEventArgs e)
        {
            _isDragMoveMode = DragMoveToggle.IsChecked == true;
            DragOverlay.Visibility = _isDragMoveMode ? Visibility.Visible : Visibility.Collapsed;
            DragOverlay.Cursor = Cursors.SizeAll;

            // Disable pointing mode if enabling drag mode
            if (_isDragMoveMode && _isPointingMode)
            {
                PointingModeToggle.IsChecked = false;
                _isPointingMode = false;
                foreach (var tab in _tabs)
                {
                    if (tab.IsInitialized && tab.WebView.CoreWebView2 != null)
                    {
                        tab.WebView.CoreWebView2.ExecuteScriptAsync("setPointingMode(false)");
                    }
                }
            }

            // Enable/disable WebView for all tabs
            foreach (var tab in _tabs)
            {
                tab.WebView.IsEnabled = !_isDragMoveMode;
            }
        }

        private void PointingModeToggle_Click(object sender, RoutedEventArgs e)
        {
            _isPointingMode = PointingModeToggle.IsChecked == true;

            // Disable drag mode if enabling pointing mode
            if (_isPointingMode && _isDragMoveMode)
            {
                DragMoveToggle.IsChecked = false;
                _isDragMoveMode = false;
                DragOverlay.Visibility = Visibility.Collapsed;
                foreach (var tab in _tabs)
                {
                    tab.WebView.IsEnabled = true;
                }
            }
            
            foreach (var tab in _tabs)
            {
                if (tab.IsInitialized && tab.WebView.CoreWebView2 != null)
                {
                    tab.WebView.CoreWebView2.ExecuteScriptAsync("setPointingMode(" + (_isPointingMode ? "true" : "false") + ")");
                }
            }
        }

        private void OpenInCodeButton_Click(object sender, RoutedEventArgs e)
        {
            if (FileTabControl.SelectedItem is TabItemData tab && !string.IsNullOrEmpty(tab.FilePath))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c code \"{tab.FilePath}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to open VS Code: {ex.Message}", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void DragOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isDocumentScrolling = true;
            _scrollStartPoint = e.GetPosition(DragOverlay);
            DragOverlay.CaptureMouse();
            DragOverlay.Cursor = Cursors.None;  // Hide cursor while dragging
        }

        private void DragOverlay_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isDocumentScrolling && FileTabControl.SelectedItem is TabItemData tab)
            {
                var currentPoint = e.GetPosition(DragOverlay);
                var deltaY = _scrollStartPoint.Y - currentPoint.Y;
                _scrollStartPoint = currentPoint;
                tab.WebView.CoreWebView2?.ExecuteScriptAsync($"window.scrollBy(0, {deltaY})");
            }
        }

        private void DragOverlay_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isDocumentScrolling = false;
            DragOverlay.ReleaseMouseCapture();
            DragOverlay.Cursor = Cursors.SizeAll;  // Restore cursor
        }

        private void DragOverlay_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (FileTabControl.SelectedItem is TabItemData tab)
            {
                if (Keyboard.Modifiers == ModifierKeys.Control)
                {
                    // Ctrl+Wheel: Zoom
                    e.Handled = true;
                    ApplyZoomDelta(e.Delta);
                }
                else
                {
                    // Normal wheel: Scroll
                    e.Handled = true;
                    var scrollAmount = -e.Delta;
                    tab.WebView.CoreWebView2?.ExecuteScriptAsync($"window.scrollBy(0, {scrollAmount})");
                }
            }
        }

        private void DragOverlay_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (FileTabControl.SelectedItem is TabItemData tab)
            {
                var contextMenu = new ContextMenu();
                
                var copySvgItem = new MenuItem { Header = "Copy diagram as SVG" };
                copySvgItem.Click += async (s, args) => await CopyMermaidDiagramAtCursor(tab);
                contextMenu.Items.Add(copySvgItem);
                
                var copyPngItem = new MenuItem { Header = "Copy diagram as PNG" };
                copyPngItem.Click += async (s, args) => await CopyMermaidDiagramAsPngAtCursor(tab);
                contextMenu.Items.Add(copyPngItem);
                
                contextMenu.IsOpen = true;
            }
        }

        private void TabHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _tabDragStartPoint = e.GetPosition(this);
            _isTabDragging = false;
        }

        private void TabHeader_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                _isTabDragging = false;
                return;
            }

            if (sender is not StackPanel panel || panel.Tag is not TabItemData tab)
                return;

            var currentPos = e.GetPosition(this);
            var diff = _tabDragStartPoint - currentPos;

            // Check if moved enough to start drag
            if (!_isTabDragging &&
                (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                 Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance))
            {
                _isTabDragging = true;
                _draggedTab = tab;

                // Get tab header's screen position and convert to DIP
                var tabScreenPosPhysical = panel.PointToScreen(new Point(0, 0));
                var tabScreenPos = PhysicalToDip(tabScreenPosPhysical);

                // Record initial cursor position (DIP) and window position (DIP)
                _dragStartCursorPos = GetCursorPosDip();
                _dragStartWindowPos = tabScreenPos;

                // Create preview window at tab's actual position
                CreateDragPreviewWindow(tab, tabScreenPos, panel.ActualWidth, panel.ActualHeight);

                // Start drag operation - use Text format for cross-window compatibility
                var data = new DataObject();
                data.SetData(DataFormats.Text, "MDVIEWER:" + tab.FilePath);
                var result = DragDrop.DoDragDrop(panel, data, DragDropEffects.Move);

                // Get preview window position before closing it
                Point previewPos = new Point(0, 0);
                if (_dragPreviewWindow != null)
                {
                    previewPos = new Point(_dragPreviewWindow.Left, _dragPreviewWindow.Top);
                }

                // Cleanup preview window
                CloseDragPreviewWindow();

                // Check drop result
                var screenPoint = GetCursorPosDip();
                var windowRect = new Rect(Left, Top, Width, Height);

                if (!windowRect.Contains(screenPoint))
                {
                    // Dropped outside this window - check if over another MarkdownViewer window
                    var targetWindow = FindWindowAtPosition(screenPoint);

                    if (targetWindow != null)
                    {
                        // Drop on another MarkdownViewer window - transfer the tab
                        var filePath = tab.FilePath;
                        CloseTab(tab);
                        targetWindow.LoadMarkdownFile(filePath);
                        targetWindow.Activate();
                    }
                    else if (_tabs.Count > 1)
                    {
                        // Dropped on empty space - create new window
                        DetachTabToNewWindow(tab, previewPos);
                    }
                }
                // If dropped on same window, do nothing (tab is already here)

                _isTabDragging = false;
                _draggedTab = null;
            }
        }

        private void CreateDragPreviewWindow(TabItemData tab, Point tabScreenPos, double tabWidth, double tabHeight)
        {
            // Use actual tab size with some padding
            var previewWidth = Math.Max(tabWidth + 16, 100);
            var previewHeight = Math.Max(tabHeight + 8, 28);

            _dragPreviewWindow = new Window
            {
                Width = previewWidth,
                Height = previewHeight,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                Topmost = true,
                ShowInTaskbar = false,
                IsHitTestVisible = false
            };

            var border = new Border
            {
                Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(230, 255, 255, 255)),
                BorderBrush = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0, 120, 215)),
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 4, 8, 4)
            };

            var textBlock = new TextBlock
            {
                Text = tab.Title,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            border.Child = textBlock;
            _dragPreviewWindow.Content = border;

            // Position at tab's screen position (adjust for border)
            _dragPreviewWindow.Left = tabScreenPos.X - 8;
            _dragPreviewWindow.Top = tabScreenPos.Y - 4;

            _dragPreviewWindow.Show();
        }

        private void CloseDragPreviewWindow()
        {
            if (_dragPreviewWindow != null)
            {
                _dragPreviewWindow.Close();
                _dragPreviewWindow = null;
            }
        }

        protected override void OnQueryContinueDrag(QueryContinueDragEventArgs e)
        {
            base.OnQueryContinueDrag(e);

            if (!_isTabDragging || _draggedTab == null)
                return;

            // Update preview window position based on cursor movement from start (using DIP coordinates)
            if (_dragPreviewWindow != null)
            {
                var currentCursor = GetCursorPosDip();
                var deltaX = currentCursor.X - _dragStartCursorPos.X;
                var deltaY = currentCursor.Y - _dragStartCursorPos.Y;
                _dragPreviewWindow.Left = _dragStartWindowPos.X - 8 + deltaX;
                _dragPreviewWindow.Top = _dragStartWindowPos.Y - 4 + deltaY;
            }
        }

        protected override void OnGiveFeedback(GiveFeedbackEventArgs e)
        {
            base.OnGiveFeedback(e);

            if (_isTabDragging)
            {
                e.UseDefaultCursors = false;

                // Check if cursor is over another MarkdownViewer window
                var screenPoint = GetCursorPosDip();
                var targetWindow = FindWindowAtPosition(screenPoint);

                if (targetWindow != null)
                {
                    // Over another window - show hand cursor (will merge)
                    Mouse.SetCursor(Cursors.Hand);
                }
                else
                {
                    // Not over another window - show move cursor (will create new window)
                    Mouse.SetCursor(Cursors.SizeAll);
                }

                e.Handled = true;
            }
        }

        protected override void OnDrop(DragEventArgs e)
        {
            // Handle tab detach drop - this fires when dropped back on same window
            base.OnDrop(e);
        }

        protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnPreviewMouseLeftButtonUp(e);
            _isTabDragging = false;
        }

        private void DetachTabToNewWindow(TabItemData tab, Point previewPosition)
        {
            // Save file path before closing tab
            var filePath = tab.FilePath;

            // Close tab in current window
            CloseTab(tab);

            // Create new window
            // Position so that the tab in new window aligns with where preview was
            // Preview was at (tabScreenPos.X - 8, tabScreenPos.Y - 4)
            // So tab position = preview + (8, 4)
            // New window needs to account for title bar height
            var titleBarHeight = SystemParameters.WindowCaptionHeight + 
                                 SystemParameters.ResizeFrameHorizontalBorderHeight;

            var newWindow = new MainWindow();
            newWindow.Left = previewPosition.X + 8;  // Adjust for preview border padding
            newWindow.Top = previewPosition.Y + 4 - titleBarHeight;  // Adjust for title bar
            newWindow.Show();

            // Load file in new window
            newWindow.LoadMarkdownFile(filePath);
        }

        #region Mermaid Diagram Copy

        private async Task CopyElementAsPng(TabItemData tab, string elementType)
        {
            if (elementType == "mermaid")
            {
                await CopyMermaidDiagramAsPngAtCursor(tab);
            }
            else if (elementType == "math")
            {
                await CopyMathAsPngAtCursor(tab);
            }
        }

        private async Task CopyMermaidDiagramAtCursor(TabItemData tab)
        {
            // Find mermaid diagram at the right-click position
            var script = $@"
                (function() {{
                    var x = {_contextMenuPosition.X};
                    var y = {_contextMenuPosition.Y};
                    var element = document.elementFromPoint(x, y);
                    var mermaidDiv = element ? element.closest('.mermaid') : null;
                    if (mermaidDiv) {{
                        var svg = mermaidDiv.querySelector('svg');
                        if (svg) {{
                            var serializer = new XMLSerializer();
                            return serializer.serializeToString(svg);
                        }}
                    }}
                    return '';
                }})()";
            
            var result = await tab.WebView.CoreWebView2.ExecuteScriptAsync(script);
            // Remove surrounding quotes and unescape
            if (result.StartsWith("\"") && result.EndsWith("\""))
            {
                result = result.Substring(1, result.Length - 2);
                result = System.Text.RegularExpressions.Regex.Unescape(result);
            }
            
            if (!string.IsNullOrEmpty(result) && result.Contains("<svg"))
            {
                Clipboard.SetText(result);
                StatusText.Text = "✓ SVG copied";
            }
            else
            {
                StatusText.Text = "No diagram found";
            }
        }

        private async Task CopyMermaidDiagramAsPngAtCursor(TabItemData tab)
        {
            var tcs = new TaskCompletionSource<string>();
            
            void handler(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
            {
                var msg = e.TryGetWebMessageAsString();
                if (msg != null && msg.StartsWith("PNG:"))
                {
                    tab.WebView.CoreWebView2.WebMessageReceived -= handler;
                    tcs.TrySetResult(msg.Substring(4));
                }
            }
            
            tab.WebView.CoreWebView2.WebMessageReceived += handler;
            
            // Use JavaScript to convert SVG to PNG via canvas with data URL
            // Find mermaid diagram at the right-click position
            var script = $@"
                (async function() {{
                    var x = {_contextMenuPosition.X};
                    var y = {_contextMenuPosition.Y};
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
            
            await tab.WebView.CoreWebView2.ExecuteScriptAsync(script);
            
            // Wait for result with timeout
            var timeoutTask = Task.Delay(5000);
            var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);
            
            if (completedTask == timeoutTask)
            {
                tab.WebView.CoreWebView2.WebMessageReceived -= handler;
                StatusText.Text = "✗ PNG copy timeout";
                return;
            }
            
            var result = await tcs.Task;
            
            if (!string.IsNullOrEmpty(result) && result.StartsWith("data:image/png;base64,"))
            {
                try
                {
                    var base64 = result.Substring("data:image/png;base64,".Length);
                    var bytes = Convert.FromBase64String(base64);
                    using var stream = new System.IO.MemoryStream(bytes);
                    var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    
                    Clipboard.SetImage(bitmap);
                    StatusText.Text = "✓ PNG copied";
                }
                catch
                {
                    StatusText.Text = "✗ PNG copy failed";
                }
            }
            else
            {
                StatusText.Text = "No diagram found";
            }
        }

        private async Task CopyMathAsPngAtCursor(TabItemData tab)
        {
            var tcs = new TaskCompletionSource<string>();
            
            void handler(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
            {
                var msg = e.TryGetWebMessageAsString();
                if (msg != null && msg.StartsWith("MATHPNG:"))
                {
                    tab.WebView.CoreWebView2.WebMessageReceived -= handler;
                    tcs.TrySetResult(msg.Substring(8));
                }
            }
            
            tab.WebView.CoreWebView2.WebMessageReceived += handler;
            
            // Use html2canvas to convert KaTeX math to PNG
            // Find math element (.katex) at the right-click position
            var script = $@"
                (async function() {{
                    var x = {_contextMenuPosition.X};
                    var y = {_contextMenuPosition.Y};
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
                        // Capture the element with extra space to prevent clipping
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
                        
                        // Find bounds of non-white pixels
                        var top = h, left = w, right = 0, bottom = 0;
                        for (var y = 0; y < h; y++) {{
                            for (var x = 0; x < w; x++) {{
                                var idx = (y * w + x) * 4;
                                // Check if pixel is not white (with some tolerance)
                                if (data[idx] < 250 || data[idx+1] < 250 || data[idx+2] < 250) {{
                                    if (x < left) left = x;
                                    if (x > right) right = x;
                                    if (y < top) top = y;
                                    if (y > bottom) bottom = y;
                                }}
                            }}
                        }}
                        
                        // Add padding
                        var padding = 15;
                        left = Math.max(0, left - padding);
                        top = Math.max(0, top - padding);
                        right = Math.min(w - 1, right + padding);
                        bottom = Math.min(h - 1, bottom + padding);
                        
                        var trimmedWidth = right - left + 1;
                        var trimmedHeight = bottom - top + 1;
                        
                        // Create trimmed canvas
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
            await tab.WebView.CoreWebView2.ExecuteScriptAsync(script);
            
            // Wait for result with timeout
            var timeoutTask = Task.Delay(5000);
            var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);
            
            if (completedTask == timeoutTask)
            {
                tab.WebView.CoreWebView2.WebMessageReceived -= handler;
                StatusText.Text = "✗ PNG copy timeout";
                return;
            }
            
            var pngData = await tcs.Task;
            
            if (!string.IsNullOrEmpty(pngData) && pngData.StartsWith("data:image/png"))
            {
                try
                {
                    var base64 = pngData.Substring("data:image/png;base64,".Length);
                    var bytes = Convert.FromBase64String(base64);
                    
                    using var stream = new System.IO.MemoryStream(bytes);
                    var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    
                    Clipboard.SetImage(bitmap);
                    StatusText.Text = "✓ Math PNG copied";
                }
                catch
                {
                    StatusText.Text = "✗ PNG copy failed";
                }
            }
            else
            {
                StatusText.Text = "No math found";
            }
        }

        #endregion

        protected override void OnClosed(EventArgs e)
        {
            CloseDragPreviewWindow();
            foreach (var tab in _tabs)
            {
                tab.Watcher?.Dispose();
                tab.DebounceTimer?.Stop();
                tab.WebView.Dispose();
            }
            _zoomAnimationTimer?.Stop();
            base.OnClosed(e);
        }

        #endregion
    }
}
