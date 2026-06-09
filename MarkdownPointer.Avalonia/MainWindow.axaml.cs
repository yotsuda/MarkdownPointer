using System;
using Avalonia.Controls;
using Markdig;
using MarkdownPointer.Services;             // HtmlGenerator (shared rendering core)
using MarkdownPointer.Services.WebViewHosting;

namespace MarkdownPointer.Avalonia;

public partial class MainWindow : Window
{
    private readonly HtmlGenerator _htmlGenerator;
    private readonly NativeWebView _webView;
    private readonly AvaloniaWebViewHost _host;

    public MainWindow()
    {
        InitializeComponent();

        _htmlGenerator = new HtmlGenerator(BuildPipeline());

        _webView = new NativeWebView();
        WebViewHostPanel.Children.Add(_webView);

        _host = new AvaloniaWebViewHost(_webView);
        _host.MessageReceived += OnHostMessage;
        _host.NavigationCompleted += () => StatusText.Text = "rendered — point at an element (crosshair) to copy a ref";

        Loaded += (_, _) => RenderSample();
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

    private void OnHostMessage(string message)
    {
        if (message.StartsWith("point:", StringComparison.Ordinal))
        {
            var data = message.Substring("point:".Length);
            var parts = data.Split('|', 2);
            var line = parts[0];
            var content = parts.Length > 1 ? parts[1] : "";
            var reference = $"[sample.md:{line}] {content}";

            // Proves the message flow (shared core -> IWebViewHost -> shell). Writing to the
            // OS clipboard uses Avalonia 12's IClipboard.SetDataAsync(IAsyncDataTransfer) and
            // is wired alongside the real shell, not this proof.
            StatusText.Text = "✓ ref: " + reference;
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
