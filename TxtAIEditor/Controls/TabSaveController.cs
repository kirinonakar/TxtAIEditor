using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Services;
using TxtAIEditor.Editor;

namespace TxtAIEditor.Controls
{
    public sealed class TabSaveController
    {
        private readonly Window _owner;
        private readonly IFileService _fileService;
        private readonly IFileSaveDialogService _fileSaveDialogService;
        private readonly SecureNoteEncryptionService _secureNoteEncryptionService;
        private readonly ILanguageDetectionService _languageDetectionService;
        private readonly StatusBarController _statusBarController;
        private readonly Func<OpenedTab, bool> _isTabOpen;
        private readonly Func<string, EditorDocumentSession?> _sessionProvider;
        private readonly Func<string, (WebView2 WebView, MonacoBridge Bridge)?> _bridgeProvider;
        private readonly Action<OpenedTab> _cleanDirtyStateOnOtherTabs;
        private readonly Action<OpenedTab> _updateLanguageUi;
        private readonly Func<Task> _refreshGitStatusAsync;
        private readonly Action _updateWindowTitle;
        private readonly Action<string> _addRecentFile;
        private readonly Func<string> _currentFolderProvider;
        private readonly Action<string> _reloadDirectoryRoot;
        private readonly Func<string, string, string> _getString;
        private readonly Action<string, string> _showErrorMessage;

        public TabSaveController(
            Window owner,
            IFileService fileService,
            IFileSaveDialogService fileSaveDialogService,
            SecureNoteEncryptionService secureNoteEncryptionService,
            ILanguageDetectionService languageDetectionService,
            StatusBarController statusBarController,
            Func<OpenedTab, bool> isTabOpen,
            Func<string, EditorDocumentSession?> sessionProvider,
            Func<string, (WebView2 WebView, MonacoBridge Bridge)?> bridgeProvider,
            Action<OpenedTab> cleanDirtyStateOnOtherTabs,
            Action<OpenedTab> updateLanguageUi,
            Func<Task> refreshGitStatusAsync,
            Action updateWindowTitle,
            Action<string> addRecentFile,
            Func<string> currentFolderProvider,
            Action<string> reloadDirectoryRoot,
            Func<string, string, string> getString,
            Action<string, string> showErrorMessage)
        {
            _owner = owner;
            _fileService = fileService;
            _fileSaveDialogService = fileSaveDialogService;
            _secureNoteEncryptionService = secureNoteEncryptionService;
            _languageDetectionService = languageDetectionService;
            _statusBarController = statusBarController;
            _isTabOpen = isTabOpen;
            _sessionProvider = sessionProvider;
            _bridgeProvider = bridgeProvider;
            _cleanDirtyStateOnOtherTabs = cleanDirtyStateOnOtherTabs;
            _updateLanguageUi = updateLanguageUi;
            _refreshGitStatusAsync = refreshGitStatusAsync;
            _updateWindowTitle = updateWindowTitle;
            _addRecentFile = addRecentFile;
            _currentFolderProvider = currentFolderProvider;
            _reloadDirectoryRoot = reloadDirectoryRoot;
            _getString = getString;
            _showErrorMessage = showErrorMessage;
        }

        public async Task<bool> SaveAsync(OpenedTab tab)
        {
            if (!_isTabOpen(tab))
            {
                return false;
            }

            if (tab.IsReadOnlyViewer && !tab.IsHexViewer)
            {
                return false;
            }

            if (string.IsNullOrEmpty(tab.FilePath) && !TryChooseSavePath(tab, GetSaveInitialDirectory()))
            {
                return false;
            }

            try
            {
                await SaveTabContentAsync(tab);
                tab.IsDirty = false;
                _cleanDirtyStateOnOtherTabs(tab);
                await CompleteSuccessfulSaveAsync(tab, syncLineEnding: true);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                _showErrorMessage(_getString("SaveFailedTitle", "저장 실패"), ex.Message);
                return false;
            }
        }

