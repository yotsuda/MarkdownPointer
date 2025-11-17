using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Markdig;

namespace MarkdownViewer
{
    public partial class MainWindow : Window
    {
        private FileSystemWatcher? _watcher;
        private string? _currentFilePath;
        private readonly MarkdownPipeline _pipeline;
        private DispatcherTimer? _zoomTimer;
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
            
            InitializeAsync();
        }

        private async void InitializeAsync()
        {
            await WebView.EnsureCoreWebView2Async(null);
            _isInitialized = true;
            
            // ズーム変更を監視するタイマーを設定
            SetupZoomMonitoring();
            
            // 保留中のファイルがあれば読み込む
            if (!string.IsNullOrEmpty(_pendingFilePath))
            {
                LoadMarkdownFileInternal(_pendingFilePath);
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
            const double scrollbarWidth = 20.0; // スクロールバーの幅
            
            // ズームに応じてウィンドウ幅を調整
            var targetWidth = (baseContentWidth * zoomFactor) + scrollbarWidth;
            
            // 最小幅と最大幅を設定
            targetWidth = Math.Max(400, Math.Min(targetWidth, SystemParameters.WorkArea.Width * 0.9));
            
            // スムーズにリサイズ
            Width = targetWidth;
            
            // ウィンドウが画面外に出ないように調整
            if (Left + Width > SystemParameters.WorkArea.Width)
            {
                Left = Math.Max(0, SystemParameters.WorkArea.Width - Width);
            }
        }

        public void LoadMarkdownFile(string filePath)
        {
            if (!File.Exists(filePath))
            {
                MessageBox.Show($"File not found: {filePath}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (_isInitialized)
            {
                LoadMarkdownFileInternal(filePath);
            }
            else
            {
                // WebView2 の初期化が完了するまで保留
                _pendingFilePath = filePath;
            }
        }

        private void LoadMarkdownFileInternal(string filePath)
        {
            _currentFilePath = filePath;
            FilePathText.Text = $"📄 {Path.GetFileName(filePath)}";
            Title = $"Markdown Viewer - {Path.GetFileName(filePath)}";

            // ファイル監視を設定
            SetupFileWatcher(filePath);

            // Markdown を表示
            RenderMarkdown(filePath);
        }

        private void SetupFileWatcher(string filePath)
        {
            // 既存の監視を停止
            _watcher?.Dispose();

            var directory = Path.GetDirectoryName(filePath);
            var fileName = Path.GetFileName(filePath);

            _watcher = new FileSystemWatcher(directory!)
            {
                Filter = fileName,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size
            };

            _watcher.Changed += (s, e) =>
            {
                // 短時間に複数回発火するのを防ぐ
                System.Threading.Thread.Sleep(100);
                
                Dispatcher.Invoke(() =>
                {
                    StatusText.Text = "⟳";
                    RenderMarkdown(filePath);
                    StatusText.Text = $"✓ {DateTime.Now:HH:mm:ss}";
                });
            };

            _watcher.EnableRaisingEvents = true;
            StatusBarText.Text = "👁 Watching";
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
                MessageBox.Show($"Error rendering markdown: {ex.Message}", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private string ConvertMarkdownToHtml(string markdown)
        {
            // Markdig で Markdown を HTML に変換
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

        protected override void OnClosed(EventArgs e)
        {
            _watcher?.Dispose();
            _zoomTimer?.Stop();
            base.OnClosed(e);
        }
    }
}