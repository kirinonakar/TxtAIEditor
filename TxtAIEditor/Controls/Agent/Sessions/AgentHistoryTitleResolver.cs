using System;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentHistoryTitleResolver
    {
        public string Resolve(AgentHistoryItem item, string untitled)
        {
            string savedTitle = item.Title?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(savedTitle) &&
                !string.Equals(savedTitle, untitled, StringComparison.Ordinal) &&
                !string.Equals(savedTitle, "New Session", StringComparison.OrdinalIgnoreCase))
            {
                return savedTitle;
            }

            string extractedTitle = ExtractFromSessionHistory(item.SessionHistoryText);
            if (!string.IsNullOrWhiteSpace(extractedTitle))
            {
                return extractedTitle;
            }

            return string.IsNullOrWhiteSpace(savedTitle) ? untitled : savedTitle;
        }

        private static string ExtractFromSessionHistory(string historyText)
        {
            if (string.IsNullOrWhiteSpace(historyText))
            {
                return string.Empty;
            }

            string[] lines = historyText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            for (int i = 0; i < lines.Length; i++)
            {
                if (!lines[i].StartsWith("[User request]", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                for (int j = i + 1; j < lines.Length; j++)
                {
                    string requestLine = lines[j].Trim();
                    if (requestLine.StartsWith("[assistant:", StringComparison.OrdinalIgnoreCase) ||
                        requestLine.StartsWith("[tool:", StringComparison.OrdinalIgnoreCase) ||
                        requestLine.StartsWith("[user]", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }

                    if (!string.IsNullOrWhiteSpace(requestLine) && !requestLine.StartsWith("[", StringComparison.Ordinal))
                    {
                        return requestLine;
                    }
                }
            }

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (!line.StartsWith("[User Prompt]:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string inlinePrompt = line.Substring("[User Prompt]:".Length).Trim();
                if (!string.IsNullOrWhiteSpace(inlinePrompt) &&
                    !inlinePrompt.StartsWith("[Agent persona/instruction presets]", StringComparison.OrdinalIgnoreCase) &&
                    !inlinePrompt.StartsWith("[Enabled MCP servers]", StringComparison.OrdinalIgnoreCase) &&
                    !inlinePrompt.StartsWith("[Enabled agent skills]", StringComparison.OrdinalIgnoreCase))
                {
                    return inlinePrompt;
                }

                for (int j = i + 1; j < lines.Length; j++)
                {
                    if (!lines[j].StartsWith("[User request]", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    for (int k = j + 1; k < lines.Length; k++)
                    {
                        string requestLine = lines[k].Trim();
                        if (!string.IsNullOrWhiteSpace(requestLine))
                        {
                            return requestLine;
                        }
                    }
                }
            }

            return string.Empty;
        }
    }
}
