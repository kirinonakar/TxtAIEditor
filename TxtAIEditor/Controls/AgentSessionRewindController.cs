using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TxtAIEditor.Core.Models;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentSessionRewindController
    {
        private readonly Func<bool> _isCurrentSessionRunning;
        private readonly SemaphoreSlim _toolExecutionSessionGate;
        private readonly Func<string> _currentSessionIdProvider;
        private readonly Action _saveActiveOpenSessionFromUI;
        private readonly Func<string, AgentOpenSessionState> _ensureOpenSession;
        private readonly Action<AgentOpenSessionState> _restoreOpenSession;
        private readonly Action<AgentOpenSessionState> _clearThinkingState;
        private readonly AgentSessionEditController _sessionEditController;
        private readonly AgentHistoryController _historyController;
        private readonly AgentDisplayLocalizer _displayText;
        private readonly Action<string, string> _showError;
        private readonly Func<string, string, string> _getString;

        public AgentSessionRewindController(
            Func<bool> isCurrentSessionRunning,
            SemaphoreSlim toolExecutionSessionGate,
            Func<string> currentSessionIdProvider,
            Action saveActiveOpenSessionFromUI,
            Func<string, AgentOpenSessionState> ensureOpenSession,
            Action<AgentOpenSessionState> restoreOpenSession,
            Action<AgentOpenSessionState> clearThinkingState,
            AgentSessionEditController sessionEditController,
            AgentHistoryController historyController,
            AgentDisplayLocalizer displayText,
            Action<string, string> showError,
            Func<string, string, string> getString)
        {
            _isCurrentSessionRunning = isCurrentSessionRunning;
            _toolExecutionSessionGate = toolExecutionSessionGate;
            _currentSessionIdProvider = currentSessionIdProvider;
            _saveActiveOpenSessionFromUI = saveActiveOpenSessionFromUI;
            _ensureOpenSession = ensureOpenSession;
            _restoreOpenSession = restoreOpenSession;
            _clearThinkingState = clearThinkingState;
            _sessionEditController = sessionEditController;
            _historyController = historyController;
            _displayText = displayText;
            _showError = showError;
            _getString = getString;
        }

        public async Task RewindCurrentSessionAsync()
        {
            if (_isCurrentSessionRunning())
            {
                return;
            }

            await _toolExecutionSessionGate.WaitAsync();
            try
            {
                if (_isCurrentSessionRunning())
                {
                    return;
                }

                _saveActiveOpenSessionFromUI();

                var session = _ensureOpenSession(_currentSessionIdProvider());
                if (session.RewindSnapshots.Count == 0)
                {
                    return;
                }

                AgentSessionRewindSnapshot snapshot = session.RewindSnapshots[^1];
                await RevertSessionEditsToSnapshotAsync(snapshot);
                session.RewindSnapshots.RemoveAt(session.RewindSnapshots.Count - 1);
                RestoreOpenSessionFromSnapshot(session, snapshot);
                await PersistRewoundSessionHistoryAsync(session);
            }
            catch (Exception ex)
            {
                _showError(
                    _getString("AgentSessionRewindErrorTitle", "세션 되감기 오류"),
                    string.Format(_getString("AgentSessionRewindErrorFormat", "세션을 되감는 중 오류가 발생했습니다: {0}"), ex.Message));
            }
            finally
            {
                _toolExecutionSessionGate.Release();
            }
        }

        private async Task RevertSessionEditsToSnapshotAsync(AgentSessionRewindSnapshot snapshot)
        {
            var currentEdits = _sessionEditController.SessionEdits.ToList();
            var targetEdits = snapshot.CloneSessionEdits();
            int commonPrefixLength = CountCommonEditPrefix(currentEdits, targetEdits);

            for (int i = currentEdits.Count - 1; i >= commonPrefixLength; i--)
            {
                await _sessionEditController.RevertAsync(currentEdits[i]);
            }

            _sessionEditController.Replace(targetEdits);
        }

        private void RestoreOpenSessionFromSnapshot(
            AgentOpenSessionState session,
            AgentSessionRewindSnapshot snapshot)
        {
            session.Title = string.IsNullOrWhiteSpace(snapshot.Title)
                ? _getString("AgentOpenSessionUntitled", "새 세션")
                : snapshot.Title;
            session.PromptText = snapshot.PromptText;
            session.OutputText = string.IsNullOrEmpty(snapshot.OutputText)
                ? _displayText.OutputPlaceholder
                : snapshot.OutputText;
            session.ActivityText = string.IsNullOrWhiteSpace(snapshot.ActivityText)
                ? _displayText.ActivityIdle
                : snapshot.ActivityText;
            session.SessionHistoryText = snapshot.SessionHistoryText;
            session.LastAnswerText = snapshot.LastAnswerText;
            session.SessionHistoryTokenCount = snapshot.SessionHistoryTokenCount;
            session.CurrentRunTranscriptTokens = snapshot.CurrentRunTranscriptTokens;
            session.Attachments = snapshot.CloneAttachments();
            session.SessionEdits = snapshot.CloneSessionEdits();
            session.WorkspaceRoot = snapshot.WorkspaceRoot;
            session.IsRunning = false;
            session.UpdatedAt = DateTime.Now;
            _clearThinkingState(session);
            _restoreOpenSession(session);
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
                SessionHistoryText = session.SessionHistoryText,
                LastAnswerText = session.LastAnswerText,
                SessionHistoryTokenCount = session.SessionHistoryTokenCount,
                SessionEdits = AgentSessionRewindSnapshot.CloneEdits(session.SessionEdits),
                WorkspaceRoot = session.WorkspaceRoot
            };

            await _historyController.SaveSessionAsync(item, currentSessionId);
        }

        private static int CountCommonEditPrefix(
            IReadOnlyList<AgentFileEditPreview> currentEdits,
            IReadOnlyList<AgentFileEditPreview> targetEdits)
        {
            int count = Math.Min(currentEdits.Count, targetEdits.Count);
            for (int i = 0; i < count; i++)
            {
                if (!AreSameEdit(currentEdits[i], targetEdits[i]))
                {
                    return i;
                }
            }

            return count;
        }

        private static bool AreSameEdit(AgentFileEditPreview left, AgentFileEditPreview right)
        {
            return string.Equals(left.ActionName, right.ActionName, StringComparison.Ordinal) &&
                string.Equals(left.RelativePath, right.RelativePath, StringComparison.Ordinal) &&
                string.Equals(left.FullPath, right.FullPath, StringComparison.Ordinal) &&
                string.Equals(left.OldContent, right.OldContent, StringComparison.Ordinal) &&
                string.Equals(left.NewContent, right.NewContent, StringComparison.Ordinal) &&
                left.IsNewFile == right.IsNewFile;
        }
    }
}
