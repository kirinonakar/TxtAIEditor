using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TxtAIEditor.Core.Services;

namespace TxtAIEditor.Editor
{
    public static class TextModelFactory
    {
        public const int ChunkedLineThreshold = 50_000;

        public static ITextModel Create(IEnumerable<string>? lines, string lineEnding = "\n")
        {
            if (lines is IReadOnlyCollection<string> collection &&
                collection.Count >= ChunkedLineThreshold)
            {
                return new ChunkedLineTextModel(lines, lineEnding);
            }

            string[] snapshot = lines?.ToArray() ?? Array.Empty<string>();
            return snapshot.Length >= ChunkedLineThreshold
                ? new ChunkedLineTextModel(snapshot, lineEnding)
                : new LineArrayTextModel(snapshot, lineEnding);
        }

        public static ITextModel FromText(string text)
        {
            string value = text ?? string.Empty;
            string lineEnding = DetectLineEnding(value);
            string normalized = value.Replace("\r\n", "\n").Replace('\r', '\n');
            return Create(normalized.Split('\n'), lineEnding);
        }

        private static string DetectLineEnding(string text)
        {
            int crlf = text.IndexOf("\r\n", StringComparison.Ordinal);
            if (crlf >= 0) return "\r\n";
            return text.IndexOf('\r') >= 0 ? "\r" : "\n";
        }
    }

    public sealed class ChunkedLineTextModel : ITextModel
    {
        private const int TargetLeafLines = 512;
        private const int MaxLeafLines = 1_024;
        private readonly List<List<string>> _leaves = new();
        private readonly List<int> _lineEnds = new();
        private readonly List<long> _characterEnds = new();
        private string _lineEnding;

        public ChunkedLineTextModel(IEnumerable<string>? lines = null, string lineEnding = "\n")
        {
            _lineEnding = string.IsNullOrEmpty(lineEnding) ? "\n" : lineEnding;
            AppendLeaves(lines?.Select(NormalizeSingleLine) ?? Array.Empty<string>());
            EnsureAtLeastOneLine();
            RebuildMetadata();
        }

        public int LineCount => _lineEnds.Count == 0 ? 0 : _lineEnds[^1];

        public string LineEnding
        {
            get => _lineEnding;
            set
            {
                string next = string.IsNullOrEmpty(value) ? "\n" : value;
                if (string.Equals(next, _lineEnding, StringComparison.Ordinal)) return;
                _lineEnding = next;
                RebuildMetadata();
            }
        }

        public string GetLine(int lineNumber)
        {
            if (lineNumber < 1 || lineNumber > LineCount) return string.Empty;
            (int leafIndex, int innerIndex) = LocateLine(lineNumber);
            return _leaves[leafIndex][innerIndex];
        }

        public IReadOnlyList<string> GetLines(int startLine, int count)
        {
            if (count <= 0 || startLine > LineCount) return Array.Empty<string>();
            int first = Math.Max(1, startLine);
            int remaining = Math.Min(count, LineCount - first + 1);
            var result = new List<string>(remaining);
            (int leafIndex, int innerIndex) = LocateLine(first);
            while (remaining > 0 && leafIndex < _leaves.Count)
            {
                List<string> leaf = _leaves[leafIndex];
                int take = Math.Min(remaining, leaf.Count - innerIndex);
                result.AddRange(leaf.GetRange(innerIndex, take));
                remaining -= take;
                leafIndex++;
                innerIndex = 0;
            }
            return result;
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

            var parts = new List<string> { GetLine(startLine)[(ClampColumn(GetLine(startLine), startColumn) - 1)..] };
            for (int line = startLine + 1; line < endLine; line++) parts.Add(GetLine(line));
            string last = GetLine(endLine);
            parts.Add(last[..(ClampColumn(last, endColumn) - 1)]);
            return string.Join(LineEnding, parts);
        }

        public string GetText(int? maxChars = null)
        {
            if (maxChars is <= 0) return string.Empty;
            int limit = maxChars ?? int.MaxValue;
            var builder = new StringBuilder(Math.Min(limit, 128 * 1024));
            int lineNumber = 0;
            foreach (List<string> leaf in _leaves)
            {
                foreach (string line in leaf)
                {
                    if (lineNumber++ > 0) AppendBounded(builder, LineEnding, limit);
                    AppendBounded(builder, line, limit);
                    if (builder.Length >= limit) return builder.ToString();
                }
            }
            return builder.ToString();
        }

