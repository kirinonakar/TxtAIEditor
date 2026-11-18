using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TxtAIEditor.Core.Interfaces;
using TxtAIEditor.Core.Models;
using TxtAIEditor.Core.Services;
using TxtAIEditor.Core.Services.LLM;
using static TxtAIEditor.Controls.AgentToolHelpers;

namespace TxtAIEditor.Controls
{
    internal sealed class AgentRunCoordinator
    {
        private readonly ISettingsService _settingsService;
        private readonly AgentPane _agentPane;
        private readonly AgentUiDispatcher _uiDispatcher;
        private readonly Action<string, string> _showError;
        private readonly Func<string, string, string> _getString;
        private readonly AgentFileToolService _fileTools;
        private readonly AgentDisplayLocalizer _displayText;
        private readonly AgentSkillController _skillController;
        private readonly AgentMcpController _mcpController;
        private readonly AgentOpenSessionController _openSessionController;
        private readonly AgentPromptContextService _promptContextService;
        private readonly AgentConfirmationController _confirmationController;
        private readonly AgentSelectionContextController _selectionContextController;
        private readonly AgentFileToolController _fileToolController;
        private readonly AgentToolExecutionController _toolExecutionController;
        private readonly AgentToolBatchExecutor _toolBatchExecutor;
        private readonly AgentModelRequestBuilder _modelRequestBuilder;
        private readonly AgentStepResponseHandler _stepResponseHandler;
        private readonly AgentPlanController _planController;
        private readonly AgentRunOutputController _runOutputController;
        private readonly AgentRunTranscriptService _runTranscriptService;
        private readonly AgentRunWorkspaceResolver _runWorkspaceResolver;
        private readonly AgentRunTextFormatter _runTextFormatter;
        private readonly AgentResponseStreamService _responseStreamService;
        private readonly AgentSessionHistoryCoordinator _sessionHistoryCoordinator;
        private readonly Dictionary<string, AgentRunContext> _runningSessions;
        private readonly AsyncLocal<AgentRunContext?> _activeWorkspaceRunContext = new();
        private readonly Func<string> _currentSessionIdProvider;
        private readonly Action<string, double, double> _restoreSessionHistoryState;
        private readonly Action<bool> _updateContextStatsImmediate;
        private readonly List<LlmMessageAttachment> _currentRunImageToolAttachments = new();

        private double _currentRunTranscriptTokens;

        public AgentRunCoordinator(
            ISettingsService settingsService,
            AgentPane agentPane,
            AgentUiDispatcher uiDispatcher,
            Action<string, string> showError,
            Func<string, string, string> getString,
            AgentFileToolService fileTools,
            AgentDisplayLocalizer displayText,
            AgentSkillController skillController,
            AgentMcpController mcpController,
            AgentOpenSessionController openSessionController,
            AgentPromptContextService promptContextService,
            AgentConfirmationController confirmationController,
            AgentSelectionContextController selectionContextController,
            AgentFileToolController fileToolController,
            AgentToolExecutionController toolExecutionController,
            AgentToolBatchExecutor toolBatchExecutor,
            AgentModelRequestBuilder modelRequestBuilder,
            AgentStepResponseHandler stepResponseHandler,
            AgentPlanController planController,
            AgentRunOutputController runOutputController,
            AgentRunTranscriptService runTranscriptService,
            AgentRunWorkspaceResolver runWorkspaceResolver,
            AgentRunTextFormatter runTextFormatter,
            AgentResponseStreamService responseStreamService,
            AgentSessionHistoryCoordinator sessionHistoryCoordinator,
            Dictionary<string, AgentRunContext> runningSessions,
            Func<string> currentSessionIdProvider,
            Action<string, double, double> restoreSessionHistoryState,
            Action<bool> updateContextStatsImmediate)
        {
            _settingsService = settingsService;
            _agentPane = agentPane;
            _uiDispatcher = uiDispatcher;
            _showError = showError;
            _getString = getString;
            _fileTools = fileTools;
            _displayText = displayText;
            _skillController = skillController;
            _mcpController = mcpController;
            _openSessionController = openSessionController;
            _promptContextService = promptContextService;
            _confirmationController = confirmationController;
            _selectionContextController = selectionContextController;
            _fileToolController = fileToolController;
            _toolExecutionController = toolExecutionController;
            _toolBatchExecutor = toolBatchExecutor;
            _modelRequestBuilder = modelRequestBuilder;
            _stepResponseHandler = stepResponseHandler;
            _planController = planController;
            _runOutputController = runOutputController;
            _runTranscriptService = runTranscriptService;
            _runWorkspaceResolver = runWorkspaceResolver;
            _runTextFormatter = runTextFormatter;
            _responseStreamService = responseStreamService;
            _sessionHistoryCoordinator = sessionHistoryCoordinator;
            _runningSessions = runningSessions;
            _currentSessionIdProvider = currentSessionIdProvider;
            _restoreSessionHistoryState = restoreSessionHistoryState;
            _updateContextStatsImmediate = updateContextStatsImmediate;
        }

        public string? GetActiveRunWorkspaceRoot()
        {
            AgentRunContext? context = GetActiveRunContext();
            AgentRunWorkspaceResolver.VerifyPinnedWorkspace(context);
            return context?.WorkspaceRoot;
        }

        public AgentRunContext? GetActiveRunContext()
        {
            return _toolBatchExecutor.ActiveRunContext ?? _activeWorkspaceRunContext.Value;
        }

