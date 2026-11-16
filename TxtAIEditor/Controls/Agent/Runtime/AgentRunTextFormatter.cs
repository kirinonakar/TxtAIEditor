using System;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentRunTextFormatter
    {
        private const int MaxUserPromptDisplayChars = 200;

        public string BuildRunHeader(string instruction, bool fromOrchestrator = false)
        {
            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            string marker = fromOrchestrator ? AgentSessionLabels.OrchestratorPrompt : "[User Prompt]:";
            string display = fromOrchestrator ? instruction : TruncateUserPrompt(instruction);
            return $"{timestamp}  {marker} {display}";
        }

        public static string TruncateUserPrompt(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "(empty)";
            }

            value = value.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ');
            return value.Length > MaxUserPromptDisplayChars
                ? value.Substring(0, MaxUserPromptDisplayChars) + "..."
                : value;
        }

        public static string BuildLastAnswerText(string response, string cleanResponse, bool verbose)
        {
            string answer = verbose ? response : cleanResponse;
            return (answer ?? string.Empty).Trim();
        }
    }
}
