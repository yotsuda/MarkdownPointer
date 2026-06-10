// Host bridge shim.
//
// All rendered-content -> host messages go through window.chrome.webview.postMessage(string).
// That object is native under WebView2 (the WPF app, and Avalonia-on-Windows), so this shim
// is a no-op there. Under the Avalonia shell on macOS (WKWebView) / Linux (WebKitGTK) there is
// no window.chrome.webview; Avalonia.Controls.WebView instead injects window.invokeCSharpAction
// (which routes to its webkit.messageHandlers.postAvWebViewMessage handler). This shim defines
// window.chrome.webview.postMessage on top of that, so the existing call sites work unchanged
// across every platform.
(function () {
    // WebView2 (Windows, WPF app or Avalonia): native bridge present — leave it alone.
    if (window.chrome && window.chrome.webview && typeof window.chrome.webview.postMessage === 'function') {
        return;
    }

    // WebKit via the Avalonia shell. Resolve the route lazily at call time: the host's
    // injected globals may not exist yet when this shim runs in <head>.
    function post(msg) {
        // Avalonia's unified bridge (present on every Avalonia webview backend).
        if (typeof window.invokeCSharpAction === 'function') {
            window.invokeCSharpAction(msg);
            return;
        }
        // Fall back to the raw WebKit handler Avalonia registers.
        var mh = window.webkit && window.webkit.messageHandlers;
        if (mh && mh.postAvWebViewMessage) {
            mh.postAvWebViewMessage.postMessage(msg);
            return;
        }
        // No host (e.g. opened in a plain browser).
        if (window.console && console.log) { console.log('[mdp:nohost]', msg); }
    }

    window.chrome = window.chrome || {};
    window.chrome.webview = { postMessage: post };
})();
