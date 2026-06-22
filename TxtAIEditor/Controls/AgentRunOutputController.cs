using System;
using System.Threading.Tasks;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentRunOutputController
    {
        private readonly AgentOpenSessionController _openSessionController;
        private readonly Func<string, Task<bool>> _insertIntoActiveEditorAsync;
        private readonly Func<string?, Task<bool>>? _beginStreamIntoActiveEditorAsync;
        private readonly Func<string?, string, Task<bool>>? _streamTextIntoActiveEditorAsync;
        private readonly Func<string?, Task>? _endStreamIntoActiveEditorAsync;

        public AgentRunOutputController(
            AgentOpenSessionController openSessionController,
            Func<string, Task<bool>> insertIntoActiveEditorAsync,
            Func<string?, Task<bool>>? beginStreamIntoActiveEditorAsync,
            Func<string?, string, Task<bool>>? streamTextIntoActiveEditorAsync,
            Func<string?, Task>? endStreamIntoActiveEditorAsync)
        {
            _openSessionController = openSessionController;
            _insertIntoActiveEditorAsync = insertIntoActiveEditorAsync;
            _beginStreamIntoActiveEditorAsync = beginStreamIntoActiveEditorAsync;
            _streamTextIntoActiveEditorAsync = streamTextIntoActiveEditorAsync;
            _endStreamIntoActiveEditorAsync = endStreamIntoActiveEditorAsync;
        }

        public Task BeginRunOutputBlockAsync(AgentRunContext context, string title)
        {
            return _openSessionController.BeginRunOutputBlockAsync(context, title);
        }

        public Task ClearRunActivityAsync(AgentRunContext context, string text)
        {
            return _openSessionController.ClearRunActivityAsync(context, text);
        }

        public Task AppendRunActivityAsync(AgentRunContext context, string message)
        {
            return _openSessionController.AppendRunActivityAsync(context, message);
        }

        public Task AppendRunOutputTextAsync(AgentRunContext context, string text)
        {
            return _openSessionController.AppendRunOutputTextAsync(context, text);
        }

        public Task AppendRunOutputLineAsync(AgentRunContext context, string line)
        {
            return _openSessionController.AppendRunOutputLineAsync(context, line);
        }

        public void EnqueueRunUi(
            AgentRunContext context,
            Action activeAction,
            Action<AgentOpenSessionState>? backgroundAction = null)
        {
            _openSessionController.EnqueueRunUi(context, activeAction, backgroundAction);
        }

        public Task BeginRunThinkingActivityAsync(AgentRunContext context, string label)
        {
            return _openSessionController.BeginRunThinkingActivityAsync(context, label);
        }

        public Task StopRunThinkingActivityAsync(AgentRunContext context)
        {
            return _openSessionController.StopRunThinkingActivityAsync(context);
        }

        public bool IsSessionVisible(string sessionId)
        {
            return _openSessionController.IsSessionVisible(sessionId);
        }

        public Task AppendOutputTextAndStreamToTabAsync(AgentRunContext context, string text)
        {
            return _openSessionController.AppendRunOutputTextAndExecuteAsync(
                context,
                text,
                () => context.StreamToTab ? StreamTextToTabAsync(context, text) : Task.CompletedTask);
        }

        public async Task StreamTextToTabAsync(AgentRunContext context, string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            if (!context.StreamToTabActive)
            {
                bool started = _beginStreamIntoActiveEditorAsync == null
                    ? true
                    : await _beginStreamIntoActiveEditorAsync(context.StreamToTabTargetTabId);
                if (!started)
                {
                    return;
                }

                context.StreamToTabActive = true;
            }

            if (_streamTextIntoActiveEditorAsync != null)
            {
                await _streamTextIntoActiveEditorAsync(context.StreamToTabTargetTabId, text);
                return;
            }

            await _insertIntoActiveEditorAsync(text);
        }

        public async Task FinishStreamToTabAsync(AgentRunContext context)
        {
            if (!context.StreamToTabActive)
            {
                return;
            }

            context.StreamToTabActive = false;
            if (_endStreamIntoActiveEditorAsync != null)
            {
                await _endStreamIntoActiveEditorAsync(context.StreamToTabTargetTabId);
            }
        }
    }
}
