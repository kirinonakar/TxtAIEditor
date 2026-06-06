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
            _queueGitStatusRefresh = queueGitStatusRefresh;
            _showErrorMessage = showErrorMessage;
        }

        public async Task LoadAsync(string filePath)
        {
            await _fileOpenSemaphore.WaitAsync();
            try
            {
                string? repoRoot = _gitService.FindRepositoryRoot(Path.GetDirectoryName(filePath));
                if (!string.IsNullOrEmpty(repoRoot))
                {
                    _currentRepoPathChanged(repoRoot);
                }

                if (FocusExistingTab(filePath))
                {
                    QueueGitRefreshIfNeeded(repoRoot);
                    return;
                }

                if (IsSupportedImageFile(filePath))
                {
                    _openImageTab(filePath);
                    QueueGitRefreshIfNeeded(repoRoot);
                    return;
                }

                if (IsPdfFile(filePath))
                {
                    _openPdfTab(filePath);
                    QueueGitRefreshIfNeeded(repoRoot);
                    return;
                }

                if (IsDocxFile(filePath))
                {
                    await OpenDocxFileAsync(filePath, repoRoot);
                    QueueGitRefreshIfNeeded(repoRoot);
                    return;
                }

                bool isEncrypted = await _secureNoteEncryptionService.IsSecureNoteFileAsync(filePath);
                if (isEncrypted)
                {
                    await OpenEncryptedFileAsync(filePath, repoRoot);
                    return;
                }

                var readResult = await LineArrayTextModel.LoadFromFileAsync(filePath, "Auto");
                _openNewTab(new FileTabOpenRequest
                {
                    FilePath = filePath,
                    Content = "",
                    EncodingName = readResult.EncodingName,
                    EncodingWasAutoDetected = readResult.EncodingWasAutoDetected,
                    TextModel = readResult.Model
                });

                QueueGitRefreshIfNeeded(repoRoot);
            }
            catch (Exception ex)
            {
                _showErrorMessage("파일 로드 에러", ex.Message);
            }
            finally
            {
                _fileOpenSemaphore.Release();
            }
        }

        private async Task OpenEncryptedFileAsync(string filePath, string? repoRoot)
        {
            string? password = await _promptPasswordAsync(
                _getString("EncryptionPasswordDialogTitle", "암호 입력"),
                _getString("EncryptionOpenButton", "열기"));
            if (password == null)
            {
                return;
            }

            string decryptedText = await _secureNoteEncryptionService.DecryptFileAsync(filePath, password);
            _openNewTab(new FileTabOpenRequest
            {
                FilePath = filePath,
                Content = decryptedText,
                EncodingName = "UTF-8",
                EncodingWasAutoDetected = false,
                TextModel = LineArrayTextModel.FromText(decryptedText),
                IsEncrypted = true,
                EncryptionPassword = password
            });

            QueueGitRefreshIfNeeded(repoRoot);
        }

        private bool FocusExistingTab(string filePath)
        {
            var existingTab = _viewModel.Tabs.FirstOrDefault(t =>
                string.Equals(t.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
            if (existingTab == null)
            {
                return false;
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

            return true;
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

        private static bool IsDocxFile(string filePath)
        {
            return Path.GetExtension(filePath).Equals(".docx", StringComparison.OrdinalIgnoreCase);
        }

        private async Task OpenDocxFileAsync(string filePath, string? repoRoot)
        {
            var docxService = new DocxTextExtractionService();
            string extractedText = await docxService.ExtractTextAsync(filePath);
            _openNewTab(new FileTabOpenRequest
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
}
