using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Services;

namespace TxtAIEditor.Editor
{
    public readonly record struct TextPosition(int LineNumber, int Column);

    public readonly record struct TextEdit(
        int StartLine,
        int StartColumn,
        int EndLine,
        int EndColumn,
        string Text);

    public sealed record TextSearchResult(
        int LineNumber,
        int IndexOfMatch,
        int MatchLength,
        string LineContent);

    public readonly record struct TextOperationProgress(
        int ProcessedLines,
        int TotalLines,
        TimeSpan Elapsed);

    public sealed record LineReplacement(
        int LineNumber,
        string BeforeText,
        string AfterText);

    public sealed record ReplaceAllResult(
        EditorDocumentChange? Change,
        IReadOnlyList<LineReplacement> Replacements,
        long ReplacementCount,
        TimeSpan Elapsed);

    public interface ITextModel
    {
        int LineCount { get; }
        string LineEnding { get; set; }
        string GetLine(int lineNumber);
        IReadOnlyList<string> GetLines(int startLine, int count);
        string GetTextRange(int startLine, int startColumn, int endLine, int endColumn);
        string GetText(int? maxChars = null);
        TextPosition GetPositionAt(int offset);
        int GetOffsetAt(int lineNumber, int column);
        void ApplyEdit(TextEdit edit);
        void ReplaceLine(int lineNumber, string text);
        void InsertLine(int lineNumber, string text);
        void DeleteLine(int lineNumber);
        void SplitLine(int lineNumber, string before, string after);
        void MergeLineWithPrevious(int lineNumber);
        TextSearchResult? Find(string query, int startLine, int startColumn, bool reverse, bool matchCase, bool isRegex = false);
        List<TextSearchResult> FindAll(string query, bool matchCase, bool isRegex = false, int currentLine = 1, int maxMatches = 50000);
        Task SaveAsync(string filePath, string encodingName, CancellationToken cancellationToken = default);
    }

    public sealed class LineArrayTextModel : ITextModel
    {
        internal static readonly TimeSpan UserRegexTimeout = TimeSpan.FromMilliseconds(250);
        private readonly List<string> _lines;
        private readonly LineLengthIndex _lineLengthIndex = new();
        private string _lineEnding;

        public LineArrayTextModel(IEnumerable<string>? lines = null, string lineEnding = "\n")
        {
            _lines = lines?.ToList() ?? new List<string>();
            if (_lines.Count == 0)
            {
                _lines.Add(string.Empty);
            }

            _lineEnding = string.IsNullOrEmpty(lineEnding) ? "\n" : lineEnding;
            RebuildLineLengthIndex();
        }

        public int LineCount => _lines.Count;

        public string LineEnding
        {
            get => _lineEnding;
            set
            {
                string next = string.IsNullOrEmpty(value) ? "\n" : value;
                if (string.Equals(_lineEnding, next, StringComparison.Ordinal))
                {
                    return;
                }

                _lineEnding = next;
                RebuildLineLengthIndex();
            }
        }

        public static LineArrayTextModel FromText(string text)
        {
            string lineEnding = DetectLineEnding(text);
            string normalized = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
            return new LineArrayTextModel(normalized.Split('\n'), lineEnding);
        }

        internal string[] CaptureLines()
        {
            return _lines.ToArray();
        }

        public static async Task<EditorDocumentLoadResult> LoadFromFileAsync(
            string filePath,
            string encodingName,
            CancellationToken cancellationToken = default)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException("파일을 찾을 수 없습니다.", filePath);
            }

            byte[] sample = await ReadSampleBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
            Encoding encoding = TextEncodingService.GetTextEncoding(sample, encodingName);
            bool isAuto = string.IsNullOrWhiteSpace(encodingName) ||
                encodingName.Equals("Auto", StringComparison.OrdinalIgnoreCase);
            string displayEncoding = isAuto
                ? TextEncodingService.GetDisplayName(encoding, TextEncodingService.HasUtf8Bom(sample))
                : encodingName;

