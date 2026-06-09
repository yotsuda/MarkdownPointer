// Host bridge shim.
//
// All rendered-content -> host messages go through window.chrome.webview.postMessage(string).
// On Windows (WebView2) that object is provided natively, so this shim is a no-op there.
// On macOS (WKWebView) and Linux (WebKitGTK) there is no window.chrome.webview, but both
// expose window.webkit.messageHandlers; the host registers a 'mdp' handler. This shim
// defines window.chrome.webview.postMessage so the ~9 existing call sites work unchanged
// across every platform.
(function () {
    // WebView2 (Windows): native bridge present — leave it alone.
    if (window.chrome && window.chrome.webview && typeof window.chrome.webview.postMessage === 'function') {
        return;
    }

    // WebKit (macOS WKWebView / Linux WebKitGTK): forward to the registered 'mdp' handler.
    var wk = window.webkit && window.webkit.messageHandlers && window.webkit.messageHandlers.mdp;
    var post = wk
        ? function (msg) { window.webkit.messageHandlers.mdp.postMessage(msg); }
        : function (msg) { if (window.console && console.log) { console.log('[mdp:nohost]', msg); } };

    window.chrome = window.chrome || {};
    window.chrome.webview = { postMessage: post };
})();
