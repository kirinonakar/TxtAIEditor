using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Services;
using TxtAIEditor.Editor;
using TxtAIEditor.ViewModels;

namespace TxtAIEditor.Controls
{
    public sealed class FileTabLoadController
    {
        public Func<bool>? PreserveWorkspaceOnFileOpenProvider { get; set; }

        private readonly IGitService _gitService;
        private readonly SecureNoteEncryptionService _secureNoteEncryptionService;
        private readonly ArchiveExplorerService _archiveExplorerService;
        private readonly MainWindowViewModel _viewModel;
        private readonly TabView _editorTabView;
        private readonly TabView _editorTabView2;
        private readonly Dictionary<string, (WebView2 WebView, MonacoBridge Bridge)> _tabBridges;
        private readonly Action<string> _currentRepoPathChanged;
        private readonly Func<string, string, string> _getString;
        private readonly Func<string, string, Task<string?>> _promptPasswordAsync;
        private readonly Func<FileTabOpenRequest, OpenedTab> _openNewTab;
        private readonly Func<string, OpenedTab> _openImageTab;
        private readonly Func<string, OpenedTab> _openMediaTab;
        private readonly Func<string, OpenedTab> _openPdfTab;
        private readonly Func<string, OpenedTab> _openOfficeDocumentTab;
        private readonly Func<string, OpenedTab> _openHexTab;
        private readonly Action _queueGitStatusRefresh;
        private readonly Action<string, string> _showErrorMessage;
        private readonly SemaphoreSlim _fileOpenSemaphore = new(1, 1);

        public FileTabLoadController(
            IGitService gitService,
            SecureNoteEncryptionService secureNoteEncryptionService,
            ArchiveExplorerService archiveExplorerService,
            MainWindowViewModel viewModel,
            TabView editorTabView,
            TabView editorTabView2,
            Dictionary<string, (WebView2 WebView, MonacoBridge Bridge)> tabBridges,
            Action<string> currentRepoPathChanged,
            Func<string, string, string> getString,
            Func<string, string, Task<string?>> promptPasswordAsync,
            Func<FileTabOpenRequest, OpenedTab> openNewTab,
            Func<string, OpenedTab> openImageTab,
            Func<string, OpenedTab> openMediaTab,
            Func<string, OpenedTab> openPdfTab,
            Func<string, OpenedTab> openOfficeDocumentTab,
            Func<string, OpenedTab> openHexTab,
            Action queueGitStatusRefresh,
            Action<string, string> showErrorMessage)
        {
            _gitService = gitService;
            _secureNoteEncryptionService = secureNoteEncryptionService;
            _archiveExplorerService = archiveExplorerService;
            _viewModel = viewModel;
            _editorTabView = editorTabView;
            _editorTabView2 = editorTabView2;
            _tabBridges = tabBridges;
            _currentRepoPathChanged = currentRepoPathChanged;
            _getString = getString;
            _promptPasswordAsync = promptPasswordAsync;
            _openNewTab = openNewTab;
            _openImageTab = openImageTab;
            _openMediaTab = openMediaTab;
            _openPdfTab = openPdfTab;
            _openOfficeDocumentTab = openOfficeDocumentTab;
            _openHexTab = openHexTab;
            _queueGitStatusRefresh = queueGitStatusRefresh;
            _showErrorMessage = showErrorMessage;
        }

        public async Task<OpenedTab?> LoadAsync(string filePath)
        {
            var result = await LoadWithResultAsync(filePath);
            return result.Tab;
        }

