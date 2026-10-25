using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Services;

namespace TxtAIEditor.Controls
{
    internal sealed class ExplorerRemoteTransferController
    {
        private readonly LeftSidebarPane _leftSidebar;
        private readonly StatusBarPane _statusBar;
        private readonly RemoteWorkspaceService _remoteWorkspaceService;
        private readonly ExplorerSelectionService _selection;
        private readonly Func<Task> _refreshRemoteExplorerAsync;
        private readonly Action<object> _initializePickerWindow;
        private readonly Func<string>? _homeFolderPathProvider;
        private readonly Func<string, string, string> _getString;
        private readonly Action<string, string> _showError;

        public ExplorerRemoteTransferController(
            LeftSidebarPane leftSidebar,
            StatusBarPane statusBar,
            RemoteWorkspaceService remoteWorkspaceService,
            ExplorerSelectionService selection,
            Func<Task> refreshRemoteExplorerAsync,
            Action<object> initializePickerWindow,
            Func<string>? homeFolderPathProvider,
            Func<string, string, string> getString,
            Action<string, string> showError)
        {
            _leftSidebar = leftSidebar;
            _statusBar = statusBar;
            _remoteWorkspaceService = remoteWorkspaceService;
            _selection = selection;
            _refreshRemoteExplorerAsync = refreshRemoteExplorerAsync;
            _initializePickerWindow = initializePickerWindow;
            _homeFolderPathProvider = homeFolderPathProvider;
            _getString = getString;
            _showError = showError;
        }

        public void WireEvents()
        {
            _leftSidebar.DownloadRemoteItemClick += OnDownloadRemoteItemClick;
            _leftSidebar.UploadRemoteItemClick += OnUploadRemoteItemClick;
        }

        private async void OnDownloadRemoteItemClick(object sender, RoutedEventArgs e)
        {
            var selectedItems = _selection.GetSelectedItems(sender)
                .Where(ExplorerItemCapabilities.IsRemote)
                .ToList();
            selectedItems = RemoveItemsCoveredBySelectedFolders(selectedItems);
            if (selectedItems.Count == 0)
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
            int totalItems = selectedItems.Count;
            double[] itemProgress = new double[totalItems];
            int[] itemTotalFiles = Enumerable.Repeat(1, totalItems).ToArray();
            int[] itemCompletedFiles = new int[totalItems];
            bool showFileName = totalItems == 1 && !selectedItems[0].IsFolder;
            string? lastStatusText = null;
            int lastDisplayedPercent = -1;
            object progressSync = new();

            try
            {
                Task[] downloadTasks = selectedItems.Select(async (item, index) =>
                {
                    cts.Token.ThrowIfCancellationRequested();
                    try
                    {
                        await _remoteWorkspaceService.DownloadRemoteItemToFolderAsync(
                            item.Path,
                            item.IsFolder,
                            targetLocalDirectory,
                            (currentFile, remainingFiles, totalFiles, percent) =>
                            {
                                double boundedPercent = Math.Clamp(percent, 0.0, 100.0);
                                double currentItemProgress = totalFiles <= 0
                                    ? 100.0
                                    : (totalFiles - remainingFiles +
                                        (boundedPercent >= 100.0 ? 0.0 : boundedPercent / 100.0)) /
                                        totalFiles * 100.0;
                                lock (progressSync)
                                {
                                    if (isCompleted || cts.IsCancellationRequested)
                                    {
                                        return;
                                    }

                                    itemProgress[index] = Math.Max(
                                        itemProgress[index],
                                        Math.Clamp(currentItemProgress, 0.0, 100.0));
                                    itemTotalFiles[index] = Math.Max(0, totalFiles);
                                    itemCompletedFiles[index] = Math.Max(
                                        itemCompletedFiles[index],
                                        Math.Clamp(totalFiles - remainingFiles, 0, itemTotalFiles[index]));
                                    double overallProgress = itemProgress.Average();
                                    int totalFileCount = itemTotalFiles.Sum();
                                    string statusText = FormatRemoteTransferStatus(
                                        downloadStatusPrefix,
                                        item.Name,
                                        totalFileCount - itemCompletedFiles.Sum(),
                                        totalFileCount,
                                        showFileName);
                                    int displayedPercent = (int)overallProgress;
                                    if (statusText == lastStatusText && displayedPercent == lastDisplayedPercent)
                                    {
                                        return;
                                    }

                                    lastStatusText = statusText;
                                    lastDisplayedPercent = displayedPercent;
                                    _statusBar.ShowProgress(statusText, overallProgress, () => cts.Cancel());
                                }
                            },
                            cts.Token);
                    }
                    catch
                    {
                        cts.Cancel();
                        throw;
                    }
                }).ToArray();

                await Task.WhenAll(downloadTasks);
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
                lock (progressSync)
                {
                    isCompleted = true;
                    _statusBar.HideProgress();
                }
            }
        }

