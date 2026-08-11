using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Services;
using TxtAIEditor.ViewModels;
using Windows.Storage.Pickers;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using System.Text;
 
namespace TxtAIEditor.Controls
{
    public sealed class ExplorerNavigationController
    {
        private readonly LeftSidebarPane _leftSidebar;
        private readonly MainWindowViewModel _viewModel;
        private readonly ExplorerDirectoryService _directoryService;
        private readonly ArchiveExplorerService _archiveExplorerService;
        private readonly RemoteWorkspaceService _remoteWorkspaceService;
        private readonly IGitService _gitService;
        private readonly Action<object> _initializePickerWindow;
        private readonly Action<string> _currentFolderChanged;
        private readonly Action<string> _currentRepoPathChanged;
        private readonly Func<Task> _refreshGitStatusAsync;
        private readonly Action _ensureLeftPanelVisible;
        private readonly Action<int> _showLeftSidebarPage;
        private readonly Func<string, Task> _loadFileIntoTabAsync;
        private readonly Func<string, string, Task> _loadArchiveEntryIntoTabAsync;
        private readonly ILocalizationService _localizationService;
        private readonly Func<string> _homeFolderPathProvider;
        private System.Threading.CancellationTokenSource? _remoteCancellation;
        private System.Threading.CancellationTokenSource? _flatDirectoryLoadCancellation;
        private string _currentArchiveRemotePath = string.Empty;
        private readonly HashSet<string> _loadingRemoteArchivePaths =
            new(StringComparer.OrdinalIgnoreCase);
        private const int MaxFolderHistory = 200;
        private readonly Stack<ExplorerHistoryEntry> _folderHistory = new();
        private readonly Stack<ExplorerHistoryEntry> _forwardHistory = new();
        private bool _isBackNavigation;
        private bool _isForwardNavigation;
        private string _explorerStatusBaseText = string.Empty;
        private int _treeSelectionCount;

        public enum ExplorerSortMode
        {
            Name,
            Newest,
            Oldest
        }

        private sealed class ExplorerHistoryEntry
        {
            public ExplorerHistoryEntry(string path, string secondaryPath = "", bool isArchive = false)
            {
                Path = path;
                SecondaryPath = secondaryPath;
                IsArchive = isArchive;
            }

            public string Path { get; }
            public string SecondaryPath { get; }
            public bool IsArchive { get; }
        }

        private ExplorerSortMode _currentSortMode = ExplorerSortMode.Name;

        public ExplorerNavigationController(
            LeftSidebarPane leftSidebar,
            MainWindowViewModel viewModel,
            ExplorerDirectoryService directoryService,
            ArchiveExplorerService archiveExplorerService,
            RemoteWorkspaceService remoteWorkspaceService,
            IGitService gitService,
            Action<object> initializePickerWindow,
            Action<string> currentFolderChanged,
            Action<string> currentRepoPathChanged,
            Func<Task> refreshGitStatusAsync,
            Action ensureLeftPanelVisible,
            Action<int> showLeftSidebarPage,
            Func<string, Task> loadFileIntoTabAsync,
            Func<string, string, Task> loadArchiveEntryIntoTabAsync,
            ILocalizationService localizationService,
            Func<string> homeFolderPathProvider)
        {
            _leftSidebar = leftSidebar;
            _viewModel = viewModel;
            _directoryService = directoryService;
            _archiveExplorerService = archiveExplorerService;
            _remoteWorkspaceService = remoteWorkspaceService;
            _gitService = gitService;
            _initializePickerWindow = initializePickerWindow;
            _currentFolderChanged = currentFolderChanged;
            _currentRepoPathChanged = currentRepoPathChanged;
            _refreshGitStatusAsync = refreshGitStatusAsync;
            _ensureLeftPanelVisible = ensureLeftPanelVisible;
            _showLeftSidebarPage = showLeftSidebarPage;
            _loadFileIntoTabAsync = loadFileIntoTabAsync;
            _loadArchiveEntryIntoTabAsync = loadArchiveEntryIntoTabAsync;
            _localizationService = localizationService;
            _homeFolderPathProvider = homeFolderPathProvider;
            _explorerStatusBaseText = _leftSidebar.ExplorerStatus.Text;

            WireEvents();
            _remoteWorkspaceService.FileUploaded += (_, _) =>
                _leftSidebar.DispatcherQueue.TryEnqueue(() => _ = RefreshRemoteDirectoryAsync());
            UpdateSortButtonVisuals();
            _leftSidebar.ActualThemeChanged += (sender, args) =>
            {
                RefreshExplorerItemThemeColors();
                _ = UpdateGitStatusesAsync();
            };
        }

        public string CurrentFolderPath { get; private set; } = string.Empty;
        public string CurrentArchivePath { get; private set; } = string.Empty;
        public string CurrentArchiveDirectory { get; private set; } = string.Empty;
        public bool IsViewingArchive => !string.IsNullOrWhiteSpace(CurrentArchivePath);
        public bool IsViewingRemote => _remoteWorkspaceService.IsActive;
        public bool IsTreeMode { get; private set; }

        public void SetTreeMode(bool enableTreeMode)
        {
            if (enableTreeMode == IsTreeMode)
            {
                return;
            }

            IsTreeMode = enableTreeMode;
            ClearExplorerFilterState();
            _leftSidebar.SetExplorerTreeMode(IsTreeMode);
            UpdateBackButtonState();

            if (IsViewingRemote)
            {
                if (IsTreeMode)
                {
                    _ = LoadRemoteTreeRootAsync();
                }
                else
                {
                    _ = LoadRemoteDirectoryAsync();
                }

                return;
            }

            if (string.IsNullOrWhiteSpace(CurrentFolderPath) || !Directory.Exists(CurrentFolderPath))
            {
                ClearExplorerTreeNodes();
                return;
            }

            if (IsTreeMode)
            {
                CancelFlatDirectoryLoad();
                LoadTreeRoot(ResolveTreeRoot(CurrentFolderPath));
            }
            else
            {
                LoadDirectoryRoot(CurrentFolderPath);
            }
        }

        public void LoadDirectoryRoot(string folderPath)
        {
            if (IsTreeMode)
            {
                CancelFlatDirectoryLoad();
                LoadTreeRoot(ResolveTreeRoot(folderPath));
                return;
            }

            _ = LoadFlatDirectoryRootAsync(folderPath, updateGitStatus: true);
        }

