using System;
using System.Linq;
using System.Threading.Tasks;
using TxtAIEditor.Core.Interfaces;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentSessionHistoryCoordinator
    {
        private readonly AgentPane _agentPane;
        private readonly ISettingsService _settingsService;
        private readonly AgentHistoryController _historyController;
        private readonly AgentOpenSessionController _openSessionController;
        private readonly Func<bool> _isCurrentSessionRunning;
        private readonly Func<string> _currentSessionIdProvider;
        private readonly Func<string, string, string> _getString;
        private readonly AgentHistoryTitleResolver _titleResolver = new();

        public AgentSessionHistoryCoordinator(
            AgentPane agentPane,
            ISettingsService settingsService,
            AgentHistoryController historyController,
            AgentOpenSessionController openSessionController,
            Func<bool> isCurrentSessionRunning,
            Func<string> currentSessionIdProvider,
            Func<string, string, string> getString)
        {
            _agentPane = agentPane;
            _settingsService = settingsService;
            _historyController = historyController;
            _openSessionController = openSessionController;
            _isCurrentSessionRunning = isCurrentSessionRunning;
            _currentSessionIdProvider = currentSessionIdProvider;
            _getString = getString;
        }

        public async Task SaveRunSessionToHistoryAsync(AgentRunContext context, string userInstruction)
        {
            if (context.SessionHistory.Length == 0)
            {
                return;
            }

            var openSession = _openSessionController.EnsureSession(context.SessionId);
            _openSessionController.UpdateSessionTitle(openSession, userInstruction);
            openSession.SessionHistoryText = context.SessionHistory.ToString();
            openSession.SessionHistoryTokenCount = context.SessionHistoryTokenCount;
            openSession.CurrentRunTranscriptTokens = context.CurrentRunTranscriptTokens;
            openSession.SessionEdits = context.SessionEdits.ToList();
            openSession.Attachments = context.Attachments.ToList();
            openSession.WorkspaceRoot = context.WorkspaceRoot;
            openSession.UpdatedAt = DateTime.Now;
            _openSessionController.UpdateUI();

            var item = new AgentHistoryItem
            {
                Id = context.SessionId,
                Timestamp = DateTime.Now,
                Title = openSession.Title,
                SessionHistoryText = context.SessionHistory.ToString(),
                SessionHistoryTokenCount = context.SessionHistoryTokenCount,
                SessionEdits = context.SessionEdits.ToList(),
                WorkspaceRoot = context.WorkspaceRoot
            };

            await _historyController.SaveSessionAsync(item, context.SessionId);
        }

        public void LoadHistorySession(string historyId)
        {
            if (_isCurrentSessionRunning()) return;

            var item = _historyController.GetSession(historyId);
            if (item == null) return;

            _openSessionController.SaveActiveFromUI();

            var session = _openSessionController.EnsureSession(item.Id);
            session.Title = _titleResolver.Resolve(
                item,
                _getString("AgentOpenSessionUntitled", "새 세션"));
            session.PromptText = string.Empty;
            session.OutputText = AgentHistoryFormatter.Format(
                item.SessionHistoryText,
                _settingsService.CurrentSettings.LlmAgentVerbose);
            session.ActivityText = _getString("AgentHistoryLoadedActivity", "세션 히스토리 로드됨");
            session.SessionHistoryText = item.SessionHistoryText;
            session.SessionHistoryTokenCount = item.SessionHistoryTokenCount;
            session.CurrentRunTranscriptTokens = 0;
            session.SessionEdits = item.SessionEdits.ToList();
            session.Attachments.Clear();
            session.RewindSnapshots.Clear();
            session.WorkspaceRoot = item.WorkspaceRoot ?? string.Empty;
            _openSessionController.ClearThinkingState(session);
            session.UpdatedAt = DateTime.Now;

            _agentPane.DispatcherQueue.TryEnqueue(() =>
            {
                _agentPane.HideHtmlCodeBlocks = !_settingsService.CurrentSettings.LlmAgentVerbose;
                _openSessionController.RestoreSession(session);
            });
        }

        public async Task DeleteHistorySessionAsync(string historyId)
        {
            if (string.IsNullOrEmpty(historyId)) return;

            string currentSessionId = _currentSessionIdProvider();
            await _historyController.DeleteAsync(historyId, currentSessionId);

            if (string.Equals(currentSessionId, historyId, StringComparison.Ordinal))
            {
                _openSessionController.CloseSession(historyId);
            }
            else
            {
                _historyController.UpdateUI(currentSessionId);
            }
        }

        public async Task ClearAllHistoryAsync()
        {
            string currentSessionId = _currentSessionIdProvider();
            await _historyController.ClearAsync(currentSessionId);
            _openSessionController.CloseSession(currentSessionId);
        }

        public void RefreshOutputDisplay()
        {
            if (_isCurrentSessionRunning()) return;

            var session = _openSessionController.EnsureSession(_currentSessionIdProvider());
            string text = session.SessionHistoryText ?? string.Empty;
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            _agentPane.HideHtmlCodeBlocks = _settingsService.CurrentSettings.LlmAgentVerbose == false;
            string formatted = AgentHistoryFormatter.Format(text, _settingsService.CurrentSettings.LlmAgentVerbose);
            session.OutputText = formatted;
            _agentPane.ResetOutput(formatted);
        }
    }
}
