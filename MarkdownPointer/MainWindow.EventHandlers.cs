using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
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
                UpdateSlideViewButton(tab);
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
            else if ((e.Key == Key.Left || e.Key == Key.Right) && Keyboard.Modifiers == ModifierKeys.None)
            {
                if (FileTabControl.SelectedItem is TabItemData tab && tab.IsSlideView
                    && tab.WebView?.CoreWebView2 != null)
                {
                    var direction = e.Key == Key.Left ? "prev" : "next";
                    tab.WebView.CoreWebView2.ExecuteScriptAsync(
                        $"if(typeof Reveal!=='undefined')Reveal.{direction}()");
                    e.Handled = true;
                }
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

        private void SlideViewToggle_Click(object sender, RoutedEventArgs e)
        {
            if (FileTabControl.SelectedItem is TabItemData tab)
            {
                tab.IsSlideView = SlideViewToggle.IsChecked == true;
                RenderMarkdown(tab, viewToggle: true);
                ShowStatusMessage(tab.IsSlideView ? "🎞 Slide view" : "📄 Document view");
            }
        }

        /// <summary>
        /// Syncs the slide view toggle button state with the current tab.
        /// </summary>
        internal void UpdateSlideViewButton(TabItemData tab)
        {
            SlideViewToggle.IsChecked = tab.IsSlideView;
        }

        private static readonly string[] SlideThemes =
            ["black", "white", "league", "beige", "sky", "night", "serif", "simple", "solarized", "moon", "blood", "dracula"];

        private void SlideViewToggle_RightClick(object sender, MouseButtonEventArgs e)
        {
            var menu = new ContextMenu();
            foreach (var theme in SlideThemes)
            {
                var item = new MenuItem
                {
                    Header = theme,
                    Icon = theme == _slideTheme
                        ? new System.Windows.Controls.TextBlock { Text = "✓", FontWeight = FontWeights.Bold }
                        : null
                };
                var t = theme;
                item.Click += (_, _) =>
                {
                    _slideTheme = t;
                    // Invalidate slide cache for all tabs
                    foreach (var openTab in _tabs)
                        openTab.CachedSlideHtml = null;

                    if (FileTabControl.SelectedItem is TabItemData tab && tab.IsSlideView)
                    {
                        StartSpinner($"Applying theme: {t}");
                        RenderMarkdown(tab);
                    }
                };
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
            e.Handled = true;
        }

        private void PointingModeToggle_Click(object sender, RoutedEventArgs e)
        {
            _isPointingMode = PointingModeToggle.IsChecked == true;
            _pointingModeBeforeSvg = _isPointingMode;

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

        private async void OpenInCodeButton_Click(object sender, RoutedEventArgs e)
        {
            if (FileTabControl.SelectedItem is TabItemData tab && !string.IsNullOrEmpty(tab.FilePath))
            {
                try
                {
                    var target = tab.FilePath;
                    if (tab.WebView?.CoreWebView2 != null)
                    {
                        var result = await tab.WebView.CoreWebView2.ExecuteScriptAsync(
                            @"(function() {
                                var elems = document.querySelectorAll('[data-line]');
                                var mid = window.innerHeight / 2;
                                var best = null;
                                var bestDist = Infinity;
                                for (var i = 0; i < elems.length; i++) {
                                    var el = elems[i];
                                    var rect = el.getBoundingClientRect();
                                    if (rect.height === 0) continue;
                                    if (el.querySelector('[data-line]')) continue;
                                    var center = (rect.top + rect.bottom) / 2;
                                    var dist = Math.abs(center - mid);
                                    if (dist < bestDist) { bestDist = dist; best = el; }
                                    if (rect.top > mid) break;
                                }
                                return best ? best.getAttribute('data-line') : null;
                            })()");
                        if (result != "null" && int.TryParse(result.Trim('"'), out var ln))
                        {
                            target = $"{tab.FilePath}:{ln}";
                        }
                    }
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c code -g \"{target}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    ShowStatusMessage($"✓ Opened in VS Code at L{(target.Contains(':') ? target.Substring(target.LastIndexOf(':') + 1) : "1")}");
                }
                catch (Exception ex)
                {
                    ShowStatusMessage($"✗ VS Code failed: {ex.Message}");
                }
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

        private async void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            if (FileTabControl.SelectedItem is not TabItemData tab || string.IsNullOrEmpty(tab.FilePath))
                return;

            if (!PandocService.IsPandocInstalled())
            {
                var result = MessageBox.Show(
                    "Pandoc is required for export.\nWould you like to open the Pandoc download page?",
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

            var defaultExt = tab.IsSlideView ? ".pptx" : ".docx";
            var filter = "Word Document (*.docx)|*.docx|PowerPoint (*.pptx)|*.pptx";
            var filterIndex = tab.IsSlideView ? 2 : 1;
            var fileName = Path.GetFileNameWithoutExtension(tab.FilePath) + defaultExt;

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                FileName = fileName,
                DefaultExt = defaultExt,
                Filter = filter,
                FilterIndex = filterIndex,
                InitialDirectory = Path.GetDirectoryName(tab.FilePath)
            };

            if (dialog.ShowDialog() == true)
            {
                var ext = Path.GetExtension(dialog.FileName).ToLowerInvariant();

                // Ask if user wants to apply a template
                string? templatePath = null;
                if (ext == ".pptx" || ext == ".docx")
                {
                    var applyTemplate = MessageBox.Show(
                        $"Apply a {(ext == ".pptx" ? "PowerPoint" : "Word")} template?",
                        "Template",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (applyTemplate == MessageBoxResult.Yes)
                    {
                        var templateFilter = ext == ".pptx"
                            ? "PowerPoint (*.pptx)|*.pptx"
                            : "Word Document (*.docx)|*.docx";
                        var templateDialog = new Microsoft.Win32.OpenFileDialog
                        {
                            Title = "Select template",
                            Filter = templateFilter
                        };
                        if (templateDialog.ShowDialog() == true)
                        {
                            templatePath = templateDialog.FileName;
                        }
                        else
                        {
                            return; // User cancelled template selection
                        }
                    }
                }

                StartSpinner($"Exporting {ext}");

                string? tempDir = null;
                var markdownPath = tab.FilePath;
                try
                {
                    // Pre-process: render Mermaid diagrams and SVG images as PNG for export
                    if ((ext == ".docx" || ext == ".pptx") && tab.WebView?.CoreWebView2 != null)
                    {
                        var mdContent = await File.ReadAllTextAsync(tab.FilePath);
                        bool modified = false;

                        // Mermaid diagrams → PNG
                        if (mdContent.Contains("```mermaid"))
                        {
                            tempDir = Path.Combine(Path.GetTempPath(), $"mdp_export_{Guid.NewGuid():N}");
                            Directory.CreateDirectory(tempDir);

                            var pngs = await _mermaidExportService.CaptureAllMermaidPngsAsync(tab.WebView, tempDir);
                            if (pngs.Count > 0)
                            {
                                mdContent = MermaidExportService.ReplaceMermaidBlocksWithImages(mdContent, pngs);
                                modified = true;
                            }
                        }

                        // SVG images → PNG
                        if (MermaidExportService.ContainsSvgImages(mdContent))
                        {
                            if (tempDir == null)
                            {
                                tempDir = Path.Combine(Path.GetTempPath(), $"mdp_export_{Guid.NewGuid():N}");
                                Directory.CreateDirectory(tempDir);
                            }

                            var svgPngs = await _mermaidExportService.CaptureAllInlineSvgPngsAsync(tab.WebView, tempDir);
                            if (svgPngs.Count > 0)
                            {
                                mdContent = MermaidExportService.ReplaceSvgImagesWithPngs(mdContent, svgPngs);
                                modified = true;
                            }
                        }

                        if (modified)
                        {
                            markdownPath = Path.Combine(tempDir!, "export.md");
                            await File.WriteAllTextAsync(markdownPath, mdContent);
                        }
                    }

                    // Pass original file's directory as resource-path so Pandoc can resolve relative images
                    var resourceDir = markdownPath != tab.FilePath ? Path.GetDirectoryName(tab.FilePath) : null;

                    (bool success, string? error) result;
                    if (ext == ".pptx")
                        result = await SlideService.ExportPptxAsync(markdownPath, dialog.FileName, templatePath, resourceDir);
                    else
                        result = await PandocService.ConvertAsync(markdownPath, dialog.FileName, templatePath, resourceDir);

                    // StopSpinner before ShowStatusMessage so it doesn't clear the message
                    StopSpinner();

                    if (result.success)
                    {
                        ShowStatusMessage($"✓ Exported {ext}");
                        var exportedPath = dialog.FileName;
                        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                        timer.Tick += (_, _) => { timer.Stop(); Process.Start("explorer.exe", $"/select,\"{exportedPath}\""); };
                        timer.Start();
                    }
                    else
                    {
                        ShowStatusMessage($"✗ Export failed: {result.error}");
                    }
                }
                finally
                {
                    // Ensure spinner is stopped on exception (normal path already called StopSpinner)
                    _spinnerTimer?.Stop();
                    _spinnerTimer = null;
                    if (tempDir != null)
                    {
                        try { Directory.Delete(tempDir, true); } catch { }
                    }
                }
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

        #region Hyperlink

        private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        }

        private void Hyperlink_MouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is Hyperlink hl && hl.NavigateUri != null)
                LinkStatusText.Text = hl.NavigateUri.AbsoluteUri;
        }

        private void Hyperlink_MouseLeave(object sender, MouseEventArgs e)
        {
            LinkStatusText.Text = "";
        }

        #endregion
    }
}
