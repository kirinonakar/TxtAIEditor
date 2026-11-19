using System;
using System.Threading.Tasks;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentSessionRewindController
    {
        private readonly AgentToolExecutionGate _toolExecutionSessionGate;
        private readonly Func<string> _currentSessionIdProvider;
        private readonly Action _saveActiveOpenSessionFromUI;
        private readonly Func<string, AgentOpenSessionState> _ensureOpenSession;
        private readonly Action<AgentOpenSessionState> _restoreOpenSession;
        private readonly Action<AgentOpenSessionState> _clearThinkingState;
        private readonly Action<AgentOpenSessionState> _finishRewinding;
        private readonly AgentSessionEditController _sessionEditController;
        private readonly AgentHistoryController _historyController;
        private readonly AgentDisplayLocalizer _displayText;
        private readonly Action<string, string> _showError;
        private readonly Func<string, string, string> _getString;

        public AgentSessionRewindController(
            AgentToolExecutionGate toolExecutionSessionGate,
            Func<string> currentSessionIdProvider,
            Action saveActiveOpenSessionFromUI,
            Func<string, AgentOpenSessionState> ensureOpenSession,
            Action<AgentOpenSessionState> restoreOpenSession,
            Action<AgentOpenSessionState> clearThinkingState,
            Action<AgentOpenSessionState> finishRewinding,
            AgentSessionEditController sessionEditController,
            AgentHistoryController historyController,
            AgentDisplayLocalizer displayText,
            Action<string, string> showError,
            Func<string, string, string> getString)
        {
            _toolExecutionSessionGate = toolExecutionSessionGate;
            _currentSessionIdProvider = currentSessionIdProvider;
            _saveActiveOpenSessionFromUI = saveActiveOpenSessionFromUI;
            _ensureOpenSession = ensureOpenSession;
            _restoreOpenSession = restoreOpenSession;
            _clearThinkingState = clearThinkingState;
            _finishRewinding = finishRewinding;
            _sessionEditController = sessionEditController;
            _historyController = historyController;
            _displayText = displayText;
            _showError = showError;
            _getString = getString;
        }

        public async Task RewindCurrentSessionAsync()
        {
            string sessionId = _currentSessionIdProvider();
            if (_sessionEditController.IsReviewSessionRunning(sessionId))
            {
                return;
            }

            try
            {
                using IDisposable lease = await _toolExecutionSessionGate.EnterExclusiveAsync();
                if (_currentSessionIdProvider() != sessionId || _sessionEditController.IsReviewSessionRunning(sessionId))
                {
                    return;
                }

                _saveActiveOpenSessionFromUI();

                var session = _ensureOpenSession(sessionId);
                if (session.RewindSnapshots.Count == 0)
                {
                    return;
                }

                session.IsRewinding = true;
                try
                {
                    AgentSessionRewindSnapshot snapshot = session.RewindSnapshots.ReadLatest();
                    await _sessionEditController.RevertRunAsync(sessionId, snapshot);
                    session.RewindSnapshots.RemoveLatest(snapshot);
                    RestoreOpenSessionFromSnapshot(session, snapshot);
                    await PersistRewoundSessionHistoryAsync(session);
                }
                finally
                {
                    _finishRewinding(session);
                }
            }
            catch (Exception ex)
            {
                _showError(
                    _getString("AgentSessionRewindErrorTitle", "세션 되감기 오류"),
                    string.Format(_getString("AgentSessionRewindErrorFormat", "세션을 되감는 중 오류가 발생했습니다: {0}"), ex.Message));
            }
        }

        private void RestoreOpenSessionFromSnapshot(
            AgentOpenSessionState session,
            AgentSessionRewindSnapshot snapshot)
        {
            session.Title = string.IsNullOrWhiteSpace(snapshot.Title)
                ? string.Empty
                : snapshot.Title;
            session.PromptText = snapshot.PromptText;
            session.OutputText = string.IsNullOrEmpty(snapshot.OutputText)
                ? _displayText.OutputPlaceholder
                : snapshot.OutputText;
            session.ChatPrompts = snapshot.ChatPrompts;
            session.ActivityText = string.IsNullOrWhiteSpace(snapshot.ActivityText)
                ? _displayText.ActivityIdle
                : snapshot.ActivityText;
            session.SessionHistoryText = snapshot.SessionHistoryText;
            session.ModelSessionHistoryText = snapshot.ModelSessionHistoryText;
            session.LastAnswerText = snapshot.LastAnswerText;
            session.SessionHistoryTokenCount = snapshot.SessionHistoryTokenCount;
            session.CurrentRunTranscriptTokens = snapshot.CurrentRunTranscriptTokens;
            session.Attachments = snapshot.CloneAttachments();
            session.WorkspaceRoot = snapshot.WorkspaceRoot;
            session.PendingPlanPath = snapshot.PendingPlanPath;
            session.IsRunning = false;
            session.UpdatedAt = DateTime.Now;
            _clearThinkingState(session);
            if (_currentSessionIdProvider() == session.Id)
            {
                _restoreOpenSession(session);
            }
        }

        private async Task PersistRewoundSessionHistoryAsync(AgentOpenSessionState session)
        {
            string currentSessionId = _currentSessionIdProvider();
            if (string.IsNullOrWhiteSpace(session.SessionHistoryText))
            {
                await _historyController.DeleteAsync(session.Id, currentSessionId);
                return;
            }

            var item = new AgentHistoryItem
            {
                Id = session.Id,
                Timestamp = DateTime.Now,
                Title = session.Title,
                Role = session.Role,
                Selections = session.Selections.Copy(),
                OrchestratorSessionId = session.OrchestratorSessionId,
                SessionHistoryText = session.SessionHistoryText,
                ModelSessionHistoryText = session.ModelSessionHistoryText,
                LastAnswerText = session.LastAnswerText,
                SessionHistoryTokenCount = session.SessionHistoryTokenCount,
                SessionEdits = AgentSessionRewindSnapshot.CloneEdits(session.SessionEdits),
                WorkspaceRoot = session.WorkspaceRoot
            };

            await _historyController.SaveSessionAsync(item, currentSessionId);
        }
    }
}