        public async Task<bool> SaveAsAsync(OpenedTab tab)
        {
            if (!_isTabOpen(tab))
            {
                return false;
            }

            if (tab.IsReadOnlyViewer && !tab.IsReadOnlyTextFile && !tab.IsHexViewer)
            {
                return false;
            }

            string? initialDir = GetSaveInitialDirectory(tab);
            if (initialDir == null && !string.IsNullOrEmpty(tab.FilePath) && !tab.IsArchiveEntry)
            {
                initialDir = Path.GetDirectoryName(tab.FilePath);
            }

            string? oldFilePath = tab.FilePath;
            string oldTitle = tab.Title;
            string oldLanguage = tab.Language;
            string oldEncodingName = tab.EncodingName;
            bool oldIsReadOnlyTextFile = tab.IsReadOnlyTextFile;
            string? oldArchiveSourcePath = tab.ArchiveSourcePath;
            string? oldArchiveEntryPath = tab.ArchiveEntryPath;
            if (!TryChooseSavePath(tab, initialDir))
            {
                return false;
            }

            try
            {
                ClearArchiveReadOnlyState(tab);
                await SaveTabContentAsync(tab);
                tab.IsDirty = false;
                _cleanDirtyStateOnOtherTabs(tab);
                await CompleteSuccessfulSaveAsync(tab, syncLineEnding: false);
                return true;
            }
            catch (OperationCanceledException)
            {
                tab.FilePath = oldFilePath;
                tab.Title = oldTitle;
                tab.Language = oldLanguage;
                tab.EncodingName = oldEncodingName;
                tab.IsReadOnlyTextFile = oldIsReadOnlyTextFile;
                tab.ArchiveSourcePath = oldArchiveSourcePath;
                tab.ArchiveEntryPath = oldArchiveEntryPath;
                return false;
            }
            catch (Exception ex)
            {
                tab.FilePath = oldFilePath;
                tab.Title = oldTitle;
                tab.Language = oldLanguage;
                tab.EncodingName = oldEncodingName;
                tab.IsReadOnlyTextFile = oldIsReadOnlyTextFile;
                tab.ArchiveSourcePath = oldArchiveSourcePath;
                tab.ArchiveEntryPath = oldArchiveEntryPath;
                _showErrorMessage(
                    _getString("SaveFile", "저장") + " - " + _getString("SaveAsFile", "다른 이름으로 저장"),
                    ex.Message);
                return false;
            }
        }

        private string? GetSaveInitialDirectory()
        {
            string? currentFolderPath = _currentFolderProvider();
            if (!string.IsNullOrEmpty(currentFolderPath) && Directory.Exists(currentFolderPath))
            {
                return currentFolderPath;
            }

            return null;
        }

        private string? GetSaveInitialDirectory(OpenedTab tab)
        {
            if (tab.IsArchiveEntry &&
                !string.IsNullOrWhiteSpace(tab.ArchiveSourcePath))
            {
                string? archiveDirectory = Path.GetDirectoryName(tab.ArchiveSourcePath);
                if (!string.IsNullOrWhiteSpace(archiveDirectory) && Directory.Exists(archiveDirectory))
                {
                    return archiveDirectory;
                }
            }

            return GetSaveInitialDirectory();
        }

        private bool TryChooseSavePath(OpenedTab tab, string? initialDir)
        {
            string suggestedName = GetSuggestedSaveName(tab);
            string? selectedPath = _fileSaveDialogService.ShowSaveDialog(_owner, suggestedName, initialDir);
            if (string.IsNullOrEmpty(selectedPath))
            {
                return false;
            }

            ApplySavePathToTab(tab, selectedPath);
            return true;
        }

        private static string GetSuggestedSaveName(OpenedTab tab)
        {
            if (tab.IsArchiveEntry &&
                !string.IsNullOrWhiteSpace(tab.ArchiveEntryPath))
            {
                string archiveEntryFileName = Path.GetFileName(tab.ArchiveEntryPath.Replace('/', Path.DirectorySeparatorChar));
                if (!string.IsNullOrWhiteSpace(archiveEntryFileName))
                {
                    return archiveEntryFileName;
                }
            }

            return tab.FilePath != null
                ? Path.GetFileNameWithoutExtension(tab.FilePath)
                : tab.Title;
        }

        private void ApplySavePathToTab(OpenedTab tab, string selectedPath)
        {
            tab.FilePath = selectedPath;
            tab.Title = Path.GetFileName(selectedPath);
            tab.Language = _languageDetectionService.GetMonacoLanguageName(selectedPath);
            tab.IsLanguageManuallySelected = false;
            if (string.IsNullOrWhiteSpace(tab.EncodingName))
            {
                tab.EncodingName = "UTF-8";
            }
        }

