using System;
using System.Text;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentResponseInspector
    {
        public string BuildToolCallFormatRetryNote(string detail)
        {
            var builder = new StringBuilder();
            builder.AppendLine("[Agent tool call format error]");
            builder.AppendLine("The previous assistant response was not executed.");
            builder.AppendLine("A tool turn may include brief explanatory text before or after the tool call.");
            builder.AppendLine("Include exactly one parseable <tool_call>...</tool_call> tag or native function tool call for the action you want executed.");
            builder.AppendLine("Re-emit the explanation if useful, include the tool_call, or write the final answer with no tool_call tag.");
            if (!string.IsNullOrWhiteSpace(detail))
            {
                builder.AppendLine($"Parser detail: {detail}");
            }

            return builder.ToString().TrimEnd();
        }

        public bool LooksLikeStreamedToolCallEnvelopeStart(string trimmed)
        {
            if (trimmed.StartsWith("{", StringComparison.Ordinal))
            {
                return true;
            }

            if (!trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                return false;
            }

            string fenceInfo = trimmed.Substring(3).TrimStart();
            return StartsWithFenceLanguage(fenceInfo, "json") ||
                StartsWithFenceLanguage(fenceInfo, "jsonc") ||
                StartsWithFenceLanguage(fenceInfo, "tool_call") ||
                StartsWithFenceLanguage(fenceInfo, "tool-call");
        }

        public bool ResponseMentionsSkillIntent(string response)
        {
            if (string.IsNullOrWhiteSpace(response)) return false;
            string lower = response.ToLowerInvariant();

            if (lower.Contains("skill_use") || lower.Contains("skill use")) return true;
            if (!lower.Contains("skill")) return false;

            string[] intentMarkers =
            {
                "i should use",
                "i need to use",
                "i'll use",
                "i will use",
                "let me use",
                "let me call",
                "i should call",
                "i need to call",
                "i'll call",
                "i will call",
                "going to use",
                "going to call",
                "use the",
                "call the",
            };

            foreach (string marker in intentMarkers)
            {
                if (lower.Contains(marker) && lower.Contains("skill")) return true;
            }

            return false;
        }

        private static bool StartsWithFenceLanguage(string fenceInfo, string language)
        {
            if (!fenceInfo.StartsWith(language, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (fenceInfo.Length == language.Length)
            {
                return true;
            }

            return char.IsWhiteSpace(fenceInfo[language.Length]);
        }
    }
}
