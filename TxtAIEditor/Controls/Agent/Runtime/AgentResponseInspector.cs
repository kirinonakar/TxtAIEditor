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
            builder.AppendLine("Include one or more parseable <tool_call>...</tool_call> tags, or native function tool calls, for the actions you want executed.");
            builder.AppendLine("For text tool calls, keep multiple calls in the final contiguous block and make each tag contain one valid JSON call. Re-emit the explanation if useful, include the tool calls, or write the final answer with no tool_call tag.");
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

        public bool LooksLikeToolResultReplay(string response)
        {
            if (string.IsNullOrWhiteSpace(response))
            {
                return false;
            }

            string trimmed = response.TrimStart();
            return trimmed.StartsWith("[Tool result:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("[Tool result]", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("[도구 결과:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("[도구 결과]", StringComparison.OrdinalIgnoreCase);
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

        public string BuildPendingToolIntentRetryNote()
        {
            var builder = new StringBuilder();
            builder.AppendLine("[Agent tool call missing]");
            builder.AppendLine("The previous assistant response described the next action in prose but emitted no tool call, so nothing was executed and the turn would have ended before the work finished.");
            builder.AppendLine("If the task is not complete, emit the actual tool call for that action now, using the same tool call format as the previous turns.");
            builder.AppendLine("If the task is already complete, restate the final answer without announcing further actions.");
            return builder.ToString().TrimEnd();
        }

        public bool LooksLikeUnfinishedToolIntent(string response)
        {
            if (string.IsNullOrWhiteSpace(response))
            {
                return false;
            }

            string lower = response.ToLowerInvariant();

            string[] intentMarkers =
            {
                "클릭하겠",
                "클릭합니다",
                "선택하겠",
                "선택합니다",
                "입력하겠",
                "입력합니다",
                "드래그하겠",
                "눌러서",
                "실행하겠",
                "호출하겠",
                "적용하겠",
                "저장하겠",
                "작성하겠",
                "i'll click",
                "i will click",
                "let me click",
                "i'll type",
                "i will type",
                "let me type",
                "i'll press",
                "let me press",
                "i'll drag",
                "let me drag",
                "i'll call",
                "i will call",
                "let me call",
                "i'll run",
                "let me run",
                "i'll select",
                "let me select",
                "next, i'll",
                "next, i will",
            };

            foreach (string marker in intentMarkers)
            {
                if (lower.Contains(marker))
                {
                    return true;
                }
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
