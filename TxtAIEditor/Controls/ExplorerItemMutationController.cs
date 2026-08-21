using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Services;
using TxtAIEditor.ViewModels;

namespace TxtAIEditor.Controls
{
    internal sealed class ExplorerItemMutationController
    {
        private static readonly System.Text.Encoding Utf8NoBom = new System.Text.UTF8Encoding(false);
        private readonly LeftSidebarPane _leftSidebar;
        private readonly MainWindowViewModel _viewModel;
        private readonly RemoteWorkspaceService _remoteWorkspaceService;
        private readonly TabView _primaryTabView;
        private readonly TabView _secondaryTabView;
        private readonly ExplorerSelectionService _selection;
        private readonly ExplorerDialogCoordinator _dialogs;
        private readonly Func<string> _currentFolderProvider;
        private readonly Action<string> _loadDirectoryRoot;
        private readonly Func<string, Task> _loadFileIntoTabAsync;
        private readonly Action<OpenedTab, TabViewItem> _closeTabAndCleanup;
        private readonly Func<string, string, string> _getString;
        private readonly Action<string, string> _showError;
        private readonly Func<bool> _isArchiveViewProvider;
        private readonly Func<bool> _isRemoteViewProvider;
        private readonly Func<Task> _refreshRemoteExplorerAsync;

        public ExplorerItemMutationController(
            LeftSidebarPane leftSidebar,
            MainWindowViewModel viewModel,
            RemoteWorkspaceService remoteWorkspaceService,
            TabView primaryTabView,
            TabView secondaryTabView,
            ExplorerSelectionService selection,
            ExplorerDialogCoordinator dialogs,
            Func<string> currentFolderProvider,
            Action<string> loadDirectoryRoot,
            Func<string, Task> loadFileIntoTabAsync,
            Action<OpenedTab, TabViewItem> closeTabAndCleanup,
            Func<string, string, string> getString,
            Action<string, string> showError,
            Func<bool> isArchiveViewProvider,
            Func<bool> isRemoteViewProvider,
            Func<Task> refreshRemoteExplorerAsync)
        {
            _leftSidebar = leftSidebar;
            _viewModel = viewModel;
            _remoteWorkspaceService = remoteWorkspaceService;
            _primaryTabView = primaryTabView;
            _secondaryTabView = secondaryTabView;
            _selection = selection;
            _dialogs = dialogs;
            _currentFolderProvider = currentFolderProvider;
            _loadDirectoryRoot = loadDirectoryRoot;
            _loadFileIntoTabAsync = loadFileIntoTabAsync;
            _closeTabAndCleanup = closeTabAndCleanup;
            _getString = getString;
            _showError = showError;
            _isArchiveViewProvider = isArchiveViewProvider;
            _isRemoteViewProvider = isRemoteViewProvider;
            _refreshRemoteExplorerAsync = refreshRemoteExplorerAsync;
        }

        public void WireEvents()
        {
            _leftSidebar.CreateFolderClick += OnCreateFolderClick;
            _leftSidebar.CreateFileClick += OnCreateFileClick;
            _leftSidebar.CreateNotebookClick += OnCreateNotebookClick;
            _leftSidebar.RenameClick += OnRenameClick;
            _leftSidebar.DeleteClick += OnDeleteClick;
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
                XamlRoot = _dialogs.XamlRoot,
                RequestedTheme = _dialogs.Theme
            };

            if (await _dialogs.ShowAsync(dialog) != ContentDialogResult.Primary)
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
                XamlRoot = _dialogs.XamlRoot,
                RequestedTheme = _dialogs.Theme
            };

            if (await _dialogs.ShowAsync(dialog) != ContentDialogResult.Primary)
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
                XamlRoot = _dialogs.XamlRoot,
                RequestedTheme = _dialogs.Theme
            };

            if (await _dialogs.ShowAsync(dialog) != ContentDialogResult.Primary)
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

        private async void OnRenameClick(object sender, RoutedEventArgs e)
        {
            var item = _selection.GetItem(sender);
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
                XamlRoot = _dialogs.XamlRoot,
                RequestedTheme = _dialogs.Theme
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

            ContentDialogResult result = await _dialogs.ShowAsync(dialog);
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
            List<ExplorerItem> selectedItems = _selection.GetSelectedItems(sender)
                .Where(ExplorerItemCapabilities.CanDelete)
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
                XamlRoot = _dialogs.XamlRoot,
                RequestedTheme = _dialogs.Theme
            };

            if (await _dialogs.ShowAsync(confirmDialog) != ContentDialogResult.Primary)
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
    }
}
