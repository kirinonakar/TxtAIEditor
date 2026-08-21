using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Services;
using TxtAIEditor.ViewModels;
using Windows.ApplicationModel.DataTransfer;

namespace TxtAIEditor.Controls
{
    public sealed class ExplorerFileActionsController
    {
        private readonly LeftSidebarPane _leftSidebar;
        private readonly RemoteWorkspaceService _remoteWorkspaceService;
        private readonly Func<string> _currentFolderProvider;
        private readonly Func<OpenedTab?> _activeTabProvider;
        private readonly Action<string> _loadDirectoryRoot;
        private readonly Func<string, Task> _openFileInExternalViewerAsync;
        private readonly Func<string, Task> _openFileWithDefaultProgramAsync;
        private readonly Func<string, Task<bool>> _insertTextIntoActiveEditorAsync;
        private readonly Func<string, string, string> _getString;
        private readonly Action<string, string> _showError;
        private readonly Func<bool> _isArchiveViewProvider;
        private readonly Func<bool> _isRemoteViewProvider;
        private readonly ImageConversionController _imageConversionController;
        private readonly ExplorerSelectionService _selection;
        private readonly ExplorerClipboardController _clipboardController;
        private readonly ExplorerItemMutationController _itemMutationController;
        private readonly ExplorerArchiveActionsController _archiveActionsController;
        private readonly ExplorerRemoteTransferController _remoteTransferController;
        private readonly ConditionalWeakTable<MenuFlyout, object> _localizedFlyouts = new();

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
            _remoteWorkspaceService = remoteWorkspaceService;
            _currentFolderProvider = currentFolderProvider;
            _activeTabProvider = activeTabProvider;
            _loadDirectoryRoot = loadDirectoryRoot;
            _openFileInExternalViewerAsync = openFileInExternalViewerAsync;
            _openFileWithDefaultProgramAsync = openFileWithDefaultProgramAsync;
            _insertTextIntoActiveEditorAsync = insertTextIntoActiveEditorAsync;
            _getString = getString;
            _showError = showError;
            _isArchiveViewProvider = isArchiveViewProvider;
            _isRemoteViewProvider = isRemoteViewProvider;
            _imageConversionController = new ImageConversionController(getString, themeProvider);

            _selection = new ExplorerSelectionService(leftSidebar);
            var dialogs = new ExplorerDialogCoordinator(
                xamlRootProvider,
                themeProvider,
                isTerminalVisible,
                suspendTerminal,
                resumeTerminal);
            _clipboardController = new ExplorerClipboardController(
                leftSidebar,
                _selection,
                dialogs,
                currentFolderProvider,
                loadDirectoryRoot,
                refreshTreeFolder,
                getString,
                showError,
                isArchiveViewProvider,
                isRemoteViewProvider);
            _itemMutationController = new ExplorerItemMutationController(
                leftSidebar,
                viewModel,
                remoteWorkspaceService,
                primaryTabView,
                secondaryTabView,
                _selection,
                dialogs,
                currentFolderProvider,
                loadDirectoryRoot,
                loadFileIntoTabAsync,
                closeTabAndCleanup,
                getString,
                showError,
                isArchiveViewProvider,
                isRemoteViewProvider,
                refreshRemoteExplorerAsync);
            _archiveActionsController = new ExplorerArchiveActionsController(
                leftSidebar,
                statusBar,
                viewModel,
                archiveExplorerService,
                _selection,
                dialogs,
                currentFolderProvider,
                loadDirectoryRoot,
                getString,
                showError);
            _remoteTransferController = new ExplorerRemoteTransferController(
                leftSidebar,
                statusBar,
                remoteWorkspaceService,
                _selection,
                refreshRemoteExplorerAsync,
                initializePickerWindow,
                homeFolderPathProvider,
                getString,
                showError);

            WireEvents();
        }

