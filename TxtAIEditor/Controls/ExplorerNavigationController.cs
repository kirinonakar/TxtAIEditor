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
        private readonly Func<string> _homeFolderPathProvider;

        public enum ExplorerSortMode
        {
            Name,
            Newest,
            Oldest
        }

        private ExplorerSortMode _currentSortMode = ExplorerSortMode.Name;

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
            ILocalizationService localizationService,
            Func<string> homeFolderPathProvider)
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
            _homeFolderPathProvider = homeFolderPathProvider;

            WireEvents();
            UpdateSortButtonVisuals();
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
            foreach (var item in SortItems(_directoryService.CreateDirectoryItems(folderPath)))
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
            _leftSidebar.SortClick += OnExplorerSortClick;
            _leftSidebar.OpenInWindowsExplorerClick += OnOpenInWindowsExplorerClick;
            _leftSidebar.ExplorerHomeClick += OnExplorerHomeClick;
            _leftSidebar.FileListViewItemClick += OnFileListViewItemClick;
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

        private async void OnExplorerHomeClick(object sender, RoutedEventArgs e)
        {
            string homeFolderPath = _homeFolderPathProvider()?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(homeFolderPath) || !Directory.Exists(homeFolderPath))
            {
                homeFolderPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }

            if (!string.IsNullOrWhiteSpace(homeFolderPath) && Directory.Exists(homeFolderPath))
            {
                await NavigateToFolderAsync(homeFolderPath);
            }
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

        private void OnFileListViewItemClick(object sender, Microsoft.UI.Xaml.Controls.ItemClickEventArgs e)
        {
            var item = e.ClickedItem as ExplorerItem
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

        private bool IsPathIgnored(string path, System.Collections.Generic.Dictionary<string, string> statuses)
        {
            if (statuses.TryGetValue(path, out var status) && status.Trim() == "!!")
            {
                return true;
            }

            foreach (var kvp in statuses)
            {
                if (kvp.Value.Trim() == "!!")
                {
                    string ignoredDir = kvp.Key;
                    string ignoredDirWithSlash = ignoredDir.EndsWith(Path.DirectorySeparatorChar)
                        ? ignoredDir
                        : ignoredDir + Path.DirectorySeparatorChar;

                    if (path.StartsWith(ignoredDirWithSlash, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            return false;
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

                    string folderPathWithSlash = item.Path.EndsWith(Path.DirectorySeparatorChar)
                        ? item.Path
                        : item.Path + Path.DirectorySeparatorChar;

                    foreach (var kvp in statuses)
                    {
                        if (kvp.Key.StartsWith(folderPathWithSlash, StringComparison.OrdinalIgnoreCase))
                        {
                            string status = kvp.Value.Trim();
                            if (status == "??")
                            {
                                hasAdded = true;
                            }
                            else if (status != "!!")
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
                    else if (IsPathIgnored(item.Path, statuses))
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
                    else if (IsPathIgnored(item.Path, statuses))
                    {
                        item.GitStatus = ExplorerItem.GitStatusType.Ignored;
                    }
                    else
                    {
                        item.GitStatus = ExplorerItem.GitStatusType.Clean;
                    }
                }
            }
        }

        private void OnExplorerSortClick(object sender, RoutedEventArgs e)
        {
            _currentSortMode = _currentSortMode switch
            {
                ExplorerSortMode.Name => ExplorerSortMode.Newest,
                ExplorerSortMode.Newest => ExplorerSortMode.Oldest,
                ExplorerSortMode.Oldest => ExplorerSortMode.Name,
                _ => ExplorerSortMode.Name
            };

            UpdateSortButtonVisuals();

            if (_viewModel.ExplorerItems.Count > 0)
            {
                var sorted = SortItems(_viewModel.ExplorerItems);
                _viewModel.ExplorerItems.Clear();
                foreach (var item in sorted)
                {
                    _viewModel.ExplorerItems.Add(item);
                }
            }
        }

        private void UpdateSortButtonVisuals()
        {
            string key;
            string fallback;
            string glyph;

            switch (_currentSortMode)
            {
                case ExplorerSortMode.Name:
                    key = "ExplorerSortName";
                    fallback = "이름순 정렬";
                    glyph = "\uE8CB"; // Standard sort glyph
                    break;
                case ExplorerSortMode.Newest:
                    key = "ExplorerSortNewest";
                    fallback = "수정한 날짜 최신순 정렬";
                    glyph = "\uE74B"; // Down arrow
                    break;
                case ExplorerSortMode.Oldest:
                    key = "ExplorerSortOldest";
                    fallback = "수정한 날짜 오래된순 정렬";
                    glyph = "\uE74A"; // Up arrow
                    break;
                default:
                    key = "ExplorerSortName";
                    fallback = "이름순 정렬";
                    glyph = "\uE8CB";
                    break;
            }

            string tooltipText = _localizationService.GetString(key, fallback);
            Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(_leftSidebar.ExplorerSortBtn, tooltipText);

            if (_leftSidebar.ExplorerSortBtn.Content is Microsoft.UI.Xaml.Controls.FontIcon fontIcon)
            {
                fontIcon.Glyph = glyph;
            }
        }

        private System.Collections.Generic.IEnumerable<ExplorerItem> SortItems(System.Collections.Generic.IEnumerable<ExplorerItem> items)
        {
            var folderList = new System.Collections.Generic.List<ExplorerItem>();
            var fileList = new System.Collections.Generic.List<ExplorerItem>();

            foreach (var item in items)
            {
                if (item.IsFolder)
                    folderList.Add(item);
                else
                    fileList.Add(item);
            }

            switch (_currentSortMode)
            {
                case ExplorerSortMode.Name:
                    folderList.Sort((a, b) => StrCmpLogicalW(a.Name, b.Name));
                    fileList.Sort((a, b) => StrCmpLogicalW(a.Name, b.Name));
                    break;
                case ExplorerSortMode.Newest:
                    folderList.Sort((a, b) => b.ModifiedTime.CompareTo(a.ModifiedTime));
                    fileList.Sort((a, b) => b.ModifiedTime.CompareTo(a.ModifiedTime));
                    break;
                case ExplorerSortMode.Oldest:
                    folderList.Sort((a, b) => a.ModifiedTime.CompareTo(b.ModifiedTime));
                    fileList.Sort((a, b) => a.ModifiedTime.CompareTo(b.ModifiedTime));
                    break;
            }

            var sorted = new System.Collections.Generic.List<ExplorerItem>(folderList.Count + fileList.Count);
            sorted.AddRange(folderList);
            sorted.AddRange(fileList);
            return sorted;
        }

        [System.Runtime.InteropServices.DllImport("shlwapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, ExactSpelling = true)]
        private static extern int StrCmpLogicalW(string x, string y);
    }
}
