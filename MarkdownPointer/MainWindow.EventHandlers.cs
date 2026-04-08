using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
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
            StopSpinner();
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
            else if ((e.Key == Key.Left || e.Key == Key.Right || e.Key == Key.Up || e.Key == Key.Down)
                     && Keyboard.Modifiers == ModifierKeys.None)
            {
                if (FileTabControl.SelectedItem is TabItemData tab && tab.IsSlideView
                    && tab.WebView?.CoreWebView2 != null)
                {
                    var direction = (e.Key == Key.Left || e.Key == Key.Up) ? "prev" : "next";
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

        private async void SlideViewToggle_Click(object sender, RoutedEventArgs e)
        {
            if (FileTabControl.SelectedItem is TabItemData tab)
            {
                // Save cross-view position before toggling
                if (tab.WebView.CoreWebView2 != null)
                {
                    try
                    {
                        if (tab.IsSlideView)
                        {
                            // Slide → Document: get source line of current slide
                            var lineJson = await tab.WebView.CoreWebView2.ExecuteScriptAsync(
                                "typeof Reveal !== 'undefined' && Reveal.isReady() ? " +
                                "(function(){var s=Reveal.getCurrentSlide();" +
                                "var el=s.querySelector('[data-line]');" +
                                "return el?el.getAttribute('data-line'):(s.getAttribute('data-line')||'0')" +
                                "})() : '0'");
                            if (int.TryParse(lineJson.Trim('"'), out var line) && line > 0)
                                tab.SavedSourceLine = line;
                        }
                        else
                        {
                            // Document → Slide: get source line of first visible element
                            var lineJson = await tab.WebView.CoreWebView2.ExecuteScriptAsync(
                                "(function(){var els=document.querySelectorAll('[data-line]');" +
                                "for(var i=0;i<els.length;i++){var r=els[i].getBoundingClientRect();" +
                                "if(r.bottom>0&&r.top<window.innerHeight)return els[i].getAttribute('data-line')}" +
                                "return '0'})()");
                            if (int.TryParse(lineJson.Trim('"'), out var line) && line > 0)
                                tab.SavedSourceLine = line;
                        }
                    }
                    catch { }
                }

                tab.IsSlideView = SlideViewToggle.IsChecked == true;
                RenderMarkdown(tab, viewToggle: true);
            }
        }

        /// <summary>
        /// Syncs the slide view toggle button state with the current tab.
        /// </summary>
        internal void UpdateSlideViewButton(TabItemData tab)
        {
            var isNonMarkdown = IsHtmlFile(tab.FilePath) || IsEmlFile(tab.FilePath);
            SlideViewToggle.IsEnabled = !isNonMarkdown;
            SlideViewToggle.IsChecked = tab.IsSlideView;
        }

        private static readonly string[] SlideThemes =
            ["beige", "black", "blood", "dracula", "league", "moon", "night", "serif", "simple", "sky", "solarized", "white"];

        private void SlideThemeDropdown_MouseDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true; // Prevent ToggleButton's default IsChecked toggle
            if (SlideThemeDropdown.IsChecked == true)
            {
                // Menu is open — close it (Closed handler resets IsChecked)
                return;
            }
            ShowThemeMenu(SlideThemeDropdown);
        }

        private void ShowThemeMenu(UIElement placementTarget)
        {
            var menu = new ContextMenu { PlacementTarget = placementTarget, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
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

                    if (FileTabControl.SelectedItem is TabItemData tab)
                    {
                        if (!tab.IsSlideView)
                        {
                            tab.IsSlideView = true;
                            SlideViewToggle.IsChecked = true;
                        }
                        StartSpinner($"Applying theme: {t}");
                        RenderMarkdown(tab);
                    }
                };
                menu.Items.Add(item);
            }
            menu.Closed += (_, _) => SlideThemeDropdown.IsChecked = false;
            SlideThemeDropdown.IsChecked = true;
            menu.IsOpen = true;
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
        /// SVG and HTML files don't support pointing mode.
        /// </summary>
        private void UpdatePointingModeAvailability(TabItemData tab)
        {
            var isNonPointable = !string.IsNullOrEmpty(tab.FilePath) &&
                           (tab.FilePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ||
                            IsHtmlFile(tab.FilePath) ||
                            IsEmlFile(tab.FilePath));

            PointingModeToggle.IsEnabled = !isNonPointable;

            if (isNonPointable)
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

        private void OpenInEditorButton_Click(object sender, RoutedEventArgs e)
        {
            if (FileTabControl.SelectedItem is TabItemData tab && !string.IsNullOrEmpty(tab.FilePath))
            {
                try
                {
                    Process.Start(new ProcessStartInfo(tab.FilePath) { UseShellExecute = true });
                    ShowStatusMessage("✓ Opened in editor");
                }
                catch (Exception ex)
                {
                    ShowStatusMessage($"✗ Failed to open: {ex.Message}");
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

                // Ask if user wants to apply a template (.docx only; .pptx uses SlideKit)
                string? templatePath = null;
                if (ext == ".docx")
                {
                    var applyTemplate = MessageBox.Show(
                        "Apply a Word template?",
                        "Template",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (applyTemplate == MessageBoxResult.Yes)
                    {
                        var templateDialog = new Microsoft.Win32.OpenFileDialog
                        {
                            Title = "Select template",
                            Filter = "Word Document (*.docx)|*.docx"
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

                try
                {
                    var (success, error, tempDir) = await ExportService.ExportAsync(
                        tab.FilePath, dialog.FileName, templatePath,
                        tab.WebView, _mermaidExportService);

                    // StopSpinner before ShowStatusMessage so it doesn't clear the message
                    StopSpinner();

                    if (success)
                    {
                        ShowStatusMessage($"✓ Exported {ext}");
                        var exportedPath = dialog.FileName;
                        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                        timer.Tick += (_, _) => { timer.Stop(); Process.Start("explorer.exe", $"/select,\"{exportedPath}\""); };
                        timer.Start();
                    }
                    else if (error == PandocService.PandocNotFound)
                    {
                        var answer = MessageBox.Show(
                            "Pandoc is required for export.\n\n" +
                            "Yes — Open download page\n" +
                            "No — Copy AI prompt to clipboard",
                            "Pandoc not found",
                            MessageBoxButton.YesNoCancel,
                            MessageBoxImage.Information);

                        if (answer == MessageBoxResult.Yes)
                        {
                            Process.Start(new ProcessStartInfo("https://pandoc.org/")
                                { UseShellExecute = true });
                        }
                        else if (answer == MessageBoxResult.No)
                        {
                            Clipboard.SetText("Install the latest version of Pandoc on this machine using winget.");
                            ShowStatusMessage("✓ Copied prompt to clipboard — paste into AI assistant");
                        }
                    }
                    else
                    {
                        ShowStatusMessage($"✗ Export failed: {error}");
                    }

                    ExportService.CleanupTempDir(tempDir);
                }
                finally
                {
                    // Ensure spinner is stopped on exception (normal path already called StopSpinner)
                    _spinnerTimer?.Stop();
                    _spinnerTimer = null;
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

        private void AiImport_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu
            {
                PlacementTarget = this,
                Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint
            };
            var folderItem = new MenuItem { Header = "📁 Select folder" };
            folderItem.Click += (_, _) =>
            {
                var dlg = new OpenFolderDialog { Title = "Select folder to import" };
                if (dlg.ShowDialog() == true)
                {
                    Clipboard.SetText($"Use mdp import_document to import all .docx and .pptx files to Markdown, then analyze the content and tag images.\n  \"{dlg.FolderName}\"");
                    ShowStatusMessage("✓ Copied prompt to clipboard — paste into AI assistant");
                }
            };
            var fileItem = new MenuItem { Header = "📄 Select files" };
            fileItem.Click += (_, _) =>
            {
                var dlg = new OpenFileDialog
                {
                    Title = "Select files to import",
                    Filter = "Documents (*.docx;*.pptx)|*.docx;*.pptx",
                    Multiselect = true
                };
                if (dlg.ShowDialog() == true)
                {
                    var paths = string.Join("\n", dlg.FileNames.Select(f => $"  \"{f}\""));
                    Clipboard.SetText($"Use mdp import_document to import the following files to Markdown, then analyze the content and tag images.\n{paths}");
                    ShowStatusMessage("✓ Copied prompt to clipboard — paste into AI assistant");
                }
            };
            menu.Items.Add(folderItem);
            menu.Items.Add(fileItem);
            menu.IsOpen = true;
        }

        private void AiTipLink_MouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is Hyperlink hl) hl.TextDecorations = TextDecorations.Underline;
            LinkStatusText.Text = "🗨 Click to copy AI prompt to clipboard";
            UpdateFilePathVisibility();
        }

        private void AiTipLink_MouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is Hyperlink hl) hl.TextDecorations = null;
            LinkStatusText.Text = "";
            UpdateFilePathVisibility();
        }

        private void Hyperlink_GotFocus(object sender, RoutedEventArgs e)
        {
            if (sender is Hyperlink hl) hl.TextDecorations = TextDecorations.Underline;
        }

        private void Hyperlink_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is Hyperlink hl) hl.TextDecorations = null;
        }

        private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            ShowStatusMessage("✓ Opened in browser");
            e.Handled = true;
        }

        private void Hyperlink_MouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is Hyperlink hl)
            {
                hl.TextDecorations = TextDecorations.Underline;
                if (hl.NavigateUri != null)
                {
                    LinkStatusText.Text = $"🔗 {hl.NavigateUri.AbsoluteUri}";
                    UpdateFilePathVisibility();
                }
            }
        }

        private void Hyperlink_MouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is Hyperlink hl) hl.TextDecorations = null;
            LinkStatusText.Text = "";
            UpdateFilePathVisibility();
        }

        #endregion
    }
}