            string sampleText = encoding.GetString(sample);
            string lineEnding = DetectLineEnding(sampleText);
            var lines = new List<string>();

            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 128 * 1024, useAsync: true))
            using (var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true, bufferSize: 128 * 1024))
            {
                string? line;
                while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) != null)
                {
                    lines.Add(line);
                }
            }

            if (lines.Count == 0)
            {
                lines.Add(string.Empty);
            }

            return new EditorDocumentLoadResult(
                new LineArrayTextModel(lines, lineEnding),
                displayEncoding,
                isAuto);
        }

        public string GetLine(int lineNumber)
        {
            if (lineNumber < 1 || lineNumber > _lines.Count)
            {
                return string.Empty;
            }

            return _lines[lineNumber - 1];
        }

        public IReadOnlyList<string> GetLines(int startLine, int count)
        {
            if (count <= 0 || startLine > _lines.Count)
            {
                return Array.Empty<string>();
            }

            int startIndex = Math.Max(0, startLine - 1);
            int safeCount = Math.Min(count, _lines.Count - startIndex);
            if (safeCount <= 0)
            {
                return Array.Empty<string>();
            }

            return _lines.GetRange(startIndex, safeCount);
        }

        public string GetTextRange(int startLine, int startColumn, int endLine, int endColumn)
        {
            NormalizeRange(ref startLine, ref startColumn, ref endLine, ref endColumn);
            if (startLine == endLine)
            {
                string line = GetLine(startLine);
                int start = ClampColumn(line, startColumn) - 1;
                int end = ClampColumn(line, endColumn) - 1;
                return line.Substring(start, Math.Max(0, end - start));
            }

            var parts = new List<string>();
            string first = GetLine(startLine);
            parts.Add(first.Substring(ClampColumn(first, startColumn) - 1));
            for (int lineNumber = startLine + 1; lineNumber < endLine; lineNumber++)
            {
                parts.Add(GetLine(lineNumber));
            }

            string last = GetLine(endLine);
            parts.Add(last.Substring(0, ClampColumn(last, endColumn) - 1));
            return string.Join(LineEnding, parts);
        }

        public string GetText(int? maxChars = null)
        {
            if (maxChars is <= 0)
            {
                return string.Empty;
            }

            if (maxChars == null)
            {
                return string.Join(LineEnding, _lines);
            }

            var builder = new StringBuilder(Math.Min(maxChars.Value, 128 * 1024));
            for (int i = 0; i < _lines.Count; i++)
            {
                if (i > 0)
                {
                    AppendBounded(builder, LineEnding, maxChars.Value);
                }

                AppendBounded(builder, _lines[i], maxChars.Value);
                if (builder.Length >= maxChars.Value)
                {
                    break;
                }
            }

            return builder.ToString();
        }

        public TextPosition GetPositionAt(int offset)
        {
            long documentLength = Math.Max(0, _lineLengthIndex.TotalLength - LineEnding.Length);
            long safeOffset = Math.Clamp((long)offset, 0, documentLength);
            int lineNumber = _lineLengthIndex.FindLineContainingOffset(safeOffset);
            long lineStartOffset = _lineLengthIndex.GetPrefixLength(lineNumber - 1);
            string line = _lines[lineNumber - 1];
            int columnOffset = (int)Math.Clamp(safeOffset - lineStartOffset, 0, line.Length);
            return new TextPosition(lineNumber, columnOffset + 1);
        }

        public int GetOffsetAt(int lineNumber, int column)
        {
            int safeLine = Math.Clamp(lineNumber, 1, _lines.Count);
            string line = _lines[safeLine - 1];
            long offset = _lineLengthIndex.GetPrefixLength(safeLine - 1) + ClampColumn(line, column) - 1L;
            return (int)Math.Min(int.MaxValue, offset);
        }

        public void ApplyEdit(TextEdit edit)
        {
            int startLine = edit.StartLine;
            int startColumn = edit.StartColumn;
            int endLine = edit.EndLine;
            int endColumn = edit.EndColumn;
            NormalizeRange(ref startLine, ref startColumn, ref endLine, ref endColumn);

            string prefix = GetLine(startLine).Substring(0, ClampColumn(GetLine(startLine), startColumn) - 1);
            string suffix = GetLine(endLine).Substring(ClampColumn(GetLine(endLine), endColumn) - 1);
            string normalizedText = (edit.Text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
            string[] insertedLines = normalizedText.Split('\n');

            var replacement = new List<string>();
            if (insertedLines.Length == 1)
            {
                replacement.Add(prefix + insertedLines[0] + suffix);
            }
            else
            {
                replacement.Add(prefix + insertedLines[0]);
                for (int i = 1; i < insertedLines.Length - 1; i++)
                {
                    replacement.Add(insertedLines[i]);
                }
                replacement.Add(insertedLines[^1] + suffix);
            }

            _lines.RemoveRange(startLine - 1, endLine - startLine + 1);
            _lines.InsertRange(startLine - 1, replacement);
            EnsureAtLeastOneLine();
            RebuildLineLengthIndex();
        }

        public void ReplaceLine(int lineNumber, string text)
        {
            if (lineNumber < 1 || lineNumber > _lines.Count)
            {
                return;
            }

            string normalized = NormalizeSingleLine(text);
            string previous = _lines[lineNumber - 1];
            _lines[lineNumber - 1] = normalized;
            _lineLengthIndex.UpdateLineLength(lineNumber, previous.Length, normalized.Length);
        }

        public void InsertLine(int lineNumber, string text)
        {
            int index = Math.Clamp(lineNumber - 1, 0, _lines.Count);
            _lines.Insert(index, NormalizeSingleLine(text));
            RebuildLineLengthIndex();
        }

        public void DeleteLine(int lineNumber)
        {
            if (lineNumber < 1 || lineNumber > _lines.Count)
            {
                return;
            }

            _lines.RemoveAt(lineNumber - 1);
            EnsureAtLeastOneLine();
            RebuildLineLengthIndex();
        }

        public void SplitLine(int lineNumber, string before, string after)
        {
            if (lineNumber < 1 || lineNumber > _lines.Count)
            {
                return;
            }

            _lines[lineNumber - 1] = NormalizeSingleLine(before);
            _lines.Insert(lineNumber, NormalizeSingleLine(after));
            RebuildLineLengthIndex();
        }

        public void MergeLineWithPrevious(int lineNumber)
        {
            if (lineNumber <= 1 || lineNumber > _lines.Count)
            {
                return;
            }

            _lines[lineNumber - 2] += _lines[lineNumber - 1];
            _lines.RemoveAt(lineNumber - 1);
            EnsureAtLeastOneLine();
            RebuildLineLengthIndex();
        }

        public TextSearchResult? Find(string query, int startLine, int startColumn, bool reverse, bool matchCase, bool isRegex = false)
        {
            if (string.IsNullOrEmpty(query))
            {
                return null;
            }

            if (isRegex)
            {
                Regex regex;
                try
                {
                    var regexOptions = matchCase ? RegexOptions.None : RegexOptions.IgnoreCase;
                    regex = new Regex(query, regexOptions, UserRegexTimeout);
                }
                catch (ArgumentException)
                {
                    return null;
                }

                int safeLine = Math.Clamp(startLine, 1, _lines.Count);

                if (reverse)
                {
                    for (int lineNumber = safeLine; lineNumber >= 1; lineNumber--)
                    {
                        string line = _lines[lineNumber - 1];
                        if (line.Length == 0) continue;

                        int searchStart = lineNumber == safeLine
                            ? Math.Clamp(startColumn - 2, 0, line.Length)
                            : line.Length;

                        var matches = regex.Matches(line);
                        for (int i = matches.Count - 1; i >= 0; i--)
                        {
                            var match = matches[i];
                            if (match.Index <= searchStart && match.Length > 0)
                            {
                                return new TextSearchResult(lineNumber, match.Index, match.Length, line);
                            }
                        }
                    }
                    return null;
                }
                else
                {
                    for (int lineNumber = safeLine; lineNumber <= _lines.Count; lineNumber++)
                    {
                        string line = _lines[lineNumber - 1];
                        int searchStart = lineNumber == safeLine
                            ? Math.Clamp(startColumn - 1, 0, line.Length)
                            : 0;

                        var matches = regex.Matches(line);
                        foreach (Match match in matches)
                        {
                            if (match.Index >= searchStart && match.Length > 0)
                            {
                                return new TextSearchResult(lineNumber, match.Index, match.Length, line);
                            }
                        }
                    }
                    return null;
                }
            }

            var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            int safeLineNormal = Math.Clamp(startLine, 1, _lines.Count);

            if (reverse)
            {
                for (int lineNumber = safeLineNormal; lineNumber >= 1; lineNumber--)
                {
                    string line = _lines[lineNumber - 1];
                    if (line.Length == 0)
                    {
                        continue;
                    }

                    int searchStart = lineNumber == safeLineNormal
                        ? Math.Clamp(startColumn - 2, 0, line.Length - 1)
                        : line.Length - 1;
                    int index = line.LastIndexOf(query, searchStart, comparison);
                    if (index >= 0)
                    {
                        return new TextSearchResult(lineNumber, index, query.Length, line);
                    }
                }

                return null;
            }

            for (int lineNumber = safeLineNormal; lineNumber <= _lines.Count; lineNumber++)
            {
                string line = _lines[lineNumber - 1];
                int searchStart = lineNumber == safeLineNormal
                    ? Math.Clamp(startColumn - 1, 0, line.Length)
                    : 0;
                int index = line.IndexOf(query, searchStart, comparison);
                if (index >= 0)
                {
                    return new TextSearchResult(lineNumber, index, query.Length, line);
                }
            }

            return null;
        }

        public List<TextSearchResult> FindAll(string query, bool matchCase, bool isRegex = false, int currentLine = 1, int maxMatches = 50000)
        {
            return FindAllWithLimits(
                query,
                matchCase,
                isRegex,
                currentLine,
                maxMatches,
                CancellationToken.None,
                progress: null,
                maxElapsed: Timeout.InfiniteTimeSpan);
        }

        internal List<TextSearchResult> FindAllWithLimits(
            string query,
            bool matchCase,
            bool isRegex,
            int currentLine,
            int maxMatches,
            CancellationToken cancellationToken,
            IProgress<TextOperationProgress>? progress,
            TimeSpan maxElapsed)
        {
            if (string.IsNullOrEmpty(query))
            {
                return new List<TextSearchResult>();
            }

            int startLine = Math.Clamp(currentLine, 1, _lines.Count);
            int winStart = Math.Max(1, startLine - 200);
            int winEnd = Math.Min(_lines.Count, startLine + 200);
            var results = new List<TextSearchResult>();
            var stopwatch = Stopwatch.StartNew();
            int processedLines = 0;

            void ReportProgress(bool force = false)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (maxElapsed != Timeout.InfiniteTimeSpan && stopwatch.Elapsed > maxElapsed)
                {
                    throw new OperationCanceledException("텍스트 검색 제한 시간을 초과했습니다.", cancellationToken);
                }

                processedLines++;
                if (force || processedLines % 512 == 0 || processedLines == _lines.Count)
                {
                    progress?.Report(new TextOperationProgress(processedLines, _lines.Count, stopwatch.Elapsed));
                }
            }

            if (isRegex)
            {
                Regex regex;
                try
                {
                    var regexOptions = matchCase ? RegexOptions.None : RegexOptions.IgnoreCase;
                    regex = new Regex(query, regexOptions, UserRegexTimeout);
                }
                catch (ArgumentException)
                {
                    return new List<TextSearchResult>();
                }

                void SearchRegexLine(int lineNumber)
                {
                    string line = _lines[lineNumber - 1];
                    if (line.Length == 0) return;

                    var matches = regex.Matches(line);
                    foreach (Match match in matches)
                    {
                        if (match.Length > 0)
                        {
                            results.Add(new TextSearchResult(lineNumber, match.Index, match.Length, line));
                            if (results.Count >= maxMatches)
                            {
                                break;
                            }
                        }
                    }
                }

                // 1. Nearby Window
                for (int lineNumber = winStart; lineNumber <= winEnd; lineNumber++)
                {
                    SearchRegexLine(lineNumber);
                    ReportProgress();
                    if (results.Count >= maxMatches) break;
                }

                // 2. Remaining Below
                if (results.Count < maxMatches && winEnd < _lines.Count)
                {
                    for (int lineNumber = winEnd + 1; lineNumber <= _lines.Count; lineNumber++)
                    {
                        SearchRegexLine(lineNumber);
                        ReportProgress();
                        if (results.Count >= maxMatches) break;
                    }
                }

                // 3. Remaining Above
                if (results.Count < maxMatches && winStart > 1)
                {
                    for (int lineNumber = 1; lineNumber < winStart; lineNumber++)
                    {
                        SearchRegexLine(lineNumber);
                        ReportProgress();
                        if (results.Count >= maxMatches) break;
                    }
                }

                progress?.Report(new TextOperationProgress(processedLines, _lines.Count, stopwatch.Elapsed));
                return results.OrderBy(r => r.LineNumber).ThenBy(r => r.IndexOfMatch).ToList();
            }

            var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

            void SearchNormalLine(int lineNumber)
            {
                string line = _lines[lineNumber - 1];
                if (line.Length == 0) return;

                int searchStart = 0;
                while (searchStart <= line.Length)
                {
                    int index = line.IndexOf(query, searchStart, comparison);
                    if (index < 0) break;

                    results.Add(new TextSearchResult(lineNumber, index, query.Length, line));
                    if (results.Count >= maxMatches)
                    {
                        break;
                    }
                    searchStart = index + 1;
                }
            }

            // 1. Nearby Window
            for (int lineNumber = winStart; lineNumber <= winEnd; lineNumber++)
            {
                SearchNormalLine(lineNumber);
                ReportProgress();
                if (results.Count >= maxMatches) break;
            }

            // 2. Remaining Below
            if (results.Count < maxMatches && winEnd < _lines.Count)
            {
                for (int lineNumber = winEnd + 1; lineNumber <= _lines.Count; lineNumber++)
                {
                    SearchNormalLine(lineNumber);
                    ReportProgress();
                    if (results.Count >= maxMatches) break;
                }
            }

            // 3. Remaining Above
            if (results.Count < maxMatches && winStart > 1)
            {
                for (int lineNumber = 1; lineNumber < winStart; lineNumber++)
                {
                    SearchNormalLine(lineNumber);
                    ReportProgress();
                    if (results.Count >= maxMatches) break;
                }
            }

            progress?.Report(new TextOperationProgress(processedLines, _lines.Count, stopwatch.Elapsed));
            return results.OrderBy(r => r.LineNumber).ThenBy(r => r.IndexOfMatch).ToList();
        }

        public Task SaveAsync(string filePath, string encodingName, CancellationToken cancellationToken = default)
        {
            return SaveWithProgressAsync(filePath, encodingName, cancellationToken, progress: null);
        }

        internal async Task SaveWithProgressAsync(
            string filePath,
            string encodingName,
            CancellationToken cancellationToken,
            IProgress<TextOperationProgress>? progress)
        {
            string? directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string tempFilePath = Path.Combine(directory ?? Path.GetTempPath(), $"._{Path.GetFileName(filePath)}.tmp");
            string backupFilePath = filePath + ".bak";
            Encoding encoding = TextEncodingService.GetEncodingByName(encodingName);

            try
            {
                using (var stream = new FileStream(
                    tempFilePath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 128 * 1024,
                    options: FileOptions.Asynchronous | FileOptions.SequentialScan))
                using (var writer = new StreamWriter(
                    stream,
                    encoding,
                    bufferSize: 128 * 1024,
                    leaveOpen: false))
                {
                    for (int i = 0; i < _lines.Count; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (i % 512 == 0)
                        {
                            progress?.Report(new TextOperationProgress(i, _lines.Count, TimeSpan.Zero));
                        }
                        if (i > 0)
                        {
                        await writer.WriteAsync(LineEnding.AsMemory(), cancellationToken).ConfigureAwait(false);
                        }

                        await writer.WriteAsync(_lines[i].AsMemory(), cancellationToken).ConfigureAwait(false);
                    }

                    progress?.Report(new TextOperationProgress(_lines.Count, _lines.Count, TimeSpan.Zero));
                }

                if (File.Exists(filePath))
                {
                    File.Replace(tempFilePath, filePath, backupFilePath);
                    if (File.Exists(backupFilePath))
                    {
                        File.Delete(backupFilePath);
                    }
                }
                else
                {
                    File.Move(tempFilePath, filePath);
                }
            }
            catch (Exception ex)
            {
                if (File.Exists(tempFilePath))
                {
                    try { File.Delete(tempFilePath); } catch { }
                }

                throw new IOException($"파일 저장 실패 (안전 복구 완료): {ex.Message}", ex);
            }
        }

        private static async Task<byte[]> ReadSampleBytesAsync(string filePath, CancellationToken cancellationToken)
        {
            const int sampleSize = 128 * 1024;
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, sampleSize, useAsync: true);
            byte[] buffer = new byte[Math.Min(sampleSize, (int)Math.Min(stream.Length, sampleSize))];
            int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            if (read == buffer.Length)
            {
                return buffer;
            }

            Array.Resize(ref buffer, read);
            return buffer;
        }

        private static string DetectLineEnding(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "\n";
            }

            int crlf = 0;
            int lf = 0;
            int cr = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\r')
                {
                    if (i + 1 < text.Length && text[i + 1] == '\n')
                    {
                        crlf++;
                        i++;
                    }
                    else
                    {
                        cr++;
                    }
                }
                else if (text[i] == '\n')
                {
                    lf++;
                }
            }

            if (crlf >= lf && crlf >= cr && crlf > 0) return "\r\n";
            if (cr > lf && cr > 0) return "\r";
            return "\n";
        }

        private static void AppendBounded(StringBuilder builder, string value, int maxChars)
        {
            if (builder.Length >= maxChars)
            {
                return;
            }

            int remaining = maxChars - builder.Length;
            builder.Append(value.AsSpan(0, Math.Min(value.Length, remaining)));
        }

        private static int ClampColumn(string line, int column)
        {
            return Math.Clamp(column, 1, line.Length + 1);
        }

        private static string NormalizeSingleLine(string text)
        {
            return (text ?? string.Empty).Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
        }

        private void NormalizeRange(ref int startLine, ref int startColumn, ref int endLine, ref int endColumn)
        {
            startLine = Math.Clamp(startLine, 1, _lines.Count);
            endLine = Math.Clamp(endLine, 1, _lines.Count);
            startColumn = ClampColumn(GetLine(startLine), startColumn);
            endColumn = ClampColumn(GetLine(endLine), endColumn);

            if (endLine < startLine || (endLine == startLine && endColumn < startColumn))
            {
                (startLine, endLine) = (endLine, startLine);
                (startColumn, endColumn) = (endColumn, startColumn);
            }
        }

        private void EnsureAtLeastOneLine()
        {
            if (_lines.Count == 0)
            {
                _lines.Add(string.Empty);
            }
        }

        private void RebuildLineLengthIndex()
        {
            _lineLengthIndex.Rebuild(_lines, LineEnding.Length);
        }
    }

    public sealed record EditorDocumentLoadResult(
        ITextModel Model,
        string EncodingName,
        bool EncodingWasAutoDetected);

    public sealed record EditorDocumentChange(
        string DocumentId,
        string SourceViewId,
        long BaseVersion,
        long Version,
        int StartLine,
        int OldLineCount,
        int DocumentLineCount,
        IReadOnlyList<string> Lines)
    {
        public IReadOnlyList<TextLinePatch>? LinePatches { get; init; }
    }

    public sealed class EditorDocumentBuffer
    {
        private ITextModel _model;

        public EditorDocumentBuffer(ITextModel model)
        {
            _model = model;
            DocumentId = Guid.NewGuid().ToString("N");
        }

        public event EventHandler<EditorDocumentChange>? Changed;

        public string DocumentId { get; }

        public long Version { get; private set; }

        public ITextModel Model => _model;

        internal UndoManager UndoManager { get; } = new();

        internal EditorDocumentChange? LastChange { get; private set; }

        internal EditorDocumentChange ReplaceModel(string sourceViewId, ITextModel model, bool clearUndo)
        {
            int oldLineCount = Math.Max(1, _model.LineCount);
            _model = model;
            if (clearUndo)
            {
                UndoManager.Clear();
            }
            return CommitChange(sourceViewId, 1, oldLineCount, Math.Max(1, model.LineCount));
        }

        internal EditorDocumentChange CommitChange(
            string sourceViewId,
            int startLine,
            int oldLineCount,
            int newLineCount)
        {
            long baseVersion = Version;
            Version++;
            int safeStartLine = Math.Max(1, startLine);
            int safeNewLineCount = Math.Max(0, newLineCount);
            IReadOnlyList<string> lines = safeNewLineCount == 0
                ? Array.Empty<string>()
                : _model.GetLines(
                    Math.Clamp(safeStartLine, 1, Math.Max(1, _model.LineCount)),
                    safeNewLineCount);
            var change = new EditorDocumentChange(
                DocumentId,
                sourceViewId,
                baseVersion,
                Version,
                safeStartLine,
                Math.Max(0, oldLineCount),
                Math.Max(1, _model.LineCount),
                lines);
            LastChange = change;
            Changed?.Invoke(this, change);
            return change;
        }

        internal EditorDocumentChange CommitLinePatches(
            string sourceViewId,
            IReadOnlyList<TextLinePatch> patches)
        {
            long baseVersion = Version;
            Version++;
            // All callers produce validated, line-number ordered patches. Reusing the
            // collection avoids copying and sorting hundreds of thousands of entries
            // on the UI thread after a large undo/redo operation.
            IReadOnlyList<TextLinePatch> safePatches = patches;
            int startLine = safePatches.Count > 0 ? safePatches[0].LineNumber : 1;
            var change = new EditorDocumentChange(
                DocumentId,
                sourceViewId,
                baseVersion,
                Version,
                startLine,
                0,
                Math.Max(1, _model.LineCount),
                Array.Empty<string>())
            {
                LinePatches = safePatches
            };
            LastChange = change;
            Changed?.Invoke(this, change);
            return change;
        }
    }

    public sealed class EditorDocumentSession
    {
        private const int MaxSearchMatches = 50_000;
        private static readonly TimeSpan MaxSearchElapsed = TimeSpan.FromSeconds(8);
        private EditorDocumentBuffer _buffer;

        public EditorDocumentSession(OpenedTab tab, ITextModel model)
        {
            Tab = tab;
            _buffer = new EditorDocumentBuffer(model);
            ViewVersion = _buffer.Version;
            Tab.Content = model is HexDumpTextModel ? string.Empty : model.GetText(120_000);
        }

        public OpenedTab Tab { get; }

        public ITextModel Model => _buffer.Model;

        public string DocumentId => _buffer.DocumentId;

        public long DocumentVersion => _buffer.Version;

        public long ViewVersion { get; private set; }

        public EditorDocumentChange? LastChange => _buffer.LastChange;

        public bool SharesDocumentWith(EditorDocumentSession other) =>
            ReferenceEquals(_buffer, other._buffer);

        public void ShareDocumentWith(EditorDocumentSession source)
        {
            _buffer = source._buffer;
            ViewVersion = _buffer.Version;
            RefreshTabContentPreview();
        }

        public void MarkViewSynchronized(long version)
        {
            ViewVersion = Math.Max(ViewVersion, version);
        }

        public async Task<bool> WaitForDocumentVersionAsync(long version, int timeoutMs = 700)
        {
            EditorDocumentBuffer buffer = _buffer;
            if (buffer.Version >= version) return true;

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<EditorDocumentChange>? handler = null;
            handler = (_, change) =>
            {
                if (change.Version >= version)
                {
                    tcs.TrySetResult(true);
                }
            };
            buffer.Changed += handler;
            try
            {
                if (buffer.Version >= version) return true;
                Task completed = await Task.WhenAny(
                    tcs.Task,
                    Task.Delay(Math.Max(80, timeoutMs)));
                return completed == tcs.Task && await tcs.Task;
            }
            finally
            {
                buffer.Changed -= handler;
            }
        }

        public void UpdateContentFromSync(string text)
        {
            EditorDocumentChange change = _buffer.ReplaceModel(
                Tab.Id,
                LineArrayTextModel.FromText(text),
                clearUndo: true);
            ViewVersion = change.Version;
            RefreshTabContentPreview();
        }

        public void UpdateModelFromSync(ITextModel model)
        {
            EditorDocumentChange change = _buffer.ReplaceModel(Tab.Id, model, clearUndo: true);
            ViewVersion = change.Version;
            RefreshTabContentPreview();
        }

        public void BeginUndoGroup()
        {
            _buffer.UndoManager.BeginTransaction("editor");
        }

        public void EndUndoGroup()
        {
            _buffer.UndoManager.EndTransaction();
        }

        public IReadOnlyList<string> GetLines(int startLine, int count) => Model.GetLines(startLine, count);

        public string GetText(int? maxChars = null) => Model.GetText(maxChars);

        public void ReplaceLine(int lineNumber, string text, bool trackUndo = true, bool isComposing = false)
        {
            if (lineNumber < 1 || lineNumber > Model.LineCount)
            {
                return;
            }

            // 조합 중 DOM은 뷰 로컬 상태다. 공유 문서 버퍼에는 compositionend의
            // 확정 편집만 들어오며, 예전 프로토콜의 중간 메시지도 여기서 폐기한다.
            if (isComposing)
            {
                return;
            }

            string before = Model.GetLine(lineNumber);
            string after = NormalizeSingleLine(text);

            if (string.Equals(before, after, StringComparison.Ordinal)) return;

            if (trackUndo)
            {
                _buffer.UndoManager.AddEdit(new ReplaceLineEdit(lineNumber, before, after));
            }

            Model.ReplaceLine(lineNumber, after);
            CommitViewChange(lineNumber, oldLineCount: 1, newLineCount: 1);
        }

        public int SplitLine(int lineNumber, string before, string after)
        {
            if (lineNumber < 1 || lineNumber > Model.LineCount)
            {
                return Model.LineCount;
            }

            string original = Model.GetLine(lineNumber);
            _buffer.UndoManager.AddEdit(new SplitLineEdit(
                lineNumber,
                original,
                NormalizeSingleLine(before),
                NormalizeSingleLine(after)));
            Model.SplitLine(lineNumber, before, after);
            CommitViewChange(lineNumber, oldLineCount: 1, newLineCount: 2);
            return Model.LineCount;
        }

        public int InsertLine(int lineNumber, string text)
        {
            int safeLineNumber = Math.Clamp(lineNumber, 1, Model.LineCount + 1);
            string inserted = NormalizeSingleLine(text);
            _buffer.UndoManager.AddEdit(new InsertLineEdit(safeLineNumber, inserted));
            Model.InsertLine(safeLineNumber, inserted);
            CommitViewChange(safeLineNumber, oldLineCount: 0, newLineCount: 1);
            return Model.LineCount;
        }

        public int ApplyRangeEdit(
            int startLine,
            int startColumn,
            int endLine,
            int endColumn,
            string text)
        {
            if (Model.LineCount <= 0)
            {
                return Model.LineCount;
            }

            int safeStartLine = Math.Clamp(startLine, 1, Model.LineCount);
            int safeEndLine = Math.Clamp(endLine, safeStartLine, Model.LineCount);
            string startText = Model.GetLine(safeStartLine);
            string endText = Model.GetLine(safeEndLine);
            int safeStartColumn = Math.Clamp(startColumn, 1, startText.Length + 1);
            int safeEndColumn = Math.Clamp(endColumn, 1, endText.Length + 1);
            if (safeStartLine == safeEndLine)
            {
                safeEndColumn = Math.Max(safeStartColumn, safeEndColumn);
            }

            string normalizedText = NormalizeText(text);
            var forward = new TextEdit(
                safeStartLine,
                safeStartColumn,
                safeEndLine,
                safeEndColumn,
                normalizedText);
            string replacedText = Model.GetTextRange(
                safeStartLine,
                safeStartColumn,
                safeEndLine,
                safeEndColumn);
            TextPosition replacementEnd = GetReplacementEnd(
                safeStartLine,
                safeStartColumn,
                normalizedText);
            var reverse = new TextEdit(
                safeStartLine,
                safeStartColumn,
                replacementEnd.LineNumber,
                replacementEnd.Column,
                replacedText);

            _buffer.UndoManager.AddEdit(new RangeTextEdit(forward, reverse));
            Model.ApplyEdit(forward);
            CommitViewChange(
                safeStartLine,
                oldLineCount: safeEndLine - safeStartLine + 1,
                newLineCount: normalizedText.Split('\n').Length);
            return Model.LineCount;
        }

        public int MergeLineWithPrevious(int lineNumber)
        {
            if (lineNumber <= 1 || lineNumber > Model.LineCount)
            {
                return Model.LineCount;
            }

            _buffer.UndoManager.AddEdit(new MergeLineEdit(
                lineNumber,
                Model.GetLine(lineNumber - 1),
                Model.GetLine(lineNumber)));
            Model.MergeLineWithPrevious(lineNumber);
            CommitViewChange(lineNumber - 1, oldLineCount: 2, newLineCount: 1);
            return Model.LineCount;
        }

        public int DeleteLine(int lineNumber)
        {
            if (lineNumber < 1 || lineNumber > Model.LineCount)
            {
                return Model.LineCount;
            }

            bool wasOnlyLine = Model.LineCount == 1;
            _buffer.UndoManager.AddEdit(new DeleteLineEdit(
                lineNumber,
                Model.GetLine(lineNumber),
                wasOnlyLine));
            Model.DeleteLine(lineNumber);
            CommitViewChange(lineNumber, oldLineCount: 1, newLineCount: wasOnlyLine ? 1 : 0);
            return Model.LineCount;
        }

        private static TextPosition GetReplacementEnd(int startLine, int startColumn, string text)
        {
            string[] lines = text.Split('\n');
            return lines.Length == 1
                ? new TextPosition(startLine, startColumn + lines[0].Length)
                : new TextPosition(startLine + lines.Length - 1, lines[^1].Length + 1);
        }

        private static string NormalizeText(string text)
        {
            return (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
        }

        public UndoResult? Undo()
        {
            UndoResult? result = _buffer.UndoManager.Undo(Model);
            if (result == null)
            {
                return null;
            }

            CommitUndoRedoChange(result);
            RefreshTabContentPreview();
            return result;
        }

        public UndoResult? Redo()
        {
            UndoResult? result = _buffer.UndoManager.Redo(Model);
            if (result == null)
            {
                return null;
            }

            CommitUndoRedoChange(result);
            RefreshTabContentPreview();
            return result;
        }

        public async Task<UndoResult?> UndoAsync(IProgress<TextOperationProgress>? progress = null)
        {
            UndoResult? result = await _buffer.UndoManager.UndoAsync(Model, progress);
            if (result == null)
            {
                return null;
            }

            CommitUndoRedoChange(result);
            RefreshTabContentPreview();
            return result;
        }

        public async Task<UndoResult?> RedoAsync(IProgress<TextOperationProgress>? progress = null)
        {
            UndoResult? result = await _buffer.UndoManager.RedoAsync(Model, progress);
            if (result == null)
            {
                return null;
            }

            CommitUndoRedoChange(result);
            RefreshTabContentPreview();
            return result;
        }

        public TextSearchResult? Find(string query, int startLine, int startColumn, bool reverse, bool matchCase, bool isRegex = false)
        {
            return Model.Find(query, startLine, startColumn, reverse, matchCase, isRegex);
        }

        public List<TextSearchResult> FindAll(string query, bool matchCase, bool isRegex = false, int currentLine = 1)
        {
            return Model.FindAll(query, matchCase, isRegex, currentLine);
        }

        public Task<List<TextSearchResult>> FindAllAsync(
            string query,
            bool matchCase,
            bool isRegex = false,
            int currentLine = 1,
            CancellationToken cancellationToken = default,
            IProgress<TextOperationProgress>? progress = null)
        {
            if (Model is LineArrayTextModel lineModel)
            {
                string[] lines = lineModel.CaptureLines();
                string lineEnding = lineModel.LineEnding;
                return Task.Run(
                    () => new LineArrayTextModel(lines, lineEnding)
                        .FindAllWithLimits(
                            query,
                            matchCase,
                            isRegex,
                            currentLine,
                            MaxSearchMatches,
                            cancellationToken,
                            progress,
                            MaxSearchElapsed),
                    cancellationToken);
            }

            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                List<TextSearchResult> results = Model.FindAll(
                    query,
                    matchCase,
                    isRegex,
                    currentLine,
                    MaxSearchMatches);
                cancellationToken.ThrowIfCancellationRequested();
                return results;
            }, cancellationToken);
        }

        public async Task<ReplaceAllResult> ReplaceAllAsync(
            string query,
            string replace,
            bool matchCase,
            bool isRegex,
            CancellationToken cancellationToken = default,
            IProgress<TextOperationProgress>? progress = null)
        {
            if (string.IsNullOrEmpty(query))
            {
                return new ReplaceAllResult(null, Array.Empty<LineReplacement>(), 0, TimeSpan.Zero);
            }

            long baseVersion = DocumentVersion;
            int lineCount = Model.LineCount;
            string[] snapshot = Model is LineArrayTextModel lineModel
                ? lineModel.CaptureLines()
                : Model.GetLines(1, lineCount).ToArray();
            (List<LineReplacement> Replacements, long MatchCount, TimeSpan Elapsed) plan = await Task.Run(() =>
                BuildReplaceAllPlan(
                    snapshot,
                    query,
                    replace,
                    matchCase,
                    isRegex,
                    cancellationToken,
                    progress),
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            if (DocumentVersion != baseVersion)
            {
                throw new OperationCanceledException("문서가 변경되어 모두 바꾸기 결과를 폐기했습니다.", cancellationToken);
            }

            if (plan.Replacements.Count == 0)
            {
                return new ReplaceAllResult(null, plan.Replacements, 0, plan.Elapsed);
            }

            _buffer.UndoManager.BeginTransaction("replaceAll");
            try
            {
                for (int i = 0; i < plan.Replacements.Count; i++)
                {
                    LineReplacement replacement = plan.Replacements[i];
                    _buffer.UndoManager.AddEdit(new ReplaceLineEdit(
                        replacement.LineNumber,
                        replacement.BeforeText,
                        replacement.AfterText));
                    Model.ReplaceLine(replacement.LineNumber, replacement.AfterText);
                    if ((i + 1) % 256 == 0)
                    {
                        await Task.Yield();
                    }
                }
            }
            finally
            {
                _buffer.UndoManager.EndTransaction();
            }

            CommitLinePatches(plan.Replacements.Select(replacement =>
                new TextLinePatch(replacement.LineNumber, replacement.AfterText)).ToArray());
            RefreshTabContentPreview();
            return new ReplaceAllResult(LastChange, plan.Replacements, plan.MatchCount, plan.Elapsed);
        }

        public void ReplaceAll(string query, string replace, bool matchCase, bool isRegex)
        {
            int originalLineCount = Model.LineCount;
            _buffer.UndoManager.BeginTransaction("replaceAll");
            bool changed = false;
            if (isRegex)
            {
                try
                {
                    var options = matchCase ? RegexOptions.None : RegexOptions.IgnoreCase;
                    var regex = new Regex(query, options, LineArrayTextModel.UserRegexTimeout);
                    for (int i = 1; i <= Model.LineCount; i++)
                    {
                        string original = Model.GetLine(i);
                        string nextText = regex.Replace(original, replace);
                        if (nextText != original)
                        {
                            _buffer.UndoManager.AddEdit(new ReplaceLineEdit(i, original, nextText));
                            Model.ReplaceLine(i, nextText);
                            changed = true;
                        }
                    }
                }
                catch (ArgumentException)
                {
                    // Ignore invalid regex
                }
            }
            else
            {
                var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                for (int i = 1; i <= Model.LineCount; i++)
                {
                    string original = Model.GetLine(i);
                    string nextText = ReplaceString(original, query, replace, comparison);
                    if (nextText != original)
                    {
                        _buffer.UndoManager.AddEdit(new ReplaceLineEdit(i, original, nextText));
                        Model.ReplaceLine(i, nextText);
                        changed = true;
                    }
                }
            }

            _buffer.UndoManager.EndTransaction();
            if (changed)
            {
                CommitViewChange(1, originalLineCount, Model.LineCount);
                RefreshTabContentPreview();
            }
        }

        private static string ReplaceString(string str, string oldValue, string newValue, StringComparison comparison)
        {
            if (string.IsNullOrEmpty(oldValue))
            {
                return str;
            }

            StringBuilder sb = new StringBuilder();
            int previousIndex = 0;
            int index = str.IndexOf(oldValue, comparison);
            while (index != -1)
            {
                sb.Append(str.Substring(previousIndex, index - previousIndex));
                sb.Append(newValue);
                previousIndex = index + oldValue.Length;
                index = str.IndexOf(oldValue, previousIndex, comparison);
            }
            sb.Append(str.Substring(previousIndex));
            return sb.ToString();
        }

        private static (List<LineReplacement> Replacements, long MatchCount, TimeSpan Elapsed) BuildReplaceAllPlan(
            IReadOnlyList<string> lines,
            string query,
            string replace,
            bool matchCase,
            bool isRegex,
            CancellationToken cancellationToken,
            IProgress<TextOperationProgress>? progress)
        {
            var stopwatch = Stopwatch.StartNew();
            var replacements = new List<LineReplacement>();
            long matchCount = 0;
            Regex? regex = null;
            if (isRegex)
            {
                var options = matchCase ? RegexOptions.None : RegexOptions.IgnoreCase;
                regex = new Regex(query, options, LineArrayTextModel.UserRegexTimeout);
            }

            StringComparison comparison = matchCase
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;

            for (int i = 0; i < lines.Count; i++)
            {
                if (i % 256 == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report(new TextOperationProgress(i, lines.Count, stopwatch.Elapsed));
                }

                string original = lines[i];
                int lineMatchCount = 0;
                string nextText;
                if (regex != null)
                {
                    nextText = regex.Replace(original, match =>
                    {
                        lineMatchCount++;
                        return match.Result(replace);
                    });
                }
                else
                {
                    nextText = ReplaceStringAndCount(original, query, replace, comparison, out lineMatchCount);
                }

                if (lineMatchCount == 0 || string.Equals(original, nextText, StringComparison.Ordinal))
                {
                    continue;
                }

                matchCount += lineMatchCount;
                replacements.Add(new LineReplacement(i + 1, original, nextText));
            }

            progress?.Report(new TextOperationProgress(lines.Count, lines.Count, stopwatch.Elapsed));
            return (replacements, matchCount, stopwatch.Elapsed);
        }

        private static string ReplaceStringAndCount(
            string value,
            string oldValue,
            string newValue,
            StringComparison comparison,
            out int replacementCount)
        {
            replacementCount = 0;
            if (string.IsNullOrEmpty(oldValue))
            {
                return value;
            }

            int index = value.IndexOf(oldValue, comparison);
            if (index < 0)
            {
                return value;
            }

            var builder = new StringBuilder(value.Length);
            int previousIndex = 0;
            while (index >= 0)
            {
                builder.Append(value, previousIndex, index - previousIndex);
                builder.Append(newValue);
                replacementCount++;
                previousIndex = index + oldValue.Length;
                index = value.IndexOf(oldValue, previousIndex, comparison);
            }

            builder.Append(value, previousIndex, value.Length - previousIndex);
            return builder.ToString();
        }

        public Task SaveAsync(string filePath, string encodingName, CancellationToken cancellationToken = default)
        {
            return Model.SaveAsync(filePath, encodingName, cancellationToken);
        }

        public Task SaveAsync(
            string filePath,
            string encodingName,
            CancellationToken cancellationToken,
            IProgress<TextOperationProgress>? progress)
        {
            if (Model is LineArrayTextModel lineModel)
            {
                return lineModel.SaveWithProgressAsync(filePath, encodingName, cancellationToken, progress);
            }

            progress?.Report(new TextOperationProgress(0, 1, TimeSpan.Zero));
            return SaveNonLineModelWithProgressAsync(
                Model,
                filePath,
                encodingName,
                cancellationToken,
                progress);
        }

        private static async Task SaveNonLineModelWithProgressAsync(
            ITextModel model,
            string filePath,
            string encodingName,
            CancellationToken cancellationToken,
            IProgress<TextOperationProgress>? progress)
        {
            await model.SaveAsync(filePath, encodingName, cancellationToken);
            progress?.Report(new TextOperationProgress(1, 1, TimeSpan.Zero));
        }

        public void RefreshTabContentPreview()
        {
            Tab.Content = Model is HexDumpTextModel ? string.Empty : Model.GetText(120_000);
        }

        private static string NormalizeSingleLine(string text)
        {
            return (text ?? string.Empty).Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
        }

        private void CommitViewChange(int startLine, int oldLineCount, int newLineCount)
        {
            EditorDocumentChange change = _buffer.CommitChange(
                Tab.Id,
                startLine,
                oldLineCount,
                newLineCount);
            ViewVersion = change.Version;
        }

        private void CommitLinePatches(IReadOnlyList<TextLinePatch> patches)
        {
            EditorDocumentChange change = _buffer.CommitLinePatches(Tab.Id, patches);
            ViewVersion = change.Version;
        }

        private void CommitUndoRedoChange(UndoResult result)
        {
            if (result.LinePatches is { Count: > 0 } patches)
            {
                CommitLinePatches(patches);
                return;
            }

            CommitViewChange(result.StartLine, result.OldLineCount, result.NewLineCount);
        }
    }
}
