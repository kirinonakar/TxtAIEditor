using System;
using System.Threading.Tasks;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentOutputInsertController
    {
        private readonly AgentPane _agentPane;
        private readonly Func<string?, string, OpenedTab> _openNewTabWithContent;
        private readonly Func<string> _getLastAnswerText;
        private readonly Action<string, string> _showError;
        private readonly Func<string, string, string> _getString;
        private readonly Func<string, bool> _isOutputPlaceholder;
        private readonly Func<Task> _yieldToUIAsync;

        public AgentOutputInsertController(
            AgentPane agentPane,
            Func<string?, string, OpenedTab> openNewTabWithContent,
            Func<string> getLastAnswerText,
            Action<string, string> showError,
            Func<string, string, string> getString,
            Func<string, bool> isOutputPlaceholder,
            Func<Task> yieldToUIAsync)
        {
            _agentPane = agentPane;
            _openNewTabWithContent = openNewTabWithContent;
            _getLastAnswerText = getLastAnswerText;
            _showError = showError;
            _getString = getString;
            _isOutputPlaceholder = isOutputPlaceholder;
            _yieldToUIAsync = yieldToUIAsync;
        }

        public async Task InsertOutputAsync()
        {
            string output = _getLastAnswerText();
            if (!ValidateOutput(
                    output,
                    "AgentNoLastAnswerToInsert",
                    "새 탭에 입력할 마지막 Agent 답변이 없습니다."))
            {
                return;
            }

            await _yieldToUIAsync();
            _openNewTabWithContent(null, output);
        }

        public async Task InsertNewTabOutputAsync()
        {
            string output = GetInsertableOutput();
            if (!ValidateOutput(
                    output,
                    "AgentNoOutputToInsert",
                    "입력할 Agent 응답이 없습니다."))
            {
                return;
            }

            await _yieldToUIAsync();
            _openNewTabWithContent(null, output);
        }

        private string GetInsertableOutput()
        {
            string fullOutput = _agentPane.Output.Text;
            string selectedOutput = _agentPane.Output.SelectedText;
            return !string.IsNullOrWhiteSpace(selectedOutput)
                ? selectedOutput
                : fullOutput;
        }

        private bool ValidateOutput(string output, string emptyMessageKey, string emptyMessageFallback)
        {
            if (!string.IsNullOrWhiteSpace(output) && !_isOutputPlaceholder(output))
            {
                return true;
            }

            _showError(
                _getString("AgentInsertTitle", "Agent 응답 입력"),
                _getString(emptyMessageKey, emptyMessageFallback));
            return false;
        }

        private static bool IsSelectionFromOutput(string selectedText, string fullOutput)
        {
            if (string.IsNullOrEmpty(selectedText) || string.IsNullOrEmpty(fullOutput))
            {
                return false;
            }

            if (fullOutput.Contains(selectedText, StringComparison.Ordinal))
            {
                return true;
            }

            string normalizedSelected = NormalizeLineEndings(selectedText);
            string normalizedOutput = NormalizeLineEndings(fullOutput);
            return normalizedSelected.Length > 0 &&
                normalizedOutput.Contains(normalizedSelected, StringComparison.Ordinal);
        }

        private static string NormalizeLineEndings(string value)
        {
            return (value ?? string.Empty)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n');
        }
    }
}
