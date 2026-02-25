using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using MarkdownPointer.Services;

namespace MarkdownPointer
{
    public partial class App : Application
    {
        private const string MutexName = "MarkdownPointer_SingleInstance_v2";
        private Mutex? _mutex;
        private PipeServer? _pipeServer;

        /// <summary>
        /// Pre-created WebView2 environment, shared by all tabs.
        /// Created early in startup so the first tab doesn't pay the full init cost.
        /// </summary>
        internal static CoreWebView2Environment? PreCreatedWebView2Environment { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            // Try to acquire mutex for single instance
            _mutex = new Mutex(true, MutexName, out bool createdNew);

            if (!createdNew)
            {
                // Another instance is running - send file paths and exit
                if (e.Args.Length > 0)
                {
                    PipeServer.SendToExistingInstance(new PipeMessage { Command = "open", Paths = e.Args });
                }
                else
                {
                    PipeServer.SendToExistingInstance(new PipeMessage { Command = "activate" });
                }
                Environment.Exit(0);
                return;
            }

            base.OnStartup(e);

            // Start pipe server FIRST so MCP can connect immediately
            _pipeServer = new PipeServer();
            _pipeServer.Start();

            // Pre-initialize WebView2 environment in background.
            // This runs the slow native init while the window is being created,
            // so EnsureCoreWebView2Async can reuse the cached environment.
            _ = Task.Run(async () =>
            {
                try
                {
                    var userDataFolder = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "MarkdownPointer");
                    PreCreatedWebView2Environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                }
                catch
                {
                    // Non-fatal: tabs will create their own environment on demand
                }
            });

            // Defer MainWindow creation by 300ms to give MCP commands
            // a chance to arrive first (MCP probe takes ~100-200ms).
            // If an MCP command arrives, EnsureMainWindow() in the pipe handler
            // creates the window immediately, and the timer becomes a no-op.
            var args = e.Args;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                var window = EnsureMainWindow();
                foreach (var arg in args)
                {
                    if (File.Exists(arg))
                        window.LoadMarkdownFile(arg);
                }
            };
            timer.Start();
        }

        /// <summary>
        /// Gets an existing MainWindow or creates one. Called from PipeServer handler
        /// and deferred startup. Always runs on UI thread.
        /// </summary>
        public MainWindow EnsureMainWindow()
        {
            var existing = Windows.OfType<MainWindow>().FirstOrDefault();
            if (existing != null) return existing;

            var window = new MainWindow();
            window.Show();
            return window;
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _pipeServer?.Dispose();
            _mutex?.ReleaseMutex();
            _mutex?.Dispose();
            base.OnExit(e);
        }
    }
}
