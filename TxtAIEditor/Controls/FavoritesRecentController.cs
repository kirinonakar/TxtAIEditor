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
using TxtAIEditor.Core.Services;
using TxtAIEditor.ViewModels;

namespace TxtAIEditor.Controls
{
    public sealed class FavoritesRecentController
    {
        private readonly ISettingsService _settingsService;
        private readonly IRecentFilesService _recentFilesService;
        private readonly RemoteWorkspaceService _remoteWorkspaceService;
        private readonly MainWindowViewModel _viewModel;
        private readonly LeftSidebarPane _leftSidebar;
        private readonly Action<Action> _enqueueOnUiThread;
        private readonly Func<string, Task> _navigateExplorerToFolderAsync;
        private readonly Func<bool> _isExplorerTreeMode;
        private readonly Func<string, Task> _loadFileIntoTabAsync;
        private readonly Action<string, string> _showError;
        private readonly Func<string, string, string> _getString;
        private readonly Dictionary<string, bool> _favoriteFolderHints = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<RecentFileItem> _allRecentItems = new();

        public FavoritesRecentController(
            ISettingsService settingsService,
            IRecentFilesService recentFilesService,
            RemoteWorkspaceService remoteWorkspaceService,
            MainWindowViewModel viewModel,
            LeftSidebarPane leftSidebar,
            Action<Action> enqueueOnUiThread,
            Func<string, Task> navigateExplorerToFolderAsync,
            Func<bool> isExplorerTreeMode,
            Func<string, Task> loadFileIntoTabAsync,
            Action<string, string> showError,
            Func<string, string, string>? getString = null)
        {
            _settingsService = settingsService;
            _recentFilesService = recentFilesService;
            _remoteWorkspaceService = remoteWorkspaceService;
            _viewModel = viewModel;
            _leftSidebar = leftSidebar;
            _enqueueOnUiThread = enqueueOnUiThread;
            _navigateExplorerToFolderAsync = navigateExplorerToFolderAsync;
            _isExplorerTreeMode = isExplorerTreeMode;
            _loadFileIntoTabAsync = loadFileIntoTabAsync;
            _showError = showError;
            _getString = getString ?? ((_, fallback) => fallback);

            _leftSidebar.FavoritesList.ItemsSource = _viewModel.Favorites;
            _leftSidebar.RecentFilesList.ItemsSource = _viewModel.RecentFiles;
            _remoteWorkspaceService.DirectoryOpened += (_, path) =>
                AddRecentFolder(path);
            WireEvents();
        }

        public void LoadRecentFiles()
        {
            _allRecentItems.Clear();
            _recentFilesService.LoadInto(_allRecentItems);
            RefreshRecentFiles();
        }

        public void RefreshRecentFiles(bool? filterFiles = null)
        {
            bool showFiles = filterFiles ?? IsFileTabSelected(
                _leftSidebar.RecentFileTabButton,
                _leftSidebar.RecentFolderTabButton);

            _viewModel.RecentFiles.Clear();
            var filtered = _allRecentItems.Where(i => i.IsFolder == !showFiles).ToList();
            foreach (var item in filtered)
            {
                item.DisplayPath = RemotePath.IsRemote(item.Path)
                    ? _remoteWorkspaceService.GetDisplayPath(item.Path)
                    : item.Path;
                _viewModel.RecentFiles.Add(item);
            }
        }

        public void AddRecentFile(string filePath)
        {
            _enqueueOnUiThread(() =>
            {
                _recentFilesService.Add(_allRecentItems, filePath, isFolder: false);
                RefreshRecentFiles();
            });
        }

        public void AddRecentFolder(string folderPath)
        {
            _enqueueOnUiThread(() =>
            {
                _recentFilesService.Add(_allRecentItems, folderPath, isFolder: true);
                RefreshRecentFiles();
            });
        }

        public Task AddFavoritePathAsync(string path)
        {
            return AddFavoritePathAsync(path, refreshFilterFiles: null, isFolderHint: null);
        }

        public async Task AddFavoritePathAsync(string path, bool? refreshFilterFiles)
        {
            await AddFavoritePathAsync(path, refreshFilterFiles, isFolderHint: null);
        }

        private async Task AddFavoritePathAsync(string path, bool? refreshFilterFiles, bool? isFolderHint)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            if (isFolderHint.HasValue)
            {
                _favoriteFolderHints[path] = isFolderHint.Value;
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
            RefreshFavorites(IsFileTabSelected(
                _leftSidebar.FavoritesFileTabButton,
                _leftSidebar.FavoritesFolderTabButton));
        }

