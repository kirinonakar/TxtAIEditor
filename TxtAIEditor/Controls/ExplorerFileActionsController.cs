using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using TxtAIEditor.Core.Models;
using TxtAIEditor.ViewModels;
using Windows.ApplicationModel.DataTransfer;

namespace TxtAIEditor.Controls
{
    public sealed class ExplorerFileActionsController
    {
        private readonly LeftSidebarPane _leftSidebar;
        private readonly MainWindowViewModel _viewModel;
        private readonly TabView _primaryTabView;
        private readonly TabView _secondaryTabView;
        private readonly Func<string> _currentFolderProvider;
        private readonly Func<OpenedTab?> _activeTabProvider;
        private readonly Action<string> _loadDirectoryRoot;
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
        private readonly ConditionalWeakTable<MenuFlyout, object> _localizedFlyouts = new ConditionalWeakTable<MenuFlyout, object>();

        public ExplorerFileActionsController(
            LeftSidebarPane leftSidebar,
            MainWindowViewModel viewModel,
            TabView primaryTabView,
            TabView secondaryTabView,
            Func<string> currentFolderProvider,
            Func<OpenedTab?> activeTabProvider,
            Action<string> loadDirectoryRoot,
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
            Action resumeTerminal)
        {
            _leftSidebar = leftSidebar;
            _viewModel = viewModel;
            _primaryTabView = primaryTabView;
            _secondaryTabView = secondaryTabView;
            _currentFolderProvider = currentFolderProvider;
            _activeTabProvider = activeTabProvider;
            _loadDirectoryRoot = loadDirectoryRoot;
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

            WireEvents();
        }

        private void WireEvents()
        {
            _leftSidebar.FileListViewItemRightTapped += OnFileListViewItemRightTapped;
            _leftSidebar.CreateFolderClick += OnCreateFolderClick;
            _leftSidebar.InsertMarkdownImageClick += OnInsertMarkdownImageClick;
            _leftSidebar.OpenExternalViewerClick += OnOpenExternalViewerClick;
            _leftSidebar.OpenWithDefaultProgramClick += OnOpenWithDefaultProgramClick;
            _leftSidebar.CopyFileNameClick += OnCopyFileNameClick;
            _leftSidebar.CopyFilePathClick += OnCopyFilePathClick;
            _leftSidebar.CopyFolderPathClick += OnCopyFolderPathClick;
            _leftSidebar.RenameClick += OnRenameClick;
            _leftSidebar.DeleteClick += OnDeleteClick;
            _leftSidebar.FileListViewDragOver += OnFileListViewDragOver;
            _leftSidebar.FileListViewDrop += OnFileListViewDrop;
            _leftSidebar.FileListViewItemDragOver += OnFileListViewItemDragOver;
            _leftSidebar.FileListViewItemDrop += OnFileListViewItemDrop;
        }

