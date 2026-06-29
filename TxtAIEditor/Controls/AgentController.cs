using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Services;
using TxtAIEditor.Core.Services.LLM;
using static TxtAIEditor.Controls.AgentToolHelpers;

namespace TxtAIEditor.Controls
{
    public sealed class AgentController
    {
        private const int DefaultContextStatsDelayMs = 250;
        private const int SlowContextStatsDelayMs = 900;

        private readonly ILLMService _llmService;
        private readonly ISettingsService _settingsService;
        private readonly ICredentialService _credentialService;
        private readonly AgentPane _agentPane;
        private readonly AgentUiDispatcher _uiDispatcher;
        private readonly Func<OpenedTab?> _activeTabProvider;
        private readonly Func<IReadOnlyList<OpenedTab>> _openTabsProvider;
        private readonly Action<string, string> _showError;
        private readonly Func<string, string, string> _getString;
        private readonly AgentFileToolService _fileTools;
        private readonly Action<object> _initializePickerWindow;
        private readonly Func<string, bool> _isGitRepoProvider;
        private readonly Func<string, Task>? _fileModifiedAsync;
        private readonly Func<string, Task<AgentOpenFileResult>>? _openFileInEditorAsync;
        private readonly Func<AgentFileEditPreview, Task> _openDiffViewAsync;
        private readonly Action? _beforeDialog;
        private readonly Action? _afterDialog;
        private readonly Func<string, string, bool, Task>? _revertTabOrFileAsync;
        private readonly Action<string>? _closeTabById;
        private readonly Func<string, Task>? _navigateToFolderAsync;
        private readonly Func<OpenedTab, string?, Task<bool>>? _saveTabAsync;
        private readonly AgentDisplayLocalizer _displayText;
        private readonly AgentAttachmentController _attachmentController;
        private readonly AgentPresetController _presetController;
        private readonly AgentSkillController _skillController;
        private readonly AgentMcpController _mcpController;
        private readonly AgentHistoryController _historyController;
        private readonly AgentSessionEditController _sessionEditController;
        private readonly AgentOpenSessionController _openSessionController;
        private readonly AgentPromptContextService _promptContextService;
        private readonly AgentOutputInsertController _outputInsertController;
        private readonly AgentConfirmationController _confirmationController;
        private readonly AgentTabToolController _tabToolController;
        private readonly AgentSelectionContextController _selectionContextController;
        private readonly AgentFileToolController _fileToolController;
        private readonly AgentToolExecutionController _toolExecutionController;
        private readonly AgentContextStatsController _contextStatsController;
        private readonly AgentPlanController _planController;
        private readonly AgentSessionRewindController _sessionRewindController;
        private readonly AgentRunOutputController _runOutputController;
        private readonly AgentRunTranscriptService _runTranscriptService = new();
        private readonly AgentModelContextLimitProvider _modelContextLimits = new();
        private readonly AgentLlmToolCatalog _llmToolCatalog = new();
        private readonly AgentResponseInspector _responseInspector = new();
        private readonly AgentResponseStreamService _responseStreamService;
        private readonly AgentSessionHistoryCoordinator _sessionHistoryCoordinator;
        private readonly Dictionary<string, AgentRunContext> _runningSessions = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim _toolExecutionSessionGate = new(1, 1);
        private readonly AsyncLocal<AgentRunContext?> _activeToolRunContext = new();
        private readonly AsyncLocal<AgentRunContext?> _activeWorkspaceRunContext = new();
        private string _currentSessionId = Guid.NewGuid().ToString();

