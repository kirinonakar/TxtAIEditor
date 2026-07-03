using System;
using System.IO;
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
        private readonly Func<OpenedTab, Task> _flushPendingImeAsync;
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
            Func<OpenedTab, Task> flushPendingImeAsync,
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
            _flushPendingImeAsync = flushPendingImeAsync;
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

            if (tab.IsReadOnlyViewer)
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

            if (tab.IsReadOnlyViewer)
            {
                return false;
            }

            string? initialDir = GetSaveInitialDirectory();
            if (initialDir == null && !string.IsNullOrEmpty(tab.FilePath))
            {
                initialDir = Path.GetDirectoryName(tab.FilePath);
            }

            string? oldFilePath = tab.FilePath;
            string oldTitle = tab.Title;
            string oldLanguage = tab.Language;
            string oldEncodingName = tab.EncodingName;
            if (!TryChooseSavePath(tab, initialDir))
            {
                return false;
            }

            try
            {
                await SaveTabContentAsync(tab);
                tab.IsDirty = false;
                await CompleteSuccessfulSaveAsync(tab, syncLineEnding: false);
                return true;
            }
            catch (Exception ex)
            {
                tab.FilePath = oldFilePath;
                tab.Title = oldTitle;
                tab.Language = oldLanguage;
                tab.EncodingName = oldEncodingName;
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

        private bool TryChooseSavePath(OpenedTab tab, string? initialDir)
        {
            string suggestedName = tab.FilePath != null
                ? Path.GetFileNameWithoutExtension(tab.FilePath)
                : tab.Title;
            string? selectedPath = _fileSaveDialogService.ShowSaveDialog(_owner, suggestedName, initialDir);
            if (string.IsNullOrEmpty(selectedPath))
            {
                return false;
            }

            ApplySavePathToTab(tab, selectedPath);
            return true;
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

        private async Task FlushTabEditorBeforeSaveAsync(OpenedTab tab)
        {
            var bridgeGroup = _bridgeProvider(tab.Id);
            if (bridgeGroup?.Bridge != null)
            {
                await bridgeGroup.Value.Bridge.FlushPendingEditForSaveAsync();
            }
        }

        private async Task SaveTabContentAsync(OpenedTab tab)
        {
            await FlushTabEditorBeforeSaveAsync(tab);
            await _flushPendingImeAsync(tab);

            var session = _sessionProvider(tab.Id);
            if (tab.IsEncrypted)
            {
                string? password = tab.EncryptionPassword;
                if (string.IsNullOrWhiteSpace(password))
                {
                    throw new InvalidOperationException(_getString("EncryptedTabMissingPassword", "암호화된 탭의 암호가 없습니다. 파일을 다시 열어 암호를 입력해 주세요."));
                }

                string text = session != null ? session.GetText() : tab.Content;
                await _secureNoteEncryptionService.SaveEncryptedTextFileAsync(tab.FilePath!, text, password);
                tab.Content = session?.GetText(120_000) ?? tab.Content;
                return;
            }

            if (session != null)
            {
                await session.SaveAsync(tab.FilePath!, tab.EncodingName);
                tab.Content = session.GetText(120_000);
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
