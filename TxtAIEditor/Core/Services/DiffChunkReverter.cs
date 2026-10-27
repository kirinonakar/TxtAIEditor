using System;
using System.Collections.Generic;

namespace TxtAIEditor.Core.Services
{
    internal static class DiffChunkReverter
    {
        public static string Revert(string original, string modified,
            int originalStart, int originalCount, int modifiedStart, int modifiedCount)
        {
            var a = LineStarts(original);
            var b = LineStarts(modified);
            ValidateRange(a.Count, originalStart, originalCount);
            ValidateRange(b.Count, modifiedStart, modifiedCount);
            if (originalCount == 0 && modifiedCount == 0)
                throw new ArgumentException("Empty diff chunk.");

            int startA = Offset(a, originalStart, original.Length);
            int endA = Offset(a, originalStart + originalCount, original.Length);
            int startB = Offset(b, modifiedStart, modified.Length);
            int endB = Offset(b, modifiedStart + modifiedCount, modified.Length);

            // At EOF the preceding separator belongs to this change as well.
            // This also distinguishes an empty file from one empty line.
            if (originalStart + originalCount == a.Count && modifiedStart + modifiedCount == b.Count)
            {
                if (originalStart < a.Count) startA = IncludePreviousSeparator(original, startA);
                if (modifiedStart < b.Count) startB = IncludePreviousSeparator(modified, startB);
            }

            string replacement = original.Substring(startA, endA - startA);
            int newline = modified.IndexOf('\n');
            if (newline >= 0)
            {
                string eol = newline > 0 && modified[newline - 1] == '\r' ? "\r\n" : "\n";
                replacement = replacement.Replace("\r\n", "\n").Replace("\n", eol);
            }
            return modified[..startB] + replacement + modified[endB..];
        }

        private static List<int> LineStarts(string text)
        {
            var starts = new List<int>();
            if (text.Length == 0) return starts;
            starts.Add(0);
            for (int i = 0; i < text.Length; i++)
                if (text[i] == '\n') starts.Add(i + 1);
            return starts;
        }

        private static int Offset(List<int> starts, int index, int length) =>
            index == starts.Count ? length : starts[index];

        private static int IncludePreviousSeparator(string text, int offset)
        {
            if (offset > 0 && text[offset - 1] == '\n')
            {
                offset--;
                if (offset > 0 && text[offset - 1] == '\r') offset--;
            }
            return offset;
        }

        private static void ValidateRange(int length, int start, int count)
        {
            if (start < 0 || count < 0 || start > length || count > length - start)
                throw new ArgumentOutOfRangeException(nameof(start));
        }
    }
}
