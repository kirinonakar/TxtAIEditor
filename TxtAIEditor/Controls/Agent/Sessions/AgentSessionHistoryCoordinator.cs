using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
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
        private long _historyOpenRequest;

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
            if (openSession.Role != AgentSessionRole.Subagent)
            {
                _openSessionController.UpdateSessionTitle(openSession, userInstruction);
            }
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

            await _historyController.SaveSessionAsync(CreateHistoryItem(openSession, context, historyText), context.SessionId);
        }

        public Task EnsureOrchestratorHistoryAsync(AgentOpenSessionState session, AgentRunContext context,
            string conversationTurn, AgentRunTranscript transcript, string initialTranscript)
        {
            if (_historyController.GetEntry(session.Id)?.Role == AgentSessionRole.Orchestrator)
            {
                return Task.CompletedTask;
            }

            return _historyController.SaveSessionAsync(
                CreateOrchestratorCheckpoint(session, context, conversationTurn, transcript, initialTranscript),
                _currentSessionIdProvider());
        }

        private static AgentHistoryItem CreateOrchestratorCheckpoint(AgentOpenSessionState session, AgentRunContext context,
            string conversationTurn, AgentRunTranscript transcript, string initialTranscript)
        {
            // Save the initial user turn before workers can save their own histories. A later
            // completed run replaces this checkpoint with the full conversation.
            var checkpoint = new AgentRunContext
            {
                ApiType = context.ApiType,
                SessionHistoryTokenCount = context.SessionHistoryTokenCount,
                LastAnswerText = context.LastAnswerText,
                SessionEdits = context.SessionEdits,
                WorkspaceRoot = context.WorkspaceRoot
            };
            checkpoint.SessionHistory.Append(context.SessionHistory);
            AgentRunTranscriptRecorder.AppendPromptTranscript(checkpoint, conversationTurn,
                transcript.DisplayText, initialTranscript, transcript.ModelText);
            return CreateHistoryItem(session, checkpoint, checkpoint.SessionHistory.ToString());
        }

        private static AgentHistoryItem CreateHistoryItem(AgentOpenSessionState session, AgentRunContext context, string historyText) =>
            new()
            {
                Id = session.Id,
                Timestamp = DateTime.Now,
                Title = session.Title,
                Role = session.Role,
                Selections = session.Selections.Copy(),
                OrchestratorSessionId = session.OrchestratorSessionId,
                SessionHistoryText = historyText,
                ModelSessionHistoryText = context.ModelSessionHistory.ToString(),
                LastAnswerText = context.LastAnswerText,
                SessionHistoryTokenCount = context.SessionHistoryTokenCount,
                SessionEdits = context.SessionEdits.ToList(),
                WorkspaceRoot = context.WorkspaceRoot
            };

        public void LoadHistorySession(string historyId)
        {
            _agentPane.DispatcherQueue.TryEnqueue(async () => await RestoreHistorySessionAsync(historyId));
        }

        private async Task RestoreHistorySessionAsync(string historyId)
        {
            long request = ++_historyOpenRequest;
            string previousId = _currentSessionIdProvider();
            // 아직 실행 중(thinking)인 세션을 히스토리에서 불러오면 진행 중인 런 상태를
            // 덮어쓰게 되므로 그 세션만 막는다. 다른 세션이 실행 중이어도 히스토리는 열 수 있다.
            if (_openSessionController.IsSessionRunning(historyId))
            {
                _agentPane.ClearActivity(_getString(
                    "AgentActivityHistoryLoadRunningBlocked",
                    "실행 중인 세션은 히스토리에서 불러올 수 없습니다."));
                return;
            }

            try
            {
                var item = await _historyController.ReadSessionAsync(historyId);
                string? workerGroupId = item?.Role == AgentSessionRole.Orchestrator ? historyId : null;
                if (item == null && _historyController.GetSubagentSessionIds(historyId).FirstOrDefault() is { } workerId)
                {
                    // Older saves may contain only the workers. Open the newest worker and
                    // its siblings without constructing a replacement orchestrator transcript.
                    workerGroupId = historyId;
                    item = await _historyController.ReadSessionAsync(workerId);
                }
                if (item == null || request != _historyOpenRequest || previousId != _currentSessionIdProvider() ||
                    _openSessionController.IsSessionRunning(item.Id) || _historyController.GetEntry(item.Id) == null)
                {
                    return;
                }

                _openSessionController.SaveActiveFromUI();
                bool wasOpen = _openSessionController.FindOpenSession(item.Id) != null;
                var session = _openSessionController.EnsureSession(item.Id);
                _openSessionController.ApplyHistoryItem(session, item, restoreSelections: !wasOpen);
                Exception? workerLoadError = null;
                if (workerGroupId != null)
                {
                    foreach (string id in _historyController.GetSubagentSessionIds(workerGroupId).Reverse())
                    {
                        // Preserve the live state and drafts for subagents already open.
                        if (_openSessionController.FindOpenSession(id) != null)
                        {
                            continue;
                        }
                        try
                        {
                            if (_historyController.OpenSession(id) is { } workerHistory)
                            {
                                _openSessionController.OpenUnloadedHistory(workerHistory, session.Selections);
                            }
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            workerLoadError ??= ex;
                        }
                    }
                }

                _agentPane.HideHtmlCodeBlocks = !_settingsService.CurrentSettings.LlmAgentVerbose;
                _openSessionController.RestoreSession(session);
                if (workerLoadError != null)
                {
                    _openSessionController.ShowSessionLoadError(workerLoadError);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                if (request == _historyOpenRequest && previousId == _currentSessionIdProvider())
                {
                    _openSessionController.ShowSessionLoadError(ex);
                }
            }
        }

        public async Task DeleteHistorySessionAsync(string historyId)
        {
            if (string.IsNullOrEmpty(historyId)) return;

            string currentSessionId = _currentSessionIdProvider();
            try
            {
                await _historyController.DeleteAsync(historyId, currentSessionId);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                ShowHistoryWriteError(ex);
                return;
            }

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
            try
            {
                await _historyController.ClearAsync(currentSessionId);
                _openSessionController.CloseSession(currentSessionId);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                ShowHistoryWriteError(ex);
            }
        }

        private void ShowHistoryWriteError(Exception ex) => _agentPane.ClearActivity(string.Format(
            _getString("AgentActivityHistorySaveFailedFormat", "히스토리 저장 실패: {0}"), ex.Message));

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
                string formattedHistory = AgentHistoryFormatter.Format(verboseHistory, verbose: true, getString: _getString);
                await File.WriteAllTextAsync(filePath, formattedHistory, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
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