        private bool _isRunning;
        private CancellationTokenSource? _runCancellation;
        private readonly StringBuilder _sessionHistory = new();
        private double _sessionHistoryTokenCount;
        private readonly DispatcherTimer _statsDebounceTimer;
        private double _currentRunTranscriptTokens;
        private readonly List<LlmMessageAttachment> _currentRunImageToolAttachments = new();

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
            _llmService = llmService;
            _settingsService = settingsService;
            _credentialService = credentialService;
            _agentPane = agentPane;
            _uiDispatcher = new AgentUiDispatcher(_agentPane.DispatcherQueue);
            _activeTabProvider = activeTabProvider;
            _openTabsProvider = openTabsProvider;
            _showError = showError;
            _getString = getString;
            _fileTools = fileTools;
            _fileTools.WorkspaceRootOverrideProvider = GetActiveRunWorkspaceRoot;
            _initializePickerWindow = initializePickerWindow;
            _isGitRepoProvider = isGitRepoProvider;
            _openDiffViewAsync = openDiffViewAsync;
            _fileModifiedAsync = fileModifiedAsync;
            _openFileInEditorAsync = openFileInEditorAsync;
            _beforeDialog = beforeDialog;
            _afterDialog = afterDialog;
            _revertTabOrFileAsync = revertTabOrFileAsync;
            _closeTabById = closeTabById;
            _navigateToFolderAsync = navigateToFolderAsync;
            _saveTabAsync = saveTabAsync;
            _displayText = new AgentDisplayLocalizer(_getString);
            _attachmentController = new AgentAttachmentController(
                _agentPane,
                _initializePickerWindow,
                _showError,
                _getString,
                _displayText,
                IsCurrentSessionRunning,
                UpdateContextStats,
                AgentTokenEstimator.Estimate,
                pdfTextExtractionService,
                _beforeDialog,
                _afterDialog);
            _selectionContextController = new AgentSelectionContextController(
                _activeTabProvider,
                () => _fileTools.WorkspaceRoot);
            _fileToolController = new AgentFileToolController(
                _fileTools,
                _selectionContextController,
                GetActiveTabForContext,
                () => _isRunning,
                _getString,
                _openFileInEditorAsync);
            _presetController = new AgentPresetController(
                _agentPane,
                _initializePickerWindow,
                _showError,
                _getString,
                () => UpdateContextStatsSlow(),
                _beforeDialog,
                _afterDialog);
            _skillController = new AgentSkillController(
                _agentPane,
                _getString,
                () => UpdateContextStatsSlow());
            _mcpController = new AgentMcpController(
                _agentPane,
                _initializePickerWindow,
                _credentialService,
                _showError,
                _getString,
                () => UpdateContextStatsSlow(),
                () => _fileTools.WorkspaceRoot,
                _fileModifiedAsync,
                _beforeDialog,
                _afterDialog);
            _historyController = new AgentHistoryController(_agentPane);
            _sessionEditController = new AgentSessionEditController(
                _agentPane,
                async action => await _uiDispatcher.RunAsync<bool>(async () =>
                {
                    await action();
                    return true;
                }),
                _fileModifiedAsync,
                _revertTabOrFileAsync,
                _closeTabById,
                AppendActivity,
                _showError,
                _getString);
            _openSessionController = new AgentOpenSessionController(
                _settingsService,
                _agentPane,
                _fileTools,
                _attachmentController,
                _sessionEditController,
                _historyController,
                _displayText,
                _runningSessions,
                () => _currentSessionId,
                value => _currentSessionId = value,
                () => _sessionHistory.ToString(),
                () => _sessionHistoryTokenCount,
                GetCurrentRunTranscriptTokens,
                RestoreSessionHistoryState,
                StopAgent,
                () => UpdateContextStatsImmediate(),
                _getString,
                _navigateToFolderAsync);
            _sessionHistoryCoordinator = new AgentSessionHistoryCoordinator(
                _agentPane,
                _settingsService,
                _historyController,
                _openSessionController,
                IsCurrentSessionRunning,
                () => _currentSessionId,
                _getString);
            _runOutputController = new AgentRunOutputController(
                _openSessionController,
                insertIntoActiveEditorAsync,
                beginStreamIntoActiveEditorAsync,
                streamTextIntoActiveEditorAsync,
                endStreamIntoActiveEditorAsync);
            _responseStreamService = new AgentResponseStreamService(
                _llmService,
                _agentPane,
                _uiDispatcher,
                _runOutputController,
                _openSessionController,
                _displayText,
                _responseInspector,
                force => UpdateContextStatsImmediate(force),
                _getString);
            _sessionRewindController = new AgentSessionRewindController(
                IsCurrentSessionRunning,
                _toolExecutionSessionGate,
                () => _currentSessionId,
                _openSessionController.SaveActiveFromUI,
                _openSessionController.EnsureSession,
                _openSessionController.RestoreSession,
                _openSessionController.ClearThinkingState,
                _sessionEditController,
                _historyController,
                _displayText,
                _showError,
                _getString);
            var workspaceContextBuilder = new AgentWorkspaceContextBuilder(
                () => _fileTools.WorkspaceRoot,
                openTabsProvider,
                _attachmentController);
            _promptContextService = new AgentPromptContextService(
                _agentPane,
                _fileTools,
                _presetController,
                _skillController,
                _mcpController,
                workspaceContextBuilder,
                _attachmentController,
                _displayText,
                _modelContextLimits,
                GetActiveTabForContext,
                CaptureActiveSelectionSnapshot,
                GetCurrentSessionSettings,
                () => _sessionHistory.ToString(),
                () => UpdateContextStatsImmediate(force: true),
                _getString);
            _contextStatsController = new AgentContextStatsController(
                _settingsService,
                _agentPane,
                _displayText,
                _attachmentController,
                IsCurrentSessionRunning,
                GetActiveTabForContext,
                GetActiveSelectionText,
                BuildActiveSelectionContext,
                _promptContextService.BuildAgentInstruction,
                _promptContextService.BuildWorkspaceContext,
                _promptContextService.BuildSessionHistoryForPrompt,
                GetCurrentRunTranscriptTokens,
                _sessionHistoryCoordinator.RefreshOutputDisplay,
                _getString,
                _modelContextLimits,
                _promptContextService.EstimateToolCatalogTokens,
                GetCurrentSessionSettings);
            _outputInsertController = new AgentOutputInsertController(
                _agentPane,
                openNewTabWithContent,
                _openSessionController.GetCurrentLastAnswerText,
                _showError,
                _getString,
                _displayText.IsOutputPlaceholder,
                () => _uiDispatcher.RunAsync(() => { }));
            _tabToolController = new AgentTabToolController(
                GetActiveTabForContext,
                _activeTabProvider,
                openTabsProvider,
                getTabText,
                insertIntoActiveEditorAsync,
                openNewTabWithContent,
                saveTabAsync,
                editTabAsync,
                _fileTools,
                _sessionEditController,
                action => _uiDispatcher.RunAsync(action),
                action => _uiDispatcher.RunAsync(action),
                action => _uiDispatcher.RunAsync(action));
            _confirmationController = new AgentConfirmationController(
                _settingsService,
                _agentPane,
                _fileTools,
                _isGitRepoProvider,
                async action => await _uiDispatcher.RunAsync(action),
                AppendActivity,
                _getString);
            _planController = new AgentPlanController(
                () => _activeToolRunContext.Value ?? _activeWorkspaceRunContext.Value,
                _confirmationController,
                _openTabsProvider,
                _openFileInEditorAsync,
                _saveTabAsync,
                action => _uiDispatcher.RunAsync(action),
                async action => await _uiDispatcher.RunAsync(action),
                async action => await _uiDispatcher.RunAsync(action),
                _runOutputController.AppendRunActivityAsync,
                _runOutputController.AppendRunOutputLineAsync,
                _showError,
                _getString);
            _toolExecutionController = new AgentToolExecutionController(
                _llmService,
                _fileTools,
                _fileToolController,
                _tabToolController,
                _skillController,
                _mcpController,
                AddCurrentRunImageToolAttachment,
                _planController.MakePlanAsync,
                AppendActivity,
                _getString);
            _fileTools.ConfirmFileEditAsync = _confirmationController.ConfirmFileEditAsync;
            _fileTools.ConfirmPowerShellAsync = _confirmationController.ConfirmPowerShellAsync;
            _fileTools.FileEditCommittedAsync = preview => _uiDispatcher.RunAsync(() => _sessionEditController.Track(preview));
            _fileTools.ActivityReporter = AppendActivity;
            if (_fileModifiedAsync != null)
            {
                _fileTools.FileModifiedAsync = _fileModifiedAsync;
            }

            _statsDebounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(DefaultContextStatsDelayMs)
            };
            _statsDebounceTimer.Tick += (s, e) =>
            {
                _statsDebounceTimer.Stop();
                UpdateContextStatsImmediate();
            };

            _agentPane.HideHtmlCodeBlocks = !_settingsService.CurrentSettings.LlmAgentVerbose;
            WireEvents();
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
            _currentRunTranscriptTokens = currentRunTranscriptTokens;
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

        private bool IsAnySessionRunning()
        {
            return _openSessionController.IsAnySessionRunning();
        }

        private string? GetActiveRunWorkspaceRoot()
        {
            AgentRunContext? context = _activeToolRunContext.Value ?? _activeWorkspaceRunContext.Value;
            return string.IsNullOrWhiteSpace(context?.WorkspaceRoot)
                ? null
                : context.WorkspaceRoot;
        }

        private string ResolveWorkspaceRootForRun(
            string preservedWorkspaceRoot,
            string capturedWorkspaceRoot,
            string userInstruction)
        {
            if (IsApprovedPlanExecutionPrompt(userInstruction) &&
                IsExistingDirectory(preservedWorkspaceRoot))
            {
                return NormalizeDirectoryPath(preservedWorkspaceRoot);
            }

            if (IsExistingDirectory(capturedWorkspaceRoot))
            {
                return NormalizeDirectoryPath(capturedWorkspaceRoot);
            }

            if (IsExistingDirectory(preservedWorkspaceRoot))
            {
                return NormalizeDirectoryPath(preservedWorkspaceRoot);
            }

            return _fileTools.WorkspaceRoot;
        }

