using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        private readonly IGitService _gitService;
        private readonly SecureNoteEncryptionService _secureNoteEncryptionService;
        private readonly MainWindowViewModel _viewModel;
        private readonly TabView _editorTabView;
        private readonly TabView _editorTabView2;
        private readonly Dictionary<string, (WebView2 WebView, MonacoBridge Bridge)> _tabBridges;
        private readonly Action<string> _currentRepoPathChanged;
        private readonly Func<string, string, string> _getString;
        private readonly Func<string, string, Task<string?>> _promptPasswordAsync;
        private readonly Func<FileTabOpenRequest, OpenedTab> _openNewTab;
        private readonly Func<string, OpenedTab> _openImageTab;
        private readonly Func<string, OpenedTab> _openPdfTab;
        private readonly Func<string, OpenedTab> _openOfficeDocumentTab;
        private readonly Action _queueGitStatusRefresh;
        private readonly Action<string, string> _showErrorMessage;
        private readonly SemaphoreSlim _fileOpenSemaphore = new(1, 1);

        public FileTabLoadController(
            IGitService gitService,
            SecureNoteEncryptionService secureNoteEncryptionService,
            MainWindowViewModel viewModel,
            TabView editorTabView,
            TabView editorTabView2,
            Dictionary<string, (WebView2 WebView, MonacoBridge Bridge)> tabBridges,
            Action<string> currentRepoPathChanged,
            Func<string, string, string> getString,
            Func<string, string, Task<string?>> promptPasswordAsync,
            Func<FileTabOpenRequest, OpenedTab> openNewTab,
            Func<string, OpenedTab> openImageTab,
            Func<string, OpenedTab> openPdfTab,
            Func<string, OpenedTab> openOfficeDocumentTab,
            Action queueGitStatusRefresh,
            Action<string, string> showErrorMessage)
        {
            _gitService = gitService;
            _secureNoteEncryptionService = secureNoteEncryptionService;
            _viewModel = viewModel;
            _editorTabView = editorTabView;
            _editorTabView2 = editorTabView2;
            _tabBridges = tabBridges;
            _currentRepoPathChanged = currentRepoPathChanged;
            _getString = getString;
            _promptPasswordAsync = promptPasswordAsync;
            _openNewTab = openNewTab;
            _openImageTab = openImageTab;
            _openPdfTab = openPdfTab;
            _openOfficeDocumentTab = openOfficeDocumentTab;
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
                if (!string.IsNullOrEmpty(repoRoot))
                {
                    _currentRepoPathChanged(repoRoot);
                }

                var existingTab = FocusExistingTab(filePath);
                if (existingTab != null)
                {
                    QueueGitRefreshIfNeeded(repoRoot);
                    return FileTabLoadResult.ActivatedExisting(existingTab);
                }

                if (IsSupportedImageFile(filePath))
                {
                    var tab = _openImageTab(filePath);
                    QueueGitRefreshIfNeeded(repoRoot);
                    return FileTabLoadResult.Opened(tab);
                }

                if (IsPdfFile(filePath))
                {
                    var tab = _openPdfTab(filePath);
                    QueueGitRefreshIfNeeded(repoRoot);
                    return FileTabLoadResult.Opened(tab);
                }

                if (IsOfficeDocumentFile(filePath))
                {
                    var tab = _openOfficeDocumentTab(filePath);
                    QueueGitRefreshIfNeeded(repoRoot);
                    return FileTabLoadResult.Opened(tab);
                }

                if (IsReadOnlyDocumentFile(filePath))
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
                _showErrorMessage("파일 로드 에러", ex.Message);
                return FileTabLoadResult.Failed(filePath, ex.Message);
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
                TextModel = LineArrayTextModel.FromText(decryptedText),
                IsEncrypted = true,
                EncryptionPassword = password
            });
        }

        private OpenedTab? FocusExistingTab(string filePath)
        {
            var existingTab = _viewModel.Tabs.FirstOrDefault(t =>
                string.Equals(t.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
            if (existingTab == null)
            {
                return null;
            }

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

            return existingTab;
        }

        private static TabViewItem? FindTabItem(TabView tabView, string tabId)
        {
            return tabView.TabItems
                .Cast<TabViewItem>()
                .FirstOrDefault(t => string.Equals(t.Tag as string, tabId, StringComparison.Ordinal));
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

        private static bool IsPdfFile(string filePath)
        {
            return Path.GetExtension(filePath).Equals(".pdf", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsReadOnlyDocumentFile(string filePath)
        {
            string extension = Path.GetExtension(filePath);
            return extension.Equals(".docx", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".hwpx", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsOfficeDocumentFile(string filePath)
        {
            string extension = Path.GetExtension(filePath);
            return extension.Equals(".docx", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".hwpx", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".pptx", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase);
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
                TextModel = LineArrayTextModel.FromText(extractedText),
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
