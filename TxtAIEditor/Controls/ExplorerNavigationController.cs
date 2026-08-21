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
        private readonly ExplorerItemSorter _itemSorter = new();
        private readonly ExplorerSearchService _searchService;
        private readonly ExplorerGitStatusService _gitStatusService;
        private readonly ExplorerBreadcrumbBuilder _breadcrumbBuilder;
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
            _searchService = new ExplorerSearchService(archiveExplorerService);
            _gitStatusService = new ExplorerGitStatusService(gitService);
            _breadcrumbBuilder = new ExplorerBreadcrumbBuilder(remoteWorkspaceService);
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
            ExplorerItemSorter.SortMode sortMode = _itemSorter.Mode;
            try
            {
                List<ExplorerItem> items = await Task.Run(() =>
                {
                    var loadedItems = new List<ExplorerItem>();
                    foreach (ExplorerItem item in _directoryService.CreateDirectoryItems(folderPath))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (_hideUnwantedFolders &&
                            item.IsFolder &&
                            ExplorerSearchService.IsHiddenFolderName(item.Name))
                        {
                            continue;
                        }

                        item.IsDark = isDark;
                        item.IsArchive = !item.IsFolder &&
                            _archiveExplorerService.IsSupportedArchiveFile(item.Path);
                        loadedItems.Add(item);
                    }

                    return _itemSorter.Sort(loadedItems, sortMode).ToList();
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
                    archiveItems = archiveItems.Where(item =>
                        !item.IsFolder ||
                        !ExplorerSearchService.IsHiddenFolderName(item.Name));
                }

                foreach (var item in _itemSorter.Sort(archiveItems))
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
                    if (_hideUnwantedFolders &&
                        entry.IsDirectory &&
                        ExplorerSearchService.IsHiddenFolderName(entry.Name))
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

                if (_itemSorter.Mode != ExplorerItemSorter.SortMode.Name)
                {
                    var sorted = _itemSorter.Sort(_viewModel.ExplorerItems).ToList();
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
                .Where(item => ExplorerSearchService.MatchesPattern(item.Name, query))
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

            if (ShellShortcutResolver.IsShortcut(item.Path))
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

            foreach (var childItem in _itemSorter.Sort(childItems))
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
                foreach (ExplorerItem childItem in _itemSorter.Sort(
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
            } else if (ShellShortcutResolver.IsShortcut(item.Path))
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
            List<ExplorerBreadcrumbSegment> segments;
            try
            {
                segments = _breadcrumbBuilder.Build(
                    CurrentFolderPath,
                    CurrentArchivePath,
                    CurrentArchiveDirectory,
                    _currentArchiveRemotePath,
                    IsViewingRemote);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed building explorer breadcrumb: {ex.Message}");
                segments = new List<ExplorerBreadcrumbSegment>();
            }

            bool hasSegments = segments.Count > 0;
            // 표시 상태를 먼저 정해야 폭이 이미 확보된 경우 ItemsSource 설정 시점에
            // 바로 올바른 폭으로 그려 SizeChanged를 기다리지 않는다.
            _leftSidebar.ExplorerBreadcrumb.Visibility = hasSegments ? Visibility.Visible : Visibility.Collapsed;
            _leftSidebar.ExplorerBreadcrumb.ItemsSource = hasSegments ? segments : null;
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
            Dictionary<string, string>? statuses = IsViewingArchive
                ? null
                : await _gitStatusService.GetStatusesAsync(CurrentFolderPath);
            _leftSidebar.DispatcherQueue.TryEnqueue(() =>
            {
                _gitStatusService.ApplyStatuses(GetVisibleExplorerItems(), statuses, isDark);
            });
        }

        private void OnExplorerSortClick(object sender, RoutedEventArgs e)
        {
            _itemSorter.CycleMode();

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
                var sorted = _itemSorter.Sort(_viewModel.ExplorerItems).ToList();
                _viewModel.ExplorerItems.ReplaceAll(sorted);
            }
        }

        private void UpdateSortButtonVisuals()
        {
            string key;
            string fallback;
            string glyph;

            switch (_itemSorter.Mode)
            {
                case ExplorerItemSorter.SortMode.Name:
                    key = "ExplorerSortName";
                    fallback = "이름순 정렬";
                    glyph = "\uE8CB"; // Standard sort glyph
                    break;
                case ExplorerItemSorter.SortMode.Newest:
                    key = "ExplorerSortNewest";
                    fallback = "수정한 날짜 최신순 정렬";
                    glyph = "\uE74B"; // Down arrow
                    break;
                case ExplorerItemSorter.SortMode.Oldest:
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
            var matchedItems = await Task.Run(() =>
                _searchService.SearchLocal(currentRoot, query, _hideUnwantedFolders));

            if (query == _lastFilterQuery && currentRoot == CurrentFolderPath)
            {
                _leftSidebar.DispatcherQueue.TryEnqueue(() =>
                {
                    _viewModel.ExplorerItems.Clear();
                    bool isDark = _leftSidebar.ActualTheme == ElementTheme.Dark;
                    foreach (var item in _itemSorter.Sort(matchedItems))
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
                _archiveExplorerService.SearchArchiveItems(
                    archivePath,
                    archiveDirectory,
                    query,
                    ExplorerSearchService.MatchesPattern));

            if (_hideUnwantedFolders)
            {
                matchedItems = matchedItems.Where(item =>
                    !item.IsFolder ||
                    !ExplorerSearchService.IsHiddenFolderName(item.Name)).ToList();
            }

            if (query == _lastFilterQuery &&
                archivePath == CurrentArchivePath &&
                archiveDirectory == CurrentArchiveDirectory)
            {
                _leftSidebar.DispatcherQueue.TryEnqueue(() =>
                {
                    _viewModel.ExplorerItems.Clear();
                    bool isDark = _leftSidebar.ActualTheme == ElementTheme.Dark;
                    foreach (var item in _itemSorter.Sort(matchedItems))
                    {
                        item.IsDark = isDark;
                        _viewModel.ExplorerItems.Add(item);
                    }

                    SetExplorerStatusText(FormatExplorerFilterResult(_viewModel.ExplorerItems.Count));
                });
            }
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

        private void HandleLnkFile(string lnkPath)
        {
            string? target = ShellShortcutResolver.ResolveTarget(lnkPath);
            if (string.IsNullOrWhiteSpace(target))
            {
                _ = _loadFileIntoTabAsync(lnkPath);
                return;
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
    }
}
