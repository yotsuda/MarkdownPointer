using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
                _targetZoomFactor = tab.WebView.ZoomFactor;
                _lastZoomFactor = tab.WebView.ZoomFactor;
                UpdatePointingModeAvailability(tab);
            }
            else
            {
                UpdateErrorIndicator(null);
            }
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
                // Ctrl+Shift+Tab: Previous tab
                if (_tabs.Count > 1)
                {
                    var currentIndex = FileTabControl.SelectedIndex;
                    FileTabControl.SelectedIndex = (currentIndex - 1 + _tabs.Count) % _tabs.Count;
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.Control)
            {
                // Ctrl+Tab: Next tab
                if (_tabs.Count > 1)
                {
                    var currentIndex = FileTabControl.SelectedIndex;
                    FileTabControl.SelectedIndex = (currentIndex + 1) % _tabs.Count;
                }
                e.Handled = true;
            }
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
            if (_isDocumentScrolling && FileTabControl.SelectedItem is TabItemData tab)
            {
                var currentPoint = e.GetPosition(DragOverlay);
                var deltaY = _scrollStartPoint.Y - currentPoint.Y;
                _scrollStartPoint = currentPoint;
                tab.WebView.CoreWebView2?.ExecuteScriptAsync($"window.scrollBy(0, {deltaY})");
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
                    // Ctrl+Wheel: Zoom
                    e.Handled = true;
                    ApplyZoomDelta(e.Delta);
                }
                else
                {
                    // Normal wheel: Scroll
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

                var copySvgItem = new MenuItem { Header = "Copy diagram as SVG" };
                copySvgItem.Click += async (s, args) => await _clipboardService.CopyMermaidSvgAsync(tab.WebView, _contextMenuPosition);
                contextMenu.Items.Add(copySvgItem);

                var copyPngItem = new MenuItem { Header = "Copy diagram as PNG" };
                copyPngItem.Click += async (s, args) => await _clipboardService.CopyElementAsPngAsync(tab.WebView, _contextMenuPosition, "mermaid");
                contextMenu.Items.Add(copyPngItem);

                contextMenu.IsOpen = true;
            }
        }

        #endregion

        #region Error Indicator

        private void ErrorIndicator_Click(object sender, MouseButtonEventArgs e)
        {
            if (FileTabControl.SelectedItem is TabItemData tab && tab.LastRenderErrors.Count > 0)
            {
                var errorText = string.Join(Environment.NewLine + Environment.NewLine, tab.LastRenderErrors);
                Clipboard.SetText(errorText);
                StatusText.Text = $"✓ {tab.LastRenderErrors.Count} error(s) copied to clipboard";
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
                    ShowStatusMessage("✓ Exported .docx");
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
