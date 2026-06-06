using System;
using System.Diagnostics;
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
        private readonly ILocalizationService _localizationService;

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
            Func<string, Task> loadFileIntoTabAsync,
            ILocalizationService localizationService)
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
            _localizationService = localizationService;

            WireEvents();
            _leftSidebar.ActualThemeChanged += (sender, args) =>
            {
                _ = UpdateGitStatusesAsync();
            };
        }

        public string CurrentFolderPath { get; private set; } = string.Empty;

        public void LoadDirectoryRoot(string folderPath)
        {
            _viewModel.ExplorerItems.Clear();
            SetCurrentFolderPath(folderPath);

            bool isDark = _leftSidebar.ActualTheme == ElementTheme.Dark;
            foreach (var item in _directoryService.CreateDirectoryItems(folderPath))
            {
                item.IsDark = isDark;
                _viewModel.ExplorerItems.Add(item);
            }

            _leftSidebar.ExplorerStatus.Text = $"{folderPath}\n{FormatExplorerItemCount(_viewModel.ExplorerItems.Count)}";

            // Trigger Git status update in the background
            _ = UpdateGitStatusesAsync();
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
            _leftSidebar.OpenInWindowsExplorerClick += OnOpenInWindowsExplorerClick;
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

        private void OnOpenInWindowsExplorerClick(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(CurrentFolderPath) || !Directory.Exists(CurrentFolderPath))
            {
                return;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = "explorer.exe",
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add(CurrentFolderPath);
            Process.Start(startInfo);
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

        private string FormatExplorerItemCount(int itemCount)
        {
            string key = itemCount == 1 ? "ExplorerItemCountSingular" : "ExplorerItemCountPlural";
            string fallback = itemCount == 1 ? "{0:N0}개 항목" : "{0:N0}개 항목";
            string format = _localizationService.GetString(key, fallback);
            return string.Format(format, itemCount);
        }

        public async Task UpdateGitStatusesAsync()
        {
            bool isDark = _leftSidebar.ActualTheme == ElementTheme.Dark;
            string repoPath = _gitService.FindRepositoryRoot(CurrentFolderPath) ?? string.Empty;
            if (string.IsNullOrEmpty(repoPath))
            {
                _leftSidebar.DispatcherQueue.TryEnqueue(() =>
                {
                    foreach (var item in _viewModel.ExplorerItems)
                    {
                        item.IsDark = isDark;
                        item.GitStatus = ExplorerItem.GitStatusType.Clean;
                    }
                });
                return;
            }

            var statuses = await _gitService.GetFileStatusesAsync(repoPath);
            _leftSidebar.DispatcherQueue.TryEnqueue(() =>
            {
                UpdateItemsGitStatus(statuses, isDark);
            });
        }

        private void UpdateItemsGitStatus(System.Collections.Generic.Dictionary<string, string> statuses, bool isDark)
        {
            foreach (var item in _viewModel.ExplorerItems)
            {
                item.IsDark = isDark;
                if (item.IsFolder)
                {
                    bool hasModified = false;
                    bool hasAdded = false;
                    bool hasIgnored = false;

                    string folderPathWithSlash = item.Path.EndsWith(Path.DirectorySeparatorChar)
                        ? item.Path
                        : item.Path + Path.DirectorySeparatorChar;
                    string folderPathNormalized = item.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                    foreach (var kvp in statuses)
                    {
                        string keyNormalized = kvp.Key.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        if (keyNormalized.Equals(folderPathNormalized, StringComparison.OrdinalIgnoreCase))
                        {
                            string status = kvp.Value.Trim();
                            if (status == "!!")
                            {
                                hasIgnored = true;
                            }
                            else if (status == "??")
                            {
                                hasAdded = true;
                            }
                            else
                            {
                                hasModified = true;
                            }
                        }
                        else if (kvp.Key.StartsWith(folderPathWithSlash, StringComparison.OrdinalIgnoreCase))
                        {
                            string status = kvp.Value.Trim();
                            if (status == "??")
                            {
                                hasAdded = true;
                            }
                            else if (status == "!!")
                            {
                                hasIgnored = true;
                            }
                            else
                            {
                                hasModified = true;
                            }
                        }
                    }

                    if (hasModified)
                    {
                        item.GitStatus = ExplorerItem.GitStatusType.Modified;
                    }
                    else if (hasAdded)
                    {
                        item.GitStatus = ExplorerItem.GitStatusType.Added;
                    }
                    else if (hasIgnored)
                    {
                        item.GitStatus = ExplorerItem.GitStatusType.Ignored;
                    }
                    else
                    {
                        item.GitStatus = ExplorerItem.GitStatusType.Clean;
                    }
                }
                else
                {
                    if (statuses.TryGetValue(item.Path, out string? status))
                    {
                        string trimmedStatus = status.Trim();
                        if (trimmedStatus == "??")
                        {
                            item.GitStatus = ExplorerItem.GitStatusType.Added;
                        }
                        else if (trimmedStatus == "!!")
                        {
                            item.GitStatus = ExplorerItem.GitStatusType.Ignored;
                        }
                        else
                        {
                            item.GitStatus = ExplorerItem.GitStatusType.Modified;
                        }
                    }
                    else
                    {
                        item.GitStatus = ExplorerItem.GitStatusType.Clean;
                    }
                }
            }
        }
    }
}
