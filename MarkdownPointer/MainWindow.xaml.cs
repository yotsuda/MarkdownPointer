using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Markdig;
using Microsoft.Win32;
using MarkdownPointer.Helpers;
using MarkdownPointer.Models;
using MarkdownPointer.Resources;
using MarkdownPointer.Services;

namespace MarkdownPointer
{
    /// <summary>
    /// Main window for the Markdown Viewer application.
    /// Split into partial classes for maintainability:
    /// - MainWindow.xaml.cs (this file): Core fields, constructor, utilities
    /// - MainWindow.TabManagement.cs: Tab lifecycle and rendering
    /// - MainWindow.DragDrop.cs: Tab and file drag/drop operations
    /// - MainWindow.EventHandlers.cs: XAML event handlers
    /// </summary>
    public partial class MainWindow : Window
    {
        #region Constants

        private const double ZoomMultiplier = 1.1;
        private const double MinZoom = 0.25;
        private const double MaxZoom = 5.0;
        private const double BaseContentWidth = 1060.0;
        private const double ScrollbarWidth = 20.0;
        private const double MinWindowWidth = 400.0;

        private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".dll", ".pdb", ".obj", ".lib", ".bin", ".dat",
            ".zip", ".gz", ".tar", ".7z", ".rar", ".bz2", ".xz",
            ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".tiff", ".webp",
            ".mp3", ".mp4", ".avi", ".mov", ".wmv", ".flv", ".mkv", ".wav", ".ogg",
            ".pdf", ".docx", ".xlsx", ".pptx",
            ".msi", ".cab", ".iso",
            ".woff", ".woff2", ".ttf", ".otf", ".eot",
            ".class", ".pyc", ".o", ".so", ".dylib",
            ".db", ".sqlite", ".mdb",
            ".nupkg",
        };

        #endregion

        #region Fields

        private MarkdownPipeline? _pipeline;
        private HtmlGenerator? _htmlGenerator;
        private readonly ClipboardService _clipboardService;
        private readonly MermaidExportService _mermaidExportService = new();
        private readonly Lazy<RecentFilesService> _lazyRecentFiles = new();
        private RecentFilesService _recentFiles => _lazyRecentFiles.Value;
        private readonly ObservableCollection<TabItemData> _tabs = new();
        private bool _suppressCtrlTab;

        // Zoom state
        private DispatcherTimer? _zoomAnimationTimer;
        private double _currentZoomFactor = 1.0;
        private double _targetZoomFactor = 1.0;

        // Mode toggles
        private bool _isPointingMode = true;
        private bool _pointingModeBeforeSvg = true;
        private string _slideTheme = "black";

        // UI state
        private DispatcherTimer? _statusMessageTimer;
        private DispatcherTimer? _statusBlinkTimer;
        private DispatcherTimer? _spinnerTimer;
        private int _spinnerFrame;
        private Point _contextMenuPosition;

        // Tab drag state
        private Point _tabDragStartPoint;
        private Point _dragStartCursorPos;
        private Point _dragStartWindowPos;
        private Point _tabOffsetInWindow;
        private Point _firstTabOffsetInWindow;
        private bool _isTabDragging = false;
        private TabItemData? _draggedTab = null;
        private Window? _dragPreviewWindow = null;
        private int _tabDropTargetIndex = -1;

        #endregion

        #region Constructor

        public MainWindow()
        {
            InitializeComponent();

            _clipboardService = new ClipboardService(msg => StatusText.Text = msg);

            FileTabControl.ItemsSource = _tabs;

            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            PlaceholderTitle.Text = $"Markdown Pointer v{version!.Major}.{version.Minor}.{version.Build}";

            // Defer recent files to after window is shown
            Loaded += (_, _) => RefreshRecentFiles();
            SourceInitialized += (_, _) => RefreshSystemMenu();
        }

        #endregion

        #region Lazy Markdig Pipeline

