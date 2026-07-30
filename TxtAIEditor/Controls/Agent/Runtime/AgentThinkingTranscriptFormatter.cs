using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace TxtAIEditor.Controls
{
    internal static class AgentThinkingTranscriptFormatter
    {
        private static readonly Regex ThoughtBlockRegex = new(
            @"<think>(?<thinking>.*?)(?:</think>|$)|" +
            @"<thought>(?<thinking>.*?)(?:</thought>|$)|" +
            @"<\|channel\>thought(?<thinking>.*?)(?:<channel\|>|$)",
            RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RetainedThinkingSectionRegex = new(
            @"^\[assistant: thinking\]\r?\n.*?(?=" +
            @"^\[(?:assistant: (?:tool call|final answer|response)|tool:|user\]|Retry detail:|Retry instruction\])|\z)",
            RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static string RemoveInlineThinking(string response)
        {
            if (string.IsNullOrEmpty(response))
            {
                return string.Empty;
            }

            return ThoughtBlockRegex.Replace(response, string.Empty);
        }

        public static string RemoveRetainedThinkingSections(string transcript)
        {
            if (string.IsNullOrEmpty(transcript))
            {
                return string.Empty;
            }

            return RetainedThinkingSectionRegex.Replace(transcript, string.Empty);
        }

        public static string CollectThinking(string streamedReasoning, string response)
        {
            var parts = new List<string>();
            AddIfPresent(parts, streamedReasoning);

            if (!string.IsNullOrEmpty(response))
            {
                foreach (Match match in ThoughtBlockRegex.Matches(response))
                {
                    AddIfPresent(parts, match.Groups["thinking"].Value);
                }
            }

            return string.Join(Environment.NewLine + Environment.NewLine, parts);
        }

        public static string BuildThinkingSection(string thinking)
        {
            if (string.IsNullOrWhiteSpace(thinking))
            {
                return string.Empty;
            }

            return $"[assistant: thinking]{Environment.NewLine}{thinking.Trim()}";
        }

        public static string BuildResponsePayload(string responseWithoutThinking, string thinking)
        {
            string thinkingSection = BuildThinkingSection(thinking);
            string response = responseWithoutThinking?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(thinkingSection))
            {
                return response;
            }

            if (string.IsNullOrEmpty(response))
            {
                return thinkingSection;
            }

            var builder = new StringBuilder();
            builder.AppendLine(thinkingSection);
            builder.AppendLine();
            builder.AppendLine("[assistant: response]");
            builder.Append(response);
            return builder.ToString();
        }

        public static string BuildFinalAnswerLog(string responseWithoutThinking, string thinking)
        {
            var builder = new StringBuilder();
            string thinkingSection = BuildThinkingSection(thinking);
            if (!string.IsNullOrEmpty(thinkingSection))
            {
                builder.AppendLine(thinkingSection);
                builder.AppendLine();
            }

            builder.AppendLine("[assistant: final answer]");
            builder.Append(responseWithoutThinking?.Trim() ?? string.Empty);
            return builder.ToString();
        }

        private static void AddIfPresent(List<string> parts, string? value)
        {
            string normalized = value?.Trim() ?? string.Empty;
            if (!string.IsNullOrEmpty(normalized))
            {
                parts.Add(normalized);
            }
        }
    }
}
