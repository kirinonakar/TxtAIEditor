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
            string initialTranscript,
            string? modelTranscript = null)
        {
            AppendPromptTranscriptAndResponse(
                context,
                instruction,
                transcript,
                initialTranscript,
                null,
                modelTranscript);
        }

        public static void AppendFailedRun(
            AgentRunContext context,
            string instruction,
            string transcript,
            string initialTranscript,
            string modelTranscript,
            bool responseStreamInProgress,
            string failureMessage)
        {
            // Context collection can fail before the current user turn is added.
            if (string.IsNullOrEmpty(transcript))
            {
                transcript = instruction.Trim() + Environment.NewLine;
                initialTranscript = string.Empty;
                modelTranscript = context.ModelSessionHistory.ToString().TrimEnd() +
                    Environment.NewLine + transcript;
            }

            AppendPromptTranscript(context, instruction, transcript, initialTranscript, modelTranscript);

            // Preserve the unfinished stream for history only. It is neither a final
            // answer nor a valid tool call to send back to the model on the next turn.
            if (responseStreamInProgress)
            {
                string partialResponse = AgentThinkingTranscriptFormatter.BuildResponsePayload(
                    AgentThinkingTranscriptFormatter.RemoveInlineThinking(context.StreamingResponseText),
                    context.LlmSettings.LlmRetainThinking
                        ? AgentThinkingTranscriptFormatter.CollectThinking(
                            context.StreamingReasoningText, context.StreamingResponseText)
                        : string.Empty);
                if (!string.IsNullOrWhiteSpace(partialResponse))
                {
                    AppendLine(context, "[assistant: response]");
                    AppendLine(context, partialResponse);
                    AppendLine(context);
                    context.CurrentRunTranscriptTokens += AgentTokenEstimator.Estimate(partialResponse);
                }
            }

            AppendLine(context, "[assistant: error]");
            AppendLine(context, failureMessage);
            AppendLine(context);
        }

        public static void AppendPromptTranscriptAndResponse(
            AgentRunContext context,
            string instruction,
            string transcript,
            string initialTranscript,
            string? responseLine,
            string? modelTranscript = null)
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
                if (modelTranscript == null)
                {
                    AppendModelLine(context, runTranscript.Trim());
                }
            }

            if (modelTranscript != null)
            {
                context.ModelSessionHistory.Clear();
                if (!string.IsNullOrWhiteSpace(modelTranscript))
                {
                    context.ModelSessionHistory.Append(modelTranscript.TrimEnd());
                    context.ModelSessionHistory.AppendLine();
                }
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
