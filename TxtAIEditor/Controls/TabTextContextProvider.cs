using System;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Services;
using TxtAIEditor.Editor;

namespace TxtAIEditor.Controls
{
    public sealed class TabTextContextProvider
    {
        private readonly PdfTextExtractionService _pdfTextExtractionService;
        private readonly DocumentTextExtractionService _documentTextExtractionService;
        private readonly Func<string, EditorDocumentSession?> _sessionProvider;

        public TabTextContextProvider(
            PdfTextExtractionService pdfTextExtractionService,
            Func<string, EditorDocumentSession?> sessionProvider)
        {
            _pdfTextExtractionService = pdfTextExtractionService;
            _documentTextExtractionService = new DocumentTextExtractionService();
            _sessionProvider = sessionProvider;
        }

        public string GetText(OpenedTab tab, int maxChars)
        {
            if (tab.IsPdfViewer && !string.IsNullOrWhiteSpace(tab.FilePath))
            {
                return GetCachedOrExtractedText(
                    tab,
                    maxChars,
                    () => _pdfTextExtractionService.ExtractTextAsync(tab.FilePath, maxChars)
                        .ConfigureAwait(false)
                        .GetAwaiter()
                        .GetResult());
            }

            if (tab.IsOfficeDocumentViewer && !string.IsNullOrWhiteSpace(tab.FilePath))
            {
                return GetCachedOrExtractedText(
                    tab,
                    maxChars,
                    () => _documentTextExtractionService.ExtractTextAsync(tab.FilePath, maxChars)
                        .ConfigureAwait(false)
                        .GetAwaiter()
                        .GetResult());
            }

            var session = _sessionProvider(tab.Id);
            return session?.GetText(maxChars) ?? tab.Content ?? string.Empty;
        }

        private static string GetCachedOrExtractedText(OpenedTab tab, int maxChars, Func<string> extractText)
        {
            string cached = tab.Content ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(cached))
            {
                return cached.Length > maxChars ? cached.Substring(0, maxChars) : cached;
            }

            string extracted = extractText();
            tab.Content = extracted;
            return extracted;
        }
    }
}
