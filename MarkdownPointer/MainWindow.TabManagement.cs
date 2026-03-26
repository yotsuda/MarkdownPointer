using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;
using MarkdownPointer.Models;
using MarkdownPointer.Services;

namespace MarkdownPointer
{
    // Tab management partial class
    public partial class MainWindow
    {
        #region Public API for Pipe Commands

        public List<TabItemData> GetTabs()
        {
            return _tabs.ToList();
        }

        public int GetSelectedTabIndex()
        {
            return FileTabControl.SelectedIndex;
        }

        public void ScrollToLine(int tabIndex, int line)
        {
            if (tabIndex >= 0 && tabIndex < _tabs.Count)
            {
                var tab = _tabs[tabIndex];
                FileTabControl.SelectedItem = tab;
                ScrollToLine(tab, line);
            }
        }

        public void ScrollToLine(TabItemData tab, int line)
        {
            if (tab.IsInitialized && tab.WebView.CoreWebView2 != null)
            {
                // Use setTimeout to ensure page is fully rendered
                tab.WebView.CoreWebView2.ExecuteScriptAsync($"setTimeout(function() {{ scrollToLine({line}); }}, 100)");
            }
        }

        public TabItemData? FindTabByFilePath(string filePath)
        {
            var normalizedPath = Path.GetFullPath(filePath);
            return _tabs.FirstOrDefault(t =>
                !string.IsNullOrEmpty(t.FilePath) &&
                string.Equals(Path.GetFullPath(t.FilePath), normalizedPath, StringComparison.OrdinalIgnoreCase));
        }

        public void SelectTab(TabItemData tab)
        {
            FileTabControl.SelectedItem = tab;
        }

        public void RefreshTab(TabItemData tab)
        {
            if (tab.IsInitialized)
            {
                RenderMarkdown(tab);
                ShowStatusMessage($"✓ Source reloaded at {DateTime.Now:HH:mm:ss}");
            }
        }

        public void SetSlideView(TabItemData tab, bool slideView)
        {
            if (tab.IsSlideView == slideView) return;
            tab.IsSlideView = slideView;
            UpdateSlideViewButton(tab);
            SlideViewToggle.IsChecked = slideView;
            RenderMarkdown(tab, viewToggle: true);
        }

        public async Task<SlideStateInfo?> GetSlideStateAsync(TabItemData tab)
        {
            if (!tab.IsInitialized || tab.WebView.CoreWebView2 == null || !tab.IsSlideView)
                return null;

            try
            {
                var json = await tab.WebView.CoreWebView2.ExecuteScriptAsync("getSlideState()");
                if (json == "null" || string.IsNullOrEmpty(json))
                    return null;

                var options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var state = System.Text.Json.JsonSerializer.Deserialize<SlideStateInfo>(json, options);
                return state;
            }
            catch
            {
                return null;
            }
        }

        #endregion

        #region Tab Lifecycle