        public double GetCurrentRunTranscriptTokens()
        {
            // The panel shows the current session, so the current session's running context is
            // authoritative. AsyncLocal run contexts are not visible from dispatcher-queued
            // callbacks, which previously froze the panel token count until the session was
            // re-opened (or another session was restored).
            if (_runningSessions.TryGetValue(_currentSessionIdProvider(), out AgentRunContext? sessionContext))
            {
                return sessionContext.CurrentRunTranscriptTokens + sessionContext.InFlightReasoningTokens +
                    sessionContext.InFlightResponseTokens;
            }

            AgentRunContext? context = GetActiveRunContext();
            if (context == null)
            {
                return _currentRunTranscriptTokens;
            }

            // Include both native reasoning and ordinary response chunks while streaming.
            return context.CurrentRunTranscriptTokens + context.InFlightReasoningTokens +
                context.InFlightResponseTokens;
        }

        public void RestoreCurrentRunTranscriptTokens(double currentRunTranscriptTokens)
        {
            _currentRunTranscriptTokens = currentRunTranscriptTokens;
        }

        // Estimated tokens of the request that is actually sent to the model for the current
        // session. 0 means the session has no active run or its first request has not
        // been measured yet, so callers should keep their own estimate.
        public double GetActualRequestTokens()
        {
            if (!_runningSessions.TryGetValue(_currentSessionIdProvider(), out AgentRunContext? context) ||
                context.ActualRequestTokens <= 0)
            {
                return 0;
            }

            double addedTokens = context.CurrentRunTranscriptTokens - context.ActualRequestTokensBase +
                context.InFlightReasoningTokens + context.InFlightResponseTokens;
            return addedTokens > 0 ? context.ActualRequestTokens + addedTokens : context.ActualRequestTokens;
        }

        private Task<OpenedTab?> CaptureActiveTabForRunAsync()
        {
            return _uiDispatcher.RunAsync(() =>
                _selectionContextController.CaptureActiveTabForRun(_openSessionController.IsCurrentSessionRunning()));
        }

        public async Task RunAgentAsync()
        {
            string sessionId = _currentSessionIdProvider();
            var activeOpenSession = _openSessionController.EnsureSession(sessionId);
            if (_runningSessions.ContainsKey(sessionId) || activeOpenSession.IsRewinding || activeOpenSession.Role == AgentSessionRole.Subagent)
            {
                return;
            }
            using var selectionScope = _mcpController.UseSelections(activeOpenSession.Selections);
  
            var settings = _settingsService.CurrentSettings;

            string userInstruction = _agentPane.Prompt.Text?.Trim() ?? string.Empty;
            bool requestedPlanningMode = _agentPane.PlanningMode;
            bool useSubagents = _agentPane.UseSubagents;
            if (requestedPlanningMode && string.IsNullOrWhiteSpace(userInstruction))
            {
                _showError(
                    _getString("AgentErrorTitle", "Agent 오류"),
                    _getString("AgentEmptyPrompt", "Agent에게 맡길 작업을 입력해 주세요."));
                return;
            }

            await _mcpController.ApplyPendingPluginChangesAsync(CancellationToken.None);
            await _mcpController.EnsureActiveToolsAsync(CancellationToken.None);
            if (!_openSessionController.IsSessionVisible(sessionId) || activeOpenSession.IsRewinding || _runningSessions.ContainsKey(sessionId))
            {
                return;
            }

            string targetLanguage = settings?.ResolveTargetLanguage() ?? "Korean";
            string pendingPlanPath = requestedPlanningMode
                ? activeOpenSession.PendingPlanPath ?? string.Empty
                : string.Empty;
            if (!string.IsNullOrWhiteSpace(pendingPlanPath) && !File.Exists(pendingPlanPath))
            {
                // The saved plan file is missing; fall back to a normal planning run.
                pendingPlanPath = string.Empty;
                activeOpenSession.PendingPlanPath = string.Empty;
            }

            string conversationTurn = _promptContextService.BuildConversationTurn(requestedPlanningMode
                ? string.IsNullOrWhiteSpace(pendingPlanPath)
                    ? AgentPlanController.BuildPlanningModeRequest(userInstruction, targetLanguage)
                    : AgentPlanController.BuildPlanRevisionRequest(userInstruction, pendingPlanPath, targetLanguage)
                : userInstruction);
            if (string.IsNullOrWhiteSpace(conversationTurn))
            {
                _showError(
                    _getString("AgentErrorTitle", "Agent 오류"),
                    _getString("AgentEmptyPrompt", "Agent에게 맡길 작업을 입력해 주세요."));
                return;
            }

            EditorSettings runSettings = await _openSessionController.ResolveRunSessionSettingsAsync(activeOpenSession);
            if (!_openSessionController.IsSessionVisible(sessionId) || activeOpenSession.IsRewinding || _runningSessions.ContainsKey(sessionId))
            {
                return;
            }

            string preservedWorkspaceRoot = activeOpenSession.WorkspaceRoot;
            _openSessionController.SaveActiveFromUI();
            var rewindSnapshot = AgentSessionRewindSnapshot.Capture(activeOpenSession);
            activeOpenSession.RewindSnapshots.Add(rewindSnapshot);
            if (useSubagents)
            {
                activeOpenSession.Role = AgentSessionRole.Orchestrator;
            }
            _openSessionController.UpdateSessionTitle(activeOpenSession, userInstruction);
            activeOpenSession.PromptText = _agentPane.Prompt.Text ?? string.Empty;
            _agentPane.ClearPromptForRun();
            activeOpenSession.UpdatedAt = DateTime.Now;
            activeOpenSession.WorkspaceRoot = _runWorkspaceResolver.Resolve(
                preservedWorkspaceRoot,
                activeOpenSession.WorkspaceRoot,
                userInstruction);
            var runContext = CreateRunContext(activeOpenSession, runSettings, userInstruction);
            if (AgentRunWorkspaceResolver.IsApprovedPlanExecutionPrompt(userInstruction))
            {
                runContext.PinnedWorkspaceRoot = activeOpenSession.ApprovedPlanWorkspacePin;
            }
            runContext.RewindSnapshot = rewindSnapshot;
            runContext.StreamToTab = _agentPane.StreamToTab;
            runContext.IsPlanningMode = requestedPlanningMode;
            runContext.PendingPlanPath = pendingPlanPath;
            await ExecuteRunAsync(activeOpenSession, runContext, conversationTurn, useSubagents);
        }

