using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
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

        private const double ZoomStep = 0.01;
        private const double MinZoom = 0.25;
        private const double MaxZoom = 5.0;
        private const double BaseContentWidth = 1060.0;
        private const double ScrollbarWidth = 20.0;
        private const double MinWindowWidth = 400.0;

        private static readonly string[] SupportedExtensions = { ".md", ".markdown", ".txt" };

        #endregion

        #region Fields

        private readonly MarkdownPipeline _pipeline;
        private readonly HtmlGenerator _htmlGenerator;
        private readonly ClipboardService _clipboardService;
        private readonly ObservableCollection<TabItemData> _tabs = new();

        // Zoom state
        private DispatcherTimer? _zoomAnimationTimer;
        private double _lastZoomFactor = 1.0;
        private double _targetZoomFactor = 1.0;

        // Mode toggles
        private bool _isDragMoveMode = false;
        private bool _isPointingMode = true;
        private bool _pointingModeBeforeSvg = true;

        // UI state
        private DispatcherTimer? _statusMessageTimer;
        private DispatcherTimer? _statusBlinkTimer;
        private Point _contextMenuPosition;

        // Document scroll state (for drag mode)
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
        private int _tabDropTargetIndex = -1;

        #endregion

        #region Constructor

        public MainWindow()
        {
            InitializeComponent();

            // Configure Markdig pipeline
            // Note: UseDiagrams() is excluded - we have custom Mermaid handling in LineTrackingCodeBlockRenderer
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

        #region File Dialog

        private void OpenFileDialog()
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Markdown files|*.md;*.markdown;*.txt|All files|*.*",
                Multiselect = true
            };

            if (dialog.ShowDialog() == true)
            {
                foreach (var file in dialog.FileNames)
                {
                    LoadMarkdownFile(file);
                }
            }
        }

        #endregion
    }
}