        private static bool IsApprovedPlanExecutionPrompt(string userInstruction)
        {
            return (userInstruction ?? string.Empty)
                .TrimStart()
                .StartsWith("[Approved plan execution]", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsExistingDirectory(string path)
        {
            return !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);
        }

        private static string NormalizeDirectoryPath(string path)
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private EditorSettings GetCurrentSessionSettings()
        {
            return _openSessionController.GetCurrentSessionSettings();
        }

        private OpenedTab? GetActiveTabForContext()
        {
            return _selectionContextController.GetActiveTabForContext(IsCurrentSessionRunning());
        }

        private Task<OpenedTab?> CaptureActiveTabForRunAsync()
        {
            return _uiDispatcher.RunAsync(() => _selectionContextController.CaptureActiveTabForRun(IsCurrentSessionRunning()));
        }

        private string GetActiveSelectionText()
        {
            return _selectionContextController.GetActiveSelectionText(IsCurrentSessionRunning());
        }

        private string BuildActiveSelectionContext()
        {
            return _selectionContextController.BuildActiveSelectionContext(IsCurrentSessionRunning());
        }

        private AgentSelectionSnapshot CaptureActiveSelectionSnapshot()
        {
            return _selectionContextController.CaptureActiveSelectionSnapshot(IsCurrentSessionRunning());
        }

        private void WireEvents()
        {
            _agentPane.RunRequested += async (_, _) => await RunAgentAsync();
            _agentPane.StopRequested += (_, _) => StopAgent();
            _agentPane.NewSessionRequested += (_, _) =>
            {
                _agentPane.PlanningModeCheckBox.IsChecked = false;
                _openSessionController.CreateNewSession();
            };
            _agentPane.RewindSessionRequested += async (_, _) => await _sessionRewindController.RewindCurrentSessionAsync();
            _agentPane.OpenSessionsFlyoutOpened += (_, _) =>
            {
                _openSessionController.SavePromptTitleFromUI();
                _openSessionController.UpdateUI();
            };
            _agentPane.OpenSessionSelected += (_, sessionId) => _openSessionController.SwitchSession(sessionId);
            _agentPane.OpenSessionClosed += (_, sessionId) => _openSessionController.CloseSession(sessionId);
            _agentPane.HistorySelected += (_, historyId) => _sessionHistoryCoordinator.LoadHistorySession(historyId);
            _agentPane.HistoryDeleted += async (_, historyId) => await _sessionHistoryCoordinator.DeleteHistorySessionAsync(historyId);
            _agentPane.HistoryToolbarDeleteClicked += async (_, _) => await _sessionHistoryCoordinator.ClearAllHistoryAsync();
            _agentPane.InsertOutputRequested += async (_, _) => await _outputInsertController.InsertOutputAsync();
            _agentPane.InsertNewTabOutputRequested += async (_, _) => await _outputInsertController.InsertNewTabOutputAsync();
            _agentPane.AddAttachmentRequested += async (_, _) => await _attachmentController.AddAttachmentsAsync();
            _agentPane.FilesDropped += async (_, filePaths) => await _attachmentController.AddDroppedFilesAsync(filePaths);
            _agentPane.RemoveAttachmentRequested += (_, attachment) => _attachmentController.RemoveAttachment(attachment.Id);
            _agentPane.AgentPresetAddRequested += async (_, _) => await _presetController.AddPresetAsync();
            _agentPane.AgentPresetToggled += (_, presetName) => _presetController.TogglePreset(presetName);
            _agentPane.AgentPresetEdited += async (_, presetName) => await _presetController.EditPresetAsync(presetName);
            _agentPane.AgentPresetDeleted += async (_, presetName) => await _presetController.DeletePresetAsync(presetName);
            _agentPane.AgentPresetRemoved += (_, presetName) => _presetController.RemoveSelectedPreset(presetName);
            _agentPane.AgentPresetExportRequested += async (_, _) => await _presetController.ExportPresetsAsync();
            _agentPane.AgentPresetImportRequested += async (_, _) => await _presetController.ImportPresetsAsync();
            _agentPane.AgentMcpFlyoutOpened += async (_, _) => await _mcpController.LoadAsync();
            _agentPane.AgentMcpAddRequested += async (_, _) => await _mcpController.AddMcpAsync();
            _agentPane.AgentMcpExportRequested += async (_, _) => await _mcpController.ExportMcpAsync();
            _agentPane.AgentMcpImportRequested += async (_, _) => await _mcpController.ImportMcpAsync();
            _agentPane.AgentMcpToggled += async (_, serverName) => await _mcpController.ToggleMcpAsync(serverName);
            _agentPane.AgentMcpEdited += async (_, serverName) => await _mcpController.EditMcpAsync(serverName);
            _agentPane.AgentMcpDeleted += async (_, serverName) => await _mcpController.DeleteMcpAsync(serverName);
            _agentPane.AgentMcpRemoved += (_, serverName) => _mcpController.RemoveSelectedMcp(serverName);
            _agentPane.AgentSkillFlyoutOpened += async (_, _) => await _skillController.LoadIfNeededAsync();
            _agentPane.AgentSkillToggled += (_, skillName) => _skillController.ToggleSkill(skillName);
            _agentPane.AgentSkillRefreshRequested += async (_, _) => await _skillController.LoadAsync();
            _agentPane.AgentSkillRemoved += (_, skillName) => _skillController.RemoveSelectedSkill(skillName);
            _agentPane.Prompt.TextChanged += (_, _) =>
            {
                _openSessionController.SavePromptTitleFromUI();
                UpdateContextStats();
            };
            _agentPane.PlanningModeCheckBox.Checked += (_, _) => UpdateContextStats();
            _agentPane.PlanningModeCheckBox.Unchecked += (_, _) => UpdateContextStats();

            _agentPane.DiffApproved += (_, _) => _confirmationController.ApprovePending();
            _agentPane.DiffCancelled += (_, _) => _confirmationController.CancelPending();
            _agentPane.FileRevertRequested += async (_, preview) => await _sessionEditController.RevertAsync(preview);
            _agentPane.FileDiffRequested += async (_, preview) => await _openDiffViewAsync(preview);
        }
 
        private async Task RunAgentAsync()
        {
            if (_runningSessions.ContainsKey(_currentSessionId))
            {
                return;
            }
  
            var settings = _settingsService.CurrentSettings;

            string userInstruction = _agentPane.Prompt.Text?.Trim() ?? string.Empty;
            bool requestedPlanningMode = _agentPane.PlanningMode;
            if (requestedPlanningMode && string.IsNullOrWhiteSpace(userInstruction))
            {
                _showError(
                    _getString("AgentErrorTitle", "Agent 오류"),
                    _getString("AgentEmptyPrompt", "Agent에게 맡길 작업을 입력해 주세요."));
                return;
            }

            await _mcpController.EnsureActiveToolsAsync(CancellationToken.None);
            string targetLanguage = settings?.ResolveTargetLanguage() ?? "Korean";
            string instruction = _promptContextService.BuildAgentInstruction(requestedPlanningMode
                ? AgentPlanController.BuildPlanningModeRequest(userInstruction, targetLanguage)
                : userInstruction);
            if (string.IsNullOrWhiteSpace(instruction))
            {
                _showError(
                    _getString("AgentErrorTitle", "Agent 오류"),
                    _getString("AgentEmptyPrompt", "Agent에게 맡길 작업을 입력해 주세요."));
                return;
            }

            var activeOpenSession = _openSessionController.EnsureSession(_currentSessionId);
            string preservedWorkspaceRoot = activeOpenSession.WorkspaceRoot;
            _openSessionController.SaveActiveFromUI();
            activeOpenSession.RewindSnapshots.Add(AgentSessionRewindSnapshot.Capture(activeOpenSession));
            _openSessionController.UpdateSessionTitle(activeOpenSession, userInstruction);
            activeOpenSession.PromptText = _agentPane.Prompt.Text ?? string.Empty;
            activeOpenSession.UpdatedAt = DateTime.Now;
            activeOpenSession.IsRunning = true;
            activeOpenSession.WorkspaceRoot = ResolveWorkspaceRootForRun(
                preservedWorkspaceRoot,
                activeOpenSession.WorkspaceRoot,
                userInstruction);
            EditorSettings runSettings = await _openSessionController.ResolveRunSessionSettingsAsync(activeOpenSession);

            var runContext = new AgentRunContext
            {
                SessionId = activeOpenSession.Id,
                SessionHistoryTokenCount = activeOpenSession.SessionHistoryTokenCount,
                CurrentRunTranscriptTokens = 0,
                Attachments = activeOpenSession.Attachments.ToList(),
                SessionEdits = activeOpenSession.SessionEdits.ToList(),
                LastAnswerText = activeOpenSession.LastAnswerText,
                StreamToTab = _agentPane.StreamToTab,
                WorkspaceRoot = activeOpenSession.WorkspaceRoot,
                LlmSettings = runSettings,
                IsPlanningMode = requestedPlanningMode,
                OriginalUserInstruction = userInstruction
            };
            runContext.SessionHistory.Append(activeOpenSession.SessionHistoryText ?? string.Empty);
  
            _isRunning = true;
            _runningSessions[activeOpenSession.Id] = runContext;
            _openSessionController.UpdateUI();
            _agentPane.HideHtmlCodeBlocks = !_settingsService.CurrentSettings.LlmAgentVerbose;
            _fileToolController.StartRun();
            _currentRunImageToolAttachments.Clear();
            _selectionContextController.ClearRunSnapshots();
            _currentRunTranscriptTokens = 0;
            var cancellationSource = new CancellationTokenSource();
            runContext.Cancellation = cancellationSource;
            _runCancellation = cancellationSource;
            CancellationToken cancellationToken = cancellationSource.Token;
            _openSessionController.UpdateActiveSessionBusyState();
            await _runOutputController.ClearRunActivityAsync(runContext, _getString("AgentActivityStarting", "시작 중"));
            await _runOutputController.BeginRunOutputBlockAsync(runContext, BuildRunHeader(_promptContextService.BuildInstructionDisplay(userInstruction)));
            await _runOutputController.AppendRunActivityAsync(runContext, _getString("AgentActivityCollectingContext", "맥락 수집 중"));

            string initialTranscript = string.Empty;
            string transcript = string.Empty;
            string approvedPlanExecutionPrompt = string.Empty;
            string approvedPlanWorkspaceRoot = activeOpenSession.WorkspaceRoot;
            void AppendRunSessionHistoryLine(string line = "")
            {
                runContext.SessionHistory.AppendLine(line);
                runContext.SessionHistoryTokenCount += AgentTokenEstimator.Estimate(line + Environment.NewLine);
            }

            Task PersistRunSessionToHistoryAsync()
            {
                return _sessionHistoryCoordinator.SaveRunSessionToHistoryAsync(runContext, userInstruction);
            }

            _activeWorkspaceRunContext.Value = runContext;
            try
            {
                OpenedTab? currentRunActiveTab = await CaptureActiveTabForRunAsync();
                AgentSelectionSnapshot currentRunSelectionSnapshot = _selectionContextController.CaptureSelectionForRun(_isRunning);
                runContext.StreamToTabTargetTabId = currentRunActiveTab?.Id;
                _fileToolController.SetRunContext(currentRunSelectionSnapshot, currentRunActiveTab);
                string workspaceContext = _promptContextService.BuildWorkspaceContext(
                    instruction,
                    currentRunActiveTab,
                    currentRunSelectionSnapshot,
                    runContext.Attachments,
                    runContext.WorkspaceRoot);
                string lastWorkspaceContext = workspaceContext;
                string runSelectionContext = _selectionContextController.BuildSelectionContext(currentRunSelectionSnapshot);
                runContext.PlanWorkspaceContext = workspaceContext;
                runContext.PlanSelectionContext = runSelectionContext;
                var initialTranscriptBuilder = new StringBuilder();
                string sessionHistoryForPrompt = _promptContextService.BuildSessionHistoryForPrompt(
                    instruction,
                    workspaceContext,
                    runSelectionContext);
                if (!string.IsNullOrWhiteSpace(sessionHistoryForPrompt))
                {
                    initialTranscriptBuilder.AppendLine("[Session History]");
                    initialTranscriptBuilder.AppendLine(sessionHistoryForPrompt);
                    initialTranscriptBuilder.AppendLine("=================================");
                    initialTranscriptBuilder.AppendLine();
                }
                initialTranscriptBuilder.AppendLine(workspaceContext);
                initialTranscript = initialTranscriptBuilder.ToString();

                transcript = initialTranscript;
                string response = string.Empty;

                bool completed = false;
                bool reachedToolStepLimit = false;
                int emptyResponseRetryCount = 0;
                const int maxEmptyResponseRetries = 1;
                int truncationRetryCount = 0;
                const int maxTruncationRetries = 3;
                int toolCallFormatRetryCount = 0;
                const int maxToolCallFormatRetries = 2;
                int makePlanRetryCount = 0;
                const int maxMakePlanRetries = 2;
                int skillMentionRetryCount = 0;
                const int maxSkillMentionRetries = 2;
                int repeatedDuplicateToolSkipCount = 0;
                string? lastDuplicateToolInvocationKey = null;
                const int maxRepeatedDuplicateToolSkips = 3;
                bool planningMode = requestedPlanningMode;
                int maxToolSteps = runContext.LlmSettings.LlmMaxToolCalls > 0 ? runContext.LlmSettings.LlmMaxToolCalls : 50;
                var successfulToolResults = new Dictionary<string, string>(StringComparer.Ordinal);
                int currentTaskStartEditIndex = runContext.SessionEdits.Count;

                for (int step = 0; step < maxToolSteps; step++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string currentTranscript = _runTranscriptService.BuildWithEditLedger(
                        transcript,
                        currentTaskStartEditIndex,
                        runContext.SessionEdits);

                    AgentResponseStreamResult streamResult = await _responseStreamService.RunAsync(
                        runContext,
                        instruction,
                        currentTranscript,
                        runSelectionContext,
                        planningMode,
                        _llmToolCatalog.Build(planningMode, _mcpController.GetActiveToolAliases()),
                        _promptContextService.GetImageAttachmentsForRun(runContext),
                        cancellationToken);

                    response = streamResult.Response;
                    bool truncated = streamResult.Truncated;
                    string cleanResponse = streamResult.CleanResponse;
                    int printedLength = streamResult.PrintedLength;
                    bool heldPotentialToolCallText = streamResult.HeldPotentialToolCallText;
                    bool visibleTextFlushed = streamResult.VisibleTextFlushed;

                    if (truncated && !string.IsNullOrWhiteSpace(response))
                    {
                        await _runOutputController.StopRunThinkingActivityAsync(runContext);

                        if (truncationRetryCount < maxTruncationRetries)
                        {
                            truncationRetryCount++;
                            string continuationNote =
                                "\n\n[Agent response truncated]\n" +
                                "Your previous response was cut off due to the output token limit. " +
                                "Continue exactly from where you stopped. Do not repeat what you already wrote. " +
                                "If you were about to write a tool_call, write it now.";

                            await _uiDispatcher.RunAsync(() =>
                            {
                                transcript += response + continuationNote;
                                runContext.CurrentRunTranscriptTokens += AgentTokenEstimator.Estimate(response + continuationNote);
                                UpdateContextStatsImmediate(force: true);
                            });
                            string truncationRetryMessage = _getString(
                                "AgentActivityTruncatedRetry",
                                "응답이 잘려 이어서 작성합니다.");
                            await _runOutputController.AppendRunActivityAsync(runContext, truncationRetryMessage);
                            AppendRunSessionHistoryLine($"[Truncated Response Retry]: {truncationRetryMessage}");
                            AppendRunSessionHistoryLine();
                            continue;
                        }
                    }

                    if (string.IsNullOrWhiteSpace(response))
                    {
                        await _runOutputController.StopRunThinkingActivityAsync(runContext);

                        if (emptyResponseRetryCount < maxEmptyResponseRetries)
                        {
                            emptyResponseRetryCount++;
                            string retryNote =
                                "\n\n[Agent empty response]\n" +
                                "The model returned no visible content. Continue with a final tool_call or a final answer.";

                            await _uiDispatcher.RunAsync(() =>
                            {
                                transcript += retryNote;
                                runContext.CurrentRunTranscriptTokens += AgentTokenEstimator.Estimate(retryNote);
                                UpdateContextStatsImmediate(force: true);
                            });
                            string emptyRetryMessage = _getString(
                                "AgentActivityEmptyResponseRetry",
                                "빈 응답을 수신해 다시 시도합니다.");
                            await _runOutputController.AppendRunActivityAsync(runContext, emptyRetryMessage);
                            AppendRunSessionHistoryLine($"[Empty Response Retry]: {emptyRetryMessage}");
                            AppendRunSessionHistoryLine();

                            continue;
                        }

                        string emptyResponseMessage = _getString(
                            "LlmErrorEmptyResponse",
                            "AI로부터 빈 응답을 수신했습니다.");

                            await _runOutputController.AppendRunActivityAsync(runContext, emptyResponseMessage);
                            await _runOutputController.AppendRunOutputLineAsync(runContext, emptyResponseMessage);

                        AppendRunSessionHistoryLine($"[User Prompt]: {instruction}");
                        string emptyRunTranscript = transcript.Substring(initialTranscript.Length);
                        if (!string.IsNullOrWhiteSpace(emptyRunTranscript))
                        {
                            AppendRunSessionHistoryLine(emptyRunTranscript.Trim());
                        }
                        AppendRunSessionHistoryLine($"[Agent Response]: {emptyResponseMessage}");
                        AppendRunSessionHistoryLine();
                        _ = PersistRunSessionToHistoryAsync();

                        runContext.CurrentRunTranscriptTokens += AgentTokenEstimator.Estimate(emptyResponseMessage);
                        completed = true;
                        break;
                    }

                    await _runOutputController.StopRunThinkingActivityAsync(runContext);

                    bool responseHasToolSyntax = AgentToolCallParser.ContainsToolCallSyntax(response);

                    int endLength = cleanResponse.Length;
                    if (!runContext.LlmSettings.LlmAgentVerbose && responseHasToolSyntax)
                    {
                        int toolCallIndex = AgentToolCallParser.FindToolCallIndex(cleanResponse);
                        if (toolCallIndex >= 0)
                        {
                            endLength = toolCallIndex;
                        }
                    }

                    if (!planningMode && !responseHasToolSyntax && heldPotentialToolCallText && !visibleTextFlushed && !string.IsNullOrEmpty(cleanResponse))
                    {
                        visibleTextFlushed = true;
                        await _runOutputController.AppendOutputTextAndStreamToTabAsync(runContext, cleanResponse);
                    }
                    else if (!planningMode && !responseHasToolSyntax && printedLength < endLength)
                    {
                        string remainingText = cleanResponse.Substring(printedLength, endLength - printedLength);
                        visibleTextFlushed = true;
                        await _runOutputController.AppendOutputTextAndStreamToTabAsync(runContext, remainingText);
                    }

                    cancellationToken.ThrowIfCancellationRequested();

                    bool parsedToolCall = AgentToolCallParser.TryParseMulti(response, out List<AgentToolCallParser.ToolCallInfo> toolCalls);
                    if (!parsedToolCall || toolCalls.Count == 0)
                    {
                        if (responseHasToolSyntax)
                        {
                            toolCallFormatRetryCount++;
                            AgentToolCallParser.TryGetToolCallFormatIssue(response, out string toolCallFormatIssue);
                            string retryNote = _responseInspector.BuildToolCallFormatRetryNote(
                                !string.IsNullOrWhiteSpace(toolCallFormatIssue)
                                    ? toolCallFormatIssue
                                    : "The tool_call JSON could not be parsed.");
                            transcript += "\n\n" + response + "\n\n" + retryNote;
                            runContext.CurrentRunTranscriptTokens += AgentTokenEstimator.Estimate(response + "\n\n" + retryNote);

                            string retryMessage = _getString(
                                "AgentToolCallFormatRetry",
                                "도구 호출을 해석하지 못해 다시 요청합니다.");
                            await _runOutputController.AppendRunActivityAsync(runContext, retryMessage);
                            await _runOutputController.AppendRunOutputLineAsync(runContext, retryMessage);
                            AppendRunSessionHistoryLine($"[Tool Call Format Retry]: {retryMessage}");
                            if (!string.IsNullOrWhiteSpace(response))
                            {
                                AppendRunSessionHistoryLine($"[Previous Tool Call]: {response.Trim()}");
                            }
                            AppendRunSessionHistoryLine();
                            if (_runOutputController.IsSessionVisible(runContext.SessionId))
                            {
                                UpdateContextStatsImmediate(force: true);
                            }

                            if (toolCallFormatRetryCount > maxToolCallFormatRetries)
                            {
                                string limitMessage = _getString(
                                    "AgentToolCallFormatRetryLimit",
                                    "도구 호출 형식 오류가 반복되어 Agent 실행을 중단했습니다. 작업을 다시 실행해 주세요.");
                                await _runOutputController.AppendRunActivityAsync(runContext, limitMessage);
                                await _runOutputController.AppendRunOutputLineAsync(runContext, limitMessage);

                                AppendRunSessionHistoryLine($"[User Prompt]: {instruction}");
                                string formatRunTranscript = transcript.Substring(initialTranscript.Length);
                                if (!string.IsNullOrWhiteSpace(formatRunTranscript))
                                {
                                    AppendRunSessionHistoryLine(formatRunTranscript.Trim());
                                }
                                AppendRunSessionHistoryLine($"[Agent Response]: {limitMessage}");
                                AppendRunSessionHistoryLine();
                                _ = PersistRunSessionToHistoryAsync();

                                runContext.CurrentRunTranscriptTokens += AgentTokenEstimator.Estimate(limitMessage);
                                completed = true;
                                break;
                            }

                            continue;
                        }

                        if (planningMode)
                        {
                            makePlanRetryCount++;
                            string retryNote =
                                "\n\n[Planning mode make_plan required]\n" +
                                "Do not answer with the plan as plain text. Save it by including exactly one make_plan tool_call, using the Markdown plan as the markdown argument. Do not include a path or filename.";

                            transcript += "\n\n" + response + "\n\n" + retryNote;
                            runContext.CurrentRunTranscriptTokens += AgentTokenEstimator.Estimate(response + "\n\n" + retryNote);

                            string retryMessage = _getString(
                                "AgentMakePlanRequired",
                                "계획 모드에서는 make_plan 도구로 계획서를 저장해야 합니다.");
                            await _runOutputController.AppendRunActivityAsync(runContext, retryMessage);
                            await _runOutputController.AppendRunOutputLineAsync(runContext, retryMessage);
                            AppendRunSessionHistoryLine($"[Make Plan Retry]: {retryMessage}");
                            if (!string.IsNullOrWhiteSpace(response))
                            {
                                AppendRunSessionHistoryLine($"[Previous Response]: {response.Trim()}");
                            }
                            AppendRunSessionHistoryLine();
                            if (_runOutputController.IsSessionVisible(runContext.SessionId))
                            {
                                UpdateContextStatsImmediate(force: true);
                            }

                            if (makePlanRetryCount > maxMakePlanRetries)
                            {
                                string limitMessage = _getString(
                                    "AgentMakePlanRetryLimit",
                                    "make_plan 도구 호출이 생성되지 않아 계획 모드를 중단했습니다. 다시 실행해 주세요.");
                                await _runOutputController.AppendRunActivityAsync(runContext, limitMessage);
                                await _runOutputController.AppendRunOutputLineAsync(runContext, limitMessage);

                                AppendRunSessionHistoryLine($"[User Prompt]: {instruction}");
                                string failedPlanTranscript = transcript.Substring(initialTranscript.Length);
                                if (!string.IsNullOrWhiteSpace(failedPlanTranscript))
                                {
                                    AppendRunSessionHistoryLine(failedPlanTranscript.Trim());
                                }
                                AppendRunSessionHistoryLine($"[Agent Response]: {limitMessage}");
                                AppendRunSessionHistoryLine();
                                _ = PersistRunSessionToHistoryAsync();

                                runContext.CurrentRunTranscriptTokens += AgentTokenEstimator.Estimate(limitMessage);
                                completed = true;
                                break;
                            }

                            continue;
                        }

                        if (!planningMode &&
                            skillMentionRetryCount < maxSkillMentionRetries &&
                            instruction.Contains("[Enabled agent skills]", StringComparison.OrdinalIgnoreCase) &&
                            _responseInspector.ResponseMentionsSkillIntent(response))
                        {
                            skillMentionRetryCount++;
                            string retryNote =
                                "\n\n[Skill not called]\n" +
                                "You described intent to use a skill in prose but did not emit the skill_use tool_call. " +
                                "Briefly state why if useful, then end with exactly one skill_use tool_call for the relevant skill:\n" +
                                "<tool_call>{\"name\":\"skill_use\",\"arguments\":{\"name\":\"skill-name\"}}>";

                            await _uiDispatcher.RunAsync(() =>
                            {
                                transcript += "\n\n" + response + retryNote;
                                runContext.CurrentRunTranscriptTokens += AgentTokenEstimator.Estimate(response + retryNote);
                                UpdateContextStatsImmediate(force: true);
                            });

                            string retryMessage = _getString(
                                "AgentSkillNotCalledRetry",
                                "스킬을 호출하지 않고 설명만 했습니다. 도구 호출을 다시 시도합니다.");
                            await _runOutputController.AppendRunActivityAsync(runContext, retryMessage);
                            await _runOutputController.AppendRunOutputLineAsync(runContext, retryMessage);
                            AppendRunSessionHistoryLine($"[Skill Not Called Retry]: {retryMessage}");
                            if (!string.IsNullOrWhiteSpace(response))
                            {
                                AppendRunSessionHistoryLine($"[Previous Response]: {response.Trim()}");
                            }
                            AppendRunSessionHistoryLine();
                            if (_runOutputController.IsSessionVisible(runContext.SessionId))
                            {
                                UpdateContextStatsImmediate(force: true);
                            }
                            continue;
                        }

                        if (!visibleTextFlushed && !string.IsNullOrWhiteSpace(response))
                        {
                            await _runOutputController.AppendOutputTextAndStreamToTabAsync(runContext, response);
                        }

                        toolCallFormatRetryCount = 0;
                        runContext.LastAnswerText = BuildLastAnswerText(response, cleanResponse, runContext.LlmSettings.LlmAgentVerbose);
                        await _runOutputController.AppendRunActivityAsync(runContext, _getString("AgentActivityFinalAnswer", "최종 응답 작성 완료"));
                        if (runContext.StreamToTab)
                        {
                            await _runOutputController.EndStreamedAnswerAsync(runContext);
                        }

                        AppendRunSessionHistoryLine($"[User Prompt]: {instruction}");
                        string runTranscript = transcript.Substring(initialTranscript.Length);
                        if (!string.IsNullOrWhiteSpace(runTranscript))
                        {
                            AppendRunSessionHistoryLine(runTranscript.Trim());
                        }
                        AppendRunSessionHistoryLine($"[Agent Response]: {response.Trim()}");
                        AppendRunSessionHistoryLine();
                        _ = PersistRunSessionToHistoryAsync();

                        runContext.CurrentRunTranscriptTokens += AgentTokenEstimator.Estimate(response);
                        completed = true;
                        break;
                    }

                    bool stopAfterLoopGuard = false;
                    var toolCallResults = new List<(string Name, JsonElement Args, string Result, string ResultForTranscript, bool Skipped, string NormalizedName)>();

                    foreach (var tc in toolCalls)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        string currentToolName = tc.ToolName;
                        JsonElement currentArguments = tc.Arguments;

                        string normalizedToolName = NormalizeToolName(currentToolName);
                        string toolInvocationKey = BuildToolInvocationKey(normalizedToolName, currentArguments);
                        bool skippedDuplicateTool = false;
                        string toolResult;
                        string toolResultForTranscript;
                        toolCallFormatRetryCount = 0;

                        if (!planningMode && normalizedToolName == "make_plan")
                        {
                            toolResult = "make_plan failed: this tool is only available when planning mode is enabled.";
                            toolResultForTranscript = toolResult;
                        }
                        else if (planningMode && IsMutatingTool(normalizedToolName) && normalizedToolName != "make_plan")
                        {
                            toolResult =
                                "blocked: planning mode is plan-only and cannot run file/editor mutation tools. " +
                                "Continue with safe inspection if needed, or write the detailed Markdown plan as the final answer.";
                            toolResultForTranscript = toolResult;
                        }
                        else if (ShouldSkipDuplicateSuccessfulTool(normalizedToolName) &&
                            successfulToolResults.TryGetValue(toolInvocationKey, out string? cachedToolResult))
                        {
                            skippedDuplicateTool = true;
                            toolResult = cachedToolResult ?? string.Empty;
                            if (string.Equals(lastDuplicateToolInvocationKey, toolInvocationKey, StringComparison.Ordinal))
                            {
                                repeatedDuplicateToolSkipCount++;
                            }
                            else
                            {
                                lastDuplicateToolInvocationKey = toolInvocationKey;
                                repeatedDuplicateToolSkipCount = 1;
                            }

                            var duplicateResultBuilder = new StringBuilder();
                            duplicateResultBuilder.AppendLine("[Tool execution skipped: identical successful call already ran earlier in this agent run. Cached result follows; use it instead of calling the tool again.]");
                            duplicateResultBuilder.AppendLine(toolResult);
                            if (repeatedDuplicateToolSkipCount >= 2)
                            {
                                duplicateResultBuilder.AppendLine();
                                duplicateResultBuilder.AppendLine("[Loop guard] You repeated the same skipped tool call. Choose exactly one different next action, or write the final answer. Do not call this tool again unless a later mutating tool changes the relevant files or workspace state.");
                            }

                            stopAfterLoopGuard = repeatedDuplicateToolSkipCount >= maxRepeatedDuplicateToolSkips;
                            toolResultForTranscript = duplicateResultBuilder.ToString().TrimEnd();
                        }
                        else
                        {
                            lastDuplicateToolInvocationKey = null;
                            repeatedDuplicateToolSkipCount = 0;
                            await _toolExecutionSessionGate.WaitAsync(cancellationToken);
                            try
                            {
                                _activeToolRunContext.Value = runContext;
                                await _uiDispatcher.RunAsync(() =>
                                {
                                    _sessionEditController.Replace(runContext.SessionEdits);
                                });
                                toolResult = await _toolExecutionController.ExecuteAsync(currentToolName, currentArguments, cancellationToken);
                                runContext.SessionEdits = _sessionEditController.SessionEdits.ToList();
                                _openSessionController.EnsureSession(runContext.SessionId).SessionEdits = runContext.SessionEdits.ToList();
                                if (!_runOutputController.IsSessionVisible(runContext.SessionId))
                                {
                                    await _uiDispatcher.RunAsync(() =>
                                    {
                                        var visibleSession = _openSessionController.EnsureSession(_currentSessionId);
                                        _sessionEditController.Replace(visibleSession.SessionEdits);
                                    });
                                }
                            }
                            finally
                            {
                                _activeToolRunContext.Value = null;
                                _toolExecutionSessionGate.Release();
                            }
                            toolResultForTranscript = toolResult;
                            if (IsSuccessfulToolResult(toolResult))
                            {
                                if (IsMutatingTool(normalizedToolName) ||
                                    string.Equals(normalizedToolName, "run_powershell", StringComparison.Ordinal))
                                {
                                    successfulToolResults.Clear();
                                }

                                if (ShouldSkipDuplicateSuccessfulTool(normalizedToolName))
                                {
                                    successfulToolResults[toolInvocationKey] = toolResult;
                                }
                            }
                        }

                        if (IsSuccessfulToolResult(toolResult))
                        {
                            if (!skippedDuplicateTool)
                            {
                                toolResultForTranscript = $"{toolResult}\n\n[Tool execution status: success.]";
                            }
                        }
                        else
                        {
                            successfulToolResults.Remove(toolInvocationKey);
                        }

                        toolResultForTranscript = _runTranscriptService.AddToolTimingNote(
                            normalizedToolName,
                            toolResultForTranscript,
                            toolResult);

                        toolCallResults.Add((currentToolName, currentArguments, toolResult, toolResultForTranscript, skippedDuplicateTool, normalizedToolName));

                        if (!IsSuccessfulToolResult(toolResult) || stopAfterLoopGuard)
                        {
                            break;
                        }
                    }

                    cancellationToken.ThrowIfCancellationRequested();

                    await _uiDispatcher.RunAsync(() =>
                    {
                        string refreshedContext = _promptContextService.BuildWorkspaceContext(
                            instruction,
                            currentRunActiveTab,
                            currentRunSelectionSnapshot,
                            runContext.Attachments,
                            runContext.WorkspaceRoot);
                        var addedPartBuilder = new StringBuilder();
                        addedPartBuilder.AppendLine();
                        addedPartBuilder.AppendLine();
                        addedPartBuilder.AppendLine("[Agent tool call]");
                        addedPartBuilder.AppendLine(response);

                        foreach (var tcRes in toolCallResults)
                        {
                            addedPartBuilder.AppendLine();
                            addedPartBuilder.AppendLine($"[Tool result: {tcRes.Name}]");
                            addedPartBuilder.AppendLine(tcRes.ResultForTranscript);
                        }

                        if (!string.Equals(refreshedContext, lastWorkspaceContext, StringComparison.Ordinal))
                        {
                            addedPartBuilder.AppendLine();
                            addedPartBuilder.AppendLine("[Current workspace context snapshot]");
                            addedPartBuilder.AppendLine(refreshedContext);
                            lastWorkspaceContext = refreshedContext;
                        }

                        string addedPart = addedPartBuilder.ToString();
                        transcript += addedPart;
                        runContext.CurrentRunTranscriptTokens += AgentTokenEstimator.Estimate(addedPart);
                        UpdateContextStatsImmediate(force: true);
                    });
                    
                    foreach (var tcRes in toolCallResults)
                    {
                        string displayResult = _toolExecutionController.FormatDisplayResult(
                            tcRes.NormalizedName,
                            tcRes.Args,
                            tcRes.Result,
                            tcRes.Skipped,
                            runContext.LlmSettings.LlmAgentVerbose);

                        string outputHeader = tcRes.Skipped
                            ? _getString("AgentDuplicateToolSkipped", "도구 중복 호출 건너뜀")
                            : _getString("AgentToolRunning", "도구 실행 중");
                        await _runOutputController.AppendRunOutputLineAsync(runContext, $"{outputHeader}: {tcRes.Name}");
                        await _runOutputController.AppendRunOutputTextAsync(runContext, displayResult.TrimEnd() + Environment.NewLine);
                    }

                    var makePlanRes = toolCallResults.FirstOrDefault(tc => tc.NormalizedName == "make_plan" && IsSuccessfulToolResult(tc.Result));
                    if (planningMode && makePlanRes.Name != null)
                    {
                        approvedPlanExecutionPrompt = await _planController.WaitForSavedPlanApprovalAsync(
                            runContext,
                            userInstruction,
                            cancellationToken);
                        if (!string.IsNullOrWhiteSpace(approvedPlanExecutionPrompt))
                        {
                            approvedPlanWorkspaceRoot = runContext.WorkspaceRoot;
                        }

                        AppendRunSessionHistoryLine($"[User Prompt]: {instruction}");
                        string makePlanRunTranscript = transcript.Substring(initialTranscript.Length);
                        if (!string.IsNullOrWhiteSpace(makePlanRunTranscript))
                        {
                            AppendRunSessionHistoryLine(makePlanRunTranscript.Trim());
                        }
                        AppendRunSessionHistoryLine("[Agent Response]: Plan saved for user review.");
                        AppendRunSessionHistoryLine();
                        _ = PersistRunSessionToHistoryAsync();

                        completed = true;
                        break;
                    }

                    if (stopAfterLoopGuard)
                    {
                        string loopMessage = _getString(
                            "AgentLoopGuardStopped",
                            "동일한 도구 호출 반복 루프가 감지되어 Agent 실행을 중단했습니다. 출력된 결과를 확인한 뒤 다시 실행해 주세요.");
                        await _runOutputController.AppendRunActivityAsync(runContext, loopMessage);
                        await _runOutputController.AppendRunOutputLineAsync(runContext, loopMessage);

                        AppendRunSessionHistoryLine($"[User Prompt]: {instruction}");
                        string loopRunTranscript = transcript.Substring(initialTranscript.Length);
                        if (!string.IsNullOrWhiteSpace(loopRunTranscript))
                        {
                            AppendRunSessionHistoryLine(loopRunTranscript.Trim());
                        }
                        AppendRunSessionHistoryLine($"[Agent Response]: {loopMessage}");
                        AppendRunSessionHistoryLine();
                        _ = PersistRunSessionToHistoryAsync();

                        completed = true;
                        break;
                    }
                    if (toolCalls.Count == 1)
                    {
                        var tcRes = toolCallResults[0];
                        bool isFileEditTool = tcRes.NormalizedName is "replace_in_file" or "search_replace" or "replace_range"
                            or "apply_patch" or "overwrite_file" or "append_to_file";
                        if (isFileEditTool && IsUnchangedEditCompletionResult(tcRes.Result))
                        {
                            string completeMsg = _getString(
                                "AgentFileEditAlreadyComplete",
                                "요청한 작업을 완료하였습니다.");

                            transcript += "\n\n[File edit verification: requested content already matches the current file. Task complete.]";

                            await _runOutputController.AppendRunOutputLineAsync(runContext, completeMsg);

                            AppendRunSessionHistoryLine($"[User Prompt]: {instruction}");
                            string unchangedRunTranscript = transcript.Substring(initialTranscript.Length);
                            if (!string.IsNullOrWhiteSpace(unchangedRunTranscript))
                            {
                                AppendRunSessionHistoryLine(unchangedRunTranscript.Trim());
                            }
                            AppendRunSessionHistoryLine();
                            _ = PersistRunSessionToHistoryAsync();

                            completed = true;
                            break;
                        }
                    }

                    if (step == maxToolSteps - 1)
                    {
                        reachedToolStepLimit = true;
                    }
                }

                if (!completed && reachedToolStepLimit)
                {
                    string limitMsg = _getString(
                        "AgentToolStepLimitReached",
                        "도구 호출 한도에 도달해 작업을 중단했습니다. 지금까지의 결과를 검토한 뒤 다시 실행해 주세요.");

                    await _runOutputController.AppendRunActivityAsync(runContext, limitMsg);
                    await _runOutputController.AppendRunOutputLineAsync(runContext, limitMsg);

                    AppendRunSessionHistoryLine($"[User Prompt]: {instruction}");
                    string runTranscript = transcript.Substring(initialTranscript.Length);
                    if (!string.IsNullOrWhiteSpace(runTranscript))
                    {
                        AppendRunSessionHistoryLine(runTranscript.Trim());
                    }
                    AppendRunSessionHistoryLine("[Agent Response]: Tool step limit reached before a final answer.");
                    AppendRunSessionHistoryLine();
                    _ = PersistRunSessionToHistoryAsync();
                }
            }
            catch (OperationCanceledException)
            {
                await _runOutputController.AppendRunActivityAsync(runContext, _getString("AgentActivityStopped", "중단됨"));
                await _runOutputController.AppendRunOutputLineAsync(runContext, _getString("AgentOutputStopped", "Agent 실행이 중단되었습니다."));

                AppendRunSessionHistoryLine($"[User Prompt]: {instruction}");
                string runTranscript = transcript.Substring(initialTranscript.Length);
                if (!string.IsNullOrWhiteSpace(runTranscript))
                {
                    AppendRunSessionHistoryLine(runTranscript.Trim());
                }
                AppendRunSessionHistoryLine("[Agent Response]: Agent execution was interrupted by the user.");
                AppendRunSessionHistoryLine();
                _ = PersistRunSessionToHistoryAsync();
            }
            catch (Exception ex)
            {
                await _runOutputController.AppendRunOutputLineAsync(runContext, string.Format(
                    _getString("AgentExceptionFormat", "Agent 실행 도중 예외가 발생했습니다: {0}"),
                    ex.Message));
            }
            finally
            {
                _activeWorkspaceRunContext.Value = null;
                activeOpenSession.IsRunning = false;
                _runningSessions.Remove(runContext.SessionId);
                _isRunning = IsAnySessionRunning();
                _selectionContextController.ClearRunSnapshots();
                _fileToolController.FinishRun();
                if (ReferenceEquals(_runCancellation, cancellationSource))
                {
                    _runCancellation = null;
                }

                cancellationSource.Dispose();
                await _uiDispatcher.RunAsync(async () => await _runOutputController.FinishStreamToTabAsync(runContext));
                activeOpenSession.SessionHistoryText = runContext.SessionHistory.ToString();
                activeOpenSession.SessionHistoryTokenCount = runContext.SessionHistoryTokenCount;
                activeOpenSession.CurrentRunTranscriptTokens = runContext.CurrentRunTranscriptTokens;
                activeOpenSession.SessionEdits = runContext.SessionEdits.ToList();
                activeOpenSession.Attachments = runContext.Attachments.ToList();
                activeOpenSession.LastAnswerText = runContext.LastAnswerText;
                activeOpenSession.WorkspaceRoot = runContext.WorkspaceRoot;
                _openSessionController.ClearThinkingState(activeOpenSession);
                if (_runOutputController.IsSessionVisible(runContext.SessionId))
                {
                    RestoreSessionHistoryState(
                        activeOpenSession.SessionHistoryText,
                        activeOpenSession.SessionHistoryTokenCount,
                        activeOpenSession.CurrentRunTranscriptTokens);
                }
                _openSessionController.UpdateActiveSessionBusyState();

                if (_openSessionController.IsPendingClose(runContext.SessionId))
                {
                    string closingSessionId = _openSessionController.ConsumePendingCloseSessionId();
                    _openSessionController.CloseSession(closingSessionId);
                }
                else
                {
                    UpdateContextStatsImmediate();
                    _openSessionController.UpdateUI();
                }
            }

            if (!string.IsNullOrWhiteSpace(approvedPlanExecutionPrompt))
            {
                await StartApprovedPlanSessionAsync(approvedPlanExecutionPrompt, approvedPlanWorkspaceRoot);
            }
        }

