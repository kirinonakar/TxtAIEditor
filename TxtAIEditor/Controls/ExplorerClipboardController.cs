using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace TxtAIEditor.Controls
{
    internal sealed class ExplorerClipboardController
    {
        private readonly LeftSidebarPane _leftSidebar;
        private readonly ExplorerSelectionService _selection;
        private readonly ExplorerDialogCoordinator _dialogs;
        private readonly Func<string> _currentFolderProvider;
        private readonly Action<string> _loadDirectoryRoot;
        private readonly Action<string> _refreshTreeFolder;
        private readonly Func<string, string, string> _getString;
        private readonly Action<string, string> _showError;
        private readonly Func<bool> _isArchiveViewProvider;
        private readonly Func<bool> _isRemoteViewProvider;
        private readonly HashSet<ExplorerItem> _cutItems = new();

        private string _treeDropTargetFolderPath = string.Empty;

        public ExplorerClipboardController(
            LeftSidebarPane leftSidebar,
            ExplorerSelectionService selection,
            ExplorerDialogCoordinator dialogs,
            Func<string> currentFolderProvider,
            Action<string> loadDirectoryRoot,
            Action<string> refreshTreeFolder,
            Func<string, string, string> getString,
            Action<string, string> showError,
            Func<bool> isArchiveViewProvider,
            Func<bool> isRemoteViewProvider)
        {
            _leftSidebar = leftSidebar;
            _selection = selection;
            _dialogs = dialogs;
            _currentFolderProvider = currentFolderProvider;
            _loadDirectoryRoot = loadDirectoryRoot;
            _refreshTreeFolder = refreshTreeFolder;
            _getString = getString;
            _showError = showError;
            _isArchiveViewProvider = isArchiveViewProvider;
            _isRemoteViewProvider = isRemoteViewProvider;
        }

        public void WireEvents()
        {
            _leftSidebar.CutClick += OnCutClick;
            _leftSidebar.CopyItemsClick += OnCopyItemsClick;
            _leftSidebar.PasteClick += OnPasteClick;
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

        public bool CanPasteIntoLocalDirectory(ExplorerItem? contextItem)
        {
            return !_isArchiveViewProvider() &&
                   !_isRemoteViewProvider() &&
                   (contextItem == null || (!contextItem.IsRemote && !contextItem.IsArchiveEntry)) &&
                   Directory.Exists(GetPasteTargetDirectory(contextItem));
        }

        public static bool CanPasteStorageItems()
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
            ExplorerItem? contextItem = _selection.GetItem(sender);
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
            IReadOnlyList<ExplorerItem> selectedItems = _selection.GetSelectedItems(sender);
            if (selectedItems.Count == 0 || selectedItems.Any(item => !ExplorerItemCapabilities.CanUseLocalStorage(item)))
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

            string targetDir = targetItem.IsFolder
                ? targetItem.Path
                : Path.GetDirectoryName(targetItem.Path) ?? string.Empty;
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

            return ExplorerSelectionService.GetTreeItem(hitItem.DataContext)
                ?? ExplorerSelectionService.GetTreeItem(hitItem.Content);
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
                        XamlRoot = _dialogs.XamlRoot,
                        RequestedTheme = _dialogs.Theme
                    };

                    if (await _dialogs.ShowAsync(confirmDialog) != ContentDialogResult.Primary)
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

            if (Directory.Exists(sourcePath))
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
                        XamlRoot = _dialogs.XamlRoot,
                        RequestedTheme = _dialogs.Theme
                    };

                    if (await _dialogs.ShowAsync(confirmDialog) != ContentDialogResult.Primary)
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
