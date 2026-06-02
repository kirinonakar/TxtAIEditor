using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Models;
using TxtAIEditor.ViewModels;

namespace TxtAIEditor.Controls
{
    public sealed class FavoritesRecentController
    {
        private readonly ISettingsService _settingsService;
        private readonly IRecentFilesService _recentFilesService;
        private readonly MainWindowViewModel _viewModel;
        private readonly LeftSidebarPane _leftSidebar;
        private readonly Action<Action> _enqueueOnUiThread;
        private readonly Func<string, Task> _navigateExplorerToFolderAsync;
        private readonly Func<string, Task> _loadFileIntoTabAsync;
        private readonly Action<string, string> _showError;

        public FavoritesRecentController(
            ISettingsService settingsService,
            IRecentFilesService recentFilesService,
            MainWindowViewModel viewModel,
            LeftSidebarPane leftSidebar,
            Action<Action> enqueueOnUiThread,
            Func<string, Task> navigateExplorerToFolderAsync,
            Func<string, Task> loadFileIntoTabAsync,
            Action<string, string> showError)
        {
            _settingsService = settingsService;
            _recentFilesService = recentFilesService;
            _viewModel = viewModel;
            _leftSidebar = leftSidebar;
            _enqueueOnUiThread = enqueueOnUiThread;
            _navigateExplorerToFolderAsync = navigateExplorerToFolderAsync;
            _loadFileIntoTabAsync = loadFileIntoTabAsync;
            _showError = showError;

            _leftSidebar.FavoritesList.ItemsSource = _viewModel.Favorites;
            _leftSidebar.RecentFilesList.ItemsSource = _viewModel.RecentFiles;
            WireEvents();
        }

        public void LoadRecentFiles()
        {
            _recentFilesService.LoadInto(_viewModel.RecentFiles);
        }

        public void AddRecentFile(string filePath)
        {
            _enqueueOnUiThread(() => _recentFilesService.Add(_viewModel.RecentFiles, filePath));
        }

        public Task AddFavoritePathAsync(string path)
        {
            return AddFavoritePathAsync(path, refreshFilterFiles: null);
        }

        public async Task AddFavoritePathAsync(string path, bool? refreshFilterFiles)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var settings = _settingsService.CurrentSettings;
            if (!settings.FavoritePaths.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                settings.FavoritePaths.Add(path);
                await _settingsService.SaveSettingsAsync(settings);
                RefreshFavorites(refreshFilterFiles);
            }
        }

        public void RefreshFavorites()
        {
            RefreshFavorites(null);
        }

        public void RefreshFavorites(bool? filterFiles)
        {
            _viewModel.Favorites.Clear();
            var settings = _settingsService.CurrentSettings;
            var items = new List<FavoriteItem>();

            foreach (var path in settings.FavoritePaths)
            {
                bool isFolder = Directory.Exists(path);
                bool isFile = !isFolder && File.Exists(path);
                if (!isFolder && !isFile)
                {
                    continue;
                }

                items.Add(new FavoriteItem
                {
                    Name = isFolder
                        ? Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                        : Path.GetFileName(path),
                    Path = path,
                    IsFolder = isFolder,
                    IsPinned = settings.PinnedFavoritePaths.Contains(path)
                });
            }

            var sorted = items
                .OrderByDescending(i => i.IsPinned)
                .ThenBy(i => i.Name)
                .ToList();

            if (filterFiles.HasValue)
            {
                sorted = sorted.Where(i => i.IsFolder == !filterFiles.Value).ToList();
            }

            foreach (var item in sorted)
            {
                _viewModel.Favorites.Add(item);
            }
        }

        private void WireEvents()
        {
            _leftSidebar.AddFileToFavoritesClick += OnAddFileToFavoritesClick;
            _leftSidebar.AddFolderToFavoritesClick += OnAddFolderToFavoritesClick;
            _leftSidebar.FavoriteItemDoubleTapped += OnFavoriteItemDoubleTapped;
            _leftSidebar.RemoveFavoriteClick += OnRemoveFavoriteClick;
            _leftSidebar.FavoritePinClick += OnFavoritePinClick;
            _leftSidebar.FavoritesTabClick += OnFavoritesTabClick;
            _leftSidebar.RecentFileItemDoubleTapped += OnRecentFileItemDoubleTapped;
            _leftSidebar.RemoveRecentFileClick += OnRemoveRecentFileClick;
        }