        public async Task<FileTabLoadResult> LoadWithResultAsync(string filePath)
        {
            await _fileOpenSemaphore.WaitAsync();
            try
            {
                string? repoRoot = _gitService.FindRepositoryRoot(Path.GetDirectoryName(filePath));
                if (PreserveWorkspaceOnFileOpenProvider?.Invoke() != true && !string.IsNullOrEmpty(repoRoot))
                {
                    _currentRepoPathChanged(repoRoot);
                }

                var existingTab = FocusExistingTab(filePath);
                if (existingTab != null)
                {
                    QueueGitRefreshIfNeeded(repoRoot);
                    return FileTabLoadResult.ActivatedExisting(existingTab);
                }

                string extension = Path.GetExtension(filePath);
                if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    var tab = _openHexTab(filePath);
                    QueueGitRefreshIfNeeded(repoRoot);
                    return FileTabLoadResult.Opened(tab);
                }

                if (SupportedFileTypes.IsImageFile(filePath))
                {
                    var tab = _openImageTab(filePath);
                    QueueGitRefreshIfNeeded(repoRoot);
                    return FileTabLoadResult.Opened(tab);
                }

                if (SupportedFileTypes.IsMediaFile(filePath))
                {
                    var tab = _openMediaTab(filePath);
                    QueueGitRefreshIfNeeded(repoRoot);
                    return FileTabLoadResult.Opened(tab);
                }

                if (SupportedFileTypes.IsPdfFile(filePath))
                {
                    var tab = _openPdfTab(filePath);
                    QueueGitRefreshIfNeeded(repoRoot);
                    return FileTabLoadResult.Opened(tab);
                }

                if (SupportedFileTypes.IsOfficeDocumentFile(filePath))
                {
                    var tab = _openOfficeDocumentTab(filePath);
                    QueueGitRefreshIfNeeded(repoRoot);
                    return FileTabLoadResult.Opened(tab);
                }

                if (SupportedFileTypes.IsReadOnlyDocumentFile(filePath))
                {
                    var tab = await OpenReadOnlyDocumentFileAsync(filePath);
                    QueueGitRefreshIfNeeded(repoRoot);
                    return FileTabLoadResult.Opened(tab);
                }

                bool isEncrypted = await _secureNoteEncryptionService.IsSecureNoteFileAsync(filePath);
                if (isEncrypted)
                {
                    var tab = await OpenEncryptedFileAsync(filePath);
                    if (tab != null)
                    {
                        QueueGitRefreshIfNeeded(repoRoot);
                        return FileTabLoadResult.Opened(tab);
                    }

                    return FileTabLoadResult.Failed(filePath, "opening encrypted file was cancelled.");
                }

                var readResult = await LineArrayTextModel.LoadFromFileAsync(filePath, "Auto");
                var openedTab = _openNewTab(new FileTabOpenRequest
                {
                    FilePath = filePath,
                    Content = "",
                    EncodingName = readResult.EncodingName,
                    EncodingWasAutoDetected = readResult.EncodingWasAutoDetected,
                    TextModel = readResult.Model
                });

                QueueGitRefreshIfNeeded(repoRoot);
                return FileTabLoadResult.Opened(openedTab);
            }
            catch (Exception ex)
            {
                _showErrorMessage(_getString("FileLoadErrorTitle", "파일 로드 에러"), ex.Message);
                return FileTabLoadResult.Failed(filePath, ex.Message);
            }
            finally
            {
                _fileOpenSemaphore.Release();
            }
        }

        public async Task<OpenedTab?> LoadArchiveEntryAsync(string archivePath, string entryPath)
        {
            var result = await LoadArchiveEntryWithResultAsync(archivePath, entryPath);
            return result.Tab;
        }

        public async Task<FileTabLoadResult> LoadArchiveEntryWithResultAsync(string archivePath, string entryPath)
        {
            await _fileOpenSemaphore.WaitAsync();
            try
            {
                string normalizedEntryPath = ArchiveExplorerService.NormalizeEntryPath(entryPath);
                string virtualPath = ArchiveExplorerService.CreateVirtualPath(archivePath, normalizedEntryPath);
                if (string.IsNullOrWhiteSpace(archivePath) ||
                    string.IsNullOrWhiteSpace(normalizedEntryPath) ||
                    !File.Exists(archivePath))
                {
                    return FileTabLoadResult.Failed(virtualPath, "archive entry path is invalid.");
                }

                var existingTab = FocusExistingArchiveEntryTab(archivePath, normalizedEntryPath);
                if (existingTab != null)
                {
                    return FileTabLoadResult.ActivatedExisting(existingTab);
                }

                if (RequiresExtractedViewer(normalizedEntryPath))
                {
                    string cachePath = await _archiveExplorerService.ExtractEntryToCacheFileAsync(archivePath, normalizedEntryPath);
                    OpenedTab viewerTab = OpenViewerTab(cachePath);
                    ApplyArchiveEntryTabState(viewerTab, archivePath, normalizedEntryPath, isReadOnlyTextFile: false);
                    return FileTabLoadResult.Opened(viewerTab);
                }

                byte[] bytes = await _archiveExplorerService.ReadEntryBytesAsync(archivePath, normalizedEntryPath);
                EditorDocumentLoadResult readResult = LoadTextEntry(bytes);
                OpenedTab textTab = _openNewTab(new FileTabOpenRequest
                {
                    FilePath = virtualPath,
                    Content = string.Empty,
                    IsReadOnly = true,
                    EncodingName = readResult.EncodingName,
                    EncodingWasAutoDetected = readResult.EncodingWasAutoDetected,
                    TextModel = readResult.Model
                });

                ApplyArchiveEntryTabState(textTab, archivePath, normalizedEntryPath, isReadOnlyTextFile: true);
                return FileTabLoadResult.Opened(textTab);
            }
            catch (Exception ex)
            {
                string normalizedEntryPath = ArchiveExplorerService.NormalizeEntryPath(entryPath);
                string virtualPath = ArchiveExplorerService.CreateVirtualPath(archivePath, normalizedEntryPath);
                _showErrorMessage(_getString("ArchiveEntryOpenFailedTitle", "압축 파일 항목 열기 실패"), ex.Message);
                return FileTabLoadResult.Failed(virtualPath, ex.Message);
            }
            finally
            {
                _fileOpenSemaphore.Release();
            }
        }

