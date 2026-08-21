using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TxtAIEditor.Core.Services;
using TxtAIEditor.ViewModels;

namespace TxtAIEditor.Controls
{
    internal sealed class ExplorerArchiveActionsController
    {
        private readonly LeftSidebarPane _leftSidebar;
        private readonly StatusBarPane _statusBar;
        private readonly MainWindowViewModel _viewModel;
        private readonly ArchiveExplorerService _archiveExplorerService;
        private readonly ExplorerSelectionService _selection;
        private readonly ExplorerDialogCoordinator _dialogs;
        private readonly Func<string> _currentFolderProvider;
        private readonly Action<string> _loadDirectoryRoot;
        private readonly Func<string, string, string> _getString;
        private readonly Action<string, string> _showError;

        private System.Threading.CancellationTokenSource? _archiveCts;

        public ExplorerArchiveActionsController(
            LeftSidebarPane leftSidebar,
            StatusBarPane statusBar,
            MainWindowViewModel viewModel,
            ArchiveExplorerService archiveExplorerService,
            ExplorerSelectionService selection,
            ExplorerDialogCoordinator dialogs,
            Func<string> currentFolderProvider,
            Action<string> loadDirectoryRoot,
            Func<string, string, string> getString,
            Action<string, string> showError)
        {
            _leftSidebar = leftSidebar;
            _statusBar = statusBar;
            _viewModel = viewModel;
            _archiveExplorerService = archiveExplorerService;
            _selection = selection;
            _dialogs = dialogs;
            _currentFolderProvider = currentFolderProvider;
            _loadDirectoryRoot = loadDirectoryRoot;
            _getString = getString;
            _showError = showError;
        }

        public void WireEvents()
        {
            _leftSidebar.ExtractArchiveToFolderClick += OnExtractArchiveToFolderClick;
            _leftSidebar.CompressFolderToZipClick += OnCompressFolderToZipClick;
            _leftSidebar.CompressFolderToSevenZipClick += OnCompressFolderToSevenZipClick;
        }

        private async void OnExtractArchiveToFolderClick(object sender, RoutedEventArgs e)
        {
            var item = _selection.GetItem(sender);
            if (!ExplorerItemCapabilities.IsSupportedArchive(item))
            {
                return;
            }

            string archivePath = item!.Path;
            string parentDirectory = Path.GetDirectoryName(archivePath) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(parentDirectory) || !Directory.Exists(parentDirectory))
            {
                return;
            }

            string targetDirectory = Path.Combine(
                parentDirectory,
                ExplorerItemCapabilities.GetArchiveExtractFolderName(archivePath));
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
                        XamlRoot = _dialogs.XamlRoot,
                        RequestedTheme = _dialogs.Theme
                    };

                    if (await _dialogs.ShowAsync(confirmDialog) != ContentDialogResult.Primary)
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
            IReadOnlyList<ExplorerItem> selectedItems = _selection.GetSelectedItems(sender);
            if (selectedItems.Count == 0 || !selectedItems.All(ExplorerItemCapabilities.CanCompress))
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
    }
}
