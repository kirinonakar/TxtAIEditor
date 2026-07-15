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
            if (!string.IsNullOrEmpty(previousResponse))
            {
                builder.AppendLine(string.Equals(retryType, "tool_call_format", StringComparison.Ordinal)
                    ? "[Failed tool call response]"
                    : "[Previous response]");
                builder.AppendLine(previousResponse);
            }

            builder.AppendLine("[Retry instruction]");
            builder.AppendLine(retryInstruction);
            builder.Append("[End retry detail]");
            return builder.ToString();
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
