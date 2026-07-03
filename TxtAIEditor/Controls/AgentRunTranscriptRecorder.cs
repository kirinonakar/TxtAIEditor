using System;

namespace TxtAIEditor.Controls
{
    internal static class AgentRunTranscriptRecorder
    {
        public static void AppendLine(AgentRunContext context, string line = "")
        {
            context.SessionHistory.AppendLine(line);
            context.SessionHistoryTokenCount += AgentTokenEstimator.Estimate(line + Environment.NewLine);
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
            AppendLine(context, $"[User Prompt]: {instruction}");
            string runTranscript = transcript.Substring(initialTranscript.Length);
            if (!string.IsNullOrWhiteSpace(runTranscript))
            {
                AppendLine(context, runTranscript.Trim());
            }

            if (!string.IsNullOrEmpty(responseLine))
            {
                AppendLine(context, responseLine);
            }

            AppendLine(context);
        }
    }
}
