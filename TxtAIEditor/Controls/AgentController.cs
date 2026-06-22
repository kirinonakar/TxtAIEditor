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
        private const int FallbackSessionHistoryPromptChars = 80_000;
        private const double PromptContextSafetyRatio = 0.95;
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
        private readonly AgentWorkspaceContextBuilder _workspaceContextBuilder;
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
                () => _currentRunTranscriptTokens,
                RestoreSessionHistoryState,
                StopAgent,
                () => UpdateContextStatsImmediate(),
                _getString,
                _navigateToFolderAsync);
            _runOutputController = new AgentRunOutputController(
                _openSessionController,
                insertIntoActiveEditorAsync,
                beginStreamIntoActiveEditorAsync,
                streamTextIntoActiveEditorAsync,
                endStreamIntoActiveEditorAsync);
            _sessionRewindController = new AgentSessionRewindController(
                IsCurrentSessionRunning,
                _toolExecutionSessionGate,
                () => _currentSessionId,
                SaveActiveOpenSessionFromUI,
                EnsureOpenSession,
                RestoreOpenSession,
                _openSessionController.ClearThinkingState,
                _sessionEditController,
                _historyController,
                _displayText,
                _showError,
                _getString);
            _workspaceContextBuilder = new AgentWorkspaceContextBuilder(
                () => _fileTools.WorkspaceRoot,
                openTabsProvider,
                _attachmentController);
            _contextStatsController = new AgentContextStatsController(
                _settingsService,
                _agentPane,
                _displayText,
                _attachmentController,
                IsCurrentSessionRunning,
                GetActiveTabForContext,
                GetActiveSelectionText,
                BuildActiveSelectionContext,
                BuildAgentInstruction,
                BuildWorkspaceContext,
                BuildSessionHistoryForPrompt,
                () => _currentRunTranscriptTokens,
                RefreshOutputDisplay,
                _getString,
                _modelContextLimits,
                GetCurrentSessionSettings);
            _outputInsertController = new AgentOutputInsertController(
                _agentPane,
                insertIntoActiveEditorAsync,
                openNewTabWithContent,
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
            EnsureOpenSession(_currentSessionId);
            UpdateOpenSessionsUI();
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
            _agentPane.NewSessionRequested += (_, _) => CreateNewOpenSession();
            _agentPane.RewindSessionRequested += async (_, _) => await _sessionRewindController.RewindCurrentSessionAsync();
            _agentPane.OpenSessionsFlyoutOpened += (_, _) =>
            {
                SaveOpenSessionPromptTitleFromUI();
                UpdateOpenSessionsUI();
            };
            _agentPane.OpenSessionSelected += (_, sessionId) => SwitchOpenSession(sessionId);
            _agentPane.OpenSessionClosed += (_, sessionId) => CloseOpenSession(sessionId);
            _agentPane.HistorySelected += (_, historyId) => LoadHistorySession(historyId);
            _agentPane.HistoryDeleted += async (_, historyId) => await DeleteHistorySessionAsync(historyId);
            _agentPane.HistoryToolbarDeleteClicked += async (_, _) => await ClearAllHistoryAsync();
            _agentPane.InsertOutputRequested += async (_, _) => await _outputInsertController.InsertOutputAsync();
            _agentPane.InsertNewTabOutputRequested += async (_, _) => await _outputInsertController.InsertNewTabOutputAsync();
            _agentPane.AddAttachmentRequested += async (_, _) => await _attachmentController.AddAttachmentsAsync();
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
                SaveOpenSessionPromptTitleFromUI();
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
            string instruction = BuildAgentInstruction(requestedPlanningMode
                ? AgentPlanController.BuildPlanningModeRequest(userInstruction)
                : userInstruction);
            if (string.IsNullOrWhiteSpace(instruction))
            {
                _showError(
                    _getString("AgentErrorTitle", "Agent 오류"),
                    _getString("AgentEmptyPrompt", "Agent에게 맡길 작업을 입력해 주세요."));
                return;
            }

            var activeOpenSession = EnsureOpenSession(_currentSessionId);
            string preservedWorkspaceRoot = activeOpenSession.WorkspaceRoot;
            SaveActiveOpenSessionFromUI();
            activeOpenSession.RewindSnapshots.Add(AgentSessionRewindSnapshot.Capture(activeOpenSession));
            UpdateOpenSessionTitle(activeOpenSession, userInstruction);
            activeOpenSession.PromptText = _agentPane.Prompt.Text ?? string.Empty;
            activeOpenSession.UpdatedAt = DateTime.Now;
            activeOpenSession.IsRunning = true;
            activeOpenSession.WorkspaceRoot = ResolveWorkspaceRootForRun(
                preservedWorkspaceRoot,
                activeOpenSession.WorkspaceRoot,
                userInstruction);
            EditorSettings runSettings = await ResolveRunSessionSettingsAsync(activeOpenSession);

            var runContext = new AgentRunContext
            {
                SessionId = activeOpenSession.Id,
                SessionHistoryTokenCount = activeOpenSession.SessionHistoryTokenCount,
                CurrentRunTranscriptTokens = 0,
                Attachments = activeOpenSession.Attachments.ToList(),
                SessionEdits = activeOpenSession.SessionEdits.ToList(),
                StreamToTab = _agentPane.StreamToTab,
                WorkspaceRoot = activeOpenSession.WorkspaceRoot,
                LlmSettings = runSettings,
                IsPlanningMode = requestedPlanningMode,
                OriginalUserInstruction = userInstruction
            };
            runContext.SessionHistory.Append(activeOpenSession.SessionHistoryText ?? string.Empty);
  
            _isRunning = true;
            _runningSessions[activeOpenSession.Id] = runContext;
            UpdateOpenSessionsUI();
            _agentPane.HideHtmlCodeBlocks = !_settingsService.CurrentSettings.LlmAgentVerbose;
            _fileToolController.StartRun();
            _currentRunImageToolAttachments.Clear();
            _selectionContextController.ClearRunSnapshots();
            _currentRunTranscriptTokens = 0;
            var cancellationSource = new CancellationTokenSource();
            runContext.Cancellation = cancellationSource;
            _runCancellation = cancellationSource;
            CancellationToken cancellationToken = cancellationSource.Token;
            UpdateActiveSessionBusyState();
            await _runOutputController.ClearRunActivityAsync(runContext, _getString("AgentActivityStarting", "시작 중"));
            await _runOutputController.BeginRunOutputBlockAsync(runContext, BuildRunHeader(BuildInstructionDisplay(userInstruction)));
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
                return SaveRunSessionToHistoryAsync(runContext, userInstruction);
            }

            _activeWorkspaceRunContext.Value = runContext;
            try
            {
                OpenedTab? currentRunActiveTab = await CaptureActiveTabForRunAsync();
                AgentSelectionSnapshot currentRunSelectionSnapshot = _selectionContextController.CaptureSelectionForRun(_isRunning);
                runContext.StreamToTabTargetTabId = currentRunActiveTab?.Id;
                _fileToolController.SetRunContext(currentRunSelectionSnapshot, currentRunActiveTab);
                _fileToolController.SetRestrictEditsToSelection(
                    currentRunSelectionSnapshot.HasLineRange &&
                    !UserRequestAllowsEditsOutsideSelection(instruction));
                string workspaceContext = BuildWorkspaceContext(
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
                string sessionHistoryForPrompt = BuildSessionHistoryForPrompt(
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
                int toolCallFormatRetryCount = 0;
                const int maxToolCallFormatRetries = 2;
                int makePlanRetryCount = 0;
                const int maxMakePlanRetries = 2;
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
                    string thinkingLabel = _getString("AgentActivityThinking", "생각중");
                    await _runOutputController.BeginRunThinkingActivityAsync(runContext, thinkingLabel);

                    var responseBuilder = new StringBuilder();
                    int printedLength = 0;
                    bool toolCallPlaceholderShown = false;
                    bool visibleTextFlushed = false;
                    bool heldPotentialToolCallText = false;
                    bool? isJsonToolCall = null;
                    bool hasToolCall = false;
                    bool suppressStreamingText = planningMode;

                    string currentTranscript = _runTranscriptService.BuildWithEditLedger(
                        transcript,
                        currentTaskStartEditIndex,
                        runContext.SessionEdits);

                    response = await _llmService.RunAgentAsync(
                        runContext.LlmSettings,
                        instruction,
                        currentTranscript,
                        runSelectionContext,
                        "run",
                        async chunk =>
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            responseBuilder.Append(chunk);
                            string streamedText = responseBuilder.ToString();

                            if (isJsonToolCall == null)
                            {
                                string trimmed = streamedText.TrimStart();
                                if (!string.IsNullOrEmpty(trimmed))
                                {
                                    isJsonToolCall = LooksLikeStreamedToolCallEnvelopeStart(trimmed);
                                    if (isJsonToolCall.Value)
                                    {
                                        toolCallPlaceholderShown = true;
                                        if (!runContext.LlmSettings.LlmAgentVerbose)
                                        {
                                            heldPotentialToolCallText = true;
                                            int tokenCount = (int)Math.Round(AgentTokenEstimator.Estimate(streamedText));
                                            string label = _displayText.FormatPreparingToolLabel(tokenCount);
                                            _runOutputController.EnqueueRunUi(
                                                runContext,
                                                () => _agentPane.BeginThinkingActivity(label),
                                                session => _openSessionController.BeginThinkingInSession(session, label));
                                            printedLength = streamedText.Length;
                                        }
                                        else
                                        {
                                            _runOutputController.EnqueueRunUi(
                                                runContext,
                                                () => _agentPane.AppendOutputText(streamedText),
                                                session => _openSessionController.AppendOutputTextToSession(session, streamedText));
                                            printedLength = streamedText.Length;
                                        }
                                    }
                                }
                            }

                            if (isJsonToolCall == true)
                            {
                                if (!runContext.LlmSettings.LlmAgentVerbose)
                                {
                                    int tokenCount = (int)Math.Round(AgentTokenEstimator.Estimate(streamedText));
                                    string label = _displayText.FormatPreparingToolLabel(tokenCount);
                                    _runOutputController.EnqueueRunUi(
                                        runContext,
                                        () => _agentPane.UpdateThinkingActivity(label),
                                        session => _openSessionController.UpdateThinkingInSession(session, label));
                                    printedLength = streamedText.Length;
                                }
                                else
                                {
                                    _runOutputController.EnqueueRunUi(
                                        runContext,
                                        () => _agentPane.AppendOutputText(chunk),
                                        session => _openSessionController.AppendOutputTextToSession(session, chunk));
                                    printedLength = streamedText.Length;
                                }
                                return;
                            }

                            if (hasToolCall)
                            {
                                if (!runContext.LlmSettings.LlmAgentVerbose)
                                {
                                    int idx = streamedText.IndexOf("<tool_call>", StringComparison.OrdinalIgnoreCase);
                                    string toolCallText = idx >= 0 ? streamedText.Substring(idx) : streamedText;
                                    int tokenCount = (int)Math.Round(AgentTokenEstimator.Estimate(toolCallText));
                                    string label = _displayText.FormatPreparingToolLabel(tokenCount);
                                    _runOutputController.EnqueueRunUi(
                                        runContext,
                                        () => _agentPane.UpdateThinkingActivity(label),
                                        session => _openSessionController.UpdateThinkingInSession(session, label));
                                }
                                else
                                {
                                    _runOutputController.EnqueueRunUi(
                                        runContext,
                                        () => _agentPane.AppendOutputText(chunk),
                                        session => _openSessionController.AppendOutputTextToSession(session, chunk));
                                    printedLength = streamedText.Length;
                                }
                                return;
                            }

                            int checkStart = Math.Max(0, streamedText.Length - chunk.Length - 11);
                            int toolCallIndex = streamedText.IndexOf("<tool_call>", checkStart, StringComparison.OrdinalIgnoreCase);
                            if (toolCallIndex >= 0)
                            {
                                hasToolCall = true;
                                if (printedLength < toolCallIndex)
                                {
                                    string textToPrint = streamedText.Substring(printedLength, toolCallIndex - printedLength);
                                    if (!suppressStreamingText)
                                    {
                                        visibleTextFlushed = true;
                                        await _runOutputController.AppendOutputTextAndStreamToTabAsync(runContext, textToPrint);
                                    }
                                    printedLength = toolCallIndex;
                                }

                                if (!runContext.LlmSettings.LlmAgentVerbose)
                                {
                                    if (!toolCallPlaceholderShown)
                                    {
                                        toolCallPlaceholderShown = true;
                                        int tokenCount = (int)Math.Round(AgentTokenEstimator.Estimate(streamedText.Substring(toolCallIndex)));
                                        string label = _displayText.FormatPreparingToolLabel(tokenCount);
                                        _runOutputController.EnqueueRunUi(
                                            runContext,
                                            () => _agentPane.BeginThinkingActivity(label),
                                            session => _openSessionController.BeginThinkingInSession(session, label));
                                    }
                                }
                                else
                                {
                                    string toolCallText = streamedText.Substring(toolCallIndex);
                                    _runOutputController.EnqueueRunUi(
                                        runContext,
                                        () => _agentPane.AppendOutputText(toolCallText),
                                        session => _openSessionController.AppendOutputTextToSession(session, toolCallText));
                                    printedLength = streamedText.Length;
                                }
                            }
                            else
                            {
                                int holdBack = 0;
                                string tag = "<tool_call>";
                                for (int i = 1; i < tag.Length; i++)
                                {
                                    string sub = tag.Substring(0, i);
                                    if (streamedText.EndsWith(sub, StringComparison.OrdinalIgnoreCase))
                                    {
                                        holdBack = i;
                                        break;
                                    }
                                }

                                int safeLength = streamedText.Length - holdBack;
                                if (printedLength < safeLength)
                                {
                                    string textToPrint = streamedText.Substring(printedLength, safeLength - printedLength);
                                    if (!suppressStreamingText)
                                    {
                                        visibleTextFlushed = true;
                                        await _runOutputController.AppendOutputTextAndStreamToTabAsync(runContext, textToPrint);
                                    }
                                    printedLength = safeLength;
                                }
                            }
                            await Task.CompletedTask;
                        },
                        cancellationToken,
                        GetImageAttachmentsForRun(runContext),
                        planningMode);

                    cancellationToken.ThrowIfCancellationRequested();
                    response = responseBuilder.Length > 0 ? responseBuilder.ToString() : response;

                    if (string.IsNullOrWhiteSpace(response))
                    {
                        await _runOutputController.StopRunThinkingActivityAsync(runContext);

                        if (emptyResponseRetryCount < maxEmptyResponseRetries)
                        {
                            emptyResponseRetryCount++;
                            string retryNote =
                                "\n\n[Agent empty response]\n" +
                                "The model returned no visible content. Continue by writing exactly one tool_call or a final answer.";

                            await _uiDispatcher.RunAsync(() =>
                            {
                                transcript += retryNote;
                                runContext.CurrentRunTranscriptTokens += AgentTokenEstimator.Estimate(retryNote);
                                UpdateContextStatsImmediate(force: true);
                            });
                            await _runOutputController.AppendRunActivityAsync(runContext, _getString(
                                "AgentActivityEmptyResponseRetry",
                                "빈 응답을 수신해 다시 시도합니다."));

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

                        completed = true;
                        break;
                    }

                    await _runOutputController.StopRunThinkingActivityAsync(runContext);

                    bool responseHasToolSyntax = AgentToolCallParser.ContainsToolCallSyntax(response);

                    int endLength = response.Length;
                    if (!runContext.LlmSettings.LlmAgentVerbose)
                    {
                        int toolCallIndex = response.IndexOf("<tool_call>", StringComparison.OrdinalIgnoreCase);
                        if (toolCallIndex >= 0)
                        {
                            endLength = toolCallIndex;
                        }
                    }

                    if (!planningMode && !responseHasToolSyntax && heldPotentialToolCallText && !visibleTextFlushed && !string.IsNullOrEmpty(response))
                    {
                        visibleTextFlushed = true;
                        await _runOutputController.AppendOutputTextAndStreamToTabAsync(runContext, response);
                    }
                    else if (!planningMode && !responseHasToolSyntax && suppressStreamingText && !string.IsNullOrEmpty(response))
                    {
                        visibleTextFlushed = true;
                        await _runOutputController.AppendOutputTextAndStreamToTabAsync(runContext, response);
                    }
                    else if (!planningMode && !responseHasToolSyntax && printedLength < endLength)
                    {
                        string remainingText = response.Substring(printedLength, endLength - printedLength);
                        visibleTextFlushed = true;
                        await _runOutputController.AppendOutputTextAndStreamToTabAsync(runContext, remainingText);
                    }

                    cancellationToken.ThrowIfCancellationRequested();

                    string toolName = string.Empty;
                    JsonElement arguments = default;
                    string toolCallFormatIssue = string.Empty;
                    bool hasToolCallFormatIssue = responseHasToolSyntax &&
                        AgentToolCallParser.TryGetToolCallFormatIssue(response, out toolCallFormatIssue);
                    if (hasToolCallFormatIssue ||
                        !AgentToolCallParser.TryParse(response, out toolName, out arguments))
                    {
                        if (responseHasToolSyntax)
                        {
                            toolCallFormatRetryCount++;
                            string retryNote = BuildToolCallFormatRetryNote(
                                hasToolCallFormatIssue
                                    ? toolCallFormatIssue
                                    : "The tool_call JSON could not be parsed.");
                            transcript += "\n\n" + retryNote;
                            runContext.CurrentRunTranscriptTokens += AgentTokenEstimator.Estimate(retryNote);

                            string retryMessage = _getString(
                                "AgentToolCallFormatRetry",
                                "도구 호출 형식이 섞여 다시 요청합니다.");
                            await _runOutputController.AppendRunActivityAsync(runContext, retryMessage);
                            await _runOutputController.AppendRunOutputLineAsync(runContext, retryMessage);
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
                                "Do not answer with the plan as plain text. Save it by replying with exactly one make_plan tool_call, using the Markdown plan as the markdown argument. Do not include a path or filename.";

                            transcript += retryNote;
                            runContext.CurrentRunTranscriptTokens += AgentTokenEstimator.Estimate(retryNote);

                            string retryMessage = _getString(
                                "AgentMakePlanRequired",
                                "계획 모드에서는 make_plan 도구로 계획서를 저장해야 합니다.");
                            await _runOutputController.AppendRunActivityAsync(runContext, retryMessage);
                            await _runOutputController.AppendRunOutputLineAsync(runContext, retryMessage);
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

                                completed = true;
                                break;
                            }

                            continue;
                        }

                        if (!visibleTextFlushed && !string.IsNullOrWhiteSpace(response))
                        {
                            await _runOutputController.AppendOutputTextAndStreamToTabAsync(runContext, response);
                        }

                        toolCallFormatRetryCount = 0;
                        await _runOutputController.AppendRunActivityAsync(runContext, _getString("AgentActivityFinalAnswer", "최종 응답 작성 완료"));
                        if (runContext.StreamToTab)
                        {
                            await _runOutputController.StreamTextToTabAsync(runContext, "\n");
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

                        completed = true;
                        break;
                    }

                    string normalizedToolName = NormalizeToolName(toolName);
                    string toolInvocationKey = BuildToolInvocationKey(normalizedToolName, arguments);
                    bool skippedDuplicateTool = false;
                    bool stopAfterDuplicateLoopGuard = false;
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

                        stopAfterDuplicateLoopGuard = repeatedDuplicateToolSkipCount >= maxRepeatedDuplicateToolSkips;
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
                            toolResult = await _toolExecutionController.ExecuteAsync(toolName, arguments, cancellationToken);
                            runContext.SessionEdits = _sessionEditController.SessionEdits.ToList();
                            EnsureOpenSession(runContext.SessionId).SessionEdits = runContext.SessionEdits.ToList();
                            if (!_runOutputController.IsSessionVisible(runContext.SessionId))
                            {
                                await _uiDispatcher.RunAsync(() =>
                                {
                                    var visibleSession = EnsureOpenSession(_currentSessionId);
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
                    }

                    cancellationToken.ThrowIfCancellationRequested();

                    await _uiDispatcher.RunAsync(() =>
                    {
                        string refreshedContext = BuildWorkspaceContext(
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
                        addedPartBuilder.AppendLine();
                        addedPartBuilder.AppendLine($"[Tool result: {toolName}]");
                        addedPartBuilder.AppendLine(toolResultForTranscript);
                        addedPartBuilder.AppendLine();
                        addedPartBuilder.AppendLine("[Current workspace context snapshot]");
                        if (string.Equals(refreshedContext, lastWorkspaceContext, StringComparison.Ordinal))
                        {
                            addedPartBuilder.AppendLine("Context summary unchanged since the previous snapshot. This only means compact workspace metadata did not change; use the tool result and [File edits made during this user task] to determine whether this run edited files.");
                        }
                        else
                        {
                            addedPartBuilder.AppendLine(refreshedContext);
                            lastWorkspaceContext = refreshedContext;
                        }

                        string addedPart = addedPartBuilder.ToString();
                        transcript += addedPart;
                        runContext.CurrentRunTranscriptTokens += AgentTokenEstimator.Estimate(addedPart);
                        UpdateContextStatsImmediate(force: true);
                    });
                    
                    string displayResult = _toolExecutionController.FormatDisplayResult(
                        normalizedToolName,
                        arguments,
                        toolResult,
                        skippedDuplicateTool,
                        runContext.LlmSettings.LlmAgentVerbose);

                    string outputHeader = skippedDuplicateTool
                        ? _getString("AgentDuplicateToolSkipped", "도구 중복 호출 건너뜀")
                        : _getString("AgentToolRunning", "도구 실행 중");
                    await _runOutputController.AppendRunOutputLineAsync(runContext, $"{outputHeader}: {toolName}");
                    await _runOutputController.AppendRunOutputTextAsync(runContext, displayResult.TrimEnd() + Environment.NewLine);

                    if (planningMode && normalizedToolName == "make_plan" && IsSuccessfulToolResult(toolResult))
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

                    if (stopAfterDuplicateLoopGuard)
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



                    string verifyToolName = NormalizeToolName(toolName);
                    bool isFileEditTool = verifyToolName is "replace_in_file" or "search_replace" or "replace_range"
                        or "apply_patch" or "overwrite_file" or "append_to_file";
                    if (isFileEditTool && IsUnchangedEditCompletionResult(toolResult))
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
                activeOpenSession.WorkspaceRoot = runContext.WorkspaceRoot;
                _openSessionController.ClearThinkingState(activeOpenSession);
                if (_runOutputController.IsSessionVisible(runContext.SessionId))
                {
                    RestoreSessionHistoryState(
                        activeOpenSession.SessionHistoryText,
                        activeOpenSession.SessionHistoryTokenCount,
                        activeOpenSession.CurrentRunTranscriptTokens);
                }
                UpdateActiveSessionBusyState();

                if (_openSessionController.IsPendingClose(runContext.SessionId))
                {
                    string closingSessionId = _openSessionController.ConsumePendingCloseSessionId();
                    CloseOpenSession(closingSessionId);
                }
                else
                {
                    UpdateContextStatsImmediate();
                    UpdateOpenSessionsUI();
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
                SaveActiveOpenSessionFromUI();

                var session = EnsureOpenSession(Guid.NewGuid().ToString());
                session.Title = _getString("AgentPlanExecutionSessionTitle", "계획 실행");
                session.PromptText = executionPrompt;
                session.OutputText = _displayText.OutputPlaceholder;
                session.ActivityText = _displayText.ActivityIdle;
                session.SessionHistoryText = string.Empty;
                session.SessionHistoryTokenCount = 0;
                session.CurrentRunTranscriptTokens = 0;
                session.Attachments.Clear();
                session.SessionEdits.Clear();
                session.RewindSnapshots.Clear();
                session.WorkspaceRoot = string.IsNullOrWhiteSpace(workspaceRoot) ? _fileTools.WorkspaceRoot : workspaceRoot;
                session.LlmSettings = CreateSessionSettingsSnapshot();
                session.UpdatedAt = DateTime.Now;

                _agentPane.PlanningModeCheckBox.IsChecked = false;
                RestoreOpenSession(session);
            });

            await _uiDispatcher.RunAsync(async () => await RunAgentAsync());
        }

        private AgentOpenSessionState EnsureOpenSession(string sessionId)
        {
            return _openSessionController.EnsureSession(sessionId);
        }

        private EditorSettings CreateSessionSettingsSnapshot()
        {
            return _openSessionController.CreateSessionSettingsSnapshot();
        }

        private Task<EditorSettings> ResolveRunSessionSettingsAsync(AgentOpenSessionState session)
        {
            return _openSessionController.ResolveRunSessionSettingsAsync(session);
        }

        private void SaveOpenSessionPromptTitleFromUI()
        {
            _openSessionController.SavePromptTitleFromUI();
        }

        private void SaveActiveOpenSessionFromUI()
        {
            _openSessionController.SaveActiveFromUI();
        }

        private void RestoreOpenSession(AgentOpenSessionState session)
        {
            _openSessionController.RestoreSession(session);
        }

        private void CreateNewOpenSession()
        {
            _agentPane.PlanningModeCheckBox.IsChecked = false;
            _openSessionController.CreateNewSession();
        }

        private void SwitchOpenSession(string sessionId)
        {
            _openSessionController.SwitchSession(sessionId);
        }

        private void CloseOpenSession(string sessionId)
        {
            _openSessionController.CloseSession(sessionId);
        }

        private void UpdateOpenSessionsUI()
        {
            _openSessionController.UpdateUI();
        }

        private void UpdateOpenSessionTitle(AgentOpenSessionState session, string prompt)
        {
            _openSessionController.UpdateSessionTitle(session, prompt);
        }

        private void UpdateActiveSessionBusyState()
        {
            _openSessionController.UpdateActiveSessionBusyState();
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

        private static string BuildToolCallFormatRetryNote(string detail)
        {
            var builder = new StringBuilder();
            builder.AppendLine("[Agent tool call format error]");
            builder.AppendLine("The previous assistant response was not executed.");
            builder.AppendLine("A tool turn must be exactly one <tool_call>...</tool_call> tag and no other text.");
            builder.AppendLine("Progress updates, plans, markdown, and explanations must be in a separate plain-text response with no tool_call tag.");
            builder.AppendLine("Re-emit only the tool_call now, or write the final answer with no tool_call tag.");
            if (!string.IsNullOrWhiteSpace(detail))
            {
                builder.AppendLine($"Parser detail: {detail}");
            }

            return builder.ToString().TrimEnd();
        }

        private static bool LooksLikeStreamedToolCallEnvelopeStart(string trimmed)
        {
            if (trimmed.StartsWith("{", StringComparison.Ordinal))
            {
                return true;
            }

            if (!trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                return false;
            }

            string fenceInfo = trimmed.Substring(3).TrimStart();
            return StartsWithFenceLanguage(fenceInfo, "json") ||
                StartsWithFenceLanguage(fenceInfo, "jsonc") ||
                StartsWithFenceLanguage(fenceInfo, "tool_call") ||
                StartsWithFenceLanguage(fenceInfo, "tool-call");
        }

        private static bool StartsWithFenceLanguage(string fenceInfo, string language)
        {
            if (!fenceInfo.StartsWith(language, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (fenceInfo.Length == language.Length)
            {
                return true;
            }

            return char.IsWhiteSpace(fenceInfo[language.Length]);
        }

        private string BuildInstructionDisplay(string userInstruction)
        {
            var labels = new List<string>();
            string presetLabel = _presetController.GetSelectedPresetLabel();
            if (!string.IsNullOrEmpty(presetLabel))
            {
                labels.Add(presetLabel);
            }

            string mcpLabel = _mcpController.GetSelectedMcpLabel();
            if (!string.IsNullOrEmpty(mcpLabel))
            {
                labels.Add(string.Format(_getString("AgentMcpDisplayLabelFormat", "MCP: {0}"), mcpLabel));
            }

            string skillLabel = _skillController.GetSelectedSkillLabel();
            if (!string.IsNullOrEmpty(skillLabel))
            {
                labels.Add(string.Format(_getString("AgentSkillDisplayLabelFormat", "Skill: {0}"), skillLabel));
            }

            if (labels.Count == 0)
            {
                return userInstruction;
            }

            string prefix = $"[{string.Join(" · ", labels)}]";
            if (string.IsNullOrWhiteSpace(userInstruction))
            {
                return prefix;
            }

            return $"{prefix} {userInstruction}";
        }

        private string BuildAgentInstruction(string userInstruction)
        {
            string presetSection = _presetController.BuildSelectedPresetSection();
            string mcpSection = _mcpController.BuildSelectedMcpSection();
            string skillSection = _skillController.BuildSelectedSkillSection();
            string agentsMdSection = BuildWorkspaceAgentsMdSection();
            if (string.IsNullOrWhiteSpace(presetSection) &&
                string.IsNullOrWhiteSpace(mcpSection) &&
                string.IsNullOrWhiteSpace(skillSection) &&
                string.IsNullOrWhiteSpace(agentsMdSection))
            {
                return userInstruction;
            }

            var builder = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(agentsMdSection))
            {
                builder.AppendLine(agentsMdSection);
                builder.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(presetSection))
            {
                builder.AppendLine(presetSection);
                builder.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(mcpSection))
            {
                builder.AppendLine(mcpSection);
                builder.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(skillSection))
            {
                builder.AppendLine(skillSection);
                builder.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(userInstruction))
            {
                builder.AppendLine("[User request]");
                builder.Append(userInstruction);
            }

            return builder.ToString().Trim();
        }

        private string BuildWorkspaceAgentsMdSection()
        {
            if (!_agentPane.PlanningMode)
            {
                return string.Empty;
            }

            string workspaceRoot = _fileTools.WorkspaceRoot;
            if (string.IsNullOrWhiteSpace(workspaceRoot))
            {
                return string.Empty;
            }

            string agentsMdPath = Path.Combine(workspaceRoot, "AGENTS.md");
            if (!File.Exists(agentsMdPath))
            {
                return string.Empty;
            }

            try
            {
                string content = File.ReadAllText(agentsMdPath);
                if (string.IsNullOrWhiteSpace(content))
                {
                    return string.Empty;
                }

                var builder = new StringBuilder();
                builder.AppendLine("[Workspace agent rules]");
                builder.AppendLine($"Source: {agentsMdPath}");
                builder.AppendLine(content.Trim());
                return builder.ToString().Trim();
            }
            catch
            {
                return string.Empty;
            }
        }

        private string BuildSessionHistoryForPrompt(
            string instruction,
            string workspaceContext,
            string selectedText)
        {
            if (_sessionHistory.Length == 0)
            {
                return string.Empty;
            }

            string history = _sessionHistory.ToString();
            int contextLimit = GetModelContextLimitForPromptBudget();
            if (contextLimit <= 0)
            {
                if (IsLmStudioProvider())
                {
                    return string.Empty;
                }

                return BuildSessionHistoryTail(history, FallbackSessionHistoryPromptChars);
            }

            double maxPromptTokens = Math.Floor(contextLimit * PromptContextSafetyRatio);
            double basePromptTokens = EstimateAgentPromptTokens(
                instruction,
                workspaceContext,
                selectedText);
            double sessionHistoryWrapperTokens = AgentTokenEstimator.Estimate(
                "[Session History]" + Environment.NewLine +
                Environment.NewLine +
                "=================================" + Environment.NewLine + Environment.NewLine);
            double availableHistoryTokens = maxPromptTokens - basePromptTokens - sessionHistoryWrapperTokens;

            if (availableHistoryTokens <= 0)
            {
                return string.Empty;
            }

            int hardCharCap = Math.Min(history.Length, FallbackSessionHistoryPromptChars);
            string cappedHistory = BuildSessionHistoryTail(history, hardCharCap);
            if (AgentTokenEstimator.Estimate(cappedHistory) <= availableHistoryTokens)
            {
                return cappedHistory;
            }

            return TrimSessionHistoryToTokenBudget(history, hardCharCap, availableHistoryTokens);
        }

        private string BuildSessionHistoryTail(string history, int maxChars)
        {
            if (string.IsNullOrEmpty(history) || maxChars <= 0)
            {
                return string.Empty;
            }

            if (history.Length <= maxChars)
            {
                return history;
            }

            string tail = history.Substring(history.Length - maxChars);
            int firstLineBreak = tail.IndexOf('\n');
            if (firstLineBreak >= 0 && firstLineBreak + 1 < tail.Length)
            {
                tail = tail.Substring(firstLineBreak + 1);
            }

            return "[... earlier session history omitted to keep the prompt compact ...]" +
                Environment.NewLine +
                tail;
        }

        private string TrimSessionHistoryToTokenBudget(
            string history,
            int maxChars,
            double tokenBudget)
        {
            string best = string.Empty;
            int low = 1;
            int high = Math.Min(history.Length, maxChars);

            while (low <= high)
            {
                int mid = low + ((high - low) / 2);
                string candidate = BuildSessionHistoryTail(history, mid);
                double candidateTokens = AgentTokenEstimator.Estimate(candidate);

                if (candidateTokens <= tokenBudget)
                {
                    best = candidate;
                    low = mid + 1;
                }
                else
                {
                    high = mid - 1;
                }
            }

            return best;
        }

        private double EstimateAgentPromptTokens(
            string instruction,
            string workspaceContext,
            string selectedText)
        {
            string languageCode = _displayText.LanguageCode;
            string systemPrompt = AgentPromptBuilder.BuildSystemPrompt(languageCode);
            string userContent = AgentPromptBuilder.BuildUserContent(
                instruction,
                workspaceContext,
                selectedText,
                string.Empty,
                languageCode);

            return AgentTokenEstimator.Estimate(systemPrompt) +
                AgentTokenEstimator.Estimate(userContent) +
                _attachmentController.EstimatedImageTokens;
        }

        private int GetModelContextLimitForPromptBudget()
        {
            return _modelContextLimits.GetContextLimit(
                GetCurrentSessionSettings(),
                () => _agentPane.DispatcherQueue.TryEnqueue(() => UpdateContextStatsImmediate(force: true)));
        }

        private bool IsLmStudioProvider()
        {
            string provider = GetCurrentSessionSettings().LlmProvider ?? string.Empty;
            return provider.Contains("lm studio", StringComparison.OrdinalIgnoreCase) ||
                provider.Contains("lmstudio", StringComparison.OrdinalIgnoreCase);
        }

        private string BuildWorkspaceContext(string instruction)
        {
            return _workspaceContextBuilder.Build(
                instruction,
                GetActiveTabForContext(),
                true,
                CaptureActiveSelectionSnapshot().HasLineRange);
        }

        private string BuildWorkspaceContext(
            string instruction,
            OpenedTab? activeTab,
            AgentSelectionSnapshot selectionSnapshot,
            IEnumerable<AgentAttachmentState> attachments,
            string? workspaceRootOverride = null)
        {
            return _workspaceContextBuilder.Build(
                instruction,
                activeTab,
                true,
                selectionSnapshot.HasLineRange,
                attachments,
                workspaceRootOverride);
        }

        private IReadOnlyList<LlmMessageAttachment> GetImageAttachmentsForRun(AgentRunContext context)
        {
            var attachments = new List<LlmMessageAttachment>();
            attachments.AddRange(context.Attachments
                .Select(attachment => attachment.ImageContent)
                .Where(attachment => attachment != null)
                .Cast<LlmMessageAttachment>());
            attachments.AddRange(context.ImageToolAttachments);
            return attachments;
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

        private void UpdateContextStatsImmediate(bool force = false)
        {
            _contextStatsController.Update(force);
        }

        public void UpdateModelDisplay(bool forceClearCache = false)
        {
            if (forceClearCache)
            {
                EnsureOpenSession(_currentSessionId).LlmSettings = CreateSessionSettingsSnapshot();
            }

            _contextStatsController.UpdateModelDisplay(forceClearCache);
        }

        private void AppendSessionHistory(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            _sessionHistory.Append(text);
            _sessionHistoryTokenCount += AgentTokenEstimator.Estimate(text);
        }

        private void AppendSessionHistoryLine(string line = "")
        {
            _sessionHistory.AppendLine(line);
            _sessionHistoryTokenCount += AgentTokenEstimator.Estimate(line + Environment.NewLine);
        }

        private async Task SaveCurrentSessionToHistoryAsync(string userInstruction)
        {
            if (_sessionHistory.Length == 0)
            {
                return;
            }

            var openSession = EnsureOpenSession(_currentSessionId);
            UpdateOpenSessionTitle(openSession, userInstruction);
            openSession.SessionHistoryText = _sessionHistory.ToString();
            openSession.SessionHistoryTokenCount = _sessionHistoryTokenCount;
            openSession.CurrentRunTranscriptTokens = _currentRunTranscriptTokens;
            openSession.SessionEdits = _sessionEditController.SessionEdits.ToList();
            openSession.Attachments = _attachmentController.GetState();
            openSession.UpdatedAt = DateTime.Now;
            UpdateOpenSessionsUI();

            var item = new AgentHistoryItem
            {
                Id = _currentSessionId,
                Timestamp = DateTime.Now,
                Title = openSession.Title,
                SessionHistoryText = _sessionHistory.ToString(),
                SessionHistoryTokenCount = _sessionHistoryTokenCount,
                SessionEdits = _sessionEditController.SessionEdits.ToList(),
                WorkspaceRoot = openSession.WorkspaceRoot
            };

            await _historyController.SaveSessionAsync(item, _currentSessionId);
        }

        private async Task SaveRunSessionToHistoryAsync(AgentRunContext context, string userInstruction)
        {
            if (context.SessionHistory.Length == 0)
            {
                return;
            }

            var openSession = EnsureOpenSession(context.SessionId);
            UpdateOpenSessionTitle(openSession, userInstruction);
            openSession.SessionHistoryText = context.SessionHistory.ToString();
            openSession.SessionHistoryTokenCount = context.SessionHistoryTokenCount;
            openSession.CurrentRunTranscriptTokens = context.CurrentRunTranscriptTokens;
            openSession.SessionEdits = context.SessionEdits.ToList();
            openSession.Attachments = context.Attachments.ToList();
            openSession.WorkspaceRoot = context.WorkspaceRoot;
            openSession.UpdatedAt = DateTime.Now;
            UpdateOpenSessionsUI();

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

        private void LoadHistorySession(string historyId)
        {
            if (IsCurrentSessionRunning()) return;

            var item = _historyController.GetSession(historyId);
            if (item == null) return;

            SaveActiveOpenSessionFromUI();

            var session = EnsureOpenSession(item.Id);
            session.Title = ResolveHistorySessionTitle(item);
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
                RestoreOpenSession(session);
            });
        }

        private string ResolveHistorySessionTitle(AgentHistoryItem item)
        {
            string untitled = _getString("AgentOpenSessionUntitled", "새 세션");
            string savedTitle = item.Title?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(savedTitle) &&
                !string.Equals(savedTitle, untitled, StringComparison.Ordinal) &&
                !string.Equals(savedTitle, "New Session", StringComparison.OrdinalIgnoreCase))
            {
                return savedTitle;
            }

            string extractedTitle = ExtractTitleFromSessionHistory(item.SessionHistoryText);
            if (!string.IsNullOrWhiteSpace(extractedTitle))
            {
                return extractedTitle;
            }

            return string.IsNullOrWhiteSpace(savedTitle) ? untitled : savedTitle;
        }

        private static string ExtractTitleFromSessionHistory(string historyText)
        {
            if (string.IsNullOrWhiteSpace(historyText))
            {
                return string.Empty;
            }

            string[] lines = historyText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (!line.StartsWith("[User Prompt]:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string inlinePrompt = line.Substring("[User Prompt]:".Length).Trim();
                if (!string.IsNullOrWhiteSpace(inlinePrompt) &&
                    !inlinePrompt.StartsWith("[Agent persona/instruction presets]", StringComparison.OrdinalIgnoreCase) &&
                    !inlinePrompt.StartsWith("[Enabled MCP servers]", StringComparison.OrdinalIgnoreCase) &&
                    !inlinePrompt.StartsWith("[Enabled agent skills]", StringComparison.OrdinalIgnoreCase))
                {
                    return inlinePrompt;
                }

                for (int j = i + 1; j < lines.Length; j++)
                {
                    if (!lines[j].StartsWith("[User request]", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    for (int k = j + 1; k < lines.Length; k++)
                    {
                        string requestLine = lines[k].Trim();
                        if (!string.IsNullOrWhiteSpace(requestLine))
                        {
                            return requestLine;
                        }
                    }
                }
            }

            return string.Empty;
        }

        private async Task DeleteHistorySessionAsync(string historyId)
        {
            if (string.IsNullOrEmpty(historyId)) return;

            await _historyController.DeleteAsync(historyId, _currentSessionId);

            if (string.Equals(_currentSessionId, historyId, StringComparison.Ordinal))
            {
                CloseOpenSession(historyId);
            }
            else
            {
                _historyController.UpdateUI(_currentSessionId);
            }
        }

        private async Task ClearAllHistoryAsync()
        {
            await _historyController.ClearAsync(_currentSessionId);
            CloseOpenSession(_currentSessionId);
        }

        private void RefreshOutputDisplay()
        {
            if (IsCurrentSessionRunning()) return;

            var session = EnsureOpenSession(_currentSessionId);
            string text = session.SessionHistoryText ?? string.Empty;
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            _agentPane.HideHtmlCodeBlocks = !_settingsService.CurrentSettings.LlmAgentVerbose;
            string formatted = AgentHistoryFormatter.Format(text, _settingsService.CurrentSettings.LlmAgentVerbose);
            session.OutputText = formatted;
            _agentPane.ResetOutput(formatted);
        }

    }
}
