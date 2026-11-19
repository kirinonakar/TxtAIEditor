using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
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
        private readonly AgentSessionSelectionContext _selections;
        private readonly AgentPresetController _presetController;
        private readonly AgentMcpController _mcpController;
        private readonly AgentHistoryTitleResolver _historyTitleResolver = new();
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
        private readonly HashSet<string> _pendingCloseSessionIds = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _pendingSubagentShutdowns = new(StringComparer.Ordinal);
        private readonly Dictionary<AgentOpenSessionState, long> _idleDeadlines = new();
        private readonly DispatcherQueueTimer _idleTimer;
        private bool _parkingIdleSessions;
        private static readonly TimeSpan SessionIdleDelay = TimeSpan.FromMinutes(10);
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
            AgentSessionSelectionContext selections,
            AgentPresetController presetController,
            AgentMcpController mcpController,
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
            _selections = selections;
            _presetController = presetController;
            _mcpController = mcpController;
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
            _idleTimer = agentPane.DispatcherQueue.CreateTimer();
            _idleTimer.IsRepeating = false;
            _idleTimer.Tick += async (_, _) => await ParkIdleSessionsAsync();
        }

        public bool IsCurrentSessionRunning()
        {
            return _runningSessions.ContainsKey(_currentSessionIdProvider());
        }

        public bool IsAnySessionRunning()
        {
            return _runningSessions.Count > 0;
        }

        public bool HasPendingSubagentShutdown(string sessionId) =>
            _pendingSubagentShutdowns.ContainsKey(sessionId) || _pendingSubagentShutdowns.ContainsValue(sessionId);

        public void MarkSubagentShutdownPending(string parentId, string subagentId) =>
            _pendingSubagentShutdowns[subagentId] = parentId;

        public void CompleteSubagentShutdown(string subagentId)
        {
            if (_pendingSubagentShutdowns.Remove(subagentId))
            {
                UpdateUI();
                UpdateActiveSessionBusyState();
            }
        }

        public bool IsSessionRunning(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return false;
            }

            if (_runningSessions.ContainsKey(sessionId))
            {
                return true;
            }

            var session = _openSessions.FirstOrDefault(item =>
                string.Equals(item.Id, sessionId, StringComparison.Ordinal));
            return session is { IsRunning: true } or { IsRewinding: true };
        }

        public bool IsSessionVisible(string sessionId)
        {
            return string.Equals(_currentSessionIdProvider(), sessionId, StringComparison.Ordinal);
        }

        public bool IsPendingClose(string sessionId)
        {
            return _pendingCloseSessionIds.Contains(sessionId);
        }

        public bool ConsumePendingClose(string sessionId)
        {
            return !string.IsNullOrWhiteSpace(sessionId) && _pendingCloseSessionIds.Remove(sessionId);
        }

        public AgentOpenSessionState EnsureSession(string sessionId) => EnsureSession(sessionId, null);

        public AgentOpenSessionState EnsureSession(string sessionId, AgentOpenSessionState? previousState)
        {
            var session = FindOpenSession(sessionId);
            if (session != null)
            {
                return session;
            }

            session = previousState ?? CreateBlankSession(sessionId);
            _openSessions.Insert(0, session);
            if (IsSessionVisible(sessionId))
            {
                _selections.Visible = session.Selections;
            }
            return session;
        }

        public AgentOpenSessionState? FindOpenSession(string sessionId) =>
            _openSessions.FirstOrDefault(item => string.Equals(item.Id, sessionId, StringComparison.Ordinal));

        public IEnumerable<string> GetOpenSubagentSessionIds(string parentId) =>
            GetOpenSubagentSessions(parentId).Select(item => item.Id);

        public IEnumerable<AgentOpenSessionState> GetOpenSubagentSessions(string parentId) =>
            _openSessions.Where(item => item.Role == AgentSessionRole.Subagent && item.OrchestratorSessionId == parentId);

        public bool IsMcpServerInOpenSession(string id) => _openSessions.Any(session => session.Selections.McpServers.Contains(id));

        public AgentOpenSessionState CreateBlankSession(string? sessionId = null)
        {
            return new AgentOpenSessionState
            {
                Id = string.IsNullOrWhiteSpace(sessionId) ? Guid.NewGuid().ToString() : sessionId,
                Title = string.Empty,
                OutputText = _displayText.OutputPlaceholder,
                ActivityText = _displayText.ActivityIdle,
                WorkspaceRoot = CaptureCurrentWorkspaceRoot(),
                Selections = _selections.Visible.Copy(),
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
            RefreshSessionTitleDisplay();

            // TextChanged fires for every keystroke. Rebuilding the open-session menu here
            // clears and recreates its controls and queues more UI-thread work, which makes
            // the prompt itself feel delayed. The menu is refreshed when it is opened and
            // by the existing session lifecycle operations instead.
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
            session.UseSubagents = _agentPane.UseSubagents;
            session.ActivityText = _agentPane.Activity.Text ?? string.Empty;
            session.WorkspaceRoot = CaptureCurrentWorkspaceRoot();
            SyncVisibleThinkingStateToSession(session);
            if (_runningSessions.TryGetValue(session.Id, out AgentRunContext? runContext))
            {
                session.SessionHistoryText = runContext.SessionHistory.ToString();
                session.ModelSessionHistoryText = runContext.ModelSessionHistory.ToString();
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
                session.SessionEdits = _sessionEditController.GetSessionEdits(session.Id).ToList();
            }
            session.UpdatedAt = DateTime.Now;
            UpdateSessionTitle(session, session.PromptText);
            _historyController.RememberSelections(session.Id, session.Selections);
            RefreshSessionTitleDisplay();
        }

        public void RestoreSessionBody(AgentOpenSessionState session)
        {
            if (session.UnloadedHistory is { } history)
            {
                ApplyHistoryItem(session, history.ReadSession(_sessionEditController.ReadSessionEdits(session.Id)), restoreSelections: false);
            }
            else if (session.IsParked)
            {
                var edits = _sessionEditController.ReadSessionEdits(session.Id);
                session.RestoreBody();
                session.SessionEdits = edits;
                _sessionEditController.SetLiveEdits(session.Id, edits);
            }
        }

        public void OpenUnloadedHistory(AgentHistoryStore.OpenHistorySession history, AgentSessionSelections parentSelections)
        {
            var entry = history.Entry;
            var session = EnsureSession(entry.Id);
            session.UnloadedHistory = history;
            session.Title = entry.Title;
            session.Role = entry.Role;
            session.OrchestratorSessionId = entry.OrchestratorSessionId;
            session.Selections = (entry.Selections ?? parentSelections).Copy();
            session.WorkspaceRoot = entry.WorkspaceRoot ?? string.Empty;
        }

        public void ApplyHistoryItem(AgentOpenSessionState session, AgentHistoryItem item, bool restoreSelections)
        {
            session.ReleaseUnloadedHistory();
            session.DiscardStoredBody();
            session.Title = _historyTitleResolver.Resolve(item, string.Empty);
            session.Role = item.Role;
            session.OrchestratorSessionId = item.OrchestratorSessionId;
            if (restoreSelections && item.Selections is { } selections)
            {
                session.Selections = selections;
            }
            session.UseSubagents = item.Role == AgentSessionRole.Orchestrator;
            session.PromptText = string.Empty;
            session.OutputText = string.Empty;
            session.ChatPrompts = new();
            session.PendingChatPrompts = Array.Empty<string>();
            session.DeferredFollowUp = null;
            session.ChatHiddenOutput = new();
            session.NeedsHistoryFormatting = true;
            session.ActivityText = _getString("AgentHistoryLoadedActivity", "세션 히스토리 로드됨");
            session.SessionHistoryText = item.SessionHistoryText;
            session.ModelSessionHistoryText = item.ModelSessionHistoryText;
            session.LastAnswerText = string.IsNullOrWhiteSpace(item.LastAnswerText)
                ? AgentHistoryController.ExtractLastAgentResponse(item.SessionHistoryText)
                : item.LastAnswerText;
            session.SessionHistoryTokenCount = item.SessionHistoryTokenCount;
            session.CurrentRunTranscriptTokens = 0;
            session.SessionEdits = item.SessionEdits;
            _sessionEditController.SetLiveEdits(session.Id, item.SessionEdits);
            session.Attachments.Clear();
            session.RewindSnapshots.Clear();
            session.WorkspaceRoot = item.WorkspaceRoot ?? string.Empty;
            ClearThinkingState(session);
            session.UpdatedAt = DateTime.Now;
        }

        public void ShowSessionLoadError(Exception ex)
        {
            _agentPane.ClearActivity(string.Format(_getString("AgentSessionLoadFailedFormat",
                "세션을 불러오지 못했습니다: {0}"), ex.Message));
        }

        public void RestoreSession(AgentOpenSessionState session) => TryRestoreSession(session);

        private bool TryRestoreSession(AgentOpenSessionState session)
        {
            try
            {
                RestoreSessionBody(session);
                if (session.NeedsHistoryFormatting)
                {
                    FormatSessionOutput(session);
                    session.NeedsHistoryFormatting = false;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                ShowSessionLoadError(ex);
                return false;
            }

            var previous = FindOpenSession(_currentSessionIdProvider());
            _idleDeadlines.Remove(session);
            using var selectionScope = _selections.Use(session.Selections);
            _restoringOpenSession = true;
            try
            {
                _setCurrentSessionId(session.Id);
                _selections.Visible = session.Selections;
                _presetController.RefreshSelection();
                _mcpController.RefreshSessionSelection();
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
                    session.ModelSessionHistoryText = runContext.ModelSessionHistory.ToString();
                    session.SessionHistoryTokenCount = runContext.SessionHistoryTokenCount;
                    session.CurrentRunTranscriptTokens = runContext.CurrentRunTranscriptTokens;
                    session.Attachments = runContext.Attachments.ToList();
                    session.SessionEdits = runContext.SessionEdits.ToList();
                }

                _agentPane.Prompt.Text = session.PromptText ?? string.Empty;
                _agentPane.UseSubagents = session.UseSubagents;
                _agentPane.ResetOutput(string.IsNullOrEmpty(session.OutputText)
                    ? _displayText.OutputPlaceholder
                    : session.OutputText);
                if (session.ThinkingLineActive && session.IsRunning)
                {
                    _agentPane.ResumeThinkingActivityFromOutput(session.ThinkingLineTimestamp, session.LiveThinkingLabel);
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

            if (previous != null && previous != session)
            {
                ScheduleInactiveSessionParking(previous);
            }
            return true;
        }

        private void ScheduleInactiveSessionParking(AgentOpenSessionState session)
        {
            if (session.IsParked || IsSessionVisible(session.Id))
            {
                return;
            }

            _idleDeadlines[session] = Environment.TickCount64 + (long)SessionIdleDelay.TotalMilliseconds;
            ScheduleIdleTimer();
        }

        private void ScheduleIdleTimer()
        {
            if (_parkingIdleSessions)
            {
                return;
            }
            _idleTimer.Stop();
            if (_idleDeadlines.Count > 0)
            {
                _idleTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, _idleDeadlines.Values.Min() - Environment.TickCount64));
                _idleTimer.Start();
            }
        }

        private async Task ParkIdleSessionsAsync()
        {
            _parkingIdleSessions = true;
            try
            {
                long now = Environment.TickCount64;
                foreach (var (session, deadline) in _idleDeadlines.ToArray())
                {
                    bool StillScheduled() => _idleDeadlines.TryGetValue(session, out long scheduled) && scheduled == deadline;
                    bool IsBusy() => IsSessionRunning(session.Id) || HasPendingSubagentShutdown(session.Id);
                    if (deadline > now || !StillScheduled())
                    {
                        continue;
                    }
                    if (IsBusy())
                    {
                        _idleDeadlines[session] = now + (long)SessionIdleDelay.TotalMilliseconds;
                        continue;
                    }

                    if (!session.IsParked && !IsSessionVisible(session.Id) && !IsReusableBlankSession(session) &&
                        _sessionEditController.TryParkSessionEdits(session.Id, session.SessionEdits))
                    {
                        try
                        {
                            await session.ParkBodyAsync(() => StillScheduled() && !IsSessionVisible(session.Id) && !IsBusy());
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                        {
                            System.Diagnostics.Debug.WriteLine(ex.Message);
                        }
                    }
                    if (StillScheduled())
                    {
                        _idleDeadlines.Remove(session);
                    }
                }
            }
            finally
            {
                _parkingIdleSessions = false;
                ScheduleIdleTimer();
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

            // 같은 워크스페이스에 제목 없는(빈) 세션이 이미 열려 있으면
            // 추가 세션을 만들지 않고 그 세션으로 이동한다.
            string currentWorkspaceRoot = CaptureCurrentWorkspaceRoot();
            var reusableSession = _openSessions
                .Where(session => IsReusableBlankSession(session) &&
                    AreSameWorkspace(session.WorkspaceRoot, currentWorkspaceRoot))
                .OrderByDescending(session => session.UpdatedAt)
                .FirstOrDefault();
            if (reusableSession != null)
            {
                RestoreSession(reusableSession);
                return;
            }

            var session = CreateBlankSession();
            _openSessions.Insert(0, session);
            RestoreSession(session);
        }

        public void SwitchSession(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId) ||
                IsPendingClose(sessionId) ||
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
            var session = FindOpenSession(sessionId);
            if (session == null || session.IsRewinding)
            {
                return;
            }

            var sessionsToClose = _openSessions.Where(item =>
                item == session ||
                (session.Role == AgentSessionRole.Orchestrator &&
                    string.Equals(item.OrchestratorSessionId, session.Id, StringComparison.Ordinal))).ToArray();
            CloseSessions(sessionsToClose, closeAll: false);
        }

        public void CloseAllSessions() => CloseSessions(_openSessions.ToArray(), closeAll: true);

        public void FinishRewinding(AgentOpenSessionState session)
        {
            session.IsRewinding = false;
            if (ConsumePendingClose(session.Id))
            {
                CloseSession(session.Id);
            }
        }

        private void CloseSessions(AgentOpenSessionState[] sessionsToClose, bool closeAll)
        {
            var closingIds = sessionsToClose.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            var sessionsToStop = new List<string>();
            bool closedVisibleSession = false;
            foreach (var item in sessionsToClose)
            {
                if (closeAll && (item.IsRewinding ||
                    (item.OrchestratorSessionId is string parent && FindOpenSession(parent)?.IsRewinding == true)))
                {
                    _pendingCloseSessionIds.Add(item.Id);
                    continue;
                }
                if (_runningSessions.ContainsKey(item.Id))
                {
                    if (_pendingCloseSessionIds.Add(item.Id))
                    {
                        sessionsToStop.Add(item.Id);
                    }
                    continue;
                }

                closedVisibleSession |= IsSessionVisible(item.Id);
                _openSessions.Remove(item);
                _pendingCloseSessionIds.Remove(item.Id);
                _idleDeadlines.Remove(item);
                _historyController.RememberSelections(item.Id, item.Selections);
                _mcpController.ReleaseUnusedConnections(item.Selections.McpServers);
                bool parentStillOpen = item.OrchestratorSessionId is string parentId &&
                    FindOpenSession(parentId) != null && !closingIds.Contains(parentId);
                if (parentStillOpen && item.UnloadedHistory == null)
                {
                    _sessionEditController.TryParkSessionEdits(item.Id, item.SessionEdits);
                }
                else if (!parentStillOpen)
                {
                    _sessionEditController.ReleaseSessionEdits(item.Id);
                }
                item.ReleaseUnloadedHistory();
                // A running orchestrator can still send work to a closed worker.
                if (item.OrchestratorSessionId is not string ownerId || !IsSessionRunning(ownerId))
                {
                    item.DiscardStoredBody();
                    foreach (string workerId in item.RewindSnapshots.GetSubagentIds()
                        .Concat(_historyController.GetSubagentSessionIds(item.Id)).Distinct(StringComparer.Ordinal))
                    {
                        if (FindOpenSession(workerId) == null)
                        {
                            _sessionEditController.ReleaseSessionEdits(workerId);
                        }
                    }
                    item.RewindSnapshots.Clear();
                }
            }

            foreach (string runningSessionId in sessionsToStop)
            {
                _stopAgent(runningSessionId);
            }

            if (closedVisibleSession || (closeAll && IsPendingClose(_currentSessionIdProvider())))
            {
                var next = _openSessions.FirstOrDefault(item => !IsPendingClose(item.Id));
                if (next == null)
                {
                    next = CreateBlankSession();
                    _openSessions.Add(next);
                }

                if (!TryRestoreSession(next))
                {
                    var blank = CreateBlankSession();
                    _openSessions.Add(blank);
                    RestoreSession(blank);
                }
                return;
            }

            UpdateUI();
        }

        public void UpdateUI()
        {
            string currentSessionId = _currentSessionIdProvider();
            var items = _openSessions
                .Where(session => string.Equals(session.Id, currentSessionId, StringComparison.Ordinal) ||
                    !IsPendingClose(session.Id))
                .Select(session =>
                {
                    bool isSelected = string.Equals(session.Id, currentSessionId, StringComparison.Ordinal);
                    bool isRunning = session.IsRunning || _runningSessions.ContainsKey(session.Id);
                    
                    string wRoot = !string.IsNullOrWhiteSpace(session.WorkspaceRoot)
                        ? session.WorkspaceRoot
                        : CaptureCurrentWorkspaceRoot();

                    string prefix = BuildWorkspacePrefix(wRoot);

                    return new AgentOpenSessionItemViewModel
                    {
                        Id = session.Id,
                        OrchestratorSessionId = session.Role == AgentSessionRole.Subagent ? session.OrchestratorSessionId : null,
                        Title = BuildDisplayTitle(prefix, session),
                        IsSelected = isSelected,
                        IsRunning = isRunning,
                        CompletedNotificationCount = session.CompletedNotificationCount,
                        CanSelect = true,
                        CanClose = !IsPendingClose(session.Id)
                    };
                })
                .ToList();

            var currentSession = _openSessions.FirstOrDefault(item =>
                string.Equals(item.Id, currentSessionId, StringComparison.Ordinal));
            string currentSessionTitle = currentSession != null
                ? BuildSessionDisplayTitle(currentSession)
                : string.Empty;

            int completedNotificationCount = items.Sum(item => Math.Max(0, item.CompletedNotificationCount));
            _agentPane.DispatcherQueue.TryEnqueue(() =>
            {
                _completedNotificationCountChanged?.Invoke(completedNotificationCount);
                _agentPane.UpdateOpenSessionItems(items, currentSessionId);
                _agentPane.SetSessionTitle(currentSessionTitle);
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
            ScheduleInactiveSessionParking(session);
            UpdateUI();
        }

        public void UpdateSessionTitle(AgentOpenSessionState session, string prompt)
        {
            string firstLine = BuildOpenSessionTitle(prompt);
            if (!string.IsNullOrWhiteSpace(firstLine))
            {
                session.Title = firstLine;
            }
        }

        public void NotifyWorkspaceChanged()
        {
            // 폴더가 바뀌면 현재 세션의 워크스페이스도 새 폴더를 따라가야 한다.
            // 실행 중인 세션은 런 시작 시 고정된 루트를 유지한다.
            var session = EnsureSession(_currentSessionIdProvider());
            if (!session.IsRunning && !_runningSessions.ContainsKey(session.Id))
            {
                string workspaceRoot = CaptureCurrentWorkspaceRoot();
                if (!string.IsNullOrWhiteSpace(workspaceRoot))
                {
                    session.WorkspaceRoot = workspaceRoot;
                }
            }

            UpdateUI();
            RefreshSessionTitleDisplay();
        }

        private string BuildSessionDisplayTitle(AgentOpenSessionState session)
        {
            string wRoot = CaptureCurrentWorkspaceRoot();
            if (string.IsNullOrWhiteSpace(wRoot))
            {
                wRoot = session.WorkspaceRoot;
            }

            return BuildDisplayTitle(BuildWorkspacePrefix(wRoot), session);
        }

        private string BuildDisplayTitle(string prefix, AgentOpenSessionState session)
        {
            string status = _pendingSubagentShutdowns.ContainsKey(session.Id)
                ? _getString("AgentSubagentShutdownPendingStatus", "종료 지연")
                : string.Empty;
            int pendingWorkers = _pendingSubagentShutdowns.Values.Count(parentId => parentId == session.Id);
            if (pendingWorkers > 0)
            {
                status = string.Format(_getString("AgentOrchestratorShutdownPendingFormat", "서브에이전트 {0}개 종료 대기"), pendingWorkers);
            }
            if (!string.IsNullOrEmpty(status))
            {
                prefix = $"[{status}] {prefix}";
            }

            if (!HasRealSessionTitle(session))
            {
                // 아직 제목이 없는 새 세션: 워크스페이스만 표시한다.
                return (AgentSessionLabels.Prefix(session.Role) + prefix).TrimEnd();
            }

            return $"{AgentSessionLabels.Prefix(session.Role)}{prefix}{session.Title}";
        }

        private bool HasRealSessionTitle(AgentOpenSessionState session)
        {
            return !string.IsNullOrWhiteSpace(session.Title);
        }

        private static string BuildWorkspacePrefix(string? workspaceRoot)
        {
            if (string.IsNullOrWhiteSpace(workspaceRoot))
            {
                return string.Empty;
            }

            try
            {
                string trimmed = workspaceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string lastDir = Path.GetFileName(trimmed);
                if (!string.IsNullOrEmpty(lastDir))
                {
                    return $"[{lastDir}] ";
                }
            }
            catch { }

            return string.Empty;
        }

        private void RefreshSessionTitleDisplay()
        {
            var session = EnsureSession(_currentSessionIdProvider());
            _agentPane.DispatcherQueue.TryEnqueue(() =>
            {
                _agentPane.SetSessionTitle(BuildSessionDisplayTitle(session));
            });
        }

        public void UpdateActiveSessionBusyState()
        {
            bool isCurrentSessionRunning = IsCurrentSessionRunning();
            var currentSession = EnsureSession(_currentSessionIdProvider());
            _agentPane.SetBusy(isCurrentSessionRunning, currentSession.Role == AgentSessionRole.Subagent);
            _agentPane.SetCanRewindSession(!isCurrentSessionRunning && !HasPendingSubagentShutdown(currentSession.Id) &&
                currentSession.RewindSnapshots.Count > 0);
        }

        internal bool CanSubmitChatPrompt =>
            FindOpenSession(_currentSessionIdProvider()) is { IsRewinding: false } session &&
            session.Role != AgentSessionRole.Subagent && !IsPendingClose(session.Id);

        internal AgentChatSnapshot GetChatSnapshot()
        {
            var session = EnsureSession(_currentSessionIdProvider());
            string output = _agentPane.GetRawOutputText();
            return new AgentChatSnapshot(session.Id, session.WorkspaceRoot,
                session.ChatPrompts.Count == 0 ? NormalizePlaceholderOutput(output) : output,
                session.ChatPrompts, session.PendingChatPrompts, session.ChatHiddenOutput, CanSubmitChatPrompt);
        }

        internal void FormatSessionOutput(AgentOpenSessionState session)
        {
            session.ChatPrompts = new();
            session.ChatHiddenOutput = new();
            session.OutputText = AgentHistoryFormatter.Format(session.SessionHistoryText,
                _settingsService.CurrentSettings.LlmAgentVerbose, _getString, session.ChatPrompts);
        }

        public async Task BeginRunOutputBlockAsync(
            AgentRunContext context, string title, string prompt, AgentFollowUpBatch? followUp = null)
        {
            await FlushQueuedRunOutputTextAsync(context);
            await RunOnUIThreadAsync(() =>
            {
                var session = EnsureSession(context.SessionId);
                int start;
                if (IsSessionVisible(context.SessionId))
                {
                    start = _agentPane.BeginOutputBlock(title);
                    session.OutputText = _agentPane.GetRawOutputText();
                    SyncVisibleThinkingStateToSession(session);
                }
                else
                {
                    CompleteThinkingInSession(session);
                    start = AppendOutputLineToSession(session, title);
                }
                RecordChatPrompt(session, start, title, prompt, followUp);
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
                var session = FindOpenSession(context.SessionId);
                if (session == null)
                {
                    return;
                }
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

        public Task AppendRunOutputTextAsync(AgentRunContext context, string text, bool hideInChat = false)
        {
            if (string.IsNullOrEmpty(text))
            {
                return Task.CompletedTask;
            }

            QueueRunOutputText(context, text, hideInChat);
            return Task.CompletedTask;
        }

        public async Task AppendRunOutputLineAsync(
            AgentRunContext context, string line, string? prompt = null, AgentFollowUpBatch? followUp = null)
        {
            await FlushQueuedRunOutputTextAsync(context);
            await RunOnUIThreadAsync(() =>
            {
                var session = EnsureSession(context.SessionId);
                int start;
                if (IsSessionVisible(context.SessionId))
                {
                    start = _agentPane.AppendOutputLine(line);
                    session.OutputText = _agentPane.GetRawOutputText();
                    SyncVisibleThinkingStateToSession(session);
                }
                else
                {
                    CompleteThinkingInSession(session);
                    start = AppendOutputLineToSession(session, line);
                }
                if (prompt != null)
                {
                    RecordChatPrompt(session, start, line, prompt, followUp);
                }
                else
                {
                    RecordHiddenChatOutput(session, start, session.OutputText.Length);
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
                    var session = FindOpenSession(context.SessionId);
                    if (session == null)
                    {
                        return;
                    }
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

        public async Task BeginRunThinkingActivityAsync(AgentRunContext context, string label, Func<string>? liveLabel)
        {
            await FlushQueuedRunOutputTextAsync(context);
            await RunOnUIThreadAsync(() =>
            {
                var session = EnsureSession(context.SessionId);
                if (IsSessionVisible(context.SessionId))
                {
                    _agentPane.BeginThinkingActivity(label, liveLabel);
                    session.OutputText = _agentPane.GetRawOutputText();
                    SyncVisibleThinkingStateToSession(session);
                }
                else
                {
                    BeginThinkingInSession(session, label, liveLabel);
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

        private void BeginThinkingInSession(AgentOpenSessionState session, string label, Func<string>? liveLabel = null)
        {
            CompleteThinkingInSession(session);
            string text = NormalizePlaceholderOutput(session.OutputText);
            if (!string.IsNullOrEmpty(text) && !EndsWithLineBreak(text))
            {
                text += Environment.NewLine;
            }

            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            string prefix = $"{timestamp}  {liveLabel?.Invoke() ?? label}";
            session.OutputText = text + prefix;
            session.ThinkingLineActive = true;
            session.ThinkingLineStart = text.Length;
            session.ThinkingLineTimestamp = timestamp;
            session.ThinkingLinePrefix = prefix;
            session.LiveThinkingLabel = liveLabel;
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
            session.LiveThinkingLabel = null;
            session.UpdatedAt = DateTime.Now;
        }

        public void CompleteThinkingInSession(AgentOpenSessionState session)
        {
            if (!session.ThinkingLineActive)
            {
                return;
            }

            if (session.LiveThinkingLabel is { } liveLabel &&
                session.ThinkingLineStart >= 0 && session.ThinkingLineStart <= (session.OutputText?.Length ?? 0))
            {
                UpdateThinkingInSession(session, liveLabel());
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
            session.LiveThinkingLabel = null;
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

        private void AppendRunOutputTextOnCurrentThread(AgentRunContext context, string text, bool hideInChat)
        {
            var session = FindOpenSession(context.SessionId);
            if (session == null)
            {
                return;
            }
            if (IsSessionVisible(context.SessionId))
            {
                if (hideInChat)
                {
                    _agentPane.StopThinkingActivity();
                    int start = NormalizePlaceholderOutput(_agentPane.GetRawOutputText()).Length;
                    _agentPane.AppendOutputText(text);
                    RecordHiddenChatOutput(session, start, _agentPane.GetRawOutputText().Length);
                    return;
                }
                _agentPane.AppendOutputText(text);
            }
            else
            {
                if (hideInChat)
                {
                    CompleteThinkingInSession(session);
                }
                int start = hideInChat ? NormalizePlaceholderOutput(session.OutputText).Length : 0;
                AppendOutputTextToSession(session, text);
                if (hideInChat)
                {
                    RecordHiddenChatOutput(session, start, session.OutputText.Length);
                }
            }
        }

        private void QueueRunOutputText(AgentRunContext context, string text, bool hideInChat)
        {
            lock (_runOutputBufferGate)
            {
                PendingRunOutputBuffer buffer = GetOrCreateRunOutputBuffer(context.SessionId);
                buffer.Context = context;
                if (buffer.Tail == null || buffer.Tail.HideInChat != hideInChat)
                {
                    buffer.Tail = new PendingRunOutputChunk(new StringBuilder(), hideInChat);
                    buffer.Chunks.Enqueue(buffer.Tail);
                }
                buffer.Tail.Text.Append(text);

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
                if (!TryDequeueRunOutputText(sessionId, out AgentRunContext? context, out string text, out bool hideInChat))
                {
                    return;
                }

                if (context != null)
                {
                    await RunOnUIThreadAsync(
                        () => AppendRunOutputTextOnCurrentThread(context, text, hideInChat),
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

                if (!TryDequeueRunOutputText(context.SessionId, out AgentRunContext? bufferedContext, out string text, out bool hideInChat))
                {
                    return;
                }

                await RunOnUIThreadAsync(
                    () => AppendRunOutputTextOnCurrentThread(bufferedContext ?? context, text, hideInChat),
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
            out string text,
            out bool hideInChat)
        {
            lock (_runOutputBufferGate)
            {
                if (!_pendingRunOutputBuffers.TryGetValue(sessionId, out PendingRunOutputBuffer? buffer) ||
                    !buffer.Chunks.TryPeek(out var chunk))
                {
                    if (buffer != null)
                    {
                        buffer.FlushTask = null;
                    }

                    context = buffer?.Context;
                    text = string.Empty;
                    hideInChat = false;
                    return false;
                }

                int take = Math.Min(chunk.Text.Length, MaxRunOutputFlushChars);
                text = chunk.Text.ToString(0, take);
                chunk.Text.Remove(0, take);
                if (chunk.Text.Length == 0)
                {
                    buffer.Chunks.Dequeue();
                    if (buffer.Chunks.Count == 0) buffer.Tail = null;
                }
                hideInChat = chunk.HideInChat;
                context = buffer.Context;
                return true;
            }
        }

        private bool TryMarkRunOutputFlushIdleIfEmpty(string sessionId)
        {
            lock (_runOutputBufferGate)
            {
                if (!_pendingRunOutputBuffers.TryGetValue(sessionId, out PendingRunOutputBuffer? buffer) ||
                    buffer.Chunks.Count > 0)
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
            session.LiveThinkingLabel = _agentPane.LiveThinkingLabel;
        }

        private string NormalizePlaceholderOutput(string text)
        {
            if (_displayText.IsOutputPlaceholder((text ?? string.Empty).TrimStart()))
            {
                return string.Empty;
            }

            return text ?? string.Empty;
        }

        private static bool AreSameWorkspace(string? first, string? second)
        {
            if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
            {
                return string.IsNullOrWhiteSpace(first) && string.IsNullOrWhiteSpace(second);
            }

            string TrimTrailingSeparators(string value) =>
                value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return string.Equals(
                TrimTrailingSeparators(first),
                TrimTrailingSeparators(second),
                StringComparison.OrdinalIgnoreCase);
        }

        private bool IsReusableBlankSession(AgentOpenSessionState session)
        {
            if (session.IsParked || session.UnloadedHistory != null || session.IsRunning || _runningSessions.ContainsKey(session.Id))
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

        private static void RecordChatPrompt(
            AgentOpenSessionState session, int start, string header, string prompt, AgentFollowUpBatch? followUp)
        {
            int end = session.OutputText.Length;
            if (followUp == null)
            {
                session.ChatPrompts.Add(new AgentChatPrompt(start, end,
                    string.IsNullOrWhiteSpace(prompt) ? AgentChatTranscript.ReadPromptHeader(header) : prompt));
                return;
            }

            // The AgentPanel retains a single header for the batched model request.
            foreach (string instruction in followUp.Prompts)
            {
                session.ChatPrompts.Add(new AgentChatPrompt(start, end, instruction));
                start = end;
            }
            session.PendingChatPrompts = [.. session.PendingChatPrompts.Skip(followUp.Prompts.Count)];
        }

        private static void RecordHiddenChatOutput(AgentOpenSessionState session, int start, int end)
        {
            if (start >= end) return;
            var ranges = session.ChatHiddenOutput;
            if (ranges.Count > 0 && ranges[^1].End == start)
            {
                ranges[^1] = ranges[^1] with { End = end };
            }
            else
            {
                ranges.Add(new AgentChatOutputRange(start, end));
            }
        }

        private int AppendOutputLineToSession(AgentOpenSessionState session, string line)
        {
            string text = NormalizePlaceholderOutput(session.OutputText);
            if (!string.IsNullOrEmpty(text) && !text.EndsWith(Environment.NewLine, StringComparison.Ordinal))
            {
                text += Environment.NewLine;
            }

            session.OutputText = text + line + Environment.NewLine;
            session.UpdatedAt = DateTime.Now;
            return text.Length;
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
            public Queue<PendingRunOutputChunk> Chunks { get; } = new();
            public PendingRunOutputChunk? Tail { get; set; }
            public AgentRunContext? Context { get; set; }
            public Task? FlushTask { get; set; }
        }

        private sealed record PendingRunOutputChunk(StringBuilder Text, bool HideInChat);

        internal static EditorSettings CloneSessionSettings(EditorSettings settings)
        {
            return new EditorSettings
            {
                Language = settings.Language,
                LlmProvider = settings.LlmProvider,
                LlmEndpoint = settings.LlmEndpoint,
                LlmModel = settings.LlmModel,
                LlmApiFormat = settings.LlmApiFormat,
                LlmMaxContextLength = settings.LlmMaxContextLength,
                LlmModelGemini = settings.LlmModelGemini,
                LlmModelOpenAI = settings.LlmModelOpenAI,
                LlmModelOpenRouter = settings.LlmModelOpenRouter,
                LlmModelExaone = settings.LlmModelExaone,
                LlmModelLmStudio = settings.LlmModelLmStudio,
                LlmModelOpenCodeGo = settings.LlmModelOpenCodeGo,
                LlmModelOpenCodeZen = settings.LlmModelOpenCodeZen,
                LlmModelOllama = settings.LlmModelOllama,
                LlmModelOllamaCloud = settings.LlmModelOllamaCloud,
                LlmModelUnsloth = settings.LlmModelUnsloth,
                LlmVisionFallbackProvider = settings.LlmVisionFallbackProvider,
                LlmVisionFallbackModel = settings.LlmVisionFallbackModel,
                LlmVisionFallbackThinkingLevel = settings.LlmVisionFallbackThinkingLevel,
                LlmThinkingLevel = settings.LlmThinkingLevel,
                LlmConfirmBeforeSending = settings.LlmConfirmBeforeSending,
                LlmAgentVerbose = settings.LlmAgentVerbose,
                LlmRetainThinking = settings.LlmRetainThinking,
                LlmDisableTemperature = settings.LlmDisableTemperature,
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
            if (string.IsNullOrEmpty(prompt))
            {
                return string.Empty;
            }

            int lineStart = 0;
            while (lineStart < prompt.Length)
            {
                int lineEnd = lineStart;
                while (lineEnd < prompt.Length && prompt[lineEnd] != '\r' && prompt[lineEnd] != '\n')
                {
                    lineEnd++;
                }

                if (lineEnd > lineStart)
                {
                    return prompt.Substring(lineStart, lineEnd - lineStart).Trim();
                }

                lineStart = lineEnd + 1;
            }

            return string.Empty;
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