        public TextPosition GetPositionAt(int offset)
        {
            long documentLength = Math.Max(0, TotalStoredCharacters - LineEnding.Length);
            long safeOffset = Math.Clamp((long)offset, 0, documentLength);
            int leafIndex = FindFirstEndAfter(_characterEnds, safeOffset);
            long leafStart = leafIndex == 0 ? 0 : _characterEnds[leafIndex - 1];
            int lineStart = leafIndex == 0 ? 0 : _lineEnds[leafIndex - 1];
            long cursor = leafStart;
            List<string> leaf = _leaves[leafIndex];
            for (int i = 0; i < leaf.Count; i++)
            {
                long lineEnd = cursor + leaf[i].Length;
                if (safeOffset <= lineEnd)
                {
                    return new TextPosition(lineStart + i + 1, (int)(safeOffset - cursor) + 1);
                }
                cursor = lineEnd + LineEnding.Length;
            }
            return new TextPosition(LineCount, GetLine(LineCount).Length + 1);
        }

        public int GetOffsetAt(int lineNumber, int column)
        {
            int safeLine = Math.Clamp(lineNumber, 1, LineCount);
            (int leafIndex, int innerIndex) = LocateLine(safeLine);
            long offset = leafIndex == 0 ? 0 : _characterEnds[leafIndex - 1];
            List<string> leaf = _leaves[leafIndex];
            for (int i = 0; i < innerIndex; i++) offset += leaf[i].Length + LineEnding.Length;
            offset += ClampColumn(leaf[innerIndex], column) - 1L;
            return (int)Math.Min(int.MaxValue, offset);
        }