        public void RefreshFavorites(bool? filterFiles)
        {
            bool showFiles = filterFiles ?? IsFileTabSelected(
                _leftSidebar.FavoritesFileTabButton,
                _leftSidebar.FavoritesFolderTabButton);

            _viewModel.Favorites.Clear();
            var settings = _settingsService.CurrentSettings;
            var items = new List<FavoriteItem>();

            foreach (var path in settings.FavoritePaths)
            {
                var item = CreateFavoriteItem(
                    path,
                    settings.PinnedFavoritePaths.Contains(path, StringComparer.OrdinalIgnoreCase));
                items.Add(item);
            }

            var pinnedOrder = settings.PinnedFavoritePaths
                .Select((path, index) => new { path, index })
                .GroupBy(entry => entry.path, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().index, StringComparer.OrdinalIgnoreCase);

            var sorted = items
                .Where(item => item.IsPinned)
                .OrderBy(item => pinnedOrder.TryGetValue(item.Path, out int index) ? index : int.MaxValue)
                .Concat(items.Where(item => !item.IsPinned).Reverse())
                .ToList();

            sorted = sorted.Where(i => i.IsFolder == !showFiles).ToList();

            foreach (var item in sorted)
            {
                _viewModel.Favorites.Add(item);
            }
        }

        private static bool IsFileTabSelected(ToggleButton fileTab, ToggleButton folderTab)
        {
            return fileTab.IsChecked == true || folderTab.IsChecked != true;
        }

        private void WireEvents()
        {
            _leftSidebar.AddFileToFavoritesClick += OnAddFileToFavoritesClick;
            _leftSidebar.AddFolderToFavoritesClick += OnAddFolderToFavoritesClick;
            _leftSidebar.FavoriteItemClick += OnFavoriteItemClick;
            _leftSidebar.RemoveFavoriteClick += OnRemoveFavoriteClick;
            _leftSidebar.FavoritePinClick += OnFavoritePinClick;
            _leftSidebar.FavoritesTabClick += OnFavoritesTabClick;
            _leftSidebar.FavoritesList.DragItemsStarting += OnFavoritesDragItemsStarting;
            _leftSidebar.FavoritesList.DragItemsCompleted += OnFavoritesDragItemsCompleted;
            _leftSidebar.RecentFileItemClick += OnRecentFileItemClick;
            _leftSidebar.RemoveRecentFileClick += OnRemoveRecentFileClick;
            _leftSidebar.RecentTabClick += OnRecentTabClick;
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
                : RemotePath.IsRemote(explorerItem.Path)
                    ? RemotePath.GetParent(explorerItem.Path)
                    : Path.GetDirectoryName(explorerItem.Path) ?? string.Empty;

            if (string.IsNullOrEmpty(folderPath) ||
                (!RemotePath.IsRemote(folderPath) && !Directory.Exists(folderPath)))
            {
                return;
            }

            await AddFavoritePathAsync(folderPath, refreshFilterFiles: null, isFolderHint: true);
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

            await AddFavoritePathAsync(explorerItem.Path, refreshFilterFiles: null, isFolderHint: false);
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
            _favoriteFolderHints.Remove(path);
            await _settingsService.SaveSettingsAsync(settings);
            
            bool showFiles = _leftSidebar.FavoritesFileTabButton.IsChecked == true;
            RefreshFavorites(showFiles);
        }

        private FavoriteItem CreateFavoriteItem(string path, bool isPinned)
        {
            bool isFolder = _favoriteFolderHints.TryGetValue(path, out bool hintedIsFolder)
                ? hintedIsFolder
                : InferFolderWithoutTouchingFileSystem(path);

            return new FavoriteItem
            {
                Name = GetFavoriteDisplayName(path, isFolder),
                Path = path,
                DisplayPath = RemotePath.IsRemote(path)
                    ? _remoteWorkspaceService.GetDisplayPath(path)
                    : path,
                IsFolder = isFolder,
                IsPinned = isPinned
            };
        }

        private static bool InferFolderWithoutTouchingFileSystem(string path)
        {
            if (RemotePath.IsRemote(path))
            {
                return RemotePath.IsDirectory(path);
            }

            string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (trimmed.Length != path.Length)
            {
                return true;
            }

            return !Path.HasExtension(trimmed);
        }

        private static string GetFavoriteDisplayName(string path, bool isFolder)
        {
            if (RemotePath.IsRemote(path))
            {
                return RemotePath.GetName(path);
            }

            string displayPath = isFolder
                ? path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                : path;

            string name = Path.GetFileName(displayPath);
            return string.IsNullOrWhiteSpace(name) ? displayPath : name;
        }

