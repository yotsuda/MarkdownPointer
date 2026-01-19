using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using MarkdownViewer.Models;

namespace MarkdownViewer
{
    public partial class App : Application
    {
        private const string PipeName = "MarkdownViewer_Pipe";
        private const string MutexName = "MarkdownViewer_SingleInstance";
        private Mutex? _mutex;
        private CancellationTokenSource? _pipeServerCts;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Try to acquire mutex for single instance
            _mutex = new Mutex(true, MutexName, out bool createdNew);

            if (!createdNew)
            {
                // Another instance is running - send file path and exit
                if (e.Args.Length > 0)
                {
                    SendToExistingInstance(new PipeMessage { Command = "open", Path = e.Args[0] });
                }
                else
                {
                    SendToExistingInstance(new PipeMessage { Command = "activate" });
                }
                Shutdown();
                return;
            }

            // Start pipe server
            _pipeServerCts = new CancellationTokenSource();
            Task.Run(() => RunPipeServer(_pipeServerCts.Token));

            // Create main window
            var mainWindow = new MainWindow();
            if (e.Args.Length > 0 && File.Exists(e.Args[0]))
            {
                mainWindow.LoadMarkdownFile(e.Args[0]);
            }
            mainWindow.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _pipeServerCts?.Cancel();
            _mutex?.ReleaseMutex();
            _mutex?.Dispose();
            base.OnExit(e);
        }

        private void SendToExistingInstance(PipeMessage message)
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

        private async Task RunPipeServer(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, 
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    
                    await server.WaitForConnectionAsync(ct);
                    
                    var buffer = new byte[4096];
                    var bytesRead = await server.ReadAsync(buffer, 0, buffer.Length, ct);
                    
                    if (bytesRead > 0)
                    {
                        var json = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                        var message = JsonSerializer.Deserialize<PipeMessage>(json);
                        
                        if (message != null)
                        {
                            var response = await Dispatcher.InvokeAsync(() => HandlePipeMessageAsync(message)).Task.Unwrap();
                            
                            // Send response
                            var responseJson = JsonSerializer.Serialize(response);
                            var responseBytes = Encoding.UTF8.GetBytes(responseJson);
                            await server.WriteAsync(responseBytes, 0, responseBytes.Length, ct);
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

        private async Task<PipeResponse> HandlePipeMessageAsync(PipeMessage message)
        {
            switch (message.Command)
            {
                case "open":
                    if (!string.IsNullOrEmpty(message.Path) && File.Exists(message.Path))
                    {
                        // First, check if file is already open in any window
                        foreach (var win in Windows.OfType<MainWindow>())
                        {
                            var existingTab = win.FindTabByFilePath(message.Path);
                            if (existingTab != null)
                            {
                                // Bring window to front first, then select tab
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
                        var window = Windows.OfType<MainWindow>().FirstOrDefault();
                        if (window != null)
                        {
                            var tab = window.LoadMarkdownFile(message.Path, message.Line, message.Title);
                            window.BringToFront();
                            return await WaitForRenderAsync(tab);
                        }
                    }
                    return new PipeResponse { Success = false, Error = "File not found" };
                    
                case "openTemp":
                    if (!string.IsNullOrEmpty(message.Path) && File.Exists(message.Path))
                    {
                        var window = Windows.OfType<MainWindow>().FirstOrDefault();
                        if (window != null)
                        {
                            var tab = window.LoadMarkdownFile(message.Path, message.Line, message.Title, isTemp: true);
                            window.BringToFront();
                            return await WaitForRenderAsync(tab);
                        }
                    }
                    return new PipeResponse { Success = false, Error = "File not found" };
                    
                case "activate":
                    var mainWindow = Windows.OfType<MainWindow>().FirstOrDefault();
                    if (mainWindow != null)
                    {
                        mainWindow.BringToFront();
                        return new PipeResponse { Success = true };
                    }
                    return new PipeResponse { Success = false };
                    
                case "getTabs":
                    return GetTabsResponse();
                    
                default:
                    return new PipeResponse { Success = false, Error = "Unknown command" };
            }
        }
        
        private async Task<PipeResponse> WaitForRenderAsync(TabItemData? tab)
        {
            if (tab is null)
            {
                return new PipeResponse { Success = true };
            }

            if (tab.RenderCompletion != null)
            {
                try
                {
                    var errors = await tab.RenderCompletion.Task.WaitAsync(TimeSpan.FromSeconds(30));
                    return new PipeResponse { Success = true, Errors = errors.Count > 0 ? errors.ToArray() : null };
                }
                catch (TimeoutException)
                {
                    return new PipeResponse { Success = true, Error = "Render timeout" };
                }
            }

            // Existing tab - return cached errors
            var cachedErrors = tab.LastRenderErrors;
            return new PipeResponse { Success = true, Errors = cachedErrors.Count > 0 ? cachedErrors.ToArray() : null };
        }

        private PipeResponse GetTabsResponse()
        {
            var tabs = new System.Collections.Generic.List<TabInfo>();
            var tabIndex = 0;
            var windowIndex = 0;
            
            foreach (var window in Windows.OfType<MainWindow>())
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
            
            return new PipeResponse 
            { 
                Success = true, 
                Tabs = tabs.ToArray() 
            };
        }

        private class PipeMessage
        {
            public string Command { get; set; } = "";
            public string? Path { get; set; }
            public int? Index { get; set; }
            public int? Line { get; set; }
            public string? Title { get; set; }
        }
        
        private class PipeResponse
        {
            public bool Success { get; set; }
            public string? Error { get; set; }
            public TabInfo[]? Tabs { get; set; }
            public string[]? Errors { get; set; }
        }
        
        private class TabInfo
        {
            public int Index { get; set; }
            public int WindowIndex { get; set; }
            public string Title { get; set; } = "";
            public string Path { get; set; } = "";
            public bool IsSelected { get; set; }
        }
    }
}