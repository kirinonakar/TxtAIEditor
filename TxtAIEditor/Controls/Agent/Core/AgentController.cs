using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Services;
using TxtAIEditor.Core.Services.LLM;

namespace TxtAIEditor.Controls
{
    public sealed class AgentController
    {
        private const int DefaultContextStatsDelayMs = 250;
        private const int SlowContextStatsDelayMs = 900;

        private readonly AgentPane _agentPane;
        private readonly AgentAttachmentController _attachmentController;
        private readonly AgentPresetController _presetController;
        private readonly AgentSkillController _skillController;
        private readonly AgentMcpController _mcpController;
        private readonly AgentHistoryController _historyController;
        private readonly AgentSessionEditController _sessionEditController;
        private readonly AgentOpenSessionController _openSessionController;
        private readonly AgentOutputInsertController _outputInsertController;
        private readonly AgentConfirmationController _confirmationController;
        private readonly AgentSelectionContextController _selectionContextController;
        private readonly AgentContextStatsController _contextStatsController;
        private readonly AgentSessionRewindController _sessionRewindController;
        private readonly AgentSessionHistoryCoordinator _sessionHistoryCoordinator;
        private readonly AgentRunCoordinator? _runCoordinator;
        private string _currentSessionId = Guid.NewGuid().ToString();

        private readonly StringBuilder _sessionHistory = new();
        private double _sessionHistoryTokenCount;
        private readonly DispatcherTimer _statsDebounceTimer;