        private async void OnAddFolderToFavoritesClick(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuFlyoutItem item)
            {
                return;
            }

            var explorerItem = item.Tag as ExplorerItem
                ?? item.DataContext as ExplorerItem
                ?? _leftSidebar.FileList.SelectedItem as ExplorerItem;
            if (explorerItem == null)
            {
                return;
            }

            string folderPath = explorerItem.IsFolder
                ? explorerItem.Path
                : Path.GetDirectoryName(explorerItem.Path) ?? string.Empty;

            if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
            {
                return;
            }

            await AddFavoritePathAsync(folderPath);
        }

        private async void OnAddFileToFavoritesClick(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuFlyoutItem item)
            {
                return;
            }

            var explorerItem = item.Tag as ExplorerItem
                ?? item.DataContext as ExplorerItem
                ?? _leftSidebar.FileList.SelectedItem as ExplorerItem;
            if (explorerItem == null || explorerItem.IsFolder)
            {
                return;
            }

            await AddFavoritePathAsync(explorerItem.Path);
        }

        private async void OnRemoveFavoriteClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string path })
            {
                return;
            }

            var settings = _settingsService.CurrentSettings;
            settings.FavoritePaths.Remove(path);
            settings.PinnedFavoritePaths.Remove(path);
            await _settingsService.SaveSettingsAsync(settings);
            
            bool showFiles = _leftSidebar.FavoritesFileTabButton.IsChecked == true;
            RefreshFavorites(showFiles);
        }

        private async void OnFavoriteItemDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            var item = VisualTreeDataContext.FindFromOriginalSource<FavoriteItem>(e.OriginalSource)
                ?? _leftSidebar.FavoritesList.SelectedItem as FavoriteItem;
            if (item == null)
            {
                return;
            }

            if (item.IsFolder)
            {
                await _navigateExplorerToFolderAsync(item.Path);
                return;
            }

            string? parentDir = Path.GetDirectoryName(item.Path);
            if (!string.IsNullOrEmpty(parentDir) && Directory.Exists(parentDir))
            {
                await _navigateExplorerToFolderAsync(parentDir);
            }

            await _loadFileIntoTabAsync(item.Path);
        }

        private async void OnFavoritePinClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button)
            {
                return;
            }

            string? path = null;
            bool shouldPin = false;

            if (button.Tag is FavoriteItem item)
            {
                path = item.Path;
                shouldPin = !item.IsPinned;
            }
            else if (button.Tag is string p)
            {
                path = p;
                var curSettings = _settingsService.CurrentSettings;
                shouldPin = !curSettings.PinnedFavoritePaths.Contains(path);
            }

            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            var settings = _settingsService.CurrentSettings;
            if (shouldPin)
            {
                if (!settings.PinnedFavoritePaths.Contains(path))
                {
                    settings.PinnedFavoritePaths.Add(path);
                }
            }
            else
            {
                settings.PinnedFavoritePaths.Remove(path);
            }

            await _settingsService.SaveSettingsAsync(settings);
            
            bool showFiles = _leftSidebar.FavoritesFileTabButton.IsChecked == true;
            RefreshFavorites(showFiles);
        }

        private void OnFavoritesTabClick(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleButton button)
            {
                return;
            }

            bool showFiles = button == _leftSidebar.FavoritesFileTabButton;
            _leftSidebar.FavoritesFileTabButton.IsChecked = showFiles;
            _leftSidebar.FavoritesFolderTabButton.IsChecked = !showFiles;
            RefreshFavorites(showFiles);
        }

        private void OnRemoveRecentFileClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: string path })
            {
                _recentFilesService.Remove(_viewModel.RecentFiles, path);
            }
        }

        private async void OnRecentFileItemDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            var item = VisualTreeDataContext.FindFromOriginalSource<RecentFileItem>(e.OriginalSource)
                ?? _leftSidebar.RecentFilesList.SelectedItem as RecentFileItem;
            if (item == null)
            {
                return;
            }

            if (!File.Exists(item.Path))
            {
                _showError("파일 열기 실패", $"최근 파일이 존재하지 않습니다:\n{item.Path}");
                return;
            }

            string? folderPath = Path.GetDirectoryName(item.Path);
            if (!string.IsNullOrWhiteSpace(folderPath) && Directory.Exists(folderPath))
            {
                await _navigateExplorerToFolderAsync(folderPath);
            }

            await _loadFileIntoTabAsync(item.Path);
        }
    }
}
