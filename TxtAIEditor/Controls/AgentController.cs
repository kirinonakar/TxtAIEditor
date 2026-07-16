using System;
using System.Collections.Generic;
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

        private readonly ISettingsService _settingsService;
        private readonly AgentPane _agentPane;
        private readonly AgentUiDispatcher _uiDispatcher;
        private readonly Action<string, string> _showError;
        private readonly Func<string, string, string> _getString;
        private readonly AgentFileToolService _fileTools;
        private readonly Func<AgentFileEditPreview, Task> _openDiffViewAsync;
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
        private readonly AgentRunWorkspaceResolver _runWorkspaceResolver;
        private readonly AgentRunTextFormatter _runTextFormatter;
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
            _settingsService = settingsService;
            _agentPane = agentPane;
            _showError = showError;
            _getString = getString;
            _fileTools = fileTools;
            _openDiffViewAsync = openDiffViewAsync;

            var composition = AgentControllerComposition.Create(
                llmService,
                _settingsService,
                credentialService,
                _agentPane,
                activeTabProvider,
                openTabsProvider,
                getTabText,
                insertIntoActiveEditorAsync,
                openNewTabWithContent,
                _showError,
                _getString,
                _fileTools,
                pdfTextExtractionService,
                initializePickerWindow,
                isGitRepoProvider,
                _runningSessions,
                _toolExecutionSessionGate,
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
                    GetActiveRunWorkspaceRoot,
                    GetActiveTabForContext,
                    GetActiveSelectionText,
                    BuildActiveSelectionContext,
                    CaptureActiveSelectionSnapshot,
                    GetCurrentSessionSettings,
                    GetCurrentRunTranscriptTokens,
                    RestoreSessionHistoryState,
                    StopAgent,
                    AddCurrentRunImageToolAttachment,
                    AppendActivity,
                    () => _activeToolRunContext.Value ?? _activeWorkspaceRunContext.Value));

            _uiDispatcher = composition.UiDispatcher;
            _displayText = composition.DisplayText;
            _attachmentController = composition.AttachmentController;
            _presetController = composition.PresetController;
            _skillController = composition.SkillController;
            _mcpController = composition.McpController;
            _historyController = composition.HistoryController;
            _sessionEditController = composition.SessionEditController;
            _openSessionController = composition.OpenSessionController;
            _promptContextService = composition.PromptContextService;
            _outputInsertController = composition.OutputInsertController;
            _confirmationController = composition.ConfirmationController;
            _tabToolController = composition.TabToolController;
            _selectionContextController = composition.SelectionContextController;
            _fileToolController = composition.FileToolController;
            _toolExecutionController = composition.ToolExecutionController;
            _contextStatsController = composition.ContextStatsController;
            _planController = composition.PlanController;
            _sessionRewindController = composition.SessionRewindController;
            _runOutputController = composition.RunOutputController;
            _runWorkspaceResolver = composition.RunWorkspaceResolver;
            _runTextFormatter = composition.RunTextFormatter;
            _modelContextLimits = composition.ModelContextLimits;
            _llmToolCatalog = composition.LlmToolCatalog;
            _responseInspector = composition.ResponseInspector;
            _responseStreamService = composition.ResponseStreamService;
            _sessionHistoryCoordinator = composition.SessionHistoryCoordinator;

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

            _agentPane.HideHtmlCodeBlocks = !_settingsService.CurrentSettings.LlmAgentVerbose;
            AgentControllerEventBinder.Wire(
                _agentPane,
                RunAgentAsync,
                StopAgent,
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
                _openDiffViewAsync,
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
            activeOpenSession.WorkspaceRoot = _runWorkspaceResolver.Resolve(
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
                HasEnabledSkills = _skillController.HasSelectedSkills(),
                HasEnabledMcp = _mcpController.HasSelectedMcpServers(),
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
            await _runOutputController.BeginRunOutputBlockAsync(runContext, _runTextFormatter.BuildRunHeader(_promptContextService.BuildInstructionDisplay(userInstruction)));
            await _runOutputController.AppendRunActivityAsync(runContext, _getString("AgentActivityCollectingContext", "맥락 수집 중"));

            string initialTranscript = string.Empty;
            string transcript = string.Empty;
            string approvedPlanExecutionPrompt = string.Empty;
            string approvedPlanWorkspaceRoot = activeOpenSession.WorkspaceRoot;

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
                string? lastSuccessfulToolInvocationKey = null;
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
                        _llmToolCatalog.Build(
                            planningMode,
                            _mcpController.GetActiveToolAliases(),
                            runContext.HasEnabledSkills),
                        _promptContextService.GetImageAttachmentsForRun(runContext),
                        cancellationToken);

                    if (!string.IsNullOrWhiteSpace(runContext.PendingVisionFallbackContext))
                    {
                        string fallbackTranscriptPart =
                            "\n\n" + runContext.PendingVisionFallbackContext.Trim();
                        transcript += fallbackTranscriptPart;
                        runContext.CurrentRunTranscriptTokens += AgentTokenEstimator.Estimate(fallbackTranscriptPart);
                        runContext.PendingVisionFallbackContext = string.Empty;
                        await _uiDispatcher.RunAsync(() => UpdateContextStatsImmediate(force: true));
                    }

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
                            string retryDetail = _runTranscriptService.BuildRetryDetail(
                                "truncated_response",
                                response,
                                continuationNote);

                            await _uiDispatcher.RunAsync(() =>
                            {
                                transcript += retryDetail;
                                runContext.CurrentRunTranscriptTokens += AgentTokenEstimator.Estimate(retryDetail);
                                UpdateContextStatsImmediate(force: true);
                            });
                            string truncationRetryMessage = _getString(
                                "AgentActivityTruncatedRetry",
                                "응답이 잘려 이어서 작성합니다.");
                            await _runOutputController.AppendRunActivityAsync(runContext, truncationRetryMessage);
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
                            string retryDetail = _runTranscriptService.BuildRetryDetail(
                                "empty_response",
                                string.Empty,
                                retryNote);

                            await _uiDispatcher.RunAsync(() =>
                            {
                                transcript += retryDetail;
                                runContext.CurrentRunTranscriptTokens += AgentTokenEstimator.Estimate(retryDetail);
                                UpdateContextStatsImmediate(force: true);
                            });
                            string emptyRetryMessage = _getString(
                                "AgentActivityEmptyResponseRetry",
                                "빈 응답을 수신해 다시 시도합니다.");
                            await _runOutputController.AppendRunActivityAsync(runContext, emptyRetryMessage);

                            continue;
                        }

                        string emptyResponseMessage = _getString(
                            "LlmErrorEmptyResponse",
                            "AI로부터 빈 응답을 수신했습니다.");

                            await _runOutputController.AppendRunActivityAsync(runContext, emptyResponseMessage);
                            await _runOutputController.AppendRunOutputLineAsync(runContext, emptyResponseMessage);

                        AgentRunTranscriptRecorder.AppendPromptTranscriptAndResponse(
                            runContext,
                            instruction,
                            transcript,
                            initialTranscript,
                            $"[Agent Response]: {emptyResponseMessage}");
                        _ = PersistRunSessionToHistoryAsync();

                        runContext.CurrentRunTranscriptTokens += AgentTokenEstimator.Estimate(emptyResponseMessage);
                        completed = true;
                        break;
                    }

                    await _runOutputController.StopRunThinkingActivityAsync(runContext);

                    bool responseLooksLikeToolResultReplay = _responseInspector.LooksLikeToolResultReplay(response);
                    bool responseHasToolSyntax = AgentToolCallParser.ContainsToolCallSyntax(response);
                    bool responseRequiresToolHandling = responseHasToolSyntax || responseLooksLikeToolResultReplay;

                    int endLength = cleanResponse.Length;
                    if (!runContext.LlmSettings.LlmAgentVerbose && responseRequiresToolHandling)
                    {
                        if (responseLooksLikeToolResultReplay)
                        {
                            endLength = 0;
                        }
                        else
                        {
                            int toolCallIndex = AgentToolCallParser.FindToolCallIndex(cleanResponse);
                            if (toolCallIndex >= 0)
                            {
                                endLength = toolCallIndex;
                            }
                        }
                    }

                    if (planningMode && responseHasToolSyntax && !responseLooksLikeToolResultReplay)
                    {
                        int toolCallIndex = AgentToolCallParser.FindToolCallIndex(cleanResponse);
                        if (toolCallIndex > 0)
                        {
                            string visiblePrefix = cleanResponse.Substring(0, toolCallIndex);
                            if (!string.IsNullOrWhiteSpace(visiblePrefix))
                            {
                                visibleTextFlushed = true;
                                await _runOutputController.AppendOutputTextAndStreamToTabAsync(runContext, visiblePrefix);
                                await _runOutputController.EndStreamedAnswerAsync(runContext);
                            }
                        }
                    }
                    else if (!planningMode && !responseRequiresToolHandling && heldPotentialToolCallText && !visibleTextFlushed && !string.IsNullOrEmpty(cleanResponse))
                    {
                        visibleTextFlushed = true;
                        await _runOutputController.AppendOutputTextAndStreamToTabAsync(runContext, cleanResponse);
                    }
                    else if (!planningMode && printedLength < endLength)
                    {
                        string remainingText = cleanResponse.Substring(printedLength, endLength - printedLength);
                        visibleTextFlushed = true;
                        await _runOutputController.AppendOutputTextAndStreamToTabAsync(runContext, remainingText);
                    }

                    cancellationToken.ThrowIfCancellationRequested();

                    bool parsedToolCall = AgentToolCallParser.TryParseMulti(response, out List<AgentToolCallParser.ToolCallInfo> toolCalls);
                    if (responseLooksLikeToolResultReplay || !parsedToolCall || toolCalls.Count == 0)
                    {
                        if (responseHasToolSyntax || responseLooksLikeToolResultReplay)
                        {
                            toolCallFormatRetryCount++;
                            AgentToolCallParser.TryGetToolCallFormatIssue(response, out string toolCallFormatIssue);
                            if (responseLooksLikeToolResultReplay)
                            {
                                toolCallFormatIssue =
                                    "The response replayed a previous tool result instead of emitting a new tool call or final answer. " +
                                    "Do not repeat tool output from the transcript.";
                            }
                            string retryNote = _responseInspector.BuildToolCallFormatRetryNote(
                                !string.IsNullOrWhiteSpace(toolCallFormatIssue)
                                    ? toolCallFormatIssue
                                    : "The tool_call JSON could not be parsed.");
                            string retryDetail = _runTranscriptService.BuildRetryDetail(
                                responseLooksLikeToolResultReplay ? "tool_result_replay" : "tool_call_format",
                                response,
                                retryNote);
                            runContext.RetryDebugHistory.AppendLine(retryDetail);
                            string retryPromptContext = "\n\n" + retryNote;
                            transcript += retryPromptContext;
                            runContext.CurrentRunTranscriptTokens += AgentTokenEstimator.Estimate(retryPromptContext);

                            string retryMessage = _getString(
                                "AgentToolCallFormatRetry",
                                "도구 호출을 해석하지 못해 다시 요청합니다.");
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

                                AgentRunTranscriptRecorder.AppendPromptTranscriptAndResponse(
                                    runContext,
                                    instruction,
                                    transcript,
                                    initialTranscript,
                                    $"[Agent Response]: {limitMessage}");
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
                            string retryDetail = _runTranscriptService.BuildRetryDetail(
                                "make_plan_required",
                                response,
                                retryNote);

                            transcript += retryDetail;
                            runContext.CurrentRunTranscriptTokens += AgentTokenEstimator.Estimate(retryDetail);

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

                                AgentRunTranscriptRecorder.AppendPromptTranscriptAndResponse(
                                    runContext,
                                    instruction,
                                    transcript,
                                    initialTranscript,
                                    $"[Agent Response]: {limitMessage}");
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
                            string retryDetail = _runTranscriptService.BuildRetryDetail(
                                "skill_not_called",
                                response,
                                retryNote);

                            await _uiDispatcher.RunAsync(() =>
                            {
                                transcript += retryDetail;
                                runContext.CurrentRunTranscriptTokens += AgentTokenEstimator.Estimate(retryDetail);
                                UpdateContextStatsImmediate(force: true);
                            });

                            string retryMessage = _getString(
                                "AgentSkillNotCalledRetry",
                                "스킬을 호출하지 않고 설명만 했습니다. 도구 호출을 다시 시도합니다.");
                            await _runOutputController.AppendRunActivityAsync(runContext, retryMessage);
                            await _runOutputController.AppendRunOutputLineAsync(runContext, retryMessage);
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
                        runContext.LastAnswerText = AgentRunTextFormatter.BuildLastAnswerText(response, cleanResponse, runContext.LlmSettings.LlmAgentVerbose);
                        await _runOutputController.AppendRunActivityAsync(runContext, _getString("AgentActivityFinalAnswer", "최종 응답 작성 완료"));
                        if (runContext.StreamToTab)
                        {
                            await _runOutputController.EndStreamedAnswerAsync(runContext);
                        }

                        AgentRunTranscriptRecorder.AppendPromptTranscriptAndResponse(
                            runContext,
                            instruction,
                            transcript,
                            initialTranscript,
                            $"[Agent Response]: {response.Trim()}");
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
                            lastSuccessfulToolInvocationKey = null;
                            toolResult = "make_plan failed: this tool is only available when planning mode is enabled.";
                            toolResultForTranscript = toolResult;
                        }
                        else if (planningMode && IsMutatingTool(normalizedToolName) && normalizedToolName != "make_plan")
                        {
                            lastSuccessfulToolInvocationKey = null;
                            toolResult =
                                "blocked: planning mode is plan-only and cannot run file/editor mutation tools. " +
                                "Continue with safe inspection if needed, or write the detailed Markdown plan as the final answer.";
                            toolResultForTranscript = toolResult;
                        }
                        else if (ShouldReuseCachedSuccessfulTool(
                                normalizedToolName,
                                toolInvocationKey,
                                lastSuccessfulToolInvocationKey) &&
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
                            lastSuccessfulToolInvocationKey = null;
                            lastDuplicateToolInvocationKey = null;
                            repeatedDuplicateToolSkipCount = 0;
                            await _toolExecutionSessionGate.WaitAsync(cancellationToken);
                            try
                            {
                                _activeToolRunContext.Value = runContext;
                                await _uiDispatcher.RunAsync(() =>
                                {
                                    _sessionEditController.Replace(runContext.SessionEdits, runContext.SessionId);
                                });
                                toolResult = await _toolExecutionController.ExecuteAsync(currentToolName, currentArguments, cancellationToken);
                                runContext.SessionEdits = _sessionEditController.SessionEdits.ToList();
                                _openSessionController.EnsureSession(runContext.SessionId).SessionEdits = runContext.SessionEdits.ToList();
                                if (!_runOutputController.IsSessionVisible(runContext.SessionId))
                                {
                                    await _uiDispatcher.RunAsync(() =>
                                    {
                                        var visibleSession = _openSessionController.EnsureSession(_currentSessionId);
                                        _sessionEditController.Replace(visibleSession.SessionEdits, _currentSessionId);
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
                                    lastSuccessfulToolInvocationKey = toolInvocationKey;
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
                            addedPartBuilder.AppendLine($"[Parsed tool call: {tcRes.Name}]");
                            addedPartBuilder.AppendLine(tcRes.Args.GetRawText());
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

                        AgentRunTranscriptRecorder.AppendPromptTranscriptAndResponse(
                            runContext,
                            instruction,
                            transcript,
                            initialTranscript,
                            "[Agent Response]: Plan saved for user review.");
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

                        AgentRunTranscriptRecorder.AppendPromptTranscriptAndResponse(
                            runContext,
                            instruction,
                            transcript,
                            initialTranscript,
                            $"[Agent Response]: {loopMessage}");
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

                            AgentRunTranscriptRecorder.AppendPromptTranscript(
                                runContext,
                                instruction,
                                transcript,
                                initialTranscript);
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

                    AgentRunTranscriptRecorder.AppendPromptTranscriptAndResponse(
                        runContext,
                        instruction,
                        transcript,
                        initialTranscript,
                        "[Agent Response]: Tool step limit reached before a final answer.");
                    _ = PersistRunSessionToHistoryAsync();
                }
            }
            catch (OperationCanceledException)
            {
                await _runOutputController.AppendRunActivityAsync(runContext, _getString("AgentActivityStopped", "중단됨"));
                await _runOutputController.AppendRunOutputLineAsync(runContext, _getString("AgentOutputStopped", "Agent 실행이 중단되었습니다."));

                AgentRunTranscriptRecorder.AppendPromptTranscriptAndResponse(
                    runContext,
                    instruction,
                    transcript,
                    initialTranscript,
                    "[Agent Response]: Agent execution was interrupted by the user.");
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
                bool completedInBackground = !_runOutputController.IsSessionVisible(runContext.SessionId);
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
                    if (completedInBackground)
                    {
                        _openSessionController.MarkBackgroundSessionCompleted(runContext.SessionId);
                    }
                    else
                    {
                        _openSessionController.UpdateUI();
                    }
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
            context.VisionFallbackPending = true;
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
