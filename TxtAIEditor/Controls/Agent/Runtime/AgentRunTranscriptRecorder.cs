using System;

namespace TxtAIEditor.Controls
{
    internal static class AgentRunTranscriptRecorder
    {
        public static void AppendLine(AgentRunContext context, string line = "")
        {
            string logLine = AgentRunTranscriptService.ConvertToolCallTagsToLogTags(line);
            context.SessionHistory.AppendLine(logLine);
            context.SessionHistoryTokenCount += AgentTokenEstimator.Estimate(logLine + Environment.NewLine);
        }

        private static void AppendModelLine(AgentRunContext context, string line = "")
        {
            context.ModelSessionHistory.AppendLine(line);
        }

        public static void AppendPromptTranscript(
            AgentRunContext context,
            string instruction,
            string transcript,
            string initialTranscript)
        {
            AppendPromptTranscriptAndResponse(
                context,
                instruction,
                transcript,
                initialTranscript,
                null);
        }

        public static void AppendPromptTranscriptAndResponse(
            AgentRunContext context,
            string instruction,
            string transcript,
            string initialTranscript,
            string? responseLine)
        {
            if (!string.IsNullOrWhiteSpace(context.ApiType))
            {
                AppendLine(context, $"[LLM API: {context.ApiType}]");
                context.ApiType = string.Empty;
            }

            string runTranscript = transcript.Substring(initialTranscript.Length);
            if (!string.IsNullOrWhiteSpace(runTranscript))
            {
                AppendLine(context, runTranscript.Trim());
                AppendModelLine(context, runTranscript.Trim());
            }

            if (context.RetryDebugHistory.Length > 0)
            {
                AppendLine(context, context.RetryDebugHistory.ToString().Trim());
            }

            if (!string.IsNullOrEmpty(responseLine))
            {
                const string legacyPrefix = "[Agent Response]:";
                if (responseLine.StartsWith(legacyPrefix, StringComparison.Ordinal))
                {
                    AppendLine(context, "[assistant: final answer]");
                    AppendModelLine(context, "[assistant: final answer]");
                    string responseText = responseLine.Substring(legacyPrefix.Length).TrimStart();
                    AppendLine(context, responseText);
                    AppendModelLine(context, responseText);
                }
                else
                {
                    AppendLine(context, responseLine);
                    AppendModelLine(context, responseLine);
                }
            }

            AppendLine(context);
            AppendModelLine(context);
        }
    }
}
