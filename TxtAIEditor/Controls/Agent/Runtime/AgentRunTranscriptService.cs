using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using TxtAIEditor.Core.Models;
using static TxtAIEditor.Controls.AgentToolHelpers;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentRunTranscriptService
    {
        public static string ConvertToolCallTagsToLogTags(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text ?? string.Empty;
            }

            text = Regex.Replace(text, @"<tool_call(?=[\s>])", "<log_tool_call", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"</tool_call>", "</log_tool_call>", RegexOptions.IgnoreCase);
            return text;
        }

        public static string ConvertUserRequestMarkersForHistory(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text ?? string.Empty;
            }

            string newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            string[] lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            bool inUserTurn = false;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.StartsWith("[user]", StringComparison.OrdinalIgnoreCase))
                {
                    inUserTurn = true;
                    continue;
                }

                if (inUserTurn && line.Equals("[User request]", StringComparison.OrdinalIgnoreCase))
                {
                    lines[i] = "[User Prompt]:";
                    continue;
                }

                if (line.StartsWith("[assistant:", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("[Agent tool call]", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("[Tool result:", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("[tool:", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("[Agent Response]:", StringComparison.OrdinalIgnoreCase))
                {
                    inUserTurn = false;
                }
            }

            return string.Join(newline, lines);
        }

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

                int oldLineCount = CountLines(edit.OldContent);
                int newLineCount = CountLines(edit.NewContent);
                int netChange = newLineCount - oldLineCount;
                string changeSummary = netChange >= 0 ? $"+{netChange:N0}" : $"{netChange:N0}";
                builder.AppendLine($"Lines: {oldLineCount:N0} -> {newLineCount:N0} ({changeSummary})");
                builder.AppendLine();
            }

            return builder.ToString().TrimEnd();
        }

        private static int CountLines(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }

            int count = 1;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    count++;
                }
            }
            return count;
        }
    }
}
