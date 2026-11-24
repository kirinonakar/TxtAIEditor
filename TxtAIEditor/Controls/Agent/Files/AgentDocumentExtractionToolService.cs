using System;
using System.Collections.Generic;
using System.Globalization;
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

        public async Task<string> ExtractDocumentAsync(string path, string outputPath, int maxChars, string sheet)
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
            string extension = Path.GetExtension(fullPath).ToLowerInvariant();
            IReadOnlyList<string> requestedSheets = SplitSheetSelection(sheet);
            bool useRdocs2mdSheets = requestedSheets.Count > 0 &&
                Rdocs2mdConverter.IsAvailable &&
                Rdocs2mdConverter.IsSpreadsheetExtension(fullPath);
            bool useMarkdown = !useRdocs2mdSheets && Rdocs2mdConverter.SupportsExtension(fullPath);
            if (useRdocs2mdSheets)
            {
                return await ExtractSheetsAsCsvAsync(fullPath, relativePath, outputPath, limit, requestedSheets, path).ConfigureAwait(false);
            }

            if (requestedSheets.Count > 0 &&
                !extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".xls", StringComparison.OrdinalIgnoreCase))
            {
                return string.Format(
                    _getString("AgentExtractDocumentSheetUnsupportedFormat", "extract_document failed: sheet selection is supported only for Excel workbooks (.xlsx, .xls), not '{0}'."),
                    extension);
            }

            if (!useMarkdown && (extension is ".xls" or ".xlsx"))
            {
                return await ExtractSpreadsheetAsync(fullPath, relativePath, outputPath, limit, progress, requestedSheets, path).ConfigureAwait(false);
            }

            string text;
            try
            {
                _workspace.VerifyResolvedPath(fullPath);
                text = useMarkdown
                    ? Truncate(await Rdocs2mdConverter.ConvertToMarkdownAsync(fullPath, _cancellationToken()).ConfigureAwait(false), limit)
                    : await _documentTextExtractionService.ExtractTextAsync(fullPath, limit, progress, cancellationToken: _cancellationToken()).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
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

        private async Task<string> ExtractSpreadsheetAsync(
            string fullPath,
            string relativePath,
            string outputPath,
            int maxChars,
            IProgress<int> progress,
            IReadOnlyList<string> sheetNames,
            string originalPath)
        {
            IReadOnlyList<ExtractedSpreadsheetSheet> sheets;
            try
            {
                _workspace.VerifyResolvedPath(fullPath);
                sheets = await DocumentTextExtractionService.ExtractXlsxSheetsAsync(fullPath, maxChars, progress, _cancellationToken());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
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

            if (sheetNames.Count > 0)
            {
                IReadOnlyList<ExtractedSpreadsheetSheet> matchedSheets = SelectSheets(sheets, sheetNames);
                if (matchedSheets.Count == 0)
                {
                    return string.Format(
                        _getString("AgentExtractDocumentSheetNotFoundFormat", "extract_document failed: sheet '{0}' was not found in {1}. Available sheets: {2}."),
                        string.Join(", ", sheetNames),
                        relativePath,
                        string.Join(", ", sheets.Select(candidate => $"{candidate.Index}: {candidate.Name}")));
                }

                sheets = matchedSheets;
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
                await WriteExtractedContentAsync(builder, targetFullPath, sheet.CsvContent).ConfigureAwait(false);
            }

            return builder.ToString();
        }

        private async Task<string> ExtractSheetsAsCsvAsync(
            string fullPath,
            string relativePath,
            string outputPath,
            int maxChars,
            IReadOnlyList<string> sheetNames,
            string originalPath)
        {
            string baseTargetFullPath;
            try
            {
                _workspace.VerifyResolvedPath(fullPath);
                baseTargetFullPath = string.IsNullOrWhiteSpace(outputPath)
                    ? GetDefaultExtractedTextPath(fullPath, useMarkdown: false, useCsv: true)
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

            bool splitSheets = sheetNames.Count > 1;
            for (int index = 0; index < sheetNames.Count; index++)
            {
                string selection = sheetNames[index];
                int suffixNumber = TryParseSheetNumber(selection, out int selectedNumber) ? selectedNumber : index + 1;

                string csvContent;
                try
                {
                    csvContent = await ConvertSheetCsvAsync(fullPath, selection, maxChars).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    return string.Format(
                        _getString("AgentExtractDocumentReadFailedFormat", "extract_document failed: could not extract text from {0}: {1}"),
                        originalPath,
                        ex.Message);
                }

                if (string.IsNullOrWhiteSpace(csvContent))
                {
                    return string.Format(
                        _getString("AgentExtractDocumentNoTextFormat", "extract_document: no text extracted from {0}. If this is a scanned PDF or image-only document, OCR may be required."),
                        relativePath);
                }

                string targetFullPath = splitSheets
                    ? AddSheetSuffix(baseTargetFullPath, suffixNumber)
                    : baseTargetFullPath;
                await WriteExtractedContentAsync(builder, targetFullPath, csvContent).ConfigureAwait(false);
            }

            return builder.ToString();
        }

        private async Task WriteExtractedContentAsync(StringBuilder builder, string targetFullPath, string content)
        {
            _workspace.VerifyWritablePath(targetFullPath);
            string newContent = NormalizeNewlines(content);
            bool targetAlreadyExists = File.Exists(targetFullPath);
            if (targetAlreadyExists)
            {
                string oldContent = NormalizeNewlines(await File.ReadAllTextAsync(targetFullPath));
                if (string.Equals(newContent, oldContent, StringComparison.Ordinal))
                {
                    builder.AppendLine($"{_getString("AgentExtractDocumentUnchangedPrefix", "extract_document unchanged:")} {_workspace.RelativePath(targetFullPath)}");
                    return;
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

        private const int MaxExpandedSheetSelections = 500;

        // Accepts sheet names, 1-based sheet numbers, and inclusive number ranges such as "2-4".
        private static IReadOnlyList<string> SplitSheetSelection(string selection)
        {
            if (string.IsNullOrWhiteSpace(selection))
            {
                return Array.Empty<string>();
            }

            var expanded = new List<string>();
            foreach (string token in selection.Split(
                new[] { ',', ';' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!TryExpandSheetRange(token, expanded))
                {
                    expanded.Add(token);
                }
            }

            return expanded
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static bool TryExpandSheetRange(string token, List<string> expanded)
        {
            int separatorIndex = token.IndexOf('-');
            if (separatorIndex <= 0 || separatorIndex == token.Length - 1)
            {
                return false;
            }

            if (!TryParseSheetNumber(token.Substring(0, separatorIndex), out int start) ||
                !TryParseSheetNumber(token.Substring(separatorIndex + 1), out int end))
            {
                return false;
            }

            if (start > end)
            {
                (start, end) = (end, start);
            }

            for (int number = start; number <= end && expanded.Count < MaxExpandedSheetSelections; number++)
            {
                expanded.Add(number.ToString(CultureInfo.InvariantCulture));
            }

            return true;
        }

        private static bool TryParseSheetNumber(string value, out int sheetNumber)
        {
            sheetNumber = 0;
            return !string.IsNullOrWhiteSpace(value) &&
                int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out sheetNumber) &&
                sheetNumber > 0;
        }

        // Sheet names win over numbers, so a numeric value selects the sheet with that name when one exists.
        private static IReadOnlyList<ExtractedSpreadsheetSheet> SelectSheets(
            IReadOnlyList<ExtractedSpreadsheetSheet> sheets,
            IReadOnlyList<string> selections)
        {
            var matched = new List<ExtractedSpreadsheetSheet>();
            foreach (string selection in selections)
            {
                bool matchedByName = false;
                foreach (ExtractedSpreadsheetSheet candidate in sheets)
                {
                    if (string.Equals(selection, candidate.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        AddIfMissing(matched, candidate);
                        matchedByName = true;
                    }
                }

                if (matchedByName || !TryParseSheetNumber(selection, out int sheetNumber))
                {
                    continue;
                }

                foreach (ExtractedSpreadsheetSheet candidate in sheets)
                {
                    if (candidate.Index == sheetNumber)
                    {
                        AddIfMissing(matched, candidate);
                        break;
                    }
                }
            }

            return matched;
        }

        private static void AddIfMissing(List<ExtractedSpreadsheetSheet> target, ExtractedSpreadsheetSheet sheet)
        {
            if (!target.Contains(sheet))
            {
                target.Add(sheet);
            }
        }

        // Sheet names win over numbers: try the value as a sheet name, then fall back to a 1-based sheet number.
        private async Task<string> ConvertSheetCsvAsync(string fullPath, string selection, int maxChars)
        {
            if (!TryParseSheetNumber(selection, out int sheetNumber))
            {
                return Truncate(
                    await Rdocs2mdConverter.ConvertToCsvAsync(fullPath, selection, sectionRange: null, _cancellationToken()).ConfigureAwait(false),
                    maxChars);
            }

            try
            {
                return Truncate(
                    await Rdocs2mdConverter.ConvertToCsvAsync(fullPath, selection, sectionRange: null, _cancellationToken()).ConfigureAwait(false),
                    maxChars);
            }
            catch (InvalidOperationException)
            {
                // No sheet uses this numeric name, so treat the value as a 1-based sheet number instead.
            }

            return Truncate(
                await Rdocs2mdConverter.ConvertToCsvAsync(fullPath, sheetName: null, sectionRange: sheetNumber.ToString(CultureInfo.InvariantCulture), _cancellationToken()).ConfigureAwait(false),
                maxChars);
        }

        private static string AddSheetSuffix(string fullPath, int sheetIndex)
        {
            string directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
            string filenameWithoutExtension = Path.GetFileNameWithoutExtension(fullPath);
            string extension = Path.GetExtension(fullPath);
            return Path.Combine(directory, $"{filenameWithoutExtension}_sheet{sheetIndex}{extension}");
        }

        private string GetDefaultExtractedTextPath(string sourceFullPath, bool useMarkdown, bool useCsv = false)
        {
            string root = _workspace.ResolveWorkspaceRoot();
            string fileName = Path.GetFileNameWithoutExtension(sourceFullPath) + GetDefaultExtractedTextExtension(sourceFullPath, useMarkdown, useCsv: useCsv);
            if (AgentWorkspaceFileResolver.IsInsideRoot(root, sourceFullPath))
            {
                string directory = Path.GetDirectoryName(sourceFullPath) ?? root;
                return Path.Combine(directory, fileName);
            }

            return Path.Combine(root, fileName);
        }

        private static string GetDefaultExtractedTextExtension(string sourceFullPath, bool useMarkdown, bool useCsv = false)
        {
            if (useMarkdown)
            {
                return ".md";
            }

            if (useCsv)
            {
                return ".csv";
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
