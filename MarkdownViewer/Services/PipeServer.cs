using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
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

                        var responseJson = JsonSerializer.Serialize(response);
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
        var windows = Application.Current.Windows.OfType<MainWindow>();
        
        switch (message.Command)
        {
            case "open":
                return await HandleOpenAsync(message, windows);

            case "openTemp":
                return await HandleOpenTempAsync(message, windows);

            case "activate":
                return HandleActivate(windows);

            case "getTabs":
                return GetTabsResponse(windows);

            default:
                return new PipeResponse { Success = false, Error = "Unknown command" };
        }
    }

    private async Task<PipeResponse> HandleOpenAsync(PipeMessage message, IEnumerable<MainWindow> windows)
    {
        if (string.IsNullOrEmpty(message.Path) || !File.Exists(message.Path))
        {
            return new PipeResponse { Success = false, Error = "File not found" };
        }

        // Check if file is already open in any window
        foreach (var win in windows)
        {
            var existingTab = win.FindTabByFilePath(message.Path);
            if (existingTab != null)
            {
                win.BringToFront();
                win.SelectTab(existingTab);
                if (message.Line.HasValue)
                {
                    win.ScrollToLine(existingTab, message.Line.Value);
                }
                return new PipeResponse { Success = true };
            }
        }

        // File not open - open in first window
        var window = windows.FirstOrDefault();
        if (window != null)
        {
            var tab = window.LoadMarkdownFile(message.Path, message.Line, message.Title);
            window.BringToFront();
            return await WaitForRenderAsync(tab);
        }

        return new PipeResponse { Success = false, Error = "No window available" };
    }

    private async Task<PipeResponse> HandleOpenTempAsync(PipeMessage message, IEnumerable<MainWindow> windows)
    {
        if (string.IsNullOrEmpty(message.Path) || !File.Exists(message.Path))
        {
            return new PipeResponse { Success = false, Error = "File not found" };
        }

        var window = windows.FirstOrDefault();
        if (window != null)
        {
            var tab = window.LoadMarkdownFile(message.Path, message.Line, message.Title, isTemp: true);
            window.BringToFront();
            return await WaitForRenderAsync(tab);
        }

        return new PipeResponse { Success = false, Error = "No window available" };
    }

    private static PipeResponse HandleActivate(IEnumerable<MainWindow> windows)
    {
        var mainWindow = windows.FirstOrDefault();
        if (mainWindow != null)
        {
            mainWindow.BringToFront();
            return new PipeResponse { Success = true };
        }
        return new PipeResponse { Success = false };
    }

    private static async Task<PipeResponse> WaitForRenderAsync(TabItemData? tab)
    {
        if (tab?.RenderCompletion != null)
        {
            try
            {
                var errors = await tab.RenderCompletion.Task.WaitAsync(TimeSpan.FromSeconds(30));
                return new PipeResponse 
                { 
                    Success = true, 
                    Errors = errors.Count > 0 ? errors.ToArray() : null 
                };
            }
            catch (TimeoutException)
            {
                return new PipeResponse { Success = true, Error = "Render timeout" };
            }
        }

        // Existing tab - return cached errors
        if (tab != null)
        {
            var cachedErrors = tab.LastRenderErrors;
            return new PipeResponse 
            { 
                Success = true, 
                Errors = cachedErrors.Count > 0 ? cachedErrors.ToArray() : null 
            };
        }

        return new PipeResponse { Success = true };
    }

    private static PipeResponse GetTabsResponse(IEnumerable<MainWindow> windows)
    {
        var tabs = new List<TabInfo>();
        var tabIndex = 0;
        var windowIndex = 0;

        foreach (var window in windows)
        {
            var windowTabs = window.GetTabs();
            var selectedIndex = window.GetSelectedTabIndex();

            foreach (var tab in windowTabs)
            {
                tabs.Add(new TabInfo
                {
                    Index = tabIndex++,
                    WindowIndex = windowIndex,
                    Title = tab.Title,
                    Path = tab.FilePath,
                    IsSelected = (windowTabs.IndexOf(tab) == selectedIndex)
                });
            }
            windowIndex++;
        }

        return new PipeResponse { Success = true, Tabs = tabs.ToArray() };
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
    public int? Index { get; set; }
    public int? Line { get; set; }
    public string? Title { get; set; }
}

public class PipeResponse
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public TabInfo[]? Tabs { get; set; }
    public string[]? Errors { get; set; }
}

public class TabInfo
{
    public int Index { get; set; }
    public int WindowIndex { get; set; }
    public string Title { get; set; } = "";
    public string Path { get; set; } = "";
    public bool IsSelected { get; set; }
}

#endregion