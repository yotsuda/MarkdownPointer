using System.IO;
using System.Windows;

namespace MarkdownViewer
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // コマンドライン引数からファイルパスを取得
            if (e.Args.Length > 0 && File.Exists(e.Args[0]))
            {
                var mainWindow = new MainWindow();
                mainWindow.LoadMarkdownFile(e.Args[0]);
                mainWindow.Show();
            }
            else
            {
                var mainWindow = new MainWindow();
                mainWindow.Show();
            }
        }
    }
}
