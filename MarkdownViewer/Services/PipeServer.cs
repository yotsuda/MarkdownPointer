using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using MarkdownViewer.Models;

namespace MarkdownViewer.Services;

/// <summary>
/// Named Pipe server for inter-process communication.
/// Handles commands from other instances and MCP clients.
/// </summary>
public class PipeServer : IDisposable
{
    public const string PipeName = "MarkdownViewer_Pipe";
    private const int BufferSize = 65536; // 64KB for large responses
    
    private CancellationTokenSource? _cts;
    private Task? _serverTask;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
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
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName, 
                    PipeDirection.InOut, 
                    1,
                    PipeTransmissionMode.Byte, 
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(ct);

                var buffer = new byte[BufferSize];
                var bytesRead = await server.ReadAsync(buffer, ct);

                if (bytesRead > 0)
                {
                    var json = Encoding.UTF8.GetString(buffer, 0, bytesRead);
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
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Continue listening on error
            }
        }
    }

    private async Task<PipeResponse> HandleMessageAsync(PipeMessage message)
    {
        var windows = Application.Current.Windows.OfType<MainWindow>().ToList();
        
        switch (message.Command)
        {
            case "open":
                return await HandleOpenAsync(message, windows);

            case "activate":
                return HandleActivate(windows);

            default:
                return new PipeResponse { Success = false, Error = "Unknown command" };
        }
    }

    private async Task<PipeResponse> HandleOpenAsync(PipeMessage message, List<MainWindow> windows)
    {
        if (string.IsNullOrEmpty(message.Path) || !File.Exists(message.Path))
        {
            return new PipeResponse { Success = false, Error = "File not found" };
        }

        TabItemData? openedTab = null;
        MainWindow? targetWindow = null;
        int targetWindowIndex = 0;

        // Check if file is already open in any window
        for (int i = 0; i < windows.Count; i++)
        {
            var existingTab = windows[i].FindTabByFilePath(message.Path);
            if (existingTab != null)
            {
                windows[i].BringToFront();
                windows[i].SelectTab(existingTab);
                if (message.Line.HasValue)
                {
                    windows[i].ScrollToLine(existingTab, message.Line.Value);
                }
                openedTab = existingTab;
                targetWindow = windows[i];
                targetWindowIndex = i;
                break;
            }
        }

        // File not open - open in first window
        if (openedTab == null)
        {
            targetWindow = windows.FirstOrDefault();
            if (targetWindow != null)
            {
                openedTab = targetWindow.LoadMarkdownFile(message.Path, message.Line, message.Title);
                targetWindow.BringToFront();
                targetWindowIndex = 0;
            }
        }

        if (openedTab == null || targetWindow == null)
        {
            return new PipeResponse { Success = false, Error = "No window available" };
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
        return BuildFullResponse(openedTab, targetWindow, targetWindowIndex, windows);
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
    /// Send a message to an existing MarkdownViewer instance.
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
    public int? Line { get; set; }
    public string? Title { get; set; }
}

public class PipeResponse
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public OpenedTabInfo? OpenedTab { get; set; }
    public WindowInfo[]? Windows { get; set; }
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
    public string[]? Errors { get; set; }
}

#endregion
