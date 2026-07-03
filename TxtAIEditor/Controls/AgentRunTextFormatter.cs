using System;
using static TxtAIEditor.Controls.AgentToolHelpers;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentRunTextFormatter
    {
        private readonly Func<string, string, string> _getString;

        public AgentRunTextFormatter(Func<string, string, string> getString)
        {
            _getString = getString;
        }

        public string BuildRunHeader(string instruction)
        {
            string modeText = _getString("AgentModeRun", "실행");
            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            return $"{timestamp}  Agent {modeText}: {TruncateForActivity(instruction)}";
        }

        public static string BuildLastAnswerText(string response, string cleanResponse, bool verbose)
        {
            string answer = verbose ? response : cleanResponse;
            if (string.IsNullOrWhiteSpace(answer))
            {
                answer = response;
            }

            return (answer ?? string.Empty).Trim();
        }
    }
}
