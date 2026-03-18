using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using MarkdownPointer.Models;

namespace MarkdownPointer.Services;

/// <summary>
/// Named Pipe server for inter-process communication.
/// Handles commands from other instances and MCP clients.
/// </summary>
public class PipeServer : IDisposable
{
    public const string PipeName = "MarkdownPointer_Pipe";
    private const int BufferSize = 65536; // 64KB for large responses
    
    private CancellationTokenSource? _cts;
    private Task? _serverTask;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _serverTask = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _serverTask?.Wait(TimeSpan.FromSeconds(2));
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(
                    PipeName, 
                    PipeDirection.InOut, 
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, 
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(ct);

                // Handle connection in a separate task so we can accept the next one immediately
                _ = HandleConnectionAsync(server, ct);
                server = null; // Ownership transferred to HandleConnectionAsync
            }
            catch (OperationCanceledException)
            {
                server?.Dispose();
                break;
            }
            catch
            {
                server?.Dispose();
            }
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        try
        {
            using (server)
            {
                // Read full message (may arrive in multiple chunks)
                using var ms = new MemoryStream();
                var buffer = new byte[BufferSize];
                int bytesRead;
                do
                {
                    bytesRead = await server.ReadAsync(buffer, ct);
                    if (bytesRead > 0)
                        ms.Write(buffer, 0, bytesRead);
                } while (bytesRead == buffer.Length);

                if (ms.Length > 0)
                {
                    var json = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                    var message = JsonSerializer.Deserialize<PipeMessage>(json);

                    if (message != null)
                    {
                        var response = await Application.Current.Dispatcher.InvokeAsync(
                            () => HandleMessageAsync(message)).Task.Unwrap();

                        var responseJson = JsonSerializer.Serialize(response, JsonOptions);
                        var responseBytes = Encoding.UTF8.GetBytes(responseJson);
                        await server.WriteAsync(responseBytes, ct);
                    }
                }
            }
        }
        catch
        {
            // Ignore errors in individual connections
        }
    }

    private async Task<PipeResponse> HandleMessageAsync(PipeMessage message)
    {
        // Ensure at least one window exists (may be first command after lazy startup)
        ((App)Application.Current).EnsureMainWindow();
        var windows = Application.Current.Windows.OfType<MainWindow>().ToList();

        switch (message.Command)
        {
            case "open":
                return await HandleOpenAsync(message, windows);

            case "openTemp":
                return await HandleOpenTempAsync(message, windows);

            case "activate":
                return HandleActivate(windows);

            case "status":
                return await HandleStatusAsync(windows);

            case "slideControl":
                return await HandleSlideControlAsync(message, windows);

            default:
                return new PipeResponse { Success = false, Error = "Unknown command" };
        }
    }

    private async Task<PipeResponse> HandleOpenAsync(PipeMessage message, List<MainWindow> windows)
    {
        // Collect all paths to open
        var paths = new List<string>();
        if (message.Paths is { Length: > 0 })
        {
            paths.AddRange(message.Paths);
        }
        else if (!string.IsNullOrEmpty(message.Path))
        {
            paths.Add(message.Path);
        }

        if (paths.Count == 0 || !paths.Any(File.Exists))
        {
            return new PipeResponse { Success = false, Error = "File not found" };
        }

        TabItemData? openedTab = null;
        MainWindow? targetWindow = null;
        int targetWindowIndex = 0;

        foreach (var path in paths)
        {
            if (!File.Exists(path)) continue;

            TabItemData? tab = null;
            MainWindow? win = null;
            int winIndex = 0;

            // Check if file is already open in any window
            for (int i = 0; i < windows.Count; i++)
            {
                var existingTab = windows[i].FindTabByFilePath(path);
                if (existingTab != null)
                {
                    windows[i].SelectTab(existingTab);
                    if (message.Line.HasValue)
                    {
                        windows[i].ScrollToLine(existingTab, message.Line.Value);
                    }
                    tab = existingTab;
                    win = windows[i];
                    winIndex = i;
                    break;
                }
            }

            // File not open - open in first window
            if (tab == null)
            {
                win = windows.FirstOrDefault();
                if (win != null)
                {
                    tab = win.LoadMarkdownFile(path, message.Line, message.Title);
                    winIndex = 0;
                }
            }

            // Track last opened tab for response
            if (tab != null)
            {
                openedTab = tab;
                targetWindow = win;
                targetWindowIndex = winIndex;
            }
        }

        targetWindow?.BringToFront();

        if (openedTab == null || targetWindow == null)
        {
            return new PipeResponse { Success = false, Error = "No window available" };
        }

        // Switch to slide view if requested
        if (message.SlideView == true && !openedTab.IsSlideView)
        {
            targetWindow.SetSlideView(openedTab, true);
        }

        // Wait for render if it's a new tab
        if (openedTab.RenderCompletion != null)
        {
            try
            {
                await openedTab.RenderCompletion.Task.WaitAsync(TimeSpan.FromSeconds(30));
            }
            catch (TimeoutException)
            {
                // Timeout is recorded in LastRenderErrors
            }

            // Scroll to line after render completes
            if (message.Line.HasValue)
            {
                targetWindow.ScrollToLine(openedTab, message.Line.Value);
            }
        }

        // Build response with all window/tab info
        var response = BuildFullResponse(openedTab, targetWindow, targetWindowIndex, windows);

        // Include slide state if in slide view
        if (openedTab.IsSlideView)
        {
            response.SlideState = await targetWindow.GetSlideStateAsync(openedTab);
        }

        return response;
    }

    private async Task<PipeResponse> HandleOpenTempAsync(PipeMessage message, List<MainWindow> windows)
    {
        if (string.IsNullOrEmpty(message.Path) || !File.Exists(message.Path))
        {
            return new PipeResponse { Success = false, Error = "File not found" };
        }

        var targetWindow = windows.FirstOrDefault();
        if (targetWindow == null)
        {
            return new PipeResponse { Success = false, Error = "No window available" };
        }

        var openedTab = targetWindow.LoadMarkdownFile(message.Path, message.Line, message.Title, isTemp: true);
        targetWindow.BringToFront();

        if (openedTab == null)
        {
            return new PipeResponse { Success = false, Error = "Failed to open temp file" };
        }

        // Wait for render
        if (openedTab.RenderCompletion != null)
        {
            try
            {
                await openedTab.RenderCompletion.Task.WaitAsync(TimeSpan.FromSeconds(30));
            }
            catch (TimeoutException)
            {
            }

            if (message.Line.HasValue)
            {
                targetWindow.ScrollToLine(openedTab, message.Line.Value);
            }
        }

        return BuildFullResponse(openedTab, targetWindow, 0, windows);
    }

    private static PipeResponse BuildFullResponse(
        TabItemData openedTab, 
        MainWindow targetWindow, 
        int targetWindowIndex,
        List<MainWindow> windows)
    {
        var windowInfos = new List<WindowInfo>();
        OpenedTabInfo? openedTabInfo = null;

        for (int winIdx = 0; winIdx < windows.Count; winIdx++)
        {
            var window = windows[winIdx];
            var tabs = window.GetTabs();
            var selectedIndex = window.GetSelectedTabIndex();
            var tabInfos = new List<TabInfo>();

            for (int tabIdx = 0; tabIdx < tabs.Count; tabIdx++)
            {
                var tab = tabs[tabIdx];
                var errors = tab.LastRenderErrors.Count > 0 ? tab.LastRenderErrors.ToArray() : null;
                var isSelected = tabIdx == selectedIndex;

                tabInfos.Add(new TabInfo
                {
                    Index = tabIdx,
                    Title = tab.Title,
                    Path = tab.FilePath,
                    IsSelected = isSelected,
                    IsSlideView = tab.IsSlideView,
                    Errors = errors
                });

                // Capture opened tab info
                if (tab == openedTab)
                {
                    openedTabInfo = new OpenedTabInfo
                    {
                        WindowIndex = winIdx,
                        TabIndex = tabIdx,
                        Title = tab.Title,
                        Path = tab.FilePath
                    };
                }
            }

            windowInfos.Add(new WindowInfo
            {
                Index = winIdx,
                Tabs = tabInfos.ToArray()
            });
        }

        return new PipeResponse
        {
            Success = true,
            OpenedTab = openedTabInfo,
            Windows = windowInfos.ToArray()
        };
    }

    private static async Task<PipeResponse> HandleSlideControlAsync(PipeMessage message, List<MainWindow> windows)
    {
        var action = message.SlideAction;
        if (string.IsNullOrEmpty(action))
        {
            return new PipeResponse { Success = false, Error = "SlideAction is required" };
        }

        // Find the active tab in slide view
        MainWindow? targetWindow = null;
        TabItemData? activeTab = null;
        foreach (var win in windows)
        {
            if (win.FileTabControl.SelectedItem is TabItemData tab && tab.IsSlideView)
            {
                targetWindow = win;
                activeTab = tab;
                break;
            }
        }

        if (targetWindow == null || activeTab == null)
        {
            return new PipeResponse { Success = false, Error = "No slide view is active" };
        }

        // Execute slide action via reveal.js
        var jsAction = action switch
        {
            "next" => "Reveal.next()",
            "prev" => "Reveal.prev()",
            "goto" => $"Reveal.slide({message.SlideIndex ?? 0})",
            "first" => "Reveal.slide(0)",
            "last" => "Reveal.slide(Reveal.getTotalSlides() - 1)",
            _ => null
        };

        if (jsAction == null)
        {
            return new PipeResponse { Success = false, Error = $"Unknown slide action: {action}" };
        }

        if (activeTab.IsInitialized && activeTab.WebView.CoreWebView2 != null)
        {
            await activeTab.WebView.CoreWebView2.ExecuteScriptAsync(jsAction);
            // Small delay for reveal.js to update state
            await Task.Delay(100);
        }

        var slideState = await targetWindow.GetSlideStateAsync(activeTab);
        return new PipeResponse { Success = true, SlideState = slideState };
    }

    private static async Task<PipeResponse> HandleStatusAsync(List<MainWindow> windows)
    {
        var windowInfos = new List<WindowInfo>();
        SlideStateInfo? slideState = null;

        for (int winIdx = 0; winIdx < windows.Count; winIdx++)
        {
            var window = windows[winIdx];
            var tabs = window.GetTabs();
            var selectedIndex = window.GetSelectedTabIndex();
            var tabInfos = new List<TabInfo>();

            for (int tabIdx = 0; tabIdx < tabs.Count; tabIdx++)
            {
                var tab = tabs[tabIdx];
                var tabInfo = new TabInfo
                {
                    Index = tabIdx,
                    Title = tab.Title,
                    Path = tab.FilePath,
                    IsSelected = tabIdx == selectedIndex,
                    IsSlideView = tab.IsSlideView,
                    Errors = tab.LastRenderErrors.Count > 0 ? tab.LastRenderErrors.ToArray() : null
                };

                if (tabIdx == selectedIndex)
                {
                    // Get current visible line for the selected tab
                    if (tab.IsInitialized && tab.WebView.CoreWebView2 != null)
                    {
                        try
                        {
                            var lineJs = await tab.WebView.CoreWebView2.ExecuteScriptAsync(
                                "(function(){var els=document.querySelectorAll('[data-line]');" +
                                "for(var i=0;i<els.length;i++){var r=els[i].getBoundingClientRect();" +
                                "if(r.bottom>0&&r.top<window.innerHeight)return parseInt(els[i].getAttribute('data-line'));}" +
                                "return null;})()");
                            if (lineJs != "null" && int.TryParse(lineJs, out var line))
                                tabInfo.CurrentLine = line;
                        }
                        catch { }
                    }

                    if (tab.IsSlideView)
                    {
                        slideState = await window.GetSlideStateAsync(tab);
                    }
                }

                tabInfos.Add(tabInfo);
            }

            windowInfos.Add(new WindowInfo { Index = winIdx, Tabs = tabInfos.ToArray() });
        }

        return new PipeResponse
        {
            Success = true,
            Windows = windowInfos.ToArray(),
            SlideState = slideState
        };
    }

    private static PipeResponse HandleActivate(List<MainWindow> windows)
    {
        var mainWindow = windows.FirstOrDefault();
        if (mainWindow != null)
        {
            mainWindow.BringToFront();
            return new PipeResponse { Success = true };
        }
        return new PipeResponse { Success = false, Error = "No window available" };
    }

    /// <summary>
    /// Send a message to an existing MarkdownPointer instance.
    /// </summary>
    public static void SendToExistingInstance(PipeMessage message)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            client.Connect(1000);

            var json = JsonSerializer.Serialize(message);
            var bytes = Encoding.UTF8.GetBytes(json);
            client.Write(bytes, 0, bytes.Length);
            client.Flush();

            // Read response so the server-side write completes before pipe is closed
            var buffer = new byte[BufferSize];
            _ = client.Read(buffer, 0, buffer.Length);
        }
        catch
        {
            // Ignore errors when sending to existing instance
        }
    }
}

