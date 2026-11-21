using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TxtAIEditor.Core.Services;
using static TxtAIEditor.Controls.AgentTextContentUtilities;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentDocumentExtractionToolService
    {
        private readonly AgentWorkspaceFileResolver _workspace;
        private readonly DocumentTextExtractionService _documentTextExtractionService = new();
        private readonly Func<string, string, string> _getString;
        private readonly Func<string, Task> _notifyFileModifiedAsync;
        private readonly Func<Action<string>?> _activityReporterProvider;
        private readonly Func<CancellationToken> _cancellationToken;

        public AgentDocumentExtractionToolService(
            AgentWorkspaceFileResolver workspace,
            Func<string, string, string> getString,
            Func<string, Task> notifyFileModifiedAsync,
            Func<Action<string>?> activityReporterProvider,
            Func<CancellationToken> cancellationToken)
        {
            _workspace = workspace;
            _getString = getString;
            _notifyFileModifiedAsync = notifyFileModifiedAsync;
            _activityReporterProvider = activityReporterProvider;
            _cancellationToken = cancellationToken;
        }

        public async Task<string> ExtractDocumentAsync(string path, string outputPath, int maxChars)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return Rdocs2mdConverter.IsAvailable
                    ? _getString("AgentExtractDocumentEmptyPathRdocs2md", "extract_document failed: path is empty. Provide a PDF, DOCX, PPTX, XLSX, HWPX, HWP, EPUB, DOC, XLS, or PPT file path.")
                    : _getString("AgentExtractDocumentEmptyPath", "extract_document failed: path is empty. Provide a PDF, DOCX, PPTX, XLSX, HWPX, DOC, XLS, or PPT file path.");
            }

            string fullPath;
            try
            {
                fullPath = await _workspace.ResolveReadPathAsync(path, _cancellationToken());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return string.Format(_getString("AgentExtractDocumentFailedFormat", "extract_document failed: {0}"), ex.Message);
            }

            if (!File.Exists(fullPath))
            {
                return _workspace.BuildMissingFileMessage("extract_document", path);
            }

            if (!DocumentTextExtractionService.IsSupportedExtension(fullPath))
            {
                return string.Format(
                    Rdocs2mdConverter.IsAvailable
                        ? _getString("AgentExtractDocumentUnsupportedTypeFormatRdocs2md", "extract_document failed: unsupported file type '{0}'. Supported types: .pdf, .docx, .pptx, .xlsx, .hwpx, .hwp, .epub, .doc, .xls, .ppt.")
                        : _getString("AgentExtractDocumentUnsupportedTypeFormat", "extract_document failed: unsupported file type '{0}'. Supported types: .pdf, .docx, .pptx, .xlsx, .hwpx, .doc, .xls, .ppt."),
                    Path.GetExtension(fullPath));
            }

            int limit = Math.Clamp(maxChars <= 0 ? 5000000 : maxChars, 1, 50000000);
            int lastProgress = -5;
            var progress = new Progress<int>(percent =>
            {
                int clamped = Math.Clamp(percent, 0, 100);
                if (clamped < lastProgress + 5 && clamped != 100)
                {
                    return;
                }

                lastProgress = clamped;
                _activityReporterProvider()?.Invoke(string.Format(
                    _getString("AgentActivityExtractDocumentProgressFormat", "문서 텍스트 추출 중: {0}%"),
                    clamped));
            });

            string relativePath = _workspace.RelativePath(fullPath);
            string tempFilePath = string.Empty;
            bool isTempFile = false;
            string targetPathForExtraction = fullPath;
            string extension = Path.GetExtension(fullPath).ToLowerInvariant();
            bool useMarkdown = Rdocs2mdConverter.SupportsExtension(fullPath);

            try
            {
                try
                {
                    _workspace.VerifyResolvedPath(fullPath);
                    if (!useMarkdown && extension == ".doc")
                    {
                        tempFilePath = await OfficeDocumentConverter.ConvertToDocxAsync(fullPath).ConfigureAwait(false);
                        targetPathForExtraction = tempFilePath;
                        isTempFile = true;
                    }
                    else if (!useMarkdown && extension == ".xls")
                    {
                        tempFilePath = await OfficeDocumentConverter.ConvertToXlsxAsync(fullPath).ConfigureAwait(false);
                        targetPathForExtraction = tempFilePath;
                        isTempFile = true;
                    }
                    else if (!useMarkdown && extension == ".ppt")
                    {
                        tempFilePath = await OfficeDocumentConverter.ConvertToPptxAsync(fullPath).ConfigureAwait(false);
                        targetPathForExtraction = tempFilePath;
                        isTempFile = true;
                    }
                }
                catch (Exception ex)
                {
                    return string.Format(
                        _getString("AgentExtractDocumentReadFailedFormat", "extract_document failed: could not extract text from {0}: {1}"),
                        path,
                        ex.Message);
                }

                if (!useMarkdown && Path.GetExtension(targetPathForExtraction).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
                {
                    return await ExtractSpreadsheetAsync(targetPathForExtraction, relativePath, outputPath, limit, progress, path).ConfigureAwait(false);
                }

                string text;
                try
                {
                    _workspace.VerifyResolvedPath(targetPathForExtraction);
                    text = useMarkdown
                        ? Truncate(await Rdocs2mdConverter.ConvertToMarkdownAsync(targetPathForExtraction, _cancellationToken()).ConfigureAwait(false), limit)
                        : await _documentTextExtractionService.ExtractTextAsync(targetPathForExtraction, limit, progress).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    return string.Format(
                        _getString("AgentExtractDocumentReadFailedFormat", "extract_document failed: could not extract text from {0}: {1}"),
                        path,
                        ex.Message);
                }

                if (string.IsNullOrWhiteSpace(text))
                {
                    return string.Format(
                        _getString("AgentExtractDocumentNoTextFormat", "extract_document: no text extracted from {0}. If this is a scanned PDF or image-only document, OCR may be required."),
                        relativePath);
                }

                string targetFullPath;
                try
                {
                    targetFullPath = string.IsNullOrWhiteSpace(outputPath)
                        ? GetDefaultExtractedTextPath(fullPath, useMarkdown)
                        : _workspace.ResolveInsideWorkspace(outputPath);
                }
                catch (Exception ex)
                {
                    return string.Format(_getString("AgentExtractDocumentFailedFormat", "extract_document failed: {0}"), ex.Message);
                }

                string defaultOutputExtension = GetDefaultExtractedTextExtension(fullPath, useMarkdown);
                if (string.IsNullOrWhiteSpace(Path.GetExtension(targetFullPath)))
                {
                    targetFullPath += defaultOutputExtension;
                }
                _workspace.VerifyWritablePath(targetFullPath);

                string newContent = NormalizeNewlines(text);
                bool targetAlreadyExists = File.Exists(targetFullPath);
                if (targetAlreadyExists)
                {
                    string oldContent = NormalizeNewlines(await File.ReadAllTextAsync(targetFullPath));
                    if (string.Equals(newContent, oldContent, StringComparison.Ordinal))
                    {
                        var unchangedBuilder = new StringBuilder();
                        unchangedBuilder.AppendLine($"{_getString("AgentExtractDocumentUnchangedPrefix", "extract_document unchanged:")} {_workspace.RelativePath(targetFullPath)}");
                        unchangedBuilder.AppendLine(_getString("AgentExtractDocumentUnchangedDetail", "The output file already contains the extracted text."));
                        return unchangedBuilder.ToString();
                    }

                    targetFullPath = _workspace.GetAvailableSiblingPath(targetFullPath);
                }

                await AgentFileEditToolService.WriteNewWorkspaceFileAsync(_workspace, targetFullPath, newContent, Encoding.UTF8, _cancellationToken());
                await _notifyFileModifiedAsync(targetFullPath);

                string targetRelativePath = _workspace.RelativePath(targetFullPath);
                var builder = new StringBuilder();
                builder.AppendLine($"{_getString("AgentExtractDocumentSavedPrefix", "extract_document saved:")} {targetRelativePath}");
                builder.AppendLine($"{_getString("AgentExtractDocumentSourcePrefix", "source:")} {relativePath}");
                if (targetAlreadyExists)
                {
                    builder.AppendLine(string.Format(
                        _getString("AgentExtractDocumentOutputExistsNoteFormat", "note: requested output existed, so the converted text was saved to a new file instead of overwriting: {0}"),
                        targetRelativePath));
                }
                return builder.ToString();
            }
            finally
            {
                if (isTempFile && !string.IsNullOrEmpty(tempFilePath) && File.Exists(tempFilePath))
                {
                    try
                    {
                        File.Delete(tempFilePath);
                    }
                    catch
                    {
                        // Ignore delete errors for temp files
                    }
                }
            }
        }

        private async Task<string> ExtractSpreadsheetAsync(
            string fullPath,
            string relativePath,
            string outputPath,
            int maxChars,
            IProgress<int> progress,
            string originalPath)
        {
            IReadOnlyList<ExtractedSpreadsheetSheet> sheets;
            try
            {
                _workspace.VerifyResolvedPath(fullPath);
                sheets = await DocumentTextExtractionService.ExtractXlsxSheetsAsync(fullPath, maxChars, progress);
            }
            catch (Exception ex)
            {
                return string.Format(
                    _getString("AgentExtractDocumentReadFailedFormat", "extract_document failed: could not extract text from {0}: {1}"),
                    originalPath,
                    ex.Message);
            }

            if (sheets.Count == 0 || sheets.All(sheet => string.IsNullOrWhiteSpace(sheet.CsvContent)))
            {
                return string.Format(
                    _getString("AgentExtractDocumentNoTextFormat", "extract_document: no text extracted from {0}. If this is a scanned PDF or image-only document, OCR may be required."),
                    relativePath);
            }

            string baseTargetFullPath;
            try
            {
                baseTargetFullPath = string.IsNullOrWhiteSpace(outputPath)
                    ? GetDefaultExtractedTextPath(fullPath, useMarkdown: false)
                    : _workspace.ResolveInsideWorkspace(outputPath);
            }
            catch (Exception ex)
            {
                return string.Format(_getString("AgentExtractDocumentFailedFormat", "extract_document failed: {0}"), ex.Message);
            }

            if (string.IsNullOrWhiteSpace(Path.GetExtension(baseTargetFullPath)))
            {
                baseTargetFullPath += ".csv";
            }

            var builder = new StringBuilder();
            builder.AppendLine($"{_getString("AgentExtractDocumentSourcePrefix", "source:")} {relativePath}");

            bool splitSheets = sheets.Count > 1;
            foreach (ExtractedSpreadsheetSheet sheet in sheets)
            {
                string targetFullPath = splitSheets
                    ? AddSheetSuffix(baseTargetFullPath, sheet.Index)
                    : baseTargetFullPath;
                _workspace.VerifyWritablePath(targetFullPath);
                string newContent = NormalizeNewlines(sheet.CsvContent);
                bool targetAlreadyExists = File.Exists(targetFullPath);

                if (targetAlreadyExists)
                {
                    string oldContent = NormalizeNewlines(await File.ReadAllTextAsync(targetFullPath));
                    if (string.Equals(newContent, oldContent, StringComparison.Ordinal))
                    {
                        builder.AppendLine($"{_getString("AgentExtractDocumentUnchangedPrefix", "extract_document unchanged:")} {_workspace.RelativePath(targetFullPath)}");
                        continue;
                    }

                    targetFullPath = _workspace.GetAvailableSiblingPath(targetFullPath);
                }

                await AgentFileEditToolService.WriteNewWorkspaceFileAsync(_workspace, targetFullPath, newContent, Encoding.UTF8, _cancellationToken());
                await _notifyFileModifiedAsync(targetFullPath);

                string targetRelativePath = _workspace.RelativePath(targetFullPath);
                builder.AppendLine($"{_getString("AgentExtractDocumentSavedPrefix", "extract_document saved:")} {targetRelativePath}");
                if (targetAlreadyExists)
                {
                    builder.AppendLine(string.Format(
                        _getString("AgentExtractDocumentOutputExistsNoteFormat", "note: requested output existed, so the converted text was saved to a new file instead of overwriting: {0}"),
                        targetRelativePath));
                }
            }

            return builder.ToString();
        }

        private static string AddSheetSuffix(string fullPath, int sheetIndex)
        {
            string directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
            string filenameWithoutExtension = Path.GetFileNameWithoutExtension(fullPath);
            string extension = Path.GetExtension(fullPath);
            return Path.Combine(directory, $"{filenameWithoutExtension}_sheet{sheetIndex}{extension}");
        }

        private string GetDefaultExtractedTextPath(string sourceFullPath, bool useMarkdown)
        {
            string root = _workspace.ResolveWorkspaceRoot();
            string fileName = Path.GetFileNameWithoutExtension(sourceFullPath) + GetDefaultExtractedTextExtension(sourceFullPath, useMarkdown);
            if (AgentWorkspaceFileResolver.IsInsideRoot(root, sourceFullPath))
            {
                string directory = Path.GetDirectoryName(sourceFullPath) ?? root;
                return Path.Combine(directory, fileName);
            }

            return Path.Combine(root, fileName);
        }

        private static string GetDefaultExtractedTextExtension(string sourceFullPath, bool useMarkdown)
        {
            if (useMarkdown)
            {
                return ".md";
            }

            string extension = Path.GetExtension(sourceFullPath);
            return extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".xls", StringComparison.OrdinalIgnoreCase)
                ? ".csv"
                : ".txt";
        }

        private static string Truncate(string text, int maxChars)
        {
            return text.Length > maxChars ? text.Substring(0, maxChars) : text;
        }
    }
}
