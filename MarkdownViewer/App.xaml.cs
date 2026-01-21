using System.IO;
using System.Threading;
using System.Windows;
using MarkdownViewer.Services;

namespace MarkdownViewer
{
    public partial class App : Application
    {
        private const string MutexName = "MarkdownViewer_SingleInstance_v2";
        private Mutex? _mutex;
        private PipeServer? _pipeServer;

        protected override void OnStartup(StartupEventArgs e)
        {
            // Try to acquire mutex for single instance
            _mutex = new Mutex(true, MutexName, out bool createdNew);

            if (!createdNew)
            {
                // Another instance is running - send file path and exit
                if (e.Args.Length > 0)
                {
                    PipeServer.SendToExistingInstance(new PipeMessage { Command = "open", Path = e.Args[0] });
                }
                else
                {
                    PipeServer.SendToExistingInstance(new PipeMessage { Command = "activate" });
                }
                Environment.Exit(0);
                return;
            }

            base.OnStartup(e);

            // Start pipe server
            _pipeServer = new PipeServer();
            _pipeServer.Start();

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
            _pipeServer?.Dispose();
            _mutex?.ReleaseMutex();
            _mutex?.Dispose();
            base.OnExit(e);
        }
    }
}