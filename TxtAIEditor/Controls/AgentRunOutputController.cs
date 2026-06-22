using System;
using System.Collections.Generic;
using System.Text;
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
        private readonly object _streamToTabBufferGate = new();
        private readonly Dictionary<AgentRunContext, StreamToTabBuffer> _streamToTabBuffers = new();
        private const int StreamToTabFlushDelayMs = 50;
        private const int MaxStreamToTabFlushChars = 8_000;

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
                () =>
                {
                    if (context.StreamToTab)
                    {
                        QueueStreamTextToTab(context, text);
                    }

                    return Task.CompletedTask;
                });
        }

        public Task StreamTextToTabAsync(AgentRunContext context, string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return Task.CompletedTask;
            }

            QueueStreamTextToTab(context, text);
            return Task.CompletedTask;
        }

        private async Task StreamTextToTabImmediateAsync(AgentRunContext context, string text)
        {
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
            await FlushQueuedStreamToTabAsync(context);

            if (!context.StreamToTabActive)
            {
                RemoveStreamToTabBuffer(context);
                return;
            }

            context.StreamToTabActive = false;
            if (_endStreamIntoActiveEditorAsync != null)
            {
                await _endStreamIntoActiveEditorAsync(context.StreamToTabTargetTabId);
            }

            RemoveStreamToTabBuffer(context);
        }

        private void QueueStreamTextToTab(AgentRunContext context, string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            lock (_streamToTabBufferGate)
            {
                StreamToTabBuffer buffer = GetOrCreateStreamToTabBuffer(context);
                buffer.PendingText.Append(text);
                if (buffer.FlushTask is { IsCompleted: false })
                {
                    return;
                }

                buffer.FlushTask = FlushStreamToTabLoopAsync(context);
            }
        }

        private async Task FlushStreamToTabLoopAsync(AgentRunContext context)
        {
            await Task.Delay(StreamToTabFlushDelayMs);
            while (true)
            {
                string text = DequeueStreamToTabText(context, MaxStreamToTabFlushChars);
                if (string.IsNullOrEmpty(text))
                {
                    return;
                }

                await _openSessionController.RunOnUIThreadAsync(
                    async () => await StreamTextToTabImmediateAsync(context, text));

                if (!HasPendingStreamToTabText(context))
                {
                    return;
                }

                await Task.Delay(StreamToTabFlushDelayMs);
            }
        }

        private async Task FlushQueuedStreamToTabAsync(AgentRunContext context)
        {
            while (true)
            {
                Task? flushTask;
                lock (_streamToTabBufferGate)
                {
                    flushTask = _streamToTabBuffers.TryGetValue(context, out StreamToTabBuffer? buffer)
                        ? buffer.FlushTask
                        : null;
                }

                if (flushTask != null && !flushTask.IsCompleted)
                {
                    await flushTask;
                    continue;
                }

                string text = DequeueStreamToTabText(context, MaxStreamToTabFlushChars);
                if (string.IsNullOrEmpty(text))
                {
                    return;
                }

                await _openSessionController.RunOnUIThreadAsync(
                    async () => await StreamTextToTabImmediateAsync(context, text));
            }
        }

        private StreamToTabBuffer GetOrCreateStreamToTabBuffer(AgentRunContext context)
        {
            if (_streamToTabBuffers.TryGetValue(context, out StreamToTabBuffer? buffer))
            {
                return buffer;
            }

            buffer = new StreamToTabBuffer();
            _streamToTabBuffers[context] = buffer;
            return buffer;
        }

        private string DequeueStreamToTabText(AgentRunContext context, int maxChars)
        {
            lock (_streamToTabBufferGate)
            {
                if (!_streamToTabBuffers.TryGetValue(context, out StreamToTabBuffer? buffer) ||
                    buffer.PendingText.Length == 0)
                {
                    return string.Empty;
                }

                int take = Math.Min(buffer.PendingText.Length, maxChars);
                string text = buffer.PendingText.ToString(0, take);
                buffer.PendingText.Remove(0, take);
                return text;
            }
        }

        private bool HasPendingStreamToTabText(AgentRunContext context)
        {
            lock (_streamToTabBufferGate)
            {
                return _streamToTabBuffers.TryGetValue(context, out StreamToTabBuffer? buffer) &&
                    buffer.PendingText.Length > 0;
            }
        }

        private void RemoveStreamToTabBuffer(AgentRunContext context)
        {
            lock (_streamToTabBufferGate)
            {
                _streamToTabBuffers.Remove(context);
            }
        }

        private sealed class StreamToTabBuffer
        {
            public StringBuilder PendingText { get; } = new();
            public Task? FlushTask { get; set; }
        }
    }
}
