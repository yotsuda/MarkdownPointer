using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform; // ClipboardExtensions.SetTextAsync
using Avalonia.Threading;
using Markdig;
using MarkdownPointer.Services;             // HtmlGenerator (shared rendering core)
using MarkdownPointer.Services.WebViewHosting;

namespace MarkdownPointer.Avalonia;

public partial class MainWindow : Window
{
    private readonly HtmlGenerator _htmlGenerator;
    private readonly NativeWebView _webView;
    private readonly AvaloniaWebViewHost _host;
    private DispatcherTimer? _smokeTimer;
    private bool _smokeProbed;

    public MainWindow()
    {
        InitializeComponent();

        _htmlGenerator = new HtmlGenerator(BuildPipeline());

        _webView = new NativeWebView();
        WebViewHostPanel.Children.Add(_webView);

        _host = new AvaloniaWebViewHost(_webView);
        _host.MessageReceived += OnHostMessage;
        _host.NavigationCompleted += OnNavigationCompleted;

        Loaded += (_, _) => RenderSample();
    }

    private void OnNavigationCompleted()
    {
        StatusText.Text = "rendered — point at an element (crosshair) to copy a ref";
        if (App.SmokeMode && !_smokeProbed)
        {
            _smokeProbed = true;
            StartSmokeProbe();
        }
    }

    // Headless smoke (--smoke): drive a page->host bridge round-trip on the real native
    // webview. If the host receives the message the JS posted, the JS<->WebKitGTK<->C#
    // bridge works; exit 0. Otherwise time out and exit 1.
    private async void StartSmokeProbe()
    {
        _smokeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _smokeTimer.Tick += (_, _) =>
        {
            Console.Error.WriteLine("SMOKE FAIL: no bridge message within timeout");
            Exit(1);
        };
        _smokeTimer.Start();

        // Diagnostic: report what InvokeScript's JS context actually sees, then (if pointable
        // elements exist) dispatch a real click — the DOM is shared across JS worlds, so this
        // fires the page's own main-world pointing handler -> BridgeShim -> native host.
        const string probe = @"(function(){
            var diag = JSON.stringify({
                rs: document.readyState,
                url: String(location.href).slice(0,80),
                bodyKids: document.body ? document.body.childElementCount : -1,
                allEls: document.querySelectorAll('*').length,
                dataLine: document.querySelectorAll('[data-line]').length,
                htmlLen: document.documentElement ? document.documentElement.outerHTML.length : -1,
                chrome: typeof window.chrome,
                invoke: typeof window.invokeCSharpAction
            });
            var el = document.querySelector('[data-line]');
            if (el) ['mouseover','mousedown','click'].forEach(function(t){
                el.dispatchEvent(new MouseEvent(t, {bubbles:true, cancelable:true, view:window}));
            });
            return diag;
        })()";
        try
        {
            var result = await _host.ExecuteScriptAsync(probe);
            Console.Error.WriteLine("SMOKE DIAG: " + result);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("SMOKE FAIL: InvokeScript threw: " + ex.Message);
            Exit(1);
        }
    }

    private void Exit(int code)
    {
        _smokeTimer?.Stop();
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime life)
            life.Shutdown(code);
        else
            Environment.Exit(code);
    }

    // Mirrors the WPF shell's Markdig pipeline so the shared HtmlGenerator behaves identically.
    private static MarkdownPipeline BuildPipeline() => new MarkdownPipelineBuilder()
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

    private void RenderSample()
    {
        StatusText.Text = "rendering…";
        var html = _htmlGenerator.ConvertToHtml(Sample, AppContext.BaseDirectory);
        _host.NavigateToString(html, new Uri(AppContext.BaseDirectory));
    }

    private async void OnHostMessage(string message)
    {
        if (App.SmokeMode && message.StartsWith("point:", StringComparison.Ordinal))
        {
            Console.Error.WriteLine("SMOKE OK: bridge round-trip received: " + message);
            Exit(0);
            return;
        }

        if (message.StartsWith("point:", StringComparison.Ordinal))
        {
            var data = message.Substring("point:".Length);
            var parts = data.Split('|', 2);
            var line = parts[0];
            var content = parts.Length > 1 ? parts[1] : "";
            var reference = $"[sample.md:{line}] {content}";

            // Copy the filepath:line reference to the OS clipboard — the point-and-prompt payload.
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
                await clipboard.SetTextAsync(reference);

            StatusText.Text = "✓ copied: " + reference;
        }
        else if (message.StartsWith("pointhover:", StringComparison.Ordinal))
        {
            StatusText.Text = "L" + message.Substring("pointhover:".Length);
        }
    }

    private const string Sample = @"# Avalonia shell — cross-platform proof

This window is the **Avalonia** shell driving the shared rendering core
(`MarkdownPointer.Rendering`) through `IWebViewHost`. Click any element to
copy a `filepath:line` reference — same point-and-prompt as the WPF app.

## Table

| Feature | Works here |
|---------|------------|
| Markdown render | ? |
| Mermaid node pointing | ? |
| KaTeX | ? |

## Mermaid (click a node)

```mermaid
flowchart TD
    A[Start] --> B{Decision}
    B -->|yes| C[Do thing]
    B -->|no| D[Other thing]
    C --> E[End]
    D --> E
```

## Math

Inline $E = mc^2$ and a block:

$$\int_0^\infty e^{-x^2}\,dx = \frac{\sqrt{\pi}}{2}$$
";
}
