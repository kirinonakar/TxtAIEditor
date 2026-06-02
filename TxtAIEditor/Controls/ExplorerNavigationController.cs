using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Services;
using TxtAIEditor.ViewModels;
using Windows.Storage.Pickers;

namespace TxtAIEditor.Controls
{
    public sealed class ExplorerNavigationController
    {
        private readonly LeftSidebarPane _leftSidebar;
        private readonly MainWindowViewModel _viewModel;
        private readonly ExplorerDirectoryService _directoryService;
        private readonly IGitService _gitService;
        private readonly Action<object> _initializePickerWindow;
        private readonly Action<string> _currentFolderChanged;
        private readonly Action<string> _currentRepoPathChanged;
        private readonly Func<Task> _refreshGitStatusAsync;
        private readonly Action _ensureLeftPanelVisible;
        private readonly Action<int> _showLeftSidebarPage;
        private readonly Func<string, Task> _loadFileIntoTabAsync;

        public ExplorerNavigationController(
            LeftSidebarPane leftSidebar,
            MainWindowViewModel viewModel,
            ExplorerDirectoryService directoryService,
            IGitService gitService,
            Action<object> initializePickerWindow,
            Action<string> currentFolderChanged,
            Action<string> currentRepoPathChanged,
            Func<Task> refreshGitStatusAsync,
            Action ensureLeftPanelVisible,
            Action<int> showLeftSidebarPage,
            Func<string, Task> loadFileIntoTabAsync)
        {
            _leftSidebar = leftSidebar;
            _viewModel = viewModel;
            _directoryService = directoryService;
            _gitService = gitService;
            _initializePickerWindow = initializePickerWindow;
            _currentFolderChanged = currentFolderChanged;
            _currentRepoPathChanged = currentRepoPathChanged;
            _refreshGitStatusAsync = refreshGitStatusAsync;
            _ensureLeftPanelVisible = ensureLeftPanelVisible;
            _showLeftSidebarPage = showLeftSidebarPage;
            _loadFileIntoTabAsync = loadFileIntoTabAsync;

            WireEvents();
        }

        public string CurrentFolderPath { get; private set; } = string.Empty;

        public void LoadDirectoryRoot(string folderPath)
        {
            _viewModel.ExplorerItems.Clear();
            SetCurrentFolderPath(folderPath);

            foreach (var item in _directoryService.CreateDirectoryItems(folderPath))
            {
                _viewModel.ExplorerItems.Add(item);
            }

            _leftSidebar.ExplorerStatus.Text = $"{folderPath}\n{_viewModel.ExplorerItems.Count:N0}개 항목";
        }

        public async Task NavigateToFolderAsync(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
            {
                return;
            }

            UpdateRepoPath(folderPath);
            LoadDirectoryRoot(folderPath);

            _ensureLeftPanelVisible();
            _showLeftSidebarPage(0);

            await _refreshGitStatusAsync();
        }

        public void RefreshCurrentFolder()
        {
            if (!string.IsNullOrEmpty(CurrentFolderPath) && Directory.Exists(CurrentFolderPath))
            {
                LoadDirectoryRoot(CurrentFolderPath);
            }
        }

        private void WireEvents()
        {
            _leftSidebar.ExplorerUpClick += OnExplorerUpClick;
            _leftSidebar.SelectFolderClick += OnSelectFolderClick;
            _leftSidebar.RefreshClick += OnExplorerRefreshClick;
            _leftSidebar.FileListViewDoubleTapped += OnFileListViewDoubleTapped;
        }

        private async void OnSelectFolderClick(object sender, RoutedEventArgs e)
        {
            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.ComputerFolder
            };
            _initializePickerWindow(picker);
            picker.FileTypeFilter.Add("*");

            var folder = await picker.PickSingleFolderAsync();
            if (folder == null)
            {
                return;
            }

            UpdateRepoPath(folder.Path);
            LoadDirectoryRoot(folder.Path);
            await _refreshGitStatusAsync();
        }

        private void OnExplorerRefreshClick(object sender, RoutedEventArgs e)
        {
            RefreshCurrentFolder();
        }

        private void OnExplorerUpClick(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(CurrentFolderPath))
            {
                return;
            }

            var parent = Directory.GetParent(CurrentFolderPath);
            if (parent == null)
            {
                return;
            }

            UpdateRepoPath(parent.FullName);
            LoadDirectoryRoot(parent.FullName);
        }

        private void OnFileListViewDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            var item = VisualTreeDataContext.FindFromOriginalSource<ExplorerItem>(e.OriginalSource)
                       ?? _leftSidebar.FileList.SelectedItem as ExplorerItem;
            if (item == null)
            {
                return;
            }

            if (item.IsFolder)
            {
                UpdateRepoPath(item.Path);
                LoadDirectoryRoot(item.Path);
            }
            else
            {
                _ = _loadFileIntoTabAsync(item.Path);
            }
        }

        private void SetCurrentFolderPath(string folderPath)
        {
            CurrentFolderPath = folderPath;
            _currentFolderChanged(folderPath);
        }

        private void UpdateRepoPath(string path)
        {
            _currentRepoPathChanged(_gitService.FindRepositoryRoot(path) ?? string.Empty);
        }
    }
}
