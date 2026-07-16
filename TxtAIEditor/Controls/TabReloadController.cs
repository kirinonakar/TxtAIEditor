using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Services;
using TxtAIEditor.Editor;

namespace TxtAIEditor.Controls
{
    public sealed class TabReloadController
    {
        private readonly SecureNoteEncryptionService _secureNoteEncryptionService;
        private readonly ArchiveExplorerService _archiveExplorerService;
        private readonly ISettingsService _settingsService;
        private readonly Dictionary<string, (WebView2 WebView, MonacoBridge Bridge)> _tabBridges;
        private readonly Dictionary<string, EditorDocumentSession> _editorSessions;
        private readonly StatusBarController _statusBarController;
        private readonly int _initialEditorLineWarmupCount;
        private readonly Func<string, string, Task<string?>> _promptPasswordAsync;
        private readonly Func<string, string, string> _getString;
        private readonly Action<OpenedTab> _updateLivePreview;
        private readonly Action<OpenedTab> _updateLanguageUi;
        private readonly Action<OpenedTab> _schedulePreview;
        private readonly Action _updateWindowTitle;
        private readonly Action<string, string> _showErrorMessage;

        public TabReloadController(
            SecureNoteEncryptionService secureNoteEncryptionService,
            ArchiveExplorerService archiveExplorerService,
            ISettingsService settingsService,
            Dictionary<string, (WebView2 WebView, MonacoBridge Bridge)> tabBridges,
            Dictionary<string, EditorDocumentSession> editorSessions,
            StatusBarController statusBarController,
            int initialEditorLineWarmupCount,
            Func<string, string, Task<string?>> promptPasswordAsync,
            Func<string, string, string> getString,
            Action<OpenedTab> updateLivePreview,
            Action<OpenedTab> updateLanguageUi,
            Action<OpenedTab> schedulePreview,
            Action updateWindowTitle,
            Action<string, string> showErrorMessage)
        {
            _secureNoteEncryptionService = secureNoteEncryptionService;
            _archiveExplorerService = archiveExplorerService;
            _settingsService = settingsService;
            _tabBridges = tabBridges;
            _editorSessions = editorSessions;
            _statusBarController = statusBarController;
            _initialEditorLineWarmupCount = initialEditorLineWarmupCount;
            _promptPasswordAsync = promptPasswordAsync;
            _getString = getString;
            _updateLivePreview = updateLivePreview;
            _updateLanguageUi = updateLanguageUi;
            _schedulePreview = schedulePreview;
            _updateWindowTitle = updateWindowTitle;
            _showErrorMessage = showErrorMessage;
        }

        public async Task ReloadWithEncodingAsync(OpenedTab tab, string encodingName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(tab.FilePath))
                {
                    return;
                }

                if (tab.IsReadOnlyTextFile)
                {
                    await ReloadArchiveTextEntryWithEncodingAsync(tab, encodingName);
                    return;
                }

                if (tab.IsDocxViewer)
                {
                    return; // Prevent encoding change updates since encoding is managed internally by the XML parser.
                }

                bool isReadOnly = tab.FilePath.EndsWith(".diff", StringComparison.OrdinalIgnoreCase);

                if (tab.IsEncrypted)
                {
                    string? password = await GetEncryptionPasswordAsync(tab);
                    if (password == null)
                    {
                        return;
                    }

                    string decryptedText = await _secureNoteEncryptionService.DecryptFileAsync(tab.FilePath, password);
                    var encryptedModel = TextModelFactory.FromText(decryptedText);
                    tab.EncryptionPassword = password;
                    ApplyTabReloadState(tab, encryptedModel, "UTF-8", false, decryptedText);

                    var encryptedSession = new EditorDocumentSession(tab, encryptedModel);
                    _editorSessions[tab.Id] = encryptedSession;
                    await InitializeBridgeModelAsync(tab, encryptedSession, isReadOnly);
                    CompleteEncodingReload(tab);
                    return;
                }

                var readResult = await LineArrayTextModel.LoadFromFileAsync(tab.FilePath, encodingName);
                ApplyTabReloadState(
                    tab,
                    readResult.Model,
                    readResult.EncodingName,
                    readResult.EncodingWasAutoDetected,
                    readResult.Model.GetText());

