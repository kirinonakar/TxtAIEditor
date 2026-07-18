using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace TxtAIEditor.Controls
{
    public sealed class FileOpenDropController
    {
        private readonly FrameworkElement _dragOverlay;
        private readonly FrameworkElement _leftSidebar;
        private readonly FrameworkElement _rightSidebar;
        private readonly Action<object> _initializePickerWindow;
        private readonly Func<string, Task> _loadFileIntoTabAsync;
        private readonly Func<string, bool, Task> _navigateExplorerToFolderAsync;
        private readonly Func<bool> _isExplorerTreeMode;
        private readonly Func<bool> _isLeftSidebarVisible;
        private readonly Action<string, string> _showError;
        private readonly Func<string, string, string> _getString;

        public FileOpenDropController(
            FrameworkElement dragOverlay,
            FrameworkElement leftSidebar,
            FrameworkElement rightSidebar,
            Action<object> initializePickerWindow,
            Func<string, Task> loadFileIntoTabAsync,
            Func<string, bool, Task> navigateExplorerToFolderAsync,
            Func<bool> isExplorerTreeMode,
            Func<bool> isLeftSidebarVisible,
            Action<string, string> showError,
            Func<string, string, string> getString)
        {
            _dragOverlay = dragOverlay;
            _leftSidebar = leftSidebar;
            _rightSidebar = rightSidebar;
            _initializePickerWindow = initializePickerWindow;
            _loadFileIntoTabAsync = loadFileIntoTabAsync;
            _navigateExplorerToFolderAsync = navigateExplorerToFolderAsync;
            _isExplorerTreeMode = isExplorerTreeMode;
            _isLeftSidebarVisible = isLeftSidebarVisible;
            _showError = showError;
            _getString = getString;
        }

        public async Task OpenFileAsync()
        {
            var picker = new FileOpenPicker();
            _initializePickerWindow(picker);
            picker.ViewMode = PickerViewMode.List;
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;

            foreach (string extension in SupportedFileTypes.PickerFileExtensions)
            {
                picker.FileTypeFilter.Add(extension);
            }

            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                await _loadFileIntoTabAsync(file.Path);
            }
        }

        private bool IsHoveringOverLeftSidebar(DragEventArgs e)
        {
            if (!_isLeftSidebarVisible())
            {
                return false;
            }

            if (_leftSidebar is LeftSidebarPane sidebarPane)
            {
                if (sidebarPane.ExplorerActivity?.IsChecked != true)
                {
                    return false;
                }
            }

            try
            {
                var pos = e.GetPosition(_leftSidebar);
                return pos.X >= 0 && pos.X <= _leftSidebar.ActualWidth &&
                       pos.Y >= 0 && pos.Y <= _leftSidebar.ActualHeight;
            }
            catch
            {
                return false;
            }
        }

        private bool IsHoveringOverRightSidebar(DragEventArgs e)
        {
            if (_rightSidebar == null || _rightSidebar.Visibility != Visibility.Visible)
            {
                return false;
            }

            try
            {
                var pos = e.GetPosition(_rightSidebar);
                return pos.X >= 0 && pos.X <= _rightSidebar.ActualWidth &&
                       pos.Y >= 0 && pos.Y <= _rightSidebar.ActualHeight;
            }
            catch
            {
                return false;
            }
        }

        private bool IsHoveringOverSidebar(DragEventArgs e)
        {
            return IsHoveringOverLeftSidebar(e) || IsHoveringOverRightSidebar(e);
        }

        public void HandleRootDragOver(DragEventArgs e)
        {
            if (e.Handled)
            {
                return;
            }

            if (IsHoveringOverSidebar(e))
            {
                _dragOverlay.Visibility = Visibility.Collapsed;
                e.AcceptedOperation = DataPackageOperation.None;
                return;
            }

            e.Handled = true;

            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                ApplyFileOpenDragUi(e);
                _dragOverlay.Visibility = Visibility.Visible;
            }
            else
            {
                e.AcceptedOperation = DataPackageOperation.None;
            }
        }

        public void HandleDragOverlayOver(DragEventArgs e)
        {
            if (IsHoveringOverSidebar(e))
            {
                _dragOverlay.Visibility = Visibility.Collapsed;
                e.AcceptedOperation = DataPackageOperation.None;
                return;
            }

            ApplyFileOpenDragUi(e);
        }

        public async Task HandleDragOverlayDropAsync(DragEventArgs e)
        {
            if (IsHoveringOverRightSidebar(e))
            {
                _dragOverlay.Visibility = Visibility.Collapsed;
                return;
            }

            _dragOverlay.Visibility = Visibility.Collapsed;
            await HandleRootDropAsync(e);
        }

        public void HandleDragOverlayLeave()
        {
            _dragOverlay.Visibility = Visibility.Collapsed;
        }

        public async Task HandleRootDropAsync(DragEventArgs e)
        {
            if (e.Handled)
            {
                return;
            }

            if (IsHoveringOverRightSidebar(e))
            {
                return;
            }

            e.Handled = true;
            e.AcceptedOperation = DataPackageOperation.Copy;
            _dragOverlay.Visibility = Visibility.Collapsed;

            if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                return;
            }

            try
            {
                var items = await e.DataView.GetStorageItemsAsync();
                foreach (var item in items)
                {
                    if (string.IsNullOrWhiteSpace(item.Path))
                    {
                        continue;
                    }

                    if (File.Exists(item.Path))
                    {
                        string? folderPath = Path.GetDirectoryName(item.Path);
                        if (!_isExplorerTreeMode() && !string.IsNullOrWhiteSpace(folderPath) && Directory.Exists(folderPath))
                        {
                            await _navigateExplorerToFolderAsync(folderPath, _isLeftSidebarVisible());
                        }

                        await _loadFileIntoTabAsync(item.Path);
                    }
                    else if (Directory.Exists(item.Path))
                    {
                        await _navigateExplorerToFolderAsync(item.Path, true);
                    }
                }
            }
            catch (Exception ex)
            {
                _showError(_getString("DragDropErrorTitle", "드래그 앤 드롭 오류"), ex.Message);
            }
            finally
            {
                _dragOverlay.Visibility = Visibility.Collapsed;
            }
        }

        private void ApplyFileOpenDragUi(DragEventArgs e)
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = _getString("DragDropOpenFileCaption", "파일 열기");
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsContentVisible = true;
        }
    }
}
