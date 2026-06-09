using System.ComponentModel;
using System.IO;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;

namespace MarkdownPointer.Models
{
    /// <summary>
    /// Data model for a tab in the Markdown Viewer.
    /// Implements INotifyPropertyChanged for WPF binding.
    /// </summary>
    public class TabItemData : INotifyPropertyChanged, IDisposable
    {
        private string _title = "";

        /// <summary>
        /// Display title for the tab header.
        /// </summary>
        public string Title
        {
            get => _title;
            set
            {
                _title = value;
                OnPropertyChanged(nameof(Title));
            }
        }

        /// <summary>
        /// Full path to the Markdown file.
        /// </summary>
        public string FilePath { get; set; } = "";

        /// <summary>
        /// Original file name for tracking renames during file updates.
        /// </summary>
        public string OriginalFileName { get; set; } = "";

        /// <summary>
        /// WebView2 instance for rendering HTML.
        /// Used directly only for Windows/WebView2-specific features (context menu,
        /// print, image clipboard, WPF drag/drop). Cross-platform core access goes
        /// through <see cref="Host"/>.
        /// </summary>
        public WebView2 WebView { get; set; } = null!;

        /// <summary>
        /// Platform-agnostic webview contract wrapping <see cref="WebView"/>.
        /// Navigation, script execution, and host messages route through this so
        /// the shell can swap in WKWebView/WebKitGTK hosts later.
        /// </summary>
        public Services.WebViewHosting.IWebViewHost Host { get; set; } = null!;

        /// <summary>
        /// File system watcher for auto-reload on file changes.
        /// </summary>
        public FileSystemWatcher? Watcher { get; set; }

        /// <summary>
        /// Debounce timer to prevent rapid re-renders.
        /// </summary>
        public DispatcherTimer? DebounceTimer { get; set; }

        /// <summary>
        /// Whether the WebView2 has been initialized.
        /// </summary>
        public bool IsInitialized { get; set; }

        /// <summary>
        /// Reference to the MainWindow that owns this tab.
        /// Used for routing WebView messages to the correct window.
        /// </summary>
        public object? OwnerWindow { get; set; }

        /// <summary>
        /// Pending line to scroll to after initialization.
        /// </summary>
        public int? PendingScrollLine { get; set; }

        /// <summary>
        /// Last known write time of the file (for change detection).
        /// </summary>
        public DateTime LastFileWriteTime { get; set; }

        /// <summary>
        /// Whether this is a temporary file (e.g., piped content).
        /// </summary>
        public bool IsTemp { get; set; }

        /// <summary>
        /// Whether the source file has been deleted.
        /// </summary>
        public bool IsFileDeleted { get; set; }

        /// <summary>
        /// Saved scroll position for restoring after reload.
        /// </summary>
        public double SavedScrollPosition { get; set; }

        /// <summary>
        /// Saved source line number for position sync across theme/view switches.
        /// </summary>
        public int SavedSourceLine { get; set; }

        /// <summary>
        /// Current CSS zoom factor for this tab.
        /// </summary>
        public double CssZoomFactor { get; set; } = 1.0;

        /// <summary>
        /// Task completion source for waiting on render completion.
        /// </summary>
        public TaskCompletionSource<List<string>>? RenderCompletion { get; set; }

        /// <summary>
        /// Errors from the last Mermaid/KaTeX render.
        /// </summary>
        public List<string> LastRenderErrors { get; set; } = new();

        /// <summary>
        /// Cached rendered HTML for fast window detach/attach.
        /// </summary>
        public string? RenderedHtml { get; set; }

        /// <summary>
        /// Whether to render as slides (true) or as a normal document (false).
        /// </summary>
        public bool IsSlideView { get; set; }

        /// <summary>
        /// Cached slide HTML (Pandoc reveal.js) for instant toggle.
        /// Invalidated when file changes.
        /// </summary>
        public string? CachedSlideHtml { get; set; }

        /// <summary>
        /// Cached Markdig document HTML for instant toggle.
        /// Invalidated when file changes.
        /// </summary>
        public string? CachedDocHtml { get; set; }

        /// <summary>
        /// Temp file path used for NavigateToFile (avoids NavigateToString 2MB limit).
        /// Cleaned up on Dispose.
        /// </summary>
        public string? TempHtmlPath { get; set; }

        /// <summary>
        /// INotifyPropertyChanged implementation.
        /// </summary>
        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        /// <summary>
        /// Disposes all resources associated with this tab.
        /// </summary>
        public void Dispose()
        {
            Watcher?.Dispose();
            DebounceTimer?.Stop();
            WebView?.Dispose();
            CleanupTempFile();
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Deletes the temp HTML file if it exists.
        /// </summary>
        public void CleanupTempFile()
        {
            if (TempHtmlPath != null)
            {
                try { File.Delete(TempHtmlPath); } catch { }
                TempHtmlPath = null;
            }
        }
    }
}
