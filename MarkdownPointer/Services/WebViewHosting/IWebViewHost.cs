namespace MarkdownPointer.Services.WebViewHosting
{
    /// <summary>
    /// Platform-agnostic contract for the embedded webview that renders content.
    ///
    /// This captures the cross-platform CORE only: lifecycle, script execution,
    /// navigation, and the host message channel. WebView2 (Windows) implements it
    /// today; WKWebView (macOS) and WebKitGTK (Linux) implementations slot in behind
    /// the same interface when the shell moves to Avalonia.
    ///
    /// Platform-specific extras (context menus, print, native image clipboard,
    /// WPF drag/drop) are intentionally NOT here — they stay with the concrete host
    /// or move to optional capability interfaces later.
    /// </summary>
    public interface IWebViewHost
    {
        /// <summary>
        /// The underlying visual element to place in the window's tree.
        /// Typed as object because the element type is framework-bound
        /// (WPF FrameworkElement now, Avalonia Control later); the per-framework
        /// shell casts it at the composition root.
        /// </summary>
        object Control { get; }

        /// <summary>True once the underlying engine is initialized and usable.</summary>
        bool IsReady { get; }

        /// <summary>Initializes the underlying engine and wires its events. Idempotent.</summary>
        Task EnsureReadyAsync();

        /// <summary>Runs script in the page and returns its JSON-encoded result.</summary>
        Task<string> ExecuteScriptAsync(string script);

        /// <summary>Navigates to an absolute URI (e.g., a file:// URL).</summary>
        void Navigate(string uri);

        /// <summary>
        /// Raised with the raw string payload posted from the page via
        /// window.chrome.webview.postMessage (see BridgeShim.js). Replaces the
        /// WebView2-specific WebMessageReceived + TryGetWebMessageAsString.
        /// </summary>
        event Action<string> MessageReceived;

        /// <summary>Raised when a navigation finishes.</summary>
        event Action NavigationCompleted;

        /// <summary>
        /// Raised when the page requests a new window, with the target URI.
        /// The host suppresses the actual new window; the app decides what to do.
        /// </summary>
        event Action<string> NewWindowRequested;
    }
}