        public AgentController(
            ILLMService llmService,
            ISettingsService settingsService,
            ICredentialService credentialService,
            AgentPane agentPane,
            Func<OpenedTab?> activeTabProvider,
            Func<IReadOnlyList<OpenedTab>> openTabsProvider,
            Func<OpenedTab, int, string> getTabText,
            Func<string, Task<bool>> insertIntoActiveEditorAsync,
            Func<string?, string, OpenedTab> openNewTabWithContent,
            Action<string, string> showError,
            Func<string, string, string> getString,
            AgentFileToolService fileTools,
            PdfTextExtractionService pdfTextExtractionService,
            Action<object> initializePickerWindow,
            Func<string, bool> isGitRepoProvider,
            Func<AgentFileEditPreview, Task> openDiffViewAsync,
            Func<string, Task>? fileModifiedAsync = null,
            Func<string, Task<AgentOpenFileResult>>? openFileInEditorAsync = null,
            Action? beforeDialog = null,
            Action? afterDialog = null,
            Func<string, string, bool, Task>? revertTabOrFileAsync = null,
            Action<string>? closeTabById = null,
            Func<string, Task>? navigateToFolderAsync = null,
            Func<OpenedTab, string?, Task<bool>>? saveTabAsync = null,
            Func<OpenedTab, string, Task<bool>>? editTabAsync = null,
            Func<string?, Task<bool>>? beginStreamIntoActiveEditorAsync = null,
            Func<string?, string, Task<bool>>? streamTextIntoActiveEditorAsync = null,
            Func<string?, Task>? endStreamIntoActiveEditorAsync = null)
        {
            _agentPane = agentPane;
            var runningSessions = new Dictionary<string, AgentRunContext>(StringComparer.Ordinal);
            var toolExecutionSessionGate = new SemaphoreSlim(1, 1);

            var composition = AgentControllerComposition.Create(
                llmService,
                settingsService,
                credentialService,
                agentPane,
                activeTabProvider,
                openTabsProvider,
                getTabText,
                insertIntoActiveEditorAsync,
                openNewTabWithContent,
                showError,
                getString,
                fileTools,
                pdfTextExtractionService,
                initializePickerWindow,
                isGitRepoProvider,
                runningSessions,
                toolExecutionSessionGate,
                () => _currentSessionId,
                value => _currentSessionId = value,
                () => _sessionHistory.ToString(),
                () => _sessionHistoryTokenCount,
                fileModifiedAsync,
                openFileInEditorAsync,
                beforeDialog,
                afterDialog,
                revertTabOrFileAsync,
                closeTabById,
                navigateToFolderAsync,
                saveTabAsync,
                editTabAsync,
                beginStreamIntoActiveEditorAsync,
                streamTextIntoActiveEditorAsync,
                endStreamIntoActiveEditorAsync,
                new AgentControllerCompositionCallbacks(
                    IsCurrentSessionRunning,
                    UpdateContextStats,
                    UpdateContextStatsSlow,
                    force => UpdateContextStatsImmediate(force),
                    () => _runCoordinator?.GetActiveRunWorkspaceRoot(),
                    GetActiveTabForContext,
                    GetActiveSelectionText,
                    BuildActiveSelectionContext,
                    CaptureActiveSelectionSnapshot,
                    GetCurrentSessionSettings,
                    () => _runCoordinator?.GetCurrentRunTranscriptTokens() ?? 0,
                    RestoreSessionHistoryState,
                    sessionId => _runCoordinator?.StopAgent(sessionId),
                    attachment => _runCoordinator?.AddCurrentRunImageToolAttachment(attachment),
                    message => _runCoordinator?.AppendActivity(message),
                    () => _runCoordinator?.GetActiveRunContext()));

            _attachmentController = composition.AttachmentController;
            _presetController = composition.PresetController;
            _skillController = composition.SkillController;
            _mcpController = composition.McpController;
            _historyController = composition.HistoryController;
            _sessionEditController = composition.SessionEditController;
            _openSessionController = composition.OpenSessionController;
            _outputInsertController = composition.OutputInsertController;
            _confirmationController = composition.ConfirmationController;
            _selectionContextController = composition.SelectionContextController;
            _contextStatsController = composition.ContextStatsController;
            _sessionRewindController = composition.SessionRewindController;
            _sessionHistoryCoordinator = composition.SessionHistoryCoordinator;
            _runCoordinator = new AgentRunCoordinator(
                settingsService,
                agentPane,
                composition.UiDispatcher,
                showError,
                getString,
                fileTools,
                composition.DisplayText,
                composition.SkillController,
                composition.McpController,
                composition.OpenSessionController,
                composition.PromptContextService,
                composition.ConfirmationController,
                composition.SelectionContextController,
                composition.FileToolController,
                composition.ToolExecutionController,
                new AgentContextCompressionService(llmService, composition.ModelContextLimits),
                composition.PlanController,
                composition.SessionEditController,
                composition.RunOutputController,
                composition.RunWorkspaceResolver,
                composition.RunTextFormatter,
                composition.LlmToolCatalog,
                composition.ResponseInspector,
                composition.ResponseStreamService,
                composition.SessionHistoryCoordinator,
                runningSessions,
                toolExecutionSessionGate,
                () => _currentSessionId,
                RestoreSessionHistoryState,
                force => UpdateContextStatsImmediate(force));

            _statsDebounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(DefaultContextStatsDelayMs)
            };
            _statsDebounceTimer.Tick += (s, e) =>
            {
                _statsDebounceTimer.Stop();
                if (_agentPane.IsPromptInputFocused)
                {
                    return;
                }

                UpdateContextStatsImmediate();
            };

            _agentPane.HideHtmlCodeBlocks = !settingsService.CurrentSettings.LlmAgentVerbose;
            AgentControllerEventBinder.Wire(
                agentPane,
                _runCoordinator.RunAgentAsync,
                _runCoordinator.StopAgent,
                _openSessionController,
                _sessionRewindController,
                _sessionHistoryCoordinator,
                _outputInsertController,
                _attachmentController,
                _presetController,
                _mcpController,
                _skillController,
                _sessionEditController,
                _confirmationController,
                openDiffViewAsync,
                UpdateContextStats,
                UpdatePromptTokenEstimate);
            _openSessionController.EnsureSession(_currentSessionId);
            _openSessionController.UpdateUI();
            UpdateContextStatsImmediate();
            QueueDeferredStartupDataLoad();
        }