                var session = new EditorDocumentSession(tab, readResult.Model);
                _editorSessions[tab.Id] = session;
                await InitializeBridgeModelAsync(tab, session, isReadOnly);
                CompleteEncodingReload(tab);
            }
            catch (Exception ex)
            {
                _showErrorMessage(_getString("EncodingChangeFailedTitle", "인코딩 변경 실패"), ex.Message);
                _statusBarController.SyncEncodingCombo(tab);
                _statusBarController.SyncLineEndingText(tab);
            }
        }

        public async Task ReloadFromDiskAsync(OpenedTab tab)
        {
            if (tab.IsHexViewer)
            {
                try
                {
                    await ReloadHexViewerAsync(tab);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to reload hex viewer tab: {ex.Message}");
                }
                return;
            }

            if (string.IsNullOrEmpty(tab.FilePath) || !File.Exists(tab.FilePath))
            {
                return;
            }

            try
            {
                if (tab.IsDocxViewer)
                {
                    var documentService = new DocumentTextExtractionService();
                    string extractedText = await documentService.ExtractTextAsync(tab.FilePath, 50_000_000);
                    var docxModel = TextModelFactory.FromText(extractedText);
                    ApplyTabReloadState(tab, docxModel, "UTF-8", false, extractedText);

                    if (_editorSessions.TryGetValue(tab.Id, out var docxSession))
                    {
                        docxSession.UpdateContentFromSync(extractedText);
                    }

                    await SetBridgeTextAsync(tab, extractedText);
                    CompleteDiskReload(tab);
                    return;
                }

                if (tab.IsEncrypted)
                {
                    string? password = await GetEncryptionPasswordAsync(tab);
                    if (password == null)
                    {
                        return;
                    }

                    string decryptedText = await _secureNoteEncryptionService.DecryptFileAsync(tab.FilePath, password);
                    var encryptedModel = TextModelFactory.FromText(decryptedText);
                    tab.EncryptionPassword = password;
                    ApplyTabReloadState(tab, encryptedModel, "UTF-8", false, decryptedText);

                    if (_editorSessions.TryGetValue(tab.Id, out var encryptedSession))
                    {
                        encryptedSession.UpdateContentFromSync(decryptedText);
                    }

                    await SetBridgeTextAsync(tab, decryptedText);
                    CompleteDiskReload(tab);
                    return;
                }

                var readResult = await LineArrayTextModel.LoadFromFileAsync(tab.FilePath, "Auto");
                string content = readResult.Model.GetText();
                ApplyTabReloadState(
                    tab,
                    readResult.Model,
                    readResult.EncodingName,
                    readResult.EncodingWasAutoDetected,
                    content);

                if (_editorSessions.TryGetValue(tab.Id, out var session))
                {
                    session.UpdateContentFromSync(content);
                }

                await SetBridgeTextAsync(tab, content);
                CompleteDiskReload(tab);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to reload tab: {ex.Message}");
            }
        }

        private async Task<string?> GetEncryptionPasswordAsync(OpenedTab tab)
        {
            if (!string.IsNullOrWhiteSpace(tab.EncryptionPassword))
            {
                return tab.EncryptionPassword;
            }

            return await _promptPasswordAsync(
                _getString("EncryptionPasswordDialogTitle", "암호 입력"),
                _getString("EncryptionOpenButton", "열기"));
        }

        private async Task ReloadArchiveTextEntryWithEncodingAsync(OpenedTab tab, string encodingName)
        {
            if (string.IsNullOrWhiteSpace(tab.ArchiveSourcePath) ||
                string.IsNullOrWhiteSpace(tab.ArchiveEntryPath))
            {
                return;
            }

            byte[] bytes = await _archiveExplorerService.ReadEntryBytesAsync(tab.ArchiveSourcePath, tab.ArchiveEntryPath);
            var readResult = LoadArchiveTextEntry(bytes, encodingName);
            string content = readResult.Model.GetText();
            ApplyTabReloadState(
                tab,
                readResult.Model,
                readResult.EncodingName,
                readResult.EncodingWasAutoDetected,
                content);

            tab.IsReadOnlyTextFile = true;
            tab.ArchiveEntryPath = ArchiveExplorerService.NormalizeEntryPath(tab.ArchiveEntryPath);

            var session = new EditorDocumentSession(tab, readResult.Model);
            _editorSessions[tab.Id] = session;
            await InitializeBridgeModelAsync(tab, session, isReadOnly: true);
            CompleteEncodingReload(tab);
        }

        private async Task InitializeBridgeModelAsync(OpenedTab tab, EditorDocumentSession session, bool isReadOnly)
        {
            if (_tabBridges.TryGetValue(tab.Id, out var bridgeGroup) && bridgeGroup.Bridge != null)
            {
                await bridgeGroup.Bridge.InitializeModelAsync(
                    session.Model.LineCount,
                    tab.Language,
                    _settingsService.CurrentSettings,
                    isReadOnly: isReadOnly,
                    initialLines: session.GetLines(1, _initialEditorLineWarmupCount),
                    documentId: session.DocumentId,
                    documentVersion: session.DocumentVersion,
                    viewId: tab.Id);
            }
        }

        private static EditorDocumentLoadResult LoadArchiveTextEntry(byte[] bytes, string encodingName)
        {
            Encoding encoding = TextEncodingService.GetTextEncoding(bytes, encodingName);
            string text = encoding.GetString(bytes);
            bool wasAutoDetected = string.IsNullOrWhiteSpace(encodingName) ||
                                   encodingName.Equals("Auto", StringComparison.OrdinalIgnoreCase);

            return new EditorDocumentLoadResult(
                TextModelFactory.FromText(text),
                TextEncodingService.GetDisplayName(encoding, TextEncodingService.HasUtf8Bom(bytes)),
                wasAutoDetected);
        }

        private async Task ReloadHexViewerAsync(OpenedTab tab)
        {
            string? sourcePath = tab.HexSourceFilePath;
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                return;
            }

            var hexModel = new HexDumpTextModel(sourcePath);
            if (_editorSessions.TryGetValue(tab.Id, out var session))
            {
                session.UpdateModelFromSync(hexModel);
            }
            else
            {
                session = new EditorDocumentSession(tab, hexModel);
                _editorSessions[tab.Id] = session;
            }

            tab.IsDirty = false;
            tab.OriginalContent = tab.Content;
            tab.OriginalLineEnding = hexModel.LineEnding;
            tab.OriginalEncodingName = tab.EncodingName;
            tab.RefreshPreviewResourceVersion();

            await InitializeBridgeModelAsync(tab, session, isReadOnly: true);
            CompleteDiskReload(tab);
        }

        private async Task SetBridgeTextAsync(OpenedTab tab, string text)
        {
            if (_tabBridges.TryGetValue(tab.Id, out var bridgeGroup) && bridgeGroup.Bridge != null)
            {
                _editorSessions.TryGetValue(tab.Id, out var session);
                await bridgeGroup.Bridge.SetTextAsync(
                    text,
                    shouldFocus: true,
                    session?.DocumentId,
                    session?.DocumentVersion,
                    tab.Id);
                session?.MarkViewSynchronized(session.DocumentVersion);
            }
        }

        private static void ApplyTabReloadState(
            OpenedTab tab,
            ITextModel model,
            string encodingName,
            bool encodingWasAutoDetected,
            string originalContent)
        {
            tab.EncodingName = encodingName;
            tab.EncodingWasAutoDetected = encodingWasAutoDetected;
            tab.IsDirty = false;
            tab.OriginalContent = originalContent;
            tab.OriginalLineEnding = model.LineEnding;
            tab.OriginalEncodingName = encodingName;
            tab.RefreshPreviewResourceVersion();
        }

        private void CompleteEncodingReload(OpenedTab tab)
        {
            _updateLivePreview(tab);
            _statusBarController.UpdateFileStats(tab);
            _statusBarController.UpdateTotalLines(tab);
            _statusBarController.UpdateSelectionStats(null);
            _updateLanguageUi(tab);
            _statusBarController.SyncEncodingCombo(tab);
            _statusBarController.SyncLineEndingText(tab);
            _updateWindowTitle();
        }

        private void CompleteDiskReload(OpenedTab tab)
        {
            _statusBarController.UpdateFileStats(tab);
            _statusBarController.UpdateTotalLines(tab);
            _statusBarController.SyncEncodingCombo(tab);
            _schedulePreview(tab);
        }
    }
}
