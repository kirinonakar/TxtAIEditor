using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TxtAIEditor.Controls
{
    internal static class AgentToolCallParser
    {
        private const string ToolCallOpenTag = "<tool_call>";
        private const string ToolCallCloseTag = "</tool_call>";
        private const int PlaywrightCliCommandTimeoutMs = 60000;

        public static bool ContainsToolCallSyntax(string response)
        {
            if (string.IsNullOrWhiteSpace(response))
            {
                return false;
            }

            string text = response.Trim();
            return text.IndexOf("<tool_call", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf(ToolCallCloseTag, StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("</invoke>", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("</\uFF5C\uFF5CDSML\uFF5C\uFF5Ctool_calls>", StringComparison.OrdinalIgnoreCase) >= 0 ||
                LooksLikeBareToolCallEnvelope(text) ||
                TryExtractSupportedCommandFence(text, out _);
        }

        public static bool TryGetToolCallFormatIssue(string response, out string detail)
        {
            detail = string.Empty;
            if (string.IsNullOrWhiteSpace(response))
            {
                return false;
            }

            string text = response.Trim();

            // Check XML/DSML format first
            int xmlOpenIndex = FindToolCallIndex(text);
            int xmlCloseIndex = FindLastToolCallCloseIndex(text);
            
            if (xmlOpenIndex >= 0 || xmlCloseIndex >= 0)
            {
                if (xmlOpenIndex < 0 || xmlCloseIndex < 0 || xmlCloseIndex < xmlOpenIndex)
                {
                    detail = "The tool_call tag must include one matching <tool_call ...>...</invoke> (or </tool_call>) pair.";
                    return true;
                }
                
                int closeTagLength = GetCloseTagLengthAt(text, xmlCloseIndex);
                int afterClose = xmlCloseIndex + closeTagLength;
                if (afterClose < text.Length && !string.IsNullOrWhiteSpace(text.Substring(afterClose)))
                {
                    detail = "The tool_call must be the final non-empty content; put any explanation before it, not after it.";
                    return true;
                }
                
                return false;
            }

            int openIndex = text.IndexOf(ToolCallOpenTag, StringComparison.OrdinalIgnoreCase);
            int closeIndex = text.LastIndexOf(ToolCallCloseTag, StringComparison.OrdinalIgnoreCase);
            if (openIndex < 0 && closeIndex < 0)
            {
                return false;
            }

            openIndex = text.LastIndexOf(ToolCallOpenTag, StringComparison.OrdinalIgnoreCase);
            closeIndex = text.LastIndexOf(ToolCallCloseTag, StringComparison.OrdinalIgnoreCase);

            if (openIndex < 0 || closeIndex < 0 || closeIndex < openIndex)
            {
                detail = "The tool_call tag must include one matching <tool_call>...</tool_call> pair.";
                return true;
            }

            int afterCloseStd = closeIndex + ToolCallCloseTag.Length;
            if (!string.IsNullOrWhiteSpace(text.Substring(afterCloseStd)))
            {
                detail = "The tool_call must be the final non-empty content; put any explanation before it, not after it.";
                return true;
            }

            return false;
        }

        internal struct ToolCallInfo
        {
            public string ToolName;
            public JsonElement Arguments;
        }

        public static bool TryParse(string response, out string toolName, out JsonElement arguments)
        {
            toolName = string.Empty;
            arguments = default;

            if (TryParseMulti(response, out var list) && list.Count > 0)
            {
                toolName = list[0].ToolName;
                arguments = list[0].Arguments;
                return true;
            }

            return false;
        }

        public static bool TryParseMulti(string response, out List<ToolCallInfo> toolCalls)
        {
            toolCalls = new List<ToolCallInfo>();

            if (string.IsNullOrWhiteSpace(response))
            {
                return false;
            }

            string text = response.Trim();

            // Try XML/DSML tool calls first
            if (text.IndexOf("<tool_call", StringComparison.OrdinalIgnoreCase) >= 0 && TryParseXmlToolCalls(text, toolCalls))
            {
                return toolCalls.Count > 0;
            }

            bool hasToolCallTagSyntax =
                text.IndexOf(ToolCallOpenTag, StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf(ToolCallCloseTag, StringComparison.OrdinalIgnoreCase) >= 0;

            // 1. Prefer the final text tool call, so explanatory examples earlier in the response are ignored.
            if (TryExtractTrailingToolCallTagPayload(text, out string trailingTagPayload) &&
                TryParsePayloads(trailingTagPayload, toolCalls))
            {
                return toolCalls.Count > 0;
            }

            if (hasToolCallTagSyntax)
            {
                return false;
            }

            // 2. Try bare payload.
            if (TryExtractBareToolCallPayload(text, out string barePayload))
            {
                if (TryParsePayloads(barePayload, toolCalls))
                {
                    return toolCalls.Count > 0;
                }
            }

            // 3. Try command fence.
            if (TryParseSupportedCommandFence(response, out string toolName, out JsonElement arguments))
            {
                toolCalls.Add(new ToolCallInfo { ToolName = toolName, Arguments = arguments });
                return true;
            }

            return false;
        }

        private static bool TryExtractTrailingToolCallTagPayload(string text, out string payload)
        {
            payload = string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            string trimmed = text.Trim();
            int closeIndex = trimmed.LastIndexOf(ToolCallCloseTag, StringComparison.OrdinalIgnoreCase);
            if (closeIndex >= 0)
            {
                int afterClose = closeIndex + ToolCallCloseTag.Length;
                if (!string.IsNullOrWhiteSpace(trimmed.Substring(afterClose)))
                {
                    return false;
                }

                int openIndex = trimmed.LastIndexOf(ToolCallOpenTag, closeIndex, StringComparison.OrdinalIgnoreCase);
                if (openIndex < 0)
                {
                    return false;
                }

                int payloadStart = openIndex + ToolCallOpenTag.Length;
                payload = trimmed.Substring(payloadStart, closeIndex - payloadStart).Trim();
                return !string.IsNullOrWhiteSpace(payload);
            }

            int trailingOpenIndex = trimmed.LastIndexOf(ToolCallOpenTag, StringComparison.OrdinalIgnoreCase);
            if (trailingOpenIndex < 0)
            {
                return false;
            }

            payload = trimmed.Substring(trailingOpenIndex + ToolCallOpenTag.Length).Trim();
            return !string.IsNullOrWhiteSpace(payload);
        }

        private static bool TryParsePayloads(string payload, List<ToolCallInfo> toolCalls)
        {
            if (string.IsNullOrWhiteSpace(payload))
            {
                return false;
            }

            string fixedPayload = FixJsonCarriageReturnEscapes(payload);
            string trimmedPayload = fixedPayload.Trim();

            while (trimmedPayload.StartsWith(ToolCallOpenTag, StringComparison.OrdinalIgnoreCase))
            {
                trimmedPayload = trimmedPayload.Substring(ToolCallOpenTag.Length).Trim();
            }
            while (trimmedPayload.EndsWith(ToolCallCloseTag, StringComparison.OrdinalIgnoreCase))
            {
                trimmedPayload = trimmedPayload.Substring(0, trimmedPayload.Length - ToolCallCloseTag.Length).Trim();
            }

            int index = 0;
            var list = new List<ToolCallInfo>();

            while (index < trimmedPayload.Length)
            {
                // Skip whitespace and closing braces
                while (index < trimmedPayload.Length && (char.IsWhiteSpace(trimmedPayload[index]) || trimmedPayload[index] == '}'))
                {
                    index++;
                }
                if (index >= trimmedPayload.Length)
                {
                    break;
                }

                if (trimmedPayload[index] == '{')
                {
                    int tempIndex = index;
                    if (TryExtractJsonToolCall(trimmedPayload, ref tempIndex, out var tc))
                    {
                        index = tempIndex;
                        list.Add(tc);
                    }
                    else if (TryExtractBalancedJsonObject(trimmedPayload, index, out string jsonStr))
                    {
                        index += jsonStr.Length;
                        if (TryParseJsonToolCall(jsonStr, out string tName, out JsonElement args))
                        {
                            list.Add(new ToolCallInfo { ToolName = tName, Arguments = args });
                        }
                        else
                        {
                            return false;
                        }
                    }
                    else
                    {
                        return false;
                    }
                }
                else
                {
                    int openBraceIndex = trimmedPayload.IndexOf('{', index);
                    if (openBraceIndex > index)
                    {
                        string possibleName = trimmedPayload.Substring(index, openBraceIndex - index).Trim();
                        if (Regex.IsMatch(possibleName, @"^[a-zA-Z0-9_\-]+$"))
                        {
                            if (TryExtractBalancedJsonObject(trimmedPayload, openBraceIndex, out string jsonStr))
                            {
                                index = openBraceIndex + jsonStr.Length;
                                if (TryParseBareArguments(jsonStr, out JsonElement args))
                                {
                                    list.Add(new ToolCallInfo { ToolName = possibleName, Arguments = args });
                                }
                                else
                                {
                                    return false;
                                }
                            }
                            else
                            {
                                return false;
                            }
                        }
                        else
                        {
                            return false;
                        }
                    }
                    else
                    {
                        return false;
                    }
                }
            }

            if (list.Count > 0)
            {
                toolCalls.AddRange(list);
                return true;
            }

            // Fallback: try parsing as a single payload
            if (TryParsePayload(payload, out string fallbackName, out JsonElement fallbackArgs))
            {
                toolCalls.Add(new ToolCallInfo { ToolName = fallbackName, Arguments = fallbackArgs });
                return true;
            }

            return false;
        }

        private static bool TryExtractJsonToolCall(string payload, ref int index, out ToolCallInfo toolCall)
        {
            toolCall = default;
            
            var nameMatch = Regex.Match(payload.Substring(index), @"\""name\""\s*:\s*\""(?<name>[^\""\\\\]*(?:\\\\.[^\""\\\\]*)*)\""", RegexOptions.IgnoreCase);
            if (!nameMatch.Success)
            {
                return false;
            }

            var argsMatch = Regex.Match(payload.Substring(index), @"\""arguments\""\s*:\s*", RegexOptions.IgnoreCase);
            if (!argsMatch.Success)
            {
                return false;
            }

            string toolName = DecodeLenientJsonString(nameMatch.Groups["name"].Value);
            
            int absoluteArgsMatchIndex = index + argsMatch.Index + argsMatch.Length;
            int openBraceIndex = payload.IndexOf('{', absoluteArgsMatchIndex);
            if (openBraceIndex < 0)
            {
                return false;
            }

            if (TryExtractBalancedJsonObject(payload, openBraceIndex, out string jsonStr))
            {
                if (TryParseBareArguments(jsonStr, out JsonElement args))
                {
                    toolCall = new ToolCallInfo { ToolName = toolName, Arguments = args };
                    index = openBraceIndex + jsonStr.Length;
                    return true;
                }
            }

            return false;
        }

        private static bool TryParseJsonToolCall(string jsonStr, out string toolName, out JsonElement arguments)
        {
            toolName = string.Empty;
            arguments = default;
            try
            {
                using var document = JsonDocument.Parse(jsonStr);
                var root = document.RootElement.Clone();
                if (root.TryGetProperty("name", out var nameProp))
                {
                    toolName = nameProp.GetString() ?? string.Empty;
                    arguments = BuildArgumentsElement(root, stripNameProperty: true);
                    return !string.IsNullOrWhiteSpace(toolName);
                }
            }
            catch
            {
                // Fall through
            }
            return false;
        }

        private static bool TryParseBareArguments(string jsonStr, out JsonElement arguments)
        {
            arguments = default;
            try
            {
                using var document = JsonDocument.Parse(jsonStr);
                var root = document.RootElement.Clone();
                arguments = BuildArgumentsElement(root, stripNameProperty: false);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryParsePayload(string payload, out string toolName, out JsonElement arguments)
        {
            toolName = string.Empty;
            arguments = default;
            if (string.IsNullOrWhiteSpace(payload))
            {
                return false;
            }

            string fixedPayload = FixJsonCarriageReturnEscapes(payload);
            string trimmedPayload = fixedPayload.Trim();

            while (trimmedPayload.StartsWith(ToolCallOpenTag, StringComparison.OrdinalIgnoreCase))
            {
                trimmedPayload = trimmedPayload.Substring(ToolCallOpenTag.Length).Trim();
            }
            while (trimmedPayload.EndsWith(ToolCallCloseTag, StringComparison.OrdinalIgnoreCase))
            {
                trimmedPayload = trimmedPayload.Substring(0, trimmedPayload.Length - ToolCallCloseTag.Length).Trim();
            }

            int openBraceIndex = trimmedPayload.IndexOf('{');
            if (openBraceIndex > 0)
            {
                string possibleName = trimmedPayload.Substring(0, openBraceIndex).Trim();
                if (Regex.IsMatch(possibleName, @"^[a-zA-Z0-9_\-]+$"))
                {
                    toolName = possibleName;
                    string jsonPart = trimmedPayload.Substring(openBraceIndex);

                    if (TryExtractBalancedJsonObject(trimmedPayload, openBraceIndex, out string balancedJson))
                    {
                        jsonPart = balancedJson;
                    }

                    try
                    {
                        using var document = JsonDocument.Parse(jsonPart);
                        var root = document.RootElement.Clone();
                        arguments = BuildArgumentsElement(root, stripNameProperty: false);
                        return !string.IsNullOrWhiteSpace(toolName);
                    }
                    catch
                    {
                        // Fall through
                    }
                }
            }

            int jsonStart = trimmedPayload.IndexOf('{');
            if (jsonStart >= 0 && TryExtractBalancedJsonObject(trimmedPayload, jsonStart, out string json))
            {
                string fixedJson = FixJsonCarriageReturnEscapes(json);
                try
                {
                    using var document = JsonDocument.Parse(fixedJson);
                    var root = document.RootElement.Clone();
                    if (root.TryGetProperty("name", out var nameProp))
                    {
                        toolName = nameProp.GetString() ?? string.Empty;
                        arguments = BuildArgumentsElement(root, stripNameProperty: true);

                        return !string.IsNullOrWhiteSpace(toolName);
                    }
                }
                catch
                {
                    // Fall through
                }
            }

            return TryParseLenient(trimmedPayload, out toolName, out arguments);
        }

        private static bool TryParseSupportedCommandFence(string response, out string toolName, out JsonElement arguments)
        {
            toolName = string.Empty;
            arguments = default;

            if (!TryExtractSupportedCommandFence(response, out string command))
            {
                return false;
            }

            toolName = "run_powershell";
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                command,
                timeoutMs = PlaywrightCliCommandTimeoutMs
            }));
            arguments = document.RootElement.Clone();
            return true;
        }

        private static JsonElement BuildArgumentsElement(JsonElement root, bool stripNameProperty)
        {
            if (root.ValueKind != JsonValueKind.Object)
            {
                return root.Clone();
            }

            if (!stripNameProperty)
            {
                if (root.TryGetProperty("arguments", out var onlyArguments) &&
                    root.EnumerateObject().Count() == 1 &&
                    onlyArguments.ValueKind == JsonValueKind.Object)
                {
                    return onlyArguments.Clone();
                }

                return root.Clone();
            }

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                var writtenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                if (root.TryGetProperty("arguments", out var argumentsProperty))
                {
                    if (argumentsProperty.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var property in argumentsProperty.EnumerateObject())
                        {
                            property.WriteTo(writer);
                            writtenNames.Add(property.Name);
                        }
                    }
                    else
                    {
                        writer.WritePropertyName("arguments");
                        argumentsProperty.WriteTo(writer);
                        writtenNames.Add("arguments");
                    }
                }

                foreach (var property in root.EnumerateObject())
                {
                    if (property.NameEquals("name") ||
                        property.NameEquals("arguments") ||
                        writtenNames.Contains(property.Name))
                    {
                        continue;
                    }

                    property.WriteTo(writer);
                    writtenNames.Add(property.Name);
                }

                writer.WriteEndObject();
            }

            using var document = JsonDocument.Parse(stream.ToArray());
            return document.RootElement.Clone();
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

            try
            {
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
            catch
            {
                return false;
            }
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
            int toolCallIndex = text.IndexOf(ToolCallOpenTag, StringComparison.OrdinalIgnoreCase);
            if (toolCallIndex >= 0)
            {
                text = text.Substring(toolCallIndex + ToolCallOpenTag.Length).TrimStart();
            }
            else if (!text.StartsWith("{", StringComparison.Ordinal))
            {
                return false;
            }

            int jsonStart = text.IndexOf('{');
            if (jsonStart < 0)
            {
                return false;
            }

            return TryExtractBalancedJsonObject(text, jsonStart, out json);
        }

        private static bool TryExtractSupportedCommandFence(string response, out string command)
        {
            command = string.Empty;
            if (string.IsNullOrWhiteSpace(response))
            {
                return false;
            }

            string normalized = response.Replace("\r\n", "\n").Replace('\r', '\n');
            foreach (Match match in Regex.Matches(
                normalized,
                @"(?ms)^[ \t]*```(?<info>[^\n`]*)\n(?<body>.*?)(?:\n)[ \t]*```[ \t]*\z"))
            {
                string info = match.Groups["info"].Value.Trim();
                string body = match.Groups["body"].Value;
                if (IsShellCommandFenceInfo(info) &&
                    TryNormalizeSupportedCommandFenceBody(body, out command))
                {
                    return true;
                }
            }

            foreach (Match match in Regex.Matches(
                normalized,
                @"(?ms)^[ \t]*`(?<info>bash|sh|shell|powershell|pwsh|ps1)[ \t]*\n(?<body>.*?)(?:\n)[ \t]*`[ \t]*\z"))
            {
                string info = match.Groups["info"].Value.Trim();
                string body = match.Groups["body"].Value;
                if (IsShellCommandFenceInfo(info) &&
                    TryNormalizeSupportedCommandFenceBody(body, out command))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsShellCommandFenceInfo(string info)
        {
            if (string.IsNullOrWhiteSpace(info))
            {
                return false;
            }

            string language = info.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault() ?? string.Empty;
            return language.Equals("bash", StringComparison.OrdinalIgnoreCase) ||
                language.Equals("sh", StringComparison.OrdinalIgnoreCase) ||
                language.Equals("shell", StringComparison.OrdinalIgnoreCase) ||
                language.Equals("powershell", StringComparison.OrdinalIgnoreCase) ||
                language.Equals("pwsh", StringComparison.OrdinalIgnoreCase) ||
                language.Equals("ps1", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryNormalizeSupportedCommandFenceBody(string body, out string command)
        {
            command = string.Empty;
            if (string.IsNullOrWhiteSpace(body))
            {
                return false;
            }

            var lines = new List<string>();
            foreach (string rawLine in body.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                string line = rawLine.Trim();
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (line.StartsWith("$ ", StringComparison.Ordinal))
                {
                    line = line.Substring(2).TrimStart();
                }

                if (!IsSupportedPlaywrightCliCommand(line))
                {
                    return false;
                }

                lines.Add(line);
            }

            if (lines.Count == 0)
            {
                return false;
            }

            command = string.Join("\n", lines);
            return true;
        }

        private static bool IsSupportedPlaywrightCliCommand(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return false;
            }

            if (!line.Equals("playwright-cli", StringComparison.OrdinalIgnoreCase) &&
                !line.StartsWith("playwright-cli ", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string[] blockedTokens = { ";", "&&", "||", "|", ">", "<", "`", "$(", "@(", "&" };
            return !blockedTokens.Any(token => line.Contains(token, StringComparison.Ordinal));
        }

        private static bool TryExtractToolCallPayload(string response, out string payload)
        {
            payload = string.Empty;
            if (string.IsNullOrWhiteSpace(response))
            {
                return false;
            }

            string text = response.Trim();
            int toolCallIndex = text.IndexOf(ToolCallOpenTag, StringComparison.OrdinalIgnoreCase);
            if (toolCallIndex >= 0)
            {
                int payloadStart = toolCallIndex + ToolCallOpenTag.Length;
                int payloadEnd = text.IndexOf(ToolCallCloseTag, payloadStart, StringComparison.OrdinalIgnoreCase);
                payload = payloadEnd >= 0
                    ? text.Substring(payloadStart, payloadEnd - payloadStart).Trim()
                    : text.Substring(payloadStart).Trim();
            }
            else if (TryExtractBareToolCallPayload(text, out string barePayload))
            {
                payload = barePayload;
            }
            else
            {
                return false;
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

        private static bool LooksLikeBareToolCallEnvelope(string text)
        {
            if (!TryExtractBareToolCallPayload(text, out string payload))
            {
                return false;
            }

            if (!payload.StartsWith("{", StringComparison.Ordinal))
            {
                return true;
            }

            try
            {
                using var document = JsonDocument.Parse(payload);
                return document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("name", out var nameProperty) &&
                    nameProperty.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(nameProperty.GetString());
            }
            catch
            {
                return false;
            }
        }

        private static bool TryExtractBareToolCallPayload(string text, out string payload)
        {
            payload = string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            string trimmed = text.Trim();
            if (TryExtractTrailingMarkdownCodeFencePayload(trimmed, out string fencedPayload))
            {
                return TryExtractBareToolCallPayload(fencedPayload, out payload);
            }

            if (trimmed.StartsWith("{", StringComparison.Ordinal))
            {
                if (TryExtractBalancedJsonObject(trimmed, 0, out payload) &&
                    payload.Length == trimmed.Length)
                {
                    return true;
                }

                return TryExtractTrailingBareToolCallPayload(trimmed, out payload);
            }

            int openBraceIndex = trimmed.IndexOf('{');
            if (openBraceIndex <= 0)
            {
                return TryExtractTrailingBareToolCallPayload(trimmed, out payload);
            }

            string possibleName = trimmed.Substring(0, openBraceIndex).Trim();
            if (!Regex.IsMatch(possibleName, @"^[a-zA-Z0-9_\-]+$"))
            {
                return TryExtractTrailingBareToolCallPayload(trimmed, out payload);
            }

            if (!TryExtractBalancedJsonObject(trimmed, openBraceIndex, out string json) ||
                openBraceIndex + json.Length != trimmed.Length)
            {
                return TryExtractTrailingBareToolCallPayload(trimmed, out payload);
            }

            payload = trimmed;
            return true;
        }

        private static bool TryExtractTrailingBareToolCallPayload(string text, out string payload)
        {
            payload = string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            string trimmed = text.Trim();
            foreach (Match match in Regex.Matches(
                trimmed,
                @"(?m)^[ \t]*(?<name>[a-zA-Z0-9_\-]+)[ \t]*(?=\{)"))
            {
                int lineStart = match.Index;
                int nameEnd = match.Index + match.Length;
                int openBraceIndex = trimmed.IndexOf('{', nameEnd);
                if (openBraceIndex < 0)
                {
                    continue;
                }

                if (!TryExtractBalancedJsonObject(trimmed, openBraceIndex, out string json) ||
                    openBraceIndex + json.Length != trimmed.Length)
                {
                    continue;
                }

                payload = trimmed.Substring(lineStart).Trim();
                return true;
            }

            for (int i = 0; i < trimmed.Length; i++)
            {
                if (trimmed[i] != '{' || !IsAtLineStart(trimmed, i))
                {
                    continue;
                }

                if (!TryExtractBalancedJsonObject(trimmed, i, out string json) ||
                    i + json.Length != trimmed.Length)
                {
                    continue;
                }

                if (TryParseJsonToolCall(json, out _, out _))
                {
                    payload = json;
                    return true;
                }
            }

            return false;
        }

        public static int FindToolCallIndex(string text)
        {
            if (string.IsNullOrEmpty(text)) return -1;
            int idx = 0;
            while (true)
            {
                idx = text.IndexOf("<tool_call", idx, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) return -1;
                
                int nextCharIdx = idx + "<tool_call".Length;
                if (nextCharIdx >= text.Length)
                {
                    return idx; // streaming boundary
                }
                
                char nextChar = text[nextCharIdx];
                if (nextChar == '>' || char.IsWhiteSpace(nextChar))
                {
                    return idx;
                }
                idx += 10;
            }
        }

        public static int FindLastToolCallCloseIndex(string text)
        {
            if (string.IsNullOrEmpty(text)) return -1;
            string[] closeTags = { "</tool_call>", "</invoke>", "</\uFF5C\uFF5CDSML\uFF5C\uFF5Ctool_calls>" };
            int lastIdx = -1;
            foreach (var tag in closeTags)
            {
                int idx = text.LastIndexOf(tag, StringComparison.OrdinalIgnoreCase);
                if (idx > lastIdx)
                {
                    lastIdx = idx;
                }
            }
            return lastIdx;
        }

        private static int GetCloseTagLengthAt(string text, int index)
        {
            string[] closeTags = { "</tool_call>", "</invoke>", "</\uFF5C\uFF5CDSML\uFF5C\uFF5Ctool_calls>" };
            foreach (var tag in closeTags)
            {
                if (index + tag.Length <= text.Length &&
                    text.Substring(index, tag.Length).Equals(tag, StringComparison.OrdinalIgnoreCase))
                {
                    return tag.Length;
                }
            }
            return 0;
        }

        private static bool TryParseXmlToolCalls(string text, List<ToolCallInfo> toolCalls)
        {
            var toolCallRegex = new Regex("<tool_call\\s+name=[\"']?(?<name>[a-zA-Z0-9_\\-]+)[\"']?\\s*>(?<body>.*?)(?:</invoke>|</tool_call>|</\uFF5C\uFF5CDSML\uFF5C\uFF5Ctool_calls>|\\z)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            
            var matches = toolCallRegex.Matches(text);
            if (matches.Count == 0)
            {
                return false;
            }

            bool anyParsed = false;
            foreach (Match match in matches)
            {
                string toolName = match.Groups["name"].Value;
                string body = match.Groups["body"].Value;

                var paramRegex = new Regex("<parameter\\s+name=[\"']?(?<paramName>[a-zA-Z0-9_\\-]+)[\"']?[^>]*>(?<paramValue>.*?)</parameter>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                
                var paramMatches = paramRegex.Matches(body);
                var argumentValues = new Dictionary<string, object>();
                
                foreach (Match paramMatch in paramMatches)
                {
                    string paramName = paramMatch.Groups["paramName"].Value;
                    string paramValueRaw = paramMatch.Groups["paramValue"].Value;
                    
                    string paramValue = System.Net.WebUtility.HtmlDecode(paramValueRaw);
                    object parsedValue = paramValue;
                    
                    bool isStringForce = paramMatch.Value.Contains("string=\"true\"", StringComparison.OrdinalIgnoreCase) ||
                                         paramMatch.Value.Contains("type=\"string\"", StringComparison.OrdinalIgnoreCase) ||
                                         paramMatch.Value.Contains("type='string'", StringComparison.OrdinalIgnoreCase);
                                         
                    if (!isStringForce)
                    {
                        if (string.Equals(paramValue, "true", StringComparison.OrdinalIgnoreCase))
                        {
                            parsedValue = true;
                        }
                        else if (string.Equals(paramValue, "false", StringComparison.OrdinalIgnoreCase))
                        {
                            parsedValue = false;
                        }
                        else if (int.TryParse(paramValue, out int intVal))
                        {
                            parsedValue = intVal;
                        }
                        else if (double.TryParse(paramValue, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double doubleVal))
                        {
                            parsedValue = doubleVal;
                        }
                    }
                    
                    argumentValues[paramName] = parsedValue;
                }

                try
                {
                    string jsonStr = JsonSerializer.Serialize(argumentValues);
                    using var doc = JsonDocument.Parse(jsonStr);
                    toolCalls.Add(new ToolCallInfo
                    {
                        ToolName = toolName,
                        Arguments = doc.RootElement.Clone()
                    });
                    anyParsed = true;
                }
                catch
                {
                    // Ignore
                }
            }

            return anyParsed;
        }

        private static bool TryExtractTrailingMarkdownCodeFencePayload(string text, out string payload)
        {
            payload = string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            string normalized = text.Trim().Replace("\r\n", "\n").Replace('\r', '\n');
            int closingFenceIndex = normalized.LastIndexOf("```", StringComparison.Ordinal);
            if (closingFenceIndex <= 0 ||
                !IsAtLineStart(normalized, closingFenceIndex) ||
                !string.IsNullOrWhiteSpace(normalized.Substring(closingFenceIndex + 3)))
            {
                return false;
            }

            int searchIndex = closingFenceIndex - 1;
            while (searchIndex >= 0)
            {
                int openingFenceIndex = normalized.LastIndexOf("```", searchIndex, StringComparison.Ordinal);
                if (openingFenceIndex < 0)
                {
                    return false;
                }

                if (!IsAtLineStart(normalized, openingFenceIndex))
                {
                    searchIndex = openingFenceIndex - 1;
                    continue;
                }

                int infoLineEnd = FindFirstLineEnd(normalized, openingFenceIndex + 3);
                if (infoLineEnd < 0 || infoLineEnd >= closingFenceIndex)
                {
                    searchIndex = openingFenceIndex - 1;
                    continue;
                }

                string info = normalized.Substring(openingFenceIndex + 3, infoLineEnd - openingFenceIndex - 3).Trim();
                if (!IsToolCallFenceInfo(info))
                {
                    return false;
                }

                int contentStart = infoLineEnd + 1;
                payload = normalized.Substring(contentStart, closingFenceIndex - contentStart).Trim();
                return !string.IsNullOrWhiteSpace(payload);
            }

            return false;
        }

        private static bool IsToolCallFenceInfo(string info)
        {
            if (string.IsNullOrWhiteSpace(info))
            {
                return true;
            }

            string language = info.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault() ?? string.Empty;
            return language.Equals("json", StringComparison.OrdinalIgnoreCase) ||
                language.Equals("jsonc", StringComparison.OrdinalIgnoreCase) ||
                language.Equals("tool_call", StringComparison.OrdinalIgnoreCase) ||
                language.Equals("tool-call", StringComparison.OrdinalIgnoreCase);
        }

        private static int FindFirstLineEnd(string text, int startIndex)
        {
            int lineFeedIndex = text.IndexOf('\n', startIndex);
            int carriageReturnIndex = text.IndexOf('\r', startIndex);
            if (lineFeedIndex < 0)
            {
                return carriageReturnIndex;
            }

            if (carriageReturnIndex < 0)
            {
                return lineFeedIndex;
            }

            return Math.Min(lineFeedIndex, carriageReturnIndex);
        }

        private static bool IsAtLineStart(string text, int index)
        {
            if (index <= 0)
            {
                return true;
            }

            char previous = text[index - 1];
            return previous == '\n' || previous == '\r';
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