#region DTO Classes

public class PipeMessage
{
    public string Command { get; set; } = "";
    public string? Path { get; set; }
    public string[]? Paths { get; set; }
    public int? Line { get; set; }
    public string? Title { get; set; }
    public bool? SlideView { get; set; }
    public string? SlideAction { get; set; }
    public int? SlideIndex { get; set; }
}

public class PipeResponse
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public OpenedTabInfo? OpenedTab { get; set; }
    public WindowInfo[]? Windows { get; set; }
    public SlideStateInfo? SlideState { get; set; }
}

public class SlideStateInfo
{
    public int CurrentIndex { get; set; }
    public int TotalSlides { get; set; }
    public string CurrentContent { get; set; } = "";
    public string? NextContent { get; set; }
    public bool Overflowed { get; set; }
}

public class OpenedTabInfo
{
    public int WindowIndex { get; set; }
    public int TabIndex { get; set; }
    public string Title { get; set; } = "";
    public string Path { get; set; } = "";
}

public class WindowInfo
{
    public int Index { get; set; }
    public TabInfo[] Tabs { get; set; } = Array.Empty<TabInfo>();
}

public class TabInfo
{
    public int Index { get; set; }
    public string Title { get; set; } = "";
    public string Path { get; set; } = "";
    public bool IsSelected { get; set; }
    public bool IsSlideView { get; set; }
    public int? CurrentLine { get; set; }
    public string[]? Errors { get; set; }
}

#endregion