        public TabItemData? LoadMarkdownFile(string filePath, int? line = null, string? title = null, bool isTemp = false, string? renderedHtml = null)
        {
            if (!File.Exists(filePath))
            {
                MessageBox.Show($"File not found: {filePath}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return null;
            }

            // For temp files, always create a new tab (or reuse existing temp tab with same title)
            if (!isTemp)
            {
                // Check if file is already open in this window
                foreach (var tab in _tabs)
                {
                    if (string.Equals(tab.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                    {
                        FileTabControl.SelectedItem = tab;
                        if (line.HasValue)
                        {
                            ScrollToLine(tab, line.Value);
                        }
                        // Return existing tab (errors are cached from last render)
                        tab.RenderCompletion = null;
                        _recentFiles.AddFile(filePath);
                        RefreshSystemMenu();
                        return tab;
                    }
                }

                // Close the file if it's open in another window
                foreach (Window window in Application.Current.Windows)
                {
                    if (window is MainWindow mw && mw != this)
                    {
                        var tabToClose = mw._tabs.FirstOrDefault(t =>
                            string.Equals(t.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
                        if (tabToClose != null)
                        {
                            mw.CloseTab(tabToClose);
                            break;
                        }
                    }
                }
            }
            else
            {
                // For temp files, reuse existing tab with same title if exists
                var displayTitle = title ?? Path.GetFileName(filePath);
                foreach (var tab in _tabs)
                {
                    if (tab.IsTemp && tab.Title == displayTitle)
                    {
                        // Update existing temp tab
                        tab.FilePath = filePath;
                        tab.LastFileWriteTime = File.GetLastWriteTime(filePath);
                        FileTabControl.SelectedItem = tab;
                        SetupFileWatcher(tab);  // Re-setup watcher for new file
                        // Re-render to collect errors
                        tab.RenderCompletion = new TaskCompletionSource<List<string>>();
                        RefreshTab(tab);
                        if (line.HasValue)
                        {
                            ScrollToLine(tab, line.Value);
                        }
                        return tab;
                    }
                }
            }

            // Create new tab
            var newTab = new TabItemData
            {
                FilePath = filePath,
                Title = title ?? Path.GetFileName(filePath),
                WebView = new WebView2(),
                IsTemp = isTemp,
                RenderCompletion = new TaskCompletionSource<List<string>>(),
                RenderedHtml = renderedHtml  // Use cached HTML if available
            };

            // Initialize WebView2 (fire-and-forget, exceptions handled internally)
            _ = InitializeTabWebViewAsync(newTab).ContinueWith(t =>
            {
                if (t.IsFaulted && t.Exception != null)
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        MessageBox.Show($"Failed to initialize WebView2:\n{t.Exception.InnerException?.Message ?? t.Exception.Message}",
                            "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        CloseTab(newTab);
                    });
                }
            }, TaskScheduler.Default);

            _tabs.Add(newTab);
            FileTabControl.SelectedItem = newTab;
            // Ensure selection takes effect after TabControl processes the new item
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
            {
                FileTabControl.SelectedItem = newTab;
            });

            // Record in recent files (skip temp files)
            if (!isTemp)
            {
                _recentFiles.AddFile(filePath);
                RefreshSystemMenu();
            }

            // Make tab control visible so WebView2 can initialize and render.
            // PlaceholderPanel overlays it in the same grid cell, so the user
            // sees the splash until NavigationCompleted hides the overlay.
            FileTabControl.Visibility = Visibility.Visible;

            UpdateWindowTitle();
            return newTab;
        }


        #endregion

        #region Zoom

        private void SetupZoomForTab(TabItemData tab)
        {
            // Animation timer (shared across window)
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
                        var diff = _targetZoomFactor - _currentZoomFactor;

                        if (Math.Abs(diff) < 0.005)
                        {
                            _currentZoomFactor = _targetZoomFactor;
                            ApplyCssZoom(currentTab, _targetZoomFactor);
                            _zoomAnimationTimer.Stop();
                            return;
                        }

                        var step = diff * 0.3;
                        _currentZoomFactor += step;
                        ApplyCssZoom(currentTab, _currentZoomFactor);
                    }
                };
            }

            // Block WebView2's built-in Ctrl+wheel zoom.
            // Actual zoom is handled by JavaScript (CoreEventHandlers.js).
            tab.WebView.PreviewMouseWheel += (s, e) =>
            {
                if (Keyboard.Modifiers == ModifierKeys.Control)
                {
                    e.Handled = true;
                }
            };
        }

        private void ApplyCssZoom(TabItemData tab, double zoomFactor)
        {
            tab.CssZoomFactor = zoomFactor;
            var zoomStr = zoomFactor.ToString(System.Globalization.CultureInfo.InvariantCulture);
            tab.WebView.CoreWebView2?.ExecuteScriptAsync($"document.body.style.zoom = '{zoomStr}'");
        }

        private void ApplyZoomDelta(int delta)
        {
            double notches = delta / 120.0;
            double factor = Math.Pow(ZoomMultiplier, notches);
            _targetZoomFactor = Math.Clamp(_targetZoomFactor * factor, MinZoom, MaxZoom);

            _zoomAnimationTimer?.Start();
        }

        #endregion

        #region File Watcher

