using Microsoft.Web.WebView2.Wpf;

namespace MarkdownPointer.Services.WebViewHosting
{
    /// <summary>
    /// WebView2 (Windows) implementation of <see cref="IWebViewHost"/>.
    /// Wraps the existing WPF WebView2 control and translates its
    /// WebView2-specific events into the platform-agnostic contract.
    /// </summary>
    public sealed class WebView2WebViewHost : IWebViewHost
    {
        private readonly WebView2 _webView;
        private bool _wired;

        public WebView2WebViewHost(WebView2 webView)
        {
            _webView = webView;
        }

        /// <summary>The wrapped WebView2 control (for the WPF visual tree).</summary>
        public object Control => _webView;

        public bool IsReady => _webView.CoreWebView2 != null;

        public event Action<string>? MessageReceived;
        public event Action? NavigationCompleted;
        public event Action<string>? NewWindowRequested;

        public async Task EnsureReadyAsync()
        {
            if (IsReady) return;

            await _webView.EnsureCoreWebView2Async(await App.GetOrCreateWebView2EnvironmentAsync());

            var settings = _webView.CoreWebView2.Settings;
            settings.AreDefaultContextMenusEnabled = true;
            settings.IsStatusBarEnabled = false;
            settings.AreDevToolsEnabled = false;
            settings.IsWebMessageEnabled = true;
            settings.AreHostObjectsAllowed = false;

            if (!_wired)
            {
                _wired = true;

                _webView.CoreWebView2.WebMessageReceived += (_, e) =>
                {
                    var message = e.TryGetWebMessageAsString();
                    if (!string.IsNullOrEmpty(message))
                        MessageReceived?.Invoke(message);
                };

                _webView.CoreWebView2.NavigationCompleted += (_, _) =>
                    NavigationCompleted?.Invoke();

                _webView.CoreWebView2.NewWindowRequested += (_, e) =>
                {
                    e.Handled = true;
                    NewWindowRequested?.Invoke(e.Uri);
                };
            }
        }

        public Task<string> ExecuteScriptAsync(string script) =>
            _webView.CoreWebView2.ExecuteScriptAsync(script);

        public void Navigate(string uri) =>
            _webView.CoreWebView2.Navigate(uri);
    }
}
