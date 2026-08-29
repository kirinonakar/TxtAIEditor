using System;
using System.IO;
using System.Linq;
using System.Text;
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
        private readonly Func<string> _currentFolderProvider;
        private readonly Func<string, string, string> _getString;
        private readonly AgentHistoryTitleResolver _titleResolver = new();

        public AgentSessionHistoryCoordinator(
            AgentPane agentPane,
            ISettingsService settingsService,
            AgentHistoryController historyController,
            AgentOpenSessionController openSessionController,
            Func<bool> isCurrentSessionRunning,
            Func<string> currentSessionIdProvider,
            Func<string> currentFolderProvider,
            Func<string, string, string> getString)
        {
            _agentPane = agentPane;
            _settingsService = settingsService;
            _historyController = historyController;
            _openSessionController = openSessionController;
            _isCurrentSessionRunning = isCurrentSessionRunning;
            _currentSessionIdProvider = currentSessionIdProvider;
            _currentFolderProvider = currentFolderProvider;
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
            string historyText = AgentRunTranscriptService.ConvertToolCallTagsToLogTags(context.SessionHistory.ToString());
            historyText = AgentRunTranscriptService.ConvertUserRequestMarkersForHistory(historyText);
            openSession.SessionHistoryText = historyText;
            openSession.ModelSessionHistoryText = context.ModelSessionHistory.ToString();
            openSession.LastAnswerText = context.LastAnswerText;
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
                SessionHistoryText = historyText,
                ModelSessionHistoryText = context.ModelSessionHistory.ToString(),
                LastAnswerText = context.LastAnswerText,
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
                string.Empty);
            session.PromptText = string.Empty;
            session.OutputText = AgentHistoryFormatter.Format(
                item.SessionHistoryText,
                _settingsService.CurrentSettings.LlmAgentVerbose,
                _getString);
            session.ActivityText = _getString("AgentHistoryLoadedActivity", "세션 히스토리 로드됨");
            session.SessionHistoryText = item.SessionHistoryText;
            session.ModelSessionHistoryText = item.ModelSessionHistoryText;
            session.LastAnswerText = string.IsNullOrWhiteSpace(item.LastAnswerText)
                ? AgentHistoryController.ExtractLastAgentResponse(item.SessionHistoryText)
                : item.LastAnswerText;
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

        public async Task SaveCurrentVerboseHistoryAsync()
        {
            if (_isCurrentSessionRunning())
            {
                return;
            }

            _openSessionController.SaveActiveFromUI();
            AgentOpenSessionState session = _openSessionController.EnsureSession(_currentSessionIdProvider());
            string verboseHistory = session.SessionHistoryText ?? string.Empty;
            if (string.IsNullOrWhiteSpace(verboseHistory))
            {
                _agentPane.ClearActivity(_getString(
                    "AgentActivityHistorySaveEmpty",
                    "저장할 세션 히스토리가 없습니다."));
                return;
            }

            try
            {
                string currentFolder = _currentFolderProvider();
                if (string.IsNullOrWhiteSpace(currentFolder) || !Directory.Exists(currentFolder))
                {
                    throw new DirectoryNotFoundException(currentFolder);
                }

                string fileName = BuildVerboseHistoryFileName(session);
                string filePath = Path.Combine(currentFolder, fileName);
                await File.WriteAllTextAsync(filePath, verboseHistory, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                _agentPane.ClearActivity(string.Format(
                    _getString("AgentActivityHistorySavedFormat", "verbose 히스토리 저장됨: {0}"),
                    fileName));
            }
            catch (Exception ex)
            {
                _agentPane.ClearActivity(string.Format(
                    _getString("AgentActivityHistorySaveFailedFormat", "히스토리 저장 실패: {0}"),
                    ex.Message));
            }
        }

        private static string BuildVerboseHistoryFileName(AgentOpenSessionState session)
        {
            string title = string.IsNullOrWhiteSpace(session.Title) ? "session" : session.Title.Trim();
            foreach (char invalidCharacter in Path.GetInvalidFileNameChars())
            {
                title = title.Replace(invalidCharacter, '_');
            }

            title = title.Trim('.', ' ');
            if (title.Length == 0)
            {
                title = "session";
            }
            else if (title.Length > 80)
            {
                title = title.Substring(0, 80).TrimEnd('.', ' ');
            }

            return $"AgentHistory_{session.UpdatedAt:yyyyMMdd_HHmmss}_{title}.txt";
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
            string formatted = AgentHistoryFormatter.Format(text, _settingsService.CurrentSettings.LlmAgentVerbose, _getString);
            session.OutputText = formatted;
            _agentPane.ResetOutput(formatted);
        }
    }
}