        private AgentRunContext CreateRunContext(AgentOpenSessionState session, EditorSettings settings, string instruction)
        {
            var context = new AgentRunContext
            {
                SessionId = session.Id,
                Selections = session.Selections,
                SessionHistoryTokenCount = session.SessionHistoryTokenCount,
                Attachments = session.Attachments.ToList(),
                SessionEdits = session.SessionEdits.ToList(),
                InitialEditCount = session.SessionEdits.Count,
                LastAnswerText = session.Role == AgentSessionRole.Subagent ? string.Empty : session.LastAnswerText,
                WorkspaceRoot = session.WorkspaceRoot,
                LlmSettings = settings,
                HasEnabledSkills = _skillController.HasSelectedSkills(),
                HasEnabledMcp = _mcpController.HasSelectedMcpServers(),
                OriginalUserInstruction = instruction
            };
            context.SessionHistory.Append(session.SessionHistoryText);
            string modelSessionHistory = session.ModelSessionHistoryText;
            if (string.IsNullOrEmpty(modelSessionHistory))
            {
                modelSessionHistory = _promptContextService.PrepareSessionHistoryForPrompt(session.SessionHistoryText);
            }
            context.ModelSessionHistory.Append(modelSessionHistory);
            return context;
        }

        private async Task<AgentRunContext> RunSubagentAsync(
            AgentRunContext parent,
            EditorSettings workerSettings,
            OpenedTab? activeTab,
            AgentSelectionSnapshot selection,
            AgentOpenSessionState session,
            string instruction,
            AgentFollowUpQueue inbox,
            Func<string, string, Task<string>> reportProgress,
            CancellationToken cancellationToken)
        {
            session.WorkspaceRoot = parent.WorkspaceRoot;
            session.LlmSettings = workerSettings;
            session.Attachments = parent.Attachments.ToList();
            using var selectionScope = _mcpController.UseSelections(session.Selections);
            _mcpController.RefreshSessionSelection();
            var context = CreateRunContext(session, workerSettings, instruction);
            context.PinnedWorkspaceRoot = parent.PinnedWorkspaceRoot;
            context.IsPlanResearch = parent.IsPlanningMode;
            context.FollowUps = inbox;
            context.ReportToOrchestrator = reportProgress;
            string turn = $"[user]\n{AgentSessionLabels.OrchestratorPrompt}\n{instruction}";
            await ExecuteRunAsync(session, context, turn, false, activeTab, selection, cancellationToken);
            return context;
        }

