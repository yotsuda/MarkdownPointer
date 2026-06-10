using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform; // ClipboardExtensions.SetTextAsync
using Avalonia.Platform.Storage;
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

    private string? _filePath;             // null => render the built-in sample
    private string _baseDir = AppContext.BaseDirectory;
    private string _refName = "sample.md"; // shown in the copied filepath:line reference
    private double _zoom = 1.0;
    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _reloadDebounce;

    private DispatcherTimer? _smokeTimer;
    private DispatcherTimer? _smokePoll;
    private bool _smokeProbed;
    private bool _smokeBusy;

    public MainWindow()
    {
        InitializeComponent();

        SetFile(App.FilePath);

        _htmlGenerator = new HtmlGenerator(BuildPipeline());

        _webView = new NativeWebView();
        WebViewHostPanel.Children.Add(_webView);

        _host = new AvaloniaWebViewHost(_webView);
        _host.MessageReceived += OnHostMessage;
        _host.NavigationCompleted += OnNavigationCompleted;
        _host.NewWindowRequested += HandleLinkClick;   // target=_blank / window.open

        Loaded += (_, _) => { Render(); SetupWatcher(); };
        Closed += (_, _) => { _watcher?.Dispose(); _reloadDebounce?.Stop(); };

        // Ctrl+O to open a file; drag-and-drop a file onto the window to open it.
        KeyDown += OnKeyDown;
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.O && e.KeyModifiers == KeyModifiers.Control)
        {
            OpenFileDialog();
            e.Handled = true;
        }
    }

    private async void OpenFileDialog()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Markdown",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Markdown") { Patterns = new[] { "*.md", "*.markdown", "*.txt" } },
                FilePickerFileTypes.All,
            },
        });

        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (path is not null) LoadFile(path);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var path = e.DataTransfer.TryGetFile()?.TryGetLocalPath();
        if (path is not null && File.Exists(path)) LoadFile(path);
    }

    private void SetFile(string? path)
    {
        _filePath = path != null && File.Exists(path) ? Path.GetFullPath(path) : null;
        _baseDir = _filePath != null ? Path.GetDirectoryName(_filePath)! : AppContext.BaseDirectory;
        _refName = _filePath ?? "sample.md";
        Title = _filePath != null
            ? $"{Path.GetFileName(_filePath)} — MarkdownPointer"
            : "MarkdownPointer (Avalonia)";
    }

    // Navigate the single window to a different local Markdown file (e.g. a clicked link).
    private void LoadFile(string path)
    {
        _watcher?.Dispose();
        _watcher = null;
        SetFile(path);
        Render();
        SetupWatcher();
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
        else if (message.StartsWith("click:", StringComparison.Ordinal))
        {
            HandleLinkClick(message.Substring("click:".Length));
        }
        else if (message.StartsWith("hover:", StringComparison.Ordinal))
        {
            StatusText.Text = ToDisplayPath(message.Substring("hover:".Length));
        }
        else if (message == "leave:")
        {
            StatusText.Text = "";
        }
        else if (message.StartsWith("zoom:", StringComparison.Ordinal))
        {
            ApplyZoom(message.Substring("zoom:".Length));
        }
    }

    private void HandleLinkClick(string uri)
    {
        if (uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            OpenExternally(uri);
            return;
        }

        if (uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var path = Uri.UnescapeDataString(new Uri(uri).LocalPath);
                if (File.Exists(path) && IsMarkdown(path))
                    LoadFile(path);          // follow local Markdown links in-place
                else
                    OpenExternally(path);    // hand anything else to the OS
            }
            catch
            {
                // ignore malformed file URIs
            }
        }
    }

    private async void ApplyZoom(string direction)
    {
        _zoom = Math.Clamp(_zoom + (direction == "in" ? 0.1 : -0.1), 0.3, 3.0);
        await _host.ExecuteScriptAsync(
            $"document.body.style.zoom='{_zoom.ToString(System.Globalization.CultureInfo.InvariantCulture)}'");
    }

    private static bool IsMarkdown(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".md", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".markdown", StringComparison.OrdinalIgnoreCase);
    }

    private static void OpenExternally(string target)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch
        {
            // no OS handler / blocked — ignore
        }
    }

    private static string ToDisplayPath(string url)
    {
        if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            try { return Uri.UnescapeDataString(new Uri(url).LocalPath); }
            catch { return url; }
        }
        return url;
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
