using System;
using System.Collections.Generic;
using System.Text;
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

            var builder = new StringBuilder(text.Length);
            bool inToolCall = false;
            bool payloadStarted = false;
            bool inJsonString = false;
            bool escaped = false;

            for (int index = 0; index < text.Length;)
            {
                if (!inToolCall)
                {
                    if (StartsWithToolCallOpenTag(text, index, out int openTagLength))
                    {
                        builder.Append("<log_tool_call");
                        inToolCall = true;
                        payloadStarted = false;
                        index += openTagLength;
                        continue;
                    }

                    builder.Append(text[index]);
                    index++;
                    continue;
                }

                if (!payloadStarted)
                {
                    char current = text[index];
                    builder.Append(current);
                    index++;
                    if (current == '>')
                    {
                        payloadStarted = true;
                    }
                    continue;
                }

                if (inJsonString)
                {
                    char current = text[index];
                    builder.Append(current);
                    index++;
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (current == '\\')
                    {
                        escaped = true;
                    }
                    else if (current == '"')
                    {
                        inJsonString = false;
                    }

                    continue;
                }

                if (text[index] == '"')
                {
                    inJsonString = true;
                    builder.Append(text[index]);
                    index++;
                    continue;
                }

                if (TryGetToolCallCloseTagLength(text, index, out int closeTagLength))
                {
                    builder.Append("</log_tool_call>");
                    index += closeTagLength;
                    inToolCall = false;
                    payloadStarted = false;
                    continue;
                }

                builder.Append(text[index]);
                index++;
            }

            return builder.ToString();
        }

        private static bool StartsWithToolCallOpenTag(string text, int index, out int tagLength)
        {
            const string openTag = "<tool_call";
            tagLength = 0;
            if (index < 0 || index + openTag.Length > text.Length ||
                !text.AsSpan(index, openTag.Length).Equals(openTag.AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            int nextIndex = index + openTag.Length;
            if (nextIndex < text.Length &&
                !char.IsWhiteSpace(text[nextIndex]) &&
                text[nextIndex] != '>')
            {
                return false;
            }

            tagLength = openTag.Length;
            return true;
        }

        private static bool TryGetToolCallCloseTagLength(string text, int index, out int tagLength)
        {
            const string closeTag = "</tool_call>";
            const string legacyCloseTag = "</log_tool_call>";
            tagLength = 0;
            if (index < 0)
            {
                return false;
            }

            if (index + closeTag.Length <= text.Length &&
                text.AsSpan(index, closeTag.Length).Equals(closeTag.AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                tagLength = closeTag.Length;
                return true;
            }

            if (index + legacyCloseTag.Length <= text.Length &&
                text.AsSpan(index, legacyCloseTag.Length).Equals(legacyCloseTag.AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                tagLength = legacyCloseTag.Length;
                return true;
            }

            return false;
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