        private async Task<OpenedTab?> OpenEncryptedFileAsync(string filePath)
        {
            string? password = await _promptPasswordAsync(
                _getString("EncryptionPasswordDialogTitle", "암호 입력"),
                _getString("EncryptionOpenButton", "열기"));
            if (password == null)
            {
                return null;
            }

            string decryptedText = await _secureNoteEncryptionService.DecryptFileAsync(filePath, password);
            return _openNewTab(new FileTabOpenRequest
            {
                FilePath = filePath,
                Content = decryptedText,
                EncodingName = "UTF-8",
                EncodingWasAutoDetected = false,
                TextModel = TextModelFactory.FromText(decryptedText),
                IsEncrypted = true,
                EncryptionPassword = password
            });
        }

        private OpenedTab? FocusExistingTab(string filePath)
        {
            string extension = Path.GetExtension(filePath);
            bool isHexDefault = extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                                extension.Equals(".dll", StringComparison.OrdinalIgnoreCase);
            var existingTab = _viewModel.Tabs.FirstOrDefault(t =>
                isHexDefault
                    ? (t.IsHexViewer && string.Equals(t.HexSourceFilePath, filePath, StringComparison.OrdinalIgnoreCase))
                    : string.Equals(t.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
            if (existingTab == null)
            {
                return null;
            }

            FocusTab(existingTab);
            return existingTab;
        }

        private OpenedTab? FocusExistingArchiveEntryTab(string archivePath, string entryPath)
        {
            string normalizedArchivePath = NormalizePathForComparison(archivePath);
            string normalizedEntryPath = ArchiveExplorerService.NormalizeEntryPath(entryPath);
            var existingTab = _viewModel.Tabs.FirstOrDefault(t =>
                t.IsArchiveEntry &&
                !string.IsNullOrWhiteSpace(t.ArchiveSourcePath) &&
                !string.IsNullOrWhiteSpace(t.ArchiveEntryPath) &&
                string.Equals(NormalizePathForComparison(t.ArchiveSourcePath), normalizedArchivePath, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(ArchiveExplorerService.NormalizeEntryPath(t.ArchiveEntryPath), normalizedEntryPath, StringComparison.Ordinal));

            if (existingTab == null)
            {
                return null;
            }

            FocusTab(existingTab);
            return existingTab;
        }

        private void FocusTab(OpenedTab existingTab)
        {
            TabViewItem? tabItem = FindTabItem(_editorTabView, existingTab.Id);
            if (tabItem != null)
            {
                _editorTabView.SelectedItem = tabItem;
            }
            else
            {
                tabItem = FindTabItem(_editorTabView2, existingTab.Id);
                if (tabItem != null)
                {
                    _editorTabView2.SelectedItem = tabItem;
                }
            }

            if (tabItem != null && _tabBridges.TryGetValue(existingTab.Id, out var bridgeGroup) && bridgeGroup.Bridge != null)
            {
                bridgeGroup.WebView.Focus(FocusState.Programmatic);
                _ = bridgeGroup.Bridge.FocusAsync();
            }
        }

        private static TabViewItem? FindTabItem(TabView tabView, string tabId)
        {
            return tabView.TabItems
                .Cast<TabViewItem>()
                .FirstOrDefault(t => string.Equals(t.Tag as string, tabId, StringComparison.Ordinal));
        }

        private async Task<OpenedTab> OpenReadOnlyDocumentFileAsync(string filePath)
        {
            var documentService = new DocumentTextExtractionService();
            string extractedText = await documentService.ExtractTextAsync(filePath, 50_000_000);
            return _openNewTab(new FileTabOpenRequest
            {
                FilePath = filePath,
                Content = extractedText,
                EncodingName = "UTF-8",
                EncodingWasAutoDetected = false,
                TextModel = TextModelFactory.FromText(extractedText),
                IsReadOnly = true
            });
        }

        private void QueueGitRefreshIfNeeded(string? repoRoot)
        {
            if (!string.IsNullOrEmpty(repoRoot))
            {
                _queueGitStatusRefresh();
            }
        }

        private OpenedTab OpenViewerTab(string filePath)
        {
            string extension = Path.GetExtension(filePath);
            if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".dll", StringComparison.OrdinalIgnoreCase))
            {
                return _openHexTab(filePath);
            }

            if (SupportedFileTypes.IsImageFile(filePath))
            {
                return _openImageTab(filePath);
            }

            if (SupportedFileTypes.IsMediaFile(filePath))
            {
                return _openMediaTab(filePath);
            }

            if (SupportedFileTypes.IsPdfFile(filePath))
            {
                return _openPdfTab(filePath);
            }

            return _openOfficeDocumentTab(filePath);
        }

        private static bool RequiresExtractedViewer(string entryPath)
        {
            string extension = Path.GetExtension(entryPath);
            return SupportedFileTypes.IsImageFile(entryPath) ||
                   SupportedFileTypes.IsMediaFile(entryPath) ||
                   SupportedFileTypes.IsPdfFile(entryPath) ||
                   SupportedFileTypes.IsOfficeDocumentFile(entryPath) ||
                   extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".dll", StringComparison.OrdinalIgnoreCase);
        }

