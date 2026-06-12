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
        private readonly Action<object> _initializePickerWindow;
        private readonly Func<string, Task> _loadFileIntoTabAsync;
        private readonly Func<string, Task> _navigateExplorerToFolderAsync;
        private readonly Action<string, string> _showError;

        private static readonly string[] TextFileExtensions =
        {
            ".txt",
            ".md",
            ".markdown",
            ".csv",
            ".html",
            ".css",
            ".js",
            ".ts",
            ".cs",
            ".fs",
            ".vb",
            ".json",
            ".jsonc",
            ".tex",
            ".py",
            ".java",
            ".kt",
            ".swift",
            ".php",
            ".rb",
            ".rs",
            ".go",
            ".dart",
            ".lua",
            ".cpp",
            ".c",
            ".cc",
            ".cxx",
            ".h",
            ".hpp",
            ".xml",
            ".xaml",
            ".sql",
            ".sh",
            ".ps1",
            ".yaml",
            ".yml",
            ".toml",
            ".ini",
            ".diff",
            ".reg",
            ".png",
            ".jpg",
            ".jpeg",
            ".gif",
            ".bmp",
            ".webp",
            ".pdf",
            ".docx",
            ".hwpx"
        };

        public FileOpenDropController(
            FrameworkElement dragOverlay,
            Action<object> initializePickerWindow,
            Func<string, Task> loadFileIntoTabAsync,
            Func<string, Task> navigateExplorerToFolderAsync,
            Action<string, string> showError)
        {
            _dragOverlay = dragOverlay;
            _initializePickerWindow = initializePickerWindow;
            _loadFileIntoTabAsync = loadFileIntoTabAsync;
            _navigateExplorerToFolderAsync = navigateExplorerToFolderAsync;
            _showError = showError;
        }

        public async Task OpenFileAsync()
        {
            var picker = new FileOpenPicker();
            _initializePickerWindow(picker);
            picker.ViewMode = PickerViewMode.List;
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;

            foreach (string extension in TextFileExtensions)
            {
                picker.FileTypeFilter.Add(extension);
            }

            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                await _loadFileIntoTabAsync(file.Path);
            }
        }

        public void HandleRootDragOver(DragEventArgs e)
        {
            if (e.Handled)
            {
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
            ApplyFileOpenDragUi(e);
        }

        public async Task HandleDragOverlayDropAsync(DragEventArgs e)
        {
            _dragOverlay.Visibility = Visibility.Collapsed;
            await HandleRootDropAsync(e);
        }

        public void HandleDragOverlayLeave()
        {
            _dragOverlay.Visibility = Visibility.Collapsed;
        }

        public async Task HandleRootDropAsync(DragEventArgs e)
        {
            e.Handled = true;
            e.AcceptedOperation = DataPackageOperation.Copy;

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
                        if (!string.IsNullOrWhiteSpace(folderPath) && Directory.Exists(folderPath))
                        {
                            await _navigateExplorerToFolderAsync(folderPath);
                        }

                        await _loadFileIntoTabAsync(item.Path);
                    }
                    else if (Directory.Exists(item.Path))
                    {
                        await _navigateExplorerToFolderAsync(item.Path);
                    }
                }
            }
            catch (Exception ex)
            {
                _showError("드래그 앤 드롭 오류", ex.Message);
            }
        }

        private static void ApplyFileOpenDragUi(DragEventArgs e)
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "파일 열기";
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsContentVisible = true;
        }
    }
}
