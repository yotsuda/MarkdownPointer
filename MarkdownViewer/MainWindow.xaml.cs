using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
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

        #region Tab Management

        public void LoadMarkdownFile(string filePath)
        {
            if (!File.Exists(filePath))
            {
                MessageBox.Show($"File not found: {filePath}", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Check if file is already open
            foreach (var tab in _tabs)
            {
                if (string.Equals(tab.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                {
                    FileTabControl.SelectedItem = tab;
                    return;
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
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    e.Effects = DragDropEffects.Copy;
                    e.Handled = true;
                }
            };
            tab.WebView.PreviewDrop += (s, e) =>
            {
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

                // Handle mouse leave from link
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
                FileTabControl.Visibility = Visibility.Collapsed;
                PlaceholderPanel.Visibility = Visibility.Visible;
                Title = "Markdown Viewer";
                LinkStatusText.Text = "";
                StatusText.Text = "";
                WatchStatusText.Text = "";
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
            var targetWidth = (BaseContentWidth * zoomFactor) + ScrollbarWidth;
            targetWidth = Math.Max(MinWindowWidth, Math.Min(targetWidth, SystemParameters.WorkArea.Width * 0.9));
            
            Width = targetWidth;
            
            if (Left + Width > SystemParameters.WorkArea.Width)
            {
                Left = Math.Max(0, SystemParameters.WorkArea.Width - Width);
            }
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
            
            // In drag move mode, apply zoom immediately without animation for better performance
            if (_isDragMoveMode && FileTabControl.SelectedItem is TabItemData tab)
            {
                tab.WebView.ZoomFactor = _targetZoomFactor;
                _lastZoomFactor = _targetZoomFactor;
                AdjustWindowSizeForZoom(_targetZoomFactor);
                return;
            }
            
            if (!_zoomAnimationTimer!.IsEnabled)
            {
                _zoomAnimationTimer.Start();
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
            html.AppendLine($"<meta http-equiv='Content-Security-Policy' content=\"default-src 'none'; style-src 'unsafe-inline'; img-src file: data:; script-src 'nonce-{nonce}'; font-src 'none';\"/>");
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

        private void TopmostToggle_Click(object sender, RoutedEventArgs e)
        {
            Topmost = TopmostToggle.IsChecked == true;
        }

        private void DragMoveToggle_Click(object sender, RoutedEventArgs e)
        {
            _isDragMoveMode = DragMoveToggle.IsChecked == true;
            DragOverlay.Visibility = _isDragMoveMode ? Visibility.Visible : Visibility.Collapsed;
            
            // Enable/disable WebView for all tabs
            foreach (var tab in _tabs)
            {
                tab.WebView.IsEnabled = !_isDragMoveMode;
            }
        }

        private void DragOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            DragMove();
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

        protected override void OnClosed(EventArgs e)
        {
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