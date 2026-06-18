using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Windowing;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Services;

namespace TxtAIEditor.Controls
{
    public sealed class StickyNoteModeController
    {
        private readonly Window _window;
        private readonly UIElement _normalTitleBar;
        private readonly RowDefinition _titleBarRow;
        private readonly StickyNoteBar _stickyNoteBar;
        private readonly TopCommandBarPane _topToolbar;
        private readonly FrameworkElement _markdownToolbar;
        private readonly FrameworkElement _statusBar;
        private readonly ShellPanelLayoutService _shellPanelLayoutService;
        private readonly ToggleButton _leftPanelToggle;
        private readonly IStickyNoteService _stickyNoteService;
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

        public StickyNoteModeController(
            Window window,
            UIElement normalTitleBar,
            RowDefinition titleBarRow,
            StickyNoteBar stickyNoteBar,
            TopCommandBarPane topToolbar,
            FrameworkElement markdownToolbar,
            FrameworkElement statusBar,
            ShellPanelLayoutService shellPanelLayoutService,
            ToggleButton leftPanelToggle,
            IStickyNoteService stickyNoteService,
            System.Action<bool> applyLeftSidebarVisibility,
            System.Action<bool> applyPreviewVisibility)
        {
            _window = window;
            _normalTitleBar = normalTitleBar;
            _titleBarRow = titleBarRow;
            _stickyNoteBar = stickyNoteBar;
            _topToolbar = topToolbar;
            _markdownToolbar = markdownToolbar;
            _statusBar = statusBar;
            _shellPanelLayoutService = shellPanelLayoutService;
            _leftPanelToggle = leftPanelToggle;
            _stickyNoteService = stickyNoteService;
            _applyLeftSidebarVisibility = applyLeftSidebarVisibility;
            _applyPreviewVisibility = applyPreviewVisibility;

            _stickyNoteBar.ExitClick += (_, _) => Exit();
            _stickyNoteBar.TopMostClick += (_, _) => ApplyTopMostFromStickyBar();
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
            _window.SetTitleBar(null);
            _window.ExtendsContentIntoTitleBar = false;
            ApplyPresenterChromeVisible(false);

            _topToolbar.Visibility = Visibility.Collapsed;
            _markdownToolbar.Visibility = Visibility.Collapsed;
            _statusBar.Visibility = Visibility.Collapsed;

            _shellPanelLayoutService.ApplyLeftSidebarVisibility(false);
            _shellPanelLayoutService.ApplyPreviewVisibility(false);
        }

        private void Exit()
        {
            if (!_isActive)
            {
                return;
            }

            _isActive = false;
            bool topMost = _stickyNoteBar.TopMostIsChecked;
            _topToolbar.TopMostIsChecked = topMost;
            _stickyNoteService.ApplyTopMost(_window, topMost);

            _stickyNoteBar.Visibility = Visibility.Collapsed;
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
        }

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
    }
}