        public void ApplyEdit(TextEdit edit)
        {
            int startLine = edit.StartLine;
            int startColumn = edit.StartColumn;
            int endLine = edit.EndLine;
            int endColumn = edit.EndColumn;
            NormalizeRange(ref startLine, ref startColumn, ref endLine, ref endColumn);

            string startText = GetLine(startLine);
            string endText = GetLine(endLine);
            string prefix = startText[..(ClampColumn(startText, startColumn) - 1)];
            string suffix = endText[(ClampColumn(endText, endColumn) - 1)..];
            string normalized = (edit.Text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
            string[] inserted = normalized.Split('\n');
            var replacement = new List<string>(inserted.Length);
            if (inserted.Length == 1)
            {
                replacement.Add(prefix + inserted[0] + suffix);
            }
            else
            {
                replacement.Add(prefix + inserted[0]);
                for (int i = 1; i < inserted.Length - 1; i++) replacement.Add(inserted[i]);
                replacement.Add(inserted[^1] + suffix);
            }
            ReplaceLineRange(startLine, endLine, replacement);
        }

        public void ReplaceLine(int lineNumber, string text)
        {
            if (lineNumber < 1 || lineNumber > LineCount) return;
            (int leafIndex, int innerIndex) = LocateLine(lineNumber);
            _leaves[leafIndex][innerIndex] = NormalizeSingleLine(text);
            RebuildMetadata();
        }

        public void InsertLine(int lineNumber, string text)
        {
            int target = Math.Clamp(lineNumber, 1, LineCount + 1);
            if (target > LineCount)
            {
                List<string> last = _leaves[^1];
                last.Add(NormalizeSingleLine(text));
                SplitOversizedLeaf(_leaves.Count - 1);
                RebuildMetadata();
                return;
            }
            (int leafIndex, int innerIndex) = LocateLine(target);
            _leaves[leafIndex].Insert(innerIndex, NormalizeSingleLine(text));
            SplitOversizedLeaf(leafIndex);
            RebuildMetadata();
        }

        public void DeleteLine(int lineNumber)
        {
            if (lineNumber < 1 || lineNumber > LineCount) return;
            ReplaceLineRange(lineNumber, lineNumber, Array.Empty<string>());
            EnsureAtLeastOneLine();
            RebuildMetadata();
        }

        public void SplitLine(int lineNumber, string before, string after) =>
            ReplaceLineRange(lineNumber, lineNumber, new[] { NormalizeSingleLine(before), NormalizeSingleLine(after) });

        public void MergeLineWithPrevious(int lineNumber)
        {
            if (lineNumber <= 1 || lineNumber > LineCount) return;
            ReplaceLineRange(lineNumber - 1, lineNumber, new[] { GetLine(lineNumber - 1) + GetLine(lineNumber) });
        }

        public TextSearchResult? Find(string query, int startLine, int startColumn, bool reverse, bool matchCase, bool isRegex = false)
        {
            if (string.IsNullOrEmpty(query)) return null;
            Regex? regex = null;
            if (isRegex)
            {
                try
                {
                    regex = new Regex(query, matchCase ? RegexOptions.None : RegexOptions.IgnoreCase, LineArrayTextModel.UserRegexTimeout);
                }
                catch (ArgumentException) { return null; }
            }

            int safeLine = Math.Clamp(startLine, 1, LineCount);
            int step = reverse ? -1 : 1;
            for (int lineNumber = safeLine; lineNumber >= 1 && lineNumber <= LineCount; lineNumber += step)
            {
                string line = GetLine(lineNumber);
                int boundary = lineNumber == safeLine
                    ? (reverse ? Math.Clamp(startColumn - 2, 0, Math.Max(0, line.Length)) : Math.Clamp(startColumn - 1, 0, line.Length))
                    : (reverse ? line.Length : 0);
                if (regex != null)
                {
                    Match[] matches = regex.Matches(line).Cast<Match>().Where(match => match.Length > 0).ToArray();
                    IEnumerable<Match> ordered = reverse ? matches.Reverse() : matches;
                    Match? match = ordered.FirstOrDefault(item => reverse ? item.Index <= boundary : item.Index >= boundary);
                    if (match != null) return new TextSearchResult(lineNumber, match.Index, match.Length, line);
                }
                else if (line.Length > 0)
                {
                    StringComparison comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                    int index = reverse
                        ? line.LastIndexOf(query, Math.Min(boundary, line.Length - 1), comparison)
                        : line.IndexOf(query, boundary, comparison);
                    if (index >= 0) return new TextSearchResult(lineNumber, index, query.Length, line);
                }
            }
            return null;
        }

        public List<TextSearchResult> FindAll(string query, bool matchCase, bool isRegex = false, int currentLine = 1, int maxMatches = 50000)
        {
            var results = new List<TextSearchResult>();
            if (string.IsNullOrEmpty(query)) return results;
            Regex? regex = null;
            if (isRegex)
            {
                try
                {
                    regex = new Regex(query, matchCase ? RegexOptions.None : RegexOptions.IgnoreCase, LineArrayTextModel.UserRegexTimeout);
                }
                catch (ArgumentException) { return results; }
            }
            StringComparison comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            for (int lineNumber = 1; lineNumber <= LineCount && results.Count < maxMatches; lineNumber++)
            {
                string line = GetLine(lineNumber);
                if (regex != null)
                {
                    foreach (Match match in regex.Matches(line))
                    {
                        if (match.Length <= 0) continue;
                        results.Add(new TextSearchResult(lineNumber, match.Index, match.Length, line));
                        if (results.Count >= maxMatches) break;
                    }
                }
                else
                {
                    int start = 0;
                    while (start <= line.Length)
                    {
                        int index = line.IndexOf(query, start, comparison);
                        if (index < 0) break;
                        results.Add(new TextSearchResult(lineNumber, index, query.Length, line));
                        if (results.Count >= maxMatches) break;
                        start = index + 1;
                    }
                }
            }
            return results;
        }

        public async Task SaveAsync(string filePath, string encodingName, CancellationToken cancellationToken = default)
        {
            string? directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            string temp = Path.Combine(directory ?? Path.GetTempPath(), $"._{Path.GetFileName(filePath)}.tmp");
            string backup = filePath + ".bak";
            try
            {
                Encoding encoding = TextEncodingService.GetEncodingByName(encodingName);
                await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, true))
                await using (var writer = new StreamWriter(stream, encoding, 128 * 1024, false))
                {
                    int lineNumber = 0;
                    foreach (List<string> leaf in _leaves)
                    {
                        foreach (string line in leaf)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (lineNumber++ > 0) await writer.WriteAsync(LineEnding.AsMemory(), cancellationToken).ConfigureAwait(false);
                            await writer.WriteAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
                        }
                    }
                }
                if (File.Exists(filePath))
                {
                    File.Replace(temp, filePath, backup);
                    if (File.Exists(backup)) File.Delete(backup);
                }
                else File.Move(temp, filePath);
            }
            catch (Exception ex)
            {
                if (File.Exists(temp)) { try { File.Delete(temp); } catch { } }
                throw new IOException($"파일 저장 실패 (안전 복구 완료): {ex.Message}", ex);
            }
        }

        private long TotalStoredCharacters => _characterEnds.Count == 0 ? 0 : _characterEnds[^1];

        private void ReplaceLineRange(int startLine, int endLine, IReadOnlyList<string> replacement)
        {
            if (startLine < 1 || startLine > LineCount || endLine < startLine) return;
            endLine = Math.Min(endLine, LineCount);
            (int startLeaf, int startInner) = LocateLine(startLine);
            (int endLeaf, int endInner) = LocateLine(endLine);
            var merged = new List<string>();
            merged.AddRange(_leaves[startLeaf].Take(startInner));
            merged.AddRange(replacement.Select(NormalizeSingleLine));
            merged.AddRange(_leaves[endLeaf].Skip(endInner + 1));
            _leaves.RemoveRange(startLeaf, endLeaf - startLeaf + 1);
            InsertLeaves(startLeaf, merged);
            EnsureAtLeastOneLine();
            RebuildMetadata();
        }

        private void AppendLeaves(IEnumerable<string> lines)
        {
            var leaf = new List<string>(TargetLeafLines);
            foreach (string line in lines)
            {
                leaf.Add(line);
                if (leaf.Count >= TargetLeafLines)
                {
                    _leaves.Add(leaf);
                    leaf = new List<string>(TargetLeafLines);
                }
            }
            if (leaf.Count > 0) _leaves.Add(leaf);
        }

        private void InsertLeaves(int index, IReadOnlyList<string> lines)
        {
            if (lines.Count == 0) return;
            for (int offset = 0; offset < lines.Count; offset += TargetLeafLines)
            {
                int count = Math.Min(TargetLeafLines, lines.Count - offset);
                _leaves.Insert(index++, lines.Skip(offset).Take(count).ToList());
            }
        }

        private void SplitOversizedLeaf(int leafIndex)
        {
            List<string> leaf = _leaves[leafIndex];
            if (leaf.Count <= MaxLeafLines) return;
            List<string> tail = leaf.GetRange(TargetLeafLines, leaf.Count - TargetLeafLines);
            leaf.RemoveRange(TargetLeafLines, leaf.Count - TargetLeafLines);
            _leaves.Insert(leafIndex + 1, tail);
        }

        private void EnsureAtLeastOneLine()
        {
            _leaves.RemoveAll(leaf => leaf.Count == 0);
            if (_leaves.Count == 0) _leaves.Add(new List<string> { string.Empty });
        }

        private void RebuildMetadata()
        {
            _lineEnds.Clear();
            _characterEnds.Clear();
            int lines = 0;
            long characters = 0;
            foreach (List<string> leaf in _leaves)
            {
                lines += leaf.Count;
                foreach (string line in leaf) characters += line.Length + LineEnding.Length;
                _lineEnds.Add(lines);
                _characterEnds.Add(characters);
            }
        }

        private (int LeafIndex, int InnerIndex) LocateLine(int lineNumber)
        {
            int leafIndex = FindFirstEndAtLeast(_lineEnds, lineNumber);
            int previousEnd = leafIndex == 0 ? 0 : _lineEnds[leafIndex - 1];
            return (leafIndex, lineNumber - previousEnd - 1);
        }

        private static int FindFirstEndAtLeast(List<int> ends, int value)
        {
            int low = 0, high = ends.Count - 1;
            while (low < high)
            {
                int mid = low + (high - low) / 2;
                if (ends[mid] >= value) high = mid; else low = mid + 1;
            }
            return low;
        }

        private static int FindFirstEndAfter(List<long> ends, long value)
        {
            int low = 0, high = ends.Count - 1;
            while (low < high)
            {
                int mid = low + (high - low) / 2;
                if (ends[mid] > value) high = mid; else low = mid + 1;
            }
            return low;
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

        private static int ClampColumn(string line, int column) => Math.Clamp(column, 1, line.Length + 1);
        private static string NormalizeSingleLine(string text) =>
            (text ?? string.Empty).Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
        private static void AppendBounded(StringBuilder builder, string value, int maxChars)
        {
            if (builder.Length >= maxChars) return;
            builder.Append(value.AsSpan(0, Math.Min(value.Length, maxChars - builder.Length)));
        }
    }
}
