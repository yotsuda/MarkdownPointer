using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace MarkdownPointer.Avalonia;

public partial class App : Application
{
    /// <summary>
    /// CI/headless smoke mode (--smoke): render, drive a page->host bridge round-trip,
    /// then exit 0 on success / 1 on timeout. Lets a Linux CI runner verify the native
    /// WebKitGTK webview actually renders and the point-and-prompt bridge works.
    /// </summary>
    public static bool SmokeMode { get; set; }

    /// <summary>Markdown file passed on the command line (first non-flag arg), or null.</summary>
    public static string? FilePath { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}