        private async Task ExecuteRunAsync(
            AgentOpenSessionState activeOpenSession,
            AgentRunContext runContext,
            string conversationTurn,
            bool useSubagents,
            OpenedTab? inheritedTab = null,
            AgentSelectionSnapshot? inheritedSelection = null,
            CancellationToken parentCancellation = default)
        {
            string userInstruction = runContext.OriginalUserInstruction;
            using var selectionScope = _mcpController.UseSelections(runContext.Selections);
            bool isSubagent = activeOpenSession.Role == AgentSessionRole.Subagent;
            using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(parentCancellation);
            runContext.Cancellation = cancellationSource;
            CancellationToken cancellationToken = cancellationSource.Token;
            runContext.CancellationToken = cancellationToken;
            runContext.ParentCancellationToken = parentCancellation;
            activeOpenSession.IsRunning = true;
            activeOpenSession.UpdatedAt = DateTime.Now;
            _runningSessions[activeOpenSession.Id] = runContext;
            _openSessionController.UpdateUI();
            _openSessionController.UpdateActiveSessionBusyState();
            _fileToolController.StartRun();
            if (!isSubagent)
            {
                _agentPane.HideHtmlCodeBlocks = !_settingsService.CurrentSettings.LlmAgentVerbose;
                _currentRunImageToolAttachments.Clear();
                _selectionContextController.ClearRunSnapshots();
                _currentRunTranscriptTokens = 0;
            }

            string initialTranscript = string.Empty;
            var transcript = new AgentRunTranscript(runContext);
            string approvedPlanExecutionPrompt = string.Empty;
            string approvedPlanWorkspaceRoot = activeOpenSession.WorkspaceRoot;

            bool historyReady = false;
            bool responseStreamInProgress = false;
            bool sessionClosed = false;
            AgentSubagentCoordinator? orchestration = null;
            void FinishRun(string? responseLine, AgentRunStatus status = AgentRunStatus.Completed)
            {
                if (string.IsNullOrEmpty(transcript.DisplayText))
                {
                    transcript.DisplayText = conversationTurn + Environment.NewLine;
                    transcript.ModelText = runContext.ModelSessionHistory.ToString().TrimEnd() +
                        Environment.NewLine + transcript.DisplayText;
                }
                AgentRunTranscriptRecorder.AppendPromptTranscriptAndResponse(
                    runContext, conversationTurn, transcript.DisplayText,
                    initialTranscript, responseLine, transcript.ModelText);
                historyReady = true;
                runContext.Status = status;
            }

            _activeWorkspaceRunContext.Value = runContext;
            try
            {
                await _runOutputController.ClearRunActivityAsync(runContext, _getString("AgentActivityStarting", "시작 중"));
                await _runOutputController.BeginRunOutputBlockAsync(runContext, _runTextFormatter.BuildRunHeader(
                    isSubagent ? userInstruction : _promptContextService.BuildInstructionDisplay(userInstruction), isSubagent));
                await _runOutputController.AppendRunActivityAsync(runContext, _getString("AgentActivityCollectingContext", "맥락 수집 중"));
                runContext.ToolCancellationToken.ThrowIfCancellationRequested();
                runContext.PinnedWorkspaceRoot ??= AgentWorkspaceFileResolver.ResolveWorkspaceDirectory(runContext.WorkspaceRoot);
                _ = GetActiveRunWorkspaceRoot();

                string globalAgentsMdLogSection = _promptContextService.BuildGlobalAgentsMdLogSection();
                if (!string.IsNullOrWhiteSpace(globalAgentsMdLogSection))
                {
                    AgentRunTranscriptRecorder.AppendLine(runContext, globalAgentsMdLogSection);
                    await _runOutputController.AppendRunOutputTextAsync(
                        runContext,
                        globalAgentsMdLogSection + Environment.NewLine + Environment.NewLine);
                }

                OpenedTab? currentRunActiveTab = isSubagent ? inheritedTab : await CaptureActiveTabForRunAsync();
                AgentSelectionSnapshot currentRunSelectionSnapshot = inheritedSelection ?? _selectionContextController.CaptureSelectionForRun(true);
                if (useSubagents)
                {
                    EditorSettings config = _settingsService.CurrentSettings;
                    EditorSettings workerSettings = CreateSubagentSettings(runContext.LlmSettings, config);
                    var workerSessions = _openSessionController.GetOpenSubagentSessions(runContext.SessionId)
                        .Where(session => !_openSessionController.IsPendingClose(session.Id))
                        .ToDictionary(session => session.Id, StringComparer.Ordinal);
                    orchestration = new AgentSubagentCoordinator(
                        Math.Clamp(config.LlmMaxSubagents, 1, EditorSettings.MaxSubagentLimit),
                        (id, title, instruction, inbox, progress, token) =>
                        {
                            token.ThrowIfCancellationRequested();
                            if (_openSessionController.IsSessionRunning(id) || _openSessionController.HasPendingSubagentShutdown(id))
                            {
                                throw new InvalidOperationException("The previous subagent assignment is still shutting down. Retry the follow-up once it finishes.");
                            }
                            workerSessions.TryGetValue(id, out AgentOpenSessionState? previousState);
                            var workerSession = _openSessionController.EnsureSession(id, previousState);
                            _openSessionController.RestoreSessionBody(workerSession);
                            workerSession.Role = AgentSessionRole.Subagent;
                            workerSession.OrchestratorSessionId = runContext.SessionId;
                            workerSession.Title = title;
                            workerSessions[id] = workerSession;
                            if (previousState == null)
                            {
                                workerSession.Selections = runContext.Selections.Copy();
                            }
                            runContext.RewindSnapshot?.RecordSubagent(workerSession, isNewSession: previousState == null);
                            return RunSubagentAsync(runContext, workerSettings, currentRunActiveTab, currentRunSelectionSnapshot,
                                workerSession, instruction, inbox, progress, token);
                        },
                        message => _runOutputController.AppendRunActivityAsync(runContext, message),
                        _getString,
                        cancellationToken,
                        workerSessions.Values.Select(session => (session.Id, session.Title)));
                }

                runContext.StreamToTabTargetTabId = currentRunActiveTab?.Id;
                _fileToolController.SetRunContext(currentRunSelectionSnapshot, currentRunActiveTab, useCapturedContextOnly: isSubagent);
                string currentWorkspaceContext = await _promptContextService.BuildWorkspaceContextAsync(
                    conversationTurn,
                    currentRunActiveTab,
                    currentRunSelectionSnapshot,
                    runContext.Attachments,
                    runContext.PinnedWorkspaceRoot,
                    cancellationToken);
                string runSelectionContext = _selectionContextController.BuildSelectionContext(currentRunSelectionSnapshot);
                runContext.PlanWorkspaceContext = currentWorkspaceContext;
                runContext.PlanSelectionContext = runSelectionContext;
                var initialTranscriptBuilder = new StringBuilder();
                string sessionHistoryForPrompt = _promptContextService.PrepareSessionHistoryForPrompt(
                    runContext.ModelSessionHistory.ToString());
                if (!string.IsNullOrWhiteSpace(sessionHistoryForPrompt))
                {
                    initialTranscriptBuilder.Append(sessionHistoryForPrompt.TrimEnd());
                    initialTranscriptBuilder.AppendLine();
                    initialTranscriptBuilder.AppendLine();
                }
                initialTranscript = initialTranscriptBuilder.ToString();

                transcript.DisplayText = initialTranscript + conversationTurn.Trim() + Environment.NewLine;
                transcript.ModelText = transcript.DisplayText;

                if (orchestration != null)
                {
                    try
                    {
                        await _sessionHistoryCoordinator.EnsureOrchestratorHistoryAsync(
                            activeOpenSession, runContext, conversationTurn, transcript, initialTranscript);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to save orchestrator checkpoint: {ex.Message}");
                    }
                }

                bool completed = false;
                var retries = new AgentStepRetryPolicy();
                bool planningMode = runContext.IsPlanningMode;
                int maxToolSteps = runContext.LlmSettings.LlmMaxToolCalls >= 50 ? runContext.LlmSettings.LlmMaxToolCalls : 100;
                var toolInvocationCounts = new Dictionary<string, int>(StringComparer.Ordinal);
                int currentTaskStartEditIndex = runContext.SessionEdits.Count;

                for (int step = 0; step < maxToolSteps; step++)
                {
                    runContext.ToolCancellationToken.ThrowIfCancellationRequested();
                    _ = GetActiveRunWorkspaceRoot();
                    if (orchestration != null)
                    {
                        transcript.DisplayText += orchestration.DrainProgressLog();
                    }
                    string fixedPromptContext = await _modelRequestBuilder.BuildFixedPromptContextAsync(
                        runContext, orchestration, isSubagent);
                    string? queuedFollowUpPrompt = runContext.FollowUps.Take(out Task followUpRequested);
                    if (!string.IsNullOrWhiteSpace(queuedFollowUpPrompt))
                    {
                        string followUpTurn = isSubagent
                            ? $"[user]\n{AgentSessionLabels.OrchestratorPrompt}\n{queuedFollowUpPrompt}"
                            : _promptContextService.BuildConversationTurn(queuedFollowUpPrompt);
                        if (!string.IsNullOrWhiteSpace(followUpTurn))
                        {
                            string followUpPart = Environment.NewLine + Environment.NewLine + followUpTurn.Trim();
                            transcript.AppendWithTokenCount(followUpPart);
                            conversationTurn = followUpTurn;
                            await _uiDispatcher.RunAsync(() => _updateContextStatsImmediate(true));
                        }

                        string followUpHeader = _runTextFormatter.BuildRunHeader(
                            isSubagent ? queuedFollowUpPrompt : _promptContextService.BuildInstructionDisplay(queuedFollowUpPrompt), isSubagent);
                        await _runOutputController.AppendRunOutputLineAsync(runContext, followUpHeader);
                    }

                    AgentModelRequest request = await _modelRequestBuilder.BuildAsync(
                        runContext, transcript, currentTaskStartEditIndex, fixedPromptContext,
                        currentWorkspaceContext, runSelectionContext, orchestration, isSubagent);

                    if (isSubagent && followUpRequested.IsCompleted)
                    {
                        continue;
                    }

                    runContext.ToolCancellationToken.ThrowIfCancellationRequested();
                    _ = GetActiveRunWorkspaceRoot();
                    responseStreamInProgress = true;
                    bool awaitingSubagents = orchestration?.NeedsModelReview == true;
                    bool deferAnswer = awaitingSubagents || isSubagent;
                    AgentResponseStreamResult streamResult = await _responseStreamService.RunAsync(
                        runContext,
                        fixedPromptContext,
                        request.Transcript,
                        currentWorkspaceContext,
                        runSelectionContext,
                        planningMode,
                        request.Tools,
                        request.ImageAttachments,
                        cancellationToken,
                        transcript.GeminiTools,
                        deferAnswer: deferAnswer);
                    responseStreamInProgress = false;
                    runContext.ToolCancellationToken.ThrowIfCancellationRequested();
                    _ = GetActiveRunWorkspaceRoot();

                    if (!string.IsNullOrWhiteSpace(runContext.PendingVisionFallbackContext))
                    {
                        string fallbackTranscriptPart =
                            "\n\n" + runContext.PendingVisionFallbackContext.Trim();
                        transcript.AppendWithTokenCount(fallbackTranscriptPart);
                        runContext.PendingVisionFallbackContext = string.Empty;
                        await _uiDispatcher.RunAsync(() => _updateContextStatsImmediate(true));
                    }

                    if (isSubagent && followUpRequested.IsCompleted)
                    {
                        await _runOutputController.StopRunThinkingActivityAsync(runContext);
                        continue;
                    }

                    AgentStepResponse? stepResponse = await _stepResponseHandler.HandleAsync(
                        runContext, streamResult, transcript, retries, orchestration, followUpRequested, isSubagent, deferAnswer);
                    if (stepResponse is AgentFinalAnswer finalAnswer)
                    {
                        FinishRun(finalAnswer.TranscriptLog);
                        runContext.CurrentRunTranscriptTokens += AgentTokenEstimator.Estimate(finalAnswer.TranscriptLog);
                        completed = true;
                        break;
                    }
                    if (stepResponse is not AgentToolTurn toolTurn)
                    {
                        continue;
                    }

                    AgentToolBatchResult batch = await _toolBatchExecutor.ExecuteAsync(
                        activeOpenSession, runContext, toolTurn.Calls, retries, toolInvocationCounts,
                        orchestration, followUpRequested);
                    IReadOnlyList<AgentToolCallResult> toolCallResults = batch.Results;

                    runContext.ToolCancellationToken.ThrowIfCancellationRequested();

                    currentWorkspaceContext = await _uiDispatcher.RunAsync(() =>
                        _promptContextService.BuildWorkspaceContextAsync(
                            conversationTurn,
                            currentRunActiveTab,
                            currentRunSelectionSnapshot,
                            runContext.Attachments,
                            runContext.PinnedWorkspaceRoot,
                            cancellationToken));
                    var transcriptUpdate = await Task.Run(
                        () => _runTranscriptService.BuildToolTurn(toolTurn, toolCallResults), cancellationToken);
                    transcript.DisplayText += transcriptUpdate.Text;
                    if (orchestration != null)
                    {
                        transcript.DisplayText += orchestration.DrainProgressLog();
                    }
                    if (streamResult.GeminiTurn is { } geminiTurn)
                    {
                        transcript.GeminiTools?.AddTurn(transcript.ModelText.Length, transcriptUpdate.Text, geminiTurn,
                            toolCallResults.Where(result => result.NativeCallIndex.HasValue).ToDictionary(
                                result => result.NativeCallIndex!.Value,
                                result => (result.ResultForTranscript, IsSuccessfulToolResult(result.Result))));
                    }
                    transcript.ModelText += transcriptUpdate.Text;
                    runContext.CurrentRunTranscriptTokens += transcriptUpdate.Tokens;
                    await _uiDispatcher.RunAsync(() => _updateContextStatsImmediate(true));

                    foreach (var tcRes in toolCallResults)
                    {
                        if (tcRes.InterruptedWait)
                        {
                            continue;
                        }

                        bool toolSucceeded = IsSuccessfulToolResult(tcRes.Result);
                        if (isSubagent && tcRes.NormalizedName == AgentSubagentCoordinator.ReportProgressTool && toolSucceeded)
                        {
                            string progressLine = AgentSubagentCoordinator.FormatProgressLine(GetStringArgument(tcRes.Arguments, "message"));
                            transcript.DisplayText += "\n" + AgentSubagentCoordinator.ProgressMarker + progressLine + "\n";
                        }
                        if (!runContext.LlmSettings.LlmAgentVerbose && toolSucceeded &&
                            AgentSubagentCoordinator.IsCoordinationTool(tcRes.NormalizedName))
                        {
                            continue;
                        }
                        string displayResult = _toolExecutionController.FormatDisplayResult(
                            tcRes.NormalizedName,
                            tcRes.Arguments,
                            tcRes.Result,
                            false,
                            runContext.LlmSettings.LlmAgentVerbose);

                        if (!runContext.LlmSettings.LlmAgentVerbose &&
                            toolSucceeded &&
                            string.IsNullOrWhiteSpace(displayResult))
                        {
                            continue;
                        }

                        if (runContext.LlmSettings.LlmAgentVerbose || !toolSucceeded)
                        {
                            string outputHeader = _getString("AgentToolRunning", "도구 실행 중");
                            await _runOutputController.AppendRunOutputLineAsync(runContext, $"{outputHeader}: {tcRes.Name}");
                        }

                        if (!string.IsNullOrWhiteSpace(displayResult))
                        {
                            await _runOutputController.AppendRunOutputTextAsync(runContext, displayResult.TrimEnd() + Environment.NewLine);
                        }
                    }

                    if (planningMode && toolCallResults.Any(tc => tc.NormalizedName == "make_plan" && IsSuccessfulToolResult(tc.Result)))
                    {
                        activeOpenSession.PendingPlanPath = runContext.GeneratedPlanPath;
                        approvedPlanExecutionPrompt = await _planController.WaitForSavedPlanApprovalAsync(
                            runContext,
                            userInstruction,
                            cancellationToken);
                        if (!string.IsNullOrWhiteSpace(approvedPlanExecutionPrompt))
                        {
                            approvedPlanWorkspaceRoot = runContext.WorkspaceRoot;
                            activeOpenSession.PendingPlanPath = string.Empty;
                        }

                        FinishRun("[Agent Response]: Plan saved for user review.");

                        completed = true;
                        break;
                    }

                    if (batch.StopAfterLoopGuard)
                    {
                        string loopMessage = _getString(
                            "AgentLoopGuardStopped",
                            "동일한 도구 호출 반복 루프가 감지되어 Agent 실행을 중단했습니다. 출력된 결과를 확인한 뒤 다시 실행해 주세요.");
                        await _runOutputController.AppendRunActivityAsync(runContext, loopMessage);
                        await _runOutputController.AppendRunOutputLineAsync(runContext, loopMessage);

                        runContext.FailureReason = loopMessage;
                        FinishRun(null, AgentRunStatus.Failed);

                        completed = true;
                        break;
                    }
                    if (toolTurn.Calls.Count == 1 && !isSubagent && orchestration == null)
                    {
                        var tcRes = toolCallResults[0];
                        bool isFileEditTool = tcRes.NormalizedName is "search_replace" or "replace_range"
                            or "apply_patch" or "overwrite_file" or "append_to_file";
                        if (isFileEditTool && IsUnchangedEditCompletionResult(tcRes.Result))
                        {
                            string completeMsg = _getString(
                                "AgentFileEditAlreadyComplete",
                                "요청한 작업을 완료하였습니다.");

                            string verificationPart = "\n\n[File edit verification: requested content already matches the current file. Task complete.]";
                            transcript.Append(verificationPart);

                            await _runOutputController.AppendRunOutputLineAsync(runContext, completeMsg);

                            FinishRun(null);

                            completed = true;
                            break;
                        }
                    }
                }

                if (!completed)
                {
                    string limitMsg = _getString(
                        "AgentToolStepLimitReached",
                        "도구 호출 한도에 도달해 작업을 중단했습니다. 지금까지의 결과를 검토한 뒤 다시 실행해 주세요.");

                    await _runOutputController.AppendRunActivityAsync(runContext, limitMsg);
                    await _runOutputController.AppendRunOutputLineAsync(runContext, limitMsg);

                    runContext.FailureReason = limitMsg;
                    FinishRun(null, AgentRunStatus.Failed);
                }
            }
            catch (OperationCanceledException)
            {
                runContext.Status = AgentRunStatus.Stopped;
                await _runOutputController.AppendRunActivityAsync(runContext, _getString("AgentActivityStopped", "중단됨"));
                await _runOutputController.AppendRunOutputLineAsync(runContext, _getString("AgentOutputStopped", "Agent 실행이 중단되었습니다."));

                string interruptedResponse = AgentThinkingTranscriptFormatter.BuildResponsePayload(
                    AgentThinkingTranscriptFormatter.RemoveInlineThinking(runContext.StreamingResponseText),
                    runContext.LlmSettings.LlmRetainThinking
                        ? runContext.StreamingReasoningText
                        : string.Empty);
                if (!string.IsNullOrWhiteSpace(interruptedResponse))
                {
                    string interruptedTranscriptPart =
                        Environment.NewLine + Environment.NewLine + interruptedResponse.Trim();
                    transcript.AppendWithTokenCount(interruptedTranscriptPart);
                }

                FinishRun(null, AgentRunStatus.Stopped);
            }
            catch (Exception ex)
            {
                runContext.Status = AgentRunStatus.Failed;
                string failureMessage = string.Format(
                    _getString("AgentExceptionFormat", "Agent 실행 도중 예외가 발생했습니다: {0}"),
                    ex.Message);
                runContext.FailureReason = failureMessage;
                await _runOutputController.AppendRunActivityAsync(runContext, failureMessage);
                await _runOutputController.AppendRunOutputLineAsync(runContext, failureMessage);
                AgentRunTranscriptRecorder.AppendFailedRun(
                    runContext, conversationTurn, transcript.DisplayText, initialTranscript,
                    transcript.ModelText, responseStreamInProgress, failureMessage);
                historyReady = true;
            }
            finally
            {
                if (orchestration != null)
                {
                    try
                    {
                        using (orchestration)
                        {
                            var delayedWorkers = await orchestration.StopAsync();
                            foreach (var worker in delayedWorkers)
                            {
                                _openSessionController.MarkSubagentShutdownPending(runContext.SessionId, worker.AgentId);
                                if (_runningSessions.TryGetValue(worker.AgentId, out AgentRunContext? delayed))
                                {
                                    if (delayed.Cancellation is { IsCancellationRequested: false } source)
                                    {
                                        _ = RequestRunCancellationAsync(source);
                                    }
                                    _ = _runOutputController.AppendSubagentProgressAsync(delayed, _getString("AgentSubagentShutdownDelayed",
                                        "종료 요청 후에도 작업이 정리 중입니다."));
                                }
                                _ = ObserveSubagentShutdownAsync(worker);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to finish subagent shutdown: {ex.Message}");
                    }
                    string progressLog = orchestration.DrainProgressLog();
                    if (progressLog.Length > 0)
                    {
                        AgentRunTranscriptRecorder.AppendLine(runContext, progressLog);
                    }
                }

                activeOpenSession.SessionHistoryText = runContext.SessionHistory.ToString();
                activeOpenSession.ModelSessionHistoryText = runContext.ModelSessionHistory.ToString();
                activeOpenSession.SessionHistoryTokenCount = runContext.SessionHistoryTokenCount;
                activeOpenSession.CurrentRunTranscriptTokens = runContext.CurrentRunTranscriptTokens;
                activeOpenSession.SessionEdits = runContext.SessionEdits.ToList();
                activeOpenSession.Attachments = runContext.Attachments.ToList();
                activeOpenSession.LastAnswerText = runContext.LastAnswerText;
                activeOpenSession.WorkspaceRoot = runContext.WorkspaceRoot;
                if (historyReady)
                {
                    try
                    {
                        await _sessionHistoryCoordinator.SaveRunSessionToHistoryAsync(runContext, userInstruction);
                    }
                    catch (Exception ex)
                    {
                        runContext.Status = AgentRunStatus.Failed;
                        await _runOutputController.AppendRunOutputLineAsync(runContext, string.Format(
                            _getString("AgentActivityHistorySaveFailedFormat", "히스토리 저장 실패: {0}"), ex.Message));
                    }
                }
                try
                {
                    await _uiDispatcher.RunAsync(async () => await _runOutputController.FinishStreamToTabAsync(runContext));
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to finish streaming agent output: {ex.Message}");
                }
                if (runContext.RewindSnapshot is { } snapshot)
                {
                    await activeOpenSession.RewindSnapshots.ArchiveAsync(snapshot);
                    runContext.RewindSnapshot = null;
                }
                _activeWorkspaceRunContext.Value = null;
                activeOpenSession.IsRunning = false;
                _runningSessions.Remove(runContext.SessionId);
                if (!isSubagent)
                {
                    _selectionContextController.ClearRunSnapshots();
                }
                _fileToolController.FinishRun();
                _openSessionController.ClearThinkingState(activeOpenSession);
                bool completedInBackground = !_runOutputController.IsSessionVisible(runContext.SessionId);
                if (_runOutputController.IsSessionVisible(runContext.SessionId))
                {
                    _restoreSessionHistoryState(
                        activeOpenSession.SessionHistoryText,
                        activeOpenSession.SessionHistoryTokenCount,
                        activeOpenSession.CurrentRunTranscriptTokens);
                }
                _openSessionController.UpdateActiveSessionBusyState();

                if (_openSessionController.ConsumePendingClose(runContext.SessionId))
                {
                    sessionClosed = true;
                    _openSessionController.CloseSession(runContext.SessionId);
                }
                else
                {
                    // Force the refresh so the finished run's final token/context count is shown
                    // even while the prompt input still owns focus.
                    _updateContextStatsImmediate(true);
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

            if (sessionClosed || isSubagent)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(approvedPlanExecutionPrompt))
            {
                await StartApprovedPlanSessionAsync(approvedPlanExecutionPrompt, approvedPlanWorkspaceRoot,
                    runContext.PinnedWorkspaceRoot, useSubagents, runContext.Selections);
            }

            string? pendingFollowUp = runContext.FollowUps.Take(out _);
            if (!string.IsNullOrWhiteSpace(pendingFollowUp))
            {
                await _uiDispatcher.RunAsync(async () =>
                {
                    if (_openSessionController.IsSessionVisible(runContext.SessionId))
                    {
                        _agentPane.Prompt.Text = pendingFollowUp;
                        await RunAgentAsync();
                    }
                    else
                    {
                        activeOpenSession.PromptText = pendingFollowUp;
                        activeOpenSession.UpdatedAt = DateTime.Now;
                        _openSessionController.UpdateUI();
                    }
                });
            }
        }

        private async Task ObserveSubagentShutdownAsync(AgentSubagentCoordinator.PendingShutdown worker)
        {
            try { await worker.Completion; }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
            try
            {
                await _uiDispatcher.RunAsync(() => _openSessionController.CompleteSubagentShutdown(worker.AgentId));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
            }
        }

        private static EditorSettings CreateSubagentSettings(EditorSettings parent, EditorSettings preferences)
        {
            var settings = AgentOpenSessionController.CloneSessionSettings(parent);
            string provider = preferences.LlmSubagentProvider?.Trim() ?? string.Empty;
            string model = preferences.LlmSubagentModel?.Trim() ?? string.Empty;
            if (provider.Length > 0 && !provider.Equals(parent.LlmProvider, StringComparison.OrdinalIgnoreCase))
            {
                settings.LlmProvider = provider;
                settings.LlmEndpoint = SettingsLlmModelCatalog.GetDefaultEndpoint(provider, preferences.LlmEndpointCustom);
                settings.LlmModel = SettingsLlmModelCatalog.GetModelForProviderChange(preferences, provider);
                settings.LlmApiFormat = LlmRequestTuning.ApiFormatAuto;
            }

            if (model.Length > 0)
            {
                settings.LlmModel = model;
            }
            if (!settings.LlmProvider.Equals(parent.LlmProvider, StringComparison.OrdinalIgnoreCase) ||
                !settings.LlmModel.Equals(parent.LlmModel, StringComparison.OrdinalIgnoreCase))
            {
                settings.LlmMaxContextLength = LlmRequestTuning.MaxContextLengthAuto;
            }
            settings.LlmThinkingLevel = preferences.LlmSubagentThinkingLevel ?? "default";
            return settings;
        }

        private async Task StartApprovedPlanSessionAsync(string executionPrompt, string workspaceRoot,
            string? workspacePin, bool useSubagents, AgentSessionSelections selections)
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
                session.ModelSessionHistoryText = string.Empty;
                session.LastAnswerText = string.Empty;
                session.SessionHistoryTokenCount = 0;
                session.CurrentRunTranscriptTokens = 0;
                session.Attachments.Clear();
                session.SessionEdits.Clear();
                session.RewindSnapshots.Clear();
                session.WorkspaceRoot = string.IsNullOrWhiteSpace(workspaceRoot) ? _fileTools.WorkspaceRoot : workspaceRoot;
                session.ApprovedPlanWorkspacePin = workspacePin;
                session.LlmSettings = _openSessionController.CreateSessionSettingsSnapshot();
                session.UseSubagents = useSubagents;
                session.Selections = selections.Copy();
                session.UpdatedAt = DateTime.Now;

                _agentPane.PlanningModeCheckBox.IsChecked = false;
                _openSessionController.RestoreSession(session);
            });

            await _uiDispatcher.RunAsync(async () => await RunAgentAsync());
        }

