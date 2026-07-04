using System;
using System.Collections.Generic;
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
        List<TextSearchResult> FindAll(string query, bool matchCase, bool isRegex = false);
        Task SaveAsync(string filePath, string encodingName, CancellationToken cancellationToken = default);
    }

    public sealed class LineArrayTextModel : ITextModel
    {
        private readonly List<string> _lines;

        public LineArrayTextModel(IEnumerable<string>? lines = null, string lineEnding = "\n")
        {
            _lines = lines?.ToList() ?? new List<string>();
            if (_lines.Count == 0)
            {
                _lines.Add(string.Empty);
            }

            LineEnding = string.IsNullOrEmpty(lineEnding) ? "\n" : lineEnding;
        }

        public int LineCount => _lines.Count;

        public string LineEnding { get; set; }

        public static LineArrayTextModel FromText(string text)
        {
            string lineEnding = DetectLineEnding(text);
            string normalized = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
            return new LineArrayTextModel(normalized.Split('\n'), lineEnding);
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
            offset = Math.Max(0, offset);
            int remaining = offset;
            for (int i = 0; i < _lines.Count; i++)
            {
                int lineLengthWithBreak = _lines[i].Length + LineEnding.Length;
                if (remaining <= _lines[i].Length || i == _lines.Count - 1)
                {
                    return new TextPosition(i + 1, Math.Min(remaining, _lines[i].Length) + 1);
                }

                remaining -= lineLengthWithBreak;
            }

            string last = _lines[^1];
            return new TextPosition(_lines.Count, last.Length + 1);
        }

        public int GetOffsetAt(int lineNumber, int column)
        {
            int safeLine = Math.Clamp(lineNumber, 1, _lines.Count);
            int offset = 0;
            for (int i = 0; i < safeLine - 1; i++)
            {
                offset += _lines[i].Length + LineEnding.Length;
            }

            string line = _lines[safeLine - 1];
            return offset + ClampColumn(line, column) - 1;
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
        }

        public void ReplaceLine(int lineNumber, string text)
        {
            if (lineNumber < 1 || lineNumber > _lines.Count)
            {
                return;
            }

            _lines[lineNumber - 1] = NormalizeSingleLine(text);
        }

        public void InsertLine(int lineNumber, string text)
        {
            int index = Math.Clamp(lineNumber - 1, 0, _lines.Count);
            _lines.Insert(index, NormalizeSingleLine(text));
        }

        public void DeleteLine(int lineNumber)
        {
            if (lineNumber < 1 || lineNumber > _lines.Count)
            {
                return;
            }

            _lines.RemoveAt(lineNumber - 1);
            EnsureAtLeastOneLine();
        }

        public void SplitLine(int lineNumber, string before, string after)
        {
            if (lineNumber < 1 || lineNumber > _lines.Count)
            {
                return;
            }

            _lines[lineNumber - 1] = NormalizeSingleLine(before);
            _lines.Insert(lineNumber, NormalizeSingleLine(after));
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
                    regex = new Regex(query, regexOptions);
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

        public List<TextSearchResult> FindAll(string query, bool matchCase, bool isRegex = false)
        {
            if (string.IsNullOrEmpty(query))
            {
                return new List<TextSearchResult>();
            }

            if (isRegex)
            {
                Regex regex;
                try
                {
                    var regexOptions = matchCase ? RegexOptions.None : RegexOptions.IgnoreCase;
                    regex = new Regex(query, regexOptions);
                }
                catch (ArgumentException)
                {
                    return new List<TextSearchResult>();
                }

                var results = new List<TextSearchResult>();
                for (int lineNumber = 1; lineNumber <= _lines.Count; lineNumber++)
                {
                    string line = _lines[lineNumber - 1];
                    if (line.Length == 0) continue;

                    var matches = regex.Matches(line);
                    foreach (Match match in matches)
                    {
                        if (match.Length > 0)
                        {
                            results.Add(new TextSearchResult(lineNumber, match.Index, match.Length, line));
                        }
                    }
                }
                return results;
            }

            var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var resultsNormal = new List<TextSearchResult>();

            for (int lineNumber = 1; lineNumber <= _lines.Count; lineNumber++)
            {
                string line = _lines[lineNumber - 1];
                if (line.Length == 0) continue;

                int searchStart = 0;
                while (searchStart <= line.Length)
                {
                    int index = line.IndexOf(query, searchStart, comparison);
                    if (index < 0) break;

                    resultsNormal.Add(new TextSearchResult(lineNumber, index, query.Length, line));
                    searchStart = index + 1;
                }
            }

            return resultsNormal;
        }

        public async Task SaveAsync(string filePath, string encodingName, CancellationToken cancellationToken = default)
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
                using (var writer = new StreamWriter(tempFilePath, append: false, encoding))
                {
                    for (int i = 0; i < _lines.Count; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (i > 0)
                        {
                        await writer.WriteAsync(LineEnding.AsMemory(), cancellationToken).ConfigureAwait(false);
                        }

                        await writer.WriteAsync(_lines[i].AsMemory(), cancellationToken).ConfigureAwait(false);
                    }
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

            int crlf = Regex.Matches(text, "\r\n").Count;
            int lf = Regex.Matches(text.Replace("\r\n", string.Empty), "\n").Count;
            int cr = Regex.Matches(text.Replace("\r\n", string.Empty), "\r").Count;
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
    }

    public sealed record EditorDocumentLoadResult(
        ITextModel Model,
        string EncodingName,
        bool EncodingWasAutoDetected);

    public sealed class EditorDocumentSession
    {
        private readonly UndoManager _undoManager = new();
        private readonly Dictionary<int, string> _compositionBeforeLines = new();

        public EditorDocumentSession(OpenedTab tab, ITextModel model)
        {
            Tab = tab;
            Model = model;
            Tab.Content = model.GetText(120_000);
        }

        public OpenedTab Tab { get; }

        public ITextModel Model { get; private set; }

        public void UpdateContentFromSync(string text)
        {
            Model = LineArrayTextModel.FromText(text);
            _undoManager.Clear();
            _compositionBeforeLines.Clear();
            RefreshTabContentPreview();
        }

        public void UpdateModelFromSync(ITextModel model)
        {
            Model = model;
            _undoManager.Clear();
            _compositionBeforeLines.Clear();
            RefreshTabContentPreview();
        }

        public void BeginUndoGroup()
        {
            _undoManager.BeginTransaction("editor");
        }

        public void EndUndoGroup()
        {
            _undoManager.EndTransaction();
        }

        public IReadOnlyList<string> GetLines(int startLine, int count) => Model.GetLines(startLine, count);

        public string GetText(int? maxChars = null) => Model.GetText(maxChars);

        public void ReplaceLine(int lineNumber, string text, bool trackUndo = true, bool isComposing = false)
        {
            if (lineNumber < 1 || lineNumber > Model.LineCount)
            {
                return;
            }

            string before = Model.GetLine(lineNumber);
            string after = NormalizeSingleLine(text);

            if (isComposing)
            {
                if (!_compositionBeforeLines.ContainsKey(lineNumber))
                {
                    _compositionBeforeLines[lineNumber] = before;
                }

                Model.ReplaceLine(lineNumber, after);
                RefreshTabContentPreview();
                return;
            }

            if (_compositionBeforeLines.Remove(lineNumber, out string? compositionBefore))
            {
                before = compositionBefore;
            }

            if (trackUndo && !string.Equals(before, after, StringComparison.Ordinal))
            {
                _undoManager.AddEdit(new ReplaceLineEdit(lineNumber, before, after));
            }

            Model.ReplaceLine(lineNumber, after);
            RefreshTabContentPreview();
        }

        public int SplitLine(int lineNumber, string before, string after)
        {
            if (lineNumber < 1 || lineNumber > Model.LineCount)
            {
                return Model.LineCount;
            }

            string original = Model.GetLine(lineNumber);
            _undoManager.AddEdit(new SplitLineEdit(
                lineNumber,
                original,
                NormalizeSingleLine(before),
                NormalizeSingleLine(after)));
            _compositionBeforeLines.Clear();
            Model.SplitLine(lineNumber, before, after);
            RefreshTabContentPreview();
            return Model.LineCount;
        }

        public int InsertLine(int lineNumber, string text)
        {
            int safeLineNumber = Math.Clamp(lineNumber, 1, Model.LineCount + 1);
            string inserted = NormalizeSingleLine(text);
            _undoManager.AddEdit(new InsertLineEdit(safeLineNumber, inserted));
            _compositionBeforeLines.Clear();
            Model.InsertLine(safeLineNumber, inserted);
            RefreshTabContentPreview();
            return Model.LineCount;
        }

        public int MergeLineWithPrevious(int lineNumber)
        {
            if (lineNumber <= 1 || lineNumber > Model.LineCount)
            {
                return Model.LineCount;
            }

            _undoManager.AddEdit(new MergeLineEdit(
                lineNumber,
                Model.GetLine(lineNumber - 1),
                Model.GetLine(lineNumber)));
            _compositionBeforeLines.Clear();
            Model.MergeLineWithPrevious(lineNumber);
            RefreshTabContentPreview();
            return Model.LineCount;
        }

        public int DeleteLine(int lineNumber)
        {
            if (lineNumber < 1 || lineNumber > Model.LineCount)
            {
                return Model.LineCount;
            }

            _undoManager.AddEdit(new DeleteLineEdit(
                lineNumber,
                Model.GetLine(lineNumber),
                Model.LineCount == 1));
            _compositionBeforeLines.Clear();
            Model.DeleteLine(lineNumber);
            RefreshTabContentPreview();
            return Model.LineCount;
        }

        public UndoResult? Undo()
        {
            UndoResult? result = _undoManager.Undo(Model);
            if (result == null)
            {
                return null;
            }

            _compositionBeforeLines.Clear();
            RefreshTabContentPreview();
            return result;
        }

        public UndoResult? Redo()
        {
            UndoResult? result = _undoManager.Redo(Model);
            if (result == null)
            {
                return null;
            }

            _compositionBeforeLines.Clear();
            RefreshTabContentPreview();
            return result;
        }

        public TextSearchResult? Find(string query, int startLine, int startColumn, bool reverse, bool matchCase, bool isRegex = false)
        {
            return Model.Find(query, startLine, startColumn, reverse, matchCase, isRegex);
        }

        public List<TextSearchResult> FindAll(string query, bool matchCase, bool isRegex = false)
        {
            return Model.FindAll(query, matchCase, isRegex);
        }

        public void ReplaceAll(string query, string replace, bool matchCase, bool isRegex)
        {
            _undoManager.BeginTransaction("replaceAll");
            bool changed = false;
            if (isRegex)
            {
                try
                {
                    var options = matchCase ? RegexOptions.None : RegexOptions.IgnoreCase;
                    var regex = new Regex(query, options);
                    for (int i = 1; i <= Model.LineCount; i++)
                    {
                        string original = Model.GetLine(i);
                        string nextText = regex.Replace(original, replace);
                        if (nextText != original)
                        {
                            _undoManager.AddEdit(new ReplaceLineEdit(i, original, nextText));
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
                        _undoManager.AddEdit(new ReplaceLineEdit(i, original, nextText));
                        Model.ReplaceLine(i, nextText);
                        changed = true;
                    }
                }
            }

            _undoManager.EndTransaction();
            if (changed)
            {
                _compositionBeforeLines.Clear();
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

        public Task SaveAsync(string filePath, string encodingName, CancellationToken cancellationToken = default)
        {
            return Model.SaveAsync(filePath, encodingName, cancellationToken);
        }

        private void RefreshTabContentPreview()
        {
            Tab.Content = Model.GetText(120_000);
        }

        private static string NormalizeSingleLine(string text)
        {
            return (text ?? string.Empty).Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
        }
    }
}
