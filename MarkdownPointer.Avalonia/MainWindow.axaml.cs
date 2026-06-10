using System;
using System.IO;
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

    private readonly string? _filePath;   // null => render the built-in sample
    private readonly string _baseDir;
    private readonly string _refName;      // shown in the copied filepath:line reference
    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _reloadDebounce;

    private DispatcherTimer? _smokeTimer;
    private DispatcherTimer? _smokePoll;
    private bool _smokeProbed;
    private bool _smokeBusy;

    public MainWindow()
    {
        InitializeComponent();

        _filePath = App.FilePath != null && File.Exists(App.FilePath) ? Path.GetFullPath(App.FilePath) : null;
        _baseDir = _filePath != null ? Path.GetDirectoryName(_filePath)! : AppContext.BaseDirectory;
        _refName = _filePath ?? "sample.md";
        Title = _filePath != null
            ? $"{Path.GetFileName(_filePath)} — MarkdownPointer"
            : "MarkdownPointer (Avalonia)";

        _htmlGenerator = new HtmlGenerator(BuildPipeline());

        _webView = new NativeWebView();
        WebViewHostPanel.Children.Add(_webView);

        _host = new AvaloniaWebViewHost(_webView);
        _host.MessageReceived += OnHostMessage;
        _host.NavigationCompleted += OnNavigationCompleted;

        Loaded += (_, _) => { Render(); SetupWatcher(); };
        Closed += (_, _) => { _watcher?.Dispose(); _reloadDebounce?.Stop(); };
    }

    private void Render()
    {
        string markdown;
        if (_filePath != null)
        {
            var read = TryRead(_filePath);
            if (read == null) { _reloadDebounce?.Start(); return; } // mid-write; retry shortly
            markdown = read;
        }
        else
        {
            markdown = Sample;
        }

        StatusText.Text = _filePath != null ? $"rendering {Path.GetFileName(_filePath)}…" : "rendering…";
        var html = _htmlGenerator.ConvertToHtml(markdown, _baseDir);
        var sep = Path.DirectorySeparatorChar;
        _host.NavigateToString(html, new Uri(_baseDir.EndsWith(sep) ? _baseDir : _baseDir + sep));
    }

    // Tolerates the file being mid-write (returns null to retry) and a concurrent writer (ReadWrite share).
    private static string? TryRead(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            return reader.ReadToEnd();
        }
        catch (IOException)
        {
            return null;
        }
    }

    // Live reload: re-render when the file changes (debounced to coalesce rapid editor saves).
    private void SetupWatcher()
    {
        if (_filePath == null) return;

        _reloadDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _reloadDebounce.Tick += (_, _) =>
        {
            _reloadDebounce!.Stop();
            Render();
            StatusText.Text = $"reloaded {Path.GetFileName(_filePath)}";
        };

        _watcher = new FileSystemWatcher(_baseDir, Path.GetFileName(_filePath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        void Bump(object? _, FileSystemEventArgs __) =>
            Dispatcher.UIThread.Post(() => { _reloadDebounce!.Stop(); _reloadDebounce.Start(); });
        _watcher.Changed += Bump;
        _watcher.Created += Bump;
        _watcher.Renamed += (s, e) => Bump(s, e);
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
    // On WebKitGTK, NavigationCompleted fires while the document is still "loading" (body not
    // yet parsed), so poll until the rendered DOM has pointable [data-line] elements, then
    // dispatch a real click. The DOM is shared across JS worlds, so the click fires the page's
    // own main-world pointing handler -> BridgeShim -> native host (the genuine path).
    private void StartSmokeProbe()
    {
        _smokeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _smokeTimer.Tick += (_, _) =>
        {
            Console.Error.WriteLine("SMOKE FAIL: no bridge message within timeout");
            Exit(1);
        };
        _smokeTimer.Start();

        _smokePoll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _smokePoll.Tick += OnSmokePoll;
        _smokePoll.Start();
    }

    private const string SmokeProbeJs = @"(function(){
        if (document.readyState === 'loading') return 'waiting';
        if (document.querySelectorAll('[data-line]').length === 0) return 'waiting';
        var el = document.querySelector('[data-line]');
        ['mouseover','mousedown','click'].forEach(function(t){
            el.dispatchEvent(new MouseEvent(t, {bubbles:true, cancelable:true, view:window}));
        });
        return 'dispatched';
    })()";

    private async void OnSmokePoll(object? sender, EventArgs e)
    {
        if (_smokeBusy) return;
        _smokeBusy = true;
        try
        {
            var result = await _host.ExecuteScriptAsync(SmokeProbeJs);
            if (result != null && result.Contains("dispatched"))
            {
                _smokePoll?.Stop();
                Console.Error.WriteLine("SMOKE: dispatched click on a pointable element");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("SMOKE probe error (retrying): " + ex.Message);
        }
        finally
        {
            _smokeBusy = false;
        }
    }

    private void Exit(int code)
    {
        _smokeTimer?.Stop();
        _smokePoll?.Stop();
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
            var reference = $"[{_refName}:{line}] {content}";

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
