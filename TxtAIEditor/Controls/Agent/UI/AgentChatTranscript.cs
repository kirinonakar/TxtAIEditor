using System;
using System.Collections.Generic;

namespace TxtAIEditor.Controls
{
    internal sealed record AgentChatPrompt(int Start, int End, string Text);
    internal sealed record AgentChatOutputRange(int Start, int End);

    internal sealed record AgentChatSnapshot(
        string SessionId, string Output, IReadOnlyList<AgentChatPrompt> Prompts,
        IReadOnlyList<AgentChatOutputRange> HiddenOutput, bool CanSubmit);

    internal static class AgentChatTranscript
    {
        private const string UserPromptHeader = "[User Prompt]:";

        public static string ReadPromptHeader(string header)
        {
            int markerStart = header.IndexOf(UserPromptHeader, StringComparison.OrdinalIgnoreCase);
            int markerLength = UserPromptHeader.Length;
            if (markerStart < 0)
            {
                markerStart = header.IndexOf(AgentSessionLabels.OrchestratorPrompt, StringComparison.OrdinalIgnoreCase);
                markerLength = AgentSessionLabels.OrchestratorPrompt.Length;
            }
            return markerStart < 0 ? header : header[(markerStart + markerLength)..].Trim();
        }

        public static int[] GetLineStarts(string output, int lineCount)
        {
            var starts = new int[lineCount + 1];
            int line = 0;
            for (int index = 0; index < output.Length; index++)
            {
                if (output[index] == '\n')
                {
                    starts[++line] = index + 1;
                }
            }

            starts[lineCount] = output.Length;
            return starts;
        }

        public static void ReadFormattedPrompts(string output, Dictionary<string, Queue<string>> fullPrompts,
            List<AgentChatPrompt> prompts)
        {
            string[] lines = output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            int[] starts = GetLineStarts(output, lines.Length);
            char fence = '\0';
            int fenceWidth = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string trimmed = line.Trim();
                int width = 0;
                if (trimmed.Length > 0 && trimmed[0] is '`' or '~')
                {
                    while (width < trimmed.Length && trimmed[width] == trimmed[0])
                    {
                        width++;
                    }
                }

                if (fenceWidth > 0)
                {
                    if (width >= fenceWidth && trimmed[0] == fence && width == trimmed.Length)
                    {
                        fenceWidth = 0;
                    }
                    continue;
                }
                if (width >= 3)
                {
                    fence = trimmed[0];
                    fenceWidth = width;
                    continue;
                }
                bool orchestrator = line.StartsWith(AgentSessionLabels.OrchestratorPrompt, StringComparison.OrdinalIgnoreCase);
                if (!orchestrator && !line.StartsWith(UserPromptHeader, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string prompt = fullPrompts.TryGetValue(line, out var values) && values.TryDequeue(out string? original)
                    ? original
                    : ReadPromptHeader(line);
                int end = i + 1;
                if (orchestrator)
                {
                    string[] promptLines = prompt.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
                    for (int j = 1; j < promptLines.Length && end < lines.Length && lines[end] == promptLines[j]; j++)
                    {
                        end++;
                    }
                }

                prompts.Add(new AgentChatPrompt(starts[i], starts[end], prompt));
                i = end - 1;
            }
        }
    }
}
