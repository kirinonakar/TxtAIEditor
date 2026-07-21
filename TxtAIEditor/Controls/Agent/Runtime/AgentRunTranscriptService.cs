using System;
using System.Collections.Generic;
using System.Text;
using TxtAIEditor.Core.Models;
using static TxtAIEditor.Controls.AgentToolHelpers;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentRunTranscriptService
    {
        public string BuildWithEditLedger(
            string transcript,
            int currentTaskStartEditIndex,
            IReadOnlyList<AgentFileEditPreview> sessionEdits)
        {
            var builder = new StringBuilder(transcript);
            string earlierEdits = BuildDiffLog(sessionEdits, 0, currentTaskStartEditIndex);
            string currentTaskEdits = BuildDiffLog(
                sessionEdits,
                currentTaskStartEditIndex,
                sessionEdits.Count);

            builder.AppendLine();
            builder.AppendLine();
            builder.AppendLine("[Accepted file edits before this user task]");
            builder.AppendLine(string.IsNullOrEmpty(earlierEdits)
                ? "(No earlier accepted file edits in this agent session.)"
                : earlierEdits);
            builder.AppendLine();
            builder.AppendLine("[File edits made during this user task]");
            builder.AppendLine(string.IsNullOrEmpty(currentTaskEdits)
                ? "(No file edits have been made for this user task yet.)"
                : currentTaskEdits);
            builder.AppendLine();
            builder.AppendLine("[Edit timing guidance]");
            builder.AppendLine("Use the two edit sections to distinguish timing. Edits under [File edits made during this user task] were made by your tool calls for the current request; do not describe them as already done before the request.");
            return builder.ToString();
        }

        public string AddToolTimingNote(
            string normalizedToolName,
            string toolResultForTranscript,
            string toolResult)
        {
            if (!IsMutatingTool(normalizedToolName) || !IsSuccessfulToolResult(toolResult))
            {
                return toolResultForTranscript;
            }

            string timingNote = IsUnchangedEditCompletionResult(toolResult)
                ? "[Edit timing: this tool call did not write a file because the target already matched before this tool call.]"
                : "[Edit timing: this tool call changed state for the current user task. In the final answer, describe it as a change made in this run, not as something that was already done before the request.]";

            return AppendToolStatusMessage(toolResultForTranscript, timingNote);
        }

        public string BuildRetryDetail(
            string retryType,
            string previousResponse,
            string retryInstruction)
        {
            var builder = new StringBuilder();
            builder.AppendLine();
            builder.AppendLine();
            builder.AppendLine($"[Retry detail: {retryType}]");
            if (string.Equals(retryType, "tool_result_replay", StringComparison.Ordinal))
            {
                builder.AppendLine("[Tool execution: not executed; the model replayed a previous result]");
            }
            if (!string.IsNullOrEmpty(previousResponse))
            {
                builder.AppendLine(retryType is "tool_call_format" or "tool_result_replay"
                    ? "[Failed tool call response]"
                    : "[Previous response]");
                builder.AppendLine(previousResponse);
            }

            builder.AppendLine("[Retry instruction]");
            builder.AppendLine(retryInstruction);
            builder.Append("[End retry detail]");
            return builder.ToString();
        }

        public static string RemoveRetryDebugDetails(string history)
        {
            if (string.IsNullOrEmpty(history))
            {
                return string.Empty;
            }

            string[] lines = history.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            var builder = new StringBuilder(history.Length);
            bool inRetryDetail = false;
            bool inLegacyRetryPayload = false;

            foreach (string line in lines)
            {
                if (inRetryDetail)
                {
                    if (line.StartsWith("[End retry detail]", StringComparison.OrdinalIgnoreCase))
                    {
                        inRetryDetail = false;
                    }

                    continue;
                }

                if (line.StartsWith("[Retry detail:", StringComparison.OrdinalIgnoreCase))
                {
                    inRetryDetail = true;
                    continue;
                }

                if (inLegacyRetryPayload)
                {
                    if (!line.StartsWith("[User Prompt]:", StringComparison.OrdinalIgnoreCase) &&
                        !line.StartsWith("[user]", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    inLegacyRetryPayload = false;
                }

                if (line.StartsWith("[Previous Tool Call]:", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("[Previous Response]:", StringComparison.OrdinalIgnoreCase))
                {
                    inLegacyRetryPayload = true;
                    continue;
                }

                builder.AppendLine(line);
            }

            return builder.ToString().TrimEnd();
        }

        private static string BuildDiffLog(IReadOnlyList<AgentFileEditPreview> edits, int startIndex, int endIndex)
        {
            startIndex = Math.Max(0, startIndex);
            endIndex = Math.Min(edits.Count, Math.Max(startIndex, endIndex));
            if (startIndex >= endIndex)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            for (int i = startIndex; i < endIndex; i++)
            {
                AgentFileEditPreview edit = edits[i];
                builder.AppendLine($"--- File: {edit.RelativePath} (Action: {edit.ActionName}) ---");
                builder.AppendLine(edit.IsNewFile ? "[New File]" : "[Modified File]");
                builder.AppendLine($"Old chars: {edit.OldContent?.Length ?? 0:N0}");
                builder.AppendLine($"New chars: {edit.NewContent?.Length ?? 0:N0}");
                builder.AppendLine();
            }

            return builder.ToString().TrimEnd();
        }
    }
}
