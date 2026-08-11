using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Services;
using TxtAIEditor.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace TxtAIEditor.Controls
{
    public sealed class ExplorerFileActionsController
    {
        private static readonly System.Text.Encoding Utf8NoBom = new System.Text.UTF8Encoding(false);
        private readonly LeftSidebarPane _leftSidebar;
        private readonly StatusBarPane _statusBar;
        private readonly MainWindowViewModel _viewModel;
        private readonly ArchiveExplorerService _archiveExplorerService;
        private readonly RemoteWorkspaceService _remoteWorkspaceService;
        private readonly TabView _primaryTabView;
        private readonly TabView _secondaryTabView;
        private readonly Func<string> _currentFolderProvider;
        private readonly Func<OpenedTab?> _activeTabProvider;
        private readonly Action<string> _loadDirectoryRoot;
        private readonly Action<string> _refreshTreeFolder;
        private readonly Func<string, Task> _loadFileIntoTabAsync;
        private readonly Func<string, Task> _openFileInExternalViewerAsync;
        private readonly Func<string, Task> _openFileWithDefaultProgramAsync;
        private readonly Func<string, Task<bool>> _insertTextIntoActiveEditorAsync;
        private readonly Action<OpenedTab, TabViewItem> _closeTabAndCleanup;
        private readonly Func<XamlRoot> _xamlRootProvider;
        private readonly Func<ElementTheme> _themeProvider;
        private readonly Func<string, string, string> _getString;
        private readonly Action<string, string> _showError;
        private readonly Func<bool> _isTerminalVisible;
        private readonly Action _suspendTerminal;
        private readonly Action _resumeTerminal;
        private readonly Func<bool> _isArchiveViewProvider;
        private readonly Func<bool> _isRemoteViewProvider;
        private readonly Func<Task> _refreshRemoteExplorerAsync;
        private readonly Action<object> _initializePickerWindow;
        private readonly Func<string>? _homeFolderPathProvider;
        private readonly ImageConversionController _imageConversionController;
        private readonly ConditionalWeakTable<MenuFlyout, object> _localizedFlyouts = new ConditionalWeakTable<MenuFlyout, object>();
        private readonly HashSet<ExplorerItem> _cutItems = new();

        private System.Threading.CancellationTokenSource? _archiveCts;
        private string _treeDropTargetFolderPath = string.Empty;

        public ExplorerFileActionsController(
            LeftSidebarPane leftSidebar,
            StatusBarPane statusBar,
            MainWindowViewModel viewModel,
            ArchiveExplorerService archiveExplorerService,
            RemoteWorkspaceService remoteWorkspaceService,
            TabView primaryTabView,
            TabView secondaryTabView,
            Func<string> currentFolderProvider,
            Func<OpenedTab?> activeTabProvider,
            Action<string> loadDirectoryRoot,
            Action<string> refreshTreeFolder,
            Func<string, Task> loadFileIntoTabAsync,
            Func<string, Task> openFileInExternalViewerAsync,
            Func<string, Task> openFileWithDefaultProgramAsync,
            Func<string, Task<bool>> insertTextIntoActiveEditorAsync,
            Action<OpenedTab, TabViewItem> closeTabAndCleanup,
            Func<XamlRoot> xamlRootProvider,
            Func<ElementTheme> themeProvider,
            Func<string, string, string> getString,
            Action<string, string> showError,
            Func<bool> isTerminalVisible,
            Action suspendTerminal,
            Action resumeTerminal,
            Func<bool> isArchiveViewProvider,
            Func<bool> isRemoteViewProvider,
            Func<Task> refreshRemoteExplorerAsync,
            Action<object> initializePickerWindow,
            Func<string>? homeFolderPathProvider = null)
        {
            _leftSidebar = leftSidebar;
            _statusBar = statusBar;
            _viewModel = viewModel;
            _archiveExplorerService = archiveExplorerService;
            _remoteWorkspaceService = remoteWorkspaceService;
            _primaryTabView = primaryTabView;
            _secondaryTabView = secondaryTabView;
            _currentFolderProvider = currentFolderProvider;
            _activeTabProvider = activeTabProvider;
            _loadDirectoryRoot = loadDirectoryRoot;
            _refreshTreeFolder = refreshTreeFolder;
            _loadFileIntoTabAsync = loadFileIntoTabAsync;
            _openFileInExternalViewerAsync = openFileInExternalViewerAsync;
            _openFileWithDefaultProgramAsync = openFileWithDefaultProgramAsync;
            _insertTextIntoActiveEditorAsync = insertTextIntoActiveEditorAsync;
            _closeTabAndCleanup = closeTabAndCleanup;
            _xamlRootProvider = xamlRootProvider;
            _themeProvider = themeProvider;
            _getString = getString;
            _showError = showError;
            _isTerminalVisible = isTerminalVisible;
            _suspendTerminal = suspendTerminal;
            _resumeTerminal = resumeTerminal;
            _isArchiveViewProvider = isArchiveViewProvider;
            _isRemoteViewProvider = isRemoteViewProvider;
            _refreshRemoteExplorerAsync = refreshRemoteExplorerAsync;
            _initializePickerWindow = initializePickerWindow;
            _homeFolderPathProvider = homeFolderPathProvider;
            _imageConversionController = new ImageConversionController(_getString, _themeProvider);

            WireEvents();
        }

        private void WireEvents()
        {
            _leftSidebar.FileListViewItemRightTapped += OnFileListViewItemRightTapped;
            _leftSidebar.CutClick += OnCutClick;
            _leftSidebar.CopyItemsClick += OnCopyItemsClick;
            _leftSidebar.PasteClick += OnPasteClick;
            _leftSidebar.CreateFolderClick += OnCreateFolderClick;
            _leftSidebar.CreateFileClick += OnCreateFileClick;
            _leftSidebar.CreateNotebookClick += OnCreateNotebookClick;
            _leftSidebar.InsertMarkdownImageClick += OnInsertMarkdownImageClick;
            _leftSidebar.OpenExternalViewerClick += OnOpenExternalViewerClick;
            _leftSidebar.OpenWithDefaultProgramClick += OnOpenWithDefaultProgramClick;
            _leftSidebar.ExtractArchiveToFolderClick += OnExtractArchiveToFolderClick;
            _leftSidebar.CompressFolderToZipClick += OnCompressFolderToZipClick;
            _leftSidebar.CompressFolderToSevenZipClick += OnCompressFolderToSevenZipClick;
            _leftSidebar.ImageConversionClick += OnImageConversionClick;
            _leftSidebar.DownloadRemoteItemClick += OnDownloadRemoteItemClick;
            _leftSidebar.UploadRemoteItemClick += OnUploadRemoteItemClick;
            _leftSidebar.CopyFileNameClick += OnCopyFileNameClick;
            _leftSidebar.CopyFilePathClick += OnCopyFilePathClick;
            _leftSidebar.CopyFolderPathClick += OnCopyFolderPathClick;
            _leftSidebar.RenameClick += OnRenameClick;
            _leftSidebar.DeleteClick += OnDeleteClick;
            _leftSidebar.FileListViewDragOver += OnFileListViewDragOver;
            _leftSidebar.FileListViewDrop += OnFileListViewDrop;
            _leftSidebar.FileListViewItemDragOver += OnFileListViewItemDragOver;
            _leftSidebar.FileListViewItemDrop += OnFileListViewItemDrop;
            _leftSidebar.ExplorerTreeDragOver += OnExplorerTreeDragOver;
            _leftSidebar.ExplorerTreeDrop += OnExplorerTreeDrop;
            _leftSidebar.ExplorerPage.AddHandler(
                UIElement.KeyDownEvent,
                new KeyEventHandler(OnExplorerKeyDown),
                handledEventsToo: true);
        }

        private void OnFileListViewItemRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            ExplorerItem? contextItem = null;
            if (sender is FrameworkElement element)
            {
                contextItem = GetTreeExplorerItem(element.DataContext);
                if (element.DataContext is ExplorerItem listItem)
                {
                    var selectedItems = _leftSidebar.FileList.SelectedItems
                        .OfType<ExplorerItem>()
                        .ToList();
                    if (!selectedItems.Contains(listItem))
                    {
                        _leftSidebar.FileList.SelectedItems.Clear();
                        _leftSidebar.FileList.SelectedItems.Add(listItem);
                    }
                }
                else if (contextItem != null)
                {
                    var selectedItems = GetTreeSelectedExplorerItems();
                    if (!selectedItems.Any(selected => ReferenceEquals(selected, contextItem)))
                    {
                        TreeViewNode? node = FindTreeNodeFromElement(element);
                        if (node != null)
                        {
                            _leftSidebar.ExplorerTree.SelectedNodes.Clear();
                            _leftSidebar.ExplorerTree.SelectedNodes.Add(node);
                        }
                        else
                        {
                            _leftSidebar.ExplorerTree.SelectedItem = contextItem;
                        }
                    }
                }

                if (element.ContextFlyout is MenuFlyout flyout && flyout.Items.Count >= 17)
                {
                    LocalizeContextFlyout(flyout);
                    ConfigureContextFlyout(
                        flyout,
                        contextItem ?? _leftSidebar.FileList.SelectedItem as ExplorerItem,
                        GetSelectedExplorerItems(contextItem));
                    CursorResetHelper.AttachToFlyout(flyout, element);
                    CursorResetHelper.ResetToArrow(element);
                }
            }

            e.Handled = true;
        }

        private async void OnCreateFolderClick(object sender, RoutedEventArgs e)
        {
            if (_isArchiveViewProvider())
            {
                _showError(
                    _getString("CreateFolderErrorTitle", "새 폴더 만들기 오류"),
                    _getString("ArchiveExplorerReadOnlyMessage", "압축 파일 내부는 읽기 전용입니다."));
                return;
            }

            string currentFolder = _currentFolderProvider();
            if (!_isRemoteViewProvider() &&
                (string.IsNullOrWhiteSpace(currentFolder) || !Directory.Exists(currentFolder)))
            {
                _showError(
                    _getString("CreateFolderErrorTitle", "새 폴더 만들기 오류"),
                    _getString("CreateFolderNoFolderSelected", "먼저 탐색기에서 폴더를 선택하십시오."));
                return;
            }

            var nameInput = new TextBox
            {
                PlaceholderText = _getString("CreateFolderPlaceholder", "폴더 이름 입력..."),
                MinWidth = 260,
                MaxLength = 255
            };

            var dialog = new ContentDialog
            {
                Title = _getString("CreateFolderDialogTitle", "새 폴더"),
                Content = nameInput,
                PrimaryButtonText = _getString("CreateFolderDialogCreate", "만들기"),
                CloseButtonText = _getString("CreateFolderDialogCancel", "취소"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = _xamlRootProvider(),
                RequestedTheme = _themeProvider()
            };

            if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary)
            {
                return;
            }

            string folderName = nameInput.Text.Trim();
            if (string.IsNullOrWhiteSpace(folderName))
            {
                _showError(
                    _getString("CreateFolderErrorTitle", "새 폴더 만들기 오류"),
                    _getString("CreateFolderEmptyName", "폴더 이름을 입력하십시오."));
                return;
            }

            if (folderName == "." ||
                folderName == ".." ||
                folderName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                _showError(
                    _getString("CreateFolderErrorTitle", "새 폴더 만들기 오류"),
                    _getString("CreateFolderInvalidName", "폴더 이름에 사용할 수 없는 문자가 포함되어 있습니다."));
                return;
            }

            if (_isRemoteViewProvider())
            {
                try
                {
                    await _remoteWorkspaceService.CreateDirectoryAsync(
                        _remoteWorkspaceService.ActiveDirectoryVirtualPath,
                        folderName);
                    await _refreshRemoteExplorerAsync();
                }
                catch (Exception ex)
                {
                    _showError(
                        _getString("CreateFolderErrorTitle", "새 폴더 만들기 오류"),
                        ex.Message);
                }
                return;
            }

            string newFolderPath = Path.Combine(currentFolder, folderName);
            if (Directory.Exists(newFolderPath) || File.Exists(newFolderPath))
            {
                _showError(
                    _getString("CreateFolderErrorTitle", "새 폴더 만들기 오류"),
                    string.Format(
                        _getString("CreateFolderAlreadyExists", "'{0}'이(가) 이미 존재합니다."),
                        folderName));
                return;
            }

            try
            {
                Directory.CreateDirectory(newFolderPath);
                _loadDirectoryRoot(currentFolder);

                var createdItem = _viewModel.ExplorerItems
                    .FirstOrDefault(item => string.Equals(item.Path, newFolderPath, StringComparison.OrdinalIgnoreCase));
                if (createdItem != null)
                {
                    _leftSidebar.FileList.SelectedItem = createdItem;
                    _leftSidebar.FileList.ScrollIntoView(createdItem);
                }
            }
            catch (Exception ex)
            {
                _showError(
                    _getString("CreateFolderErrorTitle", "새 폴더 만들기 오류"),
                    ex.Message);
            }
        }

        private async void OnCreateFileClick(object sender, RoutedEventArgs e)
        {
            if (_isArchiveViewProvider())
            {
                _showError(
                    _getString("CreateFileErrorTitle", "새 파일 만들기 오류"),
                    _getString("ArchiveExplorerReadOnlyMessage", "압축 파일 내부는 읽기 전용입니다."));
                return;
            }

            string currentFolder = _currentFolderProvider();
            if (!_isRemoteViewProvider() &&
                (string.IsNullOrWhiteSpace(currentFolder) || !Directory.Exists(currentFolder)))
            {
                _showError(
                    _getString("CreateFileErrorTitle", "새 파일 만들기 오류"),
                    _getString("CreateFolderNoFolderSelected", "먼저 탐색기에서 폴더를 선택하십시오."));
                return;
            }

            var nameInput = new TextBox
            {
                PlaceholderText = _getString("CreateFilePlaceholder", "파일 이름 입력..."),
                MinWidth = 260,
                MaxLength = 255
            };
            var dialog = new ContentDialog
            {
                Title = _getString("CreateFileDialogTitle", "새 파일"),
                Content = nameInput,
                PrimaryButtonText = _getString("CreateFileDialogCreate", "만들기"),
                CloseButtonText = _getString("CreateFolderDialogCancel", "취소"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = _xamlRootProvider(),
                RequestedTheme = _themeProvider()
            };

            if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary)
            {
                return;
            }

            string fileName = nameInput.Text.Trim();
            if (string.IsNullOrWhiteSpace(fileName))
            {
                _showError(
                    _getString("CreateFileErrorTitle", "새 파일 만들기 오류"),
                    _getString("CreateFileEmptyName", "파일 이름을 입력하십시오."));
                return;
            }

            if (fileName is "." or ".." ||
                fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                _showError(
                    _getString("CreateFileErrorTitle", "새 파일 만들기 오류"),
                    _getString("CreateFileInvalidName", "파일 이름에 사용할 수 없는 문자가 포함되어 있습니다."));
                return;
            }

            if (_isRemoteViewProvider())
            {
                try
                {
                    string virtualPath = await _remoteWorkspaceService.CreateFileAsync(
                        _remoteWorkspaceService.ActiveDirectoryVirtualPath,
                        fileName);
                    await _refreshRemoteExplorerAsync();
                    string localPath = await _remoteWorkspaceService.DownloadVirtualFileAsync(virtualPath);
                    await _loadFileIntoTabAsync(localPath);
                }
                catch (Exception ex)
                {
                    _showError(
                        _getString("CreateFileErrorTitle", "새 파일 만들기 오류"),
                        ex.Message);
                }
                return;
            }

            string newFilePath = Path.Combine(currentFolder, fileName);
            if (File.Exists(newFilePath) || Directory.Exists(newFilePath))
            {
                _showError(
                    _getString("CreateFileErrorTitle", "새 파일 만들기 오류"),
                    string.Format(
                        _getString("CreateFolderAlreadyExists", "'{0}'이(가) 이미 존재합니다."),
                        fileName));
                return;
            }

            try
            {
                using (File.Create(newFilePath))
                {
                }
                _loadDirectoryRoot(currentFolder);
                await _loadFileIntoTabAsync(newFilePath);
            }
            catch (Exception ex)
            {
                _showError(
                    _getString("CreateFileErrorTitle", "새 파일 만들기 오류"),
                    ex.Message);
            }
        }

        private async void OnCreateNotebookClick(object sender, RoutedEventArgs e)
        {
            if (_isArchiveViewProvider())
            {
                _showError(
                    _getString("CreateNotebookErrorTitle", "새 노트북 만들기 오류"),
                    _getString("ArchiveExplorerReadOnlyMessage", "압축 파일 내부는 읽기 전용입니다."));
                return;
            }

            string currentFolder = _currentFolderProvider();
            if (!_isRemoteViewProvider() &&
                (string.IsNullOrWhiteSpace(currentFolder) || !Directory.Exists(currentFolder)))
            {
                _showError(
                    _getString("CreateNotebookErrorTitle", "새 노트북 만들기 오류"),
                    _getString("CreateFolderNoFolderSelected", "먼저 탐색기에서 폴더를 선택하십시오."));
                return;
            }

            var nameInput = new TextBox
            {
                PlaceholderText = _getString("CreateNotebookPlaceholder", "노트북 이름 입력..."),
                MinWidth = 260,
                MaxLength = 255
            };
            var dialog = new ContentDialog
            {
                Title = _getString("CreateNotebookDialogTitle", "새 노트북"),
                Content = nameInput,
                PrimaryButtonText = _getString("CreateFileDialogCreate", "만들기"),
                CloseButtonText = _getString("CreateFolderDialogCancel", "취소"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = _xamlRootProvider(),
                RequestedTheme = _themeProvider()
            };

            if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary)
            {
                return;
            }

            string fileName = nameInput.Text.Trim();
            if (string.IsNullOrWhiteSpace(fileName))
            {
                _showError(
                    _getString("CreateNotebookErrorTitle", "새 노트북 만들기 오류"),
                    _getString("CreateNotebookEmptyName", "노트북 이름을 입력하십시오."));
                return;
            }

            if (!fileName.EndsWith(".ipynb", StringComparison.OrdinalIgnoreCase))
            {
                fileName += ".ipynb";
            }

            if (fileName is "." or ".." ||
                fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                _showError(
                    _getString("CreateNotebookErrorTitle", "새 노트북 만들기 오류"),
                    _getString("CreateNotebookInvalidName", "노트북 이름에 사용할 수 없는 문자가 포함되어 있습니다."));
                return;
            }

            string initialContent = "{\n \"cells\": [],\n \"metadata\": {},\n \"nbformat\": 4,\n \"nbformat_minor\": 5\n}";

            if (_isRemoteViewProvider())
            {
                try
                {
                    string virtualPath = await _remoteWorkspaceService.CreateFileAsync(
                        _remoteWorkspaceService.ActiveDirectoryVirtualPath,
                        fileName);
                    await _refreshRemoteExplorerAsync();
                    string localPath = await _remoteWorkspaceService.DownloadVirtualFileAsync(virtualPath);
                    await File.WriteAllTextAsync(localPath, initialContent, Utf8NoBom);
                    await _remoteWorkspaceService.UploadLocalFileAsync(localPath, virtualPath);
                    await _loadFileIntoTabAsync(localPath);
                }
                catch (Exception ex)
                {
                    _showError(
                        _getString("CreateNotebookErrorTitle", "새 노트북 만들기 오류"),
                        ex.Message);
                }
                return;
            }

            string newFilePath = Path.Combine(currentFolder, fileName);
            if (File.Exists(newFilePath) || Directory.Exists(newFilePath))
            {
                _showError(
                    _getString("CreateNotebookErrorTitle", "새 노트북 만들기 오류"),
                    string.Format(
                        _getString("CreateFolderAlreadyExists", "'{0}'이(가) 이미 존재합니다."),
                        fileName));
                return;
            }

            try
            {
                await File.WriteAllTextAsync(newFilePath, initialContent, Utf8NoBom);
                _loadDirectoryRoot(currentFolder);
                await _loadFileIntoTabAsync(newFilePath);
            }
            catch (Exception ex)
            {
                _showError(
                    _getString("CreateNotebookErrorTitle", "새 노트북 만들기 오류"),
                    ex.Message);
            }
        }

        private void LocalizeContextFlyout(MenuFlyout flyout)
        {
            if (_localizedFlyouts.TryGetValue(flyout, out _))
            {
                return;
            }

            _localizedFlyouts.Add(flyout, null!);
            SetMenuText(flyout, 0, "ExplorerCut", "잘라내기");
            SetMenuText(flyout, 1, "ExplorerCopy", "복사");
            SetMenuText(flyout, 2, "ExplorerPaste", "붙여넣기");
            SetMenuText(flyout, 4, "ExplorerAddToFavorites", "즐겨찾기에 추가");
            SetMenuText(flyout, 5, "ExplorerAddFolderToFavorites", "폴더를 즐겨찾기에 추가");
            SetMenuText(flyout, 6, "ExplorerInsertMarkdownImage", "마크다운 삽입");
            SetMenuText(flyout, 7, "OpenExternalViewerTooltip", "외부 뷰어로 열기");
            SetMenuText(flyout, 8, "OpenWithDefaultProgramTooltip", "기본 프로그램으로 열기");
            SetMenuText(flyout, 10, "ExplorerExtractArchiveToFolder", "폴더에 풀기");
            SetMenuText(flyout, 11, "ExplorerCompressFolderToZip", "ZIP으로 압축하기");
            SetMenuText(flyout, 12, "ExplorerCompressFolderToSevenZip", "7z로 압축하기");
            SetMenuText(flyout, 14, "ExplorerConvertImage", "이미지 변환");
            SetMenuText(flyout, 16, "ExplorerDownload", "다운로드");
            SetMenuText(flyout, 17, "ExplorerUpload", "업로드");
            SetMenuText(flyout, 19, "ExplorerCopyFileName", "파일이름 복사");
            SetMenuText(flyout, 20, "ExplorerCopyFilePath", "경로 복사");
            SetMenuText(flyout, 21, "ExplorerCopyFolderPath", "폴더 경로 복사");
            SetMenuText(flyout, 23, "ExplorerRename", "이름 바꾸기");
            SetMenuText(flyout, 24, "ExplorerDelete", "삭제");
        }

        private void ConfigureContextFlyout(
            MenuFlyout flyout,
            ExplorerItem? item,
            IReadOnlyList<ExplorerItem> selectedItems)
        {
            bool hasSingleItem = selectedItems.Count == 1;
            bool isArchiveEntry = item?.IsArchiveEntry == true;
            bool canUseLocalStorageItems = selectedItems.Count > 0 && selectedItems.All(CanUseLocalStorageItem);
            bool canPaste = CanPasteStorageItems() && CanPasteIntoLocalDirectory(item);

            SetMenuVisibility(flyout, 0, canUseLocalStorageItems);
            SetMenuVisibility(flyout, 1, canUseLocalStorageItems);
            SetMenuVisibility(flyout, 2, CanPasteIntoLocalDirectory(item));
            SetMenuEnabled(flyout, 2, canPaste);
            SetMenuVisibility(flyout, 3, canUseLocalStorageItems || CanPasteIntoLocalDirectory(item));

            if (flyout.Items.Count > 4 && flyout.Items[4] is MenuFlyoutItem addFileFavoriteItem)
            {
                addFileFavoriteItem.Visibility = hasSingleItem && !isArchiveEntry ? Visibility.Visible : Visibility.Collapsed;
            }

            if (flyout.Items.Count > 5 && flyout.Items[5] is MenuFlyoutItem addFolderFavoriteItem)
            {
                addFolderFavoriteItem.Visibility = hasSingleItem && !isArchiveEntry ? Visibility.Visible : Visibility.Collapsed;
            }

            if (flyout.Items.Count > 6 && flyout.Items[6] is MenuFlyoutItem markdownItem)
            {
                markdownItem.Visibility = hasSingleItem && item != null && !item.IsRemote && !isArchiveEntry && !item.IsFolder && IsSupportedImageFile(item.Path)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            bool canOpenFile = hasSingleItem && CanOpenExplorerFile(item);
            if (flyout.Items.Count > 7 && flyout.Items[7] is MenuFlyoutItem externalViewerItem)
            {
                externalViewerItem.Visibility = canOpenFile ? Visibility.Visible : Visibility.Collapsed;
            }

            if (flyout.Items.Count > 8 && flyout.Items[8] is MenuFlyoutItem defaultProgramItem)
            {
                defaultProgramItem.Visibility = canOpenFile ? Visibility.Visible : Visibility.Collapsed;
            }

            bool canExtractArchive = hasSingleItem && IsSupportedArchiveFile(item);
            if (flyout.Items.Count > 10 && flyout.Items[10] is MenuFlyoutItem extractArchiveItem)
            {
                extractArchiveItem.Visibility = canExtractArchive ? Visibility.Visible : Visibility.Collapsed;
                if (canExtractArchive && item != null)
                {
                    string folderName = GetArchiveExtractFolderName(item.Path);
                    string format = _getString("ExplorerExtractArchiveToFolderFormat", "{0} 폴더에 풀기");
                    extractArchiveItem.Text = string.Format(format, folderName);
                }
            }

            bool canCompress = selectedItems.Count > 0 && selectedItems.All(CanCompressItem);
            SetMenuVisibility(flyout, 9, canExtractArchive || canCompress);
            if (flyout.Items.Count > 11 && flyout.Items[11] is MenuFlyoutItem compressToZipItem)
            {
                compressToZipItem.Visibility = canCompress ? Visibility.Visible : Visibility.Collapsed;
            }

            if (flyout.Items.Count > 12 && flyout.Items[12] is MenuFlyoutItem compressToSevenZipItem)
            {
                compressToSevenZipItem.Visibility = canCompress ? Visibility.Visible : Visibility.Collapsed;
            }

            bool canConvertImages = selectedItems.Count > 0 && selectedItems.All(CanConvertImageItem);
            SetMenuVisibility(flyout, 13, canExtractArchive || canCompress);
            SetMenuVisibility(flyout, 14, canConvertImages);

            bool canDelete = selectedItems.Count > 0 && selectedItems.All(CanDeleteItem);

            bool canTransferRemote = hasSingleItem && IsRemoteExplorerItem(item);
            SetMenuVisibility(flyout, 15, canConvertImages || canTransferRemote);
            if (flyout.Items.Count > 16 && flyout.Items[16] is MenuFlyoutItem downloadRemoteItem)
            {
                downloadRemoteItem.Visibility = canTransferRemote ? Visibility.Visible : Visibility.Collapsed;
            }

            if (flyout.Items.Count > 17 && flyout.Items[17] is MenuFlyoutItem uploadRemoteItem)
            {
                uploadRemoteItem.Visibility = canTransferRemote ? Visibility.Visible : Visibility.Collapsed;
            }

            SetMenuVisibility(flyout, 18, canTransferRemote);

            if (flyout.Items.Count > 19 && flyout.Items[19] is MenuFlyoutItem copyFileNameItem)
            {
                copyFileNameItem.Visibility = hasSingleItem ? Visibility.Visible : Visibility.Collapsed;
            }

            if (flyout.Items.Count > 20 && flyout.Items[20] is MenuFlyoutItem copyFilePathItem)
            {
                copyFilePathItem.Visibility = hasSingleItem ? Visibility.Visible : Visibility.Collapsed;
            }

            if (flyout.Items.Count > 21 && flyout.Items[21] is MenuFlyoutItem copyFolderPathItem)
            {
                copyFolderPathItem.Visibility = hasSingleItem && !isArchiveEntry ? Visibility.Visible : Visibility.Collapsed;
            }

            if (flyout.Items.Count > 23 && flyout.Items[23] is MenuFlyoutItem renameItem)
            {
                renameItem.Visibility = hasSingleItem && !isArchiveEntry ? Visibility.Visible : Visibility.Collapsed;
            }

            if (flyout.Items.Count > 24 && flyout.Items[24] is MenuFlyoutItem deleteItem)
            {
                deleteItem.Visibility = canDelete ? Visibility.Visible : Visibility.Collapsed;
            }

            SetMenuVisibility(flyout, 22, hasSingleItem || canDelete);
        }

        private void SetMenuText(MenuFlyout flyout, int index, string key, string fallback)
        {
            if (index >= 0 && index < flyout.Items.Count && flyout.Items[index] is MenuFlyoutItem item)
            {
                item.Text = _getString(key, fallback);
            }
        }

        private static void SetMenuVisibility(MenuFlyout flyout, int index, bool isVisible)
        {
            if (index >= 0 && index < flyout.Items.Count && flyout.Items[index] is FrameworkElement element)
            {
                element.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private static void SetMenuEnabled(MenuFlyout flyout, int index, bool isEnabled)
        {
            if (index >= 0 && index < flyout.Items.Count && flyout.Items[index] is MenuFlyoutItem item)
            {
                item.IsEnabled = isEnabled;
            }
        }

        private void OnCutClick(object sender, RoutedEventArgs e)
        {
            _ = SetClipboardStorageItemsAsync(sender, DataPackageOperation.Move);
        }

        private void OnCopyItemsClick(object sender, RoutedEventArgs e)
        {
            _ = SetClipboardStorageItemsAsync(sender, DataPackageOperation.Copy);
        }

        private void OnExplorerKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != Windows.System.VirtualKey.Escape || _cutItems.Count == 0)
            {
                return;
            }

            try
            {
                DataPackageView clipboard = Clipboard.GetContent();
                if (clipboard.Contains(StandardDataFormats.StorageItems) &&
                    (clipboard.RequestedOperation & DataPackageOperation.Move) == DataPackageOperation.Move)
                {
                    Clipboard.Clear();
                }
            }
            catch
            {
                // The visual state is still cleared if the clipboard is unavailable.
            }

            ClearCutItems();
            e.Handled = true;
        }

        private async void OnPasteClick(object sender, RoutedEventArgs e)
        {
            ExplorerItem? contextItem = GetExplorerItem(sender);
            string targetDirectory = GetPasteTargetDirectory(contextItem);
            if (!Directory.Exists(targetDirectory))
            {
                return;
            }

            try
            {
                DataPackageView clipboard = Clipboard.GetContent();
                if (!clipboard.Contains(StandardDataFormats.StorageItems))
                {
                    return;
                }

                IReadOnlyList<IStorageItem> storageItems = await clipboard.GetStorageItemsAsync();
                bool move = (clipboard.RequestedOperation & DataPackageOperation.Move) == DataPackageOperation.Move;
                int transferredCount = 0;
                foreach (IStorageItem storageItem in storageItems)
                {
                    if (string.IsNullOrWhiteSpace(storageItem.Path))
                    {
                        continue;
                    }

                    if (await TransferStorageItemAsync(storageItem.Path, targetDirectory, move))
                    {
                        transferredCount++;
                    }
                }

                if (move && transferredCount > 0)
                {
                    Clipboard.Clear();
                    ClearCutItems();
                }

                _loadDirectoryRoot(_currentFolderProvider());
            }
            catch (Exception ex)
            {
                _showError(
                    _getString("ExplorerPasteErrorTitle", "붙여넣기 오류"),
                    ex.Message);
            }
        }

        private async Task SetClipboardStorageItemsAsync(object sender, DataPackageOperation operation)
        {
            IReadOnlyList<ExplorerItem> selectedItems = GetSelectedExplorerItems(sender);
            if (selectedItems.Count == 0 || selectedItems.Any(item => !CanUseLocalStorageItem(item)))
            {
                return;
            }

            try
            {
                var storageItems = new List<IStorageItem>(selectedItems.Count);
                foreach (ExplorerItem item in selectedItems)
                {
                    IStorageItem storageItem = item.IsFolder
                        ? await StorageFolder.GetFolderFromPathAsync(item.Path)
                        : await StorageFile.GetFileFromPathAsync(item.Path);
                    storageItems.Add(storageItem);
                }

                var dataPackage = new DataPackage
                {
                    RequestedOperation = operation
                };
                dataPackage.SetStorageItems(storageItems);
                Clipboard.SetContent(dataPackage);
                if (operation == DataPackageOperation.Move)
                {
                    SetCutItems(selectedItems);
                }
                else
                {
                    ClearCutItems();
                }
            }
            catch (Exception ex)
            {
                _showError(
                    _getString("ExplorerClipboardErrorTitle", "클립보드 오류"),
                    ex.Message);
            }
        }

        private void SetCutItems(IEnumerable<ExplorerItem> items)
        {
            ClearCutItems();
            foreach (ExplorerItem item in items)
            {
                item.IsCut = true;
                _cutItems.Add(item);
            }
        }

        private void ClearCutItems()
        {
            foreach (ExplorerItem item in _cutItems)
            {
                item.IsCut = false;
            }

            _cutItems.Clear();
        }

        private void OnImageConversionClick(object sender, RoutedEventArgs e)
        {
            IReadOnlyList<string> imagePaths = GetSelectedExplorerItems(sender)
                .Where(CanConvertImageItem)
                .Select(item => item.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (imagePaths.Count == 0)
            {
                return;
            }

            FrameworkElement? sourceElement = sender as FrameworkElement;
            _ = _imageConversionController.ShowAsync(
                imagePaths,
                sourceElement?.XamlRoot ?? _leftSidebar.XamlRoot,
                sourceElement?.ActualTheme ?? _leftSidebar.ActualTheme);
        }

        private IReadOnlyList<ExplorerItem> GetSelectedExplorerItems(object? source)
        {
            ExplorerItem? contextItem = source as ExplorerItem;
            if (contextItem == null && source != null)
            {
                contextItem = GetExplorerItem(source);
            }

            var selectedItems = _leftSidebar.FileList.SelectedItems
                .OfType<ExplorerItem>()
                .ToList();
            if (contextItem != null && selectedItems.Any(item => ReferenceEquals(item, contextItem)))
            {
                return selectedItems;
            }

            var selectedTreeItems = GetTreeSelectedExplorerItems();
            if (contextItem != null && selectedTreeItems.Any(item => ReferenceEquals(item, contextItem)))
            {
                return selectedTreeItems;
            }

            return contextItem != null
                ? new[] { contextItem }
                : selectedTreeItems.Count > 0 ? selectedTreeItems : selectedItems;
        }

        private IReadOnlyList<ExplorerItem> GetTreeSelectedExplorerItems()
        {
            return _leftSidebar.ExplorerTree.SelectedItems
                .Select(GetTreeExplorerItem)
                .Where(item => item != null)
                .Cast<ExplorerItem>()
                .ToList();
        }

        private TreeViewNode? FindTreeNodeFromElement(DependencyObject source)
        {
            DependencyObject? current = source;
            while (current != null)
            {
                if (current is TreeViewItem treeViewItem)
                {
                    return _leftSidebar.ExplorerTree.NodeFromContainer(treeViewItem);
                }

                current = VisualTreeHelper.GetParent(current);
            }

            return null;
        }

        private static bool CanUseLocalStorageItem(ExplorerItem item)
        {
            return !item.IsRemote &&
                   !item.IsArchiveEntry &&
                   !string.IsNullOrWhiteSpace(item.Path) &&
                   (item.IsFolder ? Directory.Exists(item.Path) : File.Exists(item.Path));
        }

        private static bool IsRemoteExplorerItem(ExplorerItem? item)
        {
            return item != null && item.IsRemote && RemotePath.IsRemote(item.Path);
        }

        private static bool CanDeleteItem(ExplorerItem item)
        {
            return !item.IsArchiveEntry &&
                   !string.IsNullOrWhiteSpace(item.Path) &&
                   (item.IsRemote || (item.IsFolder ? Directory.Exists(item.Path) : File.Exists(item.Path)));
        }

        private static bool CanCompressItem(ExplorerItem item)
        {
            return CanUseLocalStorageItem(item);
        }

        private static bool CanConvertImageItem(ExplorerItem item)
        {
            return !item.IsRemote &&
                   !item.IsArchiveEntry &&
                   !item.IsFolder &&
                   !string.IsNullOrWhiteSpace(item.Path) &&
                   File.Exists(item.Path) &&
                   IsSupportedImageFile(item.Path);
        }

        private bool CanPasteIntoLocalDirectory(ExplorerItem? contextItem)
        {
            return !_isArchiveViewProvider() &&
                   !_isRemoteViewProvider() &&
                   (contextItem == null || (!contextItem.IsRemote && !contextItem.IsArchiveEntry)) &&
                   Directory.Exists(GetPasteTargetDirectory(contextItem));
        }

        private static bool CanPasteStorageItems()
        {
            try
            {
                return Clipboard.GetContent().Contains(StandardDataFormats.StorageItems);
            }
            catch
            {
                return false;
            }
        }

        private string GetPasteTargetDirectory(ExplorerItem? contextItem)
        {
            if (contextItem != null && contextItem.IsFolder && !contextItem.IsArchiveEntry && !contextItem.IsRemote)
            {
                return contextItem.Path;
            }

            if (contextItem != null && !contextItem.IsFolder && !contextItem.IsArchiveEntry && !contextItem.IsRemote)
            {
                return Path.GetDirectoryName(contextItem.Path) ?? string.Empty;
            }

            return _currentFolderProvider();
        }

        private async void OnInsertMarkdownImageClick(object sender, RoutedEventArgs e)
        {
            var item = GetExplorerItem(sender);
            if (item == null || item.IsFolder || !IsSupportedImageFile(item.Path))
            {
                return;
            }

            try
            {
                string markdown = CreateMarkdownImageText(item);
                bool inserted = await _insertTextIntoActiveEditorAsync(markdown);
                if (!inserted)
                {
                    _showError(
                        _getString("ExplorerInsertMarkdownImageErrorTitle", "마크다운 삽입 실패"),
                        _getString("ExplorerInsertMarkdownImageNoEditor", "마크다운을 삽입할 텍스트 편집기 탭을 선택해 주세요."));
                }
            }
            catch (Exception ex)
            {
                _showError(
                    _getString("ExplorerInsertMarkdownImageErrorTitle", "마크다운 삽입 실패"),
                    ex.Message);
            }
        }

        private async void OnOpenExternalViewerClick(object sender, RoutedEventArgs e)
        {
            var item = GetExplorerItem(sender);
            if (!CanOpenExplorerFile(item))
            {
                return;
            }

            try
            {
                string path = item!.IsRemote
                    ? await _remoteWorkspaceService.DownloadVirtualFileAsync(item.Path)
                    : item.Path;
                await _openFileInExternalViewerAsync(path);
            }
            catch (Exception ex)
            {
                _showError(_getString("RemoteOperationFailedTitle", "리모트 작업 실패"), ex.Message);
            }
        }

        private async void OnOpenWithDefaultProgramClick(object sender, RoutedEventArgs e)
        {
            var item = GetExplorerItem(sender);
            if (!CanOpenExplorerFile(item))
            {
                return;
            }

            try
            {
                string path = item!.IsRemote
                    ? await _remoteWorkspaceService.DownloadVirtualFileAsync(item.Path)
                    : item.Path;
                await _openFileWithDefaultProgramAsync(path);
            }
            catch (Exception ex)
            {
                _showError(_getString("RemoteOperationFailedTitle", "리모트 작업 실패"), ex.Message);
            }
        }

        private async void OnExtractArchiveToFolderClick(object sender, RoutedEventArgs e)
        {
            var item = GetExplorerItem(sender);
            if (!IsSupportedArchiveFile(item))
            {
                return;
            }

            string archivePath = item!.Path;
            string parentDirectory = Path.GetDirectoryName(archivePath) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(parentDirectory) || !Directory.Exists(parentDirectory))
            {
                return;
            }

            string targetDirectory = Path.Combine(parentDirectory, GetArchiveExtractFolderName(archivePath));
            _archiveCts = new System.Threading.CancellationTokenSource();
            var token = _archiveCts.Token;
            string archiveName = Path.GetFileName(archivePath);
            string statusTextFormat = _getString("ArchiveProgressExtracting", "[{0}] 압축 푸는 중...");
            string statusText = string.Format(statusTextFormat, archiveName);
            try
            {
                if (File.Exists(targetDirectory))
                {
                    _showError(
                        _getString("ArchiveExtractFailedTitle", "압축 풀기 실패"),
                        string.Format(
                            _getString("ArchiveExtractTargetFileExistsFormat", "'{0}' 이름의 파일이 이미 있어 압축을 풀 수 없습니다."),
                            Path.GetFileName(targetDirectory)));
                    return;
                }

                bool overwrite = false;
                if (Directory.Exists(targetDirectory) && Directory.EnumerateFileSystemEntries(targetDirectory).Any())
                {
                    var confirmDialog = new ContentDialog
                    {
                        Title = _getString("ArchiveExtractOverwriteTitle", "압축 풀기 확인"),
                        Content = string.Format(
                            _getString("ArchiveExtractOverwriteMessageFormat", "'{0}' 폴더가 이미 있습니다. 기존 파일을 덮어쓰며 압축을 풀까요?"),
                            Path.GetFileName(targetDirectory)),
                        PrimaryButtonText = _getString("ArchiveExtractOverwriteOK", "압축 풀기"),
                        CloseButtonText = _getString("CopyOverwriteCancel", "취소"),
                        DefaultButton = ContentDialogButton.Close,
                        XamlRoot = _xamlRootProvider(),
                        RequestedTheme = _themeProvider()
                    };

                    if (await ShowDialogAsync(confirmDialog) != ContentDialogResult.Primary)
                    {
                        return;
                    }

                    overwrite = true;
                }

                await Task.Run(async () =>
                    await _archiveExplorerService.ExtractArchiveToDirectoryAsync(
                        archivePath,
                        targetDirectory,
                        overwrite,
                        progress => _statusBar.ShowProgress(statusText, progress, () => _archiveCts?.Cancel()),
                        token
                    ),
                    token
                );

                _loadDirectoryRoot(parentDirectory);
                var extractedFolder = _viewModel.ExplorerItems
                    .FirstOrDefault(candidate => string.Equals(candidate.Path, targetDirectory, StringComparison.OrdinalIgnoreCase));
                if (extractedFolder != null)
                {
                    _leftSidebar.FileList.SelectedItem = extractedFolder;
                    _leftSidebar.FileList.ScrollIntoView(extractedFolder);
                }
            }
            catch (OperationCanceledException)
            {
                // Silent cancellation
            }
            catch (Exception ex)
            {
                _showError(
                    _getString("ArchiveExtractFailedTitle", "압축 풀기 실패"),
                    ex.Message);
            }
            finally
            {
                _statusBar.HideProgress();
                _archiveCts?.Dispose();
                _archiveCts = null;
            }
        }

        private async void OnCompressFolderToZipClick(object sender, RoutedEventArgs e)
        {
            await CompressSelectedItemsAsync(sender, ".zip", _archiveExplorerService.CreateZipFromPathsAsync);
        }

        private async void OnCompressFolderToSevenZipClick(object sender, RoutedEventArgs e)
        {
            await CompressSelectedItemsAsync(sender, ".7z", _archiveExplorerService.CreateSevenZipFromPathsAsync);
        }

        private async Task CompressSelectedItemsAsync(
            object sender,
            string archiveExtension,
            Func<IReadOnlyList<string>, string, Action<double>?, System.Threading.CancellationToken, Task> createArchiveAsync)
        {
            IReadOnlyList<ExplorerItem> selectedItems = GetSelectedExplorerItems(sender);
            if (selectedItems.Count == 0 || !selectedItems.All(CanCompressItem))
            {
                return;
            }

            IReadOnlyList<string> sourcePaths = selectedItems
                .Select(item => Path.GetFullPath(item.Path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (sourcePaths.Count == 0)
            {
                return;
            }

            if (sourcePaths.Any(path => Directory.Exists(path) && Directory.GetParent(path) == null))
            {
                _showError(
                    _getString("ArchiveCreateFailedTitle", "압축 파일 만들기 실패"),
                    _getString("ArchiveCreateRootFolderNotSupported", "드라이브 루트 폴더는 압축할 수 없습니다."));
                return;
            }

            string outputDirectory = GetArchiveOutputDirectory(sourcePaths);
            if (string.IsNullOrWhiteSpace(outputDirectory) || !Directory.Exists(outputDirectory))
            {
                _showError(
                    _getString("ArchiveCreateFailedTitle", "압축 파일 만들기 실패"),
                    _getString("ArchiveCreateOutputDirectoryMissing", "압축 파일을 저장할 폴더를 찾을 수 없습니다."));
                return;
            }

            string baseName = sourcePaths.Count == 1
                ? GetArchiveBaseName(sourcePaths[0])
                : _getString("ExplorerMultipleArchiveBaseName", "archive");
            string outputPath = GetUniqueArchiveOutputPath(
                outputDirectory,
                baseName,
                archiveExtension,
                sourcePaths);

            _archiveCts = new System.Threading.CancellationTokenSource();
            var token = _archiveCts.Token;
            string archiveName = Path.GetFileName(outputPath);
            string statusTextFormat = _getString("ArchiveProgressCompressing", "[{0}] 압축 중...");
            string statusText = string.Format(statusTextFormat, archiveName);
            string temporaryPath = outputPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await createArchiveAsync(
                    sourcePaths,
                    temporaryPath,
                    progress => _statusBar.ShowProgress(statusText, progress, () => _archiveCts?.Cancel()),
                    token
                );
                File.Move(temporaryPath, outputPath, overwrite: false);

                string currentFolder = _currentFolderProvider();
                if (!string.IsNullOrWhiteSpace(currentFolder) && Directory.Exists(currentFolder))
                {
                    _loadDirectoryRoot(currentFolder);
                }
            }
            catch (OperationCanceledException)
            {
                // Silent cancellation
            }
            catch (Exception ex)
            {
                _showError(
                    _getString("ArchiveCreateFailedTitle", "압축 파일 만들기 실패"),
                    ex.Message);
            }
            finally
            {
                _statusBar.HideProgress();
                _archiveCts?.Dispose();
                _archiveCts = null;
                TryDeleteTemporaryArchive(temporaryPath);
            }
        }

        private string GetArchiveOutputDirectory(IReadOnlyList<string> sourcePaths)
        {
            string? firstDirectory = GetItemParentDirectory(sourcePaths[0]);
            if (!string.IsNullOrWhiteSpace(firstDirectory) &&
                sourcePaths.All(path => string.Equals(
                    GetItemParentDirectory(path),
                    firstDirectory,
                    StringComparison.OrdinalIgnoreCase)))
            {
                return firstDirectory;
            }

            return _currentFolderProvider();
        }

        private static string? GetItemParentDirectory(string path)
        {
            if (Directory.Exists(path))
            {
                return Directory.GetParent(path)?.FullName;
            }

            return Path.GetDirectoryName(path);
        }

        private static string GetArchiveBaseName(string path)
        {
            string name = Directory.Exists(path)
                ? new DirectoryInfo(path).Name
                : Path.GetFileNameWithoutExtension(path);
            return string.IsNullOrWhiteSpace(name) ? "archive" : name;
        }

        private static string GetUniqueArchiveOutputPath(
            string outputDirectory,
            string baseName,
            string archiveExtension,
            IReadOnlyList<string> sourcePaths)
        {
            for (int suffix = 1; ; suffix++)
            {
                string candidateName = suffix == 1
                    ? baseName
                    : $"{baseName}_{suffix}";
                string candidatePath = Path.Combine(outputDirectory, candidateName + archiveExtension);
                bool isSourcePath = sourcePaths.Any(path =>
                    string.Equals(path, candidatePath, StringComparison.OrdinalIgnoreCase));
                if (!isSourcePath && !File.Exists(candidatePath) && !Directory.Exists(candidatePath))
                {
                    return candidatePath;
                }
            }
        }

        private static void TryDeleteTemporaryArchive(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        private string CreateMarkdownImageText(ExplorerItem item)
        {
            string baseDirectory = GetMarkdownBaseDirectory();
            string imagePath = item.Path;
            string relativePath = Path.GetRelativePath(baseDirectory, imagePath).Replace('\\', '/');
            string altText = Path.GetFileNameWithoutExtension(item.Name)
                .Replace("[", "\\[")
                .Replace("]", "\\]");
            return $"![{altText}]({relativePath})";
        }

        private string GetMarkdownBaseDirectory()
        {
            var activeTab = _activeTabProvider();
            if (activeTab != null &&
                !activeTab.IsReadOnlyViewer &&
                !string.IsNullOrWhiteSpace(activeTab.FilePath) &&
                File.Exists(activeTab.FilePath))
            {
                string? activeTabDirectory = Path.GetDirectoryName(activeTab.FilePath);
                if (!string.IsNullOrWhiteSpace(activeTabDirectory) && Directory.Exists(activeTabDirectory))
                {
                    return activeTabDirectory;
                }
            }

            string currentFolder = _currentFolderProvider();
            if (!string.IsNullOrWhiteSpace(currentFolder) && Directory.Exists(currentFolder))
            {
                return currentFolder;
            }

            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        private string GetConfiguredHomeFolder()
        {
            string? homeFolder = _homeFolderPathProvider?.Invoke()?.Trim();
            if (!string.IsNullOrWhiteSpace(homeFolder) && Directory.Exists(homeFolder))
            {
                return homeFolder;
            }
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        private async void OnDownloadRemoteItemClick(object sender, RoutedEventArgs e)
        {
            ExplorerItem? item = GetExplorerItem(sender);
            if (item == null || !IsRemoteExplorerItem(item))
            {
                return;
            }

            IntPtr hwnd = App.MainWindow != null ? WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow) : IntPtr.Zero;
            Microsoft.UI.WindowId windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(windowId);
            picker.SuggestedFolder = GetConfiguredHomeFolder();
            var pickResult = await picker.PickSingleFolderAsync();
            if (pickResult == null || string.IsNullOrWhiteSpace(pickResult.Path))
            {
                return;
            }

            string targetLocalDirectory = pickResult.Path;
            using System.Threading.CancellationTokenSource cts = new();

            string downloadStatusPrefix = _getString("RemoteDownloadingStatus", "다운로드 중...");
            bool isCompleted = false;

            try
            {
                _statusBar.ShowProgress(
                    $"{downloadStatusPrefix} ({item.Name})",
                    0,
                    () => cts.Cancel());

                await _remoteWorkspaceService.DownloadRemoteItemToFolderAsync(
                    item.Path,
                    item.IsFolder,
                    targetLocalDirectory,
                    (currentFile, remainingFiles, totalFiles, percent) =>
                    {
                        if (!isCompleted && !cts.IsCancellationRequested)
                        {
                            _statusBar.ShowProgress(
                                FormatRemoteTransferStatus(
                                    downloadStatusPrefix,
                                    currentFile,
                                    remainingFiles,
                                    totalFiles),
                                percent,
                                () => cts.Cancel());
                        }
                    },
                    cts.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _showError(
                    _getString("RemoteDownloadFailedTitle", "다운로드 실패"),
                    ex.Message);
            }
            finally
            {
                isCompleted = true;
                _statusBar.HideProgress();
            }
        }

        private async void OnUploadRemoteItemClick(object sender, RoutedEventArgs e)
        {
            ExplorerItem? item = GetExplorerItem(sender);
            if (item == null || !IsRemoteExplorerItem(item))
            {
                return;
            }

            var picker = new Windows.Storage.Pickers.FileOpenPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder
            };
            picker.FileTypeFilter.Add("*");
            _initializePickerWindow(picker);

            var selectedFiles = await picker.PickMultipleFilesAsync();
            if (selectedFiles.Count == 0)
            {
                return;
            }

            string targetDirectory = item.IsFolder
                ? item.Path
                : RemotePath.GetParent(item.Path);
            if (string.IsNullOrWhiteSpace(targetDirectory))
            {
                return;
            }

            using System.Threading.CancellationTokenSource cts = new();
            string uploadStatusPrefix = _getString("RemoteUploadingStatus", "업로드 중...");
            bool isCompleted = false;

            try
            {
                var uploadFiles = selectedFiles
                    .Where(file => !string.IsNullOrWhiteSpace(file.Path))
                    .ToList();
                if (uploadFiles.Count == 0)
                {
                    return;
                }

                int totalFiles = uploadFiles.Count;
                _statusBar.ShowProgress(
                    FormatRemoteTransferStatus(
                        uploadStatusPrefix,
                        uploadFiles[0].Name,
                        totalFiles,
                        totalFiles),
                    0,
                    () => cts.Cancel());

                for (int index = 0; index < totalFiles; index++)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    var file = uploadFiles[index];
                    string targetVirtualPath = RemotePath.Combine(
                        targetDirectory,
                        file.Name);
                    double lastFilePercent = 0.0;
                    await _remoteWorkspaceService.UploadLocalFileAsync(
                        file.Path,
                        targetVirtualPath,
                        percent =>
                        {
                            if (!isCompleted && !cts.IsCancellationRequested)
                            {
                                double boundedFilePercent = Math.Clamp(
                                    percent,
                                    0.0,
                                    100.0);
                                double displayPercent = Math.Max(
                                    boundedFilePercent,
                                    lastFilePercent);
                                lastFilePercent = displayPercent;
                                int remainingFiles = boundedFilePercent >= 100.0
                                    ? totalFiles - index - 1
                                    : totalFiles - index;
                                _statusBar.ShowProgress(
                                    FormatRemoteTransferStatus(
                                        uploadStatusPrefix,
                                        file.Name,
                                        remainingFiles,
                                        totalFiles),
                                    displayPercent,
                                    () => cts.Cancel());
                            }
                        },
                        cts.Token);
                }

                await _refreshRemoteExplorerAsync();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _showError(
                    _getString("RemoteUploadFailedTitle", "업로드 실패"),
                    ex.Message);
            }
            finally
            {
                isCompleted = true;
                _statusBar.HideProgress();
            }
        }

        private string FormatRemoteTransferStatus(
            string statusPrefix,
            string currentFile,
            int remainingFiles,
            int totalFiles)
        {
            string displayFile = AbbreviateFileName(currentFile);
            if (totalFiles <= 1)
            {
                return $"{statusPrefix} ({displayFile})";
            }

            string remainingText = string.Format(
                _getString(
                    "RemoteTransferRemainingFilesFormat",
                    "남은 파일 {0:N0}개"),
                Math.Max(0, remainingFiles));
            return $"{statusPrefix} ({displayFile}, {remainingText})";
        }

        private static string AbbreviateFileName(string fileName)
        {
            const int maxTextElements = 40;
            var fileNameInfo = new System.Globalization.StringInfo(fileName);
            if (fileNameInfo.LengthInTextElements <= maxTextElements)
            {
                return fileName;
            }

            return fileNameInfo.SubstringByTextElements(
                0,
                maxTextElements - 1) + "…";
        }

        private void OnCopyFileNameClick(object sender, RoutedEventArgs e)
        {
            var item = GetExplorerItem(sender);
            if (item != null && !string.IsNullOrEmpty(item.Path))
            {
                SetClipboardText(item.Name);
            }
        }

        private void OnCopyFilePathClick(object sender, RoutedEventArgs e)
        {
            var item = GetExplorerItem(sender);
            if (item != null && !string.IsNullOrEmpty(item.Path))
            {
                SetClipboardText(item.IsRemote ? RemotePath.GetDisplayPath(item.RemotePath) : item.Path);
            }
        }

        private void OnCopyFolderPathClick(object sender, RoutedEventArgs e)
        {
            var item = GetExplorerItem(sender);
            if (item != null && !string.IsNullOrEmpty(item.Path))
            {
                string folderPath = item.IsFolder
                    ? item.IsRemote ? RemotePath.GetDisplayPath(item.RemotePath) : item.Path
                    : item.IsRemote
                        ? RemotePath.GetDisplayPath(RemoteExplorerService.GetParentPath(item.RemotePath))
                        : Path.GetDirectoryName(item.Path) ?? string.Empty;
                SetClipboardText(folderPath);
            }
        }

        private async void OnRenameClick(object sender, RoutedEventArgs e)
        {
            var item = GetExplorerItem(sender);
            if (item == null || item.IsArchiveEntry || string.IsNullOrEmpty(item.Path))
            {
                return;
            }

            string oldPath = item.Path;
            string parentDir = Path.GetDirectoryName(oldPath) ?? string.Empty;
            string oldName = item.Name;

            var textBox = new TextBox
            {
                Text = oldName,
                SelectionStart = 0,
                SelectionLength = Path.GetFileNameWithoutExtension(oldName).Length
            };

            var dialog = new ContentDialog
            {
                Title = _getString("RenameDialogTitle", "이름 바꾸기"),
                Content = textBox,
                PrimaryButtonText = _getString("RenameDialogOK", "확인"),
                CloseButtonText = _getString("RenameDialogCancel", "취소"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = _xamlRootProvider(),
                RequestedTheme = _themeProvider()
            };

            bool confirmed = false;
            textBox.KeyDown += (_, e) =>
            {
                if (e.Key == Windows.System.VirtualKey.Enter)
                {
                    confirmed = true;
                    dialog.Hide();
                }
            };

            ContentDialogResult result = await ShowDialogAsync(dialog);
            if (result != ContentDialogResult.Primary && !confirmed)
            {
                return;
            }

            string newName = textBox.Text.Trim();
            if (string.IsNullOrEmpty(newName) || newName == oldName)
            {
                return;
            }

            if (item.IsRemote)
            {
                try
                {
                    await _remoteWorkspaceService.RenameAsync(item.Path, newName, item.IsFolder);
                    CloseOpenTabsForPath(item.Path);
                    await _refreshRemoteExplorerAsync();
                }
                catch (Exception ex)
                {
                    _showError(
                        _getString("RenameErrorTitle", "이름 바꾸기 오류"),
                        ex.Message);
                }
                return;
            }

            string newPath = Path.Combine(parentDir, newName);

            try
            {
                if (item.IsFolder)
                {
                    Directory.Move(oldPath, newPath);
                }
                else
                {
                    File.Move(oldPath, newPath);
                    CloseOpenTabsForPath(oldPath);
                    await _loadFileIntoTabAsync(newPath);
                }

                _loadDirectoryRoot(_currentFolderProvider());
            }
            catch (Exception ex)
            {
                _showError(
                    _getString("RenameErrorTitle", "이름 바꾸기 오류"),
                    ex.Message);
            }
        }

        private async void OnDeleteClick(object sender, RoutedEventArgs e)
        {
            List<ExplorerItem> selectedItems = GetSelectedExplorerItems(sender)
                .Where(CanDeleteItem)
                .GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
            if (selectedItems.Count == 0)
            {
                return;
            }

            var confirmDialog = new ContentDialog
            {
                Title = _getString("DeleteConfirmTitle", "삭제 확인"),
                Content = selectedItems.Count == 1
                    ? string.Format(
                        selectedItems[0].IsRemote
                            ? _getString("RemoteDeleteConfirmMessage", "'{0}'을(를) 원격 서버에서 영구 삭제하시겠습니까?")
                            : _getString("DeleteConfirmMessage", "'{0}'을(를) 휴지통으로 이동하시겠습니까?"),
                        selectedItems[0].Name)
                    : string.Format(
                        _getString("DeleteConfirmMultipleMessage", "선택한 {0}개 항목을 삭제하시겠습니까?"),
                        selectedItems.Count),
                PrimaryButtonText = _getString("DeleteConfirmOK", "삭제"),
                CloseButtonText = _getString("DeleteConfirmCancel", "취소"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = _xamlRootProvider(),
                RequestedTheme = _themeProvider()
            };

            if (await ShowDialogAsync(confirmDialog) != ContentDialogResult.Primary)
            {
                return;
            }

            try
            {
                bool deletedRemoteItem = false;
                foreach (ExplorerItem item in selectedItems)
                {
                    CloseOpenTabsForPath(item.Path);

                    if (item.IsRemote)
                    {
                        await _remoteWorkspaceService.DeleteAsync(item.Path, item.IsFolder);
                        deletedRemoteItem = true;
                        continue;
                    }

                    if (item.IsFolder)
                    {
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                            item.Path,
                            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    }
                    else
                    {
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                            item.Path,
                            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    }
                }

                if (deletedRemoteItem)
                {
                    await _refreshRemoteExplorerAsync();
                }
                else
                {
                    _loadDirectoryRoot(_currentFolderProvider());
                }
            }
            catch (Exception ex)
            {
                _showError(
                    _getString("DeleteErrorTitle", "삭제 오류"),
                    ex.Message);
            }
        }

        private ExplorerItem? GetExplorerItem(object sender)
        {
            if (sender is FrameworkElement element)
            {
                ExplorerItem? dataContextItem = GetTreeExplorerItem(element.DataContext);
                if (dataContextItem != null)
                {
                    return dataContextItem;
                }

                if (element.Tag is ExplorerItem tagItem)
                {
                    return tagItem;
                }
            }

            return GetTreeExplorerItem(_leftSidebar.ExplorerTree.SelectedItem)
                ?? _leftSidebar.FileList.SelectedItem as ExplorerItem;
        }

        private static bool CanOpenExplorerFile(ExplorerItem? item)
        {
            return item != null &&
                   !item.IsFolder &&
                   !item.IsArchiveEntry &&
                   !string.IsNullOrWhiteSpace(item.Path) &&
                   (item.IsRemote || File.Exists(item.Path));
        }

        private static bool IsSupportedArchiveFile(ExplorerItem? item)
        {
            return item != null &&
                   !item.IsFolder &&
                   !item.IsArchiveEntry &&
                   !string.IsNullOrWhiteSpace(item.Path) &&
                   File.Exists(item.Path) &&
                   ArchiveExplorerService.IsSupportedArchivePath(item.Path);
        }

        private static string GetArchiveExtractFolderName(string archivePath)
        {
            string folderName = Path.GetFileNameWithoutExtension(archivePath);
            return string.IsNullOrWhiteSpace(folderName)
                ? "archive"
                : folderName;
        }

        private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
        {
            bool terminalWasVisible = _isTerminalVisible();
            if (terminalWasVisible)
            {
                _suspendTerminal();
            }

            try
            {
                return await dialog.ShowAsync();
            }
            finally
            {
                if (terminalWasVisible)
                {
                    _resumeTerminal();
                }
            }
        }

        private void CloseOpenTabsForPath(string path)
        {
            var tabsToClose = _viewModel.Tabs
                .Where(t =>
                    string.Equals(t.FilePath, path, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(t.RemotePath, path, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var tab in tabsToClose)
            {
                var tabItem = FindTabItem(tab.Id);
                if (tabItem != null)
                {
                    _closeTabAndCleanup(tab, tabItem);
                }
            }
        }

        private TabViewItem? FindTabItem(string tabId)
        {
            return _primaryTabView.TabItems.Cast<TabViewItem>()
                .FirstOrDefault(t => t.Tag as string == tabId)
                ?? _secondaryTabView.TabItems.Cast<TabViewItem>()
                    .FirstOrDefault(t => t.Tag as string == tabId);
        }

        private static void SetClipboardText(string text)
        {
            var dp = new DataPackage();
            dp.SetText(text);
            Clipboard.SetContent(dp);
        }

        private static bool IsSupportedImageFile(string filePath)
        {
            return SupportedFileTypes.IsImageFile(filePath);
        }

        private void OnFileListViewDragOver(object sender, DragEventArgs e)
        {
            if (_isArchiveViewProvider() || _isRemoteViewProvider())
            {
                e.AcceptedOperation = DataPackageOperation.None;
                e.Handled = true;
                return;
            }

            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                e.AcceptedOperation = DataPackageOperation.Copy;
                e.DragUIOverride.Caption = _getString("DragDropCopyRootCaption", "탐색기 폴더로 복사");
                e.DragUIOverride.IsCaptionVisible = true;
                e.DragUIOverride.IsContentVisible = true;
                e.Handled = true;
            }
        }

        private async void OnFileListViewDrop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            if (_isArchiveViewProvider() || _isRemoteViewProvider())
            {
                e.AcceptedOperation = DataPackageOperation.None;
                return;
            }

            if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                return;
            }

            string currentFolder = _currentFolderProvider();
            if (string.IsNullOrWhiteSpace(currentFolder) || !Directory.Exists(currentFolder))
            {
                return;
            }

            var deferral = e.GetDeferral();
            try
            {
                var items = await e.DataView.GetStorageItemsAsync();
                foreach (var item in items)
                {
                    await CopyStorageItemAsync(item.Path, currentFolder);
                }
                _loadDirectoryRoot(currentFolder);
            }
            catch (Exception ex)
            {
                _showError(
                    _getString("DragDropCopyErrorTitle", "드래그 앤 드롭 복사 오류"),
                    ex.Message);
            }
            finally
            {
                deferral.Complete();
            }
        }

        private void OnFileListViewItemDragOver(object sender, DragEventArgs e)
        {
            if (_isRemoteViewProvider())
            {
                e.AcceptedOperation = DataPackageOperation.None;
                e.Handled = true;
                return;
            }

            if (sender is FrameworkElement targetElement &&
                targetElement.DataContext is ExplorerItem targetArchiveItem &&
                targetArchiveItem.IsArchiveEntry)
            {
                e.AcceptedOperation = DataPackageOperation.None;
                e.Handled = true;
                return;
            }

            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                e.AcceptedOperation = DataPackageOperation.Copy;

                string targetName = string.Empty;
                if (sender is FrameworkElement element && element.DataContext is ExplorerItem item)
                {
                    targetName = item.Name;
                }

                if (!string.IsNullOrEmpty(targetName))
                {
                    string format = _getString("DragDropCopyItemCaptionFormat", "'{0}' 위치로 복사");
                    e.DragUIOverride.Caption = string.Format(format, targetName);
                }
                else
                {
                    e.DragUIOverride.Caption = _getString("DragDropCopyItemCaption", "해당 위치로 복사");
                }
                e.DragUIOverride.IsCaptionVisible = true;
                e.DragUIOverride.IsContentVisible = true;
                e.Handled = true;
            }
        }

        private async void OnFileListViewItemDrop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            if (_isRemoteViewProvider())
            {
                e.AcceptedOperation = DataPackageOperation.None;
                return;
            }

            if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                return;
            }

            if (sender is not FrameworkElement element || element.DataContext is not ExplorerItem targetItem)
            {
                return;
            }

            if (targetItem.IsArchiveEntry)
            {
                return;
            }

            string targetDir;
            if (targetItem.IsFolder)
            {
                targetDir = targetItem.Path;
            }
            else
            {
                targetDir = Path.GetDirectoryName(targetItem.Path) ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(targetDir) || !Directory.Exists(targetDir))
            {
                return;
            }

            var deferral = e.GetDeferral();
            try
            {
                var items = await e.DataView.GetStorageItemsAsync();
                foreach (var item in items)
                {
                    await CopyStorageItemAsync(item.Path, targetDir);
                }
                _loadDirectoryRoot(_currentFolderProvider());
            }
            catch (Exception ex)
            {
                _showError(
                    _getString("DragDropCopyErrorTitle", "드래그 앤 드롭 복사 오류"),
                    ex.Message);
            }
            finally
            {
                deferral.Complete();
            }
        }

        private void OnExplorerTreeDragOver(object sender, DragEventArgs e)
        {
            ExplorerItem? item = FindExplorerItemAtTreePosition(e);
            string targetDir = GetTreeDropTargetFolderPath(
                item,
                useProjectRootWhenNoItem: true);
            _treeDropTargetFolderPath = targetDir;
            if (string.IsNullOrWhiteSpace(targetDir) ||
                !Directory.Exists(targetDir) ||
                !e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                e.AcceptedOperation = DataPackageOperation.None;
                e.Handled = true;
                return;
            }

            e.AcceptedOperation = DataPackageOperation.Copy;
            string targetName = Path.GetFileName(
                targetDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            string format = _getString("DragDropCopyItemCaptionFormat", "'{0}' 위치로 복사");
            e.DragUIOverride.Caption = string.Format(
                format,
                string.IsNullOrWhiteSpace(targetName) ? targetDir : targetName);
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsContentVisible = true;
            e.Handled = true;
        }

        private async void OnExplorerTreeDrop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                return;
            }

            ExplorerItem? targetItem = FindExplorerItemAtTreePosition(e);
            string targetDir = GetTreeDropTargetFolderPath(targetItem, useProjectRootWhenNoItem: false);
            if (string.IsNullOrWhiteSpace(targetDir))
            {
                targetDir = _treeDropTargetFolderPath;
            }

            _treeDropTargetFolderPath = string.Empty;
            if (string.IsNullOrWhiteSpace(targetDir) || !Directory.Exists(targetDir))
            {
                e.AcceptedOperation = DataPackageOperation.None;
                return;
            }

            var deferral = e.GetDeferral();
            try
            {
                var items = await e.DataView.GetStorageItemsAsync();
                foreach (var item in items)
                {
                    await CopyStorageItemAsync(item.Path, targetDir);
                }

                _refreshTreeFolder(targetDir);
            }
            catch (Exception ex)
            {
                _showError(
                    _getString("DragDropCopyErrorTitle", "드래그 앤 드롭 복사 오류"),
                    ex.Message);
            }
            finally
            {
                deferral.Complete();
            }
        }

        private string GetTreeDropTargetFolderPath(
            ExplorerItem? item,
            bool useProjectRootWhenNoItem)
        {
            if (item == null)
            {
                if (!useProjectRootWhenNoItem)
                {
                    return string.Empty;
                }

                string currentFolder = _currentFolderProvider();
                return Directory.Exists(currentFolder) ? currentFolder : string.Empty;
            }

            if (item.IsArchiveEntry || item.IsArchive)
            {
                return string.Empty;
            }

            string targetDir = item.IsFolder
                ? item.Path
                : Path.GetDirectoryName(item.Path) ?? string.Empty;
            return Directory.Exists(targetDir) ? targetDir : string.Empty;
        }

        private ExplorerItem? FindExplorerItemAtTreePosition(DragEventArgs e)
        {
            TreeView tree = _leftSidebar.ExplorerTree;
            Windows.Foundation.Point position = e.GetPosition(tree);
            TreeViewItem? hitItem = null;
            FindTreeViewItemAtPosition(tree, tree, position, ref hitItem);
            if (hitItem == null)
            {
                return null;
            }

            return GetTreeExplorerItem(hitItem.DataContext)
                ?? GetTreeExplorerItem(hitItem.Content);
        }

        private static void FindTreeViewItemAtPosition(
            DependencyObject parent,
            TreeView tree,
            Windows.Foundation.Point position,
            ref TreeViewItem? hitItem)
        {
            int childCount = VisualTreeHelper.GetChildrenCount(parent);
            for (int index = 0; index < childCount; index++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, index);
                if (child is TreeViewItem treeViewItem &&
                    treeViewItem.Visibility == Visibility.Visible &&
                    IsPositionInsideElement(treeViewItem, tree, position))
                {
                    hitItem = treeViewItem;
                }

                FindTreeViewItemAtPosition(child, tree, position, ref hitItem);
            }
        }

        private static bool IsPositionInsideElement(
            FrameworkElement element,
            TreeView tree,
            Windows.Foundation.Point position)
        {
            if (element.ActualWidth <= 0 || element.ActualHeight <= 0)
            {
                return false;
            }

            try
            {
                Windows.Foundation.Point topLeft = element
                    .TransformToVisual(tree)
                    .TransformPoint(new Windows.Foundation.Point(0, 0));
                var bounds = new Windows.Foundation.Rect(
                    topLeft.X,
                    topLeft.Y,
                    element.ActualWidth,
                    element.ActualHeight);
                return bounds.Contains(position);
            }
            catch
            {
                return false;
            }
        }

        private static ExplorerItem? GetTreeExplorerItem(object? value)
        {
            return value as ExplorerItem
                ?? (value as TreeViewNode)?.Content as ExplorerItem;
        }

        private Task<bool> CopyStorageItemAsync(string sourcePath, string targetDir)
        {
            return TransferStorageItemAsync(sourcePath, targetDir, move: false);
        }

        private async Task<bool> TransferStorageItemAsync(string sourcePath, string targetDir, bool move)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !Directory.Exists(targetDir))
            {
                return false;
            }

            sourcePath = Path.GetFullPath(sourcePath);
            targetDir = Path.GetFullPath(targetDir);
            if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
            {
                return false;
            }

            string name = Path.GetFileName(sourcePath);
            string destPath = Path.Combine(targetDir, name);

            if (File.Exists(sourcePath))
            {
                if (string.Equals(sourcePath, destPath, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (File.Exists(destPath))
                {
                    var confirmDialog = new ContentDialog
                    {
                        Title = _getString("CopyOverwriteTitle", "덮어쓰기 확인"),
                        Content = string.Format(_getString("CopyOverwriteMessage", "'{0}' 파일이 이미 존재합니다. 덮어쓰시겠습니까?"), name),
                        PrimaryButtonText = _getString("CopyOverwriteOK", "덮어쓰기"),
                        CloseButtonText = _getString("CopyOverwriteCancel", "취소"),
                        DefaultButton = ContentDialogButton.Close,
                        XamlRoot = _xamlRootProvider(),
                        RequestedTheme = _themeProvider()
                    };

                    if (await ShowDialogAsync(confirmDialog) != ContentDialogResult.Primary)
                    {
                        return false;
                    }
                }

                await Task.Run(() =>
                {
                    if (move)
                    {
                        File.Move(sourcePath, destPath, overwrite: true);
                    }
                    else
                    {
                        File.Copy(sourcePath, destPath, true);
                    }
                });
                return true;
            }
            else if (Directory.Exists(sourcePath))
            {
                if (string.Equals(sourcePath, targetDir, StringComparison.OrdinalIgnoreCase) ||
                    IsSameOrDescendantPath(targetDir, sourcePath))
                {
                    _showError(
                        _getString("CopyFolderErrorTitle", "폴더 복사 오류"),
                        _getString("CopyFolderSelfParent", "폴더를 자기 자신 또는 하위 폴더에 복사할 수 없습니다."));
                    return false;
                }

                if (Directory.Exists(destPath))
                {
                    var confirmDialog = new ContentDialog
                    {
                        Title = _getString("CopyOverwriteTitle", "덮어쓰기 확인"),
                        Content = string.Format(_getString("CopyOverwriteFolderMessage", "'{0}' 폴더가 이미 존재합니다. 덮어쓰시겠습니까? (기존 파일은 유지되거나 덮어써집니다)"), name),
                        PrimaryButtonText = _getString("CopyOverwriteOK", "덮어쓰기"),
                        CloseButtonText = _getString("CopyOverwriteCancel", "취소"),
                        DefaultButton = ContentDialogButton.Close,
                        XamlRoot = _xamlRootProvider(),
                        RequestedTheme = _themeProvider()
                    };

                    if (await ShowDialogAsync(confirmDialog) != ContentDialogResult.Primary)
                    {
                        return false;
                    }
                }

                await Task.Run(() =>
                {
                    if (move && !Directory.Exists(destPath))
                    {
                        try
                        {
                            Directory.Move(sourcePath, destPath);
                            return;
                        }
                        catch (IOException)
                        {
                            // Fall back to copy/delete for a cross-volume move.
                        }
                    }

                    CopyDirectory(sourcePath, destPath);
                    if (move)
                    {
                        Directory.Delete(sourcePath, recursive: true);
                    }
                });
                return true;
            }

            return false;
        }

        private static bool IsSameOrDescendantPath(string candidatePath, string parentPath)
        {
            string normalizedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parentPath)) + Path.DirectorySeparatorChar;
            string normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidatePath)) + Path.DirectorySeparatorChar;
            return normalizedCandidate.StartsWith(normalizedParent, StringComparison.OrdinalIgnoreCase);
        }

        private static void CopyDirectory(string sourceDir, string destDir)
        {
            Directory.CreateDirectory(destDir);

            foreach (string file in Directory.GetFiles(sourceDir))
            {
                string dest = Path.Combine(destDir, Path.GetFileName(file));
                File.Copy(file, dest, true);
            }

            foreach (string folder in Directory.GetDirectories(sourceDir))
            {
                string dest = Path.Combine(destDir, Path.GetFileName(folder));
                CopyDirectory(folder, dest);
            }
        }
    }
}
