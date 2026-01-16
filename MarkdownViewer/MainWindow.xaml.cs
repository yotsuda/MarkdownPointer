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
using MarkdownViewer.Resources;
using MarkdownViewer.Services;

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
        public string? RenderedHtml { get; set; }  // Cache for fast window detach
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
        private readonly HtmlGenerator _htmlGenerator;
        private readonly ClipboardService _clipboardService;
        private readonly ObservableCollection<TabItemData> _tabs = new();
        private DispatcherTimer? _zoomAnimationTimer;
        private double _lastZoomFactor = 1.0;
        private double _targetZoomFactor = 1.0;
        private bool _isDragMoveMode = false;
        private bool _isPointingMode = true;
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
        private Point _tabOffsetInWindow;
        private Point _firstTabOffsetInWindow;
        private bool _isTabDragging = false;
        private TabItemData? _draggedTab = null;
        private Window? _dragPreviewWindow = null;

        public void BringToFront()
        {
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }
            
            // Temporarily set Topmost to force window to front
            Topmost = true;
            Topmost = false;
            
            Activate();
            Focus();
        }

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

            _htmlGenerator = new HtmlGenerator(_pipeline);
            _clipboardService = new ClipboardService(msg => StatusText.Text = msg);
            
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

        #region Tab Management

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

        private void AddTab_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog();
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

            // Enable/disable WebView and text selection for all tabs
            foreach (var tab in _tabs)
            {
                tab.WebView.IsEnabled = !_isDragMoveMode;
                if (tab.IsInitialized && tab.WebView.CoreWebView2 != null)
                {
                    // Disable text selection in pan mode (like pointing mode)
                    var userSelect = _isDragMoveMode ? "none" : (_isPointingMode ? "none" : "");
                    tab.WebView.CoreWebView2.ExecuteScriptAsync($"document.body.style.userSelect = '{userSelect}'");
                }
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
                copySvgItem.Click += async (s, args) => await _clipboardService.CopyMermaidSvgAsync(tab.WebView, _contextMenuPosition);
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

                // Record tab offset from window origin (including title bar)
                _tabOffsetInWindow = new Point(tabScreenPos.X - Left, tabScreenPos.Y - Top);

                // Record first tab's offset (for new window positioning when detaching)
                // Y is same for all tabs, only X differs based on tab position
                var tabIndex = _tabs.IndexOf(tab);
                if (tabIndex == 0)
                {
                    _firstTabOffsetInWindow = _tabOffsetInWindow;
                }
                else
                {
                    // Calculate X difference between dragged tab and first tab
                    // by summing widths of all tabs before the dragged one
                    double xOffset = 0;
                    for (int i = 0; i < tabIndex; i++)
                    {
                        var container = FileTabControl.ItemContainerGenerator.ContainerFromIndex(i) as TabItem;
                        if (container != null)
                        {
                            xOffset += container.ActualWidth;
                        }
                    }
                    _firstTabOffsetInWindow = new Point(_tabOffsetInWindow.X - xOffset, _tabOffsetInWindow.Y);
                }

                // Record initial cursor position (DIP) and window position (DIP)
                _dragStartCursorPos = GetCursorPosDip();
                _dragStartWindowPos = tabScreenPos;
                // Create preview window at tab's actual position
                CreateDragPreviewWindow(tab, tabScreenPos, panel.ActualWidth, panel.ActualHeight);

                // Start drag operation - use Text format for cross-window compatibility
                var data = new DataObject();
                data.SetData(DataFormats.Text, "MDVIEWER:" + tab.FilePath);
                var result = DragDrop.DoDragDrop(panel, data, DragDropEffects.Move);

                // Cleanup preview window
                CloseDragPreviewWindow();

                // Check drop result using cursor position
                var dropCursorPos = GetCursorPosDip();
                var windowRect = new Rect(Left, Top, Width, Height);

                // Calculate where the tab should be based on cursor movement
                var cursorDelta = new Point(
                    dropCursorPos.X - _dragStartCursorPos.X,
                    dropCursorPos.Y - _dragStartCursorPos.Y);
                var tabDropPos = new Point(
                    _dragStartWindowPos.X + cursorDelta.X,
                    _dragStartWindowPos.Y + cursorDelta.Y);

                if (!windowRect.Contains(dropCursorPos))
                {
                    // Dropped outside this window - check if over another MarkdownViewer window
                    var targetWindow = FindWindowAtPosition(dropCursorPos);

                    if (targetWindow != null)
                    {
                        // Drop on another MarkdownViewer window - transfer the tab instance
                        TransferTabToWindow(tab, targetWindow);
                        targetWindow.Activate();
                    }
                    else if (_tabs.Count > 1)
                    {
                        // Dropped on empty space with multiple tabs - create new window
                        DetachTabToNewWindow(tab, tabDropPos);
                    }
                    else
                    {
                        // Only one tab - move window so tab aligns with drop position
                        Left = tabDropPos.X - _tabOffsetInWindow.X;
                        Top = tabDropPos.Y - _tabOffsetInWindow.Y;
                    }
                }

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

        private void TransferTabToWindow(TabItemData tab, MainWindow targetWindow)
        {
            // Move the tab instance to another window (no re-initialization)
            
            // Remove tab from this window's collection (don't dispose WebView2)
            tab.Watcher?.Dispose();
            tab.Watcher = null;
            tab.DebounceTimer?.Stop();
            tab.DebounceTimer = null;
            _tabs.Remove(tab);
            
            // Update this window's state
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
                    // This is the last window - show placeholder
                    FileTabControl.Visibility = Visibility.Collapsed;
                    PlaceholderPanel.Visibility = Visibility.Visible;
                    Title = "Markdown Viewer";
                }
            }
            else
            {
                UpdateWindowTitle();
            }

            // Add tab to target window
            targetWindow._tabs.Add(tab);
            targetWindow.FileTabControl.SelectedItem = tab;
            targetWindow.PlaceholderPanel.Visibility = Visibility.Collapsed;
            targetWindow.FileTabControl.Visibility = Visibility.Visible;
            targetWindow.UpdateWindowTitle();
            
            // Re-setup file watcher in target window context
            targetWindow.SetupFileWatcher(tab);
        }

        private void DetachTabToNewWindow(TabItemData tab, Point tabDropPos)
        {
            // Move the tab instance directly to the new window (no re-initialization)
            var windowWidth = Width;
            var windowHeight = Height;

            // Remove tab from this window's collection (don't dispose WebView2)
            tab.Watcher?.Dispose();
            tab.Watcher = null;
            tab.DebounceTimer?.Stop();
            tab.DebounceTimer = null;
            _tabs.Remove(tab);
            
            // Update this window's state
            if (_tabs.Count == 0)
            {
                FileTabControl.Visibility = Visibility.Collapsed;
                PlaceholderPanel.Visibility = Visibility.Visible;
                Title = "Markdown Viewer";
            }
            else
            {
                UpdateWindowTitle();
            }

            // Create new window at drop position
            var newWindow = new MainWindow();
            newWindow.WindowStartupLocation = WindowStartupLocation.Manual;
            newWindow.Width = windowWidth;
            newWindow.Height = windowHeight;
            newWindow.Left = tabDropPos.X - _firstTabOffsetInWindow.X;
            newWindow.Top = tabDropPos.Y - _firstTabOffsetInWindow.Y;
            newWindow.Show();

            // Set position again after Show() in case it was overridden
            newWindow.Left = tabDropPos.X - _firstTabOffsetInWindow.X;
            newWindow.Top = tabDropPos.Y - _firstTabOffsetInWindow.Y;

            // Add tab to new window (reuse the same WebView2 instance)
            newWindow._tabs.Add(tab);
            newWindow.FileTabControl.SelectedItem = tab;
            newWindow.PlaceholderPanel.Visibility = Visibility.Collapsed;
            newWindow.FileTabControl.Visibility = Visibility.Visible;
            newWindow.UpdateWindowTitle();
            
            // Re-setup file watcher in new window context
            newWindow.SetupFileWatcher(tab);
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