        private void SetupFileWatcher(TabItemData tab)
        {
            tab.Watcher?.Dispose();

            var directory = Path.GetDirectoryName(tab.FilePath);
            var fileName = Path.GetFileName(tab.FilePath);

            // Skip watching if directory cannot be obtained
            if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(fileName))
            {
                return;
            }

            tab.Watcher = new FileSystemWatcher(directory)
            {
                Filter = fileName,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
            };

            // Remember original file name for tracking renames
            tab.OriginalFileName = fileName;

            // Debounce timer
            tab.DebounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(200)
            };

            tab.DebounceTimer.Tick += (s, e) =>
            {
                tab.DebounceTimer.Stop();
                // Check if tab still exists
                if (!_tabs.Contains(tab)) return;

                // Check if recovering from deleted state
                var wasDeleted = tab.IsFileDeleted;
                
                // Check if file exists
                if (!File.Exists(tab.FilePath))
                {
                    // File still missing
                    return;
                }

                // File exists now - check if it changed
                var currentWriteTime = File.GetLastWriteTime(tab.FilePath);
                if (!wasDeleted && currentWriteTime == tab.LastFileWriteTime)
                {
                    // No change in timestamp, skip
                    return;
                }

                tab.LastFileWriteTime = currentWriteTime;

                // Restore from deleted state
                if (wasDeleted)
                {
                    tab.IsFileDeleted = false;
                    tab.Title = Path.GetFileName(tab.FilePath);
                }

                RenderMarkdown(tab);
                if (FileTabControl.SelectedItem != tab)
                {
                    FileTabControl.SelectedItem = tab;
                }
                ShowStatusMessage(wasDeleted
                    ? "✓ File is back"
                    : $"✓ Source updated at {currentWriteTime:HH:mm:ss}");
            };

            tab.Watcher.Changed += (s, e) =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    // Check if tab still exists
                    if (!_tabs.Contains(tab)) return;

                    // Always use debounce timer for both normal updates and recovery from deletion
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
                    // Check if tab still exists
                    if (!_tabs.Contains(tab)) return;

                    tab.IsFileDeleted = true;
                    tab.Title = "[Missing] " + Path.GetFileName(tab.FilePath);
                    if (FileTabControl.SelectedItem == tab)
                    {
                        ShowStatusMessage("⚠ File missing");
                    }
                });
            };

            tab.Watcher.Renamed += (s, e) =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    // Check if tab still exists
                    if (!_tabs.Contains(tab)) return;

                    // Check if renamed back to original file name
                    var newFileName = e.Name ?? Path.GetFileName(e.FullPath);
                    var isBackToOriginal = newFileName.Equals(tab.OriginalFileName, StringComparison.OrdinalIgnoreCase);
                    
                    if (isBackToOriginal)
                    {
                        // File was renamed back to original (e.g., from .tmp)
                        var wasDeleted = tab.IsFileDeleted;
                        tab.FilePath = e.FullPath;
                        tab.IsFileDeleted = false;
                        tab.Title = newFileName;
                        
                        if (tab.Watcher != null)
                        {
                            tab.Watcher.Filter = newFileName;
                        }
                        
                        UpdateWindowTitle();
                        tab.LastFileWriteTime = File.GetLastWriteTime(tab.FilePath);
                        RenderMarkdown(tab);
                        ShowStatusMessage("✓ File is back");
                    }
                    else
                    {
                        // Temporary rename (e.g., to .tmp) - do not update filter
                        // Keep watching the original file name
                        if (FileTabControl.SelectedItem == tab)
                        {
                            ShowStatusMessage($"⚠ File renamed to {newFileName}");
                        }
                    }
                });
            };

            tab.Watcher.Created += (s, e) =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    // Check if tab still exists
                    if (!_tabs.Contains(tab)) return;

                    // File was recreated
                    if (tab.IsFileDeleted)
                    {
                        tab.IsFileDeleted = false;
                        tab.Title = Path.GetFileName(tab.FilePath);
                        ShowStatusMessage("✓ File is back");
                    }

                    // Trigger reload
                    tab.DebounceTimer?.Stop();
                    tab.DebounceTimer?.Start();
                });
            };

            tab.Watcher.EnableRaisingEvents = true;
        }

        #endregion

        #region Tab Operations

        private void CloseTab(TabItemData tab)
        {
            tab.Dispose();
            _tabs.Remove(tab);

            if (_tabs.Count == 0)
            {
                // Check if there are other MarkdownPointer windows
                var otherWindows = Application.Current.Windows
                    .OfType<MainWindow>()
                    .Where(w => w != this)
                    .ToList();

                if (otherWindows.Count > 0)
                {
                    // Other windows exist - close this window
                    Close();
                }
                else
                {
                    // This is the last window - show placeholder instead of closing
                    FileTabControl.Visibility = Visibility.Collapsed;
                    PlaceholderPanel.Visibility = Visibility.Visible;
                    SlideViewToggle.IsEnabled = false;
                    Title = "Markdown Pointer";
                    RefreshRecentFiles();
                    LinkStatusText.Text = "";
                    WatchStatusText.Text = "";
                    UpdateFilePathStatus();
                    UpdateFilePathVisibility();
                }
            }
            else
            {
                UpdateWindowTitle();
                // WebView2 of the newly active tab steals keyboard focus after tab change.
                // Redirect focus back to the window so Ctrl+W can be pressed again immediately.
                Dispatcher.InvokeAsync(Focus, System.Windows.Threading.DispatcherPriority.Input);
            }
        }

        private void UpdateWindowTitle()
        {
            if (FileTabControl.SelectedItem is TabItemData tab)
            {
                Title = $"{tab.Title} - Markdown Pointer";
                LinkStatusText.Text = "";
                UpdateFilePathVisibility();
                WatchStatusText.Text = "👁 Watching";
            }
        }

        private static readonly string[] SpinnerFrames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];

        private void StartSpinner(string label)
        {
            _statusMessageTimer?.Stop();
            _statusBlinkTimer?.Stop();
            StatusText.BeginAnimation(OpacityProperty, null);
            StatusText.Opacity = 1.0;
            StatusText.Visibility = Visibility.Visible;
            _spinnerFrame = 0;
            StatusText.Text = $"{SpinnerFrames[0]} {label}";

            _spinnerTimer?.Stop();
            _spinnerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            _spinnerTimer.Tick += (_, _) =>
            {
                _spinnerFrame = (_spinnerFrame + 1) % SpinnerFrames.Length;
                StatusText.Text = $"{SpinnerFrames[_spinnerFrame]} {label}";
            };
            _spinnerTimer.Start();
        }

        private void StopSpinner()
        {
            _spinnerTimer?.Stop();
            _spinnerTimer = null;
            StatusText.Text = "";
        }

        private void ShowStatusMessage(string message, double seconds = 3.0)
        {
            // Stop existing timers first
            _spinnerTimer?.Stop();
            _spinnerTimer = null;
            _statusMessageTimer?.Stop();
            _statusBlinkTimer?.Stop();
            StatusText.BeginAnimation(OpacityProperty, null); // Cancel any running animation
            
            StatusText.Text = message;
            StatusText.Opacity = 1.0;
            StatusText.Visibility = Visibility.Visible;
            var flashCount = 0;
            
            _statusBlinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _statusBlinkTimer.Tick += (s, args) =>
            {
                flashCount++;
                StatusText.Visibility = (flashCount % 2 == 1) ? Visibility.Hidden : Visibility.Visible;
                
                if (flashCount >= 4) // 2 blinks
                {
                    _statusBlinkTimer.Stop();
                    StatusText.Visibility = Visibility.Visible;
                    
                    // After specified seconds, fade out and clear message
                    _statusMessageTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
                    _statusMessageTimer.Tick += (s2, args2) =>
                    {
                        _statusMessageTimer.Stop();
                        var fadeOut = new System.Windows.Media.Animation.DoubleAnimation
                        {
                            From = 1.0,
                            To = 0.0,
                            Duration = TimeSpan.FromMilliseconds(200),
                            EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
                        };
                        fadeOut.Completed += (s3, args3) =>
                        {
                            StatusText.Text = "";
                            StatusText.Opacity = 1.0;
                        };
                        StatusText.BeginAnimation(OpacityProperty, fadeOut);
                    };
                    _statusMessageTimer.Start();
                }
            };
            _statusBlinkTimer.Start();
        }

        private async void RenderMarkdown(TabItemData tab, bool viewToggle = false)
        {
            try
            {
                // Save current position before re-rendering
                if (tab.IsInitialized && tab.WebView.CoreWebView2 != null)
                {
                    try
                    {
                        if (tab.IsSlideView)
                        {
                            var indexJson = await tab.WebView.CoreWebView2.ExecuteScriptAsync(
                                "typeof Reveal !== 'undefined' && Reveal.isReady() ? Reveal.getSlides().indexOf(Reveal.getCurrentSlide()) : -1");
                            if (int.TryParse(indexJson, out var idx))
                                tab.SavedSlideIndex = idx;
                        }
                        else
                        {
                            var scrollPosJson = await tab.WebView.CoreWebView2.ExecuteScriptAsync("window.scrollY");
                            if (double.TryParse(scrollPosJson, out var scrollPos))
                                tab.SavedScrollPosition = scrollPos;
                        }
                    }
                    catch
                    {
                        // Ignore errors when getting position
                    }
                }

                // Fast path: view toggle with cached HTML
                if (viewToggle)
                {
                    var cached = tab.IsSlideView ? tab.CachedSlideHtml : tab.CachedDocHtml;
                    if (cached != null)
                    {
                        tab.RenderedHtml = cached;
                        tab.WebView.NavigateToString(cached);
                        if (tab.IsSlideView)
                            ShowStatusMessage($"🎞 Slide view ({_slideTheme})");
                        return;
                    }
                }

                // Read file asynchronously with retry for locked files
                string? markdown = null;
                for (int i = 0; i < 3; i++)
                {
                    try
                    {
                        markdown = await File.ReadAllTextAsync(tab.FilePath, Encoding.UTF8);
                        break;
                    }
                    catch (IOException) when (i < 2)
                    {
                        await Task.Delay(100);
                    }
                }

                // Check if tab still exists after async operation
                if (!_tabs.Contains(tab)) return;
                if (markdown == null) return;

                // Invalidate caches on file change (not on view toggle)
                if (!viewToggle)
                {
                    tab.CachedSlideHtml = null;
                    tab.CachedDocHtml = null;
                }

                string html;
                if (tab.IsSlideView)
                {
                    StartSpinner("Rendering slides");

                    var slideHtml = await SlideService.RenderSlidesAsync(tab.FilePath, markdown, _slideTheme);

                    StopSpinner();

                    if (slideHtml == null)
                    {
                        // Fallback to document view if Pandoc fails
                        ShowStatusMessage("⚠ Slide rendering failed, falling back to document view");
                        tab.IsSlideView = false;
                        Dispatcher.Invoke(() => UpdateSlideViewButton(tab));
                        var baseDir2 = Path.GetDirectoryName(tab.FilePath) ?? ".";
                        html = GetHtmlGenerator().ConvertToHtml(markdown, baseDir2);
                        tab.CachedDocHtml = html;
                    }
                    else
                    {
                        html = slideHtml;
                        tab.CachedSlideHtml = html;
                        ShowStatusMessage($"🎞 Slide view ({_slideTheme})");
                    }

                    // Pre-cache document view
                    if (tab.CachedDocHtml == null)
                    {
                        var baseDir = Path.GetDirectoryName(tab.FilePath);
                        tab.CachedDocHtml = GetHtmlGenerator().ConvertToHtml(markdown, baseDir!);
                    }
                }
                else
                {
                    var baseDir = Path.GetDirectoryName(tab.FilePath) ?? ".";
                    html = GetHtmlGenerator().ConvertToHtml(markdown, baseDir);
                    tab.CachedDocHtml = html;
                }

                // Check if tab still exists after async rendering
                if (!_tabs.Contains(tab)) return;

                tab.RenderedHtml = html;  // Cache for fast window detach

                tab.WebView.NavigateToString(html);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Markdown rendering error: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion
    }
}