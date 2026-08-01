using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Windowing;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Services;
using Windows.Graphics;
using WinRT.Interop;

namespace TxtAIEditor.Controls
{
    public sealed class StickyNoteModeController
    {
        private readonly Window _window;
        private readonly IntPtr _windowHandle;
        private readonly UIElement _normalTitleBar;
        private readonly RowDefinition _titleBarRow;
        private readonly StickyNoteBar _stickyNoteBar;
        private readonly UIElement _stickyNoteDragHandle;
        private readonly TopCommandBarPane _topToolbar;
        private readonly FrameworkElement _markdownToolbar;
        private readonly FrameworkElement _statusBar;
        private readonly ShellPanelLayoutService _shellPanelLayoutService;
        private readonly ToggleButton _leftPanelToggle;
        private readonly IStickyNoteService _stickyNoteService;
        private readonly ISettingsService _settingsService;
        private readonly System.Action<bool> _applyLeftSidebarVisibility;
        private readonly System.Action<bool> _applyPreviewVisibility;

        private bool _isActive;
        private bool _wasLeftSidebarVisible;
        private bool _wasRightSidebarVisible;
        private bool _wasMarkdownToolbarVisible;
        private GridLength _normalTitleBarHeight;
        private bool _restorePresenterTitleBar = true;
        private bool _restorePresenterBorder = true;
        private bool _restoreExtendsContentIntoTitleBar = true;
        private SizeInt32 _normalWindowSize;
        private bool _hasNormalWindowSize;
        private bool _wasWindowMaximized;
        private bool _isDraggingWindow;
        private uint _dragPointerId;
        private PointInt32 _dragStartWindowPosition;
        private ScreenPoint _dragStartCursorPosition;

        public StickyNoteModeController(
            Window window,
            UIElement normalTitleBar,
            RowDefinition titleBarRow,
            StickyNoteBar stickyNoteBar,
            UIElement stickyNoteDragHandle,
            TopCommandBarPane topToolbar,
            FrameworkElement markdownToolbar,
            FrameworkElement statusBar,
            ShellPanelLayoutService shellPanelLayoutService,
            ToggleButton leftPanelToggle,
            IStickyNoteService stickyNoteService,
            ISettingsService settingsService,
            System.Action<bool> applyLeftSidebarVisibility,
            System.Action<bool> applyPreviewVisibility)
        {
            _window = window;
            _windowHandle = WindowNative.GetWindowHandle(window);
            _normalTitleBar = normalTitleBar;
            _titleBarRow = titleBarRow;
            _stickyNoteBar = stickyNoteBar;
            _stickyNoteDragHandle = stickyNoteDragHandle;
            _topToolbar = topToolbar;
            _markdownToolbar = markdownToolbar;
            _statusBar = statusBar;
            _shellPanelLayoutService = shellPanelLayoutService;
            _leftPanelToggle = leftPanelToggle;
            _stickyNoteService = stickyNoteService;
            _settingsService = settingsService;
            _applyLeftSidebarVisibility = applyLeftSidebarVisibility;
            _applyPreviewVisibility = applyPreviewVisibility;

            _stickyNoteBar.ExitClick += (_, _) => Exit();
            _stickyNoteBar.TopMostClick += (_, _) => ApplyTopMostFromStickyBar();
            _stickyNoteDragHandle.PointerPressed += OnDragHandlePointerPressed;
            _stickyNoteDragHandle.PointerMoved += OnDragHandlePointerMoved;
            _stickyNoteDragHandle.PointerReleased += OnDragHandlePointerReleased;
            _stickyNoteDragHandle.PointerCaptureLost += OnDragHandlePointerCaptureLost;
        }

        public bool IsActive => _isActive;

        public void CaptureCurrentWindowSizeForPersistence()
        {
            if (!_isActive)
            {
                return;
            }

            CaptureStickyNoteWindowSize();
        }

        public void ApplyTopMostFromToolbar()
        {
            ApplyTopMost(_topToolbar.TopMostIsChecked);
        }

        public void ToggleTopMostFromShortcut()
        {
            bool topMost = !_topToolbar.TopMostIsChecked;
            _topToolbar.TopMostIsChecked = topMost;
            ApplyTopMost(topMost);
        }

        public void ToggleMode()
        {
            if (_isActive)
            {
                Exit();
            }
            else
            {
                Enter();
            }
        }

