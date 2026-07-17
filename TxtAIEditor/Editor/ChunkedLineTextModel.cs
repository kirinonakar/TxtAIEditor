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
        public const long ChunkedFileSizeThresholdBytes = 4L * 1024 * 1024;

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

        public static bool ShouldUseChunkedModelForFile(long fileSizeBytes) =>
            fileSizeBytes >= ChunkedFileSizeThresholdBytes;

        private static string DetectLineEnding(string text)
        {
            int crlf = text.IndexOf("\r\n", StringComparison.Ordinal);
            if (crlf >= 0) return "\r\n";
            return text.IndexOf('\r') >= 0 ? "\r" : "\n";
        }
    }

    public sealed class ChunkedLineTextModelBuilder
    {
        private readonly string _lineEnding;
        private List<List<string>>? _leaves = new();
        private List<string>? _currentLeaf = new(ChunkedLineTextModel.TargetLeafLineCount);

        public ChunkedLineTextModelBuilder(string lineEnding = "\n")
        {
            _lineEnding = string.IsNullOrEmpty(lineEnding) ? "\n" : lineEnding;
        }

        public int LineCount { get; private set; }

        public void AddLine(string? line)
        {
            List<List<string>> leaves = _leaves ??
                throw new InvalidOperationException("The builder has already created a model.");
            List<string> currentLeaf = _currentLeaf!;
            currentLeaf.Add(NormalizeSingleLine(line));
            LineCount++;
            if (currentLeaf.Count < ChunkedLineTextModel.TargetLeafLineCount)
            {
                return;
            }

            leaves.Add(currentLeaf);
            _currentLeaf = new List<string>(ChunkedLineTextModel.TargetLeafLineCount);
        }

        public ChunkedLineTextModel Build()
        {
            List<List<string>> leaves = _leaves ??
                throw new InvalidOperationException("The builder has already created a model.");
            if (_currentLeaf!.Count > 0)
            {
                leaves.Add(_currentLeaf);
            }

            _leaves = null;
            _currentLeaf = null;
            return new ChunkedLineTextModel(leaves, _lineEnding);
        }

        private static string NormalizeSingleLine(string? text) =>
            (text ?? string.Empty).Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
    }

    public sealed class ChunkedLineTextModel : ITextModel
    {
        internal const int TargetLeafLineCount = 512;
        private const int TargetLeafLines = TargetLeafLineCount;
        private const int MaxLeafLines = 1_024;
        private readonly List<List<string>> _leaves;
        private readonly List<long> _leafCharacterCounts = new();
        private readonly FenwickIndex _lineIndex = new();
        private readonly FenwickIndex _characterIndex = new();
        private string _lineEnding;

        public ChunkedLineTextModel(IEnumerable<string>? lines = null, string lineEnding = "\n")
        {
            _leaves = new List<List<string>>();
            _lineEnding = string.IsNullOrEmpty(lineEnding) ? "\n" : lineEnding;
            AppendLeaves(lines?.Select(NormalizeSingleLine) ?? Array.Empty<string>());
            EnsureAtLeastOneLine();
            RebuildIndexes();
        }

        internal ChunkedLineTextModel(List<List<string>> leaves, string lineEnding)
        {
            _leaves = leaves ?? throw new ArgumentNullException(nameof(leaves));
            _lineEnding = string.IsNullOrEmpty(lineEnding) ? "\n" : lineEnding;
            foreach (List<string> leaf in _leaves)
            {
                _leafCharacterCounts.Add(CalculateLeafCharacterCount(leaf));
            }
            EnsureAtLeastOneLine();
            RebuildIndexes();
        }

        public int LineCount => (int)_lineIndex.Total;

        public string LineEnding
        {
            get => _lineEnding;
            set
            {
                string next = string.IsNullOrEmpty(value) ? "\n" : value;
                if (string.Equals(next, _lineEnding, StringComparison.Ordinal)) return;
                int lineEndingLengthDelta = next.Length - _lineEnding.Length;
                _lineEnding = next;
                for (int i = 0; i < _leaves.Count; i++)
                {
                    _leafCharacterCounts[i] += (long)_leaves[i].Count * lineEndingLengthDelta;
                }
                _characterIndex.Rebuild(_leafCharacterCounts.Count, i => _leafCharacterCounts[i]);
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
            int leafIndex = _characterIndex.FindIndexContainingOffset(safeOffset);
            long leafStart = _characterIndex.GetPrefixValue(leafIndex);
            int lineStart = (int)_lineIndex.GetPrefixValue(leafIndex);
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
            long offset = _characterIndex.GetPrefixValue(leafIndex);
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
            string normalized = NormalizeSingleLine(text);
            string previous = _leaves[leafIndex][innerIndex];
            if (string.Equals(previous, normalized, StringComparison.Ordinal)) return;
            _leaves[leafIndex][innerIndex] = normalized;
            long delta = normalized.Length - previous.Length;
            _leafCharacterCounts[leafIndex] += delta;
            _characterIndex.Add(leafIndex, delta);
        }

        public void ApplyLinePatches(IReadOnlyList<TextLinePatch> patches)
        {
            var leafDeltas = new Dictionary<int, long>();
            foreach (TextLinePatch patch in patches)
            {
                if (patch.LineNumber < 1 || patch.LineNumber > LineCount) continue;
                (int leafIndex, int innerIndex) = LocateLine(patch.LineNumber);
                string normalized = NormalizeSingleLine(patch.Text);
                string previous = _leaves[leafIndex][innerIndex];
                if (string.Equals(previous, normalized, StringComparison.Ordinal)) continue;

                _leaves[leafIndex][innerIndex] = normalized;
                long delta = normalized.Length - previous.Length;
                leafDeltas[leafIndex] = leafDeltas.GetValueOrDefault(leafIndex) + delta;
            }

            foreach ((int leafIndex, long delta) in leafDeltas)
            {
                _leafCharacterCounts[leafIndex] += delta;
                _characterIndex.Add(leafIndex, delta);
            }
        }

        public void InsertLine(int lineNumber, string text)
        {
            int target = Math.Clamp(lineNumber, 1, LineCount + 1);
            if (target > LineCount)
            {
                List<string> last = _leaves[^1];
                string normalized = NormalizeSingleLine(text);
                last.Add(normalized);
                int lastLeafIndex = _leaves.Count - 1;
                long characterDelta = normalized.Length + LineEnding.Length;
                _leafCharacterCounts[lastLeafIndex] += characterDelta;
                _lineIndex.Add(lastLeafIndex, 1);
                _characterIndex.Add(lastLeafIndex, characterDelta);
                SplitOversizedLeaf(lastLeafIndex);
                return;
            }
            (int leafIndex, int innerIndex) = LocateLine(target);
            string inserted = NormalizeSingleLine(text);
            _leaves[leafIndex].Insert(innerIndex, inserted);
            long delta = inserted.Length + LineEnding.Length;
            _leafCharacterCounts[leafIndex] += delta;
            _lineIndex.Add(leafIndex, 1);
            _characterIndex.Add(leafIndex, delta);
            SplitOversizedLeaf(leafIndex);
        }

        public void DeleteLine(int lineNumber)
        {
            if (lineNumber < 1 || lineNumber > LineCount) return;
            if (LineCount == 1)
            {
                ReplaceLine(1, string.Empty);
                return;
            }

            (int leafIndex, int innerIndex) = LocateLine(lineNumber);
            List<string> leaf = _leaves[leafIndex];
            string removed = leaf[innerIndex];
            leaf.RemoveAt(innerIndex);
            long characterDelta = -(removed.Length + LineEnding.Length);
            _leafCharacterCounts[leafIndex] += characterDelta;
            _lineIndex.Add(leafIndex, -1);
            _characterIndex.Add(leafIndex, characterDelta);

            if (leaf.Count == 0)
            {
                _leaves.RemoveAt(leafIndex);
                _leafCharacterCounts.RemoveAt(leafIndex);
                RebuildIndexes();
                return;
            }

            MergeSparseLeaf(leafIndex);
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

        private long TotalStoredCharacters => _characterIndex.Total;

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
            _leafCharacterCounts.RemoveRange(startLeaf, endLeaf - startLeaf + 1);
            InsertLeaves(startLeaf, merged);
            EnsureAtLeastOneLine();
            RebuildIndexes();
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
                    _leafCharacterCounts.Add(CalculateLeafCharacterCount(leaf));
                    leaf = new List<string>(TargetLeafLines);
                }
            }
            if (leaf.Count > 0)
            {
                _leaves.Add(leaf);
                _leafCharacterCounts.Add(CalculateLeafCharacterCount(leaf));
            }
        }

        private void InsertLeaves(int index, IReadOnlyList<string> lines)
        {
            if (lines.Count == 0) return;
            for (int offset = 0; offset < lines.Count; offset += TargetLeafLines)
            {
                int count = Math.Min(TargetLeafLines, lines.Count - offset);
                List<string> leaf = lines.Skip(offset).Take(count).ToList();
                _leaves.Insert(index, leaf);
                _leafCharacterCounts.Insert(index, CalculateLeafCharacterCount(leaf));
                index++;
            }
        }

        private bool SplitOversizedLeaf(int leafIndex)
        {
            List<string> leaf = _leaves[leafIndex];
            if (leaf.Count <= MaxLeafLines) return false;
            List<string> tail = leaf.GetRange(TargetLeafLines, leaf.Count - TargetLeafLines);
            leaf.RemoveRange(TargetLeafLines, leaf.Count - TargetLeafLines);
            _leaves.Insert(leafIndex + 1, tail);
            _leafCharacterCounts[leafIndex] = CalculateLeafCharacterCount(leaf);
            _leafCharacterCounts.Insert(leafIndex + 1, CalculateLeafCharacterCount(tail));
            RebuildIndexes();
            return true;
        }

        private void MergeSparseLeaf(int leafIndex)
        {
            if (_leaves.Count <= 1 || _leaves[leafIndex].Count >= TargetLeafLines / 2)
            {
                return;
            }

            int neighborIndex = leafIndex > 0 ? leafIndex - 1 : leafIndex + 1;
            List<string> leaf = _leaves[leafIndex];
            List<string> neighbor = _leaves[neighborIndex];
            if (leaf.Count + neighbor.Count > MaxLeafLines)
            {
                return;
            }

            int targetIndex;
            if (neighborIndex < leafIndex)
            {
                neighbor.AddRange(leaf);
                targetIndex = neighborIndex;
                _leaves.RemoveAt(leafIndex);
                _leafCharacterCounts.RemoveAt(leafIndex);
            }
            else
            {
                leaf.AddRange(neighbor);
                targetIndex = leafIndex;
                _leaves.RemoveAt(neighborIndex);
                _leafCharacterCounts.RemoveAt(neighborIndex);
            }

            _leafCharacterCounts[targetIndex] = CalculateLeafCharacterCount(_leaves[targetIndex]);
            RebuildIndexes();
        }

        private void EnsureAtLeastOneLine()
        {
            for (int i = _leaves.Count - 1; i >= 0; i--)
            {
                if (_leaves[i].Count > 0) continue;
                _leaves.RemoveAt(i);
                _leafCharacterCounts.RemoveAt(i);
            }
            if (_leaves.Count == 0)
            {
                var leaf = new List<string> { string.Empty };
                _leaves.Add(leaf);
                _leafCharacterCounts.Add(CalculateLeafCharacterCount(leaf));
            }
        }

        private long CalculateLeafCharacterCount(IReadOnlyList<string> leaf)
        {
            long characters = 0;
            foreach (string line in leaf) characters += line.Length + LineEnding.Length;
            return characters;
        }

        private void RebuildIndexes()
        {
            _lineIndex.Rebuild(_leaves.Count, i => _leaves[i].Count);
            _characterIndex.Rebuild(_leafCharacterCounts.Count, i => _leafCharacterCounts[i]);
        }

        private (int LeafIndex, int InnerIndex) LocateLine(int lineNumber)
        {
            int leafIndex = _lineIndex.FindIndexContainingOffset(lineNumber - 1L);
            int previousEnd = (int)_lineIndex.GetPrefixValue(leafIndex);
            return (leafIndex, lineNumber - previousEnd - 1);
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

        private sealed class FenwickIndex
        {
            private long[] _tree = new long[1];

            public int Count => _tree.Length - 1;
            public long Total => GetPrefixValue(Count);

            public void Rebuild(int count, Func<int, long> valueAt)
            {
                _tree = new long[count + 1];
                for (int i = 1; i <= count; i++)
                {
                    _tree[i] += valueAt(i - 1);
                    int parent = i + (i & -i);
                    if (parent <= count) _tree[parent] += _tree[i];
                }
            }

            public void Add(int index, long delta)
            {
                for (int i = index + 1; i < _tree.Length; i += i & -i)
                {
                    _tree[i] += delta;
                }
            }

            public long GetPrefixValue(int count)
            {
                int index = Math.Clamp(count, 0, Count);
                long sum = 0;
                while (index > 0)
                {
                    sum += _tree[index];
                    index -= index & -index;
                }
                return sum;
            }

            public int FindIndexContainingOffset(long offset)
            {
                if (Count == 0) return 0;
                long target = Math.Max(0, offset);
                int index = 0;
                long prefix = 0;
                int bit = HighestPowerOfTwoAtMost(Count);
                while (bit != 0)
                {
                    int next = index + bit;
                    if (next <= Count && prefix + _tree[next] <= target)
                    {
                        index = next;
                        prefix += _tree[next];
                    }
                    bit >>= 1;
                }
                return Math.Min(index, Count - 1);
            }

            private static int HighestPowerOfTwoAtMost(int value)
            {
                int bit = 1;
                while (bit <= value / 2) bit <<= 1;
                return bit;
            }
        }
    }
}