        private void WireEvents()
        {
            _leftSidebar.FileListViewItemRightTapped += OnFileListViewItemRightTapped;
            if (_leftSidebar.FileList.ContextFlyout is MenuFlyout emptyAreaFlyout)
            {
                emptyAreaFlyout.Opening += OnEmptyAreaFlyoutOpening;
            }
            _leftSidebar.InsertMarkdownImageClick += OnInsertMarkdownImageClick;
            _leftSidebar.OpenExternalViewerClick += OnOpenExternalViewerClick;
            _leftSidebar.OpenWithDefaultProgramClick += OnOpenWithDefaultProgramClick;
            _leftSidebar.ImageConversionClick += OnImageConversionClick;
            _leftSidebar.CopyFileNameClick += OnCopyFileNameClick;
            _leftSidebar.CopyFilePathClick += OnCopyFilePathClick;
            _leftSidebar.CopyFolderPathClick += OnCopyFolderPathClick;

            _clipboardController.WireEvents();
            _itemMutationController.WireEvents();
            _archiveActionsController.WireEvents();
            _remoteTransferController.WireEvents();
        }

        private void OnFileListViewItemRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            ExplorerItem? contextItem = null;
            if (sender is FrameworkElement element)
            {
                contextItem = ExplorerSelectionService.GetTreeItem(element.DataContext);
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
                    var selectedItems = _selection.GetTreeSelectedItems();
                    if (!selectedItems.Any(selected => ReferenceEquals(selected, contextItem)))
                    {
                        TreeViewNode? node = _selection.FindTreeNodeFromElement(element);
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
                        _selection.GetSelectedItems(contextItem));
                    CursorResetHelper.AttachToFlyout(flyout, element);
                    CursorResetHelper.ResetToArrow(element);
                }
            }

            e.Handled = true;
        }

        private void OnEmptyAreaFlyoutOpening(object? sender, object args)
        {
            if (sender is not MenuFlyout flyout || flyout.Items.Count < 2)
            {
                return;
            }

            _leftSidebar.FileList.SelectedItems.Clear();

            ExplorerItem? currentFolderItem = null;
            string currentFolder = _currentFolderProvider();
            if (!_isArchiveViewProvider() &&
                !_isRemoteViewProvider() &&
                !string.IsNullOrWhiteSpace(currentFolder))
            {
                currentFolderItem = new ExplorerItem
                {
                    Path = currentFolder,
                    IsFolder = true
                };
            }

            if (flyout.Items[0] is MenuFlyoutItem favoriteItem)
            {
                favoriteItem.Tag = currentFolderItem;
            }

            if (flyout.Items[1] is MenuFlyoutItem pasteItem)
            {
                pasteItem.Tag = currentFolderItem;
                pasteItem.IsEnabled = ExplorerClipboardController.CanPasteStorageItems() &&
                    _clipboardController.CanPasteIntoLocalDirectory(currentFolderItem);
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
            bool canUseLocalStorageItems = selectedItems.Count > 0 &&
                selectedItems.All(ExplorerItemCapabilities.CanUseLocalStorage);
            bool canPasteIntoDirectory = _clipboardController.CanPasteIntoLocalDirectory(item);
            bool canPaste = ExplorerClipboardController.CanPasteStorageItems() && canPasteIntoDirectory;

            SetMenuVisibility(flyout, 0, canUseLocalStorageItems);
            SetMenuVisibility(flyout, 1, canUseLocalStorageItems);
            SetMenuVisibility(flyout, 2, canPasteIntoDirectory);
            SetMenuEnabled(flyout, 2, canPaste);
            SetMenuVisibility(flyout, 3, canUseLocalStorageItems || canPasteIntoDirectory);

            if (flyout.Items.Count > 4 && flyout.Items[4] is MenuFlyoutItem addFileFavoriteItem)
            {
                addFileFavoriteItem.Visibility = hasSingleItem && !isArchiveEntry && item is not { IsFolder: true }
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            if (flyout.Items.Count > 5 && flyout.Items[5] is MenuFlyoutItem addFolderFavoriteItem)
            {
                addFolderFavoriteItem.Visibility = hasSingleItem && !isArchiveEntry ? Visibility.Visible : Visibility.Collapsed;
            }

            if (flyout.Items.Count > 6 && flyout.Items[6] is MenuFlyoutItem markdownItem)
            {
                markdownItem.Visibility = hasSingleItem && item != null && !item.IsRemote && !isArchiveEntry && !item.IsFolder && ExplorerItemCapabilities.IsSupportedImage(item.Path)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            bool canOpenFile = hasSingleItem && ExplorerItemCapabilities.CanOpenFile(item);
            if (flyout.Items.Count > 7 && flyout.Items[7] is MenuFlyoutItem externalViewerItem)
            {
                externalViewerItem.Visibility = canOpenFile ? Visibility.Visible : Visibility.Collapsed;
            }

            if (flyout.Items.Count > 8 && flyout.Items[8] is MenuFlyoutItem defaultProgramItem)
            {
                defaultProgramItem.Visibility = canOpenFile ? Visibility.Visible : Visibility.Collapsed;
            }

            bool canExtractArchive = hasSingleItem && ExplorerItemCapabilities.IsSupportedArchive(item);
            if (flyout.Items.Count > 10 && flyout.Items[10] is MenuFlyoutItem extractArchiveItem)
            {
                extractArchiveItem.Visibility = canExtractArchive ? Visibility.Visible : Visibility.Collapsed;
                if (canExtractArchive && item != null)
                {
                    string folderName = ExplorerItemCapabilities.GetArchiveExtractFolderName(item.Path);
                    string format = _getString("ExplorerExtractArchiveToFolderFormat", "{0} 폴더에 풀기");
                    extractArchiveItem.Text = string.Format(format, folderName);
                }
            }

            bool canCompress = selectedItems.Count > 0 && selectedItems.All(ExplorerItemCapabilities.CanCompress);
            SetMenuVisibility(flyout, 9, canExtractArchive || canCompress);
            if (flyout.Items.Count > 11 && flyout.Items[11] is MenuFlyoutItem compressToZipItem)
            {
                compressToZipItem.Visibility = canCompress ? Visibility.Visible : Visibility.Collapsed;
            }

            if (flyout.Items.Count > 12 && flyout.Items[12] is MenuFlyoutItem compressToSevenZipItem)
            {
                compressToSevenZipItem.Visibility = canCompress ? Visibility.Visible : Visibility.Collapsed;
            }

            bool canConvertImages = selectedItems.Count > 0 && selectedItems.All(ExplorerItemCapabilities.CanConvertImage);
            SetMenuVisibility(flyout, 13, canExtractArchive || canCompress);
            SetMenuVisibility(flyout, 14, canConvertImages);

            bool canDelete = selectedItems.Count > 0 && selectedItems.All(ExplorerItemCapabilities.CanDelete);

            bool canDownloadRemote = selectedItems.Count > 0 && selectedItems.All(ExplorerItemCapabilities.IsRemote);
            bool canUploadRemote = hasSingleItem && canDownloadRemote;
            SetMenuVisibility(flyout, 15, canConvertImages || canDownloadRemote);
            if (flyout.Items.Count > 16 && flyout.Items[16] is MenuFlyoutItem downloadRemoteItem)
            {
                downloadRemoteItem.Visibility = canDownloadRemote ? Visibility.Visible : Visibility.Collapsed;
            }

            if (flyout.Items.Count > 17 && flyout.Items[17] is MenuFlyoutItem uploadRemoteItem)
            {
                uploadRemoteItem.Visibility = canUploadRemote ? Visibility.Visible : Visibility.Collapsed;
            }

            SetMenuVisibility(flyout, 18, canDownloadRemote);

            if (flyout.Items.Count > 19 && flyout.Items[19] is MenuFlyoutItem copyFileNameItem)
            {
                copyFileNameItem.Visibility = hasSingleItem ? Visibility.Visible : Visibility.Collapsed;
                copyFileNameItem.Text = item is { IsFolder: true }
                    ? _getString("ExplorerCopyFolderName", "폴더이름 복사")
                    : _getString("ExplorerCopyFileName", "파일이름 복사");
            }

            if (flyout.Items.Count > 20 && flyout.Items[20] is MenuFlyoutItem copyFilePathItem)
            {
                copyFilePathItem.Visibility = hasSingleItem && item is not { IsFolder: true }
                    ? Visibility.Visible
                    : Visibility.Collapsed;
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
            NormalizeContextFlyoutSeparators(flyout);
        }

        private static void NormalizeContextFlyoutSeparators(MenuFlyout flyout)
        {
            var pendingSeparators = new List<MenuFlyoutSeparator>();
            bool hasVisibleAction = false;

            foreach (object menuItem in flyout.Items)
            {
                if (menuItem is MenuFlyoutSeparator separator)
                {
                    if (separator.Visibility != Visibility.Visible || !hasVisibleAction)
                    {
                        separator.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        pendingSeparators.Add(separator);
                    }

                    continue;
                }

                if (menuItem is not FrameworkElement element || element.Visibility != Visibility.Visible)
                {
                    continue;
                }

                if (pendingSeparators.Count > 0)
                {
                    pendingSeparators[0].Visibility = Visibility.Visible;
                    for (int index = 1; index < pendingSeparators.Count; index++)
                    {
                        pendingSeparators[index].Visibility = Visibility.Collapsed;
                    }

                    pendingSeparators.Clear();
                }

                hasVisibleAction = true;
            }

            foreach (MenuFlyoutSeparator separator in pendingSeparators)
            {
                separator.Visibility = Visibility.Collapsed;
            }
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

        private void OnImageConversionClick(object sender, RoutedEventArgs e)
        {
            IReadOnlyList<string> imagePaths = _selection.GetSelectedItems(sender)
                .Where(ExplorerItemCapabilities.CanConvertImage)
                .Select(item => item.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (imagePaths.Count == 0)
            {
                return;
            }

            FrameworkElement? sourceElement = sender as FrameworkElement;
            _ = ShowImageConversionAndRefreshAsync(imagePaths, sourceElement);
        }

        private async Task ShowImageConversionAndRefreshAsync(
            IReadOnlyList<string> imagePaths,
            FrameworkElement? sourceElement)
        {
            bool converted = await _imageConversionController.ShowAsync(
                imagePaths,
                sourceElement?.XamlRoot ?? _leftSidebar.XamlRoot,
                sourceElement?.ActualTheme ?? _leftSidebar.ActualTheme);
            if (converted)
            {
                _loadDirectoryRoot(_currentFolderProvider());
            }
        }

        private async void OnInsertMarkdownImageClick(object sender, RoutedEventArgs e)
        {
            var item = _selection.GetItem(sender);
            if (item == null || item.IsFolder || !ExplorerItemCapabilities.IsSupportedImage(item.Path))
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
            var item = _selection.GetItem(sender);
            if (!ExplorerItemCapabilities.CanOpenFile(item))
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
            var item = _selection.GetItem(sender);
            if (!ExplorerItemCapabilities.CanOpenFile(item))
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

        private void OnCopyFileNameClick(object sender, RoutedEventArgs e)
        {
            var item = _selection.GetItem(sender);
            if (item != null && !string.IsNullOrEmpty(item.Path))
            {
                SetClipboardText(item.Name);
            }
        }

        private void OnCopyFilePathClick(object sender, RoutedEventArgs e)
        {
            var item = _selection.GetItem(sender);
            if (item != null && !string.IsNullOrEmpty(item.Path))
            {
                SetClipboardText(item.IsRemote ? RemotePath.GetDisplayPath(item.RemotePath) : item.Path);
            }
        }

        private void OnCopyFolderPathClick(object sender, RoutedEventArgs e)
        {
            var item = _selection.GetItem(sender);
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

        private static void SetClipboardText(string text)
        {
            var dp = new DataPackage();
            dp.SetText(text);
            Clipboard.SetContent(dp);
        }

    }
}
