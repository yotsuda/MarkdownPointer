using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Markdig;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;

namespace MarkdownViewer
{
    // タブのデータモデル
    public class TabItemData : INotifyPropertyChanged
    {
        private string _title = "";
        public string Title
        {
            get => _title;
            set { _title = value; OnPropertyChanged(nameof(Title)); }
        }
        
        public string FilePath { get; set; } = "";
        public WebView2 WebView { get; set; } = null!;
        public FileSystemWatcher? Watcher { get; set; }
        public DispatcherTimer? DebounceTimer { get; set; }
        public bool IsInitialized { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) => 
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public partial class MainWindow : Window
    {
        private readonly MarkdownPipeline _pipeline;
        private readonly ObservableCollection<TabItemData> _tabs = new();
        private DispatcherTimer? _zoomAnimationTimer;
        private double _lastZoomFactor = 1.0;
        private double _targetZoomFactor = 1.0;
        private bool _isDragMoveMode = false;

        public MainWindow()
        {
            InitializeComponent();
            
            // Markdig パイプラインを設定
            _pipeline = new MarkdownPipelineBuilder()
                .UseAdvancedExtensions()
                .Build();
            
            FileTabControl.ItemsSource = _tabs;
        }

        #region タブ管理

        public void LoadMarkdownFile(string filePath)
        {
            if (!File.Exists(filePath))
            {
                MessageBox.Show($"ファイルが見つかりません: {filePath}", "エラー", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // 既に開いているファイルかチェック
            foreach (var tab in _tabs)
            {
                if (string.Equals(tab.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                {
                    FileTabControl.SelectedItem = tab;
                    return;
                }
            }

            // 新しいタブを作成
            var newTab = new TabItemData
            {
                FilePath = filePath,
                Title = Path.GetFileName(filePath),
                WebView = new WebView2()
            };

            // WebView2 の初期化（fire-and-forget、例外は内部でハンドル）
            _ = InitializeTabWebViewAsync(newTab).ContinueWith(t =>
            {
                if (t.IsFaulted && t.Exception != null)
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        MessageBox.Show($"WebView2 の初期化に失敗しました:\n{t.Exception.InnerException?.Message ?? t.Exception.Message}", 
                            "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                        CloseTab(newTab);
                    });
                }
            }, TaskScheduler.Default);
            
            _tabs.Add(newTab);
            FileTabControl.SelectedItem = newTab;
            
            // プレースホルダーを非表示、タブコントロールを表示
            PlaceholderPanel.Visibility = Visibility.Collapsed;
            FileTabControl.Visibility = Visibility.Visible;
            
            UpdateWindowTitle();
        }

        private async Task InitializeTabWebViewAsync(TabItemData tab)
        {
            // WebView2上のドロップをウィンドウレベルで処理
            tab.WebView.AllowDrop = true;
            tab.WebView.PreviewDragOver += (s, e) =>
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    e.Effects = DragDropEffects.Copy;
                    e.Handled = true;
                }
            };
            tab.WebView.PreviewDrop += (s, e) =>
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                    foreach (var file in files)
                    {
                        var ext = Path.GetExtension(file).ToLowerInvariant();
                        if (ext == ".md" || ext == ".markdown" || ext == ".txt")
                        {
                            LoadMarkdownFile(file);
                        }
                    }
                    e.Handled = true;
                }
            };
            
            await tab.WebView.EnsureCoreWebView2Async(null);
            
            // タブがまだ存在するか確認
            if (!_tabs.Contains(tab)) return;
            
