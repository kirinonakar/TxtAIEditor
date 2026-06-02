using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace TxtAIEditor.Core.Services
{
    public sealed class ShellPanelLayoutService
    {
        private const double ExplorerPanelMinWidth = 150;
        private const double PreviewPanelMinWidth = 150;
        private const double NormalPreviewWidth = 400;
        private const double ExpandedPreviewWidth = 800;

        private readonly Grid _mainWorkGrid;
        private readonly ColumnDefinition _explorerColumn;
        private readonly ColumnDefinition _previewColumn;
        private readonly UIElement _leftSplitter;
        private readonly UIElement _rightSplitter;
        private readonly FrameworkElement _leftSidebar;
        private readonly FrameworkElement _rightSidebar;

        private bool _isDraggingLeftSplitter = false;
        private double _leftSplitterStartExplorerWidth = 0;
        private double _leftSplitterStartPointerX = 0;

        private bool _isDraggingRightSplitter = false;
        private double _rightSplitterStartPreviewWidth = 0;
        private double _rightSplitterStartPointerX = 0;
        private double _lastExplorerWidth = 260;
        private double _lastPreviewWidth = 400;
        private bool _isPreviewExpanded = false;

        public ShellPanelLayoutService(
            Grid mainWorkGrid,
            ColumnDefinition explorerColumn,
            ColumnDefinition previewColumn,
            UIElement leftSplitter,
            UIElement rightSplitter,
            FrameworkElement leftSidebar,
            FrameworkElement rightSidebar)
        {
            _mainWorkGrid = mainWorkGrid;
            _explorerColumn = explorerColumn;
            _previewColumn = previewColumn;
            _leftSplitter = leftSplitter;
            _rightSplitter = rightSplitter;
            _leftSidebar = leftSidebar;
            _rightSidebar = rightSidebar;

            _mainWorkGrid.SizeChanged += (s, e) => EnsurePreviewWidthWithinBounds();
        }

        public bool IsLeftSidebarVisible => _leftSidebar.Visibility == Visibility.Visible;
        public bool IsRightSidebarVisible => _rightSidebar.Visibility == Visibility.Visible;
        public bool IsPreviewExpanded => _isPreviewExpanded;

        public void ApplyLeftSidebarVisibility(bool show)
        {
            _explorerColumn.MinWidth = ExplorerPanelMinWidth;
            if (show)
            {
                _explorerColumn.MinWidth = ExplorerPanelMinWidth;
                _explorerColumn.Width = new GridLength(Math.Max(_lastExplorerWidth, _explorerColumn.MinWidth));
                _leftSplitter.Visibility = Visibility.Visible;
                _leftSidebar.Visibility = Visibility.Visible;

                EnsurePreviewWidthWithinBounds();
            }
            else
            {
                double currentWidth = _leftSidebar.ActualWidth > 0 ? _leftSidebar.ActualWidth : _explorerColumn.Width.Value;
                if (currentWidth > 0)
                {
                    _lastExplorerWidth = currentWidth;
                }

                _explorerColumn.MinWidth = 0;
                _explorerColumn.Width = new GridLength(0);
                _leftSplitter.Visibility = Visibility.Collapsed;
                _leftSidebar.Visibility = Visibility.Collapsed;
            }
        }

        public void ApplyPreviewVisibility(bool show)
        {
            if (!show)
            {
                double currentWidth = _rightSidebar.ActualWidth > 0 ? _rightSidebar.ActualWidth : _previewColumn.Width.Value;
                if (currentWidth > 0)
                {
                    _lastPreviewWidth = currentWidth;
                }

                _previewColumn.MinWidth = 0;
                _previewColumn.Width = new GridLength(0);
                _rightSplitter.Visibility = Visibility.Collapsed;
                _rightSidebar.Visibility = Visibility.Collapsed;
            }
            else
            {
                _previewColumn.MinWidth = PreviewPanelMinWidth;
                _previewColumn.Width = new GridLength(Math.Max(_lastPreviewWidth, _previewColumn.MinWidth));
                _rightSplitter.Visibility = Visibility.Visible;
                _rightSidebar.Visibility = Visibility.Visible;
            }
        }

        private double GetMaxAvailablePreviewWidth()
        {
            double totalWidth = _mainWorkGrid.ActualWidth;
            if (totalWidth <= 0) return ExpandedPreviewWidth;

            double explorerWidth = IsLeftSidebarVisible ? _explorerColumn.ActualWidth : 0;
            double leftSplitterWidth = IsLeftSidebarVisible ? 12 : 0;
            double rightSplitterWidth = 12;
            double minEditorWidth = 300; // EditorColumn MinWidth

            double maxAvailable = totalWidth - explorerWidth - leftSplitterWidth - rightSplitterWidth - minEditorWidth;
            return Math.Max(PreviewPanelMinWidth, maxAvailable);
        }

        public void EnsurePreviewWidthWithinBounds()
        {
            if (!IsRightSidebarVisible) return;

            double currentWidth = _previewColumn.Width.Value;
            double maxAvailable = GetMaxAvailablePreviewWidth();
            if (currentWidth > maxAvailable)
            {
                _previewColumn.Width = new GridLength(maxAvailable);
            }
        }

        public void TogglePreviewWidth()
        {
            if (!IsRightSidebarVisible) return;

            _isPreviewExpanded = !_isPreviewExpanded;
            double targetWidth = _isPreviewExpanded ? ExpandedPreviewWidth : NormalPreviewWidth;

            double maxAvailable = GetMaxAvailablePreviewWidth();
            if (targetWidth > maxAvailable)
            {
                targetWidth = maxAvailable;
            }

            _previewColumn.Width = new GridLength(targetWidth);
            _lastPreviewWidth = targetWidth;
        }

        public void OnLeftSplitterPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (sender is UIElement splitter)
            {
                _isDraggingLeftSplitter = true;
                _leftSplitterStartExplorerWidth = _explorerColumn.Width.Value;
                var pt = e.GetCurrentPoint(_mainWorkGrid).Position;
                _leftSplitterStartPointerX = pt.X;
                splitter.CapturePointer(e.Pointer);
                e.Handled = true;
            }
        }

        public void OnLeftSplitterPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_isDraggingLeftSplitter)
            {
                var pt = e.GetCurrentPoint(_mainWorkGrid).Position;
                double deltaX = pt.X - _leftSplitterStartPointerX;
                double newWidth = _leftSplitterStartExplorerWidth + deltaX;
                newWidth = Math.Clamp(newWidth, _explorerColumn.MinWidth, _explorerColumn.MaxWidth);
                _explorerColumn.Width = new GridLength(newWidth);

                EnsurePreviewWidthWithinBounds();

                e.Handled = true;
            }
        }

        public void OnLeftSplitterPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_isDraggingLeftSplitter && sender is UIElement splitter)
            {
                _isDraggingLeftSplitter = false;
                splitter.ReleasePointerCapture(e.Pointer);
                e.Handled = true;
            }
        }

        public void OnRightSplitterPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (sender is UIElement splitter)
            {
                _isDraggingRightSplitter = true;
                _rightSplitterStartPreviewWidth = _previewColumn.Width.Value;
                var pt = e.GetCurrentPoint(_mainWorkGrid).Position;
                _rightSplitterStartPointerX = pt.X;
                splitter.CapturePointer(e.Pointer);
                e.Handled = true;
            }
        }

        public void OnRightSplitterPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_isDraggingRightSplitter)
            {
                var pt = e.GetCurrentPoint(_mainWorkGrid).Position;
                double deltaX = pt.X - _rightSplitterStartPointerX;
                double newWidth = _rightSplitterStartPreviewWidth - deltaX;

                double maxAvailable = GetMaxAvailablePreviewWidth();
                newWidth = Math.Clamp(newWidth, _previewColumn.MinWidth, Math.Min(_previewColumn.MaxWidth, maxAvailable));

                _previewColumn.Width = new GridLength(newWidth);
                e.Handled = true;
            }
        }

        public void OnRightSplitterPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_isDraggingRightSplitter && sender is UIElement splitter)
            {
                _isDraggingRightSplitter = false;
                splitter.ReleasePointerCapture(e.Pointer);
                e.Handled = true;
            }
        }
    }
}
