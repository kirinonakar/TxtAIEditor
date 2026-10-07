using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TxtAIEditor.Controls
{
    internal static class AgentDsmlToolCallParser
    {
        private const string DsmlToken = "<\uFF5CDSML\uFF5C";
        private const string EndOfSentenceToken = "<\uFF5Cend▁of▁sentence\uFF5C>";

        private static readonly Regex ToolCallsBlockRegex = new(
            @"<\uFF5CDSML\uFF5C\s*(?<blockName>tool_calls|calls)\s*>(?<body>.*?)</\uFF5CDSML\uFF5C\s*\k<blockName>\s*>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly Regex InvokeRegex = new(
            @"<\uFF5CDSML\uFF5C\s*invoke\b(?<attributes>[^>]*)>(?<body>.*?)</\uFF5CDSML\uFF5C\s*invoke\s*>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly Regex ParameterRegex = new(
            @"<\uFF5CDSML\uFF5C\s*parameter\b(?<attributes>[^>]*)>(?<value>.*?)</\uFF5CDSML\uFF5C\s*parameter\s*>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private const string AttributePattern =
            @"(?:^|\s){0}\s*=\s*(?:""(?<double>[^""]*)""|'(?<single>[^']*)'|(?<bare>[^\s>]+))";

        private static readonly string[] CallTagNames = { "tool_calls", "calls", "invoke" };

        public static bool ContainsSyntax(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            return FindFirstCallStart(text) >= 0 ||
                text.IndexOf("</\uFF5CDSML\uFF5C", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool TryParse(string text, out List<AgentToolCallParser.ToolCallInfo> toolCalls)
        {
            toolCalls = new List<AgentToolCallParser.ToolCallInfo>();
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            MatchCollection blockMatches = ToolCallsBlockRegex.Matches(text);
            if (blockMatches.Count == 0)
            {
                return false;
            }

            int lastBlockEnd = blockMatches[^1].Index + blockMatches[^1].Length;
            if (!IsEndOfResponse(text.Substring(lastBlockEnd)))
            {
                return false;
            }

            int firstBlockIndex = blockMatches.Count - 1;
            for (int i = blockMatches.Count - 2; i >= 0; i--)
            {
                int separatorStart = blockMatches[i].Index + blockMatches[i].Length;
                int separatorLength = blockMatches[i + 1].Index - separatorStart;
                if (separatorLength < 0 || !string.IsNullOrWhiteSpace(text.Substring(separatorStart, separatorLength)))
                {
                    break;
                }

                firstBlockIndex = i;
            }

            var parsedCalls = new List<AgentToolCallParser.ToolCallInfo>();
            for (int i = firstBlockIndex; i < blockMatches.Count; i++)
            {
                if (!TryParseBlock(blockMatches[i].Groups["body"].Value, parsedCalls))
                {
                    return false;
                }
            }

            if (parsedCalls.Count == 0)
            {
                return false;
            }

            toolCalls = parsedCalls;
            return true;
        }

        public static int FindFirstCallStart(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return -1;
            }

            int searchIndex = 0;
            while (searchIndex < text.Length)
            {
                int index = text.IndexOf(DsmlToken, searchIndex, StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                {
                    return -1;
                }

                int nameStart = index + DsmlToken.Length;
                while (nameStart < text.Length && char.IsWhiteSpace(text[nameStart]))
                {
                    nameStart++;
                }

                string remaining = text.Substring(nameStart);
                foreach (string tagName in CallTagNames)
                {
                    int sharedLength = Math.Min(remaining.Length, tagName.Length);
                    if (!tagName.StartsWith(remaining.Substring(0, sharedLength), StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (remaining.Length < tagName.Length ||
                        remaining.Length == tagName.Length ||
                        char.IsWhiteSpace(remaining[tagName.Length]) ||
                        remaining[tagName.Length] == '>')
                    {
                        return index;
                    }
                }

                searchIndex = index + DsmlToken.Length;
            }

            return -1;
        }

        public static bool IsDsmlStartAt(string text, int index)
        {
            return index >= 0 && index + DsmlToken.Length <= text.Length &&
                text.AsSpan(index, DsmlToken.Length).Equals(DsmlToken.AsSpan(), StringComparison.OrdinalIgnoreCase);
        }

        public static int GetPotentialOpeningTagSuffixLength(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }

            string[] openingTags =
            {
                "<\uFF5CDSML\uFF5Ctool_calls",
                "<\uFF5CDSML\uFF5C calls",
                "<\uFF5CDSML\uFF5Cinvoke",
                "<\uFF5CDSML\uFF5C invoke"
            };

            int longestSuffixLength = 0;
            foreach (string openingTag in openingTags)
            {
                int maxLength = Math.Min(text.Length, openingTag.Length);
                for (int length = maxLength; length > longestSuffixLength; length--)
                {
                    if (text.EndsWith(openingTag.Substring(0, length), StringComparison.OrdinalIgnoreCase))
                    {
                        longestSuffixLength = length;
                        break;
                    }
                }
            }

            return longestSuffixLength;
        }

        public static int FindLastOuterCloseTag(string text, int startIndex)
        {
            if (string.IsNullOrEmpty(text))
            {
                return -1;
            }

            int lastIndex = -1;
            for (int index = Math.Max(0, startIndex); index < text.Length; index++)
            {
                if (TryGetOpeningParameterTagLength(text, index, out int openingParameterLength))
                {
                    int parameterCloseIndex = FindParameterCloseTag(text, index + openingParameterLength);
                    if (parameterCloseIndex < 0)
                    {
                        break;
                    }

                    index = parameterCloseIndex + GetClosingTagLengthAt(text, parameterCloseIndex) - 1;
                    continue;
                }

                if (GetOuterClosingTagLengthAt(text, index) is int closeLength and > 0)
                {
                    lastIndex = index;
                    index += closeLength - 1;
                }
            }

            return lastIndex;
        }

        public static int GetOuterClosingTagLengthAt(string text, int index)
        {
            if (!TryReadDsmlTagName(text, index, closing: true, out string tagName, out int tagEndIndex) ||
                (tagName != "invoke" && tagName != "tool_calls" && tagName != "calls"))
            {
                return 0;
            }

            return tagEndIndex - index + 1;
        }

        private static bool TryParseBlock(string body, List<AgentToolCallParser.ToolCallInfo> toolCalls)
        {
            MatchCollection invokeMatches = InvokeRegex.Matches(body);
            if (invokeMatches.Count == 0)
            {
                return false;
            }

            int previousEnd = 0;
            var blockCalls = new List<AgentToolCallParser.ToolCallInfo>();
            foreach (Match invokeMatch in invokeMatches)
            {
                if (!string.IsNullOrWhiteSpace(body.Substring(previousEnd, invokeMatch.Index - previousEnd)) ||
                    !TryGetAttribute(invokeMatch.Groups["attributes"].Value, "name", out string toolName) ||
                    !IsValidToolName(toolName))
                {
                    return false;
                }

                if (!TryParseParameters(invokeMatch.Groups["body"].Value, out JsonElement arguments))
                {
                    return false;
                }

                blockCalls.Add(new AgentToolCallParser.ToolCallInfo
                {
                    ToolName = toolName.Trim().Replace("\\_", "_", StringComparison.Ordinal),
                    Arguments = arguments
                });
                previousEnd = invokeMatch.Index + invokeMatch.Length;
            }

            if (!string.IsNullOrWhiteSpace(body.Substring(previousEnd)))
            {
                return false;
            }

            toolCalls.AddRange(blockCalls);
            return true;
        }

        private static bool TryParseParameters(string body, out JsonElement arguments)
        {
            arguments = default;
            MatchCollection parameterMatches = ParameterRegex.Matches(body);
            var argumentValues = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            int previousEnd = 0;

            foreach (Match parameterMatch in parameterMatches)
            {
                if (!string.IsNullOrWhiteSpace(body.Substring(previousEnd, parameterMatch.Index - previousEnd)) ||
                    !TryGetAttribute(parameterMatch.Groups["attributes"].Value, "name", out string parameterName) ||
                    string.IsNullOrWhiteSpace(parameterName) ||
                    !TryGetAttribute(parameterMatch.Groups["attributes"].Value, "string", out string stringFlag) ||
                    !TryParseParameterValue(parameterMatch.Groups["value"].Value, stringFlag, out JsonElement value) ||
                    argumentValues.ContainsKey(parameterName))
                {
                    return false;
                }

                argumentValues.Add(parameterName, value);
                previousEnd = parameterMatch.Index + parameterMatch.Length;
            }

            if (!string.IsNullOrWhiteSpace(body.Substring(previousEnd)))
            {
                return false;
            }

            using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(argumentValues));
            arguments = document.RootElement.Clone();
            return true;
        }

        private static bool TryParseParameterValue(string rawValue, string stringFlag, out JsonElement value)
        {
            value = default;
            string normalizedValue = RemoveMarkupIndentation(rawValue);

            if (string.Equals(stringFlag, "true", StringComparison.OrdinalIgnoreCase))
            {
                value = JsonSerializer.SerializeToElement(normalizedValue);
                return true;
            }

            if (!string.Equals(stringFlag, "false", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(normalizedValue);
                value = document.RootElement.Clone();
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static string RemoveMarkupIndentation(string value)
        {
            bool hasLeadingMarkupNewline = value.StartsWith("\r\n", StringComparison.Ordinal) ||
                value.StartsWith("\n", StringComparison.Ordinal);
            bool hasTrailingMarkupNewline = Regex.IsMatch(value, @"\r?\n[ \t]*\z", RegexOptions.CultureInvariant);
            if (!hasLeadingMarkupNewline || !hasTrailingMarkupNewline)
            {
                return value;
            }

            string normalized = value;
            if (normalized.StartsWith("\r\n", StringComparison.Ordinal))
            {
                normalized = normalized.Substring(2);
            }
            else if (normalized.StartsWith("\n", StringComparison.Ordinal))
            {
                normalized = normalized.Substring(1);
            }

            Match trailingMarkupNewline = Regex.Match(normalized, @"\r?\n[ \t]*\z", RegexOptions.CultureInvariant);
            if (trailingMarkupNewline.Success)
            {
                normalized = normalized.Substring(0, trailingMarkupNewline.Index);
            }

            string[] lines = normalized.Split('\n');
            if (lines.Length > 1)
            {
                int commonIndent = lines
                    .Where(line => !string.IsNullOrWhiteSpace(line))
                    .Select(line => line.TakeWhile(character => character is ' ' or '\t').Count())
                    .DefaultIfEmpty(0)
                    .Min();

                if (commonIndent > 0)
                {
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (!string.IsNullOrWhiteSpace(lines[i]))
                        {
                            lines[i] = lines[i].Substring(Math.Min(commonIndent, lines[i].Length));
                        }
                    }

                    normalized = string.Join("\n", lines);
                }
            }
            else
            {
                normalized = normalized.TrimStart(' ', '\t');
            }

            return normalized.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        }

        private static bool TryGetAttribute(string attributes, string attributeName, out string value)
        {
            value = string.Empty;
            Regex regex = new(string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                AttributePattern,
                Regex.Escape(attributeName)),
                RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
            Match match = regex.Match(attributes);
            if (!match.Success)
            {
                return false;
            }

            value = match.Groups["double"].Success
                ? match.Groups["double"].Value
                : match.Groups["single"].Success
                    ? match.Groups["single"].Value
                    : match.Groups["bare"].Value;
            return true;
        }

        private static bool IsValidToolName(string toolName)
        {
            return !string.IsNullOrWhiteSpace(toolName) &&
                Regex.IsMatch(toolName, @"^[a-zA-Z0-9_\-]+$", RegexOptions.CultureInvariant);
        }

        private static bool IsEndOfResponse(string suffix)
        {
            string remaining = suffix.Trim();
            while (remaining.EndsWith(EndOfSentenceToken, StringComparison.Ordinal))
            {
                remaining = remaining.Substring(0, remaining.Length - EndOfSentenceToken.Length).TrimEnd();
            }

            return string.IsNullOrWhiteSpace(remaining);
        }

        private static bool TryGetOpeningParameterTagLength(string text, int index, out int length)
        {
            length = 0;
            if (!TryReadDsmlTagName(text, index, closing: false, out string tagName, out int tagEndIndex) || tagName != "parameter")
            {
                return false;
            }

            length = tagEndIndex - index + 1;
            return true;
        }

        private static int FindParameterCloseTag(string text, int startIndex)
        {
            for (int index = Math.Max(0, startIndex); index < text.Length; index++)
            {
                if (TryReadDsmlTagName(text, index, closing: true, out string tagName, out _) && tagName == "parameter")
                {
                    return index;
                }
            }

            return -1;
        }

        private static int GetClosingTagLengthAt(string text, int index)
        {
            if (!TryReadDsmlTagName(text, index, closing: true, out string tagName, out int tagEndIndex) || tagName != "parameter")
            {
                return 0;
            }

            return tagEndIndex - index + 1;
        }

        private static bool TryReadDsmlTagName(string text, int index, bool closing, out string tagName, out int tagEndIndex)
        {
            tagName = string.Empty;
            tagEndIndex = -1;
            if (index < 0 || index >= text.Length)
            {
                return false;
            }

            int cursor = index;
            if (closing)
            {
                if (text[cursor] != '<' || cursor + 1 >= text.Length || text[cursor + 1] != '/')
                {
                    return false;
                }

                cursor += 2;
            }
            else if (text[cursor] != '<')
            {
                return false;
            }
            else
            {
                cursor++;
            }

            if (cursor + DsmlToken.Length - 1 > text.Length ||
                !text.AsSpan(cursor, DsmlToken.Length - 1).Equals(DsmlToken.AsSpan(1), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            cursor += DsmlToken.Length - 1;
            while (cursor < text.Length && char.IsWhiteSpace(text[cursor]))
            {
                cursor++;
            }

            int nameStart = cursor;
            while (cursor < text.Length && (char.IsLetter(text[cursor]) || text[cursor] == '_'))
            {
                cursor++;
            }

            if (cursor == nameStart)
            {
                return false;
            }

            tagName = text.Substring(nameStart, cursor - nameStart);
            if (closing)
            {
                while (cursor < text.Length && char.IsWhiteSpace(text[cursor]))
                {
                    cursor++;
                }

                if (cursor >= text.Length || text[cursor] != '>')
                {
                    return false;
                }

                tagEndIndex = cursor;
                return true;
            }

            tagEndIndex = text.IndexOf('>', cursor);
            return tagEndIndex >= 0;
        }
    }
}