        public void QueueFollowUpPrompt(string prompt)
        {
            if (_openSessionController.EnsureSession(_currentSessionIdProvider()).Role == AgentSessionRole.Subagent)
            {
                return;
            }

            string trimmedPrompt = prompt?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(trimmedPrompt))
            {
                return;
            }

            if (!_runningSessions.TryGetValue(_currentSessionIdProvider(), out AgentRunContext? context))
            {
                _ = _uiDispatcher.RunAsync(async () =>
                {
                    _agentPane.Prompt.Text = trimmedPrompt;
                    await RunAgentAsync();
                });
                return;
            }

            context.FollowUps.TryQueue(trimmedPrompt);
            string queuedNotice = _getString(
                "AgentFollowUpQueued",
                "추가 지시가 대기 중입니다. 다음 도구 호출이 끝나면 전송됩니다.");
            _ = _runOutputController.AppendRunActivityAsync(context, queuedNotice);
        }

        public void StopAgent()
        {
            StopAgent(_currentSessionIdProvider());
        }

        public void StopAgent(string sessionId)
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

            if (context.Cancellation is { } source)
            {
                _ = RequestRunCancellationAsync(source);
            }
            _confirmationController.CancelPending(sessionId);
        }

        private static async Task RequestRunCancellationAsync(CancellationTokenSource source)
        {
            try { await source.CancelAsync(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
        }

        public void AddCurrentRunImageToolAttachment(LlmMessageAttachment attachment)
        {
            AgentRunContext? context = _toolBatchExecutor.ActiveRunContext;
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

        public void AppendActivity(string message)
        {
            AgentRunContext? context = GetActiveRunContext();
            if (context != null)
            {
                _ = _runOutputController.AppendRunActivityAsync(context, message);
                return;
            }

            _openSessionController.AppendActivityToCurrentSession(message);
        }
    }
}
