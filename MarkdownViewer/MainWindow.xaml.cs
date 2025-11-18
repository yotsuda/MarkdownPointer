using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Markdig;
using Microsoft.Win32;

namespace MarkdownViewer
{
    public partial class MainWindow : Window
    {
        private FileSystemWatcher? _watcher;
        private string? _currentFilePath;
        private readonly MarkdownPipeline _pipeline;
        private DispatcherTimer? _zoomTimer;
        private DispatcherTimer? _debounceTimer;
        private double _lastZoomFactor = 1.0;
        private string? _pendingFilePath;
        private bool _isInitialized = false;

        public MainWindow()
        {
            InitializeComponent();
            
            // Markdig パイプラインを設定（GitHub Flavored Markdown）
            _pipeline = new MarkdownPipelineBuilder()
                .UseAdvancedExtensions()
                .Build();
            
            // デバウンス用タイマーを初期化
            _debounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(200)
            };
            _debounceTimer.Tick += DebounceTimer_Tick;
            
            InitializeAsync();
        }

        private async void InitializeAsync()
        {
            try
            {
                await WebView.EnsureCoreWebView2Async(null);
                
                // WebView2 の不要なUIを無効化
                WebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                WebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                WebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                
                // ドロップされたファイルをインターセプト
                WebView.CoreWebView2.NewWindowRequested += CoreWebView2_NewWindowRequested;
                
                _isInitialized = true;
                
                // ズーム変更を監視するタイマーを設定
                SetupZoomMonitoring();
                
                // 保留中のファイルがあれば読み込む
                if (!string.IsNullOrEmpty(_pendingFilePath))
                {
                    LoadMarkdownFileInternal(_pendingFilePath);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"WebView2 の初期化に失敗しました:\n{ex.Message}", 
                    "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SetupZoomMonitoring()
        {
            _zoomTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            
            _zoomTimer.Tick += (s, e) =>
            {
                if (WebView?.CoreWebView2 != null)
                {
                    var currentZoom = WebView.ZoomFactor;
                    if (Math.Abs(currentZoom - _lastZoomFactor) > 0.01)
                    {
                        _lastZoomFactor = currentZoom;
                        AdjustWindowSizeForZoom(currentZoom);
                    }
                }
            };
            
            _zoomTimer.Start();
        }

        private void AdjustWindowSizeForZoom(double zoomFactor)
        {
            // コンテンツの基本幅: 980px (max-width) + 80px (padding) = 1060px
            const double baseContentWidth = 1060.0;
            const double scrollbarWidth = 20.0;
            
            var targetWidth = (baseContentWidth * zoomFactor) + scrollbarWidth;
            targetWidth = Math.Max(400, Math.Min(targetWidth, SystemParameters.WorkArea.Width * 0.9));
            
            Width = targetWidth;
            
            if (Left + Width > SystemParameters.WorkArea.Width)
            {
                Left = Math.Max(0, SystemParameters.WorkArea.Width - Width);
            }
        }

        #region ファイル操作

        public void LoadMarkdownFile(string filePath)
        {
            if (!File.Exists(filePath))
            {
                MessageBox.Show($"ファイルが見つかりません: {filePath}", "エラー", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (_isInitialized)
            {
                LoadMarkdownFileInternal(filePath);
            }
            else
            {
                _pendingFilePath = filePath;
            }
        }

        private void LoadMarkdownFileInternal(string filePath)
        {
            _currentFilePath = filePath;
            FilePathText.Text = $"📄 {Path.GetFileName(filePath)}";
            Title = $"Markdown Viewer - {Path.GetFileName(filePath)}";

            // プレースホルダーを非表示
            PlaceholderPanel.Visibility = Visibility.Collapsed;

            // ファイル監視を設定
            SetupFileWatcher(filePath);

            // Markdown を表示
            RenderMarkdown(filePath);
        }

        private void SetupFileWatcher(string filePath)
        {
            _watcher?.Dispose();

            var directory = Path.GetDirectoryName(filePath);
            var fileName = Path.GetFileName(filePath);

            _watcher = new FileSystemWatcher(directory!)
            {
                Filter = fileName,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
            };

            _watcher.Changed += (s, e) =>
            {
                // UIスレッドでデバウンスタイマーをリセット
                Dispatcher.Invoke(() =>
                {
                    _debounceTimer?.Stop();
                    _debounceTimer?.Start();
                    StatusText.Text = "⟳";
                });
            };

            _watcher.Deleted += (s, e) =>
            {
                // ファイルが削除されたらウィンドウを閉じる
                Dispatcher.Invoke(() =>
                {
                    Close();
                });
            };

            _watcher.EnableRaisingEvents = true;
            WatchStatusText.Text = "👁 監視中";
        }

        private void DebounceTimer_Tick(object? sender, EventArgs e)
        {
            _debounceTimer?.Stop();
            
            if (!string.IsNullOrEmpty(_currentFilePath))
            {
                RenderMarkdown(_currentFilePath);
                StatusText.Text = $"✓ {DateTime.Now:HH:mm:ss}";
            }
        }

        private void RenderMarkdown(string filePath)
        {
            try
            {
                var markdown = File.ReadAllText(filePath, Encoding.UTF8);
                var html = ConvertMarkdownToHtml(markdown);
                WebView.NavigateToString(html);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Markdown の表示エラー: {ex.Message}", "エラー", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenFileDialog()
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Markdown files (*.md;*.markdown)|*.md;*.markdown|All files (*.*)|*.*",
                Title = "Markdown ファイルを開く"
            };

            if (dialog.ShowDialog() == true)
            {
                LoadMarkdownFile(dialog.FileName);
            }
        }

        #endregion

        #region HTML変換

        private string ConvertMarkdownToHtml(string markdown)
        {
            var htmlContent = Markdown.ToHtml(markdown, _pipeline);
            
            var html = new StringBuilder();
            html.AppendLine("<!DOCTYPE html>");
            html.AppendLine("<html><head>");
            html.AppendLine("<meta charset='utf-8'/>");
            html.AppendLine("<style>");
            html.AppendLine(@"
                body { 
                    font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Helvetica, Arial, sans-serif;
                    line-height: 1.6;
                    padding: 40px;
                    max-width: 980px;
                    margin: 0 auto;
                    background-color: #ffffff;
                    color: #24292e;
                }
                h1, h2, h3, h4, h5, h6 { 
                    margin-top: 24px; 
                    margin-bottom: 16px; 
                    font-weight: 600; 
                    line-height: 1.25;
                }
                h1 { font-size: 2em; border-bottom: 1px solid #eaecef; padding-bottom: 0.3em; }
                h2 { font-size: 1.5em; border-bottom: 1px solid #eaecef; padding-bottom: 0.3em; }
                h3 { font-size: 1.25em; }
                code { 
                    background-color: rgba(27,31,35,0.05); 
                    padding: 0.2em 0.4em; 
                    margin: 0;
                    border-radius: 3px;
                    font-family: 'Consolas', 'Monaco', 'Courier New', monospace;
                    font-size: 85%;
                }
                pre { 
                    background-color: #f6f8fa; 
                    padding: 16px; 
                    border-radius: 6px;
                    overflow: auto;
                    line-height: 1.45;
                }
                pre code {
                    background-color: transparent;
                    padding: 0;
                    font-size: 100%;
                }
                blockquote {
                    padding: 0 1em;
                    color: #6a737d;
                    border-left: 0.25em solid #dfe2e5;
                    margin: 0 0 16px 0;
                }
                ul, ol { 
                    padding-left: 2em; 
                    margin-bottom: 16px;
                }
                li { margin-bottom: 4px; }
                table {
                    border-collapse: collapse;
                    width: 100%;
                    margin-bottom: 16px;
                }
                table th, table td {
                    padding: 6px 13px;
                    border: 1px solid #dfe2e5;
                }
                table th {
                    font-weight: 600;
                    background-color: #f6f8fa;
                }
                table tr:nth-child(2n) {
                    background-color: #f6f8fa;
                }
                a {
                    color: #0366d6;
                    text-decoration: none;
                }
                a:hover {
                    text-decoration: underline;
                }
                img {
                    max-width: 100%;
                    box-sizing: border-box;
                }
                hr {
                    height: 0.25em;
                    padding: 0;
                    margin: 24px 0;
                    background-color: #e1e4e8;
                    border: 0;
                }
                p {
                    margin-bottom: 16px;
                }
            ");
            html.AppendLine("</style>");
            html.AppendLine("</head><body>");
            html.AppendLine(htmlContent);
            html.AppendLine("</body></html>");
            
            return html.ToString();
        }

        #endregion

        #region イベントハンドラー

        private void CoreWebView2_NewWindowRequested(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2NewWindowRequestedEventArgs e)
        {
            // 新しいウィンドウを開かせない
            e.Handled = true;
            
            // file:// URL の場合はファイルを開く
            if (e.Uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                var uri = new Uri(e.Uri);
                var filePath = uri.LocalPath;
                
                var ext = Path.GetExtension(filePath).ToLowerInvariant();
                if (ext == ".md" || ext == ".markdown" || ext == ".txt")
                {
                    LoadMarkdownFile(filePath);
                }
                else
                {
                    MessageBox.Show("Markdown ファイル (.md, .markdown) をドロップしてください。", 
                        "ファイル形式エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files.Length > 0)
                {
                    var file = files[0];
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    if (ext == ".md" || ext == ".markdown" || ext == ".txt")
                    {
                        LoadMarkdownFile(file);
                    }
                    else
                    {
                        MessageBox.Show("Markdown ファイル (.md, .markdown) をドロップしてください。", 
                            "ファイル形式エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
            }
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F5)
            {
                // F5: 再読み込み
                if (!string.IsNullOrEmpty(_currentFilePath))
                {
                    RenderMarkdown(_currentFilePath);
                    StatusText.Text = $"✓ {DateTime.Now:HH:mm:ss}";
                }
                e.Handled = true;
            }
            else if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control)
            {
                // Ctrl+O: ファイルを開く
                OpenFileDialog();
                e.Handled = true;
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _watcher?.Dispose();
            _zoomTimer?.Stop();
            _debounceTimer?.Stop();
            base.OnClosed(e);
        }

        #endregion
    }
}