        private void Enter()
        {
            if (_isActive)
            {
                return;
            }

            _wasWindowMaximized = (_window.AppWindow.Presenter as OverlappedPresenter)?.State == OverlappedPresenterState.Maximized;
            if (_wasWindowMaximized && _window.AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.Restore();
            }

            _normalWindowSize = _window.AppWindow.Size;
            _hasNormalWindowSize = IsUsableWindowSize(_normalWindowSize);
            _isActive = true;
            _wasLeftSidebarVisible = _shellPanelLayoutService.IsLeftSidebarVisible;
            _wasRightSidebarVisible = _shellPanelLayoutService.IsRightSidebarVisible;
            _wasMarkdownToolbarVisible = _markdownToolbar.Visibility == Visibility.Visible;
            _normalTitleBarHeight = _titleBarRow.Height;
            _restoreExtendsContentIntoTitleBar = _window.ExtendsContentIntoTitleBar;

            _stickyNoteBar.TopMostIsChecked = _topToolbar.TopMostIsChecked;
            _normalTitleBar.Visibility = Visibility.Collapsed;
            _titleBarRow.Height = new GridLength(0);
            _stickyNoteBar.Visibility = Visibility.Visible;
            _stickyNoteDragHandle.Visibility = Visibility.Visible;
            _window.SetTitleBar(null);
            _window.ExtendsContentIntoTitleBar = false;
            ApplyPresenterChromeVisible(false);

            _topToolbar.Visibility = Visibility.Collapsed;
            _markdownToolbar.Visibility = Visibility.Collapsed;
            _statusBar.Visibility = Visibility.Collapsed;

            _shellPanelLayoutService.ApplyLeftSidebarVisibility(false);
            _shellPanelLayoutService.ApplyPreviewVisibility(false);
            ResizeWindow(GetStickyNoteWindowSize());
        }

        private void Exit()
        {
            if (!_isActive)
            {
                return;
            }

            CaptureCurrentWindowSizeForPersistence();
            if (_window.AppWindow.Presenter is OverlappedPresenter currentPresenter &&
                currentPresenter.State == OverlappedPresenterState.Maximized)
            {
                currentPresenter.Restore();
            }

            _isActive = false;
            StopWindowDrag();
            bool topMost = _stickyNoteBar.TopMostIsChecked;
            _topToolbar.TopMostIsChecked = topMost;
            _stickyNoteService.ApplyTopMost(_window, topMost);

            _stickyNoteBar.Visibility = Visibility.Collapsed;
            _stickyNoteDragHandle.Visibility = Visibility.Collapsed;
            _titleBarRow.Height = _normalTitleBarHeight;
            _normalTitleBar.Visibility = Visibility.Visible;
            ApplyPresenterChromeVisible(true);
            _window.ExtendsContentIntoTitleBar = _restoreExtendsContentIntoTitleBar;
            _window.SetTitleBar(_normalTitleBar);

            _topToolbar.Visibility = Visibility.Visible;
            _markdownToolbar.Visibility = _wasMarkdownToolbarVisible ? Visibility.Visible : Visibility.Collapsed;
            _statusBar.Visibility = Visibility.Visible;

            _leftPanelToggle.IsChecked = _wasLeftSidebarVisible;
            _applyLeftSidebarVisibility(_wasLeftSidebarVisible);
            _applyPreviewVisibility(_wasRightSidebarVisible);

            if (_hasNormalWindowSize)
            {
                ResizeWindow(_normalWindowSize);
            }

            if (_wasWindowMaximized && _window.AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.Maximize();
            }

            _wasWindowMaximized = false;
            PersistSettings();
        }

        private SizeInt32 GetStickyNoteWindowSize()
        {
            var settings = _settingsService.CurrentSettings;
            if (settings.StickyNoteWindowWidth > 0 && settings.StickyNoteWindowHeight > 0)
            {
                return new SizeInt32(settings.StickyNoteWindowWidth, settings.StickyNoteWindowHeight);
            }

            return _hasNormalWindowSize ? _normalWindowSize : _window.AppWindow.Size;
        }

        private void CaptureStickyNoteWindowSize()
        {
            var presenter = _window.AppWindow.Presenter as OverlappedPresenter;
            bool isRestored = presenter == null || presenter.State == OverlappedPresenterState.Restored;
            var currentSize = _window.AppWindow.Size;
            if (isRestored && IsUsableWindowSize(currentSize))
            {
                var settings = _settingsService.CurrentSettings;
                settings.StickyNoteWindowWidth = currentSize.Width;
                settings.StickyNoteWindowHeight = currentSize.Height;
            }

            if (_hasNormalWindowSize)
            {
                var settings = _settingsService.CurrentSettings;
                settings.WindowWidth = _normalWindowSize.Width;
                settings.WindowHeight = _normalWindowSize.Height;
            }
        }

