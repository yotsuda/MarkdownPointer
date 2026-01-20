using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MarkdownViewer.Helpers;
using MarkdownViewer.Models;

namespace MarkdownViewer
{
    // Tab and file drag/drop partial class
    public partial class MainWindow
    {
        #region Tab Header Drag Events

        private void TabHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _tabDragStartPoint = e.GetPosition(this);
            _isTabDragging = false;
        }

        private void TabHeader_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                _isTabDragging = false;
                return;
            }

            if (sender is not StackPanel panel || panel.Tag is not TabItemData tab)
                return;

            var currentPos = e.GetPosition(this);
            var diff = _tabDragStartPoint - currentPos;

            // Check if moved enough to start drag
            if (!_isTabDragging &&
                (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                 Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance))
            {
                _isTabDragging = true;
                _draggedTab = tab;

                // Get tab header's screen position and convert to DIP
                var tabScreenPosPhysical = panel.PointToScreen(new Point(0, 0));
                var tabScreenPos = PhysicalToDip(tabScreenPosPhysical);

                // Record tab offset from window origin (including title bar)
                _tabOffsetInWindow = new Point(tabScreenPos.X - Left, tabScreenPos.Y - Top);

                // Record first tab's offset (for new window positioning when detaching)
                var tabIndex = _tabs.IndexOf(tab);
                if (tabIndex == 0)
                {
                    _firstTabOffsetInWindow = _tabOffsetInWindow;
                }
                else
                {
                    // Calculate X difference between dragged tab and first tab
                    double xOffset = 0;
                    for (int i = 0; i < tabIndex; i++)
                    {
                        var container = FileTabControl.ItemContainerGenerator.ContainerFromIndex(i) as TabItem;
                        if (container != null)
                        {
                            xOffset += container.ActualWidth;
                        }
                    }
                    _firstTabOffsetInWindow = new Point(_tabOffsetInWindow.X - xOffset, _tabOffsetInWindow.Y);
                }

                // Record initial cursor position (DIP) and window position (DIP)
                _dragStartCursorPos = GetCursorPosDip();
                _dragStartWindowPos = tabScreenPos;

                // Create preview window at tab's actual position
                CreateDragPreviewWindow(tab, tabScreenPos, panel.ActualWidth, panel.ActualHeight);

                // Start drag operation - use Text format for cross-window compatibility
                var data = new DataObject();
                data.SetData(DataFormats.Text, "MDVIEWER:" + tab.FilePath);
                var result = DragDrop.DoDragDrop(panel, data, DragDropEffects.Move);

                // Cleanup preview window
                CloseDragPreviewWindow();

                // Check drop result using cursor position
                var dropCursorPos = GetCursorPosDip();
                var windowRect = new Rect(Left, Top, Width, Height);

                // Calculate where the tab should be based on cursor movement
                var cursorDelta = new Point(
                    dropCursorPos.X - _dragStartCursorPos.X,
                    dropCursorPos.Y - _dragStartCursorPos.Y);
                var tabDropPos = new Point(
                    _dragStartWindowPos.X + cursorDelta.X,
                    _dragStartWindowPos.Y + cursorDelta.Y);

                if (!windowRect.Contains(dropCursorPos))
                {
                    // Dropped outside this window - check if over another MarkdownViewer window
                    var targetWindow = FindWindowAtPosition(dropCursorPos);

                    if (targetWindow != null)
                    {
                        // Drop on another MarkdownViewer window - transfer the tab instance
                        TransferTabToWindow(tab, targetWindow);
                        targetWindow.Activate();
                    }
                    else if (_tabs.Count > 1)
                    {
                        // Dropped on empty space with multiple tabs - create new window
                        DetachTabToNewWindow(tab, tabDropPos);
                    }
                    else
                    {
                        // Only one tab - move window so tab aligns with drop position
                        Left = tabDropPos.X - _tabOffsetInWindow.X;
                        Top = tabDropPos.Y - _tabOffsetInWindow.Y;
                    }
                }
                else
                {
                    // Dropped inside this window - reorder tabs using saved index
                    if (_tabDropTargetIndex >= 0)
                    {
                        var sourceIndex = _tabs.IndexOf(tab);
                        var targetIndex = _tabDropTargetIndex;
                        if (sourceIndex < targetIndex) targetIndex--;
                        if (sourceIndex != targetIndex && targetIndex >= 0 && targetIndex < _tabs.Count)
                        {
                            _tabs.Move(sourceIndex, targetIndex);
                        }
                    }
                }

                // Hide drop indicators on all windows
                foreach (var window in Application.Current.Windows.OfType<MainWindow>())
                {
                    window.HideTabDropIndicator();
                }
                _isTabDragging = false;
                _draggedTab = null;
            }
        }

        #endregion

        #region Drag Preview Window

        private void CreateDragPreviewWindow(TabItemData tab, Point tabScreenPos, double tabWidth, double tabHeight)
        {
            // Use actual tab size with some padding
            var previewWidth = Math.Max(tabWidth + 16, 100);
            var previewHeight = Math.Max(tabHeight + 8, 28);

            _dragPreviewWindow = new Window
            {
                Width = previewWidth,
                Height = previewHeight,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                Topmost = true,
                ShowInTaskbar = false,
                IsHitTestVisible = false
            };

            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0, 120, 215)),
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 4, 8, 4)
            };

            var textBlock = new TextBlock
            {
                Text = tab.Title,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            border.Child = textBlock;
            _dragPreviewWindow.Content = border;

            // Position at tab's screen position (adjust for border)
            _dragPreviewWindow.Left = tabScreenPos.X - 8;
            _dragPreviewWindow.Top = tabScreenPos.Y - 4;

            _dragPreviewWindow.Show();
        }

        private void CloseDragPreviewWindow()
        {
            if (_dragPreviewWindow != null)
            {
                _dragPreviewWindow.Close();
                _dragPreviewWindow = null;
            }
        }

        #endregion

        #region Drag Feedback Overrides

        protected override void OnQueryContinueDrag(QueryContinueDragEventArgs e)
        {
            base.OnQueryContinueDrag(e);

            if (!_isTabDragging || _draggedTab == null)
                return;

            // Update preview window position based on cursor movement from start (using DIP coordinates)
            var currentCursor = GetCursorPosDip();
            if (_dragPreviewWindow != null)
            {
                var deltaX = currentCursor.X - _dragStartCursorPos.X;
                var deltaY = currentCursor.Y - _dragStartCursorPos.Y;
                _dragPreviewWindow.Left = _dragStartWindowPos.X - 8 + deltaX;
                _dragPreviewWindow.Top = _dragStartWindowPos.Y - 4 + deltaY;
            }

            // Update drop indicator if cursor is over tab strip
            UpdateDropIndicatorFromScreenPos(currentCursor);
        }

        protected override void OnGiveFeedback(GiveFeedbackEventArgs e)
        {
            base.OnGiveFeedback(e);

            if (_isTabDragging)
            {
                e.UseDefaultCursors = false;

                // Check if cursor is over another MarkdownViewer window
                var screenPoint = GetCursorPosDip();
                var targetWindow = FindWindowAtPosition(screenPoint);

                if (targetWindow != null)
                {
                    // Over another window - show hand cursor (will merge)
                    Mouse.SetCursor(Cursors.Hand);
                }
                else
                {
                    // Not over another window - show move cursor (will create new window)
                    Mouse.SetCursor(Cursors.SizeAll);
                }

                e.Handled = true;
            }
        }

        protected override void OnDrop(DragEventArgs e)
        {
            // Handle tab detach drop - this fires when dropped back on same window
            base.OnDrop(e);
        }

        protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnPreviewMouseLeftButtonUp(e);
            _isTabDragging = false;
        }

        #endregion

        #region Drop Indicator

        private void UpdateTabDropIndicator(Point pos)
        {
            _tabDropTargetIndex = -1;
            double indicatorX = 0;

            for (int i = 0; i < _tabs.Count; i++)
            {
                var container = FileTabControl.ItemContainerGenerator.ContainerFromIndex(i) as TabItem;
                if (container == null) continue;

                var tabPos = container.TransformToAncestor(FileTabControl).Transform(new Point(0, 0));
                var tabWidth = container.ActualWidth;
                var tabMidX = tabPos.X + tabWidth / 2;

                if (pos.X < tabMidX)
                {
                    _tabDropTargetIndex = i;
                    indicatorX = tabPos.X;
                    break;
                }
                else
                {
                    _tabDropTargetIndex = i + 1;
                    indicatorX = tabPos.X + tabWidth;
                }
            }

            if (_tabDropTargetIndex >= 0)
            {
                // Position the indicator
                Canvas.SetLeft(TabDropIndicator, indicatorX - 1);
                Canvas.SetTop(TabDropIndicator, 2);
                TabDropIndicator.Visibility = Visibility.Visible;
            }
        }

        public void HideTabDropIndicator()
        {
            TabDropIndicator.Visibility = Visibility.Collapsed;
            _tabDropTargetIndex = -1;
        }

        private void UpdateDropIndicatorFromScreenPos(Point screenPos)
        {
            // Check if cursor is over this window's tab strip area
            var windowRect = new Rect(Left, Top, Width, Height);
            if (!windowRect.Contains(screenPos))
            {
                HideTabDropIndicator();

                // Check if over another MarkdownViewer window
                var targetWindow = FindWindowAtPosition(screenPos);
                if (targetWindow != null)
                {
                    targetWindow.ShowDropIndicatorAtScreenPos(screenPos);
                }
                return;
            }

            // Hide indicators on other windows
            foreach (var window in Application.Current.Windows.OfType<MainWindow>())
            {
                if (window != this)
                {
                    window.HideTabDropIndicator();
                }
            }

            // Convert screen position to TabControl position
            var tabControlScreenPos = FileTabControl.PointToScreen(new Point(0, 0));
            var tabControlPos = PhysicalToDip(tabControlScreenPos);
            var localPos = new Point(screenPos.X - tabControlPos.X, screenPos.Y - tabControlPos.Y);

            // Check if within tab strip height (roughly first 30 pixels)
            if (localPos.Y < 0 || localPos.Y > 30)
            {
                HideTabDropIndicator();
                return;
            }

            UpdateTabDropIndicator(localPos);
        }

        public void ShowDropIndicatorAtScreenPos(Point screenPos)
        {
            // Convert screen position to TabControl position
            var tabControlScreenPos = FileTabControl.PointToScreen(new Point(0, 0));
            var tabControlPos = PhysicalToDip(tabControlScreenPos);
            var localPos = new Point(screenPos.X - tabControlPos.X, screenPos.Y - tabControlPos.Y);

            // Check if within tab strip height
            if (localPos.Y < 0 || localPos.Y > 30)
            {
                HideTabDropIndicator();
                return;
            }

            UpdateTabDropIndicator(localPos);
        }

        #endregion

        #region Tab Transfer Between Windows

        private void TransferTabToWindow(TabItemData tab, MainWindow targetWindow)
        {
            // Move the tab instance to another window (no re-initialization)

            // Remove tab from this window's collection (don't dispose WebView2)
            tab.Watcher?.Dispose();
            tab.Watcher = null;
            tab.DebounceTimer?.Stop();
            tab.DebounceTimer = null;
            _tabs.Remove(tab);

            // Update this window's state
            if (_tabs.Count == 0)
            {
                // Check if there are other MarkdownViewer windows
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
                    // This is the last window - show placeholder
                    FileTabControl.Visibility = Visibility.Collapsed;
                    PlaceholderPanel.Visibility = Visibility.Visible;
                    Title = "Markdown Viewer";
                }
            }
            else
            {
                UpdateWindowTitle();
            }

            // Add tab to target window at drop position
            if (targetWindow._tabDropTargetIndex >= 0 && targetWindow._tabDropTargetIndex <= targetWindow._tabs.Count)
            {
                targetWindow._tabs.Insert(targetWindow._tabDropTargetIndex, tab);
            }
            else
            {
                targetWindow._tabs.Add(tab);
            }
            targetWindow.FileTabControl.SelectedItem = tab;
            targetWindow.PlaceholderPanel.Visibility = Visibility.Collapsed;
            targetWindow.FileTabControl.Visibility = Visibility.Visible;
            targetWindow.UpdateWindowTitle();

            // Re-setup file watcher in target window context
            targetWindow.SetupFileWatcher(tab);

            // Ensure WebView is enabled/disabled based on target window's drag mode
            tab.WebView.IsEnabled = !targetWindow._isDragMoveMode;

            // Sync pointing mode state with target window
            if (tab.IsInitialized && tab.WebView.CoreWebView2 != null)
            {
                tab.WebView.CoreWebView2.ExecuteScriptAsync($"setPointingMode({(targetWindow._isPointingMode ? "true" : "false")})");
            }

            // Update owner window reference for message routing
            tab.OwnerWindow = targetWindow;
        }

        private void DetachTabToNewWindow(TabItemData tab, Point tabDropPos)
        {
            // Move the tab instance directly to the new window (no re-initialization)
            var windowWidth = Width;
            var windowHeight = Height;

            // Remove tab from this window's collection (don't dispose WebView2)
            tab.Watcher?.Dispose();
            tab.Watcher = null;
            tab.DebounceTimer?.Stop();
            tab.DebounceTimer = null;
            _tabs.Remove(tab);

            // Update this window's state
            if (_tabs.Count == 0)
            {
                FileTabControl.Visibility = Visibility.Collapsed;
                PlaceholderPanel.Visibility = Visibility.Visible;
                Title = "Markdown Viewer";
            }
            else
            {
                UpdateWindowTitle();
            }

            // Create new window at drop position
            var newWindow = new MainWindow();
            newWindow.WindowStartupLocation = WindowStartupLocation.Manual;
            newWindow.Width = windowWidth;
            newWindow.Height = windowHeight;
            newWindow.Left = tabDropPos.X - _firstTabOffsetInWindow.X;
            newWindow.Top = tabDropPos.Y - _firstTabOffsetInWindow.Y;
            newWindow.Show();

            // Set position again after Show() in case it was overridden
            newWindow.Left = tabDropPos.X - _firstTabOffsetInWindow.X;
            newWindow.Top = tabDropPos.Y - _firstTabOffsetInWindow.Y;

            // Add tab to new window (reuse the same WebView2 instance)
            newWindow._tabs.Add(tab);
            newWindow.FileTabControl.SelectedItem = tab;
            newWindow.PlaceholderPanel.Visibility = Visibility.Collapsed;
            newWindow.FileTabControl.Visibility = Visibility.Visible;
            newWindow.UpdateWindowTitle();

            // Re-setup file watcher in new window context
            newWindow.SetupFileWatcher(tab);

            // Inherit pointing mode state from source window
            newWindow._isPointingMode = _isPointingMode;
            newWindow.PointingModeToggle.IsChecked = _isPointingMode;
            if (tab.IsInitialized && tab.WebView.CoreWebView2 != null)
            {
                tab.WebView.CoreWebView2.ExecuteScriptAsync($"setPointingMode({(_isPointingMode ? "true" : "false")})");
            }

            // Inherit drag/pan mode state from source window
            newWindow._isDragMoveMode = _isDragMoveMode;
            newWindow.DragMoveToggle.IsChecked = _isDragMoveMode;
            newWindow.DragOverlay.Visibility = _isDragMoveMode ? Visibility.Visible : Visibility.Collapsed;
            tab.WebView.IsEnabled = !_isDragMoveMode;

            // Inherit topmost state from source window
            newWindow.Topmost = Topmost;
            newWindow.TopmostToggle.IsChecked = Topmost;

            // Update owner window reference for message routing
            tab.OwnerWindow = newWindow;
        }

        #endregion

        #region File Drop

        private void Window_Drop(object sender, DragEventArgs e)
        {
            // Handle tab drop from another MarkdownViewer window
            if (e.Data.GetDataPresent(DataFormats.Text))
            {
                var text = e.Data.GetData(DataFormats.Text) as string;
                if (!string.IsNullOrEmpty(text) && text.StartsWith("MDVIEWER:"))
                {
                    var filePath = text.Substring(9); // Remove "MDVIEWER:" prefix
                    if (IsSupportedFile(filePath))
                    {
                        LoadMarkdownFile(filePath);
                        e.Effects = DragDropEffects.Move;
                        e.Handled = true;
                        return;
                    }
                }
            }

            // Handle file drop from Explorer
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                foreach (var file in files)
                {
                    if (IsSupportedFile(file))
                    {
                        LoadMarkdownFile(file);
                    }
                }
            }
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            // Accept tab drops from other MarkdownViewer windows
            if (e.Data.GetDataPresent(DataFormats.Text))
            {
                var text = e.Data.GetData(DataFormats.Text) as string;
                if (!string.IsNullOrEmpty(text) && text.StartsWith("MDVIEWER:"))
                {
                    e.Effects = DragDropEffects.Move;
                    e.Handled = true;
                    return;
                }
            }

            // Accept file drops from Explorer
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

        private static bool IsSupportedFile(string filePath)
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            return Array.Exists(SupportedExtensions, e => e == ext);
        }

        #endregion
    }
}
