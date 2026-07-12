using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TxtAIEditor.Editor
{
    public sealed class HexDumpTextModel : ITextModel
    {
        private const int BytesPerRow = 16;
        private const string HeaderOffsetLabel = "Offset(h)";
        private string _filePath;
        private readonly long _fileLength;
        private readonly int _offsetWidth;
        private readonly long _displayRowCount;
        private readonly bool _isTruncated;
        private readonly Dictionary<long, byte> _editedBytes = new();
        private string _lineEnding = "\n";

        public HexDumpTextModel(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("File path is required.", nameof(filePath));
            }

            _filePath = filePath;
            _fileLength = new FileInfo(filePath).Length;
            _offsetWidth = Math.Max(HeaderOffsetLabel.Length, _fileLength > uint.MaxValue ? 16 : 8);

            long rowCount = Math.Max(1, (_fileLength + BytesPerRow - 1) / BytesPerRow);
            long maxRows = int.MaxValue - 2L;
            _displayRowCount = Math.Min(rowCount, maxRows);
            _isTruncated = rowCount > maxRows;
        }

        public bool HasPendingEdits => _editedBytes.Count > 0;

        public int LineCount => 1 + (int)_displayRowCount + (_isTruncated ? 1 : 0);

        public string LineEnding
        {
            get => _lineEnding;
            set => _lineEnding = string.IsNullOrEmpty(value) ? "\n" : value;
        }

        public string GetLine(int lineNumber)
        {
            return GetLines(lineNumber, 1) is [var line] ? line : string.Empty;
        }

        public IReadOnlyList<string> GetLines(int startLine, int count)
        {
            if (count <= 0 || startLine > LineCount)
            {
                return Array.Empty<string>();
            }

            int safeStart = Math.Max(1, startLine);
            int safeEnd = Math.Min(LineCount, safeStart + count - 1);
            var lines = new List<string>(safeEnd - safeStart + 1);

            int firstDataLine = Math.Max(2, safeStart);
            int lastDataLine = Math.Min(1 + (int)_displayRowCount, safeEnd);
            byte[] data = Array.Empty<byte>();
            int dataLength = 0;
            if (firstDataLine <= lastDataLine)
            {
                long firstOffset = (long)(firstDataLine - 2) * BytesPerRow;
                int requestedLength = checked((lastDataLine - firstDataLine + 1) * BytesPerRow);
                int availableLength = (int)Math.Min(requestedLength, Math.Max(0, _fileLength - firstOffset));
                data = new byte[availableLength];

                using var stream = new FileStream(
                    _filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 64 * 1024,
                    useAsync: false);
                stream.Seek(firstOffset, SeekOrigin.Begin);
                while (dataLength < data.Length)
                {
                    int read = stream.Read(data, dataLength, data.Length - dataLength);
                    if (read == 0)
                    {
                        break;
                    }

                    dataLength += read;
                }

                for (int i = 0; i < dataLength; i++)
                {
                    if (_editedBytes.TryGetValue(firstOffset + i, out byte editedByte))
                    {
                        data[i] = editedByte;
                    }
                }
            }

            for (int lineNumber = safeStart; lineNumber <= safeEnd; lineNumber++)
            {
                if (lineNumber == 1)
                {
                    lines.Add(FormatHeaderLine());
                    continue;
                }

                if (_isTruncated && lineNumber == LineCount)
                {
                    lines.Add("The file is too large to display completely in this hex view.");
                    continue;
                }

                int rowIndex = lineNumber - firstDataLine;
                int dataOffset = rowIndex * BytesPerRow;
                int rowLength = Math.Min(BytesPerRow, Math.Max(0, dataLength - dataOffset));
                long fileOffset = (long)(lineNumber - 2) * BytesPerRow;
                ReadOnlySpan<byte> rowBytes = dataOffset >= 0 && dataOffset <= dataLength
                    ? data.AsSpan(dataOffset, rowLength)
                    : ReadOnlySpan<byte>.Empty;
                lines.Add(FormatHexRow(fileOffset, rowBytes));
            }

            return lines;
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

            int limit = Math.Min(maxChars ?? 120_000, 120_000);
            var builder = new StringBuilder(Math.Min(limit, 128 * 1024));
            int lineNumber = 1;

            while (lineNumber <= LineCount && builder.Length < limit)
            {
                int batchCount = Math.Min(256, LineCount - lineNumber + 1);
                foreach (string line in GetLines(lineNumber, batchCount))
                {
                    if (builder.Length > 0)
                    {
                        AppendBounded(builder, LineEnding, limit);
                    }

                    AppendBounded(builder, line, limit);
                    if (builder.Length >= limit)
                    {
                        break;
                    }
                }

                lineNumber += batchCount;
            }

            return builder.ToString();
        }

        public TextPosition GetPositionAt(int offset)
        {
            offset = Math.Max(0, offset);
            int remaining = offset;

            for (int lineNumber = 1; lineNumber <= LineCount; lineNumber++)
            {
                string line = GetLine(lineNumber);
                if (remaining <= line.Length || lineNumber == LineCount)
                {
                    return new TextPosition(lineNumber, Math.Min(remaining, line.Length) + 1);
                }

                remaining -= line.Length + LineEnding.Length;
            }

            string last = GetLine(LineCount);
            return new TextPosition(LineCount, last.Length + 1);
        }

        public int GetOffsetAt(int lineNumber, int column)
        {
            int safeLine = Math.Clamp(lineNumber, 1, LineCount);
            int offset = 0;
            for (int i = 1; i < safeLine; i++)
            {
                offset += GetLine(i).Length + LineEnding.Length;
            }

            string line = GetLine(safeLine);
            return offset + ClampColumn(line, column) - 1;
        }

        public void ApplyEdit(TextEdit edit)
        {
        }

        public int ApplyByteEdit(long offset, ReadOnlySpan<byte> bytes)
        {
            if (offset < 0 || offset >= _fileLength || bytes.IsEmpty)
            {
                return 0;
            }

            int writableCount = (int)Math.Min(bytes.Length, _fileLength - offset);
            for (int i = 0; i < writableCount; i++)
            {
                _editedBytes[offset + i] = bytes[i];
            }

            return writableCount;
        }

        public void ReplaceLine(int lineNumber, string text)
        {
        }

        public void InsertLine(int lineNumber, string text)
        {
        }

        public void DeleteLine(int lineNumber)
        {
        }

        public void SplitLine(int lineNumber, string before, string after)
        {
        }

        public void MergeLineWithPrevious(int lineNumber)
        {
        }

        public TextSearchResult? Find(string query, int startLine, int startColumn, bool reverse, bool matchCase, bool isRegex = false)
        {
            if (string.IsNullOrEmpty(query))
            {
                return null;
            }

            Regex? regex = null;
            if (isRegex)
            {
                try
                {
                    regex = new Regex(query, matchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
                }
                catch (ArgumentException)
                {
                    return null;
                }
            }

            var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            int safeLine = Math.Clamp(startLine, 1, LineCount);

            if (reverse)
            {
                for (int lineNumber = safeLine; lineNumber >= 1; lineNumber--)
                {
                    string line = GetLine(lineNumber);
                    int searchStart = lineNumber == safeLine
                        ? Math.Clamp(startColumn - 2, 0, Math.Max(0, line.Length - 1))
                        : Math.Max(0, line.Length - 1);

                    if (TryFindInLine(line, query, searchStart, reverse: true, comparison, regex) is { } match)
                    {
                        return new TextSearchResult(lineNumber, match.IndexOfMatch, match.MatchLength, line);
                    }
                }

                return null;
            }

            for (int lineNumber = safeLine; lineNumber <= LineCount; lineNumber++)
            {
                string line = GetLine(lineNumber);
                int searchStart = lineNumber == safeLine
                    ? Math.Clamp(startColumn - 1, 0, line.Length)
                    : 0;

                if (TryFindInLine(line, query, searchStart, reverse: false, comparison, regex) is { } match)
                {
                    return new TextSearchResult(lineNumber, match.IndexOfMatch, match.MatchLength, line);
                }
            }

            return null;
        }

        public List<TextSearchResult> FindAll(string query, bool matchCase, bool isRegex = false, int currentLine = 1, int maxMatches = 50000)
        {
            var results = new List<TextSearchResult>();
            if (string.IsNullOrEmpty(query))
            {
                return results;
            }

            Regex? regex = null;
            if (isRegex)
            {
                try
                {
                    regex = new Regex(query, matchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
                }
                catch (ArgumentException)
                {
                    return results;
                }
            }

            var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            int startLine = Math.Clamp(currentLine, 1, LineCount);
            int winStart = Math.Max(1, startLine - 200);
            int winEnd = Math.Min(LineCount, startLine + 200);

            void SearchLine(int lineNumber)
            {
                string line = GetLine(lineNumber);
                if (regex != null)
                {
                    foreach (Match match in regex.Matches(line))
                    {
                        if (match.Length > 0)
                        {
                            results.Add(new TextSearchResult(lineNumber, match.Index, match.Length, line));
                            if (results.Count >= maxMatches) break;
                        }
                    }
                }
                else
                {
                    int searchStart = 0;
                    while (searchStart <= line.Length)
                    {
                        int index = line.IndexOf(query, searchStart, comparison);
                        if (index < 0) break;

                        results.Add(new TextSearchResult(lineNumber, index, query.Length, line));
                        if (results.Count >= maxMatches) break;
                        searchStart = index + 1;
                    }
                }
            }

            // 1. Nearby Window
            for (int lineNumber = winStart; lineNumber <= winEnd; lineNumber++)
            {
                SearchLine(lineNumber);
                if (results.Count >= maxMatches) break;
            }

            // 2. Remaining Below
            if (results.Count < maxMatches && winEnd < LineCount)
            {
                for (int lineNumber = winEnd + 1; lineNumber <= LineCount; lineNumber++)
                {
                    SearchLine(lineNumber);
                    if (results.Count >= maxMatches) break;
                }
            }

            // 3. Remaining Above
            if (results.Count < maxMatches && winStart > 1)
            {
                for (int lineNumber = 1; lineNumber < winStart; lineNumber++)
                {
                    SearchLine(lineNumber);
                    if (results.Count >= maxMatches) break;
                }
            }

            return results.OrderBy(r => r.LineNumber).ThenBy(r => r.IndexOfMatch).ToList();
        }

        public Task SaveAsync(string filePath, string encodingName, CancellationToken cancellationToken = default)
        {
            return SaveEditedBytesAsync(filePath, cancellationToken);
        }

        private Task SaveEditedBytesAsync(string filePath, CancellationToken cancellationToken)
        {
            if (!string.Equals(
                    Path.GetFullPath(filePath),
                    Path.GetFullPath(_filePath),
                    StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(_filePath, filePath, overwrite: true);
            }

            if (_editedBytes.Count == 0)
            {
                _filePath = filePath;
                return Task.CompletedTask;
            }

            return Task.Run(() =>
            {
                using var stream = new FileStream(
                    filePath,
                    FileMode.Open,
                    FileAccess.Write,
                    FileShare.ReadWrite,
                    bufferSize: 64 * 1024,
                    useAsync: false);

                var edits = _editedBytes.OrderBy(pair => pair.Key).ToArray();
                int editIndex = 0;
                while (editIndex < edits.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    long runOffset = edits[editIndex].Key;
                    int runEnd = editIndex + 1;
                    while (runEnd < edits.Length && edits[runEnd].Key == edits[runEnd - 1].Key + 1)
                    {
                        runEnd++;
                    }

                    byte[] run = new byte[runEnd - editIndex];
                    for (int i = editIndex; i < runEnd; i++)
                    {
                        run[i - editIndex] = edits[i].Value;
                    }

                    stream.Position = runOffset;
                    stream.Write(run, 0, run.Length);
                    editIndex = runEnd;
                }

                stream.Flush();
                _editedBytes.Clear();
                _filePath = filePath;
            }, cancellationToken);
        }

        private string FormatHeaderLine()
        {
            var builder = new StringBuilder();
            builder.Append(HeaderOffsetLabel.PadLeft(_offsetWidth));
            builder.Append("  ");

            for (int i = 0; i < BytesPerRow; i++)
            {
                if (i == 8)
                {
                    builder.Append(' ');
                }

                builder.Append(i.ToString("X2"));
                builder.Append(' ');
            }

            builder.Append(" |ASCII");
            return builder.ToString();
        }

        private string FormatHexRow(long offset, ReadOnlySpan<byte> bytes)
        {
            var builder = new StringBuilder(_offsetWidth + 68);
            string offsetText = offset.ToString(_offsetWidth > 9 ? "X16" : "X8");
            builder.Append(offsetText.PadLeft(_offsetWidth));
            builder.Append("  ");

            for (int i = 0; i < BytesPerRow; i++)
            {
                if (i == 8)
                {
                    builder.Append(' ');
                }

                if (i < bytes.Length)
                {
                    builder.Append(bytes[i].ToString("X2"));
                }
                else
                {
                    builder.Append("  ");
                }

                builder.Append(' ');
            }

            builder.Append(" |");
            for (int i = 0; i < bytes.Length; i++)
            {
                byte b = bytes[i];
                builder.Append(b >= 0x20 && b <= 0x7E ? (char)b : '.');
            }

            for (int i = bytes.Length; i < BytesPerRow; i++)
            {
                builder.Append(' ');
            }

            builder.Append('|');
            return builder.ToString();
        }

        private static TextSearchResult? TryFindInLine(
            string line,
            string query,
            int searchStart,
            bool reverse,
            StringComparison comparison,
            Regex? regex)
        {
            if (regex != null)
            {
                MatchCollection matches = regex.Matches(line);
                if (reverse)
                {
                    for (int i = matches.Count - 1; i >= 0; i--)
                    {
                        Match match = matches[i];
                        if (match.Length > 0 && match.Index <= searchStart)
                        {
                            return new TextSearchResult(0, match.Index, match.Length, line);
                        }
                    }
                }
                else
                {
                    foreach (Match match in matches)
                    {
                        if (match.Length > 0 && match.Index >= searchStart)
                        {
                            return new TextSearchResult(0, match.Index, match.Length, line);
                        }
                    }
                }

                return null;
            }

            int index = reverse
                ? line.LastIndexOf(query, searchStart, comparison)
                : line.IndexOf(query, searchStart, comparison);
            return index >= 0
                ? new TextSearchResult(0, index, query.Length, line)
                : null;
        }

        private void NormalizeRange(ref int startLine, ref int startColumn, ref int endLine, ref int endColumn)
        {
            startLine = Math.Clamp(startLine, 1, LineCount);
            endLine = Math.Clamp(endLine, 1, LineCount);
            startColumn = ClampColumn(GetLine(startLine), startColumn);
            endColumn = ClampColumn(GetLine(endLine), endColumn);

            if (endLine < startLine || (endLine == startLine && endColumn < startColumn))
            {
                (startLine, endLine) = (endLine, startLine);
                (startColumn, endColumn) = (endColumn, startColumn);
            }
        }

        private static int ClampColumn(string line, int column)
        {
            return Math.Clamp(column, 1, line.Length + 1);
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
    }
}