        private static void ClearArchiveReadOnlyState(OpenedTab tab)
        {
            if (!tab.IsReadOnlyTextFile)
            {
                return;
            }

            tab.IsReadOnlyTextFile = false;
            tab.ArchiveSourcePath = null;
            tab.ArchiveEntryPath = null;
        }

        private async Task<long?> FlushTabEditorBeforeSaveAsync(OpenedTab tab)
        {
            var bridgeGroup = _bridgeProvider(tab.Id);
            if (bridgeGroup?.Bridge != null)
            {
                long? version = await bridgeGroup.Value.Bridge.FlushPendingEditForSaveAsync();
                if (version == null)
                {
                    throw new InvalidOperationException(_getString(
                        "SaveEditorFlushFailed",
                        "편집 입력을 확정하지 못해 저장을 취소했습니다. 다시 시도해 주세요."));
                }
                return version;
            }
            return null;
        }

        private async Task SaveTabContentAsync(OpenedTab tab)
        {
            long? flushedDocumentVersion = await FlushTabEditorBeforeSaveAsync(tab);

            var session = _sessionProvider(tab.Id);
            if (session != null && flushedDocumentVersion is long expectedVersion)
            {
                bool reachedVersion = await session.WaitForDocumentVersionAsync(expectedVersion);
                if (!reachedVersion)
                {
                    throw new InvalidOperationException(_getString(
                        "SaveEditorFlushFailed",
                        "편집 입력을 확정하지 못해 저장을 취소했습니다. 다시 시도해 주세요."));
                }
            }
            if (tab.IsEncrypted)
            {
                string? password = tab.EncryptionPassword;
                if (string.IsNullOrWhiteSpace(password))
                {
                    throw new InvalidOperationException(_getString("EncryptedTabMissingPassword", "암호화된 탭의 암호가 없습니다. 파일을 다시 열어 암호를 입력해 주세요."));
                }

                string text = session != null ? (session.Model is HexDumpTextModel ? string.Empty : session.GetText()) : tab.Content;
                await _secureNoteEncryptionService.SaveEncryptedTextFileAsync(tab.FilePath!, text, password);
                tab.Content = session != null && session.Model is HexDumpTextModel ? string.Empty : (session?.GetText(120_000) ?? tab.Content);
                return;
            }

            if (session != null)
            {
                using var cancellation = new CancellationTokenSource();
                bool saveProgressActive = true;
                var progress = new Progress<TextOperationProgress>(value =>
                {
                    if (saveProgressActive)
                    {
                        _statusBarController.ShowTextOperationProgress(
                            "save",
                            value,
                            cancellation.Cancel);
                    }
                });
                try
                {
                    await session.SaveAsync(
                        tab.FilePath!,
                        tab.EncodingName,
                        cancellation.Token,
                        progress);
                }
                finally
                {
                    saveProgressActive = false;
                    _statusBarController.HideTextOperationProgress();
                }
                tab.Content = session.Model is HexDumpTextModel ? string.Empty : session.GetText(120_000);
                return;
            }

            await _fileService.SaveTextFileAsync(tab.FilePath!, tab.Content, tab.EncodingName);
        }

        private async Task CompleteSuccessfulSaveAsync(OpenedTab tab, bool syncLineEnding)
        {
            _statusBarController.UpdateFileStats(tab);
            _statusBarController.UpdateTotalLines(tab);
            _updateLanguageUi(tab);
            _statusBarController.SyncEncodingCombo(tab);
            if (syncLineEnding)
            {
                _statusBarController.SyncLineEndingText(tab);
            }

            await _refreshGitStatusAsync();
            _updateWindowTitle();

            if (!string.IsNullOrEmpty(tab.FilePath) && File.Exists(tab.FilePath))
            {
                _addRecentFile(tab.FilePath);
            }

            string currentFolderPath = _currentFolderProvider();
            if (!string.IsNullOrEmpty(currentFolderPath) && Directory.Exists(currentFolderPath))
            {
                _reloadDirectoryRoot(currentFolderPath);
            }
        }
    }
}