        private async Task<bool> LoadFlatDirectoryRootAsync(string folderPath, bool updateGitStatus)
        {
            ClearExplorerFilterState();
            CancelFlatDirectoryLoad();
            var cancellation = new System.Threading.CancellationTokenSource();
            System.Threading.CancellationToken cancellationToken = cancellation.Token;
            _flatDirectoryLoadCancellation = cancellation;

            _remoteWorkspaceService.Deactivate();
            _leftSidebar.ExplorerTreeModeBtn.IsEnabled = true;
            CurrentArchivePath = string.Empty;
            CurrentArchiveDirectory = string.Empty;
            _currentArchiveRemotePath = string.Empty;
            SetCurrentFolderPath(folderPath);

            bool isDark = _leftSidebar.ActualTheme == ElementTheme.Dark;
            ExplorerSortMode sortMode = _currentSortMode;
            try
            {
                List<ExplorerItem> items = await Task.Run(() =>
                {
                    var loadedItems = new List<ExplorerItem>();
                    foreach (ExplorerItem item in _directoryService.CreateDirectoryItems(folderPath))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (_hideUnwantedFolders && item.IsFolder && IsHiddenFolderName(item.Name))
                        {
                            continue;
                        }

                        item.IsDark = isDark;
                        item.IsArchive = !item.IsFolder &&
                            _archiveExplorerService.IsSupportedArchiveFile(item.Path);
                        loadedItems.Add(item);
                    }

                    return SortItems(loadedItems, sortMode).ToList();
                }, cancellationToken);

                if (cancellation.IsCancellationRequested ||
                    IsTreeMode ||
                    IsViewingRemote ||
                    IsViewingArchive ||
                    !string.Equals(CurrentFolderPath, folderPath, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                _viewModel.ExplorerItems.ReplaceAll(items);
                SetExplorerStatusText(FormatExplorerItemCount(items.Count));

                if (updateGitStatus)
                {
                    await UpdateGitStatusesAsync();
                }

                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed loading folder '{folderPath}': {ex.Message}");
                return false;
            }
            finally
            {
                if (ReferenceEquals(_flatDirectoryLoadCancellation, cancellation))
                {
                    _flatDirectoryLoadCancellation = null;
                }

                cancellation.Dispose();
            }
        }

        private void CancelFlatDirectoryLoad()
        {
            var pendingLoad = _flatDirectoryLoadCancellation;
            _flatDirectoryLoadCancellation = null;
            pendingLoad?.Cancel();
        }

        private void LoadArchiveDirectoryRoot(string archivePath, string entryDirectory)
        {
            ClearExplorerFilterState();
            CancelFlatDirectoryLoad();
            try
            {
                if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
                {
                    return;
                }

                string normalizedEntryDirectory = ArchiveExplorerService.NormalizeEntryPath(entryDirectory);
                if (!_isBackNavigation)
                {
                    if (string.IsNullOrEmpty(CurrentArchivePath))
                    {
                        if (!string.IsNullOrWhiteSpace(CurrentFolderPath))
                        {
                            PushHistory(new ExplorerHistoryEntry(CurrentFolderPath));
                        }
                    }
                    else if (!string.Equals(CurrentArchivePath, archivePath, StringComparison.OrdinalIgnoreCase) ||
                             !string.Equals(CurrentArchiveDirectory, normalizedEntryDirectory, StringComparison.OrdinalIgnoreCase))
                    {
                        PushHistory(new ExplorerHistoryEntry(
                            CurrentArchivePath,
                            CurrentArchiveDirectory,
                            isArchive: true));
                    }
                }

                _viewModel.ExplorerItems.Clear();
                CurrentArchivePath = archivePath;
                CurrentArchiveDirectory = normalizedEntryDirectory;

                bool isDark = _leftSidebar.ActualTheme == ElementTheme.Dark;
                System.Collections.Generic.IEnumerable<ExplorerItem> archiveItems =
                    _archiveExplorerService.CreateArchiveItems(archivePath, CurrentArchiveDirectory);
                if (_hideUnwantedFolders)
                {
                    archiveItems = archiveItems.Where(item => !item.IsFolder || !IsHiddenFolderName(item.Name));
                }

                foreach (var item in SortItems(archiveItems))
                {
                    item.IsDark = isDark;
                    ApplyArchiveDisplayPath(item);
                    _viewModel.ExplorerItems.Add(item);
                }

                SetExplorerStatusText(FormatExplorerItemCount(_viewModel.ExplorerItems.Count));
                UpdateExplorerBreadcrumb();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed reading archive: {ex.Message}");
                string title = _localizationService.GetString("ArchiveOpenFailedTitle", "압축 파일 열기 실패");
                SetExplorerStatusText(
                    $"{GetArchiveDisplayPath(archivePath)}\n{title}: {ex.Message}");
            }
        }

        private string GetArchiveDisplayPath(string archivePath)
        {
            string remotePath = _currentArchiveRemotePath;
            if (string.IsNullOrWhiteSpace(remotePath))
            {
                _remoteWorkspaceService.TryGetVirtualPath(archivePath, out remotePath);
            }

            return string.IsNullOrWhiteSpace(remotePath)
                ? archivePath
                : _remoteWorkspaceService.GetDisplayPath(remotePath);
        }

        private void ApplyArchiveDisplayPath(ExplorerItem item)
        {
            if (string.IsNullOrWhiteSpace(item.ArchivePath) ||
                !_remoteWorkspaceService.TryGetVirtualPath(
                    item.ArchivePath,
                    out string remoteArchivePath))
            {
                return;
            }

            string entryPath = ArchiveExplorerService.NormalizeEntryPath(item.ArchiveEntryPath);
            item.DisplayPath =
                $"{_remoteWorkspaceService.GetDisplayPath(remoteArchivePath)}!/{entryPath}";
        }

        public bool TryOpenArchive(string archivePath, bool revealInLeftPanel = true)
        {
            if (!_archiveExplorerService.IsSupportedArchiveFile(archivePath))
            {
                return false;
            }

            string fullArchivePath;
            try
            {
                fullArchivePath = Path.GetFullPath(archivePath);
            }
            catch
            {
                return false;
            }

            if (!File.Exists(fullArchivePath))
            {
                return false;
            }

            string? folderPath = Path.GetDirectoryName(fullArchivePath);
            bool isRemoteArchive = _remoteWorkspaceService.TryGetVirtualPath(
                fullArchivePath,
                out string remoteArchivePath);
            _currentArchiveRemotePath = isRemoteArchive
                ? remoteArchivePath
                : string.Empty;
            if (!isRemoteArchive &&
                !string.IsNullOrWhiteSpace(folderPath) &&
                Directory.Exists(folderPath))
            {
                UpdateRepoPath(folderPath);
                SetCurrentFolderPath(folderPath);
            }

            if (revealInLeftPanel)
            {
                _ensureLeftPanelVisible();
                _showLeftSidebarPage(0);
            }

            LoadArchiveDirectoryRoot(fullArchivePath, string.Empty);
            return true;
        }

        public async Task NavigateToFolderAsync(string folderPath, bool revealInLeftPanel = true)
        {
            if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
            {
                return;
            }

            UpdateRepoPath(folderPath);
            if (IsTreeMode)
            {
                CancelFlatDirectoryLoad();
                LoadTreeRoot(ResolveTreeRoot(folderPath));
            }
            else if (!await LoadFlatDirectoryRootAsync(folderPath, updateGitStatus: false))
            {
                return;
            }

            if (revealInLeftPanel)
            {
                _ensureLeftPanelVisible();
                _showLeftSidebarPage(0);
            }

            await _refreshGitStatusAsync();
        }

        public async Task<bool> NavigateRemoteVirtualPathAsync(
            string virtualPath,
            bool revealInLeftPanel = true)
        {
            if (!await _remoteWorkspaceService.ActivateVirtualPathAsync(virtualPath))
            {
                return false;
            }

            _leftSidebar.ExplorerTreeModeBtn.IsEnabled = true;
            SetCurrentFolderPath(_remoteWorkspaceService.ActiveDirectoryVirtualPath);
            _currentRepoPathChanged(string.Empty);
            if (IsTreeMode)
            {
                await LoadRemoteTreeRootAsync();
            }
            else
            {
                await LoadRemoteDirectoryAsync();
            }

            if (revealInLeftPanel)
            {
                _ensureLeftPanelVisible();
                _showLeftSidebarPage(0);
            }

            return true;
        }

        public async Task OpenRemoteFileAsync(string virtualPath)
        {
            try
            {
                OpenedTab? existingTab = _viewModel.Tabs.FirstOrDefault(tab =>
                    string.Equals(tab.RemotePath, virtualPath, StringComparison.OrdinalIgnoreCase));
                if (existingTab?.FilePath is string existingLocalPath && File.Exists(existingLocalPath))
                {
                    await _loadFileIntoTabAsync(existingLocalPath);
                    return;
                }

                string localPath = await _remoteWorkspaceService.DownloadVirtualFileAsync(virtualPath);
                await _loadFileIntoTabAsync(localPath);
            }
            catch (Exception ex)
            {
                SetExplorerStatusText(string.Format(
                    _localizationService.GetString("RemoteOperationFailedFormat", "작업 실패: {0}"),
                    ex.Message));
            }
        }

        public Task RefreshRemoteDirectoryAsync()
        {
            return !IsViewingRemote
                ? Task.CompletedTask
                : IsTreeMode
                    ? LoadRemoteTreeRootAsync()
                    : LoadRemoteDirectoryAsync();
        }

        private async Task LoadRemoteDirectoryAsync(bool clearFilter = true)
        {
            if (!IsViewingRemote || _remoteWorkspaceService.ActiveConnection == null)
            {
                return;
            }

            if (clearFilter)
            {
                ClearExplorerFilterState();
            }

            CurrentArchivePath = string.Empty;
            CurrentArchiveDirectory = string.Empty;
            _currentArchiveRemotePath = string.Empty;
            _remoteCancellation?.Cancel();
            _remoteCancellation?.Dispose();
            _remoteCancellation = new System.Threading.CancellationTokenSource();
            var cancellationToken = _remoteCancellation.Token;
            RemoteConnectionSettings connection = _remoteWorkspaceService.ActiveConnection;
            string loadingText = _localizationService.GetString("RemoteLoadingDirectory", "폴더를 불러오는 중...");
            SetExplorerStatusText(
                $"{connection.Profile.Name} · {connection.Profile.ProtocolLabel}\n{loadingText}");

            try
            {
                var entries = await _remoteWorkspaceService.ListActiveDirectoryAsync(cancellationToken);
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                _viewModel.ExplorerItems.Clear();
                bool isDark = _leftSidebar.ActualTheme == ElementTheme.Dark;
                foreach (RemoteDirectoryEntry entry in entries)
                {
                    if (_hideUnwantedFolders && entry.IsDirectory && IsHiddenFolderName(entry.Name))
                    {
                        continue;
                    }

                    var item = new ExplorerItem
                    {
                        Name = entry.Name,
                        Path = RemotePath.Create(
                            connection.Profile.Id,
                            entry.FullPath,
                            entry.IsDirectory,
                            connection.Profile.Name),
                        DisplayPath = _remoteWorkspaceService.GetDisplayPath(
                            RemotePath.Create(
                                connection.Profile.Id,
                                entry.FullPath,
                                entry.IsDirectory,
                                connection.Profile.Name)),
                        IsFolder = entry.IsDirectory,
                        ModifiedTime = entry.ModifiedTime?.LocalDateTime ?? DateTime.MinValue,
                        IsRemote = true,
                        RemoteServerId = connection.Profile.Id,
                        RemotePath = entry.FullPath,
                        IsArchive = !entry.IsDirectory &&
                            ArchiveExplorerService.IsSupportedArchivePath(entry.Name),
                        IsDark = isDark
                    };
                    _viewModel.ExplorerItems.Add(item);
                }

                if (_currentSortMode != ExplorerSortMode.Name)
                {
                    var sorted = SortItems(_viewModel.ExplorerItems).ToList();
                    _viewModel.ExplorerItems.Clear();
                    foreach (ExplorerItem item in sorted)
                    {
                        _viewModel.ExplorerItems.Add(item);
                    }
                }

                SetCurrentFolderPath(_remoteWorkspaceService.ActiveDirectoryVirtualPath);
                SetExplorerStatusText(FormatExplorerItemCount(_viewModel.ExplorerItems.Count));
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                SetExplorerStatusText(string.Format(
                    _localizationService.GetString("RemoteOperationFailedFormat", "작업 실패: {0}"),
                    ex.Message));
            }
        }

        private async Task ApplyRemoteFilterAsync(string query)
        {
            await LoadRemoteDirectoryAsync(clearFilter: false);
            if (string.IsNullOrWhiteSpace(query))
            {
                return;
            }

            var matched = _viewModel.ExplorerItems
                .Where(item => MatchesPattern(item.Name, query))
                .ToList();
            _viewModel.ExplorerItems.Clear();
            foreach (ExplorerItem item in matched)
            {
                _viewModel.ExplorerItems.Add(item);
            }

            SetExplorerStatusText(FormatExplorerFilterResult(matched.Count));
        }

        public void RefreshCurrentFolder()
        {
            if (IsViewingRemote)
            {
                _ = IsTreeMode
                    ? LoadRemoteTreeRootAsync()
                    : LoadRemoteDirectoryAsync();
                return;
            }

            if (IsViewingArchive)
            {
                if (File.Exists(CurrentArchivePath))
                {
                    LoadArchiveDirectoryRoot(CurrentArchivePath, CurrentArchiveDirectory);
                }
                else if (!string.IsNullOrEmpty(CurrentFolderPath) && Directory.Exists(CurrentFolderPath))
                {
                    LoadDirectoryRoot(CurrentFolderPath);
                }

                return;
            }

            if (!string.IsNullOrEmpty(CurrentFolderPath) && Directory.Exists(CurrentFolderPath))
            {
                LoadDirectoryRoot(CurrentFolderPath);
            }
        }

        public void RefreshTreeFolder(string folderPath)
        {
            if (!IsTreeMode)
            {
                RefreshCurrentFolder();
                return;
            }

            if (IsViewingRemote)
            {
                _ = LoadRemoteTreeRootAsync();
                return;
            }

            Microsoft.UI.Xaml.Controls.TreeViewNode? node = FindTreeNodeByPath(folderPath);
            if (node == null)
            {
                LoadTreeRoot(CurrentFolderPath);
                return;
            }

            node.IsExpanded = false;
            PopulateTreeNode(node, forceReload: true);
            node.IsExpanded = true;
        }

        private void RefreshExplorerItemThemeColors()
        {
            bool isDark = _leftSidebar.ActualTheme == ElementTheme.Dark;
            foreach (var item in _viewModel.ExplorerItems)
            {
                item.IsDark = isDark;
                item.RefreshThemeColors();
            }

            foreach (var item in EnumerateTreeItems())
            {
                item.IsDark = isDark;
                item.RefreshThemeColors();
            }
        }

        private void WireEvents()
        {
            _leftSidebar.ExplorerUpClick += OnExplorerUpClick;
            _leftSidebar.ExplorerBackClick += OnExplorerBackClick;
            _leftSidebar.ExplorerForwardClick += OnExplorerForwardClick;
            _leftSidebar.SelectFolderClick += OnSelectFolderClick;
            _leftSidebar.RefreshClick += OnExplorerRefreshClick;
            _leftSidebar.SortClick += OnExplorerSortClick;
            _leftSidebar.RemoteServerSelected += OnRemoteServerSelected;
            _leftSidebar.OpenInWindowsExplorerClick += OnOpenInWindowsExplorerClick;
            _leftSidebar.ExplorerHomeClick += OnExplorerHomeClick;
            _leftSidebar.ExplorerTreeModeClick += OnExplorerTreeModeClick;
            _leftSidebar.ExplorerTreeExpanding += OnExplorerTreeExpanding;
            _leftSidebar.ExplorerTreeItemInvoked += OnExplorerTreeItemInvoked;
            _leftSidebar.FileListViewItemClick += OnFileListViewItemClick;
            _leftSidebar.FileList.SelectionChanged += (_, _) => UpdateExplorerSelectionStatus();
            _leftSidebar.ExplorerTreeSelectionCountChanged += OnExplorerTreeSelectionCountChanged;
            _leftSidebar.ExplorerFilterTextChanged += OnExplorerFilterTextChanged;
            _leftSidebar.ExplorerHideUnwantedChanged += OnHideUnwantedChanged;
            _leftSidebar.ExplorerBreadcrumb.SegmentClicked += OnExplorerBreadcrumbItemClicked;
        }

        private void OnExplorerTreeSelectionCountChanged(int selectedCount)
        {
            _treeSelectionCount = Math.Max(0, selectedCount);
            UpdateExplorerSelectionStatus();
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

            await NavigateToFolderAsync(folder.Path);
        }

        private void OnExplorerRefreshClick(object sender, RoutedEventArgs e)
        {
            RefreshCurrentFolder();
        }

        private async void OnRemoteServerSelected(object? sender, Core.Models.RemoteServerSelectedEventArgs e)
        {
            if (!_remoteWorkspaceService.Activate(e.Profile))
            {
                SetExplorerStatusText(_localizationService.GetString(
                    "RemoteCredentialMissing",
                    "Windows 자격 증명 관리자에서 서버 주소 또는 비밀번호를 찾을 수 없습니다."));
                return;
            }

            _remoteWorkspaceService.NotifyActiveDirectoryOpened();
            _leftSidebar.ExplorerTreeModeBtn.IsEnabled = true;
            SetCurrentFolderPath(_remoteWorkspaceService.ActiveDirectoryVirtualPath);
            _currentRepoPathChanged(string.Empty);
            ClearExplorerFilterState();
            if (IsTreeMode)
            {
                await LoadRemoteTreeRootAsync();
            }
            else
            {
                await LoadRemoteDirectoryAsync();
            }
        }

        private void OnOpenInWindowsExplorerClick(object sender, RoutedEventArgs e)
        {
            string folderPath = CurrentFolderPath;
            if (IsViewingArchive)
            {
                folderPath = Path.GetDirectoryName(CurrentArchivePath) ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            {
                return;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = "explorer.exe",
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add(folderPath);
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

        private void OnExplorerTreeModeClick(object? sender, RoutedEventArgs e)
        {
            SetTreeMode(_leftSidebar.ExplorerTreeModeBtn.IsChecked == true);
        }

        private void OnExplorerTreeExpanding(object? sender, Microsoft.UI.Xaml.Controls.TreeViewExpandingEventArgs e)
        {
            Microsoft.UI.Xaml.Controls.TreeViewNode node = e.Node;
            _leftSidebar.DispatcherQueue.TryEnqueue(() =>
            {
                if (node.HasUnrealizedChildren)
                {
                    PopulateTreeNode(node);

                    // The Expanding event can arrive before the visual container
                    // reflects the node state. Keep the node open after loading.
                    if (!node.IsExpanded)
                    {
                        node.IsExpanded = true;
                    }
                }
            });
        }

        private void OnExplorerTreeItemInvoked(object? sender, Microsoft.UI.Xaml.Controls.TreeViewItemInvokedEventArgs e)
        {
            if (IsExplorerSelectionModifierDown())
            {
                return;
            }

            Microsoft.UI.Xaml.Controls.TreeViewNode? node = e.InvokedItem as Microsoft.UI.Xaml.Controls.TreeViewNode;
            ExplorerItem? item = e.InvokedItem as ExplorerItem
                ?? node?.Content as ExplorerItem;
            if (item == null)
            {
                return;
            }

            node ??= FindTreeNode(item);
            if (item.IsFolder || item.IsArchive)
            {
                if (node != null)
                {
                    if (node.HasUnrealizedChildren)
                    {
                        PopulateTreeNode(node);
                    }

                    node.IsExpanded = !node.IsExpanded;
                }

                return;
            }

            if (item.IsArchiveEntry)
            {
                _ = _loadArchiveEntryIntoTabAsync(item.ArchivePath, item.ArchiveEntryPath);
                return;
            }

            if (item.IsRemote)
            {
                _ = OpenRemoteFileAsync(item.Path);
                return;
            }

            if (string.Equals(Path.GetExtension(item.Path), LnkExtension, StringComparison.OrdinalIgnoreCase))
            {
                HandleLnkFile(lnkPath: item.Path);
                return;
            }

            _ = _loadFileIntoTabAsync(item.Path);
        }

        private string ResolveTreeRoot(string folderPath)
        {
            string? repositoryRoot = _gitService.FindRepositoryRoot(folderPath);
            return !string.IsNullOrWhiteSpace(repositoryRoot) && Directory.Exists(repositoryRoot)
                ? repositoryRoot
                : folderPath;
        }

        private void LoadTreeRoot(string folderPath)
        {
            ClearExplorerFilterState();
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            {
                return;
            }

            _viewModel.ExplorerItems.Clear();
            CurrentArchivePath = string.Empty;
            CurrentArchiveDirectory = string.Empty;
            _currentArchiveRemotePath = string.Empty;
            SetCurrentFolderPath(folderPath);
            UpdateRepoPath(folderPath);

            var directoryInfo = new DirectoryInfo(folderPath);
            var rootItem = new ExplorerItem
            {
                Name = string.IsNullOrWhiteSpace(directoryInfo.Name) ? directoryInfo.FullName : directoryInfo.Name,
                Path = directoryInfo.FullName,
                IsFolder = true,
                ModifiedTime = directoryInfo.LastWriteTime,
                IsDark = _leftSidebar.ActualTheme == ElementTheme.Dark
            };

            var rootNode = new Microsoft.UI.Xaml.Controls.TreeViewNode
            {
                Content = rootItem,
                HasUnrealizedChildren = true
            };

            ClearExplorerTreeNodes();
            _leftSidebar.ExplorerTree.RootNodes.Add(rootNode);
            PopulateTreeNode(rootNode);
            rootNode.IsExpanded = true;

            SetExplorerStatusText(FormatExplorerItemCount(rootNode.Children.Count));
            _ = UpdateGitStatusesAsync();
        }

        private async Task LoadRemoteTreeRootAsync()
        {
            ClearExplorerFilterState();
            if (!IsViewingRemote || _remoteWorkspaceService.ActiveConnection == null)
            {
                return;
            }

            CurrentArchivePath = string.Empty;
            CurrentArchiveDirectory = string.Empty;
            _currentArchiveRemotePath = string.Empty;
            RemoteConnectionSettings connection = _remoteWorkspaceService.ActiveConnection;
            string rootPath = _remoteWorkspaceService.ActiveDirectoryPath;
            string rootName = rootPath == "/"
                ? connection.Profile.Name
                : rootPath.TrimEnd('/').Split('/').LastOrDefault() ?? connection.Profile.Name;
            var rootItem = new ExplorerItem
            {
                Name = rootName,
                Path = RemotePath.Create(
                    connection.Profile.Id,
                    rootPath,
                    isDirectory: true,
                    serverName: connection.Profile.Name),
                DisplayPath = _remoteWorkspaceService.GetDisplayPath(
                    RemotePath.Create(
                        connection.Profile.Id,
                        rootPath,
                        isDirectory: true,
                        serverName: connection.Profile.Name)),
                IsFolder = true,
                IsRemote = true,
                RemoteServerId = connection.Profile.Id,
                RemotePath = rootPath,
                IsDark = _leftSidebar.ActualTheme == ElementTheme.Dark
            };
            var rootNode = new Microsoft.UI.Xaml.Controls.TreeViewNode
            {
                Content = rootItem,
                HasUnrealizedChildren = true
            };

            _viewModel.ExplorerItems.Clear();
            ClearExplorerTreeNodes();
            _leftSidebar.ExplorerTree.RootNodes.Add(rootNode);
            await PopulateRemoteTreeNodeAsync(rootNode, rootItem);
            rootNode.IsExpanded = true;
            SetCurrentFolderPath(_remoteWorkspaceService.ActiveDirectoryVirtualPath);
            SetExplorerStatusText(FormatExplorerItemCount(rootNode.Children.Count));
        }

        private void PopulateTreeNode(
            Microsoft.UI.Xaml.Controls.TreeViewNode node,
            bool forceReload = false)
        {
            if ((!forceReload && !node.HasUnrealizedChildren) ||
                node.Content is not ExplorerItem item)
            {
                return;
            }

            if (item.IsRemote && item.IsArchive)
            {
                _ = PopulateRemoteArchiveTreeNodeAsync(node, item);
                return;
            }

            if (item.IsRemote)
            {
                _ = PopulateRemoteTreeNodeAsync(node, item);
                return;
            }

            node.Children.Clear();
            bool isDark = _leftSidebar.ActualTheme == ElementTheme.Dark;
            System.Collections.Generic.IEnumerable<ExplorerItem> childItems;
            if (item.IsArchive)
            {
                childItems = _archiveExplorerService.CreateArchiveItems(item.Path, string.Empty);
            }
            else if (item.IsFolder && item.IsArchiveEntry)
            {
                childItems = _archiveExplorerService.CreateArchiveItems(item.ArchivePath, item.ArchiveEntryPath);
            }
            else if (item.IsFolder)
            {
                childItems = _directoryService.CreateDirectoryItems(item.Path);
            }
            else
            {
                node.HasUnrealizedChildren = false;
                return;
            }

            foreach (var childItem in SortItems(childItems))
            {
                childItem.IsDark = isDark;
                ApplyArchiveDisplayPath(childItem);
                childItem.IsArchive = !childItem.IsArchiveEntry &&
                                      !childItem.IsFolder &&
                                      _archiveExplorerService.IsSupportedArchiveFile(childItem.Path);
                node.Children.Add(new Microsoft.UI.Xaml.Controls.TreeViewNode
                {
                    Content = childItem,
                    HasUnrealizedChildren = childItem.IsFolder || childItem.IsArchive
                });
            }

            node.HasUnrealizedChildren = false;
            _ = UpdateGitStatusesAsync();
        }

        private async Task PopulateRemoteArchiveTreeNodeAsync(
            Microsoft.UI.Xaml.Controls.TreeViewNode node,
            ExplorerItem item)
        {
            if (!_loadingRemoteArchivePaths.Add(item.Path))
            {
                return;
            }

            try
            {
                string localArchivePath =
                    await _remoteWorkspaceService.DownloadVirtualFileAsync(item.Path);
                if (node.Content != item)
                {
                    return;
                }

                bool isDark = _leftSidebar.ActualTheme == ElementTheme.Dark;
                node.Children.Clear();
                foreach (ExplorerItem childItem in SortItems(
                             _archiveExplorerService.CreateArchiveItems(
                                 localArchivePath,
                                 string.Empty)))
                {
                    childItem.IsDark = isDark;
                    ApplyArchiveDisplayPath(childItem);
                    node.Children.Add(new Microsoft.UI.Xaml.Controls.TreeViewNode
                    {
                        Content = childItem,
                        HasUnrealizedChildren = childItem.IsFolder
                    });
                }

                node.HasUnrealizedChildren = false;
                node.IsExpanded = true;
            }
            catch (Exception ex)
            {
                SetExplorerStatusText(string.Format(
                    _localizationService.GetString("RemoteOperationFailedFormat", "작업 실패: {0}"),
                    ex.Message));
            }
            finally
            {
                _loadingRemoteArchivePaths.Remove(item.Path);
            }
        }

        private async Task PopulateRemoteTreeNodeAsync(
            Microsoft.UI.Xaml.Controls.TreeViewNode node,
            ExplorerItem item)
        {
            if (!item.IsFolder || !item.IsRemote ||
                _remoteWorkspaceService.ActiveConnection == null)
            {
                node.HasUnrealizedChildren = false;
                return;
            }

            try
            {
                var entries = await _remoteWorkspaceService.ListDirectoryAsync(item.RemotePath);
                if (node.Content != item ||
                    _remoteWorkspaceService.ActiveConnection == null)
                {
                    return;
                }

                RemoteConnectionSettings connection = _remoteWorkspaceService.ActiveConnection;
                bool isDark = _leftSidebar.ActualTheme == ElementTheme.Dark;
                node.Children.Clear();
                foreach (RemoteDirectoryEntry entry in entries)
                {
                    var child = new ExplorerItem
                    {
                        Name = entry.Name,
                        Path = RemotePath.Create(
                            connection.Profile.Id,
                            entry.FullPath,
                            entry.IsDirectory,
                            connection.Profile.Name),
                        DisplayPath = _remoteWorkspaceService.GetDisplayPath(
                            RemotePath.Create(
                                connection.Profile.Id,
                                entry.FullPath,
                                entry.IsDirectory,
                                connection.Profile.Name)),
                        IsFolder = entry.IsDirectory,
                        ModifiedTime = entry.ModifiedTime?.LocalDateTime ?? DateTime.MinValue,
                        IsRemote = true,
                        RemoteServerId = connection.Profile.Id,
                        RemotePath = entry.FullPath,
                        IsArchive = !entry.IsDirectory &&
                            ArchiveExplorerService.IsSupportedArchivePath(entry.Name),
                        IsDark = isDark
                    };
                    node.Children.Add(new Microsoft.UI.Xaml.Controls.TreeViewNode
                    {
                        Content = child,
                        HasUnrealizedChildren = child.IsFolder || child.IsArchive
                    });
                }

                node.HasUnrealizedChildren = false;
                node.IsExpanded = true;
            }
            catch (Exception ex)
            {
                SetExplorerStatusText(string.Format(
                    _localizationService.GetString("RemoteOperationFailedFormat", "작업 실패: {0}"),
                    ex.Message));
            }
        }

        private Microsoft.UI.Xaml.Controls.TreeViewNode? FindTreeNode(ExplorerItem item)
        {
            foreach (var rootNode in _leftSidebar.ExplorerTree.RootNodes)
            {
                var match = FindTreeNode(rootNode, item);
                if (match != null)
                {
                    return match;
                }
            }

            return null;
        }

        private Microsoft.UI.Xaml.Controls.TreeViewNode? FindTreeNodeByPath(string folderPath)
        {
            foreach (var rootNode in _leftSidebar.ExplorerTree.RootNodes)
            {
                var match = FindTreeNodeByPath(rootNode, folderPath);
                if (match != null)
                {
                    return match;
                }
            }

            return null;
        }

        private static Microsoft.UI.Xaml.Controls.TreeViewNode? FindTreeNodeByPath(
            Microsoft.UI.Xaml.Controls.TreeViewNode node,
            string folderPath)
        {
            if (node.Content is ExplorerItem item &&
                string.Equals(item.Path, folderPath, StringComparison.OrdinalIgnoreCase))
            {
                return node;
            }

            foreach (var child in node.Children)
            {
                var match = FindTreeNodeByPath(child, folderPath);
                if (match != null)
                {
                    return match;
                }
            }

            return null;
        }

        private static Microsoft.UI.Xaml.Controls.TreeViewNode? FindTreeNode(
            Microsoft.UI.Xaml.Controls.TreeViewNode node,
            ExplorerItem item)
        {
            if (ReferenceEquals(node.Content, item))
            {
                return node;
            }

            foreach (var child in node.Children)
            {
                var match = FindTreeNode(child, item);
                if (match != null)
                {
                    return match;
                }
            }

            return null;
        }

        private void OnExplorerUpClick(object sender, RoutedEventArgs e)
        {
            if (IsViewingArchive)
            {
                NavigateArchiveUp();
                return;
            }

            if (IsViewingRemote)
            {
                if (_remoteWorkspaceService.NavigateUp())
                {
                    _ = LoadRemoteDirectoryAsync();
                }
                return;
            }

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

        private void OnExplorerBackClick(object sender, RoutedEventArgs e)
        {
            if (_folderHistory.Count == 0)
            {
                return;
            }

            ExplorerHistoryEntry entry = _folderHistory.Pop();
            PushForwardHistory(CreateCurrentHistoryEntry());
            UpdateBackButtonState();
            _isBackNavigation = true;
            try
            {
                NavigateToHistoryEntry(entry);
            }
            finally
            {
                _isBackNavigation = false;
            }
        }

        private void OnExplorerForwardClick(object sender, RoutedEventArgs e)
        {
            if (_forwardHistory.Count == 0)
            {
                return;
            }

            ExplorerHistoryEntry entry = _forwardHistory.Pop();
            _isForwardNavigation = true;
            try
            {
                NavigateToHistoryEntry(entry);
            }
            finally
            {
                _isForwardNavigation = false;
            }

            UpdateBackButtonState();
        }

        private void NavigateToHistoryEntry(ExplorerHistoryEntry entry)
        {
            if (entry.IsArchive)
            {
                LoadArchiveDirectoryRoot(entry.Path, entry.SecondaryPath);
            }
            else if (RemotePath.IsRemote(entry.Path))
            {
                _ = NavigateRemoteVirtualPathAsync(entry.Path, revealInLeftPanel: false);
            }
            else if (Directory.Exists(entry.Path))
            {
                UpdateRepoPath(entry.Path);
                LoadDirectoryRoot(entry.Path);
            }
        }

        private ExplorerHistoryEntry CreateCurrentHistoryEntry()
        {
            if (IsViewingArchive && !string.IsNullOrWhiteSpace(CurrentArchivePath))
            {
                return new ExplorerHistoryEntry(CurrentArchivePath, CurrentArchiveDirectory, isArchive: true);
            }

            return new ExplorerHistoryEntry(CurrentFolderPath);
        }

        private void PushForwardHistory(ExplorerHistoryEntry entry)
        {
            if (string.IsNullOrWhiteSpace(entry.Path))
            {
                return;
            }

            _forwardHistory.Push(entry);
            if (_forwardHistory.Count > MaxFolderHistory)
            {
                ExplorerHistoryEntry[] entries = _forwardHistory.ToArray();
                _forwardHistory.Clear();
                for (int i = 0; i < entries.Length - 1; i++)
                {
                    _forwardHistory.Push(entries[i]);
                }
            }
        }

        private void NavigateArchiveUp()
        {
            if (string.IsNullOrEmpty(CurrentArchiveDirectory))
            {
                if (!string.IsNullOrWhiteSpace(_currentArchiveRemotePath))
                {
                    string remoteParent = RemotePath.GetParent(_currentArchiveRemotePath);
                    _ = NavigateRemoteVirtualPathAsync(remoteParent, revealInLeftPanel: false);
                    return;
                }

                string archiveFolderPath = Path.GetDirectoryName(CurrentArchivePath) ?? CurrentFolderPath;
                if (!string.IsNullOrWhiteSpace(archiveFolderPath) && Directory.Exists(archiveFolderPath))
                {
                    UpdateRepoPath(archiveFolderPath);
                    LoadDirectoryRoot(archiveFolderPath);
                }

                return;
            }

            string parentEntryPath = ArchiveExplorerService.GetParentEntryPath(CurrentArchiveDirectory);
            LoadArchiveDirectoryRoot(CurrentArchivePath, parentEntryPath);
        }

        private void OnFileListViewItemClick(object sender, Microsoft.UI.Xaml.Controls.ItemClickEventArgs e)
        {
            var item = e.ClickedItem as ExplorerItem
                       ?? _leftSidebar.FileList.SelectedItem as ExplorerItem;
            if (item == null)
            {
                return;
            }

            // Extended selection uses Ctrl/Shift clicks. Those clicks should only
            // update the selection and must never navigate or open a file.
            if (IsExplorerSelectionModifierDown())
            {
                return;
            }

            if (item.IsRemote)
            {
                if (item.IsFolder)
                {
                    _remoteWorkspaceService.NavigateTo(item.RemotePath);
                    _ = LoadRemoteDirectoryAsync();
                }
                else
                {
                    _ = OpenRemoteFileAsync(item.Path);
                }
                return;
            }

            if (item.IsFolder)
            {
                if (item.IsArchiveEntry)
                {
                    LoadArchiveDirectoryRoot(item.ArchivePath, item.ArchiveEntryPath);
                    return;
                }

                UpdateRepoPath(item.Path);
                LoadDirectoryRoot(item.Path);
            }
            else if (item.IsArchiveEntry)
            {
                _ = _loadArchiveEntryIntoTabAsync(item.ArchivePath, item.ArchiveEntryPath);
            }
            else if (item.IsArchive || _archiveExplorerService.IsSupportedArchiveFile(item.Path))
            {
                LoadArchiveDirectoryRoot(item.Path, string.Empty);
            } else if (string.Equals(Path.GetExtension(item.Path), LnkExtension, StringComparison.OrdinalIgnoreCase))
            {
                HandleLnkFile(lnkPath: item.Path);
            }
			else
            {
                _ = _loadFileIntoTabAsync(item.Path);
            }
        }

        private static bool IsExplorerSelectionModifierDown()
        {
            var controlState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
                Windows.System.VirtualKey.Control);
            var shiftState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
                Windows.System.VirtualKey.Shift);
            return (controlState & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down ||
                   (shiftState & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
        }

        private void SetCurrentFolderPath(string folderPath)
        {
            if (!_isBackNavigation &&
                !string.IsNullOrWhiteSpace(CurrentFolderPath) &&
                !string.Equals(CurrentFolderPath, folderPath, StringComparison.OrdinalIgnoreCase))
            {
                PushHistory(new ExplorerHistoryEntry(CurrentFolderPath));
            }

            CurrentFolderPath = folderPath;
            _currentFolderChanged(folderPath);
            UpdateExplorerBreadcrumb();
        }

        private void UpdateExplorerBreadcrumb()
        {
            var segments = new List<ExplorerBreadcrumbSegment>();
            try
            {
                if (IsViewingArchive && !string.IsNullOrWhiteSpace(CurrentArchivePath))
                {
                    BuildArchiveBreadcrumb(segments);
                }
                else if (IsViewingRemote && _remoteWorkspaceService.IsActive)
                {
                    BuildRemoteBreadcrumb(segments, CurrentFolderPath);
                }
                else if (!string.IsNullOrWhiteSpace(CurrentFolderPath))
                {
                    BuildLocalBreadcrumb(segments, CurrentFolderPath);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed building explorer breadcrumb: {ex.Message}");
            }

            bool hasSegments = segments.Count > 0;
            // 표시 상태를 먼저 정해야 폭이 이미 확보된 경우 ItemsSource 설정 시점에
            // 바로 올바른 폭으로 그려 SizeChanged를 기다리지 않는다.
            _leftSidebar.ExplorerBreadcrumb.Visibility = hasSegments ? Visibility.Visible : Visibility.Collapsed;
            _leftSidebar.ExplorerBreadcrumb.ItemsSource = hasSegments ? segments : null;
        }

        private void BuildLocalBreadcrumb(List<ExplorerBreadcrumbSegment> segments, string folderPath)
        {
            string root = Path.GetPathRoot(folderPath) ?? string.Empty;
            string remainder = string.IsNullOrEmpty(root) ? folderPath : folderPath[root.Length..];
            if (!string.IsNullOrEmpty(root))
            {
                string rootName = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                segments.Add(new ExplorerBreadcrumbSegment(
                    string.IsNullOrEmpty(rootName) ? root : rootName,
                    root));
            }

            string current = root;
            foreach (string part in remainder.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries))
            {
                current = string.IsNullOrEmpty(current) ? part : Path.Combine(current, part);
                segments.Add(new ExplorerBreadcrumbSegment(part, current));
            }
        }

        private void BuildRemoteBreadcrumb(List<ExplorerBreadcrumbSegment> segments, string virtualPath)
        {
            if (!RemotePath.TryParse(virtualPath, out Guid serverId, out string remotePath))
            {
                return;
            }

            string serverName = RemotePath.GetServerNameHint(virtualPath)
                ?? _remoteWorkspaceService.ActiveConnection?.Profile.Name
                ?? "Remote";
            segments.Add(new ExplorerBreadcrumbSegment(
                serverName,
                RemotePath.Create(serverId, "/", isDirectory: true, serverName)));

            string current = string.Empty;
            foreach (string part in remotePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                current = $"{current}/{part}";
                segments.Add(new ExplorerBreadcrumbSegment(
                    part,
                    RemotePath.Create(serverId, current, isDirectory: true, serverName)));
            }
        }

        private void BuildArchiveBreadcrumb(List<ExplorerBreadcrumbSegment> segments)
        {
            string archivePath = CurrentArchivePath;
            string entryDirectory = CurrentArchiveDirectory;

            if (!string.IsNullOrWhiteSpace(_currentArchiveRemotePath) &&
                RemotePath.TryParse(_currentArchiveRemotePath, out Guid serverId, out string remoteArchivePath))
            {
                string serverName = RemotePath.GetServerNameHint(_currentArchiveRemotePath) ?? "Remote";
                segments.Add(new ExplorerBreadcrumbSegment(
                    serverName,
                    RemotePath.Create(serverId, "/", isDirectory: true, serverName)));

                string current = string.Empty;
                string[] archiveParts = remoteArchivePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < archiveParts.Length; i++)
                {
                    current = $"{current}/{archiveParts[i]}";
                    bool isLast = i == archiveParts.Length - 1;
                    segments.Add(new ExplorerBreadcrumbSegment(
                        archiveParts[i],
                        isLast
                            ? RemotePath.GetParent(_currentArchiveRemotePath)
                            : RemotePath.Create(serverId, current, isDirectory: true, serverName)));
                }
            }
            else
            {
                string root = Path.GetPathRoot(archivePath) ?? string.Empty;
                string remainder = string.IsNullOrEmpty(root) ? archivePath : archivePath[root.Length..];
                if (!string.IsNullOrEmpty(root))
                {
                    string rootName = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    segments.Add(new ExplorerBreadcrumbSegment(
                        string.IsNullOrEmpty(rootName) ? root : rootName,
                        root));
                }

                string current = root;
                string[] archiveParts = remainder.Split(
                    new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                    StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < archiveParts.Length; i++)
                {
                    current = string.IsNullOrEmpty(current) ? archiveParts[i] : Path.Combine(current, archiveParts[i]);
                    bool isLast = i == archiveParts.Length - 1;
                    segments.Add(new ExplorerBreadcrumbSegment(
                        archiveParts[i],
                        isLast ? (Path.GetDirectoryName(archivePath) ?? root) : current));
                }
            }

            segments.Add(new ExplorerBreadcrumbSegment(
                "!",
                archivePath,
                isArchive: true,
                archivePath: archivePath));

            if (!string.IsNullOrWhiteSpace(entryDirectory))
            {
                string currentEntry = string.Empty;
                foreach (string part in entryDirectory.Split('/', StringSplitOptions.RemoveEmptyEntries))
                {
                    currentEntry = string.IsNullOrEmpty(currentEntry) ? part : $"{currentEntry}/{part}";
                    segments.Add(new ExplorerBreadcrumbSegment(
                        part,
                        currentEntry,
                        isArchive: true,
                        archivePath: archivePath));
                }
            }
        }

        private void OnExplorerBreadcrumbItemClicked(object? sender, ExplorerPathSegmentClickedEventArgs e)
        {
            ExplorerBreadcrumbSegment segment = e.Segment;

            if (segment.IsArchive)
            {
                if (!string.IsNullOrWhiteSpace(segment.ArchivePath) && File.Exists(segment.ArchivePath))
                {
                    LoadArchiveDirectoryRoot(segment.ArchivePath, segment.EntryDirectory);
                }

                return;
            }

            if (RemotePath.IsRemote(segment.Path))
            {
                _ = NavigateRemoteVirtualPathAsync(segment.Path, revealInLeftPanel: false);
                return;
            }

            if (!string.IsNullOrWhiteSpace(segment.Path) && Directory.Exists(segment.Path))
            {
                UpdateRepoPath(segment.Path);
                LoadDirectoryRoot(segment.Path);
            }
        }

        private void PushHistory(ExplorerHistoryEntry entry)
        {
            _folderHistory.Push(entry);
            if (_folderHistory.Count > MaxFolderHistory)
            {
                ExplorerHistoryEntry[] entries = _folderHistory.ToArray();
                _folderHistory.Clear();
                for (int i = 0; i < entries.Length - 1; i++)
                {
                    _folderHistory.Push(entries[i]);
                }
            }

            if (!_isForwardNavigation)
            {
                _forwardHistory.Clear();
            }

            UpdateBackButtonState();
        }

        private void UpdateBackButtonState()
        {
            _leftSidebar.ExplorerBackBtn.IsEnabled = !IsTreeMode && _folderHistory.Count > 0;
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

        private void SetExplorerStatusText(string text)
        {
            _explorerStatusBaseText = text;
            UpdateExplorerSelectionStatus();
        }

        private void UpdateExplorerSelectionStatus()
        {
            int selectedCount = IsTreeMode
                ? _treeSelectionCount
                : _leftSidebar.FileList.SelectedItems.Count;
            if (selectedCount <= 0)
            {
                _leftSidebar.ExplorerStatus.Text = _explorerStatusBaseText;
                return;
            }

            string format = _localizationService.GetString(
                "ExplorerSelectedItemCountFormat",
                "{0} ({1:N0})");
            _leftSidebar.ExplorerStatus.Text = string.Format(
                format,
                _explorerStatusBaseText,
                selectedCount);
        }

        private void ClearExplorerTreeNodes()
        {
            // Clear selection while every selected node still belongs to the tree.
            // Letting RootNodes.Clear detach selected nodes itself can corrupt the
            // WinUI TreeView selection projection in multiple-selection mode.
            _leftSidebar.ClearExplorerTreeSelection();
            _treeSelectionCount = 0;
            _leftSidebar.ExplorerTree.RootNodes.Clear();
        }

        public async Task UpdateGitStatusesAsync()
        {
            bool isDark = _leftSidebar.ActualTheme == ElementTheme.Dark;
            if (IsViewingArchive)
            {
                _leftSidebar.DispatcherQueue.TryEnqueue(() =>
                {
                    foreach (var item in GetVisibleExplorerItems())
                    {
                        item.IsDark = isDark;
                        item.GitStatus = ExplorerItem.GitStatusType.Clean;
                    }
                });
                return;
            }

            string repoPath = _gitService.FindRepositoryRoot(CurrentFolderPath) ?? string.Empty;
            if (string.IsNullOrEmpty(repoPath))
            {
                _leftSidebar.DispatcherQueue.TryEnqueue(() =>
                {
                    foreach (var item in GetVisibleExplorerItems())
                    {
                        item.IsDark = isDark;
                        item.GitStatus = ExplorerItem.GitStatusType.Clean;
                    }
                });
                return;
            }

            var statuses = await _gitService.GetFileStatusesAsync(
                repoPath,
                includeAllUntrackedFiles: true,
                matchIgnoredDirectories: true);
            _leftSidebar.DispatcherQueue.TryEnqueue(() =>
            {
                UpdateItemsGitStatus(GetVisibleExplorerItems(), statuses, isDark);
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

        private void UpdateItemsGitStatus(
            System.Collections.Generic.IEnumerable<ExplorerItem> items,
            System.Collections.Generic.Dictionary<string, string> statuses,
            bool isDark)
        {
            foreach (var item in items)
            {
                item.IsDark = isDark;
                if (item.IsFolder)
                {
                    bool hasModified = false;
                    bool hasAdded = false;

                    if (statuses.TryGetValue(item.Path, out string? folderStatus))
                    {
                        string trimmedFolderStatus = folderStatus.Trim();
                        if (trimmedFolderStatus == "??")
                        {
                            hasAdded = true;
                        }
                        else if (trimmedFolderStatus != "!!")
                        {
                            hasModified = true;
                        }
                    }

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

            if (IsTreeMode && IsViewingRemote)
            {
                _ = LoadRemoteTreeRootAsync();
                return;
            }

            if (IsTreeMode && !string.IsNullOrWhiteSpace(CurrentFolderPath))
            {
                LoadTreeRoot(CurrentFolderPath);
                return;
            }

            if (_viewModel.ExplorerItems.Count > 0)
            {
                var sorted = SortItems(_viewModel.ExplorerItems).ToList();
                _viewModel.ExplorerItems.ReplaceAll(sorted);
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

        private System.Collections.Generic.IEnumerable<ExplorerItem> SortItems(
            System.Collections.Generic.IEnumerable<ExplorerItem> items,
            ExplorerSortMode? sortMode = null)
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

            switch (sortMode ?? _currentSortMode)
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

        private string _lastFilterQuery = string.Empty;
        private bool _hideUnwantedFolders = true;
        private bool _isClearingExplorerFilter;

        private void ClearExplorerFilterState()
        {
            _isClearingExplorerFilter = true;
            try
            {
                _lastFilterQuery = string.Empty;
                _leftSidebar.ClearExplorerFilter();
            }
            finally
            {
                _isClearingExplorerFilter = false;
            }
        }

        private async void OnExplorerFilterTextChanged(object sender, Microsoft.UI.Xaml.Controls.TextChangedEventArgs e)
        {
            if (_isClearingExplorerFilter)
            {
                return;
            }

            if (sender is Microsoft.UI.Xaml.Controls.TextBox textBox)
            {
                string query = textBox.Text;
                _lastFilterQuery = query;
                await ApplyFilterAsync(query);
            }
        }

        private async void OnHideUnwantedChanged(object sender, RoutedEventArgs e)
        {
            _hideUnwantedFolders = _leftSidebar.ExplorerHideUnwantedBtn.IsChecked != true;
            if (!string.IsNullOrWhiteSpace(_lastFilterQuery))
            {
                await ApplyFilterAsync(_lastFilterQuery);
            }
            else
            {
                RefreshCurrentFolder();
            }
        }

        private async Task ApplyFilterAsync(string query)
        {
            if (IsViewingRemote)
            {
                await ApplyRemoteFilterAsync(query);
                return;
            }

            if (IsViewingArchive)
            {
                await ApplyArchiveFilterAsync(query);
                return;
            }

            if (string.IsNullOrEmpty(CurrentFolderPath) || !Directory.Exists(CurrentFolderPath))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(query))
            {
                LoadDirectoryRoot(CurrentFolderPath);
                return;
            }

            string currentRoot = CurrentFolderPath;
            var matchedItems = await Task.Run(() => PerformRecursiveSearch(currentRoot, query));

            if (query == _lastFilterQuery && currentRoot == CurrentFolderPath)
            {
                _leftSidebar.DispatcherQueue.TryEnqueue(() =>
                {
                    _viewModel.ExplorerItems.Clear();
                    bool isDark = _leftSidebar.ActualTheme == ElementTheme.Dark;
                    foreach (var item in SortItems(matchedItems))
                    {
                        item.IsDark = isDark;
                        ApplyArchiveDisplayPath(item);
                        _viewModel.ExplorerItems.Add(item);
                    }

                    SetExplorerStatusText(FormatExplorerFilterResult(_viewModel.ExplorerItems.Count));
                });

                await UpdateGitStatusesAsync();
            }
        }

        private async Task ApplyArchiveFilterAsync(string query)
        {
            if (string.IsNullOrWhiteSpace(CurrentArchivePath) || !File.Exists(CurrentArchivePath))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(query))
            {
                LoadArchiveDirectoryRoot(CurrentArchivePath, CurrentArchiveDirectory);
                return;
            }

            string archivePath = CurrentArchivePath;
            string archiveDirectory = CurrentArchiveDirectory;
            var matchedItems = await Task.Run(() =>
                _archiveExplorerService.SearchArchiveItems(archivePath, archiveDirectory, query, MatchesPattern));

            if (_hideUnwantedFolders)
            {
                matchedItems = matchedItems.Where(item => !item.IsFolder || !IsHiddenFolderName(item.Name)).ToList();
            }

            if (query == _lastFilterQuery &&
                archivePath == CurrentArchivePath &&
                archiveDirectory == CurrentArchiveDirectory)
            {
                _leftSidebar.DispatcherQueue.TryEnqueue(() =>
                {
                    _viewModel.ExplorerItems.Clear();
                    bool isDark = _leftSidebar.ActualTheme == ElementTheme.Dark;
                    foreach (var item in SortItems(matchedItems))
                    {
                        item.IsDark = isDark;
                        _viewModel.ExplorerItems.Add(item);
                    }

                    SetExplorerStatusText(FormatExplorerFilterResult(_viewModel.ExplorerItems.Count));
                });
            }
        }

        private static bool IsHiddenFolderName(string name)
        {
            return name.StartsWith(".", StringComparison.Ordinal) ||
                string.Equals(name, "node_modules", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "obj", StringComparison.OrdinalIgnoreCase);
        }

        private static bool MatchesPattern(string name, string pattern)
        {
            if (string.IsNullOrEmpty(pattern))
                return true;

            if (pattern.Contains('*') || pattern.Contains('?'))
            {
                string regexPattern = "^" + Regex.Escape(pattern)
                    .Replace("\\*", ".*")
                    .Replace("\\?", ".") + "$";
                return Regex.IsMatch(name, regexPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }

            return name.Contains(pattern, StringComparison.OrdinalIgnoreCase);
        }


        private System.Collections.Generic.List<ExplorerItem> PerformRecursiveSearch(string rootPath, string query)
        {
            var results = new System.Collections.Generic.List<ExplorerItem>();
            var dirsToProcess = new System.Collections.Generic.Stack<string>();
            dirsToProcess.Push(rootPath);

            var visited = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                visited.Add(Path.GetFullPath(rootPath));
            }
            catch
            {
                visited.Add(rootPath);
            }

            var ignoredFolderNames = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "node_modules", "obj", ".git", ".vs", ".idea", "dist", "build", "out"
            };

            while (dirsToProcess.Count > 0)
            {
                string currentDir = dirsToProcess.Pop();
                try
                {
                    var dirInfo = new DirectoryInfo(currentDir);

                    if (currentDir != rootPath)
                    {
                        if (dirInfo.Attributes.HasFlag(FileAttributes.Hidden) ||
                            dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                            ignoredFolderNames.Contains(dirInfo.Name) ||
                            (_hideUnwantedFolders && IsHiddenFolderName(dirInfo.Name)))
                        {
                            continue;
                        }
                    }

                    foreach (var file in dirInfo.GetFiles())
                    {
                        if (file.Attributes.HasFlag(FileAttributes.Hidden))
                        {
                            continue;
                        }

                        if (MatchesPattern(file.Name, query))
                        {
                            string relPath = Path.GetRelativePath(rootPath, file.FullName);
                            string? relativeDir = Path.GetDirectoryName(relPath);

                            results.Add(new ExplorerItem
                            {
                                Name = file.Name,
                                Path = file.FullName,
                                IsFolder = false,
                                IsArchive = _archiveExplorerService.IsSupportedArchiveFile(file.FullName),
                                ModifiedTime = file.LastWriteTime,
                                SubPath = relativeDir ?? string.Empty
                            });
                        }
                    }

                    foreach (var subDir in dirInfo.GetDirectories())
                    {
                        if (subDir.Attributes.HasFlag(FileAttributes.Hidden) ||
                            subDir.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                            ignoredFolderNames.Contains(subDir.Name) ||
                            (_hideUnwantedFolders && IsHiddenFolderName(subDir.Name)))
                        {
                            continue;
                        }

                        string canonicalSubPath;
                        try
                        {
                            canonicalSubPath = Path.GetFullPath(subDir.FullName);
                        }
                        catch
                        {
                            canonicalSubPath = subDir.FullName;
                        }

                        if (!visited.Add(canonicalSubPath))
                        {
                            continue;
                        }

                        if (MatchesPattern(subDir.Name, query))
                        {
                            string relPath = Path.GetRelativePath(rootPath, subDir.FullName);
                            string? relativeDir = Path.GetDirectoryName(relPath);

                            results.Add(new ExplorerItem
                            {
                                Name = subDir.Name,
                                Path = subDir.FullName,
                                IsFolder = true,
                                ModifiedTime = subDir.LastWriteTime,
                                SubPath = relativeDir ?? string.Empty
                            });
                        }

                        dirsToProcess.Push(subDir.FullName);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error scanning folder {currentDir}: {ex.Message}");
                }
            }

            return results;
        }

        private string FormatExplorerFilterResult(int matchCount)
        {
            string key = "ExplorerFilterResultFormat";
            string fallback = "{0:N0}개 결과";
            string format = _localizationService.GetString(key, fallback);
            return string.Format(format, matchCount);
        }

        private System.Collections.Generic.IEnumerable<ExplorerItem> GetVisibleExplorerItems()
        {
            return IsTreeMode ? EnumerateTreeItems() : _viewModel.ExplorerItems;
        }

        private System.Collections.Generic.IEnumerable<ExplorerItem> EnumerateTreeItems()
        {
            foreach (var rootNode in _leftSidebar.ExplorerTree.RootNodes)
            {
                foreach (var item in EnumerateTreeItems(rootNode))
                {
                    yield return item;
                }
            }
        }

        private static System.Collections.Generic.IEnumerable<ExplorerItem> EnumerateTreeItems(
            Microsoft.UI.Xaml.Controls.TreeViewNode node)
        {
            if (node.Content is ExplorerItem item)
            {
                yield return item;
            }

            foreach (var child in node.Children)
            {
                foreach (var descendant in EnumerateTreeItems(child))
                {
                    yield return descendant;
                }
            }
        }

        private const string LnkExtension = ".lnk";

        private static string? ResolveShortcutTarget(string lnkPath)
        {
            if (!string.Equals(Path.GetExtension(lnkPath), LnkExtension, StringComparison.OrdinalIgnoreCase))
                return null;

            try
            {
                var shellLinkType = Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"))
                    ?? throw new InvalidOperationException("Failed to get ShellLink type");
                object shellLink = Activator.CreateInstance(shellLinkType)!;

                var persistFile = (IPersistFile)shellLink;
                persistFile.Load(lnkPath, 0); // STGM_READ

                var link = (IShellLinkW)shellLink;
                // Resolve the link (SLR_NO_UI = 0x01 avoids showing error dialogs)
                link.Resolve(IntPtr.Zero, 0x01);

                var sb = new StringBuilder(1024);
                link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0x00);
                string targetPath = sb.ToString();

                if (string.IsNullOrWhiteSpace(targetPath))
                {
                    sb.Clear();
                    link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0x04); // SLGP_RAWPATH
                    targetPath = sb.ToString();
                }

                if (string.IsNullOrWhiteSpace(targetPath))
                {
                    if (link.GetIDList(out IntPtr pidl) == 0 && pidl != IntPtr.Zero)
                    {
                        try
                        {
                            var idListSb = new StringBuilder(1024);
                            if (SHGetPathFromIDListW(pidl, idListSb))
                            {
                                targetPath = idListSb.ToString();
                            }
                        }
                        finally
                        {
                            Marshal.FreeCoTaskMem(pidl);
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(targetPath))
                {
                    targetPath = Environment.ExpandEnvironmentVariables(targetPath);
                }

                return targetPath;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to resolve shortcut target for '{lnkPath}': {ex.Message}");
                return null;
            }
        }

        private void HandleLnkFile(string lnkPath)
        {
            string? target = ResolveShortcutTarget(lnkPath);
            if (string.IsNullOrWhiteSpace(target))
            {
                _ = _loadFileIntoTabAsync(lnkPath);
                return;
            }

            if (!Directory.Exists(target) && !File.Exists(target) && !Path.IsPathRooted(target))
            {
                string? lnkDir = Path.GetDirectoryName(lnkPath);
                if (!string.IsNullOrEmpty(lnkDir))
                {
                    string combined = Path.GetFullPath(Path.Combine(lnkDir, target));
                    if (Directory.Exists(combined) || File.Exists(combined))
                    {
                        target = combined;
                    }
                }
            }

            if (Directory.Exists(target))
            {
                UpdateRepoPath(target);
                LoadDirectoryRoot(target);
            }
            else if (File.Exists(target))
            {
                _ = _loadFileIntoTabAsync(target);
            }
            else
            {
                _ = _loadFileIntoTabAsync(lnkPath);
            }
        }

        [System.Runtime.InteropServices.DllImport("shlwapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, ExactSpelling = true)]
        private static extern int StrCmpLogicalW(string x, string y);

        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, ExactSpelling = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool SHGetPathFromIDListW(IntPtr pidl, [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszPath);
    }
}

[ComImport]
[Guid("00021401-0000-0000-C000-000000000046")]
internal class ShellLink
{
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("000214F9-0000-0000-C000-000000000046")]
internal interface IShellLinkW
{
    void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, uint fFlags);
    [PreserveSig]
    int GetIDList(out IntPtr ppidl);
    void SetIDList(IntPtr pidl);
    void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
    void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
    void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
    void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
    void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
    void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
    void GetHotkey(out short pwHotkey);
    void SetHotkey(short wHotkey);
    void GetShowCmd(out int piShowCmd);
    void SetShowCmd(int iShowCmd);
    void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
    void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
    void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
    void Resolve(IntPtr hwnd, uint fFlags);
    void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("0000010B-0000-0000-C000-000000000046")]
internal interface IPersistFile
{
    // IPersist
    void GetClassID(out Guid pClassID);
    // IPersistFile
    [PreserveSig]
    int IsDirty();
    void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
    void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
    void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
    void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
}