        private async void OnFavoriteItemClick(object sender, ItemClickEventArgs e)
        {
            var item = e.ClickedItem as FavoriteItem
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

            if (RemotePath.IsRemote(item.Path))
            {
                string parentRemotePath = RemotePath.GetParent(item.Path);
                if (!_isExplorerTreeMode())
                {
                    await _navigateExplorerToFolderAsync(parentRemotePath);
                }
                await _loadFileIntoTabAsync(item.Path);
                return;
            }

            string? parentDir = Path.GetDirectoryName(item.Path);
            if (!_isExplorerTreeMode() && !string.IsNullOrEmpty(parentDir) && Directory.Exists(parentDir))
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

        private static void OnFavoritesDragItemsStarting(object sender, DragItemsStartingEventArgs e)
        {
            if (e.Items.OfType<FavoriteItem>().Any(item => !item.IsPinned))
            {
                e.Cancel = true;
            }
        }

        private async void OnFavoritesDragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs e)
        {
            var visiblePinnedPaths = _viewModel.Favorites
                .Where(item => item.IsPinned)
                .Select(item => item.Path)
                .ToList();
            if (visiblePinnedPaths.Count < 2)
            {
                return;
            }

            var visiblePinnedSet = new HashSet<string>(visiblePinnedPaths, StringComparer.OrdinalIgnoreCase);
            var settings = _settingsService.CurrentSettings;
            var reorderedPinnedPaths = settings.PinnedFavoritePaths.ToList();
            var visiblePositions = reorderedPinnedPaths
                .Select((path, index) => new { path, index })
                .Where(entry => visiblePinnedSet.Contains(entry.path))
                .Select(entry => entry.index)
                .ToList();

            if (visiblePositions.Count != visiblePinnedPaths.Count)
            {
                RefreshFavorites();
                return;
            }

            for (int index = 0; index < visiblePositions.Count; index++)
            {
                reorderedPinnedPaths[visiblePositions[index]] = visiblePinnedPaths[index];
            }

            settings.PinnedFavoritePaths = reorderedPinnedPaths;
            bool showFiles = _leftSidebar.FavoritesFileTabButton.IsChecked == true;
            RefreshFavorites(showFiles);
            await _settingsService.SaveSettingsAsync(settings);
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

        private void OnRecentTabClick(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleButton button)
            {
                return;
            }

            bool showFiles = button == _leftSidebar.RecentFileTabButton;
            _leftSidebar.RecentFileTabButton.IsChecked = showFiles;
            _leftSidebar.RecentFolderTabButton.IsChecked = !showFiles;
            RefreshRecentFiles(showFiles);
        }

        private void OnRemoveRecentFileClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: string path })
            {
                _recentFilesService.Remove(_allRecentItems, path);
                RefreshRecentFiles();
            }
        }

        private async void OnRecentFileItemClick(object sender, ItemClickEventArgs e)
        {
            var item = e.ClickedItem as RecentFileItem
                ?? _leftSidebar.RecentFilesList.SelectedItem as RecentFileItem;
            if (item == null)
            {
                return;
            }

            if (item.IsFolder)
            {
                if (RemotePath.IsRemote(item.Path))
                {
                    await _navigateExplorerToFolderAsync(item.Path);
                    return;
                }

                if (!Directory.Exists(item.Path))
                {
                    _showError(
                        _getString("FolderOpenFailedTitle", "폴더 열기 실패"),
                        string.Format(_getString("RecentFolderMissingMessageFormat", "최근 폴더가 존재하지 않습니다:\n{0}"), item.Path));
                    return;
                }

                await _navigateExplorerToFolderAsync(item.Path);
                return;
            }

            if (RemotePath.IsRemote(item.Path))
            {
                string parentRemotePath = RemotePath.GetParent(item.Path);
                if (!_isExplorerTreeMode())
                {
                    await _navigateExplorerToFolderAsync(parentRemotePath);
                }
                await _loadFileIntoTabAsync(item.Path);
                return;
            }

            if (!File.Exists(item.Path))
            {
                _showError(
                    _getString("FileOpenFailedTitle", "파일 열기 실패"),
                    string.Format(_getString("RecentFileMissingMessageFormat", "최근 파일이 존재하지 않습니다:\n{0}"), item.Path));
                return;
            }

            string? folderPath = Path.GetDirectoryName(item.Path);
            if (!_isExplorerTreeMode() && !string.IsNullOrWhiteSpace(folderPath) && Directory.Exists(folderPath))
            {
                await _navigateExplorerToFolderAsync(folderPath);
            }

            await _loadFileIntoTabAsync(item.Path);
        }
    }
}
