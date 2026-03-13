using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MarkdownPointer.Models;
using MarkdownPointer.Services;

namespace MarkdownPointer
{
    // Event handlers partial class
    public partial class MainWindow
    {
        #region Tab Control Events

        private void TabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (FileTabControl.SelectedItem is TabItemData tab)
            {
                UpdateWindowTitle();
                UpdateErrorIndicator(tab);
                _targetZoomFactor = tab.CssZoomFactor;
                _currentZoomFactor = tab.CssZoomFactor;
                UpdatePointingModeAvailability(tab);

            }
            else
            {
                UpdateErrorIndicator(null);
            }
            UpdateFilePathStatus();
            UpdateFilePathVisibility();
        }

        private void CloseTab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is TabItemData tab)
            {
                CloseTab(tab);
            }
        }

        private void AddTab_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog();
        }

        #endregion

        #region Keyboard Shortcuts

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F5)
            {
                if (FileTabControl.SelectedItem is TabItemData tab)
                {
                    RenderMarkdown(tab);
                    ShowStatusMessage($"✓ Source reloaded at {DateTime.Now:HH:mm:ss}");
                }
                e.Handled = true;
            }
            else if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control)
            {
                OpenFileDialog();
                e.Handled = true;
            }
            else if ((e.Key == Key.W && Keyboard.Modifiers == ModifierKeys.Control) ||
                     (e.Key == Key.F4 && Keyboard.Modifiers == ModifierKeys.Control))
            {
                if (FileTabControl.SelectedItem is TabItemData tab)
                {
                    CloseTab(tab);
                }
                e.Handled = true;
            }
            else if (e.Key == Key.P && Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (FileTabControl.SelectedItem is TabItemData tab && tab.WebView.CoreWebView2 != null)
                {
                    tab.WebView.CoreWebView2.ShowPrintUI();
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Tab && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                // Ctrl+Tab / Ctrl+Shift+Tab: Switch tab
                // Guard: WebView2 re-dispatches the same keystroke after tab switch
                if (_suppressCtrlTab)
                {
                    e.Handled = true;
                    return;
                }

                if (_tabs.Count > 1)
                {
                    // Move focus away from WebView2 BEFORE switching tab,
                    // otherwise TabControl reverts selection to the focused tab.
                    Focus();

                    var currentIndex = FileTabControl.SelectedIndex;
                    if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                        FileTabControl.SelectedIndex = (currentIndex - 1 + _tabs.Count) % _tabs.Count;
                    else
                        FileTabControl.SelectedIndex = (currentIndex + 1) % _tabs.Count;

                    // Suppress duplicate events from WebView2 re-dispatching the keystroke
                    _suppressCtrlTab = true;
                    Dispatcher.InvokeAsync(() => _suppressCtrlTab = false,
                        System.Windows.Threading.DispatcherPriority.Input);
                }
                e.Handled = true;
            }
            else if (e.Key == Key.G && Keyboard.Modifiers == ModifierKeys.Control)
            {
                ShowGoToLineBox();
                e.Handled = true;
            }
        }

        private Window? _goToLineWindow;

        private void ShowGoToLineBox()
        {
            if (FileTabControl.SelectedItem is not TabItemData)
                return;

            if (_goToLineWindow != null)
                return;

            var dlg = new Window
            {
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                SizeToContent = SizeToContent.WidthAndHeight,
                Background = System.Windows.Media.Brushes.White,
                ShowInTaskbar = false,
            };
            _goToLineWindow = dlg;

            var border = new Border
            {
                BorderBrush = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#0366d6")),
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(16, 12, 16, 12),
            };

            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(new TextBlock
            {
                Text = "Go to Line:",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
            });

            var input = new TextBox { Width = 120, FontSize = 14, Padding = new Thickness(4, 2, 4, 2) };
            input.PreviewTextInput += (s, ev) => { ev.Handled = !ev.Text.All(char.IsDigit); };
            DataObject.AddPastingHandler(input, (s, ev) =>
            {
                if (ev.DataObject.GetDataPresent(typeof(string)))
                {
                    if (!((string)ev.DataObject.GetData(typeof(string))).All(char.IsDigit))
                        ev.CancelCommand();
                }
                else ev.CancelCommand();
            });
            panel.Children.Add(input);
            border.Child = panel;
            dlg.Content = border;

            void CloseDialog()
            {
                if (_goToLineWindow == null) return;
                _goToLineWindow = null;
                dlg.Close();
            }

            input.KeyDown += (s, ev) =>
            {
                if (ev.Key == Key.Enter)
                {
                    if (int.TryParse(input.Text.Trim(), out int line) && line > 0 &&
                        FileTabControl.SelectedItem is TabItemData tab &&
                        tab.WebView?.CoreWebView2 != null)
                    {
                        tab.WebView.CoreWebView2.ExecuteScriptAsync($"scrollToLine({line})");
                    }
                    CloseDialog();
                    ev.Handled = true;
                }
                else if (ev.Key == Key.Escape)
                {
                    CloseDialog();
                    ev.Handled = true;
                }
            };

            dlg.Deactivated += (s, ev) => CloseDialog();
            dlg.Closed += (s, ev) => _goToLineWindow = null;
            dlg.ContentRendered += (s, ev) => input.Focus();
            dlg.Show();
        }

        private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                ApplyZoomDelta(e.Delta);
            }
        }

        #endregion

        #region Toolbar Toggle Buttons

        private void TopmostToggle_Click(object sender, RoutedEventArgs e)
        {
            Topmost = TopmostToggle.IsChecked == true;
        }

        private void DragMoveToggle_Click(object sender, RoutedEventArgs e)
        {
            _isDragMoveMode = DragMoveToggle.IsChecked == true;
            DragOverlay.Visibility = _isDragMoveMode ? Visibility.Visible : Visibility.Collapsed;
            DragOverlay.Cursor = Cursors.SizeAll;

            // Disable pointing mode if enabling drag mode
            if (_isDragMoveMode)
            {
                _pointingModeBeforeSvg = false;
            }
            if (_isDragMoveMode && _isPointingMode)
            {
                PointingModeToggle.IsChecked = false;
                _isPointingMode = false;
                foreach (var tab in _tabs)
                {
                    if (tab.IsInitialized && tab.WebView.CoreWebView2 != null)
                    {
                        tab.WebView.CoreWebView2.ExecuteScriptAsync("setPointingMode(false)");
                    }
                }
            }

            // Enable/disable WebView and text selection for all tabs
            foreach (var tab in _tabs)
            {
                tab.WebView.IsEnabled = !_isDragMoveMode;
                if (tab.IsInitialized && tab.WebView.CoreWebView2 != null)
                {
                    // Disable text selection in pan mode (like pointing mode)
                    var userSelect = _isDragMoveMode ? "none" : (_isPointingMode ? "none" : "");
                    tab.WebView.CoreWebView2.ExecuteScriptAsync($"document.body.style.userSelect = '{userSelect}'");
                }
            }
        }

        private void PointingModeToggle_Click(object sender, RoutedEventArgs e)
        {
            _isPointingMode = PointingModeToggle.IsChecked == true;
            _pointingModeBeforeSvg = _isPointingMode;

            // Disable drag mode if enabling pointing mode
            if (_isPointingMode && _isDragMoveMode)
            {
                DragMoveToggle.IsChecked = false;
                _isDragMoveMode = false;
                DragOverlay.Visibility = Visibility.Collapsed;
                foreach (var tab in _tabs)
                {
                    tab.WebView.IsEnabled = true;
                }
            }

            foreach (var tab in _tabs)
            {
                if (tab.IsInitialized && tab.WebView.CoreWebView2 != null)
                {
                    tab.WebView.CoreWebView2.ExecuteScriptAsync("setPointingMode(" + (_isPointingMode ? "true" : "false") + ")");
                }
            }
        }

        /// <summary>
        /// Updates pointing mode availability based on file type.
        /// SVG files don't support pointing mode since they are typically auto-generated.
        /// </summary>
        private void UpdatePointingModeAvailability(TabItemData tab)
        {
            var isSvgFile = !string.IsNullOrEmpty(tab.FilePath) && 
                           tab.FilePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase);
            
            PointingModeToggle.IsEnabled = !isSvgFile;
            
            if (isSvgFile)
            {
                if (_isPointingMode)
                {
                    // Save state and disable pointing mode for SVG files
                    _pointingModeBeforeSvg = true;
                    PointingModeToggle.IsChecked = false;
                    _isPointingMode = false;
                    
                    foreach (var t in _tabs)
                    {
                        if (t.IsInitialized && t.WebView.CoreWebView2 != null)
                        {
                            t.WebView.CoreWebView2.ExecuteScriptAsync("setPointingMode(false)");
                        }
                    }
                }
            }
            else
            {
                // Restore pointing mode when switching back to non-SVG file
                if (_pointingModeBeforeSvg && !_isPointingMode)
                {
                    PointingModeToggle.IsChecked = true;
                    _isPointingMode = true;
                    
                    foreach (var t in _tabs)
                    {
                        if (t.IsInitialized && t.WebView.CoreWebView2 != null)
                        {
                            t.WebView.CoreWebView2.ExecuteScriptAsync("setPointingMode(true)");
                        }
                    }
                }
            }
        }

        private void OpenInCodeButton_Click(object sender, RoutedEventArgs e)
        {
            if (FileTabControl.SelectedItem is TabItemData tab && !string.IsNullOrEmpty(tab.FilePath))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c code \"{tab.FilePath}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    ShowStatusMessage("✓ Opened in VS Code");
                }
                catch (Exception ex)
                {
                    ShowStatusMessage($"✗ VS Code failed: {ex.Message}");
                }
            }
        }

        #endregion

        #region Drag Overlay Events (Pan Mode)

        private void DragOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isDocumentScrolling = true;
            _scrollStartPoint = e.GetPosition(DragOverlay);
            DragOverlay.CaptureMouse();
            DragOverlay.Cursor = Cursors.None;  // Hide cursor while dragging
        }

        private void DragOverlay_MouseMove(object sender, MouseEventArgs e)
        {
            if (FileTabControl.SelectedItem is not TabItemData tab) return;

            if (_isDocumentScrolling)
            {
                var currentPoint = e.GetPosition(DragOverlay);
                var deltaX = _scrollStartPoint.X - currentPoint.X;
                var deltaY = _scrollStartPoint.Y - currentPoint.Y;
                _scrollStartPoint = currentPoint;
                tab.WebView.CoreWebView2?.ExecuteScriptAsync($"window.scrollBy({deltaX}, {deltaY})");
            }
            else if (tab.WebView.CoreWebView2 != null)
            {
                var pos = e.GetPosition(tab.WebView);
                var x = pos.X / tab.CssZoomFactor;
                var y = pos.Y / tab.CssZoomFactor;
                tab.WebView.CoreWebView2.ExecuteScriptAsync(
                    $"(function(){{ var el = document.elementFromPoint({x:F0},{y:F0}); if(!el) return; var p = getPointableElement(el); if(p){{ var l = getElementLine(p); window.chrome.webview.postMessage('pointhover:'+l); }} else {{ window.chrome.webview.postMessage('pointleave:'); }} }})()");
            }
        }

        private void DragOverlay_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isDocumentScrolling = false;
            DragOverlay.ReleaseMouseCapture();
            DragOverlay.Cursor = Cursors.SizeAll;  // Restore cursor
        }

        private void DragOverlay_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (FileTabControl.SelectedItem is TabItemData tab)
            {
                if (Keyboard.Modifiers == ModifierKeys.Control)
                {
                    // Ctrl+Wheel: Zoom (Mermaid diagram or page)
                    e.Handled = true;
                    var pos = e.GetPosition(tab.WebView);
                    var x = pos.X / tab.CssZoomFactor;
                    var y = pos.Y / tab.CssZoomFactor;
                    var direction = e.Delta > 0 ? "in" : "out";
                    tab.WebView.CoreWebView2?.ExecuteScriptAsync(
                        $"(function(){{ var el = document.elementFromPoint({x:F0},{y:F0}); while(el && !el.classList.contains('mermaid-scroll-container')) el = el.parentElement; if(el) {{ zoomMermaidDiagram(el, '{direction}'); }} else {{ window.chrome.webview.postMessage('zoom:{direction}'); }} }})()");
                }
                else
                {
                    // Normal wheel: Scroll page
                    e.Handled = true;
                    var scrollAmount = -e.Delta;
                    tab.WebView.CoreWebView2?.ExecuteScriptAsync($"window.scrollBy(0, {scrollAmount})");
                }
            }
        }

        private void DragOverlay_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (FileTabControl.SelectedItem is TabItemData tab)
            {
                var contextMenu = new ContextMenu();

                var savePngItem = new MenuItem { Header = "Save as Image..." };
                savePngItem.Click += async (s, args) => await _clipboardService.SaveMermaidPngAsync(tab.WebView, _contextMenuPosition);
                contextMenu.Items.Add(savePngItem);

                contextMenu.Items.Add(new Separator());

                var copyPngItem = new MenuItem { Header = "Copy as Image" };
                copyPngItem.Click += async (s, args) => await _clipboardService.CopyElementAsPngAsync(tab.WebView, _contextMenuPosition, "mermaid");
                contextMenu.Items.Add(copyPngItem);

                var copySvgItem = new MenuItem { Header = "Copy as SVG" };
                copySvgItem.Click += async (s, args) => await _clipboardService.CopyMermaidSvgAsync(tab.WebView, _contextMenuPosition);
                contextMenu.Items.Add(copySvgItem);

                contextMenu.IsOpen = true;
            }
        }

        #endregion

        #region Error Indicator

        private void ErrorIndicator_Click(object sender, MouseButtonEventArgs e)
        {
            if (FileTabControl.SelectedItem is TabItemData tab && tab.LastRenderErrors.Count > 0)
            {
                var errorText = $"[{tab.FilePath}]{Environment.NewLine}{string.Join(Environment.NewLine, tab.LastRenderErrors)}";
                Clipboard.SetText(errorText);
                ShowStatusMessage($"✓ {tab.LastRenderErrors.Count} error(s) copied to clipboard");
            }
        }

        #endregion



        #region Export

        private async void ExportDocxButton_Click(object sender, RoutedEventArgs e)
        {
            if (FileTabControl.SelectedItem is not TabItemData tab || string.IsNullOrEmpty(tab.FilePath))
                return;

            if (!PandocService.IsPandocInstalled())
            {
                var result = MessageBox.Show(
                    "Pandoc is required to export .docx files.\nWould you like to open the Pandoc download page?",
                    "Pandoc not found",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);

                if (result == MessageBoxResult.Yes)
                {
                    Process.Start(new ProcessStartInfo("https://pandoc.org/installing.html")
                        { UseShellExecute = true });
                }
                return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                FileName = Path.GetFileNameWithoutExtension(tab.FilePath) + ".docx",
                DefaultExt = ".docx",
                Filter = "Word Document (*.docx)|*.docx",
                InitialDirectory = Path.GetDirectoryName(tab.FilePath)
            };

            if (dialog.ShowDialog() == true)
            {
                var (success, error) = await PandocService.ConvertToDocxAsync(tab.FilePath, dialog.FileName);
                if (success)
                {
                    ShowStatusMessage("✓ Exported .docx");
                    var exportedPath = dialog.FileName;
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                    timer.Tick += (_, _) => { timer.Stop(); Process.Start("explorer.exe", $"/select,\"{exportedPath}\""); };
                    timer.Start();
                }
                else
                    ShowStatusMessage($"✗ Export failed: {error}");
            }
        }

        #endregion

        #region Copy Source

        private void CopySourceButton_Click(object sender, RoutedEventArgs e)
        {
            if (FileTabControl.SelectedItem is TabItemData tab && !string.IsNullOrEmpty(tab.FilePath))
            {
                try
                {
                    var content = System.IO.File.ReadAllText(tab.FilePath);
                    Clipboard.SetText(content);
                    ShowStatusMessage("✓ Source copied");
                }
                catch (Exception ex)
                {
                    ShowStatusMessage($"✗ Failed: {ex.Message}");
                }
            }
        }

        private void CopyPathButton_Click(object sender, RoutedEventArgs e)
        {
            if (FileTabControl.SelectedItem is TabItemData tab && !string.IsNullOrEmpty(tab.FilePath))
            {
                try
                {
                    Clipboard.SetText(tab.FilePath);
                    ShowStatusMessage("✓ Path copied");
                }
                catch (Exception ex)
                {
                    ShowStatusMessage($"✗ Failed: {ex.Message}");
                }
            }
        }

        #endregion
    }
}
