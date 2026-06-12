using System;
using System.IO;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Editor;

namespace TxtAIEditor.Controls
{
    public sealed class EditorTabDocumentFactory
    {
        private readonly ILanguageDetectionService _languageDetectionService;
        private readonly Func<string, string, string> _getString;

        public EditorTabDocumentFactory(
            ILanguageDetectionService languageDetectionService,
            Func<string, string, string> getString)
        {
            _languageDetectionService = languageDetectionService;
            _getString = getString;
        }

        public EditorTabDocumentParts Create(
            string? filePath,
            string content,
            bool isReadOnly,
            string encodingName,
            bool encodingWasAutoDetected,
            ITextModel? textModel,
            bool isEncrypted,
            string? encryptionPassword)
        {
            var tab = new OpenedTab
            {
                EncodingName = encodingName,
                EncodingWasAutoDetected = encodingWasAutoDetected,
                IsEncrypted = isEncrypted,
                EncryptionPassword = encryptionPassword
            };

            bool effectiveReadOnly = isReadOnly ||
                filePath?.EndsWith(".diff", StringComparison.OrdinalIgnoreCase) == true ||
                IsExtractedDocumentViewerFile(filePath);

            if (filePath != null)
            {
                tab.FilePath = filePath;
                tab.Title = Path.GetFileName(filePath);
                tab.Content = content;
                tab.Language = _languageDetectionService.GetMonacoLanguageName(filePath);
                if (IsExtractedDocumentViewerFile(filePath))
                {
                    tab.IsDocxViewer = true;
                }
            }
            else
            {
                tab.Title = _getString("UntitledNewTab", "제목 없음");
                tab.Content = content;
            }

            var documentModel = textModel ?? LineArrayTextModel.FromText(content);
            var session = new EditorDocumentSession(tab, documentModel);
            tab.OriginalContent = documentModel.GetText();
            tab.OriginalLineEnding = documentModel.LineEnding;
            tab.OriginalEncodingName = encodingName;

            return new EditorTabDocumentParts(tab, session, effectiveReadOnly);
        }

        private static bool IsExtractedDocumentViewerFile(string? filePath)
        {
            return filePath?.EndsWith(".docx", StringComparison.OrdinalIgnoreCase) == true ||
                   filePath?.EndsWith(".hwpx", StringComparison.OrdinalIgnoreCase) == true;
        }
    }

    public sealed class EditorTabDocumentParts
    {
        public EditorTabDocumentParts(
            OpenedTab tab,
            EditorDocumentSession session,
            bool isReadOnly)
        {
            Tab = tab;
            Session = session;
            IsReadOnly = isReadOnly;
        }

        public OpenedTab Tab { get; }
        public EditorDocumentSession Session { get; }
        public bool IsReadOnly { get; }
    }
}
