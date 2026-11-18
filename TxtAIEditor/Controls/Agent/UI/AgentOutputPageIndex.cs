using System;
using System.Collections.Generic;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentOutputPageIndex
    {
        private const int MaxPageChars = 24_000;
        private const int MaxPageLines = 240;
        private const string GlobalRulesStart = "[Global agent rules]";
        private const string GlobalRulesEnd = "[End global agent rules]";

        [Flags]
        private enum HiddenSections { None = 0, GlobalRules = 1, CompressedContext = 2 }

        private readonly record struct PageStart(int Offset, string? Fence = null, HiddenSections Hidden = HiddenSections.None,
            bool ContinuesLine = false);
        private readonly List<PageStart> _pages = new() { new(0) };

        public int LastPage => _pages.Count - 1;

        public void Reset() => _pages.RemoveRange(1, LastPage);

        public void DiscardPageStartsAfter(int offset)
        {
            while (LastPage > 0 && _pages[^1].Offset >= offset)
            {
                _pages.RemoveAt(LastPage);
            }
        }

        public void Update(string text)
        {
            while (FindNextPage(text, _pages[^1]) is { } next)
            {
                _pages.Add(next);
            }
        }

        public string GetText(string text, int page, bool hideDetails)
        {
            var start = _pages[page];
            if (start.Offset >= text.Length)
            {
                return string.Empty;
            }
            int end = page == LastPage ? text.Length : _pages[page + 1].Offset;
            string body = text[start.Offset..Math.Min(end, text.Length)];
            if (start.Fence is { } fence)
            {
                body = fence + "\n" + body;
            }
            if (hideDetails && start.Hidden.HasFlag(HiddenSections.CompressedContext))
            {
                body = AgentRunTranscriptService.CompressedContextStartMarker + "\n" + body;
            }
            if (hideDetails && start.Hidden.HasFlag(HiddenSections.GlobalRules))
            {
                body = GlobalRulesStart + "\n" + body;
            }
            return body;
        }

        private static PageStart? FindNextPage(string text, PageStart start)
        {
            int offset = start.Offset;
            if (text.Length - offset <= MaxPageLines)
            {
                return null;
            }

            int charLimit = offset + Math.Min(text.Length - offset, MaxPageChars);
            string? fence = start.Fence;
            HiddenSections hidden = start.Hidden;
            PageStart? lastBlank = null;
            PageStart? fenceStart = null;
            for (int lines = 0; offset < charLimit && lines < MaxPageLines; lines++)
            {
                var available = text.AsSpan(offset, charLimit - offset);
                int lineLength = available.IndexOfAny('\r', '\n');
                if (lineLength < 0)
                {
                    if (charLimit == text.Length)
                    {
                        return null;
                    }
                    if (offset == start.Offset)
                    {
                        int end = char.IsSurrogatePair(text, charLimit - 1) ? charLimit - 1 : charLimit;
                        if (!start.ContinuesLine)
                        {
                            fence = UpdateFence(available.TrimStart(), fence);
                        }
                        return new PageStart(end, fence, hidden, ContinuesLine: true);
                    }
                    break;
                }

                var line = available[..lineLength].Trim();
                if (lines > 0 || !start.ContinuesLine)
                {
                    string? previousFence = fence;
                    fence = UpdateFence(line, fence);
                    if (fence != previousFence)
                    {
                        fenceStart = fence != null && offset > start.Offset
                            ? new PageStart(offset, Hidden: hidden) : null;
                    }
                    if (fence?.StartsWith("```html", StringComparison.OrdinalIgnoreCase) != true)
                    {
                        hidden = UpdateHiddenSection(line, hidden);
                    }
                }

                offset += lineLength + 1;
                if (text[offset - 1] == '\r' && offset < text.Length && text[offset] == '\n')
                {
                    offset++;
                }
                if (line.IsEmpty && fence == null && hidden == HiddenSections.None &&
                    (lines >= MaxPageLines / 2 || offset - start.Offset >= MaxPageChars / 2))
                {
                    lastBlank = new PageStart(offset);
                }
            }

            return offset < text.Length ? fenceStart ?? lastBlank ?? new PageStart(offset, fence, hidden) : null;
        }

        private static string? UpdateFence(ReadOnlySpan<char> line, string? current) =>
            line.StartsWith("```", StringComparison.Ordinal)
                ? current == null ? line[..Math.Min(line.Length, 64)].ToString() : null
                : current;

        private static HiddenSections UpdateHiddenSection(ReadOnlySpan<char> line, HiddenSections current)
        {
            if (current.HasFlag(HiddenSections.CompressedContext))
            {
                return line.Equals(AgentRunTranscriptService.CompressedContextEndMarker, StringComparison.OrdinalIgnoreCase)
                    ? current & ~HiddenSections.CompressedContext : current;
            }
            if (line.Equals(AgentRunTranscriptService.CompressedContextStartMarker, StringComparison.OrdinalIgnoreCase))
            {
                return current | HiddenSections.CompressedContext;
            }
            if (line.Equals(GlobalRulesStart, StringComparison.OrdinalIgnoreCase))
            {
                return current | HiddenSections.GlobalRules;
            }
            return line.Equals(GlobalRulesEnd, StringComparison.OrdinalIgnoreCase)
                ? current & ~HiddenSections.GlobalRules : current;
        }
    }
}
