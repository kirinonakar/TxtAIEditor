using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TxtAIEditor.Controls
{
    internal static class AgentProcessOutputCapture
    {
        internal const int MaximumCapturedBytes = 65536;

        // Keep draining both pipes so verbose children cannot deadlock. Only the
        // beginning and end are retained, with a fixed memory cost per pipe.
        internal static async Task<(byte[] Head, byte[] Tail, bool Truncated)> ReadAsync(
            Stream stream, CancellationToken cancellationToken)
        {
            int half = MaximumCapturedBytes / 2;
            byte[] head = new byte[half];
            byte[] tail = new byte[half];
            byte[] buffer = new byte[8192];
            int headCount = 0, tailCount = 0, tailOffset = 0;
            long total = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                total += read;
                int prefix = Math.Min(read, half - headCount);
                Buffer.BlockCopy(buffer, 0, head, headCount, prefix);
                headCount += prefix;
                int remaining = read - prefix;
                if (remaining == 0) continue;
                int first = Math.Min(remaining, half - tailOffset);
                Buffer.BlockCopy(buffer, prefix, tail, tailOffset, first);
                Buffer.BlockCopy(buffer, prefix + first, tail, 0, remaining - first);
                tailOffset = (tailOffset + remaining) % half;
                tailCount = Math.Min(half, tailCount + remaining);
            }
            Array.Resize(ref head, headCount);
            byte[] orderedTail = new byte[tailCount];
            int start = tailCount == half ? tailOffset : 0;
            int count = Math.Min(tailCount, half - start);
            Buffer.BlockCopy(tail, start, orderedTail, 0, count);
            Buffer.BlockCopy(tail, 0, orderedTail, count, tailCount - count);
            return (head, orderedTail, total > MaximumCapturedBytes);
        }

        internal static string TruncateMiddle(string text, int maxCharacters = 20000)
        {
            if (text.Length <= maxCharacters) return text;
            const string marker = "\n[output truncated in middle]\n";
            int head = (maxCharacters - marker.Length) / 2;
            int tail = maxCharacters - marker.Length - head;
            if (char.IsHighSurrogate(text[head - 1])) head--;
            if (char.IsLowSurrogate(text[text.Length - tail])) tail--;
            return text.Substring(0, head) + marker + text.Substring(text.Length - tail);
        }

        internal static string FormatOutput(string stdout, string stderr, int exitCode)
        {
            string status = $"[exit_code] {exitCode}";
            const string stderrHeader = "\n[stderr]\n";
            int budget = 20000 - status.Length - 1;
            if (string.IsNullOrWhiteSpace(stderr))
                return TruncateMiddle(stdout.TrimEnd(), budget) + "\n" + status;

            budget -= stderrHeader.Length;
            int stderrBudget = Math.Min(stderr.Length, budget / 2);
            int stdoutBudget = Math.Min(stdout.Length, budget - stderrBudget);
            stderrBudget = budget - stdoutBudget;
            // Bound each pipe independently, otherwise a large stderr body could hide
            // the compiler summary at the end of stdout (or vice versa).
            return TruncateMiddle(stdout.TrimEnd(), stdoutBudget) + stderrHeader +
                TruncateMiddle(stderr.TrimEnd(), stderrBudget) + "\n" + status;
        }
    }
}
