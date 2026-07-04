using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TxtAIEditor.Editor
{
    public sealed class HexDumpTextModel : ITextModel
    {
        private const int BytesPerRow = 16;
        private const string HeaderOffsetLabel = "Offset(h)";
        private readonly string _filePath;
        private readonly long _fileLength;
        private readonly int _offsetWidth;
        private readonly long _displayRowCount;
        private readonly bool _isTruncated;
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

            FileStream? stream = null;
            try
            {
                if (RangeIncludesHexRows(safeStart, safeEnd))
                {
                    stream = new FileStream(
                        _filePath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete,
                        bufferSize: 4096,
                        useAsync: false);
                }

                for (int lineNumber = safeStart; lineNumber <= safeEnd; lineNumber++)
                {
                    lines.Add(FormatLine(lineNumber, stream));
                }
            }
            finally
            {
                stream?.Dispose();
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

            int limit = maxChars ?? int.MaxValue;
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

        public List<TextSearchResult> FindAll(string query, bool matchCase, bool isRegex = false)
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
            for (int lineNumber = 1; lineNumber <= LineCount; lineNumber++)
            {
                string line = GetLine(lineNumber);
                if (regex != null)
                {
                    foreach (Match match in regex.Matches(line))
                    {
                        if (match.Length > 0)
                        {
                            results.Add(new TextSearchResult(lineNumber, match.Index, match.Length, line));
                        }
                    }
                    continue;
                }

                int searchStart = 0;
                while (searchStart <= line.Length)
                {
                    int index = line.IndexOf(query, searchStart, comparison);
                    if (index < 0)
                    {
                        break;
                    }

                    results.Add(new TextSearchResult(lineNumber, index, query.Length, line));
                    searchStart = index + 1;
                }
            }

            return results;
        }

        public Task SaveAsync(string filePath, string encodingName, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("Hex dump views are read-only.");
        }

        private string FormatLine(int lineNumber, FileStream? stream)
        {
            if (lineNumber == 1)
            {
                return FormatHeaderLine();
            }

            if (_isTruncated && lineNumber == LineCount)
            {
                return "The file is too large to display completely in this hex view.";
            }

            if (stream == null)
            {
                return "The source file is not available.";
            }

            long offset = (long)(lineNumber - 2) * BytesPerRow;
            Span<byte> bytes = stackalloc byte[BytesPerRow];
            int read = ReadRow(stream, offset, bytes);
            return FormatHexRow(offset, bytes[..read]);
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

        private static int ReadRow(FileStream stream, long offset, Span<byte> bytes)
        {
            if (offset >= stream.Length)
            {
                return 0;
            }

            stream.Seek(offset, SeekOrigin.Begin);
            int totalRead = 0;
            while (totalRead < bytes.Length)
            {
                int read = stream.Read(bytes[totalRead..]);
                if (read == 0)
                {
                    break;
                }

                totalRead += read;
            }

            return totalRead;
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

        private bool RangeIncludesHexRows(int startLine, int endLine)
        {
            int firstDataLine = 2;
            int lastDataLine = 1 + (int)_displayRowCount;
            return endLine >= firstDataLine && startLine <= lastDataLine;
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
