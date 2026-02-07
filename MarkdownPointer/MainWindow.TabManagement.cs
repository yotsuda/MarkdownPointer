using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;
using MarkdownPointer.Models;

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

            // Hide placeholder, show tab control
            PlaceholderPanel.Visibility = Visibility.Collapsed;
            FileTabControl.Visibility = Visibility.Visible;

            UpdateWindowTitle();
            return newTab;
        }


        #endregion

        #region Zoom

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
                        var currentZoom = currentTab.WebView.ZoomFactor;
                        var diff = _targetZoomFactor - currentZoom;

                        if (Math.Abs(diff) < 0.005)
                        {
                            currentTab.WebView.ZoomFactor = _targetZoomFactor;
                            _zoomAnimationTimer.Stop();
                            AdjustWindowSizeForZoom(_targetZoomFactor);
                            return;
                        }

                        var step = diff * 0.3;
                        var newZoom = currentZoom + step;
                        currentTab.WebView.ZoomFactor = newZoom;

                        // Call directly in drag move mode as ZoomFactorChanged does not fire
                        if (_isDragMoveMode)
                        {
                            AdjustWindowSizeForZoom(newZoom);
                        }
                    }
                };
            }

            // Mouse wheel handling
            tab.WebView.PreviewMouseWheel += (s, e) =>
            {
                if (Keyboard.Modifiers == ModifierKeys.Control)
                {
                    e.Handled = true;
                    ApplyZoomDelta(e.Delta);
                }
            };
        }

        private void AdjustWindowSizeForZoom(double zoomFactor)
        {
            // Window size is now fixed - zoom only affects content
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
                    Title = "Markdown Viewer";
                    LinkStatusText.Text = "";
                    WatchStatusText.Text = "";
                }
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
                Title = $"{tab.Title} - Markdown Viewer";
                LinkStatusText.Text = "";
                WatchStatusText.Text = "👁 Watching";
            }
        }

        private void ShowStatusMessage(string message, double seconds = 3.0)
        {
            // Stop existing timers first
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

        private async void RenderMarkdown(TabItemData tab)
        {
            try
            {
                // Save current scroll position before re-rendering
                if (tab.IsInitialized && tab.WebView.CoreWebView2 != null)
                {
                    try
                    {
                        var scrollPosJson = await tab.WebView.CoreWebView2.ExecuteScriptAsync("window.scrollY");
                        if (double.TryParse(scrollPosJson, out var scrollPos))
                        {
                            tab.SavedScrollPosition = scrollPos;
                        }
                    }
                    catch
                    {
                        // Ignore errors when getting scroll position
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

                var baseDir = Path.GetDirectoryName(tab.FilePath);
                var html = _htmlGenerator.ConvertToHtml(markdown!, baseDir!);
                tab.RenderedHtml = html;  // Cache for fast window detach
                
                tab.WebView.NavigateToString(html);

                // Bring window to front after rendering
                BringToFront();
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