        private async Task StartApprovedPlanSessionAsync(string executionPrompt, string workspaceRoot)
        {
            await _uiDispatcher.RunAsync(() =>
            {
                _openSessionController.SaveActiveFromUI();

                var session = _openSessionController.EnsureSession(Guid.NewGuid().ToString());
                session.Title = _getString("AgentPlanExecutionSessionTitle", "계획 실행");
                session.PromptText = executionPrompt;
                session.OutputText = _displayText.OutputPlaceholder;
                session.ActivityText = _displayText.ActivityIdle;
                session.SessionHistoryText = string.Empty;
                session.LastAnswerText = string.Empty;
                session.SessionHistoryTokenCount = 0;
                session.CurrentRunTranscriptTokens = 0;
                session.Attachments.Clear();
                session.SessionEdits.Clear();
                session.RewindSnapshots.Clear();
                session.WorkspaceRoot = string.IsNullOrWhiteSpace(workspaceRoot) ? _fileTools.WorkspaceRoot : workspaceRoot;
                session.LlmSettings = _openSessionController.CreateSessionSettingsSnapshot();
                session.UpdatedAt = DateTime.Now;

                _agentPane.PlanningModeCheckBox.IsChecked = false;
                _openSessionController.RestoreSession(session);
            });

            await _uiDispatcher.RunAsync(async () => await RunAgentAsync());
        }

