using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TxtAIEditor.Controls
{
    internal static class AgentToolCallParser
    {
        public static bool TryParse(string response, out string toolName, out JsonElement arguments)
        {
            toolName = string.Empty;
            arguments = default;

            if (!TryExtractToolCallPayload(response, out string payload))
            {
                return false;
            }

            string fixedPayload = FixJsonCarriageReturnEscapes(payload);
            string trimmedPayload = fixedPayload.Trim();

            int openBraceIndex = trimmedPayload.IndexOf('{');
            if (openBraceIndex > 0)
            {
                string possibleName = trimmedPayload.Substring(0, openBraceIndex).Trim();
                if (Regex.IsMatch(possibleName, @"^[a-zA-Z0-9_\-]+$"))
                {
                    toolName = possibleName;
                    string jsonPart = trimmedPayload.Substring(openBraceIndex);
                    try
                    {
                        using var document = JsonDocument.Parse(jsonPart);
                        var root = document.RootElement.Clone();
                        arguments = root.TryGetProperty("arguments", out var argsProp)
                            ? argsProp.Clone()
                            : root.Clone();
                        return !string.IsNullOrWhiteSpace(toolName);
                    }
                    catch
                    {
                        // Fall through to lenient/other parsing methods on failure.
                    }
                }
            }

            if (TryExtractToolCallJson(response, out string json))
            {
                string fixedJson = FixJsonCarriageReturnEscapes(json);
                try
                {
                    using var document = JsonDocument.Parse(fixedJson);
                    var root = document.RootElement.Clone();
                    if (root.TryGetProperty("name", out var nameProp))
                    {
                        toolName = nameProp.GetString() ?? string.Empty;
                        if (root.TryGetProperty("arguments", out var argsProp))
                        {
                            arguments = argsProp.Clone();
                        }
                        else
                        {
                            using var emptyDocument = JsonDocument.Parse("{}");
                            arguments = emptyDocument.RootElement.Clone();
                        }

                        return !string.IsNullOrWhiteSpace(toolName);
                    }
                }
                catch
                {
                    // Fall through to lenient parsing.
                }
            }

            return TryParseLenient(trimmedPayload, out toolName, out arguments);
        }

        private static string FixJsonCarriageReturnEscapes(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                return json;
            }

            var sb = new StringBuilder(json.Length);
            int len = json.Length;
            for (int i = 0; i < len; i++)
            {
                if (json[i] == '\\')
                {
                    if (i + 1 >= len)
                    {
                        sb.Append('\\');
                        break;
                    }

                    if (json[i + 1] == 'r')
                    {
                        bool isFollowedByNewline = false;
                        if (i + 3 < len && json[i + 2] == '\\' && json[i + 3] == 'n')
                        {
                            isFollowedByNewline = true;
                        }
                        else if (i + 2 < len && (json[i + 2] == '\n' || json[i + 2] == '\r'))
                        {
                            isFollowedByNewline = true;
                        }

                        if (!isFollowedByNewline)
                        {
                            sb.Append("\\\\r");
                        }
                        else
                        {
                            sb.Append('\\').Append('r');
                        }

                        i++;
                        continue;
                    }

                    sb.Append('\\').Append(json[i + 1]);
                    i++;
                    continue;
                }

                sb.Append(json[i]);
            }

            return sb.ToString();
        }

        private static bool TryParseLenient(string json, out string toolName, out JsonElement arguments)
        {
            toolName = string.Empty;
            arguments = default;

            string? name = ExtractLenientStringProperty(json, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            string argumentsText = ExtractLenientObjectProperty(json, "arguments") ?? "{}";
            var argumentValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in ExtractLenientStringProperties(argumentsText))
            {
                argumentValues[property.Key] = property.Value;
            }

            var numberMatches = Regex.Matches(
                argumentsText,
                "\"(?<key>[^\"\\\\]*(?:\\\\.[^\"\\\\]*)*)\"\\s*:\\s*(?<value>-?\\d+)",
                RegexOptions.Singleline);

            foreach (Match match in numberMatches)
            {
                string key = DecodeLenientJsonString(match.Groups["key"].Value);
                if (!argumentValues.ContainsKey(key))
                {
                    argumentValues[key] = match.Groups["value"].Value;
                }
            }

            string repairedJson = JsonSerializer.Serialize(argumentValues);
            using var argumentsDocument = JsonDocument.Parse(repairedJson);
            toolName = DecodeLenientJsonString(name);
            arguments = argumentsDocument.RootElement.Clone();
            return !string.IsNullOrWhiteSpace(toolName);
        }

        private static IEnumerable<KeyValuePair<string, string>> ExtractLenientStringProperties(string objectText)
        {
            if (string.IsNullOrEmpty(objectText))
            {
                yield break;
            }

            int i = 0;
            while (i < objectText.Length)
            {
                int keyQuoteIndex = objectText.IndexOf('"', i);
                if (keyQuoteIndex < 0)
                {
                    yield break;
                }

                if (!TryReadQuotedToken(objectText, keyQuoteIndex, out string rawKey, out int keyEndIndex))
                {
                    yield break;
                }

                int colonIndex = SkipWhitespace(objectText, keyEndIndex + 1);
                if (colonIndex >= objectText.Length || objectText[colonIndex] != ':')
                {
                    i = keyEndIndex + 1;
                    continue;
                }

                int valueStartIndex = SkipWhitespace(objectText, colonIndex + 1);
                if (valueStartIndex >= objectText.Length || objectText[valueStartIndex] != '"')
                {
                    i = valueStartIndex + 1;
                    continue;
                }

                int valueContentStart = valueStartIndex + 1;
                int valueEndIndex = FindLenientStringValueEnd(objectText, valueContentStart);
                string rawValue = objectText.Substring(
                    valueContentStart,
                    Math.Max(0, valueEndIndex - valueContentStart));

                yield return new KeyValuePair<string, string>(
                    DecodeLenientJsonString(rawKey),
                    DecodeLenientJsonString(rawValue));

                i = Math.Min(objectText.Length, valueEndIndex + 1);
            }
        }

        private static bool TryReadQuotedToken(string text, int quoteIndex, out string rawValue, out int endQuoteIndex)
        {
            rawValue = string.Empty;
            endQuoteIndex = -1;
            if (quoteIndex < 0 || quoteIndex >= text.Length || text[quoteIndex] != '"')
            {
                return false;
            }

            bool escaped = false;
            for (int i = quoteIndex + 1; i < text.Length; i++)
            {
                char ch = text[i];
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (ch == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (ch == '"')
                {
                    rawValue = text.Substring(quoteIndex + 1, i - quoteIndex - 1);
                    endQuoteIndex = i;
                    return true;
                }
            }

            rawValue = text.Substring(quoteIndex + 1);
            endQuoteIndex = text.Length;
            return true;
        }

        private static int FindLenientStringValueEnd(string text, int valueStartIndex)
        {
            bool escaped = false;
            for (int i = valueStartIndex; i < text.Length; i++)
            {
                char ch = text[i];
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (ch == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (ch != '"')
                {
                    continue;
                }

                int nextIndex = SkipWhitespace(text, i + 1);
                if (nextIndex >= text.Length ||
                    text[nextIndex] == '}' ||
                    text[nextIndex] == ']')
                {
                    return i;
                }

                if (text[nextIndex] == ',')
                {
                    int afterCommaIndex = SkipWhitespace(text, nextIndex + 1);
                    if (LooksLikePropertyKeyAt(text, afterCommaIndex))
                    {
                        return i;
                    }
                }
            }

            return text.Length;
        }

        private static bool LooksLikePropertyKeyAt(string text, int quoteIndex)
        {
            if (!TryReadQuotedToken(text, quoteIndex, out _, out int keyEndIndex))
            {
                return false;
            }

            int colonIndex = SkipWhitespace(text, keyEndIndex + 1);
            return colonIndex < text.Length && text[colonIndex] == ':';
        }

        private static int SkipWhitespace(string text, int startIndex)
        {
            int i = Math.Max(0, startIndex);
            while (i < text.Length && char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            return i;
        }

        private static string? ExtractLenientStringProperty(string text, string propertyName)
        {
            var match = Regex.Match(
                text,
                $"\"{Regex.Escape(propertyName)}\"\\s*:\\s*\"(?<value>[^\"\\\\]*(?:\\\\.[^\"\\\\]*)*)\"",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);

            return match.Success ? match.Groups["value"].Value : null;
        }

        private static string? ExtractLenientObjectProperty(string text, string propertyName)
        {
            var match = Regex.Match(
                text,
                $"\"{Regex.Escape(propertyName)}\"\\s*:",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                return null;
            }

            int objectStart = text.IndexOf('{', match.Index + match.Length);
            if (objectStart < 0)
            {
                return null;
            }

            if (TryExtractBalancedJsonObject(text, objectStart, out string objectText))
            {
                return objectText;
            }

            string fallback = text.Substring(objectStart);
            int closingTagIndex = fallback.IndexOf("</tool_call>", StringComparison.OrdinalIgnoreCase);
            if (closingTagIndex >= 0)
            {
                fallback = fallback.Substring(0, closingTagIndex);
            }

            return fallback;
        }

        private static string DecodeLenientJsonString(string value)
        {
            var builder = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char ch = value[i];
                if (ch != '\\' || i + 1 >= value.Length)
                {
                    builder.Append(ch);
                    continue;
                }

                char next = value[++i];
                switch (next)
                {
                    case '"':
                    case '\\':
                    case '/':
                        builder.Append(next);
                        break;
                    case 'n':
                        builder.Append('\n');
                        break;
                    case 'r':
                        builder.Append('\r');
                        break;
                    case 'u' when i + 4 < value.Length:
                        string hex = value.Substring(i + 1, 4);
                        if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int codePoint))
                        {
                            builder.Append((char)codePoint);
                            i += 4;
                        }
                        else
                        {
                            builder.Append('\\').Append(next);
                        }
                        break;
                    default:
                        builder.Append('\\').Append(next);
                        break;
                }
            }

            return builder.ToString();
        }

        private static bool TryExtractToolCallJson(string response, out string json)
        {
            json = string.Empty;
            if (string.IsNullOrWhiteSpace(response))
            {
                return false;
            }

            string text = response.Trim();
            int toolCallIndex = text.IndexOf("<tool_call>", StringComparison.OrdinalIgnoreCase);
            if (toolCallIndex >= 0)
            {
                text = text.Substring(toolCallIndex + "<tool_call>".Length).TrimStart();
            }

            int jsonStart = text.IndexOf('{');
            if (jsonStart < 0)
            {
                return false;
            }

            return TryExtractBalancedJsonObject(text, jsonStart, out json);
        }

        private static bool TryExtractToolCallPayload(string response, out string payload)
        {
            payload = string.Empty;
            if (string.IsNullOrWhiteSpace(response))
            {
                return false;
            }

            string text = response.Trim();
            int toolCallIndex = text.IndexOf("<tool_call>", StringComparison.OrdinalIgnoreCase);
            if (toolCallIndex >= 0)
            {
                int payloadStart = toolCallIndex + "<tool_call>".Length;
                int payloadEnd = text.IndexOf("</tool_call>", payloadStart, StringComparison.OrdinalIgnoreCase);
                payload = payloadEnd >= 0
                    ? text.Substring(payloadStart, payloadEnd - payloadStart).Trim()
                    : text.Substring(payloadStart).Trim();
            }
            else
            {
                int jsonStart = text.IndexOf('{');
                if (jsonStart < 0)
                {
                    return false;
                }

                payload = text.Substring(jsonStart).Trim();
            }

            if (payload.StartsWith("{", StringComparison.Ordinal))
            {
                return true;
            }

            int openBraceIndex = payload.IndexOf('{');
            if (openBraceIndex <= 0)
            {
                return false;
            }

            string possibleName = payload.Substring(0, openBraceIndex).Trim();
            return Regex.IsMatch(possibleName, @"^[a-zA-Z0-9_\-]+$");
        }

        private static bool TryExtractBalancedJsonObject(string text, int jsonStart, out string json)
        {
            json = string.Empty;
            int depth = 0;
            bool inString = false;
            bool escaped = false;
            for (int i = jsonStart; i < text.Length; i++)
            {
                char ch = text[i];
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (ch == '\\' && inString)
                {
                    escaped = true;
                    continue;
                }

                if (ch == '"')
                {
                    inString = !inString;
                    continue;
                }

                if (inString)
                {
                    continue;
                }

                if (ch == '{')
                {
                    depth++;
                }
                else if (ch == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        json = text.Substring(jsonStart, i - jsonStart + 1);
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
