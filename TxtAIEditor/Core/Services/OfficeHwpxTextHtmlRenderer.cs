using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using static TxtAIEditor.Core.Services.OfficeDocumentHtmlRendererUtilities;
using static TxtAIEditor.Core.Services.OfficeHwpxRenderUtilities;

namespace TxtAIEditor.Core.Services
{
    internal static class OfficeHwpxTextHtmlRenderer
    {
        internal static IReadOnlyDictionary<XText, string> GetHwpxRenderedTextValues(
            XElement paragraph, bool normalizeLeadingSpaces = true)
        {
            var textNodes = paragraph
                .DescendantNodes()
                .OfType<XText>()
                .Where(textNode =>
                    textNode.Parent?.Name.LocalName == "t" &&
                    !IsInsideNestedElement(paragraph, textNode, "tbl"))
                .ToList();
            var softWrapPositions = paragraph
                .Descendants()
                .Where(element =>
                    element.Name.LocalName == "lineseg" &&
                    !IsInsideNestedElement(paragraph, element, "tbl"))
                .Select(element => GetAttributeValue(element, "textpos"))
                .Select(value => int.TryParse(
                    value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int position)
                    ? position
                    : -1)
                .Where(position => position > 0)
                .ToHashSet();

            var renderedValues = new Dictionary<XText, string>();
            if (softWrapPositions.Count > 0)
            {
                int textOffset = 0;
                foreach (XText textNode in textNodes)
                {
                    string text = textNode.Value;
                    string renderedText = NormalizeHwpxSoftWrapPadding(text, textOffset, softWrapPositions);
                    if (!ReferenceEquals(renderedText, text))
                    {
                        renderedValues[textNode] = renderedText;
                    }

                    textOffset += text.Length;
                }
            }

            if (normalizeLeadingSpaces)
            {
                NormalizeHwpxLeadingSpaceRuns(paragraph, renderedValues);
            }
            return renderedValues;
        }

        private static void NormalizeHwpxLeadingSpaceRuns(
            XElement paragraph,
            IDictionary<XText, string> renderedValues)
        {
            var leadingSpaceNodes = new List<XText>();
            int leadingSpaceCount = 0;
            foreach (XNode node in paragraph.DescendantNodes())
            {
                if (IsInsideNestedElement(paragraph, node, "tbl"))
                {
                    continue;
                }

                if (node is XText textNode && textNode.Parent?.Name.LocalName == "t")
                {
                    string text = renderedValues.TryGetValue(textNode, out string? renderedText)
                        ? renderedText
                        : textNode.Value;
                    if (text.Length > 0 && text.All(value => value == ' '))
                    {
                        leadingSpaceNodes.Add(textNode);
                        leadingSpaceCount += text.Length;
                        continue;
                    }

                    if (leadingSpaceNodes.Count == 0 || string.IsNullOrEmpty(text))
                    {
                        return;
                    }

                    foreach (XText leadingSpaceNode in leadingSpaceNodes)
                    {
                        renderedValues[leadingSpaceNode] = string.Empty;
                    }

                    renderedValues[textNode] = new string(' ', leadingSpaceCount) + text;
                    return;
                }

                if (leadingSpaceNodes.Count > 0 &&
                    node is XElement element &&
                    element.Name.LocalName is "tab" or "lineBreak" or "br" or "cr" or
                        "nbSpace" or "fwSpace" or "pic" or "img" or "container")
                {
                    return;
                }
            }
        }

        private static string NormalizeHwpxSoftWrapPadding(
            string text,
            int textOffset,
            IReadOnlySet<int> softWrapPositions)
        {
            StringBuilder? normalized = null;
            int copyStart = 0;
            for (int index = 0; index < text.Length;)
            {
                if (text[index] != ' ')
                {
                    index++;
                    continue;
                }

                int whitespaceStart = index;
                while (index < text.Length && text[index] == ' ')
                {
                    index++;
                }

                int whitespaceEnd = index;
                if (whitespaceEnd - whitespaceStart < 2 ||
                    whitespaceStart == 0 ||
                    whitespaceEnd >= text.Length ||
                    !softWrapPositions.Any(position =>
                        position >= textOffset + whitespaceStart &&
                        position < textOffset + whitespaceEnd))
                {
                    continue;
                }

                normalized ??= new StringBuilder(text.Length);
                normalized.Append(text, copyStart, whitespaceStart - copyStart);
                if (!IsHangulSyllable(text[whitespaceStart - 1]) ||
                    !IsHangulSyllable(text[whitespaceEnd]))
                {
                    normalized.Append(' ');
                }

                copyStart = whitespaceEnd;
            }

            if (normalized == null)
            {
                return text;
            }

            normalized.Append(text, copyStart, text.Length - copyStart);
            return normalized.ToString();
        }

