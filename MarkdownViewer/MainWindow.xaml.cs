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
            _pipeline = new MarkdownPipelineBuilder()
                .UseAdvancedExtensions()
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
        
        #endregion

        #region Tab Management

        public void LoadMarkdownFile(string filePath)
        {
            if (!File.Exists(filePath))
            {
                MessageBox.Show($"File not found: {filePath}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Check if file is already open in this window
            foreach (var tab in _tabs)
            {
                if (string.Equals(tab.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                {
                    FileTabControl.SelectedItem = tab;
                    return;
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

            // Create new tab
            var newTab = new TabItemData
            {
                FilePath = filePath,
                Title = Path.GetFileName(filePath),
                WebView = new WebView2()
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
            tab.WebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            tab.WebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            tab.WebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            tab.WebView.CoreWebView2.Settings.IsWebMessageEnabled = true;
            tab.WebView.CoreWebView2.Settings.AreHostObjectsAllowed = false;

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
            };

            // Setup zoom
            SetupZoomForTab(tab);

            tab.IsInitialized = true;

            // Setup file watcher
            SetupFileWatcher(tab);

            // Render Markdown
            RenderMarkdown(tab);
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

                // Bring the updated tab to front
                FileTabControl.SelectedItem = tab;
                Activate();

                RenderMarkdown(tab);
                StatusText.Text = $"✓ {DateTime.Now:HH:mm:ss}";
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
            var htmlContent = Markdown.ToHtml(markdown, _pipeline);

            // Convert path for file:// URL
            var baseUrl = new Uri(baseDir + Path.DirectorySeparatorChar).AbsoluteUri;

            // Generate nonce for CSP
            var nonce = Convert.ToBase64String(Guid.NewGuid().ToByteArray());

            var html = new StringBuilder();
            html.AppendLine("<!DOCTYPE html>");
            html.AppendLine("<html><head>");
            html.AppendLine("<meta charset='utf-8'/>");
            // Content Security Policy to prevent XSS attacks
            html.AppendLine($"<meta http-equiv='Content-Security-Policy' content=\"default-src 'none'; style-src 'unsafe-inline'; img-src file: data:; script-src 'nonce-{nonce}' https://cdn.jsdelivr.net; font-src 'none';\"/>");
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
            ");
            html.AppendLine("</style>");
            html.AppendLine($"<script nonce='{nonce}'>");
            html.AppendLine(@"
                // Handle link clicks
                document.addEventListener('click', function(e) {
                    var target = e.target;
                    while (target && target.tagName !== 'A') {
                        target = target.parentElement;
                    }
                    if (target && target.href) {
                        e.preventDefault();
                        window.chrome.webview.postMessage('click:' + target.href);
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
            // Mermaid.js for diagram rendering
            html.AppendLine("<script src='https://cdn.jsdelivr.net/npm/mermaid/dist/mermaid.min.js'></script>");
            html.AppendLine($"<script nonce='{nonce}'>mermaid.initialize({{ startOnLoad: true, theme: 'default' }});</script>");
            html.AppendLine("</head><body>");
            html.AppendLine(htmlContent);
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

            // Enable/disable WebView for all tabs
            foreach (var tab in _tabs)
            {
                tab.WebView.IsEnabled = !_isDragMoveMode;
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