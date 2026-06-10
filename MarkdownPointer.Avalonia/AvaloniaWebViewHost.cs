using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using MarkdownPointer.Services.WebViewHosting;

namespace MarkdownPointer.Avalonia;

/// <summary>
/// Avalonia (native per-OS webview) implementation of <see cref="IWebViewHost"/>.
/// Wraps Avalonia.Controls.NativeWebView, which is WebView2 on Windows,
/// WKWebView on macOS, and WebKitGTK on Linux — the same contract the WPF
/// WebView2WebViewHost satisfies, so the shared rendering core drives both shells.
/// </summary>
public sealed class AvaloniaWebViewHost : IWebViewHost
{
    private readonly NativeWebView _webView;

    public AvaloniaWebViewHost(NativeWebView webView)
    {
        _webView = webView;

        // window.chrome.webview.postMessage(...) on the page surfaces here as e.Body.
        _webView.WebMessageReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Body))
                MessageReceived?.Invoke(e.Body);
        };

        _webView.NavigationCompleted += (_, _) => NavigationCompleted?.Invoke();

        // target=_blank / window.open: suppress the popup and let the app decide
        // (open in the OS browser, or follow a local Markdown link in-place).
        _webView.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (e.Request is { } uri)
                NewWindowRequested?.Invoke(uri.ToString());
        };
    }

    public object Control => _webView;

    // NativeWebView is usable once attached/loaded; the shell renders after Loaded.
    public bool IsReady => true;

    public Task EnsureReadyAsync() => Task.CompletedTask;

    public async Task<string> ExecuteScriptAsync(string script) =>
        await _webView.InvokeScript(script) ?? string.Empty;

    /// <summary>Renders HTML directly (no temp file; NativeWebView has no NavigateToString size limit concern here).</summary>
    public void NavigateToString(string html, Uri baseUri) => _webView.NavigateToString(html, baseUri);

    public void Navigate(string uri) => _webView.Navigate(new Uri(uri));

    public event Action<string>? MessageReceived;
    public event Action? NavigationCompleted;
    public event Action<string>? NewWindowRequested;
}