        private void ResizeWindow(SizeInt32 size)
        {
            if (!IsUsableWindowSize(size))
            {
                return;
            }

            try
            {
                _window.AppWindow.Resize(size);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to resize sticky note window: {ex.Message}");
            }
        }

        private void PersistSettings()
        {
            _ = PersistSettingsAsync();
        }

        private async Task PersistSettingsAsync()
        {
            try
            {
                await _settingsService.SaveSettingsAsync(_settingsService.CurrentSettings);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save sticky note window size: {ex.Message}");
            }
        }

        private static bool IsUsableWindowSize(SizeInt32 size) => size.Width > 0 && size.Height > 0;

        private void ApplyTopMostFromStickyBar()
        {
            bool topMost = _stickyNoteBar.TopMostIsChecked;
            _topToolbar.TopMostIsChecked = topMost;
            _stickyNoteService.ApplyTopMost(_window, topMost);
        }

        private void ApplyTopMost(bool topMost)
        {
            _stickyNoteService.ApplyTopMost(_window, topMost);
            _stickyNoteBar.TopMostIsChecked = topMost;
        }

        private void OnDragHandlePointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (!_isActive || _isDraggingWindow)
            {
                return;
            }

            var point = e.GetCurrentPoint(_stickyNoteDragHandle);
            if (!point.Properties.IsLeftButtonPressed || !GetCursorPos(out ScreenPoint cursorPosition))
            {
                return;
            }

            _dragPointerId = e.Pointer.PointerId;
            _dragStartCursorPosition = cursorPosition;
            if (!GetWindowRect(_windowHandle, out WindowRect windowRect))
            {
                return;
            }

            _dragStartWindowPosition = new PointInt32(windowRect.Left, windowRect.Top);
            _isDraggingWindow = _stickyNoteDragHandle.CapturePointer(e.Pointer);
            e.Handled = _isDraggingWindow;
        }

        private void OnDragHandlePointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_isDraggingWindow || e.Pointer.PointerId != _dragPointerId || !GetCursorPos(out ScreenPoint cursorPosition))
            {
                return;
            }

            var newPosition = new PointInt32(
                _dragStartWindowPosition.X + cursorPosition.X - _dragStartCursorPosition.X,
                _dragStartWindowPosition.Y + cursorPosition.Y - _dragStartCursorPosition.Y);

            if (!SetWindowPos(
                    _windowHandle,
                    IntPtr.Zero,
                    newPosition.X,
                    newPosition.Y,
                    0,
                    0,
                    SetWindowPosNoSize | SetWindowPosNoZOrder | SetWindowPosNoActivate))
            {
                System.Diagnostics.Debug.WriteLine("Failed to move sticky note window.");
            }

            e.Handled = true;
        }

        private void OnDragHandlePointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_isDraggingWindow || e.Pointer.PointerId != _dragPointerId)
            {
                return;
            }

            StopWindowDrag(e);
            e.Handled = true;
        }

        private void OnDragHandlePointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            StopWindowDrag();
        }

        private void StopWindowDrag(PointerRoutedEventArgs? args = null)
        {
            if (!_isDraggingWindow)
            {
                return;
            }

            _isDraggingWindow = false;
            if (args != null)
            {
                _stickyNoteDragHandle.ReleasePointerCapture(args.Pointer);
            }
        }

        private void ApplyPresenterChromeVisible(bool visible)
        {
            if (_window.AppWindow.Presenter is not OverlappedPresenter presenter)
            {
                return;
            }

            if (!visible)
            {
                _restorePresenterTitleBar = presenter.HasTitleBar;
                _restorePresenterBorder = presenter.HasBorder;
                presenter.SetBorderAndTitleBar(_restorePresenterBorder, false);
                return;
            }

            presenter.SetBorderAndTitleBar(_restorePresenterBorder, _restorePresenterTitleBar);
        }

        private const uint SetWindowPosNoSize = 0x0001;
        private const uint SetWindowPosNoZOrder = 0x0004;
        private const uint SetWindowPosNoActivate = 0x0010;

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out ScreenPoint point);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr window, out WindowRect rect);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(
            IntPtr window,
            IntPtr insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);

        [StructLayout(LayoutKind.Sequential)]
        private struct ScreenPoint
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }
    }
}
