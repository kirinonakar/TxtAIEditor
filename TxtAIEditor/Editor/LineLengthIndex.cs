using System;
using System.Collections.Generic;

namespace TxtAIEditor.Editor
{
    internal sealed class LineLengthIndex
    {
        private long[] _tree = new long[1];

        public int Count => _tree.Length - 1;

        public long TotalLength => GetPrefixLength(Count);

        public void Rebuild(IReadOnlyList<string> lines, int lineEndingLength)
        {
            _tree = new long[lines.Count + 1];
            for (int i = 0; i < lines.Count; i++)
            {
                Add(i + 1, lines[i].Length + lineEndingLength);
            }
        }

        public void UpdateLineLength(int lineNumber, int oldLength, int newLength)
        {
            if (lineNumber < 1 || lineNumber > Count || oldLength == newLength)
            {
                return;
            }

            Add(lineNumber, newLength - oldLength);
        }

        public long GetPrefixLength(int lineCount)
        {
            int index = Math.Clamp(lineCount, 0, Count);
            long sum = 0;
            while (index > 0)
            {
                sum += _tree[index];
                index -= index & -index;
            }

            return sum;
        }

        public int FindLineContainingOffset(long offset)
        {
            if (Count == 0)
            {
                return 1;
            }

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

            return Math.Min(index + 1, Count);
        }

        private void Add(int index, long delta)
        {
            while (index < _tree.Length)
            {
                _tree[index] += delta;
                index += index & -index;
            }
        }

        private static int HighestPowerOfTwoAtMost(int value)
        {
            int bit = 1;
            while (bit <= value / 2)
            {
                bit <<= 1;
            }

            return bit;
        }
    }
}
