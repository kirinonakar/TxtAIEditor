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
        private readonly RightSidebarPane _rightSidebar;
        private readonly EditorWorkspacePane _editorWorkspace;
        private readonly TopCommandBarPane _topToolbar;
        private readonly FrameworkElement _markdownToolbar;
        private readonly FrameworkElement _statusBar;
        private readonly ShellPanelLayoutService _shellPanelLayoutService;
        private readonly ToggleButton _leftPanelToggle;
        private readonly IStickyNoteService _stickyNoteService;
        private readonly ISettingsService _settingsService;
        private readonly System.Action<bool> _applyLeftSidebarVisibility;
        private readonly System.Action<bool> _applyPreviewVisibility;
        private readonly System.Action _refreshTabLayout;

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
        private PointInt32 _normalWindowPosition;
        private bool _hasNormalWindowPosition;
        private bool _wasWindowMaximized;
        private bool _isDraggingWindow;
        private uint _dragPointerId;
        private PointInt32 _dragStartWindowPosition;
        private ScreenPoint _dragStartCursorPosition;
        private bool _isStickyAgentPanelInEditor;
        private bool _stickyAgentPaneRestoreQueued;

        public StickyNoteModeController(
            Window window,
            UIElement normalTitleBar,
            RowDefinition titleBarRow,
            StickyNoteBar stickyNoteBar,
            RightSidebarPane rightSidebar,
            EditorWorkspacePane editorWorkspace,
            UIElement stickyNoteDragHandle,
            TopCommandBarPane topToolbar,
            FrameworkElement markdownToolbar,
            FrameworkElement statusBar,
            ShellPanelLayoutService shellPanelLayoutService,
            ToggleButton leftPanelToggle,
            IStickyNoteService stickyNoteService,
            ISettingsService settingsService,
            System.Action<bool> applyLeftSidebarVisibility,
            System.Action<bool> applyPreviewVisibility,
            System.Action refreshTabLayout)
        {
            _window = window;
            _windowHandle = WindowNative.GetWindowHandle(window);
            _normalTitleBar = normalTitleBar;
            _titleBarRow = titleBarRow;
            _stickyNoteBar = stickyNoteBar;
            _stickyNoteDragHandle = stickyNoteDragHandle;
            _rightSidebar = rightSidebar;
            _editorWorkspace = editorWorkspace;
            _topToolbar = topToolbar;
            _markdownToolbar = markdownToolbar;
            _statusBar = statusBar;
            _shellPanelLayoutService = shellPanelLayoutService;
            _leftPanelToggle = leftPanelToggle;
            _stickyNoteService = stickyNoteService;
            _settingsService = settingsService;
            _applyLeftSidebarVisibility = applyLeftSidebarVisibility;
            _applyPreviewVisibility = applyPreviewVisibility;
            _refreshTabLayout = refreshTabLayout;

            _stickyNoteBar.ExitClick += (_, _) => Exit();
            _stickyNoteBar.TopMostClick += (_, _) => ApplyTopMostFromStickyBar();
            _stickyNoteBar.AgentToggleClick += (_, _) => ApplyAgentPanelFromStickyBar();
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

            CaptureStickyNoteWindowPlacement();
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
            _normalWindowPosition = _window.AppWindow.Position;
            _hasNormalWindowPosition = IsUsableWindowPosition(_normalWindowPosition);
            _isActive = true;
            _wasLeftSidebarVisible = _shellPanelLayoutService.IsLeftSidebarVisible;
            _wasRightSidebarVisible = _shellPanelLayoutService.IsRightSidebarVisible;
            _wasMarkdownToolbarVisible = _markdownToolbar.Visibility == Visibility.Visible;
            _normalTitleBarHeight = _titleBarRow.Height;
            _restoreExtendsContentIntoTitleBar = _window.ExtendsContentIntoTitleBar;

            _stickyNoteBar.TopMostIsChecked = _topToolbar.TopMostIsChecked;
            _stickyNoteBar.AgentIsChecked = false;
            _isStickyAgentPanelInEditor = false;
            RestoreAgentPaneIntoRightSidebar();
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
            ApplySavedStickyNotePosition();
            _refreshTabLayout();
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

            _stickyNoteBar.AgentIsChecked = false;
            // Tear the editor-side host down first: if anything below throws, the agent
            // pane must not be left hidden behind the collapsed editor host.
            HideStickyAgentPanelFromEditor();
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

            // Restore the agent pane only after the right sidebar is visible again so
            // its Agent tab realizes and renders the restored content.
            HideStickyAgentPanelFromEditor();

            if (_hasNormalWindowSize)
            {
                ResizeWindow(_normalWindowSize);
            }

            if (_hasNormalWindowPosition)
            {
                MoveWindow(_normalWindowPosition);
            }

            if (_wasWindowMaximized && _window.AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.Maximize();
            }

            _wasWindowMaximized = false;
            _refreshTabLayout();
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

        private void CaptureStickyNoteWindowPlacement()
        {
            var presenter = _window.AppWindow.Presenter as OverlappedPresenter;
            bool isRestored = presenter == null || presenter.State == OverlappedPresenterState.Restored;
            var currentSize = _window.AppWindow.Size;
            var currentPosition = _window.AppWindow.Position;
            if (isRestored && IsUsableWindowSize(currentSize))
            {
                var settings = _settingsService.CurrentSettings;
                settings.StickyNoteWindowWidth = currentSize.Width;
                settings.StickyNoteWindowHeight = currentSize.Height;
                if (IsUsableWindowPosition(currentPosition))
                {
                    settings.StickyNoteWindowX = currentPosition.X;
                    settings.StickyNoteWindowY = currentPosition.Y;
                }
            }

            if (_hasNormalWindowSize)
            {
                var settings = _settingsService.CurrentSettings;
                settings.WindowWidth = _normalWindowSize.Width;
                settings.WindowHeight = _normalWindowSize.Height;
                if (_hasNormalWindowPosition)
                {
                    settings.WindowX = _normalWindowPosition.X;
                    settings.WindowY = _normalWindowPosition.Y;
                }
            }
        }

        private void ApplySavedStickyNotePosition()
        {
            var settings = _settingsService.CurrentSettings;
            if (!HasSavedWindowPosition(settings.StickyNoteWindowX, settings.StickyNoteWindowY))
            {
                return;
            }

            MoveWindow(new PointInt32(settings.StickyNoteWindowX, settings.StickyNoteWindowY));
        }

        private void MoveWindow(PointInt32 position)
        {
            try
            {
                _window.AppWindow.Move(position);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to move sticky note window: {ex.Message}");
            }
        }

        private static bool HasSavedWindowPosition(int x, int y) => x >= 0 && y >= 0;

        private static bool IsUsableWindowPosition(PointInt32 position) => position.X > -30000 && position.Y > -30000;

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

        private void ApplyAgentPanelFromStickyBar()
        {
            ApplyEditorAgentPanelVisibility(_stickyNoteBar.AgentIsChecked);
        }

        // Sticky note mode swaps the editor display area for the agent panel while
        // leaving the right sidebar's selected tab untouched. Re-hosting the pane
        // can fail, so every step is guarded and rolled back instead of letting an
        // exception escape the click handler (which WinUI turns into a crash).
        private void ApplyEditorAgentPanelVisibility(bool visible)
        {
            if (visible)
            {
                ShowStickyAgentPanelInEditor();
            }
            else
            {
                HideStickyAgentPanelFromEditor();
            }
        }

        private void ShowStickyAgentPanelInEditor()
        {
            if (_isStickyAgentPanelInEditor)
            {
                return;
            }

            try
            {
                UIElement? content = _rightSidebar.AgentPane;
                if (content == null)
                {
                    _stickyNoteBar.AgentIsChecked = false;
                    return;
                }

                _rightSidebar.AgentContentHost.Content = null;

                if (!_editorWorkspace.ShowStickyAgentPanel(content))
                {
                    RestoreAgentPaneIntoRightSidebar();
                    _stickyNoteBar.AgentIsChecked = false;
                    return;
                }

                _isStickyAgentPanelInEditor = true;
            }
            catch (Exception ex)
            {
                LogStickyAgentPanelFailure($"Failed to show the agent pane in the editor: {ex}");
                RestoreAgentPaneIntoRightSidebar();
                _stickyNoteBar.AgentIsChecked = false;
            }
        }

        // Tears the editor-side panel down and always leaves the agent pane back in
        // the right sidebar's Agent tab, which must never be left empty.
        private void HideStickyAgentPanelFromEditor()
        {
            try
            {
                _editorWorkspace.HideStickyAgentPanel();
            }
            catch (Exception ex)
            {
                LogStickyAgentPanelFailure($"Failed to hide the agent pane from the editor: {ex}");
            }

            _isStickyAgentPanelInEditor = false;
            RestoreAgentPaneIntoRightSidebar();
        }

        private void RestoreAgentPaneIntoRightSidebar()
        {
            UIElement? content = _rightSidebar.AgentPane;
            if (content == null)
            {
                return;
            }

            ContentControl host = _rightSidebar.AgentContentHost;
            if (ReferenceEquals(host.Content, content))
            {
                return;
            }

            try
            {
                _editorWorkspace.ReleaseStickyAgentPanelContent();
                host.Content = null;
                host.Content = content;

                if (ReferenceEquals(host.Content, content))
                {
                    return;
                }

                LogStickyAgentPanelFailure("Agent pane content was not accepted by the right sidebar host.");
            }
            catch (Exception ex)
            {
                LogStickyAgentPanelFailure($"Failed to restore the agent pane: {ex.Message}");
            }

            // The editor-side host may still be releasing the pane during this pass;
            // retry on the next dispatcher passes so the Agent tab cannot stay empty
            // after leaving sticky note mode.
            if (_stickyAgentPaneRestoreQueued)
            {
                return;
            }

            _stickyAgentPaneRestoreQueued = true;
            if (!_editorWorkspace.DispatcherQueue.TryEnqueue(() =>
            {
                _stickyAgentPaneRestoreQueued = false;
                RestoreAgentPaneIntoRightSidebar();
            }))
            {
                _stickyAgentPaneRestoreQueued = false;
            }
        }

        // Diagnostics for the sticky-note agent pane re-hosting. The pane must never
        // be left outside the right sidebar, so failures are recorded to a log file
        // that survives the session.
        private static void LogStickyAgentPanelFailure(string message)
        {
            System.Diagnostics.Debug.WriteLine(message);
            try
            {
                string directory = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "TxtAIEditor");
                System.IO.Directory.CreateDirectory(directory);
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(directory, "sticky-agent-panel.log"),
                    $"{DateTime.Now:O} {message}{Environment.NewLine}");
            }
            catch
            {
                // Diagnostics must never surface as user-visible failures.
            }
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
