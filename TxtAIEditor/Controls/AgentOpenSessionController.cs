using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentOpenSessionController
    {
        private readonly ISettingsService _settingsService;
        private readonly AgentPane _agentPane;
        private readonly AgentFileToolService _fileTools;
        private readonly AgentAttachmentController _attachmentController;
        private readonly AgentSessionEditController _sessionEditController;
        private readonly AgentHistoryController _historyController;
        private readonly AgentDisplayLocalizer _displayText;
        private readonly Dictionary<string, AgentRunContext> _runningSessions;
        private readonly Func<string> _currentSessionIdProvider;
        private readonly Action<string> _setCurrentSessionId;
        private readonly Func<string> _sessionHistoryTextProvider;
        private readonly Func<double> _sessionHistoryTokenCountProvider;
        private readonly Func<double> _currentRunTranscriptTokensProvider;
        private readonly Action<string, double, double> _restoreSessionHistoryState;
        private readonly Action<string> _stopAgent;
        private readonly Action _updateContextStatsImmediate;
        private readonly Action<int>? _completedNotificationCountChanged;
        private readonly Func<string, string, string> _getString;
        private readonly Func<string, Task>? _navigateToFolderAsync;
        private readonly List<AgentOpenSessionState> _openSessions = new();
        private readonly object _runOutputBufferGate = new();
        private readonly Dictionary<string, PendingRunOutputBuffer> _pendingRunOutputBuffers = new(StringComparer.Ordinal);
        private string? _pendingCloseSessionId;
        private bool _restoringOpenSession;
        private const int RunOutputFlushDelayMs = 75;
        private const int MaxRunOutputFlushChars = 8_000;

        public AgentOpenSessionController(
            ISettingsService settingsService,
            AgentPane agentPane,
            AgentFileToolService fileTools,
            AgentAttachmentController attachmentController,
            AgentSessionEditController sessionEditController,
            AgentHistoryController historyController,
            AgentDisplayLocalizer displayText,
            Dictionary<string, AgentRunContext> runningSessions,
            Func<string> currentSessionIdProvider,
            Action<string> setCurrentSessionId,
            Func<string> sessionHistoryTextProvider,
            Func<double> sessionHistoryTokenCountProvider,
            Func<double> currentRunTranscriptTokensProvider,
            Action<string, double, double> restoreSessionHistoryState,
            Action<string> stopAgent,
            Action updateContextStatsImmediate,
            Action<int>? completedNotificationCountChanged,
            Func<string, string, string> getString,
            Func<string, Task>? navigateToFolderAsync)
        {
            _settingsService = settingsService;
            _agentPane = agentPane;
            _fileTools = fileTools;
            _attachmentController = attachmentController;
            _sessionEditController = sessionEditController;
            _historyController = historyController;
            _displayText = displayText;
            _runningSessions = runningSessions;
            _currentSessionIdProvider = currentSessionIdProvider;
            _setCurrentSessionId = setCurrentSessionId;
            _sessionHistoryTextProvider = sessionHistoryTextProvider;
            _sessionHistoryTokenCountProvider = sessionHistoryTokenCountProvider;
            _currentRunTranscriptTokensProvider = currentRunTranscriptTokensProvider;
            _restoreSessionHistoryState = restoreSessionHistoryState;
            _stopAgent = stopAgent;
            _updateContextStatsImmediate = updateContextStatsImmediate;
            _completedNotificationCountChanged = completedNotificationCountChanged;
            _getString = getString;
            _navigateToFolderAsync = navigateToFolderAsync;
        }

        public bool IsCurrentSessionRunning()
        {
            return _runningSessions.ContainsKey(_currentSessionIdProvider());
        }

        public bool IsAnySessionRunning()
        {
            return _runningSessions.Count > 0;
        }

        public bool IsSessionVisible(string sessionId)
        {
            return string.Equals(_currentSessionIdProvider(), sessionId, StringComparison.Ordinal);
        }

        public bool IsPendingClose(string sessionId)
        {
            return string.Equals(_pendingCloseSessionId, sessionId, StringComparison.Ordinal);
        }

        public string ConsumePendingCloseSessionId()
        {
            string closingSessionId = _pendingCloseSessionId ?? string.Empty;
            _pendingCloseSessionId = null;
            return closingSessionId;
        }

        public AgentOpenSessionState EnsureSession(string sessionId)
        {
            var session = _openSessions.FirstOrDefault(item =>
                string.Equals(item.Id, sessionId, StringComparison.Ordinal));
            if (session != null)
            {
                return session;
            }

            session = CreateBlankSession(sessionId);
            _openSessions.Insert(0, session);
            return session;
        }

        public AgentOpenSessionState CreateBlankSession(string? sessionId = null)
        {
            return new AgentOpenSessionState
            {
                Id = string.IsNullOrWhiteSpace(sessionId) ? Guid.NewGuid().ToString() : sessionId,
                Title = GetUntitledOpenSessionTitle(),
                OutputText = _displayText.OutputPlaceholder,
                ActivityText = _displayText.ActivityIdle,
                WorkspaceRoot = CaptureCurrentWorkspaceRoot(),
                LlmSettings = CreateSessionSettingsSnapshot()
            };
        }

        public EditorSettings GetCurrentSessionSettings()
        {
            var session = EnsureSession(_currentSessionIdProvider());
            session.LlmSettings ??= CreateSessionSettingsSnapshot();
            return session.LlmSettings;
        }

        public string GetCurrentLastAnswerText()
        {
            var session = EnsureSession(_currentSessionIdProvider());
            return session.LastAnswerText ?? string.Empty;
        }

        public EditorSettings CreateSessionSettingsSnapshot()
        {
            return CloneSessionSettings(_settingsService.CurrentSettings);
        }

        public Task<EditorSettings> ResolveRunSessionSettingsAsync(AgentOpenSessionState session)
        {
            session.LlmSettings ??= CreateSessionSettingsSnapshot();
            return Task.FromResult(CloneSessionSettings(session.LlmSettings));
        }

        public void SavePromptTitleFromUI()
        {
            if (_restoringOpenSession)
            {
                return;
            }

            var session = EnsureSession(_currentSessionIdProvider());
            session.PromptText = _agentPane.Prompt.Text ?? string.Empty;
            UpdateSessionTitle(session, session.PromptText);
            session.UpdatedAt = DateTime.Now;
            UpdateUI();
        }

        public void SaveActiveFromUI()
        {
            if (_restoringOpenSession)
            {
                return;
            }

            var session = EnsureSession(_currentSessionIdProvider());
            session.PromptText = _agentPane.Prompt.Text ?? string.Empty;
            session.OutputText = _agentPane.GetRawOutputText();
            session.ActivityText = _agentPane.Activity.Text ?? string.Empty;
            session.WorkspaceRoot = CaptureCurrentWorkspaceRoot();
            SyncVisibleThinkingStateToSession(session);
            if (_runningSessions.TryGetValue(session.Id, out AgentRunContext? runContext))
            {
                session.SessionHistoryText = runContext.SessionHistory.ToString();
                session.SessionHistoryTokenCount = runContext.SessionHistoryTokenCount;
                session.CurrentRunTranscriptTokens = runContext.CurrentRunTranscriptTokens;
                session.Attachments = runContext.Attachments.ToList();
                session.SessionEdits = runContext.SessionEdits.ToList();
            }
            else
            {
                session.SessionHistoryText = _sessionHistoryTextProvider();
                session.SessionHistoryTokenCount = _sessionHistoryTokenCountProvider();
                session.CurrentRunTranscriptTokens = _currentRunTranscriptTokensProvider();
                session.Attachments = _attachmentController.GetState();
                session.SessionEdits = _sessionEditController.SessionEdits.ToList();
            }
            session.UpdatedAt = DateTime.Now;
            UpdateSessionTitle(session, session.PromptText);
        }

        public void RestoreSession(AgentOpenSessionState session)
        {
            _restoringOpenSession = true;
            try
            {
                _setCurrentSessionId(session.Id);
                session.CompletedNotificationCount = 0;
                bool isRunningSession = _runningSessions.TryGetValue(session.Id, out AgentRunContext? runContext);
                string sessionHistory = isRunningSession
                    ? runContext?.SessionHistory.ToString() ?? string.Empty
                    : session.SessionHistoryText ?? string.Empty;
                double sessionHistoryTokenCount = isRunningSession
                    ? runContext?.SessionHistoryTokenCount ?? 0
                    : session.SessionHistoryTokenCount;
                double currentRunTranscriptTokens = isRunningSession
                    ? runContext?.CurrentRunTranscriptTokens ?? 0
                    : session.CurrentRunTranscriptTokens;
                _restoreSessionHistoryState(
                    sessionHistory,
                    sessionHistoryTokenCount,
                    currentRunTranscriptTokens);

                if (isRunningSession && runContext != null)
                {
                    session.SessionHistoryText = runContext.SessionHistory.ToString();
                    session.SessionHistoryTokenCount = runContext.SessionHistoryTokenCount;
                    session.CurrentRunTranscriptTokens = runContext.CurrentRunTranscriptTokens;
                    session.Attachments = runContext.Attachments.ToList();
                    session.SessionEdits = runContext.SessionEdits.ToList();
                }

                _agentPane.Prompt.Text = session.PromptText ?? string.Empty;
                _agentPane.ResetOutput(string.IsNullOrEmpty(session.OutputText)
                    ? _displayText.OutputPlaceholder
                    : session.OutputText);
                if (session.ThinkingLineActive && session.IsRunning)
                {
                    _agentPane.ResumeThinkingActivityFromOutput();
                }

                _agentPane.ClearActivity(string.IsNullOrWhiteSpace(session.ActivityText)
                    ? _displayText.ActivityIdle
                    : session.ActivityText);
                if (isRunningSession && runContext != null)
                {
                    _attachmentController.Replace(runContext.Attachments);
                    _sessionEditController.Replace(runContext.SessionEdits, runContext.SessionId);
                }
                else
                {
                    _attachmentController.Replace(session.Attachments);
                    _sessionEditController.Replace(session.SessionEdits, session.Id);
                }
                _historyController.UpdateUI(_currentSessionIdProvider());
                RestoreWorkspaceRoot(session);
                UpdateUI();
                UpdateActiveSessionBusyState();
                _updateContextStatsImmediate();
            }
            finally
            {
                _restoringOpenSession = false;
            }
        }

        public void CreateNewSession()
        {
            SaveActiveFromUI();
            var currentSession = EnsureSession(_currentSessionIdProvider());
            if (IsReusableBlankSession(currentSession))
            {
                currentSession.UpdatedAt = DateTime.Now;
                RestoreSession(currentSession);
                return;
            }

            var session = CreateBlankSession();
            _openSessions.Insert(0, session);
            RestoreSession(session);
        }

        public void SwitchSession(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId) ||
                string.Equals(_currentSessionIdProvider(), sessionId, StringComparison.Ordinal))
            {
                return;
            }

            var session = _openSessions.FirstOrDefault(item =>
                string.Equals(item.Id, sessionId, StringComparison.Ordinal));
            if (session == null)
            {
                return;
            }

            SaveActiveFromUI();
            RestoreSession(session);
        }

        public void CloseSession(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return;
            }

            bool isRunningSession = _runningSessions.TryGetValue(sessionId, out _);
            if (isRunningSession)
            {
                _pendingCloseSessionId = sessionId;
                _stopAgent(sessionId);
                UpdateUI();
                if (string.Equals(_currentSessionIdProvider(), sessionId, StringComparison.Ordinal))
                {
                    return;
                }
            }

            if (!isRunningSession && string.Equals(_currentSessionIdProvider(), sessionId, StringComparison.Ordinal))
            {
                var current = _openSessions.FirstOrDefault(item =>
                    string.Equals(item.Id, sessionId, StringComparison.Ordinal));
                if (current != null)
                {
                    _openSessions.Remove(current);
                }

                var next = _openSessions.FirstOrDefault();
                if (next == null)
                {
                    next = CreateBlankSession();
                    _openSessions.Add(next);
                }

                RestoreSession(next);
                return;
            }

            var session = _openSessions.FirstOrDefault(item =>
                string.Equals(item.Id, sessionId, StringComparison.Ordinal));
            if (session != null)
            {
                _openSessions.Remove(session);
                UpdateUI();
            }
        }

        public void UpdateUI()
        {
            string currentSessionId = _currentSessionIdProvider();
            var items = _openSessions
                .Select(session =>
                {
                    bool isSelected = string.Equals(session.Id, currentSessionId, StringComparison.Ordinal);
                    bool isRunning = session.IsRunning || _runningSessions.ContainsKey(session.Id);
                    
                    string wRoot = !string.IsNullOrWhiteSpace(session.WorkspaceRoot)
                        ? session.WorkspaceRoot
                        : CaptureCurrentWorkspaceRoot();

                    string prefix = string.Empty;
                    if (!string.IsNullOrWhiteSpace(wRoot))
                    {
                        try
                        {
                            string trimmed = wRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                            string lastDir = Path.GetFileName(trimmed);
                            if (!string.IsNullOrEmpty(lastDir))
                            {
                                prefix = $"[{lastDir}] ";
                            }
                        }
                        catch { }
                    }

                    string rawTitle = string.IsNullOrWhiteSpace(session.Title)
                        ? GetUntitledOpenSessionTitle()
                        : session.Title;

                    return new AgentOpenSessionItemViewModel
                    {
                        Id = session.Id,
                        Title = $"{prefix}{rawTitle}",
                        IsSelected = isSelected,
                        IsRunning = isRunning,
                        CompletedNotificationCount = session.CompletedNotificationCount,
                        CanSelect = true,
                        CanClose = true
                    };
                })
                .ToList();

            int completedNotificationCount = items.Sum(item => Math.Max(0, item.CompletedNotificationCount));
            _agentPane.DispatcherQueue.TryEnqueue(() =>
            {
                _completedNotificationCountChanged?.Invoke(completedNotificationCount);
                _agentPane.UpdateOpenSessionItems(items, currentSessionId);
            });
        }

        public void MarkBackgroundSessionCompleted(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId) || IsSessionVisible(sessionId))
            {
                return;
            }

            var session = EnsureSession(sessionId);
            session.CompletedNotificationCount++;
            session.UpdatedAt = DateTime.Now;
            UpdateUI();
        }

        public void UpdateSessionTitle(AgentOpenSessionState session, string prompt)
        {
            string firstLine = BuildOpenSessionTitle(prompt);
            if (!string.IsNullOrWhiteSpace(firstLine))
            {
                session.Title = firstLine;
            }
            else if (string.IsNullOrWhiteSpace(session.Title))
            {
                session.Title = GetUntitledOpenSessionTitle();
            }
        }

        public void UpdateActiveSessionBusyState()
        {
            bool isCurrentSessionRunning = IsCurrentSessionRunning();
            var currentSession = EnsureSession(_currentSessionIdProvider());
            _agentPane.SetBusy(isCurrentSessionRunning);
            _agentPane.SetCanRewindSession(!isCurrentSessionRunning && currentSession.RewindSnapshots.Count > 0);
        }

        public async Task BeginRunOutputBlockAsync(AgentRunContext context, string title)
        {
            await FlushQueuedRunOutputTextAsync(context);
            await RunOnUIThreadAsync(() =>
            {
                var session = EnsureSession(context.SessionId);
                if (IsSessionVisible(context.SessionId))
                {
                    _agentPane.BeginOutputBlock(title);
                    session.OutputText = _agentPane.GetRawOutputText();
                    SyncVisibleThinkingStateToSession(session);
                }
                else
                {
                    CompleteThinkingInSession(session);
                    AppendOutputLineToSession(session, title);
                }
            });
        }

        public async Task ClearRunActivityAsync(AgentRunContext context, string text)
        {
            await RunOnUIThreadAsync(() =>
            {
                var session = EnsureSession(context.SessionId);
                session.ActivityText = text;
                if (IsSessionVisible(context.SessionId))
                {
                    _agentPane.ClearActivity(text);
                }
            });
        }

        public async Task AppendRunActivityAsync(AgentRunContext context, string message)
        {
            await FlushQueuedRunOutputTextAsync(context);
            await RunOnUIThreadAsync(() =>
            {
                var session = EnsureSession(context.SessionId);
                string timestamp = DateTime.Now.ToString("HH:mm:ss");
                string line = $"{timestamp}  {message}";
                CompleteThinkingInSession(session);
                AppendOutputLineToSession(session, line);
                session.ActivityText = string.IsNullOrWhiteSpace(session.ActivityText) ||
                    _displayText.IsActivityIdle(session.ActivityText)
                    ? line
                    : session.ActivityText + Environment.NewLine + line;

                if (IsSessionVisible(context.SessionId))
                {
                    _agentPane.AppendActivity(message);
                    session.OutputText = _agentPane.GetRawOutputText();
                    session.ActivityText = _agentPane.Activity.Text ?? string.Empty;
                    SyncVisibleThinkingStateToSession(session);
                }
            });
        }

        public Task AppendRunOutputTextAsync(AgentRunContext context, string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return Task.CompletedTask;
            }

            QueueRunOutputText(context, text);
            return Task.CompletedTask;
        }

        public async Task AppendRunOutputLineAsync(AgentRunContext context, string line)
        {
            await FlushQueuedRunOutputTextAsync(context);
            await RunOnUIThreadAsync(() =>
            {
                var session = EnsureSession(context.SessionId);
                if (IsSessionVisible(context.SessionId))
                {
                    _agentPane.AppendOutputLine(line);
                    session.OutputText = _agentPane.GetRawOutputText();
                    SyncVisibleThinkingStateToSession(session);
                }
                else
                {
                    CompleteThinkingInSession(session);
                    AppendOutputLineToSession(session, line);
                }
            });
        }

        public void EnqueueRunUi(
            AgentRunContext context,
            Action activeAction,
            Action<AgentOpenSessionState>? backgroundAction = null)
        {
            _agentPane.DispatcherQueue.TryEnqueue(
                DispatcherQueuePriority.Low,
                () =>
                {
                    var session = EnsureSession(context.SessionId);
                    if (IsSessionVisible(context.SessionId))
                    {
                        activeAction();
                    }
                    else
                    {
                        backgroundAction?.Invoke(session);
                    }
                });
        }

        public Task BeginRunThinkingActivityAsync(AgentRunContext context, string label)
        {
            return BeginRunThinkingActivityCoreAsync(context, label);
        }

        private async Task BeginRunThinkingActivityCoreAsync(AgentRunContext context, string label)
        {
            await FlushQueuedRunOutputTextAsync(context);
            await RunOnUIThreadAsync(() =>
            {
                var session = EnsureSession(context.SessionId);
                if (IsSessionVisible(context.SessionId))
                {
                    _agentPane.BeginThinkingActivity(label);
                    session.OutputText = _agentPane.GetRawOutputText();
                    SyncVisibleThinkingStateToSession(session);
                }
                else
                {
                    BeginThinkingInSession(session, label);
                }
            });
        }

        public Task StopRunThinkingActivityAsync(AgentRunContext context)
        {
            return StopRunThinkingActivityCoreAsync(context);
        }

        private async Task StopRunThinkingActivityCoreAsync(AgentRunContext context)
        {
            await FlushQueuedRunOutputTextAsync(context);
            await RunOnUIThreadAsync(() =>
            {
                var session = EnsureSession(context.SessionId);
                if (IsSessionVisible(context.SessionId))
                {
                    _agentPane.StopThinkingActivity();
                    session.OutputText = _agentPane.GetRawOutputText();
                    SyncVisibleThinkingStateToSession(session);
                }
                else
                {
                    CompleteThinkingInSession(session);
                }
            });
        }

        public async Task AppendRunOutputTextAndExecuteAsync(
            AgentRunContext context,
            string text,
            Func<Task> afterAppendAsync)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            QueueRunOutputText(context, text);
            await afterAppendAsync();
        }

        public void AppendActivityToCurrentSession(string message)
        {
            _agentPane.DispatcherQueue.TryEnqueue(() =>
            {
                var session = EnsureSession(_currentSessionIdProvider());
                _agentPane.AppendActivity(message);
                session.OutputText = _agentPane.GetRawOutputText();
                session.ActivityText = _agentPane.Activity.Text ?? string.Empty;
                SyncVisibleThinkingStateToSession(session);
            });
        }

        public void BeginThinkingInSession(AgentOpenSessionState session, string label)
        {
            string text = NormalizePlaceholderOutput(session.OutputText);
            if (!string.IsNullOrEmpty(text) && !EndsWithLineBreak(text))
            {
                text += Environment.NewLine;
            }

            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            string prefix = $"{timestamp}  {label}";
            session.OutputText = text + prefix;
            session.ThinkingLineActive = true;
            session.ThinkingLineStart = text.Length;
            session.ThinkingLineTimestamp = timestamp;
            session.ThinkingLinePrefix = prefix;
            session.UpdatedAt = DateTime.Now;
        }

        public void UpdateThinkingInSession(AgentOpenSessionState session, string label)
        {
            if (!session.ThinkingLineActive)
            {
                BeginThinkingInSession(session, label);
                return;
            }

            string text = session.OutputText ?? string.Empty;
            if (session.ThinkingLineStart < 0 || session.ThinkingLineStart > text.Length)
            {
                BeginThinkingInSession(session, label);
                return;
            }

            string timestamp = string.IsNullOrWhiteSpace(session.ThinkingLineTimestamp)
                ? DateTime.Now.ToString("HH:mm:ss")
                : session.ThinkingLineTimestamp;
            string prefix = $"{timestamp}  {label}";
            session.OutputText = text.Substring(0, session.ThinkingLineStart) + prefix;
            session.ThinkingLinePrefix = prefix;
            session.ThinkingLineTimestamp = timestamp;
            session.UpdatedAt = DateTime.Now;
        }

        public void CompleteThinkingInSession(AgentOpenSessionState session)
        {
            if (!session.ThinkingLineActive)
            {
                return;
            }

            if (!EndsWithLineBreak(session.OutputText ?? string.Empty))
            {
                session.OutputText = (session.OutputText ?? string.Empty) + Environment.NewLine;
            }

            ClearThinkingState(session);
            session.UpdatedAt = DateTime.Now;
        }

        public void ClearThinkingState(AgentOpenSessionState session)
        {
            session.ThinkingLineActive = false;
            session.ThinkingLineStart = 0;
            session.ThinkingLineTimestamp = string.Empty;
            session.ThinkingLinePrefix = string.Empty;
        }

        public void SyncVisibleThinkingStateToSession(AgentOpenSessionState session)
        {
            if (_agentPane.IsThinkingActivityActive)
            {
                CaptureThinkingStateFromOutput(session);
            }
            else
            {
                ClearThinkingState(session);
            }
        }

        private void AppendRunOutputTextOnCurrentThread(AgentRunContext context, string text)
        {
            var session = EnsureSession(context.SessionId);
            if (IsSessionVisible(context.SessionId))
            {
                _agentPane.AppendOutputText(text);
            }
            else
            {
                AppendOutputTextToSession(session, text);
            }
        }

        private void QueueRunOutputText(AgentRunContext context, string text)
        {
            lock (_runOutputBufferGate)
            {
                PendingRunOutputBuffer buffer = GetOrCreateRunOutputBuffer(context.SessionId);
                buffer.Context = context;
                buffer.PendingText.Append(text);

                if (buffer.FlushTask is { IsCompleted: false })
                {
                    return;
                }

                buffer.FlushTask = FlushRunOutputBufferLoopAsync(context.SessionId);
            }
        }

        private async Task FlushRunOutputBufferLoopAsync(string sessionId)
        {
            await Task.Delay(RunOutputFlushDelayMs);

            while (true)
            {
                if (!TryDequeueRunOutputText(sessionId, out AgentRunContext? context, out string text))
                {
                    return;
                }

                if (context != null)
                {
                    await RunOnUIThreadAsync(
                        () => AppendRunOutputTextOnCurrentThread(context, text),
                        DispatcherQueuePriority.Low);
                }

                if (TryMarkRunOutputFlushIdleIfEmpty(sessionId))
                {
                    return;
                }

                await Task.Delay(RunOutputFlushDelayMs);
            }
        }

        private async Task FlushQueuedRunOutputTextAsync(AgentRunContext context)
        {
            while (true)
            {
                Task? flushTask;
                lock (_runOutputBufferGate)
                {
                    flushTask = _pendingRunOutputBuffers.TryGetValue(context.SessionId, out PendingRunOutputBuffer? buffer)
                        ? buffer.FlushTask
                        : null;
                }

                if (flushTask != null && !flushTask.IsCompleted)
                {
                    await flushTask;
                    continue;
                }

                if (!TryDequeueRunOutputText(context.SessionId, out AgentRunContext? bufferedContext, out string text))
                {
                    return;
                }

                await RunOnUIThreadAsync(
                    () => AppendRunOutputTextOnCurrentThread(bufferedContext ?? context, text),
                    DispatcherQueuePriority.Low);
            }
        }

        private PendingRunOutputBuffer GetOrCreateRunOutputBuffer(string sessionId)
        {
            if (_pendingRunOutputBuffers.TryGetValue(sessionId, out PendingRunOutputBuffer? buffer))
            {
                return buffer;
            }

            buffer = new PendingRunOutputBuffer();
            _pendingRunOutputBuffers[sessionId] = buffer;
            return buffer;
        }

        private bool TryDequeueRunOutputText(
            string sessionId,
            out AgentRunContext? context,
            out string text)
        {
            lock (_runOutputBufferGate)
            {
                if (!_pendingRunOutputBuffers.TryGetValue(sessionId, out PendingRunOutputBuffer? buffer) ||
                    buffer.PendingText.Length == 0)
                {
                    if (buffer != null)
                    {
                        buffer.FlushTask = null;
                    }

                    context = buffer?.Context;
                    text = string.Empty;
                    return false;
                }

                int take = Math.Min(buffer.PendingText.Length, MaxRunOutputFlushChars);
                text = buffer.PendingText.ToString(0, take);
                buffer.PendingText.Remove(0, take);
                context = buffer.Context;
                return true;
            }
        }

        private bool TryMarkRunOutputFlushIdleIfEmpty(string sessionId)
        {
            lock (_runOutputBufferGate)
            {
                if (!_pendingRunOutputBuffers.TryGetValue(sessionId, out PendingRunOutputBuffer? buffer) ||
                    buffer.PendingText.Length > 0)
                {
                    return false;
                }

                buffer.FlushTask = null;
                return true;
            }
        }

        private string CaptureCurrentWorkspaceRoot()
        {
            return _fileTools.WorkspaceRoot;
        }

        private void RestoreWorkspaceRoot(AgentOpenSessionState session)
        {
            if (_navigateToFolderAsync == null ||
                string.IsNullOrWhiteSpace(session.WorkspaceRoot) ||
                !Directory.Exists(session.WorkspaceRoot))
            {
                return;
            }

            _ = _navigateToFolderAsync(session.WorkspaceRoot);
        }

        private void CaptureThinkingStateFromOutput(AgentOpenSessionState session)
        {
            string text = session.OutputText ?? string.Empty;
            int lineStart = FindLastLineStart(text);
            string line = lineStart < text.Length
                ? text.Substring(lineStart).TrimEnd('\r', '\n')
                : string.Empty;
            session.ThinkingLineActive = !string.IsNullOrWhiteSpace(line);
            session.ThinkingLineStart = lineStart;
            session.ThinkingLineTimestamp = ExtractThinkingTimestamp(line);
            session.ThinkingLinePrefix = StripTrailingThinkingDots(line);
        }

        private string GetUntitledOpenSessionTitle()
        {
            return _getString("AgentOpenSessionUntitled", "새 세션");
        }

        private string NormalizePlaceholderOutput(string text)
        {
            if (_displayText.IsOutputPlaceholder((text ?? string.Empty).TrimStart()))
            {
                return string.Empty;
            }

            return text ?? string.Empty;
        }

        private bool IsReusableBlankSession(AgentOpenSessionState session)
        {
            if (session.IsRunning || _runningSessions.ContainsKey(session.Id))
            {
                return false;
            }

            bool hasOutput = !string.IsNullOrWhiteSpace(NormalizePlaceholderOutput(session.OutputText));
            bool hasActivity = !string.IsNullOrWhiteSpace(session.ActivityText) &&
                !_displayText.IsActivityIdle(session.ActivityText);

            return string.IsNullOrWhiteSpace(session.PromptText) &&
                string.IsNullOrWhiteSpace(session.SessionHistoryText) &&
                !hasOutput &&
                !hasActivity &&
                session.Attachments.Count == 0 &&
                session.SessionEdits.Count == 0;
        }

        private void AppendOutputLineToSession(AgentOpenSessionState session, string line)
        {
            string text = NormalizePlaceholderOutput(session.OutputText);
            if (!string.IsNullOrEmpty(text) && !text.EndsWith(Environment.NewLine, StringComparison.Ordinal))
            {
                text += Environment.NewLine;
            }

            session.OutputText = text + line + Environment.NewLine;
            session.UpdatedAt = DateTime.Now;
        }

        public void AppendOutputTextToSession(AgentOpenSessionState session, string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            CompleteThinkingInSession(session);
            session.OutputText = NormalizePlaceholderOutput(session.OutputText) + text;
            session.UpdatedAt = DateTime.Now;
        }

        public Task RunOnUIThreadAsync(
            Action action,
            DispatcherQueuePriority priority = DispatcherQueuePriority.Normal)
        {
            if (_agentPane.DispatcherQueue.HasThreadAccess)
            {
                try
                {
                    action();
                    return Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    return Task.FromException(ex);
                }
            }

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            bool queued = _agentPane.DispatcherQueue.TryEnqueue(priority, () =>
            {
                try
                {
                    action();
                    tcs.SetResult();
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });

            if (!queued)
            {
                tcs.SetException(new InvalidOperationException("Failed to enqueue Agent UI action."));
            }

            return tcs.Task;
        }

        public Task RunOnUIThreadAsync(
            Func<Task> func,
            DispatcherQueuePriority priority = DispatcherQueuePriority.Normal)
        {
            if (_agentPane.DispatcherQueue.HasThreadAccess)
            {
                return func();
            }

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            bool queued = _agentPane.DispatcherQueue.TryEnqueue(priority, async () =>
            {
                try
                {
                    await func();
                    tcs.SetResult();
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });

            if (!queued)
            {
                tcs.SetException(new InvalidOperationException("Failed to enqueue Agent UI action."));
            }

            return tcs.Task;
        }

        private sealed class PendingRunOutputBuffer
        {
            public StringBuilder PendingText { get; } = new();
            public AgentRunContext? Context { get; set; }
            public Task? FlushTask { get; set; }
        }

        private static EditorSettings CloneSessionSettings(EditorSettings settings)
        {
            return new EditorSettings
            {
                Language = settings.Language,
                LlmProvider = settings.LlmProvider,
                LlmEndpoint = settings.LlmEndpoint,
                LlmModel = settings.LlmModel,
                LlmModelGemini = settings.LlmModelGemini,
                LlmModelOpenAI = settings.LlmModelOpenAI,
                LlmModelOpenRouter = settings.LlmModelOpenRouter,
                LlmModelLmStudio = settings.LlmModelLmStudio,
                LlmModelOpenCodeGo = settings.LlmModelOpenCodeGo,
                LlmModelOpenCodeZen = settings.LlmModelOpenCodeZen,
                LlmModelOllama = settings.LlmModelOllama,
                LlmModelOllamaCloud = settings.LlmModelOllamaCloud,
                LlmVisionFallbackProvider = settings.LlmVisionFallbackProvider,
                LlmVisionFallbackModel = settings.LlmVisionFallbackModel,
                LlmThinkingLevel = settings.LlmThinkingLevel,
                LlmConfirmBeforeSending = settings.LlmConfirmBeforeSending,
                LlmAgentVerbose = settings.LlmAgentVerbose,
                LlmAgentAutoApproveGitEdits = settings.LlmAgentAutoApproveGitEdits,
                LlmAgentAutoApprovePowerShell = settings.LlmAgentAutoApprovePowerShell,
                LlmAgentAutoApprovePlanning = settings.LlmAgentAutoApprovePlanning,
                LlmMaxToolCalls = settings.LlmMaxToolCalls,
                LlmSourceLanguage = settings.LlmSourceLanguage,
                LlmTargetLanguage = settings.LlmTargetLanguage
            };
        }

        private static string BuildOpenSessionTitle(string prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt))
            {
                return string.Empty;
            }

            return prompt
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()
                ?.Trim() ?? string.Empty;
        }

        private static int FindLastLineStart(string text)
        {
            int lastLf = (text ?? string.Empty).LastIndexOf('\n');
            if (lastLf >= 0)
            {
                return lastLf + 1;
            }

            int lastCr = (text ?? string.Empty).LastIndexOf('\r');
            return lastCr >= 0 ? lastCr + 1 : 0;
        }

        private static string ExtractThinkingTimestamp(string line)
        {
            return line.Length >= 8 &&
                line[2] == ':' &&
                line[5] == ':'
                ? line.Substring(0, 8)
                : string.Empty;
        }

        private static string StripTrailingThinkingDots(string line)
        {
            int end = line.Length;
            int count = 0;
            while (end > 0 && line[end - 1] == '.' && count < 3)
            {
                end--;
                count++;
            }

            return line.Substring(0, end);
        }

        private static bool EndsWithLineBreak(string text)
        {
            return (text ?? string.Empty).EndsWith("\r\n", StringComparison.Ordinal) ||
                (text ?? string.Empty).EndsWith("\n", StringComparison.Ordinal) ||
                (text ?? string.Empty).EndsWith("\r", StringComparison.Ordinal);
        }
    }
}
