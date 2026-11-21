using System;
using System.Threading.Tasks;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentRunOutputController
    {
        private readonly AgentOpenSessionController _openSessionController;

        public AgentRunOutputController(AgentOpenSessionController openSessionController)
        {
            _openSessionController = openSessionController;
        }

        public Task BeginRunOutputBlockAsync(
            AgentRunContext context, string title, string prompt, AgentFollowUpBatch? followUp = null)
        {
            return _openSessionController.BeginRunOutputBlockAsync(context, title, prompt, followUp);
        }

        public Task ClearRunActivityAsync(AgentRunContext context, string text)
        {
            return _openSessionController.ClearRunActivityAsync(context, text);
        }

        public Task AppendRunActivityAsync(AgentRunContext context, string message)
        {
            return _openSessionController.AppendRunActivityAsync(context, message);
        }

        public Task AppendRunNoticeAsync(AgentRunContext context, string message)
        {
            return _openSessionController.AppendRunNoticeAsync(context, message);
        }

        public async Task AppendSubagentProgressAsync(AgentRunContext context, string message)
        {
            try
            {
                await AppendRunActivityAsync(context, AgentSubagentCoordinator.FormatProgressLine(message));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
            }
        }

        public Task AppendRunOutputTextAsync(AgentRunContext context, string text)
        {
            return _openSessionController.AppendRunOutputTextAsync(context, text);
        }

        public Task AppendRunDiagnosticTextAsync(AgentRunContext context, string text)
        {
            return _openSessionController.AppendRunOutputTextAsync(context, text, hideInChat: true);
        }

        public Task AppendRunOutputLineAsync(
            AgentRunContext context, string line, string? prompt = null, AgentFollowUpBatch? followUp = null)
        {
            return _openSessionController.AppendRunOutputLineAsync(context, line, prompt, followUp);
        }

        public void EnqueueRunUi(
            AgentRunContext context,
            Action activeAction,
            Action<AgentOpenSessionState>? backgroundAction = null)
        {
            _openSessionController.EnqueueRunUi(context, activeAction, backgroundAction);
        }

        public Task BeginRunThinkingActivityAsync(AgentRunContext context, string label, Func<string>? liveLabel = null)
        {
            return _openSessionController.BeginRunThinkingActivityAsync(context, label, liveLabel);
        }

        public Task StopRunThinkingActivityAsync(AgentRunContext context)
        {
            return _openSessionController.StopRunThinkingActivityAsync(context);
        }

        public bool IsSessionVisible(string sessionId)
        {
            return _openSessionController.IsSessionVisible(sessionId);
        }
    }
}