        private async void OnUploadRemoteItemClick(object sender, RoutedEventArgs e)
        {
            ExplorerItem? item = _selection.GetItem(sender);
            if (!ExplorerItemCapabilities.IsRemote(item))
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

            string targetDirectory = item!.IsFolder
                ? item.Path
                : RemotePath.GetParent(item.Path);
            if (string.IsNullOrWhiteSpace(targetDirectory))
            {
                return;
            }

            using System.Threading.CancellationTokenSource cts = new();
            string uploadStatusPrefix = _getString("RemoteUploadingStatus", "업로드 중...");
            bool isCompleted = false;
            object progressSync = new();

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
                double[] fileProgress = new double[totalFiles];
                string lastStatusText = FormatRemoteTransferStatus(
                    uploadStatusPrefix,
                    uploadFiles[0].Name,
                    totalFiles,
                    totalFiles,
                    showFileName: totalFiles == 1);
                int lastDisplayedPercent = 0;
                _statusBar.ShowProgress(
                    lastStatusText,
                    0,
                    () => cts.Cancel());

                Task[] uploadTasks = uploadFiles.Select(async (file, index) =>
                {
                    cts.Token.ThrowIfCancellationRequested();
                    string targetVirtualPath = RemotePath.Combine(
                        targetDirectory,
                        file.Name);
                    try
                    {
                        await _remoteWorkspaceService.UploadLocalFileAsync(
                            file.Path,
                            targetVirtualPath,
                            percent =>
                            {
                                lock (progressSync)
                                {
                                    if (isCompleted || cts.IsCancellationRequested)
                                    {
                                        return;
                                    }

                                    fileProgress[index] = Math.Max(
                                        fileProgress[index],
                                        Math.Clamp(percent, 0.0, 100.0));
                                    double overallProgress = fileProgress.Average();
                                    int remainingFiles = fileProgress.Count(value => value < 100.0);
                                    string statusText = FormatRemoteTransferStatus(
                                        uploadStatusPrefix,
                                        file.Name,
                                        remainingFiles,
                                        totalFiles,
                                        showFileName: totalFiles == 1);
                                    int displayedPercent = (int)overallProgress;
                                    if (statusText == lastStatusText && displayedPercent == lastDisplayedPercent)
                                    {
                                        return;
                                    }

                                    lastStatusText = statusText;
                                    lastDisplayedPercent = displayedPercent;
                                    _statusBar.ShowProgress(statusText, overallProgress, () => cts.Cancel());
                                }
                            },
                            cts.Token);
                    }
                    catch
                    {
                        cts.Cancel();
                        throw;
                    }
                }).ToArray();

                await Task.WhenAll(uploadTasks);

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
                lock (progressSync)
                {
                    isCompleted = true;
                    _statusBar.HideProgress();
                }
            }
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

        private static List<ExplorerItem> RemoveItemsCoveredBySelectedFolders(
            IReadOnlyList<ExplorerItem> selectedItems)
        {
            var parsedItems = new List<(ExplorerItem Item, Guid ServerId, string RemotePath)>();
            foreach (ExplorerItem item in selectedItems)
            {
                if (RemotePath.TryParse(item.Path, out Guid serverId, out string remotePath))
                {
                    parsedItems.Add((item, serverId, remotePath));
                }
            }

            var selectedFolders = parsedItems
                .Where(candidate => candidate.Item.IsFolder)
                .ToList();

            return selectedItems
                .Where(item =>
                {
                    var candidate = parsedItems.FirstOrDefault(parsed => ReferenceEquals(parsed.Item, item));
                    if (candidate.Item == null)
                    {
                        return true;
                    }

                    return !selectedFolders.Any(folder =>
                        !ReferenceEquals(folder.Item, item) &&
                        folder.ServerId == candidate.ServerId &&
                        IsRemoteDescendant(candidate.RemotePath, folder.RemotePath));
                })
                .ToList();
        }

        private static bool IsRemoteDescendant(string candidatePath, string folderPath)
        {
            if (string.Equals(candidatePath, folderPath, StringComparison.Ordinal))
            {
                return false;
            }

            string descendantPrefix = string.Equals(folderPath, "/", StringComparison.Ordinal)
                ? "/"
                : folderPath.TrimEnd('/') + "/";
            return candidatePath.StartsWith(descendantPrefix, StringComparison.Ordinal);
        }

        private string FormatRemoteTransferStatus(
            string statusPrefix,
            string currentFile,
            int remainingFiles,
            int totalFiles,
            bool showFileName)
        {
            if (showFileName)
            {
                return $"{statusPrefix} ({AbbreviateFileName(currentFile)})";
            }

            string countText = string.Format(
                _getString(
                    "RemoteTransferFileCountFormat",
                    "파일 {0:N0}/{1:N0}개 완료"),
                Math.Clamp(totalFiles - remainingFiles, 0, totalFiles),
                totalFiles);
            return $"{statusPrefix} ({countText})";
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
    }
}