        private void OnFileListViewItemRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: ExplorerItem item })
            {
                _leftSidebar.FileList.SelectedItem = item;
            }

            if (sender is FrameworkElement element && element.ContextFlyout is MenuFlyout flyout && flyout.Items.Count >= 12)
            {
                LocalizeContextFlyout(flyout);
                ConfigureContextFlyout(flyout, _leftSidebar.FileList.SelectedItem as ExplorerItem);
                CursorResetHelper.AttachToFlyout(flyout, element);
                CursorResetHelper.ResetToArrow(element);
            }

            e.Handled = true;
        }

        private async void OnCreateFolderClick(object sender, RoutedEventArgs e)
        {
            string currentFolder = _currentFolderProvider();
            if (string.IsNullOrWhiteSpace(currentFolder) || !Directory.Exists(currentFolder))
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

        private void LocalizeContextFlyout(MenuFlyout flyout)
        {
            if (_localizedFlyouts.TryGetValue(flyout, out _))
            {
                return;
            }

            _localizedFlyouts.Add(flyout, null!);
            ((MenuFlyoutItem)flyout.Items[0]).Text = _getString("ExplorerAddToFavorites", "즐겨찾기에 추가");
            ((MenuFlyoutItem)flyout.Items[1]).Text = _getString("ExplorerAddFolderToFavorites", "폴더를 즐겨찾기에 추가");
            ((MenuFlyoutItem)flyout.Items[2]).Text = _getString("ExplorerInsertMarkdownImage", "마크다운 삽입");
            ((MenuFlyoutItem)flyout.Items[3]).Text = _getString("OpenExternalViewerTooltip", "외부 뷰어로 열기");
            ((MenuFlyoutItem)flyout.Items[4]).Text = _getString("OpenWithDefaultProgramTooltip", "기본 프로그램으로 열기");
            ((MenuFlyoutItem)flyout.Items[6]).Text = _getString("ExplorerCopyFileName", "파일이름 복사");
            ((MenuFlyoutItem)flyout.Items[7]).Text = _getString("ExplorerCopyFilePath", "경로 복사");
            ((MenuFlyoutItem)flyout.Items[8]).Text = _getString("ExplorerCopyFolderPath", "폴더 경로 복사");
            ((MenuFlyoutItem)flyout.Items[10]).Text = _getString("ExplorerRename", "이름 바꾸기");
            ((MenuFlyoutItem)flyout.Items[11]).Text = _getString("ExplorerDelete", "삭제");
        }

        private static void ConfigureContextFlyout(MenuFlyout flyout, ExplorerItem? item)
        {
            if (flyout.Items.Count > 2 && flyout.Items[2] is MenuFlyoutItem markdownItem)
            {
                markdownItem.Visibility = item != null && !item.IsFolder && IsSupportedImageFile(item.Path)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            bool canOpenFile = CanOpenExplorerFile(item);
            if (flyout.Items.Count > 3 && flyout.Items[3] is MenuFlyoutItem externalViewerItem)
            {
                externalViewerItem.Visibility = canOpenFile ? Visibility.Visible : Visibility.Collapsed;
            }

            if (flyout.Items.Count > 4 && flyout.Items[4] is MenuFlyoutItem defaultProgramItem)
            {
                defaultProgramItem.Visibility = canOpenFile ? Visibility.Visible : Visibility.Collapsed;
            }
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

            await _openFileInExternalViewerAsync(item!.Path);
        }

        private async void OnOpenWithDefaultProgramClick(object sender, RoutedEventArgs e)
        {
            var item = GetExplorerItem(sender);
            if (!CanOpenExplorerFile(item))
            {
                return;
            }

            await _openFileWithDefaultProgramAsync(item!.Path);
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
                SetClipboardText(item.Path);
            }
        }

        private void OnCopyFolderPathClick(object sender, RoutedEventArgs e)
        {
            var item = GetExplorerItem(sender);
            if (item != null && !string.IsNullOrEmpty(item.Path))
            {
                string folderPath = item.IsFolder ? item.Path : Path.GetDirectoryName(item.Path) ?? string.Empty;
                SetClipboardText(folderPath);
            }
        }

        private async void OnRenameClick(object sender, RoutedEventArgs e)
        {
            var item = GetExplorerItem(sender);
            if (item == null || string.IsNullOrEmpty(item.Path))
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
                XamlRoot = _xamlRootProvider(),
                RequestedTheme = _themeProvider()
            };

            if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary)
            {
                return;
            }

            string newName = textBox.Text.Trim();
            if (string.IsNullOrEmpty(newName) || newName == oldName)
            {
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
            var item = GetExplorerItem(sender);
            if (item == null || string.IsNullOrEmpty(item.Path))
            {
                return;
            }

            var confirmDialog = new ContentDialog
            {
                Title = _getString("DeleteConfirmTitle", "삭제 확인"),
                Content = string.Format(
                    _getString("DeleteConfirmMessage", "'{0}'을(를) 휴지통으로 이동하시겠습니까?"),
                    item.Name),
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
                CloseOpenTabsForPath(item.Path);

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

                _loadDirectoryRoot(_currentFolderProvider());
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
                if (element.DataContext is ExplorerItem dataContextItem)
                {
                    return dataContextItem;
                }

                if (element.Tag is ExplorerItem tagItem)
                {
                    return tagItem;
                }
            }

            return _leftSidebar.FileList.SelectedItem as ExplorerItem;
        }

        private static bool CanOpenExplorerFile(ExplorerItem? item)
        {
            return item != null &&
                   !item.IsFolder &&
                   !string.IsNullOrWhiteSpace(item.Path) &&
                   File.Exists(item.Path);
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
                .Where(t => string.Equals(t.FilePath, path, StringComparison.OrdinalIgnoreCase))
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
            string extension = Path.GetExtension(filePath);
            return extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".webp", StringComparison.OrdinalIgnoreCase);
        }

        private void OnFileListViewDragOver(object sender, DragEventArgs e)
        {
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
            if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                return;
            }

            if (sender is not FrameworkElement element || element.DataContext is not ExplorerItem targetItem)
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

        private async Task CopyStorageItemAsync(string sourcePath, string targetDir)
        {
            if (string.IsNullOrWhiteSpace(sourcePath)) return;

            string name = Path.GetFileName(sourcePath);
            string destPath = Path.Combine(targetDir, name);

            if (File.Exists(sourcePath))
            {
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
                        return;
                    }
                }

                await Task.Run(() => File.Copy(sourcePath, destPath, true));
            }
            else if (Directory.Exists(sourcePath))
            {
                if (destPath.StartsWith(sourcePath, StringComparison.OrdinalIgnoreCase))
                {
                    _showError(
                        _getString("CopyFolderErrorTitle", "폴더 복사 오류"),
                        _getString("CopyFolderSelfParent", "폴더를 자기 자신 또는 하위 폴더에 복사할 수 없습니다."));
                    return;
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
                        return;
                    }
                }

                await Task.Run(() => CopyDirectory(sourcePath, destPath));
            }
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