        private static EditorDocumentLoadResult LoadTextEntry(byte[] bytes)
        {
            const int sampleSize = 128 * 1024;
            int sampleLength = Math.Min(bytes.Length, sampleSize);
            byte[] sample = bytes.AsSpan(0, sampleLength).ToArray();
            Encoding encoding = TextEncodingService.GetTextEncoding(sample, "Auto");
            string text = encoding.GetString(bytes);

            return new EditorDocumentLoadResult(
                TextModelFactory.FromText(text),
                TextEncodingService.GetDisplayName(encoding, TextEncodingService.HasUtf8Bom(sample)),
                EncodingWasAutoDetected: true);
        }

        private static void ApplyArchiveEntryTabState(
            OpenedTab tab,
            string archivePath,
            string entryPath,
            bool isReadOnlyTextFile)
        {
            tab.ArchiveSourcePath = archivePath;
            tab.ArchiveEntryPath = ArchiveExplorerService.NormalizeEntryPath(entryPath);
            tab.IsReadOnlyTextFile = isReadOnlyTextFile;

            string title = Path.GetFileName(tab.ArchiveEntryPath.Replace('/', Path.DirectorySeparatorChar));
            if (!string.IsNullOrWhiteSpace(title))
            {
                tab.Title = title;
            }
        }

        private static string NormalizePathForComparison(string path)
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return path;
            }
        }
    }

    public sealed class FileTabOpenRequest
    {
        public string? FilePath { get; set; }
        public string Content { get; set; } = "";
        public bool IsReadOnly { get; set; }
        public string EncodingName { get; set; } = "UTF-8";
        public bool EncodingWasAutoDetected { get; set; } = true;
        public ITextModel? TextModel { get; set; }
        public bool IsEncrypted { get; set; }
        public string? EncryptionPassword { get; set; }
    }

    public sealed class FileTabLoadResult
    {
        public OpenedTab? Tab { get; init; }
        public bool Success => Tab != null;
        public bool ActivatedExistingTab { get; init; }
        public string FullPath { get; init; } = string.Empty;
        public string? ErrorMessage { get; init; }

        public static FileTabLoadResult Opened(OpenedTab tab)
        {
            return new FileTabLoadResult
            {
                Tab = tab,
                FullPath = tab.FilePath ?? string.Empty
            };
        }

        public static FileTabLoadResult ActivatedExisting(OpenedTab tab)
        {
            return new FileTabLoadResult
            {
                Tab = tab,
                ActivatedExistingTab = true,
                FullPath = tab.FilePath ?? string.Empty
            };
        }

        public static FileTabLoadResult Failed(string fullPath, string errorMessage)
        {
            return new FileTabLoadResult
            {
                FullPath = fullPath,
                ErrorMessage = errorMessage
            };
        }
    }
}
