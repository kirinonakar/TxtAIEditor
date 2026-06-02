using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using TxtAIEditor.Core.Services;

namespace TxtAIEditor.Controls
{
    public sealed class ShellPaneController
    {
        private readonly LeftSidebarPane _leftSidebar;
        private readonly StatusBarPane _statusBar;
        private readonly ShellPanelLayoutService _layoutService;
        private readonly TextBox _searchQueryInput;
        private readonly Func<Task> _saveSidebarVisibilityAsync;
        private readonly Action _refreshFavoriteFiles;
        private readonly Action _refreshTocForActiveTab;
        private readonly Action _refreshActivePreview;

        public ShellPaneController(
            LeftSidebarPane leftSidebar,
            StatusBarPane statusBar,
            ShellPanelLayoutService layoutService,
            TextBox searchQueryInput,
            Func<Task> saveSidebarVisibilityAsync,
            Action refreshFavoriteFiles,
            Action refreshTocForActiveTab,
            Action refreshActivePreview)
        {
            _leftSidebar = leftSidebar;
            _statusBar = statusBar;
            _layoutService = layoutService;
            _searchQueryInput = searchQueryInput;
            _saveSidebarVisibilityAsync = saveSidebarVisibilityAsync;
            _refreshFavoriteFiles = refreshFavoriteFiles;
            _refreshTocForActiveTab = refreshTocForActiveTab;
            _refreshActivePreview = refreshActivePreview;

            WireEvents();
        }

        public void ShowLeftSidebarPage(int index)
        {
            int safeIndex = _leftSidebar.ShowPage(index);

            if (safeIndex == 1)
            {
                _leftSidebar.FavoritesFileTabButton.IsChecked = true;
                _leftSidebar.FavoritesFolderTabButton.IsChecked = false;
                _refreshFavoriteFiles();
            }

            if (safeIndex == 3)
            {
                QueueSearchFocus();
            }

            if (safeIndex == 6)
            {
                _refreshTocForActiveTab();
            }
        }

        public void EnsureLeftPanelVisible()
        {
            if (_statusBar.LeftPanelToggleButton.IsChecked == true && _layoutService.IsLeftSidebarVisible)
            {
                return;
            }

            _statusBar.LeftPanelToggleButton.IsChecked = true;
            ApplyLeftSidebarVisibility(true);
            _ = _saveSidebarVisibilityAsync();
        }

        public void FocusSearchPanel()
        {
            EnsureLeftPanelVisible();
            ShowLeftSidebarPage(3);
            QueueSearchFocus();
        }

        public void ApplyLeftSidebarVisibility(bool show)
        {
            _layoutService.ApplyLeftSidebarVisibility(show);
        }

        public void ApplyPreviewVisibility(bool show)
        {
            _statusBar.RightPanelToggleButton.IsChecked = show;
            _layoutService.ApplyPreviewVisibility(show);
            if (show)
            {
                _refreshActivePreview();
            }
        }

        public async Task ToggleLeftPanelAsync()
        {
            bool show = _statusBar.LeftPanelToggleButton.IsChecked != true;
            _statusBar.LeftPanelToggleButton.IsChecked = show;
            ApplyLeftSidebarVisibility(show);
            await _saveSidebarVisibilityAsync();
        }

        public async Task ToggleRightPanelAsync()
        {
            bool show = _statusBar.RightPanelToggleButton.IsChecked != true;
            _statusBar.RightPanelToggleButton.IsChecked = show;
            ApplyPreviewVisibility(show);
            await _saveSidebarVisibilityAsync();
        }

        private void WireEvents()
        {
            _leftSidebar.LeftActivityClick += OnLeftActivityClick;
            _statusBar.LeftPanelToggleClick += OnToggleLeftPanelClick;
            _statusBar.RightPanelToggleClick += OnTogglePreviewClick;
            _statusBar.ExpandPreviewClick += OnExpandPreviewClick;
        }

        private void OnLeftActivityClick(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleButton button &&
                int.TryParse(button.Tag?.ToString(), out int index))
            {
                ShowLeftSidebarPage(index);
            }
        }

        private async void OnToggleLeftPanelClick(object sender, RoutedEventArgs e)
        {
            bool show = _statusBar.LeftPanelToggleButton.IsChecked == true;
            ApplyLeftSidebarVisibility(show);
            await _saveSidebarVisibilityAsync();
        }

        private async void OnTogglePreviewClick(object sender, RoutedEventArgs e)
        {
            ApplyPreviewVisibility(_statusBar.RightPanelToggleButton.IsChecked == true);
            await _saveSidebarVisibilityAsync();
        }

        private void OnExpandPreviewClick(object sender, RoutedEventArgs e)
        {
            _layoutService.TogglePreviewWidth();
        }

        private void QueueSearchFocus()
        {
            _leftSidebar.DispatcherQueue.TryEnqueue(() =>
            {
                _searchQueryInput.Focus(FocusState.Programmatic);
                _searchQueryInput.Focus(FocusState.Keyboard);
            });
        }
    }
}