        private static bool IsHangulSyllable(char value)
        {
            return value >= '\uAC00' && value <= '\uD7A3';
        }

        internal static string? GetHwpxListMarkerSpacing(XElement fixedWidthSpace)
        {
            if (fixedWidthSpace.Name.LocalName != "fwSpace")
            {
                return null;
            }

            XElement? paragraph = fixedWidthSpace.Ancestors()
                .FirstOrDefault(element => element.Name.LocalName == "p");
            if (paragraph == null)
            {
                return null;
            }

            var inlineNodes = paragraph.DescendantNodes()
                .Where(node =>
                    !IsInsideNestedElement(paragraph, node, "tbl") &&
                    (node is XText textNode && textNode.Parent?.Name.LocalName == "t" ||
                     node is XElement element &&
                     element.Name.LocalName is "tab" or "lineBreak" or "br" or "cr" or
                         "nbSpace" or "fwSpace" or "pic" or "img" or "container"))
                .ToList();
            int spaceIndex = inlineNodes.FindIndex(node => ReferenceEquals(node, fixedWidthSpace));
            if (spaceIndex < 0)
            {
                return null;
            }

            int sequenceStart = spaceIndex;
            while (sequenceStart > 0 &&
                   inlineNodes[sequenceStart - 1] is XElement previousSpace &&
                   previousSpace.Name.LocalName == "fwSpace")
            {
                sequenceStart--;
            }

            int sequenceEnd = spaceIndex;
            while (sequenceEnd + 1 < inlineNodes.Count &&
                   inlineNodes[sequenceEnd + 1] is XElement nextSpace &&
                   nextSpace.Name.LocalName == "fwSpace")
            {
                sequenceEnd++;
            }

            if (sequenceStart == 0 ||
                sequenceEnd + 1 >= inlineNodes.Count ||
                inlineNodes[sequenceStart - 1] is not XText ||
                inlineNodes[sequenceEnd + 1] is not XText suffix ||
                string.IsNullOrEmpty(suffix.Value))
            {
                return null;
            }

            int prefixStart = sequenceStart - 1;
            while (prefixStart > 0 && inlineNodes[prefixStart - 1] is XText)
            {
                prefixStart--;
            }

            string markerPrefix = string.Concat(
                inlineNodes
                    .Skip(prefixStart)
                    .Take(sequenceStart - prefixStart)
                    .OfType<XText>()
                    .Select(textNode => textNode.Value));
            Match markerMatch = Regex.Match(
                markerPrefix,
                @"(?:^|\s)(?:[가-힣]\.|(?<numeric>\d+\))|(?<note>※)|(?<dash>-))$");
            if (!markerMatch.Success)
            {
                return null;
            }

            if (sequenceEnd - sequenceStart + 1 < 2 &&
                !markerMatch.Groups["dash"].Success)
            {
                return null;
            }

            if (spaceIndex != sequenceStart)
            {
                return string.Empty;
            }

            if (markerMatch.Groups["numeric"].Success ||
                markerMatch.Groups["note"].Success)
            {
                return " ";
            }

            return suffix.Value.StartsWith(' ')
                ? string.Empty
                : " ";
        }

        internal static string GetHwpxTextStyle(XText textNode, IReadOnlyDictionary<string, string> characterStyles)
        {
            XElement? parent = textNode.Parent;
            while (parent != null)
            {
                if (parent.Name.LocalName == "run")
                {
                    string styleId = GetAttributeValue(parent, "charPrIDRef");
                    return characterStyles.TryGetValue(styleId, out string? style) ? style : string.Empty;
                }

                parent = parent.Parent;
            }

            return string.Empty;
        }

        internal static void AppendStyledText(StringBuilder builder, string text, string style)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(style))
            {
                builder.Append(Html(text));
                return;
            }

            builder.Append("<span style=\"")
                .Append(Html(style))
                .Append("\">")
                .Append(Html(text))
                .Append("</span>");
        }
    }
}