        /// <summary>
        /// Lazily initializes the Markdig pipeline and HtmlGenerator on first use.
        /// Saves ~70ms from constructor by deferring until a file is actually rendered.
        /// </summary>
        internal HtmlGenerator GetHtmlGenerator()
        {
            if (_htmlGenerator != null) return _htmlGenerator;

            _pipeline = new MarkdownPipelineBuilder()
                .UseAbbreviations()
                .UseAutoIdentifiers(Markdig.Extensions.AutoIdentifiers.AutoIdentifierOptions.GitHub)
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
            return _htmlGenerator;
        }

        #endregion

        #region Window Lifecycle

        public void BringToFront()
        {
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            // Use native API to force foreground (works across processes)
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                NativeMethods.ForceForegroundWindow(hwnd);
            }

            // Also use WPF methods as backup
            Topmost = true;
            Topmost = false;
            Activate();
            Focus();
        }

        protected override void OnClosed(EventArgs e)
        {
            CloseDragPreviewWindow();
            foreach (var tab in _tabs)
            {
                tab.Dispose();
            }
            _zoomAnimationTimer?.Stop();
            base.OnClosed(e);
        }

        #endregion

        #region Coordinate Utilities

        /// <summary>
        /// Convert physical pixels to WPF DIP coordinates.
        /// </summary>
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

        /// <summary>
        /// Get cursor position in WPF DIP coordinates.
        /// </summary>
        private Point GetCursorPosDip()
        {
            if (NativeMethods.GetCursorPos(out var pt))
            {
                return PhysicalToDip(new Point(pt.X, pt.Y));
            }
            return new Point(0, 0);
        }

        /// <summary>
        /// Find another MainWindow at the given screen position (excluding this window).
        /// </summary>
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

        #endregion

        #region Recent Files

        private static readonly System.Windows.Media.SolidColorBrush LinkBrush = new(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#0366d6"));
        private static readonly System.Windows.Media.SolidColorBrush PinBrush = new(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#8B949E"));
        private static readonly System.Windows.Media.SolidColorBrush PinActiveBrush = new(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#D29922"));
        private static readonly System.Windows.Media.FontFamily EmojiFont = new("Segoe UI Emoji");

        private void RefreshRecentFiles()
        {
            var files = _recentFiles.GetRecentFiles();
            RefreshSystemMenu();
            if (files.Count > 0)
            {
                RecentFilesPanel.Visibility = Visibility.Visible;
                RecentFilesList.Items.Clear();
                foreach (var entry in files)
                {
                    var panel = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Margin = new Thickness(0, 2, 0, 2),
                        Focusable = true,
                        FocusVisualStyle = null,
                        Tag = entry.Path
                    };
                    panel.KeyDown += (s, args) =>
                    {
                        if (args.Key == Key.Enter && s is StackPanel sp && sp.Tag is string f)
                            OpenRecentFile(f);
                    };
                    panel.GotFocus += (s, _) => { if (s is StackPanel sp) sp.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE8, 0xEA, 0xED)); };
                    panel.LostFocus += (s, _) => { if (s is StackPanel sp) sp.Background = System.Windows.Media.Brushes.Transparent; };

                    var pinned = entry.Pinned;
                    var pin = new TextBlock
                    {
                        Text = "📌",
                        FontFamily = EmojiFont,
                        FontSize = 11,
                        Foreground = pinned ? PinActiveBrush : PinBrush,
                        Opacity = pinned ? 1.0 : 0.4,
                        Cursor = Cursors.Hand,
                        Margin = new Thickness(0, 0, 6, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        Tag = entry.Path,
                        ToolTip = pinned ? "Unpin" : "Pin"
                    };
                    pin.MouseLeftButtonUp += PinFile_Click;
                    pin.MouseEnter += (s, _) => { if (s is TextBlock t) t.Opacity = 1.0; };
                    pin.MouseLeave += (s, _) => { if (s is TextBlock t) t.Opacity = pinned ? 1.0 : 0.4; };

                    var tb = new TextBlock
                    {
                        Text = Path.Combine(Path.GetFileName(Path.GetDirectoryName(entry.Path)!), Path.GetFileName(entry.Path)),
                        ToolTip = entry.Path,
                        FontSize = 13,
                        Foreground = LinkBrush,
                        Cursor = Cursors.Hand,
                        Tag = entry.Path
                    };
                    tb.MouseEnter += RecentLink_MouseEnter;
                    tb.MouseLeave += RecentLink_MouseLeave;
                    tb.MouseLeftButtonUp += RecentFile_Click;

                    var close = new TextBlock
                    {
                        Text = "✕",
                        FontSize = 11,
                        Foreground = PinBrush,
                        Opacity = 0,
                        Cursor = Cursors.Hand,
                        Margin = new Thickness(6, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        Tag = entry.Path,
                        ToolTip = "Remove from recent"
                    };
                    close.MouseLeftButtonUp += RemoveRecentFile_Click;

                    panel.MouseEnter += (s, _) => close.Opacity = 1.0;
                    panel.MouseLeave += (s, _) => close.Opacity = 0;

                    panel.Children.Add(pin);
                    panel.Children.Add(tb);
                    panel.Children.Add(close);
                    RecentFilesList.Items.Add(panel);
                }
            }
            else
            {
                RecentFilesPanel.Visibility = Visibility.Collapsed;
            }

            var folders = _recentFiles.GetRecentFolders();
            if (folders.Count > 0)
            {
                RecentFoldersPanel.Visibility = Visibility.Visible;
                RecentFoldersList.Items.Clear();
                foreach (var (folder, pinned) in folders)
                {
                    var panel = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Margin = new Thickness(0, 2, 0, 2),
                        Focusable = true,
                        FocusVisualStyle = null,
                        Tag = folder
                    };
                    panel.KeyDown += (s, args) =>
                    {
                        if (args.Key == Key.Enter && s is StackPanel sp && sp.Tag is string f)
                            OpenRecentFolder(f);
                    };
                    panel.GotFocus += (s, _) => { if (s is StackPanel sp) sp.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE8, 0xEA, 0xED)); };
                    panel.LostFocus += (s, _) => { if (s is StackPanel sp) sp.Background = System.Windows.Media.Brushes.Transparent; };

                    var pin = new TextBlock
                    {
                        Text = "📌",
                        FontFamily = EmojiFont,
                        FontSize = 11,
                        Foreground = pinned ? PinActiveBrush : PinBrush,
                        Opacity = pinned ? 1.0 : 0.4,
                        Cursor = Cursors.Hand,
                        Margin = new Thickness(0, 0, 6, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        Tag = folder,
                        ToolTip = pinned ? "Unpin" : "Pin"
                    };
                    pin.MouseLeftButtonUp += PinFolder_Click;
                    pin.MouseEnter += (s, _) => { if (s is TextBlock t) t.Opacity = 1.0; };
                    pin.MouseLeave += (s, _) => { if (s is TextBlock t) t.Opacity = pinned ? 1.0 : 0.4; };

                    var tb = new TextBlock
                    {
                        Text = folder,
                        FontSize = 13,
                        Foreground = LinkBrush,
                        Cursor = Cursors.Hand,
                        Tag = folder
                    };
                    tb.MouseEnter += RecentLink_MouseEnter;
                    tb.MouseLeave += RecentLink_MouseLeave;
                    tb.MouseLeftButtonUp += RecentFolder_Click;

                    var close = new TextBlock
                    {
                        Text = "✕",
                        FontSize = 11,
                        Foreground = PinBrush,
                        Opacity = 0,
                        Cursor = Cursors.Hand,
                        Margin = new Thickness(6, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        Tag = folder,
                        ToolTip = "Remove from recent"
                    };
                    close.MouseLeftButtonUp += RemoveRecentFolder_Click;

                    panel.MouseEnter += (s, _) => close.Opacity = 1.0;
                    panel.MouseLeave += (s, _) => close.Opacity = 0;

                    panel.Children.Add(pin);
                    panel.Children.Add(tb);
                    panel.Children.Add(close);
                    RecentFoldersList.Items.Add(panel);
                }
            }
            else
            {
                RecentFoldersPanel.Visibility = Visibility.Collapsed;
            }
        }

        private const uint SysMenuRecentBase = 0x1000;
        private const uint SysMenuFolderBase = 0x2000;
        private readonly List<string> _sysMenuRecentFiles = new();
        private readonly List<string> _sysMenuRecentFolders = new();

        private void RefreshSystemMenu()
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            // Reset system menu to default
            NativeMethods.GetSystemMenu(hwnd, true);
            var sysMenu = NativeMethods.GetSystemMenu(hwnd, false);

            var files = _recentFiles.GetRecentFiles();
            var folders = _recentFiles.GetRecentFolders();

            _sysMenuRecentFiles.Clear();
            _sysMenuRecentFiles.AddRange(files.Select(e => e.Path));
            _sysMenuRecentFolders.Clear();
            _sysMenuRecentFolders.AddRange(folders.Select(f => f.Path));

            if (files.Count == 0 && folders.Count == 0) return;

            var defaultCount = NativeMethods.GetMenuItemCount(sysMenu);

            // Separator before our items
            NativeMethods.InsertMenu(sysMenu, (uint)defaultCount, NativeMethods.MF_BYPOSITION | NativeMethods.MF_SEPARATOR, 0, string.Empty);

            // Recent Folders
            if (folders.Count > 0)
            {
                NativeMethods.InsertMenu(sysMenu, (uint)(defaultCount + 1),
                    NativeMethods.MF_BYPOSITION | NativeMethods.MF_STRING | NativeMethods.MF_GRAYED, 0, "Recent Folders");
                for (int i = 0; i < folders.Count; i++)
                {
                    var prefix = folders[i].Pinned ? "  * " : "  ";
                    NativeMethods.InsertMenu(sysMenu, (uint)(defaultCount + 2 + i),
                        NativeMethods.MF_BYPOSITION | NativeMethods.MF_STRING, SysMenuFolderBase + (uint)i,
                        prefix + folders[i].Path);
                }
            }

            // Recent Files
            if (files.Count > 0)
            {
                var offset = defaultCount + 1 + (folders.Count > 0 ? 1 + folders.Count : 0);
                NativeMethods.InsertMenu(sysMenu, (uint)offset,
                    NativeMethods.MF_BYPOSITION | NativeMethods.MF_STRING | NativeMethods.MF_GRAYED, 0, "Recent Files");
                for (int i = 0; i < files.Count; i++)
                {
                    var display = Path.Combine(Path.GetFileName(Path.GetDirectoryName(files[i].Path)!), Path.GetFileName(files[i].Path));
                    var prefix = files[i].Pinned ? "  * " : "  ";
                    NativeMethods.InsertMenu(sysMenu, (uint)(offset + 1 + i),
                        NativeMethods.MF_BYPOSITION | NativeMethods.MF_STRING, SysMenuRecentBase + (uint)i,
                        prefix + display);
                }
            }

            // Hook WndProc if not already hooked
            EnsureSystemMenuHook();
        }

        private bool _sysMenuHooked;

        private void EnsureSystemMenuHook()
        {
            if (_sysMenuHooked) return;
            var source = System.Windows.Interop.HwndSource.FromHwnd(
                new System.Windows.Interop.WindowInteropHelper(this).Handle);
            source?.AddHook(WndProc);
            _sysMenuHooked = true;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == NativeMethods.WM_SYSCOMMAND)
            {
                var id = (uint)(wParam.ToInt64() & 0xFFFF);
                if (id >= SysMenuRecentBase && id < SysMenuRecentBase + 100)
                {
                    var index = (int)(id - SysMenuRecentBase);
                    if (index < _sysMenuRecentFiles.Count)
                    {
                        OpenRecentFile(_sysMenuRecentFiles[index]);
                        handled = true;
                    }
                }
                else if (id >= SysMenuFolderBase && id < SysMenuFolderBase + 100)
                {
                    var index = (int)(id - SysMenuFolderBase);
                    if (index < _sysMenuRecentFolders.Count)
                    {
                        OpenRecentFolder(_sysMenuRecentFolders[index]);
                        handled = true;
                    }
                }
            }
            return IntPtr.Zero;
        }

        private void RecentLink_MouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is TextBlock tb)
            {
                tb.TextDecorations = TextDecorations.Underline;
                if (tb.Tag is string path)
                    LinkStatusText.Text = path;
            }
        }

        private void RecentLink_MouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is TextBlock tb)
            {
                tb.TextDecorations = null;
                LinkStatusText.Text = "";
            }
        }

        private void OpenRecentFile(string path)
        {
            if (File.Exists(path))
                LoadMarkdownFile(path);
            else
                ShowStatusMessage($"✗ File not found: {path}");
        }

        private void OpenRecentFolder(string folder)
        {
            if (Directory.Exists(folder))
                OpenFileDialog(folder);
            else
                ShowStatusMessage($"✗ Folder not found: {folder}");
        }

        private void RecentFile_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is TextBlock tb && tb.Tag is string path)
                OpenRecentFile(path);
        }

        private void RecentFolder_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is TextBlock tb && tb.Tag is string folder)
                OpenRecentFolder(folder);
        }

        private void PinFile_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is TextBlock tb && tb.Tag is string path)
            {
                _recentFiles.ToggleFilePin(path);
                RefreshRecentFiles();
            }
        }

        private void PinFolder_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is TextBlock tb && tb.Tag is string folder)
            {
                _recentFiles.ToggleFolderPin(folder);
                RefreshRecentFiles();
            }
        }

        private void RemoveRecentFile_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is TextBlock tb && tb.Tag is string path)
            {
                _recentFiles.RemoveFile(path);
                RefreshRecentFiles();
            }
        }

        private void RemoveRecentFolder_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is TextBlock tb && tb.Tag is string folder)
            {
                _recentFiles.RemoveFolder(folder);
                RefreshRecentFiles();
            }
        }

        #endregion

        #region File Path Status

        private void FilePathText_MouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is TextBlock tb)
                tb.TextDecorations = TextDecorations.Underline;
        }

        private void FilePathText_MouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is TextBlock tb)
                tb.TextDecorations = null;
        }

        private void FilePathText_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is TextBlock tb && tb.Tag is string path && File.Exists(path))
            {
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
                ShowStatusMessage("📂 Opened in Explorer");
            }
        }

        private void UpdateFilePathStatus()
        {
            if (FileTabControl.SelectedItem is TabItemData tab && !string.IsNullOrEmpty(tab.FilePath))
            {
                FilePathText.Text = tab.FilePath;
                FilePathText.ToolTip = "Click to reveal in Explorer";
                FilePathText.Tag = tab.FilePath;
            }
            else
            {
                FilePathText.Text = "";
                FilePathText.ToolTip = null;
                FilePathText.Tag = null;
            }
        }

        internal void UpdateFilePathVisibility()
        {
            if (!string.IsNullOrEmpty(LinkStatusText.Text))
            {
                FilePathText.Visibility = Visibility.Collapsed;
                LineNumberText.Visibility = Visibility.Collapsed;
                LinkStatusText.Visibility = Visibility.Visible;
            }
            else
            {
                FilePathText.Visibility = Visibility.Visible;
                LineNumberText.Visibility = Visibility.Visible;
                LinkStatusText.Visibility = Visibility.Collapsed;
            }
        }

        #endregion

        #region File Dialog

        private void OpenFileDialog(string? initialDirectory = null)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Markdown files|*.md;*.markdown;*.txt|SVG files|*.svg|All files|*.*",
                Multiselect = true
            };
            if (initialDirectory != null)
                dialog.InitialDirectory = initialDirectory;

            if (dialog.ShowDialog() == true)
            {
                var skipped = new List<string>();
                foreach (var file in dialog.FileNames)
                {
                    if (IsSupportedFile(file))
                        LoadMarkdownFile(file);
                    else
                        skipped.Add(Path.GetFileName(file));
                }
                if (skipped.Count > 0)
                    ShowStatusMessage($"Skipped binary: {string.Join(", ", skipped)}");
            }
        }

        #endregion
    }
}