            // WebView2 の設定
            tab.WebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            tab.WebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            tab.WebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            
            // ドロップされたファイルを新しいウィンドウで開かせない
            tab.WebView.CoreWebView2.NewWindowRequested += (s, e) =>
            {
                e.Handled = true;
                if (e.Uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                {
                    var uri = new Uri(e.Uri);
                    var path = uri.LocalPath;
                    var ext = Path.GetExtension(path).ToLowerInvariant();
                    if (ext == ".md" || ext == ".markdown" || ext == ".txt")
                    {
                        LoadMarkdownFile(path);
                    }
                }
            };
            
            // リンククリック時の処理（JavaScriptからのメッセージ）
            tab.WebView.CoreWebView2.WebMessageReceived += (s, e) =>
            {
                var uri = e.TryGetWebMessageAsString();
                
                if (string.IsNullOrEmpty(uri))
                {
                    return;
                }
                
                // リモートURL（http/https）はブラウザで開く
                if (uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || 
                    uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
                    return;
                }
                
                // ローカルファイル
                if (uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var fileUri = new Uri(uri);
                        var path = Uri.UnescapeDataString(fileUri.LocalPath);
                        var ext = Path.GetExtension(path).ToLowerInvariant();
                        
                        if (ext == ".md" || ext == ".markdown" || ext == ".txt")
                        {
                            // Markdownファイルは新しいタブで開く
                            if (File.Exists(path))
                            {
                                LoadMarkdownFile(path);
                            }
                            else
                            {
                                MessageBox.Show($"ファイルが見つかりません: {path}", "エラー", 
                                    MessageBoxButton.OK, MessageBoxImage.Warning);
                            }
                        }
                        else
                        {
                            // その他のファイルはデフォルトアプリで開く
                            if (File.Exists(path))
                            {
                                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                            }
                            else
                            {
                                MessageBox.Show($"ファイルが見つかりません: {path}", "エラー", 
                                    MessageBoxButton.OK, MessageBoxImage.Warning);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"ファイルを開けませんでした: {ex.Message}", "エラー", 
                            MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            };
            
            // ズーム設定
            SetupZoomForTab(tab);
            
            tab.IsInitialized = true;
            
            // ファイル監視を設定
            SetupFileWatcher(tab);
            
            // Markdown を表示
            RenderMarkdown(tab);
        }

        private void SetupZoomForTab(TabItemData tab)
        {
            tab.WebView.ZoomFactorChanged += (s, e) =>
            {
                if (FileTabControl.SelectedItem == tab)
                {
                    var currentZoom = tab.WebView.ZoomFactor;
                    if (Math.Abs(currentZoom - _lastZoomFactor) > 0.001)
                    {
                        _lastZoomFactor = currentZoom;
                        AdjustWindowSizeForZoom(currentZoom);
                    }
                }
            };
            
            // アニメーション用タイマー（ウィンドウ共通）
            if (_zoomAnimationTimer == null)
            {
                _zoomAnimationTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(16)
                };
                
                _zoomAnimationTimer.Tick += (s, e) =>
                {
                    if (FileTabControl.SelectedItem is TabItemData currentTab)
                    {
                        var currentZoom = currentTab.WebView.ZoomFactor;
                        var diff = _targetZoomFactor - currentZoom;
                        
                        if (Math.Abs(diff) < 0.005)
                        {
                            currentTab.WebView.ZoomFactor = _targetZoomFactor;
                            _zoomAnimationTimer.Stop();
                            AdjustWindowSizeForZoom(_targetZoomFactor);
                            return;
                        }
                        
                        var step = diff * 0.1;
                        var newZoom = currentZoom + step;
                        currentTab.WebView.ZoomFactor = newZoom;
                        
                        // ドラッグ移動モードではZoomFactorChangedが発火しないので直接呼び出す
                        if (_isDragMoveMode)
                        {
                            AdjustWindowSizeForZoom(newZoom);
                        }
                    }
                };
            }
            
            // マウスホイールハンドリング
            tab.WebView.PreviewMouseWheel += (s, e) =>
            {
                if (Keyboard.Modifiers == ModifierKeys.Control)
                {
                    e.Handled = true;
                    
                    double zoomStep = 0.01;
                    
                    if (e.Delta > 0)
                    {
                        _targetZoomFactor = Math.Min(5.0, _targetZoomFactor + zoomStep);
                    }
                    else
                    {
                        _targetZoomFactor = Math.Max(0.25, _targetZoomFactor - zoomStep);
                    }
                    
                    if (!_zoomAnimationTimer!.IsEnabled)
                    {
                        _zoomAnimationTimer.Start();
                    }
                }
            };
        }

        private void SetupFileWatcher(TabItemData tab)
        {
            tab.Watcher?.Dispose();

            var directory = Path.GetDirectoryName(tab.FilePath);
            var fileName = Path.GetFileName(tab.FilePath);

            // ディレクトリが取得できない場合は監視しない
            if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(fileName))
            {
                return;
            }

            tab.Watcher = new FileSystemWatcher(directory)
            {
                Filter = fileName,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
            };

            // デバウンス用タイマー
            tab.DebounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(200)
            };
            
            tab.DebounceTimer.Tick += (s, e) =>
            {
                tab.DebounceTimer.Stop();
                // タブがまだ存在するか確認
                if (!_tabs.Contains(tab)) return;
                
                RenderMarkdown(tab);
                if (FileTabControl.SelectedItem == tab)
                {
                    StatusText.Text = $"✓ {DateTime.Now:HH:mm:ss}";
                }
            };

            tab.Watcher.Changed += (s, e) =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    // タブがまだ存在するか確認
                    if (!_tabs.Contains(tab)) return;
                    
                    tab.DebounceTimer?.Stop();
                    tab.DebounceTimer?.Start();
                    if (FileTabControl.SelectedItem == tab)
                    {
                        StatusText.Text = "⟳";
                    }
                });
            };

            tab.Watcher.Deleted += (s, e) =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    // タブがまだ存在するか確認
                    if (!_tabs.Contains(tab)) return;
                    
                    CloseTab(tab);
                });
            };

            tab.Watcher.Renamed += (s, e) =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    // タブがまだ存在するか確認
                    if (!_tabs.Contains(tab)) return;
                    
                    tab.FilePath = e.FullPath;
                    tab.Title = e.Name ?? Path.GetFileName(e.FullPath);
                    
                    if (tab.Watcher != null && e.Name != null)
                    {
                        tab.Watcher.Filter = e.Name;
                    }
                    
                    UpdateWindowTitle();
                    if (FileTabControl.SelectedItem == tab)
                    {
                        StatusText.Text = $"✓ {DateTime.Now:HH:mm:ss}";
                    }
                });
            };

            tab.Watcher.EnableRaisingEvents = true;
        }

        private void CloseTab(TabItemData tab)
        {
            tab.Watcher?.Dispose();
            tab.DebounceTimer?.Stop();
            tab.WebView.Dispose();
            
            _tabs.Remove(tab);
            
            if (_tabs.Count == 0)
            {
                FileTabControl.Visibility = Visibility.Collapsed;
                PlaceholderPanel.Visibility = Visibility.Visible;
                Title = "Markdown Viewer";
                FilePathText.Text = "";
                StatusText.Text = "";
                WatchStatusText.Text = "";
            }
            else
            {
                UpdateWindowTitle();
            }
        }

        private void UpdateWindowTitle()
        {
            if (FileTabControl.SelectedItem is TabItemData tab)
            {
                Title = $"Markdown Viewer - {tab.Title}";
                FilePathText.Text = $"📄 {tab.Title}";
                WatchStatusText.Text = "👁 監視中";
            }
        }

        private void RenderMarkdown(TabItemData tab)
        {
            try
            {
                var markdown = File.ReadAllText(tab.FilePath, Encoding.UTF8);
                var baseDir = Path.GetDirectoryName(tab.FilePath);
                var html = ConvertMarkdownToHtml(markdown, baseDir!);
                tab.WebView.NavigateToString(html);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Markdown の表示エラー: {ex.Message}", "エラー", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #region ウィンドウサイズ調整

        private void AdjustWindowSizeForZoom(double zoomFactor)
        {
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

        #endregion

        #region HTML変換

        private string ConvertMarkdownToHtml(string markdown, string baseDir)
        {
            var htmlContent = Markdown.ToHtml(markdown, _pipeline);
            
            // file:// URL用にパスを変換
            var baseUrl = new Uri(baseDir + Path.DirectorySeparatorChar).AbsoluteUri;
            
            var html = new StringBuilder();
            html.AppendLine("<!DOCTYPE html>");
            html.AppendLine("<html><head>");
            html.AppendLine("<meta charset='utf-8'/>");
            html.AppendLine($"<base href='{baseUrl}'/>");
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
            html.AppendLine("<script>");
            html.AppendLine(@"
                document.addEventListener('click', function(e) {
                    var target = e.target;
                    while (target && target.tagName !== 'A') {
                        target = target.parentElement;
                    }
                    if (target && target.href) {
                        e.preventDefault();
                        window.chrome.webview.postMessage(target.href);
                    }
                });
            ");
            html.AppendLine("</script>");
            html.AppendLine("</head><body>");
            html.AppendLine(htmlContent);
            html.AppendLine("</body></html>");
            
            return html.ToString();
        }

        #endregion

        #region ファイル操作

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

        #region イベントハンドラー

        private void TabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (FileTabControl.SelectedItem is TabItemData tab)
            {
                UpdateWindowTitle();
                _targetZoomFactor = tab.WebView.ZoomFactor;
                _lastZoomFactor = tab.WebView.ZoomFactor;
            }
        }

        private void CloseTab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is TabItemData tab)
            {
                CloseTab(tab);
            }
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                foreach (var file in files)
                {
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    if (ext == ".md" || ext == ".markdown" || ext == ".txt")
                    {
                        LoadMarkdownFile(file);
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
                if (FileTabControl.SelectedItem is TabItemData tab)
                {
                    RenderMarkdown(tab);
                    StatusText.Text = $"✓ {DateTime.Now:HH:mm:ss}";
                }
                e.Handled = true;
            }
            else if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control)
            {
                OpenFileDialog();
                e.Handled = true;
            }
            else if (e.Key == Key.W && Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (FileTabControl.SelectedItem is TabItemData tab)
                {
                    CloseTab(tab);
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Tab && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                // Ctrl+Shift+Tab: 前のタブへ
                if (_tabs.Count > 1)
                {
                    var currentIndex = FileTabControl.SelectedIndex;
                    FileTabControl.SelectedIndex = (currentIndex - 1 + _tabs.Count) % _tabs.Count;
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.Control)
            {
                // Ctrl+Tab: 次のタブへ
                if (_tabs.Count > 1)
                {
                    var currentIndex = FileTabControl.SelectedIndex;
                    FileTabControl.SelectedIndex = (currentIndex + 1) % _tabs.Count;
                }
                e.Handled = true;
            }
        }

        private void TopmostToggle_Click(object sender, RoutedEventArgs e)
        {
            Topmost = TopmostToggle.IsChecked == true;
        }

        private void DragMoveToggle_Click(object sender, RoutedEventArgs e)
        {
            _isDragMoveMode = DragMoveToggle.IsChecked == true;
            DragOverlay.Visibility = _isDragMoveMode ? Visibility.Visible : Visibility.Collapsed;
            
            // 全タブの WebView を無効化/有効化
            foreach (var tab in _tabs)
            {
                tab.WebView.IsEnabled = !_isDragMoveMode;
            }
        }

        private void DragOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            DragMove();
        }

        private void DragOverlay_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control && FileTabControl.SelectedItem is TabItemData tab)
            {
                e.Handled = true;
                
                double zoomStep = 0.01;
                
                if (e.Delta > 0)
                {
                    _targetZoomFactor = Math.Min(5.0, _targetZoomFactor + zoomStep);
                }
                else
                {
                    _targetZoomFactor = Math.Max(0.25, _targetZoomFactor - zoomStep);
                }
                
                if (!_zoomAnimationTimer!.IsEnabled)
                {
                    _zoomAnimationTimer.Start();
                }
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            foreach (var tab in _tabs)
            {
                tab.Watcher?.Dispose();
                tab.DebounceTimer?.Stop();
                tab.WebView.Dispose();
            }
            _zoomAnimationTimer?.Stop();
            base.OnClosed(e);
        }

        #endregion
    }
}