        private void StopAgent()
        {
            StopAgent(_currentSessionId);
        }

        private void StopAgent(string sessionId)
        {
            if (!_runningSessions.TryGetValue(sessionId, out AgentRunContext? context))
            {
                return;
            }

            if (context.Cancellation?.IsCancellationRequested == true)
            {
                return;
            }

            _ = _runOutputController.AppendRunActivityAsync(context, _getString("AgentActivityStopRequested", "중단 요청됨"));

            context.Cancellation?.Cancel();
            _confirmationController.CancelPending();
        }

        private string BuildRunHeader(string instruction)
        {
            string modeText = _getString("AgentModeRun", "실행");
            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            return $"{timestamp}  Agent {modeText}: {TruncateForActivity(instruction)}";
        }

        private static string BuildLastAnswerText(string response, string cleanResponse, bool verbose)
        {
            string answer = verbose ? response : cleanResponse;
            if (string.IsNullOrWhiteSpace(answer))
            {
                answer = response;
            }

            return (answer ?? string.Empty).Trim();
        }

        private void AddCurrentRunImageToolAttachment(LlmMessageAttachment attachment)
        {
            AgentRunContext? context = _activeToolRunContext.Value;
            if (context == null)
            {
                _currentRunImageToolAttachments.RemoveAll(existing =>
                    string.Equals(existing.DisplayName, attachment.DisplayName, StringComparison.OrdinalIgnoreCase));
                _currentRunImageToolAttachments.Add(attachment);
                return;
            }

            context.ImageToolAttachments.RemoveAll(existing =>
                string.Equals(existing.DisplayName, attachment.DisplayName, StringComparison.OrdinalIgnoreCase));
            context.ImageToolAttachments.Add(attachment);
        }

        private void AppendActivity(string message)
        {
            AgentRunContext? context = _activeToolRunContext.Value ?? _activeWorkspaceRunContext.Value;
            if (context != null)
            {
                _ = _runOutputController.AppendRunActivityAsync(context, message);
                return;
            }

            _openSessionController.AppendActivityToCurrentSession(message);
        }

        public void UpdateContextStats()
        {
            UpdateContextStatsAfterDelay(DefaultContextStatsDelayMs);
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
            _statsDebounceTimer.Interval = TimeSpan.FromMilliseconds(delayMilliseconds);
            _statsDebounceTimer.Start();
        }

        private double GetCurrentRunTranscriptTokens()
        {
            var activeRunContext = _activeToolRunContext.Value ?? _activeWorkspaceRunContext.Value;
            if (activeRunContext != null)
            {
                return activeRunContext.CurrentRunTranscriptTokens;
            }
            return _currentRunTranscriptTokens;
        }

        private void UpdateContextStatsImmediate(bool force = false)
        {
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