        public IReadOnlyList<AgentFileEditPreview> SessionEdits => _sessionEditController.SessionEdits;

        private void RestoreSessionHistoryState(
            string sessionHistoryText,
            double sessionHistoryTokenCount,
            double currentRunTranscriptTokens)
        {
            _sessionHistory.Clear();
            _sessionHistory.Append(sessionHistoryText ?? string.Empty);
            _sessionHistoryTokenCount = sessionHistoryTokenCount;
            _runCoordinator?.RestoreCurrentRunTranscriptTokens(currentRunTranscriptTokens);
        }

        private void QueueDeferredStartupDataLoad()
        {
            var dispatcher = _agentPane.DispatcherQueue ?? Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            if (dispatcher?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _ = LoadStartupDataAsync()) == true)
            {
                return;
            }

            _ = LoadStartupDataAsync();
        }

        private async Task LoadStartupDataAsync()
        {
            await _presetController.LoadAsync();
            await _mcpController.LoadAsync();
            await _skillController.LoadAsync();
            await _historyController.LoadAsync(_currentSessionId);
        }

        public void SetActiveTab(OpenedTab? activeTab)
        {
            _selectionContextController.SetActiveTab(activeTab);
            UpdateContextStats();
        }

        public void SetSelectionText(string selectedText, OpenedTab? sourceTab = null, int startLine = 0, int endLine = 0)
        {
            _selectionContextController.SetSelectionText(selectedText, sourceTab, startLine, endLine);
            UpdateContextStats();
        }

        public void ClearSelection()
        {
            _selectionContextController.ClearSelection();
            UpdateContextStats();
        }

        private bool IsCurrentSessionRunning()
        {
            return _openSessionController.IsCurrentSessionRunning();
        }

        private EditorSettings GetCurrentSessionSettings()
        {
            return _openSessionController.GetCurrentSessionSettings();
        }

        private OpenedTab? GetActiveTabForContext()
        {
            return _selectionContextController.GetActiveTabForContext(IsCurrentSessionRunning());
        }

        private string GetActiveSelectionText()
        {
            return _selectionContextController.GetActiveSelectionText(IsCurrentSessionRunning());
        }

        private string BuildActiveSelectionContext()
        {
            return _selectionContextController.BuildActiveSelectionContext(IsCurrentSessionRunning());
        }

        internal void CloseMcpSessions()
        {
            _mcpController.Close();
        }

        private AgentSelectionSnapshot CaptureActiveSelectionSnapshot()
        {
            return _selectionContextController.CaptureActiveSelectionSnapshot(IsCurrentSessionRunning());
        }

        public void UpdateContextStats()
        {
            UpdateContextStatsAfterDelay(DefaultContextStatsDelayMs);
        }

        private void UpdatePromptTokenEstimate()
        {
            if (IsCurrentSessionRunning())
            {
                return;
            }

            _contextStatsController.UpdatePromptTokenEstimate();
        }

        private void UpdateContextStatsSlow()
        {
            UpdateContextStatsAfterDelay(SlowContextStatsDelayMs);
        }

        private void UpdateContextStatsAfterDelay(int delayMilliseconds)
        {
            if (IsCurrentSessionRunning())
            {
                return;
            }

            _statsDebounceTimer.Stop();
            if (_agentPane.IsPromptInputFocused)
            {
                return;
            }

            _statsDebounceTimer.Interval = TimeSpan.FromMilliseconds(delayMilliseconds);
            _statsDebounceTimer.Start();
        }

        private void UpdateContextStatsImmediate(bool force = false)
        {
            if (_agentPane.IsPromptInputFocused)
            {
                return;
            }

            _contextStatsController.Update(force);
        }

        public void UpdateModelDisplay(bool forceClearCache = false)
        {
            if (forceClearCache)
            {
                _openSessionController.EnsureSession(_currentSessionId).LlmSettings =
                    _openSessionController.CreateSessionSettingsSnapshot();
            }

            _contextStatsController.UpdateModelDisplay(forceClearCache);
        }

    }
}
